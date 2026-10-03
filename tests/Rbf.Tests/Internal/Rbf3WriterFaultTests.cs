using System.Reflection;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class Rbf3WriterFaultTests {
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
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(301)]
    [InlineData(8195)]
    public void SimulatedSelectionFailureAfterFooter_CancelsBuilderAndAllowsNewEpoch(int payloadLength) {
        using var fixture = new Rbf3WriterFixture();
        var builder = fixture.File.BeginAppend();
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
    [InlineData(false)]
    [InlineData(true)]
    public void UnadvancedBorrowGuard_PrecedesPaddingAndKeepsSameBuilderRetryable(bool borrowMemory) {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend();
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

    private sealed class SelectionFailureException : Exception { }
}
