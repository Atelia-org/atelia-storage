using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfScanBoundaryTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void EmptyAndEofBoundaries_AreEmpty_AndEmptyCanScanNonemptySuffix() {
        using var file = RbfFile.CreateNew(_path);
        Assert.Equal(4, file.TailOffset);
        var empty = file.ScanForward(RbfScanBoundary.Empty).Unwrap().GetEnumerator();
        Assert.False(empty.MoveNext());
        Assert.Null(empty.TerminationError);
        var ticket = file.Append(17, [1, 2, 3], [4, 5]).Unwrap();
        var boundary = file.GetScanBoundaryAfter(ticket).Unwrap();
        Assert.Equal(file.GetPhysicalOffsetImmediatelyAfter(ticket), boundary.EndExclusive);
        // Independent, explicit input vector: LE Tag, LE meta length, LE tombstone, unpadded content.
        Assert.Equal(RollingCrc.CrcForward(new byte[] {17,0,0,0,2,0,0,0,0,0,0,0,1,2,3,4,5}), boundary.AnchorContentCrc32C);
        var eof = file.ScanForward(boundary).Unwrap().GetEnumerator();
        Assert.False(eof.MoveNext());
        Assert.Null(eof.TerminationError);
        var all = file.ScanForward(RbfScanBoundary.Empty).Unwrap().GetEnumerator();
        Assert.True(all.MoveNext());
        Assert.Equal(ticket, all.Current.Ticket);
    }

    [Fact]
    public void MalformedBoundaries_ReturnArgumentErrors() {
        using var file = RbfFile.CreateNew(_path);
        var ticket = file.Append(1, new byte[32]).Unwrap();
        var boundary = file.GetScanBoundaryAfter(ticket).Unwrap();
        Assert.IsType<RbfArgumentError>(file.ScanForward(default(RbfScanBoundary)).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(new RbfScanBoundary(5, default, 0)).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(new RbfScanBoundary(8, default, 0)).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(new RbfScanBoundary(4, default, 1)).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(boundary with {EndExclusive = boundary.EndExclusive + 4}).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(boundary with {EndExclusive = boundary.EndExclusive - 4}).Error);
        Assert.IsType<RbfArgumentError>(file.GetScanBoundaryAfter(default).Error);
        Assert.IsType<RbfCrcMismatchError>(file.ScanForward(boundary with {AnchorContentCrc32C = boundary.AnchorContentCrc32C ^ 1}).Error);
    }

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots16)]
    public void DirectBoundary_DoesNotValidateUnrelatedPrefix_ObservesRealReads(RbfCacheMode mode) {
        SizedPtr first, anchor, suffix;
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            first = file.Append(1, new byte[16384]).Unwrap();
            anchor = file.Append(2, [2]).Unwrap();
            boundary = file.GetScanBoundaryAfter(anchor).Unwrap();
            suffix = file.Append(3, [3]).Unwrap();
        }
        var bytes = File.ReadAllBytes(_path);
        bytes[first.Offset] ^= 1; // corrupt unrelated framing; a prefix scan would fail.
        File.WriteAllBytes(_path, bytes);
        // Exercise direct-boundary reads independently of the public opener's membership validation.
        using (var file = RawRbfTestFile.OpenExisting(_path, cacheMode: mode)) {
            var reads = new List<(long offset, int length, bool raw)>();
            ((RbfFileImpl)file).ReadObserver = (offset, length, raw) => reads.Add((offset, length, raw));
            Assert.Equal(boundary, file.GetScanBoundaryAfter(anchor).Unwrap());
            var scan = file.ScanForward(boundary).Unwrap().GetEnumerator();
            Assert.True(scan.MoveNext());
            Assert.Equal(suffix, scan.Current.Ticket);
            Assert.False(scan.MoveNext());
            Assert.Null(scan.TerminationError);
            Assert.NotEmpty(reads);
            Assert.All(reads.Where(r => !r.raw), r => Assert.True(r.offset >= anchor.Offset));
            Assert.Contains(reads, r => r.raw);
            if (mode == RbfCacheMode.Off) {
                Assert.All(reads, r => Assert.True(r.offset >= anchor.Offset));
            }
            else {
                // Cached pages may include adjacent prefix bytes: no claim of zero raw prefix I/O.
                Assert.All(reads.Where(r => r.raw), r => Assert.True(r.offset >= (anchor.Offset & ~4095L)));
            }
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(28)]
    public void TailRead_OnlyExactHeaderIsEmpty(int length) {
        var bytes = new byte[length];
        RbfLayout.Fence.CopyTo(bytes);
        File.WriteAllBytes(_path, bytes);
        if (length != 4) {
            Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(_path));
            if (length != 5) {
                using var raw = RawRbfTestFile.OpenExisting(_path);
                var reverse = raw.ScanReverse(showTombstone: true).GetEnumerator();
                Assert.False(reverse.MoveNext());
                Assert.IsType<RbfFramingError>(reverse.TerminationError);
            }
        }
        else {
            using var file = RbfFile.OpenReadOnlyExisting(_path);
            var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
            Assert.False(reverse.MoveNext());
            if (length == 4) { Assert.Null(reverse.TerminationError); }
            else { Assert.IsType<RbfFramingError>(reverse.TerminationError); }
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(0)] // HeadLen
    [InlineData(4)] // payload CRC
    [InlineData(-16)] // trailer CRC
    [InlineData(-1)] // tail Fence
    public void AnchorAndTailCorruption_FailsWithoutChangingBytes(int corruption) {
        SizedPtr ticket;
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            file.Append(1, [1]).Unwrap();
            ticket = file.Append(2, [2]).Unwrap();
            boundary = file.GetScanBoundaryAfter(ticket).Unwrap();
        }
        var bytes = File.ReadAllBytes(_path);
        long offset = corruption == -1 ? boundary.EndExclusive - 1 : ticket.Offset + (corruption < 0 ? ticket.Length + corruption : corruption);
        bytes[offset] ^= 1;
        File.WriteAllBytes(_path, bytes);
        using (var file = RawRbfTestFile.OpenExisting(_path)) {
            Assert.True(file.GetScanBoundaryAfter(ticket).IsFailure);
            Assert.True(file.ScanForward(boundary).IsFailure);
            var reverse = file.ScanReverse(showTombstone: true).GetEnumerator();
            if (reverse.MoveNext()) {
                // Reverse is framing-only: deterministic tail callers must checked-read this frame.
                Assert.Equal(ticket, reverse.Current.Ticket);
                Assert.True(file.ReadPooledFrame(reverse.Current.Ticket).IsFailure);
            }
            else { Assert.NotNull(reverse.TerminationError); }
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Fact]
    public void TombstoneAnchorAndSuffix_PreserveWitnessAndFiltering() {
        SizedPtr anchor, suffix;
        using (var file = RawRbfTestFile.CreateLegacy(_path)) {
            anchor = file.Append(7, [1, 2, 3], [4]).Unwrap();
            suffix = file.Append(8, [5]).Unwrap();
        }
        var bytes = File.ReadAllBytes(_path);
        foreach (var (ticket, tag, payloadLength, metaLength) in new[] {
            (anchor, 7u, 3, 1), (suffix, 8u, 1, 0)
        }) {
            var layout = new FrameLayout(payloadLength, metaLength);
            layout.FillTrailer(bytes.AsSpan((int)(ticket.Offset + layout.TrailerCodewordOffset), TrailerCodewordHelper.Size), tag, isTombstone: true);
        }
        File.WriteAllBytes(_path, bytes);
        using var reader = RbfFile.OpenReadOnlyExisting(_path);
        var boundary = reader.GetScanBoundaryAfter(anchor).Unwrap();
        Assert.Equal(RollingCrc.CrcForward(new byte[] {7,0,0,0,1,0,0,0,1,0,0,0,1,2,3,4}), boundary.AnchorContentCrc32C);
        var hidden = reader.ScanForward(boundary).Unwrap().GetEnumerator();
        Assert.False(hidden.MoveNext());
        Assert.Null(hidden.TerminationError);
        var shown = reader.ScanForward(boundary, showTombstone: true).Unwrap().GetEnumerator();
        Assert.True(shown.MoveNext());
        Assert.Equal(suffix, shown.Current.Ticket);
        Assert.True(shown.Current.IsTombstone);
        Assert.False(shown.MoveNext());
        Assert.Null(shown.TerminationError);
        var reverse = reader.ScanReverse(showTombstone: true).GetEnumerator();
        Assert.True(reverse.MoveNext());
        Assert.Equal(suffix, reverse.Current.Ticket);
        Assert.Equal(reader.TailOffset, reader.GetScanBoundaryAfter(reverse.Current.Ticket).Unwrap().EndExclusive);
    }

    [Fact]
    public void BoundaryMethods_RespectBuilderAndDisposeGuards() {
        var file = RbfFile.CreateNew(_path);
        var ticket = file.Append(1, [1]).Unwrap();
        var builder = file.BeginAppend();
        try {
            Assert.Throws<InvalidOperationException>(() => { file.GetScanBoundaryAfter(ticket); });
            Assert.Throws<InvalidOperationException>(() => { file.ScanForward(RbfScanBoundary.Empty); });
        }
        finally { builder.Dispose(); }
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { file.GetScanBoundaryAfter(ticket); });
        Assert.Throws<ObjectDisposedException>(() => { file.ScanForward(RbfScanBoundary.Empty); });
    }

    [Fact]
    public void AlignedNonFrameAnchor_IsFramingError() {
        using var file = RbfFile.CreateNew(_path);
        var ticket = file.Append(1, new byte[64]).Unwrap();
        var fake = SizedPtr.Create(ticket.Offset + 4, ticket.Length - 4);
        var boundary = new RbfScanBoundary(file.GetPhysicalOffsetImmediatelyAfter(fake), fake, 0);
        Assert.IsType<RbfFramingError>(file.ScanForward(boundary).Error);
    }
}
