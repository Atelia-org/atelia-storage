using System.Collections;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.RecoveryReportsPublic;

public sealed class RecoveryReportsViewTests {
    [Fact]
    public void HealthyCreateAndAllHealthyQueryOpensKeepLedgerEmpty() {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId current;
        const string name = "healthy-recovery-report-tag";
        using (var seed = fixture.Create()) {
            var view = seed.RecoveryReports;
            Assert.Empty(view);
            current = seed.CreateHead(7, [0xff], out _).Unwrap();
            seed.CreateTag(name, [0xfe], out _).Unwrap();
            Assert.Empty(view);
            AssertReadOnlySurface(view);
        }
        foreach (bool readOnly in new[] { false, true }) {
            using var store = fixture.Open(readOnly);
            var view = store.RecoveryReports;
            Assert.Empty(view);
            Assert.Equal(current, store.ReadHead(current.HeadId).Unwrap().Id);
            Assert.Equal(current, store.ReadEntry(current).Unwrap().Id);
            Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(current.HeadId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            Assert.Empty(store.ListHeadOrigins(1).Unwrap());
            Assert.Equal(new byte[] { 0xfe }, store.ResolveTag(name).Unwrap().Bytes.ToArray());
            Assert.Equal(name, store.ReadTagInfo(name).Unwrap().Name);
            Assert.Equal(TagScanEnd.Complete, store.ScanTags(_ => true).Unwrap());
            Assert.True(store.ReadHead(8u).IsFailure);
            Assert.True(store.ResolveTag("missing-recovery-report-tag").IsFailure);
            Assert.Empty(view);
            Assert.Empty(store.RecoveryReports);
        }
    }

    [Fact]
    public void SavedViewGrowsInCallOrderAndFixedIntervalsAndValuesSurviveDisposeAndBorrowedDataClosure() {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId first;
        HeadEntryId second;
        const string name = "mixed-recovery-report-tag";
        using (var seed = fixture.Create()) {
            first = seed.CreateHead(7, [1], out _).Unwrap();
            second = seed.CreateHead(19, [2], out _).Unwrap();
            seed.CreateTag(name, [3], out _).Unwrap();
        }
        TailImage firstImage = RecoveryReportsFixture.Damage(fixture.HeadPath(first.HeadId.LocalId), completeTail: true);
        TailImage secondImage = RecoveryReportsFixture.Damage(fixture.HeadPath(second.HeadId.LocalId), completeTail: false, prefixLength: 3);
        TailImage tagImage = RecoveryReportsFixture.Damage(fixture.OnlyTagPath(), completeTail: true);
        uint bucket = RecoveryReportsFixture.LocalId(tagImage.Path);
        using var store = fixture.Open();
        IReadOnlyList<HeadStoreRecoveryReport> view = store.RecoveryReports;
        Assert.Empty(view);
        AssertReadOnlySurface(view);

        int beforeFirst = view.Count;
        Assert.Equal(first, store.ReadHead(first.HeadId).Unwrap().Id);
        int afterFirst = view.Count;
        Assert.Equal(1, afterFirst - beforeFirst);
        HeadStoreRecoveryReport[] firstInterval = Interval(view, beforeFirst, afterFirst);
        firstImage.AssertReport(Assert.Single(firstInterval), HeadStoreRecoveryFileKind.Head, first.HeadId.LocalId);

        int beforeTag = view.Count;
        Assert.Equal(new byte[] { 3 }, store.ResolveTag(name).Unwrap().Bytes.ToArray());
        int afterTag = view.Count;
        Assert.Equal(1, afterTag - beforeTag);
        tagImage.AssertReport(Assert.Single(Interval(view, beforeTag, afterTag)), HeadStoreRecoveryFileKind.TagBucket, bucket);

        int beforeSecond = view.Count;
        Assert.Equal(second, store.ReadEntry(second).Unwrap().Id);
        int afterSecond = view.Count;
        Assert.Equal(1, afterSecond - beforeSecond);
        secondImage.AssertReport(Assert.Single(Interval(view, beforeSecond, afterSecond)), HeadStoreRecoveryFileKind.Head, second.HeadId.LocalId);
        Assert.Equal(firstInterval, Interval(view, beforeFirst, afterFirst));
        Assert.Equal(new[] { HeadStoreRecoveryFileKind.Head, HeadStoreRecoveryFileKind.TagBucket, HeadStoreRecoveryFileKind.Head },
            view.Select(report => report.FileKind));
        Assert.Equal(new[] { first.HeadId.LocalId, bucket, second.HeadId.LocalId }, view.Select(report => report.LocalId));
        HeadStoreRecoveryReport[] saved = view.ToArray();
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
            Assert.Equal(first, reopened.ReadHead(first.HeadId).Unwrap().Id);
            Assert.Equal(second, reopened.ReadHead(second.HeadId).Unwrap().Id);
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
    public void BorrowedFrameStoreRecoveryDoesNotEnterHeadStoreLedger() {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId current = fixture.SeedHead();
        fixture.Data.Append(65, [0xfd, 7, 8]).Unwrap();
        fixture.Data.ConfirmDurable();
        fixture.Data.Dispose();
        string dataPath = Directory.EnumerateFiles(Path.Combine(fixture.DataRoot, "active"), "*.rbf").Single();
        uint fileId = RecoveryReportsFixture.LocalId(dataPath);
        TailImage dataImage = RecoveryReportsFixture.Damage(dataPath, completeTail: true);
        TailImage headImage = RecoveryReportsFixture.Damage(fixture.HeadPath(current.HeadId.LocalId), completeTail: false);
        using (var data = DataStore.Open(fixture.DataRoot)) {
            var fsReport = Assert.Single(data.RecoveryReports);
            Assert.Equal(fileId, fsReport.Key);
            Assert.Equal(Atelia.Rbf.RbfTailRecoveryAction.CompletedTail, fsReport.Value.Action);
            Assert.Equal((long)dataImage.Damaged.Length, fsReport.Value.OriginalLength);
            Assert.Equal((long)dataImage.Complete.Length, fsReport.Value.FinalLength);
            using (var store = Store.Open(fixture.Root, data)) {
                var view = store.RecoveryReports;
                Assert.Empty(view);
                Assert.Equal(current, store.ReadHead(current.HeadId).Unwrap().Id);
                headImage.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.Head, current.HeadId.LocalId);
                Assert.Equal(fsReport, Assert.Single(data.RecoveryReports));
            }
            data.EnsureUsable();
        }
        dataImage.AssertRestored();
        headImage.AssertRestored();
    }

    private static HeadStoreRecoveryReport[] Interval(IReadOnlyList<HeadStoreRecoveryReport> view, int before, int after) =>
        Enumerable.Range(before, after - before).Select(index => view[index]).ToArray();

    private static void AssertReadOnlySurface(IReadOnlyList<HeadStoreRecoveryReport> view) {
        Assert.False(view is HeadStoreRecoveryReport[]);
        Assert.False(view is IList<HeadStoreRecoveryReport>);
        Assert.False(view is ICollection<HeadStoreRecoveryReport>);
        Assert.False(view is IList);
        Assert.False(view is ICollection); // No ordinary ICollection.SyncRoot route to a mutable backing list.
        Assert.False(view is IDisposable);
        Assert.Equal(view.Count, ((IEnumerable)view).Cast<HeadStoreRecoveryReport>().Count());
    }
}
