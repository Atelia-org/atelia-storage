using System.Buffers.Binary;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>独立 byte/bit scalar 向量验证 wire 单位、CRC 端序与 XOR phase。</summary>
public class RbfWireCodecTests {
    [Fact]
    public void Profiles_KeepLegacyDefaultsAndUseDistinctProductionHeader() {
        Assert.Equal("RBF1"u8.ToArray(), RbfLayout.Fence.ToArray());
        Assert.Equal("RBF1"u8.ToArray(), RbfLayout.GetFence(RbfProfile.Rbf1).ToArray());
        Assert.Equal("RBF3"u8.ToArray(), RbfLayout.GetFence(RbfProfile.Rbf3).ToArray());
        Assert.Equal(0x31464252u, RbfLayout.GetFenceWord(RbfProfile.Rbf1));
        Assert.Equal(0x33464252u, RbfLayout.GetFenceWord(RbfProfile.Rbf3));
        Assert.Equal(0x33464252u, BinaryPrimitives.ReadUInt32LittleEndian(RbfLayout.GetFence(RbfProfile.Rbf3)));
        Assert.Equal(24, FrameLayout.FixedOverhead);
        Assert.Equal(24, new FrameLayout(0).FrameLength);
        Assert.Equal(RbfProfile.Rbf1, new FrameLayout(0).Profile);
        Assert.Equal(28, RbfLayout.GetMinFrameLength(RbfProfile.Rbf3));
        Assert.Equal(0, RbfLayout.GetTailKeySize(RbfProfile.Rbf1));
        Assert.Equal(4, RbfLayout.GetTailKeySize(RbfProfile.Rbf3));
        Assert.Equal(SizedPtr.MaxLength - 24, RbfLayout.GetMaxPayloadAndMetaLength(RbfProfile.Rbf1));
        Assert.Equal(SizedPtr.MaxLength - 28, RbfLayout.GetMaxPayloadAndMetaLength(RbfProfile.Rbf3));
    }

