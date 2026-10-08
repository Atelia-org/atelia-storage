using System.Buffers;
using Xunit;

namespace Atelia.Binary.Tests;

public class ControlledValueTests {
    [Theory]
    [InlineData(null, "00")]
    [InlineData("", "0100")]
    [InlineData("A", "010341")]
    [InlineData("中", "01022D4E")]
    public void RawStrings_HaveIndependentWireGolden(string? value, string expectedHex) {
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledString(value);
        byte[] wire = ControlledTestHelpers.Write(plan);
        Assert.Equal(Convert.FromHexString(expectedHex), wire);
        Assert.Equal((long)wire.Length, plan.EncodedLength);
        var reader = new BareValueReader(wire);
        Assert.Equal(value, reader.ReadControlledString());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void RawUnpairedSurrogate_HasRuntimeConstructedIndependentGolden() {
        // Custom attribute string serialization cannot carry a lone UTF-16 surrogate intact.
        string value = new('\uD800', 1);
        byte[] wire = ControlledTestHelpers.Write(BareValueEncoding.PrepareControlledString(value));
        Assert.Equal(Convert.FromHexString("010200D8"), wire);
        var reader = new BareValueReader(wire);
        Assert.Equal(value, reader.ReadControlledString());
        Assert.True(reader.End);
    }

    [Fact]
    public void NullAndEmptyBytes_AreDifferentAndHaveIndependentGolden() {
        byte[] nullWire = ControlledTestHelpers.Write(ControlledValueEncodingPlan.Null);
        byte[] emptyWire = ControlledTestHelpers.Write(BareValueEncoding.PrepareControlledBytes([]));
        byte[] bytesWire = ControlledTestHelpers.Write(BareValueEncoding.PrepareControlledBytes([0xAA, 0xBB]));
        Assert.Equal(new byte[] { 0 }, nullWire);
        Assert.Equal(new byte[] { 1, 0 }, emptyWire);
        Assert.Equal(new byte[] { 1, 2, 0xAA, 0xBB }, bytesWire);
        var nullReader = new BareValueReader(nullWire);
        var emptyReader = new BareValueReader(emptyWire);
        Assert.Null(nullReader.ReadControlledBytes());
        Assert.Empty(emptyReader.ReadControlledBytes()!);
        Assert.True(nullReader.End);
        Assert.True(emptyReader.End);
    }

    [Fact]
    public void RawBytes_ReturnOwnedStorageWhileOrdinaryBytesRemainBorrowed() {
        byte[] controlledWire = [1, 2, 0xAA, 0xBB, 0x55];
        var controlledReader = new BareValueReader(controlledWire);
        byte[] owned = controlledReader.ReadControlledBytes()!;
        controlledWire[2] = 0xCC;
        Assert.Equal(new byte[] { 0xAA, 0xBB }, owned);
        Assert.Equal(0x55, controlledReader.ReadByte());

        byte[] plainWire = [2, 0xAA, 0xBB];
        var plainReader = new BareValueReader(plainWire);
        ReadOnlySpan<byte> borrowed = plainReader.ReadBytes();
        plainWire[1] = 0xCC;
        Assert.Equal(0xCC, borrowed[0]);
    }

    [Fact]
    public void NullableScalarComposition_PreservesBitsAndRequiresExplicitCompositeCommit() {
        uint nanBits = 0x7FC01234;
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        writer.WriteSingleLE(BitConverter.UInt32BitsToSingle(nanBits));
        Assert.Equal(Convert.FromHexString("00013412C07F"), sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(sink.WrittenSpan);
        Assert.False(reader.ReadBoolean());
        Assert.True(reader.ReadBoolean());
        Assert.Equal(nanBits, BitConverter.SingleToUInt32Bits(reader.ReadSingleLE()));
        Assert.True(reader.End);

        var incomplete = new BareValueReader(new byte[] { 1, 0 });
        var working = incomplete;
        Assert.True(working.ReadBoolean());
        Exception? failure = null;
        try { _ = working.ReadSingleLE(); }
        catch (Exception error) { failure = error; }
        Assert.IsType<EndOfStreamException>(failure);
        Assert.Equal(0, incomplete.ConsumedCount);
        Assert.Equal(1, working.ConsumedCount);
    }

    [Fact]
    public void ZeroLimits_AdmitNullButExcludeEvenEmptyInnerHeaders() {
        var nullString = new BareValueReader(new byte[] { 0 });
        var nullBytes = new BareValueReader(new byte[] { 0 });
        Assert.Null(nullString.ReadControlledString(0, 0));
        Assert.Null(nullBytes.ReadControlledBytes(0, 0));
        ControlledTestHelpers.AssertFailure<InvalidDataException>([1, 0], false, 0, 0);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([1, 0], true, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawLimits_CountActualNonminimalHeaderBeforePayloadAllocation(bool bytes) {
        byte[] nonminimal = bytes ? [1, 0x81, 0, 0x41] : [1, 0x83, 0, 0x41];
        byte[] minimal = bytes ? [1, 1, 0x41] : [1, 3, 0x41];
        ControlledTestHelpers.AssertFailure<InvalidDataException>(nonminimal, bytes, 2, int.MaxValue);
        ControlledTestHelpers.AssertFailure<InvalidDataException>(nonminimal, bytes, int.MaxValue, 2);
        var reader = new BareValueReader(minimal);
        if (bytes) { Assert.Equal(new byte[] { 0x41 }, reader.ReadControlledBytes(2, 2)); }
        else { Assert.Equal("A", reader.ReadControlledString(2, 2)); }
        Assert.True(reader.End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawHeaderAndBodyTruncation_AreEndOfStreamAndAtomic(bool bytes) {
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([], bytes);
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([1], bytes);
        ControlledTestHelpers.AssertFailure<EndOfStreamException>([1, 0x80], bytes);
        ControlledTestHelpers.AssertFailure<EndOfStreamException>(bytes ? [1, 2, 0x41] : [1, 5, 0x41], bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputRangesLimitsAndUnknownControls_AreInvalidDataAndAtomic(bool bytes) {
        ControlledTestHelpers.AssertFailure<InvalidDataException>([3], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([0xFF], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([1, 0x80, 0x80, 0x80, 0x80, 0x08], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 0, 1], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 1, 0], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 0x80, 0x80, 0x80, 0x80, 0x08, 1], bytes);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 1, 0x80, 0x80, 0x80, 0x80, 0x08], bytes);
        // Limits are checked before the body is required, so missing body does not override them.
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 10, 10], bytes, 9, 10);
        ControlledTestHelpers.AssertFailure<InvalidDataException>([2, 10, 10], bytes, 10, 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeLimits_AreCallingErrorsBeforeInputAndAtomic(bool bytes) {
        ControlledTestHelpers.AssertFailure<ArgumentOutOfRangeException>([], bytes, -1, 0);
        ControlledTestHelpers.AssertFailure<ArgumentOutOfRangeException>([], bytes, 0, -1);
        ControlledTestHelpers.AssertFailure<ArgumentOutOfRangeException>([0], bytes, -1, int.MaxValue);
    }

    [Fact]
    public void RawInvalidUtf8_RejectsWithoutAdvancing() {
        ControlledTestHelpers.AssertFailure<InvalidDataException>([1, 5, 0xC0, 0x80], bytes: false);
    }
}

internal static class ControlledTestHelpers {
    internal static byte[] Write(ControlledValueEncodingPlan plan) {
        var sink = new ArrayBufferWriter<byte>();
        new BareValueWriter(sink).WritePreparedValue(plan);
        return sink.WrittenSpan.ToArray();
    }

    internal static void AssertFailure<TException>(byte[] wire, bool bytes,
        int maxStoredByteCount = int.MaxValue, int maxDecodedByteCount = int.MaxValue)
        where TException : Exception {
        byte[] withPrefix = [0xAA, .. wire];
        var reader = new BareValueReader(withPrefix);
        Assert.Equal(0xAA, reader.ReadByte());
        Exception? failure = null;
        try {
            if (bytes) { _ = reader.ReadControlledBytes(maxStoredByteCount, maxDecodedByteCount); }
            else { _ = reader.ReadControlledString(maxStoredByteCount, maxDecodedByteCount); }
        }
        catch (Exception error) { failure = error; }
        Assert.IsType<TException>(failure);
        Assert.Equal(1, reader.ConsumedCount);
        Assert.Equal(wire.Length, reader.RemainingCount);
    }
}
