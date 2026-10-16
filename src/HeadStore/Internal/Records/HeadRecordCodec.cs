using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;

namespace Atelia.HeadStore.Internal.Records;

/// <summary>使用实际公共 RBF 元信息判别本版 head 帧，不解释 HeadEntry payload bytes 或共同发布资格。</summary>
internal static class HeadRecordCodec {
    internal const uint HeaderTag = 0;
    internal const uint SingleEntryTag = 1;
    internal const uint ConditionalUpdateTag = 2;

    internal static bool IsHeaderShape(RbfFrameInfo info) {
        if (info.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive
            || info.Tag != HeaderTag || info.IsTombstone || info.TailMetaLength != 0
            || info.PayloadLength is not (HeadHeaderCodec.NoOriginPayloadSize or HeadHeaderCodec.WithOriginPayloadSize)) {
            return false;
        }

        var measured = RbfFile.MeasureWriteSize(info.PayloadLength, 0);
        return measured.IsSuccess && info.Ticket.Length == measured.Value.FrameLength;
    }

    internal static bool IsSingleEntryShape(RbfFrameInfo info) {
        if (info.Tag != SingleEntryTag || info.IsTombstone
            || info.TailMetaLength != RecordMetadataCodec.EncodedSize
            || info.PayloadLength < 1 || info.PayloadLength > HeadStore.MaxHeadEntryPayloadLength) {
            return false;
        }

        var measured = RbfFile.MeasureWriteSize(info.PayloadLength, RecordMetadataCodec.EncodedSize);
        return measured.IsSuccess && info.Ticket.Length == measured.Value.FrameLength;
    }

    internal static bool IsPublicationShape(RbfFrameInfo info) {
        if (info.Tag == SingleEntryTag) { return IsSingleEntryShape(info); }
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
