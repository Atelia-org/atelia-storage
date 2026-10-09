using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Atelia.FrameStore.Internal.Runtime;

namespace Atelia.FrameStore.Internal.Platform;

/// <summary>
/// Narrow Windows/Linux admission for a stable, caller-selected root. The protocol excludes
/// external mutation of the root or its managed entries; this is not an adversarial path sandbox.
/// </summary>
internal static class FrameStorePlatform {
    private const string OwnerLockName = "framestore.lock";

    internal static string AdmitRoot(string root, bool createIfMissing) {
        RequireSupportedPlatform();
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!TryAdmitRoot(fullPath)) {
            if (!createIfMissing) { throw new DirectoryNotFoundException(fullPath); }
            CreateDirectoryCore(fullPath, exactName: false);
        }
        return fullPath;
    }

    /// <summary>The caller-selected root has no persisted canonical spelling; qualify its actual native type only.</summary>
    internal static bool TryAdmitRoot(string root) {
        RequireSupportedPlatform();
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        FileStatus? status = StatusForPath(fullPath);
        if (status is null) { return false; }
        RequireKind(status.Value, fullPath, directory: true);
        return true;
    }

    internal static void RequireDirectory(string path) {
        if (!TryRequireDirectory(path)) { throw new DirectoryNotFoundException(path); }
    }

    internal static void RequireFile(string path) {
        if (!TryRequireFile(path)) { throw new FileNotFoundException("Required ordinary file is missing.", path); }
    }

    internal static bool TryRequireDirectory(string path) {
        return TryQualify(path, directory: true, out _);
    }

    internal static bool TryRequireFile(string path) {
        return TryQualify(path, directory: false, out _);
    }

    /// <summary>Caller has already checked the actual enumerated component with the canonical name codec.</summary>
    internal static void RequireEnumeratedFile(string path) {
        RequireEnumeratedKind(path, directory: false);
    }

    /// <summary>Caller has already checked the actual enumerated component with the canonical name codec.</summary>
    internal static void RequireEnumeratedDirectory(string path) {
        RequireEnumeratedKind(path, directory: true);
    }

    /// <summary>Inspect actual direct-entry spelling, including hidden entries and case aliases.</summary>
    internal static void RequireExactComponent(string parent, string component, bool allowMissing = false) {
        RequireSupportedPlatform();
        if (string.IsNullOrEmpty(component) || component is "." or ".." || component.IndexOfAny(['/', '\\']) >= 0) {
            throw new ArgumentException("Expected a single path component.", nameof(component));
        }
        bool found = false;
        string? alias = null;
        var options = new EnumerationOptions {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        IEnumerator<string>? entries = null;
        CleanupErrors errors = default;
        try {
            entries = Directory.EnumerateFileSystemEntries(parent, "*", options).GetEnumerator();
            while (entries.MoveNext()) {
                string actual = Path.GetFileName(entries.Current);
                if (string.Equals(actual, component, StringComparison.Ordinal)) { found = true; }
                else if (string.Equals(actual, component, StringComparison.OrdinalIgnoreCase)) { alias = actual; }
            }
        }
        catch (Exception error) { errors.Add(error); }
        var toClose = entries;
        entries = null;
        try { toClose?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        errors.ThrowIfAny();
        if (!found && alias is not null) {
            throw new InvalidDataException($"Path component '{alias}' is not the required exact spelling '{component}'.");
        }
        if (!found && !allowMissing) { throw new FileNotFoundException("Required exact path component is missing.", Path.Combine(parent, component)); }
    }

    /// <summary>Create only the final directory component; an existing ordinary parent is required.</summary>
    internal static void CreateDirectory(string path) {
        CreateDirectoryCore(path, exactName: true);
    }

    private static void CreateDirectoryCore(string path, bool exactName) {
        RequireSupportedPlatform();
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("A final directory component is required.", nameof(path));
        _ = QualifyEnumerated(parent, directory: true);
        if (exactName) { RequireExactComponent(parent, Path.GetFileName(fullPath), allowMissing: true); }
        if (OperatingSystem.IsWindows()) {
            if (!Windows.CreateDirectory(fullPath, IntPtr.Zero)) {
                int error = Marshal.GetLastPInvokeError();
                if (error != 183) { throw NativeError("CreateDirectory", fullPath, error); }
            }
        }
        else if (Linux.Mkdir(fullPath, 0x1c0) != 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error != 17) { throw NativeError("mkdir", fullPath, error); }
        }
        if (exactName) { RequireDirectory(fullPath); }
        else { _ = QualifyEnumerated(fullPath, directory: true); }
    }

    /// <summary>Paths have already obtained actual spelling qualification; check native type and filesystem without re-enumeration.</summary>
    internal static void RequireSameFileSystem(string firstDirectory, string secondDirectory) {
        FileStatus first = QualifyEnumerated(firstDirectory, directory: true);
        FileStatus second = QualifyEnumerated(secondDirectory, directory: true);
        if (first.FileSystem != second.FileSystem) {
            throw new InvalidDataException("FrameStore directories must belong to the same filesystem and mount.");
        }
    }

    /// <summary>The returned same handle owns the lifetime lock and must be disposed last.</summary>
    internal static IDisposable AcquireOwnerLock(string root, bool create, bool readOnly) {
        RequireSupportedPlatform();
        if (create && readOnly) { throw new ArgumentException("Read-only admission cannot bootstrap a control file.", nameof(create)); }
        FileStatus rootStatus = QualifyEnumerated(root, directory: true);
        string path = Path.Combine(root, OwnerLockName);
        RequireExactComponent(root, OwnerLockName, allowMissing: create);
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows()) {
            handle = Windows.CreateFile(path, readOnly ? Windows.GenericRead : Windows.GenericRead | Windows.GenericWrite,
                readOnly ? 1u : 0u, IntPtr.Zero, create ? 4u : 3u, Windows.OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw NativeError("CreateFile owner lock", path, error);
            }
        }
        else {
            int flags = (readOnly ? 0 : 2) | Linux.CloseOnExec | Linux.NoFollow | Linux.NonBlock;
            if (create) { flags |= 0x40; }
            int descriptor = Linux.Open(path, flags, 0x180);
            if (descriptor < 0) { throw NativeError("open owner lock", path, Marshal.GetLastPInvokeError()); }
            try { handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true); }
            catch (Exception error) {
                CleanupErrors errors = default;
                errors.Add(error);
                try {
                    if (Linux.Close(descriptor) != 0) { errors.Add(NativeError("close unowned owner descriptor", path, Marshal.GetLastPInvokeError())); }
                }
                catch (Exception closeError) { errors.Add(closeError); }
                errors.ThrowIfAny();
                throw;
            }
        }
        try {
            FileStatus status = StatusForHandle(handle, path);
            RequireKind(status, path, directory: false);
            if (status.Length != 0) { throw new InvalidDataException("FrameStore control file must remain exactly empty."); }
            if (status.FileSystem != rootStatus.FileSystem) { throw new InvalidDataException("Control file is on a different filesystem."); }
            if (OperatingSystem.IsLinux() && Linux.Flock(handle, (readOnly ? 1 : 2) | 4) != 0) {
                throw NativeError("flock owner lock", path, Marshal.GetLastPInvokeError());
            }
            RequireExactComponent(root, OwnerLockName);
            return handle;
        }
        catch (Exception error) { CloseAfterFailure(handle, error); throw; }
    }

    /// <summary>Native rename on the same filesystem; never copy or replace the destination.</summary>
    internal static void MoveFileNoOverwrite(string source, string target) {
        RequireFile(source);
        string sourceParent = Path.GetDirectoryName(Path.GetFullPath(source))!;
        string targetParent = Path.GetDirectoryName(Path.GetFullPath(target))!;
        RequireSameFileSystem(sourceParent, targetParent);
        FileStatus sourceStatus = Qualify(source, directory: false);
        FileStatus targetParentStatus = Qualify(targetParent, directory: true);
        if (sourceStatus.FileSystem != targetParentStatus.FileSystem) { throw new InvalidDataException("Source file is on a different filesystem."); }
        if (TryRequireFile(target)) { throw new IOException("Move destination already exists."); }
        if (OperatingSystem.IsWindows()) {
            // No MOVEFILE_COPY_ALLOWED and no MOVEFILE_REPLACE_EXISTING.
            if (!Windows.MoveFileEx(source, target, 0)) { throw NativeError("MoveFileEx", source, Marshal.GetLastPInvokeError()); }
        }
        else if (Linux.RenameAt2(Linux.CurrentDirectory, source, Linux.CurrentDirectory, target, 1) != 0) {
            // RENAME_NOREPLACE; unsupported kernels/filesystems fail closed, without a fallback.
            throw NativeError("renameat2 RENAME_NOREPLACE", source, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Delete one qualified private file; a missing entry is an error, not a successful cancellation.</summary>
    internal static void DeleteFile(string path) {
        RequireFile(path);
        if (OperatingSystem.IsWindows()) {
            if (!Windows.DeleteFile(path)) { throw NativeError("DeleteFile", path, Marshal.GetLastPInvokeError()); }
        }
        else if (Linux.Unlink(path) != 0) { throw NativeError("unlink", path, Marshal.GetLastPInvokeError()); }
    }

    private static FileStatus Qualify(string path, bool directory) {
        if (TryQualify(path, directory, out FileStatus status)) { return status; }
        if (directory) { throw new DirectoryNotFoundException(path); }
        throw new FileNotFoundException("Required ordinary file is missing.", path);
    }

    private static void RequireEnumeratedKind(string path, bool directory) {
        _ = QualifyEnumerated(path, directory);
    }

    private static FileStatus QualifyEnumerated(string path, bool directory) {
        RequireSupportedPlatform();
        FileStatus? status = StatusForPath(path);
        if (status is null) {
            if (directory) { throw new DirectoryNotFoundException(path); }
            throw new FileNotFoundException("Enumerated ordinary file is missing.", path);
        }
        RequireKind(status.Value, path, directory);
        if (!directory) { RequireFileParentFileSystem(path, status.Value); }
        return status.Value;
    }

    private static bool TryQualify(string path, bool directory, out FileStatus status) {
        RequireSupportedPlatform();
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        FileStatus? candidate = StatusForPath(fullPath);
        status = default;
        string? parent = Path.GetDirectoryName(fullPath);
        if (candidate is null) {
            if (parent is not null) {
                try { RequireExactComponent(parent, Path.GetFileName(fullPath), allowMissing: true); }
                catch (DirectoryNotFoundException) { }
            }
            return false;
        }
        RequireKind(candidate.Value, path, directory);
        if (!directory) { RequireFileParentFileSystem(fullPath, candidate.Value); }
        if (parent is not null) { RequireExactComponent(parent, Path.GetFileName(fullPath)); }
        status = candidate.Value;
        return true;
    }

    private static void RequireKind(FileStatus status, string path, bool directory) {
        if (!status.Ordinary || status.Directory != directory) {
            throw new InvalidDataException($"'{path}' must be an ordinary {(directory ? "directory" : "file")} without a link or reparse point.");
        }
    }

    private static void RequireFileParentFileSystem(string path, FileStatus file) {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        FileStatus parentStatus = QualifyEnumerated(parent, directory: true);
        if (file.FileSystem != parentStatus.FileSystem) {
            throw new InvalidDataException("A managed FrameStore file must belong to its ordinary parent's filesystem and mount.");
        }
    }

    private static FileStatus? StatusForPath(string path) {
        if (OperatingSystem.IsWindows()) {
            // Access=0 permits metadata qualification even while the owner holds FileShare.None.
            SafeFileHandle handle = Windows.CreateFile(path, 0, 7, IntPtr.Zero, 3,
                Windows.OpenReparsePoint | Windows.BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error is 2 or 3) { return null; }
                throw NativeError("CreateFile metadata", path, error);
            }
            FileStatus status;
            try { status = StatusForHandle(handle, path); }
            catch (Exception error) { CloseAfterFailure(handle, error); throw; }
            handle.Dispose();
            return status;
        }
        if (Linux.Statx(Linux.CurrentDirectory, path, 0x100 | 0x800, Linux.RequiredMask, out Linux.StatxBuffer stat) != 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == 2) { return null; }
            throw NativeError("statx no-follow", path, error);
        }
        return LinuxStatus(stat, path);
    }

    private static FileStatus StatusForHandle(SafeFileHandle handle, string path) {
        if (OperatingSystem.IsWindows()) {
            if (!Windows.GetFileInformationByHandle(handle, out Windows.FileInformation info)) {
                throw NativeError("GetFileInformationByHandle", path, Marshal.GetLastPInvokeError());
            }
            uint type = Windows.GetFileType(handle);
            bool ordinary = type == 1 && (info.Attributes & 0x400) == 0;
            ulong length = ((ulong)info.SizeHigh << 32) | info.SizeLow;
            return new FileStatus(ordinary, (info.Attributes & 0x10) != 0, length,
                ordinary ? new FileSystemIdentity(WindowsVolume(handle, path), 0, 0, 0) : default);
        }
        int descriptor = checked((int)handle.DangerousGetHandle());
        if (Linux.Statx(descriptor, "", 0x1000, Linux.RequiredMask, out Linux.StatxBuffer stat) != 0) {
            throw NativeError("statx owner descriptor", path, Marshal.GetLastPInvokeError());
        }
        return LinuxStatus(stat, path);
    }

    private static FileStatus LinuxStatus(Linux.StatxBuffer stat, string path) {
        if ((stat.Mask & Linux.RequiredMask) != Linux.RequiredMask) {
            throw new PlatformNotSupportedException($"statx did not provide type, size and mount identity for '{path}'.");
        }
        int kind = stat.Mode & 0xf000;
        return new FileStatus(kind is 0x4000 or 0x8000, kind == 0x4000, stat.Size,
            new FileSystemIdentity(null, stat.DeviceMajor, stat.DeviceMinor, stat.MountId));
    }

    private static string WindowsVolume(SafeFileHandle handle, string path) {
        var buffer = new StringBuilder(512);
        uint count = Windows.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
        if (count == 0) { throw NativeError("GetFinalPathNameByHandle volume GUID", path, Marshal.GetLastPInvokeError()); }
        if (count >= buffer.Capacity) {
            buffer = new StringBuilder(checked((int)count + 1));
            count = Windows.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 1);
            if (count == 0 || count >= buffer.Capacity) { throw NativeError("GetFinalPathNameByHandle volume GUID", path, Marshal.GetLastPInvokeError()); }
        }
        string finalPath = buffer.ToString();
        const string prefix = @"\\?\Volume{";
        int end = finalPath.IndexOf('}', prefix.Length);
        if (!finalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || end < 0) {
            throw new PlatformNotSupportedException("FrameStore requires a Windows volume with a native GUID identity.");
        }
        return finalPath[..(end + 1)];
    }

    private static void CloseAfterFailure(SafeFileHandle handle, Exception first) {
        CleanupErrors errors = default;
        errors.Add(first);
        try { handle.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        errors.ThrowIfAny();
    }

    private static void RequireSupportedPlatform() {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) {
            throw new PlatformNotSupportedException("FrameStore filesystem admission supports Windows and Linux only.");
        }
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86 or Architecture.Arm64)) {
            throw new PlatformNotSupportedException("Linux native constants are qualified for x86, x64 and arm64 only.");
        }
    }

    private static Exception NativeError(string operation, string path, int error) {
        string message = $"{operation} failed for '{path}': {new Win32Exception(error).Message} (native error {error}).";
        if ((OperatingSystem.IsWindows() && error == 5) || (OperatingSystem.IsLinux() && error is 1 or 13)) {
            return new UnauthorizedAccessException(message);
        }
        if ((OperatingSystem.IsWindows() && error == 2) || (OperatingSystem.IsLinux() && error == 2)) {
            return new FileNotFoundException(message, path);
        }
        if (OperatingSystem.IsWindows() && error == 3) { return new DirectoryNotFoundException(message); }
        return new IOException(message, new Win32Exception(error));
    }

    private readonly record struct FileSystemIdentity(string? WindowsVolume, uint DeviceMajor, uint DeviceMinor, ulong MountId);
    private readonly record struct FileStatus(bool Ordinary, bool Directory, ulong Length, FileSystemIdentity FileSystem);

    private static class Windows {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint OpenReparsePoint = 0x00200000;
        internal const uint BackupSemantics = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation {
            internal uint Attributes;
            internal uint CreationTimeLow, CreationTimeHigh;
            internal uint LastAccessTimeLow, LastAccessTimeHigh;
            internal uint LastWriteTimeLow, LastWriteTimeHigh;
            internal uint VolumeSerialNumber;
            internal uint SizeHigh, SizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern uint GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectory(string path, IntPtr security);

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string source, string target, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteFile(string path);
    }

    private static class Linux {
        internal const int CurrentDirectory = -100;
        internal const int CloseOnExec = 0x80000;
        internal const int NoFollow = 0x20000;
        internal const int NonBlock = 0x800;
        internal const uint RequiredMask = 0x1 | 0x200 | 0x1000;

        // The Linux UAPI statx buffer is 256 bytes, independent of libc's architecture-specific stat.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct StatxBuffer {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(40)] internal ulong Size;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
            [FieldOffset(144)] internal ulong MountId;
        }

        [DllImport("libc", EntryPoint = "statx", ExactSpelling = true, SetLastError = true)]
        internal static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer buffer);

        [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "close", ExactSpelling = true, SetLastError = true)]
        internal static extern int Close(int descriptor);

        [DllImport("libc", EntryPoint = "flock", ExactSpelling = true, SetLastError = true)]
        internal static extern int Flock(SafeFileHandle descriptor, int operation);

        [DllImport("libc", EntryPoint = "mkdir", ExactSpelling = true, SetLastError = true)]
        internal static extern int Mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "renameat2", ExactSpelling = true, SetLastError = true)]
        internal static extern int RenameAt2(int oldDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
            int newDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string target, uint flags);

        [DllImport("libc", EntryPoint = "unlink", ExactSpelling = true, SetLastError = true)]
        internal static extern int Unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    }
}
