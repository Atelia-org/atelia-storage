using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;

namespace Atelia.HeadStore;

public sealed partial class HeadStore {
    // Peers belong to one member check, never to the permanent current-head cache.
    private IRbfFile? _conditionalPeer;

    // AteliaResult deliberately rejects a null success value. This local wrapper
    // represents a checked CU which supplied a valid witness of nonpublication.
    private readonly record struct PublicationRead(HeadEntry? Entry);
    private readonly record struct CheckedPublication(int PayloadLength, int RootOffset, long RecordedAtUnixMs);

    /// <summary>完整检查一条本地主链发布记录，并应用当前值和历史共用的 CU 判据。</summary>
    private AteliaResult<PublicationRead> ReadPublication(IRbfFile file, CheckedHeadHeader header,
        RbfFrameInfo info, Func<AteliaError?>? beforeWork = null, Action? checkState = null,
        bool deferOwnedCleanup = false, HashSet<uint>? dependencies = null) {
        RequireConditionalContext(file, header);
        CheckConditionalState(checkState);
        if (!header.IsAllowedPublication(info)) {
            return new HeadRecordError("The selected physical record is not a legal publication after its checked header.");
        }

        AteliaError? workError = BeforeConditionalWork(beforeWork, checkState);
        if (workError is not null) { return workError; }
        var read = file.ReadPooledFrame(info.Ticket);
        if (read.IsFailure) {
            CheckConditionalState(checkState);
            return read.Error!;
        }
        RbfPooledFrame? frame = read.Value!;
        AteliaResult<PublicationRead> result = default;
        CleanupErrors errors = default;
        try {
            CheckConditionalState(checkState);
            var checkedFrame = CheckPublicationFrame(frame, header, info.Ticket);
            if (checkedFrame.IsFailure) { result = checkedFrame.Error!; }
            else if (frame.Tag != info.Tag || frame.TailMetaLength != info.TailMetaLength
                || checkedFrame.Value.PayloadLength != info.PayloadLength || frame.IsTombstone != info.IsTombstone) {
                result = new HeadRecordError("The complete publication differs from its observed physical metadata.");
            }
            else {
                CheckedPublication publication = checkedFrame.Value;
                var committed = frame.Tag == HeadRecordCodec.SingleEntryTag
                    ? AteliaResult<bool>.Success(true)
                    : CheckConditionalGroup(file, header, frame, publication.PayloadLength, beforeWork, checkState);
                if (committed.IsFailure) { result = committed.Error!; }
                else if (!committed.Value) { result = new PublicationRead(null); }
                else {
                    // Capture dependencies from this same accepted record, while its checked
                    // CU table is still leased. Rejected candidates contribute nothing.
                    if (dependencies is not null) {
                        dependencies.Add(header.HeadId.LocalId);
                        if (frame.Tag == HeadRecordCodec.ConditionalUpdateTag) {
                            ReadOnlySpan<byte> members = frame.PayloadAndMeta[..publication.PayloadLength];
                            int count = ConditionalUpdateCodec.GetMemberCount(members);
                            for (int index = 0; index < count; index++) {
                                ConditionalUpdateCodec.GetMember(members, index, out uint localId, out _);
                                dependencies.Add(localId);
                            }
                        }
                    }
                    byte[] rootBytes = frame.PayloadAndMeta[publication.RootOffset..publication.PayloadLength].ToArray();
                    result = new PublicationRead(new HeadEntry(HeadEntryId.Create(header.HeadId, info.Ticket),
                        publication.RecordedAtUnixMs, rootBytes));
                }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseConditionalFrame(ref frame, ref errors);
            // Audit must close its own unregistered local handles before releasing
            // owned files and the root lock in its outer operation boundary.
            if (_faulted && !deferOwnedCleanup) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        CheckConditionalState(checkState);
        return result;
    }

    /// <summary>只跳过完整合法但未提交的 CU；已观察到的任何必要错误均阻断回退。</summary>
    private AteliaResult<HeadEntry> ReadPublishedCurrent(IRbfFile file, CheckedHeadHeader header,
        HashSet<uint>? dependencies = null) {
        RequireConditionalContext(file, header);
        CheckConditionalState(null);
        var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
        CheckConditionalState(null);
        long fixedTail = file.TailOffset;
        bool first = true;
        while (true) {
            CheckConditionalState(null);
            bool hasRecord = reverse.MoveNext();
            CheckConditionalState(null);
            if (!hasRecord) {
                return reverse.TerminationError ?? new HeadRecordError("A published current Entry is missing.");
            }
            RbfFrameInfo info = reverse.Current;
            if (first && file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != fixedTail) {
                return new HeadRecordError("The current physical record must end at the actual EOF.");
            }
            first = false;
            if (info.Ticket == header.HeaderTicket && HeadRecordCodec.IsHeaderShape(info)) {
                if (header.Origin is not ForkOrigin origin) {
                    return new HeadRecordError("A root Head must contain its initial SingleEntry.");
                }
                var opened = GetOrOpen(origin.SourceHeadId.LocalId);
                if (opened.IsFailure) { return opened.Error!; }
                IRbfFile source = opened.Value!;
                var sourceHeader = HeadRecordReader.CheckHeader(source, _identity, origin.SourceHeadId);
                if (sourceHeader.IsFailure) { return sourceHeader.Error!; }
                var exact = ReadExactPublication(source, sourceHeader.Value, origin.SourceEntryTicket,
                    dependencies: dependencies);
                if (exact.IsFailure) { return exact.Error!; }
                return exact.Value.Entry is { } sourceEntry
                    ? sourceEntry
                    : new HeadRecordError("The exact fork origin is not a published Entry.");
            }
            var publication = ReadPublication(file, header, info, dependencies: dependencies);
            if (publication.IsFailure) { return publication.Error!; }
            if (publication.Value.Entry is { } entry) { return entry; }
        }
    }

    /// <summary>发布前只检查真实本地 tail 的完整资格和 CU self，不读取其他成员或递归旧组。</summary>
    private AteliaResult<bool> CheckLocalCurrent(IRbfFile file, CheckedHeadHeader header) {
        RequireConditionalContext(file, header);
        CheckConditionalState(null);
        var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
        CheckConditionalState(null);
        bool hasRecord = reverse.MoveNext();
        CheckConditionalState(null);
        if (!hasRecord) { return reverse.TerminationError ?? new HeadRecordError("A current publication is missing."); }
        RbfFrameInfo info = reverse.Current;
        if (file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != file.TailOffset) {
            return new HeadRecordError("The current physical record has an invalid local publication shape.");
        }
        if (info.Ticket == header.HeaderTicket && HeadRecordCodec.IsHeaderShape(info)) {
            return header.Origin.HasValue
                ? AteliaResult<bool>.Success(true)
                : new HeadRecordError("A root Head must contain its initial SingleEntry.");
        }
        if (!header.IsAllowedPublication(info)) {
            return new HeadRecordError("The current physical record has an invalid local publication shape.");
        }
        var read = file.ReadPooledFrame(info.Ticket);
        if (read.IsFailure) {
            CheckConditionalState(null);
            return read.Error!;
        }
        RbfPooledFrame? frame = read.Value!;
        AteliaResult<bool> result = default;
        CleanupErrors errors = default;
        try {
            CheckConditionalState(null);
            var checkedFrame = CheckPublicationFrame(frame, header, info.Ticket);
            if (checkedFrame.IsFailure) { result = checkedFrame.Error!; }
            else if (frame.Tag != info.Tag || frame.TailMetaLength != info.TailMetaLength
                || checkedFrame.Value.PayloadLength != info.PayloadLength || frame.IsTombstone != info.IsTombstone) {
                result = new HeadRecordError("The complete current publication differs from its physical metadata.");
            }
            else { result = true; }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseConditionalFrame(ref frame, ref errors);
            if (_faulted) { CleanupOwned(ref errors); }
        }
        errors.ThrowIfAny();
        CheckConditionalState(null);
        return result;
    }

    private static AteliaResult<CheckedPublication> CheckPublicationFrame(RbfPooledFrame frame,
        CheckedHeadHeader header, SizedPtr wanted) {
        int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
        if (!header.IsAllowedPublicationKind(wanted.Offset, frame.Tag)
            || frame.Ticket != wanted || frame.IsTombstone
            || frame.TailMetaLength != RecordMetadataCodec.EncodedSize
            || payloadLength < 1
            || !RecordMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], out long recordedAtUnixMs)) {
            return new HeadRecordError("The complete publication has an invalid envelope or metadata.");
        }
        var measured = RbfFile.MeasureWriteSize(payloadLength, frame.TailMetaLength);
        if (measured.IsFailure || measured.Value.FrameLength != wanted.Length) {
            return new HeadRecordError("The complete publication has an invalid frame size.");
        }
        int rootOffset;
        if (frame.Tag == HeadRecordCodec.SingleEntryTag) {
            if (payloadLength > MaxHeadEntryPayloadLength) {
                return new HeadRecordError("The complete Entry exceeds the root payload bound.");
            }
            rootOffset = 0;
        }
        else if (frame.Tag == HeadRecordCodec.ConditionalUpdateTag) {
            if (!ConditionalUpdateCodec.TryRead(frame.PayloadAndMeta[..payloadLength], header.HeadId.LocalId,
                    wanted, out rootOffset)) {
                return new HeadRecordError("The complete ConditionalUpdate has an invalid envelope, member table, root payload, or self ticket.");
            }
        }
        else { return new HeadRecordError("The complete publication has an unknown or misplaced kind."); }
        return new CheckedPublication(payloadLength, rootOffset, recordedAtUnixMs);
    }

