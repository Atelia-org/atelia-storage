using Xunit;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.RecoveryReportsPublic;

public sealed class HeadRecoveryReportsTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InitialAndLaterCompletedTailsAreReportedOnCurrentOrExactAccess(bool later, bool exact) {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId current = fixture.SeedHead(later);
        TailImage image = RecoveryReportsFixture.Damage(fixture.HeadPath(current.HeadId.LocalId), completeTail: true);
        using (var store = fixture.Open()) {
            IReadOnlyList<HeadStoreRecoveryReport> view = store.RecoveryReports;
            Assert.Empty(view); // Admission discovers paths without opening their RBF contents.
            Assert.Equal(new[] { current.HeadId }, store.ListHeads().Unwrap());
            Assert.Empty(view);
            var entry = exact ? store.ReadEntry(current).Unwrap() : store.ReadHead(current.HeadId).Unwrap();
            Assert.Equal(current, entry.Id);
            image.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.Head, current.HeadId.LocalId);
            HeadStoreRecoveryReport saved = view[0];
            Assert.Equal(current, store.ReadHead(current.HeadId).Unwrap().Id);
            Assert.Equal(current, store.ReadEntry(current).Unwrap().Id);
            Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(current.HeadId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            Assert.Empty(store.ListHeadOrigins(1).Unwrap());
            Assert.Equal(new[] { saved }, view); // Cache and temporary-handle borrowing add nothing.
        }
        image.AssertRestored();
        using var healthy = fixture.Open();
        Assert.Equal(current, healthy.ReadHead(current.HeadId).Unwrap().Id);
        Assert.Empty(healthy.RecoveryReports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialNextHeadIsReportedWithExactDiscardedLengthAndNoTicket(int prefixLength) {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId current = fixture.SeedHead(later: true);
        TailImage image = RecoveryReportsFixture.Damage(fixture.HeadPath(current.HeadId.LocalId), completeTail: false, prefixLength);
        using (var store = fixture.Open()) {
            Assert.Empty(store.RecoveryReports);
            Assert.Equal(current, store.ReadHead(current.HeadId).Unwrap().Id);
            image.AssertReport(Assert.Single(store.RecoveryReports), HeadStoreRecoveryFileKind.Head, current.HeadId.LocalId);
        }
        image.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedHistoryReportsSourceTemporaryAndHealthyReopensPreserveOldAction(bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId sourceInitial;
        HeadEntryId source;
        HeadEntryId child;
        using (var seed = fixture.Create()) {
            sourceInitial = seed.CreateHead(7, [1], out _).Unwrap();
            source = seed.PublishHead(sourceInitial.HeadId, [2], out _).Unwrap();
            child = seed.ForkHead(19, source, out _).Unwrap();
        }
        TailImage image = RecoveryReportsFixture.Damage(fixture.HeadPath(source.HeadId.LocalId), completeTail);
        using (var store = fixture.Open()) {
            var view = store.RecoveryReports;
            var delivered = new List<HeadEntryId>();
            Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(child.HeadId, RecoveryReportsFixture.Unlimited, entry => {
                delivered.Add(entry.Id);
                Assert.Equal(entry.Id.HeadId == child.HeadId ? 0 : 1, view.Count);
                return true;
            }).Unwrap());
            Assert.Equal(new[] { child, source, sourceInitial }, delivered);
            image.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.Head, source.HeadId.LocalId);
            HeadStoreRecoveryReport saved = view[0];
            RecoveryReportsFixture.AssertReleased(image.Path); // The action does not retain the history temporary.
            Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(child.HeadId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
            RecoveryReportsFixture.AssertReleased(image.Path);
            Assert.Equal(source, store.ReadHead(source.HeadId).Unwrap().Id);
            Assert.Equal(new[] { saved }, view);
        }
        image.AssertRestored();
    }

    [Fact]
    public void ListForksReportsEveryRecoveredTemporaryAndDoesNotTreatHeaderQueryAsPublicationRead() {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId source;
        HeadEntryId child;
        using (var seed = fixture.Create()) {
            source = seed.CreateHead(7, [1], out _).Unwrap();
            child = seed.ForkHead(19, source, out _).Unwrap();
        }
        TailImage sourceImage = RecoveryReportsFixture.Damage(fixture.HeadPath(source.HeadId.LocalId), completeTail: false);
        TailImage childImage = RecoveryReportsFixture.Damage(fixture.HeadPath(child.HeadId.LocalId), completeTail: true);
        using (var store = fixture.Open()) {
            var view = store.RecoveryReports;
            Assert.Empty(view);
            HeadOriginInfo edge = Assert.Single(store.ListHeadOrigins(2).Unwrap());
            Assert.Equal(child.HeadId, edge.ChildHeadId);
            Assert.Equal(source.HeadId, edge.SourceHeadId);
            Assert.Equal(source.Ticket, edge.SourceEntryTicket);
            Assert.Equal(2, view.Count);
            sourceImage.AssertReport(Assert.Single(view, report => report.LocalId == source.HeadId.LocalId), HeadStoreRecoveryFileKind.Head, source.HeadId.LocalId);
            childImage.AssertReport(Assert.Single(view, report => report.LocalId == child.HeadId.LocalId), HeadStoreRecoveryFileKind.Head, child.HeadId.LocalId);
            var saved = view.ToArray();
            RecoveryReportsFixture.AssertReleased(sourceImage.Path);
            RecoveryReportsFixture.AssertReleased(childImage.Path);
            Assert.Single(store.ListHeadOrigins(2).Unwrap());
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
    public void ReadOnlyHeadOpenChannelsRejectRecoveryWithoutChangingAnyBytesOrReports(string channel, bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId current = fixture.SeedHead(later: true);
        RecoveryReportsFixture.Damage(fixture.HeadPath(current.HeadId.LocalId), completeTail);
        var before = fixture.CaptureFiles();
        using var store = fixture.Open(readOnly: true);
        var view = store.RecoveryReports;
        Assert.Empty(view);
        Assert.Throws<InvalidDataException>(() => Access(store, current, channel));
        Assert.Empty(view);
        Assert.Empty(store.RecoveryReports);
        store.EnsureUsable();
        fixture.AssertUnchangedWithHashes(before);
    }

    internal static void Access(Store store, HeadEntryId entryId, string channel) {
        switch (channel) {
            case "current": Assert.Equal(entryId, store.ReadHead(entryId.HeadId).Unwrap().Id); break;
            case "exact": Assert.Equal(entryId, store.ReadEntry(entryId).Unwrap().Id); break;
            case "history":
                Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(entryId.HeadId, RecoveryReportsFixture.Unlimited, _ => true).Unwrap());
                break;
            case "forks": store.ListHeadOrigins(int.MaxValue).Unwrap(); break;
            default: throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}
