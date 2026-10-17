using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    private IRbfFile? _historyHead;
    private uint _historyHeadId;
    private bool _historyActive;

    /// <summary>从固定上界向前交付自有 checked 发布Entry，按 ForkOrigin 接续精确来源前缀。</summary>
    /// <remarks>
    /// visitor 的 false 已接收当前项并正常停止；活动调用拒绝递归历史及本 owner 的所有 mutation。
    /// 已交付的值可保留；停止或预算失败不证明未访问历史健康。
    /// 合法未成立 CU 不交付Entry，但其必要读取和 peer 访问仍消耗工作预算。
    /// </remarks>
    public AteliaResult<HeadHistoryEnd> ReadHeadHistory(HeadId headId, HeadHistoryBudget budget,
        Func<HeadEntry, bool> visitor, CancellationToken cancellationToken = default) =>
        ReadHistory(headId, null, budget, visitor, cancellationToken);

    /// <summary>从准确 Entry（包含该项）向前交付自有发布记录，并沿 ForkOrigin 接续来源历史。</summary>
    /// <remarks>
    /// 起点必须是已成立发布，不能跳过或回退；后续合法未成立 CU 按普通历史规则跳过。
    /// 每页重复的起点也消耗 Entry 与工作预算；visitor 返回 false 表示已经接收当前项。
    /// 与 ReadHeadHistory 共用回调、取消、资源清理及 mutation 重入限制。
    /// </remarks>
    public AteliaResult<HeadHistoryEnd> ReadEntryHistory(HeadEntryId startInclusive, HeadHistoryBudget budget,
        Func<HeadEntry, bool> visitor, CancellationToken cancellationToken = default) =>
        ReadHistory(startInclusive.HeadId, startInclusive.Ticket, budget, visitor, cancellationToken);

    private AteliaResult<HeadHistoryEnd> ReadHistory(HeadId headId, SizedPtr? startInclusive,
        HeadHistoryBudget budget, Func<HeadEntry, bool> visitor, CancellationToken cancellationToken) {
        EnsureOwnerUsable();
        if (!headId.TryGetLocalId(_identity, out _)) { return InvalidHead(); }
        if (startInclusive is { Length: <= 0 }) { return InvalidEntryId(); }
        if (visitor is null || budget.MaxEntries <= 0 || budget.MaxWorkSteps <= 0) {
            return new HeadStoreRuntimeError("ArgumentError", "A nonnull visitor and two positive history budgets are required.");
        }
        if (_tagScanActive) { return TagScanActive(); }
        if (_historyActive) {
            return new HeadStoreRuntimeError("HistoryActive", "A history visitor is already active on this HeadStore.");
        }

        _historyActive = true;
        try {
            AteliaResult<HeadHistoryEnd> result = default;
            CleanupErrors errors = default;
            try {
                var work = new HistoryWork(this, budget, cancellationToken);
                result = ReadHistoryCore(headId, startInclusive, visitor, work);
            }
            catch (Exception error) { errors.Add(error); }
            finally {
                CloseConditionalPeer(ref errors);
                CloseHistoryTemporary(ref errors);
                if (_faulted) { CleanupOwned(ref errors); }
            }
            errors.ThrowIfAny();
            CheckHistoryState(cancellationToken);
            return result;
        }
        finally { _historyActive = false; }
    }

    private AteliaResult<HeadHistoryEnd> ReadHistoryCore(HeadId headId, SizedPtr? exactStart,
        Func<HeadEntry, bool> visitor, HistoryWork work) {
        var visited = new HashSet<HeadId>();
        bool declaredOrigin = false;

        while (true) {
            AteliaError? budgetError = work.CheckEntryAvailable();
            if (budgetError is not null) { return budgetError; }
            if (!visited.Add(headId)) { return InvalidHistoryOrigin("The inherited head path contains a cycle."); }
            budgetError = work.TakeWorkStep();
            if (budgetError is not null) { return budgetError; }
            var opened = GetHistoryHead(headId.GetLocalId(_identity), work);
            if (opened.IsFailure) { return opened.Error!; }
            IRbfFile file = opened.Value!;
            var checkedHeader = HeadRecordReader.CheckHeader(file, _identity, headId, work.TakeWorkStep, work.CheckState);
            if (checkedHeader.IsFailure) { return checkedHeader.Error!; }
            CheckedHeadHeader header = checkedHeader.Value;

            work.CheckState();
            long fixedTail = file.TailOffset;
            RbfReverseEnumerator reverse;
            if (exactStart is SizedPtr ticket) {
                if (ticket.Offset < header.RecordsOffset
                    || file.GetPhysicalOffsetImmediatelyAfter(ticket) > fixedTail) {
                    return InvalidHistoryAnchor(declaredOrigin, "The exact Entry lies outside the checked header's completed range.");
                }

                // The direct trailer read bounds kind/meta/payload dimensions before either helper's full read.
                budgetError = work.CheckWorkAvailable();
                if (budgetError is not null) { return budgetError; }
                var sourceInfo = file.ReadFrameInfo(ticket);
                work.CheckState();
                if (sourceInfo.IsFailure) { return sourceInfo.Error!; }
                if (!HeadRecordCodec.IsPublicationShape(sourceInfo.Value)) {
                    return new HeadRecordError("The exact source physical record has an invalid publication shape.");
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
                budgetError = work.CheckEntryAvailable();
                if (budgetError is not null) { return budgetError; }
                budgetError = work.TakeWorkStep();
                if (budgetError is not null) { return budgetError; }
                bool hasEntry = reverse.MoveNext();
                work.CheckState();
                if (!hasEntry) {
                    return reverse.TerminationError
                        ?? new HeadRecordError("The historical sequence ended before its checked header or root initial Entry was reached.");
                }

                RbfFrameInfo info = reverse.Current;
                bool isExactAnchor = first && exactStart.HasValue;
                if (first) {
                    if (exactStart is SizedPtr requestedTicket) {
                        if (info.Ticket.Packed != requestedTicket.Packed) {
                            return InvalidHistoryAnchor(declaredOrigin, "The bounded reverse scan did not start at the exact Entry.");
                        }
                    }
                    else if (file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != fixedTail) {
                        return new HeadRecordError("The first historical Entry must end at the fixed actual EOF.");
                    }
                    first = false;
                }
                if (info.Ticket == header.HeaderTicket && HeadRecordCodec.IsHeaderShape(info)) {
                    if (header.Origin is not ForkOrigin nextSource) {
                        return new HeadRecordError("The root head's direct initial SingleEntry is missing.");
                    }
                    exactStart = nextSource.SourceEntryTicket;
                    declaredOrigin = true;
                    headId = nextSource.SourceHeadId;
                    CloseHistoryTemporary();
                    work.CheckState();
                    break;
                }
                if (!header.IsAllowedPublication(info)) {
                    return new HeadRecordError("The historical record is not a legal publication after its checked header.");
                }

                var read = ReadPublication(file, header, info, work.TakeWorkStep, work.CheckState);
                work.CheckState();
                if (read.IsFailure) { return read.Error!; }
                HeadEntry? entry = read.Value.Entry;
                if (entry is null) {
                    if (isExactAnchor) {
                        return InvalidHistoryAnchor(declaredOrigin, "The exact CU is not a published entry.");
                    }
                    continue;
                }
                work.DeliverEntry();
                bool keepGoing = visitor(entry);
                work.CheckState();
                if (!keepGoing) { return HeadHistoryEnd.VisitorStopped; }
                // Root completion is already proved by its checked direct SingleEntry.
                // Preserve the exact entry/work budget without an additional header read.
                if (header.Origin is null && info.Ticket.Offset == header.RecordsOffset) {
                    return HeadHistoryEnd.Complete;
                }
            }
        }
    }

    private AteliaResult<IRbfFile> GetHistoryHead(uint localId, HistoryWork work) {
        work.CheckState();
        if (_ownedHeads.TryGetValue(localId, out OwnedHead? cached)) {
            return AteliaResult<IRbfFile>.Success(cached.File!);
        }
        if (_historyHead is not null) { throw new InvalidOperationException("The previous history temporary was not released."); }

        var resolved = _files.ResolveHead(localId, _markOwnedCleanupFault);
        work.CheckState();
        if (resolved.IsFailure) { return resolved.Error!; }

        IRbfFile file;
        work.CheckState();
        try { file = _files.OpenHead(resolved.Value, IsReadOnly, _markOwnedCleanupFault); }
        catch {
            // Writable access may have attempted recovery; read-only query exceptions alone do not fault.
            if (!IsReadOnly) { _faulted = true; }
            throw;
        }
        _historyHead = file;
        _historyHeadId = localId;
        work.CheckState();
        return AteliaResult<IRbfFile>.Success(file);
    }

    private void CheckHistoryState(CancellationToken cancellationToken) {
        EnsureOwnerUsable();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IRbfFile? TakeHistoryTemporary() {
        IRbfFile? toClose = _historyHead;
        _historyHead = null;
        _historyHeadId = 0;
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
        new HeadStoreRuntimeError("InvalidForkOrigin", detail);

    private static AteliaError InvalidHistoryAnchor(bool declaredOrigin, string detail) =>
        declaredOrigin ? InvalidHistoryOrigin(detail) : new HeadRecordError(detail);

    // A single call owns the counters; helper callbacks charge their actual MoveNext and complete read separately.
    private sealed class HistoryWork(HeadStore owner, HeadHistoryBudget budget, CancellationToken cancellationToken) {
        private long _entriesRemaining = budget.MaxEntries;
        private long _workRemaining = budget.MaxWorkSteps;

        internal void CheckState() => owner.CheckHistoryState(cancellationToken);

        internal AteliaError? CheckEntryAvailable() {
            CheckState();
            return _entriesRemaining > 0 ? null : BudgetExceeded();
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

        internal void DeliverEntry() { _entriesRemaining--; }

        private static AteliaError BudgetExceeded() =>
            new HeadStoreRuntimeError("HistoryBudgetExceeded", "The history query cannot continue within its supplied budget.");
    }
}
