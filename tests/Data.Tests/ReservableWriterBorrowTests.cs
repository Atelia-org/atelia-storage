using System.Buffers;
using Xunit;

namespace Atelia.Data.Tests;

public class ReservableWriterBorrowTests {
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void Commit_WithBorrowedBuffer_PreservesLeaseUntilAdvance(
        bool useSink,
        bool borrowMemory,
        bool separateTail,
        bool cancelBorrow
    ) {
        var pool = new TrackingPool();
        var output = new TestHelpers.CollectingWriter();
        var options = new ChunkedReservableWriterOptions {
            MinChunkSize = 1024,
            MaxChunkSize = 1024,
            Pool = pool,
        };
        IReservableBufferWriter writer = useSink
            ? new SinkReservableWriter(output, options)
            : new ChunkedReservableWriter(output, options);
        using var disposable = (IDisposable)writer;

        // Filling the first chunk forces the ordinary borrow into a separate, empty tail.
        int reservationLength = separateTail ? 1024 : 4;
        writer.ReserveSpan(reservationLength, out int token).Fill(0x11);
        Span<byte> borrowed = borrowMemory ? writer.GetMemory(4).Span : writer.GetSpan(4);
        borrowed[..4].Fill(0x22);
        byte[] borrowedBuffer = pool.RentedBuffers[^1];
        int borrowedLength = borrowed.Length;
        Assert.Equal(separateTail ? 2 : 1, pool.RentedBuffers.Count);

        writer.Commit(token);

        var expected = Enumerable.Repeat((byte)0x11, reservationLength).ToList();
        Assert.Equal(expected.ToArray(), output.Data());
        Assert.Equal((long)reservationLength, writer.Length);
        Assert.True(pool.IsRented(borrowedBuffer));
        Assert.Equal(separateTail ? 1 : 0, pool.ReturnCount);
        Assert.Equal(Enumerable.Repeat((byte)0x22, 4).ToArray(), borrowed[..4].ToArray());
        Assert.False(writer.TryGetReservedSpan(token, out _));
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));

        // Commit ends the reservation, but the independent ordinary borrow remains active.
        Assert.Throws<InvalidOperationException>(() => writer.GetSpan(1));
        Assert.Throws<InvalidOperationException>(() => writer.GetMemory(1));
        Assert.Throws<InvalidOperationException>(() => writer.ReserveSpan(1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(borrowedLength + 1));
        Assert.True(pool.IsRented(borrowedBuffer));

        borrowed[..4].Fill(0x33);
        writer.Advance(cancelBorrow ? 0 : 4);
        if (!cancelBorrow) { expected.AddRange(Enumerable.Repeat((byte)0x33, 4)); }

        Assert.Equal(expected.ToArray(), output.Data());
        Assert.Equal((long)expected.Count, writer.Length);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);
        if (writer is ChunkedReservableWriter chunked) {
            Assert.True(chunked.IsPassthrough);
            Assert.Equal(writer.Length, chunked.FlushedLength);
            Assert.Equal(0, chunked.PendingLength);
        }
        else {
            var sink = (SinkReservableWriter)writer;
            Assert.True(sink.IsIdle);
            Assert.Equal(writer.Length, sink.PushedLength);
            Assert.Equal(0, sink.PendingLength);
        }

        // A fresh ordinary write must neither reuse the abandoned borrow nor retain its chunk.
        int rentCountBeforeNextWrite = pool.RentedBuffers.Count;
        writer.GetSpan(3)[..3].Fill(0x44);
        writer.Advance(3);
        expected.AddRange(Enumerable.Repeat((byte)0x44, 3));
        Assert.Equal(expected.ToArray(), output.Data());
        Assert.Equal((long)expected.Count, writer.Length);
        Assert.Equal(rentCountBeforeNextWrite + (useSink ? 1 : 0), pool.RentedBuffers.Count);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);

        disposable.Dispose();
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PassthroughAdvanceZero_CancelsInnerBorrow(bool useInnerSink, bool borrowMemory) {
        var pool = new TrackingPool();
        var output = new TestHelpers.CollectingWriter();
        var options = new ChunkedReservableWriterOptions {
            MinChunkSize = 1024,
            MaxChunkSize = 1024,
            Pool = pool,
        };
        IReservableBufferWriter inner = useInnerSink
            ? new SinkReservableWriter(output, options)
            : new ChunkedReservableWriter(output, options);
        using var innerDisposable = (IDisposable)inner;
        using var outer = new ChunkedReservableWriter(inner, options);

        Span<byte> cancelled = borrowMemory ? outer.GetMemory(4).Span : outer.GetSpan(4);
        cancelled[..4].Fill(0x99);
        outer.Advance(0);

        Assert.Empty(output.Data());
        Assert.Equal(0, outer.Length);
        Assert.Equal(0, inner.Length);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Equal(useInnerSink ? 1 : 0, pool.RentedBuffers.Count);
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);

        // A strict inner writer must accept the next request after passthrough cancellation.
        Span<byte> next = borrowMemory ? outer.GetMemory(4).Span : outer.GetSpan(4);
        next[..4].Fill(0x45);
        outer.Advance(4);

        Assert.Equal(Enumerable.Repeat((byte)0x45, 4).ToArray(), output.Data());
        Assert.Equal(4, outer.Length);
        Assert.Equal(4, inner.Length);
        Assert.True(outer.IsPassthrough);
        Assert.Equal(useInnerSink ? 2 : 0, pool.RentedBuffers.Count);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);

        outer.Dispose();
        innerDisposable.Dispose();
        Assert.Equal(pool.RentedBuffers.Count, pool.ReturnCount);
    }

    private sealed class TrackingPool : ArrayPool<byte> {
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);
        public List<byte[]> RentedBuffers { get; } = new();
        public int ReturnCount { get; private set; }
        public int OutstandingCount => _outstanding.Count;

        public bool IsRented(byte[] buffer) => _outstanding.Contains(buffer);

        public override byte[] Rent(int minimumLength) {
            var buffer = new byte[minimumLength];
            RentedBuffers.Add(buffer);
            Assert.True(_outstanding.Add(buffer));
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            Assert.True(_outstanding.Remove(array), "Buffer returned without an outstanding lease.");
            ReturnCount++;
            array.AsSpan().Fill(0xE7);
        }
    }
}
