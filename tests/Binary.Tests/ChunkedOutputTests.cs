using System.Text;
using Xunit;

namespace Atelia.Binary.Tests;

public class ChunkedOutputTests {
    private const int ChosenSizeHintBound = 16 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void LargeRawAndPrefixedBytesUseIndependentBoundedBuffers(int extraCapacity) {
        byte[] payload = Enumerable.Range(0, ChosenSizeHintBound * 3 + 17).Select(i => unchecked((byte)i)).ToArray();
        var rawSink = new StringRecordingSink(extraCapacity);
        new BareValueWriter(rawSink).WriteRawBytes(payload);
        Assert.Equal(payload, rawSink.WrittenBytes);
        AssertBounded(rawSink);
        var prefixedSink = new StringRecordingSink(extraCapacity);
        new BareValueWriter(prefixedSink).WriteBytes(payload);
        byte[] expected = StringTestHelpers.Prefix((uint)payload.Length, payload);
        Assert.Equal(expected, prefixedSink.WrittenBytes);
        Assert.Equal((long)expected.Length, BareValueEncoding.MeasureBytes(payload.Length));
        AssertBounded(prefixedSink);
    }

    [Theory]
    [InlineData(16380)]
    [InlineData(16381)]
    [InlineData(16382)]
    [InlineData(16383)]
    [InlineData(16384)]
    public void Utf8SurrogatePairAtChunkBoundaryMatchesCompleteStrictEncoding(int prefixLength) {
        string value = new string('A', prefixLength) + "😀" + new string('B', 18000);
        byte[] payload = new UTF8Encoding(false, true).GetBytes(value);
        byte[] expected = StringTestHelpers.Prefix((uint)(payload.Length * 2 + 1), payload);
        foreach (int extraCapacity in new[] { 0, 31 }) {
            var sink = new StringRecordingSink(extraCapacity);
            Assert.Equal(expected, StringTestHelpers.EncodeString(value, sink));
            AssertBounded(sink);
            var reader = new BareValueReader(sink.WrittenBytes);
            Assert.Equal(value, reader.ReadString());
            reader.EnsureFullyConsumed();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Utf16ChunksPreserveLoneSurrogatesAndEveryCodeUnit(int extraCapacity) {
        string value = new string('中', 8191) + "\uD800A\uDC00" + new string('文', 17000);
        byte[] payload = new byte[value.Length * 2];
        for (int i = 0; i < value.Length; i++) {
            payload[i * 2] = unchecked((byte)value[i]);
            payload[i * 2 + 1] = (byte)(value[i] >> 8);
        }
        byte[] expected = StringTestHelpers.Prefix((uint)payload.Length, payload);
        var sink = new StringRecordingSink(extraCapacity);
        Assert.Equal(expected, StringTestHelpers.EncodeString(value, sink));
        AssertBounded(sink);
        var reader = new BareValueReader(expected);
        Assert.Equal(value, reader.ReadString());
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void GetSpanFaultPreservesOriginalExceptionAndAlreadyAdvancedPrefix() {
        var sink = new StringRecordingSink { ThrowOnGetSpanCall = 3 };
        var writer = new BareValueWriter(sink);
        string value = new('A', ChosenSizeHintBound * 2);
        StringSinkException failure = Assert.Throws<StringSinkException>(() => writer.WriteString(value));
        Assert.Same(sink.Failure, failure);
        byte[] complete = StringTestHelpers.Prefix((uint)(value.Length * 2 + 1), Encoding.ASCII.GetBytes(value));
        Assert.Equal(complete.AsSpan(0, 3 + ChosenSizeHintBound).ToArray(), sink.WrittenBytes);
        Assert.Equal(2, sink.AdvanceCalls);
    }

    [Fact]
    public void AdvanceFaultIsNotWrappedOrPresentedAsRollback() {
        var sink = new StringRecordingSink { ThrowOnAdvanceCall = 2 };
        var writer = new BareValueWriter(sink);
        StringSinkException failure = Assert.Throws<StringSinkException>(() => writer.WriteString(new string('A', ChosenSizeHintBound + 1)));
        Assert.Same(sink.Failure, failure);
        Assert.Equal(new byte[] { 0x83, 0x80, 0x02 }, sink.WrittenBytes);
        Assert.Equal(2, sink.AdvanceCalls);
    }

    private static void AssertBounded(StringRecordingSink sink) {
        Assert.True(sink.SizeHints.Count > 1);
        Assert.All(sink.SizeHints, hint => Assert.InRange(hint, 1, ChosenSizeHintBound));
    }
}
