using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Internal.Tags;

namespace Atelia.VersionStore;

public sealed partial class VersionStore {
    /// <summary>在只读 owner 上完整校验全部正式 ref、fork 来源及实际 tag 桶，成功后交付自有统计。</summary>
    /// <remarks>
    /// 首个错误停止；不恢复、修复、发布、解码 opaque payload 或检查应用 data 闭包。
    /// 完整检查所有普通 Snapshot 与 CU；合法未成立 CU 不计发布快照，也不能满足 fork 来源。
    /// 可写 owner 返回 ReadOnlyRequired，活动 history/tag visitor 返回相应 Active 失败，均不执行 I/O。
    /// </remarks>
    public AteliaResult<VersionStoreAudit> Audit(CancellationToken cancellationToken = default) {
        EnsureOwnerUsable();
        if (_tagScanActive) { return TagScanActive(); }
        if (_historyActive) {
            return new VersionStoreRuntimeError("HistoryActive", "Audit is not allowed during an active history visitor.");
        }
        if (!IsReadOnly) {
            return new VersionStoreRuntimeError("ReadOnlyRequired", "Audit requires a read-only VersionStore owner.");
        }
        void CheckState() {
            EnsureOwnerUsable();
            cancellationToken.ThrowIfCancellationRequested();
        }

        CheckState();
        var resources = new AuditResources();
        AteliaError? result = null;
        CleanupErrors errors = default;
        try { result = AuditCore(resources, CheckState); }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseConditionalPeer(ref errors);
            CloseAuditResource(ref resources.Child, ref errors);
            CloseAuditResource(ref resources.File, ref errors);
            CloseAuditResource(ref resources.Buckets, ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        CheckState();
        if (result is not null) { return result; }
        // No report exists until all scans reached normal EOF and every necessary owned close succeeded.
        return new VersionStoreAudit(resources.RefCount, resources.SnapshotCount, resources.ForkCount,
            resources.TagBucketCount, resources.TagCount);
    }

    private AteliaError? AuditCore(AuditResources resources, Action checkState) {
        var nodes = new Dictionary<RefId, AuditRefNode>();
        AteliaError? BeforeHeaderWork() { checkState(); return null; }
        AteliaError? discovery = _files.VisitRefTargets(target => {
            checkState();
            RefId refId = RefId.Create(_identity, target.LocalId);
            if (nodes.ContainsKey(refId)) {
                return InvalidHistoryOrigin("The formal ref enumeration repeated an identity.");
            }
            resources.File = _files.OpenRef(target, readOnly: true, _markOwnedCleanupFault);
            checkState();
            var header = RefRecordReader.CheckHeader(resources.File, _identity, refId, BeforeHeaderWork, checkState);
            checkState();
            if (header.IsFailure) { return header.Error!; }
            nodes.Add(refId, new AuditRefNode(target, header.Value));
            resources.RefCount = checked(resources.RefCount + 1);
            CloseAuditResource(ref resources.File);
            checkState();
            return null;
        }, checkState, _markOwnedCleanupFault);
        checkState();
        if (discovery is not null) { return discovery; }
        AteliaError? graphError = PrepareAuditForkGraph(nodes, resources, checkState);
        if (graphError is not null) { return graphError; }

        foreach (var pair in nodes) {
            checkState();
            AuditRefNode node = pair.Value;
            resources.File = _files.OpenRef(node.Target, readOnly: true, _markOwnedCleanupFault);
            checkState();
            var header = CheckAuditHeader(resources.File, node, checkState);
            if (header.IsFailure) { return header.Error!; }
            AteliaError? contentError = AuditRefFrames(resources, node, header.Value, checkState);
            CloseAuditResource(ref resources.File);
            checkState();
            if (contentError is not null) { return contentError; }
        }

        checkState();
        resources.Buckets = GetTagFiles().EnumerateTagBucketTargets(checkState, _markOwnedCleanupFault);
        checkState();
        while (true) {
            checkState();
            bool hasBucket = resources.Buckets.MoveNext();
            checkState();
            if (!hasBucket) { return null; }
            QualifiedTagBucketTarget target = resources.Buckets.Current;
            checkState();
            resources.File = GetTagFiles().OpenTagBucket(target, readOnly: true, _markOwnedCleanupFault);
            checkState();
            var header = TagRecordReader.CheckHeader(resources.File, _identity, target.BucketId, checkState);
            if (header.IsFailure) { return header.Error!; }
            AteliaError? contentError = AuditTagFrames(resources, header.Value, checkState);
            CloseAuditResource(ref resources.File);
            checkState();
            if (contentError is not null) { return contentError; }
            resources.TagBucketCount = checked(resources.TagBucketCount + 1);
        }
    }

    private static AteliaError? PrepareAuditForkGraph(Dictionary<RefId, AuditRefNode> nodes,
        AuditResources resources, Action checkState) {
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is not ForkOrigin origin) { continue; }
            if (origin.SourceRefId == pair.Key || !nodes.TryGetValue(origin.SourceRefId, out AuditRefNode? source)) {
                return InvalidHistoryOrigin("A declared source is self-linked or missing from the checked formal ref set.");
            }
            source.Children ??= [];
            if (!source.Children.TryGetValue(origin.SourceSnapshotTicket, out List<AuditRefNode>? children)) {
                children = [];
                source.Children.Add(origin.SourceSnapshotTicket, children);
            }
            children.Add(pair.Value);
            resources.ForkCount = checked(resources.ForkCount + 1);
        }

