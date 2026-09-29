using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Atelia;
using Atelia.EventJournal;
using Atelia.EventJournal.Toolkit;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
using Store = Atelia.RbfSegmentStore.RbfSegmentStore;

// Explicit opt-in test process. continue-copy writes only a new disposable copy.
// Reflection is limited to the already-existing offline-tool phase probe for SIGKILL.
static class Program {
    static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    static int Main(string[] args) {
        try {
            if (args.Length != 3) { throw new ArgumentException("Expected kill-child|continue-copy source output [phase is supplied via LEGACY_KILL_PHASE]."); }
            if (args[0] == "kill-child") {
                string phase = Environment.GetEnvironmentVariable("LEGACY_KILL_PHASE") ?? throw new ArgumentException("Missing phase.");
                var property = typeof(LegacyUpgrade).GetProperty("Probe", BindingFlags.Static | BindingFlags.NonPublic)!;
                property.SetValue(null, (Action<string>)(seen => {
                    if (seen != phase) { return; }
                    Emit(new { status = "ReadyToKill", phase }); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite);
                }));
                UpgradeReport result = LegacyUpgrade.Upgrade(args[1], LegacyUpgrade.Profile, args[2]);
                Emit(new { status = "ProbeNotReached", phase, result.Status, result.ExitCode }); return 1;
            }
            if (args[0] != "continue-copy") { throw new ArgumentException("Unknown mode."); }
            ContinueCopy(args[1], args[2]); return 0;
        }
        catch (Exception error) {
            Emit(new { status = "Failed", type = error.GetType().Name }); return 1;
        }
    }
    static void ContinueCopy(string source, string destination) {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination) || destination == source || destination.StartsWith(source + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            throw new ArgumentException("Copy must be new and outside source.");
        }
        var before = Inventory(source);
        var audit = JournalToolkit.Audit(source);
        if (!audit.Completed || audit.FactsStatus != "Healthy" || audit.IndexesStatus != "Consistent") { throw new InvalidOperationException("Source candidate is not healthy."); }
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) { Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory))); }
        foreach (var file in before) {
            string target = Path.Combine(destination, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var from = new FileStream(Path.Combine(source, file.Key), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            from.CopyTo(to); to.Flush(true);
        }
        foreach (var file in before) { if (Hash(Path.Combine(destination, file.Key)) != file.Value) { throw new InvalidOperationException("Copy mismatch."); } }
        uint oldActive;
        using (var store = Store.OpenReadOnlyExisting(Path.Combine(destination, "events"))) { oldActive = store.ActiveSegmentNumber; }
        ulong sequence = MaxEventSequence(destination);
        string name = "upgrade-validation-" + Guid.NewGuid().ToString("N");
        using (var journal = Journal.OpenExisting(destination, new() {
            EventSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 128 },
            RefSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 128 }, RefStoreCacheCapacity = 0
        })) {
            EventAddress? parent = null;
            foreach (var branch in journal.ListBranches()) {
                if (journal.GetHead(journal.OpenBranch(branch).Unwrap()) is { } head) { parent = head; break; }
            }
            RefId id = journal.CreateBranch(name, parent).Unwrap();
            for (int i = 1; i <= 4; i++) {
                EventAddress next = journal.CommitToRef(id, parent, "discardable-copy-smoke"u8, hint: new(0xBAADF00D)).Unwrap().EventAddress;
                EventFrameHeader header = journal.ReadEventHeaderChecked(next).Unwrap();
                if (header.Parent != parent || header.SequenceNumber != sequence + (ulong)i) { throw new InvalidOperationException("Continued frame mismatch."); }
                parent = next;
            }
            journal.MoveRef(id, parent, parent).Unwrap();
            journal.CreateTag(name, parent!.Value).Unwrap();
            journal.ArchiveRef(id, parent, reasonKind: 79).Unwrap();
        }
        using (var store = Store.OpenReadOnlyExisting(Path.Combine(destination, "events"))) {
            if (store.ActiveSegmentNumber <= oldActive) { throw new InvalidOperationException("Rotation was not exercised."); }
        }
        using (var journal = Journal.OpenReadOnlyExisting(destination)) {
            journal.ResolveTag(name).Unwrap();
            if (journal.OpenBranch(name).IsSuccess) { throw new InvalidOperationException("Archived validation branch is still active."); }
        }
        audit = JournalToolkit.Audit(destination);
        if (!audit.Completed || audit.FactsStatus != "Healthy" || audit.IndexesStatus != "Consistent") { throw new InvalidOperationException("Continued copy audit failed."); }
        var after = Inventory(source);
        if (!before.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(after.OrderBy(p => p.Key, StringComparer.Ordinal))) { throw new InvalidOperationException("Source changed."); }
        Emit(new { status = "ContinuedCopyPassed", source, destination, publicOperations = "create/append/advance/move/tag/archive/rotation", fullAudit = "Passed", sourceBytesUnchanged = true });
    }
    static Dictionary<string, string> Inventory(string root) {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("Reparse root."); }
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(string directory) {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("Reparse inventory."); }
                if ((attributes & FileAttributes.Directory) != 0) { Walk(path); }
                else { files.Add(Path.GetRelativePath(root, path), Hash(path)); }
            }
        }
        Walk(root); return files;
    }
    static string Hash(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    static ulong MaxEventSequence(string root) {
        ulong maximum = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "events"), "*.rbf", SearchOption.AllDirectories)) {
            using var file = RbfFile.OpenReadOnlyExisting(path);
            foreach (var info in file.ScanForward()) {
                using var frame = file.ReadPooledFrame(info.Ticket).ToDisposable();
                var value = frame.Unwrap();
                maximum = Math.Max(maximum, EventFrameHeaderCodec.Decode(value.PayloadAndMeta[^value.TailMetaLength..]).Unwrap().SequenceNumber);
            }
        }
        return maximum;
    }
}
