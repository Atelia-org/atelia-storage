using System.Buffers;
using Xunit;

namespace Atelia.Binary.Tests;

public class ScalarTests {
    [Fact]
    public void FixedValues_HaveIndependentLittleEndianGolden() {
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteByte(0xA5);
        writer.WriteSByte(-2);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        writer.WriteUInt16LE(0x1234);
        writer.WriteInt16LE(-2);
        writer.WriteUInt32LE(0x12345678);
        writer.WriteInt32LE(-2);
        writer.WriteUInt64LE(0x0123456789ABCDEF);
        writer.WriteInt64LE(-2);
        writer.WriteCharLE('\uD800');
        byte[] expected = [
            0xA5, 0xFE, 0x00, 0x01, 0x34, 0x12, 0xFE, 0xFF,
            0x78, 0x56, 0x34, 0x12, 0xFE, 0xFF, 0xFF, 0xFF,
            0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01,
            0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xD8
        ];
        Assert.Equal(expected, sink.WrittenSpan.ToArray());

        var reader = new BareValueReader(expected);
        Assert.Equal((byte)0xA5, reader.ReadByte());
        Assert.Equal((sbyte)-2, reader.ReadSByte());
        Assert.False(reader.ReadBoolean());
        Assert.True(reader.ReadBoolean());
        Assert.Equal((ushort)0x1234, reader.ReadUInt16LE());
        Assert.Equal((short)-2, reader.ReadInt16LE());
        Assert.Equal(0x12345678U, reader.ReadUInt32LE());
        Assert.Equal(-2, reader.ReadInt32LE());
        Assert.Equal(0x0123456789ABCDEFUL, reader.ReadUInt64LE());
        Assert.Equal(-2L, reader.ReadInt64LE());
        Assert.Equal('\uD800', reader.ReadCharLE());
        reader.EnsureFullyConsumed();
    }

    public static IEnumerable<object[]> HalfPatterns() {
        yield return [0x0000, new byte[] { 0x00, 0x00 }];
        yield return [0x8000, new byte[] { 0x00, 0x80 }];
        yield return [0x0001, new byte[] { 0x01, 0x00 }];
        yield return [0x7C00, new byte[] { 0x00, 0x7C }];
        yield return [0xFC00, new byte[] { 0x00, 0xFC }];
        yield return [0x7E01, new byte[] { 0x01, 0x7E }];
        yield return [0x7D23, new byte[] { 0x23, 0x7D }];
        yield return [0xFFFF, new byte[] { 0xFF, 0xFF }];
    }

    [Theory]
    [MemberData(nameof(HalfPatterns))]
    public void Half_PreservesEverySpecifiedBitPattern(int pattern, byte[] expected) {
        ushort bits = (ushort)pattern;
        var sink = new ArrayBufferWriter<byte>();
        new BareValueWriter(sink).WriteHalfLE(BitConverter.UInt16BitsToHalf(bits));
        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(expected);
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(reader.ReadHalfLE()));
    }

    public static IEnumerable<object[]> SinglePatterns() {
        yield return [0x00000000U, new byte[] { 0, 0, 0, 0 }];
        yield return [0x80000000U, new byte[] { 0, 0, 0, 0x80 }];
        yield return [0x00000001U, new byte[] { 1, 0, 0, 0 }];
        yield return [0x7F800000U, new byte[] { 0, 0, 0x80, 0x7F }];
        yield return [0xFF800000U, new byte[] { 0, 0, 0x80, 0xFF }];
        yield return [0x7FC00001U, new byte[] { 1, 0, 0xC0, 0x7F }];
        yield return [0x7FA12345U, new byte[] { 0x45, 0x23, 0xA1, 0x7F }];
        yield return [0xFFC54321U, new byte[] { 0x21, 0x43, 0xC5, 0xFF }];
    }

    [Theory]
    [MemberData(nameof(SinglePatterns))]
    public void Single_PreservesEverySpecifiedBitPattern(uint bits, byte[] expected) {
        var sink = new ArrayBufferWriter<byte>();
        new BareValueWriter(sink).WriteSingleLE(BitConverter.UInt32BitsToSingle(bits));
        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(expected);
        Assert.Equal(bits, BitConverter.SingleToUInt32Bits(reader.ReadSingleLE()));
    }

    public static IEnumerable<object[]> DoublePatterns() {
        yield return [0x0000000000000000UL, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }];
        yield return [0x8000000000000000UL, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80 }];
        yield return [0x0000000000000001UL, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }];
        yield return [0x7FF0000000000000UL, new byte[] { 0, 0, 0, 0, 0, 0, 0xF0, 0x7F }];
        yield return [0xFFF0000000000000UL, new byte[] { 0, 0, 0, 0, 0, 0, 0xF0, 0xFF }];
        yield return [0x7FF8000000000001UL, new byte[] { 1, 0, 0, 0, 0, 0, 0xF8, 0x7F }];
        yield return [0x7FF123456789ABCDUL, new byte[] { 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0xF1, 0x7F }];
        yield return [0xFFFABCDE12345678UL, new byte[] { 0x78, 0x56, 0x34, 0x12, 0xDE, 0xBC, 0xFA, 0xFF }];
    }

    [Theory]
    [MemberData(nameof(DoublePatterns))]
    public void Double_PreservesEverySpecifiedBitPattern(ulong bits, byte[] expected) {
        var sink = new ArrayBufferWriter<byte>();
        new BareValueWriter(sink).WriteDoubleLE(BitConverter.UInt64BitsToDouble(bits));
        Assert.Equal(expected, sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(expected);
        Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(reader.ReadDoubleLE()));
    }

    [Fact]
    public void EveryFixedReader_TruncationPreservesExistingCursor() {
        int[] widths = [1, 1, 1, 2, 2, 4, 4, 8, 8, 2, 2, 4, 8];
        for (int kind = 0; kind < widths.Length; kind++) {
            for (int length = 0; length < widths[kind]; length++) {
                byte[] source = new byte[length + 1];
                source[0] = 0xAA;
                var reader = new BareValueReader(source);
                reader.ReadByte();
                Exception? failure = null;
                try {
                    ReadScalar(ref reader, kind);
                }
                catch (Exception error) {
                    failure = error;
                }

                Assert.IsType<EndOfStreamException>(failure);
                Assert.Equal(1, reader.ConsumedCount);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(127)]
    [InlineData(255)]
    public void Boolean_RejectsOtherBitsWithoutAdvancing(int value) {
        var reader = new BareValueReader(new byte[] { 0xAA, (byte)value });
        reader.ReadByte();
        Exception? failure = null;
        try {
            reader.ReadBoolean();
        }
        catch (Exception error) {
            failure = error;
        }

        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(1, reader.ConsumedCount);
    }

    private static void ReadScalar(ref BareValueReader reader, int kind) {
        switch (kind) {
            case 0: reader.ReadByte(); break;
            case 1: reader.ReadSByte(); break;
            case 2: reader.ReadBoolean(); break;
            case 3: reader.ReadUInt16LE(); break;
            case 4: reader.ReadInt16LE(); break;
            case 5: reader.ReadUInt32LE(); break;
            case 6: reader.ReadInt32LE(); break;
            case 7: reader.ReadUInt64LE(); break;
            case 8: reader.ReadInt64LE(); break;
            case 9: reader.ReadCharLE(); break;
            case 10: reader.ReadHalfLE(); break;
            case 11: reader.ReadSingleLE(); break;
            case 12: reader.ReadDoubleLE(); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
