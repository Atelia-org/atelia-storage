using System.Security.Cryptography;
using System.Text.Json;
using Atelia.Rbf;
using Xunit;
using Journal = Atelia.EventJournal.EventJournal;
namespace Atelia.EventJournal.Toolkit.Tests;
public sealed class CandidateConsistencyTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolkit-consistency-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output");
    public CandidateConsistencyTests() { Directory.CreateDirectory(_root); using var journal = Journal.CreateNew(Source); journal.CreateBranch("main", null).Unwrap(); }
    [Theory]
    [InlineData("add-fact")]
    [InlineData("delete-fact")]
    [InlineData("change-fact")]
    [InlineData("add-index")]
    [InlineData("delete-index")]
    [InlineData("change-index")]
    public void SourceInventoryDriftNeverPublishesManifest(string change) {
        string index = Path.Combine(Source, "refs", "catalog.snapshot");
        if (change == "add-index") { File.Delete(index); }
        JournalToolkit.Probe = point => {
            if (point != "BeforeSourceRecheck") { return; }
            switch (change) {
                case "add-fact": using (var rbf = RbfFile.CreateNew(Path.Combine(Source, "events", "buckets", "000000", "00000002.rbf"))) { rbf.DurableFlush(); } break;
                case "delete-fact": File.Delete(Path.Combine(Source, "refs", "ref-op-log.rbf")); break;
                case "change-fact": File.AppendAllText(Path.Combine(Source, "refs", "ref-op-log.rbf"), "x"); break;
                case "add-index": File.WriteAllBytes(index, [0]); break;
                case "delete-index": File.Delete(index); break;
                case "change-index": File.AppendAllText(index, "x"); break;
            }
        };
        var report = JournalToolkit.RebuildIndexes(Source, Output);
        Assert.Equal(3, report.ExitCode); Assert.False(report.Completed); Assert.Equal("Incomplete", report.FactsStatus);
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData("CandidateWritten")]
    [InlineData("BeforeManifestPublish")]
    public void CandidateIoFailureRetainsIncompleteOutputWithoutManifest(string failure) {
        JournalToolkit.Probe = point => { if (point == failure) { throw new IOException("PRIVATE-EXCEPTION-SECRET"); } };
        var report = JournalToolkit.RebuildIndexes(Source, Output);
        Assert.Equal(3, report.ExitCode); Assert.False(report.Completed);
        Assert.Contains(report.Findings, f => f.Code == "IoFailure");
        Assert.DoesNotContain("PRIVATE-EXCEPTION-SECRET", ToolkitJson.Serialize(report));
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Fact]
    public void CancellationAfterCandidateWritesDoesNotPublishManifest() {
        using var cancel = new CancellationTokenSource();
        JournalToolkit.Probe = point => { if (point == "CandidateWritten") { cancel.Cancel(); } };
        var report = JournalToolkit.RebuildIndexes(Source, Output, cancel.Token);
        Assert.Equal(3, report.ExitCode); Assert.Contains(report.Findings, f => f.Code == "OperationCancelled");
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Fact]
    public void AuditDetectsAddedFactsAndIndexes() {
        JournalToolkit.Probe = point => { if (point == "AuditBeforeSourceRecheck") { File.Delete(Path.Combine(Source, "events", "active.segment")); } };
        var report = JournalToolkit.Audit(Source);
        Assert.Equal(3, report.ExitCode); Assert.Equal("Incomplete", report.FactsStatus); Assert.False(report.Completed);
    }
    [Fact]
    public void AuditAndRebuildReadOnlyFilesWithoutChangingBytes() {
        var files = Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories).ToArray();
        var hashes = files.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        foreach (var file in files) { File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly); }
        Assert.Equal(0, JournalToolkit.Audit(Source).ExitCode);
        Assert.Equal(0, JournalToolkit.RebuildIndexes(Source, Output).ExitCode);
        foreach (var file in files) { Assert.Equal(hashes[file], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))); }
    }
    [Fact]
    public void UnknownFileInsideEventStoreIsInventoryError() {
        File.WriteAllText(Path.Combine(Source, "events", "unknown.rbf"), "private");
        Assert.Contains(JournalToolkit.Audit(Source).Findings, f => f.Code == "DirectoryInventoryInvalid" && f.RelativePath == "events/unknown.rbf");
        Assert.Equal(2, JournalToolkit.RebuildIndexes(Source, Output).ExitCode);
        Assert.False(Directory.Exists(Output));
    }
    [Fact]
    public void OutputCreatedAfterPrecheckIsNeverReused() {
        JournalToolkit.Probe = point => { if (point == "BeforeCandidateCreate") { Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "sentinel"), "owned"); } };
        var report = JournalToolkit.RebuildIndexes(Source, Output);
        Assert.Equal(3, report.ExitCode); Assert.Equal("owned", File.ReadAllText(Path.Combine(Output, "sentinel")));
        Assert.False(Directory.Exists(Path.Combine(Output, "candidate")));
    }
    [Theory]
    [InlineData("tamper")]
    [InlineData("delete")]
    [InlineData("add")]
    public void CandidateInventoryDriftNeverPublishesManifest(string change) {
        JournalToolkit.Probe = point => {
            if (point != "BeforeManifestPublish") { return; }
            string file = Path.Combine(Output, "candidate", "events", "active.segment");
            if (change == "tamper") { File.AppendAllText(file, "x"); }
            else if (change == "delete") { File.Delete(file); }
            else { File.WriteAllText(Path.Combine(Output, "candidate", "unexpected"), "x"); }
        };
        Assert.Equal(3, JournalToolkit.RebuildIndexes(Source, Output).ExitCode);
        Assert.False(File.Exists(Path.Combine(Output, "manifest.json")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FramingStopsCoverageAndReportsExplicitInvalid(bool shortTail) {
        string path = Path.Combine(Source, "refs", "ref-op-log.rbf");
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write)) { file.Write(shortTail ? new byte[1] : new byte[4]); }
        var report = JournalToolkit.Audit(Source);
        Assert.Equal(2, report.ExitCode); Assert.Equal("Invalid", report.FactsStatus); Assert.False(report.Completed);
        Assert.Contains(report.Findings, f => f.RelativePath == "refs/ref-op-log.rbf" && f.Offset.HasValue);
    }
    [Fact]
    public void FileSystemRootCannotTreatDescendantReportAsOutside() {
        string root = Path.GetPathRoot(Path.GetFullPath(_root))!;
        string target = Path.Combine(_root, "new-report.json");
        using var stdout = new StringWriter();
        Assert.Equal(64, ToolkitCli.Run(["audit", root, "--report", target], stdout));
        Assert.Equal("", stdout.ToString()); Assert.False(File.Exists(target));
    }
    public void Dispose() {
        JournalToolkit.Probe = null;
        if (Directory.Exists(_root)) {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(_root, true);
        }
    }
}
