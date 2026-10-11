using System.Collections;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.RecoveryReportsPublic;

public sealed class RecoveryReportsViewTests {
    [Fact]
    public void HealthyCreateAndAllHealthyQueryOpensKeepLedgerEmpty() {
        using var fixture = new RecoveryReportsFixture();
        RefRevision current;
        const string name = "healthy-recovery-report-tag";
        using (var seed = fixture.Create()) {
            var view = seed.RecoveryReports;
            Assert.Empty(view);
            current = seed.CreateRef(7, [0xff], out _).Unwrap();
            seed.CreateTag(name, [0xfe], out _).Unwrap();
            Assert.Empty(view);
            AssertReadOnlySurface(view);
        }
        foreach (bool readOnly in new[] { false, true }) {
            using var store = fixture.Open(readOnly);
            var view = store.RecoveryReports;
            Assert.Empty(view);
            Assert.Equal(current, store.ReadRef(current.RefId).Unwrap().Revision);
            Assert.Equal(current, store.ReadRevision(current).Unwrap().Revision);
            Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(current.RefId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            Assert.Empty(store.ListForks(1).Unwrap());
            Assert.Equal(new byte[] { 0xfe }, store.ResolveTag(name).Unwrap().Bytes.ToArray());
            Assert.Equal(name, store.ReadTagInfo(name).Unwrap().Name);
            Assert.Equal(TagScanEnd.Complete, store.ScanTags(_ => true).Unwrap());
            Assert.True(store.ReadRef(8u).IsFailure);
            Assert.True(store.ResolveTag("missing-recovery-report-tag").IsFailure);
            Assert.Empty(view);
            Assert.Empty(store.RecoveryReports);
        }
    }

    [Fact]
    public void SavedViewGrowsInCallOrderAndFixedIntervalsAndValuesSurviveDisposeAndBorrowedDataClosure() {
        using var fixture = new RecoveryReportsFixture();
        RefRevision first;
        RefRevision second;
        const string name = "mixed-recovery-report-tag";
        using (var seed = fixture.Create()) {
            first = seed.CreateRef(7, [1], out _).Unwrap();
            second = seed.CreateRef(19, [2], out _).Unwrap();
            seed.CreateTag(name, [3], out _).Unwrap();
        }
        TailImage firstImage = RecoveryReportsFixture.Damage(fixture.RefPath(first.RefId.LocalId), completeTail: true);
        TailImage secondImage = RecoveryReportsFixture.Damage(fixture.RefPath(second.RefId.LocalId), completeTail: false, prefixLength: 3);
        TailImage tagImage = RecoveryReportsFixture.Damage(fixture.OnlyTagPath(), completeTail: true);
        uint bucket = RecoveryReportsFixture.LocalId(tagImage.Path);
        using var store = fixture.Open();
        IReadOnlyList<VersionStoreRecoveryReport> view = store.RecoveryReports;
        Assert.Empty(view);
        AssertReadOnlySurface(view);

        int beforeFirst = view.Count;
        Assert.Equal(first, store.ReadRef(first.RefId).Unwrap().Revision);
        int afterFirst = view.Count;
        Assert.Equal(1, afterFirst - beforeFirst);
        VersionStoreRecoveryReport[] firstInterval = Interval(view, beforeFirst, afterFirst);
        firstImage.AssertReport(Assert.Single(firstInterval), VersionStoreRecoveryFileKind.Ref, first.RefId.LocalId);

        int beforeTag = view.Count;
        Assert.Equal(new byte[] { 3 }, store.ResolveTag(name).Unwrap().Bytes.ToArray());
        int afterTag = view.Count;
        Assert.Equal(1, afterTag - beforeTag);
        tagImage.AssertReport(Assert.Single(Interval(view, beforeTag, afterTag)), VersionStoreRecoveryFileKind.TagBucket, bucket);

        int beforeSecond = view.Count;
        Assert.Equal(second, store.ReadRevision(second).Unwrap().Revision);
        int afterSecond = view.Count;
        Assert.Equal(1, afterSecond - beforeSecond);
        secondImage.AssertReport(Assert.Single(Interval(view, beforeSecond, afterSecond)), VersionStoreRecoveryFileKind.Ref, second.RefId.LocalId);
        Assert.Equal(firstInterval, Interval(view, beforeFirst, afterFirst));
        Assert.Equal(new[] { VersionStoreRecoveryFileKind.Ref, VersionStoreRecoveryFileKind.TagBucket, VersionStoreRecoveryFileKind.Ref },
            view.Select(report => report.FileKind));
        Assert.Equal(new[] { first.RefId.LocalId, bucket, second.RefId.LocalId }, view.Select(report => report.LocalId));
        VersionStoreRecoveryReport[] saved = view.ToArray();
        AssertReadOnlySurface(view);

        store.Dispose();
        Assert.Equal(saved, store.RecoveryReports);
        Assert.Equal(saved, view);
        Assert.Equal(saved[0], view[0]);
        RecoveryReportsFixture.AssertReleased(firstImage.Path);
        RecoveryReportsFixture.AssertReleased(secondImage.Path);
        RecoveryReportsFixture.AssertReleased(tagImage.Path);
        RecoveryReportsFixture.AssertReleased(Path.Combine(fixture.Root, "versionstore.lock"));
        fixture.Data.Dispose();
        Assert.Equal(saved, store.RecoveryReports); // Neither getter nor view checks borrowed data's lifetime.
        Assert.Equal(saved, view);
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var reopened = Store.Open(fixture.Root, data)) {
            Assert.Equal(first, reopened.ReadRef(first.RefId).Unwrap().Revision);
            Assert.Equal(second, reopened.ReadRef(second.RefId).Unwrap().Revision);
            Assert.Equal(name, reopened.ReadTagInfo(name).Unwrap().Name);
            Assert.Empty(reopened.RecoveryReports);
            Assert.Equal(saved, view);
        }
        GC.KeepAlive(view); // Keep the saved collection rooted throughout actual file and owner reopens.
        firstImage.AssertRestored();
        secondImage.AssertRestored();
        tagImage.AssertRestored();
    }

