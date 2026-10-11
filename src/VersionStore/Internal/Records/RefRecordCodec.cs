using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Records;

/// <summary>使用实际公共 RBF 元信息判别本版 ref 帧，不解释 root bytes 或共同发布资格。</summary>
internal static class RefRecordCodec {
    internal const uint HeaderTag = 0;
    internal const uint SnapshotTag = 1;
    internal const uint ConditionalUpdateTag = 2;

    internal static bool IsHeaderShape(RbfFrameInfo info) {
        if (info.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive
            || info.Tag != HeaderTag || info.IsTombstone || info.TailMetaLength != 0
            || info.PayloadLength is not (RefHeaderCodec.NoOriginPayloadSize or RefHeaderCodec.WithOriginPayloadSize)) {
            return false;
        }

        var measured = RbfFile.MeasureWriteSize(info.PayloadLength, 0);
        return measured.IsSuccess && info.Ticket.Length == measured.Value.FrameLength;
    }

    internal static bool IsSnapshotShape(RbfFrameInfo info) {
        if (info.Tag != SnapshotTag || info.IsTombstone
            || info.TailMetaLength != RecordMetadataCodec.EncodedSize
            || info.PayloadLength < 1 || info.PayloadLength > VersionStore.MaxRootPayloadLength) {
            return false;
        }

        var measured = RbfFile.MeasureWriteSize(info.PayloadLength, RecordMetadataCodec.EncodedSize);
        return measured.IsSuccess && info.Ticket.Length == measured.Value.FrameLength;
    }

    internal static bool IsPublicationShape(RbfFrameInfo info) {
        if (info.Tag == SnapshotTag) { return IsSnapshotShape(info); }
        if (info.Tag != ConditionalUpdateTag || info.IsTombstone
            || info.TailMetaLength != RecordMetadataCodec.EncodedSize
            || info.PayloadLength < ConditionalUpdateCodec.MinimumPayloadLength
            || info.PayloadLength > ConditionalUpdateCodec.MaxPayloadLength) {
            return false;
        }

        var measured = RbfFile.MeasureWriteSize(info.PayloadLength, RecordMetadataCodec.EncodedSize);
        return measured.IsSuccess && info.Ticket.Length == measured.Value.FrameLength;
    }
}
