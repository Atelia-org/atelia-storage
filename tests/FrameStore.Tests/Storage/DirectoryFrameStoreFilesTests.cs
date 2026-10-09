using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Storage;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Storage;

public sealed class DirectoryFrameStoreFilesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrameStore-storage-" + Guid.NewGuid().ToString("N"));

    private string ActivePath(uint id) => Path.Combine(_root, FrameStorePaths.GetActiveRelativePath(id));
    private string ArchivePath(uint id) => Path.Combine(_root, FrameStorePaths.GetArchiveRelativePath(id));
    private string PrivatePath(uint id) => Path.Combine(_root, DirectoryFrameStoreFiles.CreatingDirectoryName, FrameStorePaths.GetFileName(id));

    private StoreIdentity CreateEmpty() {
        using (var store = FrameStore.Create(_root)) {
            Assert.True(StoreIdentity.TryRead(store.StoreId, out var identity));
            return identity;
        }
    }

    private static byte[] Header(StoreIdentity identity, uint id) {
        byte[] header = new byte[FileHeaderCodec.PayloadSize];
        Assert.True(FileHeaderCodec.TryWrite(identity, id, header));
        return header;
    }

    private static void InitializeFile(string path, StoreIdentity identity, uint id) {
        using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
        file.Append(0, Header(identity, id)).Unwrap();
        Assert.Equal(FileHeaderCodec.InitializationBoundary, file.TailOffset);
        file.DurableFlush();
    }

    [Fact]
    public void EmptyCreateHasOnlyControlAndNecessaryEmptyDirectories() {
        var identity = CreateEmpty();
        Assert.Equal(identity, DirectoryFrameStoreFiles.ReadGateAndCheckLayout(_root));
        var files = new DirectoryFrameStoreFiles(_root, identity);
        var discovery = files.DiscoverFormalFiles();
        Assert.Equal(0u, discovery.MaxPublishedFileId);
        Assert.Empty(discovery.ActiveFileIds);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "creating")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "archive")));
        using var reopened = FrameStore.Open(_root);
        Assert.Empty(reopened.RecoveryReports);
        reopened.ConfirmDurable();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "active")));
    }

    [Fact]
    public void FormalDiscoverySortsActiveAndCountsArchiveHighIdsWithoutOpeningContents() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(4), identity, 4);
        InitializeFile(ActivePath(1), identity, 1);
        string archive = ArchivePath(0x89abcdef);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        // Archive content intentionally has no RBF qualification; normal name discovery remains lazy.
        File.WriteAllBytes(archive, [0x7f]);
        Directory.CreateDirectory(Path.Combine(_root, "archive", "3fffff"));
        var discovery = new DirectoryFrameStoreFiles(_root, identity).DiscoverFormalFiles();
        Assert.Equal(new uint[] { 1, 4 }, discovery.ActiveFileIds);
        Assert.Equal(0x89abcdefu, discovery.MaxPublishedFileId);
        using (var reopened = FrameStore.Open(_root)) {
            Assert.Equal(2, reopened.RecoveryReports.Count);
        }
        using var readOnly = FrameStore.OpenReadOnly(_root);
    }

    [Fact]
    public void FormalDiscoveryRejectsDuplicatePositionsBeforeRecoveryOrPrivateCancellation() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(1), identity, 1);
        string archive = ArchivePath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.Copy(ActivePath(1), archive);
        File.WriteAllBytes(PrivatePath(2), []);
        byte[] active = File.ReadAllBytes(ActivePath(1));
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Equal(active, File.ReadAllBytes(ActivePath(1)));
        Assert.True(File.Exists(PrivatePath(2)));
    }

    [Theory]
    [InlineData("active", "00000000.rbf")]
    [InlineData("active", "00000001.RBF")]
    [InlineData("active", "00000001.rbf.bak")]
    [InlineData("active", ".hidden")]
    [InlineData("archive", "400000")]
    [InlineData("archive", "FFFFFF")]
    [InlineData("creating", ".hidden")]
    public void UnknownAndNoncanonicalManagedEntriesRejectBothModesAndRemain(string directory, string name) {
        CreateEmpty();
        string path = Path.Combine(_root, directory, name);
        File.WriteAllBytes(path, []);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Throws<InvalidDataException>(() => FrameStore.OpenReadOnly(_root));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CanonicalNamesDoNotTurnDirectoriesIntoFilesOrFilesIntoBuckets() {
        var identity = CreateEmpty();
        string active = ActivePath(1);
        Directory.CreateDirectory(active);
        Assert.Throws<InvalidDataException>(() => new DirectoryFrameStoreFiles(_root, identity).DiscoverFormalFiles());
        Directory.Delete(active);
        string bucket = Path.Combine(_root, "archive", "000000");
        File.WriteAllBytes(bucket, []);
        Assert.Throws<InvalidDataException>(() => FrameStore.OpenReadOnly(_root));
    }

    [Fact]
    public void WrongArchiveBucketRejectsRatherThanIncreasingMax() {
        CreateEmpty();
        string bucket = Path.Combine(_root, "archive", "000000");
        Directory.CreateDirectory(bucket);
        File.WriteAllBytes(Path.Combine(bucket, "00000400.rbf"), []);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
    }

    [Fact]
    public void AllActualHeaderPrefixesAreRetainedByReadOnlyAndCancelledByWritableOpen() {
        var identity = CreateEmpty();
        string privatePath = PrivatePath(1);
        InitializeFile(privatePath, identity, 1);
        byte[] complete = File.ReadAllBytes(privatePath);
        Assert.Equal(FileHeaderCodec.InitializationBoundary, complete.Length);
        for (int length = 0; length <= complete.Length; length++) {
            byte[] prefix = complete[..length];
            File.WriteAllBytes(privatePath, prefix);
            using (var reader = FrameStore.OpenReadOnly(_root)) {
                Assert.Equal(prefix, File.ReadAllBytes(privatePath));
                Assert.Empty(reader.RecoveryReports);
            }
            using (var writer = FrameStore.Open(_root)) {
                Assert.False(File.Exists(privatePath));
                Assert.Empty(writer.RecoveryReports);
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "active")));
            }
        }
        using var store = FrameStore.Open(_root);
        var address = store.Append(3, new byte[] { 1 }).Unwrap();
        Assert.Equal(1u, address.FileId);
    }

    [Fact]
    public void PrivateWrongIdentityAndSuffixRemainRejectedAcrossRepeatedOpens() {
        var identity = CreateEmpty();
        byte[] otherBytes = new byte[StoreIdentity.EncodedSize];
        Assert.True(identity.TryWrite(otherBytes));
        otherBytes[0] ^= 0x80;
        if (otherBytes.All(value => value == 0)) { otherBytes[1] = 1; }
        Assert.True(StoreIdentity.TryRead(otherBytes, out var other));
        Assert.NotEqual(identity, other);
        InitializeFile(PrivatePath(1), other, 1);
        byte[] bad = File.ReadAllBytes(PrivatePath(1));
        for (int attempt = 0; attempt < 2; attempt++) {
            Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
            Assert.Throws<InvalidDataException>(() => FrameStore.OpenReadOnly(_root));
            Assert.Equal(bad, File.ReadAllBytes(PrivatePath(1)));
        }
        File.Delete(PrivatePath(1));
        InitializeFile(PrivatePath(1), identity, 1);
        using (var stream = new FileStream(PrivatePath(1), FileMode.Append, FileAccess.Write)) { stream.WriteByte(1); }
        bad = File.ReadAllBytes(PrivatePath(1));
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Equal(bad, File.ReadAllBytes(PrivatePath(1)));
    }

    [Fact]
    public void PrivateWrongNumberAndMultipleItemsRejectBeforeAnyActiveRecovery() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(1), identity, 1);
        using (var stream = new FileStream(ActivePath(1), FileMode.Append, FileAccess.Write)) { stream.WriteByte(1); }
        byte[] unrecovered = File.ReadAllBytes(ActivePath(1));
        File.WriteAllBytes(PrivatePath(3), []);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Equal(unrecovered, File.ReadAllBytes(ActivePath(1)));
        File.Delete(PrivatePath(3));
        File.WriteAllBytes(PrivatePath(2), []);
        File.WriteAllBytes(PrivatePath(3), []);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Equal(unrecovered, File.ReadAllBytes(ActivePath(1)));
        Assert.True(File.Exists(PrivatePath(2)));
        Assert.True(File.Exists(PrivatePath(3)));
    }

    [Fact]
    public void FormalHeaderPrefixesAreRejectedBeforeRbfCanRepairThem() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(1), identity, 1);
        byte[] complete = File.ReadAllBytes(ActivePath(1));
        for (int length = 0; length < complete.Length; length++) {
            byte[] prefix = complete[..length];
            File.WriteAllBytes(ActivePath(1), prefix);
            Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
            Assert.Equal(prefix, File.ReadAllBytes(ActivePath(1)));
            Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
            Assert.Equal(prefix, File.ReadAllBytes(ActivePath(1)));
        }
    }

    [Fact]
    public void MissingLayoutIsRejectedAndNeverRecreated() {
        CreateEmpty();
        Directory.Delete(Path.Combine(_root, "creating"));
        Assert.Throws<DirectoryNotFoundException>(() => FrameStore.Open(_root));
        Assert.Throws<DirectoryNotFoundException>(() => FrameStore.OpenReadOnly(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "creating")));
    }

    [Fact]
    public void BadGatePreventsPrivateCancellationAndActiveRecovery() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(1), identity, 1);
        using (var stream = new FileStream(ActivePath(1), FileMode.Append, FileAccess.Write)) { stream.WriteByte(1); }
        byte[] active = File.ReadAllBytes(ActivePath(1));
        File.WriteAllBytes(PrivatePath(2), []);
        File.WriteAllBytes(Path.Combine(_root, DirectoryFrameStoreFiles.GateFileName), new byte[24]);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Equal(active, File.ReadAllBytes(ActivePath(1)));
        Assert.True(File.Exists(PrivatePath(2)));
    }

    [Fact]
    public void ExhaustedMaxAcceptsEmptyPrivateSlotAndRetainedLowActiveButRejectsPrivateFile() {
        var identity = CreateEmpty();
        InitializeFile(ActivePath(1), identity, 1);
        string last = ArchivePath(uint.MaxValue);
        Directory.CreateDirectory(Path.GetDirectoryName(last)!);
        File.WriteAllBytes(last, []);
        using (var store = FrameStore.Open(_root)) {
            Assert.Equal(1u, store.Append(0, new byte[] { 9 }).Unwrap().FileId);
        }
        File.WriteAllBytes(PrivatePath(2), []);
        Assert.Throws<InvalidDataException>(() => FrameStore.Open(_root));
        Assert.Throws<InvalidDataException>(() => FrameStore.OpenReadOnly(_root));
        Assert.True(File.Exists(PrivatePath(2)));
    }

    [Fact]
    public void ArchivePublishConflictPreservesBothFilesAndPrivateCreationConflictPreservesPrivate() {
        var identity = CreateEmpty();
        var files = new DirectoryFrameStoreFiles(_root, identity);
        using (files.CreateActive(1)) { }
        string archived = ArchivePath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.WriteAllBytes(archived, [7]);
        Assert.Throws<IOException>(() => files.ArchiveClosed(1));
        Assert.True(File.Exists(ActivePath(1)));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(archived));
        File.WriteAllBytes(PrivatePath(2), [11]);
        Assert.Throws<IOException>(() => files.CreateActive(2));
        Assert.Equal(new byte[] { 11 }, File.ReadAllBytes(PrivatePath(2)));
    }

    [Fact]
    public void ArchiveRandomReadClosesTemporaryFileBeforeTransferringOwnedBuffer() {
        var identity = CreateEmpty();
        var files = new DirectoryFrameStoreFiles(_root, identity);
        FrameAddress address;
        using (var file = files.CreateActive(1)) {
            address = FrameAddress.Create(1, file.Append(13, new byte[] { 8, 9 }, new byte[] { 10 }).Unwrap());
            file.DurableFlush();
        }
        files.ArchiveClosed(1);
        bool faulted = false;
        using var read = files.ReadUnowned(address, () => faulted = true).Unwrap();
        Assert.False(faulted);
        Assert.Equal(address, read.Address);
        Assert.Equal(13u, read.Tag);
        Assert.Equal(1, read.TailMetaLength);
        // An exclusive raw handle proves the temporary reader was released before the successful return.
        using (new FileStream(ArchivePath(1), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(new byte[] { 8, 9, 10 }, read.PayloadAndMeta.ToArray());
    }

    public void Dispose() {
        if (!Directory.Exists(_root)) { return; }
        // Each test owns this unique TEMP root. Remove only its enumerated children, without recursive delete.
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(_root);
        while (pending.TryPop(out var directory)) {
            directories.Add(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); }
                else { File.Delete(path); }
            }
        }
        for (int i = directories.Count - 1; i >= 0; i--) { Directory.Delete(directories[i]); }
    }
}
