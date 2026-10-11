using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.VersionStore.Internal.Platform;
using Atelia.VersionStore.Internal.Runtime;
using Xunit;

namespace Atelia.VersionStore.Tests.Platform;

public class VersionStorePlatformTests {
    [Fact]
    public void RootCreateCreatesOnlyFinalComponentAndOpenDoesNotCreate() {
        using var fixture = new PlatformFixture();
        string root = Path.Combine(fixture.Parent, "store");
        Assert.Throws<DirectoryNotFoundException>(() => VersionStorePlatform.AdmitRoot(root, createIfMissing: false));
        Assert.False(Directory.Exists(root));
        Assert.Equal(root, VersionStorePlatform.AdmitRoot(root, createIfMissing: true));
        Assert.Equal(root, VersionStorePlatform.AdmitRoot(root, createIfMissing: false));

        string absentParent = Path.Combine(fixture.Parent, "absent", "store");
        Assert.Throws<DirectoryNotFoundException>(() => VersionStorePlatform.AdmitRoot(absentParent, createIfMissing: true));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absentParent)));
    }

    [Fact]
    public void MissingProbesDoNotCreateAndWrongTypesAreNotMissing() {
        using var fixture = new PlatformFixture();
        string file = Path.Combine(fixture.Parent, "item");
        File.WriteAllBytes(file, [0x31]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireDirectory(file));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AdmitRoot(file, createIfMissing: true));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireFile(fixture.Parent));
        Assert.False(VersionStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "missing")));
        Assert.False(VersionStorePlatform.TryRequireDirectory(Path.Combine(fixture.Parent, "missing")));
        Assert.False(VersionStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "missing-parent", "item")));
        Assert.Equal(new byte[] { 0x31 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void QualificationChecksActualSpellingEvenOnCaseInsensitiveLookup() {
        using var fixture = new PlatformFixture();
        string directory = Path.Combine(fixture.Parent, "Creating");
        Directory.CreateDirectory(directory);
        string control = Path.Combine(fixture.Parent, "VersionStore.lock");
        File.WriteAllBytes(control, []);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireDirectory(Path.Combine(fixture.Parent, "creating")));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "versionstore.lock")));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Empty(File.ReadAllBytes(control));
    }

    [LinuxFact]
    public void LinuxCanonicalManagedComponentAndCaseAliasCannotCoexist() {
        using var fixture = new PlatformFixture();
        string refs = Path.Combine(fixture.Parent, "refs");
        Directory.CreateDirectory(refs);
        Directory.CreateDirectory(Path.Combine(fixture.Parent, "Refs"));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireDirectory(refs));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.CreateDirectory(refs));

        string control = Path.Combine(fixture.Parent, "versionstore.lock");
        string alias = Path.Combine(fixture.Parent, "VersionStore.lock");
        File.WriteAllBytes(control, []);
        File.WriteAllBytes(alias, [0x41]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireFile(control));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Empty(File.ReadAllBytes(control));
        Assert.Equal(new byte[] { 0x41 }, File.ReadAllBytes(alias));
    }

    [WindowsFact]
    public void WindowsCallerRootAndParentSpellingIsNotPersistentCanonicalName() {
        using var fixture = new PlatformFixture();
        string actualRoot = Path.Combine(fixture.Parent, "store");
        Directory.CreateDirectory(actualRoot);
        string callerRoot = Path.Combine(fixture.Parent.ToUpperInvariant(), "STORE");
        Assert.True(VersionStorePlatform.TryAdmitRoot(callerRoot));
        Assert.Equal(callerRoot, VersionStorePlatform.AdmitRoot(callerRoot, createIfMissing: false));
        using (VersionStorePlatform.AcquireOwnerLock(callerRoot, create: true, readOnly: false)) {
            VersionStorePlatform.RequireFile(Path.Combine(callerRoot, "versionstore.lock"));
        }
        string newRoot = Path.Combine(fixture.Parent.ToUpperInvariant(), "new-store");
        Assert.Equal(newRoot, VersionStorePlatform.AdmitRoot(newRoot, createIfMissing: true));
    }

    [Fact]
    public void DirectoryCreationReusesOrdinaryExactDirectoryAndRejectsFile() {
        using var fixture = new PlatformFixture();
        string directory = Path.Combine(fixture.Parent, "creating");
        VersionStorePlatform.CreateDirectory(directory);
        VersionStorePlatform.CreateDirectory(directory);
        VersionStorePlatform.RequireEnumeratedDirectory(directory);
        VersionStorePlatform.RequireSameFileSystem(fixture.Parent, directory);
        string file = Path.Combine(fixture.Parent, "refs");
        File.WriteAllBytes(file, [0x39]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.CreateDirectory(file));
        Assert.Equal(new byte[] { 0x39 }, File.ReadAllBytes(file));
    }

    [Fact]
    public void DirectEntryCheckIncludesHiddenNamesAndRejectsMissing() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, ".hidden");
        File.WriteAllBytes(path, []);
        if (OperatingSystem.IsWindows()) { File.SetAttributes(path, FileAttributes.Hidden); }
        VersionStorePlatform.RequireExactComponent(fixture.Parent, ".hidden");
        VersionStorePlatform.RequireFile(path);
        VersionStorePlatform.RequireEnumeratedFile(path);
        Assert.Throws<FileNotFoundException>(() => VersionStorePlatform.RequireExactComponent(fixture.Parent, "missing"));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void SameProcessOwnerMatrixIsStrict(bool heldReadOnly, bool requestedReadOnly, bool allowed) {
        using var fixture = new PlatformFixture();
        using (VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false)) { }
        using var held = VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: heldReadOnly);
        if (allowed) {
            using var shared = VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: requestedReadOnly);
        }
        else {
            Assert.Throws<IOException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: requestedReadOnly));
        }
    }

    [Fact]
    public void BootstrapIsZeroAndMetadataQualificationWorksWhileWriterHoldsLock() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "versionstore.lock");
        using (VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false)) {
            VersionStorePlatform.RequireFile(path);
            VersionStorePlatform.RequireEnumeratedFile(path);
            Assert.Throws<IOException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        }
        Assert.Empty(File.ReadAllBytes(path));
        using (VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: false)) { }
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void VersionStoreControlIsSeparateFromFrameStoreControl() {
        using var fixture = new PlatformFixture();
        string dataControl = Path.Combine(fixture.Parent, "framestore.lock");
        File.WriteAllBytes(dataControl, [0x42]);
        using (var dataLock = new FileStream(dataControl, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            using var owner = VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false);
            Assert.True(File.Exists(Path.Combine(fixture.Parent, "versionstore.lock")));
        }
        Assert.Equal(new byte[] { 0x42 }, File.ReadAllBytes(dataControl));
        Assert.Empty(File.ReadAllBytes(Path.Combine(fixture.Parent, "versionstore.lock")));
    }

    [Fact]
    public void ReadOnlyControlCannotBootstrapAndDoesNotCreateAnEntry() {
        using var fixture = new PlatformFixture();
        Assert.Throws<ArgumentException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Parent));
    }

    [Fact]
    public void ManagedLeafFileQualifiesAgainstActualRootIdentity() {
        using var fixture = new PlatformFixture();
        string refs = Path.Combine(fixture.Parent, "refs");
        VersionStorePlatform.CreateDirectory(refs);
        string path = Path.Combine(refs, "00000001.rbf");
        File.WriteAllBytes(path, [0x39]);
        VersionStorePlatform.RequireEnumeratedFile(path);
        VersionStorePlatform.RequireFileOnFileSystem(fixture.Parent, path);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireFileOnFileSystem(fixture.Parent, refs));
    }

    [Fact]
    public void OrdinaryAdmissionRejectionDoesNotMarkAnOwnedCleanupFault() {
        using var fixture = new PlatformFixture();
        int cleanupFaults = 0;
        Action mark = () => cleanupFaults++;
        string wrongType = Path.Combine(fixture.Parent, "refs");
        File.WriteAllBytes(wrongType, [0x38]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireDirectory(wrongType, mark));
        Assert.False(VersionStorePlatform.TryRequireFile(Path.Combine(fixture.Parent, "missing"), mark));
        using (VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false, mark)) {
            VersionStorePlatform.RequireFile(Path.Combine(fixture.Parent, "versionstore.lock"), mark);
        }
        Assert.Equal(0, cleanupFaults);
    }

    [Fact]
    public void NecessaryCleanupFailureMarksOwnerAfterClearingSlotAndNeverRetries() {
        var failure = new IOException("metadata close failed");
        var resource = new FailingResource(failure);
        FailingResource? slot = resource;
        int cleanupFaults = 0;
        CleanupErrors errors = default;
        Action mark = () => {
            Assert.Null(slot);
            cleanupFaults++;
        };
        VersionStorePlatform.DisposeOwnedResource(ref slot, ref errors, mark);
        Assert.Same(failure, Assert.Throws<IOException>(() => errors.ThrowIfAny()));
        VersionStorePlatform.DisposeOwnedResource(ref slot, ref errors, mark);
        Assert.Equal(1, resource.DisposeCalls);
        Assert.Equal(1, cleanupFaults);
    }

    [Fact]
    public void CleanupAndMarkerFailuresRetainThePrimaryErrorFirst() {
        var primary = new InvalidDataException("admission failed");
        var cleanup = new IOException("enumerator close failed");
        var marker = new InvalidOperationException("fault marker failed");
        FailingResource? slot = new(cleanup);
        CleanupErrors errors = default;
        errors.Add(primary);
        VersionStorePlatform.DisposeOwnedResource(ref slot, ref errors, () => throw marker);
        var aggregate = Assert.Throws<AggregateException>(() => errors.ThrowIfAny());
        Assert.Equal(new Exception[] { primary, cleanup, marker }, aggregate.InnerExceptions);
        Assert.Null(slot);
    }

    [Fact]
    public void MissingOpenLockDoesNotBootstrapAndNonemptyLockIsNeverTruncated() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "versionstore.lock");
        Assert.Throws<FileNotFoundException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: false));
        Assert.Throws<FileNotFoundException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly: true));
        Assert.False(File.Exists(path));
        File.WriteAllBytes(path, [0x71]);
        foreach (bool readOnly in new[] { false, true }) {
            Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: false, readOnly));
        }
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Equal(new byte[] { 0x71 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void PendingPublicationNeverOverwritesAndPreservesBytes() {
        using var fixture = new PlatformFixture();
        string creating = Path.Combine(fixture.Parent, "creating");
        string refs = Path.Combine(fixture.Parent, "refs");
        VersionStorePlatform.CreateDirectory(creating);
        VersionStorePlatform.CreateDirectory(refs);
        string source = Path.Combine(creating, "pending.rbf");
        string target = Path.Combine(refs, "00000001.rbf");
        File.WriteAllBytes(source, [0x13, 0x27]);
        File.WriteAllBytes(target, [0x45]);
        Assert.Throws<IOException>(() => VersionStorePlatform.MoveFileNoOverwrite(source, target));
        Assert.Equal(new byte[] { 0x13, 0x27 }, File.ReadAllBytes(source));
        Assert.Equal(new byte[] { 0x45 }, File.ReadAllBytes(target));
        VersionStorePlatform.DeleteFile(target);
        VersionStorePlatform.MoveFileNoOverwrite(source, target);
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
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.MoveFileNoOverwrite(source, target));
        Directory.Delete(target);
        File.WriteAllBytes(Path.Combine(fixture.Parent, "Target"), [0x29]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.MoveFileNoOverwrite(source, target));
        Assert.Equal(new byte[] { 0x13 }, File.ReadAllBytes(source));
        Assert.Equal(new byte[] { 0x29 }, File.ReadAllBytes(Path.Combine(fixture.Parent, "Target")));
    }

    [Fact]
    public void PrivateDeleteRejectsMissingAndDirectory() {
        using var fixture = new PlatformFixture();
        string path = Path.Combine(fixture.Parent, "private");
        Assert.Throws<FileNotFoundException>(() => VersionStorePlatform.DeleteFile(path));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.DeleteFile(fixture.Parent));
        File.WriteAllBytes(path, [0x19]);
        VersionStorePlatform.DeleteFile(path);
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
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AdmitRoot(link, createIfMissing: false));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireDirectory(link));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireEnumeratedDirectory(link));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.CreateDirectory(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [LinuxFact]
    public void LinuxFileSymlinkIncludingDanglingLinkIsRejected() {
        using var fixture = new PlatformFixture();
        string target = Path.Combine(fixture.Parent, "target");
        File.WriteAllBytes(target, [0x41]);
        string link = Path.Combine(fixture.Parent, "versionstore.lock");
        File.CreateSymbolicLink(link, target);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireFile(link));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireEnumeratedFile(link));
        Assert.Throws<IOException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.DeleteFile(link));
        File.Delete(target);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireFile(link));
    }

    [LinuxFact]
    public void LinuxFifoIsRejectedWithoutBlockingOrWriting() {
        using var fixture = new PlatformFixture();
        string fifo = Path.Combine(fixture.Parent, "versionstore.lock");
        Assert.Equal(0, Mkfifo(fifo, 0x180));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.TryRequireFile(fifo));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireEnumeratedFile(fifo));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AcquireOwnerLock(fixture.Parent, create: true, readOnly: false));
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.AdmitRoot(fifo, createIfMissing: false));
    }

    [LinuxFact]
    public void LinuxDifferentMountIsRejectedBeforeMutation() {
        using var fixture = new PlatformFixture();
        VersionStorePlatform.RequireDirectory("/proc");
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireSameFileSystem(fixture.Parent, "/proc"));
        VersionStorePlatform.RequireEnumeratedFile("/proc/version");
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.RequireFileOnFileSystem(fixture.Parent, "/proc/version"));
        string source = Path.Combine(fixture.Parent, "source");
        File.WriteAllBytes(source, [0x36]);
        Assert.Throws<InvalidDataException>(() => VersionStorePlatform.MoveFileNoOverwrite(source, "/proc/versionstore-no-overwrite-probe"));
        Assert.Equal(new byte[] { 0x36 }, File.ReadAllBytes(source));
    }

    private sealed class FailingResource(Exception failure) : IDisposable {
        internal int DisposeCalls;
        public void Dispose() {
            DisposeCalls++;
            throw failure;
        }
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
    internal string Parent { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VersionStore-platform-" + Guid.NewGuid().ToString("N")));
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
