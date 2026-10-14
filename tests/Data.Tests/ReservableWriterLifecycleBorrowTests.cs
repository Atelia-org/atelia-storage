using System.Buffers;
using Xunit;

namespace Atelia.Data.Tests;

public class ReservableWriterLifecycleBorrowTests {
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ResetOrDispose_CancelsPassthroughBorrowWithoutResettingInner(
        bool useInnerSink,
        bool borrowMemory,
        bool disposeOuter
    ) {
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

        outer.GetSpan(2)[..2].Fill(0x11);
        outer.Advance(2);
        Span<byte> abandoned = borrowMemory ? outer.GetMemory(4).Span : outer.GetSpan(4);
        abandoned[..4].Fill(0x99);

        if (disposeOuter) { outer.Dispose(); }
        else { outer.Reset(); }

        Assert.Equal(new byte[] { 0x11, 0x11 }, output.Data());
        Assert.Equal(2, inner.Length);
        Assert.Equal(0, pool.OutstandingCount);

        // The borrowed inner remains usable and retains all previously advanced bytes.
        inner.GetSpan(3)[..3].Fill(0x33);
        inner.Advance(3);
        Assert.Equal(new byte[] { 0x11, 0x11, 0x33, 0x33, 0x33 }, output.Data());
        Assert.Equal(5, inner.Length);
        if (disposeOuter) {
            Assert.Throws<ObjectDisposedException>(() => outer.GetSpan(1));
            Assert.Throws<ObjectDisposedException>(() => outer.GetMemory(1));
        }
        else {
            Assert.Equal(0, outer.Length);
            outer.GetMemory(1).Span[0] = 0x44;
            outer.Advance(1);
            Assert.Equal(new byte[] { 0x11, 0x11, 0x33, 0x33, 0x33, 0x44 }, output.Data());
            Assert.Equal(1, outer.Length);
            Assert.Equal(6, inner.Length);
        }

        outer.Dispose();
        innerDisposable.Dispose();
        Assert.Equal(0, pool.OutstandingCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IdleOrBufferedResetOrDispose_DoesNotCancelOrOwnInner(bool buffered, bool disposeOuter) {
        using var inner = new CountingStrictWriter();
        var pool = new TrackingPool();
        using var outer = new ChunkedReservableWriter(inner, new ChunkedReservableWriterOptions {
            MinChunkSize = 1024,
            MaxChunkSize = 1024,
            Pool = pool,
        });
        outer.GetSpan(2)[..2].Fill(0x11);
        outer.Advance(2);
        if (buffered) {
            outer.ReserveSpan(4, out _).Fill(0x99);
            outer.GetSpan(4)[..4].Fill(0x88);
        }

        if (disposeOuter) { outer.Dispose(); }
        else { outer.Reset(); }

        Assert.Equal(0, inner.CancellationAttempts);
        Assert.Equal(0, inner.ResetCount);
        Assert.Equal(0, inner.DisposeCount);
        Assert.Equal(0, pool.OutstandingCount);
        Assert.Equal(new byte[] { 0x11, 0x11 }, inner.Data());
        inner.GetSpan(2)[..2].Fill(0x22);
        inner.Advance(2);
        Assert.Equal(new byte[] { 0x11, 0x11, 0x22, 0x22 }, inner.Data());

        outer.Dispose();
        Assert.Equal(0, inner.CancellationAttempts);
        Assert.Equal(0, inner.ResetCount);
        Assert.Equal(0, inner.DisposeCount);
    }

    [Theory]
    [InlineData("AdvanceZero")]
    [InlineData("AdvancePositive")]
    [InlineData("Reset")]
    [InlineData("Dispose")]
    public void AdvanceAcceptedThenThrows_DoesNotConsumeOrCancelBorrowAgain(string operation) {
        var failure = new InjectedCancellationException();
        bool positiveAdvance = operation == "AdvancePositive";
        using var inner = new CountingStrictWriter(failure, positiveAdvance ? 4 : 0);
        using var outer = new ChunkedReservableWriter(inner);
        outer.GetSpan(2)[..2].Fill(0x11);
        outer.Advance(2);
        outer.GetSpan(4)[..4].Fill(0x99);

        var actual = Assert.Throws<InjectedCancellationException>(() => {
            switch (operation) {
                case "AdvanceZero": outer.Advance(0); break;
                case "AdvancePositive": outer.Advance(4); break;
                case "Reset": outer.Reset(); break;
                case "Dispose": outer.Dispose(); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        });

        Assert.Same(failure, actual);
        Assert.Equal(positiveAdvance ? 0 : 1, inner.CancellationAttempts);
        Assert.False(inner.HasBorrow);
        byte[] expected = positiveAdvance
            ? new byte[] { 0x11, 0x11, 0x99, 0x99, 0x99, 0x99 }
            : new byte[] { 0x11, 0x11 };
        Assert.Equal(expected, inner.Data());
        int advanceAttempts = inner.AdvanceAttempts;
        Exception? repeatedAdvance = Record.Exception(() => outer.Advance(1));
        Assert.True(repeatedAdvance is ArgumentOutOfRangeException or ObjectDisposedException,
            "The consumed borrow must be rejected by the outer writer before reaching the inner writer.");
        Assert.Equal(advanceAttempts, inner.AdvanceAttempts);

        outer.Dispose();
        outer.Dispose();
        Assert.Equal(positiveAdvance ? 0 : 1, inner.CancellationAttempts);
        Assert.Equal(0, inner.ResetCount);
        Assert.Equal(0, inner.DisposeCount);
        inner.GetMemory(1).Span[0] = 0x33;
        inner.Advance(1);
        Assert.Equal(expected.Concat(new byte[] { 0x33 }).ToArray(), inner.Data());
    }

    private sealed class InjectedCancellationException : Exception { }

    // A strict ordinary writer that can accept cancellation before reporting an injected failure.
    private sealed class CountingStrictWriter(Exception? cancellationFailure = null, int failingCount = 0) : IBufferWriter<byte>, IDisposable {
        private readonly ArrayBufferWriter<byte> _output = new();
        private bool _disposed;
        private bool _failureReported;
        private int _lastLength;
        public bool HasBorrow { get; private set; }
        public int AdvanceAttempts { get; private set; }
        public int CancellationAttempts { get; private set; }
        public int ResetCount { get; private set; }
        public int DisposeCount { get; private set; }

        public byte[] Data() => _output.WrittenSpan.ToArray();

        public Memory<byte> GetMemory(int sizeHint = 0) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (HasBorrow) { throw new InvalidOperationException("Previous buffer not advanced."); }
            Memory<byte> memory = _output.GetMemory(sizeHint);
            _lastLength = memory.Length;
            HasBorrow = true;
            return memory;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Advance(int count) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            AdvanceAttempts++;
            if (count == 0) { CancellationAttempts++; }
            if (!HasBorrow) { throw new InvalidOperationException("No outstanding buffer."); }
            if (count < 0 || count > _lastLength) { throw new ArgumentOutOfRangeException(nameof(count)); }
            _output.Advance(count);
            HasBorrow = false;
            _lastLength = 0;
            if (count == failingCount && cancellationFailure is not null && !_failureReported) {
                _failureReported = true;
                throw cancellationFailure;
            }
        }

        public void Reset() {
            ResetCount++;
            _output.Clear();
            HasBorrow = false;
            _lastLength = 0;
        }

        public void Dispose() {
            DisposeCount++;
            _disposed = true;
        }
    }

    private sealed class TrackingPool : ArrayPool<byte> {
        private readonly HashSet<byte[]> _outstanding = new(ReferenceEqualityComparer.Instance);
        public int OutstandingCount => _outstanding.Count;

        public override byte[] Rent(int minimumLength) {
            var buffer = new byte[minimumLength];
            Assert.True(_outstanding.Add(buffer));
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            Assert.True(_outstanding.Remove(array), "Buffer returned without an outstanding lease.");
            array.AsSpan().Fill(0xE7);
        }
    }
}
