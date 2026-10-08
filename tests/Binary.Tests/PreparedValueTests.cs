using System.Buffers;
using System.IO.Compression;
using Xunit;

namespace Atelia.Binary.Tests;

public class PreparedValueTests {
    [Theory]
    [InlineData(ValueCompression.None)]
    [InlineData(ValueCompression.Brotli)]
    public void NullNeverCompressesAndHasExactBudget(ValueCompression compression) {
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledString(null, compression);
        Assert.Same(ControlledValueEncodingPlan.Null, plan);
        Assert.Equal(1L, plan.EncodedLength);
        Assert.Equal(new byte[] { 0 }, ControlledTestHelpers.Write(plan));
    }

    [Fact]
    public void UnknownCompressionRejectsEvenNullInput() {
        Assert.Throws<ArgumentOutOfRangeException>(() => BareValueEncoding.PrepareControlledString(null, (ValueCompression)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BareValueEncoding.PrepareControlledString("A", (ValueCompression)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BareValueEncoding.PrepareControlledBytes([], (ValueCompression)2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16)]
    public void UnprofitableCompressionFallsBackToExactRawEncoding(int length) {
        byte[] value = Enumerable.Range(0, length).Select(i => unchecked((byte)(i * 73 + 17))).ToArray();
        byte[] raw = ControlledTestHelpers.Write(BareValueEncoding.PrepareControlledBytes(value));
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledBytes(value, ValueCompression.Brotli);
        byte[] selected = ControlledTestHelpers.Write(plan);
        Assert.Equal(raw, selected);
        Assert.Equal(1, selected[0]);
        Assert.Equal((long)selected.Length, plan.EncodedLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareSnapshotsBytes_AndRepeatedWriteDoesNotDependOnMutableSource(bool compressed) {
        byte[] source = compressed ? new byte[20000] : new byte[] { 0xAA, 0xBB };
        byte[] expected = source.ToArray();
        // The small input exercises owned Raw fallback after explicitly requesting Brotli.
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledBytes(source, ValueCompression.Brotli);
        Array.Fill(source, (byte)0xCC);
        byte[] first = ControlledTestHelpers.Write(plan);
        byte[] second = ControlledTestHelpers.Write(plan);
        Assert.Equal(first, second);
        Assert.Equal(compressed ? 2 : 1, first[0]);
        Assert.Equal((long)first.Length, plan.EncodedLength);
        var reader = new BareValueReader(first);
        Assert.Equal(expected, reader.ReadControlledBytes());
        Assert.True(reader.End);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("中")]
    [InlineData("\uFEFF")]
    public void ControlledStringPreparation_UsesTheOrdinaryLosslessInnerChoice(string value) {
        var plainSink = new ArrayBufferWriter<byte>();
        new BareValueWriter(plainSink).WriteString(value);
        byte[] wire = ControlledTestHelpers.Write(BareValueEncoding.PrepareControlledString(value));
        Assert.Equal(new byte[] { 1 }.Concat(plainSink.WrittenSpan.ToArray()).ToArray(), wire);
    }

    [Fact]
    public void BrotliPreparation_PreservesRuntimeLoneSurrogatesAndLeadingBomCodeUnit() {
        // Construct malformed UTF-16 at runtime, avoiding attribute metadata's UTF-8 replacement.
        string value = "\uFEFF" + new string('\uD800', 20000);
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledString(value, ValueCompression.Brotli);
        byte[] wire = ControlledTestHelpers.Write(plan);
        Assert.Equal(2, wire[0]);
        Assert.Equal((long)wire.Length, plan.EncodedLength);
        var reader = new BareValueReader(wire);
        Assert.Equal(value, reader.ReadControlledString());
        Assert.True(reader.End);
    }

    [Fact]
    public void CompressionDecision_ComparesCompleteEnvelopeIncludingBothVarintHeaders() {
        // BCL independently supplies a candidate; assertions concern the public complete-size
        // decision, not exact compressed bytes (which are not promised across runtime versions).
        foreach (int length in new[] { 12 }.Concat(Enumerable.Range(0, 54).Select(i => 8 + i * 7))) {
            byte[] source = new byte[length];
            // 12 zero bytes provide an exact envelope tie with the initial q3/window22 encoder.
            // Other cases exercise both outcomes and VarUInt32 boundary costs.
            int modulus = length == 12 ? 1 : length / 3 + 1;
            for (int i = 0; i < source.Length; i++) { source[i] = (byte)((i * 37) % modulus); }
            var plainSink = new ArrayBufferWriter<byte>();
            new BareValueWriter(plainSink).WriteBytes(source);
            byte[] inner = plainSink.WrittenSpan.ToArray();
            byte[] candidate = new byte[BrotliEncoder.GetMaxCompressedLength(inner.Length)];
            Assert.True(BrotliEncoder.TryCompress(inner, candidate, out int candidateLength, quality: 3, window: 22));
            long candidateSize = 1L + IndependentVarintLength(candidateLength)
                + IndependentVarintLength(inner.Length) + candidateLength;
            long rawSize = 1L + inner.Length;
            ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledBytes(source, ValueCompression.Brotli);
            byte[] wire = ControlledTestHelpers.Write(plan);
            Assert.Equal(candidateSize < rawSize ? 2 : 1, wire[0]);
            Assert.Equal(Math.Min(candidateSize, rawSize), plan.EncodedLength);
            var reader = new BareValueReader(wire);
            Assert.Equal(source, reader.ReadControlledBytes());
        }
    }

    [Fact]
    public void PreparedWrite_ChunksOwnedBodyAndPreservesSinkFailures() {
        byte[] source = Enumerable.Range(0, 70000).Select(i => unchecked((byte)(i * 23))).ToArray();
        ControlledValueEncodingPlan plan = BareValueEncoding.PrepareControlledBytes(source);
        var sink = new NoncontiguousControlledSink();
        new BareValueWriter(sink).WritePreparedValue(plan);
        Assert.All(sink.Hints, hint => Assert.InRange(hint, 1, 16 * 1024));
        Assert.Equal(plan.EncodedLength, (long)sink.Bytes.Count);
        var reader = new BareValueReader(sink.Bytes.ToArray());
        Assert.Equal(source, reader.ReadControlledBytes());

        var failure = new IOException("controlled sink failure");
        var failing = new NoncontiguousControlledSink(failure);
        Assert.Same(failure, Assert.Throws<IOException>(() => new BareValueWriter(failing).WritePreparedValue(plan)));
        Assert.Equal(new byte[] { 1 }, failing.Bytes.ToArray()); // no rollback after the first successful Advance.
    }

    [Fact]
    public void NullPlanAndDefaultWriterRejectBeforeSinkInteraction() {
        var sink = new NoncontiguousControlledSink();
        var writer = new BareValueWriter(sink);
        Assert.Throws<ArgumentNullException>(() => writer.WritePreparedValue(null!));
        Assert.Empty(sink.Hints);
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WritePreparedValue(ControlledValueEncodingPlan.Null));
        Assert.Throws<InvalidOperationException>(() => default(BareValueWriter).WritePreparedValue(
            BareValueEncoding.PrepareControlledBytes([])));
    }

    private static int IndependentVarintLength(int value) {
        int bytes = 1;
        while (value >= 128) { value >>= 7; bytes++; }
        return bytes;
    }

    private sealed class NoncontiguousControlledSink(Exception? secondRequestFailure = null) : IBufferWriter<byte> {
        private byte[] _block = [];
        public List<int> Hints { get; } = [];
        public List<byte> Bytes { get; } = [];
        public void Advance(int count) => Bytes.AddRange(_block.AsSpan(0, count).ToArray());
        public Memory<byte> GetMemory(int sizeHint = 0) {
            Hints.Add(sizeHint);
            if (secondRequestFailure is not null && Hints.Count == 2) { throw secondRequestFailure; }
            _block = new byte[Math.Max(1, sizeHint) + 7];
            return _block;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
