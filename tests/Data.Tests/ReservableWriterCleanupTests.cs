using System.Buffers;
using Xunit;

namespace Atelia.Data.Tests;

public class ReservableWriterCleanupTests {
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Cleanup_EarlyReturnsAcceptThenThrow_AttemptsEveryLeaseAndClosesState(
        bool useSink,
        bool disposeWriter,
        bool failTwoReturns
    ) {
        var pool = new CleanupPool(failTwoReturns ? [1, 2] : [1]);
        var output = new TestHelpers.CollectingWriter();
        var options = new ChunkedReservableWriterOptions {
            MinChunkSize = 1024,
            MaxChunkSize = 1024,
            Pool = pool,
        };
        IReservableBufferWriter writer = useSink
            ? new SinkReservableWriter(output, options)
            : new ChunkedReservableWriter(output, options);
        var disposable = (IDisposable)writer;
        try {
            // Three separate leases, two reservations, and a live ordinary borrow.
            // Returning the first lease fails while later leases still need cleanup.
            writer.ReserveSpan(1024, out int firstToken).Fill(0x11);
            writer.ReserveSpan(1024, out int secondToken).Fill(0x22);
            writer.GetMemory(1024).Span.Fill(0x33);
            Assert.Equal(3, pool.RentCount);

            Exception? failure = Record.Exception(() => {
                if (disposeWriter) { disposable.Dispose(); }
                else { Reset(writer); }
            });

            Assert.Equal(new[] { 1, 2, 3 }, pool.ReturnedLeaseIds);
            Assert.Equal(0, pool.OutstandingCount);
            Assert.Empty(output.Data());
            Assert.Equal(0, writer.Length);
            Assert.Equal(failTwoReturns ? 2 : 1, pool.ReportedFailures.Count);
            Assert.Same(pool.ReportedFailures[0], failure);
            Assert.Contains("CleanupPool.Return", failure!.StackTrace);
            if (writer is SinkReservableWriter sink) {
                Assert.Equal(0, sink.PendingReservationCount);
                Assert.Equal(0, sink.PushedLength);
                Assert.Equal(0, sink.PendingLength);
                Assert.True(sink.IsIdle);
            }
            else {
                var chunked = (ChunkedReservableWriter)writer;
                Assert.Equal(0, chunked.PendingReservationCount);
                Assert.Equal(0, chunked.FlushedLength);
                Assert.Equal(0, chunked.PendingLength);
                Assert.True(chunked.IsPassthrough);
            }

            if (disposeWriter) {
                // A throwing Dispose must still finish its lifecycle transition.
                Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
                Assert.Throws<ObjectDisposedException>(() => writer.GetMemory(1));
                Assert.Throws<ObjectDisposedException>(() => writer.ReserveSpan(1, out _));
                Assert.Throws<ObjectDisposedException>(() => writer.Commit(firstToken));
                Assert.Throws<ObjectDisposedException>(() => Reset(writer));
            }
            else {
                Assert.False(writer.TryGetReservedSpan(firstToken, out _));
                Assert.False(writer.TryGetReservedSpan(secondToken, out _));
                Assert.Throws<InvalidOperationException>(() => writer.Commit(firstToken));
                // No continued-writing guarantee after failed Reset: close the writer only.
            }

            disposable.Dispose();
            disposable.Dispose();
            Assert.Equal(new[] { 1, 2, 3 }, pool.ReturnedLeaseIds);
            Assert.Equal(0, pool.OutstandingCount);
        }
        finally {
            // Keep a failing assertion from masking itself with a second injected failure.
            pool.DisableFailures();
            disposable.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChunkedCleanup_CancellationAcceptsThenThrows_ClearsStateWithoutClosingInner(bool disposeWriter) {
        using var inner = new ThrowingCancellationWriter();
        using var writer = new ChunkedReservableWriter(inner);
        writer.GetSpan(2)[..2].Fill(0x11);
        writer.Advance(2);
        writer.GetSpan(4)[..4].Fill(0x99);

        Exception? failure = Record.Exception(() => {
            if (disposeWriter) { writer.Dispose(); }
            else { writer.Reset(); }
        });

        Assert.Same(inner.Failure, failure);
        Assert.Contains("ThrowingCancellationWriter.Advance", failure!.StackTrace);
        Assert.Equal(0, writer.Length);
        Assert.Equal(0, writer.FlushedLength);
        Assert.Equal(0, writer.PendingLength);
        Assert.Equal(0, writer.PendingReservationCount);
        Assert.True(writer.IsPassthrough);
        Assert.False(inner.HasBorrow);
        Assert.Equal(1, inner.CancellationAttempts);
        Assert.Equal(0, inner.DisposeAttempts);
        Assert.Equal(new byte[] { 0x11, 0x11 }, inner.Data());

        if (disposeWriter) {
            Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(1));
            Assert.Throws<ObjectDisposedException>(() => writer.Reset());
        }
        else {
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(1));
        }

        writer.Dispose();
        writer.Dispose();
        Assert.Equal(1, inner.CancellationAttempts);
        Assert.Equal(0, inner.DisposeAttempts);
        inner.GetMemory(1).Span[0] = 0x22;
        inner.Advance(1);
        Assert.Equal(new byte[] { 0x11, 0x11, 0x22 }, inner.Data());
    }

