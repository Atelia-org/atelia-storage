using Atelia.Data;
using Atelia.Rbf.ReadCache;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfCandidateTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-candidate-{Guid.NewGuid()}.bin");
    public void Dispose() => File.Delete(_path);

    private (SizedPtr First, SizedPtr Outside, long Eof) WriteFrames() {
        using var file = RbfFile.CreateNew(_path);
        var first = file.Append(7, new byte[5000], "meta"u8).Unwrap();
        var outside = file.Append(8, new byte[8000], "outside-meta"u8).Unwrap();
        return (first, outside, file.GetPhysicalOffsetImmediatelyAfter(first));
    }

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots2)]
    [InlineData(RbfCacheMode.Slots4)]
    [InlineData(RbfCacheMode.Slots8)]
    [InlineData(RbfCacheMode.Slots16)]
    [InlineData(RbfCacheMode.Slots32)]
    [InlineData(RbfCacheMode.Slots64)]
    public void AllRoutes_ExcludeCompleteOutsideFrame_AndKeepPrefetchWithinEof(RbfCacheMode mode) {
        var (first, outside, eof) = WriteFrames();
        using var file = RbfFile.OpenReadOnlyCandidate(_path, eof, mode);
        var observations = new List<(long Offset, int Length, bool Raw)>();
        ((RbfFileImpl)file).ReadObserver = (offset, length, raw) => observations.Add((offset, length, raw));
        var info = file.ReadFrameInfo(first).Unwrap();
        Assert.Equal(7u, info.Tag);
        int rentCount = 0;
        info.Reader.BufferRentObserver = _ => rentCount++;
        for (int repeat = 0; repeat < 2; repeat++) {
            using var frame = info.ReadPooledFrame().Unwrap();
            Assert.Equal(5004, frame.PayloadAndMeta.Length);
            using var meta = info.ReadPooledTailMeta().Unwrap();
            Assert.True(meta.TailMeta.SequenceEqual("meta"u8));
            Assert.True(file.ReadFrame(first, new byte[first.Length]).IsSuccess);
            Assert.True(file.ReadTailMeta(first, new byte[4]).IsSuccess);
        }
        rentCount = 0;
        Assert.True(file.ReadPooledFrame(outside).IsFailure);
        Assert.True(file.ReadFrame(outside, new byte[outside.Length]).IsFailure);
        Assert.True(file.ReadFrameInfo(outside).IsFailure);
        Assert.True(file.ReadTailMeta(outside, new byte[12]).IsFailure);
        Assert.True(file.ReadPooledTailMeta(outside).IsFailure);
        Assert.True(file.ReadFrameInfoImmediatelyAfter(outside).IsFailure);
        Assert.True(file.ReadFrameInfoImmediatelyAfter(first).IsSuccess);
        Assert.True(file.GetScanBoundaryAfter(outside).IsFailure);

        var forward = file.ScanForward(true).GetEnumerator();
        Assert.True(forward.MoveNext());
        Assert.Equal(first, forward.Current.Ticket);
        Assert.False(forward.MoveNext());
        Assert.Null(forward.TerminationError);
        var reverse = file.ScanReverse(true).GetEnumerator();
        Assert.True(reverse.MoveNext());
        Assert.Equal(first, reverse.Current.Ticket);
        Assert.False(reverse.MoveNext());
        Assert.Null(reverse.TerminationError);

        // An info forged with this candidate's reader must still fail, including zero-meta fast paths.
        var forged = new RbfFrameInfo(info.Reader, outside, 8, 8000, 12, false);
        Assert.True(forged.ReadPooledFrame().IsFailure);
        Assert.True(forged.ReadFrame(new byte[outside.Length]).IsFailure);
        Assert.True(forged.ReadTailMeta(new byte[12]).IsFailure);
        Assert.True(forged.ReadPooledTailMeta().IsFailure);
        var emptyMeta = new RbfFrameInfo(info.Reader, outside, 8, 8012, 0, false);
        Assert.True(emptyMeta.ReadTailMeta(Span<byte>.Empty).IsFailure);
        Assert.True(emptyMeta.ReadPooledTailMeta().IsFailure);
        Assert.Equal(0, rentCount);
        Assert.NotEmpty(observations);
        Assert.All(observations, item => Assert.True(item.Offset >= 0 && item.Length <= eof - item.Offset));
    }

    [Fact]
    public void InvalidHugeTickets_AreRejectedBeforePoolAllocationOrReads() {
        var (first, _, eof) = WriteFrames();
        using var file = RbfFile.OpenReadOnlyCandidate(_path, eof);
        var info = file.ReadFrameInfo(first).Unwrap();
        int readCount = 0;
        int rentCount = 0;
        info.Reader.BufferRentObserver = _ => rentCount++;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => readCount++;
        var huge = SizedPtr.Create(4, SizedPtr.MaxLength);
        // Warm the error path before measuring; a mistaken pool rent would dwarf this bound.
        Assert.True(file.ReadPooledFrame(huge).IsFailure);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(file.ReadPooledFrame(huge).IsFailure);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        Assert.True(file.ReadPooledFrame(SizedPtr.Create(SizedPtr.MaxOffset, SizedPtr.MaxLength)).IsFailure);
        Assert.True(file.ReadFrameInfo(default).IsFailure);
        Assert.Equal(0, readCount);
        Assert.Equal(0, rentCount);
    }

    [Fact]
    public void CandidateRejectsMutation_WithoutChangingSource() {
        var (_, _, eof) = WriteFrames();
        byte[] original = File.ReadAllBytes(_path);
        using (var file = RbfFile.OpenReadOnlyCandidate(_path, eof)) {
            Assert.Throws<InvalidOperationException>(() => file.Append(1, new byte[1]));
            Assert.Throws<InvalidOperationException>(() => file.BeginAppend());
            Assert.Throws<InvalidOperationException>(() => file.DurableFlush());
            Assert.Equal(eof, file.TailOffset);
        }
        Assert.Equal(original, File.ReadAllBytes(_path));
    }

    [Fact]
    public void CandidateAcceptsUnalignedPhysicalTail_AndHeaderOnly() {
        var (_, _, eof) = WriteFrames();
        using (var append = new FileStream(_path, FileMode.Append, FileAccess.Write)) { append.WriteByte(0); }
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(_path));
        using var candidate = RbfFile.OpenReadOnlyCandidate(_path, eof);
        using var header = RbfFile.OpenReadOnlyCandidate(_path, 4);
        var scan = header.ScanForward().GetEnumerator();
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
        Assert.Throws<ArgumentOutOfRangeException>(() => RbfFile.OpenReadOnlyCandidate(_path, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RbfFile.OpenReadOnlyCandidate(_path, eof + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RbfFile.OpenReadOnlyCandidate(_path, long.MaxValue - 3));
    }

    [Fact]
    public void FullFrameBytesWithoutItsWholeFence_AreRejected() {
        var (first, _, _) = WriteFrames();
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new RandomAccessReader(handle, first.EndOffsetExclusive);
        int calls = 0;
        reader.ReadObserver = (_, _, _) => calls++;
        Assert.True(RbfReadImpl.ReadPooledFrame(reader, first).IsFailure);
        Assert.True(RbfReadImpl.ReadFrameInfo(reader, first).IsFailure);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CandidateConstructionRejectsHeaderFenceAndTrailerContradictions(int corruption) {
        var (_, _, eof) = WriteFrames();
        byte[] bytes = File.ReadAllBytes(_path);
        int offset = corruption switch {
            0 => 0,
            1 => checked((int)eof - 1),
            _ => checked((int)eof - RbfLayout.FenceSize - TrailerCodewordHelper.Size)
        };
        bytes[offset] ^= 0xFF;
        File.WriteAllBytes(_path, bytes);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyCandidate(_path, eof));
        // Failed construction closes its handle.
        using var exclusive = File.OpenHandle(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void DirectTicketReadRequiresActualFence_NotOnlyRange() {
        var (first, _, eof) = WriteFrames();
        byte[] bytes = File.ReadAllBytes(_path);
        bytes[checked((int)first.EndOffsetExclusive)] ^= 0xFF;
        File.WriteAllBytes(_path, bytes);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new RandomAccessReader(handle, eof);
        int rents = 0;
        reader.BufferRentObserver = _ => rents++;
        Assert.True(RbfReadImpl.ReadPooledFrame(reader, first).IsFailure);
        Assert.True(RbfReadImpl.ReadFrameInfo(reader, first).IsFailure);
        Assert.Equal(0, rents);
    }

    private sealed class BudgetStop : Exception { }

    [Fact]
    public void BudgetAndIoExceptions_PropagateUnchanged() {
        var (first, _, eof) = WriteFrames();
        Assert.Throws<BudgetStop>(() => RbfFile.OpenReadOnlyCandidate(_path, eof, beforeRead: _ => throw new BudgetStop()));
        Assert.Throws<IOException>(() => RbfFile.OpenReadOnlyCandidate(_path, eof, beforeRead: _ => throw new IOException("injected")));
        bool stop = false;
        using var file = RbfFile.OpenReadOnlyCandidate(_path, eof, beforeRead: _ => { if (stop) { throw new BudgetStop(); } });
        stop = true;
        Assert.Throws<BudgetStop>(() => file.ReadPooledFrame(first));
    }

    [Fact]
    public void CancellationIsObservedEvenWithWarmCache() {
        var (first, _, eof) = WriteFrames();
        using var cancellation = new CancellationTokenSource();
        using var file = RbfFile.OpenReadOnlyCandidate(_path, eof, RbfCacheMode.Slots16, token: cancellation.Token);
        var info = file.ReadFrameInfo(first).Unwrap();
        using (var frame = info.ReadPooledFrame().Unwrap()) { }
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => info.ReadPooledFrame());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderClipsBothCacheHitsAndRawPrefetch(bool cached) {
        File.WriteAllBytes(_path, new byte[16384]);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int eof = 5000;
        var charged = new List<int>();
        using RandomAccessReader reader = cached
            ? new ReverseReadCache(handle, fixedEof: eof, beforeRead: charged.Add)
            : new RandomAccessReader(handle, eof, charged.Add);
        var observed = new List<(long Offset, int Count)>();
        reader.ReadObserver = (offset, count, _) => observed.Add((offset, count));
        Assert.Equal(100, reader.Read(new byte[100], 4900));
        Assert.Equal(100, reader.Read(new byte[300], 4900));
        Assert.Equal(0, reader.Read(new byte[20], eof));
        Assert.Equal(0, reader.Read(new byte[20], long.MaxValue));
        Assert.All(observed, read => Assert.True(read.Count <= eof - read.Offset));
        Assert.All(charged, count => Assert.InRange(count, 1, eof));
    }
}
