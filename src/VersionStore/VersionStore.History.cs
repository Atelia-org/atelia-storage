using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    private IRbfFile? _historyRef;
    private uint _historyRefId;
    private bool _historyActive;

    /// <summary>从固定上界向前交付自有 checked 快照，按 ForkOrigin 接续精确来源前缀。</summary>
    /// <remarks>
    /// visitor 的 false 已接收当前项并正常停止；活动调用拒绝递归历史及本 owner 的所有 mutation。
    /// 已交付的值可保留；停止或预算失败不证明未访问历史健康。
    /// </remarks>
    public AteliaResult<HistoryEnd> ReadRefHistory(RefId refId, HistoryBudget budget,
        Func<RefBytesSnapshot, bool> visitor, CancellationToken cancellationToken = default) {
        EnsureUsable();
        if (!refId.BelongsTo(_identity)) { return InvalidRef(); }
        if (visitor is null || budget.MaxSnapshots <= 0 || budget.MaxWorkSteps <= 0) {
            return new VersionStoreRuntimeError("ArgumentError", "A nonnull visitor and two positive history budgets are required.");
        }
        if (_tagScanActive) { return TagScanActive(); }
        if (_historyActive) {
            return new VersionStoreRuntimeError("HistoryActive", "A history visitor is already active on this VersionStore.");
        }

        _historyActive = true;
        try {
            AteliaResult<HistoryEnd> result = default;
            CleanupErrors errors = default;
            try {
                var work = new HistoryWork(this, budget, cancellationToken);
                result = ReadHistoryCore(refId, visitor, work);
            }
            catch (Exception error) { errors.Add(error); }
            finally {
                CloseHistoryTemporary(ref errors);
                if (_faulted) { CleanupOwned(ref errors); }
            }
            errors.ThrowIfAny();
            CheckHistoryState(cancellationToken);
            return result;
        }
        finally { _historyActive = false; }
    }

    private AteliaResult<HistoryEnd> ReadHistoryCore(RefId refId, Func<RefBytesSnapshot, bool> visitor,
        HistoryWork work) {
        var visited = new HashSet<RefId>();
        ForkOrigin? source = null;
        RefBytesSnapshot? childInitial = null;

        while (true) {
            AteliaError? budgetError = work.CheckSnapshotAvailable();
            if (budgetError is not null) { return budgetError; }
            if (!visited.Add(refId)) { return InvalidHistoryOrigin("The inherited ref path contains a cycle."); }
            budgetError = work.TakeWorkStep();
            if (budgetError is not null) { return budgetError; }
            var opened = GetHistoryRef(refId.LocalId, work);
            if (opened.IsFailure) { return opened.Error!; }
            IRbfFile file = opened.Value!;
            var checkedHeader = RefRecordReader.CheckHeader(file, _identity, refId, work.TakeWorkStep, work.CheckState);
            if (checkedHeader.IsFailure) { return checkedHeader.Error!; }
            CheckedRefHeader header = checkedHeader.Value;

            work.CheckState();
            long fixedTail = file.TailOffset;
            RbfReverseEnumerator reverse;
            if (source is ForkOrigin origin) {
                SizedPtr ticket = origin.SourceSnapshotTicket;
                if (ticket.Offset < header.InitialSnapshotOffset
                    || file.GetPhysicalOffsetImmediatelyAfter(ticket) > fixedTail) {
                    return InvalidHistoryOrigin("The exact source Snapshot lies outside the checked header's completed range.");
                }

                // The direct trailer read bounds kind/meta/payload dimensions before either helper's full read.
                budgetError = work.CheckWorkAvailable();
                if (budgetError is not null) { return budgetError; }
                var sourceInfo = file.ReadFrameInfo(ticket);
                work.CheckState();
                if (sourceInfo.IsFailure) { return sourceInfo.Error!; }
                if (!RefRecordCodec.IsSnapshotShape(sourceInfo.Value)) {
                    return new RefRecordError("The exact source physical record has an invalid Snapshot shape.");
                }

                budgetError = work.TakeWorkStep();
                if (budgetError is not null) { return budgetError; }
                var boundary = file.GetScanBoundaryAfter(ticket);
                work.CheckState();
                if (boundary.IsFailure) { return boundary.Error!; }

                // Boundary ScanReverse independently rereads the complete anchor and its Fence.
                budgetError = work.TakeWorkStep();
                if (budgetError is not null) { return budgetError; }
                var sequence = file.ScanReverse(boundary.Value, showTombstone: true);
                work.CheckState();
                if (sequence.IsFailure) { return sequence.Error!; }
                reverse = sequence.Value.GetEnumerator();
            }
            else {
                reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
                work.CheckState();
            }

            bool first = true;
            while (true) {
                budgetError = work.CheckSnapshotAvailable();
                if (budgetError is not null) { return budgetError; }
                budgetError = work.TakeWorkStep();
                if (budgetError is not null) { return budgetError; }
                bool hasSnapshot = reverse.MoveNext();
                work.CheckState();
                if (!hasSnapshot) {
                    return reverse.TerminationError
                        ?? new RefRecordError("The historical sequence ended before its direct initial Snapshot was proved.");
                }

                RbfFrameInfo info = reverse.Current;
                if (first) {
                    if (source is ForkOrigin exactSource) {
                        if (info.Ticket.Packed != exactSource.SourceSnapshotTicket.Packed) {
                            return InvalidHistoryOrigin("The bounded reverse scan did not start at the exact source Snapshot.");
                        }
                    }
                    else if (file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != fixedTail) {
                        return new RefRecordError("The first historical Snapshot must end at the fixed actual EOF.");
                    }
                    first = false;
                }
                if (info.Ticket.Offset < header.InitialSnapshotOffset || !RefRecordCodec.IsSnapshotShape(info)) {
                    return new RefRecordError("The historical record is not a legal Snapshot after the checked header.");
                }

                budgetError = work.TakeWorkStep();
                if (budgetError is not null) { return budgetError; }
                var read = RefRecordReader.ReadSnapshot(info, refId);
                work.CheckState();
                if (read.IsFailure) { return read.Error!; }
                RefBytesSnapshot snapshot = read.Value!;
                if (childInitial is not null) {
                    if (!snapshot.Payload.SequenceEqual(childInitial.Payload)) {
                        return InvalidHistoryOrigin("The child initial payload is not an exact byte clone of its source Snapshot.");
                    }
                    childInitial = null;
                }

                bool isInitial = info.Ticket.Offset == header.InitialSnapshotOffset;
                work.DeliverSnapshot();
                bool keepGoing = visitor(snapshot);
                work.CheckState();
                if (!keepGoing) { return HistoryEnd.VisitorStopped; }
                if (!isInitial) { continue; }
                if (header.Origin is not ForkOrigin nextSource) { return HistoryEnd.Complete; }

                childInitial = snapshot;
                source = nextSource;
                refId = nextSource.SourceRefId;
                CloseHistoryTemporary();
                work.CheckState();
                break;
            }
        }
    }

    private AteliaResult<IRbfFile> GetHistoryRef(uint localId, HistoryWork work) {
        work.CheckState();
        if (_ownedRefs.TryGetValue(localId, out OwnedRef? cached)) {
            return AteliaResult<IRbfFile>.Success(cached.File!);
        }
        if (_historyRef is not null) { throw new InvalidOperationException("The previous history temporary was not released."); }

        var resolved = _files.ResolveRef(localId, _markOwnedCleanupFault);
        work.CheckState();
        if (resolved.IsFailure) { return resolved.Error!; }

        IRbfFile file;
        work.CheckState();
        try { file = _files.OpenRef(resolved.Value, IsReadOnly, _markOwnedCleanupFault); }
        catch {
            // Writable access may have attempted recovery; read-only query exceptions alone do not fault.
            if (!IsReadOnly) { _faulted = true; }
            throw;
        }
        _historyRef = file;
        _historyRefId = localId;
        work.CheckState();
        return AteliaResult<IRbfFile>.Success(file);
    }

    private void CheckHistoryState(CancellationToken cancellationToken) {
        EnsureUsable();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IRbfFile? TakeHistoryTemporary() {
        IRbfFile? toClose = _historyRef;
        _historyRef = null;
        _historyRefId = 0;
        return toClose;
    }

    private void CloseHistoryTemporary() {
        IRbfFile? toClose = TakeHistoryTemporary();
        try { toClose?.Dispose(); }
        catch { _faulted = true; throw; }
    }

    private void CloseHistoryTemporary(ref CleanupErrors errors) {
        IRbfFile? toClose = TakeHistoryTemporary();
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private static AteliaError InvalidHistoryOrigin(string detail) =>
        new VersionStoreRuntimeError("InvalidForkOrigin", detail);

    // A single call owns the counters; helper callbacks charge their actual MoveNext and complete read separately.
    private sealed class HistoryWork(VersionStore owner, HistoryBudget budget, CancellationToken cancellationToken) {
        private long _snapshotsRemaining = budget.MaxSnapshots;
        private long _workRemaining = budget.MaxWorkSteps;

        internal void CheckState() => owner.CheckHistoryState(cancellationToken);

        internal AteliaError? CheckSnapshotAvailable() {
            CheckState();
            return _snapshotsRemaining > 0 ? null : BudgetExceeded();
        }

        internal AteliaError? TakeWorkStep() {
            AteliaError? error = CheckWorkAvailable();
            if (error is not null) { return error; }
            _workRemaining--;
            return null;
        }

        internal AteliaError? CheckWorkAvailable() {
            CheckState();
            return _workRemaining > 0 ? null : BudgetExceeded();
        }

        internal void DeliverSnapshot() { _snapshotsRemaining--; }

        private static AteliaError BudgetExceeded() =>
            new VersionStoreRuntimeError("HistoryBudgetExceeded", "The history query cannot continue within its supplied budget.");
    }
}
