using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.HeadStore;

/// <summary>串行使用的不透明 HeadEntry payload 发布 owner；借入一个实际 data FrameStore。</summary>
public sealed partial class HeadStore : IDisposable {
    /// <summary>单条完整 HeadEntry payload 的最大长度，单位 bytes。</summary>
    public const int MaxHeadEntryPayloadLength = 1_048_576;

    /// <summary>单个 tag 名称的最大长度，单位 UTF-16 code units。</summary>
    public const int MaxTagNameCodeUnits = 1024;

    private readonly DataStore _data;
    private readonly StoreIdentity _identity;
    private readonly byte[] _headStoreId;
    private readonly IHeadStoreFiles _files;
    private readonly IHeadStoreTagFiles? _tagFiles;
    private readonly Dictionary<uint, OwnedHead> _ownedHeads = [];
    private readonly Action _markOwnedCleanupFault;
    private readonly HeadStoreTestHooks? _testHooks;
    private IDisposable? _ownerLock;
    private IRbfFile? _pendingHead;
    private uint _autoHeadIdHighWatermark;
    private bool _disposed;
    private bool _faulted;

    /// <summary>接收已完成门/根/锁资格的内部资源；成功后接管句柄，失败由调用者清理。</summary>
    /// <remarks>
    /// 这里重验 borrowed data，不替代 factory 在任何 HeadStore I/O 之前的借入检查。
    /// </remarks>
    internal HeadStore(DataStore data, StoreIdentity headStoreId, StoreIdentity boundDataStoreId,
        uint reservedHeadIdUpperBound, bool readOnly, IHeadStoreFiles files, IDisposable ownerLock,
        HeadStoreTestHooks? testHooks = null, uint? autoHeadIdHighWatermark = null,
        IHeadStoreTagFiles? tagFiles = null, HeadStoreRecoveryLog? recoveryLog = null) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ownerLock);
        if (reservedHeadIdUpperBound == uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(reservedHeadIdUpperBound));
        }
        uint highWatermark = autoHeadIdHighWatermark ?? reservedHeadIdUpperBound;
        if (highWatermark < reservedHeadIdUpperBound) {
            throw new ArgumentOutOfRangeException(nameof(autoHeadIdHighWatermark));
        }
        byte[] identityBytes = new byte[StoreIdentity.EncodedSize];
        if (!headStoreId.TryWrite(identityBytes)) {
            throw new ArgumentException("A nonzero HeadStore identity is required.", nameof(headStoreId));
        }
        data.EnsureUsable();
        if (!readOnly && data.IsReadOnly) {
            throw new ArgumentException("A writable HeadStore requires writable borrowed data.", nameof(data));
        }
        if (!StoreIdentity.TryRead(data.StoreId, out StoreIdentity actualDataId)
            || actualDataId != boundDataStoreId) {
            throw new ArgumentException("The borrowed data identity differs from the checked binding.", nameof(boundDataStoreId));
        }
        _data = data;
        _identity = headStoreId;
        _headStoreId = identityBytes;
        _files = files;
        _tagFiles = tagFiles ?? files as IHeadStoreTagFiles;
        _markOwnedCleanupFault = () => _faulted = true;
        _testHooks = testHooks;
        RecoveryReports = (recoveryLog ?? new HeadStoreRecoveryLog()).Reports;
        IsReadOnly = readOnly;
        ReservedHeadIdUpperBound = reservedHeadIdUpperBound;
        _autoHeadIdHighWatermark = highWatermark;
        _ownerLock = ownerLock;
    }

    /// <summary>在 fresh 根创建空 HeadStore，借入可写 data；预留低号仅用于指定创建。</summary>
    public static HeadStore Create(string rootPath, DataStore data, uint reservedHeadIdUpperBound = 0) =>
        HeadStoreFactory.Create(rootPath, data, reservedHeadIdUpperBound);

    /// <summary>独占打开已有 HeadStore，发现完整正式编号集合并取消合格 private pending。</summary>
    public static HeadStore Open(string rootPath, DataStore data) => HeadStoreFactory.Open(rootPath, data, readOnly: false);

    /// <summary>共享只读打开已有 HeadStore，不恢复或取消 pending。</summary>
    public static HeadStore OpenReadOnly(string rootPath, DataStore data) => HeadStoreFactory.Open(rootPath, data, readOnly: true);

    /// <summary>持久 HeadStore 身份的 16 个 canonical opaque bytes。</summary>
    public ReadOnlySpan<byte> HeadStoreId => _headStoreId;

    /// <summary>本 owner 是否只读。</summary>
    public bool IsReadOnly { get; }

    /// <summary>创建后不可变的预留本地编号上界。</summary>
    public uint ReservedHeadIdUpperBound { get; }

    /// <summary>本 owner 累计的成功物理修尾动作，只读视图随惰性打开追加。</summary>
    /// <remarks>
    /// 初始为空，仅包含成功 RBF open 返回的非 None 动作；既有项和顺序不变。
    /// getter 与保存的视图在 fault/Dispose 后仍可读。固定集合由调用方复制；
    /// 枚举不提供跨调用快照保证。报告不证明发布成立、data 健康或旧调用 Confirmed。
    /// </remarks>
    public IReadOnlyList<HeadStoreRecoveryReport> RecoveryReports { get; }

    /// <summary>检查本 owner 和实际借入 data 的生命周期资格，不执行 I/O 或确认输出。</summary>
    /// <remarks>
    /// 先检查本 owner 的 Dispose/fault，再传播 data 的原异常；纯检查失败不额外停用本 owner。
    /// 允许只读 owner、活跃 data Builder 和现有 visitor；调用方仍须保持 data 存活并串行操作。
    /// </remarks>
    public void EnsureUsable() {
        EnsureOwnerUsable();
        _data.EnsureUsable();
    }

    /// <summary>完整发现全部正式 head 的路径身份；结果自有且只读，不证明 head 内容健康。</summary>
    public AteliaResult<IReadOnlyList<HeadId>> ListHeads() {
        EnsureOwnerUsable();
        AteliaResult<IReadOnlyList<uint>> listed;
        try {
            listed = _files.ListHeadIds(_markOwnedCleanupFault);
            EnsureOwnerUsable();
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
        if (listed.IsFailure) { return listed.Error!; }
        IReadOnlyList<uint> locals = listed.Value!;
        var heads = new HeadId[locals.Count];
        for (int index = 0; index < heads.Length; index++) {
            heads[index] = HeadId.Create(_identity, locals[index]);
        }
        return AteliaResult<IReadOnlyList<HeadId>>.Success(new HeadIdList(heads));
    }

    /// <summary>在自动区用 H+1 创建无来源 head，初始化完整 payload 后一次发布整个文件。</summary>
    public AteliaResult<HeadEntryId> CreateHead(ReadOnlySpan<byte> payload, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (_autoHeadIdHighWatermark == uint.MaxValue) {
            return new HeadStoreRuntimeError("HeadIdExhausted", "The automatic head identifier domain is exhausted.");
        }
        return CreateNewHead(checked(_autoHeadIdHighWatermark + 1), payload, automatic: true, ref outcome);
    }

    /// <summary>在预留区 create-only 创建指定 head；已占目标不能覆盖、复用或认领。</summary>
    public AteliaResult<HeadEntryId> CreateHead(uint destLocalHeadId, ReadOnlySpan<byte> payload,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (destLocalHeadId == 0 || destLocalHeadId > ReservedHeadIdUpperBound) {
            return new HeadStoreRuntimeError("ArgumentError", "A specified head identifier must belong to the reserved low-number domain.");
        }
        return CreateNewHead(destLocalHeadId, payload, automatic: false, ref outcome);
    }

    private AteliaResult<HeadEntryId> CreateNewHead(uint localId, ReadOnlySpan<byte> payload,
        bool automatic, scoped ref PublicationOutcome outcome) {
        if (payload.Length is < 1 or > MaxHeadEntryPayloadLength) {
            return new HeadStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }
        byte[] captured = payload.ToArray();
        var prepared = PrepareNewTarget(localId);
        if (prepared.IsFailure) { return prepared.Error!; }
        return PublishNewHead(prepared.Value, captured, automatic, null, null, ref outcome);
    }

    // All callers supply privately captured owned bytes; this synchronous helper never retains the span.
    private AteliaResult<HeadEntryId> PublishNewHead(QualifiedHeadTarget target, ReadOnlySpan<byte> captured,
        bool automatic, ForkOrigin? origin, IRbfFile? sourceFile, scoped ref PublicationOutcome outcome) {
        int headerPayloadSize = origin.HasValue
            ? HeadHeaderCodec.WithOriginPayloadSize
            : HeadHeaderCodec.NoOriginPayloadSize;
        var headerSize = RbfFile.MeasureWriteSize(headerPayloadSize);
        if (headerSize.IsFailure) { return headerSize.Error!; }
        var entrySize = RbfFile.MeasureWriteSize(captured.Length, RecordMetadataCodec.EncodedSize);
        if (entrySize.IsFailure) { return entrySize.Error!; }
        long headerOffset = RbfScanBoundary.Empty.EndExclusive;
        long entryOffset = headerOffset + headerSize.Value.AppendLength;
        if (!SizedPtr.TryCreate(headerOffset, headerSize.Value.FrameLength, out _)
            || !SizedPtr.TryCreate(entryOffset, entrySize.Value.FrameLength, out _)) {
            return new HeadStoreRuntimeError("HeadCapacityExceeded", "The initial head frames cannot represent complete tickets.");
        }
        uint localId = target.LocalId;
        HeadId headId = HeadId.Create(_identity, localId);
        Span<byte> header = stackalloc byte[headerPayloadSize];
        if (!HeadHeaderCodec.TryWrite(_identity, headId, origin, header)) {
            throw new InvalidOperationException("Cannot encode the prepared new head header.");
        }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> tailMeta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(recordedAt, tailMeta);

        try {
            if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
            else { _data.ConfirmDurable(); }
            // The exact source is confirmed after data, before any private child output.
            sourceFile?.DurableFlush();
            _pendingHead = _files.CreatePending(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            // All pre-I/O rejections were prepared above. A private Append rejection is a plan failure,
            // so Unwrap stops this owner instead of leaving a healthy owner with an occupied pending.
            _pendingHead.Append(HeadRecordCodec.HeaderTag, header).Unwrap();
            SizedPtr entryTicket = _pendingHead.Append(HeadRecordCodec.SingleEntryTag, captured, tailMeta).Unwrap();
            _pendingHead.DurableFlush();
            IRbfFile toClose = _pendingHead;
            _pendingHead = null;
            toClose.Dispose();
            _testHooks?.BeforeNewHeadRename?.Invoke();
            outcome = PublicationOutcome.Unknown;
            _files.PublishPending(target, _markOwnedCleanupFault);
            outcome = PublicationOutcome.Confirmed;
            if (automatic) { _autoHeadIdHighWatermark = localId; }
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return HeadEntryId.Create(headId, entryTicket);
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<QualifiedHeadTarget> PrepareNewTarget(uint localId) {
        try {
            var prepared = _files.PrepareNewHead(localId, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            return prepared;
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
    }

    /// <summary>读取此库指定非零本地编号的 checked 当前Entry。</summary>
    public AteliaResult<HeadEntry> ReadHead(uint localHeadId) {
        EnsureOwnerUsable();
        if (localHeadId == 0) { return InvalidHead(); }
        return ReadCurrent(HeadId.Create(_identity, localHeadId));
    }

    /// <summary>读取最新 checked 发布Entry；合法未提交 CU 不进入当前值，必要错误仍阻断回退。</summary>
    public AteliaResult<HeadEntry> ReadHead(HeadId headId) {
        EnsureOwnerUsable();
        if (!headId.BelongsTo(_identity)) { return InvalidHead(); }
        return ReadCurrent(headId);
    }

    /// <summary>整体替换 HeadEntry payload：先确认 data，再追加并确认一条完整 SingleEntry。</summary>
    /// <remarks>正常失败保持 NotAttempted；异常通过必选 out 保留本次发布证据。</remarks>
    public AteliaResult<HeadEntryId> PublishHead(HeadId headId, ReadOnlySpan<byte> payload,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new HeadStoreRuntimeError("ReadOnly", "This HeadStore owner is read-only."); }
        if (!headId.BelongsTo(_identity)) { return InvalidHead(); }
        if (payload.Length is < 1 or > MaxHeadEntryPayloadLength) {
            return new HeadStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }

        // Capture only the effective slice. Allocation and all definite preparation precede the barrier.
        byte[] captured = payload.ToArray();
        return PublishCapturedHead(headId, captured, ref outcome);
    }

    // Public admission and private input capture are complete; both single-head entry paths reuse this kernel.
    private AteliaResult<HeadEntryId> PublishCapturedHead(HeadId headId, ReadOnlySpan<byte> captured,
        scoped ref PublicationOutcome outcome) {
        var measured = RbfFile.MeasureWriteSize(captured.Length, RecordMetadataCodec.EncodedSize);
        if (measured.IsFailure) { return measured.Error!; }
        var opened = GetOrOpen(headId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        long tail = file.TailOffset;
        if (!SizedPtr.TryCreate(tail, measured.Value.FrameLength, out _)) {
            return new HeadStoreRuntimeError("HeadCapacityExceeded", "The real head tail cannot represent another complete Entry ticket.");
        }
        var checkedHeader = HeadRecordReader.CheckHeader(file, _identity, headId);
        if (checkedHeader.IsFailure) { return checkedHeader.Error!; }
        var current = CheckLocalCurrent(file, checkedHeader.Value);
        if (current.IsFailure) { return current.Error!; }
        long recordedAt = _testHooks?.GetRecordedAtUnixMs?.Invoke()
            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> tailMeta = stackalloc byte[RecordMetadataCodec.EncodedSize];
        RecordMetadataCodec.TryWrite(recordedAt, tailMeta);

        try {
            if (_testHooks?.ConfirmDataDurable is { } confirm) { confirm(); }
            else { _data.ConfirmDurable(); }
            outcome = PublicationOutcome.Unknown;
            var append = file.Append(HeadRecordCodec.SingleEntryTag, captured, tailMeta);
            if (append.IsFailure) {
                // The public RBF failure Result proves pre-I/O rejection for this sole output attempt.
                outcome = PublicationOutcome.NotAttempted;
                return append.Error!;
            }
            SizedPtr ticket = append.Value;
            _testHooks?.AfterHeadRecordAppended?.Invoke(append.Value);
            file.DurableFlush();
            outcome = PublicationOutcome.Confirmed;
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return HeadEntryId.Create(headId, ticket);
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<HeadEntry> ReadCurrent(HeadId headId) {
        var opened = GetOrOpen(headId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        var header = HeadRecordReader.CheckHeader(file, _identity, headId);
        if (header.IsFailure) { return header.Error!; }
        return ReadPublishedCurrent(file, header.Value);
    }

    private AteliaResult<IRbfFile> GetOrOpen(uint localId) {
        if (_historyHeadId == localId && _historyHead is { } historyFile) {
            return AteliaResult<IRbfFile>.Success(historyFile);
        }
        if (_ownedHeads.TryGetValue(localId, out OwnedHead? cached)) { return AteliaResult<IRbfFile>.Success(cached.File!); }
        // Path qualification errors are ordinary query failures; they do not start writable recovery.
        QualifiedHeadTarget target;
        try {
            var resolved = _files.ResolveHead(localId, _markOwnedCleanupFault);
            // A cleanup marker cannot be followed by a normal Result or another output attempt.
            EnsureOwnerUsable();
            if (resolved.IsFailure) { return resolved.Error!; }
            target = resolved.Value;
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
        IRbfFile file;
        try { file = _files.OpenHead(target, IsReadOnly, _markOwnedCleanupFault); }
        catch (Exception error) {
            if (!IsReadOnly || _faulted) { FaultAndCleanup(error); }
            throw;
        }

        IRbfFile? pendingTransfer = file;
        try {
            EnsureOwnerUsable();
            _ownedHeads.Add(localId, new OwnedHead(file));
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

    private static AteliaError InvalidHead() =>
        new HeadStoreRuntimeError("ArgumentError", "A nondefault HeadId bound to this HeadStore is required.");

    private void EnsureOwnerUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("HeadStore is faulted; dispose and reopen it."); }
    }

    private AteliaError? GetMutationRejection() {
        EnsureOwnerUsable();
        if (_tagScanActive) { return TagScanActive(); }
        return _historyActive
            ? new HeadStoreRuntimeError("HistoryActive", "Mutations are not allowed during an active history visitor.")
            : null;
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
            CloseTagScanBucket(ref errors);
            CloseTagScanTargets(ref errors);
            CloseConditionalPeer(ref errors);
            CloseHistoryTemporary(ref errors);
            CloseTagOperation(ref errors);
            IRbfFile? pending = _pendingHead;
            _pendingHead = null;
            try { pending?.Dispose(); }
            catch (Exception error) { _faulted = true; errors.Add(error); }
            // Enumerate the dictionary struct directly: Values can allocate its collection on first access.
            foreach (var pair in _ownedHeads) {
                OwnedHead entry = pair.Value;
                IRbfFile? toClose = entry.File;
                entry.File = null;
                try { toClose?.Dispose(); }
                catch (Exception error) { _faulted = true; errors.Add(error); }
            }
            _ownedHeads.Clear();
        }
        finally {
            IDisposable? ownerLock = _ownerLock;
            _ownerLock = null;
            try { ownerLock?.Dispose(); }
            catch (Exception error) { _faulted = true; errors.Add(error); }
        }
    }

    /// <summary>单次关闭全部 owned head 资源并最后释放根锁；不确认或 Dispose 借入 data。</summary>
    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        CleanupErrors errors = default;
        CleanupOwned(ref errors);
        errors.ThrowIfAny();
    }

    private sealed class OwnedHead(IRbfFile file) {
        internal IRbfFile? File = file;
    }

    // Expose only read interfaces; ICollection.SyncRoot must not reveal the owned backing array.
    private sealed class HeadIdList(HeadId[] entries) : IReadOnlyList<HeadId> {
        private readonly HeadId[] _entries = entries;
        public int Count => _entries.Length;
        public HeadId this[int index] => _entries[index];
        public IEnumerator<HeadId> GetEnumerator() {
            for (int index = 0; index < _entries.Length; index++) { yield return _entries[index]; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
