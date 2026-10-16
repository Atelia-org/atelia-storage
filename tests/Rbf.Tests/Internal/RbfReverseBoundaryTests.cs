using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfReverseBoundaryTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-reverse-boundary-{Guid.NewGuid():N}.rbf");

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        File.Delete(_path);
    }

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots16)]
    public void OldAnchorWithManyLaterFrames_ReadsOnlySelectedPrefix(RbfCacheMode cacheMode) {
        SizedPtr first, anchor;
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            first = file.Append(1, [1]).Unwrap();
            anchor = file.Append(2, [2, 3], [4]).Unwrap();
            boundary = file.GetScanBoundaryAfter(anchor).Unwrap();
            for (int i = 0; i < 200; i++) { file.Append((uint)(i + 3), new byte[4096]).Unwrap(); }
        }

        // Public factory EOF qualification happens before this observation window.
        using var reader = RbfFile.OpenReadOnlyExisting(_path, cacheMode);
        var reads = new List<(long Offset, int Length, bool Raw)>();
        ((RbfFileImpl)reader).ReadObserver = (offset, length, raw) => reads.Add((offset, length, raw));
        var scan = reader.ScanReverse(boundary).Unwrap().GetEnumerator();
        Assert.True(scan.MoveNext());
        Assert.Equal(anchor, scan.Current.Ticket);
        Assert.True(scan.MoveNext());
        Assert.Equal(first, scan.Current.Ticket);
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
        Assert.Contains(reads, read => read.Offset == anchor.Offset && read.Length == anchor.Length && !read.Raw);
        Assert.Contains(reads, read => read.Raw);
        Assert.All(reads.Where(read => !read.Raw), read => Assert.True(read.Offset + read.Length <= boundary.EndExclusive));
        if (cacheMode == RbfCacheMode.Off) {
            Assert.All(reads, read => Assert.True(read.Offset + read.Length <= boundary.EndExclusive));
        }
        else {
            // Cache page prefetch is allowed to include suffix bytes; no logical suffix frame is parsed.
            Assert.Contains(reads, read => read.Raw && read.Offset + read.Length > boundary.EndExclusive);
        }
    }

    [Theory]
    [InlineData(RbfCacheMode.Off, false)]
    [InlineData(RbfCacheMode.Off, true)]
    [InlineData(RbfCacheMode.Slots16, false)]
    [InlineData(RbfCacheMode.Slots16, true)]
    public void EmptyBoundary_ReturnsEmptyWithoutReads(RbfCacheMode cacheMode, bool nonempty) {
        using var file = RbfFile.CreateNew(_path, cacheMode);
        if (nonempty) { file.Append(1, [1]).Unwrap(); }
        int reads = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        var scan = file.ScanReverse(RbfScanBoundary.Empty).Unwrap().GetEnumerator();
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EofBoundary_MatchesExistingReverseOrder(bool showTombstone) {
        SizedPtr[] tickets = WriteFixture((1, [1], [], false), (2, [2], [], true), (3, [3], [], false));
        using var file = RbfFile.OpenReadOnlyExisting(_path);
        var boundary = file.GetScanBoundaryAfter(tickets[^1]).Unwrap();
        var bounded = file.ScanReverse(boundary, showTombstone).Unwrap().GetEnumerator();
        var ordinary = file.ScanReverse(showTombstone).GetEnumerator();
        var actual = new List<SizedPtr>();
        var expected = new List<SizedPtr>();
        while (bounded.MoveNext()) { actual.Add(bounded.Current.Ticket); }
        while (ordinary.MoveNext()) { expected.Add(ordinary.Current.Ticket); }
        Assert.Equal(expected, actual);
        Assert.Null(bounded.TerminationError);
        Assert.Null(ordinary.TerminationError);
        Assert.Equal(showTombstone ? 3 : 2, actual.Count);
    }

    [Theory]
    [InlineData(0)] // default
    [InlineData(1)] // below header
    [InlineData(2)] // negative end
    [InlineData(3)] // unaligned end
    [InlineData(4)] // beyond current tail
    [InlineData(5)] // empty anchor with nonempty end
    [InlineData(6)] // empty anchor with nonzero witness
    [InlineData(7)] // anchor/end mismatch, still within current tail
    [InlineData(8)] // anchor starts in Header
    [InlineData(9)] // anchor is too short
    public void MalformedBoundary_ReturnsArgumentErrorBeforeReads(int vector) {
        using var file = RbfFile.CreateNew(_path);
        var anchor = file.Append(1, new byte[64]).Unwrap();
        var valid = file.GetScanBoundaryAfter(anchor).Unwrap();
        file.Append(2, [2]).Unwrap();
        var malformed = vector switch {
            0 => default,
            1 => new RbfScanBoundary(0, default, 0),
            2 => new RbfScanBoundary(-4, default, 0),
            3 => valid with { EndExclusive = valid.EndExclusive - 1 },
            4 => valid with { EndExclusive = file.TailOffset + 4 },
            5 => new RbfScanBoundary(8, default, 0),
            6 => RbfScanBoundary.Empty with { AnchorContentCrc32C = 1 },
            7 => valid with { EndExclusive = valid.EndExclusive + 4 },
            8 => new RbfScanBoundary(anchor.Length + 4, SizedPtr.Create(0, anchor.Length), 0),
            9 => new RbfScanBoundary(12, SizedPtr.Create(4, 4), 0),
            _ => throw new ArgumentOutOfRangeException(nameof(vector))
        };
        int reads = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        Assert.IsType<RbfArgumentError>(file.ScanReverse(malformed).Error);
        Assert.IsType<RbfArgumentError>(file.ScanForward(malformed).Error);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void WrongContentWitness_IsRejectedWithoutChangingBytes() {
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            boundary = file.GetScanBoundaryAfter(file.Append(1, [1, 2], [3]).Unwrap()).Unwrap();
        }
        byte[] before = File.ReadAllBytes(_path);
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) {
            var changed = boundary with { AnchorContentCrc32C = boundary.AnchorContentCrc32C ^ 1 };
            Assert.IsType<RbfCrcMismatchError>(file.ScanReverse(changed).Error);
            Assert.IsType<RbfCrcMismatchError>(file.ScanForward(changed).Error);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void CrcValidResealedAnchor_MustStillMatchOriginalWitness() {
        var anchor = WriteFixture((7, [1, 2, 3], [4], false))[0];
        RbfScanBoundary original;
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) { original = file.GetScanBoundaryAfter(anchor).Unwrap(); }
        byte[] bytes = File.ReadAllBytes(_path);
        // Valid frame, same physical length, changed unpadded content.
        BuildFrame(7, [9, 2, 3], [4], false).CopyTo(bytes.AsSpan((int)anchor.Offset));
        File.WriteAllBytes(_path, bytes);
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) {
            using var frame = file.ReadPooledFrame(anchor).Unwrap();
            Assert.Equal(new byte[] { 9, 2, 3, 4 }, frame.PayloadAndMeta.ToArray());
            Assert.IsType<RbfCrcMismatchError>(file.ScanReverse(original).Error);
            Assert.NotEqual(original.AnchorContentCrc32C, file.GetScanBoundaryAfter(anchor).Unwrap().AnchorContentCrc32C);
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(0)] // HeadLen
    [InlineData(1)] // payload
    [InlineData(2)] // payload CRC
    [InlineData(3)] // Trailer CRC
    [InlineData(4)] // tail Fence
    public void CorruptAnchor_IsRejectedWithoutSkippingToEarlierFrame(int corruption) {
        SizedPtr[] tickets = WriteFixture((1, [1], [], false), (2, [2, 3, 4, 5], [], false));
        SizedPtr anchor = tickets[^1];
        RbfScanBoundary boundary;
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) { boundary = file.GetScanBoundaryAfter(anchor).Unwrap(); }
        var layout = new FrameLayout(RbfProfile.Rbf3, 4);
        byte[] bytes = File.ReadAllBytes(_path);
        long offset = corruption switch {
            0 => anchor.Offset,
            1 => anchor.Offset + FrameLayout.PayloadOffset,
            2 => anchor.Offset + layout.PayloadCrcOffset,
            3 => anchor.Offset + layout.TrailerCodewordOffset,
            4 => boundary.EndExclusive - 1,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        bytes[offset] ^= 1;
        File.WriteAllBytes(_path, bytes);
        // Local admission is isolated from the public factory's EOF qualification.
        using (var file = RawRbfTestFile.OpenExisting(_path)) {
            var rejected = file.ScanReverse(boundary);
            Assert.True(rejected.IsFailure);
            if (corruption is 0 or 4) { Assert.IsType<RbfFramingError>(rejected.Error); }
            else { Assert.IsType<RbfCrcMismatchError>(rejected.Error); }
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Fact]
    public void AlignedNonFrameAnchor_IsRejectedAsFramingError() {
        using var file = RbfFile.CreateNew(_path);
        var ticket = file.Append(1, new byte[64]).Unwrap();
        var fake = SizedPtr.Create(ticket.Offset + 4, ticket.Length - 4);
        var boundary = new RbfScanBoundary(file.GetPhysicalOffsetImmediatelyAfter(fake), fake, 0);
        Assert.IsType<RbfFramingError>(file.ScanReverse(boundary).Error);
    }

    [Fact]
    public void CrcValidInvalidAnchorStructure_IsRejectedAsFramingError() {
        var anchor = WriteFixture((1, [1, 2, 3, 4], [], false))[0];
        RbfScanBoundary boundary;
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) { boundary = file.GetScanBoundaryAfter(anchor).Unwrap(); }
        byte[] bytes = File.ReadAllBytes(_path);
        var layout = new FrameLayout(RbfProfile.Rbf3, 4);
        var trailer = bytes.AsSpan((int)anchor.Offset + layout.TrailerCodewordOffset, 16);
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)anchor.EndOffsetExclusive - 4));
        for (int i = 0; i < trailer.Length; i++) { trailer[i] ^= (byte)(key >> ((i & 3) * 8)); }
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) | 0x00010000u;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], descriptor);
        RollingCrc.SealCodewordBackward(trailer);
        Assert.True(RollingCrc.CheckCodewordBackward(trailer));
        for (int i = 0; i < trailer.Length; i++) { trailer[i] ^= (byte)(key >> ((i & 3) * 8)); }
        File.WriteAllBytes(_path, bytes);
        using (var file = RawRbfTestFile.OpenExisting(_path)) {
            Assert.IsType<RbfFramingError>(file.ScanReverse(boundary).Error);
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedPrefixDamage_IsDeferredToEnumerationOrFullRead(bool framingDamage) {
        SizedPtr[] tickets = WriteFixture((1, [1, 2, 3, 4], [], false), (2, [5], [], false), (3, [6], [], false));
        RbfScanBoundary boundary;
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) { boundary = file.GetScanBoundaryAfter(tickets[1]).Unwrap(); }
        byte[] bytes = File.ReadAllBytes(_path);
        var layout = new FrameLayout(RbfProfile.Rbf3, 4);
        bytes[tickets[0].Offset + (framingDamage ? layout.TrailerCodewordOffset : FrameLayout.PayloadOffset)] ^= 1;
        File.WriteAllBytes(_path, bytes);
        using (var file = RawRbfTestFile.OpenExisting(_path)) {
            var reads = new List<long>();
            ((RbfFileImpl)file).ReadObserver = (offset, _, _) => reads.Add(offset);
            var scan = file.ScanReverse(boundary).Unwrap().GetEnumerator();
            Assert.All(reads, offset => Assert.True(offset >= tickets[1].Offset));
            Assert.True(scan.MoveNext());
            Assert.Equal(tickets[1], scan.Current.Ticket);
            if (framingDamage) {
                Assert.False(scan.MoveNext());
                Assert.IsType<RbfCrcMismatchError>(scan.TerminationError);
                Assert.False(scan.MoveNext());
                Assert.IsType<RbfCrcMismatchError>(scan.TerminationError);
            }
            else {
                Assert.True(scan.MoveNext());
                Assert.Equal(tickets[0], scan.Current.Ticket);
                Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(scan.Current.Ticket).Error);
                Assert.False(scan.MoveNext());
                Assert.Null(scan.TerminationError);
            }
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TombstoneAnchor_PreservesDecodedWitnessAndFiltering(bool showTombstone) {
        SizedPtr[] tickets = WriteFixture((1, [1], [], false), (7, [2, 3, 4], [5], true), (3, [6], [], false));
        using var file = RbfFile.OpenReadOnlyExisting(_path);
        var boundary = file.GetScanBoundaryAfter(tickets[1]).Unwrap();
        Assert.Equal(RollingCrc.CrcForward(new byte[] { 7, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 3, 4, 5 }), boundary.AnchorContentCrc32C);
        var scan = file.ScanReverse(boundary, showTombstone).Unwrap().GetEnumerator();
        if (showTombstone) {
            Assert.True(scan.MoveNext());
            Assert.Equal(tickets[1], scan.Current.Ticket);
            Assert.True(scan.Current.IsTombstone);
        }
        Assert.True(scan.MoveNext());
        Assert.Equal(tickets[0], scan.Current.Ticket);
        Assert.False(scan.Current.IsTombstone);
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildingGuard_RejectsBeforeReadAndLeavesBuilderActive(bool sized) {
        using var file = RbfFile.CreateNew(_path);
        var boundary = file.GetScanBoundaryAfter(file.Append(1, [1]).Unwrap()).Unwrap();
        long tail = file.TailOffset;
        int reads = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        using var builder = sized ? file.BeginAppend(2, 0, out _) : file.BeginAppend();
        var writer = builder.PayloadAndMeta;
        writer.GetSpan(1)[0] = 2;
        writer.Advance(1);
        Assert.Throws<InvalidOperationException>(() => { file.ScanReverse(boundary); });
        Assert.Throws<InvalidOperationException>(() => { file.ScanReverse(default(RbfScanBoundary)); });
        Assert.Equal(tail, file.TailOffset);
        Assert.Equal(0, reads);
        writer.GetSpan(1)[0] = 3;
        writer.Advance(1);
        var ticket = builder.EndAppend(2).Unwrap();
        Assert.Equal(tail, ticket.Offset);
        using var frame = file.ReadPooledFrame(ticket).Unwrap();
        Assert.Equal(new byte[] { 2, 3 }, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void Dispose_RejectsNewEntryAndAlreadyAcquiredEnumerator() {
        var file = RbfFile.CreateNew(_path);
        var boundary = file.GetScanBoundaryAfter(file.Append(1, [1]).Unwrap()).Unwrap();
        var scan = file.ScanReverse(boundary).Unwrap().GetEnumerator();
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { file.ScanReverse(boundary); });
        Assert.Throws<ObjectDisposedException>(() => { file.ScanReverse(RbfScanBoundary.Empty); });
        bool rejected = false;
        try { scan.MoveNext(); }
        catch (ObjectDisposedException) { rejected = true; }
        Assert.True(rejected);
        Assert.Null(scan.TerminationError);
    }

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots16)]
    public void SharedWriteFault_RejectsNewEntryAndOldEnumeratorBeforeRead(RbfCacheMode cacheMode) {
        using var file = RbfFile.CreateNew(_path, cacheMode);
        var boundary = file.GetScanBoundaryAfter(file.Append(1, [1]).Unwrap()).Unwrap();
        var scan = file.ScanReverse(boundary).Unwrap().GetEnumerator();
        Assert.True(scan.MoveNext());
        int reads = 0;
        ((RbfFileImpl)file).ReadObserver = (_, _, _) => reads++;
        RbfWriteInstrumentation.Current = new() { BeforeFlush = _ => throw new IOException("Injected flush failure.") };
        Assert.Throws<IOException>(() => file.DurableFlush());
        RbfWriteInstrumentation.Current = null;
        Assert.Throws<InvalidOperationException>(() => { file.ScanReverse(boundary); });
        Assert.Throws<InvalidOperationException>(() => { file.ScanReverse(default(RbfScanBoundary)); });
        bool rejected = false;
        try { scan.MoveNext(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionIoException_PropagatesAndAllowsSubsequentHealthyRead(bool duringFenceRead) {
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            boundary = file.GetScanBoundaryAfter(file.Append(1, new byte[8192]).Unwrap()).Unwrap();
        }
        byte[] before = File.ReadAllBytes(_path);
        using (var file = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off)) {
            var failure = new IOException("Injected admission read failure.");
            long failingOffset = duringFenceRead ? boundary.EndExclusive - 4 : boundary.AnchorTicket.Offset;
            ((RbfFileImpl)file).ReadObserver = (offset, _, raw) => { if (!raw && offset == failingOffset) { throw failure; } };
            Assert.Same(failure, Assert.Throws<IOException>(() => { file.ScanReverse(boundary); }));
            ((RbfFileImpl)file).ReadObserver = null;
            var scan = file.ScanReverse(boundary).Unwrap().GetEnumerator();
            Assert.True(scan.MoveNext());
            Assert.Equal(boundary.AnchorTicket, scan.Current.Ticket);
            Assert.False(scan.MoveNext());
            Assert.Null(scan.TerminationError);
        }
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    [Fact]
    public void EnumeratorIoException_PropagatesInsteadOfBecomingNormalEof() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var boundary = file.GetScanBoundaryAfter(file.Append(1, [1]).Unwrap()).Unwrap();
        var scan = file.ScanReverse(boundary).Unwrap().GetEnumerator();
        var failure = new IOException("Injected enumeration read failure.");
        ((RbfFileImpl)file).ReadObserver = (_, _, raw) => { if (!raw) { throw failure; } };
        Exception? thrown = null;
        try { scan.MoveNext(); }
        catch (IOException error) { thrown = error; }
        Assert.Same(failure, thrown);
        Assert.Null(scan.TerminationError);
        ((RbfFileImpl)file).ReadObserver = null;
        Assert.True(scan.MoveNext());
        Assert.Equal(boundary.AnchorTicket, scan.Current.Ticket);
    }

    [Fact]
    public void BadCurrentTail_StillFailsPublicColdOpenDespiteHealthyOldAnchor() {
        SizedPtr[] tickets = WriteFixture((1, [1], [], false), (2, [2], [], false), (3, [3], [], false));
        RbfScanBoundary boundary;
        using (var file = RbfFile.OpenReadOnlyExisting(_path)) { boundary = file.GetScanBoundaryAfter(tickets[1]).Unwrap(); }
        byte[] bytes = File.ReadAllBytes(_path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(_path, bytes);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(_path));
        using (var raw = RawRbfTestFile.OpenExisting(_path)) {
            var scan = raw.ScanReverse(boundary).Unwrap().GetEnumerator();
            Assert.True(scan.MoveNext());
            Assert.Equal(tickets[1], scan.Current.Ticket);
            Assert.True(scan.MoveNext());
            Assert.Equal(tickets[0], scan.Current.Ticket);
            Assert.False(scan.MoveNext());
            Assert.Null(scan.TerminationError);
        }
        Assert.Equal(bytes, File.ReadAllBytes(_path));
    }

    [Fact]
    public void Rbf3NormalWriter_NestedImagesInPayloadDoNotBecomeScanMembers() {
        byte[] currentFrame = BuildFrame(72, [4, 5, 6], [], false);
        byte[] payload = "RBF3"u8.ToArray().Concat(currentFrame).Concat("RBF3"u8.ToArray()).ToArray();
        SizedPtr first, anchor;
        RbfScanBoundary boundary;
        using (var file = RbfFile.CreateNew(_path)) {
            first = file.Append(1, [1]).Unwrap();
            using var builder = file.BeginAppend(payload.Length, 0, out var declared);
            var writer = builder.PayloadAndMeta;
            payload.CopyTo(writer.GetSpan(payload.Length));
            writer.Advance(payload.Length);
            anchor = builder.EndAppend(2).Unwrap();
            Assert.Equal(declared, anchor);
            boundary = file.GetScanBoundaryAfter(anchor).Unwrap();
            file.Append(3, [3]).Unwrap();
        }
        byte[] bytes = File.ReadAllBytes(_path);
        for (long offset = anchor.Offset; offset < anchor.EndOffsetExclusive; offset += 4) {
            Assert.NotEqual(0x33464252u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)));
        }
        using var reader = RbfFile.OpenReadOnlyExisting(_path);
        var scan = reader.ScanReverse(boundary).Unwrap().GetEnumerator();
        Assert.True(scan.MoveNext());
        Assert.Equal(anchor, scan.Current.Ticket);
        using var frame = reader.ReadPooledFrame(scan.Current.Ticket).Unwrap();
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
        Assert.True(scan.MoveNext());
        Assert.Equal(first, scan.Current.Ticket);
        Assert.False(scan.MoveNext());
        Assert.Null(scan.TerminationError);
    }

    private SizedPtr[] WriteFixture(params (uint Tag, byte[] Payload, byte[] Meta, bool Tombstone)[] frames) {
        using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        ReadOnlySpan<byte> fence = "RBF3"u8;
        stream.Write(fence);
        var tickets = new List<SizedPtr>();
        foreach (var frame in frames) {
            byte[] wire = BuildFrame(frame.Tag, frame.Payload, frame.Meta, frame.Tombstone);
            tickets.Add(SizedPtr.Create(stream.Position, wire.Length));
            stream.Write(wire);
            stream.Write(fence);
        }
        return tickets.ToArray();
    }

    private static byte[] BuildFrame(uint tag, byte[] payload, byte[] meta, bool tombstone) {
        var layout = new FrameLayout(RbfProfile.Rbf3, payload.Length, meta.Length);
        byte[] bytes = new byte[layout.FrameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, layout.WireFrameLength);
        payload.CopyTo(bytes.AsSpan(FrameLayout.PayloadOffset));
        meta.CopyTo(bytes.AsSpan(FrameLayout.PayloadOffset + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(layout.PayloadCrcOffset),
            RollingCrc.CrcForward(bytes.AsSpan(FrameLayout.PayloadOffset, layout.PayloadCrcCoverageLength)));
        layout.FillTrailer(bytes.AsSpan(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, tombstone);
        const uint key = 0xE1020304u;
        for (int offset = 4; offset < bytes.Length - 4; offset++) { bytes[offset] ^= (byte)(key >> (((offset - 4) & 3) * 8)); }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), key);
        for (int offset = 0; offset < bytes.Length; offset += 4) {
            Assert.NotEqual(0x33464252u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        }
        return bytes;
    }
}
