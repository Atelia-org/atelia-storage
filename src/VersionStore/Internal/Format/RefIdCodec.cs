using System.Buffers.Binary;

namespace Atelia.VersionStore.Internal.Format;

/// <summary>唯一的内部 4B LE local 字段 codec；宿主身份/CRC/全记录资格由调用方建立。</summary>
internal static class RefIdCodec {
    internal const int EncodedSize = 4;

    internal static bool TryRead(ReadOnlySpan<byte> source, StoreIdentity expectedIdentity, out RefId refId) {
        refId = default;
        if (source.Length != EncodedSize || expectedIdentity == default) { return false; }
        uint localId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (localId == 0) { return false; }
        refId = RefId.Create(expectedIdentity, localId);
        return true;
    }

    internal static bool TryWrite(RefId refId, StoreIdentity expectedIdentity, Span<byte> destination) {
        if (!refId.BelongsTo(expectedIdentity) || destination.Length < EncodedSize) { return false; }
        BinaryPrimitives.WriteUInt32LittleEndian(destination, refId.LocalId);
        return true;
    }
}
