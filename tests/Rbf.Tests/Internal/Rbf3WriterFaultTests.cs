using System.Buffers;
using System.Reflection;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class Rbf3WriterFaultTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BeginRentFailure_DoesNotPublishEpochAndClosedBuilderCannotAffectNextBuilder(bool commitOldBuilder, bool sized) {
        var pool = new BuilderPool();
        using var fixture = new Rbf3WriterFixture(builderPool: pool);
        var original = fixture.File.Append(11, new byte[] { 1 }, ReadOnlySpan<byte>.Empty).Unwrap();
        var info = fixture.File.ReadFrameInfo(original).Unwrap();
        var oldBuilder = fixture.File.BeginAppend();
        var oldWriter = oldBuilder.PayloadAndMeta;
        Rbf3WriterOracle.WriteBuilder(oldBuilder, new byte[] { 5 });
        if (commitOldBuilder) { oldBuilder.EndAppend(12).Unwrap(); }
        else { oldBuilder.Dispose(); }
        Assert.Equal(1, pool.ReturnCalls);
        long tail = fixture.File.TailOffset;
        byte[] before = fixture.ReadBytes();
        var failure = new OutOfMemoryException("Simulated Builder rent failure.");
        pool.NextRentFailure = failure;
        int reads = 0;
        int outputCalls = 0;
        fixture.File.ReadObserver = (_, _, _) => reads++;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => { outputCalls++; return request.RequestedBytes; },
            BeforeFlush = _ => outputCalls++,
            BeforeSetLength = _ => outputCalls++
        };

        var actual = Assert.Throws<OutOfMemoryException>(() => Begin(fixture.File, 1, sized, out _));

        Assert.Same(failure, actual);
        Assert.Equal(2, pool.RentCalls);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.OutstandingRentals);
        Assert.Equal(0, reads);
        Assert.Equal(0, outputCalls);
        Assert.Equal(tail, fixture.File.TailOffset);
        Assert.Equal(before, fixture.ReadBytes());
        fixture.File.ReadObserver = null;
        using var oldFrame = info.ReadPooledFrame().Unwrap();
        Assert.Equal(new byte[] { 1 }, oldFrame.PayloadAndMeta.ToArray());
        Assert.Equal(original, fixture.File.ReadFrameInfo(original).Unwrap().Ticket);

        // Append retains its own scratch pool and stays usable after Builder initialization fails.
        fixture.File.Append(13, new byte[9000], ReadOnlySpan<byte>.Empty,
            Rbf3WriterOracle.KnownKey(uint.MaxValue)).Unwrap();
        Assert.Equal(2, pool.RentCalls);
        Assert.Equal(1, fixture.Pool.RentCalls);
        using var next = Begin(fixture.File, 1, sized, out var earlyTicket);
        Assert.Equal(3, pool.RentCalls);
        var nextWriter = next.PayloadAndMeta;
        nextWriter.ReserveSpan(1, out int nextToken).Fill(7);
        long nextLength = nextWriter.Length;

        oldBuilder.Dispose();
        Assert.True(oldBuilder.EndAppend(14).IsFailure);
        Assert.Throws<InvalidOperationException>(() => { oldBuilder.PayloadAndMeta.GetSpan(1); });
        Assert.Throws<InvalidOperationException>(() => { oldWriter.GetSpan(1); });
        Assert.Throws<InvalidOperationException>(() => oldWriter.Advance(1));
        Assert.Throws<InvalidOperationException>(() => oldWriter.Commit(nextToken));
        Assert.Throws<InvalidOperationException>(() => { oldWriter.TryGetReservedSpan(nextToken, out _); });
        Assert.Equal(nextLength, nextWriter.Length);
        nextWriter.Commit(nextToken);
        var nextTicket = next.EndAppend(15).Unwrap();
        if (sized) { Assert.Equal(earlyTicket, nextTicket); }
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(nextTicket), new byte[] { 7 }, Array.Empty<byte>(), 15);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuilderDisposeReturnFailure_PropagatesWithoutOutputAndFileDisposeDoesNotReturnLeaseAgain(bool sized) {
        var pool = new BuilderPool();
        using var fixture = new Rbf3WriterFixture(builderPool: pool);
        var builder = Begin(fixture.File, 3, sized, out _);
        Rbf3WriterOracle.WriteBuilder(builder, new byte[] { 1, 2, 3 });
        long tail = fixture.File.TailOffset;
        byte[] before = fixture.ReadBytes();
        var failure = new OutOfMemoryException("Simulated accepted Builder return failure.");
        pool.NextReturnFailure = failure;
        int reads = 0;
        int outputCalls = 0;
        fixture.File.ReadObserver = (_, _, _) => reads++;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => { outputCalls++; return request.RequestedBytes; },
            BeforeFlush = _ => outputCalls++,
            BeforeSetLength = _ => outputCalls++
        };

        var actual = Assert.Throws<OutOfMemoryException>(() => builder.Dispose());

        Assert.Same(failure, actual);
        Assert.Equal(0, reads);
        Assert.Equal(0, outputCalls);
        Assert.Equal(tail, fixture.File.TailOffset);
        Assert.Equal(before, fixture.ReadBytes());
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.OutstandingRentals);
        fixture.File.Dispose();
        Assert.True(fixture.Handle.IsClosed);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, reads);
        Assert.Equal(0, outputCalls);
        fixture.File.Dispose();
        Assert.Equal(1, pool.ReturnCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCommitReturnFailure_AfterCompleteOutputPermanentlyFaultsSharedReader(bool sized) {
        var pool = new BuilderPool();
        using var fixture = new Rbf3WriterFixture(builderPool: pool);
        var original = fixture.File.Append(11, new byte[] { 1 }, ReadOnlySpan<byte>.Empty).Unwrap();
        var info = fixture.File.ReadFrameInfo(original).Unwrap();
        var forward = fixture.File.ScanForward().GetEnumerator();
        long tail = fixture.File.TailOffset;
        var builder = Begin(fixture.File, 1, sized, out var earlyTicket);
        Rbf3WriterOracle.WriteBuilder(builder, new byte[] { 7 });
        var failure = new OutOfMemoryException("Simulated accepted Builder return failure after output.");
        pool.NextReturnFailure = failure;
        var writes = new List<RbfWriteObservation>();
        RbfWriteInstrumentation.Current = new() { AfterWrite = writes.Add };

        var actual = Assert.Throws<OutOfMemoryException>(() => builder.EndAppend(12));

        Assert.Same(failure, actual);
        var write = Assert.Single(writes);
        Assert.Equal(tail, write.Offset);
        Assert.Equal(36, write.RequestedBytes);
        Assert.Equal(write.RequestedBytes, write.WrittenBytes);
        Assert.Equal(tail + 36, RandomAccess.GetLength(fixture.Handle));
        Assert.Equal(tail, fixture.File.TailOffset);
        // A complete one-byte frame and its Fence reached the real file before Return threw.
        var writtenTicket = SizedPtr.Create(tail, 32);
        if (sized) { Assert.Equal(earlyTicket, writtenTicket); }
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(writtenTicket), new byte[] { 7 }, Array.Empty<byte>(), 12);
        Assert.Equal(1, pool.ReturnCalls);
        Assert.Equal(0, pool.OutstandingRentals);

        builder.Dispose();
        Assert.Throws<InvalidOperationException>(() => fixture.File.Append(13, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
        Assert.Throws<InvalidOperationException>(() => fixture.File.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => fixture.File.DurableFlush());
        Assert.Throws<InvalidOperationException>(() => fixture.File.ReadFrameInfo(original));
        Assert.Throws<InvalidOperationException>(() => info.ReadPooledFrame());
        bool forwardRefused = false;
        try { forward.MoveNext(); }
        catch (InvalidOperationException) { forwardRefused = true; }
        Assert.True(forwardRefused);
        Assert.Single(writes);
        fixture.File.Dispose();
        Assert.True(fixture.Handle.IsClosed);
        Assert.Equal(1, pool.ReturnCalls);
        using var reopened = RbfFile.OpenExisting(fixture.Path, out var recovery, RbfCacheMode.Off);
        Assert.Equal(RbfTailRecoveryAction.None, recovery.Action);
        using var recovered = reopened.ReadPooledFrame(writtenTicket).Unwrap();
        Assert.Equal(new byte[] { 7 }, recovered.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void SimulatedSelectionFailure_PrecedesOutputAndDoesNotFaultAppend() {
        using var fixture = new Rbf3WriterFixture();
        var original = fixture.File.Append(11, new byte[] { 1 }, ReadOnlySpan<byte>.Empty).Unwrap();
        var info = fixture.File.ReadFrameInfo(original).Unwrap();
        long tail = fixture.File.TailOffset;
        byte[] before = fixture.ReadBytes();
        int writes = 0;
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => { writes++; return request.RequestedBytes; } };
        var failure = new SelectionFailureException();

        var actual = Assert.Throws<SelectionFailureException>(() => fixture.File.Append(12, new byte[9000], ReadOnlySpan<byte>.Empty,
            (_, _, _, _) => throw failure));

        Assert.Same(failure, actual);
        Assert.Equal(0, writes);
        Assert.Equal(0, fixture.Pool.RentCalls);
        Assert.Equal(tail, fixture.File.TailOffset);
        Assert.Equal(before, fixture.ReadBytes());
        using var oldFrame = info.ReadPooledFrame().Unwrap();
        Assert.Equal(new byte[] { 1 }, oldFrame.PayloadAndMeta.ToArray());
        fixture.File.Append(13, new byte[] { 2 }, ReadOnlySpan<byte>.Empty).Unwrap();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScratchRentFailure_PrecedesOutputAndLeavesWriterUsable(bool undersized) {
        var pool = new Rbf3ScratchPool { FailRent = !undersized, ReturnUndersized = undersized };
        using var fixture = new Rbf3WriterFixture(pool);
        byte[] before = fixture.ReadBytes();
        int writes = 0;
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => { writes++; return request.RequestedBytes; } };
        if (undersized) {
            Assert.Throws<InvalidOperationException>(() => fixture.File.Append(11, new byte[9000], ReadOnlySpan<byte>.Empty,
                Rbf3WriterOracle.KnownKey(uint.MaxValue)));
            Assert.Equal(1, pool.ReturnCalls);
        }
        else {
            Assert.Throws<OutOfMemoryException>(() => fixture.File.Append(11, new byte[9000], ReadOnlySpan<byte>.Empty,
                Rbf3WriterOracle.KnownKey(uint.MaxValue)));
        }
        Assert.Equal(0, writes);
        Assert.Equal(4, fixture.File.TailOffset);
        Assert.Equal(before, fixture.ReadBytes());
        pool.FailRent = false;
        pool.ReturnUndersized = false;
        fixture.File.Append(12, new byte[9000], ReadOnlySpan<byte>.Empty, Rbf3WriterOracle.KnownKey(uint.MaxValue)).Unwrap();
        fixture.File.Dispose();
        Assert.Equal(undersized ? 2 : 1, pool.ReturnCalls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(301, false)]
    [InlineData(8195, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(301, true)]
    [InlineData(8195, true)]
    public void SimulatedSelectionFailureAfterFooter_CancelsBuilderAndAllowsNewEpoch(int payloadLength, bool sized) {
        using var fixture = new Rbf3WriterFixture();
        var builder = Begin(fixture.File, payloadLength, sized, out _);
        var writer = builder.PayloadAndMeta;
        byte[] payload = Rbf3WriterOracle.Pattern(payloadLength);
        Rbf3WriterOracle.WriteBuilder(builder, payload);
        var concrete = (SinkReservableWriter)typeof(RbfFileImpl).GetField("_builderWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.File)!;
        byte[] before = fixture.ReadBytes();
        var failure = new SelectionFailureException();
        int writes = 0;
        RbfWriteInstrumentation.Current = new() { BeforeWrite = request => { writes++; return request.RequestedBytes; } };

        var actual = Assert.Throws<SelectionFailureException>(() => builder.EndAppend(11, 0, () => {
            Assert.Equal(4 + ((payload.Length + 3) & ~3) + 20, concrete.Length);
            Assert.Equal(0, concrete.PushedLength);
            Assert.Equal(1, concrete.PendingReservationCount);
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.Equal(0, writes);
        Assert.Equal(before, fixture.ReadBytes());
        Assert.Equal(4, fixture.File.TailOffset);
        Assert.Equal(0, concrete.Length);
        Assert.Equal(0, concrete.PendingReservationCount);
        Assert.True(builder.EndAppend(11).IsFailure);
        Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(1); });
        using var next = fixture.File.BeginAppend();
        builder.Dispose(); // Old epoch cannot cancel the next builder.
        Rbf3WriterOracle.WriteBuilder(next, new byte[] { 7 });
        var ticket = next.EndAppend(12).Unwrap();
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 7 }, Array.Empty<byte>(), 12);
    }

    [Fact]
    public void CorrectableBuilderResultFailures_DoNotAppendPaddingOrCloseEpoch() {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend();
        var writer = builder.PayloadAndMeta;
        Rbf3WriterOracle.WriteBuilder(builder, new byte[] { 1 });
        long before = writer.Length;
        Assert.True(builder.EndAppend(11, -1).IsFailure);
        Assert.True(builder.EndAppend(11, 2).IsFailure);
        Assert.Equal(before, writer.Length);
        Assert.Equal(4, fixture.ReadBytes().Length);
        writer.ReserveSpan(3, out int token).Fill(2);
        before = writer.Length;
        Assert.True(builder.EndAppend(11).IsFailure);
        Assert.Equal(before, writer.Length);
        writer.Commit(token);
        var ticket = builder.EndAppend(11).Unwrap();
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 1, 2, 2, 2 }, Array.Empty<byte>(), 11);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnadvancedBorrowGuard_PrecedesPaddingAndKeepsSameBuilderRetryable(bool borrowMemory, bool sized) {
        using var fixture = new Rbf3WriterFixture();
        using var builder = Begin(fixture.File, 1, sized, out _);
        var writer = builder.PayloadAndMeta;
        Rbf3WriterOracle.WriteBuilder(builder, new byte[] { 1 });
        long length = writer.Length;
        if (borrowMemory) { writer.GetMemory(3).Span.Fill(0xBB); }
        else { writer.GetSpan(3).Fill(0xBB); }

        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(11));

        Assert.Equal(length, writer.Length);
        Assert.Equal(4, fixture.ReadBytes().Length);
        writer.Advance(0);
        var ticket = builder.EndAppend(11).Unwrap();
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 1 }, Array.Empty<byte>(), 11);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrueOutputFailure_FaultsWriterAndExistingFrameHandlesEvenAfterAbort(bool useBuilder) {
        using var fixture = new Rbf3WriterFixture();
        var original = fixture.File.Append(11, new byte[] { 1 }, ReadOnlySpan<byte>.Empty).Unwrap();
        var info = fixture.File.ReadFrameInfo(original).Unwrap();
        long tail = fixture.File.TailOffset;
        RbfWriteInstrumentation.Current = new() { BeforeWrite = _ => 2 };
        if (useBuilder) {
            var builder = fixture.File.BeginAppend();
            Rbf3WriterOracle.WriteBuilder(builder, new byte[9000]);
            Assert.Throws<IOException>(() => builder.EndAppend(12));
            builder.Dispose();
        }
        else {
            Assert.Throws<IOException>(() => fixture.File.Append(12, new byte[9000], ReadOnlySpan<byte>.Empty,
                Rbf3WriterOracle.KnownKey(uint.MaxValue)));
        }
        RbfWriteInstrumentation.Current = null;
        Assert.Equal(tail, fixture.File.TailOffset);
        Assert.Throws<InvalidOperationException>(() => fixture.File.Append(13, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
        Assert.Throws<InvalidOperationException>(() => fixture.File.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => fixture.File.DurableFlush());
        Assert.Throws<InvalidOperationException>(() => info.ReadPooledFrame());
        Assert.Throws<InvalidOperationException>(() => fixture.File.ReadFrameInfo(original));
        fixture.File.Dispose();
        Assert.Equal(useBuilder ? 0 : 1, fixture.Pool.ReturnCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlushFailureBeforeOrAfterBarrier_IsPermanent(bool afterBarrier) {
        using var fixture = new Rbf3WriterFixture();
        var ticket = fixture.File.Append(11, new byte[] { 1 }, ReadOnlySpan<byte>.Empty).Unwrap();
        RbfWriteInstrumentation.Current = new() {
            BeforeFlush = _ => { if (!afterBarrier) { throw new IOException("Before barrier"); } },
            AfterFlush = _ => throw new IOException("After barrier")
        };
        Assert.Throws<IOException>(() => fixture.File.DurableFlush());
        RbfWriteInstrumentation.Current = null;
        Assert.Throws<InvalidOperationException>(() => fixture.File.ReadFrameInfo(ticket));
        Assert.Throws<InvalidOperationException>(() => fixture.File.BeginAppend());
    }

    [Fact]
    public void ReadOnlyLegacyFacade_RejectsAllWriteEntrypointsWithoutBytesChanging() {
        using var fixture = new Rbf3WriterFixture(profile: RbfProfile.Rbf1, readOnly: true);
        byte[] before = fixture.ReadBytes();
        Assert.Throws<InvalidOperationException>(() => fixture.File.Append(11, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
        Assert.Throws<InvalidOperationException>(() => fixture.File.BeginAppend());
        Assert.Throws<InvalidOperationException>(() => fixture.File.DurableFlush());
        Assert.Equal(before, fixture.ReadBytes());
    }

    private static RbfFrameBuilder Begin(IRbfFile file, int payloadLength, bool sized, out SizedPtr ticket) {
        ticket = default;
        return sized ? file.BeginAppend(payloadLength, 0, out ticket) : file.BeginAppend();
    }

    private sealed class BuilderPool : ArrayPool<byte> {
        private readonly HashSet<byte[]> _owned = new();
        internal int RentCalls { get; private set; }
        internal int ReturnCalls { get; private set; }
        internal int OutstandingRentals => _owned.Count;
        internal Exception? NextRentFailure { get; set; }
        internal Exception? NextReturnFailure { get; set; }

        public override byte[] Rent(int minimumLength) {
            RentCalls++;
            Exception? failure = NextRentFailure;
            NextRentFailure = null;
            if (failure is not null) { throw failure; }
            byte[] buffer = new byte[minimumLength];
            Assert.True(_owned.Add(buffer));
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false) {
            ReturnCalls++;
            Assert.True(_owned.Remove(array), "Each outstanding Builder rental may be returned only once.");
            if (clearArray) { array.AsSpan().Clear(); }
            Exception? failure = NextReturnFailure;
            NextReturnFailure = null;
            if (failure is not null) { throw failure; }
        }
    }

    private sealed class SelectionFailureException : Exception { }
}
