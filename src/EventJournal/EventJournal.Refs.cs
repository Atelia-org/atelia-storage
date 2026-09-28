using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public readonly record struct CommitToRefOutcome(RefId RefId, EventAddress EventAddress);

public sealed partial class EventJournal {
    public AteliaResult<RefId> OpenBranch(string branchName) {
        ThrowIfDisposed();
        var nameError = ValidateBranchName(branchName);
        if (nameError is not null) { return nameError; }

        if (_branches.TryGetValue(branchName, out RefId refId)) { return refId; }

        return new EventJournalError(
            "BranchNotFound",
            $"Branch '{branchName}' is not bound to an active ref.",
            "Create the branch first, or list branches to inspect active names."
        );
    }

    public IReadOnlyList<string> ListBranches() {
        ThrowIfDisposed();
        var names = _branches.Keys.ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public AteliaResult<RefId> CreateBranch(string branchName, EventAddress? startPoint, uint reasonKind = 0) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var nameError = ValidateBranchName(branchName);
        if (nameError is not null) { return nameError; }
        if (_branches.ContainsKey(branchName)) { return BranchAlreadyExistsError(branchName); }
        if (!TryValidateTarget(startPoint, out AteliaError? startError)) {
            return new EventJournalError(
                "BranchStartPointInvalid",
                $"Cannot create branch '{branchName}' because its start point is invalid.",
                "Use a checked-readable EventAddress as branch start point.",
                Cause: startError
            );
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var createOp = new RefOpFrame(RefOpOperation.Create, branchName, default, default, 0, null, startPoint, timestamp, reasonKind);
        byte[] allocationPayload = RefOpFrameCodec.Encode(in createOp);
        var preparedBind = createOp with { Operation = RefOpOperation.BindName };
        byte[] bindPayload = RefOpFrameCodec.Encode(in preparedBind);
        byte[] initPayload = new byte[RefMoveFrameCodec.FixedLength];
        _branches.EnsureCapacity(checked(_branches.Count + 1));
        _activeRefNames.EnsureCapacity(checked(_activeRefNames.Count + 1));
        var capacityError = PrecheckCatalogOperation(checked((long)_branches.Count + _tags.Count + 1), [allocationPayload, bindPayload], out bool checkpoint);
        if (capacityError is not null) { return capacityError; }
        if (checkpoint) { CheckpointCatalog(); }
        try {
            if (startPoint is { } target) { _segments.ConfirmDurable(target.SegmentNumber); }
            var createTicketResult = AppendRefOpPayload(allocationPayload);
            if (createTicketResult.IsFailure) { return createTicketResult.Error!; }

            var refId = new RefId(createTicketResult.Unwrap().Packed);
            using var storeUse = new RefStoreUse(RefMoveStore.CreateNew(_refObjectsPath, refId, _options.RefSegmentStoreOptions), owned: true);
            var refObject = storeUse.Store;
            var initMove = new RefMoveFrame(refId, 1, timestamp, RefMoveOperation.Init, null, null, startPoint, reasonKind);
            RefMoveFrameCodec.Encode(in initMove, initPayload);
            var initResult = refObject.AppendPreparedMove(in initMove, initPayload);
            if (initResult.IsFailure) { return initResult.Error!; }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bindPayload.AsSpan(12),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bindPayload.AsSpan(12)) | 1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPayload.AsSpan(16), refId.Packed);
            var bindResult = AppendRefOpPayload(bindPayload);
            if (bindResult.IsFailure) { return bindResult.Error!; }

            OperationProbe?.Invoke("RefBeforeInstall");
            _branches[branchName] = refId;
            _activeRefNames.Add(refId, branchName);
            RetainRef(new RefState(refId, startPoint, LastMoveSequenceNumber: 1, Closed: false), storeUse);
            return refId;
        }
        catch (Exception ex) { LatchFault(ex); throw; }
    }

    public AteliaResult<RefId> ForkBranch(string branchName, RefId sourceRefId, EventAddress sourceHead, uint reasonKind = 0) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var sourceStateResult = LoadRefState(sourceRefId);
        if (sourceStateResult.IsFailure) { return sourceStateResult.Error!; }

        RefState sourceState = sourceStateResult.Unwrap();
        if (sourceState.Closed) { return RefClosedError(sourceRefId); }
        if (!Nullable.Equals(sourceState.Head, sourceHead)) {
            return new EventJournalError(
                "ForkSourceHeadMismatch",
                "Fork source head does not match the current source ref head.",
                "Reload the source ref head and retry fork with the observed head."
            );
        }

        var nameError = ValidateBranchName(branchName);
        if (nameError is not null) { return nameError; }
        if (_branches.ContainsKey(branchName)) { return BranchAlreadyExistsError(branchName); }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var forkOp = new RefOpFrame(RefOpOperation.Fork, branchName, default, sourceRefId, sourceState.LastMoveSequenceNumber, sourceHead, sourceHead, timestamp, reasonKind);
        byte[] allocationPayload = RefOpFrameCodec.Encode(in forkOp);
        var preparedBind = forkOp with { Operation = RefOpOperation.BindName };
        byte[] bindPayload = RefOpFrameCodec.Encode(in preparedBind);
        byte[] initPayload = new byte[RefMoveFrameCodec.FixedLength];
        _branches.EnsureCapacity(checked(_branches.Count + 1));
        _activeRefNames.EnsureCapacity(checked(_activeRefNames.Count + 1));
        var capacityError = PrecheckCatalogOperation(checked((long)_branches.Count + _tags.Count + 1), [allocationPayload, bindPayload], out bool checkpoint);
        if (capacityError is not null) { return capacityError; }
        if (checkpoint) { CheckpointCatalog(); }
        try {
            _segments.ConfirmDurable(sourceHead.SegmentNumber);
            var forkTicketResult = AppendRefOpPayload(allocationPayload);
            if (forkTicketResult.IsFailure) { return forkTicketResult.Error!; }

            var refId = new RefId(forkTicketResult.Unwrap().Packed);
            using var storeUse = new RefStoreUse(RefMoveStore.CreateNew(_refObjectsPath, refId, _options.RefSegmentStoreOptions), owned: true);
            var refObject = storeUse.Store;
            var initMove = new RefMoveFrame(refId, 1, timestamp, RefMoveOperation.Init, null, null, sourceHead, reasonKind);
            RefMoveFrameCodec.Encode(in initMove, initPayload);
            var initResult = refObject.AppendPreparedMove(in initMove, initPayload);
            if (initResult.IsFailure) { return initResult.Error!; }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bindPayload.AsSpan(12),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bindPayload.AsSpan(12)) | 1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bindPayload.AsSpan(16), refId.Packed);
            var bindResult = AppendRefOpPayload(bindPayload);
            if (bindResult.IsFailure) { return bindResult.Error!; }

            OperationProbe?.Invoke("RefBeforeInstall");
            _branches[branchName] = refId;
            _activeRefNames.Add(refId, branchName);
            RetainRef(new RefState(refId, sourceHead, LastMoveSequenceNumber: 1, Closed: false), storeUse);
            return refId;
        }
        catch (Exception ex) { LatchFault(ex); throw; }
    }

    public EventAddress? GetHead(RefId refId) {
        ThrowIfDisposed();
        RefState state = LoadRefState(refId).Unwrap();
        if (state.Closed) { throw new InvalidOperationException($"Ref {refId} is closed."); }
        return state.Head;
    }

    public AteliaResult<bool> AdvanceRef(RefId refId, EventAddress? expectedOldHead, EventAddress newHead, uint reasonKind = 0) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }

        RefState state = stateResult.Unwrap();
        if (state.Closed) { return RefClosedError(refId); }
        var casError = ValidateExpectedHead(state, expectedOldHead);
        if (casError is not null) { return casError; }

        var newHeaderResult = ReadEventHeaderChecked(newHead);
        if (newHeaderResult.IsFailure) { return InvalidRefTargetError(newHead, newHeaderResult.Error!); }
        if (!Nullable.Equals(newHeaderResult.Unwrap().Parent, expectedOldHead)) {
            return new EventJournalError(
                "AdvanceTopologyMismatch",
                "AdvanceRef requires the new Event parent to match expectedOldHead.",
                "Use MoveRef for reset/rewind/retarget operations."
            );
        }

        return AppendRefMove(state, RefMoveOperation.Advance, expectedOldHead, newHead, reasonKind);
    }

    public AteliaResult<bool> MoveRef(RefId refId, EventAddress? expectedOldHead, EventAddress? newHead, uint reasonKind = 0) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }

        RefState state = stateResult.Unwrap();
        if (state.Closed) { return RefClosedError(refId); }
        var casError = ValidateExpectedHead(state, expectedOldHead);
        if (casError is not null) { return casError; }

        if (!TryValidateTarget(newHead, out AteliaError? targetError)) { return InvalidNullableRefTargetError(newHead, targetError!); }

        return AppendRefMove(state, RefMoveOperation.Move, expectedOldHead, newHead, reasonKind);
    }

    public AteliaResult<bool> ArchiveRef(RefId refId, EventAddress? expectedOldHead, uint reasonKind = 0) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }

        RefState state = stateResult.Unwrap();
        if (state.Closed) { return RefClosedError(refId); }
        var casError = ValidateExpectedHead(state, expectedOldHead);
        if (casError is not null) { return casError; }

        _activeRefNames.TryGetValue(refId, out string? branchName);
        if (branchName is null) {
            return new EventJournalError(
                "RefNotBound",
                $"Ref {refId} is not bound to an active branch name.",
                "Only active named refs can be archived through ArchiveRef."
            );
        }

        if (state.LastMoveSequenceNumber == ulong.MaxValue) { return new EventJournalError("RefMoveSequenceExhausted", "Ref move sequence cannot advance."); }
        ulong closeSequence = checked(state.LastMoveSequenceNumber + 1);
        var archiveOp = new RefOpFrame(RefOpOperation.Archive, branchName, refId, default, closeSequence, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reasonKind);
        byte[] archivePayload = RefOpFrameCodec.Encode(in archiveOp);
        var closeMove = new RefMoveFrame(refId, closeSequence, archiveOp.UtcUnixTimeMilliseconds, RefMoveOperation.Close, expectedOldHead, state.Head, null, reasonKind);
        byte[] closePayload = new byte[RefMoveFrameCodec.FixedLength];
        RefMoveFrameCodec.Encode(in closeMove, closePayload);
        var capacityError = PrecheckCatalogOperation(checked((long)_branches.Count + _tags.Count - 1), [archivePayload], out bool checkpoint);
        if (capacityError is not null) { return capacityError; }
        if (checkpoint) { CheckpointCatalog(); }
        try {
            var closeResult = AppendRefMove(state, in closeMove, closePayload);
            if (closeResult.IsFailure) { return closeResult.Error!; }

            var archiveResult = AppendRefOpPayload(archivePayload);
            if (archiveResult.IsFailure) { return archiveResult.Error!; }

            _branches.Remove(branchName);
            _activeRefNames.Remove(refId);
            RemoveRefEntry(refId);
            return true;
        }
        catch (Exception ex) { LatchFault(ex); throw; }
    }

    public AteliaResult<IReadOnlyList<RefMoveFrame>> ReadReflog(RefId refId) {
        ThrowIfDisposed();
        try {
            using var storeUse = OpenRefStore(refId);
            var refObject = storeUse.Store;
            var moves = refObject.ReadAllMoves();
            return moves.IsFailure ? RefReadError(moves.Error!, RefMoveStore.GetObjectPath(_refObjectsPath, refId)) : moves;
        }
        catch (StorageOpenException ex) {
            return ex.ReasonCode == "LegacyOrIncompleteLayout" ? MaintenanceError("MetadataMissing", ex.StoragePath) : OpenError(ex);
        }
        catch (Exception ex) when (IsReadException(ex)) {
            return new EventJournalError(
                "RefReflogReadFailed",
                $"Failed to read reflog for ref {refId}: {ex.Message}",
                "Verify that the ref object exists and is not corrupted.",
                Cause: new EventJournalError("ReadException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
            );
        }
    }

    public AteliaResult<CommitToRefOutcome> CommitToRef(
        string branchName,
        EventAddress? expectedHead,
        ReadOnlySpan<byte> payload,
        uint opaqueEventKind = 0,
        AddressHint hint = default,
        uint reasonKind = 0,
        EventPayloadWriteOptions? writeOptions = null
    ) {
        var refIdResult = OpenBranch(branchName);
        if (refIdResult.IsFailure) { return refIdResult.Error!; }

        return CommitToRef(
            refIdResult.Unwrap(),
            expectedHead,
            payload,
            opaqueEventKind,
            hint,
            reasonKind,
            writeOptions
        );
    }

    public AteliaResult<CommitToRefOutcome> CommitToRef(
        RefId refId,
        EventAddress? expectedHead,
        ReadOnlySpan<byte> payload,
        uint opaqueEventKind = 0,
        AddressHint hint = default,
        uint reasonKind = 0,
        EventPayloadWriteOptions? writeOptions = null
    ) {
        ThrowIfDisposed();
        ThrowIfReadOnly();
        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }
        if (stateResult.Unwrap().Closed) { return RefClosedError(refId); }
        if (stateResult.Unwrap().LastMoveSequenceNumber == ulong.MaxValue) { return new EventJournalError("RefMoveSequenceExhausted", "Ref move sequence cannot advance."); }

        var eventResult = AppendEventFrame(expectedHead, payload, opaqueEventKind, hint, writeOptions: writeOptions);
        if (eventResult.IsFailure) { return eventResult.Error!; }

        EventAddress newEvent = eventResult.Unwrap();
        var advanceResult = AdvanceRef(refId, expectedHead, newEvent, reasonKind);
        if (advanceResult.IsFailure) {
            return new EventJournalError(
                "CommitRefAdvanceFailed",
                "EventFrame was appended, but ref advance failed.",
                "The returned error details include the orphan EventAddress. Retry ref advance or inspect the orphan event.",
                new Dictionary<string, string> { ["OrphanEventAddress"] = FormatEventAddress(newEvent) },
                advanceResult.Error
            );
        }

        return new CommitToRefOutcome(refId, newEvent);
    }

    private static string RefsDirectory(string journalPath) => Path.Combine(journalPath, "refs");
    private static string RefObjectsDirectory(string journalPath) => Path.Combine(RefsDirectory(journalPath), "objects");
    private static string RefOpLogPath(string journalPath) => Path.Combine(RefsDirectory(journalPath), "ref-op-log.rbf");

    private static IRbfFile CreateRefOpLog(string journalPath, EventJournalOptions options) {
        Directory.CreateDirectory(RefsDirectory(journalPath));
        Directory.CreateDirectory(RefObjectsDirectory(journalPath));
        return RbfFile.CreateNew(RefOpLogPath(journalPath), options.RefOpLogOptions.CacheMode);
    }

    private static IRbfFile OpenRefOpLog(string journalPath, EventJournalOptions options) => OpenRefOpLogCore(journalPath, options, readOnly: false);

    private static IRbfFile OpenReadOnlyRefOpLog(string journalPath, EventJournalOptions options) => OpenRefOpLogCore(journalPath, options, readOnly: true);

    private static IRbfFile OpenRefOpLogCore(string journalPath, EventJournalOptions options, bool readOnly) {
        string path = RefOpLogPath(journalPath);
        try {
            return readOnly ? RbfFile.OpenReadOnlyExisting(path, options.RefOpLogOptions.CacheMode)
                : RbfFile.OpenExisting(path, options.RefOpLogOptions.CacheMode);
        }
        catch (InvalidDataException ex) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", path, innerException: ex); }
    }

    private static Dictionary<string, RefId> ReplayRefOpLog(IRbfFile log, string path, CatalogSnapshot snapshot,
        out Dictionary<string, EventAddress> tags, out long suffixCount) {
        tags = snapshot.Tags;
        suffixCount = 0;
        var branches = snapshot.Branches;
        var names = new Dictionary<RefId, string>();
        foreach (var entry in branches) { names.Add(entry.Value, entry.Key); }
        long limit = Math.Max(1024, snapshot.LiveCount);
        var sequence = log.ScanForward(snapshot.Boundary, showTombstone: true);
        if (sequence.IsFailure) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "BoundaryMismatch", path); }
        var enumerator = sequence.Unwrap().GetEnumerator();
        long end = snapshot.Boundary.EndExclusive;
        try {
            while (end < log.TailOffset) {
                if (suffixCount == limit) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "SuffixBudgetExceeded", path, end); }
                if (!enumerator.MoveNext()) { throw new InvalidDataException("Control suffix ended before physical EOF."); }
                RbfFrameInfo info = enumerator.Current;
                if (info.Ticket.Length > 248 || log.GetPhysicalOffsetImmediatelyAfter(info.Ticket) - info.Ticket.Offset > 252) {
                    throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "ControlFrameTooLarge", path, info.Ticket.Offset);
                }
                if (info.IsTombstone || info.TailMetaLength != 0) { throw new InvalidDataException("Invalid control frame metadata."); }
                using var frame = info.ReadPooledFrame().ToDisposable();
                if (frame.IsFailure) { throw new InvalidDataException("Invalid control frame CRC."); }
                if (info.Tag == TagBindingFrameTag) {
                    var tag = TagBindingFrameCodec.Decode(frame.Unwrap().PayloadAndMeta);
                    if (!tags.TryAdd(tag.Name, tag.Target)) { throw new InvalidDataException("Duplicate immutable tag."); }
                }
                else if (info.Tag == RefOpFrameTag) {
                    var decoded = RefOpFrameCodec.Decode(frame.Unwrap().PayloadAndMeta);
                    if (decoded.IsFailure) { throw CatalogReadException(decoded.Error!, path, info.Ticket.Offset); }
                    var op = decoded.Unwrap();
                    if (ValidateBranchName(op.BranchName) is not null) { throw new InvalidDataException("Invalid branch name."); }
                    switch (op.Operation) {
                        case RefOpOperation.Create:
                        case RefOpOperation.Fork:
                            if (!IsAllocation(op)) { throw new InvalidDataException("Invalid allocation."); }
                            break;
                        case RefOpOperation.BindName:
                            SizedPtr allocationTicket = SizedPtr.FromPacked(op.RefId.Packed);
                            if (op.RefId.IsDefault || allocationTicket.Offset < 4 || allocationTicket.Length is < 24 or > 248
                                || log.GetPhysicalOffsetImmediatelyAfter(allocationTicket) != info.Ticket.Offset) {
                                throw new InvalidDataException("Binding allocation must precede the bind frame.");
                            }
                            var allocation = ReadAllocation(log, path, op.RefId);
                            if (allocation.IsFailure) { throw CatalogReadException(allocation.Error!, path, info.Ticket.Offset); }
                            var origin = allocation.Unwrap();
                            if (SizedPtr.FromPacked(op.RefId.Packed).Offset >= info.Ticket.Offset
                                || op != origin with { Operation = RefOpOperation.BindName, RefId = op.RefId }
                                || branches.ContainsKey(op.BranchName) || names.ContainsKey(op.RefId)) {
                                throw new InvalidDataException("Invalid or duplicate name binding.");
                            }
                            branches.Add(op.BranchName, op.RefId);
                            names.Add(op.RefId, op.BranchName);
                            break;
                        case RefOpOperation.Archive:
                            if (op.RefId.IsDefault || !op.SourceRefId.IsDefault || op.SourceMoveSequenceNumber <= 1
                                || op.SourceHead is not null || op.StartHead is not null
                                || !branches.TryGetValue(op.BranchName, out var id) || id != op.RefId) {
                                throw new InvalidDataException("Invalid archive binding.");
                            }
                            branches.Remove(op.BranchName);
                            names.Remove(op.RefId);
                            break;
                        default: throw new InvalidDataException("Unknown control record.");
                    }
                }
                else { throw new InvalidDataException("Unknown control frame tag."); }
                suffixCount++;
                end = log.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
            }
            if (enumerator.TerminationError is not null) { throw new InvalidDataException("Invalid control suffix tail."); }
            return branches;
        }
        catch (InvalidDataException ex) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "CatalogInvalid", path, innerException: ex); }
    }

    private static StorageOpenException CatalogReadException(AteliaError error, string path, long offset) {
        var mapped = error.ErrorCode is "EventJournal.FormatUnsupported" or "EventJournal.MaintenanceRequired" ? error : RefReadError(error, path);
        bool unsupported = mapped.ErrorCode == "EventJournal.FormatUnsupported";
        var details = mapped.Details;
        ushort? version = details is not null && details.TryGetValue("ObservedVersion", out var observed)
            ? ushort.Parse(observed, System.Globalization.CultureInfo.InvariantCulture) : null;
        string reason = details is not null && details.TryGetValue("ReasonCode", out var code) ? code : "CatalogInvalid";
        return new StorageOpenException(unsupported ? StorageOpenErrorKind.FormatUnsupported : StorageOpenErrorKind.MaintenanceRequired,
            reason, path, offset, version);
    }

    private static bool IsAllocation(RefOpFrame op) => op.RefId.IsDefault
        && (op.Operation == RefOpOperation.Create
            ? op.SourceRefId.IsDefault && op.SourceMoveSequenceNumber == 0 && op.SourceHead is null
            : op.Operation == RefOpOperation.Fork && !op.SourceRefId.IsDefault && op.SourceMoveSequenceNumber != 0
                && op.SourceHead is not null && Nullable.Equals(op.SourceHead, op.StartHead));

    // Called after all predictable validation/allocation, before the first business write.
    private AteliaError? PrecheckCatalogOperation(long nextLiveCount, byte[][] payloads, out bool checkpoint) {
        checkpoint = false;
        long offset = _refOpLog.TailOffset;
        foreach (byte[] payload in payloads) {
            if (offset < 4 || (offset & 3) != 0 || offset > SizedPtr.MaxOffset) {
                return new EventJournalError("RefOpCapacityExhausted", "The complete control operation does not fit its address range.");
            }
            offset = checked(offset + ((payload.Length + 3L) & ~3L) + 28);
        }
        if (checked(_catalogSuffixCount + payloads.Length) > Math.Max(1024, _catalogSnapshotLiveCount)
            || _catalogSnapshotLiveCount > checked(2 * Math.Max(1024, nextLiveCount))) {
            checkpoint = true;
        }
        return null;
    }

    private void CheckpointCatalog() {
        try {
            OperationProbe?.Invoke("CheckpointBeforeLogFlush");
            _refOpLog.DurableFlush();
            OperationProbe?.Invoke("CheckpointBeforeBoundary");
            RbfScanBoundary boundary = RbfScanBoundary.Empty;
            if (_refOpLog.TailOffset != 4) {
                var reverse = _refOpLog.ScanReverse(showTombstone: true).GetEnumerator();
                if (!reverse.MoveNext() || reverse.Current.Ticket.Length > 248) { throw new InvalidDataException("Invalid checkpoint anchor."); }
                boundary = _refOpLog.GetScanBoundaryAfter(reverse.Current.Ticket).Unwrap();
            }
            var snapshot = new CatalogSnapshot(boundary, _branches, _tags);
            JournalFormat.Publish(Path.Combine(RefsDirectory(JournalPath), CatalogSnapshotCodec.FileName),
                stream => CatalogSnapshotCodec.Write(stream, snapshot), OperationProbe);
            OperationProbe?.Invoke("CheckpointBeforeInstall");
            _catalogSnapshotLiveCount = snapshot.LiveCount;
            _catalogSuffixCount = 0;
        }
        catch (Exception ex) { LatchFault(ex); throw; }
    }

    internal (long SnapshotLiveCount, long SuffixCount) CatalogCounts => (_catalogSnapshotLiveCount, _catalogSuffixCount);

    private AteliaResult<SizedPtr> AppendRefOpPayload(byte[] payload) {
        try {
            OperationProbe?.Invoke("RefOpBeforeAppend");
            var appendResult = _refOpLog.Append(RefOpFrameTag, payload);
            if (appendResult.IsFailure) { return appendResult.Error!; }

            _refOpLog.DurableFlush();
            _catalogSuffixCount++;
            OperationProbe?.Invoke("RefOpAfterDurableFlush");
            return appendResult.Unwrap();
        } catch (Exception ex) { LatchFault(ex); throw; }
    }

    private AteliaResult<bool> AppendRefMove(RefState state, RefMoveOperation operation, EventAddress? expectedOldHead, EventAddress? newHead, uint reasonKind) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state.LastMoveSequenceNumber == ulong.MaxValue) { return new EventJournalError("RefMoveSequenceExhausted", "Ref move sequence cannot advance."); }
        var move = new RefMoveFrame(state.RefId, checked(state.LastMoveSequenceNumber + 1), timestamp, operation, expectedOldHead, state.Head, newHead, reasonKind);

        return AppendRefMove(state, in move);
    }

    private AteliaResult<bool> AppendRefMove(RefState state, scoped in RefMoveFrame move, byte[]? preparedPayload = null) {
        try {
            if (move.NewTarget is { } target) { _segments.ConfirmDurable(target.SegmentNumber); }
            using var storeUse = OpenRefStore(state.RefId);
            var refObject = storeUse.Store;
            var appendResult = preparedPayload is null ? refObject.AppendMove(in move) : refObject.AppendPreparedMove(in move, preparedPayload);
            if (appendResult.IsFailure) { return appendResult.Error!; }

            OperationProbe?.Invoke("RefMoveAfterDurableFlush");
            bool closed = move.Operation == RefMoveOperation.Close;
            RetainRef(state with { Head = move.NewTarget, LastMoveSequenceNumber = move.MoveSequenceNumber, Closed = closed }, storeUse);
            return true;
        } catch (Exception ex) { LatchFault(ex); throw; }
    }

    private AteliaResult<RefState> LoadRefState(RefId refId) {
        if (refId.IsDefault) { return new EventJournalError("RefIdInvalid", "RefId cannot be default 0.", "Use a RefId returned by CreateBranch/OpenBranch/ForkBranch."); }
        if (TryGetRefEntry(refId, out var cached)) { return cached.State; }

        try {
            var allocationResult = ReadAllocation(refId);
            if (allocationResult.IsFailure) { return allocationResult.Error!; }
            RefOpFrame allocation = allocationResult.Unwrap();
            using var storeUse = OpenRefStore(refId);
            var refObject = storeUse.Store;
            var endpointsResult = refObject.ReadEndpoints();
            if (endpointsResult.IsFailure) { return RefReadError(endpointsResult.Error!, RefMoveStore.GetObjectPath(_refObjectsPath, refId)); }
            var endpoints = endpointsResult.Unwrap();
            RefMoveFrame init = endpoints.Init;
            RefMoveFrame last = endpoints.Last;
            if (init.Operation != RefMoveOperation.Init || init.MoveSequenceNumber != 1 || init.OldTarget is not null
                || init.ExpectedOldTarget is not null || !Nullable.Equals(init.NewTarget, allocation.StartHead)) {
                return MaintenanceError("CatalogInvalid", RefMoveStore.GetObjectPath(_refObjectsPath, refId));
            }
            bool same = endpoints.InitAddress == endpoints.LastAddress;
            if (last.Operation == RefMoveOperation.Init) {
                if (!same) { return MaintenanceError("CatalogInvalid", RefMoveStore.GetObjectPath(_refObjectsPath, refId)); }
            }
            else if (same || last.MoveSequenceNumber <= 1 || !Nullable.Equals(last.OldTarget, last.ExpectedOldTarget)
                || last.Operation == RefMoveOperation.Advance && last.NewTarget is null
                || last.Operation == RefMoveOperation.Close && last.NewTarget is not null) {
                return MaintenanceError("CatalogInvalid", RefMoveStore.GetObjectPath(_refObjectsPath, refId));
            }
            if (!TryValidateTarget(last.NewTarget, out AteliaError? targetError)) { return InvalidNullableRefTargetError(last.NewTarget, targetError); }
            var state = new RefState(refId, last.NewTarget, last.MoveSequenceNumber, last.Operation == RefMoveOperation.Close);
            RetainRef(state, storeUse);
            return state;
        }
        catch (StorageOpenException ex) {
            if (ex.ReasonCode == "LegacyOrIncompleteLayout") { return MaintenanceError("MetadataMissing", ex.StoragePath); }
            return OpenError(ex);
        }
        catch (Exception ex) when (IsReadException(ex)) {
            return new EventJournalError(
                "RefObjectReadFailed",
                $"Failed to read ref object {refId}: {ex.Message}",
                "Verify that the ref object exists and is not corrupted.",
                Cause: new EventJournalError("ReadException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
            );
        }
    }

    private AteliaResult<RefOpFrame> ReadAllocation(RefId refId) => ReadAllocation(_refOpLog, RefOpLogPath(JournalPath), refId);

    private static AteliaResult<RefOpFrame> ReadAllocation(IRbfFile log, string path, RefId refId) {
        SizedPtr ticket = SizedPtr.FromPacked(refId.Packed);
        if (ticket.Offset < 4 || ticket.Length is < 24 or > 248) { return MaintenanceError("CatalogInvalid", path); }
        using var result = log.ReadPooledFrame(ticket).ToDisposable();
        if (result.IsFailure) { return MaintenanceError("CatalogInvalid", path, result.Error); }
        var frame = result.Unwrap();
        if (frame.Tag != RefOpFrameTag || frame.IsTombstone || frame.TailMetaLength != 0) { return MaintenanceError("CatalogInvalid", path); }
        var decoded = RefOpFrameCodec.Decode(frame.PayloadAndMeta);
        if (decoded.IsFailure) { return RefReadError(decoded.Error!, path); }
        var op = decoded.Unwrap();
        if (ValidateBranchName(op.BranchName) is not null || op.Operation is not (RefOpOperation.Create or RefOpOperation.Fork)
            || !op.RefId.IsDefault || op.Operation == RefOpOperation.Create && (!op.SourceRefId.IsDefault || op.SourceMoveSequenceNumber != 0 || op.SourceHead is not null)
            || op.Operation == RefOpOperation.Fork && (op.SourceRefId.IsDefault || op.SourceMoveSequenceNumber == 0 || op.SourceHead is null || !Nullable.Equals(op.SourceHead, op.StartHead))) {
            return MaintenanceError("CatalogInvalid", path);
        }
        return op;
    }

    private static EventJournalError RefReadError(AteliaError error, string path) {
        bool version = error.ErrorCode is "EventJournal.RefMoveVersionUnsupported" or "EventJournal.RefOpVersionUnsupported";
        bool flags = error.ErrorCode is "EventJournal.RefMoveFlagsUnsupported" or "EventJournal.RefOpFlagsUnsupported";
        if (error.Details is { } metadata && metadata.TryGetValue("SegmentNumber", out string? segment)) {
            path = Path.Combine(path, "segments", segment + ".rbf");
        }
        var details = new Dictionary<string, string> { ["ReasonCode"] = version ? "UnsupportedVersion" : flags ? "UnsupportedFlags" : "InvalidTail", ["StoragePath"] = path };
        if (error.Details is { } fields) {
            foreach (string key in new[] { "ObservedVersion", "Offset" }) {
                if (fields.TryGetValue(key, out string? value)) { details[key] = value; }
            }
        }
        return new EventJournalError(version || flags ? "FormatUnsupported" : "MaintenanceRequired", "Ref record cannot be read strictly.", Details: details, Cause: error);
    }

    private static EventJournalError MaintenanceError(string code, string path, AteliaError? cause = null) => new(
        "MaintenanceRequired", "Storage requires offline maintenance.", Details: new Dictionary<string, string> { ["ReasonCode"] = code, ["StoragePath"] = path }, Cause: cause);

    private static EventJournalError OpenError(StorageOpenException ex) {
        var details = new Dictionary<string, string> { ["ReasonCode"] = ex.ReasonCode, ["StoragePath"] = ex.StoragePath };
        if (ex.Offset is { } offset) { details["Offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        if (ex.ObservedVersion is { } version) { details["ObservedVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        return new EventJournalError(ex.Kind == StorageOpenErrorKind.MaintenanceRequired ? "MaintenanceRequired" : "FormatUnsupported", "Storage cannot be read strictly.", Details: details);
    }

    private bool TryValidateTarget(EventAddress? target, out AteliaError? error) {
        error = null;
        if (target is null) { return true; }

        var result = ReadEventHeaderChecked(target.Value);
        if (result.IsSuccess) { return true; }

        error = result.Error;
        return false;
    }

    private static AteliaError? ValidateExpectedHead(RefState state, EventAddress? expectedOldHead) {
        if (Nullable.Equals(state.Head, expectedOldHead)) { return null; }

        return new EventJournalError(
            "RefCasMismatch",
            "Ref current head does not match expectedOldHead; no ref move was written.",
            "Reload the ref head and retry with the observed value."
        );
    }

    internal static AteliaError? ValidateBranchName(string branchName) {
        if (branchName is null || branchName.Length == 0 || branchName == "." || branchName == ".." || branchName.EndsWith(".", StringComparison.Ordinal) || branchName.EndsWith(".lock", StringComparison.Ordinal)) { return InvalidBranchNameError(branchName); }

        int utf8Length = System.Text.Encoding.UTF8.GetByteCount(branchName);
        if (utf8Length is < 1 or > 128) { return InvalidBranchNameError(branchName); }

        for (int i = 0; i < branchName.Length; i++) {
            char c = branchName[i];
            bool valid = i == 0
                ? c is >= 'a' and <= 'z' || c is >= '0' and <= '9'
                : c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '.' or '_' or '-';
            if (!valid) { return InvalidBranchNameError(branchName); }
        }

        return null;
    }

    private static EventJournalError InvalidBranchNameError(string? branchName) => new(
        "BranchNameInvalid",
        $"Invalid branch name '{branchName}'.",
        "Use 1..128 UTF-8 bytes matching [a-z0-9][a-z0-9._-]*, excluding '.', '..', trailing '.', and '.lock'."
    );

    private static EventJournalError BranchAlreadyExistsError(string branchName) => new(
        "BranchAlreadyExists",
        $"Branch '{branchName}' is already bound to an active ref.",
        "Choose a different branch name or archive the existing branch first."
    );

    private static EventJournalError RefClosedError(RefId refId) => new(
        "RefClosed",
        $"Ref {refId} is closed.",
        "Closed refs cannot be advanced or moved. Create or open an active branch instead."
    );

    private static EventJournalError InvalidRefTargetError(EventAddress target, AteliaError cause) => new(
        "RefTargetInvalid",
        $"Ref target {FormatEventAddress(target)} is not a checked-readable EventFrame.",
        "Only point refs at durable EventFrames from this EventJournal.",
        Cause: cause
    );

    private static EventJournalError InvalidNullableRefTargetError(EventAddress? target, AteliaError? cause) => new(
        "RefTargetInvalid",
        target is null ? "Ref target is invalid." : $"Ref target {FormatEventAddress(target.Value)} is invalid.",
        "Only point refs at durable EventFrames from this EventJournal.",
        Cause: cause
    );

    private static string FormatEventAddress(EventAddress address) => $"{address.SegmentNumber:x8}:{address.Ticket.Packed:x16}:{address.Hint.Packed:x8}";

    private sealed record RefState(RefId RefId, EventAddress? Head, ulong LastMoveSequenceNumber, bool Closed);
}