    [Theory]
    [InlineData(7u, 28)]
    [InlineData(8u, 32)]
    [InlineData(9u, 36)]
    [InlineData(10u, 40)]
    [InlineData(11u, 44)]
    [InlineData(0x03FFFFFFu, 268435452)]
    public void Rbf3Length_UnitsNeedNotHaveZeroLowBits(uint units, int bytes) {
        var decoded = RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf3, units);
        Assert.True(decoded.IsSuccess);
        Assert.Equal(bytes, decoded.Value);
        Assert.Equal(units, RbfWireCodec.EncodeFrameLength(RbfProfile.Rbf3, bytes));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(0x04000000u)]
    [InlineData(0x40000007u)]
    [InlineData(0xFFFFFFFFu)]
    public void Rbf3Length_RejectsFullWireValueBeforeShift(uint units) {
        var result = RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf3, units);
        Assert.True(result.IsFailure);
        Assert.IsType<RbfFramingError>(result.Error);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(27)]
    [InlineData(29)]
    [InlineData(268435456)]
    [InlineData(int.MaxValue)]
    public void Rbf3Length_EncodingRejectsInvalidByteLengths(int bytes) {
        Assert.Throws<ArgumentOutOfRangeException>(() => RbfWireCodec.EncodeFrameLength(RbfProfile.Rbf3, bytes));
    }

    [Fact]
    public void Rbf1Length_PreservesByteUnitAndItsLargerPayloadCapacity() {
        foreach (int bytes in new[] { 24, 28, SizedPtr.MaxLength }) {
            Assert.Equal((uint)bytes, RbfWireCodec.EncodeFrameLength(RbfProfile.Rbf1, bytes));
            Assert.Equal(bytes, RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf1, (uint)bytes).Value);
        }
        foreach (uint invalid in new[] { 0u, 23u, 25u, (uint)SizedPtr.MaxLength + 4, uint.MaxValue }) {
            Assert.IsType<RbfFramingError>(RbfWireCodec.DecodeFrameLength(RbfProfile.Rbf1, invalid).Error);
        }
        Assert.Equal(SizedPtr.MaxLength, new FrameLayout(SizedPtr.MaxLength - 24).FrameLength);
        Assert.Equal(SizedPtr.MaxLength, new FrameLayout(RbfProfile.Rbf3, SizedPtr.MaxLength - 28).FrameLength);
    }

    [Fact]
    public void Rbf3MaximumFrame_StoresMaximumUnitsAndPreservesMaximumMeta() {
        const int metaLength = 65535;
        const int payloadLength = 268435452 - 28 - metaLength;
        var layout = new FrameLayout(RbfProfile.Rbf3, payloadLength, metaLength);
        byte[] trailer = new byte[16];
        layout.FillTrailer(trailer, uint.MaxValue);
        Assert.Equal(ScalarTrailer(65535, uint.MaxValue, 0x03FFFFFF), trailer);
        var parsed = RbfWireCodec.ParseEncodedTrailer(ScalarXor(trailer, 0x80000001u), 0x80000001u);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(268435452u, parsed.Value.TailLen);
        Assert.Equal(metaLength, parsed.Value.TailMetaLen);
        Assert.Equal(0, parsed.Value.PaddingLen);
        Assert.Equal(payloadLength, TrailerCodewordHelper.ComputePayloadLength(RbfProfile.Rbf3, parsed.Value.TailLen, metaLength, 0).Value);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    [InlineData(17, 5)]
    [InlineData(232, 0)]
    [InlineData(0, 65535)]
    public void Rbf3Layout_OffsetsAndCapacityIncludeOnlyOneRawKey(int payloadLength, int metaLength) {
        int padding = (-(payloadLength + metaLength)) & 3;
        int coverage = payloadLength + metaLength + padding;
        var layout = new FrameLayout(RbfProfile.Rbf3, payloadLength, metaLength);
        Assert.Equal(28 + coverage, layout.FrameLength);
        Assert.Equal((uint)(28 + coverage) / 4, layout.WireFrameLength);
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(4 + payloadLength, layout.TailMetaOffset);
        Assert.Equal(4 + payloadLength + metaLength, layout.PaddingOffset);
        Assert.Equal(padding, layout.PaddingLength);
        Assert.Equal(4 + coverage, layout.PayloadCrcOffset);
        Assert.Equal(8 + coverage, layout.TrailerCodewordOffset);
        Assert.Equal(24 + coverage, layout.TailKeyOffset);
        Assert.Equal(4, layout.TailKeyLength);
        Assert.Equal(20 + coverage, layout.EncodedBodyLength);
        Assert.Equal(coverage, layout.PayloadCrcCoverageLength);
    }

    [Theory]
    [InlineData(0, 0, 0x11223344u, false)]
    [InlineData(3, 3, 0xFEDCBA98u, true)]
    [InlineData(5, 3, 0xFFFFFFFFu, false)]
    [InlineData(7, 65535, 0u, true)]
    public void FillTrailer_UsesScalarCrcOverOriginalLittleEndianUnits(int payloadLength, int metaLength, uint tag, bool tombstone) {
        var layout = new FrameLayout(RbfProfile.Rbf3, payloadLength, metaLength);
        uint descriptor = (tombstone ? 0x80000000u : 0) | ((uint)layout.PaddingLength << 29) | (uint)metaLength;
        byte[] expected = ScalarTrailer(descriptor, tag, (uint)layout.FrameLength / 4);
        byte[] actual = new byte[16];
        layout.FillTrailer(actual, tag, tombstone);
        Assert.Equal(expected, actual);

        var parsed = RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, actual);
        Assert.True(parsed.IsSuccess);
        Assert.Equal((uint)layout.FrameLength, parsed.Value.TailLen);
        Assert.Equal(metaLength, parsed.Value.TailMetaLen);
        Assert.Equal(layout.PaddingLength, parsed.Value.PaddingLen);
        Assert.Equal(tombstone, parsed.Value.IsTombstone);
        Assert.Equal(tag, parsed.Value.FrameTag);
    }

    [Fact]
    public void ParseTrailer_GoldenVectorValidatesUnitsBeforeByteNormalization() {
        // Scalar CRC32C, consuming bytes 15..4, yields BE 6A47EC8E for this LE(U=7) vector.
        byte[] trailer = Convert.FromHexString("6A47EC8E000000004433221107000000");
        byte[] original = trailer.ToArray();
        var parsed = RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, trailer);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(0x6A47EC8Eu, parsed.Value.TrailerCrc32C);
        Assert.Equal(0x11223344u, parsed.Value.FrameTag);
        Assert.Equal(28u, parsed.Value.TailLen);
        Assert.Equal(original, trailer);
        Assert.Equal(7u, TrailerCodewordHelper.Parse(trailer).TailLen);

        // Replacing U with L before checking the unchanged CRC is invalid.
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(12), 28);
        Assert.IsType<RbfCrcMismatchError>(RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, trailer).Error);
    }

    [Fact]
    public void ParseTrailer_LegacyGoldenVectorRetainsByteLength() {
        byte[] trailer = Convert.FromHexString("605F43DC000000004433221118000000");
        var parsed = RbfWireCodec.ParseTrailer(RbfProfile.Rbf1, trailer);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(24u, parsed.Value.TailLen);
        Assert.Equal(0x605F43DCu, parsed.Value.TrailerCrc32C);
        Assert.Equal(0x11223344u, parsed.Value.FrameTag);
    }

    [Fact]
    public void ParseTrailer_ChecksCrcBeforeInvalidUnitsAndDoesNotMutateInput() {
        byte[] trailer = ScalarTrailer(0, 1, 0x40000007u);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, trailer).Error);
        trailer[0] ^= 1;
        byte[] original = trailer.ToArray();
        Assert.IsType<RbfCrcMismatchError>(RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, trailer).Error);
        Assert.Equal(original, trailer);
    }

    [Theory]
    [InlineData(0x00010000u)]
    [InlineData(0x10000000u)]
    [InlineData(1u)]
    [InlineData(0x20000000u)]
    public void ParseTrailer_RejectsReservedBitsOrImpossibleMetaPadding(uint descriptor) {
        byte[] trailer = ScalarTrailer(descriptor, 0, 7);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, trailer).Error);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x80000001u)]
    [InlineData(0xFFFFFFFFu)]
    public void ParseEncodedTrailer_AcceptsFullUintKeysAndTailWordBeforeDecodedRangeCheck(uint key) {
        byte[] plaintext = ScalarTrailer(0, 0x11223344u, 7);
        byte[] encoded = ScalarXor(plaintext, key);
        byte[] original = encoded.ToArray();
        uint encodedTail = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(12));
        Assert.Equal(key, encodedTail ^ 7u); // Recovery reconstructs from U, not physical L=28.
        if ((key & 0x80000000u) != 0) { Assert.True(encodedTail >= 0x80000000u); }
        var parsed = RbfWireCodec.ParseEncodedTrailer(encoded, key);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(28u, parsed.Value.TailLen);
        Assert.Equal(0x11223344u, parsed.Value.FrameTag);
        Assert.Equal(original, encoded);
    }

    [Fact]
    public void ParseEncodedTrailer_HighKeyGoldenVectorUsesPhaseZero() {
        byte[] encoded = Convert.FromHexString("6B47EC0E010000804533229106000080");
        var result = RbfWireCodec.ParseEncodedTrailer(encoded, 0x80000001u);
        Assert.True(result.IsSuccess);
        Assert.Equal(28u, result.Value.TailLen);
        Assert.Equal(0x6A47EC8Eu, result.Value.TrailerCrc32C);
        Assert.Equal(0x11223344u, result.Value.FrameTag);
    }

    [Fact]
    public void ParseEncodedTrailer_RejectsFenceKeyAndEncodedFenceEvenWithValidPlaintextCrc() {
        uint fence = 0x33464252u;
        byte[] ordinary = ScalarTrailer(0, 1, 7);
        byte[] encodedWithFenceKey = ScalarXor(ordinary, fence);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseEncodedTrailer(encodedWithFenceKey, fence).Error);

        byte[] markerTag = ScalarTrailer(0, fence, 7);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseEncodedTrailer(markerTag, 0).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void ParseEncodedTrailer_RejectsMarkerInEveryEncodedWordBeforeCrc(int offset) {
        byte[] encoded = ScalarXor(ScalarTrailer(0, 1, 7), 0x80000001u);
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(offset), 0x33464252u);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseEncodedTrailer(encoded, 0x80000001u).Error);
    }

    [Fact]
    public void ParseEncodedTrailer_DecodedHighUnitsCannotPassViaSmallEncodedTailWord() {
        // Encoded TailLen happens to be 7, but the plaintext U is outside the legal uint range.
        byte[] encoded = ScalarXor(ScalarTrailer(0, 1, 0x80000007u), 0x80000000u);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(12)));
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseEncodedTrailer(encoded, 0x80000000u).Error);
    }

    [Fact]
    public void ParseEncodedTrailer_CorruptDecodedUnitsRetainCrcFailure() {
        byte[] encoded = ScalarXor(ScalarTrailer(0, 1, 7), 0xFFFFFFFFu);
        encoded[12] ^= 1;
        Assert.IsType<RbfCrcMismatchError>(RbfWireCodec.ParseEncodedTrailer(encoded, 0xFFFFFFFFu).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(15)]
    public void ParseTrailer_TruncationReturnsFramingFailure(int count) {
        byte[] truncated = new byte[count];
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseTrailer(RbfProfile.Rbf3, truncated).Error);
        Assert.IsType<RbfFramingError>(RbfWireCodec.ParseEncodedTrailer(truncated, 0).Error);
    }

    [Fact]
    public void CompleteDecodedFrame_LocatesTrailerBeforeRawKeyAndChecksPhysicalLength() {
        // L=36, payload=3, meta=3, padding=2. Trailer occupies [16,32), Key [32,36).
        byte[] frame = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 9);
        ScalarTrailer(0x40000003u, 0xFEDCBA98u, 9).CopyTo(frame, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(32), 0xFFFFFFFFu);
        var parsed = FrameLayout.ResultFromTrailer(RbfProfile.Rbf3, frame, out var trailer);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(36u, trailer.TailLen);
        Assert.Equal(3, parsed.Value.PayloadLength);
        Assert.Equal(3, parsed.Value.TailMetaLength);
        Assert.Equal(2, parsed.Value.PaddingLength);
        Assert.Equal(32, parsed.Value.TailKeyOffset);

        ScalarTrailer(0x40000003u, 0xFEDCBA98u, 10).CopyTo(frame, 16);
        Assert.IsType<RbfFramingError>(FrameLayout.ResultFromTrailer(RbfProfile.Rbf3, frame, out _).Error);
    }

    private static byte[] ScalarTrailer(uint descriptor, uint tag, uint wireLength) {
        byte[] trailer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(4), descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(8), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(12), wireLength);
        uint crc = uint.MaxValue;
        for (int i = 15; i >= 4; i--) {
            crc ^= trailer[i];
            for (int bit = 0; bit < 8; bit++) {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0x82F63B78u : 0);
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc ^ uint.MaxValue);
        return trailer;
    }

    private static byte[] ScalarXor(ReadOnlySpan<byte> plaintext, uint key) {
        byte[] encoded = new byte[plaintext.Length];
        for (int i = 0; i < encoded.Length; i++) {
            encoded[i] = (byte)(plaintext[i] ^ (byte)(key >> ((i & 3) * 8)));
        }
        return encoded;
    }
}
