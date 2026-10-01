using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Final writer-image byte cuts model F1 prefixes; these are not process-kill or power-loss tests.</summary>
public sealed class RbfPrefixInspectorTests(ITestOutputHelper output) : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rbf-s0a-" + Guid.NewGuid().ToString("N"));

    private string NewPath() {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".rbf");
    }

    public void Dispose() {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }

    private byte[] WriterImage(int payloadLength, bool builder = false, int metaLength = 0) {
        string path = NewPath();
        byte[] payload = Enumerable.Range(0, payloadLength).Select(i => (byte)(i * 13 + 7)).ToArray();
        byte[] meta = Enumerable.Range(0, metaLength).Select(i => (byte)(i + 1)).ToArray();
        using (var file = RbfFile.CreateNew(path)) {
            if (builder) {
                using var frame = file.BeginAppend();
                for (int offset = 0; offset < payload.Length;) {
                    int count = Math.Min(1024, payload.Length - offset);
                    payload.AsSpan(offset, count).CopyTo(frame.PayloadAndMeta.GetSpan(count));
                    frame.PayloadAndMeta.Advance(count);
                    offset += count;
                }
                meta.CopyTo(frame.PayloadAndMeta.GetSpan(meta.Length));
                frame.PayloadAndMeta.Advance(meta.Length);
                Assert.True(frame.EndAppend(0x12345678, meta.Length).IsSuccess);
            }
            else { Assert.True(file.Append(0x12345678, payload, meta).IsSuccess); }
        }
        return File.ReadAllBytes(path);
    }

    private static RbfPrefixInspection Inspect(byte[] bytes, int? length = null) {
        using var stream = new MemoryStream(bytes, 0, length ?? bytes.Length, false);
        return RbfPrefixInspector.Inspect(stream);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallRealWriterImage_EveryByteCut_IsQualifiedOrExplicitlyUnresolved(bool builder) {
        byte[] bytes = WriterImage(7, builder, 2);
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        int trailerStart = 4 + frameLength - TrailerCodewordHelper.Size;
        for (int cut = 0; cut <= bytes.Length; cut++) {
            var result = Inspect(bytes, cut);
            RbfPrefixStatus expected = cut < 4 ? RbfPrefixStatus.IncompleteHeader
                : cut == 4 || cut == bytes.Length ? RbfPrefixStatus.Complete
                : cut > trailerStart && cut < trailerStart + 12 ? RbfPrefixStatus.Unresolved
                : RbfPrefixStatus.IncompleteSuffix;
            Assert.Equal(expected, result.Status);
            Assert.Equal(cut, result.OriginalLength);
            Assert.Equal(cut < 4 ? 0 : cut == bytes.Length ? cut : 4, result.LastCompleteBoundary);
            Assert.Equal(cut, result.ReturnedBytes);
            Assert.Equal(cut, result.RequestedBytes);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4068)]
    [InlineData(4069)]
    [InlineData(8164)]
    [InlineData(8165)]
    [InlineData(200003)]
    public void RealAppendBoundariesAndLargeFrame_PrefixSamplesAndComplete(int payloadLength) {
        byte[] bytes = WriterImage(payloadLength);
        foreach (int cut in new[] { 4, 5, 8, bytes.Length / 2, bytes.Length - 4, bytes.Length - 1, bytes.Length }.Distinct()) {
            var result = Inspect(bytes, cut);
            Assert.Contains(result.Status, new[] { RbfPrefixStatus.Complete, RbfPrefixStatus.IncompleteSuffix, RbfPrefixStatus.Unresolved });
            Assert.Equal(cut, result.ReturnedBytes);
        }
        var complete = Inspect(bytes);
        Assert.Equal(RbfPrefixStatus.Complete, complete.Status);
        Assert.Equal(bytes.Length, complete.LastCompleteBoundary);
        Assert.Equal(bytes.Length, complete.RequestedBytes);
        Assert.True(complete.ReadRequests <= 8 + payloadLength / RbfPrefixInspector.ChunkSize);
    }

    [Fact]
    public void RealLargeBuilder_MultipleAdvances_CommitImagePrefixSamples() {
        byte[] bytes = WriterImage(200003, builder: true, metaLength: 7);
        Assert.Equal(RbfPrefixStatus.Complete, Inspect(bytes).Status);
        foreach (int cut in new[] { 8, 1032, 4096, 65536, 65537, 131072, bytes.Length - 24, bytes.Length - 23, bytes.Length - 4, bytes.Length - 1 }) {
            var result = Inspect(bytes, cut);
            Assert.Equal(RbfPrefixStatus.IncompleteSuffix, result.Status);
            Assert.Equal(4, result.LastCompleteBoundary);
            Assert.Equal(cut, result.ReturnedBytes);
        }
        int trailer = bytes.Length - 4 - 16;
        Assert.Equal(RbfPrefixStatus.Unresolved, Inspect(bytes, trailer + 1).Status);
        Assert.Equal(RbfPrefixStatus.IncompleteSuffix, Inspect(bytes, trailer + 12).Status);
    }

    [Fact]
    public void BuilderReservationBlocksOutput_AbortLeavesOnlyPreviouslyCompleteBytes() {
        string path = NewPath();
        using (var file = RbfFile.CreateNew(path)) {
            Assert.True(file.Append(1, "kept"u8).IsSuccess);
            long original = file.TailOffset;
            using (var builder = file.BeginAppend()) {
                for (int i = 0; i < 20; i++) {
                    builder.PayloadAndMeta.GetSpan(8192)[..8192].Fill(0xA7);
                    builder.PayloadAndMeta.Advance(8192);
                    Assert.Equal(original, new FileInfo(path).Length);
                }
            }
            Assert.Equal(original, new FileInfo(path).Length);
            Assert.Equal(original, file.TailOffset);
        }
        Assert.Equal(RbfPrefixStatus.Complete, RbfPrefixInspector.Inspect(path).Status);
    }

    [Fact]
    public void CreateNewHeader_AllCutsAndContradictoryBytes() {
        string path = NewPath();
        using (RbfFile.CreateNew(path)) { }
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal("RBF1"u8.ToArray(), bytes);
        for (int cut = 0; cut < 4; cut++) { Assert.Equal(RbfPrefixStatus.IncompleteHeader, Inspect(bytes, cut).Status); }
        for (int i = 0; i < 4; i++) {
            byte[] bad = (byte[])bytes.Clone(); bad[i] ^= 1;
            Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, i + 1).Status);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HeadLen_IllegalCompleteAndImpossiblePartial(int count) {
        byte[] image = WriterImage(0);
        image[4] = 1;
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(image, 4 + count).Status);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 0xFFFF_FFFC);
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(image, 8).Status);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 0);
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(image, 8).Status);
        Assert.Equal(RbfPrefixStatus.IncompleteSuffix, Inspect(image, 5).Status); // low 00 can complete as 256
    }

    [Fact]
    public void ExistingPayloadCrcByte_IsCheckedBeforeTrailerExists() {
        byte[] bytes = WriterImage(5);
        int crcStart = bytes.Length - 4 - 16 - 4;
        for (int i = 0; i < 4; i++) {
            byte[] bad = (byte[])bytes.Clone(); bad[crcStart + i] ^= 0x80;
            Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, crcStart + i + 1).Status);
        }
    }

    [Fact]
    public void DescriptorPartial_ReservedAndImpossibleMeta_AreRejectedEarly() {
        byte[] bytes = WriterImage(0);
        int trailer = bytes.Length - 4 - 16;
        byte[] bad = (byte[])bytes.Clone(); bad[trailer + 6] = 1;
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, trailer + 7).Status);
        bad = (byte[])bytes.Clone(); bad[trailer + 4] = 1;
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, trailer + 5).Status);
        bad = (byte[])bytes.Clone(); bad[trailer + 7] = 1; // descriptor reserved bit24
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, trailer + 8).Status);
        bad = (byte[])bytes.Clone(); bad[trailer + 7] = 0x20; // pad1 cannot fit empty coverage
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, trailer + 8).Status);
    }

    [Fact]
    public void MaximumFrameLengthPrefix_IsLegal_AndNextAlignedLengthIsRejected() {
        byte[] bytes = "RBF1\0\0\0\0"u8.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)SizedPtr.MaxLength);
        Assert.Equal(RbfPrefixStatus.IncompleteSuffix, Inspect(bytes).Status);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)SizedPtr.MaxLength + 4);
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bytes).Status);
        // This is a legal maximum-sized image prefix, not a 256MiB writer/allocation test.
        Assert.Equal(SizedPtr.MaxLength - FrameLayout.FixedOverhead, RbfFile.MaxPayloadAndMetaLength);
    }

    [Fact]
    public void KnownDescriptorAndTag_ValidateTrailerCrcBeforeTailLenArrives() {
        byte[] bytes = WriterImage(3);
        int trailer = bytes.Length - 4 - 16;
        bytes[trailer] ^= 1;
        Assert.Equal(RbfPrefixStatus.Unresolved, Inspect(bytes, trailer + 11).Status);
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bytes, trailer + 12).Status);
        for (int cut = trailer + 12; cut <= bytes.Length; cut++) { Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bytes, cut).Status); }
    }

    [Fact]
    public void PartialTailLen_ContradictionIsRejectedAndUnresolvedNeverQualifies() {
        byte[] bytes = WriterImage(3);
        int trailer = bytes.Length - 4 - 16;
        for (int i = 0; i < 4; i++) {
            byte[] bad = (byte[])bytes.Clone(); bad[trailer + 12 + i] ^= 1;
            Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, trailer + 13 + i).Status);
        }
    }

    [Fact]
    public void MissingFence_StillRejectsBodyPaddingTrailerAndFenceContradictions() {
        byte[] bytes = WriterImage(1);
        int trailer = bytes.Length - 4 - 16;
        byte[] bad = (byte[])bytes.Clone(); bad[8] ^= 1;
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, bytes.Length - 4).Status);
        bad = (byte[])bytes.Clone(); bad[trailer + 8] ^= 1;
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, bytes.Length - 4).Status);
        bad = (byte[])bytes.Clone();
        int crcStart = trailer - 4;
        bad[crcStart - 1] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(crcStart), RollingCrc.CrcForward(bad.AsSpan(8, crcStart - 8)));
        Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, bytes.Length - 4).Status);
        for (int i = 0; i < 4; i++) {
            bad = (byte[])bytes.Clone(); bad[bytes.Length - 4 + i] ^= 1;
            Assert.Equal(RbfPrefixStatus.Invalid, Inspect(bad, bytes.Length - 3 + i).Status);
        }
    }

    [Fact]
    public void EmbeddedValidFrame_IsOpaque_AndDoesNotBecomeBoundaryAfterBadOuterFrame() {
        byte[] embedded = WriterImage(2);
        string path = NewPath();
        using (var file = RbfFile.CreateNew(path)) {
            Assert.True(file.Append(1, "previous"u8).IsSuccess);
            Assert.True(file.Append(2, embedded).IsSuccess);
        }
        byte[] bytes = File.ReadAllBytes(path);
        long previous = 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) + 4;
        Assert.Equal(RbfPrefixStatus.Complete, Inspect(bytes).Status);
        // Keep the embedded image completely valid while corrupting the outer PayloadCRC.
        bytes[bytes.Length - 24] ^= 1;
        Assert.Equal(embedded, bytes.AsSpan((int)previous + 4, embedded.Length).ToArray());
        var result = Inspect(bytes);
        Assert.Equal(RbfPrefixStatus.Invalid, result.Status);
        Assert.Equal(previous, result.LastCompleteBoundary);
    }

    [Fact]
    public void CompleteTombstone_IsPreservedAsPhysicalFact() {
        byte[] bytes = WriterImage(5);
        int trailer = bytes.Length - 4 - 16;
        var parsed = TrailerCodewordHelper.Parse(bytes.AsSpan(trailer, 16));
        TrailerCodewordHelper.Serialize(bytes.AsSpan(trailer), parsed.FrameDescriptor | 0x80000000, parsed.FrameTag, parsed.TailLen);
        Assert.Equal(RbfPrefixStatus.Complete, Inspect(bytes).Status);
        Assert.Equal(bytes.Length, Inspect(bytes).LastCompleteBoundary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathInspection_LeavesExactSourceBytesAndLengthUnchanged(bool corrupted) {
        byte[] bytes = WriterImage(23)[..^1];
        if (corrupted) { bytes[8] ^= 1; }
        string path = NewPath(); File.WriteAllBytes(path, bytes);
        var result = RbfPrefixInspector.Inspect(path);
        Assert.Equal(corrupted ? RbfPrefixStatus.Invalid : RbfPrefixStatus.IncompleteSuffix, result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(bytes.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void BudgetAndCancellationAreUnfinished_NotCorruptionOrShortTail() {
        byte[] bytes = WriterImage(200003);
        using var stream = new MemoryStream(bytes, false);
        var limited = RbfPrefixInspector.Inspect(stream, 15);
        Assert.Equal(RbfPrefixStatus.BudgetExceeded, limited.Status);
        Assert.Equal(15, limited.RequestedBytes);
        Assert.Equal(15, limited.ReturnedBytes);
        Assert.Equal(4, limited.LastCompleteBoundary);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = RbfPrefixInspector.Inspect(stream, cancellationToken: cancelled.Token);
        Assert.Equal(RbfPrefixStatus.Cancelled, stopped.Status);
        Assert.Equal(0, stopped.ReadRequests);
        using var mid = new CancellationTokenSource();
        using var custom = new TestStream(bytes, afterRead: n => { if (n > 8) { mid.Cancel(); } });
        stopped = RbfPrefixInspector.Inspect(custom, cancellationToken: mid.Token);
        Assert.Equal(RbfPrefixStatus.Cancelled, stopped.Status);
        Assert.True(stopped.ReturnedBytes <= 8 + RbfPrefixInspector.ChunkSize);
    }

    [Fact]
    public void ShortReadsChargeAllRequests_EarlyZeroAndThrownReadAreIoFailure() {
        byte[] bytes = WriterImage(11);
        using var shortReads = new TestStream(bytes, maxRead: 1);
        var result = RbfPrefixInspector.Inspect(shortReads);
        Assert.Equal(RbfPrefixStatus.Complete, result.Status);
        Assert.Equal(bytes.Length, result.ReturnedBytes);
        Assert.Equal(bytes.Length, result.ReadRequests);
        Assert.True(result.RequestedBytes > result.ReturnedBytes);
        using var limited = new TestStream(bytes, maxRead: 1);
        Assert.Equal(RbfPrefixStatus.BudgetExceeded, RbfPrefixInspector.Inspect(limited, bytes.Length).Status);
        using var earlyZero = new TestStream(bytes, failAt: 8);
        Assert.Equal(RbfPrefixStatus.IoFailure, RbfPrefixInspector.Inspect(earlyZero).Status);
        using var thrown = new TestStream(bytes, failAt: 8, throwIo: true);
        Assert.Equal(RbfPrefixStatus.IoFailure, RbfPrefixInspector.Inspect(thrown).Status);
    }

    [Fact]
    public void HistoryCost_IsExactlyReturnedFileLength_WithoutPrefetchOrResidentFrameList() {
        string path = NewPath();
        using (var file = RbfFile.CreateNew(path)) {
            for (int i = 0; i < 1000; i++) { Assert.True(file.Append((uint)i, "data"u8).IsSuccess); }
        }
        var result = RbfPrefixInspector.Inspect(path);
        Assert.Equal(RbfPrefixStatus.Complete, result.Status);
        Assert.Equal(result.OriginalLength, result.ReturnedBytes);
        Assert.Equal(result.OriginalLength, result.RequestedBytes);
        Assert.Equal(1 + 5 * 1000, result.ReadRequests);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(256, 4)]
    [InlineData(4096, 4)]
    [InlineData(1, 1024 * 1024)]
    public void CostWitness_ReportsActualReadsAndAllocation(int frameCount, int payloadLength) {
        string path = NewPath();
        byte[] payload = new byte[payloadLength];
        using (var file = RbfFile.CreateNew(path)) {
            for (int i = 0; i < frameCount; i++) { Assert.True(file.Append((uint)i, payload).IsSuccess); }
        }
        // Warm managed paths only. This measurement does not establish a cold OS/device cache.
        using (var warm = new MemoryStream("RBF1"u8.ToArray(), false)) { RbfPrefixInspector.Inspect(warm); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var result = RbfPrefixInspector.Inspect(path);
        clock.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"S0A COST frames={frameCount} payload={payloadLength} length={result.OriginalLength} requested={result.RequestedBytes} returned={result.ReturnedBytes} calls={result.ReadRequests} elapsedMs={clock.Elapsed.TotalMilliseconds:F3} allocatedThread={allocated}; OS/device cache not cold");
        Assert.Equal(RbfPrefixStatus.Complete, result.Status);
        Assert.Equal(result.OriginalLength, result.ReturnedBytes);
        Assert.Equal(result.OriginalLength, result.RequestedBytes);
        // A loose fixed ceiling witnesses bounded scratch space, not a latency promise.
        Assert.True(allocated < 256 * 1024, $"Allocated {allocated} bytes.");
        using var limitedSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        var limited = RbfPrefixInspector.Inspect(limitedSource, Math.Max(0, result.OriginalLength - 1));
        Assert.Equal(RbfPrefixStatus.BudgetExceeded, limited.Status);
        Assert.True(limited.RequestedBytes < result.OriginalLength);
    }

    private sealed class TestStream(byte[] bytes, int maxRead = int.MaxValue, long failAt = long.MaxValue,
        bool throwIo = false, Action<long>? afterRead = null) : MemoryStream(bytes, false) {
        public override int Read(Span<byte> buffer) {
            if (Position >= failAt) {
                if (throwIo) { throw new IOException("Injected read failure."); }
                return 0;
            }
            int result = base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
            afterRead?.Invoke(Position);
            return result;
        }
    }
}
