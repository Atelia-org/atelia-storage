using Atelia.Data;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class JournalV2Tests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "journal-v2-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatePublishesFrozenMetadataAndLegacyOrIncompleteRootCannotBeAdopted() {
        using (var journal = EventJournal.CreateNew(_root)) { }
        Assert.Equal("454a464d0200100000000000b47af8d3", Convert.ToHexStringLower(File.ReadAllBytes(Path.Combine(_root, "journal.format"))));
        File.Delete(Path.Combine(_root, "journal.format"));
        var before = Snapshot();
        AssertRejected("LegacyOrIncompleteLayout", StorageOpenErrorKind.FormatUnsupported);
        AssertSnapshot(before);
    }

    [Theory]
    [InlineData("events/active.segment")]
    [InlineData("refs/catalog.snapshot")]
    public void IdentifiedV2RequiresMetadataBeforeWritableOpen(string relativePath) {
        using (var journal = EventJournal.CreateNew(_root)) { }
        File.Delete(Path.Combine(_root, relativePath));
        var before = Snapshot();
        AssertRejected("MetadataMissing", StorageOpenErrorKind.MaintenanceRequired);
        AssertSnapshot(before);
    }

    [Fact]
    public void UnknownRootVersionRejectedBeforeAnyWrite() {
        using (var journal = EventJournal.CreateNew(_root)) { }
        string marker = Path.Combine(_root, "journal.format");
        byte[] bytes = File.ReadAllBytes(marker);
        bytes[4] = 99;
        File.WriteAllBytes(marker, bytes);
        var before = Snapshot();
        AssertRejected("UnsupportedVersion", StorageOpenErrorKind.FormatUnsupported);
        AssertSnapshot(before);
    }

    [Fact]
    public void HeadAndSequenceIgnoreUnrelatedHistoricalPayloadCorruption() {
        RefId id;
        EventAddress start, old, current;
        using (var journal = EventJournal.CreateNew(_root)) {
            start = journal.AppendEventFrame(null, [1]).Unwrap();
            id = journal.CreateBranch("main", start).Unwrap();
            old = journal.AppendEventFrame(start, [2]).Unwrap();
            journal.AdvanceRef(id, start, old).Unwrap();
            current = journal.AppendEventFrame(null, [3]).Unwrap();
            journal.MoveRef(id, old, current).Unwrap();
        }
        string file = EventPath(1);
        Flip(file, start.Ticket);
        Flip(file, old.Ticket);
        using var reopened = EventJournal.OpenExisting(_root);
        Assert.Equal(current, reopened.GetHead(id));
        Assert.True(reopened.ReadEvent(start).IsFailure);
        var next = reopened.AppendEventFrame(current, [4]).Unwrap();
        Assert.Equal(4ul, reopened.ReadEventHeaderChecked(next).Unwrap().SequenceNumber);
    }

    [Fact]
    public void CurrentTargetFailureNeverFallsBackToOlderMove() {
        RefId id;
        EventAddress current;
        using (var journal = EventJournal.CreateNew(_root)) {
            var start = journal.AppendEventFrame(null, [1]).Unwrap();
            id = journal.CreateBranch("main", start).Unwrap();
            current = journal.AppendEventFrame(start, [2]).Unwrap();
            journal.AdvanceRef(id, start, current).Unwrap();
            journal.AppendEventFrame(null, [3]).Unwrap();
        }
        Flip(EventPath(1), current.Ticket);
        using var reopened = EventJournal.OpenExisting(_root);
        Assert.Equal("EventJournal.RefTargetInvalid", reopened.MoveRef(id, current, null).Error!.ErrorCode);
    }

    [Fact]
    public void RefMissingLocatorIsLazyMaintenanceErrorWithPreciseDetails() {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root)) { id = journal.CreateBranch("main", null).Unwrap(); }
        string locator = Path.Combine(_root, "refs", "objects", id.ToHexString(), "active.segment");
        File.Delete(locator);
        using var reopened = EventJournal.OpenExisting(_root);
        var error = reopened.MoveRef(id, null, null).Error!;
        Assert.Equal("EventJournal.MaintenanceRequired", error.ErrorCode);
        Assert.Equal("MetadataMissing", error.Details!["ReasonCode"]);
        Assert.Equal(locator, error.Details["StoragePath"]);
        Assert.Equal("EventJournal.MaintenanceRequired", reopened.ReadReflog(id).Error!.ErrorCode);
    }

    [Theory]
    [InlineData("EventBeforeAppend")]
    [InlineData("EventAfterDurableFlush")]
    public void PersistentEventExceptionsFaultEveryDataEntryAndDisposeRemainsAvailable(string stage) {
        var journal = EventJournal.CreateNew(_root);
        journal.OperationProbe = s => { if (s == stage) { throw new IOException("injected"); } };
        Assert.Throws<IOException>(() => journal.AppendEventFrame(null, [1]));
        AssertFaulted(journal);
        journal.Dispose();
        journal.Dispose();
        using var reopened = EventJournal.OpenExisting(_root);
    }

    [Theory]
    [InlineData("RefOpBeforeAppend")]
    [InlineData("RefOpAfterDurableFlush")]
    [InlineData("RefBeforeInstall")]
    public void RefPublicationAndInstallationExceptionsLatchFault(string stage) {
        using var journal = EventJournal.CreateNew(_root);
        journal.OperationProbe = s => { if (s == stage) { throw new IOException("injected"); } };
        Assert.Throws<IOException>(() => journal.CreateBranch("main", null));
        AssertFaulted(journal);
    }

    [Fact]
    public void PureValidationDoesNotFault() {
        using var journal = EventJournal.CreateNew(_root);
        Assert.True(journal.CreateBranch("BAD", null).IsFailure);
        journal.CreateBranch("main", null).Unwrap();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LazyRefUnknownFormatReportsStructuredFormatUnsupported(bool flags) {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root)) { id = journal.CreateBranch("main", null).Unwrap(); }
        string segment = Path.Combine(_root, "refs", "objects", id.ToHexString(), "segments", "00000001.rbf");
        File.Delete(segment);
        byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
        RefMoveFrameCodec.Encode(new(id, 1, 0, RefMoveOperation.Init, null, null, null, 0), payload);
        payload[flags ? 15 : 4] = flags ? (byte)128 : (byte)2;
        using (var file = Atelia.Rbf.RbfFile.CreateNew(segment)) { file.Append(EventJournal.RefMoveFrameTag, payload).Unwrap(); file.DurableFlush(); }
        using var reopened = EventJournal.OpenExisting(_root);
        var error = reopened.MoveRef(id, null, null).Error!;
        Assert.Equal("EventJournal.FormatUnsupported", error.ErrorCode);
        Assert.Equal(flags ? "UnsupportedFlags" : "UnsupportedVersion", error.Details!["ReasonCode"]);
        Assert.Equal(segment, error.Details["StoragePath"]);
        if (!flags) { Assert.Equal("2", error.Details["ObservedVersion"]); }
    }

    [Fact]
    public void CommitMoveSequenceOverflowRejectsBeforeCreatingOrphanAndDoesNotFault() {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root)) { id = journal.CreateBranch("main", null).Unwrap(); }
        string segment = Path.Combine(_root, "refs", "objects", id.ToHexString(), "segments", "00000001.rbf");
        using (var file = Atelia.Rbf.RbfFile.OpenExisting(segment)) {
            Span<byte> payload = stackalloc byte[RefMoveFrameCodec.FixedLength];
            RefMoveFrameCodec.Encode(new(id, ulong.MaxValue, 0, RefMoveOperation.Move, null, null, null, 0), payload);
            file.Append(EventJournal.RefMoveFrameTag, payload).Unwrap(); file.DurableFlush();
        }
        byte[] before = File.ReadAllBytes(EventPath(1));
        using (var reopened = EventJournal.OpenExisting(_root)) {
            Assert.Equal("EventJournal.RefMoveSequenceExhausted", reopened.CommitToRef(id, null, [1]).Error!.ErrorCode);
            Assert.Equal(before.Length, reopened.ReadPhysicalAppendFrontier().TailOffset);
            Assert.Null(reopened.GetHead(id));
        }
        Assert.Equal(before, File.ReadAllBytes(EventPath(1)));
    }

    [Fact]
    public void MissingTagTargetIsCheckedOnlyWhenThatTagIsResolved() {
        using (var journal = EventJournal.CreateNew(_root)) { journal.AppendEventFrame(null, [1]).Unwrap(); }
        using (var log = Atelia.Rbf.RbfFile.OpenExisting(Path.Combine(_root, "refs", "ref-op-log.rbf"))) {
            var absent = new EventAddress(SizedPtr.Create(4, 88), 99, default);
            log.Append(EventJournal.TagBindingFrameTag, TagBindingFrameCodec.Encode("missing", absent)).Unwrap(); log.DurableFlush();
        }
        using var reopened = EventJournal.OpenReadOnlyExisting(_root);
        Assert.Equal("EventJournal.TagTargetInvalid", reopened.ResolveTag("missing").Error!.ErrorCode);
        Assert.Equal("EventJournal.TagNotFound", reopened.ResolveTag("unrelated").Error!.ErrorCode);
    }

    private static void AssertFaulted(EventJournal journal) {
        Assert.Throws<InvalidOperationException>(() => journal.ListBranches());
        Assert.Throws<InvalidOperationException>(() => journal.ResolveTag("saved"));
        Assert.Throws<InvalidOperationException>(() => journal.AppendEventFrame(null, []));
        Assert.Throws<InvalidOperationException>(() => journal.ReadPhysicalAppendFrontier());
    }
    private void AssertRejected(string code, StorageOpenErrorKind kind) {
        foreach (var open in new Func<EventJournal>[] { () => EventJournal.OpenExisting(_root), () => EventJournal.OpenOrCreate(_root), () => EventJournal.OpenReadOnlyExisting(_root) }) {
            var error = Assert.Throws<StorageOpenException>(() => { using var journal = open(); });
            Assert.Equal(code, error.ReasonCode);
            Assert.Equal(kind, error.Kind);
        }
    }
    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    private void AssertSnapshot(Dictionary<string, byte[]> before) {
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var p in before) { Assert.Equal(p.Value, after[p.Key]); }
    }
    private string EventPath(uint n) => Path.Combine(_root, "events", "buckets", "000000", $"{n:x8}.rbf");
    private static void Flip(string path, SizedPtr ticket) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = ticket.Offset + 4;
        int b = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(b ^ 1));
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
