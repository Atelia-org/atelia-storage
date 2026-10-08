using System.Buffers;
using K4os.Compression.LZ4;
using Xunit;

namespace Atelia.Binary.Tests;

public class Lz4BlockTests {
    // Hand-authored from the standard block syntax, independent of both production Write
    // and K4os Encode: https://github.com/lz4/lz4/blob/v1.9.4/doc/lz4_Block_format.md
    [Theory]
    [InlineData("200341", 2, "A")]
    [InlineData("30024100", 3, "A")]
    [InlineData("1000", 1, "")]
    [InlineData("30830041", 3, "A")] // Redundant inner VarInt counts towards U.
    public void IndependentLiteralStringGoldens_CountActualInnerBytes(string bodyHex, byte decoded, string expected) {
        byte[] body = Convert.FromHexString(bodyHex);
        byte[] wire = [3, (byte)body.Length, decoded, .. body, 0x55];
        var reader = new BareValueReader(wire);
        Assert.Equal(expected, reader.ReadControlledString(body.Length, decoded));
        Assert.Equal(wire.Length - 1, reader.ConsumedCount);
        Assert.Equal(0x55, reader.ReadByte());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void IndependentMatchGolden_HandlesOverlapAndOwnedBytes() {
        // U=33: literal bytes 20 AA, offset=1, match=19+7, final five AA literals.
        byte[] wire = Convert.FromHexString("030C212F20AA01000750AAAAAAAAAA");
        var reader = new BareValueReader(wire);
        byte[] result = reader.ReadControlledBytes(12, 33)!;
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 32), result);
        reader.EnsureFullyConsumed();
        Array.Fill(wire, (byte)0);
        Assert.All(result, value => Assert.Equal(0xAA, value));
    }

    [Fact]
    public void IndependentExtendedLiteralGolden_AndRedundantEnvelopeLengthsAreAccepted() {
        // U=302: bytes header AC 02 + 300 A's. Literal length=15+255+32, C=305.
        byte[] body = [0xF0, 0xFF, 0x20, 0xAC, 2, .. Enumerable.Repeat((byte)0x41, 300)];
        byte[] wire = [3, 0xB1, 0x82, 0, 0xAE, 0x82, 0, .. body];
        var reader = new BareValueReader(wire);
        Assert.Equal(Enumerable.Repeat((byte)0x41, 300), reader.ReadControlledBytes(305, 302));
        reader.EnsureFullyConsumed();
        ControlledTestHelpers.AssertFailure<InvalidDataException>(wire, true, 304, 302);
        ControlledTestHelpers.AssertFailure<InvalidDataException>(wire, true, 305, 301);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteOuterEnvelope_IsRequiredBeforeBlockValidation(bool bytes) {
        byte[] wire = Convert.FromHexString("030302200341");
        for (int length = 0; length < wire.Length; length++) {
            ControlledTestHelpers.AssertFailure<EndOfStreamException>(wire[..length], bytes);
        }
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([3, 0x80], bytes);
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([3, 1, 0x80], bytes);
        foreach (byte[] prefix in new byte[][] {
            [3, 0, 1], [3, 1, 0], [3, 0x80, 0x80, 0x80, 0x80, 8, 1],
            [3, 1, 0x80, 0x80, 0x80, 0x80, 8]
        }) {
            ControlledTestHelpers.AssertFailure<InvalidDataException>(prefix, bytes);
        }
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 10, 10], bytes, 9, 10);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 10, 10], bytes, 10, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedBlockTailConcatenationAndWrongU_AreInvalidAndAtomic(bool bytes) {
        byte[] body = Convert.FromHexString("200341");
        for (int length = 1; length < body.Length; length++) {
            ControlledTestHelpers.AssertFailure<InvalidDataException>([3, (byte)length, 2, .. body[..length]], bytes);
        }
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 4, 2, .. body, 0], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 6, 2, .. body, .. body], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 3, 1, .. body], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, 3, 3, .. body], bytes);
    }

    [Theory]
    [InlineData("2F20AA00000750AAAAAAAAAA", 33)] // Offset=0 is invalid, even if a safe decoder tolerates it.
    [InlineData("2F20AA03000750AAAAAAAAAA", 33)] // Offset exceeds complete prior output: would require a dictionary.
    [InlineData("2F20AA01", 33)]               // Truncated offset.
    [InlineData("2F20AA0100", 33)]             // Missing extended match length.
    [InlineData("2F20AA0100FF", 33)]           // Extended match exceeds U; bounded arithmetic must reject.
    [InlineData("F0", 100)]                    // Missing extended literal length.
    [InlineData("F0FF", 100)]                  // Oversized extended literal length.
    [InlineData("104101004041414141", 9)]      // Only four final literals and a late last match.
    [InlineData("10410100504141414141", 10)]   // Five final literals, but last match starts <12B before U.
    [InlineData("1F41010000", 20)]             // No final literal sequence.
    public void InvalidStructure_IsRejectedWithoutCommittingCursor(string bodyHex, byte decoded) {
        byte[] body = Convert.FromHexString(bodyHex);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, (byte)body.Length, decoded, .. body], true);
    }

    [Theory]
    [InlineData("200541", 2, false)] // Truncated UTF-8 inner codeword.
    [InlineData("3005C080", 3, false)] // Invalid UTF-8.
    [InlineData("30034100", 3, false)] // String plus unconsumed bytes.
    [InlineData("200541", 2, true)] // Bytes declare five but contain one.
    [InlineData("200000", 2, true)] // Empty bytes plus unconsumed byte.
    [InlineData("1000", 2, true)] // Correct literal block, wrong U.
    public void CompleteBlock_DoesNotQualifyInvalidInnerValue(string bodyHex, byte decoded, bool bytes) {
        byte[] body = Convert.FromHexString(bodyHex);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3, (byte)body.Length, decoded, .. body], bytes);
    }

    [Fact]
    public void MixedMethods_UseOnePreparedWriteAndTransparentReadWithExactBudgets() {
        string text = string.Concat(Enumerable.Repeat("状态\uD800;世界;", 500));
        byte[] source = Enumerable.Repeat((byte)0xAB, 8192).ToArray();
        ValueCompression[] methods = [ValueCompression.None, ValueCompression.Brotli, ValueCompression.Lz4Block];
        var plans = methods.Select(method => (String: BareValueEncoding.PrepareControlledString(text, method),
            Bytes: BareValueEncoding.PrepareControlledBytes(source, method))).ToArray();
        Array.Fill(source, (byte)0);
        var output = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(output);
        for (int i = 0; i < plans.Length; i++) {
            int start = output.WrittenCount;
            writer.WritePreparedValue(plans[i].String);
            Assert.Equal(i == 0 ? 1 : i + 1, output.WrittenSpan[start]);
            writer.WritePreparedValue(plans[i].Bytes);
        }
        Assert.Equal(plans.Sum(p => p.String.EncodedLength + p.Bytes.EncodedLength), output.WrittenCount);
        var reader = new BareValueReader(output.WrittenSpan);
        foreach (var plan in plans) {
            Assert.Equal(text, reader.ReadControlledString(65536, 65536));
            Assert.Equal(Enumerable.Repeat((byte)0xAB, 8192), reader.ReadControlledBytes(65536, 65536));
        }
        reader.EnsureFullyConsumed();
        var repeated = new ArrayBufferWriter<byte>();
        var repeatedWriter = new BareValueWriter(repeated);
        repeatedWriter.WritePreparedValue(plans[2].Bytes);
        repeatedWriter.WritePreparedValue(plans[2].Bytes);
        int size = checked((int)plans[2].Bytes.EncodedLength);
        Assert.Equal(size * 2, repeated.WrittenCount);
        Assert.True(repeated.WrittenSpan[..size].SequenceEqual(repeated.WrittenSpan[size..]));
    }

    [Fact]
    public void Preparation_ComparesCompleteSizeAndFallsBackWhenTheBoundedCandidateCannotFit() {
        var random = new Random(71004);
        foreach (int length in new[] { 0, 1, 3, 12, 13, 16, 32, 126, 127, 128, 255, 1024, 65536, 1_048_576 }) {
            foreach (bool compressible in new[] { false, true }) {
                byte[] source = new byte[length];
                if (compressible) { Array.Fill(source, (byte)0x42); }
                else { random.NextBytes(source); }
                var plain = new ArrayBufferWriter<byte>();
                new BareValueWriter(plain).WriteBytes(source);
                // Independent full-capacity encoder result, rather than production's U-byte trial.
                byte[] candidate = new byte[LZ4Codec.MaximumOutputSize(plain.WrittenCount)];
                int stored = LZ4Codec.Encode(plain.WrittenSpan, candidate, LZ4Level.L00_FAST);
                Assert.True(stored > 0);
                long compressedSize = 1L + BareValueEncoding.MeasureVarUInt32((uint)stored)
                    + BareValueEncoding.MeasureVarUInt32((uint)plain.WrittenCount) + stored;
                long expected = Math.Min(1L + plain.WrittenCount, compressedSize);
                var plan = BareValueEncoding.PrepareControlledBytes(source, ValueCompression.Lz4Block);
                Assert.Equal(expected, plan.EncodedLength);
                var output = new ArrayBufferWriter<byte>();
                new BareValueWriter(output).WritePreparedValue(plan);
                Assert.Equal(expected, output.WrittenCount);
                Assert.Equal(compressedSize < 1L + plain.WrittenCount ? 3 : 1, output.WrittenSpan[0]);
                var reader = new BareValueReader(output.WrittenSpan);
                Assert.Equal(source, reader.ReadControlledBytes());
                reader.EnsureFullyConsumed();
            }
        }
    }

    [Theory]
    [InlineData(LZ4Level.L03_HC)]
    [InlineData(LZ4Level.L12_MAX)]
    public void Decoder_IsIndependentOfWriterCompressionLevel(LZ4Level level) {
        byte[] payload = new byte[131072];
        for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i % 251); }
        var inner = new ArrayBufferWriter<byte>();
        new BareValueWriter(inner).WriteBytes(payload);
        byte[] candidate = new byte[LZ4Codec.MaximumOutputSize(inner.WrittenCount)];
        int stored = LZ4Codec.Encode(inner.WrittenSpan, candidate, level);
        Assert.True(stored > 0);
        var output = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(output);
        writer.WriteByte(3);
        writer.WriteVarUInt32((uint)stored);
        writer.WriteVarUInt32((uint)inner.WrittenCount);
        writer.WriteRawBytes(candidate.AsSpan(0, stored));
        var reader = new BareValueReader(output.WrittenSpan);
        Assert.Equal(payload, reader.ReadControlledBytes());
        reader.EnsureFullyConsumed();
    }
}
