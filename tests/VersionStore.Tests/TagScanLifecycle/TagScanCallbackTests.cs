using Atelia.Data;
using Atelia.VersionStore.Tests.TagPublic;
using Xunit;

namespace Atelia.VersionStore.Tests.TagScanLifecycle;

public sealed class TagScanCallbackTests {
    private static readonly HistoryBudget UnlimitedHistory = new(100, 1000);

    [Fact]
    public void CallbackRejectsEveryMutationRecursiveScanAndEnteringHistoryBeforeAnyIo() {
        using var f = new TagScanFaultFixture();
        RefId sourceId = f.SeedRef();
        RefRevision revision = f.Store.ReadRef(sourceId).Unwrap().Revision;
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        int callbacks = 0;

        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => {
            callbacks++;
            int events = f.Events.Count;
            PublicationOutcome outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.CreateRef([2], out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.CreateRef(2, [2], out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.PublishRef(sourceId, [2], out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.ForkRef(revision, out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.ForkRef(2, revision, out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.CreateTag("new", [2], out outcome), outcome);
            outcome = PublicationOutcome.Confirmed;
            AssertGuard(f.Store.CreateTag("new", [2], revision, out outcome), outcome);
            Assert.Equal("VersionStore.TagScanActive", f.Store.ScanTags(_ => true).Error!.ErrorCode);
            Assert.Equal("VersionStore.TagScanActive",
                f.Store.ReadRefHistory(sourceId, UnlimitedHistory, _ => true).Error!.ErrorCode);
            Assert.Equal(events, f.Events.Count);
            return true;
        }).Unwrap());

        Assert.Equal(1, callbacks);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        f.Operations.EntryPaths = null;
        Assert.True(f.Store.PublishRef(sourceId, [2], out var confirmed).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.True(f.Store.CreateTag("new", [2], revision, out confirmed).Unwrap());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackAllowsReadRefInfoResolveAndForkQueryAndBorrowsCheckedCurrentBucket(bool readOnly) {
        using var f = new TagScanFaultFixture(readOnly);
        RefId id = f.SeedRef();
        var seed = f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [0xff, 0]),
            new TagPublicRecord(TagScanFaultFixture.SameBucketName, [3]));
        f.Order(TagScanFaultFixture.Name);
        var retained = new List<TagInfo>();

        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(info => {
            retained.Add(info);
            var scan = Assert.Single(f.Tags.Opened);
            Assert.Equal(0, scan.DisposeCalls);
            int events = f.Events.Count;
            f.Store.EnsureUsable();
            Assert.Equal(events, f.Events.Count);
            var current = f.Store.ReadRef(id).Unwrap();
            Assert.Equal(id, current.Revision.RefId);
            var exact = f.Store.ReadRevision(current.Revision).Unwrap();
            Assert.Equal(current.Revision, exact.Revision);
            Assert.Equal(current.Payload.ToArray(), exact.Payload.ToArray());
            Assert.Equal(info.Name, f.Store.ReadTagInfo(info.Name).Unwrap().Name);
            Assert.Equal(info.Name == TagScanFaultFixture.Name ? new byte[] { 0xff, 0 } : new byte[] { 3 },
                f.Store.ResolveTag(info.Name).Unwrap().Bytes.ToArray());
            Assert.Empty(f.Store.ListForks(1).Unwrap());
            Assert.Single(f.Tags.Opened);
            Assert.Single(scan.Reads, read => !read.Raw && read.Offset == seed.Header.Offset && read.Length == seed.Header.Length);
            Assert.Equal(0, scan.DisposeCalls);
            return true;
        }).Unwrap());

        Assert.Equal(2, retained.Count);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, f.Tags.OpenCalls);
        f.Store.Dispose();
        Assert.Equal(new[] { TagScanFaultFixture.Name, TagScanFaultFixture.SameBucketName }, retained.Select(info => info.Name));
        Assert.All(retained, info => { Assert.Null(info.RelatedRefId); Assert.Null(info.RelatedTicket); });
    }