        // Each node has at most one source. White/gray/black paths make deep and shared chains linear.
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.State != 0) { continue; }
            AuditRefNode? current = pair.Value;
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
        return null;
    }

    private AteliaResult<CheckedRefHeader> CheckAuditHeader(IRbfFile file, AuditRefNode node, Action checkState) {
        AteliaError? BeforeHeaderWork() { checkState(); return null; }
        var header = RefRecordReader.CheckHeader(file, _identity, node.RefId, BeforeHeaderWork, checkState);
        checkState();
        if (header.IsFailure) { return header.Error!; }
        if (header.Value.HeaderTicket != node.HeaderTicket || header.Value.InitialSnapshotOffset != node.InitialOffset
            || header.Value.Origin != node.Origin) {
            return new RefHeaderError("The formal ref header differs from its checked immutable declaration.");
        }
        return header.Value;
    }

    private AteliaError? AuditRefFrames(AuditResources resources, AuditRefNode node,
        CheckedRefHeader header, Action checkState) {
        IRbfFile file = resources.File!;
        checkState();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState();
        bool hasHeader = forward.MoveNext();
        checkState();
        if (!hasHeader) {
            return forward.TerminationError ?? new RefHeaderError("The checked RefHeader is missing from the physical chain.");
        }
        if (forward.Current.Ticket != header.HeaderTicket || !RefRecordCodec.IsHeaderShape(forward.Current)) {
            return new RefHeaderError("The first physical frame differs from its checked RefHeader.");
        }

        bool hasInitial = false;
        while (true) {
            checkState();
            bool hasSnapshot = forward.MoveNext();
            checkState();
            if (!hasSnapshot) {
                if (forward.TerminationError is { } error) { return error; }
                if (!hasInitial) { return new RefRecordError("The direct second physical initial Snapshot is missing."); }
                if (node.Children is { Count: > 0 }) {
                    return InvalidHistoryOrigin("A declared exact source ticket is not a published snapshot member of its formal ref's physical chain.");
                }
                return null;
            }
            RbfFrameInfo info = forward.Current;
            if (!hasInitial && (info.Ticket.Offset != header.InitialSnapshotOffset || !RefRecordCodec.IsSnapshotShape(info))) {
                return new RefRecordError("The ordinary initial Snapshot is not the direct second physical frame.");
            }
            hasInitial = true;
            if (!RefRecordCodec.IsPublicationShape(info)) {
                return new RefRecordError("The physical ref chain contains an invalid publication record.");
            }
            if (info.Tag == RefRecordCodec.ConditionalUpdateTag) {
                var published = ReadPublication(file, header, info, checkState: checkState, deferOwnedCleanup: true);
                checkState();
                if (published.IsFailure) { return published.Error!; }
                RefBytesSnapshot? snapshot = published.Value.Snapshot;
                if (snapshot is null) {
                    if (node.Children is not null && node.Children.ContainsKey(info.Ticket)) {
                        return InvalidHistoryOrigin("A declared exact source CU is not a published snapshot.");
                    }
                    continue;
                }
                resources.SnapshotCount = checked(resources.SnapshotCount + 1);
                AteliaError? sourceError = AuditSourceChildren(resources, node, info.Ticket, snapshot.Payload, checkState);
                if (sourceError is not null) { return sourceError; }
                continue;
            }

            var read = RefRecordReader.ReadCheckedSnapshotFrame(info, checkState);
            if (read.IsFailure) { return read.Error!; }
            using RbfPooledFrame frame = read.Value!;
            checkState();
            resources.SnapshotCount = checked(resources.SnapshotCount + 1);
            AteliaError? cloneError = AuditSourceChildren(resources, node, info.Ticket,
                frame.PayloadAndMeta[..^frame.TailMetaLength], checkState);
            if (cloneError is not null) { return cloneError; }
        }
    }

    private AteliaError? AuditSourceChildren(AuditResources resources, AuditRefNode node, SizedPtr ticket,
        ReadOnlySpan<byte> sourcePayload, Action checkState) {
        if (node.Children is null || !node.Children.TryGetValue(ticket, out List<AuditRefNode>? children)) { return null; }
        foreach (AuditRefNode child in children) {
            AteliaError? cloneError = AuditForkClone(resources, child, sourcePayload, checkState);
            if (cloneError is not null) { return cloneError; }
        }
        node.Children.Remove(ticket);
        return null;
    }

    private AteliaError? AuditForkClone(AuditResources resources, AuditRefNode child,
        ReadOnlySpan<byte> sourcePayload, Action checkState) {
        checkState();
        resources.Child = _files.OpenRef(child.Target, readOnly: true, _markOwnedCleanupFault);
        checkState();
        var header = CheckAuditHeader(resources.Child, child, checkState);
        if (header.IsFailure) { return header.Error!; }
        checkState();
        var next = resources.Child.ReadFrameInfoImmediatelyAfter(header.Value.HeaderTicket);
        checkState();
        if (next.IsFailure) { return next.Error!; }
        if (!next.Value.HasValue || next.Value.Value.Ticket.Offset != header.Value.InitialSnapshotOffset) {
            return new RefRecordError("The fork's direct second physical initial Snapshot is missing.");
        }
        var read = RefRecordReader.ReadCheckedSnapshotFrame(next.Value.Value, checkState);
        if (read.IsFailure) { return read.Error!; }
        using (RbfPooledFrame initial = read.Value!) {
            checkState();
            if (!sourcePayload.SequenceEqual(initial.PayloadAndMeta[..^initial.TailMetaLength])) {
                return InvalidHistoryOrigin("The fork's initial root bytes differ from its exact main-chain source Snapshot.");
            }
        }
        CloseAuditResource(ref resources.Child);
        checkState();
        return null;
    }

    private static AteliaError? AuditTagFrames(AuditResources resources, CheckedTagBucketHeader header,
        Action checkState) {
        checkState();
        var forward = resources.File!.ScanForward(showTombstone: true).GetEnumerator();
        checkState();
        bool hasHeader = forward.MoveNext();
        checkState();
        if (!hasHeader) {
            return forward.TerminationError ?? new TagHeaderError("The checked BucketHeader is missing from the physical chain.");
        }
        if (forward.Current.Ticket != header.HeaderTicket || !TagRecordCodec.IsHeaderShape(forward.Current)) {
            return new TagHeaderError("The first physical frame differs from its checked BucketHeader.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (true) {
            checkState();
            bool hasRecord = forward.MoveNext();
            checkState();
            if (!hasRecord) { return forward.TerminationError; }
            var metadata = TagRecordReader.CheckCompleteRecord(forward.Current, header, checkState);
            checkState();
            if (metadata.IsFailure) { return metadata.Error!; }
            if (!names.Add(metadata.Value.Name)) {
                return new TagDuplicateNameError("The formal tag bucket contains multiple Ordinal-equal names.");
            }
            resources.TagCount = checked(resources.TagCount + 1);
        }
    }

    private void CloseAuditResource<T>(ref T? slot) where T : class, IDisposable {
        T? toClose = slot;
        slot = null;
        try { toClose?.Dispose(); }
        catch { _faulted = true; throw; }
    }

    private void CloseAuditResource<T>(ref T? slot, ref CleanupErrors errors) where T : class, IDisposable {
        T? toClose = slot;
        slot = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private sealed class AuditResources {
        internal IRbfFile? File;
        internal IRbfFile? Child;
        internal IEnumerator<QualifiedTagBucketTarget>? Buckets;
        internal long RefCount;
        internal long SnapshotCount;
        internal long ForkCount;
        internal long TagBucketCount;
        internal long TagCount;
    }

    private sealed class AuditRefNode(QualifiedRefTarget target, CheckedRefHeader header) {
        internal QualifiedRefTarget Target { get; } = target;
        internal RefId RefId { get; } = header.RefId;
        internal SizedPtr HeaderTicket { get; } = header.HeaderTicket;
        internal long InitialOffset { get; } = header.InitialSnapshotOffset;
        internal ForkOrigin? Origin { get; } = header.Origin;
        internal Dictionary<SizedPtr, List<AuditRefNode>>? Children;
        internal byte State;
    }
}
