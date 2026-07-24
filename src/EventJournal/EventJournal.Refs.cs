using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public readonly record struct CommitToRefOutcome(RefId RefId, EventAddress EventAddress);

public sealed partial class EventJournal {
    private const long RbfHeaderOnlyLength = 4;

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
        var createTicketResult = AppendRefOp(createOp);
        if (createTicketResult.IsFailure) { return createTicketResult.Error!; }

        var refId = new RefId(createTicketResult.Unwrap().Packed);
        using var refObject = RefMoveStore.CreateNew(_refObjectsPath, refId, _options.RefSegmentStoreOptions);
        var initMove = new RefMoveFrame(refId, 1, timestamp, RefMoveOperation.Init, null, null, startPoint, reasonKind);
        var initResult = refObject.AppendMove(in initMove);
        if (initResult.IsFailure) { return initResult.Error!; }

        var bindOp = new RefOpFrame(RefOpOperation.BindName, branchName, refId, default, 0, null, startPoint, timestamp, reasonKind);
        var bindResult = AppendRefOp(bindOp);
        if (bindResult.IsFailure) { return bindResult.Error!; }

        _branches[branchName] = refId;
        _refStates[refId] = new RefState(refId, startPoint, LastMoveSequenceNumber: 1, Closed: false);
        return refId;
    }

    public AteliaResult<RefId> ForkBranch(string branchName, RefId sourceRefId, EventAddress sourceHead, uint reasonKind = 0) {
        ThrowIfDisposed();
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
        var forkTicketResult = AppendRefOp(forkOp);
        if (forkTicketResult.IsFailure) { return forkTicketResult.Error!; }

        var refId = new RefId(forkTicketResult.Unwrap().Packed);
        using var refObject = RefMoveStore.CreateNew(_refObjectsPath, refId, _options.RefSegmentStoreOptions);
        var initMove = new RefMoveFrame(refId, 1, timestamp, RefMoveOperation.Init, null, null, sourceHead, reasonKind);
        var initResult = refObject.AppendMove(in initMove);
        if (initResult.IsFailure) { return initResult.Error!; }

        var bindOp = new RefOpFrame(RefOpOperation.BindName, branchName, refId, sourceRefId, sourceState.LastMoveSequenceNumber, sourceHead, sourceHead, timestamp, reasonKind);
        var bindResult = AppendRefOp(bindOp);
        if (bindResult.IsFailure) { return bindResult.Error!; }

        _branches[branchName] = refId;
        _refStates[refId] = new RefState(refId, sourceHead, LastMoveSequenceNumber: 1, Closed: false);
        return refId;
    }

    public EventAddress? GetHead(RefId refId) {
        ThrowIfDisposed();
        RefState state = LoadRefState(refId).Unwrap();
        if (state.Closed) { throw new InvalidOperationException($"Ref {refId} is closed."); }
        return state.Head;
    }

    public AteliaResult<bool> AdvanceRef(RefId refId, EventAddress? expectedOldHead, EventAddress newHead, uint reasonKind = 0) {
        ThrowIfDisposed();
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
        var stateResult = LoadRefState(refId);
        if (stateResult.IsFailure) { return stateResult.Error!; }

        RefState state = stateResult.Unwrap();
        if (state.Closed) { return RefClosedError(refId); }
        var casError = ValidateExpectedHead(state, expectedOldHead);
        if (casError is not null) { return casError; }

        string? branchName = _branches.FirstOrDefault(pair => pair.Value == refId).Key;
        if (branchName is null) {
            return new EventJournalError(
                "RefNotBound",
                $"Ref {refId} is not bound to an active branch name.",
                "Only active named refs can be archived through ArchiveRef."
            );
        }

        var closeResult = AppendRefMove(state, RefMoveOperation.Close, expectedOldHead, null, reasonKind);
        if (closeResult.IsFailure) { return closeResult.Error!; }

        var archiveOp = new RefOpFrame(RefOpOperation.Archive, branchName, refId, default, state.LastMoveSequenceNumber + 1, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), reasonKind);
        var archiveResult = AppendRefOp(archiveOp);
        if (archiveResult.IsFailure) { return archiveResult.Error!; }

        _branches.Remove(branchName);
        _refStates[refId] = state with { Head = null, LastMoveSequenceNumber = state.LastMoveSequenceNumber + 1, Closed = true };
        return true;
    }

    public AteliaResult<IReadOnlyList<RefMoveFrame>> ReadReflog(RefId refId) {
        ThrowIfDisposed();
        try {
            using var refObject = RefMoveStore.OpenExisting(_refObjectsPath, refId, _options.RefSegmentStoreOptions);
            return refObject.ReadAllMoves();
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

        RefId refId = refIdResult.Unwrap();
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

    private static IRbfFile OpenRefOpLog(string journalPath, EventJournalOptions options, bool createIfMissing) {
        Directory.CreateDirectory(RefsDirectory(journalPath));
        Directory.CreateDirectory(RefObjectsDirectory(journalPath));
        string path = RefOpLogPath(journalPath);
        if (!File.Exists(path)) {
            if (!createIfMissing) { throw new FileNotFoundException("EventJournal ref-op-log does not exist.", path); }
            return RbfFile.CreateNew(path, options.RefOpLogOptions.CacheMode);
        }

        if (options.RefOpLogOptions.RecoverActiveTailOnOpen) { RecoverRbfTail(path, options.RefOpLogOptions.CacheMode); }
        return RbfFile.OpenExisting(path, options.RefOpLogOptions.CacheMode);
    }

    private static Dictionary<string, RefId> ReplayRefOpLog(IRbfFile refOpLog) {
        var knownRefs = new HashSet<RefId>();
        var branches = new Dictionary<string, RefId>(StringComparer.Ordinal);

        var enumerator = refOpLog.ScanForward().GetEnumerator();
        while (enumerator.MoveNext()) {
            RbfFrameInfo info = enumerator.Current;
            if (info.Tag != RefOpFrameTag) { throw new InvalidDataException($"ref-op-log contains unexpected frame tag 0x{info.Tag:X8}."); }

            using var frameResult = info.ReadPooledFrame().ToDisposable();
            if (frameResult.IsFailure) { throw new InvalidDataException($"Failed to read ref-op-log frame: {frameResult.Error!.Message}"); }

            RefOpFrame op = RefOpFrameCodec.Decode(frameResult.Unwrap().PayloadAndMeta).Unwrap();
            RefId allocatedRefId = new(info.Ticket.Packed);

            switch (op.Operation) {
                case RefOpOperation.Create:
                case RefOpOperation.Fork:
                    knownRefs.Add(allocatedRefId);
                    break;
                case RefOpOperation.BindName:
                    if (op.RefId.IsDefault || !knownRefs.Contains(op.RefId)) { throw new InvalidDataException($"ref-op-log binds unknown RefId {op.RefId}."); }
                    branches[op.BranchName] = op.RefId;
                    break;
                case RefOpOperation.Archive:
                    if (op.RefId.IsDefault) { throw new InvalidDataException("ref-op-log archive frame has default RefId."); }
                    if (branches.TryGetValue(op.BranchName, out RefId activeRefId) && activeRefId == op.RefId) { branches.Remove(op.BranchName); }
                    break;
                default:
                    throw new InvalidDataException($"Unsupported ref-op-log operation {op.Operation}.");
            }
        }

        if (enumerator.TerminationError is not null) { throw new InvalidDataException($"ref-op-log scan failed: {enumerator.TerminationError.Message}"); }
        return branches;
    }

    private AteliaResult<SizedPtr> AppendRefOp(in RefOpFrame op) {
        byte[] payload = RefOpFrameCodec.Encode(in op);
        var appendResult = _refOpLog.Append(RefOpFrameTag, payload);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        _refOpLog.DurableFlush();
        return appendResult.Unwrap();
    }

    private AteliaResult<bool> AppendRefMove(RefState state, RefMoveOperation operation, EventAddress? expectedOldHead, EventAddress? newHead, uint reasonKind) {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var move = new RefMoveFrame(state.RefId, state.LastMoveSequenceNumber + 1, timestamp, operation, expectedOldHead, state.Head, newHead, reasonKind);

        using var refObject = RefMoveStore.OpenExisting(_refObjectsPath, state.RefId, _options.RefSegmentStoreOptions);
        var appendResult = refObject.AppendMove(in move);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        bool closed = operation == RefMoveOperation.Close;
        _refStates[state.RefId] = state with { Head = newHead, LastMoveSequenceNumber = move.MoveSequenceNumber, Closed = closed };
        return true;
    }

    private AteliaResult<RefState> LoadRefState(RefId refId) {
        if (refId.IsDefault) { return new EventJournalError("RefIdInvalid", "RefId cannot be default 0.", "Use a RefId returned by CreateBranch/OpenBranch/ForkBranch."); }
        if (_refStates.TryGetValue(refId, out RefState? cached)) { return cached; }

        try {
            using var refObject = RefMoveStore.OpenExisting(_refObjectsPath, refId, _options.RefSegmentStoreOptions);
            var movesResult = refObject.ReadAllMoves();
            if (movesResult.IsFailure) { return movesResult.Error!; }

            RefState? state = null;
            foreach (RefMoveFrame move in movesResult.Unwrap()) {
                var applyResult = TryApplyMove(state, move, out RefState? nextState);
                if (applyResult is not null) { return applyResult; }

                if (!TryValidateTarget(move.NewTarget, out _)) {
                    if (state is null) { return InvalidNullableRefTargetError(move.NewTarget, null); }
                    break;
                }

                state = nextState;
            }

            if (state is null) {
                return new EventJournalError(
                    "RefObjectEmpty",
                    $"Ref object {refId} does not contain a usable Init move.",
                    "Inspect or repair the ref object."
                );
            }

            _refStates[refId] = state;
            return state;
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

    private AteliaError? TryApplyMove(RefState? current, RefMoveFrame move, out RefState? next) {
        next = null;

        if (current is null) {
            if (move.Operation != RefMoveOperation.Init || move.MoveSequenceNumber != 1 || move.OldTarget is not null || move.ExpectedOldTarget is not null) {
                return new EventJournalError(
                    "RefObjectFirstMoveInvalid",
                    "Ref object first move must be Init sequence 1 with null old/expected target.",
                    "Treat this ref object as malformed."
                );
            }

            next = new RefState(move.RefId, move.NewTarget, move.MoveSequenceNumber, Closed: false);
            return null;
        }

        if (current.RefId != move.RefId || move.MoveSequenceNumber != current.LastMoveSequenceNumber + 1) {
            return new EventJournalError(
                "RefMoveSequenceInvalid",
                "RefMoveFrame sequence is not contiguous for its ref object.",
                "Treat this ref object as malformed."
            );
        }

        if (current.Closed) { return RefClosedError(current.RefId); }
        if (move.Operation == RefMoveOperation.Init) {
            return new EventJournalError(
                "RefMoveOperationInvalid",
                "Init may only appear as the first RefMoveFrame.",
                "Treat this ref object as malformed."
            );
        }

        if (!Nullable.Equals(move.OldTarget, current.Head) || !Nullable.Equals(move.ExpectedOldTarget, current.Head)) {
            return new EventJournalError(
                "RefMoveOldTargetMismatch",
                "RefMoveFrame old/expected target does not match previous ref head.",
                "Treat this ref object as malformed."
            );
        }

        if (move.Operation == RefMoveOperation.Advance && move.NewTarget is null) { return new EventJournalError("RefMoveOperationInvalid", "Advance move must have a non-null new target.", "Treat this ref object as malformed."); }

        if (move.Operation == RefMoveOperation.Close && move.NewTarget is not null) { return new EventJournalError("RefMoveOperationInvalid", "Close move must have a null new target.", "Treat this ref object as malformed."); }

        next = new RefState(move.RefId, move.NewTarget, move.MoveSequenceNumber, Closed: move.Operation == RefMoveOperation.Close);
        return null;
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

    private static AteliaError? ValidateBranchName(string branchName) {
        if (branchName.Length == 0 || branchName == "." || branchName == ".." || branchName.EndsWith(".", StringComparison.Ordinal) || branchName.EndsWith(".lock", StringComparison.Ordinal)) { return InvalidBranchNameError(branchName); }

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

    private static EventJournalError InvalidBranchNameError(string branchName) => new(
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

    private static void RecoverRbfTail(string path, RbfCacheMode cacheMode) {
        long fileLength = new FileInfo(path).Length;
        if (fileLength == RbfHeaderOnlyLength) { return; }

        RbfRecoveryHit? recoveryHit = null;
        using (var scanner = RbfRecovery.OpenReadOnly(path, cacheMode)) {
            foreach (RbfRecoveryHit hit in scanner.ScanBackward()) {
                recoveryHit = hit;
                break;
            }
        }

        if (recoveryHit is not { } foundHit) { throw new InvalidDataException($"RBF file is not recoverable: {path}"); }

        RbfRecovery.TruncateToSuggestedTail(path, foundHit);
    }

    private sealed record RefState(RefId RefId, EventAddress? Head, ulong LastMoveSequenceNumber, bool Closed);
}
