using System.Buffers;
using Xunit;

namespace Atelia.Binary.Tests;

public class CoreContractTests {
    [Fact]
    public void DefaultReader_IsEmptyAndAcceptsZeroRawLength() {
        BareValueReader reader = default;
        Assert.True(reader.End);
        Assert.Equal(0, reader.ConsumedCount);
        Assert.Equal(0, reader.RemainingCount);
        Assert.True(reader.ReadRawBytes(0).IsEmpty);
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void RawBytes_BorrowInputAndTrackActualConsumption() {
        byte[] source = [0x10, 0x20, 0x30, 0x40];
        var reader = new BareValueReader(source);
        Assert.Equal((byte)0x10, reader.ReadByte());
        ReadOnlySpan<byte> borrowed = reader.ReadRawBytes(2);
        Assert.Equal(new byte[] { 0x20, 0x30 }, borrowed.ToArray());
        Assert.Equal(3, reader.ConsumedCount);
        Assert.Equal(1, reader.RemainingCount);
        source[1] = 0x99;
        Assert.Equal((byte)0x99, borrowed[0]);
        Assert.False(reader.End);
        Assert.Equal((byte)0x40, reader.ReadByte());
        Assert.True(reader.End);
    }

    [Fact]
    public void RawLengthAndTailErrors_DoNotAdvance() {
        var reader = new BareValueReader(new byte[] { 0xAA, 0xBB });
        reader.ReadByte();
        Exception? negative = null;
        try {
            reader.ReadRawBytes(-1);
        }
        catch (Exception error) {
            negative = error;
        }

        Assert.IsType<ArgumentOutOfRangeException>(negative);
        Assert.Equal(1, reader.ConsumedCount);
        Exception? truncated = null;
        try {
            reader.ReadRawBytes(int.MaxValue);
        }
        catch (Exception error) {
            truncated = error;
        }

        Assert.IsType<EndOfStreamException>(truncated);
        Assert.Equal(1, reader.ConsumedCount);
        Exception? trailing = null;
        try {
            reader.EnsureFullyConsumed();
        }
        catch (Exception error) {
            trailing = error;
        }

        Assert.IsType<InvalidDataException>(trailing);
        Assert.Equal(1, reader.ConsumedCount);
    }

    [Fact]
    public void CompositeRead_UsesCopyWhenAtomicityIsNeeded() {
        // Presence is read successfully, but the present Single is truncated.
        var reader = new BareValueReader(new byte[] { 1, 0, 0 });
        var trial = reader;
        Exception? failure = null;
        try {
            if (trial.ReadBoolean()) {
                trial.ReadSingleLE();
            }

            reader = trial;
        }
        catch (Exception error) {
            failure = error;
        }

        Assert.IsType<EndOfStreamException>(failure);
        Assert.Equal(1, trial.ConsumedCount);
        Assert.Equal(0, reader.ConsumedCount);
    }

    [Fact]
    public void NullableScalarSchema_CanPreserveFloatingBits() {
        var sink = new ArrayBufferWriter<byte>();
        var writer = new BareValueWriter(sink);
        writer.WriteBoolean(false);
        writer.WriteBoolean(true);
        writer.WriteSingleLE(BitConverter.UInt32BitsToSingle(0x80000000));
        writer.WriteBoolean(true);
        writer.WriteSingleLE(BitConverter.UInt32BitsToSingle(0x7FC01234));
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0x80, 1, 0x34, 0x12, 0xC0, 0x7F }, sink.WrittenSpan.ToArray());
        var reader = new BareValueReader(sink.WrittenSpan);
        Assert.False(reader.ReadBoolean());
        Assert.True(reader.ReadBoolean());
        Assert.Equal(0x80000000U, BitConverter.SingleToUInt32Bits(reader.ReadSingleLE()));
        Assert.True(reader.ReadBoolean());
        Assert.Equal(0x7FC01234U, BitConverter.SingleToUInt32Bits(reader.ReadSingleLE()));
        reader.EnsureFullyConsumed();
    }

    [Fact]
    public void DefaultWriter_RejectsEveryCoreWriteIncludingEmptyRaw() {
        Action<BareValueWriter>[] operations = [
            writer => writer.WriteRawBytes([]),
            writer => writer.WriteByte(0),
            writer => writer.WriteSByte(0),
            writer => writer.WriteBoolean(false),
            writer => writer.WriteUInt16LE(0),
            writer => writer.WriteInt16LE(0),
            writer => writer.WriteUInt32LE(0),
            writer => writer.WriteInt32LE(0),
            writer => writer.WriteUInt64LE(0),
            writer => writer.WriteInt64LE(0),
            writer => writer.WriteCharLE('\0'),
            writer => writer.WriteHalfLE((Half)0),
            writer => writer.WriteSingleLE(0),
            writer => writer.WriteDoubleLE(0),
            writer => writer.WriteVarUInt16(0),
            writer => writer.WriteVarUInt32(0),
            writer => writer.WriteVarUInt64(0),
            writer => writer.WriteVarInt16(0),
            writer => writer.WriteVarInt32(0),
            writer => writer.WriteVarInt64(0)
        ];
        foreach (Action<BareValueWriter> operation in operations) {
            Assert.Throws<InvalidOperationException>(() => operation(default));
        }

        Assert.Throws<ArgumentNullException>(() => new BareValueWriter(null!));
    }

    [Fact]
    public void HealthyEmptyRaw_DoesNotCallSink() {
        var sink = new ChunkSink { FailGetSpanAt = 1 };
        new BareValueWriter(sink).WriteRawBytes([]);
        Assert.Empty(sink.Hints);
        Assert.Equal(0, sink.AdvanceCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void RawWriter_UsesBoundedNoncontiguousChunksEvenWhenSinkReturnsExtraSpace(int extraSpace) {
        byte[] source = Enumerable.Range(0, 50_003).Select(index => unchecked((byte)index)).ToArray();
        var sink = new ChunkSink { ExtraSpace = extraSpace };
        new BareValueWriter(sink).WriteRawBytes(source);
        Assert.Equal(source, sink.Bytes.ToArray());
        Assert.Equal(new[] { 16384, 16384, 16384, 851 }, sink.Hints);
        Assert.All(sink.Hints, hint => Assert.InRange(hint, 1, 16384));
        Assert.Equal(4, sink.AdvanceCount);
    }

    [Fact]
    public void WriterCopies_AreFacadesOverTheSameSink() {
        var sink = new ChunkSink();
        var writer = new BareValueWriter(sink);
        var copy = writer;
        writer.WriteByte(0xAA);
        copy.WriteByte(0xBB);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, sink.Bytes.ToArray());
    }

    [Fact]
    public void SinkGetSpanFailure_IsPreservedAfterCompletedPrefix() {
        byte[] source = Enumerable.Range(0, 40_000).Select(index => unchecked((byte)index)).ToArray();
        var failure = new IOException("probe GetSpan failure");
        var sink = new ChunkSink { FailGetSpanAt = 3, Failure = failure };
        Exception result = Assert.Throws<IOException>(() => new BareValueWriter(sink).WriteRawBytes(source));
        Assert.Same(failure, result);
        Assert.Equal(source.AsSpan(0, 32768).ToArray(), sink.Bytes.ToArray());
        Assert.Equal(2, sink.AdvanceCount);
    }

    [Fact]
    public void SinkAdvanceFailure_IsPreservedAndDoesNotInventRollback() {
        var failure = new IOException("probe Advance failure");
        var sink = new ChunkSink { FailAdvanceAt = 1, Failure = failure };
        Exception result = Assert.Throws<IOException>(() => new BareValueWriter(sink).WriteUInt32LE(0x12345678));
        Assert.Same(failure, result);
        // This sink commits bytes before throwing: the facade promises no rollback.
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, sink.Bytes.ToArray());
    }

    private sealed class ChunkSink : IBufferWriter<byte> {
        private byte[]? _current;
        public List<int> Hints { get; } = [];
        public List<byte> Bytes { get; } = [];
        public int ExtraSpace { get; init; }
        public int FailGetSpanAt { get; init; }
        public int FailAdvanceAt { get; init; }
        public Exception Failure { get; init; } = new IOException("probe failure");
        public int AdvanceCount { get; private set; }

        public void Advance(int count) {
            Assert.NotNull(_current);
            Assert.InRange(count, 0, _current.Length);
            Bytes.AddRange(_current.AsSpan(0, count).ToArray());
            Array.Fill(_current, (byte)0xCD);
            _current = null;
            AdvanceCount++;
            if (AdvanceCount == FailAdvanceAt) {
                throw Failure;
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => GetBuffer(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => GetBuffer(sizeHint);

        private byte[] GetBuffer(int sizeHint) {
            Hints.Add(sizeHint);
            if (Hints.Count == FailGetSpanAt) {
                throw Failure;
            }

            Assert.Null(_current);
            _current = new byte[Math.Max(sizeHint, 1) + ExtraSpace];
            return _current;
        }
    }
}
