using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.VersionStore.Internal.Tags;

/// <summary>区分小元信息资格与完整 root envelope，不解释 opaque payload。</summary>
internal static class TagRecordCodec {
    internal const uint HeaderTag = 0;
    internal const uint RecordTag = 1;
    private const int MaxPayloadAndMetadataLength = VersionStore.MaxRootPayloadLength + TagMetadataCodec.MaxEncodedSize;

    internal static bool IsHeaderShape(RbfFrameInfo info) {
        if (info.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive || info.Tag != HeaderTag
            || info.IsTombstone || info.TailMetaLength != 0 || info.PayloadLength != TagBucketHeaderCodec.PayloadSize) {
            return false;
        }
        var size = RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0);
        return size.IsSuccess && info.Ticket.Length == size.Value.FrameLength;
    }

    // 必须先取得身份元信息；M=0/超界也不改变一个合法名字的占名资格。
    internal static bool IsMetadataShape(RbfFrameInfo info) =>
        info.Tag == RecordTag && !info.IsTombstone
        && info.TailMetaLength is >= TagMetadataCodec.MinEncodedSize and <= TagMetadataCodec.MaxEncodedSize;

    internal static bool IsRootShape(RbfFrameInfo info) {
        if (!IsMetadataShape(info) || info.PayloadLength is < 1 or > VersionStore.MaxRootPayloadLength) {
            return false;
        }
        var size = RbfFile.MeasureWriteSize(info.PayloadLength, info.TailMetaLength);
        return size.IsSuccess && info.Ticket.Length == size.Value.FrameLength;
    }

    /// <summary>在物化最终 meta、barrier 与输出前度量完整帧并检查真实追加起点。</summary>
    internal static AteliaResult<RbfWriteSize> MeasureAppend(int payloadLength, int tailMetaLength, long appendOffset) {
        if (payloadLength is < 1 or > VersionStore.MaxRootPayloadLength
            || tailMetaLength is < TagMetadataCodec.MinEncodedSize or > TagMetadataCodec.MaxEncodedSize) {
            return new TagCapacityError("The root payload or tag metadata length is outside the format capacity.");
        }
        long totalLength = checked((long)payloadLength + tailMetaLength);
        if (totalLength > MaxPayloadAndMetadataLength || totalLength > RbfFile.MaxPayloadAndMetaLength) {
            return new TagCapacityError("The complete tag record exceeds the payload and metadata capacity.");
        }
        var size = RbfFile.MeasureWriteSize(payloadLength, tailMetaLength);
        if (size.IsFailure) { return size.Error!; }
        // SizedPtr bounds its start and frame length, not the legal last frame's end/Fence.
        if (!SizedPtr.TryCreate(appendOffset, size.Value.FrameLength, out _)) {
            return new TagCapacityError("The real bucket tail cannot represent another complete TagRecord ticket.");
        }
        return size;
    }

    private sealed record TagCapacityError(string Detail) : AteliaError("VersionStore.TagCapacityExceeded", Detail);
}
