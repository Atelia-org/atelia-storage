using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Tags;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    /// <summary>在只读 owner 上完整校验全部正式 head、fork 来源及实际 tag 桶，成功后交付自有统计。</summary>
    /// <remarks>
    /// 首个错误停止；不恢复、修复、发布、解码 opaque payload 或检查应用 data 闭包。
    /// 完整检查所有普通 SingleEntry 与 CU；合法未成立 CU 不计发布Entry，也不能满足 fork 来源。
    /// 可写 owner 返回 ReadOnlyRequired，活动 history/tag visitor 返回相应 Active 失败，均不执行 I/O。
    /// </remarks>
    public AteliaResult<HeadStoreAudit> Audit(CancellationToken cancellationToken = default) {
        EnsureOwnerUsable();
        if (_tagScanActive) { return TagScanActive(); }
        if (_historyActive) {
            return new HeadStoreRuntimeError("HistoryActive", "Audit is not allowed during an active history visitor.");
        }
        if (!IsReadOnly) {
            return new HeadStoreRuntimeError("ReadOnlyRequired", "Audit requires a read-only HeadStore owner.");
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
            CloseAuditResource(ref resources.Member, ref errors);
            CloseAuditResource(ref resources.File, ref errors);
            CloseAuditResource(ref resources.Buckets, ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        CheckState();
        if (result is not null) { return result; }
        // No report exists until all scans reached normal EOF and every necessary owned close succeeded.
        return new HeadStoreAudit(resources.HeadCount, resources.EntryCount, resources.OriginCount,
            resources.TagBucketCount, resources.TagCount);
    }

    private AteliaError? AuditCore(AuditResources resources, Action checkState) {
        var nodes = new Dictionary<HeadId, AuditHeadNode>();
        AteliaError? BeforeHeaderWork() { checkState(); return null; }
        AteliaError? discovery = _files.VisitHeadTargets(target => {
            checkState();
            HeadId headId = HeadId.Create(_identity, target.LocalId);
            if (nodes.ContainsKey(headId)) {
                return InvalidHistoryOrigin("The formal head enumeration repeated an identity.");
            }
            resources.File = _files.OpenHead(target, readOnly: true, _markOwnedCleanupFault);
            checkState();
            var header = HeadRecordReader.CheckHeader(resources.File, _identity, headId, BeforeHeaderWork, checkState);
            checkState();
            if (header.IsFailure) { return header.Error!; }
            nodes.Add(headId, new AuditHeadNode(target, header.Value));
            resources.HeadCount = checked(resources.HeadCount + 1);
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
            AuditHeadNode node = pair.Value;
            resources.File = _files.OpenHead(node.Target, readOnly: true, _markOwnedCleanupFault);
            checkState();
            var header = CheckAuditHeader(resources.File, node, checkState);
            if (header.IsFailure) { return header.Error!; }
            AteliaError? contentError = AuditHeadFrames(resources, node, header.Value, checkState);
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
            AteliaError? contentError = AuditTagFrames(resources, header.Value, nodes, checkState);
            CloseAuditResource(ref resources.File);
            checkState();
            if (contentError is not null) { return contentError; }
            resources.TagBucketCount = checked(resources.TagBucketCount + 1);
        }
    }

    private static AteliaError? PrepareAuditForkGraph(Dictionary<HeadId, AuditHeadNode> nodes,
        AuditResources resources, Action checkState) {
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.Origin is not ForkOrigin origin) { continue; }
            if (origin.SourceHeadId == pair.Key || !nodes.TryGetValue(origin.SourceHeadId, out AuditHeadNode? source)) {
                return InvalidHistoryOrigin("A declared source is self-linked or missing from the checked formal head set.");
            }
            source.PendingOrigins ??= [];
            source.PendingOrigins.Add(origin.SourceEntryTicket);
            resources.OriginCount = checked(resources.OriginCount + 1);
        }

        // Each node has at most one source. White/gray/black paths make deep and shared chains linear.
        foreach (var pair in nodes) {
            checkState();
            if (pair.Value.State != 0) { continue; }
            AuditHeadNode? current = pair.Value;
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
        return null;
    }

    private AteliaResult<CheckedHeadHeader> CheckAuditHeader(IRbfFile file, AuditHeadNode node, Action checkState) {
        AteliaError? BeforeHeaderWork() { checkState(); return null; }
        var header = HeadRecordReader.CheckHeader(file, _identity, node.HeadId, BeforeHeaderWork, checkState);
        checkState();
        if (header.IsFailure) { return header.Error!; }
        if (header.Value.HeaderTicket != node.HeaderTicket || header.Value.RecordsOffset != node.RecordsOffset
            || header.Value.Origin != node.Origin) {
            return new HeadHeaderError("The formal head header differs from its checked immutable declaration.");
        }
        return header.Value;
    }

    private AteliaError? AuditHeadFrames(AuditResources resources, AuditHeadNode node,
        CheckedHeadHeader header, Action checkState) {
        IRbfFile file = resources.File!;
        checkState();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState();
        bool hasHeader = forward.MoveNext();
        checkState();
        if (!hasHeader) {
            return forward.TerminationError ?? new HeadHeaderError("The checked HeadHeader is missing from the physical chain.");
        }
        if (forward.Current.Ticket != header.HeaderTicket || !HeadRecordCodec.IsHeaderShape(forward.Current)) {
            return new HeadHeaderError("The first physical frame differs from its checked HeadHeader.");
        }

        bool hasLocalRecord = false;
        while (true) {
            checkState();
            bool hasEntry = forward.MoveNext();
            checkState();
            if (!hasEntry) {
                if (forward.TerminationError is { } error) { return error; }
                if (!hasLocalRecord && header.Origin is null) {
                    return new HeadRecordError("The root head's direct initial SingleEntry is missing.");
                }
                if (node.PendingOrigins is { Count: > 0 }) {
                    return InvalidHistoryOrigin("A declared exact source ticket is not a published entry member of its formal head's physical chain.");
                }
                return null;
            }
            RbfFrameInfo info = forward.Current;
            if (!hasLocalRecord && info.Ticket.Offset != header.RecordsOffset) {
                return new HeadRecordError("The first local publication does not directly follow its checked header.");
            }
            hasLocalRecord = true;
            if (!header.IsAllowedPublication(info)) {
                return new HeadRecordError("The physical head chain contains an invalid publication record.");
            }
            if (info.Tag == HeadRecordCodec.ConditionalUpdateTag) {
                var published = ReadPublication(file, header, info, checkState: checkState, deferOwnedCleanup: true);
                checkState();
                if (published.IsFailure) { return published.Error!; }
                HeadEntry? entry = published.Value.Entry;
                if (entry is null) {
                    if (node.PendingOrigins is not null && node.PendingOrigins.Contains(info.Ticket)) {
                        return InvalidHistoryOrigin("A declared exact source CU is not a published entry.");
                    }
                    continue;
                }
                resources.EntryCount = checked(resources.EntryCount + 1);
                node.PendingOrigins?.Remove(info.Ticket);
                continue;
            }

            var read = HeadRecordReader.ReadCheckedSingleEntryFrame(info, checkState);
            if (read.IsFailure) { return read.Error!; }
            using RbfPooledFrame frame = read.Value!;
            checkState();
            resources.EntryCount = checked(resources.EntryCount + 1);
            node.PendingOrigins?.Remove(info.Ticket);
        }
    }

    private AteliaError? AuditTagFrames(AuditResources resources, CheckedTagBucketHeader header,
        Dictionary<HeadId, AuditHeadNode> nodes, Action checkState) {
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
            var read = TagRecordReader.CheckCompleteRecord(forward.Current, header, checkState);
            checkState();
            if (read.IsFailure) { return read.Error!; }
            HeadSetTag tag = read.Value!;
            if (!names.Add(tag.Name)) {
                return new TagDuplicateNameError("The formal tag bucket contains multiple Ordinal-equal names.");
            }
            foreach (var pair in tag.Entries) {
                checkState();
                HeadEntryId entryId = pair.Value;
                if (!nodes.TryGetValue(pair.Key, out AuditHeadNode? selected)) {
                    return InvalidHistoryOrigin("A HeadSetTag selected Head is missing from the checked formal head set.");
                }
                if (pair.Key != entryId.HeadId
                    && (selected.Origin is not ForkOrigin origin || origin.SourceHeadId != entryId.HeadId
                        || origin.SourceEntryTicket != entryId.Ticket)) {
                    return InvalidHistoryOrigin("A cross-Head HeadSetTag member differs from its selected Head's exact fixed origin.");
                }
                if (!nodes.TryGetValue(entryId.HeadId, out AuditHeadNode? entryOwner)) {
                    return InvalidHistoryOrigin("A HeadSetTag Entry owner is missing from the checked formal head set.");
                }
                resources.Member = _files.OpenHead(entryOwner.Target, readOnly: true, _markOwnedCleanupFault);
                checkState();
                var memberHeader = CheckAuditHeader(resources.Member, entryOwner, checkState);
                if (memberHeader.IsFailure) { return memberHeader.Error!; }
                var member = ReadExactPublication(resources.Member, memberHeader.Value, entryId.Ticket,
                    checkState, deferOwnedCleanup: true);
                if (member.IsFailure) { return member.Error!; }
                if (member.Value.Entry is null) {
                    return new HeadRecordError("A HeadSetTag member is not a published Entry.");
                }
                if (pair.Key == entryId.HeadId) {
                    // Cross-Head members match an Origin already proved by AuditHeadFrames.
                    // A same-Head tag ticket has no such witness: exact CRC/Fence alone can
                    // also describe an offline embedded frame outside the physical chain.
                    AteliaError? membership = CheckAuditTagMemberChain(resources.Member, memberHeader.Value,
                        entryId.Ticket, checkState);
                    if (membership is not null) { return membership; }
                }
                CloseAuditResource(ref resources.Member);
                checkState();
            }
            resources.TagCount = checked(resources.TagCount + 1);
        }
    }

    private static AteliaError? CheckAuditTagMemberChain(IRbfFile file, CheckedHeadHeader header,
        SizedPtr wanted, Action checkState) {
        checkState();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        checkState();
        bool hasHeader = forward.MoveNext();
        checkState();
        if (!hasHeader) {
            return forward.TerminationError ?? new HeadHeaderError("The checked HeadHeader is missing from the physical chain.");
        }
        RbfFrameInfo first = forward.Current;
        checkState();
        if (first.Ticket != header.HeaderTicket || !HeadRecordCodec.IsHeaderShape(first)) {
            return new HeadHeaderError("The first physical frame differs from its checked HeadHeader.");
        }

        // The earlier whole-Head audit checked every publication and normal EOF. This
        // prefix only proves that this exact accepted ticket belongs to that same chain.
        while (true) {
            checkState();
            bool hasEntry = forward.MoveNext();
            checkState();
            if (!hasEntry) {
                return forward.TerminationError
                    ?? InvalidHistoryOrigin("A HeadSetTag member ticket is not in its formal Head's physical chain.");
            }
            RbfFrameInfo info = forward.Current;
            checkState();
            if (info.Ticket == wanted) { return null; }
            if (info.Ticket.Offset >= wanted.Offset) {
                return InvalidHistoryOrigin("A HeadSetTag member ticket is not in its formal Head's physical chain.");
            }
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
        internal IRbfFile? Member;
        internal IEnumerator<QualifiedTagBucketTarget>? Buckets;
        internal long HeadCount;
        internal long EntryCount;
        internal long OriginCount;
        internal long TagBucketCount;
        internal long TagCount;
    }

    private sealed class AuditHeadNode(QualifiedHeadTarget target, CheckedHeadHeader header) {
        internal QualifiedHeadTarget Target { get; } = target;
        internal HeadId HeadId { get; } = header.HeadId;
        internal SizedPtr HeaderTicket { get; } = header.HeaderTicket;
        internal long RecordsOffset { get; } = header.RecordsOffset;
        internal ForkOrigin? Origin { get; } = header.Origin;
        internal HashSet<SizedPtr>? PendingOrigins;
        internal byte State;
    }
}
