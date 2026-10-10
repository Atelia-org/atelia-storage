using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;

namespace Atelia.VersionStore.Internal.Format;

// 来源只声明数值坐标，不能独立签发 RefRevision 或证明源成员/内容健康。
internal readonly record struct ForkOrigin(RefId SourceRefId, SizedPtr SourceSnapshotTicket);
internal readonly record struct RefHeader(RefId RefId, ForkOrigin? Origin);

/// <summary>完整 RBF CRC 通过后解释的 24/36B header payload；本 codec 不签发发布资格。</summary>
internal static class RefHeaderCodec {
    internal const int NoOriginPayloadSize = 24;
    internal const int WithOriginPayloadSize = 36;

    private static readonly long s_minimumSnapshotOffset = RbfScanBoundary.Empty.EndExclusive +
        RbfFile.MeasureWriteSize(NoOriginPayloadSize, 0).Value.AppendLength;
    private static readonly int s_minimumSnapshotLength =
        RbfFile.MeasureWriteSize(1, RecordMetadataCodec.EncodedSize).Value.FrameLength;
    private static readonly int s_maximumSnapshotLength =
        RbfFile.MeasureWriteSize(VersionStore.MaxRootPayloadLength, RecordMetadataCodec.EncodedSize).Value.FrameLength;

    internal static bool TryRead(
        ReadOnlySpan<byte> payload, StoreIdentity expectedIdentity, RefId expectedRefId, out RefHeader header) {
        header = default;
        if (payload.Length is not (NoOriginPayloadSize or WithOriginPayloadSize) ||
            !expectedRefId.BelongsTo(expectedIdentity) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload) != FormatGateCodec.CurrentVersion ||
            !StoreIdentity.TryRead(payload.Slice(4, StoreIdentity.EncodedSize), out StoreIdentity actualIdentity) ||
            actualIdentity != expectedIdentity ||
            !RefIdCodec.TryRead(payload.Slice(20, RefIdCodec.EncodedSize), expectedIdentity, out RefId actualRefId) ||
            actualRefId != expectedRefId) {
            return false;
        }

        ForkOrigin? origin = null;
        if (payload.Length == WithOriginPayloadSize) {
            if (!RefIdCodec.TryRead(payload.Slice(24, RefIdCodec.EncodedSize), expectedIdentity, out RefId sourceRefId) ||
                sourceRefId == expectedRefId) { return false; }
            SizedPtr sourceTicket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(payload[28..]));
            if (!IsValidSourceTicket(sourceTicket)) { return false; }
            origin = new ForkOrigin(sourceRefId, sourceTicket);
        }

        header = new RefHeader(actualRefId, origin);
        return true;
    }

    internal static bool TryWrite(
        StoreIdentity expectedIdentity, RefId refId, ForkOrigin? origin, Span<byte> destination) {
        int payloadSize = origin.HasValue ? WithOriginPayloadSize : NoOriginPayloadSize;
        if (!refId.BelongsTo(expectedIdentity) || destination.Length < payloadSize) { return false; }
        if (origin is ForkOrigin source &&
            (!source.SourceRefId.BelongsTo(expectedIdentity) || source.SourceRefId == refId ||
            !IsValidSourceTicket(source.SourceSnapshotTicket))) { return false; }

        Span<byte> payload = destination[..payloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, FormatGateCodec.CurrentVersion);
        expectedIdentity.TryWrite(payload.Slice(4, StoreIdentity.EncodedSize));
        RefIdCodec.TryWrite(refId, expectedIdentity, payload.Slice(20, RefIdCodec.EncodedSize));
        if (origin is ForkOrigin validSource) {
            RefIdCodec.TryWrite(validSource.SourceRefId, expectedIdentity, payload.Slice(24, RefIdCodec.EncodedSize));
            BinaryPrimitives.WriteUInt64LittleEndian(payload[28..], validSource.SourceSnapshotTicket.Packed);
        }

        return true;
    }

    private static bool IsValidSourceTicket(SizedPtr ticket) {
        // Packed 已保证可表示与对齐；合法最后帧的末端/Fence 可以超过 MaxOffset，不能另加 end 上界。
        return ticket.Offset >= s_minimumSnapshotOffset &&
            ticket.Length >= s_minimumSnapshotLength && ticket.Length <= s_maximumSnapshotLength;
    }
}
