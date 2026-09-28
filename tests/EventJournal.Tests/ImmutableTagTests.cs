using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using System.Buffers.Binary;
using System.Reflection;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class ImmutableTagTests : IDisposable {
    private readonly List<string> _paths = [];

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch { /* best-effort temporary-directory cleanup */ }
        }
    }

    [Fact]
    public void CreateAndResolveTag_PersistsAcrossWritableReadOnlyAndOpenOrCreate() {
        string path = NewPath();
        EventAddress target;
        using (var journal = EventJournal.CreateNew(path)) {
            target = journal.AppendEventFrame(null, [1], hint: new AddressHint(0x1122_3344)).Unwrap();
            Assert.True(journal.CreateTag("release.1", target).Unwrap());
        }

        using (var journal = EventJournal.OpenExisting(path)) {
            Assert.Equal(target, journal.ResolveTag("release.1").Unwrap());
        }
        using (var journal = EventJournal.OpenReadOnlyExisting(path)) {
            Assert.Equal(target, journal.ResolveTag("release.1").Unwrap());
        }
        using (var journal = EventJournal.OpenOrCreate(path)) {
            Assert.Equal(target, journal.ResolveTag("release.1").Unwrap());
        }
    }

    [Fact]
    public void TagAndBranchNamesAreIndependent_AndTagSurvivesRefMoveAndArchive() {
        string path = NewPath();
        using var journal = EventJournal.CreateNew(path);
        EventAddress root = journal.AppendEventFrame(null, [1]).Unwrap();
        EventAddress moved = journal.AppendEventFrame(root, [2]).Unwrap();
        RefId branch = journal.CreateBranch("stable", root).Unwrap();

        Assert.True(journal.CreateTag("stable", root).Unwrap());
        Assert.True(journal.MoveRef(branch, root, moved).Unwrap());
        Assert.True(journal.ArchiveRef(branch, moved).Unwrap());

        Assert.Equal(root, journal.ResolveTag("stable").Unwrap());
        Assert.True(journal.OpenBranch("stable").IsFailure);
    }

    [Fact]
    public void CreateTag_RejectsDuplicateAndInvalidInputsWithoutWritingAnyStore() {
        string path = NewPath();
        using var journal = EventJournal.CreateNew(path);
        EventAddress target = journal.AppendEventFrame(null, [1], hint: new AddressHint(7)).Unwrap();
        Assert.True(journal.CreateTag("once", target).Unwrap());
        StoreLengths before = Measure(path);

        Assert.Equal("EventJournal.TagAlreadyExists", journal.CreateTag("once", target).Error!.ErrorCode);
        Assert.Equal("EventJournal.TagAlreadyExists", journal.CreateTag("once", target with { Hint = new AddressHint(8) }).Error!.ErrorCode);
        foreach (string? name in new[] { null, "", ".", "a.", "a.lock", "Upper", "a/b", "é", new string('a', 129) }) {
            Assert.Equal("EventJournal.TagNameInvalid", journal.CreateTag(name!, target).Error!.ErrorCode);
        }
        Assert.Equal("EventJournal.TagTargetInvalid", journal.CreateTag("default", default).Error!.ErrorCode);
        Assert.Equal("EventJournal.TagTargetInvalid", journal.CreateTag("missing", new EventAddress(SizedPtr.Create(4, 24), 99, default)).Error!.ErrorCode);
        Assert.Equal("EventJournal.TagTargetInvalid", journal.CreateTag("hint", target with { Hint = new AddressHint(9) }).Error!.ErrorCode);

        Assert.Equal(before, Measure(path));
        Assert.Equal("EventJournal.TagNotFound", journal.ResolveTag("absent").Error!.ErrorCode);
    }

    [Fact]
    public void CreateTag_ReadOnlyAndDisposedRejectWithoutAppending() {
        string path = NewPath();
        EventAddress target;
        using (var journal = EventJournal.CreateNew(path)) {
            target = journal.AppendEventFrame(null, [1]).Unwrap();
        }
        StoreLengths before = Measure(path);
        IReadOnlyDictionary<string, byte[]> bytesBefore = SnapshotTree(path);
        using (var readOnly = EventJournal.OpenReadOnlyExisting(path)) {
            Assert.Throws<InvalidOperationException>(() => readOnly.CreateTag("readonly", target));
            Assert.Equal("EventJournal.TagNotFound", readOnly.ResolveTag("missing").Error!.ErrorCode);
        }
        Assert.Equal(before, Measure(path));
        AssertTreeEqual(bytesBefore, SnapshotTree(path));

        var disposed = EventJournal.OpenExisting(path);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.CreateTag("disposed", target));
        Assert.Throws<ObjectDisposedException>(() => disposed.ResolveTag("disposed"));
    }

    [Fact]
    public void CreateTag_OnHistoricalSegmentDoesNotRotateAndRemainsResolvable() {
        string path = NewPath();
        var options = SmallSegments();
        EventAddress root;
        using (var journal = EventJournal.CreateNew(path, options)) {
            root = journal.AppendEventFrame(null, [1]).Unwrap();
            _ = journal.AppendEventFrame(root, [2]).Unwrap();
            Assert.True(journal.ActiveSegmentNumber > root.SegmentNumber);
            uint active = journal.ActiveSegmentNumber;
            Assert.True(journal.CreateTag("historic", root).Unwrap());
            Assert.Equal(active, journal.ActiveSegmentNumber);
        }
        using var reopened = EventJournal.OpenReadOnlyExisting(path, StrictOptions());
        Assert.Equal(root, reopened.ResolveTag("historic").Unwrap());
    }

    [Fact]
    public void CreateTag_AppendRefusalDoesNotFaultOrAppendAndCanBeRetried() {
        string path = NewPath();
        using var journal = EventJournal.CreateNew(path);
        EventAddress target = journal.AppendEventFrame(null, [1]).Unwrap();
        StoreLengths before = Measure(path);

        FieldInfo refLogField = typeof(EventJournal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object refLog = refLogField.GetValue(journal)!;
        FieldInfo tailOffset = refLog.GetType().GetField("_tailOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long originalTail = (long)tailOffset.GetValue(refLog)!;
        try {
            tailOffset.SetValue(refLog, SizedPtr.MaxOffset + SizedPtr.Alignment);
            var refused = journal.CreateTag("retry", target);
            Assert.True(refused.IsFailure);
            Assert.StartsWith("Rbf.", refused.Error!.ErrorCode);
            Assert.Equal(before, Measure(path));
        }
        finally {
            tailOffset.SetValue(refLog, originalTail);
        }

        Assert.True(journal.CreateTag("retry", target).Unwrap());
        Assert.Equal(target, journal.ResolveTag("retry").Unwrap());
    }

    [Fact]
    public void TagBindingCodec_HasFixedLittleEndianWitness() {
        var address = new EventAddress(SizedPtr.Create(4, 24), 1, new AddressHint(0x4433_2211));
        byte[] bytes = TagBindingFrameCodec.Encode("a", address);
        byte[] expected = [
            0x45, 0x4A, 0x54, 0x47, 0x01, 0x00, 0x20, 0x00,
            0x06, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x61
        ];
        Assert.Equal(expected, bytes);
        Assert.Equal(new TagBindingFrame("a", address), TagBindingFrameCodec.Decode(expected));
    }

    [Theory]
    [MemberData(nameof(InvalidTagFrameMutations))]
    public void StrictOpen_RejectsMalformedTagFramesWithoutChangingBytes(string _, Func<byte[], byte[]> mutate, bool tailMeta, bool corruptCrc, uint tag) {
        string path = NewPath();
        EventAddress target = CreateTarget(path);
        byte[] payload = mutate(TagBindingFrameCodec.Encode("raw", target));
        SizedPtr ticket = AppendRawTag(path, tag, payload, tailMeta ? [0x55] : []);
        if (corruptCrc) { CorruptPayloadByte(RefLogPath(path), ticket); }
        byte[] before = File.ReadAllBytes(RefLogPath(path));

        AssertAllStrictOpenEntrypointsReject(path, before);
    }

    public static IEnumerable<object[]> InvalidTagFrameMutations() {
        yield return Case("unknown-tag", p => p, false, false, tag: 0x1234_5678);
        yield return Case("version", p => { BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 2); return p; });
        yield return Case("magic", p => { p[0] ^= 1; return p; });
        yield return Case("short-header", p => p[..20]);
        yield return Case("header-length", p => { p[6] = 31; return p; });
        yield return Case("empty-name", p => { p[24] = 0; return p[..32]; });
        yield return Case("invalid-name", p => { p[32] = (byte)'A'; return p; });
        yield return Case("trailing-byte", p => [.. p, 0]);
        yield return Case("length", p => { BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(24), 2); return p; });
        yield return Case("reserved", p => { p[26] = 1; return p; });
        yield return Case("non-ascii", p => { p[32] = 0xFF; return p; });
        yield return Case("default-address", p => { p.AsSpan(8, 16).Clear(); return p; });
        yield return Case("missing-target", p => { BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), 99); return p; });
        yield return Case("tail-meta", p => p, true, false);
        yield return Case("crc", p => p, false, true);

        static object[] Case(string name, Func<byte[], byte[]> mutation, bool tailMeta = false, bool crc = false, uint tag = EventJournal.TagBindingFrameTag)
            => [name, mutation, tailMeta, crc, tag];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StrictOpen_RejectsDuplicatePersistedTagBinding(bool sameTarget) {
        string path = NewPath();
        EventAddress target;
        EventAddress otherTarget;
        using (var journal = EventJournal.CreateNew(path)) {
            target = journal.AppendEventFrame(null, [1], hint: new AddressHint(0xAA)).Unwrap();
            otherTarget = journal.AppendEventFrame(target, [2], hint: new AddressHint(0xBB)).Unwrap();
        }
        AppendRawTag(path, EventJournal.TagBindingFrameTag, TagBindingFrameCodec.Encode("duplicate", target), []);
        EventAddress second = sameTarget ? target : otherTarget;
        AppendRawTag(path, EventJournal.TagBindingFrameTag, TagBindingFrameCodec.Encode("duplicate", second), []);
        byte[] before = File.ReadAllBytes(RefLogPath(path));

        AssertAllStrictOpenEntrypointsReject(path, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictOpen_RejectsTombstoneAndTruncatedTagTailWithoutChangingBytes(bool truncate) {
        string path = NewPath();
        EventAddress target = CreateTarget(path);
        SizedPtr ticket = AppendRawTag(path, EventJournal.TagBindingFrameTag, TagBindingFrameCodec.Encode("bad-tail", target), []);
        if (truncate) {
            using var stream = new FileStream(RefLogPath(path), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.SetLength(stream.Length - 1);
            stream.Flush(flushToDisk: true);
        }
        else {
            MarkTombstone(path, ticket);
        }
        byte[] before = File.ReadAllBytes(RefLogPath(path));

        AssertAllStrictOpenEntrypointsReject(path, before);
    }

    [Fact]
    public void StrictOpen_RejectsTagWhoseTargetEventCrcIsBadWithoutChangingEitherStore() {
        string path = NewPath();
        EventAddress target;
        using (var journal = EventJournal.CreateNew(path)) {
            target = journal.AppendEventFrame(null, [1], hint: new AddressHint(0xCC)).Unwrap();
            Assert.True(journal.CreateTag("event-crc", target).Unwrap());
        }
        CorruptPayloadByte(EventSegmentPath(path, target.SegmentNumber), target.Ticket);
        byte[] eventBefore = File.ReadAllBytes(EventSegmentPath(path, target.SegmentNumber));
        byte[] refBefore = File.ReadAllBytes(RefLogPath(path));

        AssertAllStrictOpenEntrypointsReject(path, refBefore);
        Assert.Equal(eventBefore, File.ReadAllBytes(EventSegmentPath(path, target.SegmentNumber)));
    }

    [Theory]
    [InlineData(0, TagPublicationOutcome.NotAttempted, false)]
    [InlineData(1, TagPublicationOutcome.NotAttempted, false)]
    [InlineData(2, TagPublicationOutcome.Unknown, true)]
    [InlineData(3, TagPublicationOutcome.Confirmed, true)]
    public void CreateTag_PublicationFailureFaultsInstanceAndStrictReopenShowsDurableTruth(int stageValue, TagPublicationOutcome outcome, bool survives) {
        string path = NewPath();
        EventAddress target;
        var journal = EventJournal.CreateNew(path);
        try {
            target = journal.AppendEventFrame(null, [1]).Unwrap();
            TagPublicationStage stage = (TagPublicationStage)stageValue;
            journal.TagPublicationProbe = observed => {
                if (observed == stage) { throw new IOException("deterministic test witness"); }
            };
            TagPublicationException error = Assert.Throws<TagPublicationException>(() => journal.CreateTag("uncertain", target));
            Assert.Equal(outcome, error.Outcome);
            Assert.Equal("uncertain", error.TagName);
            Assert.Throws<InvalidOperationException>(() => journal.AppendEventFrame(null, [2]));
            Assert.Throws<InvalidOperationException>(() => journal.CreateBranch("blocked", target));
            Assert.Throws<InvalidOperationException>(() => journal.ResolveTag("uncertain"));
            Assert.Throws<InvalidOperationException>(() => journal.ReadAncestorChain(target));
        }
        finally {
            journal.Dispose();
        }

        using var reopened = EventJournal.OpenReadOnlyExisting(path, StrictOptions());
        if (survives) {
            Assert.Equal(target, reopened.ResolveTag("uncertain").Unwrap());
        }
        else {
            Assert.Equal("EventJournal.TagNotFound", reopened.ResolveTag("uncertain").Error!.ErrorCode);
        }
    }

    private static EventJournalOptions SmallSegments() => new() {
        EventSegmentStoreOptions = new RbfSegmentStoreOptions { SegmentSizeThresholdBytes = 8 }
    };

    private static EventJournalOptions StrictOptions() => new() {
        EventSegmentStoreOptions = new RbfSegmentStoreOptions(),
        RefSegmentStoreOptions = new RbfSegmentStoreOptions(),
        RefOpLogOptions = new RefOpLogOptions { RecoverActiveTailOnOpen = false }
    };

    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), "atelia-immutable-tags-" + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    private static EventAddress CreateTarget(string path) {
        using var journal = EventJournal.CreateNew(path);
        return journal.AppendEventFrame(null, [1], hint: new AddressHint(0xAA)).Unwrap();
    }

    private static StoreLengths Measure(string path) => new(
        Directory.EnumerateFiles(Path.Combine(path, "events"), "*.rbf", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length),
        new FileInfo(RefLogPath(path)).Length,
        Directory.Exists(Path.Combine(path, "refs", "objects"))
            ? Directory.EnumerateFiles(Path.Combine(path, "refs", "objects"), "*.rbf", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length)
            : 0
    );

    private static string RefLogPath(string path) => Path.Combine(path, "refs", "ref-op-log.rbf");

    private static string EventSegmentPath(string path, uint segmentNumber) => Path.Combine(
        path, "events", "buckets", "000000", $"{segmentNumber:x8}.rbf"
    );

    private static void AssertAllStrictOpenEntrypointsReject(string path, byte[] expectedRefLogBytes) {
        Assert.Throws<InvalidDataException>(() => { using var unexpected = EventJournal.OpenExisting(path, StrictOptions()); });
        Assert.Equal(expectedRefLogBytes, File.ReadAllBytes(RefLogPath(path)));
        Assert.Throws<InvalidDataException>(() => { using var unexpected = EventJournal.OpenOrCreate(path, StrictOptions()); });
        Assert.Equal(expectedRefLogBytes, File.ReadAllBytes(RefLogPath(path)));
        Assert.Throws<InvalidDataException>(() => { using var unexpected = EventJournal.OpenReadOnlyExisting(path, StrictOptions()); });
        Assert.Equal(expectedRefLogBytes, File.ReadAllBytes(RefLogPath(path)));
    }

    private static SizedPtr AppendRawTag(string path, uint tag, byte[] payload, byte[] tailMeta) {
        using IRbfFile log = RbfFile.OpenExisting(RefLogPath(path));
        SizedPtr ticket = log.Append(tag, payload, tailMeta).Unwrap();
        log.DurableFlush();
        return ticket;
    }

    private static void CorruptPayloadByte(string path, SizedPtr ticket) {
        long offset = ticket.Offset + 4;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x80));
        stream.Flush(flushToDisk: true);
    }

    private static void MarkTombstone(string path, SizedPtr ticket) {
        long trailerOffset = ticket.EndOffsetExclusive - 16;
        Span<byte> trailer = stackalloc byte[16];
        using var stream = new FileStream(RefLogPath(path), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = trailerOffset;
        stream.ReadExactly(trailer);
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) | 0x8000_0000u;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], descriptor);
        RollingCrc.SealCodewordBackward(trailer);
        stream.Position = trailerOffset;
        stream.Write(trailer);
        stream.Flush(flushToDisk: true);
    }

    private static IReadOnlyDictionary<string, byte[]> SnapshotTree(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(
            path => Path.GetRelativePath(root, path),
            File.ReadAllBytes,
            StringComparer.Ordinal
        );

    private static void AssertTreeEqual(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) {
            Assert.Equal(bytes, actual[path]);
        }
    }

    private readonly record struct StoreLengths(long Events, long RefLog, long RefObjects);
}
