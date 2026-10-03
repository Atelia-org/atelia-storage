using System.Buffers;
using Xunit;

namespace Atelia.Data.Tests;

public class ReservableWriterPoolReturnTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_ReturnAcceptedThenThrows_DisposeDoesNotRepeatReturn(bool useSink) {
        var pool = new AcceptThenThrowPool(failingReturnAttempt: 2);
        var output = new TestHelpers.CollectingWriter();
        var writer = CreateWriter(useSink, output, pool);
        using var disposable = (IDisposable)writer;
        writer.ReserveSpan(4, out _).Fill(0x5A);
        writer.GetSpan(1024)[..1024].Fill(0x5A);
        writer.Advance(1024);
        Assert.Equal(2, pool.RentCount);
        Assert.Empty(output.Data());

        var failure = Assert.Throws<InjectedReturnFailureException>(() => Reset(writer));
        Assert.Same(pool.Failure, failure);
        Assert.Equal(new[] { 1, 2 }, pool.ReturnLeaseIds);

        // After the resource failure, the caller only closes the writer.
        disposable.Dispose();
        Assert.Equal(new[] { 1, 2 }, pool.ReturnLeaseIds);
        Assert.Empty(output.Data());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_ReturnAcceptedThenThrows_DisposeDoesNotRepeatReturn(bool useSink) {
        var pool = new AcceptThenThrowPool(failingReturnAttempt: 2);
        var output = new TestHelpers.CollectingWriter();
        var writer = CreateWriter(useSink, output, pool);
        using var disposable = (IDisposable)writer;
        writer.ReserveSpan(4, out int token).Fill(0x5A);
        writer.GetSpan(1024)[..1024].Fill(0x5A);
        writer.Advance(1024);
        Assert.Equal(2, pool.RentCount);
        Assert.Empty(output.Data());

        var failure = Assert.Throws<InjectedReturnFailureException>(() => writer.Commit(token));
        Assert.Same(pool.Failure, failure);
        Assert.Equal(new[] { 1, 2 }, pool.ReturnLeaseIds);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 1028).ToArray(), output.Data());

        // Successful output precedes the recycle failure; Dispose must not retry Return.
        disposable.Dispose();
        Assert.Equal(new[] { 1, 2 }, pool.ReturnLeaseIds);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 1028).ToArray(), output.Data());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_RerentsSameArray_CanReturnTheNewLease(bool useSink) {
        var pool = new AcceptThenThrowPool();
        var output = new TestHelpers.CollectingWriter();
        var writer = CreateWriter(useSink, output, pool);
        using var disposable = (IDisposable)writer;
        writer.ReserveSpan(4, out _).Fill(0x11);
        Reset(writer);
        Assert.Equal(new[] { 1 }, pool.ReturnLeaseIds);

        writer.ReserveSpan(4, out _).Fill(0x22);
        Assert.Equal(2, pool.RentCount);
        Assert.Same(pool.RentedBuffers[0], pool.RentedBuffers[1]);
        Reset(writer);

        disposable.Dispose();
        Assert.Equal(new[] { 1, 2 }, pool.ReturnLeaseIds);
        Assert.Empty(output.Data());
    }

    private static IReservableBufferWriter CreateWriter(
        bool useSink,
        TestHelpers.CollectingWriter output,
        ArrayPool<byte> pool
    ) {
        var options = new ChunkedReservableWriterOptions {
            MinChunkSize = 1024,
            MaxChunkSize = 1024,
            Pool = pool,
        };
        return useSink
            ? new SinkReservableWriter(output, options)
            : new ChunkedReservableWriter(output, options);
    }

    private static void Reset(IReservableBufferWriter writer) {
        if (writer is SinkReservableWriter sinkWriter) { sinkWriter.Reset(); }
        else { ((ChunkedReservableWriter)writer).Reset(); }
    }

    private sealed class InjectedReturnFailureException : Exception { }

    private sealed class AcceptThenThrowPool(int? failingReturnAttempt = null) : ArrayPool<byte> {
        private readonly Queue<byte[]> _available = new();
        private readonly Dictionary<byte[], int> _leaseIds = new(ReferenceEqualityComparer.Instance);
        public List<byte[]> RentedBuffers { get; } = new();
        public List<int> ReturnLeaseIds { get; } = new();
        public int RentCount => RentedBuffers.Count;
        public InjectedReturnFailureException Failure { get; } = new();

        public override byte[] Rent(int minimumLength) {
            byte[] array = _available.TryDequeue(out var reusable) ? reusable : new byte[minimumLength];
            Assert.True(array.Length >= minimumLength);
            RentedBuffers.Add(array);
            _leaseIds[array] = RentCount;
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            ReturnLeaseIds.Add(_leaseIds[array]);
            if (clearArray) { array.AsSpan().Clear(); }
            _available.Enqueue(array); // The pool accepts ownership before the injected failure.
            if (ReturnLeaseIds.Count == failingReturnAttempt) { throw Failure; }
        }
    }
}
