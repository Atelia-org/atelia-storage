using Atelia.Data;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.History;

public sealed class PublicHistoryTests {
    [Fact]
    public void PublicPublicationHistoryKeepsSameBytesAndRewindAsDistinctOwnedRevisionsAcrossColdOwners() {
        using var fixture = new HistoryFixture();
        byte[][] payloads = [[0xff, 0], [0xff, 0], [0xfe], [0xff, 0]];
        RefRevision[] revisions = fixture.SeedPublic(7, payloads);
        List<RefBytesSnapshot> saved;
        using (var writer = fixture.Open()) {
            saved = HistoryFixture.Collect(writer, fixture.Id(7));
            Assert.Equal(revisions.Reverse(), saved.Select(snapshot => snapshot.Revision));
            Assert.Equal(payloads.Reverse(), saved.Select(snapshot => snapshot.Payload.ToArray()));
            Assert.Equal(4, saved.Select(snapshot => snapshot.Revision).Distinct().Count());
            fixture.AssertReleased(7);
        }
        using (var reader = fixture.Open(readOnly: true)) {
            var cold = HistoryFixture.Collect(reader, fixture.Id(7));
            Assert.Equal(saved.Select(snapshot => snapshot.Revision), cold.Select(snapshot => snapshot.Revision));
            Assert.Equal(saved.Select(snapshot => snapshot.RecordedAtUnixMs), cold.Select(snapshot => snapshot.RecordedAtUnixMs));
            Assert.Equal(saved.Select(snapshot => snapshot.Payload.ToArray()), cold.Select(snapshot => snapshot.Payload.ToArray()));
        }
        fixture.Data.Dispose();
        Assert.Equal(payloads.Reverse(), saved.Select(snapshot => snapshot.Payload.ToArray()));
        using var reopenedData = DataStore.OpenReadOnly(fixture.DataRoot);
        using var reopened = Store.OpenReadOnly(fixture.Root, reopenedData);
        Assert.Equal(revisions.Reverse(), HistoryFixture.Collect(reopened, fixture.Id(7)).Select(snapshot => snapshot.Revision));
    }

