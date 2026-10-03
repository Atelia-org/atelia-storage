using System.Buffers.Binary;
using Atelia.Data.Binary;

namespace Atelia.Rbf.Internal;

/// <summary>在 wire 长度与内部 byte 长度之间转换，并验证原始 TrailerCodeword。</summary>
internal static class RbfWireCodec {
    private const uint Rbf3MinLengthUnits = 7;
    private const uint Rbf3LengthUnitsLimit = 1u << 26;

    /// <summary>先验证完整 wire 值，再转换为 byte 长度。</summary>
    internal static AteliaResult<int> DecodeFrameLength(RbfProfile profile, uint wireLength) {
        switch (profile) {
            case RbfProfile.Rbf1:
                if (wireLength < FrameLayout.MinFrameLength || wireLength > FrameLayout.MaxFrameLength ||
                    (wireLength & RbfLayout.AlignmentMask) != 0) {
                    return new RbfFramingError($"Invalid RBF1 frame byte length: {wireLength}.");
                }
                return (int)wireLength;
            case RbfProfile.Rbf3:
                // Range-check U before shifting: 0x40000007 must not alias the valid length 28.
                if (wireLength < Rbf3MinLengthUnits || wireLength >= Rbf3LengthUnitsLimit) {
                    return new RbfFramingError($"Invalid RBF3 frame length units: {wireLength}.");
                }
                return (int)(wireLength << 2);
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown RBF profile.");
        }
    }

    /// <summary>将合法 byte 长度转换成 profile 的 wire 字段。</summary>
    internal static uint EncodeFrameLength(RbfProfile profile, int frameLength) {
        if (frameLength < RbfLayout.GetMinFrameLength(profile) || frameLength > FrameLayout.MaxFrameLength ||
            (frameLength & RbfLayout.AlignmentMask) != 0) {
            throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength, "Frame length must be in the profile's aligned byte range.");
        }
        return profile == RbfProfile.Rbf3 ? (uint)frameLength >> 2 : (uint)frameLength;
    }

    /// <summary>验证 plaintext Trailer 的原始 CRC，然后将 TailLen 归一为 bytes。</summary>
    internal static AteliaResult<TrailerCodewordData> ParseTrailer(RbfProfile profile, scoped ReadOnlySpan<byte> plaintextTrailer) {
        if (plaintextTrailer.Length < TrailerCodewordHelper.Size) {
            return new RbfFramingError($"TrailerCodeword truncated: {plaintextTrailer.Length} bytes.");
        }

        // ParseAndValidate must see LE(U) for RBF3. Do not replace that word with LE(L).
        var parsed = TrailerCodewordHelper.ParseAndValidate(plaintextTrailer);
        if (parsed.IsFailure) { return parsed.Error!; }

        var raw = parsed.Value;
        var length = DecodeFrameLength(profile, raw.TailLen);
        if (length.IsFailure) { return length.Error!; }
        var payloadLength = TrailerCodewordHelper.ComputePayloadLength(profile, (uint)length.Value, raw.TailMetaLen, raw.PaddingLen);
        if (payloadLength.IsFailure) { return payloadLength.Error!; }

        return new TrailerCodewordData {
            TrailerCrc32C = raw.TrailerCrc32C,
            FrameDescriptor = raw.FrameDescriptor,
            FrameTag = raw.FrameTag,
            TailLen = (uint)length.Value
        };
    }

    /// <summary>验证 RBF3 的 encoded Trailer 并按 body phase 0 解码。</summary>
    internal static AteliaResult<TrailerCodewordData> ParseEncodedTrailer(scoped ReadOnlySpan<byte> encodedTrailer, uint key) {
        if (encodedTrailer.Length < TrailerCodewordHelper.Size) {
            return new RbfFramingError($"Encoded TrailerCodeword truncated: {encodedTrailer.Length} bytes.");
        }
        uint fence = RbfLayout.GetFenceWord(RbfProfile.Rbf3);
        if (key == fence) { return new RbfFramingError("RBF3 TailKey equals Fence."); }

        encodedTrailer = encodedTrailer[..TrailerCodewordHelper.Size];
        for (int offset = 0; offset < encodedTrailer.Length; offset += sizeof(uint)) {
            if (BinaryPrimitives.ReadUInt32LittleEndian(encodedTrailer[offset..]) == fence) {
                return new RbfFramingError("RBF3 encoded TrailerCodeword contains Fence.");
            }
        }

        // Body and Trailer begin on a 4-byte boundary, so every Trailer byte uses phase 0.
        Span<byte> plaintext = stackalloc byte[TrailerCodewordHelper.Size];
        XorEscape.Copy(encodedTrailer, plaintext, key);
        return ParseTrailer(RbfProfile.Rbf3, plaintext);
    }
}
