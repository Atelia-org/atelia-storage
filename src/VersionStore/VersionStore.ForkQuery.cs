using System.Collections;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    /// <summary>在单次有界扫描中检查全部正式 ref header，交付完整、自有的创建来源边集合。</summary>
    /// <remarks>预算包含无来源根；不读取 Snapshot 或逐边校验源 ticket。历史 visitor 可以串行调用。</remarks>
    public AteliaResult<IReadOnlyList<ForkInfo>> ListForks(int maxRefs,
        CancellationToken cancellationToken = default) {
        EnsureUsable();
        if (maxRefs <= 0) {
            return new VersionStoreRuntimeError("ArgumentError", "A positive formal ref query budget is required.");
        }
        void CheckState() {
            EnsureUsable();
            cancellationToken.ThrowIfCancellationRequested();
        }
        AteliaError? BeforeHeaderWork() {
            CheckState();
            return null;
        }
        CheckState();

        IRbfFile? temporary = null;
        CleanupErrors errors = default;
        AteliaResult<IReadOnlyList<ForkInfo>> result = default;
        try {
            // Grow only for checked nodes actually visited, never from the caller's upper bound.
            var nodes = new Dictionary<RefId, ForkQueryNode>();
            AteliaError? scanError = _files.VisitRefTargets(target => {
                CheckState();
                if (nodes.Count == maxRefs) {
                    return new VersionStoreRuntimeError("ForkQueryBudgetExceeded",
                        "The complete formal ref set exceeds the supplied query budget.");
                }
                RefId refId = RefId.Create(_identity, target.LocalId);
                if (nodes.ContainsKey(refId)) {
                    return InvalidHistoryOrigin("The formal ref enumeration repeated an identity.");
                }
                IRbfFile file = GetForkQueryRef(target, ref temporary, CheckState);
                var header = RefRecordReader.CheckHeader(file, _identity, refId, BeforeHeaderWork, CheckState);
                CheckState();
                if (header.IsFailure) { return header.Error!; }
                nodes.Add(refId, new ForkQueryNode(header.Value.Origin));
                CheckState();
                CloseForkQueryTemporary(ref temporary);
                CheckState();
                return null;
            }, CheckState, _markOwnedCleanupFault);
            CheckState();
            if (scanError is not null) { result = scanError; }
            else { result = BuildForkQueryResult(nodes, CheckState); }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            // Backend has already released its enumerator; preserve any primary error and release the root lock last.
            CloseForkQueryTemporary(ref temporary, ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        CheckState();
        return result;
    }

    private IRbfFile GetForkQueryRef(QualifiedRefTarget target, ref IRbfFile? temporary, Action checkState) {
        checkState();
        if (_historyRefId == target.LocalId && _historyRef is { } historyFile) { return historyFile; }
        if (_ownedRefs.TryGetValue(target.LocalId, out OwnedRef? cached)) { return cached.File!; }
        if (temporary is not null) { throw new InvalidOperationException("The previous fork query temporary was not released."); }
        try { temporary = _files.OpenRef(target, IsReadOnly, _markOwnedCleanupFault); }
        catch {
            // Writable opening can attempt recovery. Pure read-only opening failures do not stop a healthy owner.
            if (!IsReadOnly) { _faulted = true; }
            throw;
        }
        checkState();
        return temporary;
    }

    private void CloseForkQueryTemporary(ref IRbfFile? temporary) {
        IRbfFile? toClose = temporary;
        temporary = null;
        try { toClose?.Dispose(); }
        catch { _faulted = true; throw; }
    }

    private void CloseForkQueryTemporary(ref IRbfFile? temporary, ref CleanupErrors errors) {
        IRbfFile? toClose = temporary;
        temporary = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private static AteliaResult<IReadOnlyList<ForkInfo>> BuildForkQueryResult(
        Dictionary<RefId, ForkQueryNode> nodes, Action checkState) {
        int edgeCount = 0;
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is not ForkOrigin origin) { continue; }
            if (origin.SourceRefId == pair.Key || !nodes.ContainsKey(origin.SourceRefId)) {
                return InvalidHistoryOrigin("A declared source is self-linked or missing from the checked formal ref set.");
            }
            edgeCount++;
        }

        // Every node has at most one source. Walk white nodes once, then mark that path complete;
        // completed paths are never retraversed, so deep chains need neither recursion nor a second node table.
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.State != 0) { continue; }
            ForkQueryNode? current = pair.Value;
            while (current is not null && current.State == 0) {
                checkState();
                current.State = 1;
                current = current.Origin is ForkOrigin origin ? nodes[origin.SourceRefId] : null;
            }
            if (current is { State: 1 }) { return InvalidHistoryOrigin("The declared fork graph contains a cycle."); }
            current = pair.Value;
            while (current is not null && current.State == 1) {
                checkState();
                current.State = 2;
                current = current.Origin is ForkOrigin origin ? nodes[origin.SourceRefId] : null;
            }
        }

        checkState();
        var edges = new ForkInfo[edgeCount];
        int index = 0;
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is ForkOrigin origin) {
                edges[index++] = new ForkInfo(pair.Key, origin.SourceRefId, origin.SourceSnapshotTicket);
            }
        }
        return AteliaResult<IReadOnlyList<ForkInfo>>.Success(new ForkInfoList(edges));
    }

    private sealed class ForkQueryNode(ForkOrigin? origin) {
        internal ForkOrigin? Origin { get; } = origin;
        internal byte State;
    }

    // Only these read interfaces escape; neither a mutable array/List nor ICollection.SyncRoot is exposed.
    private sealed class ForkInfoList(ForkInfo[] entries) : IReadOnlyList<ForkInfo> {
        private readonly ForkInfo[] _entries = entries;
        public int Count => _entries.Length;
        public ForkInfo this[int index] => _entries[index];
        public IEnumerator<ForkInfo> GetEnumerator() {
            for (int index = 0; index < _entries.Length; index++) { yield return _entries[index]; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
