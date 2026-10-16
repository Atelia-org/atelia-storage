using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Records;

/// <summary>在已准入、Idle 的公共 RBF 句柄上取得必要 head 记录资格。</summary>
/// <remarks>不打开、恢复、关闭文件；只验证调用方选中的必要记录，不额外遍历。调用方负责串行及句柄生命周期。</remarks>
internal static class HeadRecordReader {
    internal static AteliaResult<CheckedHeadHeader> CheckHeader(
        IRbfFile file, StoreIdentity expectedIdentity, HeadId expectedHeadId,
        Func<AteliaError?>? beforeWork = null, Action? checkState = null
    ) {
        ArgumentNullException.ThrowIfNull(file);
        if (expectedIdentity == default || !expectedHeadId.BelongsTo(expectedIdentity)) {
            throw new ArgumentException("The expected HeadId must belong to the checked store identity.", nameof(expectedHeadId));
        }
        if (file.Format != RbfFormat.Rbf3) { return new HeadHeaderError("Head files must use RBF3."); }

        checkState?.Invoke();
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        AteliaError? workError = beforeWork?.Invoke();
        if (workError is not null) { return workError; }
        bool hasHeader = forward.MoveNext();
        checkState?.Invoke();
        if (!hasHeader) {
            return forward.TerminationError ?? new HeadHeaderError("The first physical HeadHeader is missing.");
        }

        RbfFrameInfo info = forward.Current;
        if (!HeadRecordCodec.IsHeaderShape(info)) { return new HeadHeaderError("The first physical frame has an invalid HeadHeader shape."); }

        // Exact shape has bounded the complete frame before allocating its stack buffer.
        workError = beforeWork?.Invoke();
        if (workError is not null) { return workError; }
        Span<byte> buffer = stackalloc byte[info.Ticket.Length];
        var read = info.ReadFrame(buffer);
        checkState?.Invoke();
        if (read.IsFailure) { return read.Error; }
        if (!HeadHeaderCodec.TryRead(read.Value.PayloadAndMeta, expectedIdentity, expectedHeadId, out HeadHeader header)) {
            return new HeadHeaderError("The header version, identity, HeadId, or origin does not match the admitted context.");
        }

        long initialOffset = file.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
        var size = RbfFile.MeasureWriteSize(info.PayloadLength, 0).Value;
        if (initialOffset != RbfScanBoundary.Empty.EndExclusive + size.AppendLength) {
            return new HeadHeaderError("The checked HeadHeader has an invalid physical successor boundary.");
        }
        return new CheckedHeadHeader(file, header, info.Ticket, initialOffset);
    }

    internal static AteliaResult<HeadEntry> ReadCurrent(
        IRbfFile file, StoreIdentity expectedIdentity, HeadId expectedHeadId
    ) {
        var header = CheckHeader(file, expectedIdentity, expectedHeadId);
        if (header.IsFailure) { return header.Error; }
        return ReadCurrent(file, header.Value);
    }

    internal static AteliaResult<HeadEntry> ReadCurrent(IRbfFile file, CheckedHeadHeader header) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked header must belong to this RBF handle.", nameof(header)); }

        var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
        if (!reverse.MoveNext()) {
            return reverse.TerminationError ?? new HeadRecordError("A current Entry is missing.");
        }

        RbfFrameInfo info = reverse.Current;
        if (info.Ticket.Offset < header.InitialEntryOffset
            || file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != file.TailOffset) {
            return new HeadRecordError("The current record must follow the checked header and end at the actual EOF.");
        }
        if (!HeadRecordCodec.IsSingleEntryShape(info)) { return new HeadRecordError("The current physical record has an invalid Entry shape."); }

