using Atelia.RbfSegmentStore;
namespace Atelia.EventJournal.Toolkit;
internal sealed class LegacyInventoryException(string code, string relative) : Exception {
    internal string Code { get; } = code;
    internal string Relative { get; } = relative;
}
internal sealed record LegacyInventory(string[] Directories, SourceFact[] Files) {
    internal bool EqualsInventory(LegacyInventory other) => Directories.SequenceEqual(other.Directories) && Files.SequenceEqual(other.Files);
    internal static LegacyInventory Capture(string root, CancellationToken token, bool legacy) {
        ToolkitPaths.RequireNoReparse(root);
        if (!ToolkitPaths.RequireOrdinaryEntry(root)) { throw new LegacyInventoryException("UnknownInventoryEntry", "."); }
        var directories = new List<string> { "." }; var files = new List<SourceFact>();
        Walk(root);
        token.ThrowIfCancellationRequested();
        return new(directories.Order(StringComparer.Ordinal).ToArray(), files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
        void Walk(string directory) {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                bool isDirectory;
                try { isDirectory = ToolkitPaths.RequireOrdinaryEntry(path); }
                catch (ArgumentException) { throw new LegacyInventoryException("UnknownInventoryEntry", relative); }
                if (legacy && IsMetadata(Path.GetFileName(path))) { throw new LegacyInventoryException("MixedLayout", relative); }
                if (isDirectory) {
                    if (!AllowedDirectory(relative, legacy)) { throw new LegacyInventoryException("UnknownInventoryEntry", relative); }
                    directories.Add(relative); Walk(path);
                }
                else {
                    string? kind = Kind(relative, legacy);
                    if (kind is null) { throw new LegacyInventoryException("UnknownInventoryEntry", relative); }
                    files.Add(new(kind, relative, new FileInfo(path).Length, AuditEngine.Hash(path)));
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
    private static bool IsMetadata(string name) => new[] { "journal.format", "active.segment", "catalog.snapshot" }.Any(marker =>
        name == marker || name.EndsWith(".tmp", StringComparison.Ordinal) && (name.StartsWith(marker + ".", StringComparison.Ordinal) || name.StartsWith("." + marker + ".", StringComparison.Ordinal)));
    private static bool Derived(string path) => path.StartsWith("cache/forward-plans/v1/", StringComparison.Ordinal);
    private static bool AllowedDirectory(string path, bool legacy) {
        if (path is "events" or "events/buckets" or "refs" or "refs/objects") { return true; }
        if (legacy && (path is "cache" or "cache/forward-plans" or "cache/forward-plans/v1" || Derived(path))) { return true; }
        string[] parts = path.Split('/');
        if (parts.Length == 3 && parts[0] == "events" && parts[1] == "buckets") { return RbfSegmentPath.TryParseBucketName(parts[2], out _); }
        if (parts.Length is 3 or 4 && parts[0] == "refs" && parts[1] == "objects" && ValidId(parts[2])) { return parts.Length == 3 || parts[3] == "segments"; }
        return false;
    }
    private static bool ValidId(string name) => RefId.ParseHex(name).IsSuccess && name != "0000000000000000";
    internal static string? Kind(string path, bool legacy) {
        if (legacy && Derived(path)) { return "ExcludedDerived"; }
        if (!legacy && path == "journal.format") { return "Format"; }
        if (path == "refs/ref-op-log.rbf") { return "RefOpLog"; }
        if (!legacy && path == "refs/catalog.snapshot") { return "CatalogSnapshot"; }
        if (!legacy && path == "events/active.segment") { return "Locator"; }
        string[] parts = path.Split('/');
        if (parts.Length == 4 && parts[0] == "events" && parts[1] == "buckets" && RbfSegmentPath.TryParseBucketName(parts[2], out _)
            && RbfSegmentPath.TryParseSegmentFileName(parts[3], out uint eventSegment) && eventSegment != 0 && parts[2] == RbfSegmentPath.BucketName(eventSegment)) { return "EventSegment"; }
        if (parts.Length >= 4 && parts[0] == "refs" && parts[1] == "objects" && ValidId(parts[2])) {
            if (!legacy && parts.Length == 4 && parts[3] == "active.segment") { return "Locator"; }
            if (parts.Length == 5 && parts[3] == "segments" && RbfSegmentPath.TryParseSegmentFileName(parts[4], out uint refSegment) && refSegment != 0) { return "RefMoveSegment"; }
        }
        return null;
    }
}
