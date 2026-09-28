using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class RefCacheTests : IDisposable {
    private readonly List<string> _paths = new();
    private string NewPath() {
        string path = Path.Combine(Path.GetTempPath(), "ej-ref-cache-" + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }
    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
    }

    [Fact]
    public void Options_DefaultAndNegativeCapacity() {
        Assert.Equal(32, new EventJournalOptions().RefStoreCacheCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => EventJournal.CreateNew(NewPath(), new EventJournalOptions { RefStoreCacheCapacity = -1 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    public void EntriesStayBoundedAndColdReadsKeepAuthoritativeHead(int capacity) {
        string path = NewPath();
        var options = new EventJournalOptions { RefStoreCacheCapacity = capacity };
        using var journal = EventJournal.CreateNew(path, options);
        var root = journal.AppendEventFrame(null, "root"u8).Unwrap();
        var refs = new List<RefId>();
        for (int i = 0; i < 35; i++) {
            refs.Add(journal.CreateBranch($"branch-{i}", root).Unwrap());
            Assert.Equal(Math.Min(i + 1, capacity), journal.RetainedRefEntryCount);
        }
        foreach (RefId id in refs) {
            Assert.Equal(root, journal.GetHead(id));
            Assert.True(journal.RetainedRefEntryCount <= capacity);
            Assert.Single(journal.ReadReflog(id).Unwrap());
        }
        var child = journal.CommitToRef(refs[0], root, "child"u8).Unwrap().EventAddress;
        journal.GetHead(refs[1]);
        Assert.Equal(child, journal.GetHead(refs[0]));
        Assert.Equal(2, journal.ReadReflog(refs[0]).Unwrap().Count);
        if (capacity == 0) {
            Assert.Equal(0, journal.RetainedRefEntryCount);
            using var store = RefMoveStore.OpenExisting(Path.Combine(path, "refs", "objects"), refs[0], options.RefSegmentStoreOptions);
            Assert.Equal(child, store.ReadEndpoints().Unwrap().Last.NewTarget);
        }
    }

    [Fact]
    public void LruReusesStoreAndEvictionReleasesExclusiveHandle() {
        string path = NewPath();
        var options = new EventJournalOptions { RefStoreCacheCapacity = 2 };
        using var journal = EventJournal.CreateNew(path, options);
        var a = journal.CreateBranch("a", null).Unwrap();
        var b = journal.CreateBranch("b", null).Unwrap();
        var storeA = journal.PeekRefStore(a)!;
        var storeB = journal.PeekRefStore(b)!;
        journal.GetHead(a);
        Assert.Single(journal.ReadReflog(a).Unwrap());
        journal.CommitToRef(a, null, "a"u8).Unwrap();
        Assert.Same(storeA, journal.PeekRefStore(a));
        journal.CreateBranch("c", null).Unwrap();
        Assert.True(storeB.IsDisposed);
        Assert.False(storeA.IsDisposed);
        using var reopened = RefMoveStore.OpenExisting(Path.Combine(path, "refs", "objects"), b, options.RefSegmentStoreOptions);
        Assert.True(reopened.ReadEndpoints().IsSuccess);
    }

    [Fact]
    public void ArchiveRemovesEntryAndRecreatedNameGetsNewIdentity() {
        using var journal = EventJournal.CreateNew(NewPath(), new EventJournalOptions { RefStoreCacheCapacity = 1 });
        var original = journal.CreateBranch("branch", null).Unwrap();
        var store = journal.PeekRefStore(original)!;
        journal.ArchiveRef(original, null).Unwrap();
        Assert.True(store.IsDisposed);
        Assert.Equal(0, journal.RetainedRefEntryCount);
        var recreated = journal.CreateBranch("branch", null).Unwrap();
        Assert.NotEqual(original, recreated);
        Assert.Null(journal.GetHead(recreated));
        Assert.ThrowsAny<Exception>(() => journal.GetHead(original));
        Assert.Equal(2, journal.ReadReflog(original).Unwrap().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateAndForkFailureReleaseUninstalledStore(bool fork) {
        string path = NewPath();
        var options = new EventJournalOptions { RefStoreCacheCapacity = 1 };
        using var journal = EventJournal.CreateNew(path, options);
        var root = journal.AppendEventFrame(null, "root"u8).Unwrap();
        var source = journal.CreateBranch("source", root).Unwrap();
        journal.OperationProbe = phase => { if (phase == "RefBeforeInstall") { throw new IOException("injected install failure"); } };
        Assert.Throws<IOException>(() => {
            if (fork) { journal.ForkBranch("new", source, root); }
            else { journal.CreateBranch("new", root); }
        });
        string objects = Path.Combine(path, "refs", "objects");
        var newId = Directory.GetDirectories(objects).Select(directory => RefId.ParseHex(Path.GetFileName(directory)).Unwrap()).Single(id => id != source);
        using var reopened = RefMoveStore.OpenExisting(objects, newId, options.RefSegmentStoreOptions);
        Assert.Equal(root, reopened.ReadEndpoints().Unwrap().Last.NewTarget);
        Assert.Null(journal.PeekRefStore(newId));
    }

    [Fact]
    public void DisposeAttemptsRemainingResourcesAfterOneEntryThrows() {
        string path = NewPath();
        var options = new EventJournalOptions { RefStoreCacheCapacity = 2 };
        var journal = EventJournal.CreateNew(path, options);
        var a = journal.CreateBranch("a", null).Unwrap();
        var b = journal.CreateBranch("b", null).Unwrap();
        var storeA = journal.PeekRefStore(a)!;
        var storeB = journal.PeekRefStore(b)!;
        storeA.DisposeProbe = () => throw new IOException("injected disposal failure");
        var error = Assert.Throws<AggregateException>(() => journal.Dispose());
        Assert.Single(error.InnerExceptions);
        Assert.True(storeA.IsDisposed);
        Assert.True(storeB.IsDisposed);
        Assert.Equal(0, journal.RetainedRefEntryCount);
        journal.Dispose();
        using var reopened = EventJournal.OpenExisting(path, options);
        Assert.Null(reopened.GetHead(a));
        Assert.Null(reopened.GetHead(b));
    }

    [Fact]
    public void FaultGuardPreservesDisposableResourceOwnership() {
        string path = NewPath();
        var options = new EventJournalOptions { RefStoreCacheCapacity = 2 };
        var journal = EventJournal.CreateNew(path, options);
        var a = journal.CreateBranch("a", null).Unwrap();
        var b = journal.CreateBranch("b", null).Unwrap();
        var stores = new[] { journal.PeekRefStore(a)!, journal.PeekRefStore(b)! };
        journal.OperationProbe = phase => { if (phase == "RefMoveAfterDurableFlush") { throw new IOException("injected"); } };
        Assert.Throws<IOException>(() => journal.CommitToRef(a, null, "a"u8));
        Assert.Throws<InvalidOperationException>(() => journal.GetHead(a));
        journal.Dispose();
        Assert.All(stores, store => Assert.True(store.IsDisposed));
        Assert.Equal(0, journal.RetainedRefEntryCount);
        Assert.Throws<ObjectDisposedException>(() => journal.GetHead(a));
        using var reopened = RefMoveStore.OpenExisting(Path.Combine(path, "refs", "objects"), a, options.RefSegmentStoreOptions);
        Assert.True(reopened.ReadEndpoints().IsSuccess);
    }
}
