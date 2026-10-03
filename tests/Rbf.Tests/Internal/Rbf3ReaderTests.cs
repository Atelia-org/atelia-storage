using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf.ReadCache;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Independent RBF3 wire fixtures exercise every production reader route.</summary>
public sealed class Rbf3ReaderTests : IDisposable {
    private const uint Fence = 0x33464252u;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf3-reader-{Guid.NewGuid()}.bin");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void DefaultFrameInfo_ReadMethodsThrowInitializationError() {
        RbfFrameInfo info = default;
        const string message = "Frame info is not initialized.";

        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => info.ReadTailMeta(Array.Empty<byte>())).Message);
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => info.ReadPooledTailMeta()).Message);
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => info.ReadFrame(Array.Empty<byte>())).Message);
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => info.ReadPooledFrame()).Message);
    }

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots2)]
    [InlineData(RbfCacheMode.Slots4)]
    [InlineData(RbfCacheMode.Slots8)]
    [InlineData(RbfCacheMode.Slots16)]
    [InlineData(RbfCacheMode.Slots32)]
    [InlineData(RbfCacheMode.Slots64)]
    public void AllCheckedRoutes_HighKeys_KeepCacheEncoded(RbfCacheMode mode) {
        foreach (uint key in new uint[] { 0, 0xE1020304u, uint.MaxValue }) {
            byte[] payload = Bytes(12003);
            byte[] meta = Bytes(101);
            byte[] wire = BuildFrame(payload, meta, key);
            WriteImage(wire);
            using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using RandomAccessReader reader = CreateReader(handle, mode);
            SizedPtr ticket = SizedPtr.Create(4, wire.Length);
            var info = RbfReadImpl.ReadFrameInfo(reader, ticket).Unwrap();
            Assert.Equal(key, info.EscapeKey);
            Assert.Equal(payload.Length, info.PayloadLength);

            for (int repeat = 0; repeat < 2; repeat++) {
                byte[] buffer = new byte[ticket.Length];
                var frame = RbfReadImpl.ReadFrame(reader, ticket, buffer).Unwrap();
                Assert.True(frame.PayloadAndMeta[..payload.Length].SequenceEqual(payload));
                Assert.True(frame.PayloadAndMeta[payload.Length..].SequenceEqual(meta));
                using var pooled = RbfReadImpl.ReadPooledFrame(reader, ticket).Unwrap();
                Assert.True(pooled.PayloadAndMeta.SequenceEqual(frame.PayloadAndMeta));
                var fromInfo = info.ReadFrame(buffer).Unwrap();
                Assert.True(fromInfo.PayloadAndMeta[..payload.Length].SequenceEqual(payload));
                Assert.True(fromInfo.PayloadAndMeta[payload.Length..].SequenceEqual(meta));
                using var pooledFromInfo = info.ReadPooledFrame().Unwrap();
                Assert.True(pooledFromInfo.PayloadAndMeta[..payload.Length].SequenceEqual(payload));
                Assert.True(pooledFromInfo.PayloadAndMeta[payload.Length..].SequenceEqual(meta));
                byte[] raw = new byte[wire.Length];
                Assert.Equal(raw.Length, reader.Read(raw, ticket.Offset));
                Assert.Equal(wire, raw);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 65535)]
    public void Preview_UsesBodyBytePhase_AndRentsOnlyMeta(int payloadPhase, int metaLength) {
        byte[] payload = Bytes(128 + payloadPhase);
        byte[] meta = Bytes(metaLength);
        byte[] wire = BuildFrame(payload, meta, 0xE1020304u);
        WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3);
        var info = RbfReadImpl.ReadFrameInfo(reader, SizedPtr.Create(4, wire.Length)).Unwrap();
        var rents = new List<int>();
        var reads = new List<int>();
        reader.BufferRentObserver = rents.Add;
        reader.ReadObserver = (_, length, raw) => { if (!raw) { reads.Add(length); } };

        byte[] buffer = new byte[metaLength + 2];
        buffer[^1] = 0xA5;
        var preview = info.ReadTailMeta(buffer).Unwrap();
        Assert.True(preview.TailMeta.SequenceEqual(meta));
        Assert.Equal(0xA5, buffer[^1]);
        using var pooled = info.ReadPooledTailMeta().Unwrap();
        Assert.True(pooled.TailMeta.SequenceEqual(meta));
        Assert.Equal(metaLength == 0 ? Array.Empty<int>() : new[] { metaLength }, rents);
        Assert.All(reads, length => Assert.Equal(metaLength, length));

        reader.MarkWriteFaulted();
        Assert.Throws<InvalidOperationException>(() => info.ReadPooledTailMeta());
        Assert.Throws<InvalidOperationException>(() => info.ReadPooledFrame());
    }

    [Fact]
    public void Reverse_IsOneTailRead_AndDoesNotReadHead() {
        byte[] wire = BuildFrame(Bytes(9), Bytes(3), uint.MaxValue);
        // Reverse metadata intentionally treats its checked Trailer as the length authority.
        BinaryPrimitives.WriteUInt32LittleEndian(wire, 0x40000007u);
        byte[] image = WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3);
        var reads = new List<(long Offset, int Length)>();
        reader.ReadObserver = (offset, length, raw) => { if (!raw) { reads.Add((offset, length)); } };
        var info = RbfReadImpl.ReadTrailerBefore(reader, image.Length).Unwrap();
        Assert.Equal(SizedPtr.Create(4, wire.Length), info.Ticket);
        Assert.Equal(new[] { ((long)image.Length - 24, 24) }, reads);
        Assert.True(RbfReadImpl.ReadFrameInfoAt(reader, 4, image.Length).IsFailure);
        Assert.True(info.ReadFrame(new byte[wire.Length]).IsFailure);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6u)]
    [InlineData(0x04000000u)]
    [InlineData(0x40000007u)]
    [InlineData(uint.MaxValue)]
    public void Forward_RejectsRawUnitsBeforeShiftOrTailReads(uint units) {
        byte[] wire = BuildFrame([], [], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wire, units);
        byte[] image = WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3);
        var reads = new List<int>();
        reader.ReadObserver = (_, length, raw) => { if (!raw) { reads.Add(length); } };
        Assert.True(RbfReadImpl.ReadFrameInfoAt(reader, 4, image.Length).IsFailure);
        Assert.Equal(new[] { 4 }, reads);
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(0x04000000u)]
    [InlineData(0x40000007u)]
    [InlineData(uint.MaxValue)]
    public void Tail_RejectsCrcSelfConsistentIllegalUnits(uint units) {
        const uint key = 0xE1020304u;
        byte[] wire = BuildFrame([], [], key);
        ScalarXor(wire.AsSpan(4, wire.Length - 8), key);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(wire.Length - 8), units);
        SealTrailer(wire.AsSpan(wire.Length - 20, 16));
        ScalarXor(wire.AsSpan(4, wire.Length - 8), key);
        byte[] image = WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3);
        Assert.True(RbfReadImpl.ReadFrameInfo(reader, SizedPtr.Create(4, wire.Length)).IsFailure);
        Assert.True(RbfReadImpl.ReadTrailerBefore(reader, image.Length).IsFailure);
        Assert.True(RbfReadImpl.ReadFrame(reader, SizedPtr.Create(4, wire.Length), new byte[wire.Length]).IsFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TicketReads_RejectBothCrcFailures_InfoReadsRejectContentCorruption(bool damageTrailer) {
        byte[] wire = BuildFrame(Bytes(5), Bytes(3), uint.MaxValue);
        WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new ReverseReadCache(handle, profile: RbfProfile.Rbf3);
        SizedPtr ticket = SizedPtr.Create(4, wire.Length);
        var info = RbfReadImpl.ReadFrameInfo(reader, ticket).Unwrap();
        int damaged = damageTrailer ? wire.Length - 20 : 4;
        wire[damaged] ^= 0x80;
        WriteImage(wire);
        reader.InvalidateFrom(0);

        Assert.True(RbfReadImpl.ReadFrame(reader, ticket, new byte[wire.Length]).IsFailure);
        Assert.True(RbfReadImpl.ReadPooledFrame(reader, ticket).IsFailure);
        if (!damageTrailer) {
            Assert.True(info.ReadFrame(new byte[wire.Length]).IsFailure);
            Assert.True(info.ReadPooledFrame().IsFailure);
            // Metadata qualification and preview remain independent of PayloadCRC.
            Assert.True(RbfReadImpl.ReadFrameInfo(reader, ticket).IsSuccess);
            Assert.True(info.ReadTailMeta(new byte[3]).IsSuccess);
        }
    }

    [Fact]
    public void Tail_RejectsRawFenceKey_AndVisibleEncodedFence() {
        byte[] wire = BuildFrame(Bytes(8), [], 0xE1020304u);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(wire.Length - 4), Fence);
        WriteImage(wire);
        using (var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read))
        using (var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3)) {
            Assert.True(RbfReadImpl.ReadFrameInfo(reader, SizedPtr.Create(4, wire.Length)).IsFailure);
        }
        wire = BuildFrame(Bytes(8), [], 0xE1020304u);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(wire.Length - 20), Fence);
        WriteImage(wire);
        using var secondHandle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var secondReader = new RandomAccessReader(secondHandle, profile: RbfProfile.Rbf3);
        var result = RbfReadImpl.ReadFrameInfo(secondReader, SizedPtr.Create(4, wire.Length));
        Assert.IsType<RbfFramingError>(result.Error);
        Assert.Contains("contains Fence", result.Error!.Message);
    }

    [Fact]
    public void Scans_FilterEncodedTombstones_AndPreservePhysicalTickets() {
        byte[] tombstone = BuildFrame(Bytes(1), [], uint.MaxValue, isTombstone: true);
        byte[] live = BuildFrame(Bytes(7), Bytes(1), 0xE1020304u);
        byte[] image = WriteImage(tombstone, live);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        using var reader = new ReverseReadCache(handle, profile: RbfProfile.Rbf3);
        SizedPtr liveTicket = SizedPtr.Create(8 + tombstone.Length, live.Length);
        var forward = new RbfForwardEnumerator(reader, 4, image.Length, false);
        Assert.True(forward.MoveNext());
        Assert.Equal(liveTicket, forward.Current.Ticket);
        Assert.False(forward.MoveNext());
        Assert.Null(forward.TerminationError);
        var reverse = new RbfReverseEnumerator(reader, image.Length, false);
        Assert.True(reverse.MoveNext());
        Assert.Equal(liveTicket, reverse.Current.Ticket);
        Assert.False(reverse.MoveNext());
        Assert.Null(reverse.TerminationError);
        var all = new RbfReverseEnumerator(reader, image.Length, true);
        Assert.True(all.MoveNext());
        Assert.True(all.MoveNext());
        Assert.True(all.Current.IsTombstone);
    }

    [Fact]
    public void Boundary_UsesDecodedContentWitness_AndRequiresProfileFence() {
        byte[] payload = Bytes(5);
        byte[] meta = Bytes(3);
        byte[] wire = BuildFrame(payload, meta, uint.MaxValue);
        byte[] image = WriteImage(wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3);
        SizedPtr ticket = SizedPtr.Create(4, wire.Length);
        var boundary = RbfReadImpl.GetScanBoundaryAfter(reader, ticket, image.Length, image.Length).Unwrap();
        byte[] witness = new byte[12 + payload.Length + meta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(witness, 0x12345678u);
        BinaryPrimitives.WriteUInt32LittleEndian(witness.AsSpan(4), (uint)meta.Length);
        payload.CopyTo(witness.AsSpan(12));
        meta.CopyTo(witness.AsSpan(12 + payload.Length));
        Assert.Equal(ScalarCrc(witness), boundary.AnchorContentCrc32C);
        image[^1] ^= 1;
        File.WriteAllBytes(_path, image);
        Assert.True(RbfReadImpl.ReadFrame(reader, ticket, new byte[wire.Length]).IsSuccess);
        Assert.True(RbfReadImpl.GetScanBoundaryAfter(reader, ticket, image.Length, image.Length).IsFailure);
    }

    [Fact]
    public void CandidateGuard_UsesRbf3Fence_AndRejectsOutsideWithoutRent() {
        byte[] wire = BuildFrame(Bytes(3), [], 0xE1020304u);
        byte[] image = WriteImage(wire, wire);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read);
        long eof = 8 + wire.Length;
        using var reader = new ReverseReadCache(handle, fixedEof: eof, profile: RbfProfile.Rbf3);
        Assert.True(RbfReadImpl.ReadFrameInfo(reader, SizedPtr.Create(4, wire.Length)).IsSuccess);
        int rents = 0;
        reader.BufferRentObserver = _ => rents++;
        Assert.True(RbfReadImpl.ReadPooledFrame(reader, SizedPtr.Create(eof, wire.Length)).IsFailure);
        Assert.Equal(0, rents);
        Assert.True(image.Length > eof);
    }

    [Fact]
    public void OfflineRecovery_RejectsRbf3FenceRollingCrcAndTruncateWithoutChangingBytes() {
        byte[] wire = BuildFrame(Bytes(3), [], uint.MaxValue);
        byte[] image = WriteImage(wire);
        Assert.Throws<NotSupportedException>(() => RbfRecovery.OpenReadOnly(_path));
        RbfRecoveryHit hit;
        using (var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read))
        using (var reader = new RandomAccessReader(handle, profile: RbfProfile.Rbf3)) {
            var info = RbfReadImpl.ReadFrameInfo(reader, SizedPtr.Create(4, wire.Length)).Unwrap();
            hit = new RbfRecoveryHit(info, 4 + wire.Length, RbfRecoveryConfidence.FrameBoundary);
            foreach (var strategy in new[] { RbfRecoveryBoundarySearchStrategy.Fence, RbfRecoveryBoundarySearchStrategy.RollingCrc }) {
                Assert.Throws<NotSupportedException>(() => {
                    var scan = new RbfRecoveryEnumerator(reader, image.Length, new() { BoundarySearchStrategy = strategy });
                    scan.MoveNext();
                });
            }
        }
        Assert.Throws<NotSupportedException>(() => RbfRecovery.TruncateToSuggestedTail(_path, hit));
        Assert.Equal(image, File.ReadAllBytes(_path));
    }

    private byte[] WriteImage(params byte[][] frames) {
        byte[] image = new byte[4 + frames.Sum(frame => frame.Length + 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(image, Fence);
        int offset = 4;
        foreach (byte[] frame in frames) {
            frame.CopyTo(image.AsSpan(offset));
            offset += frame.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), Fence);
            offset += 4;
        }
        File.WriteAllBytes(_path, image);
        return image;
    }

    private static RandomAccessReader CreateReader(Microsoft.Win32.SafeHandles.SafeFileHandle handle, RbfCacheMode mode) =>
        mode == RbfCacheMode.Off ? new RandomAccessReader(handle, profile: RbfProfile.Rbf3) : new ReverseReadCache(handle, (int)mode, profile: RbfProfile.Rbf3);

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)((i * 37 + 11) & 255)).ToArray();

    // No production layout, wire codec, XOR or CRC helper participates in fixture construction.
    private static byte[] BuildFrame(byte[] payload, byte[] meta, uint key, bool isTombstone = false) {
        int padding = -(payload.Length + meta.Length) & 3;
        int length = 28 + payload.Length + meta.Length + padding;
        byte[] frame = new byte[length];
        uint units = (uint)length >> 2;
        BinaryPrimitives.WriteUInt32LittleEndian(frame, units);
        payload.CopyTo(frame.AsSpan(4));
        meta.CopyTo(frame.AsSpan(4 + payload.Length));
        int payloadCrcOffset = length - 24;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(payloadCrcOffset), ScalarCrc(frame.AsSpan(4, payloadCrcOffset - 4)));
        Span<byte> trailer = frame.AsSpan(length - 20, 16);
        uint descriptor = (isTombstone ? 0x80000000u : 0) | (uint)padding << 29 | (uint)meta.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], 0x12345678u);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], units);
        SealTrailer(trailer);
        ScalarXor(frame.AsSpan(4, length - 8), key);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(length - 4), key);
        Assert.NotEqual(Fence, key);
        for (int offset = 4; offset < length - 4; offset += 4) {
            Assert.NotEqual(Fence, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset)));
        }
        return frame;
    }

    private static void SealTrailer(Span<byte> trailer) => BinaryPrimitives.WriteUInt32BigEndian(trailer, ScalarCrc(trailer[4..], backward: true));

    private static uint ScalarCrc(ReadOnlySpan<byte> bytes, bool backward = false) {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++) {
            crc ^= bytes[backward ? bytes.Length - 1 - i : i];
            for (int bit = 0; bit < 8; bit++) { crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u); }
        }
        return crc ^ uint.MaxValue;
    }

    private static void ScalarXor(Span<byte> bytes, uint key) {
        for (int i = 0; i < bytes.Length; i++) { bytes[i] ^= (byte)((key >> ((i & 3) * 8)) & 255); }
    }
}
