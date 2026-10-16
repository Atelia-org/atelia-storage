using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Format;

// 来源只声明数值坐标，不能独立签发 HeadEntryId 或证明源成员/内容健康。
internal readonly record struct ForkOrigin(HeadId SourceHeadId, SizedPtr SourceEntryTicket);
internal readonly record struct HeadHeader(HeadId HeadId, ForkOrigin? Origin);

/// <summary>完整 RBF CRC 通过后解释的 24/36B header payload；本 codec 不签发发布资格。</summary>
internal static class HeadHeaderCodec {
    internal const int NoOriginPayloadSize = 24;
    internal const int WithOriginPayloadSize = 36;

    private static readonly long s_minimumEntryOffset = RbfScanBoundary.Empty.EndExclusive +
        RbfFile.MeasureWriteSize(NoOriginPayloadSize, 0).Value.AppendLength;
    private static readonly int s_minimumEntryLength =
        RbfFile.MeasureWriteSize(1, RecordMetadataCodec.EncodedSize).Value.FrameLength;

    internal static bool TryRead(
        ReadOnlySpan<byte> payload, StoreIdentity expectedIdentity, HeadId expectedHeadId, out HeadHeader header) {
        header = default;
        if (payload.Length is not (NoOriginPayloadSize or WithOriginPayloadSize) ||
            !expectedHeadId.BelongsTo(expectedIdentity) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload) != FormatGateCodec.CurrentVersion ||
            !StoreIdentity.TryRead(payload.Slice(4, StoreIdentity.EncodedSize), out StoreIdentity actualIdentity) ||
            actualIdentity != expectedIdentity ||
            !HeadIdCodec.TryRead(payload.Slice(20, HeadIdCodec.EncodedSize), expectedIdentity, out HeadId actualHeadId) ||
            actualHeadId != expectedHeadId) {
            return false;
        }

        ForkOrigin? origin = null;
        if (payload.Length == WithOriginPayloadSize) {
            if (!HeadIdCodec.TryRead(payload.Slice(24, HeadIdCodec.EncodedSize), expectedIdentity, out HeadId sourceHeadId) ||
                sourceHeadId == expectedHeadId) { return false; }
            SizedPtr sourceTicket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(payload[28..]));
            if (!IsValidSourceTicket(sourceTicket)) { return false; }
            origin = new ForkOrigin(sourceHeadId, sourceTicket);
        }

        header = new HeadHeader(actualHeadId, origin);
        return true;
    }

    internal static bool TryWrite(
        StoreIdentity expectedIdentity, HeadId headId, ForkOrigin? origin, Span<byte> destination) {
        int payloadSize = origin.HasValue ? WithOriginPayloadSize : NoOriginPayloadSize;
        if (!headId.BelongsTo(expectedIdentity) || destination.Length < payloadSize) { return false; }
        if (origin is ForkOrigin source &&
            (!source.SourceHeadId.BelongsTo(expectedIdentity) || source.SourceHeadId == headId ||
            !IsValidSourceTicket(source.SourceEntryTicket))) { return false; }

        Span<byte> payload = destination[..payloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, FormatGateCodec.CurrentVersion);
        expectedIdentity.TryWrite(payload.Slice(4, StoreIdentity.EncodedSize));
        HeadIdCodec.TryWrite(headId, expectedIdentity, payload.Slice(20, HeadIdCodec.EncodedSize));
        if (origin is ForkOrigin validSource) {
            HeadIdCodec.TryWrite(validSource.SourceHeadId, expectedIdentity, payload.Slice(24, HeadIdCodec.EncodedSize));
            BinaryPrimitives.WriteUInt64LittleEndian(payload[28..], validSource.SourceEntryTicket.Packed);
        }

        return true;
    }

    private static bool IsValidSourceTicket(SizedPtr ticket) {
        // Packed 已保证可表示与对齐；合法最后帧的末端/Fence 可以超过 MaxOffset，不能另加 end 上界。
        // 来源可能是 CU；这里仅检查发布坐标的外层尺寸，不证明记录或共同发布资格。
        return ticket.Offset >= s_minimumEntryOffset &&
            ticket.Length >= s_minimumEntryLength && ticket.Length <= SizedPtr.MaxLength;
    }
}
