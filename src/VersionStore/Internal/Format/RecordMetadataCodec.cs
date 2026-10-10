using System.Buffers.Binary;

namespace Atelia.VersionStore.Internal.Format;

/// <summary>精确 meta8 的完整 int64 LE 字段；只有整帧 checked 内容才能签发已检查时间。</summary>
internal static class RecordMetadataCodec {
    internal const int EncodedSize = 8;

    internal static bool TryRead(ReadOnlySpan<byte> source, out long recordedAtUnixMs) {
        recordedAtUnixMs = default;
        if (source.Length != EncodedSize) { return false; }
        recordedAtUnixMs = BinaryPrimitives.ReadInt64LittleEndian(source);
        return true;
    }

    internal static bool TryWrite(long recordedAtUnixMs, Span<byte> destination) {
        if (destination.Length < EncodedSize) { return false; }
        BinaryPrimitives.WriteInt64LittleEndian(destination, recordedAtUnixMs);
        return true;
    }
}