    [Theory]
    [InlineData("unknown.rbf")]
    [InlineData("subdirectory")]
    public void SameBucketCallbackQueryStillRequiresAllDirectoryPathsBeforeBorrowing(string badName) {
        using var f = new TagScanFaultFixture();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        string good = f.BucketPath(TagScanFaultFixture.Name);
        string bad = Path.Combine(Path.GetDirectoryName(good)!, badName);
        if (badName == "subdirectory") { Directory.CreateDirectory(bad); }
        else { File.WriteAllBytes(bad, [1]); }
        f.Operations.EntryPaths = [good, bad];

        Assert.Equal(TagScanEnd.VisitorStopped, f.Store.ScanTags(info => {
            Assert.Throws<InvalidDataException>(() => f.Store.ReadTagInfo(info.Name));
            Assert.Throws<InvalidDataException>(() => f.Store.ResolveTag(info.Name));
            Assert.Equal(0, Assert.Single(f.Tags.Opened).DisposeCalls);
            return false;
        }).Unwrap());

        Assert.Equal(1, f.Tags.OpenCalls);
        Assert.Equal(1, f.Operations.Enumerators[0].MoveNextCalls); // The scan did not advance to the bad suffix.
        Assert.Equal(2, f.Operations.Enumerators[1].MoveNextCalls); // Named reads still proved the entire path set.
        Assert.Equal(2, f.Operations.Enumerators[2].MoveNextCalls);
        Assert.All(f.Operations.Enumerators, cursor => Assert.Equal(1, cursor.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        if (badName == "subdirectory") { Directory.Delete(bad); }
        else { File.Delete(bad); }
        f.Operations.EntryPaths = [good];
        Assert.Equal(TagScanFaultFixture.Name, f.Store.ReadTagInfo(TagScanFaultFixture.Name).Unwrap().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackQueriesAnotherBucketThroughAnIndependentHandleAndClosesOnlyThatHandle(bool readOnly) {
        using var f = new TagScanFaultFixture(readOnly);
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.SeedBucket(TagScanFaultFixture.OtherBucketName, new TagPublicRecord(TagScanFaultFixture.OtherBucketName, [2]));
        f.Order(TagScanFaultFixture.Name, TagScanFaultFixture.OtherBucketName);

        Assert.Equal(TagScanEnd.VisitorStopped, f.Store.ScanTags(info => {
            Assert.Equal(TagScanFaultFixture.Name, info.Name);
            var scan = Assert.Single(f.Tags.Opened);
            Assert.Equal(TagScanFaultFixture.OtherBucketName, f.Store.ReadTagInfo(TagScanFaultFixture.OtherBucketName).Unwrap().Name);
            Assert.Equal(2, f.Tags.Opened.Count);
            Assert.Equal(1, f.Tags.Opened[1].DisposeCalls);
            Assert.Equal(0, scan.DisposeCalls);
            Assert.Equal(new byte[] { 2 }, f.Store.ResolveTag(TagScanFaultFixture.OtherBucketName).Unwrap().Bytes.ToArray());
            Assert.Equal(3, f.Tags.Opened.Count);
            Assert.Equal(1, f.Tags.Opened[2].DisposeCalls);
            Assert.Equal(0, scan.DisposeCalls);
            return false;
        }).Unwrap());

        Assert.All(f.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryCallbackCanScanUsingIndependentBucketSlotAndBorrowItsActiveRef(bool cancelScan) {
        using var f = new TagScanFaultFixture();
        RefId id = f.SeedRef(twoSnapshots: true);
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        int visits = 0;

        Assert.Equal(HistoryEnd.Complete, f.Store.ReadRefHistory(id, UnlimitedHistory, _ => {
            visits++;
            using var cancellation = new CancellationTokenSource();
            Func<TagInfo, bool> visitor = info => {
                Assert.Equal(TagScanFaultFixture.Name, info.Name);
                Assert.Equal(id, f.Store.ReadRef(id).Unwrap().Revision.RefId);
                Assert.Equal(0, f.Refs.Opened[id.LocalId].DisposeCalls);
                if (cancelScan) { cancellation.Cancel(); }
                return false;
            };
            if (cancelScan) {
                Assert.Equal(cancellation.Token,
                    Assert.Throws<OperationCanceledException>(() => f.Store.ScanTags(visitor, cancellation.Token)).CancellationToken);
            }
            else { Assert.Equal(TagScanEnd.VisitorStopped, f.Store.ScanTags(visitor).Unwrap()); }
            Assert.Equal(0, f.Refs.Opened[id.LocalId].DisposeCalls);
            Assert.All(f.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
            return true;
        }).Unwrap());

        Assert.Equal(2, visits);
        Assert.Equal(1, f.Refs.OpenCalls);
        Assert.Equal(1, f.Refs.Opened[id.LocalId].DisposeCalls);
        Assert.Equal(2, f.Tags.OpenCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        f.Operations.EntryPaths = null;
        Assert.True(f.Store.CreateTag("history-finished", [4], out _).Unwrap());
    }

    [Fact]
    public void RealPublicOwnerClearsScanGuardAfterVisitorThrowsAndRetainedInfoSurvivesClose() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        RefRevision revision = store.CreateRef(1, [1], out _).Unwrap();
        store.CreateTag("A", [2], revision, out _).Unwrap();
        TagInfo? saved = null;
        var primary = new IOException("visitor failure");

        Assert.Same(primary, Assert.Throws<IOException>(() => store.ScanTags(info => { saved = info; throw primary; })));

        Assert.Equal(TagScanEnd.VisitorStopped, store.ScanTags(_ => false).Unwrap());
        Assert.True(store.PublishRef(revision.RefId, [3], out _).IsSuccess);
        Assert.True(store.CreateTag("Release", [4], out _).Unwrap());
        store.Dispose();
        Assert.NotNull(saved);
        Assert.Equal("A", saved.Name);
        Assert.Equal(revision.RefId, saved.RelatedRefId);
        Assert.Equal(revision.Ticket, saved.RelatedTicket);
        fixture.Data.EnsureUsable();
    }

    private static void AssertGuard<T>(AteliaResult<T> result, PublicationOutcome outcome) where T : notnull {
        Assert.Equal("VersionStore.TagScanActive", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
    }
}
