using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FrameStoreResourceProbe;

internal sealed class ProbeReport {
    public int SchemaVersion { get; } = 1;
    public string Status { get; set; } = "running";
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public required ProbeOptions Input { get; init; }
    public object Environment { get; } = new {
        Framework = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeVersion = System.Environment.Version.ToString(),
        System.Environment.ProcessorCount,
        ProcessId = System.Environment.ProcessId,
        ServerGC = System.Runtime.GCSettings.IsServerGC,
        FileLockingEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING")
    };
    public string CacheCondition { get; } = "New owner each Open; OS/storage caches uncontrolled and expected warm after construction. No cache flush or cold-disk claim.";
    public string BudgetSemantics { get; } = "Cooperative elapsed-time checks before operations; synchronous I/O/CRC/GC already in progress is not interrupted. Disk checked at sample boundaries against actual allocation bytes when available, otherwise logical file lengths.";
    public string MemoryInterpretation { get; } = "AllocatedBytesDelta measures all process-thread managed allocations during the operation. ManagedBytes is a full-GC live checkpoint, not a pool inventory or leak test. ManagedPoolRetainedBytes is unknown. Samples/Assertions/ExpectedFailures and address tables are retained by the probe and contribute to live growth. Explicit GC can also affect pool trimming. RSS includes runtime, code, native allocation and OS paging.";
    public string HandleInterpretation { get; } = "Windows Process.HandleCount; Linux /proc/self/fd count includes the observer enumeration fd. BeforeForcedGc is observed immediately on snapshot entry, before the probe runs GC/finalizers; AfterForcedGc is a separate observation. Natural GC/finalization may occur during an operation. Both counts include runtime/observer resources. Warmup initializes measurement, JSON, logging and public API paths. Counts/deltas have no fixed leak threshold.";
    public List<ProbeSample> Samples { get; } = [];
    public List<ProbeAssertion> Assertions { get; } = [];
    public List<object> ExpectedFailures { get; } = [];
    public string? Failure { get; set; }
    public DiskSnapshot? FinalDisk { get; set; }
}

internal sealed record ProbeAssertion(string Name, bool Passed, string Detail);
internal sealed record ProbeSample(
    string Scenario, string Phase, int Repeat, int Scale, double ElapsedMilliseconds,
    long AllocatedBytesDelta, ResourceSnapshot Before, ResourceSnapshot After,
    DiskSnapshot Disk, Dictionary<string, object?> Details);

internal sealed record ResourceSnapshot(
    long ManagedBytes, long GcHeapSizeBytes, long GcFragmentedBytes, bool ForcedGc,
    long RssBytes, long? NativeHandleCountBeforeForcedGc, string? NativeHandleErrorBeforeForcedGc,
    long? NativeHandleCountAfterForcedGc, string? NativeHandleErrorAfterForcedGc,
    int Gen0Collections, int Gen1Collections, int Gen2Collections) {
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    public static ResourceSnapshot Capture(bool forcedGc = true) {
        // Record immediate post-operation handles before our explicit GC can rescue forgotten Dispose.
        var (handlesBefore, errorBefore) = ReadNativeHandles();
        if (forcedGc) {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
        long live = GC.GetTotalMemory(forceFullCollection: false);
        var gc = GC.GetGCMemoryInfo();
        var (handlesAfter, errorAfter) = ReadNativeHandles();
        CurrentProcess.Refresh();
        return new(live, gc.HeapSizeBytes, gc.FragmentedBytes, forcedGc, CurrentProcess.WorkingSet64,
            handlesBefore, errorBefore, handlesAfter, errorAfter,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static (long? Count, string? Error) ReadNativeHandles() {
        long? handles = null;
        string? handleError = null;
        try {
            if (OperatingSystem.IsWindows()) { CurrentProcess.Refresh(); handles = CurrentProcess.HandleCount; }
            else if (OperatingSystem.IsLinux()) { handles = Directory.GetFileSystemEntries("/proc/self/fd").LongLength; }
            else { handleError = "Native handle observation is available only on Windows/Linux."; }
        }
        catch (Exception error) { handleError = error.GetType().Name + ": " + error.Message; }
        return (handles, handleError);
    }
}

internal sealed record DiskSnapshot(
    long FileCount, long DirectoryCount, long ActiveFileCount, long ArchiveFileCount, long ArchiveBucketCount,
    long LogicalFileBytes, long? AllocatedFileBytes, string AllocationMetric, string? AllocationError) {
    public static DiskSnapshot Capture(string root) {
        if (!Directory.Exists(root)) { return new(0, 0, 0, 0, 0, 0, 0, Metric, null); }
        long files = 0, active = 0, archive = 0, buckets = 0, length = 0, allocated = 0;
        string? allocationError = null;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            files++;
            length += new FileInfo(path).Length;
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            string[] parts = relative.Split('/');
            if (parts.Length >= 2 && parts[^2] == "active") { active++; }
            if (parts.Length >= 3 && parts[^3] == "archive") { archive++; }
            if (allocationError is null) {
                try { allocated += NativeDiskAllocation.GetBytes(path); }
                catch (Exception error) { allocationError = error.GetType().Name + ": " + error.Message; }
            }
        }
        long directories = 0;
        foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) {
            directories++;
            if (Path.GetFileName(Path.GetDirectoryName(path)) == "archive") { buckets++; }
        }
        return new(files, directories, active, archive, buckets, length,
            allocationError is null ? allocated : null, Metric, allocationError);
    }

    private static string Metric => OperatingSystem.IsWindows()
        ? "GetFileInformationByHandleEx FileStandardInfo.AllocationSize via access=0 metadata handle; directory metadata excluded"
        : "Linux statx stx_blocks * 512 for regular files; directory metadata excluded";
}

internal static class NativeDiskAllocation {
    public static long GetBytes(string path) {
        if (OperatingSystem.IsWindows()) {
            // Access=0 queries metadata and remains compatible with owned FileShare.None data handles.
            using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()); }
            if (!GetFileInformationByHandleEx(handle, 1, out var result, (uint)Marshal.SizeOf<FileStandardInfo>())) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            return result.AllocationSize;
        }
        if (OperatingSystem.IsLinux()) {
            const uint StatxBlocks = 0x400;
            if (Statx(-100, path, 0x100, StatxBlocks, out var result) != 0) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            if ((result.Mask & StatxBlocks) == 0) { throw new IOException("statx did not provide stx_blocks."); }
            return checked((long)result.Blocks * 512);
        }
        throw new PlatformNotSupportedException("Allocation byte observation requires Windows/Linux.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(48)] public ulong Blocks;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileStandardInfo {
        [FieldOffset(0)] public long AllocationSize;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileStandardInfo result, uint bufferSize);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, out StatxBuffer result);
}

internal static class JsonOutput {
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
