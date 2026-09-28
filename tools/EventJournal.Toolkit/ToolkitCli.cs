namespace Atelia.EventJournal.Toolkit;
public static class ToolkitCli {
    public static int Run(string[] args, TextWriter stdout, CancellationToken cancellationToken = default) {
        if (args.Length is not (2 or 4) || args[0] is not ("audit" or "rebuild-indexes")
            || args[0] == "rebuild-indexes" && (args.Length != 4 || args[2] != "--output")
            || args[0] == "audit" && args.Length == 4 && args[2] != "--report") { return 64; }
        try {
            if (args[0] == "audit" && args.Length == 4) { ToolkitPaths.RequireOutsideNew(args[1], args[3]); }
            if (args[0] == "rebuild-indexes") { ToolkitPaths.RequireOutsideNew(args[1], args[3]); }
            AuditReport report = args[0] == "audit" ? JournalToolkit.Audit(args[1], cancellationToken)
                : JournalToolkit.RebuildIndexes(args[1], args[3], cancellationToken);
            string json = ToolkitJson.Serialize(report);
            if (args[0] == "audit" && args.Length == 4) {
                using var file = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file); writer.Write(json); writer.Flush(); file.Flush(true);
            }
            else { stdout.WriteLine(json); }
            return report.ExitCode;
        }
        catch (ArgumentException) { return 64; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) {
            var report = new AuditReport { Completed = false, FactsStatus = "Incomplete" };
            report.Findings.Add(new("Error", e is OperationCanceledException ? "OperationCancelled" : "IoFailure", ".", null, null, null));
            stdout.WriteLine(ToolkitJson.Serialize(report)); return 3;
        }
    }
}
internal static class ToolkitPaths {
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int UnixMkdir([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WindowsCreateDirectory(string path, IntPtr securityAttributes);
    internal static void CreateNewDirectory(string path) {
        bool created = OperatingSystem.IsWindows() ? WindowsCreateDirectory(path, IntPtr.Zero) : UnixMkdir(path, 448) == 0;
        if (!created) { throw new IOException("Candidate directory could not be created exclusively."); }
    }
    internal static IEnumerable<string> EnumerateFilesNoReparse(string directory) {
        RequireNoReparse(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("Reparse paths are not supported."); }
            if ((attrs & FileAttributes.Directory) != 0) { foreach (string child in EnumerateFilesNoReparse(path)) { yield return child; } }
            else { yield return path; }
        }
    }
    internal static void RequireNoReparse(string path) {
        string full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current)) {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("Reparse paths are not supported."); } }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static void RequireOutsideNew(string source, string target) {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string full = Path.GetFullPath(target);
        RequireNoReparse(root); RequireNoReparse(full);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (full.Equals(root, comparison) || full.StartsWith(prefix, comparison)
            || File.Exists(full) || Directory.Exists(full)) { throw new ArgumentException("Output must be new and outside the source."); }
        if (!Directory.Exists(Path.GetDirectoryName(full))) { throw new ArgumentException("Output parent must exist."); }
    }
}
