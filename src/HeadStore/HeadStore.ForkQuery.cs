using System.Collections;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    /// <summary>读取指定 Head 的已检查 header 中声明的固定创建来源；独立根没有来源。</summary>
    /// <remarks>
    /// 只访问所选 Head，不枚举其他 Head 或读取来源 Entry，也不证明来源存在、发布成立或整图健康。
    /// 可在历史 visitor 内串行调用；借用已有句柄，其他目标用临时句柄，不增加长期缓存。
    /// 可写惰性打开仍可能执行正常尾恢复；只读打开不修复。
    /// </remarks>
    public AteliaResult<HeadOrigin> ReadHeadOrigin(HeadId headId) {
        EnsureOwnerUsable();
        if (!headId.TryGetLocalId(_identity, out uint localId)) { return InvalidHead(); }
        AteliaError? BeforeHeaderWork() { EnsureOwnerUsable(); return null; }

        IRbfFile? temporary = null;
        CleanupErrors errors = default;
        AteliaResult<HeadOrigin> result = default;
        try {
            var resolved = _files.ResolveHead(localId, _markOwnedCleanupFault);
            EnsureOwnerUsable();
            if (resolved.IsFailure) { result = resolved.Error!; }
            else {
                IRbfFile file = GetForkQueryHead(resolved.Value, ref temporary, EnsureOwnerUsable);
                var header = HeadRecordReader.CheckHeader(file, _identity, headId, BeforeHeaderWork, EnsureOwnerUsable);
                EnsureOwnerUsable();
                if (header.IsFailure) { result = header.Error!; }
                else {
                    HeadEntryId? source = header.Value.Origin is ForkOrigin origin
                        ? HeadEntryId.Create(origin.SourceHeadId, origin.SourceEntryTicket) : null;
                    result = new HeadOrigin(source);
                }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseForkQueryTemporary(ref temporary, ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        EnsureOwnerUsable();
        return result;
    }

    /// <summary>在单次有界扫描中检查全部正式 head header，交付完整、自有的创建来源边集合。</summary>
    /// <remarks>预算包含无来源根；不读取 Entry 或逐边校验源 ticket。历史 visitor 可以串行调用。</remarks>
    public AteliaResult<IReadOnlyList<HeadOriginInfo>> ListHeadOrigins(int maxHeads,
        CancellationToken cancellationToken = default) {
        EnsureOwnerUsable();
        if (maxHeads <= 0) {
            return new HeadStoreRuntimeError("ArgumentError", "A positive formal head query budget is required.");
        }
        void CheckState() {
            EnsureOwnerUsable();
            cancellationToken.ThrowIfCancellationRequested();
        }
        AteliaError? BeforeHeaderWork() {
            CheckState();
            return null;
        }
        CheckState();

        IRbfFile? temporary = null;
        CleanupErrors errors = default;
        AteliaResult<IReadOnlyList<HeadOriginInfo>> result = default;
        try {
            // Grow only for checked nodes actually visited, never from the caller's upper bound.
            var nodes = new Dictionary<HeadId, ForkQueryNode>();
            AteliaError? scanError = _files.VisitHeadTargets(target => {
                CheckState();
                if (nodes.Count == maxHeads) {
                    return new HeadStoreRuntimeError("ForkQueryBudgetExceeded",
                        "The complete formal head set exceeds the supplied query budget.");
                }
                HeadId headId = HeadId.Create(_identity, target.LocalId);
                if (nodes.ContainsKey(headId)) {
                    return InvalidHistoryOrigin("The formal head enumeration repeated an identity.");
                }
                IRbfFile file = GetForkQueryHead(target, ref temporary, CheckState);
                var header = HeadRecordReader.CheckHeader(file, _identity, headId, BeforeHeaderWork, CheckState);
                CheckState();
                if (header.IsFailure) { return header.Error!; }
                nodes.Add(headId, new ForkQueryNode(header.Value.Origin));
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

    private IRbfFile GetForkQueryHead(QualifiedHeadTarget target, ref IRbfFile? temporary, Action checkState) {
        checkState();
        if (_historyHeadId == target.LocalId && _historyHead is { } historyFile) { return historyFile; }
        if (_ownedHeads.TryGetValue(target.LocalId, out OwnedHead? cached)) { return cached.File!; }
        if (temporary is not null) { throw new InvalidOperationException("The previous fork query temporary was not released."); }
        try { temporary = _files.OpenHead(target, IsReadOnly, _markOwnedCleanupFault); }
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

    private static AteliaResult<IReadOnlyList<HeadOriginInfo>> BuildForkQueryResult(
        Dictionary<HeadId, ForkQueryNode> nodes, Action checkState) {
        int edgeCount = 0;
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is not ForkOrigin origin) { continue; }
            if (origin.SourceHeadId == pair.Key || !nodes.ContainsKey(origin.SourceHeadId)) {
                return InvalidHistoryOrigin("A declared source is self-linked or missing from the checked formal head set.");
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
                current = current.Origin is ForkOrigin origin ? nodes[origin.SourceHeadId] : null;
            }
            if (current is { State: 1 }) { return InvalidHistoryOrigin("The declared fork graph contains a cycle."); }
            current = pair.Value;
            while (current is not null && current.State == 1) {
                checkState();
                current.State = 2;
                current = current.Origin is ForkOrigin origin ? nodes[origin.SourceHeadId] : null;
            }
        }

        checkState();
        var edges = new HeadOriginInfo[edgeCount];
        int index = 0;
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is ForkOrigin origin) {
                edges[index++] = new HeadOriginInfo(pair.Key, origin.SourceHeadId, origin.SourceEntryTicket);
            }
        }
        return AteliaResult<IReadOnlyList<HeadOriginInfo>>.Success(new ForkInfoList(edges));
    }

    private sealed class ForkQueryNode(ForkOrigin? origin) {
        internal ForkOrigin? Origin { get; } = origin;
        internal byte State;
    }

    // Only these read interfaces escape; neither a mutable array/List nor ICollection.SyncRoot is exposed.
    private sealed class ForkInfoList(HeadOriginInfo[] entries) : IReadOnlyList<HeadOriginInfo> {
        private readonly HeadOriginInfo[] _entries = entries;
        public int Count => _entries.Length;
        public HeadOriginInfo this[int index] => _entries[index];
        public IEnumerator<HeadOriginInfo> GetEnumerator() {
            for (int index = 0; index < _entries.Length; index++) { yield return _entries[index]; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
