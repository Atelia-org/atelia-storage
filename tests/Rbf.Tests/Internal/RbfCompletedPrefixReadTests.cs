using System.Buffers;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfCompletedPrefixReadTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-completed-prefix-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Delete(_path);
    }

    [Theory]
    [InlineData(false, RbfCacheMode.Off)]
    [InlineData(true, RbfCacheMode.Off)]
    [InlineData(false, RbfCacheMode.Slots16)]
    [InlineData(true, RbfCacheMode.Slots16)]
    public void ActiveBuilder_AllRandomReadRoutesReadHistoricalFrames_ThenContinueBuilding(bool sized, RbfCacheMode cacheMode) {
        using var file = RbfFile.CreateNew(_path, cacheMode);
        // An aligned Fence word forces actual XOR decoding instead of only exercising key zero.
        byte[] payload = RbfLayout.GetFence(RbfProfile.Rbf3).ToArray()
            .Concat(Enumerable.Range(0, 23).Select(i => (byte)i)).ToArray();
        byte[] meta = new byte[] { 31, 32, 33 };
        var historical = file.Append(7, payload, meta).Unwrap();
        Assert.NotEqual(0u, file.ReadFrameInfo(historical).Unwrap().EscapeKey);
        var emptyMeta = file.Append(8, new byte[] { 41 }).Unwrap();
        // Prime the short tail page before BeginAppend invalidates its overlapping cache entry.
        AssertHistoricalReads(file, historical, payload, meta, 7);
        long completedTail = file.TailOffset;
        using var builder = Begin(file, sized, 5, 2, out var early);
        var writer = builder.PayloadAndMeta;
        writer.Write(new byte[] { 51, 52, 53 });

        AssertHistoricalReads(file, historical, payload, meta, 7);
        AssertHistoricalReads(file, emptyMeta, new byte[] { 41 }, Array.Empty<byte>(), 8);
        Assert.Equal(completedTail, file.TailOffset);
        writer.Write(new byte[] { 54, 55, 61, 62 });
        var completed = builder.EndAppend(9, 2).Unwrap();
        if (sized) { Assert.Equal(early, completed); }
        AssertHistoricalReads(file, completed, new byte[] { 51, 52, 53, 54, 55 }, new byte[] { 61, 62 }, 9);
        AssertHistoricalReads(file, historical, payload, meta, 7);
    }

    [Theory]
    [InlineData(false, RbfCacheMode.Off)]
    [InlineData(true, RbfCacheMode.Off)]
    [InlineData(false, RbfCacheMode.Slots16)]
    [InlineData(true, RbfCacheMode.Slots16)]
    public void ActiveBuilder_OutsideCompletedPrefixRejectsBeforeReadOrRent_AndCancelKeepsHistory(bool sized, RbfCacheMode cacheMode) {
        using var file = RbfFile.CreateNew(_path, cacheMode);
        var historical = file.Append(7, new byte[] { 1 }, new byte[] { 2 }).Unwrap();
        var info = file.ReadFrameInfo(historical).Unwrap();
        long completedTail = file.TailOffset;
        using var builder = Begin(file, sized, 1, 0, out var early);
        builder.PayloadAndMeta.Write(new byte[] { 3 });
        int reads = 0;
        int rents = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        info.Reader.BufferRentObserver = _ => rents++;

        SizedPtr provisional = sized ? early : SizedPtr.Create(completedTail, RbfFile.MeasureWriteSize(1).Unwrap().FrameLength);
        // FrameBytes ending exactly at the completed tail still lack their terminal Fence.
        SizedPtr fenceCrossesTail = SizedPtr.Create(historical.Offset, historical.Length + RbfLayout.FenceSize);
        SizedPtr bodyCrossesTail = SizedPtr.Create(historical.Offset, historical.Length + 2 * RbfLayout.FenceSize);
        foreach (var ticket in new[] { provisional, fenceCrossesTail, bodyCrossesTail,
                     SizedPtr.Create(SizedPtr.MaxOffset, SizedPtr.MaxLength), default(SizedPtr) }) {
            AssertRandomReadThrows<InvalidOperationException>(file, ticket);
        }
        Assert.Equal(0, reads);
        Assert.Equal(0, rents);
        Assert.Equal(completedTail, file.TailOffset);

        builder.Dispose();
        AssertHistoricalReads(file, historical, new byte[] { 1 }, new byte[] { 2 }, 7);
        Assert.Equal(completedTail, file.TailOffset);
        using var next = file.BeginAppend(1, 0, out var replacement);
        Assert.Equal(provisional, replacement);
        next.PayloadAndMeta.Write(new byte[] { 4 });
        Assert.Equal(replacement, next.EndAppend(9).Unwrap());
        AssertHistoricalReads(file, replacement, new byte[] { 4 }, Array.Empty<byte>(), 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveBuilder_ScansBoundariesAndPhysicalSuccessorStillReject(bool sized) {
        using var file = RbfFile.CreateNew(_path);
        var historical = file.Append(7, new byte[] { 1 }).Unwrap();
        var boundary = file.GetScanBoundaryAfter(historical).Unwrap();
        using var builder = Begin(file, sized, 0, 0, out _);

        Assert.Throws<InvalidOperationException>(() => file.ScanForward());
        Assert.Throws<InvalidOperationException>(() => file.ScanReverse());
        Assert.Throws<InvalidOperationException>(() => file.ScanForward(boundary));
        Assert.Throws<InvalidOperationException>(() => file.GetScanBoundaryAfter(historical));
        Assert.Throws<InvalidOperationException>(() => file.ReadFrameInfoImmediatelyAfter(historical));
        // The existing pure address calculation does not touch Building state or I/O.
        Assert.Equal(file.TailOffset, file.GetPhysicalOffsetImmediatelyAfter(historical));
        AssertHistoricalReads(file, historical, new byte[] { 1 }, Array.Empty<byte>(), 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalReadsAndFlush_PreserveUnadvancedBorrowOrPendingReservation(bool reserved) {
        using var fixture = new Rbf3WriterFixture();
        var historical = fixture.File.Append(7, new byte[] { 1 }, new byte[] { 2 }).Unwrap();
        byte[] completedBytes = fixture.ReadBytes();
        long completedTail = fixture.File.TailOffset;
        using var builder = fixture.File.BeginAppend(4, 2, out var early);
        var writer = builder.PayloadAndMeta;
        int reservationToken = -1;
        Span<byte> pending = reserved
            ? writer.ReserveSpan(6, out reservationToken)
            : writer.GetSpan(6)[..6];
        byte[] expected = new byte[] { 3, 4, 5, 6, 7, 8 };
        expected.CopyTo(pending);
        long pendingLength = writer.Length;
        int writes = 0;
        int flushes = 0;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => { writes++; return request.RequestedBytes; },
            BeforeFlush = _ => flushes++
        };

        AssertHistoricalReads(fixture.File, historical, new byte[] { 1 }, new byte[] { 2 }, 7);
        fixture.File.DurableFlush();
        Assert.Equal(0, writes);
        Assert.Equal(1, flushes);
        Assert.Equal(completedTail, fixture.File.TailOffset);
        Assert.Equal(completedBytes, fixture.ReadBytes());
        Assert.Equal(pendingLength, writer.Length);
        Assert.True(pending.SequenceEqual(expected));
        if (reserved) {
            Assert.True(writer.TryGetReservedSpan(reservationToken, out var stillReserved));
            Assert.True(stillReserved.SequenceEqual(expected));
            writer.Commit(reservationToken);
        }
        else { writer.Advance(expected.Length); }

        Assert.Equal(early, builder.EndAppend(9).Unwrap());
        Assert.True(writes > 0);
        // The earlier flush did not grant the newly completed frame a flush of its own.
        Assert.Equal(1, flushes);
        AssertHistoricalReads(fixture.File, early, new byte[] { 3, 4, 5, 6 }, new byte[] { 7, 8 }, 9);
        fixture.File.DurableFlush();
        Assert.Equal(2, flushes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveBuilder_HistoricalReadsStillValidatePayloadAndTrailerCrc(bool corruptTrailer) {
        using var fixture = new Rbf3WriterFixture();
        var historical = fixture.File.Append(7, new byte[] { 1, 2 }, new byte[] { 3 }).Unwrap();
        // Test-only corruption of an already completed frame; no read cache masks the mutation.
        long corruptionOffset = corruptTrailer ? historical.EndOffsetExclusive - 8 : historical.Offset + FrameLayout.PayloadOffset;
        byte[] value = new byte[1];
        Assert.Equal(1, RandomAccess.Read(fixture.Handle, value, corruptionOffset));
        value[0] ^= 1;
        RandomAccess.Write(fixture.Handle, value, corruptionOffset);
        using var builder = fixture.File.BeginAppend();

        Assert.IsType<RbfCrcMismatchError>(fixture.File.ReadFrame(historical, new byte[historical.Length]).Error);
        Assert.IsType<RbfCrcMismatchError>(fixture.File.ReadPooledFrame(historical).Error);
        if (corruptTrailer) {
            Assert.IsType<RbfCrcMismatchError>(fixture.File.ReadFrameInfo(historical).Error);
            Assert.IsType<RbfCrcMismatchError>(fixture.File.ReadTailMeta(historical, new byte[1]).Error);
            Assert.IsType<RbfCrcMismatchError>(fixture.File.ReadPooledTailMeta(historical).Error);
        }
        else {
            // Metadata preview remains L2; relaxing Building does not turn it into a payload CRC read.
            Assert.Equal(historical, fixture.File.ReadFrameInfo(historical).Unwrap().Ticket);
            Assert.Equal(new byte[] { 3 }, fixture.File.ReadTailMeta(historical, new byte[1]).Unwrap().TailMeta.ToArray());
            using var meta = fixture.File.ReadPooledTailMeta(historical).Unwrap();
            Assert.Equal(new byte[] { 3 }, meta.TailMeta.ToArray());
        }
        builder.PayloadAndMeta.Write(new byte[] { 4 });
        var next = builder.EndAppend(9).Unwrap();
        AssertHistoricalReads(fixture.File, next, new byte[] { 4 }, Array.Empty<byte>(), 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveBuilder_FlushFaultAndDisposeStillRejectAllRandomReadRoutesBeforeReadOrRent(bool sized) {
        using var file = RbfFile.CreateNew(_path);
        var historical = file.Append(7, new byte[] { 1 }, new byte[] { 2 }).Unwrap();
        var info = file.ReadFrameInfo(historical).Unwrap();
        using var builder = Begin(file, sized, 1, 0, out _);
        // Repopulate the short tail page during Building before the shared fault is introduced.
        AssertHistoricalReads(file, historical, new byte[] { 1 }, new byte[] { 2 }, 7);
        int reads = 0;
        int rents = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        info.Reader.BufferRentObserver = _ => rents++;
        RbfWriteInstrumentation.Current = new() { BeforeFlush = _ => throw new IOException("Simulated flush failure.") };
        Assert.Throws<IOException>(() => file.DurableFlush());
        RbfWriteInstrumentation.Current = null;

        AssertRandomReadThrows<InvalidOperationException>(file, historical);
        Assert.Equal(0, reads);
        Assert.Equal(0, rents);
        builder.Dispose();
        AssertRandomReadThrows<InvalidOperationException>(file, historical);
        file.Dispose();
        AssertRandomReadThrows<ObjectDisposedException>(file, historical);
        Assert.Equal(0, reads);
        Assert.Equal(0, rents);
    }

    [Fact]
    public void ActiveBuilder_DisposedHealthyOwnerRejectsAllRandomReadRoutes() {
        using var file = RbfFile.CreateNew(_path);
        var historical = file.Append(7, new byte[] { 1 }, new byte[] { 2 }).Unwrap();
        using var builder = file.BeginAppend();
        file.Dispose();
        AssertRandomReadThrows<ObjectDisposedException>(file, historical);
    }

    private static RbfFrameBuilder Begin(IRbfFile file, bool sized, int payloadLength, int metaLength, out SizedPtr early) {
        early = default;
        return sized ? file.BeginAppend(payloadLength, metaLength, out early) : file.BeginAppend();
    }

    private static void AssertRandomReadThrows<T>(IRbfFile file, SizedPtr ticket) where T : Exception {
        // Empty caller buffers also prove that lifecycle/prefix rejection precedes buffer-size handling.
        Assert.Throws<T>(() => { file.ReadFrame(ticket, Span<byte>.Empty); });
        Assert.Throws<T>(() => { file.ReadPooledFrame(ticket); });
        Assert.Throws<T>(() => { file.ReadFrameInfo(ticket); });
        Assert.Throws<T>(() => { file.ReadTailMeta(ticket, Span<byte>.Empty); });
        Assert.Throws<T>(() => { file.ReadPooledTailMeta(ticket); });
    }

    private static void AssertHistoricalReads(IRbfFile file, SizedPtr ticket, byte[] payload, byte[] meta, uint tag) {
        byte[] expected = payload.Concat(meta).ToArray();
        var frame = file.ReadFrame(ticket, new byte[ticket.Length]).Unwrap();
        Assert.Equal(expected, frame.PayloadAndMeta.ToArray());
        Assert.Equal(tag, frame.Tag);
        using var pooledFrame = file.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(expected, pooledFrame.PayloadAndMeta.ToArray());
        Assert.Equal(tag, pooledFrame.Tag);
        var info = file.ReadFrameInfo(ticket).Unwrap();
        Assert.Equal(ticket, info.Ticket);
        Assert.Equal(tag, info.Tag);
        Assert.Equal(payload.Length, info.PayloadLength);
        Assert.Equal(meta.Length, info.TailMetaLength);
        var tailMeta = file.ReadTailMeta(ticket, new byte[meta.Length]).Unwrap();
        Assert.Equal(meta, tailMeta.TailMeta.ToArray());
        using var pooledMeta = file.ReadPooledTailMeta(ticket).Unwrap();
        Assert.Equal(meta, pooledMeta.TailMeta.ToArray());
    }
}
