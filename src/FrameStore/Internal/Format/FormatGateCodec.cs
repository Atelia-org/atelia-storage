using System.Buffers.Binary;
using Atelia.Data.Hashing;

namespace Atelia.FrameStore.Internal.Format;

/// <summary>framestore.format 普通控制记录；本类不读取路径或认领实际根资格。</summary>
internal static class FormatGateCodec {
    internal const uint CurrentVersion = 1;
    internal const int EncodedSize = 24;

    internal static bool TryRead(ReadOnlySpan<byte> source, out StoreIdentity identity) {
        identity = default;
        if (source.Length != EncodedSize || !RollingCrc.CheckCodewordForward(source)) { return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != CurrentVersion) { return false; }
        return StoreIdentity.TryRead(source.Slice(sizeof(uint), StoreIdentity.EncodedSize), out identity);
    }

    internal static bool TryWrite(StoreIdentity identity, Span<byte> destination) {
        if (identity == default || destination.Length < EncodedSize) { return false; }
        Span<byte> record = destination[..EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, CurrentVersion);
        identity.TryWrite(record.Slice(sizeof(uint), StoreIdentity.EncodedSize));
        RollingCrc.SealCodewordForward(record);
        return true;
    }
}
