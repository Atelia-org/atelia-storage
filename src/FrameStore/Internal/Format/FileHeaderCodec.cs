using System.Buffers.Binary;
using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>已取得完整 RBF CRC 资格后解释的固定 header payload。</summary>
internal static class FileHeaderCodec {
    internal const int PayloadSize = 24;

    private static readonly RbfWriteSize s_writeSize = RbfFile.MeasureWriteSize(PayloadSize, 0).Value;

    internal static int HeaderFrameLength => s_writeSize.FrameLength;
    internal static long InitializationBoundary => RbfScanBoundary.Empty.EndExclusive + s_writeSize.AppendLength;

    internal static bool TryWrite(StoreIdentity expectedIdentity, uint fileId, Span<byte> destination) {
        if (expectedIdentity == default || fileId == 0 || destination.Length < PayloadSize) { return false; }
        Span<byte> payload = destination[..PayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, FormatGateCodec.CurrentVersion);
        expectedIdentity.TryWrite(payload.Slice(sizeof(uint), StoreIdentity.EncodedSize));
        BinaryPrimitives.WriteUInt32LittleEndian(payload[20..], fileId);
        return true;
    }

    // 期望值来自 checked 格式门及路径/预定目标，不能从被检查的 header 反向认领。
    internal static bool TryValidate(ReadOnlySpan<byte> payload, StoreIdentity expectedIdentity, uint expectedFileId) {
        if (payload.Length != PayloadSize || expectedIdentity == default || expectedFileId == 0) { return false; }
        return BinaryPrimitives.ReadUInt32LittleEndian(payload) == FormatGateCodec.CurrentVersion &&
            StoreIdentity.TryRead(payload.Slice(sizeof(uint), StoreIdentity.EncodedSize), out StoreIdentity actualIdentity) &&
            actualIdentity == expectedIdentity &&
            BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]) == expectedFileId;
    }
}
