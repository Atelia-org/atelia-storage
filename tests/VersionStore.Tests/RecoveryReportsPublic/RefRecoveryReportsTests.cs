using Xunit;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.RecoveryReportsPublic;

public sealed class RefRecoveryReportsTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InitialAndLaterCompletedTailsAreReportedOnCurrentOrExactAccess(bool later, bool exact) {
        using var fixture = new RecoveryReportsFixture();
        RefRevision current = fixture.SeedRef(later);
        TailImage image = RecoveryReportsFixture.Damage(fixture.RefPath(current.RefId.LocalId), completeTail: true);
        using (var store = fixture.Open()) {
            IReadOnlyList<VersionStoreRecoveryReport> view = store.RecoveryReports;
            Assert.Empty(view); // Admission discovers paths without opening their RBF contents.
            Assert.Equal(new[] { current.RefId }, store.ListRefs().Unwrap());
            Assert.Empty(view);
            var snapshot = exact ? store.ReadRevision(current).Unwrap() : store.ReadRef(current.RefId).Unwrap();
            Assert.Equal(current, snapshot.Revision);
            image.AssertReport(Assert.Single(view), VersionStoreRecoveryFileKind.Ref, current.RefId.LocalId);
            VersionStoreRecoveryReport saved = view[0];
            Assert.Equal(current, store.ReadRef(current.RefId).Unwrap().Revision);
            Assert.Equal(current, store.ReadRevision(current).Unwrap().Revision);
            Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(current.RefId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            Assert.Empty(store.ListForks(1).Unwrap());
            Assert.Equal(new[] { saved }, view); // Cache and temporary-handle borrowing add nothing.
        }
        image.AssertRestored();
        using var healthy = fixture.Open();
        Assert.Equal(current, healthy.ReadRef(current.RefId).Unwrap().Revision);
        Assert.Empty(healthy.RecoveryReports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialNextHeadIsReportedWithExactDiscardedLengthAndNoTicket(int prefixLength) {
        using var fixture = new RecoveryReportsFixture();
        RefRevision current = fixture.SeedRef(later: true);
        TailImage image = RecoveryReportsFixture.Damage(fixture.RefPath(current.RefId.LocalId), completeTail: false, prefixLength);
        using (var store = fixture.Open()) {
            Assert.Empty(store.RecoveryReports);
            Assert.Equal(current, store.ReadRef(current.RefId).Unwrap().Revision);
            image.AssertReport(Assert.Single(store.RecoveryReports), VersionStoreRecoveryFileKind.Ref, current.RefId.LocalId);
        }
        image.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedHistoryReportsSourceTemporaryAndHealthyReopensPreserveOldAction(bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        RefRevision sourceInitial;
        RefRevision source;
        RefRevision child;
        using (var seed = fixture.Create()) {
            sourceInitial = seed.CreateRef(7, [1], out _).Unwrap();
            source = seed.PublishRef(sourceInitial.RefId, [2], out _).Unwrap();
            child = seed.ForkRef(19, source, out _).Unwrap();
        }
        TailImage image = RecoveryReportsFixture.Damage(fixture.RefPath(source.RefId.LocalId), completeTail);
        using (var store = fixture.Open()) {
            var view = store.RecoveryReports;
            var delivered = new List<RefRevision>();
            Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(child.RefId, RecoveryReportsFixture.Unlimited, snapshot => {
                delivered.Add(snapshot.Revision);
                Assert.Equal(snapshot.Revision.RefId == child.RefId ? 0 : 1, view.Count);
                return true;
            }).Unwrap());
            Assert.Equal(new[] { child, source, sourceInitial }, delivered);
            image.AssertReport(Assert.Single(view), VersionStoreRecoveryFileKind.Ref, source.RefId.LocalId);
            VersionStoreRecoveryReport saved = view[0];
            RecoveryReportsFixture.AssertReleased(image.Path); // The action does not retain the history temporary.
            Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(child.RefId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            RecoveryReportsFixture.AssertReleased(image.Path);
            Assert.Equal(source, store.ReadRef(source.RefId).Unwrap().Revision);
            Assert.Equal(new[] { saved }, view);
        }
        image.AssertRestored();
    }

    [Fact]
    public void ListForksReportsEveryRecoveredTemporaryAndDoesNotTreatHeaderQueryAsPublicationRead() {
        using var fixture = new RecoveryReportsFixture();
        RefRevision source;
        RefRevision child;
        using (var seed = fixture.Create()) {
            source = seed.CreateRef(7, [1], out _).Unwrap();
            child = seed.ForkRef(19, source, out _).Unwrap();
        }
        TailImage sourceImage = RecoveryReportsFixture.Damage(fixture.RefPath(source.RefId.LocalId), completeTail: false);
        TailImage childImage = RecoveryReportsFixture.Damage(fixture.RefPath(child.RefId.LocalId), completeTail: true);
        using (var store = fixture.Open()) {
            var view = store.RecoveryReports;
            Assert.Empty(view);
            ForkInfo edge = Assert.Single(store.ListForks(2).Unwrap());
            Assert.Equal(child.RefId, edge.ChildRefId);
            Assert.Equal(source.RefId, edge.SourceRefId);
            Assert.Equal(source.Ticket, edge.SourceSnapshotTicket);
            Assert.Equal(2, view.Count);
            sourceImage.AssertReport(Assert.Single(view, report => report.LocalId == source.RefId.LocalId), VersionStoreRecoveryFileKind.Ref, source.RefId.LocalId);
            childImage.AssertReport(Assert.Single(view, report => report.LocalId == child.RefId.LocalId), VersionStoreRecoveryFileKind.Ref, child.RefId.LocalId);
            var saved = view.ToArray();
            RecoveryReportsFixture.AssertReleased(sourceImage.Path);
            RecoveryReportsFixture.AssertReleased(childImage.Path);
            Assert.Single(store.ListForks(2).Unwrap());
            Assert.Equal(saved, view);
        }
        sourceImage.AssertRestored();
        childImage.AssertRestored();
    }

    [Theory]
    [InlineData("current", false)]
    [InlineData("current", true)]
    [InlineData("exact", false)]
    [InlineData("exact", true)]
    [InlineData("history", false)]
    [InlineData("history", true)]
    [InlineData("forks", false)]
    [InlineData("forks", true)]
    public void ReadOnlyRefOpenChannelsRejectRecoveryWithoutChangingAnyBytesOrReports(string channel, bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        RefRevision current = fixture.SeedRef(later: true);
        RecoveryReportsFixture.Damage(fixture.RefPath(current.RefId.LocalId), completeTail);
        var before = fixture.Snapshot();
        using var store = fixture.Open(readOnly: true);
        var view = store.RecoveryReports;
        Assert.Empty(view);
        Assert.Throws<InvalidDataException>(() => Access(store, current, channel));
        Assert.Empty(view);
        Assert.Empty(store.RecoveryReports);
        store.EnsureUsable();
        fixture.AssertUnchangedWithHashes(before);
    }

    internal static void Access(Store store, RefRevision revision, string channel) {
        switch (channel) {
            case "current": Assert.Equal(revision, store.ReadRef(revision.RefId).Unwrap().Revision); break;
            case "exact": Assert.Equal(revision, store.ReadRevision(revision).Unwrap().Revision); break;
            case "history":
                Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(revision.RefId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
                break;
            case "forks": store.ListForks(int.MaxValue).Unwrap(); break;
            default: throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}