    private static void Reset(IReservableBufferWriter writer) {
        if (writer is SinkReservableWriter sink) { sink.Reset(); }
        else { ((ChunkedReservableWriter)writer).Reset(); }
    }

    private sealed class CleanupPool(int[] failingLeaseIds) : ArrayPool<byte> {
        private readonly HashSet<int> _failingLeaseIds = [.. failingLeaseIds];
        private readonly Dictionary<byte[], int> _outstanding = new(ReferenceEqualityComparer.Instance);
        private bool _failuresEnabled = true;

        public int RentCount { get; private set; }
        public int OutstandingCount => _outstanding.Count;
        public List<int> ReturnedLeaseIds { get; } = new();
        public List<Exception> ReportedFailures { get; } = new();

        public void DisableFailures() => _failuresEnabled = false;

        public override byte[] Rent(int minimumLength) {
            var buffer = new byte[minimumLength];
            _outstanding.Add(buffer, ++RentCount);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            Assert.True(_outstanding.Remove(array, out int leaseId), "A lease was returned more than once.");
            ReturnedLeaseIds.Add(leaseId);
            array.AsSpan().Fill(0xE7);
            if (_failuresEnabled && _failingLeaseIds.Contains(leaseId)) {
                var failure = new InjectedReturnFailureException(leaseId);
                ReportedFailures.Add(failure);
                throw failure;
            }
        }
    }

    private sealed class InjectedReturnFailureException(int leaseId)
        : Exception($"Accepted lease {leaseId}, then failed.") { }

    private sealed class ThrowingCancellationWriter : IBufferWriter<byte>, IDisposable {
        private readonly ArrayBufferWriter<byte> _output = new();
        private bool _disposed;

        public Exception Failure { get; } = new InvalidOperationException("Cancellation accepted, then failed.");
        public bool HasBorrow { get; private set; }
        public int CancellationAttempts { get; private set; }
        public int DisposeAttempts { get; private set; }

        public byte[] Data() => _output.WrittenSpan.ToArray();

        public Memory<byte> GetMemory(int sizeHint = 0) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasBorrow) { throw new InvalidOperationException("Previous buffer not advanced."); }
            Memory<byte> memory = _output.GetMemory(sizeHint);
            HasBorrow = true;
            return memory;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Advance(int count) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!HasBorrow) { throw new InvalidOperationException("No outstanding buffer."); }
            _output.Advance(count);
            HasBorrow = false;
            if (count == 0) {
                CancellationAttempts++;
                throw Failure;
            }
        }

        public void Dispose() {
            DisposeAttempts++;
            _disposed = true;
        }
    }
}
