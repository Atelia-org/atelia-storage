using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Storage;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore;

/// <summary>串行使用的不透明根 payload 发布 owner；借入一个实际 data FrameStore。</summary>
public sealed partial class VersionStore : IDisposable {
    /// <summary>单条完整 root payload 的最大长度，单位 bytes。</summary>
    public const int MaxRootPayloadLength = 1_048_576;

    /// <summary>单个 tag 名称的最大长度，单位 UTF-16 code units。</summary>
    public const int MaxTagNameCodeUnits = 1024;

    private readonly DataStore _data;
    private readonly StoreIdentity _identity;
    private readonly byte[] _versionStoreId;
    private readonly IVersionStoreFiles _files;
    private readonly IVersionStoreTagFiles? _tagFiles;
    private readonly Dictionary<uint, OwnedRef> _ownedRefs = [];
    private readonly Action _markOwnedCleanupFault;
    private readonly VersionStoreTestHooks? _testHooks;
    private IDisposable? _ownerLock;
    private IRbfFile? _pendingRef;
    private uint _autoRefIdHighWatermark;
    private bool _disposed;
    private bool _faulted;

    /// <summary>接收已完成门/根/锁资格的内部资源；成功后接管句柄，失败由调用者清理。</summary>
    /// <remarks>
    /// 这里重验 borrowed data，不替代 factory 在任何 VS I/O 之前的借入检查。
    /// </remarks>
    internal VersionStore(DataStore data, StoreIdentity versionStoreId, StoreIdentity boundDataStoreId,
        uint reservedRefIdUpperBound, bool readOnly, IVersionStoreFiles files, IDisposable ownerLock,
        VersionStoreTestHooks? testHooks = null, uint? autoRefIdHighWatermark = null,
        IVersionStoreTagFiles? tagFiles = null, VersionStoreRecoveryLog? recoveryLog = null) {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ownerLock);
        if (reservedRefIdUpperBound == uint.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(reservedRefIdUpperBound));
        }
        uint highWatermark = autoRefIdHighWatermark ?? reservedRefIdUpperBound;
        if (highWatermark < reservedRefIdUpperBound) {
            throw new ArgumentOutOfRangeException(nameof(autoRefIdHighWatermark));
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
        _tagFiles = tagFiles ?? files as IVersionStoreTagFiles;
        _markOwnedCleanupFault = () => _faulted = true;
        _testHooks = testHooks;
        RecoveryReports = (recoveryLog ?? new VersionStoreRecoveryLog()).Reports;
        IsReadOnly = readOnly;
        ReservedRefIdUpperBound = reservedRefIdUpperBound;
        _autoRefIdHighWatermark = highWatermark;
        _ownerLock = ownerLock;
    }

    /// <summary>在 fresh 根创建空 VersionStore，借入可写 data；预留低号仅用于指定创建。</summary>
    public static VersionStore Create(string rootPath, DataStore data, uint reservedRefIdUpperBound = 0) =>
        VersionStoreFactory.Create(rootPath, data, reservedRefIdUpperBound);

    /// <summary>独占打开已有 VersionStore，发现完整正式编号集合并取消合格 private pending。</summary>
    public static VersionStore Open(string rootPath, DataStore data) => VersionStoreFactory.Open(rootPath, data, readOnly: false);

    /// <summary>共享只读打开已有 VersionStore，不恢复或取消 pending。</summary>
    public static VersionStore OpenReadOnly(string rootPath, DataStore data) => VersionStoreFactory.Open(rootPath, data, readOnly: true);

    /// <summary>持久 VersionStore 身份的 16 个 canonical opaque bytes。</summary>
    public ReadOnlySpan<byte> VersionStoreId => _versionStoreId;

    /// <summary>本 owner 是否只读。</summary>
    public bool IsReadOnly { get; }

    /// <summary>创建后不可变的预留本地编号上界。</summary>
    public uint ReservedRefIdUpperBound { get; }

    /// <summary>本 owner 累计的成功物理修尾动作，只读视图随惰性打开追加。</summary>
    /// <remarks>
    /// 初始为空，仅包含成功 RBF open 返回的非 None 动作；既有项和顺序不变。
    /// getter 与保存的视图在 fault/Dispose 后仍可读。固定集合由调用方复制；
    /// 枚举不提供跨调用快照保证。报告不证明发布成立、data 健康或旧调用 Confirmed。
    /// </remarks>
    public IReadOnlyList<VersionStoreRecoveryReport> RecoveryReports { get; }

    /// <summary>检查本 owner 和实际借入 data 的生命周期资格，不执行 I/O 或确认输出。</summary>
    /// <remarks>
    /// 先检查本 owner 的 Dispose/fault，再传播 data 的原异常；纯检查失败不额外停用本 owner。
    /// 允许只读 owner、活跃 data Builder 和现有 visitor；调用方仍须保持 data 存活并串行操作。
    /// </remarks>
    public void EnsureUsable() {
        EnsureOwnerUsable();
        _data.EnsureUsable();
    }

    /// <summary>完整发现全部正式 ref 的路径身份；结果自有且只读，不证明 ref 内容健康。</summary>
    public AteliaResult<IReadOnlyList<RefId>> ListRefs() {
        EnsureOwnerUsable();
        AteliaResult<IReadOnlyList<uint>> listed;
        try {
            listed = _files.ListRefIds(_markOwnedCleanupFault);
            EnsureOwnerUsable();
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
        if (listed.IsFailure) { return listed.Error!; }
        IReadOnlyList<uint> locals = listed.Value!;
        var refs = new RefId[locals.Count];
        for (int index = 0; index < refs.Length; index++) {
            refs[index] = RefId.Create(_identity, locals[index]);
        }
        return AteliaResult<IReadOnlyList<RefId>>.Success(new RefIdList(refs));
    }

    /// <summary>在自动区用 H+1 创建无来源 ref，初始化完整 payload 后一次发布整个文件。</summary>
    public AteliaResult<RefRevision> CreateRef(ReadOnlySpan<byte> payload, out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (_autoRefIdHighWatermark == uint.MaxValue) {
            return new VersionStoreRuntimeError("RefIdExhausted", "The automatic ref identifier domain is exhausted.");
        }
        return CreateNewRef(checked(_autoRefIdHighWatermark + 1), payload, automatic: true, ref outcome);
    }

    /// <summary>在预留区 create-only 创建指定 ref；已占目标不能覆盖、复用或认领。</summary>
    public AteliaResult<RefRevision> CreateRef(uint destLocalRefId, ReadOnlySpan<byte> payload,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (destLocalRefId == 0 || destLocalRefId > ReservedRefIdUpperBound) {
            return new VersionStoreRuntimeError("ArgumentError", "A specified ref identifier must belong to the reserved low-number domain.");
        }
        return CreateNewRef(destLocalRefId, payload, automatic: false, ref outcome);
    }

    private AteliaResult<RefRevision> CreateNewRef(uint localId, ReadOnlySpan<byte> payload,
        bool automatic, scoped ref PublicationOutcome outcome) {
        if (payload.Length is < 1 or > MaxRootPayloadLength) {
            return new VersionStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }
        byte[] captured = payload.ToArray();
        var prepared = PrepareNewTarget(localId);
        if (prepared.IsFailure) { return prepared.Error!; }
        return PublishNewRef(prepared.Value, captured, automatic, null, null, ref outcome);
    }

    // All callers supply privately captured owned bytes; this synchronous helper never retains the span.
    private AteliaResult<RefRevision> PublishNewRef(QualifiedRefTarget target, ReadOnlySpan<byte> captured,
        bool automatic, ForkOrigin? origin, IRbfFile? sourceFile, scoped ref PublicationOutcome outcome) {
        int headerPayloadSize = origin.HasValue
            ? RefHeaderCodec.WithOriginPayloadSize
            : RefHeaderCodec.NoOriginPayloadSize;
        var headerSize = RbfFile.MeasureWriteSize(headerPayloadSize);
        if (headerSize.IsFailure) { return headerSize.Error!; }
        var snapshotSize = RbfFile.MeasureWriteSize(captured.Length, RecordMetadataCodec.EncodedSize);
        if (snapshotSize.IsFailure) { return snapshotSize.Error!; }
        long headerOffset = RbfScanBoundary.Empty.EndExclusive;
        long snapshotOffset = headerOffset + headerSize.Value.AppendLength;
        if (!SizedPtr.TryCreate(headerOffset, headerSize.Value.FrameLength, out _)
            || !SizedPtr.TryCreate(snapshotOffset, snapshotSize.Value.FrameLength, out _)) {
            return new VersionStoreRuntimeError("RefCapacityExceeded", "The initial ref frames cannot represent complete tickets.");
        }
        uint localId = target.LocalId;
        RefId refId = RefId.Create(_identity, localId);
        Span<byte> header = stackalloc byte[headerPayloadSize];
        if (!RefHeaderCodec.TryWrite(_identity, refId, origin, header)) {
            throw new InvalidOperationException("Cannot encode the prepared new ref header.");
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
            _pendingRef = _files.CreatePending(_markOwnedCleanupFault);
            EnsureOwnerUsable();
            // All pre-I/O rejections were prepared above. A private Append rejection is a plan failure,
            // so Unwrap stops this owner instead of leaving a healthy owner with an occupied pending.
            _pendingRef.Append(RefRecordCodec.HeaderTag, header).Unwrap();
            SizedPtr snapshotTicket = _pendingRef.Append(RefRecordCodec.SnapshotTag, captured, tailMeta).Unwrap();
            _pendingRef.DurableFlush();
            IRbfFile toClose = _pendingRef;
            _pendingRef = null;
            toClose.Dispose();
            _testHooks?.BeforeNewRefRename?.Invoke();
            outcome = PublicationOutcome.Unknown;
            _files.PublishPending(target, _markOwnedCleanupFault);
            outcome = PublicationOutcome.Confirmed;
            if (automatic) { _autoRefIdHighWatermark = localId; }
            _testHooks?.AfterPublicationConfirmed?.Invoke();
            return RefRevision.Create(refId, snapshotTicket);
        }
        catch (Exception error) {
            FaultAndCleanup(error);
            throw;
        }
    }

    private AteliaResult<QualifiedRefTarget> PrepareNewTarget(uint localId) {
        try {
            var prepared = _files.PrepareNewRef(localId, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            return prepared;
        }
        catch (Exception error) {
            if (_faulted) { FaultAndCleanup(error); }
            throw;
        }
    }

    /// <summary>读取此库指定非零本地编号的 checked 当前快照。</summary>
    public AteliaResult<RefBytesSnapshot> ReadRef(uint localRefId) {
        EnsureOwnerUsable();
        if (localRefId == 0) { return InvalidRef(); }
        return ReadCurrent(RefId.Create(_identity, localRefId));
    }

    /// <summary>读取最新 checked 发布快照；合法未提交 CU 不进入当前值，必要错误仍阻断回退。</summary>
    public AteliaResult<RefBytesSnapshot> ReadRef(RefId refId) {
        EnsureOwnerUsable();
        if (!refId.BelongsTo(_identity)) { return InvalidRef(); }
        return ReadCurrent(refId);
    }

    /// <summary>整体替换 root payload：先确认 data，再追加并确认一条完整 Snapshot。</summary>
    /// <remarks>正常失败保持 NotAttempted；异常通过必选 out 保留本次发布证据。</remarks>
    public AteliaResult<RefRevision> PublishRef(RefId refId, ReadOnlySpan<byte> payload,
        out PublicationOutcome outcome) {
        outcome = PublicationOutcome.NotAttempted;
        AteliaError? rejection = GetMutationRejection();
        if (rejection is not null) { return rejection; }
        if (IsReadOnly) { return new VersionStoreRuntimeError("ReadOnly", "This VersionStore owner is read-only."); }
        if (!refId.BelongsTo(_identity)) { return InvalidRef(); }
        if (payload.Length is < 1 or > MaxRootPayloadLength) {
            return new VersionStoreRuntimeError("ArgumentError", "Root payload length must be between 1 and 1,048,576 bytes.");
        }

        // Capture only the effective slice. Allocation and all definite preparation precede the barrier.
        byte[] captured = payload.ToArray();
        return PublishCapturedRef(refId, captured, ref outcome);
    }

    // Public admission and private input capture are complete; both single-ref entry paths reuse this kernel.
    private AteliaResult<RefRevision> PublishCapturedRef(RefId refId, ReadOnlySpan<byte> captured,
        scoped ref PublicationOutcome outcome) {
        var measured = RbfFile.MeasureWriteSize(captured.Length, RecordMetadataCodec.EncodedSize);
        if (measured.IsFailure) { return measured.Error!; }
        var opened = GetOrOpen(refId.LocalId);
        if (opened.IsFailure) { return opened.Error!; }
        IRbfFile file = opened.Value!;
        long tail = file.TailOffset;
        if (!SizedPtr.TryCreate(tail, measured.Value.FrameLength, out _)) {
            return new VersionStoreRuntimeError("RefCapacityExceeded", "The real ref tail cannot represent another complete Snapshot ticket.");
        }
        var checkedHeader = RefRecordReader.CheckHeader(file, _identity, refId);
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
            var append = file.Append(RefRecordCodec.SnapshotTag, captured, tailMeta);
            if (append.IsFailure) {
                // The public RBF failure Result proves pre-I/O rejection for this sole output attempt.
                outcome = PublicationOutcome.NotAttempted;
                return append.Error!;
            }
            SizedPtr ticket = append.Value;
            _testHooks?.AfterRefRecordAppended?.Invoke(append.Value);
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
        IRbfFile file = opened.Value!;
        var header = RefRecordReader.CheckHeader(file, _identity, refId);
        if (header.IsFailure) { return header.Error!; }
        return ReadPublishedCurrent(file, header.Value);
    }

    private AteliaResult<IRbfFile> GetOrOpen(uint localId) {
        if (_historyRefId == localId && _historyRef is { } historyFile) {
            return AteliaResult<IRbfFile>.Success(historyFile);
        }
        if (_ownedRefs.TryGetValue(localId, out OwnedRef? cached)) { return AteliaResult<IRbfFile>.Success(cached.File!); }
        // Path qualification errors are ordinary query failures; they do not start writable recovery.
        QualifiedRefTarget target;
        try {
            var resolved = _files.ResolveRef(localId, _markOwnedCleanupFault);
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
        try { file = _files.OpenRef(target, IsReadOnly, _markOwnedCleanupFault); }
        catch (Exception error) {
            if (!IsReadOnly || _faulted) { FaultAndCleanup(error); }
            throw;
        }

        IRbfFile? pendingTransfer = file;
        try {
            EnsureOwnerUsable();
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

    private void EnsureOwnerUsable() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) { throw new InvalidOperationException("VersionStore is faulted; dispose and reopen it."); }
    }

    private AteliaError? GetMutationRejection() {
        EnsureOwnerUsable();
        if (_tagScanActive) { return TagScanActive(); }
        return _historyActive
            ? new VersionStoreRuntimeError("HistoryActive", "Mutations are not allowed during an active history visitor.")
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
            IRbfFile? pending = _pendingRef;
            _pendingRef = null;
            try { pending?.Dispose(); }
            catch (Exception error) { _faulted = true; errors.Add(error); }
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

    // Expose only read interfaces; ICollection.SyncRoot must not reveal the owned backing array.
    private sealed class RefIdList(RefId[] entries) : IReadOnlyList<RefId> {
        private readonly RefId[] _entries = entries;
        public int Count => _entries.Length;
        public RefId this[int index] => _entries[index];
        public IEnumerator<RefId> GetEnumerator() {
            for (int index = 0; index < _entries.Length; index++) { yield return _entries[index]; }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
