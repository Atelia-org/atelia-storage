using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Records;

/// <summary>在已准入、Idle 的公共 RBF 句柄上取得必要 header/head 资格。</summary>
/// <remarks>不打开、恢复、关闭文件，不访问初始或来源内容；调用方负责串行及句柄生命周期。</remarks>
internal static class RefRecordReader {
    internal static AteliaResult<CheckedRefHeader> CheckHeader(
        IRbfFile file, StoreIdentity expectedIdentity, RefId expectedRefId
    ) {
        ArgumentNullException.ThrowIfNull(file);
        if (expectedIdentity == default || !expectedRefId.BelongsTo(expectedIdentity)) {
            throw new ArgumentException("The expected RefId must belong to the checked store identity.", nameof(expectedRefId));
        }
        if (file.Format != RbfFormat.Rbf3) { return new RefHeaderError("Ref files must use RBF3."); }

        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        if (!forward.MoveNext()) {
            return forward.TerminationError ?? new RefHeaderError("The first physical RefHeader is missing.");
        }

        RbfFrameInfo info = forward.Current;
        if (!RefRecordCodec.IsHeaderShape(info)) { return new RefHeaderError("The first physical frame has an invalid RefHeader shape."); }

        // Exact shape has bounded the complete frame before allocating its stack buffer.
        Span<byte> buffer = stackalloc byte[info.Ticket.Length];
        var read = info.ReadFrame(buffer);
        if (read.IsFailure) { return read.Error; }
        if (!RefHeaderCodec.TryRead(read.Value.PayloadAndMeta, expectedIdentity, expectedRefId, out RefHeader header)) {
            return new RefHeaderError("The header version, identity, RefId, or origin does not match the admitted context.");
        }

        long initialOffset = file.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
        var size = RbfFile.MeasureWriteSize(info.PayloadLength, 0).Value;
        if (initialOffset != RbfScanBoundary.Empty.EndExclusive + size.AppendLength) {
            return new RefHeaderError("The checked RefHeader has an invalid physical successor boundary.");
        }
        return new CheckedRefHeader(file, header, info.Ticket, initialOffset);
    }

    internal static AteliaResult<RefBytesSnapshot> ReadCurrent(
        IRbfFile file, StoreIdentity expectedIdentity, RefId expectedRefId
    ) {
        var header = CheckHeader(file, expectedIdentity, expectedRefId);
        if (header.IsFailure) { return header.Error; }
        return ReadCurrent(file, header.Value);
    }

    internal static AteliaResult<RefBytesSnapshot> ReadCurrent(IRbfFile file, CheckedRefHeader header) {
        ArgumentNullException.ThrowIfNull(file);
        if (!header.IsFor(file)) { throw new ArgumentException("The checked header must belong to this RBF handle.", nameof(header)); }

        var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
        if (!reverse.MoveNext()) {
            return reverse.TerminationError ?? new RefRecordError("A current Snapshot is missing.");
        }

        RbfFrameInfo info = reverse.Current;
        if (info.Ticket.Offset < header.InitialSnapshotOffset
            || file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != file.TailOffset) {
            return new RefRecordError("The current record must follow the checked header and end at the actual EOF.");
        }
        if (!RefRecordCodec.IsSnapshotShape(info)) { return new RefRecordError("The current physical record has an invalid Snapshot shape."); }

        // Shape/capacity checks above precede the complete pooled read.
        var read = info.ReadPooledFrame();
        if (read.IsFailure) { return read.Error; }
        byte[] ownedBytes;
        long recordedAtUnixMs;
        using (RbfPooledFrame frame = read.Value!) {
            int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
            if (frame.Tag != RefRecordCodec.SnapshotTag || frame.IsTombstone
                || frame.TailMetaLength != RecordMetadataCodec.EncodedSize
                || payloadLength != info.PayloadLength
                || !RecordMetadataCodec.TryRead(frame.PayloadAndMeta[payloadLength..], out recordedAtUnixMs)) {
                return new RefRecordError("The completely checked Snapshot has an invalid envelope or metadata.");
            }

            ownedBytes = frame.PayloadAndMeta[..payloadLength].ToArray();
        }
        // Only independent values survive the pooled frame's disposal.
        return new RefBytesSnapshot(RefRevision.Create(header.RefId, info.Ticket), recordedAtUnixMs, ownedBytes);
    }

    /// <summary>独立检查首帧与直接第二帧，不把初始化内容检查塞入日常当前值读取。</summary>
    internal static AteliaResult<SizedPtr> CheckInitialization(
        IRbfFile file, StoreIdentity expectedIdentity, RefId expectedRefId
    ) {
        var header = CheckHeader(file, expectedIdentity, expectedRefId);
        if (header.IsFailure) { return header.Error; }
        var next = file.ReadFrameInfoImmediatelyAfter(header.Value.HeaderTicket);
        if (next.IsFailure) { return next.Error; }
        if (!next.Value.HasValue) { return new RefRecordError("The initial Snapshot is missing."); }

        RbfFrameInfo info = next.Value.Value;
        if (info.Ticket.Offset != header.Value.InitialSnapshotOffset || !RefRecordCodec.IsSnapshotShape(info)) {
            return new RefRecordError("The direct second physical frame has an invalid initial Snapshot shape.");
        }
        var read = info.ReadPooledFrame();
        if (read.IsFailure) { return read.Error; }
        using RbfPooledFrame frame = read.Value!;
        return info.Ticket;
    }
}

internal sealed record RefHeaderError(string Detail) : AteliaError("VersionStore.InvalidRefHeader", Detail);
internal sealed record RefRecordError(string Detail) : AteliaError("VersionStore.InvalidRefRecord", Detail);
