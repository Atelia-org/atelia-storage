using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Rbf;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;
namespace Atelia.EventJournal.Toolkit.Tests;

public sealed class LegacyUpgradeTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-upgrade-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output");
    public LegacyUpgradeTests() { Directory.CreateDirectory(_root); }
    private void Fixture(string name = "complex") {
        string from = Path.Combine(AppContext.BaseDirectory, "LegacyFixtures", name, "journal");
        Directory.CreateDirectory(Source);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LegacyFixtures", "provenance.json")));
        foreach (var dir in provenance.RootElement.GetProperty("fixtureDirectories").GetProperty(name).EnumerateArray()) {
            string relative = dir.GetString()!;
            if (relative != "journal") { Directory.CreateDirectory(Path.Combine(Source, relative["journal/".Length..])); }
        }
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(Path.Combine(Source, Path.GetRelativePath(from, dir))); }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) { File.Copy(file, Path.Combine(Source, Path.GetRelativePath(from, file))); }
    }
    private Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private void Unchanged(Dictionary<string, string> before) => Assert.Equal(before.OrderBy(p => p.Key), Snapshot(Source).OrderBy(p => p.Key));
    [Theory]
    [InlineData("empty")]
    [InlineData("events-only")]
    [InlineData("complex")]
    [InlineData("empty-active")]
    public void FixedOldBinaryFixturesCheckAndCopySuccessfully(string name) {
        Fixture(name); var before = Snapshot(Source);
        var check = LegacyUpgrade.Check(Source, LegacyUpgrade.Profile);
        Assert.Equal("Eligible", check.Status); Assert.True(check.SourceScanCompleted); Assert.Equal(0, check.ExitCode);
        Assert.False(Directory.Exists(Output)); Unchanged(before);
        var created = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal("Created", created.Status); Assert.Equal(0, created.ExitCode); Unchanged(before);
        string target = Path.Combine(Output, "journal");
        var audit = JournalToolkit.Audit(target);
        Assert.True(audit.Completed); Assert.Equal("Healthy", audit.FactsStatus); Assert.Equal("Consistent", audit.IndexesStatus);
        Assert.False(Directory.Exists(Path.Combine(target, "cache")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "manifest.json")));
        var m = manifest.RootElement;
        Assert.True(m.GetProperty("completed").GetBoolean()); Assert.True(m.GetProperty("factsCopiedByteExactly").GetBoolean());
        Assert.Equal("EventJournalLegacyUpgrade", m.GetProperty("kind").GetString());
        Assert.Equal(LegacyUpgrade.ProfileBaselineRevision, m.GetProperty("profileBaselineRevision").GetString());
        foreach (var file in m.GetProperty("sourceFiles").EnumerateArray()) {
            if (file.GetProperty("kind").GetString() == "ExcludedDerived") { continue; }
            string relative = file.GetProperty("relativePath").GetString()!;
            Assert.Equal(File.ReadAllBytes(Path.Combine(Source, relative)), File.ReadAllBytes(Path.Combine(target, relative)));
        }
        foreach (var file in m.GetProperty("outputs").EnumerateArray()) {
            string path = Path.Combine(target, file.GetProperty("relativePath").GetString()!);
            Assert.Equal(new FileInfo(path).Length, file.GetProperty("length").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), file.GetProperty("sha256").GetString());
        }
    }
    [Theory]
    [InlineData("BeforeFactCopy")]
    [InlineData("CopyChunkWritten")]
    [InlineData("FactCopied")]
    [InlineData("LocatorWritten")]
    [InlineData("CatalogWritten")]
    [InlineData("FormatWritten")]
    [InlineData("TargetAudited")]
    [InlineData("BeforeManifestWrite")]
    [InlineData("BeforeManifestPublish")]
    public void FailureBeforeReleaseNeverPublishesManifest(string point) {
        Fixture(); var before = Snapshot(Source);
        LegacyUpgrade.Probe = p => { if (p == point) { throw new IOException("PRIVATE-EXCEPTION-SECRET"); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal("Incomplete", report.Status); Assert.Equal(3, report.ExitCode);
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json"))); Unchanged(before);
        Assert.DoesNotContain("PRIVATE-EXCEPTION-SECRET", ToolkitJson.Serialize(report));
    }
    [Theory]
    [InlineData("SourceScanned")]
    [InlineData("CopyChunkWritten")]
    [InlineData("CatalogWritten")]
    [InlineData("FormatWritten")]
    [InlineData("BeforeManifestPublish")]
    public void CancellationBeforeReleaseNeverPublishesManifest(string point) {
        Fixture(); using var cancel = new CancellationTokenSource();
        LegacyUpgrade.Probe = p => { if (p == point) { cancel.Cancel(); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output, cancel.Token);
        Assert.Equal("Incomplete", report.Status); Assert.Contains(report.Findings, f => f.Code == "OperationCancelled");
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseCannotBeDowngradedByLateIoOrCancellation(bool cancel) {
        Fixture(); using var cancellation = new CancellationTokenSource();
        LegacyUpgrade.Probe = point => { if (point == "ManifestPublished") { if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(); } throw new IOException("late"); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output, cancellation.Token);
        Assert.Equal("Created", report.Status); Assert.Equal(0, report.ExitCode);
        Assert.DoesNotContain(report.Findings, f => f.Code is "IoFailure" or "OperationCancelled");
        Assert.True(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Fact]
    public void ManifestTemporaryTamperingPreventsRelease() {
        Fixture();
        LegacyUpgrade.Probe = p => { if (p == "BeforeManifestPublish") { File.WriteAllText(Path.Combine(Output, ".manifest.tmp"), "{\"completed\":false}"); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal("Incomplete", report.Status); Assert.Contains(report.Findings, f => f.Code == "OutputChanged");
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData("new-file")]
    [InlineData("deleted-file")]
    [InlineData("changed-file")]
    [InlineData("new-directory")]
    [InlineData("deleted-directory")]
    [InlineData("derived-change")]
    public void CompleteSourceInventoryDriftIsIncomplete(string change) {
        Fixture(); Directory.CreateDirectory(Path.Combine(Source, "cache", "forward-plans", "v1", "empty"));
        LegacyUpgrade.Probe = p => {
            if (p != "SourceScanned") { return; }
            string log = Path.Combine(Source, "refs", "ref-op-log.rbf");
            switch (change) {
                case "new-file": File.WriteAllText(Path.Combine(Source, "new-entry"), "x"); break;
                case "deleted-file": File.Delete(log); break;
                case "changed-file": File.AppendAllText(log, "x"); break;
                case "new-directory": Directory.CreateDirectory(Path.Combine(Source, "cache", "forward-plans", "v1", "new")); break;
                case "deleted-directory": Directory.Delete(Path.Combine(Source, "cache", "forward-plans", "v1", "empty")); break;
                case "derived-change": File.WriteAllText(Path.Combine(Source, "cache", "forward-plans", "v1", "new.efplan"), "x"); break;
            }
        };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "SourceChanged"); Assert.False(Directory.Exists(Output));
    }
    [Theory]
    [InlineData("new-file")]
    [InlineData("deleted-file")]
    [InlineData("changed-file")]
    [InlineData("new-directory")]
    [InlineData("bundle-entry")]
    public void OutputInventoryDriftIsIncomplete(string change) {
        Fixture();
        LegacyUpgrade.Probe = p => {
            if (p != "TargetAudited") { return; }
            string target = Path.Combine(Output, "journal"); string log = Path.Combine(target, "refs", "ref-op-log.rbf");
            switch (change) {
                case "new-file": File.WriteAllText(Path.Combine(target, "new-entry"), "x"); break;
                case "deleted-file": File.Delete(log); break;
                case "changed-file": File.AppendAllText(log, "x"); break;
                case "new-directory": Directory.CreateDirectory(Path.Combine(target, "extra")); break;
                case "bundle-entry": File.WriteAllText(Path.Combine(Output, "extra"), "x"); break;
            }
        };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "OutputChanged"); Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData("journal.format")]
    [InlineData("events/active.segment")]
    [InlineData("refs/catalog.snapshot")]
    [InlineData("events/.active.segment.any.tmp")]
    [InlineData("journal.format.any.tmp")]
    public void MixedV2MetadataCannotFallBackToLegacy(string relative) {
        Fixture(); File.WriteAllBytes(Path.Combine(Source, relative), [0]);
        var report = LegacyUpgrade.Check(Source, LegacyUpgrade.Profile);
        Assert.Equal(2, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "MixedLayout"); Assert.False(report.SourceScanCompleted);
    }
    [Theory]
    [InlineData("unrelated.tmp")]
    [InlineData("cache/other/file")]
    [InlineData("events/buckets/000001/00000001.rbf")]
    public void UnknownInventoryCannotBeIgnored(string relative) {
        Fixture(); string path = Path.Combine(Source, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "PRIVATE-PAYLOAD-SECRET");
        var report = LegacyUpgrade.Check(Source, LegacyUpgrade.Profile);
        Assert.Equal(2, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "UnknownInventoryEntry"); Assert.DoesNotContain("PRIVATE-PAYLOAD-SECRET", ToolkitJson.Serialize(report));
    }
    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void BadTailHasNoOutputAndPreservesSource(int length) {
        Fixture(); using (var stream = new FileStream(Path.Combine(Source, "refs", "ref-op-log.rbf"), FileMode.Append)) { stream.Write(new byte[length]); }
        var before = Snapshot(Source); var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal(2, report.ExitCode); Assert.False(report.SourceScanCompleted); Assert.False(Directory.Exists(Output)); Unchanged(before);
    }
    [Fact]
    public void LinuxFifoIsRejectedBeforeAnyReadCanBlock() {
        if (!OperatingSystem.IsLinux()) { return; }
        Fixture(); string path = Path.Combine(Source, "refs", "fifo.rbf");
        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false }; start.ArgumentList.Add(path);
        using (var process = Process.Start(start)!) { process.WaitForExit(); Assert.Equal(0, process.ExitCode); }
        try { var report = LegacyUpgrade.Check(Source, LegacyUpgrade.Profile); Assert.Equal(2, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "UnknownInventoryEntry" && f.RelativePath == "refs/fifo.rbf"); }
        finally { File.Delete(path); }
        Assert.Throws<ArgumentException>(() => ToolkitPaths.RequireOrdinaryEntry("/dev/null"));
    }
    [Fact]
    public void SourceReparseIsRejectedWithoutFollowingIt() {
        Fixture(); string link = Path.Combine(Source, "cache", "forward-plans", "v1", "linked");
        Directory.CreateSymbolicLink(link, _root);
        try { Assert.Equal(2, LegacyUpgrade.Check(Source, LegacyUpgrade.Profile).ExitCode); }
        finally { Directory.Delete(link); }
    }
    [Theory]
    [InlineData("missing-profile")]
    [InlineData("both")]
    [InlineData("existing")]
    [InlineData("inside")]
    public void CliErrorsDoNotCreateOrOverwrite(string error) {
        Fixture(); string[] args = error switch {
            "missing-profile" => ["upgrade-v1tov2", Source, "--check-only"],
            "both" => ["upgrade-v1tov2", Source, "--profile", LegacyUpgrade.Profile, "--check-only", "--output", Output],
            "inside" => ["upgrade-v1tov2", Source, "--profile", LegacyUpgrade.Profile, "--output", Path.Combine(Source, "out")],
            _ => ["upgrade-v1tov2", Source, "--profile", LegacyUpgrade.Profile, "--output", Output]
        };
        if (error == "existing") { Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "sentinel"), "unchanged"); }
        using var stdout = new StringWriter(); Assert.Equal(64, ToolkitCli.Run(args, stdout)); Assert.Equal("", stdout.ToString());
        if (error == "existing") { Assert.Equal("unchanged", File.ReadAllText(Path.Combine(Output, "sentinel"))); }
    }
    [Fact]
    public void CliUnknownProfileIsExplicitRejectedAndCheckOnlyHasNoOutput() {
        Fixture(); using var output = new StringWriter();
        Assert.Equal(2, ToolkitCli.Run(["upgrade-v1tov2", Source, "--profile", "unknown", "--check-only"], output));
        using var report = JsonDocument.Parse(output.ToString()); Assert.Equal("Rejected", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("outputRoot").ValueKind); Assert.False(Directory.Exists(Output));
    }
    [Fact]
    public void PreExecutionIoGetsMetadataOnlyIncompleteReport() {
        Fixture(); LegacyUpgrade.Probe = p => { if (p == "BeforeOutputPathCheck") { throw new IOException("PRIVATE-IO-SECRET"); } };
        using var stdout = new StringWriter(); Assert.Equal(3, ToolkitCli.Run(["upgrade-v1tov2", Source, "--profile", LegacyUpgrade.Profile, "--output", Output], stdout));
        Assert.Contains("Incomplete", stdout.ToString()); Assert.DoesNotContain("PRIVATE-IO-SECRET", stdout.ToString()); Assert.False(Directory.Exists(Output));
    }
    [Fact]
    public void StdoutFailureAfterReleaseReturnsTransportThreeAndKeepsCreatedManifest() {
        Fixture(); using var stdout = new FailingWriter();
        Assert.Equal(3, ToolkitCli.Run(["upgrade-v1tov2", Source, "--profile", LegacyUpgrade.Profile, "--output", Output], stdout));
        using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "manifest.json"))); Assert.True(m.RootElement.GetProperty("completed").GetBoolean());
    }
    [Fact]
    public void RootReparseAppearingAfterSourceScanIsSourceChanged() {
        Fixture(); var before = Snapshot(Source); string retained = Path.Combine(_root, "retained");
        LegacyUpgrade.Probe = p => { if (p == "SourceScanned") { Directory.Move(Source, retained); Directory.CreateSymbolicLink(Source, retained); } };
        try {
            var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
            Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "SourceChanged"); Assert.False(Directory.Exists(Output));
            Assert.Equal(before.OrderBy(p => p.Key), Snapshot(retained).OrderBy(p => p.Key));
        }
        finally { Directory.Delete(Source); }
    }
    [Fact]
    public void TargetAuditCancellationRemainsCancellationNotOutputChanged() {
        Fixture(); using var cancellation = new CancellationTokenSource();
        LegacyUpgrade.Probe = p => { if (p == "BeforeTargetAudit") { JournalToolkit.Probe = point => { if (point == "AuditBeforeSourceRecheck") { cancellation.Cancel(); } }; } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output, cancellation.Token);
        Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "OperationCancelled");
        Assert.DoesNotContain(report.Findings, f => f.Code == "OutputChanged"); Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData("forward")]
    [InlineData("rollback")]
    [InlineData("sequence")]
    [InlineData("reason")]
    [InlineData("ownership")]
    public void SharedV2AuditTreatsArchiveTimeAsObservationButKeepsIdentityChecks(string change) {
        using (var journal = Journal.CreateNew(Source)) {
            var head = journal.AppendEventFrame(null, "payload"u8).Unwrap();
            var id = journal.CreateBranch("main", head).Unwrap(); journal.ArchiveRef(id, head, reasonKind: 17).Unwrap();
        }
        string logPath = Path.Combine(Source, "refs", "ref-op-log.rbf");
        var records = new List<(uint Tag, byte[] Payload)>();
        using (var log = RbfFile.OpenReadOnlyExisting(logPath)) {
            var scan = log.ScanForward(showTombstone: true).GetEnumerator();
            while (scan.MoveNext()) { using var frame = scan.Current.ReadPooledFrame().ToDisposable(); records.Add((scan.Current.Tag, frame.Unwrap().PayloadAndMeta.ToArray())); }
        }
        var archive = RefOpFrameCodec.Decode(records[^1].Payload).Unwrap();
        archive = change switch {
            "forward" => archive with { UtcUnixTimeMilliseconds = archive.UtcUnixTimeMilliseconds + 10000 },
            "rollback" => archive with { UtcUnixTimeMilliseconds = archive.UtcUnixTimeMilliseconds - 10000 },
            "sequence" => archive with { SourceMoveSequenceNumber = archive.SourceMoveSequenceNumber + 1 },
            "reason" => archive with { ReasonKind = archive.ReasonKind + 1 },
            _ => archive with { RefId = new RefId(archive.RefId.Packed + 4) }
        };
        records[^1] = (records[^1].Tag, RefOpFrameCodec.Encode(archive));
        File.Delete(logPath); using (var log = RbfFile.CreateNew(logPath)) { foreach (var frame in records) { log.Append(frame.Tag, frame.Payload).Unwrap(); } log.DurableFlush(); }
        var report = JournalToolkit.Audit(Source);
        Assert.Equal(change is "forward" or "rollback" ? "Healthy" : "Invalid", report.FactsStatus);
        Assert.Equal(change is "forward" or "rollback" ? 0 : 2, report.ExitCode);
    }
    [Fact]
    public void SourceChangingDuringCopyDoesNotPublishManifest() {
        Fixture(); bool changed = false;
        LegacyUpgrade.Probe = p => { if (p == "BeforeFactCopy" && !changed) { changed = true; File.AppendAllText(Path.Combine(Source, "refs", "ref-op-log.rbf"), "x"); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "SourceChanged"); Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Fact]
    public void OutputCreatedAfterPreflightIsNeverReused() {
        Fixture(); LegacyUpgrade.Probe = p => { if (p == "BeforeBundleCreate") { Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "sentinel"), "owned"); } };
        var report = LegacyUpgrade.Upgrade(Source, LegacyUpgrade.Profile, Output);
        Assert.Equal(3, report.ExitCode); Assert.Equal("owned", File.ReadAllText(Path.Combine(Output, "sentinel"))); Assert.False(Directory.Exists(Path.Combine(Output, "journal")));
    }
    private sealed class FailingWriter : StringWriter { public override void WriteLine(string? value) => throw new IOException("stdout failed"); }
    public void Dispose() { LegacyUpgrade.Probe = null; JournalToolkit.Probe = null; if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
