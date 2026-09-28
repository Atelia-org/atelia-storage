using System.Buffers.Binary;
using Atelia.Data.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.EventJournal.Toolkit.Tests;

public sealed class ToolkitIntegrationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolkit-integration-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output");
    private string Log => Path.Combine(Source, "refs", "ref-op-log.rbf");
    private string EventFile(uint number = 1) => Path.Combine(Source, "events", "buckets", "000000", $"{number:x8}.rbf");
    private string ObjectRoot(RefId id) => Path.Combine(Source, "refs", "objects", id.ToHexString());
    private string MoveFile(RefId id) => Path.Combine(ObjectRoot(id), "segments", "00000001.rbf");

    [Fact]
    public void HealthyCli_ReportsFixedSchemaAndCountsWithoutPayload() {
        var fixture = CreateFixture();
        var before = Snapshot();
        var (code, report, json) = Run("audit", Source);
        Assert.Equal(0, code);
        Assert.Equal(1, report.GetProperty("schemaVersion").GetInt32());
        Assert.True(report.GetProperty("completed").GetBoolean());
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.Equal("Consistent", Text(report, "indexesStatus"));
        Assert.Equal(3, report.GetProperty("counts").GetProperty("events").GetInt64());
        Assert.Equal(1, report.GetProperty("counts").GetProperty("tagBindings").GetInt64());
        foreach (string name in new[] { "eventSegments", "events", "refObjects", "refMoves", "refOpFrames", "tagBindings", "storedFrameBytesChecked" }) {
            Assert.True(report.GetProperty("counts").GetProperty(name).GetInt64() >= 0);
        }
        Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", json);
        Assert.False(report.TryGetProperty("exitCode", out _));
        AssertUnchanged(before);
        Assert.NotEqual(default, fixture.Head);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingIndexes_RebuildCandidateInstallsInCopyAndPreservesSource(bool missingSnapshot, bool missingLocators) {
        var fixture = CreateFixture();
        if (missingSnapshot) { File.Delete(Path.Combine(Source, "refs", "catalog.snapshot")); }
        if (missingLocators) {
            foreach (string locator in Directory.EnumerateFiles(Source, "active.segment", SearchOption.AllDirectories)) { File.Delete(locator); }
        }
        var before = Snapshot();
        var (auditCode, audit, _) = Run("audit", Source);
        Assert.Equal(2, auditCode);
        Assert.Equal("Healthy", Text(audit, "factsStatus"));
        Assert.Equal("Missing", Text(audit, "indexesStatus"));
        Assert.Equal(3, audit.GetProperty("counts").GetProperty("events").GetInt64());
        var (rebuildCode, _, _) = Run("rebuild-indexes", Source, "--output", Output);
        Assert.Equal(2, rebuildCode);
        AssertUnchanged(before);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "manifest.json")));
        var m = manifest.RootElement;
        Assert.True(m.GetProperty("completed").GetBoolean());
        Assert.Equal(1, m.GetProperty("schemaVersion").GetInt32());
        AssertManifestFiles(m.GetProperty("sourceFacts"), Source);
        AssertManifestFiles(m.GetProperty("outputs"), Path.Combine(Output, "candidate"));
        foreach (var index in m.GetProperty("sourceIndexes").EnumerateArray()) {
            if (!index.GetProperty("present").GetBoolean()) {
                Assert.Equal(JsonValueKind.Null, index.GetProperty("length").ValueKind);
                Assert.Equal(JsonValueKind.Null, index.GetProperty("sha256").ValueKind);
            }
        }
        string copy = Path.Combine(_root, "installed-copy");
        CopyTree(Source, copy);
        CopyTree(Path.Combine(Output, "candidate"), copy);
        using var reopened = Journal.OpenReadOnlyExisting(copy);
        Assert.Equal(fixture.Ref, reopened.OpenBranch("main").Unwrap());
        Assert.Equal(fixture.Head, reopened.GetHead(fixture.Ref));
        Assert.Equal(fixture.Root, reopened.ResolveTag("saved").Unwrap());
        Assert.Equal(new[] { fixture.Root, fixture.Middle, fixture.Head }, reopened.ReadChronologicalChain(fixture.Ref, checkedRead: true).Unwrap());
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidOldLocatorWithNext_RefusesCandidateEvenWhenNextIsEmpty(bool emptyNext) {
        CreateFixture();
        using (var next = RbfFile.CreateNew(EventFile(2))) {
            if (!emptyNext) {
                byte[] payload = "next"u8.ToArray();
                var header = new EventFrameHeader(EventPayloadCodecId.Identity, 4, 0, 0,
                    default, (uint)payload.Length, Parent: null);
                byte[] tailMeta = new byte[EventFrameHeaderCodec.FixedLength];
                EventFrameHeaderCodec.Encode(header, tailMeta);
                next.Append(Journal.EventFrameTag, payload, tailMeta).Unwrap();
            }
            next.DurableFlush();
        }
        var (_, report, _) = Run("audit", Source);
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.True(Text(report, "indexesStatus") == "Ambiguous"
            || report.GetProperty("findings").EnumerateArray().Any(f => Text(f, "code") == "NextSegmentPresent"));
        AssertRefusesRebuild();
    }

    [Fact]
    public void MissingLocatorWithHighestEmptyBeyondOne_RefusesCandidate() {
        CreateFixture();
        using (var next = RbfFile.CreateNew(EventFile(2))) { next.DurableFlush(); }
        File.Delete(Path.Combine(Source, "events", "active.segment"));
        AssertRefusesRebuild();
    }

    [Theory]
    [InlineData("wrong-bucket")]
    [InlineData("bad-name")]
    [InlineData("zero")]
    [InlineData("gap")]
    [InlineData("flat-nested")]
    public void InvalidInventory_ReportsErrorAndCannotRebuild(string kind) {
        var fixture = CreateFixture();
        switch (kind) {
            case "wrong-bucket":
                Directory.CreateDirectory(Path.Combine(Source, "events", "buckets", "000001"));
                File.Copy(EventFile(), Path.Combine(Source, "events", "buckets", "000001", "00000001.rbf"));
                break;
            case "bad-name": File.Copy(EventFile(), Path.Combine(Path.GetDirectoryName(EventFile())!, "not-a-segment.rbf")); break;
            case "zero": File.Copy(EventFile(), Path.Combine(Path.GetDirectoryName(EventFile())!, "00000000.rbf")); break;
            case "gap": File.Copy(EventFile(), EventFile(3)); break;
            case "flat-nested":
                string nested = Path.Combine(ObjectRoot(fixture.Ref), "segments", "nested");
                Directory.CreateDirectory(nested);
                File.Copy(MoveFile(fixture.Ref), Path.Combine(nested, "00000001.rbf"));
                break;
        }
        var (_, audit, _) = Run("audit", Source);
        Assert.Contains(audit.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "DirectoryInventoryInvalid");
        AssertRefusesRebuild();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedMiddleHistory_DailyEndpointsRemainReadableButAuditFails(bool refHistory) {
        var fixture = CreateFixture();
        string file = refHistory ? MoveFile(fixture.Ref) : EventFile();
        SizedPtr middle;
        if (refHistory) {
            using var reader = RbfFile.OpenReadOnlyExisting(file);
            middle = ReadTickets(reader)[1];
        }
        else { middle = fixture.Middle.Ticket; }
        Flip(file, middle.Offset + 4);
        using (var daily = Journal.OpenReadOnlyExisting(Source)) {
            Assert.Equal(fixture.Head, daily.GetHead(fixture.Ref));
            Assert.True(daily.ReadEventHeaderChecked(fixture.Head).IsSuccess);
        }
        var before = Snapshot();
        var (code, report, json) = Run("audit", Source);
        Assert.Equal(2, code);
        Assert.Equal("Invalid", Text(report, "factsStatus"));
        string relative = Path.GetRelativePath(Source, file).Replace('\\', '/');
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "relativePath") == relative && f.GetProperty("offset").GetInt64() == middle.Offset);
        Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", json);
        AssertUnchanged(before);
        AssertRefusesRebuild();
    }

    [Theory]
    [InlineData("duplicate-tag")]
    [InlineData("illegal-move")]
    [InlineData("close-without-archive")]
    public void ValidCrcButInvalidFacts_CannotBecomeRepairCandidate(string kind) {
        var fixture = CreateFixture();
        if (kind == "duplicate-tag") {
            using var log = RbfFile.OpenExisting(Log);
            byte[] payload = Array.Empty<byte>();
            foreach (var info in log.ScanForward()) {
                if (info.Tag != Journal.TagBindingFrameTag) { continue; }
                using var frame = log.ReadPooledFrame(info.Ticket).ToDisposable();
                payload = frame.Unwrap().PayloadAndMeta.ToArray();
            }
            Assert.NotEmpty(payload);
            log.Append(Journal.TagBindingFrameTag, payload).Unwrap();
            log.DurableFlush();
        }
        else {
            var move = new RefMoveFrame(fixture.Ref, kind == "illegal-move" ? 99ul : 4ul, 0,
                kind == "illegal-move" ? RefMoveOperation.Move : RefMoveOperation.Close,
                fixture.Head, fixture.Head, kind == "illegal-move" ? fixture.Head : null, 0);
            byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
            RefMoveFrameCodec.Encode(move, payload);
            using var file = RbfFile.OpenExisting(MoveFile(fixture.Ref));
            file.Append(Journal.RefMoveFrameTag, payload).Unwrap();
            file.DurableFlush();
        }
        var (code, report, _) = Run("audit", Source);
        Assert.Equal(2, code);
        Assert.Equal("Invalid", Text(report, "factsStatus"));
        if (kind == "close-without-archive") {
            Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "IncompleteArchive" && Text(f, "severity") == "Error");
        }
        AssertRefusesRebuild();
    }

    [Theory]
    [InlineData("no-object")]
    [InlineData("empty-with-locator")]
    [InlineData("empty-without-locator")]
    public void NeverBoundAllocation_IsWarningAndEmptyObjectMayReceiveLocatorCandidate(string kind) {
        using (var journal = Journal.CreateNew(Source)) { }
        RefId orphan;
        using (var log = RbfFile.OpenExisting(Log)) {
            var allocation = new RefOpFrame(RefOpOperation.Create, "unpublished", default, default, 0, null, null, 0, 0);
            SizedPtr ticket = log.Append(Journal.RefOpFrameTag, RefOpFrameCodec.Encode(allocation)).Unwrap();
            orphan = new RefId(ticket.Packed);
            log.DurableFlush();
        }
        if (kind != "no-object") {
            using (var segments = SegmentStore.CreateNew(ObjectRoot(orphan), new RbfSegmentStoreOptions { NewStoreLayout = RbfSegmentStoreLayout.Flat })) { }
            if (kind == "empty-without-locator") { File.Delete(Path.Combine(ObjectRoot(orphan), "active.segment")); }
        }
        var before = Snapshot();
        var (code, report, _) = Run("audit", Source);
        Assert.Equal(kind == "empty-without-locator" ? 2 : 0, code);
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "UnpublishedRef" && Text(f, "severity") == "Warning");
        var (rebuildCode, _, _) = Run("rebuild-indexes", Source, "--output", Output);
        Assert.Equal(kind == "empty-without-locator" ? 2 : 0, rebuildCode);
        Assert.True(File.Exists(Path.Combine(Output, "manifest.json")));
        AssertUnchanged(before);
        Assert.False(File.Exists(Path.Combine(Output, "candidate", "refs", "ref-op-log.rbf")));
        if (kind == "empty-without-locator") {
            Assert.True(File.Exists(Path.Combine(Output, "candidate", "refs", "objects", orphan.ToHexString(), "active.segment")));
        }
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("inside")]
    [InlineData("symlink")]
    public void UnsafeOutputPath_IsRejectedWithoutChangingSource(string kind) {
        CreateFixture();
        string output = Output;
        if (kind == "existing") { Directory.CreateDirectory(output); }
        if (kind == "inside") { output = Path.Combine(Source, "candidate"); }
        if (kind == "symlink") {
            string link = Path.Combine(_root, "source-link");
            Directory.CreateSymbolicLink(link, Source);
            output = Path.Combine(link, "candidate");
        }
        var before = Snapshot();
        using var writer = new StringWriter();
        int code = ToolkitCli.Run(new[] { "rebuild-indexes", Source, "--output", output }, writer);
        string json = writer.ToString();
        Assert.Equal(64, code);
        Assert.False(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", json);
        AssertUnchanged(before);
    }

    [Fact]
    public void CancelledAudit_IsIncompleteAndExitThree() {
        CreateFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var writer = new StringWriter();
        int code = ToolkitCli.Run(new[] { "audit", Source }, writer, cancelled.Token);
        Assert.Equal(3, code);
        using var report = JsonDocument.Parse(writer.ToString());
        Assert.False(report.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal("Incomplete", Text(report.RootElement, "factsStatus"));
        Assert.Contains(report.RootElement.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "OperationCancelled");
    }

    [Fact]
    public void AuditReport_IsCreateOnlyAndOutsideSource() {
        CreateFixture();
        string reportFile = Path.Combine(_root, "audit.json");
        var before = Snapshot();
        using var writer = new StringWriter();
        Assert.Equal(0, ToolkitCli.Run(new[] { "audit", Source, "--report", reportFile }, writer));
        using (var report = JsonDocument.Parse(File.ReadAllText(reportFile))) { Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32()); }
        byte[] bytes = File.ReadAllBytes(reportFile);
        Assert.NotEqual(0, ToolkitCli.Run(new[] { "audit", Source, "--report", reportFile }, new StringWriter()));
        Assert.Equal(bytes, File.ReadAllBytes(reportFile));
        Assert.NotEqual(0, ToolkitCli.Run(new[] { "audit", Source, "--report", Path.Combine(Source, "audit.json") }, new StringWriter()));
        AssertUnchanged(before);
    }

    [Fact]
    public void EmptySegmentOneWithMissingIndexes_HasUniqueRebuildCandidate() {
        using (var journal = Journal.CreateNew(Source)) { }
        File.Delete(Path.Combine(Source, "events", "active.segment"));
        File.Delete(Path.Combine(Source, "refs", "catalog.snapshot"));
        var before = Snapshot();
        var (code, report, _) = Run("rebuild-indexes", Source, "--output", Output);
        Assert.Equal(2, code);
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.True(File.Exists(Path.Combine(Output, "manifest.json")));
        string copy = Path.Combine(_root, "empty-installed-copy");
        CopyTree(Source, copy);
        CopyTree(Path.Combine(Output, "candidate"), copy);
        using var opened = Journal.OpenReadOnlyExisting(copy);
        Assert.Empty(opened.ListBranches());
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnsupportedRoot_IsDefiniteInvalidExitTwoWithoutCandidate(bool missing) {
        CreateFixture();
        string marker = Path.Combine(Source, "journal.format");
        if (missing) { File.Delete(marker); }
        else { File.WriteAllBytes(marker, "PRIVATE-PAYLOAD-SECRET"u8.ToArray()); }
        var (code, report, json) = Run("audit", Source);
        Assert.Equal(2, code);
        Assert.Equal("Invalid", Text(report, "factsStatus"));
        Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", json);
        Assert.DoesNotContain("Exception", json);
        AssertRefusesRebuild();
    }

    [Fact]
    public void CancelledRebuild_HasNoCompletedManifestAndPreservesSource() {
        CreateFixture();
        var before = Snapshot();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var writer = new StringWriter();
        Assert.Equal(3, ToolkitCli.Run(new[] { "rebuild-indexes", Source, "--output", Output }, writer, cancelled.Token));
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
        AssertUnchanged(before);
    }

    [Fact]
    public void CorruptLocatorWithHighestEmptyBeyondOne_CannotSelectAnOlderHead() {
        CreateFixture();
        using (var next = RbfFile.CreateNew(EventFile(2))) { next.DurableFlush(); }
        string locator = Path.Combine(Source, "events", "active.segment");
        Flip(locator, 19);
        var (_, report, _) = Run("audit", Source);
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.Equal("Ambiguous", Text(report, "indexesStatus"));
        AssertRefusesRebuild();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyHistoricalSegment_WithValidPublishedSegmentTwo_IsInvalidFact(bool refObject) {
        RefId id;
        using (var journal = Journal.CreateNew(Source)) {
            EventAddress root = journal.AppendEventFrame(null, "root"u8).Unwrap();
            id = journal.CreateBranch("main", root).Unwrap();
        }
        string first = refObject ? MoveFile(id) : EventFile();
        string second = refObject ? Path.Combine(ObjectRoot(id), "segments", "00000002.rbf") : EventFile(2);
        File.Move(first, second);
        using (var empty = RbfFile.CreateNew(first)) { empty.DurableFlush(); }
        SetLocatorActive(Path.Combine(refObject ? ObjectRoot(id) : Path.Combine(Source, "events"), "active.segment"), 2);
        var (_, report, _) = Run("audit", Source);
        Assert.Equal("Invalid", Text(report, "factsStatus"));
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "severity") == "Error");
        AssertRefusesRebuild();
    }

    [Fact]
    public void NeverBoundInitializedRef_CanBePublicForkSourceWithoutInventingItsBinding() {
        RefId source;
        EventAddress root;
        using (var journal = Journal.CreateNew(Source)) {
            root = journal.AppendEventFrame(null, "root"u8).Unwrap();
            source = journal.CreateBranch("unbound-source", root).Unwrap();
        }
        using (var log = RbfFile.OpenExisting(Log)) {
            List<SizedPtr> tickets = ReadTickets(log);
            Assert.Equal(2, tickets.Count);
            log.Truncate(tickets[1].Offset);
            log.DurableFlush();
        }
        RefId fork;
        using (var journal = Journal.OpenExisting(Source)) {
            Assert.Empty(journal.ListBranches());
            fork = journal.ForkBranch("fork", source, root).Unwrap();
            Assert.Equal(root, journal.GetHead(fork));
            Assert.Equal(new[] { "fork" }, journal.ListBranches());
        }
        var before = Snapshot();
        var (code, report, _) = Run("audit", Source);
        Assert.Equal(0, code);
        Assert.Equal("Healthy", Text(report, "factsStatus"));
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "UnpublishedRef" && Text(f, "refId") == source.ToHexString());
        Assert.Equal(0, Run("rebuild-indexes", Source, "--output", Output).Code);
        AssertUnchanged(before);
        string installed = Path.Combine(_root, "fork-installed-copy");
        CopyTree(Source, installed);
        CopyTree(Path.Combine(Output, "candidate"), installed);
        using var reopened = Journal.OpenReadOnlyExisting(installed);
        Assert.Equal(new[] { "fork" }, reopened.ListBranches());
        Assert.Equal(fork, reopened.OpenBranch("fork").Unwrap());
        Assert.Equal(root, reopened.GetHead(source));
        Assert.Equal(root, reopened.GetHead(fork));
    }

    [Fact]
    public void SourceInventoryReparse_StopsWithInvalidFactsAndDefiniteExitTwo() {
        CreateFixture();
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "PRIVATE-PAYLOAD-SECRET");
        string link = Path.Combine(Source, "escaped-inventory");
        Directory.CreateSymbolicLink(link, outside);
        try {
            var (code, report, json) = Run("audit", Source);
            Assert.Equal(2, code);
            Assert.False(report.GetProperty("completed").GetBoolean());
            Assert.Equal("Invalid", Text(report, "factsStatus"));
            Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", json);
            Assert.NotEmpty(report.GetProperty("findings").EnumerateArray());
            AssertRefusesRebuildWithoutSnapshot();
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocatorLikeFile_IsInventoryErrorInBucketButExcludedWhenInCache(bool strayBucketLocator) {
        CreateFixture();
        string directory = strayBucketLocator ? Path.GetDirectoryName(EventFile())! : Path.Combine(Source, "cache");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(Source, "events", "active.segment"), Path.Combine(directory, "active.segment"));
        var (code, report, _) = Run("audit", Source);
        if (strayBucketLocator) {
            Assert.Equal(2, code);
            Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "DirectoryInventoryInvalid");
            AssertRefusesRebuild();
        }
        else {
            Assert.Equal(0, code);
            Assert.Equal(0, Run("rebuild-indexes", Source, "--output", Output).Code);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "manifest.json")));
            foreach (string section in new[] { "sourceFacts", "sourceIndexes", "outputs" }) {
                Assert.DoesNotContain(manifest.RootElement.GetProperty(section).EnumerateArray(), f => Text(f, "relativePath").StartsWith("cache/", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void AllocationReusingCurrentlyBoundName_IsInvalidFact() {
        CreateFixture();
        using (var log = RbfFile.OpenExisting(Log)) {
            var allocation = new RefOpFrame(RefOpOperation.Create, "main", default, default, 0, null, null, 0, 0);
            log.Append(Journal.RefOpFrameTag, RefOpFrameCodec.Encode(allocation)).Unwrap();
            log.DurableFlush();
        }
        var (code, report, _) = Run("audit", Source);
        Assert.Equal(2, code);
        Assert.Equal("Invalid", Text(report, "factsStatus"));
        Assert.Contains(report.GetProperty("findings").EnumerateArray(), f => Text(f, "code") == "RefAllocationInvalid");
        AssertRefusesRebuild();
    }

    private void AssertRefusesRebuildWithoutSnapshot() {
        var (code, _, _) = Run("rebuild-indexes", Source, "--output", Output);
        Assert.Equal(2, code);
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }

    private static void SetLocatorActive(string path, uint active) {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(20, bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), active);
        RollingCrc.SealCodewordForward(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private (RefId Ref, EventAddress Root, EventAddress Middle, EventAddress Head) CreateFixture() {
        using var journal = Journal.CreateNew(Source);
        EventAddress root = journal.AppendEventFrame(null, "PRIVATE-PAYLOAD-SECRET"u8).Unwrap();
        RefId id = journal.CreateBranch("main", root).Unwrap();
        EventAddress middle = journal.CommitToRef(id, root, "PRIVATE-PAYLOAD-SECRET"u8).Unwrap().EventAddress;
        EventAddress head = journal.CommitToRef(id, middle, "PRIVATE-PAYLOAD-SECRET"u8).Unwrap().EventAddress;
        journal.CreateTag("saved", root).Unwrap();
        return (id, root, middle, head);
    }

    private void AssertRefusesRebuild() {
        var before = Snapshot();
        var (code, _, _) = Run("rebuild-indexes", Source, "--output", Output);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
        AssertUnchanged(before);
    }

    private static (int Code, JsonElement Report, string Json) Run(params string[] args) {
        using var writer = new StringWriter();
        int code = ToolkitCli.Run(args, writer);
        string json = writer.ToString();
        using var parsed = JsonDocument.Parse(json);
        return (code, parsed.RootElement.Clone(), json);
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(Source, p), File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before) {
        var after = Snapshot();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var entry in before) { Assert.Equal(entry.Value, after[entry.Key]); }
    }
    private static void AssertManifestFiles(JsonElement entries, string root) {
        var paths = entries.EnumerateArray().Select(e => Text(e, "relativePath")).ToArray();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in entries.EnumerateArray()) {
            string relative = Text(entry, "relativePath");
            Assert.False(Path.IsPathRooted(relative));
            Assert.DoesNotContain("..", relative.Split('/'));
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, relative));
            Assert.Equal(bytes.LongLength, entry.GetProperty("length").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), Text(entry, "sha256"));
        }
    }
    private static void CopyTree(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
    private static List<SizedPtr> ReadTickets(IRbfFile reader) {
        var tickets = new List<SizedPtr>();
        foreach (var info in reader.ScanForward()) { tickets.Add(info.Ticket); }
        return tickets;
    }
    private static void Flip(string path, long offset) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = offset;
        int value = file.ReadByte();
        Assert.NotEqual(-1, value);
        file.Position--;
        file.WriteByte((byte)(value ^ 1));
    }
    public void Dispose() {
        string link = Path.Combine(_root, "source-link");
        if (Directory.Exists(link)) { Directory.Delete(link); }
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
}
