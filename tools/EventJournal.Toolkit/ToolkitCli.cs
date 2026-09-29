namespace Atelia.EventJournal.Toolkit;
public static class ToolkitCli {
    public static int Run(string[] args, TextWriter stdout, CancellationToken cancellationToken = default) {
        if (args.Length > 0 && args[0] == "upgrade-v1tov2") { return RunUpgrade(args, stdout, cancellationToken); }
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
    private static int RunUpgrade(string[] args, TextWriter stdout, CancellationToken token) {
        if (args.Length is not (5 or 6) || args[2] != "--profile"
            || args.Length == 5 && args[4] != "--check-only" || args.Length == 6 && args[4] != "--output") { return 64; }
        UpgradeReport report;
        try {
            report = args.Length == 5 ? LegacyUpgrade.Check(args[1], args[3], token) : LegacyUpgrade.Upgrade(args[1], args[3], args[5], token);
        }
        catch (ArgumentException) { return 64; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) {
            report = new UpgradeReport { Operation = args.Length == 5 ? "CheckOnly" : "Upgrade", SourceProfile = args[3], Status = "Incomplete",
                OutputRoot = args.Length == 6 ? Path.GetFullPath(args[5]) : null };
            report.Findings.Add(new("Error", e is OperationCanceledException ? "OperationCancelled" : "IoFailure", ".", null, null, null));
        }
        try { stdout.WriteLine(ToolkitJson.Serialize(report)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException) {
            // Transport failure cannot undo an already released candidate or invent an Incomplete storage report.
            return 3;
        }
        return report.ExitCode;
    }
}
internal static class ToolkitPaths {
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int UnixMkdir([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WindowsCreateDirectory(string path, IntPtr securityAttributes);
    // Linux statx has a fixed 256-byte UAPI layout; mode at 0x1c is independent of stat ABI.
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int directory, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, [System.Runtime.InteropServices.Out] byte[] buffer);
    internal static bool RequireOrdinaryEntry(string path) {
        if (OperatingSystem.IsLinux()) {
            byte[] info = new byte[256];
            if (LinuxStatx(-100, path, 0x100, 1, info) != 0) {
                int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == 2) { throw new FileNotFoundException("Cannot inspect file type.", path); }
                if (error == 13) { throw new UnauthorizedAccessException("Cannot inspect file type."); }
                throw new IOException("Cannot inspect file type.");
            }
            if ((BitConverter.ToUInt32(info, 0) & 1) == 0) { throw new IOException("File type is unavailable."); }
            int type = BitConverter.ToUInt16(info, 28) & 0xf000;
            if (type is not (0x4000 or 0x8000)) { throw new ArgumentException("Only ordinary files and directories are supported."); }
            return type == 0x4000;
        }
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) { throw new ArgumentException("Only ordinary files and directories are supported."); }
        return (attrs & FileAttributes.Directory) != 0;
    }
    internal static void CreateNewDirectory(string path) {
        bool created = OperatingSystem.IsWindows() ? WindowsCreateDirectory(path, IntPtr.Zero) : UnixMkdir(path, 448) == 0;
        if (!created) { throw new IOException("Candidate directory could not be created exclusively."); }
    }
    internal static IEnumerable<string> EnumerateFilesNoReparse(string directory) {
        RequireNoReparse(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
            bool isDirectory = RequireOrdinaryEntry(path);
            if (isDirectory) { foreach (string child in EnumerateFilesNoReparse(path)) { yield return child; } }
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
