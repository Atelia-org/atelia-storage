using System.Buffers.Binary;
using Atelia.Data.Hashing;

namespace Atelia.HeadStore.Internal.Format;

internal readonly record struct FormatGate(
    StoreIdentity HeadStoreId, StoreIdentity DataStoreId, uint ReservedHeadIdUpperBound);

/// <summary>44B 普通格式门记录；不执行 I/O，也不证明实际根或 borrowed data 绑定资格。</summary>
internal static class FormatGateCodec {
    internal const uint CurrentVersion = 4;
    internal const int EncodedSize = 44;

    internal static bool TryRead(ReadOnlySpan<byte> source, out FormatGate gate) {
        gate = default;
        if (source.Length != EncodedSize || !RollingCrc.CheckCodewordForward(source)) { return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != CurrentVersion ||
            !StoreIdentity.TryRead(source.Slice(4, StoreIdentity.EncodedSize), out StoreIdentity headStoreId) ||
            !StoreIdentity.TryRead(source.Slice(20, StoreIdentity.EncodedSize), out StoreIdentity dataStoreId)) {
            return false;
        }

        uint upperBound = BinaryPrimitives.ReadUInt32LittleEndian(source[36..]);
        if (upperBound >= HeadStore.MaxLocalHeadId) { return false; }
        gate = new FormatGate(headStoreId, dataStoreId, upperBound);
        return true;
    }

    internal static bool TryWrite(FormatGate gate, Span<byte> destination) {
        if (gate.HeadStoreId == default || gate.DataStoreId == default ||
            gate.ReservedHeadIdUpperBound >= HeadStore.MaxLocalHeadId || destination.Length < EncodedSize) {
            return false;
        }

        Span<byte> record = destination[..EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, CurrentVersion);
        gate.HeadStoreId.TryWrite(record.Slice(4, StoreIdentity.EncodedSize));
        gate.DataStoreId.TryWrite(record.Slice(20, StoreIdentity.EncodedSize));
        BinaryPrimitives.WriteUInt32LittleEndian(record[36..], gate.ReservedHeadIdUpperBound);
        RollingCrc.SealCodewordForward(record);
        return true;
    }
}
