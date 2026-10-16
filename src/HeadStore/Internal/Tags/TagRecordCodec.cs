using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Tags;

/// <summary>区分小元信息资格与完整成员表包络；容量独立于 HeadEntry payload 上限。</summary>
internal static class TagRecordCodec {
    internal const uint HeaderTag = 0;
    internal const uint RecordTag = 1;

    internal static bool IsHeaderShape(RbfFrameInfo info) {
        if (info.Ticket.Offset != RbfScanBoundary.Empty.EndExclusive || info.Tag != HeaderTag
            || info.IsTombstone || info.TailMetaLength != 0 || info.PayloadLength != TagBucketHeaderCodec.PayloadSize) {
            return false;
        }
        var size = RbfFile.MeasureWriteSize(TagBucketHeaderCodec.PayloadSize, 0);
        return size.IsSuccess && info.Ticket.Length == size.Value.FrameLength;
    }

    // 成员表长度或内容损坏不改变独立元信息中合法名字的占名资格。
    internal static bool IsMetadataShape(RbfFrameInfo info) =>
        info.Tag == RecordTag && !info.IsTombstone
        && info.TailMetaLength is >= TagMetadataCodec.MinEncodedSize and <= TagMetadataCodec.MaxEncodedSize;

    internal static bool IsEntriesShape(RbfFrameInfo info) {
        if (!IsMetadataShape(info) || info.PayloadLength < TagEntriesCodec.MemberEncodedSize
            || info.PayloadLength % TagEntriesCodec.MemberEncodedSize != 0) {
            return false;
        }
        var size = RbfFile.MeasureWriteSize(info.PayloadLength, info.TailMetaLength);
        return size.IsSuccess && info.Ticket.Length == size.Value.FrameLength;
    }

    internal static bool IsValidEntryCount(uint entryCount, int tailMetaLength) {
        if (entryCount == 0 || tailMetaLength is < TagMetadataCodec.MinEncodedSize or > TagMetadataCodec.MaxEncodedSize) {
            return false;
        }
        long payloadLength = checked((long)TagEntriesCodec.MemberEncodedSize * entryCount);
        if (payloadLength + tailMetaLength > RbfFile.MaxPayloadAndMetaLength) { return false; }
        return RbfFile.MeasureWriteSize(checked((int)payloadLength), tailMetaLength).IsSuccess;
    }

    /// <summary>仅按真实成员数量和小元信息尺寸预检自然容量；失败前不分配成员表。</summary>
    internal static AteliaResult<int> MeasurePayloadLength(int entryCount, int tailMetaLength) {
        if (entryCount <= 0 || !IsValidEntryCount((uint)entryCount, tailMetaLength)) {
            return new TagCapacityError("The member count and tag metadata exceed the complete record capacity.");
        }
        return checked(TagEntriesCodec.MemberEncodedSize * entryCount);
    }

    /// <summary>在物化最终 meta、barrier 与输出前度量完整帧并检查真实追加起点。</summary>
    internal static AteliaResult<RbfWriteSize> MeasureAppend(int payloadLength, int tailMetaLength, long appendOffset) {
        if (payloadLength < TagEntriesCodec.MemberEncodedSize || payloadLength % TagEntriesCodec.MemberEncodedSize != 0
            || tailMetaLength is < TagMetadataCodec.MinEncodedSize or > TagMetadataCodec.MaxEncodedSize) {
            return new TagCapacityError("The member table or tag metadata length is outside the format capacity.");
        }
        long totalLength = checked((long)payloadLength + tailMetaLength);
        if (totalLength > RbfFile.MaxPayloadAndMetaLength) {
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

    private sealed record TagCapacityError(string Detail) : AteliaError("HeadStore.TagCapacityExceeded", Detail);
}
