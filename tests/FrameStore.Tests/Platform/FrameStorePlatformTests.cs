using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.FrameStore.Internal.Platform;
using Xunit;

namespace Atelia.FrameStore.Tests.Platform;

public class FrameStorePlatformTests {
    [Fact]
    public void RootCreateCreatesOnlyFinalComponentAndOpenDoesNotCreate() {
        using var fixture = new PlatformFixture();
        string root = Path.Combine(fixture.Parent, "store");
        Assert.Throws<DirectoryNotFoundException>(() => FrameStorePlatform.AdmitRoot(root, createIfMissing: false));
        Assert.False(Directory.Exists(root));
        Assert.Equal(root, FrameStorePlatform.AdmitRoot(root, createIfMissing: true));
        Assert.Equal(root, FrameStorePlatform.AdmitRoot(root, createIfMissing: false));

        string absentParent = Path.Combine(fixture.Parent, "absent", "store");
        Assert.Throws<DirectoryNotFoundException>(() => FrameStorePlatform.AdmitRoot(absentParent, createIfMissing: true));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absentParent)));
    }

    [Fact]
    public void MissingProbesDoNotCreateAndWrongTypesAreNotMissing() {
        using var fixture = new PlatformFixture();
        string file = Path.Combine(fixture.Parent, "item");
        File.WriteAllBytes(file, [0x31]);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireDirectory(file));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AdmitRoot(file, createIfMissing: true));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireFile(fixture.Parent));
        Assert.False(FrameStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "missing")));
        Assert.False(FrameStorePlatform.TryRequireDirectory(Path.Combine(fixture.Parent, "missing")));
        Assert.False(FrameStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "missing-parent", "item")));
        Assert.Equal(new byte[] { 0x31 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void QualificationChecksActualSpellingEvenOnCaseInsensitiveLookup() {
        using var fixture = new PlatformFixture();
        string directory = Path.Combine(fixture.Parent, "Active");
        Directory.CreateDirectory(directory);
        string control = Path.Combine(fixture.Parent, "FrameStore.lock");
        File.WriteAllBytes(control, []);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireDirectory(Path.Combine(fixture.Parent, "active")));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "framestore.lock")));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Empty(File.ReadAllBytes(control));
    }

    [WindowsFact]
    public void WindowsCallerRootAndParentSpellingIsNotPersistentCanonicalName() {
        using var fixture = new PlatformFixture();
        string actualRoot = Path.Combine(fixture.Parent, "store");
        Directory.CreateDirectory(actualRoot);
        string callerRoot = Path.Combine(fixture.Parent.ToUpperInvariant(), "STORE");
        Assert.True(FrameStorePlatform.TryAdmitRoot(callerRoot));
        Assert.Equal(callerRoot, FrameStorePlatform.AdmitRoot(callerRoot, createIfMissing: false));
        using (FrameStorePlatform.AcquireOwnerLock(callerRoot, create: true, readOnly: false)) {
            FrameStorePlatform.RequireFile(Path.Combine(callerRoot, "framestore.lock"));
        }
        string newRoot = Path.Combine(fixture.Parent.ToUpperInvariant(), "new-store");
        Assert.Equal(newRoot, FrameStorePlatform.AdmitRoot(newRoot, createIfMissing: true));
    }

    [Fact]
    public void DirectoryCreationReusesOrdinaryExactDirectoryAndRejectsFile() {
        using var fixture = new PlatformFixture();
        string directory = Path.Combine(fixture.Parent, "active");
        FrameStorePlatform.CreateDirectory(directory);
        FrameStorePlatform.CreateDirectory(directory);
        FrameStorePlatform.RequireEnumeratedDirectory(directory);
        FrameStorePlatform.RequireSameFileSystem(fixture.Parent, directory);
        string file = Path.Combine(fixture.Parent, "archive");
        File.WriteAllBytes(file, [0x39]);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.CreateDirectory(file));
        Assert.Equal(new byte[] { 0x39 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void DirectEntryCheckIncludesHiddenNamesAndRejectsMissing() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, ".hidden");
        File.WriteAllBytes(path, []);
        if (OperatingSystem.IsWindows()) { File.SetAttributes(path, FileAttributes.Hidden); }
        FrameStorePlatform.RequireExactComponent(fixture.Parent, ".hidden");
        FrameStorePlatform.RequireFile(path);
        FrameStorePlatform.RequireEnumeratedFile(path);
        Assert.Throws<FileNotFoundException>(() => FrameStorePlatform.RequireExactComponent(fixture.Parent, "missing"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void SameProcessOwnerMatrixIsStrict(bool heldReadOnly, bool requestedReadOnly, bool allowed) {
        using var fixture = new PlatformFixture();
        using (FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false)) { }
        using var held = FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: heldReadOnly);
        if (allowed) {
            using var shared = FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: requestedReadOnly);
        }
        else {
            Assert.Throws<IOException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: requestedReadOnly));
        }
    }

    [Fact]
    public void BootstrapIsZeroAndMetadataQualificationWorksWhileWriterHoldsLock() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "framestore.lock");
        using (FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false)) {
            FrameStorePlatform.RequireFile(path);
            FrameStorePlatform.RequireEnumeratedFile(path);
            Assert.Throws<IOException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        }
        Assert.Empty(File.ReadAllBytes(path));
        using (FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: false)) { }
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingOpenLockDoesNotBootstrapAndNonemptyLockIsNeverTruncated() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "framestore.lock");
        Assert.Throws<FileNotFoundException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: false));
        Assert.Throws<FileNotFoundException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: true));
        Assert.False(File.Exists(path));
        File.WriteAllBytes(path, [0x71]);
        foreach (bool readOnly in new[] { false, true }) {
            Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly));
        }
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Equal(new byte[] { 0x71 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void MoveNeverOverwritesAndPreservesBytesAndPublishedBasename() {
        using var fixture = new PlatformFixture();
        string active = Path.Combine(fixture.Parent, "active");
        string archive = Path.Combine(fixture.Parent, "archive");
        FrameStorePlatform.CreateDirectory(active);
        FrameStorePlatform.CreateDirectory(archive);
        string source = Path.Combine(active, "00000001.rbf");
        string target = Path.Combine(archive, "00000001.rbf");
        File.WriteAllBytes(source, [0x13, 0x27]);
        File.WriteAllBytes(target, [0x45]);
        Assert.Throws<IOException>(() => FrameStorePlatform.MoveFileNoOverwrite(source, target));
        Assert.Equal(new byte[] { 0x13, 0x27 }, File.ReadAllBytes(source));
        Assert.Equal(new byte[] { 0x45 }, File.ReadAllBytes(target));
        FrameStorePlatform.DeleteFile(target);
        FrameStorePlatform.MoveFileNoOverwrite(source, target);
        Assert.False(File.Exists(source));
        Assert.Equal(new byte[] { 0x13, 0x27 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void MoveRejectsDestinationDirectoryAndCaseAliasWithoutChangingSource() {
        using var fixture = new PlatformFixture();
        string source = Path.Combine(fixture.Parent, "source");
        File.WriteAllBytes(source, [0x13]);
        string target = Path.Combine(fixture.Parent, "target");
        Directory.CreateDirectory(target);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.MoveFileNoOverwrite(source, target));
        Directory.Delete(target);
        File.WriteAllBytes(Path.Combine(fixture.Parent, "Target"), [0x29]);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.MoveFileNoOverwrite(source, target));
        Assert.Equal(new byte[] { 0x13 }, File.ReadAllBytes(source));
        Assert.Equal(new byte[] { 0x29 }, File.ReadAllBytes(Path.Combine(fixture.Parent, "Target")));
    }

    [Fact]
    public void PrivateDeleteRejectsMissingAndDirectory() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "private");
        Assert.Throws<FileNotFoundException>(() => FrameStorePlatform.DeleteFile(path));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.DeleteFile(fixture.Parent));
        File.WriteAllBytes(path, [0x19]);
        FrameStorePlatform.DeleteFile(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RootAndManagedDirectoryLinksAreRejectedWithoutFollowing() {
        using var fixture = new PlatformFixture();
        string target = Path.Combine(fixture.Parent, "target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(fixture.Parent, "link");
        CreateDirectoryLink(link, target);
        fixture.DirectoryLinks.Add(link);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AdmitRoot(link, createIfMissing: false));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireDirectory(link));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.RequireEnumeratedDirectory(link));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.CreateDirectory(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [LinuxFact]
    public void LinuxFileSymlinkIncludingDanglingLinkIsRejected() {
        using var fixture = new PlatformFixture();
        string target = Path.Combine(fixture.Parent, "target");
        File.WriteAllBytes(target, [0x41]);
        string link = Path.Combine(fixture.Parent, "framestore.lock");
        File.CreateSymbolicLink(link, target);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireFile(link));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.RequireEnumeratedFile(link));
        Assert.Throws<IOException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.DeleteFile(link));
        File.Delete(target);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireFile(link));
    }

    [LinuxFact]
    public void LinuxFifoIsRejectedWithoutBlockingOrWriting() {
        using var fixture = new PlatformFixture();
        string fifo = Path.Combine(fixture.Parent, "framestore.lock");
        Assert.Equal(0, Mkfifo(fifo, 0x180));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.TryRequireFile(fifo));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.RequireEnumeratedFile(fifo));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.AdmitRoot(fifo, createIfMissing: false));
    }

    [LinuxFact]
    public void LinuxDifferentMountIsRejectedBeforeMutation() {
        using var fixture = new PlatformFixture();
        FrameStorePlatform.RequireDirectory("/proc");
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.RequireSameFileSystem(fixture.Parent, "/proc"));
        string source = Path.Combine(fixture.Parent, "source");
        File.WriteAllBytes(source, [0x36]);
        Assert.Throws<InvalidDataException>(() => FrameStorePlatform.MoveFileNoOverwrite(source, "/proc/framestore-no-overwrite-probe"));
        Assert.Equal(new byte[] { 0x36 }, File.ReadAllBytes(source));
    }

    private static void CreateDirectoryLink(string link, string target) {
        if (OperatingSystem.IsLinux()) { Directory.CreateSymbolicLink(link, target); return; }
        // A directory junction needs no symlink privilege. Both generated paths are owned by this fixture.
        var start = new ProcessStartInfo("cmd.exe") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\""
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Junction probe did not start.");
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("libc", EntryPoint = "mkfifo", ExactSpelling = true, SetLastError = true)]
    private static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}

