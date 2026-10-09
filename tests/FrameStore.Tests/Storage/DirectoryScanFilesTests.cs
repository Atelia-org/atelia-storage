using Atelia.FrameStore.Internal.Format;
using Atelia.FrameStore.Internal.Storage;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Storage;

public sealed class DirectoryScanFilesTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FrameStore-scan-files-" + Guid.NewGuid().ToString("N"));

    private string ActivePath(uint id) => Path.Combine(_root, FrameStorePaths.GetActiveRelativePath(id));
    private string ArchivePath(uint id) => Path.Combine(_root, FrameStorePaths.GetArchiveRelativePath(id));
    private string PrivatePath(uint id) => Path.Combine(_root, DirectoryFrameStoreFiles.CreatingDirectoryName, FrameStorePaths.GetFileName(id));

    private DirectoryFrameStoreFiles CreateEmpty(out StoreIdentity identity) {
        using var store = FrameStore.Create(_root);
        Assert.True(StoreIdentity.TryRead(store.StoreId, out identity));
        return new DirectoryFrameStoreFiles(_root, identity);
    }

    private static void InitializeFile(string path, StoreIdentity identity, uint id) {
        using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
        byte[] header = new byte[FileHeaderCodec.PayloadSize];
        Assert.True(FileHeaderCodec.TryWrite(identity, id, header));
        file.Append(0, header).Unwrap();
        file.DurableFlush();
    }

    private void AddUnqualifiedArchive(uint id) {
        string path = ArchivePath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Name discovery has no content/identity qualification or tail-recovery authority.
        File.WriteAllBytes(path, [0x7f]);
    }

    [Fact]
    public void EmptyScanVisitsNoFileAndStillChecksItsCheckpoint() {
        var files = CreateEmpty(out _);
        int visits = 0;
        int checkpoints = 0;
        bool faulted = false;
        Assert.Null(files.VisitScanFiles((_, _) => { visits++; return null; },
            () => checkpoints++, () => faulted = true));
        Assert.Equal(0, visits);
        Assert.True(checkpoints > 0);
        Assert.False(faulted);
    }

    [Fact]
    public void ScanDiscoversActualFormalFilesAndUsesArchiveMaxToQualifyPreservedPrivatePrefix() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(4), identity, 4);
        InitializeFile(ActivePath(1), identity, 1);
        AddUnqualifiedArchive(0x89abcdef);
        Directory.CreateDirectory(Path.Combine(_root, "archive", "3fffff"));
        InitializeFile(PrivatePath(0x89abcdf0), identity, 0x89abcdf0);
        byte[] complete = File.ReadAllBytes(PrivatePath(0x89abcdf0));
        // Exercise empty, fence, partial, and complete initialization without strict RO RBF open.
        foreach (int length in new[] { 0, 1, 4, complete.Length / 2, complete.Length }) {
            byte[] prefix = complete[..length];
            File.WriteAllBytes(PrivatePath(0x89abcdf0), prefix);
            var visited = new List<(uint Id, bool Archived)>();
            bool faulted = false;
            Assert.Null(files.VisitScanFiles((id, archived) => {
                visited.Add((id, archived));
                return null;
            }, static () => { }, () => faulted = true));
            Assert.Equal(new[] { (1u, false), (4u, false), (0x89abcdefu, true) }, visited.OrderBy(item => item.Id).ToArray());
            Assert.Equal(prefix, File.ReadAllBytes(PrivatePath(0x89abcdf0)));
            Assert.Equal(new byte[] { 0x7f }, File.ReadAllBytes(ArchivePath(0x89abcdef)));
            Assert.False(faulted);
        }
    }

    [Theory]
    [InlineData("active", "00000000.rbf")]
    [InlineData("active", "00000001.RBF")]
    [InlineData("active", "00000001.rbf.bak")]
    [InlineData("active", ".hidden")]
    [InlineData("archive", "400000")]
    [InlineData("archive", "FFFFFF")]
    public void ScanAndFactoryDiscoveryRejectTheSameUnexpectedFormalNamesBeforeFileCallbacks(string directory, string name) {
        var files = CreateEmpty(out _);
        string path = Path.Combine(_root, directory, name);
        File.WriteAllBytes(path, []);
        int visits = 0;
        bool faulted = false;
        Assert.Throws<InvalidDataException>(() => files.DiscoverFormalFiles());
        Assert.Throws<InvalidDataException>(() => files.VisitScanFiles((_, _) => { visits++; return null; },
            static () => { }, () => faulted = true));
        Assert.Equal(0, visits);
        Assert.False(faulted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DuplicateFormalPositionsAndWrongBucketRejectBeforeAnyFileCallback() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(1), identity, 1);
        AddUnqualifiedArchive(1);
        int visits = 0;
        bool faulted = false;
        Assert.Throws<InvalidDataException>(() => files.VisitScanFiles((_, _) => { visits++; return null; },
            static () => { }, () => faulted = true));
        File.Delete(ArchivePath(1));
        File.WriteAllBytes(Path.Combine(_root, "archive", "000000", "00000400.rbf"), []);
        Assert.Throws<InvalidDataException>(() => files.VisitScanFiles((_, _) => { visits++; return null; },
            static () => { }, () => faulted = true));
        Assert.Equal(0, visits);
        Assert.False(faulted);
    }

    [Fact]
    public void PrivateWrongIdUnknownEntryAndOversizedContentsRemainAndRejectBeforeCallbacks() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(1), identity, 1);
        string wrongId = PrivatePath(3);
        File.WriteAllBytes(wrongId, []);
        int visits = 0;
        bool faulted = false;
        void CheckRejected() {
            Assert.Throws<InvalidDataException>(() => files.VisitScanFiles((_, _) => { visits++; return null; },
                static () => { }, () => faulted = true));
        }
        CheckRejected();
        Assert.True(File.Exists(wrongId));
        File.Delete(wrongId);
        string unknown = Path.Combine(_root, "creating", ".hidden");
        File.WriteAllBytes(unknown, []);
        CheckRejected();
        Assert.True(File.Exists(unknown));
        File.Delete(unknown);
        byte[] oversized = new byte[checked((int)FileHeaderCodec.InitializationBoundary + 1)];
        File.WriteAllBytes(PrivatePath(2), oversized);
        CheckRejected();
        Assert.Equal(oversized, File.ReadAllBytes(PrivatePath(2)));
        Assert.Equal(0, visits);
        Assert.False(faulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileFailureStopsImmediatelyAndReturnsTheSameError(bool archived) {
        var files = CreateEmpty(out var identity);
        if (archived) {
            AddUnqualifiedArchive(1);
            AddUnqualifiedArchive(2);
        }
        else {
            InitializeFile(ActivePath(1), identity, 1);
            InitializeFile(ActivePath(2), identity, 2);
        }
        var failure = new ScanTestError();
        int visits = 0;
        bool faulted = false;
        Assert.Same(failure, files.VisitScanFiles((_, location) => {
            Assert.Equal(archived, location);
            visits++;
            return failure;
        }, static () => { }, () => faulted = true));
        Assert.Equal(1, visits);
        Assert.False(faulted);
    }

    [Fact]
    public void FileVisitorExceptionPropagatesWithoutFaultAndTheNextScanStillWorks() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(1), identity, 1);
        var failure = new InvalidOperationException("visitor failed");
        bool faulted = false;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            files.VisitScanFiles((_, _) => throw failure, static () => { }, () => faulted = true)));
        Assert.False(faulted);
        int visits = 0;
        Assert.Null(files.VisitScanFiles((_, _) => { visits++; return null; }, static () => { }, () => faulted = true));
        Assert.Equal(1, visits);
        Assert.False(faulted);
    }

    [Fact]
    public void CancellationAtEveryCheckpointClosesPrivateReaderAndPreservesAllContents() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(1), identity, 1);
        AddUnqualifiedArchive(4);
        InitializeFile(PrivatePath(5), identity, 5);
        byte[] privateBytes = File.ReadAllBytes(PrivatePath(5));
        int totalCheckpoints = 0;
        bool faulted = false;
        Assert.Null(files.VisitScanFiles(static (_, _) => null, () => totalCheckpoints++, () => faulted = true));
        for (int stop = 1; stop <= totalCheckpoints; stop++) {
            int observed = 0;
            var failure = new OperationCanceledException("checkpoint cancelled");
            Assert.Same(failure, Assert.Throws<OperationCanceledException>(() =>
                files.VisitScanFiles(static (_, _) => null, () => {
                    if (++observed == stop) { throw failure; }
                }, () => faulted = true)));
            Assert.Equal(stop, observed);
            Assert.False(faulted);
            // The temporary private qualification stream has been closed even for cancellation after open/read.
            using (new FileStream(PrivatePath(5), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Equal(privateBytes, File.ReadAllBytes(PrivatePath(5)));
            Assert.Equal(new byte[] { 0x7f }, File.ReadAllBytes(ArchivePath(4)));
        }
    }

    [Fact]
    public void CheckpointAfterTheLastFileVisitorRejectsCancellation() {
        var files = CreateEmpty(out var identity);
        InitializeFile(ActivePath(1), identity, 1);
        using var cancellation = new CancellationTokenSource();
        int visits = 0;
        bool faulted = false;
        Assert.Throws<OperationCanceledException>(() => files.VisitScanFiles((_, _) => {
            visits++;
            cancellation.Cancel();
            return null;
        }, () => cancellation.Token.ThrowIfCancellationRequested(), () => faulted = true));
        Assert.Equal(1, visits);
        Assert.False(faulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanOpenTransfersReadOnlyFileBeforeSeparateHeaderQualification(bool archived) {
        var files = CreateEmpty(out var identity);
        string path = archived ? ArchivePath(1) : ActivePath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A valid RBF3 file with the wrong per-file identity proves open has not performed header qualification.
        InitializeFile(path, identity, 2);
        byte[] before = File.ReadAllBytes(path);
        using (var file = files.OpenScanFile(1, archived)) {
            var header = files.CheckScanHeader(file, 1);
            Assert.True(header.IsFailure);
            Assert.Equal("FrameStore.InvalidHeader", header.Error!.ErrorCode);
            Assert.Equal(FileHeaderCodec.InitializationBoundary, file.TailOffset);
            Assert.Throws<InvalidOperationException>(() => file.Append(3, new byte[] { 1 }));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanOpenRejectsIncompleteTailWithoutRepairingIt(bool archived) {
        var files = CreateEmpty(out var identity);
        string path = archived ? ArchivePath(1) : ActivePath(1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        InitializeFile(path, identity, 1);
        using (var file = RbfFile.OpenExisting(path, out _, RbfCacheMode.Off)) {
            file.Append(3, new byte[] { 1, 2, 3 }).Unwrap();
            file.DurableFlush();
        }
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(stream.Length - 1);
        }
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => files.OpenScanFile(1, archived));
        Assert.Equal(before, File.ReadAllBytes(path));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    private sealed record ScanTestError() : AteliaError("Tests.ScanStopped", "Stop at the first visited file.");

    public void Dispose() {
        if (!Directory.Exists(_root)) { return; }
        // Each test owns its generated TEMP root; cleanup never follows a recursive shell command.
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
