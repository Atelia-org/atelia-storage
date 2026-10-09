using Atelia.FrameStore.Internal.Format;
using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Runtime;

/// <summary>
/// 串行 owner 运行内核。构造不取得目录或锁资格；仅已资格化工厂能够移交这些资源。
/// 公开 owner 经 FrameStoreFactory 取得目录资格，不公开此内部移交接缝。
/// </summary>
internal sealed class FrameStoreCore : IDisposable {
    private readonly List<FrameFileEntry> _active = [];
    private readonly IFrameStoreFiles _files;
    private readonly Action _markOwnedCleanupFault;
    private IDisposable? _ownerLock;
    private readonly bool _writable;
    private readonly int _frameEndOverhead;
    private bool _handoffComplete;
    private bool _disposed;
    private bool _faulted;
    private int _outstandingBuilders;
    private uint _maxPublishedFileId;

    // ownerLock ownership transfers only on normal constructor return; factory cleans it if validation/allocation throws.
    internal FrameStoreCore(IFrameStoreFiles files, IDisposable ownerLock, uint maxPublishedFileId,
        bool writable = true, long rotationThresholdBytes = RotationThreshold.DefaultBytes,
        int maxOutstandingBuilders = 32) {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ownerLock);
        // ReadOnly does not consume the write policy. Factory parameter/config preflight remains its responsibility.
        if (writable) {
            RotationThreshold.Validate(rotationThresholdBytes);
            if (maxOutstandingBuilders < 1) { throw new ArgumentOutOfRangeException(nameof(maxOutstandingBuilders)); }
        }
        _files = files;
        _ownerLock = ownerLock;
        _writable = writable;
        _maxPublishedFileId = maxPublishedFileId;
        RotationThresholdBytes = rotationThresholdBytes;
        MaxOutstandingBuilders = maxOutstandingBuilders;
        _markOwnedCleanupFault = MarkFaulted;
        var minimumFrame = RbfFile.MeasureWriteSize(0, 0).Value;
        _frameEndOverhead = minimumFrame.AppendLength - minimumFrame.FrameLength;
    }

    internal long RotationThresholdBytes { get; }
    internal int MaxOutstandingBuilders { get; }
    internal int OutstandingBuilders => _outstandingBuilders;
    internal uint MaxPublishedFileId => _maxPublishedFileId;
    internal bool IsFaulted => _faulted;

    /// <summary>
    /// 只供工厂在移交阶段交付完成恢复、身份/header 检查的 retained active handle。
    /// max 必须来自 active/archive 正式名称的完整发现，不能只取 active 最大值。
    /// 正常返回才转移 handle 所有权；本方法不执行扫描来替代资格取得。
    /// </summary>
    internal void AdoptQualifiedActive(uint fileId, IRbfFile file) {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(file);
        if (_handoffComplete) { throw new InvalidOperationException("The qualified-handle handoff is already complete."); }
        if (!_writable) { throw new InvalidOperationException("ReadOnly owners do not retain active writer handles."); }
        if (fileId == 0 || fileId > _maxPublishedFileId) { throw new ArgumentOutOfRangeException(nameof(fileId)); }
        int index = FindInsertionIndex(fileId);
        if (index < _active.Count && _active[index].FileId == fileId) {
            throw new InvalidOperationException("An active file with this FileId is already owned.");
        }
        if (file.Format != RbfFormat.Rbf3) { throw new InvalidDataException("FrameStore active files must use RBF3."); }
        long tail = file.TailOffset;
        if (tail < FileHeaderCodec.InitializationBoundary) { throw new InvalidDataException("The qualified active file is shorter than its initialized header."); }
        var entry = new FrameFileEntry(fileId);
        _active.EnsureCapacity(checked(_active.Count + 1));
        entry.File = file;
        entry.CompletedTailOffset = tail;
        entry.NeedsConfirmation = true;
        entry.Stopped = tail > RotationThresholdBytes;
        _active.Insert(index, entry);
    }

    /// <summary>后续可写 Open 在全部资源资格通过后、交付 owner 前调用。</summary>
    internal void CompleteWritableHandoff() {
        EnsureWritable();
        DrainStopped();
    }

    internal AteliaResult<FrameAddress> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) {
        EnsureWritable();
        var size = RbfFile.MeasureWriteSize(payload.Length, tailMeta.Length);
        if (size.IsFailure) { return size.Error!; }
        RejectExhaustionIfNoCandidate();
        DrainStopped();
        var entry = SelectOrCreate();
        entry.ShortLease = true;
        try {
            AteliaResult<SizedPtr> result;
            try { result = entry.File!.Append(tag, payload, tailMeta); }
            catch {
                MarkFaulted();
                throw;
            }
            if (result.IsFailure) { return result.Error!; }
            RegisterCompletedOutput(entry, result.Value);
            return FrameAddress.Create(entry.FileId, result.Value);
        }
        finally {
            entry.ShortLease = false;
        }
    }

    internal FrameBuilder BeginAppend() => BeginAppendCore(null, 0, out _);

    internal FrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out FrameAddress address) =>
        BeginAppendCore(payloadLength, tailMetaLength, out address);

    private FrameBuilder BeginAppendCore(int? payloadLength, int tailMetaLength, out FrameAddress address) {
        address = default;
        EnsureWritable();
        if (payloadLength.HasValue) {
            var size = RbfFile.MeasureWriteSize(payloadLength.Value, tailMetaLength);
            if (size.IsFailure) {
                string name = payloadLength.Value < 0 ? nameof(payloadLength)
                    : tailMetaLength < 0 || tailMetaLength > RbfFile.MaxTailMetaLength ? nameof(tailMetaLength)
                    : nameof(payloadLength);
                throw new ArgumentOutOfRangeException(name, size.Error!.Message);
            }
        }
        if (_outstandingBuilders >= MaxOutstandingBuilders) {
            throw new InvalidOperationException($"MaxOutstandingBuilders ({MaxOutstandingBuilders}) is full; current count is {_outstandingBuilders}.");
        }
        RejectExhaustionIfNoCandidate();
        DrainStopped();
        var entry = SelectOrCreate();
        // All wrapper allocation happens before the inner Builder is prepared. Successful signing is field-only.
        var lease = new FrameLease(this, entry);
        entry.CurrentLease = lease;
        try {
            if (payloadLength.HasValue) {
                lease.Builder = entry.File!.BeginAppend(payloadLength.Value, tailMetaLength, out var ticket);
                address = FrameAddress.Create(entry.FileId, ticket);
            }
            else { lease.Builder = entry.File!.BeginAppend(); }
        }
        catch {
            entry.CurrentLease = null;
            // RBF Begin has not issued a Builder. No output-phase inference or phantom quota is introduced.
            throw;
        }
        lease.Active = true;
        lease.Counted = true;
        _outstandingBuilders++;
        return new FrameBuilder(lease);
    }

    internal AteliaResult<FrameAddress> EndAppend(FrameLease lease, uint tag, int? tailMetaLength) {
        EnsureUsable();
        if (!lease.IsCurrent) { return new FrameStoreStateError("The FrameStore lease has already ended or is not current."); }
        if (lease.HasUnadvancedBorrow) {
            throw new InvalidOperationException("Advance the outstanding buffer borrow, including Advance(0), before EndAppend.");
        }
        AteliaResult<SizedPtr> result;
        try {
            result = tailMetaLength.HasValue
                ? lease.Builder.EndAppend(tag, tailMetaLength.Value)
                : lease.Builder.EndAppend(tag);
        }
        catch {
            EndLease(lease);
            MarkFaulted();
            throw;
        }
        if (result.IsFailure) { return result.Error!; }
        // No maintenance, callbacks, collections or allocation after normal RBF success.
        var entry = lease.Entry;
        RegisterCompletedOutput(entry, result.Value);
        EndLease(lease);
        return FrameAddress.Create(entry.FileId, result.Value);
    }

    internal void Cancel(FrameLease lease) {
        if (!lease.IsCurrent) { return; }
        var builder = lease.Builder;
        // Invalidate every alias before attempting cancellation; even exceptional cleanup is tried only once.
        EndLease(lease);
        try { builder.Dispose(); }
        catch {
            MarkFaulted();
            throw;
        }
    }

    internal AteliaResult<FrameRead> ReadFrame(FrameAddress address) {
        EnsureUsable();
        _handoffComplete = true;
        if (address == default) { return new FrameStoreStateError("The default FrameAddress cannot be read."); }
        int index = FindInsertionIndex(address.FileId);
        if (index >= _active.Count || _active[index].FileId != address.FileId) {
            return _files.ReadUnowned(address, _markOwnedCleanupFault);
        }
        // The retained handle keeps its checked header qualification, also while a Builder is active.
        var result = _active[index].File!.ReadPooledFrame(address.Ticket);
        if (result.IsFailure) { return result.Error!; }
        var frame = result.Value!;
        try { return new FrameRead(address, frame); }
        catch (Exception primary) {
            CleanupErrors errors = default;
            errors.Add(primary);
            try { frame.Dispose(); }
            catch (Exception cleanup) {
                MarkFaulted();
                errors.Add(cleanup);
            }
            errors.ThrowIfAny();
            throw;
        }
    }

    internal void ConfirmDurable() {
        EnsureWritable();
        DrainStopped();
        for (int i = 0; i < _active.Count; i++) {
            var entry = _active[i];
            if (!entry.NeedsConfirmation) { continue; }
            try { entry.File!.DurableFlush(); }
            catch {
                MarkFaulted();
                throw;
            }
            entry.NeedsConfirmation = false;
        }
    }

    internal void EnsureUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("The FrameStore owner is faulted; dispose it before reopening."); }
    }

    private void EnsureWritable() {
        EnsureUsable();
        if (!_writable) { throw new InvalidOperationException("The FrameStore owner is read-only."); }
        _handoffComplete = true;
    }

    private void MarkFaulted() => _faulted = true;

    private void RejectExhaustionIfNoCandidate() {
        if (_maxPublishedFileId == uint.MaxValue && FindAvailable() is null) {
            throw new InvalidOperationException("FrameStore FileId space is exhausted and no active file is available.");
        }
    }

    private FrameFileEntry? FindAvailable() {
        for (int i = 0; i < _active.Count; i++) {
            if (_active[i].IsAvailable) { return _active[i]; }
        }
        return null;
    }

    private FrameFileEntry SelectOrCreate() {
        var available = FindAvailable();
        if (available is not null) { return available; }
        uint fileId = checked(_maxPublishedFileId + 1);
        // Preallocate everything required for registration before the backend can publish an active file.
        var entry = new FrameFileEntry(fileId);
        _active.EnsureCapacity(checked(_active.Count + 1));
        IRbfFile file;
        try { file = _files.CreateActive(fileId); }
        catch {
            MarkFaulted();
            throw;
        }
        _maxPublishedFileId = fileId;
        entry.File = file;
        entry.CompletedTailOffset = FileHeaderCodec.InitializationBoundary;
        entry.NeedsConfirmation = true;
        entry.Stopped = entry.CompletedTailOffset > RotationThresholdBytes;
        _active.Add(entry);
        return entry;
    }

    private void RegisterCompletedOutput(FrameFileEntry entry, SizedPtr ticket) {
        // Public RBF sizing provides the terminal overhead; the successful ticket provides the end.
        // No fallible getter/delegation remains after inner success, and no wire constant is copied here.
        entry.CompletedTailOffset = ticket.EndOffsetExclusive + _frameEndOverhead;
        entry.NeedsConfirmation = true;
        entry.Stopped = entry.CompletedTailOffset > RotationThresholdBytes;
    }

    private void EndLease(FrameLease lease) {
        lease.Active = false;
        lease.HasUnadvancedBorrow = false;
        lease.Builder = default;
        if (ReferenceEquals(lease.Entry.CurrentLease, lease)) { lease.Entry.CurrentLease = null; }
        if (lease.Counted) {
            lease.Counted = false;
            _outstandingBuilders--;
        }
    }

    private void DrainStopped() {
        for (int i = 0; i < _active.Count;) {
            var entry = _active[i];
            if (!entry.Stopped || entry.CurrentLease is not null || entry.ShortLease) {
                i++;
                continue;
            }
            try {
                var file = entry.File!;
                file.DurableFlush();
                entry.NeedsConfirmation = false;
                // Take the handle slot before Dispose. A failed close is never retried by owner cleanup.
                entry.File = null;
                file.Dispose();
                _files.ArchiveClosed(entry.FileId);
                _active.RemoveAt(i);
            }
            catch {
                MarkFaulted();
                throw;
            }
        }
    }

    private int FindInsertionIndex(uint fileId) {
        int low = 0;
        int high = _active.Count;
        while (low < high) {
            int mid = low + (high - low) / 2;
            if (_active[mid].FileId < fileId) { low = mid + 1; }
            else { high = mid; }
        }
        return low;
    }

    /// <summary>失效所有 Lease，逐个单次清理数据句柄，最后关闭移交的控制锁；不 flush 或归档。</summary>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        for (int i = 0; i < _active.Count; i++) {
            var lease = _active[i].CurrentLease;
            if (lease is not null) { EndLease(lease); }
        }
        CleanupErrors errors = default;
        try {
            for (int i = 0; i < _active.Count; i++) {
                var entry = _active[i];
                var file = entry.File;
                entry.File = null;
                if (file is null) { continue; }
                try { file.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        finally {
            var ownerLock = _ownerLock;
            _ownerLock = null;
            if (ownerLock is not null) {
                try { ownerLock.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
        }
        errors.ThrowIfAny();
    }
}
