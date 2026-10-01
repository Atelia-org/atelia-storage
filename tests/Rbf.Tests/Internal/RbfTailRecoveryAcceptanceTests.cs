using System.Buffers.Binary;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Public open contract, using real files and real injected prefix writes.</summary>
public sealed class RbfTailRecoveryAcceptanceTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rbf-tail-acceptance-{Guid.NewGuid():N}");
    private string ImagePath => Path.Combine(_directory, "image.rbf");

    public RbfTailRecoveryAcceptanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(7, false)]
    [InlineData(12, false)]
    [InlineData(31, false)]
    [InlineData(64, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    [InlineData(12, true)]
    [InlineData(31, true)]
    [InlineData(64, true)]
    public void EveryOriginalBytePrefix_HasOneAction_StableFrames_AndCanAppend(int payloadLength, bool builder) {
        var (complete, prior, tail) = CreateImage(payloadLength, builder);
        int start = checked((int)tail.Offset);
        int crcStart = start + tail.Length - 20;

        for (int cut = start; cut <= complete.Length; cut++) {
            byte[] input = complete[..cut];
            File.WriteAllBytes(ImagePath, input);
            RbfTailRecoveryAction expected = cut == start || cut == complete.Length
                ? RbfTailRecoveryAction.None
                : cut < start + 4 ? RbfTailRecoveryAction.Truncated
                : cut < start + tail.Length ? RbfTailRecoveryAction.CompletedTombstone
                : RbfTailRecoveryAction.CompletedFence;

            using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
                Assert.Equal(expected, report.Action);
                Assert.Equal(cut, report.OriginalLength);
                Assert.Equal(file.TailOffset, report.FinalLength);
                Assert.Equal(expected == RbfTailRecoveryAction.None ? (long?)null : tail.Offset, report.AffectedFrameOffset);
                Assert.Equal(expected is RbfTailRecoveryAction.CompletedTombstone or RbfTailRecoveryAction.CompletedFence
                    ? tail : (SizedPtr?)null, report.FrameTicket);
                Assert.Equal(expected == RbfTailRecoveryAction.Truncated || cut == start ? start : complete.Length, file.TailOffset);

                var physical = Collect(file, reverse: false, showTombstone: true);
                Assert.Equal(expected == RbfTailRecoveryAction.Truncated || cut == start ? 1 : 2, physical.Count);
                Assert.Equal(prior, physical[0].Ticket);
                if (physical.Count == 2) {
                    Assert.Equal(tail, physical[1].Ticket);
                    Assert.Equal(expected == RbfTailRecoveryAction.CompletedTombstone, physical[1].IsTombstone);
                    using var recovered = file.ReadPooledFrame(tail).Unwrap();
                    if (recovered.IsTombstone) {
                        Assert.Equal(0u, recovered.Tag);
                        Assert.Equal(0, physical[1].TailMetaLength);
                        Assert.Equal(tail.Length - 24, recovered.PayloadAndMeta.Length);
                        int existingCoverage = Math.Max(0, Math.Min(cut, crcStart) - start - 4);
                        Assert.Equal(complete.AsSpan(start + 4, existingCoverage).ToArray(),
                            recovered.PayloadAndMeta[..existingCoverage].ToArray());
                        Assert.All(recovered.PayloadAndMeta[existingCoverage..].ToArray(), value => Assert.Equal((byte)0, value));
                    }
                    else {
                        Assert.Equal(22u, recovered.Tag);
                        Assert.Equal(Payload(payloadLength), recovered.PayloadAndMeta.ToArray());
                        Assert.Equal(builder ? Math.Min(payloadLength, 3) : 0, physical[1].TailMetaLength);
                    }
                }
                Assert.Equal(physical.AsEnumerable().Reverse().Select(x => x.Ticket),
                    Collect(file, reverse: true, showTombstone: true).Select(x => x.Ticket));
                Assert.Equal(physical.Count(x => !x.IsTombstone), Collect(file, reverse: false).Count);
            }

            byte[] repaired = File.ReadAllBytes(ImagePath);
            Assert.Equal(complete[..start], repaired[..start]);
            if (expected == RbfTailRecoveryAction.CompletedFence) {
                Assert.Equal(complete, repaired);
            }
            if (expected == RbfTailRecoveryAction.CompletedTombstone) {
                Assert.Equal(complete.AsSpan(start, 4).ToArray(), repaired.AsSpan(start, 4).ToArray());
            }
            using (var reopened = RbfFile.OpenExisting(ImagePath, out var again)) {
                Assert.Equal(RbfTailRecoveryAction.None, again.Action);
                Assert.Null(again.AffectedFrameOffset);
                Assert.Null(again.FrameTicket);
                var appended = reopened.Append(33, new byte[] { 9, 8, 7 }).Unwrap();
                Assert.Equal(repaired.Length, appended.Offset);
                using var frame = reopened.ReadPooledFrame(appended).Unwrap();
                Assert.Equal(new byte[] { 9, 8, 7 }, frame.PayloadAndMeta.ToArray());
                Assert.Equal(33u, Collect(reopened, reverse: true)[0].Tag);
            }
        }
    }

    [Fact]
    public void EveryRepairWritePrefix_ReopensWithinTheSameTailFrame() {
        var (complete, _, tail) = CreateImage(12);
        int start = checked((int)tail.Offset);
        int crcStart = start + tail.Length - 20;
        int[] originals = [start + 4, start + 5, crcStart, crcStart + 1, start + tail.Length - 1];
        foreach (int originalCut in originals) {
            int afterTrim = Math.Min(originalCut, crcStart);
            int writeLength = complete.Length - afterTrim;
            for (int written = 0; written <= writeLength; written++) {
                File.WriteAllBytes(ImagePath, complete[..originalCut]);
                int remaining = written;
                RbfWriteInstrumentation.Current = new() {
                    BeforeWrite = request => Math.Min(request.RequestedBytes, remaining),
                    AfterWrite = observation => {
                        remaining -= observation.WrittenBytes;
                        if (remaining == 0) { throw new IOException("Stopped at requested repair prefix."); }
                    }
                };
                try {
                    Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _));
                }
                finally { RbfWriteInstrumentation.Current = null; }
                byte[] interrupted = File.ReadAllBytes(ImagePath); // also proves failed open released its exclusive handle
                Assert.Equal(afterTrim + written, interrupted.Length);
                Assert.Equal(complete[..start], interrupted[..start]);
                Assert.Equal(complete.AsSpan(start, 4).ToArray(), interrupted.AsSpan(start, 4).ToArray());
                using (var reopened = RbfFile.OpenExisting(ImagePath, out var report)) {
                    Assert.Equal(interrupted.Length == complete.Length ? RbfTailRecoveryAction.None
                        : interrupted.Length >= start + tail.Length ? RbfTailRecoveryAction.CompletedFence
                        : RbfTailRecoveryAction.CompletedTombstone, report.Action);
                    using var frame = reopened.ReadPooledFrame(tail).Unwrap();
                    Assert.True(frame.IsTombstone);
                    Assert.Single(Collect(reopened, reverse: false));
                    Assert.Equal(2, Collect(reopened, reverse: false, showTombstone: true).Count);
                }
            }
        }
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 12)]
    [InlineData(false, 200000)]
    [InlineData(true, 200000)]
    public void ActualAppendOutputFailures_LeaveExactlyWrittenPrefix_AndRecover(bool builder, int payloadLength) {
        var (complete, _, tail) = CreateImage(payloadLength, builder);
        int start = checked((int)tail.Offset);
        byte[] prefix = complete[..start];
        byte[] content = Payload(payloadLength);
        var observed = new List<RbfWriteObservation>();
        File.WriteAllBytes(ImagePath, prefix);
        using (var file = RbfFile.OpenExisting(ImagePath, out _)) {
            RbfWriteInstrumentation.Current = new() { AfterWrite = write => observed.Add(write) };
            try { Assert.Equal(tail, AppendTail(file, content, builder)); }
            finally { RbfWriteInstrumentation.Current = null; }
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
        Assert.NotEmpty(observed);
        int total = complete.Length - start;
        var cuts = payloadLength < 100 ? Enumerable.Range(0, total + 1).ToHashSet() : new HashSet<int> { 0, total };
        foreach (var write in observed) {
            int offset = checked((int)(write.Offset - start));
            cuts.Add(offset);
            cuts.Add(offset + write.RequestedBytes / 2);
            cuts.Add(offset + write.RequestedBytes - 1);
            cuts.Add(offset + write.RequestedBytes);
        }
        foreach (int cut in cuts.Order()) {
            File.WriteAllBytes(ImagePath, prefix);
            using (var file = RbfFile.OpenExisting(ImagePath, out _)) {
                int remaining = cut;
                RbfWriteInstrumentation.Current = new() {
                    BeforeWrite = write => Math.Min(remaining, write.RequestedBytes),
                    AfterWrite = write => {
                        remaining -= write.WrittenBytes;
                        if (remaining == 0) { throw new IOException("Stopped original output at requested prefix."); }
                    }
                };
                try { Assert.Throws<IOException>(() => AppendTail(file, content, builder)); }
                finally { RbfWriteInstrumentation.Current = null; }
            }
            Assert.Equal(complete[..(start + cut)], File.ReadAllBytes(ImagePath));
            using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
            Assert.Equal(cut == 0 || cut == total ? RbfTailRecoveryAction.None
                : cut < 4 ? RbfTailRecoveryAction.Truncated
                : cut < tail.Length ? RbfTailRecoveryAction.CompletedTombstone
                : RbfTailRecoveryAction.CompletedFence, report.Action);
            var physical = Collect(reopened, reverse: true, showTombstone: true);
            Assert.Equal(cut < 4 ? 1 : 2, physical.Count);
            if (cut >= 4) {
                Assert.Equal(tail, physical[0].Ticket);
                using var recovered = reopened.ReadPooledFrame(tail).Unwrap();
                Assert.Equal(cut < tail.Length, recovered.IsTombstone);
            }
        }
    }

    [Fact]
    public void LargeCoverageCompletion_CrossesStreamingChunks_AndPreservesExistingContent() {
        var (complete, _, tail) = CreateImage(200000, builder: true);
        int start = checked((int)tail.Offset);
        int coverageExisting = 65537;
        byte[] input = complete[..(start + 4 + coverageExisting)];
        File.WriteAllBytes(ImagePath, input);
        var writes = new List<RbfWriteObservation>();
        RbfWriteInstrumentation.Current = new() { AfterWrite = write => writes.Add(write) };
        try {
            using var file = RbfFile.OpenExisting(ImagePath, out var report);
            Assert.Equal(RbfTailRecoveryAction.CompletedTombstone, report.Action);
            using var frame = file.ReadPooledFrame(tail).Unwrap();
            Assert.True(frame.IsTombstone);
            Assert.Equal(Payload(200000)[..coverageExisting], frame.PayloadAndMeta[..coverageExisting].ToArray());
            Assert.All(frame.PayloadAndMeta[coverageExisting..].ToArray(), value => Assert.Equal((byte)0, value));
            Assert.Equal(0, file.ReadFrameInfo(tail).Unwrap().TailMetaLength);
        }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.True(writes.Count > 2);
        Assert.All(writes, write => Assert.InRange(write.RequestedBytes, 1, 65536));
        Assert.Equal(input, File.ReadAllBytes(ImagePath)[..input.Length]);
    }

    [Fact]
    public void ConsecutiveRecoveredTombstones_ArePhysicalMembers_AndHiddenFromNormalStream() {
        var (complete, _, tail) = CreateImage(12);
        File.WriteAllBytes(ImagePath, complete[..(checked((int)tail.Offset) + 6)]);
        SizedPtr secondTail;
        using (var file = RbfFile.OpenExisting(ImagePath, out var firstReport)) {
            Assert.Equal(RbfTailRecoveryAction.CompletedTombstone, firstReport.Action);
            secondTail = file.Append(33, new byte[] { 5, 6, 7, 8 }).Unwrap();
        }
        byte[] firstRecovery = File.ReadAllBytes(ImagePath);
        RawRbfTestFile.SetLength(ImagePath, secondTail.Offset + 6);
        using (var file = RbfFile.OpenExisting(ImagePath, out var secondReport)) {
            Assert.Equal(RbfTailRecoveryAction.CompletedTombstone, secondReport.Action);
            Assert.Equal(secondTail, secondReport.FrameTicket);
            Assert.Equal(new uint[] { 11 }, Collect(file, reverse: false).Select(info => info.Tag));
            var physical = Collect(file, reverse: false, showTombstone: true);
            Assert.Equal(3, physical.Count);
            Assert.True(physical[1].IsTombstone);
            Assert.True(physical[2].IsTombstone);
            file.Append(44, new byte[] { 9 }).Unwrap();
            Assert.Equal(new uint[] { 11, 44 }, Collect(file, reverse: false).Select(info => info.Tag));
            Assert.Equal(new uint[] { 44, 11 }, Collect(file, reverse: true).Select(info => info.Tag));
        }
        Assert.Equal(firstRecovery[..checked((int)secondTail.Offset)], File.ReadAllBytes(ImagePath)[..checked((int)secondTail.Offset)]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(RbfTailRecoveryAction.None, report.Action);
        Assert.Equal(4, Collect(reopened, reverse: true, showTombstone: true).Count);
    }

    [Theory]
    [InlineData("before-length")]
    [InlineData("after-length")]
    [InlineData("before-flush")]
    [InlineData("after-flush")]
    public void PartialHeadTruncate_FailuresReleaseHandle_AndReopen(string failurePoint) {
        var (complete, _, tail) = CreateImage(12);
        int start = checked((int)tail.Offset);
        File.WriteAllBytes(ImagePath, complete[..(start + 2)]);
        void Fail(string phase) { if (phase == failurePoint) { throw new IOException(phase); } }
        RbfWriteInstrumentation.Current = new() {
            BeforeSetLength = request => { Assert.Equal(start, request.Length); Fail("before-length"); },
            AfterSetLength = _ => Fail("after-length"),
            BeforeFlush = _ => Fail("before-flush"),
            AfterFlush = _ => Fail("after-flush"),
            BeforeWrite = _ => throw new InvalidOperationException("Truncating a partial HeadLen must not write.")
        };
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        byte[] interrupted = File.ReadAllBytes(ImagePath);
        Assert.Equal(complete[..start], interrupted[..start]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(failurePoint == "before-length" ? RbfTailRecoveryAction.Truncated : RbfTailRecoveryAction.None, report.Action);
        Assert.Equal(start, reopened.TailOffset);
        Assert.Single(Collect(reopened, reverse: false));
    }

    [Theory]
    [InlineData("before-write")]
    [InlineData("partial-write")]
    [InlineData("after-write")]
    [InlineData("before-flush")]
    [InlineData("after-flush")]
    public void PureFenceCompletion_FailuresNeverDiscardFrameBytes(string failurePoint) {
        var (complete, _, tail) = CreateImage(12, builder: true);
        int frameEnd = checked((int)tail.Offset) + tail.Length;
        File.WriteAllBytes(ImagePath, complete[..frameEnd]);
        void Fail(string phase) { if (phase == failurePoint) { throw new IOException(phase); } }
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => {
                Assert.Equal(frameEnd, request.Offset);
                Assert.Equal(4, request.RequestedBytes);
                Fail("before-write");
                return failurePoint == "partial-write" ? 1 : request.RequestedBytes;
            },
            AfterWrite = _ => Fail("after-write"),
            BeforeFlush = _ => Fail("before-flush"),
            AfterFlush = _ => Fail("after-flush"),
            BeforeSetLength = _ => throw new InvalidOperationException("Complete FrameBytes must never be truncated.")
        };
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        byte[] interrupted = File.ReadAllBytes(ImagePath);
        Assert.Equal(complete[..frameEnd], interrupted[..frameEnd]);
        using (var reopened = RbfFile.OpenExisting(ImagePath, out var report)) {
            Assert.Equal(interrupted.Length == complete.Length ? RbfTailRecoveryAction.None : RbfTailRecoveryAction.CompletedFence, report.Action);
            using var frame = reopened.ReadPooledFrame(tail).Unwrap();
            Assert.False(frame.IsTombstone);
            Assert.Equal(Payload(12), frame.PayloadAndMeta.ToArray());
            Assert.Equal(3, reopened.ReadFrameInfo(tail).Unwrap().TailMetaLength);
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
    }

    [Theory]
    [InlineData("before-trim")]
    [InlineData("after-trim")]
    [InlineData("before-trim-flush")]
    [InlineData("after-trim-flush")]
    [InlineData("before-final-flush")]
    [InlineData("after-final-flush")]
    public void MutationAndFlushFailures_ReleaseHandle_AndCanReopen(string failurePoint) {
        var (complete, _, tail) = CreateImage(12);
        int crcStart = checked((int)tail.Offset) + tail.Length - 20;
        File.WriteAllBytes(ImagePath, complete[..(crcStart + 2)]);
        var phases = new List<string>();
        int flush = 0;
        void Observe(string phase) {
            phases.Add(phase);
            if (phase == failurePoint) { throw new IOException(phase); }
        }
        RbfWriteInstrumentation.Current = new() {
            BeforeSetLength = request => { Assert.Equal(crcStart, request.Length); Observe("before-trim"); },
            AfterSetLength = _ => Observe("after-trim"),
            BeforeFlush = _ => { flush++; Observe(flush == 1 ? "before-trim-flush" : "before-final-flush"); },
            AfterFlush = _ => Observe(flush == 1 ? "after-trim-flush" : "after-final-flush"),
            BeforeWrite = request => {
                Assert.Contains("after-trim-flush", phases);
                return request.RequestedBytes;
            }
        };
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(failurePoint, phases[^1]);
        byte[] interrupted = File.ReadAllBytes(ImagePath);
        Assert.Equal(complete[..checked((int)tail.Offset)], interrupted[..checked((int)tail.Offset)]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out _);
        using var frame = reopened.ReadPooledFrame(tail).Unwrap();
        Assert.True(frame.IsTombstone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullFrameCrcCorruption_IsNeverIncomplete_EvenWithoutFence(bool trailerCrc) {
        var (complete, _, tail) = CreateImage(12);
        int corrupt = checked((int)tail.Offset) + tail.Length - (trailerCrc ? 16 : 20);
        complete[corrupt] ^= 0x80;
        for (int missingFence = 0; missingFence <= 4; missingFence++) {
            byte[] image = complete[..(complete.Length - missingFence)];
            AssertRejectedWithoutMutation(image);
        }
    }

    [Fact]
    public void KnownPartialCrcAndDescriptorContradictions_RejectBeforeTrimming() {
        var (complete, _, tail) = CreateImage(12);
        int start = checked((int)tail.Offset);
        int crcStart = start + tail.Length - 20;
        byte[] crc = complete[..(crcStart + 1)];
        crc[^1] ^= 1;
        AssertRejectedWithoutMutation(crc);
        byte[] descriptor = complete[..(start + tail.Length - 8)];
        descriptor[^1] |= 0x10; // reserved descriptor bit 28, full descriptor available
        AssertRejectedWithoutMutation(descriptor);
        byte[] padding = complete[..(start + tail.Length - 8)];
        padding[^1] |= 0x40; // known nonzero byte cannot become required zero padding
        AssertRejectedWithoutMutation(padding);
        byte[] fence = complete[..(complete.Length - 2)];
        fence[^1] ^= 1;
        AssertRejectedWithoutMutation(fence);
        byte[] head = complete[..(start + 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(start), 25);
        AssertRejectedWithoutMutation(head);
    }

    [Fact]
    public void DamagedEarlierStructure_OrAdjacentPrefixPayload_IsNotSkipped() {
        var (complete, prior, tail) = CreateImage(12);
        byte[] earlierTrailer = complete.ToArray();
        earlierTrailer[checked((int)prior.Offset) + prior.Length - 16] ^= 1;
        AssertRejectedWithoutMutation(earlierTrailer);
        byte[] adjacentPayload = complete[..(checked((int)tail.Offset) + 7)];
        adjacentPayload[checked((int)prior.Offset) + 4] ^= 1;
        AssertRejectedWithoutMutation(adjacentPayload);
    }

    [Fact]
    public void OpenValidatesTailCrc_WhileOlderPayloadAuditRemainsAnExplicitRead() {
        var (complete, prior, _) = CreateImage(12);
        complete[checked((int)prior.Offset) + 4] ^= 1;
        File.WriteAllBytes(ImagePath, complete);
        using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
            Assert.Equal(RbfTailRecoveryAction.None, report.Action);
            Assert.Equal(2, Collect(file, reverse: false).Count);
            Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(prior).Error);
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
    }

    [Fact]
    public void EmbeddedCompleteFrameAtEof_IsNotExposedAsAMainSequenceMember() {
        var (inner, _, _) = CreateImage(4);
        byte[] outer;
        SizedPtr ticket;
        string source = Path.Combine(_directory, "outer.rbf");
        using (var file = RbfFile.CreateNew(source)) {
            ticket = file.Append(44, inner.Concat(new byte[32]).ToArray()).Unwrap();
        }
        outer = File.ReadAllBytes(source);
        byte[] incomplete = outer[..(checked((int)ticket.Offset) + 4 + inner.Length)];
        File.WriteAllBytes(ImagePath, incomplete);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
        Assert.Equal(incomplete, File.ReadAllBytes(ImagePath));
        using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(RbfTailRecoveryAction.CompletedTombstone, report.Action);
        Assert.Equal(ticket, report.FrameTicket);
        Assert.Empty(Collect(reopened, reverse: false));
        var physical = Collect(reopened, reverse: true, showTombstone: true);
        Assert.Single(physical);
        Assert.Equal(ticket, physical[0].Ticket);
        Assert.True(physical[0].IsTombstone);
    }

    [Fact]
    public void ReadOnlyOpen_RejectsEveryIncompletePrefix_WithoutChangingBytes() {
        var (complete, _, tail) = CreateImage(7);
        for (int cut = checked((int)tail.Offset) + 1; cut < complete.Length; cut++) {
            byte[] image = complete[..cut];
            File.WriteAllBytes(ImagePath, image);
            Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
            Assert.Equal(image, File.ReadAllBytes(ImagePath));
        }
    }

    [Fact]
    public void HeaderCreationResidue_IsNotARecoverableFrame() {
        for (int length = 0; length < 4; length++) {
            AssertRejectedWithoutMutation(new byte[] { 0x52, 0x42, 0x46, 0x31 }[..length]);
        }
    }

    private void AssertRejectedWithoutMutation(byte[] image) {
        File.WriteAllBytes(ImagePath, image);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
        Assert.Equal(image, File.ReadAllBytes(ImagePath));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
        Assert.Equal(image, File.ReadAllBytes(ImagePath));
    }

    private (byte[] Bytes, SizedPtr Prior, SizedPtr Tail) CreateImage(int payloadLength, bool builder = false) {
        string source = Path.Combine(_directory, $"source-{Guid.NewGuid():N}.rbf");
        SizedPtr prior;
        SizedPtr tail;
        using (var file = RbfFile.CreateNew(source)) {
            prior = file.Append(11, new byte[] { 1, 2, 3 }).Unwrap();
            tail = AppendTail(file, Payload(payloadLength), builder);
        }
        return (File.ReadAllBytes(source), prior, tail);
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 17 + 3)).ToArray();

    private static SizedPtr AppendTail(IRbfFile file, byte[] content, bool builder) {
        if (!builder) { return file.Append(22, content).Unwrap(); }
        using var frame = file.BeginAppend();
        content.CopyTo(frame.PayloadAndMeta.GetSpan(content.Length));
        frame.PayloadAndMeta.Advance(content.Length);
        return frame.EndAppend(22, tailMetaLength: Math.Min(content.Length, 3)).Unwrap();
    }

    private static List<RbfFrameInfo> Collect(IRbfFile file, bool reverse, bool showTombstone = false) {
        var result = new List<RbfFrameInfo>();
        if (reverse) {
            var scan = file.ScanReverse(showTombstone: showTombstone).GetEnumerator();
            while (scan.MoveNext()) { result.Add(scan.Current); }
            Assert.Null(scan.TerminationError);
        }
        else {
            var scan = file.ScanForward(showTombstone: showTombstone).GetEnumerator();
            while (scan.MoveNext()) { result.Add(scan.Current); }
            Assert.Null(scan.TerminationError);
        }
        return result;
    }
}
