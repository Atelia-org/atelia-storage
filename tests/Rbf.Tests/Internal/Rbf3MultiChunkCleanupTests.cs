using System.Buffers;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class Rbf3MultiChunkCleanupTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileDispose_FirstBuilderReturnThrows_StillReturnsEveryLeaseAndClosesReaderAndHandle(bool sized) {
        var pool = new AcceptThenThrowPool();
        using var fixture = new Rbf3WriterFixture(builderPool: pool);
        var ticket = fixture.File.Append(11, new byte[9000], ReadOnlySpan<byte>.Empty,
            Rbf3WriterOracle.KnownKey(uint.MaxValue)).Unwrap();
        var info = fixture.File.ReadFrameInfo(ticket).Unwrap();
        Assert.Equal(1, fixture.Pool.RentCalls);

        const int chunkLength = 64 * 1024;
        var builder = sized ? fixture.File.BeginAppend(2 * chunkLength, 0, out _) : fixture.File.BeginAppend();
        var writer = builder.PayloadAndMeta;
        for (int i = 0; i < 2; i++) {
            writer.GetSpan(chunkLength)[..chunkLength].Fill(5);
            writer.Advance(chunkLength);
        }
        Assert.Equal(3, pool.RentCalls); // HeadLen's chunk plus two full payload chunks.
        byte[] before = fixture.ReadBytes();
        long tail = fixture.File.TailOffset;
        int reads = 0;
        int outputCalls = 0;
        fixture.File.ReadObserver = (_, _, _) => reads++;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => { outputCalls++; return request.RequestedBytes; },
            BeforeFlush = _ => outputCalls++,
            BeforeSetLength = _ => outputCalls++
        };

        var failure = Assert.Throws<InjectedReturnFailureException>(() => fixture.File.Dispose());

        Assert.Same(pool.Failure, failure);
        Assert.True(fixture.Handle.IsClosed);
        Assert.Equal(tail, fixture.File.TailOffset);
        Assert.Equal(1, fixture.Pool.ReturnCalls);
        Assert.Throws<ObjectDisposedException>(() => info.ReadPooledFrame());
        Assert.Throws<ObjectDisposedException>(() => fixture.File.ReadFrameInfo(ticket));
        Assert.Equal(0, reads);
        Assert.Equal(0, outputCalls);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));

        int returnCalls = pool.ReturnLeaseIds.Count;
        builder.Dispose();
        fixture.File.Dispose();
        Assert.Equal(returnCalls, pool.ReturnLeaseIds.Count);
        Assert.Equal(1, fixture.Pool.ReturnCalls);
        Assert.Equal(0, outputCalls);

        Assert.Equal(new[] { 1, 2, 3 }, pool.ReturnLeaseIds);
        Assert.Equal(0, pool.OutstandingRentals);
    }

    private sealed class InjectedReturnFailureException : Exception { }

    private sealed class AcceptThenThrowPool : ArrayPool<byte> {
        private readonly Dictionary<byte[], int> _leases = new(ReferenceEqualityComparer.Instance);
        internal int RentCalls { get; private set; }
        internal int OutstandingRentals => _leases.Count;
        internal List<int> ReturnLeaseIds { get; } = new();
        internal InjectedReturnFailureException Failure { get; } = new();

        public override byte[] Rent(int minimumLength) {
            byte[] buffer = new byte[minimumLength];
            _leases.Add(buffer, ++RentCalls);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            Assert.True(_leases.Remove(array, out int leaseId), "Each Builder lease may be returned only once.");
            ReturnLeaseIds.Add(leaseId);
            if (clearArray) { array.AsSpan().Clear(); }
            if (ReturnLeaseIds.Count == 1) { throw Failure; }
        }
    }
}