    private AteliaResult<bool> CheckConditionalGroup(IRbfFile localFile, CheckedHeadHeader localHeader,
        RbfPooledFrame localFrame, int payloadLength, Func<AteliaError?>? beforeWork, Action? checkState) {
        ReadOnlySpan<byte> membersPayload = localFrame.PayloadAndMeta[..payloadLength];
        int count = ConditionalUpdateCodec.GetMemberCount(membersPayload);
        for (int index = 0; index < count; index++) {
            CheckConditionalState(checkState);
            ConditionalUpdateCodec.GetMember(membersPayload, index, out uint localId, out SizedPtr wanted);
            // The local complete read already established codec, self and this exact table.
            if (localId == localHeader.HeadId.LocalId) { continue; }
            var matched = CheckConditionalMember(localFile, localHeader, localId, wanted,
                membersPayload, beforeWork, checkState);
            if (matched.IsFailure) { return matched.Error!; }
            if (!matched.Value) { return false; }
        }
        return true;
    }

    private AteliaResult<bool> CheckConditionalMember(IRbfFile localFile, CheckedHeadHeader localHeader,
        uint localId, SizedPtr wanted, ReadOnlySpan<byte> membersPayload,
        Func<AteliaError?>? beforeWork, Action? checkState) {
        AteliaResult<bool> result = default;
        CleanupErrors errors = default;
        try {
            AteliaError? workError = BeforeConditionalWork(beforeWork, checkState);
            if (workError is not null) { result = workError; }
            else {
                var opened = GetConditionalPeer(localFile, localHeader, localId, checkState);
                if (opened.IsFailure) { result = opened.Error!; }
                else {
                    IRbfFile peer = opened.Value!;
                    var header = HeadRecordReader.CheckHeader(peer, _identity, HeadId.Create(_identity, localId),
                        () => BeforeConditionalWork(beforeWork, checkState), () => CheckConditionalState(checkState));
                    if (header.IsFailure) { result = header.Error!; }
                    else { result = ReadDesignatedConditionalMember(peer, header.Value, wanted, membersPayload, beforeWork, checkState); }
                }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally {
            CloseConditionalPeer(ref errors);
            // ReadPublication still owns its local pooled frame. Its outer finally
            // returns that lease before tearing down owned files and the root lock.
        }
        errors.ThrowIfAny();
        CheckConditionalState(checkState);
        return result;
    }

    private AteliaResult<IRbfFile> GetConditionalPeer(IRbfFile localFile, CheckedHeadHeader localHeader,
        uint localId, Action? checkState) {
        CheckConditionalState(checkState);
        if (localId == localHeader.HeadId.LocalId) { return AteliaResult<IRbfFile>.Success(localFile); }
        if (_ownedHeads.TryGetValue(localId, out OwnedHead? owned)) { return AteliaResult<IRbfFile>.Success(owned.File!); }
        if (_historyHeadId == localId && _historyHead is { } historyFile) { return AteliaResult<IRbfFile>.Success(historyFile); }
        if (_conditionalPeer is not null) { throw new InvalidOperationException("The previous conditional peer was not released."); }
        var resolved = _files.ResolveHead(localId, _markOwnedCleanupFault);
        CheckConditionalState(checkState);
        if (resolved.IsFailure) { return resolved.Error!; }
        try { _conditionalPeer = _files.OpenHead(resolved.Value, IsReadOnly, _markOwnedCleanupFault); }
        catch {
            // Writable opening may have started recovery, while read-only query errors alone do not fault.
            if (!IsReadOnly) { _faulted = true; }
            throw;
        }
        CheckConditionalState(checkState);
        return AteliaResult<IRbfFile>.Success(_conditionalPeer!);
    }

    private AteliaResult<bool> ReadDesignatedConditionalMember(IRbfFile peer, CheckedHeadHeader header,
        SizedPtr wanted, ReadOnlySpan<byte> membersPayload, Func<AteliaError?>? beforeWork, Action? checkState) {
        CheckConditionalState(checkState);
        // The public scan entry provides the RBF Idle/disposed/fault gate even for exact EOF.
        var reverse = peer.ScanReverse(showTombstone: true).GetEnumerator();
        CheckConditionalState(checkState);
        long tail = peer.TailOffset;
        if (wanted.Offset == tail) { return false; }
        if (wanted.Offset > tail || wanted.Offset < header.RecordsOffset) {
            return new HeadRecordError("The designated member offset is outside its checked head's completed range.");
        }

        AteliaError? workError = BeforeConditionalWork(beforeWork, checkState);
        if (workError is not null) { return workError; }
        var exact = peer.ReadPooledFrame(wanted);
        if (exact.IsSuccess) {
            RbfPooledFrame? frame = exact.Value!;
            AteliaResult<bool> result = default;
            CleanupErrors errors = default;
            try {
                CheckConditionalState(checkState);
                var publication = CheckPublicationFrame(frame, header, wanted);
                if (publication.IsFailure) { result = publication.Error!; }
                else if (frame.Tag == HeadRecordCodec.SingleEntryTag) { result = false; }
                else {
                    result = ConditionalUpdateCodec.MembersEqual(frame.PayloadAndMeta[..publication.Value.PayloadLength], membersPayload);
                }
            }
            catch (Exception error) { errors.Add(error); }
            finally {
                CloseConditionalFrame(ref frame, ref errors);
            }
            errors.ThrowIfAny();
            CheckConditionalState(checkState);
            return result;
        }
        CheckConditionalState(checkState);
        AteliaError exactError = exact.Error!;
        if (exactError.ErrorCode is not ("Rbf.ArgumentError" or "Rbf.FramingError")) { return exactError; }

        while (true) {
            workError = BeforeConditionalWork(beforeWork, checkState);
            if (workError is not null) { return workError; }
            bool hasActual = reverse.MoveNext();
            CheckConditionalState(checkState);
            if (!hasActual) {
                return reverse.TerminationError ?? new HeadRecordError("The designated member offset is not a real physical frame start.");
            }
            RbfFrameInfo actual = reverse.Current;
            AteliaError? observationError = CheckConditionalObservation(actual, header);
            if (observationError is not null) { return observationError; }
            if (actual.Ticket.Offset > wanted.Offset) { continue; }
            if (actual.Ticket.Offset < wanted.Offset) {
                return new HeadRecordError("The designated member offset is not a real physical frame start.");
            }
            if (actual.Ticket == wanted) { return exactError; }

            workError = BeforeConditionalWork(beforeWork, checkState);
            if (workError is not null) { return workError; }
            bool hasPredecessor = reverse.MoveNext();
            CheckConditionalState(checkState);
            if (!hasPredecessor) {
                return reverse.TerminationError ?? new HeadRecordError("The replaced member has no real physical predecessor.");
            }
            RbfFrameInfo predecessor = reverse.Current;
            observationError = CheckConditionalObservation(predecessor, header);
            if (observationError is not null) { return observationError; }
            if (peer.GetPhysicalOffsetImmediatelyAfter(predecessor.Ticket) != wanted.Offset) {
                return new HeadRecordError("The replaced member does not immediately follow its real predecessor.");
            }
            workError = BeforeConditionalWork(beforeWork, checkState);
            if (workError is not null) { return workError; }
            var successor = peer.ReadFrameInfoImmediatelyAfter(predecessor.Ticket);
            CheckConditionalState(checkState);
            if (successor.IsFailure) { return successor.Error!; }
            if (!successor.Value.HasValue || successor.Value.Value != actual) {
                return new HeadRecordError("The actual member slot differs from its structurally checked direct successor.");
            }
            // Different length plus genuine chain structure disproves this ticket.
            // The replacement payload, meta schema and alternative group are deliberately unvisited.
            return false;
        }
    }

    private static AteliaError? CheckConditionalObservation(RbfFrameInfo info, CheckedHeadHeader header) {
        if (info.Ticket == header.HeaderTicket && HeadRecordCodec.IsHeaderShape(info)) { return null; }
        if (info.IsTombstone || !header.IsAllowedPublicationKind(info.Ticket.Offset, info.Tag)) {
            return new HeadRecordError("The observed physical member chain has an unknown or misplaced kind or tombstone.");
        }
        return null;
    }

    private static void RequireConditionalContext(IRbfFile file, CheckedHeadHeader header) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked header must belong to this RBF handle.", nameof(header)); }
    }

    private void CheckConditionalState(Action? checkState) {
        EnsureOwnerUsable();
        checkState?.Invoke();
        EnsureOwnerUsable();
    }

    private AteliaError? BeforeConditionalWork(Func<AteliaError?>? beforeWork, Action? checkState) {
        CheckConditionalState(checkState);
        AteliaError? error = beforeWork?.Invoke();
        CheckConditionalState(checkState);
        return error;
    }

    private void CloseConditionalPeer(ref CleanupErrors errors) {
        IRbfFile? toClose = _conditionalPeer;
        _conditionalPeer = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }

    private void CloseConditionalFrame(ref RbfPooledFrame? frame, ref CleanupErrors errors) {
        RbfPooledFrame? toClose = frame;
        frame = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { _faulted = true; errors.Add(error); }
    }
}
