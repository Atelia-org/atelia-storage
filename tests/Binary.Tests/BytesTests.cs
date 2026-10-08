using Xunit;

namespace Atelia.Binary.Tests;

public class BytesTests {
    [Fact]
    public void RawAndLengthPrefixedGoldensHaveDistinctBoundaries() {
        byte[] payload = [0x00, 0x80, 0xFF, 0x41];
        var rawSink = new StringRecordingSink();
        new BareValueWriter(rawSink).WriteRawBytes(payload);
        Assert.Equal(payload, rawSink.WrittenBytes);
        var prefixedSink = new StringRecordingSink();
        new BareValueWriter(prefixedSink).WriteBytes(payload);
        Assert.Equal(new byte[] { 0x04, 0x00, 0x80, 0xFF, 0x41 }, prefixedSink.WrittenBytes);
        var emptySink = new StringRecordingSink();
        new BareValueWriter(emptySink).WriteBytes([]);
        Assert.Equal(new byte[] { 0x00 }, emptySink.WrittenBytes);
        var rawReader = new BareValueReader(payload);
        Assert.Equal(payload, rawReader.ReadRawBytes(4).ToArray());
        Assert.True(rawReader.End);
        var prefixedReader = new BareValueReader(prefixedSink.WrittenBytes);
        Assert.Equal(payload, prefixedReader.ReadBytes().ToArray());
        Assert.True(prefixedReader.End);
    }

    [Fact]
    public void ReadBytesBorrowsSourceAndConsumesActualNonminimalHeader() {
        byte[] source = [0x82, 0x00, 0xAA, 0xBB, 0x42];
        var reader = new BareValueReader(source);
        ReadOnlySpan<byte> bytes = reader.ReadBytes(2);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, bytes.ToArray());
        Assert.Equal(4, reader.ConsumedCount);
        Assert.Equal(3L, BareValueEncoding.MeasureBytes(bytes.Length));
        source[2] = 0xCC;
        Assert.Equal(0xCC, bytes[0]);
        Assert.Equal(0x42, reader.ReadByte());
    }

    [Fact]
    public void ByteLengthsUseLongExactMeasuresWithoutAllocation() {
        Assert.Equal(1L, BareValueEncoding.MeasureBytes(0));
        Assert.Equal(128L, BareValueEncoding.MeasureBytes(127));
        Assert.Equal(130L, BareValueEncoding.MeasureBytes(128));
        Assert.Equal(2147483652L, BareValueEncoding.MeasureBytes(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => BareValueEncoding.MeasureBytes(-1));
    }

    [Fact]
    public void EmptyAllowsZeroLimitAndAllFailuresPreserveCursor() {
        var empty = new BareValueReader([0x80, 0x00]);
        Assert.Empty(empty.ReadBytes(0).ToArray());
        Assert.Equal(2, empty.ConsumedCount);
        AssertReadFailure<ArgumentOutOfRangeException>([], -1);
        AssertReadFailure<InvalidDataException>([0x01, 0xAA], 0);
        AssertReadFailure<InvalidDataException>([0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);
        AssertReadFailure<EndOfStreamException>([0xFF, 0xFF, 0xFF, 0xFF, 0x07]);
        byte[] encoded = [0x03, 0x00, 0x80, 0xFF];
        for (int length = 0; length < encoded.Length; length++) {
            AssertReadFailure<EndOfStreamException>(encoded.AsSpan(0, length).ToArray());
        }
    }

    [Fact]
    public void DefaultWriterRejectsEvenEmptyBytes() {
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WriteBytes([]));
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WriteRawBytes([]));
        var sink = new StringRecordingSink();
        new BareValueWriter(sink).WriteRawBytes([]);
        Assert.Empty(sink.SizeHints);
    }

    private static void AssertReadFailure<TException>(byte[] encoded, int limit = int.MaxValue) where TException : Exception {
        byte[] source = [0x42, .. encoded];
        var reader = new BareValueReader(source);
        reader.ReadByte();
        Exception? failure = null;
        try {
            reader.ReadBytes(limit);
        }
        catch (Exception exception) {
            failure = exception;
        }
        Assert.IsType<TException>(failure);
        Assert.Equal(1, reader.ConsumedCount);
        Assert.Equal(encoded.Length, reader.RemainingCount);
    }
}
