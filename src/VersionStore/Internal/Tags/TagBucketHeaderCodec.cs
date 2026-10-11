using System.Buffers.Binary;
using Atelia.VersionStore.Internal.Format;

namespace Atelia.VersionStore.Internal.Tags;

internal readonly record struct TagBucketHeader(uint BucketId);

/// <summary>完整 RBF CRC 通过后解释的精确 24B BucketHeader；不授予文件资格。</summary>
internal static class TagBucketHeaderCodec {
    internal const int PayloadSize = 24;

    internal static bool TryRead(ReadOnlySpan<byte> payload, StoreIdentity expectedIdentity,
        uint expectedBucketId, out TagBucketHeader header) {
        header = default;
        if (payload.Length != PayloadSize || expectedIdentity == default
            || expectedBucketId >= TagNameCodec.BucketCount
            || BinaryPrimitives.ReadUInt32LittleEndian(payload) != FormatGateCodec.CurrentVersion
            || !StoreIdentity.TryRead(payload.Slice(4, StoreIdentity.EncodedSize), out StoreIdentity identity)
            || identity != expectedIdentity
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]) != expectedBucketId) {
            return false;
        }
        header = new TagBucketHeader(expectedBucketId);
        return true;
    }

    internal static bool TryWrite(StoreIdentity identity, uint bucketId, Span<byte> destination) {
        if (identity == default || bucketId >= TagNameCodec.BucketCount || destination.Length < PayloadSize) {
            return false;
        }
        Span<byte> payload = destination[..PayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, FormatGateCodec.CurrentVersion);
        identity.TryWrite(payload.Slice(4, StoreIdentity.EncodedSize));
        BinaryPrimitives.WriteUInt32LittleEndian(payload[20..], bucketId);
        return true;
    }
}
