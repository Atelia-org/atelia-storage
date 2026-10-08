using Xunit;

namespace Atelia.Binary.Tests;

public class BrotliValidationTests {
    // Independent fixtures: Google Brotli Python bindings 1.0.9, quality=5, lgwin as below.
    // Source: https://github.com/google/brotli/tree/v1.0.9/python
    // Generated with brotli.compress(bytes.fromhex(innerHex), quality=5, lgwin=window).
    // Their inner bytes are hand-authored Bare goldens, not output of Atelia.Binary's encoder.
    [Theory]
    [InlineData(10, "21040004034103")]
    [InlineData(16, "100010034103")]
    [InlineData(18, "830080034103")]
    [InlineData(22, "8B0080034103")]
    [InlineData(24, "8F0080034103")]
    public void IndependentStringGolden_AcceptsStandardWindowsAndUnprofitableRepresentation(int window, string bodyHex) {
        Assert.InRange(window, 10, 24);
        byte[] body = Convert.FromHexString(bodyHex);
        byte[] wire = [2, (byte)body.Length, 2, .. body, 0x55];
        var reader = new BareValueReader(wire);
        Assert.Equal("A", reader.ReadControlledString(body.Length, 2));
        Assert.Equal(wire.Length - 1, reader.ConsumedCount);
        Assert.Equal(0x55, reader.ReadByte());
        Assert.True(reader.End);
    }

    [Fact]
    public void IndependentBytesGolden_IsOwnedAndCountsCompleteInnerValue() {
        byte[] wire = Convert.FromHexString("02070303018002AABB03");
        var reader = new BareValueReader(wire);
        byte[] result = reader.ReadControlledBytes(7, 3)!;
        Array.Fill(wire, (byte)0);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result);
        Assert.True(reader.End);
    }

    [Fact]
    public void IndependentEmptyGolden_UsesOneByteDecodedHeaderAndAllowsNonminimalEnvelopeLengths() {
        // innerHex=00, q5/window18 => 0300800003 (C=5, U=1).
        byte[] wire = Convert.FromHexString("02850081000300800003");
        var stringReader = new BareValueReader(wire);
        var bytesReader = new BareValueReader(wire);
        Assert.Equal("", stringReader.ReadControlledString(5, 1));
        Assert.Empty(bytesReader.ReadControlledBytes(5, 1)!);
        Assert.True(stringReader.End);
        Assert.True(bytesReader.End);
        ControlledTestHelpers.AssertFailure<InvalidDataException>(wire, false, 5, 0);
    }

    [Fact]
    public void IndependentLargeFixture_RequiresExactOutputAndCompleteStream() {
        // innerHex=A09C01 + 20000 repetitions of 41 (bytes length prefix + bytes), q5/window18.
        byte[] body = Convert.FromHexString("13224E00A403823841C569816417011C02");
        byte[] wire = [2, 17, 0xA3, 0x9C, 1, .. body]; // U=20003, not payload length 20000.
        var reader = new BareValueReader(wire);
        byte[] result = reader.ReadControlledBytes(17, 20003)!;
        Assert.Equal(20000, result.Length);
        Assert.All(result, value => Assert.Equal(0x41, value));
        Assert.True(reader.End);
        byte[] tooSmall = [2, 17, 0xA2, 0x9C, 1, .. body];
        byte[] tooLarge = [2, 17, 0xA4, 0x9C, 1, .. body];
        ControlledTestHelpers.AssertFailure<InvalidDataException>(tooSmall, true);
        ControlledTestHelpers.AssertFailure<InvalidDataException>(tooLarge, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterTruncation_IsEndOfStreamAtEveryHeaderAndBodyBoundary(bool bytes) {
        byte[] wire = Convert.FromHexString("020601830080034103");
        // The complete body actually decodes to U=2, but every outer prefix is independently truncated.
        for (int length = 0; length < wire.Length; length++) {
            ControlledTestHelpers.AssertFailure<EndOfStreamException>(wire[..length], bytes);
        }
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([2, 0x80], bytes);
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([2, 1, 0x80], bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredBodyTruncationGarbageConcatenationAndWrongU_AreInvalidAndAtomic(bool bytes) {
        byte[] body = Convert.FromHexString("830080034103");
        for (int length = 1; length < body.Length; length++) {
            ControlledTestHelpers.AssertFailure<InvalidDataException>([2, (byte)length, 2, .. body[..length]], bytes);
        }
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 7, 2, .. body, 0xCC], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 12, 2, .. body, .. body], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 6, 1, .. body], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 6, 3, .. body], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 1, 2, 0xFF], bytes);
    }

    [Theory]
    [InlineData("830080054103", 2)] // inner 0541: string's 2B UTF-8 payload is truncated.
    [InlineData("03018005C08003", 3)] // inner 05C080: invalid overlong UTF-8.
    [InlineData("03018003410003", 3)] // inner 034100: valid string plus an extra byte.
    public void CompleteCompressedStream_DoesNotQualifyInvalidStringInnerValue(string bodyHex, byte decodedCount) {
        byte[] body = Convert.FromHexString(bodyHex);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, (byte)body.Length, decodedCount, .. body], false);
    }

    [Theory]
    [InlineData("830080054103", 2)] // bytes inner declares 5B but carries 1B.
    [InlineData("830080000003", 2)] // bytes inner 0000: empty bytes plus extra byte.
    public void CompleteCompressedStream_DoesNotQualifyInvalidBytesInnerValue(string bodyHex, byte decodedCount) {
        byte[] body = Convert.FromHexString(bodyHex);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, (byte)body.Length, decodedCount, .. body], true);
    }
}
