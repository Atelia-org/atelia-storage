using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore;

/// <summary>串行使用的不透明根 payload 发布 owner；借入一个实际 data FrameStore。</summary>
/// <remarks>本施工片只有内部 owner 接线，目录创建/打开工厂尚未实施。</remarks>
public sealed class VersionStore : IDisposable {
    /// <summary>单条完整 root payload 的最大长度，单位 bytes。</summary>
    public const int MaxRootPayloadLength = 1_048_576;

    private readonly DataStore _data;
    private readonly StoreIdentity _identity;
    private readonly byte[] _versionStoreId;
    private readonly IVersionStoreFiles _files;
    private readonly Dictionary<uint, OwnedRef> _ownedRefs = [];
    private readonly Action _markOwnedCleanupFault;
    private readonly VersionStoreTestHooks? _testHooks;
    private IDisposable? _ownerLock;
    private bool _disposed;
    private bool _faulted;

    /// <summary>接收已完成门/根/锁资格的内部资源；成功后接管句柄，失败由调用者清理。</summary>
    /// <remarks>
    /// 这里重验 borrowed data，不替代未来 factory 在任何 VS I/O 之前的借入检查。
    /// 本轮测试接线不具有生产目录或平台资格。
    /// </remarks>
    internal VersionStore(DataStore data, StoreIdentity versionStoreId, StoreIdentity boundDataStoreId,
        uint reservedRefIdUpperBound, bool readOnly, IVersionStoreFiles files, IDisposable ownerLock,
        VersionStoreTestHooks? testHooks = null) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ownerLock);
        if (reservedRefIdUpperBound == uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(reservedRefIdUpperBound));
        }
        byte[] identityBytes = new byte[StoreIdentity.EncodedSize];
        if (!versionStoreId.TryWrite(identityBytes)) {
            throw new ArgumentException("A nonzero VersionStore identity is required.", nameof(versionStoreId));
        }
        data.EnsureUsable();
        if (!readOnly && data.IsReadOnly) {
            throw new ArgumentException("A writable VersionStore requires writable borrowed data.", nameof(data));
        }
        if (!StoreIdentity.TryRead(data.StoreId, out StoreIdentity actualDataId)
            || actualDataId != boundDataStoreId) {
            throw new ArgumentException("The borrowed data identity differs from the checked binding.", nameof(boundDataStoreId));
        }
        _data = data;
        _identity = versionStoreId;
        _versionStoreId = identityBytes;
        _files = files;
        _markOwnedCleanupFault = () => _faulted = true;
        _testHooks = testHooks;
        IsReadOnly = readOnly;
        ReservedRefIdUpperBound = reservedRefIdUpperBound;
        _ownerLock = ownerLock;
    }

    /// <summary>持久 VersionStore 身份的 16 个 canonical opaque bytes。</summary>
    public ReadOnlySpan<byte> VersionStoreId => _versionStoreId;

    /// <summary>本 owner 是否只读。</summary>
    public bool IsReadOnly { get; }

    /// <summary>创建后不可变的预留本地编号上界。</summary>
    public uint ReservedRefIdUpperBound { get; }

    /// <summary>读取此库指定非零本地编号的 checked 当前快照。</summary>
    public AteliaResult<RefBytesSnapshot> ReadRef(uint localRefId) {
        EnsureUsable();
        if (localRefId == 0) { return InvalidRef(); }
        return ReadCurrent(RefId.Create(_identity, localRefId));
    }

    /// <summary>读取此库已发布 ref 的 checked 当前快照，不回退到旧记录。</summary>
    public AteliaResult<RefBytesSnapshot> ReadRef(RefId refId) {
        EnsureUsable();
        if (!refId.BelongsTo(_identity)) { return InvalidRef(); }
        return ReadCurrent(refId);
    }

    /// <summary>整体替换 root payload：先确认 data，再追加并确认一条完整 Snapshot。</summary>
    /// <remarks>正常失败保持 NotAttempted；异常通过必选 out 保留本次发布证据。</remarks>
    public AteliaResult<RefRevision> PublishRef(RefId refId, ReadOnlySpan<byte> payload,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        EnsureUsable();
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (!refId.BelongsTo(_identity)) { return InvalidRef(); }
        if (payload.Length is < 1 or > MaxRootPayloadLength) {
            return new VersionStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }

        // Capture only the effective slice. Allocation and all definite preparation precede the barrier.
        byte[] captured = payload.ToArray();
        var measured = RbfFile.MeasureWriteSize(captured.Length, RecordMetadataCodec.EncodedSize);
        if (measured.IsFailure) { return measured.Error!; }
        var opened = GetOrOpen(refId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        long tail = file.TailOffset;
        if (!SizedPtr.TryCreate(tail, measured.Value.FrameLength, out _)) {
            return new VersionStoreRuntimeError("RefCapacityExceeded", "The real ref tail cannot represent another complete Snapshot ticket.");
        }
        var current = RefRecordReader.ReadCurrent(file, _identity, refId);
        if (current.IsFailure) { return current.Error!; }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> tailMeta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(recordedAt, tailMeta);

        try {
            if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
            else { _data.ConfirmDurable(); }
            outcome = PublicationOutcome.Unknown;
            var append = file.Append(RefRecordCodec.SnapshotTag, captured, tailMeta);
            if (append.IsFailure) {
                // The public RBF failure Result proves pre-I/O rejection for this sole output attempt.
                outcome = PublicationOutcome.NotAttempted;
                return append.Error!;
            }
            SizedPtr ticket = append.Value;
            file.DurableFlush();
            outcome = PublicationOutcome.Confirmed;
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return RefRevision.Create(refId, ticket);
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<RefBytesSnapshot> ReadCurrent(RefId refId) {
        var opened = GetOrOpen(refId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        return RefRecordReader.ReadCurrent(opened.Value!, _identity, refId);
    }

    private AteliaResult<IRbfFile> GetOrOpen(uint localId) {
        if (_ownedRefs.TryGetValue(localId, out OwnedRef? cached)) { return AteliaResult<IRbfFile>.Success(cached.File!); }
        // Path qualification errors are ordinary query failures; they do not start writable recovery.
        QualifiedRefTarget target;
        try {
            var resolved = _files.ResolveRef(localId, _markOwnedCleanupFault);
            // A cleanup marker cannot be followed by a normal Result or another output attempt.
            EnsureUsable();
            if (resolved.IsFailure) { return resolved.Error!; }
            target = resolved.Value;
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
        IRbfFile file;
        try { file = _files.OpenRef(target, IsReadOnly, _markOwnedCleanupFault); }
        catch (Exception error) {
            if (!IsReadOnly || _faulted) { FaultAndCleanup(error); }
            throw;
        }

        IRbfFile? pendingTransfer = file;
        try {
            EnsureUsable();
            _ownedRefs.Add(localId, new OwnedRef(file));
            pendingTransfer = null;
            return AteliaResult<IRbfFile>.Success(file);
        }
        catch (Exception error) {
            CleanupErrors errors = default;
            errors.Add(error);
            var toClose = pendingTransfer;
            pendingTransfer = null;
            try { toClose?.Dispose(); }
            catch (Exception cleanupError) { _faulted = true; errors.Add(cleanupError); }
            if (_faulted) { CleanupOwned(ref errors); }
            errors.ThrowIfAny();
            throw;
        }
    }

    private static AteliaError InvalidRef() =>
        new VersionStoreRuntimeError("ArgumentError", "A nondefault RefId bound to this VersionStore is required.");

    private void EnsureUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("VersionStore is faulted; dispose and reopen it."); }
    }

    private void FaultAndCleanup(Exception primary) {
        _faulted = true;
        CleanupErrors errors = default;
        errors.Add(primary);
        CleanupOwned(ref errors);
        errors.ThrowIfAny();
    }

    private void CleanupOwned(ref CleanupErrors errors) {
        try {
            // Enumerate the dictionary struct directly: Values can allocate its collection on first access.
            foreach (var pair in _ownedRefs) {
                OwnedRef entry = pair.Value;
                IRbfFile? toClose = entry.File;
                entry.File = null;
                try { toClose?.Dispose(); }
                catch (Exception error) { _faulted = true; errors.Add(error); }
            }
            _ownedRefs.Clear();
        }
        finally {
            IDisposable? ownerLock = _ownerLock;
            _ownerLock = null;
            try { ownerLock?.Dispose(); }
            catch (Exception error) { _faulted = true; errors.Add(error); }
        }
    }

    /// <summary>单次关闭全部 owned ref 资源并最后释放根锁；不确认或 Dispose 借入 data。</summary>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        CleanupErrors errors = default;
        CleanupOwned(ref errors);
        errors.ThrowIfAny();
    }

    private sealed class OwnedRef(IRbfFile file) {
        internal IRbfFile? File = file;
    }
}
