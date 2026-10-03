using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using Xunit;

namespace Atelia.Data.Tests;

public class SinkReservableWriterXorEscapeTests {
    private const uint Fence = 0x32464252;
    private const byte Unwritten = 0xCC;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TinyBodyAcrossUnalignedChunkEdges_Selects63AndPreservesReservation(int edgeLength) {
        using var fixture = new WriterFixture();
        int reservationLength = 1024 - edgeLength;
        fixture.Writer.ReserveSpan(reservationLength, out int token).Fill(0xA5);
        byte[] body = ForbiddenKeys(63, 63);
        Write(fixture.Writer, body.AsSpan(0, edgeLength));
        Write(fixture.Writer, body.AsSpan(edgeLength));
        WriterState before = Capture(fixture);

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, Fence,
            () => throw new InvalidOperationException("Tiny must not use RNG."));

        Assert.Equal(63u, key);
        AssertMetadataUnchanged(fixture, before);
        byte[] encoded = ScalarXor(body, key);
        Assert.All(fixture.Pool.Buffers[0].Take(reservationLength), value => Assert.Equal((byte)0xA5, value));
        Assert.Equal(encoded.AsSpan(0, edgeLength).ToArray(), fixture.Pool.Buffers[0].AsSpan(reservationLength).ToArray());
        Assert.Equal(encoded.AsSpan(edgeLength).ToArray(), fixture.Pool.Buffers[1].AsSpan(0, body.Length - edgeLength).ToArray());
        Assert.All(fixture.Pool.Buffers[1].Skip(body.Length - edgeLength), value => Assert.Equal(Unwritten, value));
        AssertMarkerFree(encoded, Fence);

        fixture.Writer.Commit(token);
        Assert.Equal(Enumerable.Repeat((byte)0xA5, reservationLength).Concat(encoded).ToArray(), fixture.Sink.Data());
        Assert.Equal(fixture.Pool.RentCount, fixture.Pool.ReturnCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AlreadyPushedPrefixAndNonFirstReservation_UseLogicalPhaseZero(int edgeLength) {
        using var fixture = new WriterFixture(oversizeSlack: edgeLength);
        byte[] prefix = Enumerable.Range(1, 17).Select(value => (byte)value).ToArray();
        Write(fixture.Writer, prefix);
        Assert.Equal(prefix.Length, fixture.Writer.PushedLength);

        // Advance(0) retains an empty active chunk; the oversized reservation starts in the next one.
        _ = fixture.Writer.GetSpan(1);
        fixture.Writer.Advance(0);
        fixture.Writer.ReserveSpan(1025, out int token).Fill(0xA5);
        byte[] body = ForbiddenKeys(2, 1);
        Write(fixture.Writer, body.AsSpan(0, edgeLength));
        Write(fixture.Writer, body.AsSpan(edgeLength));
        WriterState before = Capture(fixture);

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, Fence);

        Assert.Equal(1u, key);
        AssertMetadataUnchanged(fixture, before);
        Assert.Equal(prefix, fixture.Sink.Data());
        Assert.Equal(before.Buffers[0], fixture.Pool.Buffers[0]);
        Assert.All(fixture.Pool.Buffers[1], value => Assert.Equal(Unwritten, value));
        Assert.All(fixture.Pool.Buffers[2].Take(1025), value => Assert.Equal((byte)0xA5, value));
        byte[] encoded = ScalarXor(body, key);
        Assert.Equal(encoded.AsSpan(0, edgeLength).ToArray(), fixture.Pool.Buffers[2].AsSpan(1025).ToArray());
        Assert.Equal(encoded.AsSpan(edgeLength).ToArray(), fixture.Pool.Buffers[3].AsSpan(0, body.Length - edgeLength).ToArray());
        Assert.All(fixture.Pool.Buffers[3].Skip(body.Length - edgeLength), value => Assert.Equal(Unwritten, value));

        fixture.Writer.Commit(token);
        Assert.Equal(prefix.Concat(Enumerable.Repeat((byte)0xA5, 1025)).Concat(encoded).ToArray(), fixture.Sink.Data());
        Assert.Equal(fixture.Pool.RentCount, fixture.Pool.ReturnCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void EmptyOrZeroBody_ReturnsZeroWithoutRandomOrWriterChanges(int bodyLength) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        if (bodyLength != 0) { Write(fixture.Writer, new byte[bodyLength]); }
        WriterState before = Capture(fixture);

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, Fence,
            () => throw new InvalidOperationException("Zero must not use RNG."));

        Assert.Equal(0u, key);
        AssertUnchanged(fixture, before);
        Assert.True(fixture.Writer.TryGetReservedSpan(token, out _));
    }

