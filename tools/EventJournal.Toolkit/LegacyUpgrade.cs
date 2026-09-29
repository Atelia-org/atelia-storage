using System.Security.Cryptography;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
namespace Atelia.EventJournal.Toolkit;

public static class LegacyUpgrade {
    public const string Profile = "legacy-bb7c4fb";
    public const string ProfileBaselineRevision = "bb7c4fb3eb6477783c70ee61bc62b832be195d07";
    private static readonly AsyncLocal<Action<string>?> ProbeSlot = new();
    internal static Action<string>? Probe { get => ProbeSlot.Value; set => ProbeSlot.Value = value; }
    public static UpgradeReport Check(string source, string profile, CancellationToken cancellationToken = default) => Execute(source, profile, null, cancellationToken);
    public static UpgradeReport Upgrade(string source, string profile, string output, CancellationToken cancellationToken = default) {
        Probe?.Invoke("BeforeOutputPathCheck");
        ToolkitPaths.RequireOutsideNew(source, output);
        return Execute(source, profile, output, cancellationToken);
    }
    private static UpgradeReport Execute(string source, string profile, string? output, CancellationToken token) {
        var report = new UpgradeReport { Operation = output is null ? "CheckOnly" : "Upgrade", SourceProfile = profile,
            OutputRoot = output is null ? null : Path.GetFullPath(output) };
        if (profile != Profile) { Add("LegacyProfileMismatch", "."); return report; }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) { Add("UnsupportedPlatform", "."); return report; }
        bool published = false;
        bool outputCreated = false;
        try {
            string root = Path.GetFullPath(source);
            token.ThrowIfCancellationRequested();
            LegacyInventory before = LegacyInventory.Capture(root, token, legacy: true);
            Probe?.Invoke("SourceInventoried");
            var engine = new AuditEngine(root, token, legacy: true).Run();
            report.Counts = engine.Report.Counts; report.Findings.AddRange(engine.Report.Findings);
            report.SourceScanCompleted = engine.Report.Completed;
            if (engine.Report.FactsStatus == "Incomplete") { report.Status = "Incomplete"; return report; }
            Probe?.Invoke("SourceScanned");
            RequireSourceUnchanged();
            if (!engine.Report.Completed || engine.Report.FactsStatus != "Healthy" || !engine.UniqueBoundaries) { return report; }
            if (output is null) { report.Status = "Eligible"; return report; }
            token.ThrowIfCancellationRequested();
            Probe?.Invoke("BeforeBundleCreate"); RequireSourceUnchanged();
            try { ToolkitPaths.RequireOutsideNew(root, output); }
            catch (ArgumentException) { throw new UpgradeDriftException("OutputChanged", "."); }
            ToolkitPaths.CreateNewDirectory(output); outputCreated = true;
            string journalRoot = Path.Combine(output, "journal");
            ToolkitPaths.CreateNewDirectory(journalRoot);
            string[] targetDirectories = before.Directories.Where(d => d != "cache" && !d.StartsWith("cache/", StringComparison.Ordinal)).ToArray();
            foreach (string relative in targetDirectories.Where(d => d != ".")) {
                token.ThrowIfCancellationRequested(); string directory = Target(relative);
                ToolkitPaths.RequireNoReparse(directory); Directory.CreateDirectory(directory);
            }
            var outputs = new List<SourceFact>();
            foreach (var fact in before.Files.Where(f => f.Kind != "ExcludedDerived")) {
                token.ThrowIfCancellationRequested(); Probe?.Invoke("BeforeFactCopy");
                string from = Path.Combine(root, fact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                try { if (ToolkitPaths.RequireOrdinaryEntry(from)) { throw new UpgradeDriftException("SourceChanged", fact.RelativePath); } }
                catch (ArgumentException) { throw new UpgradeDriftException("SourceChanged", fact.RelativePath); }
                string to = Target(fact.RelativePath); ToolkitPaths.RequireNoReparse(to);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long copied = 0; byte[] buffer = new byte[128 * 1024];
                using (var sourceStream = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var targetStream = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    int read;
                    while ((read = sourceStream.Read(buffer)) != 0) {
                        token.ThrowIfCancellationRequested(); targetStream.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); copied = checked(copied + read);
                        Probe?.Invoke("CopyChunkWritten");
                    }
                    targetStream.Flush(true);
                }
                string copiedHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (copied != fact.Length || copiedHash != fact.Sha256) { throw new UpgradeDriftException("SourceChanged", fact.RelativePath); }
                outputs.Add(fact); Probe?.Invoke("FactCopied");
            }
            foreach (var store in engine.Stores.OrderBy(s => s.Root, StringComparer.Ordinal)) {
                WriteMetadata(store.Root + "/active.segment", "Locator", stream => stream.Write(SegmentLocator.Encode(store.Layout, store.Segments.Keys.Max())));
                Probe?.Invoke("LocatorWritten");
            }
            WriteMetadata("refs/catalog.snapshot", "CatalogSnapshot", stream => CatalogSnapshotCodec.Write(stream,
                new CatalogSnapshot(engine.LogBoundary, engine.Branches, engine.Tags)));
            Probe?.Invoke("CatalogWritten");
            WriteMetadata("journal.format", "Format", stream => stream.Write(JournalFormat.Encode()));
            Probe?.Invoke("FormatWritten");
            SourceFact[] expectedFiles = outputs.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
            var expectedTarget = new LegacyInventory(targetDirectories.Order(StringComparer.Ordinal).ToArray(), expectedFiles);
            RequireTargetUnchanged();
            Probe?.Invoke("BeforeTargetAudit"); RequireTargetUnchanged();
            var targetAudit = JournalToolkit.Audit(journalRoot, token);
            if (targetAudit.FactsStatus == "Incomplete") { report.Status = "Incomplete"; report.Findings.AddRange(targetAudit.Findings); return report; }
            if (!targetAudit.Completed || targetAudit.FactsStatus != "Healthy" || targetAudit.IndexesStatus != "Consistent") {
                report.Findings.AddRange(targetAudit.Findings); Add("TargetValidationFailed", "."); return report;
            }
            Probe?.Invoke("TargetAudited"); RequireTargetUnchanged();
            if (!DailyCheck(journalRoot, engine, before)) { Add("TargetValidationFailed", "."); return report; }
            Probe?.Invoke("DailyReadChecked");
            RequireSourceUnchanged(); RequireTargetUnchanged(); RequireBundleUnchanged();
            var manifest = new UpgradeManifest(1, "EventJournalLegacyUpgrade", true, Profile, ProfileBaselineRevision, 2,
                root, before.Directories, before.Files, expectedFiles, expectedTarget.Directories, true,
                new("Passed", "Passed", "Passed", "Passed"), report.Findings.LongCount(f => f.Severity == "Warning"));
            Probe?.Invoke("BeforeManifestWrite");
            RequireSourceUnchanged(); RequireTargetUnchanged(); RequireBundleUnchanged();
            string manifestJson = ToolkitJson.Serialize(manifest);
            string temporary = Path.Combine(output, ".manifest.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(manifestJson); writer.Flush(); stream.Flush(true);
            }
            Probe?.Invoke("BeforeManifestPublish");
            RequireSourceUnchanged(); RequireTargetUnchanged(); RequireBundleUnchanged(allowTemporary: true);
            token.ThrowIfCancellationRequested();
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(manifestJson))) { throw new UpgradeDriftException("OutputChanged", ".manifest.tmp"); }
            ToolkitPaths.RequireNoReparse(output);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(output, "manifest.json"), overwrite: false);
            published = true; report.Status = "Created";
            // Post-release probe exists only for failure witnesses. No validation or cancellation follows release.
            Probe?.Invoke("ManifestPublished");
            return report;

            string Target(string relative) => Path.Combine(journalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            void WriteMetadata(string relative, string kind, Action<Stream> write) {
                token.ThrowIfCancellationRequested(); string target = Target(relative); ToolkitPaths.RequireNoReparse(target);
                using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
                outputs.Add(new(kind, relative, new FileInfo(target).Length, AuditEngine.Hash(target)));
            }
            void RequireSourceUnchanged() {
                token.ThrowIfCancellationRequested();
                LegacyInventory observed;
                try { observed = LegacyInventory.Capture(root, token, legacy: true); }
                catch (LegacyInventoryException e) { throw new UpgradeDriftException("SourceChanged", e.Relative); }
                catch (ArgumentException) { throw new UpgradeDriftException("SourceChanged", "."); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new UpgradeDriftException("SourceChanged", "."); }
                if (!before.EqualsInventory(observed)) { throw new UpgradeDriftException("SourceChanged", "."); }
            }
            void RequireTargetUnchanged() {
                token.ThrowIfCancellationRequested();
                LegacyInventory observed;
                try { observed = LegacyInventory.Capture(journalRoot, token, legacy: false); }
                catch (LegacyInventoryException e) { throw new UpgradeDriftException("OutputChanged", e.Relative); }
                catch (ArgumentException) { throw new UpgradeDriftException("OutputChanged", "."); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new UpgradeDriftException("OutputChanged", "."); }
                if (!expectedTarget.EqualsInventory(observed)) { throw new UpgradeDriftException("OutputChanged", "."); }
            }
            void RequireBundleUnchanged(bool allowTemporary = false) {
                ToolkitPaths.RequireNoReparse(output);
                foreach (string path in Directory.EnumerateFileSystemEntries(output)) {
                    string name = Path.GetFileName(path); bool isDirectory = ToolkitPaths.RequireOrdinaryEntry(path);
                    if (name == "journal" && isDirectory || allowTemporary && name == ".manifest.tmp" && !isDirectory) { continue; }
                    throw new UpgradeDriftException("OutputChanged", name);
                }
            }
        }
        catch (OperationCanceledException) { if (!published) { report.Status = "Incomplete"; Add("OperationCancelled", "."); } }
        catch (UpgradeDriftException e) { if (!published) { report.Status = "Incomplete"; Add(e.Code, e.Relative); } }
        catch (LegacyInventoryException e) { if (!published) { Add(e.Code, e.Relative); } }
        catch (ArgumentException) {
            if (!published) {
                if (outputCreated) { report.Status = "Incomplete"; Add("OutputChanged", "."); }
                else { Add("UnknownInventoryEntry", "."); }
            }
        }
        catch (OutOfMemoryException) when (!published) { report.Status = "Incomplete"; Add("ResourceLimit", "."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            if (!published) { report.Status = "Incomplete"; Add("IoFailure", "."); }
        }
        return report;
        void Add(string code, string relative) => report.Findings.Add(new("Error", code, relative, null, null, null));
    }
    private static bool DailyCheck(string target, AuditEngine engine, LegacyInventory source) {
        try {
        using var journal = Journal.OpenReadOnlyExisting(target);
        var eventStore = engine.Stores.Single(s => s.Root == "events");
        uint highest = eventStore.Segments.Keys.Max();
        var frontier = journal.ReadPhysicalAppendFrontier();
        if (frontier.SegmentNumber != highest || frontier.TailOffset != source.Files.Single(f => f.RelativePath == eventStore.Segments[highest]).Length
            || journal.ActiveSegmentNumber != highest
            || !journal.ListBranches().Order(StringComparer.Ordinal).SequenceEqual(engine.Branches.Keys.Order(StringComparer.Ordinal))) { return false; }
        foreach (var branch in engine.Branches) {
            if (journal.OpenBranch(branch.Key).Unwrap() != branch.Value || journal.GetHead(branch.Value) != engine.RefHead(branch.Value)) { return false; }
        }
        foreach (var tag in engine.Tags) { if (journal.ResolveTag(tag.Key).Unwrap() != tag.Value) { return false; } }
        if (engine.LastEventAddress is { } last && journal.ReadEventHeaderChecked(last).Unwrap().SequenceNumber != engine.LastEventSequence) { return false; }
        return true;
        }
        catch (StorageOpenException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
internal sealed class UpgradeDriftException(string code, string relative) : Exception {
    internal string Code { get; } = code;
    internal string Relative { get; } = relative;
}