    [Fact]
    public void BorrowedFrameStoreRecoveryDoesNotEnterVersionStoreLedger() {
        using var fixture = new RecoveryReportsFixture();
        RefRevision current = fixture.SeedRef();
        fixture.Data.Append(65, [0xfd, 7, 8]).Unwrap();
        fixture.Data.ConfirmDurable();
        fixture.Data.Dispose();
        string dataPath = Directory.EnumerateFiles(Path.Combine(fixture.DataRoot, "active"), "*.rbf").Single();
        uint fileId = RecoveryReportsFixture.LocalId(dataPath);
        TailImage dataImage = RecoveryReportsFixture.Damage(dataPath, completeTail: true);
        TailImage refImage = RecoveryReportsFixture.Damage(fixture.RefPath(current.RefId.LocalId), completeTail: false);
        using (var data = DataStore.Open(fixture.DataRoot)) {
            var fsReport = Assert.Single(data.RecoveryReports);
            Assert.Equal(fileId, fsReport.Key);
            Assert.Equal(Atelia.Rbf.RbfTailRecoveryAction.CompletedTail, fsReport.Value.Action);
            Assert.Equal((long)dataImage.Damaged.Length, fsReport.Value.OriginalLength);
            Assert.Equal((long)dataImage.Complete.Length, fsReport.Value.FinalLength);
            using (var store = Store.Open(fixture.Root, data)) {
                var view = store.RecoveryReports;
                Assert.Empty(view);
                Assert.Equal(current, store.ReadRef(current.RefId).Unwrap().Revision);
                refImage.AssertReport(Assert.Single(view), VersionStoreRecoveryFileKind.Ref, current.RefId.LocalId);
                Assert.Equal(fsReport, Assert.Single(data.RecoveryReports));
            }
            data.EnsureUsable();
        }
        dataImage.AssertRestored();
        refImage.AssertRestored();
    }

    private static VersionStoreRecoveryReport[] Interval(IReadOnlyList<VersionStoreRecoveryReport> view, int before, int after) =>
        Enumerable.Range(before, after - before).Select(index => view[index]).ToArray();

    private static void AssertReadOnlySurface(IReadOnlyList<VersionStoreRecoveryReport> view) {
        Assert.False(view is VersionStoreRecoveryReport[]);
        Assert.False(view is IList<VersionStoreRecoveryReport>);
        Assert.False(view is ICollection<VersionStoreRecoveryReport>);
        Assert.False(view is IList);
        Assert.False(view is ICollection); // No ordinary ICollection.SyncRoot route to a mutable backing list.
        Assert.False(view is IDisposable);
        Assert.Equal(view.Count, ((IEnumerable)view).Cast<VersionStoreRecoveryReport>().Count());
    }
}