        return ReadSingleEntry(info, header.HeadId);
    }

    /// <summary>复制一条完整检查的普通 SingleEntry；调用方另行取得成员与历史位置资格。</summary>
    internal static AteliaResult<HeadEntry> ReadSingleEntry(RbfFrameInfo info, HeadId headId) {
        if (!HeadRecordCodec.IsSingleEntryShape(info)) { return new HeadRecordError("The physical record has an invalid Entry shape."); }

        // Shape/capacity checks above precede the complete pooled read.
        var read = info.ReadPooledFrame();
        if (read.IsFailure) { return read.Error; }
        byte[] ownedBytes;
        long recordedAtUnixMs;
        using (RbfPooledFrame frame = read.Value!) {
            int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
            if (frame.Tag != HeadRecordCodec.SingleEntryTag || frame.IsTombstone
                || frame.TailMetaLength != RecordMetadataCodec.EncodedSize
                || payloadLength != info.PayloadLength
                || !RecordMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], out recordedAtUnixMs)) {
                return new HeadRecordError("The completely checked Entry has an invalid envelope or metadata.");
            }

            ownedBytes = frame.PayloadAndMeta[..payloadLength].ToArray();
        }
        // Only independent values survive the pooled frame's disposal.
        return new HeadEntry(HeadEntryId.Create(headId, info.Ticket), recordedAtUnixMs, ownedBytes);
    }

    /// <summary>完整检查实际主链 Entry 并移交 pooled 帧；调用方负责单次归还，不复制 opaque bytes。</summary>
    internal static AteliaResult<RbfPooledFrame> ReadCheckedSingleEntryFrame(RbfFrameInfo info, Action? checkState = null) {
        checkState?.Invoke();
        if (!HeadRecordCodec.IsSingleEntryShape(info)) { return new HeadRecordError("The physical record has an invalid Entry shape."); }
        var read = info.ReadPooledFrame();
        if (read.IsFailure) {
            checkState?.Invoke();
            return read.Error;
        }
        RbfPooledFrame? pending = read.Value!;
        try {
            checkState?.Invoke();
            int payloadLength = pending.PayloadAndMeta.Length - pending.TailMetaLength;
            if (pending.Ticket != info.Ticket || pending.Tag != HeadRecordCodec.SingleEntryTag || pending.IsTombstone
                || pending.TailMetaLength != RecordMetadataCodec.EncodedSize || payloadLength != info.PayloadLength
                || !RecordMetadataCodec.TryRead(pending.PayloadAndMeta[payloadLength..], out _)) {
                return new HeadRecordError("The completely checked Entry has an invalid envelope or metadata.");
            }
            RbfPooledFrame output = pending;
            pending = null;
            return output;
        }
        finally { pending?.Dispose(); }
    }

    /// <summary>为已签发 entryId 捕获 exact Entry bytes，并重验指定 anchor 的尾 Fence。</summary>
    /// <remarks>只检查本次源 header 后的指定记录，不读取初始内容或扫描源后缀。</remarks>
    internal static AteliaResult<byte[]> CaptureExactSingleEntry(IRbfFile file, CheckedHeadHeader header, SizedPtr ticket) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked header must belong to this RBF handle.", nameof(header)); }
        if (ticket.Offset < header.InitialEntryOffset
            || file.GetPhysicalOffsetImmediatelyAfter(ticket) > file.TailOffset) {
            return new HeadRecordError("The exact source Entry lies outside the checked header's completed range.");
        }

        // Bound kind/meta/payload dimensions before the complete pooled ticket read.
        var readInfo = file.ReadFrameInfo(ticket);
        if (readInfo.IsFailure) { return readInfo.Error!; }
        RbfFrameInfo info = readInfo.Value;
        if (!HeadRecordCodec.IsSingleEntryShape(info)) {
            return new HeadRecordError("The exact source physical record has an invalid Entry shape.");
        }
        var read = file.ReadPooledFrame(ticket);
        if (read.IsFailure) { return read.Error!; }
        byte[] ownedBytes;
        using (RbfPooledFrame frame = read.Value!) {
            int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
            if (frame.Tag != HeadRecordCodec.SingleEntryTag || frame.IsTombstone
                || frame.TailMetaLength != RecordMetadataCodec.EncodedSize
                || payloadLength != info.PayloadLength
                || !RecordMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], out _)) {
                return new HeadRecordError("The completely checked source Entry has an invalid envelope or metadata.");
            }
            ownedBytes = frame.PayloadAndMeta[..payloadLength].ToArray();
        }

        // Complete CRC alone does not cover the Fence outside the ticket. This helper
        // rereads the full anchor and its Fence without parsing any subsequent frame.
        var boundary = file.GetScanBoundaryAfter(ticket);
        if (boundary.IsFailure) { return boundary.Error!; }
        return ownedBytes;
    }

    /// <summary>独立检查首帧与直接第二帧，不把初始化内容检查塞入日常当前值读取。</summary>
    internal static AteliaResult<SizedPtr> CheckInitialization(
        IRbfFile file, StoreIdentity expectedIdentity, HeadId expectedHeadId
    ) {
        var header = CheckHeader(file, expectedIdentity, expectedHeadId);
        if (header.IsFailure) { return header.Error; }
        var next = file.ReadFrameInfoImmediatelyAfter(header.Value.HeaderTicket);
        if (next.IsFailure) { return next.Error; }
        if (!next.Value.HasValue) { return new HeadRecordError("The initial Entry is missing."); }

        RbfFrameInfo info = next.Value.Value;
        if (info.Ticket.Offset != header.Value.InitialEntryOffset || !HeadRecordCodec.IsSingleEntryShape(info)) {
            return new HeadRecordError("The direct second physical frame has an invalid initial Entry shape.");
        }
        var read = info.ReadPooledFrame();
        if (read.IsFailure) { return read.Error; }
        using RbfPooledFrame frame = read.Value!;
        return info.Ticket;
    }
}

internal sealed record HeadHeaderError(string Detail) : AteliaError("HeadStore.InvalidHeadHeader", Detail);
internal sealed record HeadRecordError(string Detail) : AteliaError("HeadStore.InvalidHeadRecord", Detail);