    [Fact]
    public void PhysicalOrderAndStoredTimesRemainIndependentOfTimestampOrder() {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.WriteMirror(7, null,
            new HistoryRecord([1], long.MaxValue), new HistoryRecord([2], -4), new HistoryRecord([3], long.MinValue), new HistoryRecord([4], -4));
        using var store = fixture.Open();
        var snapshots = HistoryFixture.Collect(store, fixture.Id(7));
        Assert.Equal(revisions.Reverse(), snapshots.Select(snapshot => snapshot.Revision));
        Assert.Equal(new long[] { -4, long.MinValue, -4, long.MaxValue }, snapshots.Select(snapshot => snapshot.RecordedAtUnixMs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadInitialContentAllowsHealthyPrefixAndVisitorStopButFailsWhenNeeded(bool readOnly) {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.SeedPublic(7, HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52), HistoryFixture.Bytes(value: 0x53));
        fixture.FlipPayload(revisions[0]);
        using var store = fixture.Open(readOnly);
        var delivered = new List<RefBytesSnapshot>();
        var stopped = store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot);
            return delivered.Count < 2;
        });
        Assert.Equal(HistoryEnd.VisitorStopped, stopped.Unwrap());
        Assert.Equal(new[] { revisions[2], revisions[1] }, delivered.Select(snapshot => snapshot.Revision));
        Assert.Equal(revisions[2], store.ReadRef(fixture.Id(7)).Unwrap().Revision);
        delivered.Clear();
        var full = store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot);
            return true;
        });
        Assert.True(full.IsFailure);
        Assert.StartsWith("Rbf.", full.Error!.ErrorCode);
        Assert.Equal(new[] { revisions[2], revisions[1] }, delivered.Select(snapshot => snapshot.Revision));
    }

    [Theory]
    [InlineData(0u, 8, 128)]
    [InlineData(2u, 8, 128)]
    [InlineData(1u, 0, 128)]
    [InlineData(1u, 7, 128)]
    [InlineData(1u, 9, 128)]
    [InlineData(1u, 8, 0)]
    [InlineData(1u, 8, 1048577)]
    public void NecessaryBadOldEnvelopeIsRejectedWithoutSkippingIt(uint tag, int metadataLength, int payloadLength) {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.WriteMirror(7, null,
            new HistoryRecord(HistoryFixture.Bytes(), 1), new HistoryRecord(HistoryFixture.Bytes(payloadLength), 2, tag, metadataLength), new HistoryRecord([3], 3));
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidRefRecord", result.Error!.ErrorCode);
        Assert.Equal(new[] { revisions[2] }, delivered);
        Assert.Equal(revisions[2], store.ReadRef(fixture.Id(7)).Unwrap().Revision);
    }

    [Fact]
    public void HeaderOnlyFileFailsWithoutDeliveringAHeaderAsSnapshot() {
        using var fixture = new HistoryFixture();
        fixture.WriteMirror(7, null);
        using var store = fixture.Open();
        int calls = 0;
        var result = store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, _ => { calls++; return true; });
        Assert.Equal("VersionStore.InvalidRefRecord", result.Error!.ErrorCode);
        Assert.Equal(0, calls);
        fixture.AssertReleased(7);
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(1L, 0L)]
    [InlineData(-1L, 1L)]
    [InlineData(1L, -1L)]
    public void NonpositiveBudgetRejectsBeforeLookingForTheRef(long maxSnapshots, long maxWorkSteps) {
        using var fixture = new HistoryFixture();
        using var store = fixture.Open();
        int calls = 0;
        var result = store.ReadRefHistory(fixture.Id(7), new(maxSnapshots, maxWorkSteps), _ => { calls++; return true; });
        Assert.Equal("VersionStore.ArgumentError", result.Error!.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DefaultForeignRefAndNullVisitorRejectThenOwnerRemainsUsable() {
        using var fixture = new HistoryFixture();
        using var other = new HistoryFixture();
        using var store = fixture.Open();
        Assert.Equal("VersionStore.ArgumentError", store.ReadRefHistory(default, HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
        Assert.Equal("VersionStore.ArgumentError", store.ReadRefHistory(other.Id(7), HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
        Assert.Equal("VersionStore.ArgumentError", store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, null!).Error!.ErrorCode);
        var initial = store.CreateRef(7, [1], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Single(HistoryFixture.Collect(store, initial.RefId));
    }

    [Fact]
    public void CallbackQueriesReuseTheActiveFileAndHistoryKeepsItsCapturedHeadWhileMutationsAreRejected() {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.SeedPublic(7, [1], [2], [3]);
        var dataAddress = fixture.Data.Append(9, [0x64]).Unwrap();
        fixture.Data.ConfirmDurable();
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            Assert.Equal(revisions[2], store.ReadRef(fixture.Id(7)).Unwrap().Revision);
            Assert.Equal(revisions[2], store.ReadRef(7u).Unwrap().Revision);
            using var dataFrame = fixture.Data.ReadFrame(dataAddress).Unwrap();
            Assert.Equal(new byte[] { 0x64 }, dataFrame.PayloadAndMeta.ToArray());
            Assert.Equal("VersionStore.HistoryActive", store.ReadRefHistory(fixture.Id(7), HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
            Assert.Equal("VersionStore.HistoryActive", store.PublishRef(fixture.Id(7), [4], out var published).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, published);
            Assert.Equal("VersionStore.HistoryActive", store.CreateRef([4], out var automatic).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, automatic);
            Assert.Equal("VersionStore.HistoryActive", store.CreateRef(8, [4], out var reserved).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, reserved);
            return true;
        });
        Assert.Equal(HistoryEnd.Complete, result.Unwrap());
        Assert.Equal(revisions.Reverse(), delivered);
        fixture.AssertReleased(7);
        Assert.False(File.Exists(fixture.RefPath(8)));
        RefRevision next = store.PublishRef(fixture.Id(7), [4], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(next, store.ReadRef(fixture.Id(7)).Unwrap().Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationFromLastCallbackOverridesNormalEndAndDoesNotFaultWriter(bool continueReading) {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        using var cancellation = new CancellationTokenSource();
        RefBytesSnapshot? saved = null;
        var exception = Assert.Throws<OperationCanceledException>(() => {
            store.ReadRefHistory(initial.RefId, new(1, 5), snapshot => {
                saved = snapshot;
                cancellation.Cancel();
                return continueReading;
            }, cancellation.Token);
        });
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(initial, saved!.Revision);
        fixture.AssertReleased(7);
        Assert.Equal(HistoryEnd.Complete, store.ReadRefHistory(initial.RefId, new(1, 5), _ => true).Unwrap());
        Assert.True(store.PublishRef(initial.RefId, [2], out var outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void PreCanceledCallDoesNotDeliverAndNextCallCanRead() {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => {
            store.ReadRefHistory(initial.RefId, HistoryFixture.Unlimited, _ => { calls++; return true; }, cancellation.Token);
        });
        Assert.Equal(0, calls);
        fixture.AssertReleased(7);
        Assert.Single(HistoryFixture.Collect(store, initial.RefId));
    }

    [Fact]
    public void VisitorExceptionPropagatesUnchangedAndReleasesTemporaryFileAndGuard() {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        var expected = new FormatException("application payload decoder");
        RefBytesSnapshot? saved = null;
        Assert.Same(expected, Assert.Throws<FormatException>(() => {
            store.ReadRefHistory(initial.RefId, HistoryFixture.Unlimited, snapshot => {
                saved = snapshot;
                throw expected;
            });
        }));
        Assert.Equal(new byte[] { 1 }, saved!.Payload.ToArray());
        fixture.AssertReleased(7);
        Assert.Single(HistoryFixture.Collect(store, initial.RefId));
        Assert.True(store.PublishRef(initial.RefId, [2], out var outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeFromLastCallbackInvalidatesResultReleasesRootLockAndPreservesOwnedSnapshot(bool continueReading) {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        RefBytesSnapshot? saved = null;
        Assert.Throws<ObjectDisposedException>(() => {
            store.ReadRefHistory(initial.RefId, new(1, 5), snapshot => {
                saved = snapshot;
                store.Dispose();
                return continueReading;
            });
        });
        fixture.Data.EnsureUsable();
        fixture.AssertReleased(7);
        Assert.Equal(new byte[] { 1 }, saved!.Payload.ToArray());
        using var reopened = fixture.Open();
        Assert.Equal(initial, reopened.ReadRef(initial.RefId).Unwrap().Revision);
        Assert.Throws<ObjectDisposedException>(() => { store.ReadRefHistory(default, default, null!); });
    }
}
