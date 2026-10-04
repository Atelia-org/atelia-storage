using System.Buffers;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public class RbfSizedAppendTests {
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 3, false)]
    [InlineData(3, 1, true)]
    [InlineData(5, 3, true)]
    [InlineData(257, 3, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, 3, false)]
    public void KnownLengths_EarlyTicketMatchesCompletionAndIndependentWire(int payloadLength, int metaLength, bool explicitMeta) {
        using var fixture = new Rbf3WriterFixture();
        IRbfFile file = fixture.File;
        byte[] payload = Rbf3WriterOracle.Pattern(payloadLength);
        byte[] meta = Rbf3WriterOracle.Pattern(metaLength);
        var size = RbfFile.MeasureWriteSize(payloadLength, metaLength).Unwrap();
        long start = file.TailOffset;
        using var builder = file.BeginAppend(payloadLength, metaLength, out var early);
        Assert.Equal(RbfFormat.Rbf3, file.Format);
        Assert.Equal(start, early.Offset);
        Assert.Equal(size.FrameLength, early.Length);
        Assert.Equal(start, file.TailOffset);
        Assert.Equal(4, fixture.ReadBytes().Length);
        Assert.Throws<InvalidOperationException>(() => { file.ReadPooledFrame(early); });
        Assert.Throws<InvalidOperationException>(() => { file.BeginAppend(); });

        var writer = builder.PayloadAndMeta;
        writer.Write(payload);
        writer.Write(meta);
        var completed = explicitMeta ? builder.EndAppend(83, metaLength).Unwrap() : builder.EndAppend(83).Unwrap();
        Assert.Equal(early, completed);
        Assert.Equal(start + size.AppendLength, file.TailOffset);
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(completed), payload, meta, 83);
        using var frame = file.ReadPooledFrame(completed).Unwrap();
        Assert.Equal(payload.Concat(meta).ToArray(), frame.PayloadAndMeta.ToArray());
        Assert.Equal(metaLength, frame.TailMetaLength);
    }

    [Theory]
    [InlineData(-1, 0, "payloadLength")]
    [InlineData(0, -1, "tailMetaLength")]
    [InlineData(0, 65536, "tailMetaLength")]
    [InlineData(int.MaxValue, 0, "payloadLength")]
    [InlineData(RbfFile.MaxPayloadAndMetaLength, 1, "payloadLength")]
    [InlineData(int.MaxValue, int.MaxValue, "tailMetaLength")]
    public void InvalidDeclaration_RejectsBeforeBuildingAndAllowsValidRetry(int payloadLength, int metaLength, string parameterName) {
        using var fixture = new Rbf3WriterFixture();
        var early = SizedPtr.Create(4, 28);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => {
            fixture.File.BeginAppend(payloadLength, metaLength, out early);
        });
        Assert.Equal(parameterName, error.ParamName);
        Assert.Equal(default(SizedPtr), early);
        Assert.Equal(4, fixture.File.TailOffset);
        Assert.Equal(4, fixture.ReadBytes().Length);
        using var retry = fixture.File.BeginAppend(0, 0, out var retryTicket);
        Assert.Equal(retryTicket, retry.EndAppend(83).Unwrap());
    }

    [Fact]
    public void ShortWrite_RejectsEvenWhenAlignmentProducesTheSameTicketAndCanBeCorrected() {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend(3, 1, out var early);
        var writer = builder.PayloadAndMeta;
        writer.Write(new byte[] { 1, 2, 3 });
        long length = writer.Length;
        Assert.Equal(early.Length, RbfFile.MeasureWriteSize(3, 0).Unwrap().FrameLength);

        var rejected = builder.EndAppend(83);
        Assert.True(rejected.IsFailure);
        Assert.IsType<RbfArgumentError>(rejected.Error);
        Assert.Equal(length, writer.Length);
        Assert.Equal(4, fixture.File.TailOffset);
        Assert.Equal(4, fixture.ReadBytes().Length);
        writer.Write(new byte[] { 4 });
        Assert.Equal(early, builder.EndAppend(83).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 1, 2, 3 }, new byte[] { 4 }, 83);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    public void ExplicitMetaConflict_RejectsWithoutFinalizeAndAllowsMatchingRetry(int conflictingMeta) {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend(2, 2, out var early);
        var writer = builder.PayloadAndMeta;
        writer.Write(new byte[] { 1, 2, 3, 4 });
        long length = writer.Length;
        var rejected = builder.EndAppend(83, conflictingMeta);
        Assert.True(rejected.IsFailure);
        Assert.IsType<RbfArgumentError>(rejected.Error);
        Assert.Equal(length, writer.Length);
        Assert.Equal(4, fixture.ReadBytes().Length);
        Assert.Equal(early, builder.EndAppend(83, 2).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 1, 2 }, new byte[] { 3, 4 }, 83);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvanceQuota_LeavesBorrowForCorrectionAndDoesNotClipCapacity(bool memory) {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend(2, 1, out var early);
        var writer = builder.PayloadAndMeta;
        Assert.Equal(4, writer.Length); // The existing projection includes the internal HeadLen reservation.
        Span<byte> borrowed = memory ? writer.GetMemory(64).Span : writer.GetSpan(64);
        Assert.True(borrowed.Length >= 64);
        new byte[] { 1, 2, 3 }.AsSpan().CopyTo(borrowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(-1));
        Assert.Equal(4, writer.Length);
        writer.Advance(2);

        borrowed = writer.GetSpan(64);
        Assert.True(borrowed.Length >= 64);
        borrowed[0] = 3;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(2));
        Assert.Equal(6, writer.Length);
        writer.Advance(1);
        Assert.True(writer.GetMemory(64).Length >= 64);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(1));
        writer.Advance(0);
        Assert.False(writer.GetSpan().IsEmpty);
        writer.Advance(0);
        Assert.Equal(early, builder.EndAppend(83).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 1, 2 }, new byte[] { 3 }, 83);
    }

    [Fact]
    public void ReservationQuota_CountsAtReserveAndBackfillCommitDoesNotConsumeAgain() {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend(4, 1, out var early);
        var writer = builder.PayloadAndMeta;
        Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(0, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(-1, out _); });
        writer.ReserveSpan(4, out int token).Fill(1);
        Assert.Equal(8, writer.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => { writer.ReserveSpan(2, out _); });
        Assert.Equal(8, writer.Length);
        Assert.True(writer.TryGetReservedSpan(token, out var reserved));
        new byte[] { 1, 2, 3, 4 }.AsSpan().CopyTo(reserved);
        writer.Write(new byte[] { 5 });
        var pending = builder.EndAppend(83);
        Assert.True(pending.IsFailure);
        Assert.IsType<RbfStateError>(pending.Error);
        Assert.Equal(4, fixture.ReadBytes().Length);

        Assert.True(writer.GetSpan(64).Length >= 64);
        writer.Commit(token); // Reservation backfill is legal during an ordinary span borrow.
        Assert.False(writer.TryGetReservedSpan(token, out _));
        Assert.Equal(9, writer.Length);
        Assert.Throws<InvalidOperationException>(() => { builder.EndAppend(83); });
        Assert.Throws<InvalidOperationException>(() => writer.Commit(token));
        Assert.Equal(4, fixture.ReadBytes().Length);
        writer.Advance(0);
        Assert.Equal(early, builder.EndAppend(83).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 1, 2, 3, 4 }, new byte[] { 5 }, 83);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnadvancedBorrow_RejectsRepeatedRequestsAndEndBeforeMutation(bool memory) {
        using var fixture = new Rbf3WriterFixture();
        using var builder = fixture.File.BeginAppend(1, 0, out var early);
        var writer = builder.PayloadAndMeta;
        writer.Write(new byte[] { 7 });
        if (memory) { writer.GetMemory(8).Span.Fill(9); }
        else { writer.GetSpan(8).Fill(9); }
        long length = writer.Length;
        Assert.Throws<InvalidOperationException>(() => { writer.GetSpan(); });
        Assert.Throws<InvalidOperationException>(() => { writer.GetMemory(); });
        Assert.Throws<InvalidOperationException>(() => { builder.EndAppend(83); });
        Assert.Equal(length, writer.Length);
        Assert.Equal(4, fixture.ReadBytes().Length);
        writer.Advance(0);
        Assert.Equal(early, builder.EndAppend(83).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 7 }, Array.Empty<byte>(), 83);
    }

    [Fact]
    public void UnknownMode_DoesNotInheritDeclarationAndRetainsBothEndSemantics() {
        using var fixture = new Rbf3WriterFixture();
        using (var known = fixture.File.BeginAppend(1, 1, out _)) {
            known.PayloadAndMeta.Write(new byte[] { 1, 2 });
            known.EndAppend(83).Unwrap();
        }
        using (var unknown = fixture.File.BeginAppend()) {
            unknown.PayloadAndMeta.Write(new byte[] { 3, 4, 5, 6 });
            var ticket = unknown.EndAppend(84).Unwrap();
            Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 3, 4, 5, 6 }, Array.Empty<byte>(), 84);
        }
        using (var unknown = fixture.File.BeginAppend()) {
            unknown.PayloadAndMeta.Write(new byte[] { 7, 8, 9 });
            var ticket = unknown.EndAppend(85, 2).Unwrap();
            Rbf3WriterOracle.AssertWire(fixture.ReadFrame(ticket), new byte[] { 7 }, new byte[] { 8, 9 }, 85);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosedAndStaleEpochs_RejectBeforeModeAndQuotaChecks(bool oldKnown, bool oldCommitted) {
        using var fixture = new Rbf3WriterFixture();
        var old = oldKnown ? fixture.File.BeginAppend(2, 2, out _) : fixture.File.BeginAppend();
        var oldWriter = old.PayloadAndMeta;
        oldWriter.Write(new byte[] { 1, 2, 3, 4 });
        if (oldCommitted) { old.EndAppend(83, 2).Unwrap(); }
        else { old.Dispose(); }
        Assert.IsType<RbfStateError>(old.EndAppend(84).Error);
        Assert.IsType<RbfStateError>(old.EndAppend(84, -1).Error);
        Assert.Throws<InvalidOperationException>(() => oldWriter.Advance(-1));
        Assert.Throws<InvalidOperationException>(() => { oldWriter.ReserveSpan(-1, out _); });

        using var current = fixture.File.BeginAppend(1, 1, out var early);
        Assert.IsType<RbfStateError>(old.EndAppend(84).Error);
        Assert.IsType<RbfStateError>(old.EndAppend(84, -1).Error);
        old.Dispose(); // A stale Dispose cannot cancel the new epoch.
        Assert.Throws<InvalidOperationException>(() => oldWriter.Advance(0));
        Assert.Throws<InvalidOperationException>(() => { oldWriter.GetSpan(); });
        Assert.Throws<InvalidOperationException>(() => { oldWriter.GetMemory(); });
        Assert.Throws<InvalidOperationException>(() => { oldWriter.ReserveSpan(1, out _); });
        Assert.Throws<InvalidOperationException>(() => oldWriter.Commit(0));
        Assert.Throws<InvalidOperationException>(() => { oldWriter.TryGetReservedSpan(0, out _); });
        current.PayloadAndMeta.Write(new byte[] { 9, 10 });
        Assert.Equal(early, current.EndAppend(85).Unwrap());
        Rbf3WriterOracle.AssertWire(fixture.ReadFrame(early), new byte[] { 9 }, new byte[] { 10 }, 85);
    }

    [Fact]
    public void DefaultDisposedAndBuildingOwners_RejectBeforeInvalidLengths() {
        RbfFrameBuilder emptyBuilder = default;
        RbfPayloadWriter emptyWriter = default;
        Assert.Throws<InvalidOperationException>(() => { emptyBuilder.EndAppend(83); });
        Assert.Throws<InvalidOperationException>(() => { emptyBuilder.EndAppend(83, -1); });
        Assert.Throws<InvalidOperationException>(() => emptyWriter.Advance(-1));
        Assert.Throws<InvalidOperationException>(() => { emptyWriter.ReserveSpan(-1, out _); });
        Assert.Throws<InvalidOperationException>(() => { emptyWriter.GetSpan(); });
        Assert.Throws<InvalidOperationException>(() => { emptyWriter.GetMemory(); });

        using var fixture = new Rbf3WriterFixture();
        var builder = fixture.File.BeginAppend(1, 1, out _);
        var writer = builder.PayloadAndMeta;
        Assert.Throws<InvalidOperationException>(() => { fixture.File.BeginAppend(-1, -1, out _); });
        fixture.File.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { fixture.File.BeginAppend(-1, -1, out _); });
        Assert.Throws<ObjectDisposedException>(() => writer.Advance(-1));
        Assert.Throws<ObjectDisposedException>(() => { writer.ReserveSpan(-1, out _); });
        Assert.IsType<RbfStateError>(builder.EndAppend(83).Error);
        Assert.IsType<RbfStateError>(builder.EndAppend(83, -1).Error);
    }

    [Fact]
    public void ReadOnlyOwner_RejectsBeforeInvalidDeclaration() {
        using var fixture = new Rbf3WriterFixture(readOnly: true);
        Assert.Throws<InvalidOperationException>(() => { fixture.File.BeginAppend(-1, -1, out _); });
        Assert.Equal(4, fixture.ReadBytes().Length);
    }
}
