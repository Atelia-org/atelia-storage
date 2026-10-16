using System.Runtime.InteropServices;
using Xunit;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

public sealed class PublicPathAdmissionTests {
    [Theory]
    [InlineData("versionstore.format", "VersionStore.format", false)]
    [InlineData("versionstore.lock", "VersionStore.lock", false)]
    [InlineData("refs", "Refs", true)]
    [InlineData("creating", "Creating", true)]
    [InlineData("tags", "Tags", true)]
    public void ManagedComponentsRequireTheirActualExactSpelling(string canonical, string alias, bool directory) {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        if (canonical == "tags") { Directory.CreateDirectory(Path.Combine(fixture.Root, canonical)); }
        string source = Path.Combine(fixture.Root, canonical);
        string temporary = Path.Combine(fixture.Root, "rename-temporary");
        string destination = Path.Combine(fixture.Root, alias);
        if (directory) { Directory.Move(source, temporary); Directory.Move(temporary, destination); }
        else { File.Move(source, temporary); File.Move(temporary, destination); }
        var before = fixture.CaptureFiles();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("7.rbf")]
    [InlineData("00000000.rbf")]
    [InlineData("00000001.RBF")]
    [InlineData("0000000A.rbf")]
    [InlineData("000000001.rbf")]
    [InlineData(".hidden")]
    [InlineData("00000001.rbf.bak")]
    [InlineData("０００００００１.rbf")]
    public void HeadDiscoveryRejectsInvalidActualNamesBeforeCancellingPending(string name) {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        File.WriteAllBytes(Path.Combine(fixture.Root, "refs", name), [0xff]);
        File.WriteAllBytes(fixture.PendingPath, [0x73]);
        var before = fixture.CaptureFiles();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Equal(new byte[] { 0xff }, readOnly.ReadHead(7u).Unwrap().Payload.ToArray());
            Assert.Throws<InvalidDataException>(() => { readOnly.ListHeads(); });
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void DirectReadRejectsCaseAliasEvenWhereFilesystemLookupWouldFindIt() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        string canonical = fixture.HeadPath(7);
        string temporary = Path.Combine(fixture.Root, "refs", "rename-temporary");
        File.Move(canonical, temporary);
        File.Move(temporary, Path.Combine(fixture.Root, "refs", "00000007.RBF"));
        var before = fixture.CaptureFiles();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(7u); });
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("versionstore.format")]
    [InlineData("versionstore.lock")]
    [InlineData("creating/pending.rbf")]
    public void ManagedFileNamesCannotAuthorizeDirectoryTypes(string relative) {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        string path = Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) { File.Delete(path); }
        Directory.CreateDirectory(path);
        var before = fixture.CaptureFiles();
        // Native lock acquisition may reject a directory before handle-type qualification runs.
        if (relative == "versionstore.lock") {
            if (OperatingSystem.IsWindows()) {
                Assert.Throws<UnauthorizedAccessException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
                Assert.Throws<UnauthorizedAccessException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
            }
            else {
                Assert.Throws<IOException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
                Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
            }
        }
        else {
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void CanonicalHeadDirectoryIsRejectedByDiscoveryAndTargetAccess() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        Directory.CreateDirectory(fixture.HeadPath(8));
        var before = fixture.CaptureFiles();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ListHeads(); });
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(8u); });
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void RootDirectoryLinkIsRejectedWithoutFollowingItsTarget() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        string target = Path.Combine(fixture.Sandbox, "root-target");
        Directory.Move(fixture.Root, target);
        PublicHeadStoreFixture.CreateDirectoryLink(fixture.Root, target);
        try {
            byte[] gate = File.ReadAllBytes(Path.Combine(target, "versionstore.format"));
            byte[] reference = File.ReadAllBytes(Path.Combine(target, "refs", "00000007.rbf"));
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
            Assert.Equal(gate, File.ReadAllBytes(Path.Combine(target, "versionstore.format")));
            Assert.Equal(reference, File.ReadAllBytes(Path.Combine(target, "refs", "00000007.rbf")));
        }
        finally { Directory.Delete(fixture.Root); }
    }

    [Theory]
    [InlineData("refs")]
    [InlineData("creating")]
    [InlineData("tags")]
    public void ManagedDirectoryLinksAreRejectedEvenWithOrdinarySameFilesystemTargets(string name) {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        string component = Path.Combine(fixture.Root, name);
        if (name == "tags") { Directory.CreateDirectory(component); }
        string target = Path.Combine(fixture.Sandbox, "target-" + name);
        Directory.Move(component, target);
        PublicHeadStoreFixture.CreateDirectoryLink(component, target);
        try {
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
            Assert.True(Directory.Exists(target));
            fixture.Data.EnsureUsable();
        }
        finally { Directory.Delete(component); }
    }

    [PublicLinuxFact]
    public void FileSymlinkAtNecessaryHeadTargetIsRejectedWithoutFollowingOrRepairingIt() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        string target = Path.Combine(fixture.Sandbox, "ref-target.rbf");
        byte[] original = File.ReadAllBytes(fixture.HeadPath(7));
        File.Move(fixture.HeadPath(7), target);
        File.CreateSymbolicLink(fixture.HeadPath(7), target);
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(7u); });
            Assert.Throws<InvalidDataException>(() => { readOnly.ListHeads(); });
        }
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Equal(original, File.ReadAllBytes(target));
        File.Delete(target); // Dangling managed links must still be rejected, rather than reported missing.
        using var dangling = Store.OpenReadOnly(fixture.Root, fixture.Data);
        Assert.Throws<InvalidDataException>(() => { dangling.ReadHead(7u); });
    }

    [PublicLinuxFact]
    public void PendingFifoIsRejectedWithoutOpeningBlockingOrDeletingIt() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        Assert.Equal(0, Mkfifo(fixture.PendingPath, 0x180));
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        Assert.Contains(fixture.PendingPath, Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "creating")));
    }

    [DllImport("libc", EntryPoint = "mkfifo", ExactSpelling = true, SetLastError = true)]
    private static extern int Mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}

internal sealed class PublicLinuxFactAttribute : FactAttribute {
    public PublicLinuxFactAttribute() {
        if (!OperatingSystem.IsLinux()) { Skip = "Linux file symlink/FIFO public admission vector."; }
    }
}