internal sealed class LinuxFactAttribute : FactAttribute {
    public LinuxFactAttribute() {
        if (!OperatingSystem.IsLinux()) { Skip = "Linux native type/mount vector."; }
    }
}

internal sealed class WindowsFactAttribute : FactAttribute {
    public WindowsFactAttribute() {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows caller path spelling vector."; }
    }
}

internal sealed class PlatformFixture : IDisposable {
    internal string Parent { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FrameStore-platform-" + Guid.NewGuid().ToString("N")));
    internal List<string> DirectoryLinks { get; } = [];

    internal PlatformFixture() {
        Directory.CreateDirectory(Parent);
    }

    public void Dispose() {
        // The absolute generated fixture remains inside TEMP; it is never a caller-selected store root.
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Parent.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
            throw new InvalidOperationException("Refusing to remove a fixture outside TEMP.");
        }
        foreach (string link in DirectoryLinks) {
            string fullPath = Path.GetFullPath(link);
            string prefix = Path.TrimEndingDirectorySeparator(Parent) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                throw new InvalidOperationException("Refusing to remove a link outside the generated fixture.");
            }
            // Remove only the fixture-owned link itself; never recurse through its target.
            Directory.Delete(fullPath, recursive: false);
        }
        Directory.Delete(Parent, recursive: true);
    }
}