    [Fact]
    public void FenceInUnadvancedCapacity_IsOutsideSelectionAndTransform() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, new byte[8]);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Writer.GetSpan(4), Fence);
        fixture.Writer.Advance(0);
        WriterState before = Capture(fixture);

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, Fence,
            () => throw new InvalidOperationException("Unwritten capacity must not prohibit Zero."));

        Assert.Equal(0u, key);
        AssertUnchanged(fixture, before);
    }

    [Theory]
    [InlineData(0x04000000u)]
    [InlineData(0x04000001u)]
    [InlineData(Fence)]
    [InlineData(uint.MaxValue)]
    public void SupportedFenceDomain_SelectsKeyWithoutRequiringAlignedReservation(uint fence) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(3, out int token).Fill(0xA5);
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, fence);
        Write(fixture.Writer, body);

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, fence);

        Assert.Equal(1u, key);
        fixture.Writer.Commit(token);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }.Concat(ScalarXor(body, key)).ToArray(), fixture.Sink.Data());
    }

    [Fact]
    public void LargeBody_RetriesForbiddenRandomCandidatesBeforeAnyXor() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        byte[] body = ForbiddenKeys(64, 4);
        Write(fixture.Writer, body);
        WriterState before = Capture(fixture);
        var candidates = new Queue<uint>(new uint[] { 0, Fence, 1, 2, 3, uint.MaxValue });

        uint key = fixture.Writer.XorEscapeSinceReservationEnd(token, Fence, () => {
            AssertUnchanged(fixture, before);
            Assert.True(fixture.Writer.TryGetReservedSpan(token, out _));
            return candidates.Dequeue();
        });

        Assert.Equal(uint.MaxValue, key);
        Assert.Empty(candidates);
        AssertMetadataUnchanged(fixture, before);
        byte[] encoded = ScalarXor(body, key);
        AssertMarkerFree(encoded, Fence);
        fixture.Writer.Commit(token);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5 }.Concat(encoded).ToArray(), fixture.Sink.Data());
    }

    [Fact]
    public void RandomFailureAfterCollisions_PreservesBytesStateAndTokenWithoutReset() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, ForbiddenKeys(64, 4));
        WriterState before = Capture(fixture);
        var candidates = new Queue<uint>(new uint[] { 0, Fence, 1, 2, 3 });
        var failure = new RandomFailureException();

        var actual = Assert.Throws<RandomFailureException>(() =>
            fixture.Writer.XorEscapeSinceReservationEnd(token, Fence, () => {
                AssertUnchanged(fixture, before);
                return candidates.Count != 0 ? candidates.Dequeue() : throw failure;
            }));

        Assert.Same(failure, actual);
        Assert.Empty(candidates);
        AssertUnchanged(fixture, before);
        Assert.True(fixture.Writer.TryGetReservedSpan(token, out _));
        // Data does not fault or reset its owner: a new complete selection can still use the same bytes.
        Assert.Equal(uint.MaxValue, fixture.Writer.XorEscapeSinceReservationEnd(token, Fence, () => uint.MaxValue));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(63u)]
    [InlineData(64u)]
    [InlineData(0x03FFFFFFu)]
    public void InvalidFenceIncludingEmptyBody_RejectsWithoutChanges(uint fence) {
        foreach (int bodyLength in new[] { 0, 4 }) {
            using var fixture = new WriterFixture();
            fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
            if (bodyLength != 0) { Write(fixture.Writer, new byte[bodyLength]); }
            WriterState before = Capture(fixture);

            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, fence));

            AssertUnchanged(fixture, before);
        }
    }

    [Fact]
    public void InvalidAndCommittedTokens_RejectWithoutChanges() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int oldToken).Fill(0xA5);
        Write(fixture.Writer, new byte[8]);
        WriterState before = Capture(fixture);
        Assert.Throws<InvalidOperationException>(() => fixture.Writer.XorEscapeSinceReservationEnd(oldToken + 1, Fence));
        AssertUnchanged(fixture, before);

        fixture.Writer.Commit(oldToken);
        fixture.Writer.ReserveSpan(4, out int currentToken).Fill(0xB5);
        Write(fixture.Writer, new byte[8]);
        before = Capture(fixture);
        Assert.Throws<InvalidOperationException>(() => fixture.Writer.XorEscapeSinceReservationEnd(oldToken, Fence));
        AssertUnchanged(fixture, before);
        Assert.True(fixture.Writer.TryGetReservedSpan(currentToken, out _));
    }

    [Fact]
    public void ResetInvalidatesOldTokenAndReturnsEveryBufferExactlyOnce() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(1021, out int oldToken).Fill(0xA5);
        byte[] body = ForbiddenKeys(63, 63);
        Write(fixture.Writer, body.AsSpan(0, 3));
        Write(fixture.Writer, body.AsSpan(3));
        Assert.Equal(63u, fixture.Writer.XorEscapeSinceReservationEnd(oldToken, Fence));
        Assert.Equal(0, fixture.Pool.ReturnCount);

        fixture.Writer.Reset();
        Assert.Equal(fixture.Pool.RentCount, fixture.Pool.ReturnCount);
        fixture.Writer.ReserveSpan(4, out int newToken).Fill(0xB5);
        Write(fixture.Writer, new byte[8]);
        WriterState before = Capture(fixture);
        Assert.Throws<InvalidOperationException>(() => fixture.Writer.XorEscapeSinceReservationEnd(oldToken, Fence));
        AssertUnchanged(fixture, before);
        Assert.Equal(0u, fixture.Writer.XorEscapeSinceReservationEnd(newToken, Fence));

        fixture.Writer.Dispose();
        Assert.Equal(fixture.Pool.RentCount, fixture.Pool.ReturnCount);
        Assert.Equal(0, fixture.Sink.PushCalls);
    }

    [Fact]
    public void MultiplePendingReservations_RejectWithoutChanges() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        fixture.Writer.ReserveSpan(4, out int secondToken).Fill(0xB5);
        Write(fixture.Writer, new byte[8]);
        WriterState before = Capture(fixture);

        Assert.Throws<InvalidOperationException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, Fence));

        AssertUnchanged(fixture, before);
        Assert.True(fixture.Writer.TryGetReservedSpan(token, out _));
        Assert.True(fixture.Writer.TryGetReservedSpan(secondToken, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnadvancedSpanOrMemory_RejectsWithoutEndingBorrow(bool borrowMemory) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, new byte[8]);
        if (borrowMemory) { _ = fixture.Writer.GetMemory(10); }
        else { _ = fixture.Writer.GetSpan(10); }
        WriterState before = Capture(fixture);

        Assert.Throws<InvalidOperationException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, Fence));

        AssertUnchanged(fixture, before);
        Assert.Throws<InvalidOperationException>(() => fixture.Writer.GetSpan(1));
        fixture.Writer.Advance(0);
        Assert.Equal(0u, fixture.Writer.XorEscapeSinceReservationEnd(token, Fence));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)]
    [InlineData(long.MaxValue - 4)]
    public void DerivedLengthOutsideIntDomain_RejectsBeforeScanning(long byteLength) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, ForbiddenKeys(2, 1));
        SetLogicalLength(fixture.Writer, byteLength + 4);
        WriterState before = Capture(fixture);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, Fence,
            () => throw new RandomFailureException()));

        AssertUnchanged(fixture, before);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(3L)]
    [InlineData(2147483647L)]
    public void MisalignedDerivedLength_RejectsBeforeScanning(long byteLength) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, ForbiddenKeys(2, 1));
        SetLogicalLength(fixture.Writer, byteLength + 4);
        WriterState before = Capture(fixture);

        Assert.Throws<ArgumentException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, Fence,
            () => throw new RandomFailureException()));

        AssertUnchanged(fixture, before);
    }

    [Theory]
    [InlineData(268435460L)]
    [InlineData(2147483644L)]
    public void LargeAlignedIntLengths_HaveNoRbfCapacityLimit(long byteLength) {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, ForbiddenKeys(2, 1));
        // Only logical metadata is expanded. Zero hits the first word, then RNG fails before a huge scan.
        SetLogicalLength(fixture.Writer, byteLength + 4);
        WriterState before = Capture(fixture);
        var failure = new RandomFailureException();

        var actual = Assert.Throws<RandomFailureException>(() =>
            fixture.Writer.XorEscapeSinceReservationEnd(token, Fence, () => throw failure));

        Assert.Same(failure, actual);
        AssertUnchanged(fixture, before);
    }

    [Fact]
    public void DisposedWriter_RejectsBeforeOtherValidationAndDoesNotReturnBuffersTwice() {
        using var fixture = new WriterFixture();
        fixture.Writer.ReserveSpan(4, out int token).Fill(0xA5);
        Write(fixture.Writer, new byte[8]);
        fixture.Writer.Dispose();
        WriterState before = Capture(fixture);

        Assert.Throws<ObjectDisposedException>(() => fixture.Writer.XorEscapeSinceReservationEnd(token, 0));

        AssertUnchanged(fixture, before);
        Assert.Equal(fixture.Pool.RentCount, fixture.Pool.ReturnCount);
    }

    private static void Write(SinkReservableWriter writer, ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    private static byte[] ForbiddenKeys(int wordCount, int forbiddenCount) {
        byte[] body = new byte[wordCount * 4];
        for (int i = 0; i < forbiddenCount; i++) {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 4), Fence ^ (uint)i);
        }
        return body;
    }

    private static byte[] ScalarXor(byte[] bytes, uint key) {
        byte[] encoded = new byte[bytes.Length];
        for (int i = 0; i < encoded.Length; i++) { encoded[i] = (byte)(bytes[i] ^ (byte)(key >> ((i & 3) * 8))); }
        return encoded;
    }

    private static void AssertMarkerFree(byte[] bytes, uint fence) {
        for (int i = 0; i < bytes.Length; i += 4) {
            Assert.NotEqual(fence, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)));
        }
    }

    private static void SetLogicalLength(SinkReservableWriter writer, long length) {
        typeof(SinkReservableWriter).GetField("_length", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(writer, length);
    }

    private static WriterState Capture(WriterFixture fixture) {
        return new WriterState(fixture.Writer.Length, fixture.Writer.PushedLength, fixture.Writer.PendingReservationCount,
            fixture.Sink.PushCalls, fixture.TraceCalls, fixture.Pool.RentCount, fixture.Pool.ReturnCount,
            fixture.Pool.Buffers.Select(bytes => bytes.ToArray()).ToArray());
    }

    private static void AssertMetadataUnchanged(WriterFixture fixture, WriterState before) {
        Assert.Equal(before.Length, fixture.Writer.Length);
        Assert.Equal(before.PushedLength, fixture.Writer.PushedLength);
        Assert.Equal(before.PendingReservations, fixture.Writer.PendingReservationCount);
        Assert.Equal(before.PushCalls, fixture.Sink.PushCalls);
        Assert.Equal(before.TraceCalls, fixture.TraceCalls);
        Assert.Equal(before.RentCount, fixture.Pool.RentCount);
        Assert.Equal(before.ReturnCount, fixture.Pool.ReturnCount);
    }

    private static void AssertUnchanged(WriterFixture fixture, WriterState before) {
        AssertMetadataUnchanged(fixture, before);
        for (int i = 0; i < before.Buffers.Length; i++) { Assert.Equal(before.Buffers[i], fixture.Pool.Buffers[i]); }
    }

    private sealed record WriterState(long Length, long PushedLength, int PendingReservations, int PushCalls,
        int TraceCalls, int RentCount, int ReturnCount, byte[][] Buffers);

    private sealed class RandomFailureException : Exception { }

    private sealed class WriterFixture : IDisposable {
        public TrackingPool Pool { get; }
        public RecordingSink Sink { get; } = new();
        public SinkReservableWriter Writer { get; }
        public int TraceCalls { get; private set; }

        public WriterFixture(int oversizeSlack = 0) {
            Pool = new TrackingPool(oversizeSlack);
            Writer = new SinkReservableWriter(Sink, new ChunkedReservableWriterOptions {
                MinChunkSize = 1024,
                MaxChunkSize = 1024,
                Pool = Pool,
                DebugLog = (_, _) => TraceCalls++,
            });
        }

        public void Dispose() { Writer.Dispose(); }
    }

    private sealed class RecordingSink : IByteSink {
        private readonly TestHelpers.CollectingWriter _inner = new();
        public int PushCalls { get; private set; }

        public void Push(ReadOnlySpan<byte> bytes) {
            PushCalls++;
            _inner.Push(bytes);
        }

        public byte[] Data() { return _inner.Data(); }
    }

    private sealed class TrackingPool(int oversizeSlack) : ArrayPool<byte> {
        private readonly HashSet<byte[]> _returned = new();
        public List<byte[]> Buffers { get; } = new();
        public int RentCount => Buffers.Count;
        public int ReturnCount => _returned.Count;

        public override byte[] Rent(int minimumLength) {
            byte[] bytes = new byte[minimumLength + (minimumLength > 1024 ? oversizeSlack : 0)];
            bytes.AsSpan().Fill(Unwritten);
            Buffers.Add(bytes);
            return bytes;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            Assert.True(Buffers.Contains(array), "Only rented buffers may be returned.");
            Assert.True(_returned.Add(array), "Each buffer must be returned exactly once.");
            if (clearArray) { array.AsSpan().Clear(); }
        }
    }
}
