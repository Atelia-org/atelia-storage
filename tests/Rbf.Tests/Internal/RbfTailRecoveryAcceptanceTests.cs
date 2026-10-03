using System.Buffers.Binary;
using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>Public Open, real prefix-write faults, and explicit legacy read-only responsibility.</summary>
public sealed class RbfTailRecoveryAcceptanceTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rbf-tail-acceptance-{Guid.NewGuid():N}");
    private string ImagePath => Path.Combine(_directory, "image.rbf");
    public RbfTailRecoveryAcceptanceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() {
        RbfWriteInstrumentation.Current = null;
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(7, false)]
    [InlineData(12, false)] [InlineData(31, false)] [InlineData(64, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(7, true)]
    [InlineData(12, true)] [InlineData(31, true)] [InlineData(64, true)]
    public void EveryOriginalBytePrefix_HasOneAction_StableFrames_AndCanAppend(int payloadLength, bool builder) {
        var (complete, prior, tail) = CreateImage(payloadLength, builder);
        int start = checked((int)tail.Offset);
        for (int cut = start; cut <= complete.Length; cut++) {
            File.WriteAllBytes(ImagePath, complete[..cut]);
            RbfTailRecoveryAction expected = ExpectedAction(cut, start, tail.Length, complete.Length);
            bool retainsTail = cut == complete.Length || expected == RbfTailRecoveryAction.CompletedTail;
            using (var file = RbfFile.OpenExisting(ImagePath, out var report)) {
                Assert.Equal(expected, report.Action);
                Assert.Equal(cut, report.OriginalLength);
                Assert.Equal(retainsTail ? complete.Length : start, report.FinalLength);
                Assert.Equal(expected == RbfTailRecoveryAction.None ? (long?)null : tail.Offset, report.AffectedFrameOffset);
                Assert.Equal(expected == RbfTailRecoveryAction.CompletedTail ? tail : (SizedPtr?)null, report.FrameTicket);
                var physical = Collect(file, false, true);
                Assert.Equal(retainsTail ? 2 : 1, physical.Count);
                Assert.Equal(prior, physical[0].Ticket);
                Assert.All(physical, info => Assert.False(info.IsTombstone));
                if (retainsTail) {
                    Assert.Equal(tail, physical[1].Ticket);
                    using var frame = file.ReadPooledFrame(tail).Unwrap();
                    Assert.Equal(22u, frame.Tag);
                    Assert.Equal(Payload(payloadLength), frame.PayloadAndMeta.ToArray());
                    Assert.Equal(builder ? Math.Min(payloadLength, 3) : 0, physical[1].TailMetaLength);
                }
                Assert.Equal(physical.AsEnumerable().Reverse().Select(x => x.Ticket),
                    Collect(file, true, true).Select(x => x.Ticket));
            }
            byte[] repaired = File.ReadAllBytes(ImagePath);
            Assert.Equal(retainsTail ? complete : complete[..start], repaired);
            using var reopened = RbfFile.OpenExisting(ImagePath, out var again);
            Assert.Equal(RbfTailRecoveryAction.None, again.Action);
            Assert.Null(again.AffectedFrameOffset);
            Assert.Null(again.FrameTicket);
            var appended = reopened.Append(33, new byte[] { 9, 8, 7 }).Unwrap();
            Assert.Equal(repaired.Length, appended.Offset);
            using var appendedFrame = reopened.ReadPooledFrame(appended).Unwrap();
            Assert.Equal(new byte[] { 9, 8, 7 }, appendedFrame.PayloadAndMeta.ToArray());
        }
    }

    [Fact]
    public void EveryRepairWritePrefix_ReopensWithinTheSameTailFrame() {
        var (complete, _, tail) = CreateImage(12);
        int start = checked((int)tail.Offset);
        // Body complete: only the Key/Fence suffix is eligible for completion.
        for (int cut = start + tail.Length - 4; cut < complete.Length; cut++) {
            for (int written = 0; written <= complete.Length - cut; written++) {
                File.WriteAllBytes(ImagePath, complete[..cut]);
                int remaining = written;
                RbfWriteInstrumentation.Current = new() {
                    BeforeWrite = request => Math.Min(request.RequestedBytes, remaining),
                    AfterWrite = observation => {
                        remaining -= observation.WrittenBytes;
                        if (remaining == 0) throw new IOException("Stopped at repair suffix prefix.");
                    }
                };
                try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
                finally { RbfWriteInstrumentation.Current = null; }
                byte[] interrupted = File.ReadAllBytes(ImagePath);
                Assert.Equal(cut + written, interrupted.Length);
                Assert.Equal(complete[..interrupted.Length], interrupted);
                using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
                Assert.Equal(interrupted.Length == complete.Length ? RbfTailRecoveryAction.None
                    : RbfTailRecoveryAction.CompletedTail, report.Action);
                using var frame = reopened.ReadPooledFrame(tail).Unwrap();
                Assert.False(frame.IsTombstone);
                Assert.Equal(Payload(12), frame.PayloadAndMeta.ToArray());
                Assert.Equal(2, Collect(reopened, false, true).Count);
            }
        }
    }

    [Theory]
    [InlineData(false, 12)] [InlineData(true, 12)]
    [InlineData(false, 200000)] [InlineData(true, 200000)]
    public void ActualAppendOutputFailures_LeaveExactlyWrittenPrefix_AndRecover(bool builder, int payloadLength) {
        var (complete, _, tail) = CreateImage(payloadLength, builder);
        int start = checked((int)tail.Offset);
        byte[] prefix = complete[..start];
        var observed = new List<RbfWriteObservation>();
        File.WriteAllBytes(ImagePath, prefix);
        using (var file = RbfFile.OpenExisting(ImagePath, out _)) {
            RbfWriteInstrumentation.Current = new() { AfterWrite = write => observed.Add(write) };
            try { Assert.Equal(tail, AppendTail(file, Payload(payloadLength), builder)); }
            finally { RbfWriteInstrumentation.Current = null; }
        }
        Assert.Equal(complete, File.ReadAllBytes(ImagePath));
        Assert.NotEmpty(observed);
        int total = complete.Length - start;
        var cuts = payloadLength < 100 ? Enumerable.Range(0, total + 1).ToHashSet() : new HashSet<int> { 0, total };
        foreach (var write in observed) {
            int offset = checked((int)(write.Offset - start));
            cuts.Add(offset); cuts.Add(offset + 1); cuts.Add(offset + write.RequestedBytes - 1);
            cuts.Add(offset + write.RequestedBytes);
        }
        foreach (int cut in cuts.Where(c => c >= 0 && c <= total).Order()) {
            File.WriteAllBytes(ImagePath, prefix);
            using (var file = RbfFile.OpenExisting(ImagePath, out _)) {
                int remaining = cut;
                RbfWriteInstrumentation.Current = new() {
                    BeforeWrite = request => Math.Min(request.RequestedBytes, remaining),
                    AfterWrite = observation => {
                        remaining -= observation.WrittenBytes;
                        if (remaining == 0) throw new IOException("Stopped at actual append prefix.");
                    }
                };
                try { Assert.Throws<IOException>(() => AppendTail(file, Payload(payloadLength), builder)); }
                finally { RbfWriteInstrumentation.Current = null; }
                Assert.Equal(start, file.TailOffset);
            }
            Assert.Equal(complete[..(start + cut)], File.ReadAllBytes(ImagePath));
            using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
            Assert.Equal(ExpectedAction(start + cut, start, tail.Length, complete.Length), report.Action);
            Assert.All(Collect(reopened, false, true), info => Assert.False(info.IsTombstone));
            if (cut >= tail.Length - 4) {
                using var frame = reopened.ReadPooledFrame(tail).Unwrap();
                Assert.Equal(Payload(payloadLength), frame.PayloadAndMeta.ToArray());
            }
        }
    }

    [Fact]
    public void LargeIncompleteCoverage_IsTruncated_WithoutSynthesizingContent() {
        var (complete, prior, tail) = CreateImage(200000);
        File.WriteAllBytes(ImagePath, complete[..checked((int)tail.Offset + 65537)]);
        using (var reopened = RbfFile.OpenExisting(ImagePath, out var report)) {
            Assert.Equal(RbfTailRecoveryAction.Truncated, report.Action);
            Assert.Null(report.FrameTicket);
            Assert.Equal(tail.Offset, reopened.TailOffset);
            Assert.Equal(prior, Assert.Single(Collect(reopened, false, true)).Ticket);
            Assert.True(reopened.ReadPooledFrame(tail).IsFailure);
        }
        Assert.Equal(complete[..checked((int)tail.Offset)], File.ReadAllBytes(ImagePath));
    }

    [Fact]
    public void ConsecutiveLegacyTombstones_AreReadOnlyPhysicalMembers_AndHiddenFromNormalStream() {
        using (var legacy = RawRbfTestFile.CreateLegacy(ImagePath)) {
            legacy.Append(1, "prior"u8).Unwrap();
            legacy.Append(0, "first"u8, isTombstone: true).Unwrap();
            legacy.Append(0, "second"u8, isTombstone: true).Unwrap();
            legacy.Append(4, "last"u8).Unwrap();
        }
        byte[] before = File.ReadAllBytes(ImagePath);
        using (var file = RbfFile.OpenReadOnlyExisting(ImagePath)) {
            var physical = Collect(file, false, true);
            Assert.Equal(4, physical.Count);
            Assert.True(physical[1].IsTombstone); Assert.True(physical[2].IsTombstone);
            Assert.Equal(2, Collect(file, false).Count);
            Assert.Equal(physical.AsEnumerable().Reverse().Select(x => x.Ticket), Collect(file, true, true).Select(x => x.Ticket));
            using var tombstone = physical[1].ReadPooledFrame().Unwrap();
            Assert.Equal("first"u8.ToArray(), tombstone.PayloadAndMeta.ToArray());
        }
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
        Assert.Equal(before, File.ReadAllBytes(ImagePath));
    }

    [Theory]
    [InlineData("before-truncate")] [InlineData("after-truncate")]
    [InlineData("before-flush")] [InlineData("after-flush")]
    public void PartialHeadTruncate_FailuresReleaseHandle_AndReopen(string failurePoint) {
        var (complete, _, tail) = CreateImage(12);
        File.WriteAllBytes(ImagePath, complete[..checked((int)tail.Offset + 1)]);
        RbfWriteInstrumentation.Current = new() {
            BeforeSetLength = _ => { if (failurePoint == "before-truncate") throw new IOException("before truncate"); },
            AfterSetLength = _ => { if (failurePoint == "after-truncate") throw new IOException("after truncate"); },
            BeforeFlush = _ => { if (failurePoint == "before-flush") throw new IOException(failurePoint); },
            AfterFlush = _ => { if (failurePoint == "after-flush") throw new IOException(failurePoint); }
        };
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        using var reopened = RbfFile.OpenExisting(ImagePath, out _);
        Assert.Equal(tail.Offset, reopened.TailOffset);
        Assert.Single(Collect(reopened, false, true));
    }

    [Theory]
    [InlineData("before-write")] [InlineData("partial-write")] [InlineData("after-write")]
    [InlineData("before-flush")] [InlineData("after-flush")]
    public void PureFenceCompletion_FailuresNeverDiscardFrameBytes(string failurePoint) {
        var (complete, _, tail) = CreateImage(12);
        int length = checked((int)tail.Offset + tail.Length);
        File.WriteAllBytes(ImagePath, complete[..length]);
        RbfWriteInstrumentation.Current = Hooks(failurePoint);
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        byte[] interrupted = File.ReadAllBytes(ImagePath);
        Assert.True(interrupted.Length >= length);
        Assert.Equal(complete[..length], interrupted[..length]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out _);
        using var frame = reopened.ReadPooledFrame(tail).Unwrap();
        Assert.Equal(Payload(12), frame.PayloadAndMeta.ToArray());
    }

    [Theory]
    [InlineData("before-write")] [InlineData("partial-write")] [InlineData("after-write")]
    [InlineData("before-flush")] [InlineData("after-flush")]
    public void TailKeyCompletion_MutationAndFlushFailures_ReleaseHandle_AndCanReopen(string failurePoint) {
        var (complete, _, tail) = CreateImage(12);
        int cut = checked((int)tail.Offset + tail.Length - 4);
        File.WriteAllBytes(ImagePath, complete[..cut]);
        RbfWriteInstrumentation.Current = Hooks(failurePoint);
        try { Assert.Throws<IOException>(() => RbfFile.OpenExisting(ImagePath, out _)); }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(complete[..cut], File.ReadAllBytes(ImagePath)[..cut]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out _);
        using var frame = reopened.ReadPooledFrame(tail).Unwrap();
        Assert.False(frame.IsTombstone);
        Assert.Equal(Payload(12), frame.PayloadAndMeta.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteBody_StructuralCrcRejects_AndPayloadCrcIsDeferred_EvenWithoutFence(bool trailerCrc) {
        var (complete, _, tail) = CreateImage(12);
        int offset = checked((int)tail.Offset + tail.Length - (trailerCrc ? 20 : 24));
        complete[offset] ^= 1;
        File.WriteAllBytes(ImagePath, complete[..^4]);
        if (trailerCrc) {
            Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
            Assert.Equal(complete[..^4], File.ReadAllBytes(ImagePath));
        }
        else {
            using var file = RbfFile.OpenExisting(ImagePath, out var report);
            Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Action);
            Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(tail).Error);
        }
    }

    [Fact]
    public void IncompleteTrailer_IsTruncated_WithoutPartialCrcCompletionSolving() {
        var (complete, _, tail) = CreateImage(12);
        int trailer = checked((int)tail.Offset + tail.Length - 20);
        complete[trailer] ^= 1;
        File.WriteAllBytes(ImagePath, complete[..(trailer + 3)]);
        using var reopened = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Action);
        Assert.Equal(tail.Offset, reopened.TailOffset);
    }

    [Fact]
    public void LegacyEarlierStructure_IsEagerlyChecked_WithoutRepairingOrSkippingDamage() {
        using (var legacy = RawRbfTestFile.CreateLegacy(ImagePath)) {
            legacy.Append(1, "prior"u8).Unwrap();
            legacy.Append(2, "tail"u8).Unwrap();
        }
        byte[] bytes = File.ReadAllBytes(ImagePath); bytes[4] ^= 1;
        File.WriteAllBytes(ImagePath, bytes);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
        Assert.Equal(bytes, File.ReadAllBytes(ImagePath));
    }

    [Fact]
    public void OpenDefersHistoricalAndTailPayloadCrc_ToExplicitCheckedRead() {
        var (complete, prior, tail) = CreateImage(12);
        complete[checked((int)prior.Offset + 4)] ^= 1;
        complete[checked((int)tail.Offset + 4)] ^= 1;
        File.WriteAllBytes(ImagePath, complete);
        using var file = RbfFile.OpenExisting(ImagePath, out var report);
        Assert.Equal(RbfTailRecoveryAction.None, report.Action);
        Assert.Equal(2, Collect(file, false).Count);
        Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(prior).Error);
        Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(tail).Error);
    }

    [Fact]
    public void LegacyEmbeddedCompleteFrameAtEof_IsNotExposedAsAMainSequenceMember() {
        byte[] embedded = "RBF1"u8.ToArray().Concat(RawRbfTestFile.LegacyFrame(8, "inner"u8))
            .Concat("RBF1"u8.ToArray()).ToArray();
        SizedPtr outer;
        using (var legacy = RawRbfTestFile.CreateLegacy(ImagePath)) {
            legacy.Append(1, "prior"u8).Unwrap();
            outer = legacy.Append(2, embedded).Unwrap();
        }
        byte[] bytes = File.ReadAllBytes(ImagePath)[..checked((int)outer.Offset + 4 + embedded.Length)];
        File.WriteAllBytes(ImagePath, bytes);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
        Assert.Equal(bytes, File.ReadAllBytes(ImagePath));
    }

    [Fact]
    public void ReadOnlyOpen_RejectsEveryIncompletePrefix_WithoutChangingBytes() {
        var (complete, _, tail) = CreateImage(12);
        for (int cut = checked((int)tail.Offset + 1); cut < complete.Length; cut++) {
            File.WriteAllBytes(ImagePath, complete[..cut]);
            Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(ImagePath));
            Assert.Equal(complete[..cut], File.ReadAllBytes(ImagePath));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void HeaderCreationResidue_IsNotARecoverableFrame(int cut) {
        byte[] bytes = ("RBF3"u8)[..cut].ToArray();
        File.WriteAllBytes(ImagePath, bytes);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(ImagePath, out _));
        Assert.Equal(bytes, File.ReadAllBytes(ImagePath));
    }

    private (byte[] Bytes, SizedPtr Prior, SizedPtr Tail) CreateImage(int length, bool builder = false) {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".rbf");
        SizedPtr prior, tail;
        using (var file = RbfFile.CreateNew(path)) {
            prior = file.Append(11, "prior"u8).Unwrap();
            tail = AppendTail(file, Payload(length), builder);
        }
        byte[] bytes = File.ReadAllBytes(path);
        // Fixed inputs permit Key0, so repeated writes have the same exact prefix oracle.
        // Random Key distribution and forced failures have separate tests.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)tail.Offset + tail.Length - 4))));
        return (bytes, prior, tail);
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13 + 7)).ToArray();
    private static SizedPtr AppendTail(IRbfFile file, byte[] payload, bool builder) {
        if (!builder) return file.Append(22, payload).Unwrap();
        using var frame = file.BeginAppend();
        for (int offset = 0; offset < payload.Length;) {
            int count = Math.Min(1024, payload.Length - offset);
            payload.AsSpan(offset, count).CopyTo(frame.PayloadAndMeta.GetSpan(count));
            frame.PayloadAndMeta.Advance(count);
            offset += count;
        }
        return frame.EndAppend(22, Math.Min(payload.Length, 3)).Unwrap();
    }
    private static RbfTailRecoveryAction ExpectedAction(int cut, int start, int length, int complete) =>
        cut == start || cut == complete ? RbfTailRecoveryAction.None :
        cut < start + length - 4 ? RbfTailRecoveryAction.Truncated : RbfTailRecoveryAction.CompletedTail;
    private static RbfWriteHooks Hooks(string point) => new() {
        BeforeWrite = request => {
            if (point == "before-write") throw new IOException(point);
            return point == "partial-write" ? 1 : request.RequestedBytes;
        },
        AfterWrite = _ => { if (point == "after-write") throw new IOException(point); },
        BeforeFlush = _ => { if (point == "before-flush") throw new IOException(point); },
        AfterFlush = _ => { if (point == "after-flush") throw new IOException(point); }
    };
    private static List<RbfFrameInfo> Collect(IRbfFile file, bool reverse, bool showTombstone = false) {
        var frames = new List<RbfFrameInfo>();
        if (reverse) {
            var scan = file.ScanReverse(showTombstone: showTombstone).GetEnumerator();
            while (scan.MoveNext()) frames.Add(scan.Current);
            Assert.Null(scan.TerminationError);
        }
        else {
            var scan = file.ScanForward(showTombstone: showTombstone).GetEnumerator();
            while (scan.MoveNext()) frames.Add(scan.Current);
            Assert.Null(scan.TerminationError);
        }
        return frames;
    }
}
