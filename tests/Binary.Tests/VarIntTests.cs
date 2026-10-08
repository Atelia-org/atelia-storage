using System.Buffers;
using Xunit;

namespace Atelia.Binary.Tests;

public class VarIntTests {
    public static IEnumerable<object[]> UnsignedGolden() {
        yield return [0UL, new byte[] { 0x00 }];
        yield return [127UL, new byte[] { 0x7F }];
        yield return [128UL, new byte[] { 0x80, 0x01 }];
        yield return [16383UL, new byte[] { 0xFF, 0x7F }];
        yield return [16384UL, new byte[] { 0x80, 0x80, 0x01 }];
        yield return [(ulong)ushort.MaxValue, new byte[] { 0xFF, 0xFF, 0x03 }];
        yield return [(ulong)uint.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }];
        yield return [ulong.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }];
    }

    [Theory]
    [MemberData(nameof(UnsignedGolden))]
    public void UnsignedGolden_AllRepresentableWidths(ulong value, byte[] expected) {
        AssertUnsignedGolden(64, value, expected);
        if (value <= uint.MaxValue) {
            AssertUnsignedGolden(32, value, expected);
        }

        if (value <= ushort.MaxValue) {
            AssertUnsignedGolden(16, value, expected);
        }
    }

    public static IEnumerable<object[]> SignedGolden() {
        yield return [16, 0L, new byte[] { 0 }];
        yield return [16, -1L, new byte[] { 1 }];
        yield return [16, 1L, new byte[] { 2 }];
        yield return [16, (long)short.MinValue, new byte[] { 0xFF, 0xFF, 0x03 }];
        yield return [16, (long)short.MaxValue, new byte[] { 0xFE, 0xFF, 0x03 }];
        yield return [32, 0L, new byte[] { 0 }];
        yield return [32, -1L, new byte[] { 1 }];
        yield return [32, 1L, new byte[] { 2 }];
        yield return [32, (long)int.MinValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }];
        yield return [32, (long)int.MaxValue, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0x0F }];
        yield return [64, 0L, new byte[] { 0 }];
        yield return [64, -1L, new byte[] { 1 }];
        yield return [64, 1L, new byte[] { 2 }];
        yield return [64, long.MinValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }];
        yield return [64, long.MaxValue, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }];
    }

    [Theory]
    [MemberData(nameof(SignedGolden))]
    public void SignedGolden_IncludesBothExtremes(int width, long value, byte[] expected) {
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        int measured;
        if (width == 16) {
            writer.WriteVarInt16((short)value);
            measured = BareValueEncoding.MeasureVarInt16((short)value);
        }
        else if (width == 32) {
            writer.WriteVarInt32((int)value);
            measured = BareValueEncoding.MeasureVarInt32((int)value);
        }
        else {
            writer.WriteVarInt64(value);
            measured = BareValueEncoding.MeasureVarInt64(value);
        }

        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        Assert.Equal(expected.Length, measured);
        var reader = new BareValueReader(expected);
        long actual = width == 16 ? reader.ReadVarInt16() : width == 32 ? reader.ReadVarInt32() : reader.ReadVarInt64();
        Assert.Equal(value, actual);
        reader.EnsureFullyConsumed();
    }

    [Theory]
    [InlineData(16, 3)]
    [InlineData(32, 5)]
    [InlineData(64, 10)]
    public void NonminimalZero_StopsAtItsTerminatorAndMeasuresTheValue(int width, int maxBytes) {
        for (int byteCount = 2; byteCount <= maxBytes; byteCount++) {
            byte[] source = Enumerable.Repeat((byte)0x80, byteCount + 1).ToArray();
            source[byteCount - 1] = 0;
            source[byteCount] = 0x55;
            var reader = new BareValueReader(source);
            Assert.Equal(0UL, ReadUnsigned(ref reader, width));
            Assert.Equal(byteCount, reader.ConsumedCount);
            Assert.Equal((byte)0x55, reader.ReadByte());
            Assert.Equal(1, width == 16 ? BareValueEncoding.MeasureVarUInt16(0) : width == 32 ? BareValueEncoding.MeasureVarUInt32(0) : BareValueEncoding.MeasureVarUInt64(0));
        }
    }

    [Fact]
    public void PaddingAcceptance_DependsOnDeclaredWidth() {
        byte[] value = [0x80, 0x80, 0x80, 0x80, 0x80, 0];
        var wide = new BareValueReader(value);
        Assert.Equal(0UL, wide.ReadVarUInt64());
        Assert.Equal(6, wide.ConsumedCount);
        var narrow = new BareValueReader(value);
        AssertFailure<InvalidDataException>(ref narrow, 32);
    }

    [Theory]
    [InlineData(16, 3, 3)]
    [InlineData(32, 5, 15)]
    [InlineData(64, 10, 1)]
    public void InvalidLastGroupOrContinuation_DoesNotAdvanceOrReadNextField(int width, int maxBytes, int maxPayload) {
        byte[] overflow = Enumerable.Repeat((byte)0x80, maxBytes + 2).ToArray();
        overflow[0] = 0xAA;
        overflow[maxBytes] = (byte)(maxPayload + 1);
        overflow[maxBytes + 1] = 0x55;
        var reader = new BareValueReader(overflow);
        reader.ReadByte();
        AssertFailure<InvalidDataException>(ref reader, width);
        Assert.Equal(1, reader.ConsumedCount);
        overflow[maxBytes] = 0x80;
        reader = new BareValueReader(overflow);
        reader.ReadByte();
        AssertFailure<InvalidDataException>(ref reader, width);
        Assert.Equal(1, reader.ConsumedCount);
    }

    [Theory]
    [InlineData(16, 3)]
    [InlineData(32, 5)]
    [InlineData(64, 10)]
    public void EveryPrematureEnd_IsTruncationAndPreservesCursor(int width, int maxBytes) {
        for (int length = 0; length < maxBytes; length++) {
            byte[] source = Enumerable.Repeat((byte)0x80, length + 1).ToArray();
            source[0] = 0xAA;
            var reader = new BareValueReader(source);
            reader.ReadByte();
            AssertFailure<EndOfStreamException>(ref reader, width);
            Assert.Equal(1, reader.ConsumedCount);
        }
    }

    [Fact]
    public void SignedAndUnsigned_AllWidths_RoundTripDeterministicRandomBits() {
        var random = new Random(0xB0B1);
        byte[] bytes = new byte[8];
        for (int sample = 0; sample < 1000; sample++) {
            random.NextBytes(bytes);
            ulong value = BitConverter.ToUInt64(bytes);
            var sink = new ArrayBufferWriter<byte>();
            var writer = new BareValueWriter(sink);
            writer.WriteVarUInt16(unchecked((ushort)value));
            writer.WriteVarUInt32(unchecked((uint)value));
            writer.WriteVarUInt64(value);
            writer.WriteVarInt16(unchecked((short)value));
            writer.WriteVarInt32(unchecked((int)value));
            writer.WriteVarInt64(unchecked((long)value));
            var reader = new BareValueReader(sink.WrittenSpan);
            Assert.Equal(unchecked((ushort)value), reader.ReadVarUInt16());
            Assert.Equal(unchecked((uint)value), reader.ReadVarUInt32());
            Assert.Equal(value, reader.ReadVarUInt64());
            Assert.Equal(unchecked((short)value), reader.ReadVarInt16());
            Assert.Equal(unchecked((int)value), reader.ReadVarInt32());
            Assert.Equal(unchecked((long)value), reader.ReadVarInt64());
            reader.EnsureFullyConsumed();
        }
    }

    private static void AssertUnsignedGolden(int width, ulong value, byte[] expected) {
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        int measured;
        if (width == 16) {
            writer.WriteVarUInt16((ushort)value);
            measured = BareValueEncoding.MeasureVarUInt16((ushort)value);
        }
        else if (width == 32) {
            writer.WriteVarUInt32((uint)value);
            measured = BareValueEncoding.MeasureVarUInt32((uint)value);
        }
        else {
            writer.WriteVarUInt64(value);
            measured = BareValueEncoding.MeasureVarUInt64(value);
        }

        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        Assert.Equal(expected.Length, measured);
        var reader = new BareValueReader(expected);
        Assert.Equal(value, ReadUnsigned(ref reader, width));
        reader.EnsureFullyConsumed();
    }

    private static ulong ReadUnsigned(ref BareValueReader reader, int width) => width switch {
        16 => reader.ReadVarUInt16(),
        32 => reader.ReadVarUInt32(),
        64 => reader.ReadVarUInt64(),
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };

    private static void AssertFailure<T>(ref BareValueReader reader, int width) where T : Exception {
        int before = reader.ConsumedCount;
        Exception? failure = null;
        try {
            ReadUnsigned(ref reader, width);
        }
        catch (Exception error) {
            failure = error;
        }

        Assert.IsType<T>(failure);
        Assert.Equal(before, reader.ConsumedCount);
    }
}
