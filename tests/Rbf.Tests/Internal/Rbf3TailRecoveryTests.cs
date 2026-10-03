using System.Buffers.Binary;
using Atelia.Data;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Independent scalar RBF3 wire images exercise public structure-only open and recovery.</summary>
public sealed class Rbf3TailRecoveryTests : IDisposable {
    private const uint Fence = 0x33464252;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rbf3-tail-{Guid.NewGuid():N}");
    private string ImagePath => Path.Combine(_directory, "image.rbf");

    public Rbf3TailRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(1, 301u)]
    [InlineData(2, 0x80000001u)]
    [InlineData(3, uint.MaxValue)]
    [InlineData(7, 0xFEDCBA98u)]
    [InlineData(64, 0x80000001u)]
    public void EveryNormalTailPrefix_TruncatesIncompleteBody_OrPreservesCompletedBody(int payloadLength, uint key) {
        byte[] prior = MakeFrame(Payload(8));
        byte[] payload = Payload(payloadLength);
        byte[] tail = MakeFrame(payload, key);
        byte[] complete = MakeFile(prior, tail);
        int start = 4 + prior.Length;
        int length = tail.Length - 4;
        var ticket = SizedPtr.Create(start, length);
        for (int cut = start; cut <= complete.Length; cut++) {
            byte[] input = complete[..cut];
            File.WriteAllBytes(ImagePath, input);
            bool needsAction = cut != start && cut != complete.Length;
            if (needsAction) {
                Assert.Throws<InvalidDataException>(() => { using var file = RbfFile.OpenReadOnlyExisting(ImagePath); });
                Assert.Equal(input, File.ReadAllBytes(ImagePath));
            }
            RbfTailRecoveryAction expected = !needsAction ? RbfTailRecoveryAction.None
                : cut < start + length - 4 ? RbfTailRecoveryAction.Truncated : RbfTailRecoveryAction.CompletedTail;
            int expectedLength = cut == start || expected == RbfTailRecoveryAction.Truncated ? start : complete.Length;
            using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
                Assert.Equal(expected, report.Action);
                Assert.Equal(cut, report.OriginalLength);
                Assert.Equal(expectedLength, report.FinalLength);
                Assert.Equal(expectedLength, file.TailOffset);
                Assert.Equal(needsAction ? (long?)start : null, report.AffectedFrameOffset);
                Assert.Equal(expected == RbfTailRecoveryAction.CompletedTail ? (SizedPtr?)ticket : null, report.FrameTicket);
                using var previous = file.ReadPooledFrame(SizedPtr.Create(4, prior.Length - 4)).Unwrap();
                Assert.Equal(Payload(8), previous.PayloadAndMeta.ToArray());
                if (expectedLength == complete.Length) {
                    using var recovered = file.ReadPooledFrame(ticket).Unwrap();
                    Assert.Equal(payload, recovered.PayloadAndMeta.ToArray());
                    Assert.False(recovered.IsTombstone);
                }
            }
            Assert.Equal(complete[..expectedLength], File.ReadAllBytes(ImagePath));
            using var reopened = RbfFile.OpenExisting(ImagePath, out var again);
            Assert.Equal(RbfTailRecoveryAction.None, again.Action);
            Assert.Null(again.FrameTicket);
            var appended = reopened.Append(51, new byte[] { 9, 8, 7 }).Unwrap();
            Assert.Equal(expectedLength, appended.Offset);
            using var frame = reopened.ReadPooledFrame(appended).Unwrap();
            Assert.Equal(new byte[] { 9, 8, 7 }, frame.PayloadAndMeta.ToArray());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ArbitraryPartialHeadUnits_TruncatesWithoutByteAlignmentGuard(int count) {
        foreach (byte value in new byte[] { 0, 1, 3, 7, 255 }) {
            byte[] prefix = MakeFile(MakeFrame(Payload(4)));
            byte[] image = [.. prefix, .. Enumerable.Repeat(value, count)];
            File.WriteAllBytes(ImagePath, image);
            using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
                Assert.Equal(RbfTailRecoveryAction.Truncated, report.Action);
                Assert.Equal(prefix.Length, file.TailOffset);
                Assert.Null(report.FrameTicket);
            }
            Assert.Equal(prefix, File.ReadAllBytes(ImagePath));
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6u)]
    [InlineData(0x04000000u)]
    [InlineData(0x40000007u)]
    [InlineData(uint.MaxValue)]
    public void CompleteInvalidUnits_NeverAliasAValidByteLength(uint units) {
        byte[] complete = MakeFile(MakeFrame(Payload(0)));
        byte[] invalidHead = (byte[])complete.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidHead.AsSpan(4), units);
        RejectWithoutModification(invalidHead);
        byte[] invalidTail = (byte[])complete.Clone();
        RewriteTrailer(invalidTail, 0, trailer => BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], units));
        RejectWithoutModification(invalidTail);
        byte[] incomplete = invalidHead[..8];
        RejectWithoutModification(incomplete);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(1000)]
    public void HealthyOpen_OnlyRequestsTheFinalStructure_IndependentOfHistory(int count) {
        byte[] frame = MakeFrame(Payload(1), 0x80000001u);
        File.WriteAllBytes(ImagePath, MakeFile(Enumerable.Repeat(frame, count).ToArray()));
        using var metrics = RbfReadMetrics.Begin();
        using var file = RbfFile.OpenExisting(ImagePath, out var report, RbfCacheMode.Off);
        var snapshot = metrics.Snapshot();
        Assert.Equal(RbfTailRecoveryAction.None, report.Action);
        Assert.Equal(1, snapshot.HeaderReadCalls);
        Assert.Equal(39, snapshot.RequestedBytes);
        Assert.Equal(39, snapshot.ReturnedBytes);
        Assert.Equal(4, snapshot.ReadCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PayloadAndPayloadCrcDamage_DoNotPreventStructuralOpen_OrUniqueCompletion(bool damageCrc, bool missingKey) {
        byte[] frame = MakeFrame(Payload(9), 0xFEDCBA98u);
        byte[] complete = MakeFile(frame);
        int length = frame.Length - 4;
        complete[4 + (damageCrc ? length - 24 : 4)] ^= 1;
        byte[] image = missingKey ? complete[..length] : complete;
        File.WriteAllBytes(ImagePath, image);
        using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
            Assert.Equal(missingKey ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.None, report.Action);
            Assert.True(file.ReadPooledFrame(SizedPtr.Create(4, length)).IsFailure);
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
    }

    [Fact]
    public void DamagedPredecessorContent_StillAllowsTailTruncationAndCompletion() {
        byte[] prior = MakeFrame(Payload(8), 301);
        byte[] tail = MakeFrame(Payload(7), 0x80000001u);
        byte[] complete = MakeFile(prior, tail);
        complete[8] ^= 1;
        int start = 4 + prior.Length;
        foreach (int cut in new[] { start + 4, complete.Length - 8 }) {
            File.WriteAllBytes(ImagePath, complete[..cut]);
            using var file = RbfFile.OpenExisting(ImagePath, out var report);
            Assert.Equal(cut == start + 4 ? RbfTailRecoveryAction.Truncated : RbfTailRecoveryAction.CompletedTail, report.Action);
            Assert.True(file.ReadPooledFrame(SizedPtr.Create(4, prior.Length - 4)).IsFailure);
        }
    }

    [Fact]
    public void VisibleStructureContradictions_RejectWithoutWriting() {
        const uint key = 0x80000001;
        byte[] original = MakeFile(MakeFrame(Payload(1), key));
        var bad = new List<byte[]>();
        byte[] crc = (byte[])original.Clone();
        crc[original.Length - 24] ^= 1;
        bad.Add(crc);
        byte[] reserved = (byte[])original.Clone();
        RewriteTrailer(reserved, key, trailer => trailer[6] |= 1);
        bad.Add(reserved);
        byte[] meta = (byte[])original.Clone();
        RewriteTrailer(meta, key, trailer => { trailer[4] = 255; trailer[5] = 255; });
        bad.Add(meta);
        byte[] padding = (byte[])original.Clone();
        padding[9] ^= 1;
        bad.Add(padding);
        byte[] rawKey = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(rawKey.AsSpan(rawKey.Length - 8), Fence);
        bad.Add(rawKey);
        byte[] keyPrefix = original[..^7];
        keyPrefix[^1] ^= 1;
        bad.Add(keyPrefix);
        byte[] fencePrefix = original[..^1];
        fencePrefix[^1] ^= 1;
        bad.Add(fencePrefix);
        byte[] tailMismatch = (byte[])original.Clone();
        RewriteTrailer(tailMismatch, key, trailer => BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], 9));
        bad.Add(tailMismatch);
        foreach (byte[] image in bad) { RejectWithoutModification(image); }
    }

    [Fact]
    public void FirstBadAlignedFence_IsRejectedWithoutFallingBackToAnEarlierFrame() {
        byte[] prior = MakeFrame(Payload(8));
        byte[] tail = MakeFrame(Payload(64));
        int start = 4 + prior.Length;
        byte[] image = MakeFile(prior, tail)[..(start + 20)];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(start + 12), Fence);
        RejectWithoutModification(image);
    }

    [Fact]
    public void PartialTrailer_IsDiscardedWithoutGuessingItsKeyOrValidatingUnknownFields() {
        byte[] tail = MakeFrame(Payload(8), 0x80000001u);
        byte[] image = MakeFile(tail)[..^10];
        image[^1] ^= 0x40;
        File.WriteAllBytes(ImagePath, image);
        using var file = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Action);
        Assert.Equal(4, file.TailOffset);
        Assert.Null(report.FrameTicket);
    }

    [Fact]
    public void EveryCompletionWritePrefix_CanReopenWithoutChangingExistingBytesOrKey() {
        byte[] complete = MakeFile(MakeFrame(Payload(3), 0xFEDCBA98u));
        int cut = complete.Length - 8;
        for (int written = 0; written <= 8; written++) {
            File.WriteAllBytes(ImagePath, complete[..cut]);
            int count = written;
            RbfWriteInstrumentation.Current = new() {
                BeforeWrite = _ => count,
                AfterWrite = _ => throw new IOException("Stopped recovery at a closure byte prefix.")
            };
            try { Assert.Throws<IOException>(() => { using var file = RbfFile.OpenExisting(ImagePath, out _); }); }
            finally { RbfWriteInstrumentation.Current = null; }
            Assert.Equal(complete[..(cut + written)], File.ReadAllBytes(ImagePath));
            using (var exclusive = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using (var reopened = RbfFile.OpenExisting(ImagePath, out var report)) {
                Assert.Equal(written == 8 ? RbfTailRecoveryAction.None : RbfTailRecoveryAction.CompletedTail, report.Action);
                using var frame = reopened.ReadPooledFrame(SizedPtr.Create(4, complete.Length - 8)).Unwrap();
                Assert.Equal(Payload(3), frame.PayloadAndMeta.ToArray());
            }
            Assert.Equal(complete, File.ReadAllBytes(ImagePath));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SetLengthOrFlushFailure_ClosesTheOpenHandle_AndDoesNotRetry(bool afterMutation, bool flush) {
        byte[] image = [.. MakeFile(MakeFrame(Payload(8))), 0xFF];
        int attempts = 0;
        File.WriteAllBytes(ImagePath, image);
        Action fail = () => { attempts++; throw new IOException("Injected recovery failure."); };
        RbfWriteInstrumentation.Current = flush
            ? afterMutation ? new() { AfterFlush = _ => fail() } : new() { BeforeFlush = _ => fail() }
            : afterMutation ? new() { AfterSetLength = _ => fail() } : new() { BeforeSetLength = _ => fail() };
        try { Assert.Throws<IOException>(() => { using var file = RbfFile.OpenExisting(ImagePath, out _); }); }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(1, attempts);
        byte[] expected = afterMutation || flush ? image[..^1] : image;
        Assert.Equal(expected, File.ReadAllBytes(ImagePath));
        using (var exclusive = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(expected.Length == image.Length ? RbfTailRecoveryAction.Truncated : RbfTailRecoveryAction.None, report.Action);
    }

    [Fact]
    public void CompletedTail_IsRevalidatedAfterFlushBeforeReturningAReport() {
        byte[] complete = MakeFile(MakeFrame(Payload(3), 0x80000001u));
        File.WriteAllBytes(ImagePath, complete[..^8]);
        using var handle = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        RbfWriteInstrumentation.Current = new() {
            AfterFlush = _ => RandomAccess.Write(handle, new byte[] { (byte)(complete[^24] ^ 1) }, complete.Length - 24)
        };
        try { Assert.Throws<InvalidDataException>(() => { _ = RbfTailRecovery.Open(handle, writable: true, out _); }); }
        finally { RbfWriteInstrumentation.Current = null; }
    }

    [Fact]
    public void MaximumTailMeta_AndNonzeroEncodedPadding_PreserveTheOriginalFrameWhenKeyIsMissing() {
        byte[] payloadAndMeta = Payload(ushort.MaxValue);
        byte[] complete = MakeFile(MakeFrame(payloadAndMeta, 0xFEDCBA98u, metaLength: ushort.MaxValue));
        File.WriteAllBytes(ImagePath, complete[..^8]);
        var ticket = SizedPtr.Create(4, complete.Length - 8);
        using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
            Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Action);
            Assert.Equal((SizedPtr?)ticket, report.FrameTicket);
            Assert.Equal(ushort.MaxValue, file.ReadFrameInfo(ticket).Unwrap().TailMetaLength);
            using var frame = file.ReadPooledFrame(ticket).Unwrap();
            Assert.Equal(payloadAndMeta, frame.PayloadAndMeta.ToArray());
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
    }

    [Theory]
    [InlineData("RBF2")]
    [InlineData("RBF4")]
    public void UnknownOrExperimentalHeader_DoesNotFallbackToTailMagic(string header) {
        byte[] image = MakeFile(MakeFrame(Payload(8)));
        System.Text.Encoding.ASCII.GetBytes(header).CopyTo(image, 0);
        RejectWithoutModification(image);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseMaximumLengthFrame_OnlyNeedsStructure_AndMissingKeyIsUniquelyCompleted(bool missingKey) {
        long completeLength = (long)SizedPtr.MaxLength + 8;
        long eof = missingKey ? completeLength - 8 : completeLength;
        using (SparseRbfTestFile.CreateNew(ImagePath, eof)) { }
        byte[] trailer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(8), 37);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.AsSpan(12), (uint)SizedPtr.MaxLength >> 2);
        SealTrailer(trailer);
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Fence);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)SizedPtr.MaxLength >> 2);
        using (SafeFileHandle handle = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            RandomAccess.Write(handle, head, 0);
            RandomAccess.Write(handle, trailer, 4L + SizedPtr.MaxLength - 20);
            if (!missingKey) {
                Span<byte> closing = stackalloc byte[8];
                closing.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(closing[4..], Fence);
                RandomAccess.Write(handle, closing, completeLength - 8);
            }
        }
        using var metrics = RbfReadMetrics.Begin();
        using (var file = RbfFile.OpenExisting(ImagePath, out var report, RbfCacheMode.Off)) {
            Assert.Equal(missingKey ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.None, report.Action);
            Assert.Equal(completeLength, file.TailOffset);
            if (missingKey) { Assert.Equal(SizedPtr.Create(4, SizedPtr.MaxLength), report.FrameTicket); }
        }
        long requested = metrics.Snapshot().RequestedBytes;
        if (missingKey) { Assert.InRange(requested, SizedPtr.MaxLength, (long)SizedPtr.MaxLength + 135); }
        else { Assert.Equal(36, requested); }
        using var final = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(completeLength, RandomAccess.GetLength(final));
        Span<byte> check = stackalloc byte[8];
        Assert.Equal(8, RandomAccess.Read(final, check, completeLength - 8));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(check));
        Assert.Equal(Fence, BinaryPrimitives.ReadUInt32LittleEndian(check[4..]));
    }

    private void RejectWithoutModification(byte[] image) {
        foreach (bool writable in new[] { false, true }) {
            File.WriteAllBytes(ImagePath, image);
            int mutations = 0;
            RbfWriteInstrumentation.Current = new() {
                BeforeWrite = request => { mutations++; return request.RequestedBytes; },
                BeforeSetLength = _ => mutations++,
                BeforeFlush = _ => mutations++
            };
            try {
                Assert.Throws<InvalidDataException>(() => {
                    using var file = writable ? RbfFile.OpenExisting(ImagePath, out _) : RbfFile.OpenReadOnlyExisting(ImagePath);
                });
            }
            finally { RbfWriteInstrumentation.Current = null; }
            Assert.Equal(0, mutations);
            Assert.Equal(image, File.ReadAllBytes(ImagePath));
            using var exclusive = File.OpenHandle(ImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 17 + 1)).ToArray();

    private static byte[] MakeFile(params byte[][] frames) {
        byte[] image = new byte[4 + frames.Sum(frame => frame.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(image, Fence);
        int offset = 4;
        foreach (byte[] frame in frames) {
            frame.CopyTo(image, offset);
            offset += frame.Length;
        }
        return image;
    }

    // This fixture intentionally bypasses the production writer, layout, codec and XOR selector.
    private static byte[] MakeFrame(byte[] payload, uint key = 0, int metaLength = 0) {
        int padding = (-payload.Length) & 3;
        int length = 28 + payload.Length + padding;
        byte[] frame = new byte[length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length >> 2);
        payload.CopyTo(frame, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(length - 24), ScalarCrc(frame.AsSpan(4, payload.Length + padding)));
        Span<byte> trailer = frame.AsSpan(length - 20, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], ((uint)padding << 29) | (uint)metaLength);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], 19);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)length >> 2);
        SealTrailer(trailer);
        ScalarXor(frame.AsSpan(4, length - 8), key);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(length - 4), key);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(length), Fence);
        Assert.NotEqual(Fence, key);
        for (int offset = 4; offset < length - 4; offset += 4) {
            Assert.NotEqual(Fence, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset)));
        }
        return frame;
    }

    private delegate void TrailerEdit(Span<byte> plaintext);

    private static void RewriteTrailer(byte[] image, uint key, TrailerEdit edit) {
        int trailerOffset = image.Length - 24;
        Span<byte> trailer = image.AsSpan(trailerOffset, 16);
        ScalarXor(trailer, key);
        edit(trailer);
        SealTrailer(trailer);
        ScalarXor(trailer, key);
    }

    private static void SealTrailer(Span<byte> trailer) => BinaryPrimitives.WriteUInt32BigEndian(trailer, ScalarCrc(trailer[4..], reverse: true));

    private static void ScalarXor(Span<byte> body, uint key) {
        for (int i = 0; i < body.Length; i++) { body[i] ^= (byte)(key >> ((i & 3) * 8)); }
    }

    private static uint ScalarCrc(ReadOnlySpan<byte> bytes, bool reverse = false) {
        uint crc = uint.MaxValue;
        for (int i = 0; i < bytes.Length; i++) {
            crc ^= bytes[reverse ? bytes.Length - 1 - i : i];
            for (int bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0x82F63B78u); }
        }
        return ~crc;
    }
}
