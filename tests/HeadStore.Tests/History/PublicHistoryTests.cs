using Atelia.Data;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.History;

public sealed class PublicHistoryTests {
    [Fact]
    public void PublicPublicationHistoryKeepsSameBytesAndRewindAsDistinctOwnedEntryIdsAcrossColdOwners() {
        using var fixture = new HistoryFixture();
        byte[][] payloads = [[0xff, 0], [0xff, 0], [0xfe], [0xff, 0]];
        HeadEntryId[] entryIds = fixture.SeedPublic(7, payloads);
        List<HeadEntry> saved;
        using (var writer = fixture.Open()) {
            saved = HistoryFixture.Collect(writer, fixture.Id(7));
            Assert.Equal(entryIds.Reverse(), saved.Select(entry => entry.Id));
            Assert.Equal(payloads.Reverse(), saved.Select(entry => entry.Payload.ToArray()));
            Assert.Equal(4, saved.Select(entry => entry.Id).Distinct().Count());
            fixture.AssertReleased(7);
        }
        using (var reader = fixture.Open(readOnly: true)) {
            var cold = HistoryFixture.Collect(reader, fixture.Id(7));
            Assert.Equal(saved.Select(entry => entry.Id), cold.Select(entry => entry.Id));
            Assert.Equal(saved.Select(entry => entry.RecordedAtUnixMs), cold.Select(entry => entry.RecordedAtUnixMs));
            Assert.Equal(saved.Select(entry => entry.Payload.ToArray()), cold.Select(entry => entry.Payload.ToArray()));
        }
        fixture.Data.Dispose();
        Assert.Equal(payloads.Reverse(), saved.Select(entry => entry.Payload.ToArray()));
        using var reopenedData = DataStore.OpenReadOnly(fixture.DataRoot);
        using var reopened = Store.OpenReadOnly(fixture.Root, reopenedData);
        Assert.Equal(entryIds.Reverse(), HistoryFixture.Collect(reopened, fixture.Id(7)).Select(entry => entry.Id));
    }

    [Fact]
    public void PhysicalOrderAndStoredTimesRemainIndependentOfTimestampOrder() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.WriteMirror(7, null,
            new HistoryRecord([1], long.MaxValue), new HistoryRecord([2], -4), new HistoryRecord([3], long.MinValue), new HistoryRecord([4], -4));
        using var store = fixture.Open();
        var entries = HistoryFixture.Collect(store, fixture.Id(7));
        Assert.Equal(entryIds.Reverse(), entries.Select(entry => entry.Id));
        Assert.Equal(new long[] { -4, long.MinValue, -4, long.MaxValue }, entries.Select(entry => entry.RecordedAtUnixMs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadInitialContentAllowsHealthyPrefixAndVisitorStopButFailsWhenNeeded(bool readOnly) {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.SeedPublic(7, HistoryFixture.Bytes(), HistoryFixture.Bytes(value: 0x52), HistoryFixture.Bytes(value: 0x53));
        fixture.FlipPayload(entryIds[0]);
        using var store = fixture.Open(readOnly);
        var delivered = new List<HeadEntry>();
        var stopped = store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, entry => {
            delivered.Add(entry);
            return delivered.Count < 2;
        });
        Assert.Equal(HeadHistoryEnd.VisitorStopped, stopped.Unwrap());
        Assert.Equal(new[] { entryIds[2], entryIds[1] }, delivered.Select(entry => entry.Id));
        Assert.Equal(entryIds[2], store.ReadHead(fixture.Id(7)).Unwrap().Id);
        delivered.Clear();
        var full = store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, entry => {
            delivered.Add(entry);
            return true;
        });
        Assert.True(full.IsFailure);
        Assert.StartsWith("Rbf.", full.Error!.ErrorCode);
        Assert.Equal(new[] { entryIds[2], entryIds[1] }, delivered.Select(entry => entry.Id));
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
        HeadEntryId[] entryIds = fixture.WriteMirror(7, null,
            new HistoryRecord(HistoryFixture.Bytes(), 1), new HistoryRecord(HistoryFixture.Bytes(payloadLength), 2, tag, metadataLength), new HistoryRecord([3], 3));
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.InvalidHeadRecord", result.Error!.ErrorCode);
        Assert.Equal(new[] { entryIds[2] }, delivered);
        Assert.Equal(entryIds[2], store.ReadHead(fixture.Id(7)).Unwrap().Id);
    }

    [Fact]
    public void HeaderOnlyFileFailsWithoutDeliveringAHeaderAsEntry() {
        using var fixture = new HistoryFixture();
        fixture.WriteMirror(7, null);
        using var store = fixture.Open();
        int calls = 0;
        var result = store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, _ => { calls++; return true; });
        Assert.Equal("HeadStore.InvalidHeadRecord", result.Error!.ErrorCode);
        Assert.Equal(0, calls);
        fixture.AssertReleased(7);
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(1L, 0L)]
    [InlineData(-1L, 1L)]
    [InlineData(1L, -1L)]
    public void NonpositiveBudgetRejectsBeforeLookingForTheHead(long maxEntries, long maxWorkSteps) {
        using var fixture = new HistoryFixture();
        using var store = fixture.Open();
        int calls = 0;
        var result = store.ReadHeadHistory(fixture.Id(7), new(maxEntries, maxWorkSteps), _ => { calls++; return true; });
        Assert.Equal("HeadStore.ArgumentError", result.Error!.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DefaultForeignHeadAndNullVisitorRejectThenOwnerRemainsUsable() {
        using var fixture = new HistoryFixture();
        using var other = new HistoryFixture();
        using var store = fixture.Open();
        Assert.Equal("HeadStore.ArgumentError", store.ReadHeadHistory(default, HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.ArgumentError", store.ReadHeadHistory(other.Id(7), HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.ArgumentError", store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, null!).Error!.ErrorCode);
        var initial = store.CreateHead(7, [1], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Single(HistoryFixture.Collect(store, initial.HeadId));
    }

    [Fact]
    public void CallbackQueriesReuseTheActiveFileAndHistoryKeepsItsCapturedHeadWhileMutationsAreRejected() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.SeedPublic(7, [1], [2], [3]);
        var dataAddress = fixture.Data.Append(9, [0x64]).Unwrap();
        fixture.Data.ConfirmDurable();
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            Assert.Equal(entryIds[2], store.ReadHead(fixture.Id(7)).Unwrap().Id);
            Assert.Equal(entryIds[2], store.ReadHead(7u).Unwrap().Id);
            using var dataFrame = fixture.Data.ReadFrame(dataAddress).Unwrap();
            Assert.Equal(new byte[] { 0x64 }, dataFrame.PayloadAndMeta.ToArray());
            Assert.Equal("HeadStore.HistoryActive", store.ReadHeadHistory(fixture.Id(7), HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
            Assert.Equal("HeadStore.HistoryActive", store.PublishHead(fixture.Id(7), [4], out var published).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, published);
            Assert.Equal("HeadStore.HistoryActive", store.CreateHead([4], out var automatic).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, automatic);
            Assert.Equal("HeadStore.HistoryActive", store.CreateHead(8, [4], out var reserved).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, reserved);
            return true;
        });
        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Equal(entryIds.Reverse(), delivered);
        fixture.AssertReleased(7);
        Assert.False(File.Exists(fixture.HeadPath(8)));
        HeadEntryId next = store.PublishHead(fixture.Id(7), [4], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(next, store.ReadHead(fixture.Id(7)).Unwrap().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationFromLastCallbackOverridesNormalEndAndDoesNotFaultWriter(bool continueReading) {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        using var cancellation = new CancellationTokenSource();
        HeadEntry? saved = null;
        var exception = Assert.Throws<OperationCanceledException>(() => {
            store.ReadHeadHistory(initial.HeadId, new(1, 5), entry => {
                saved = entry;
                cancellation.Cancel();
                return continueReading;
            }, cancellation.Token);
        });
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(initial, saved!.Id);
        fixture.AssertReleased(7);
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadHeadHistory(initial.HeadId, new(1, 5), _ => true).Unwrap());
        Assert.True(store.PublishHead(initial.HeadId, [2], out var outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void PreCanceledCallDoesNotDeliverAndNextCallCanRead() {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => {
            store.ReadHeadHistory(initial.HeadId, HistoryFixture.Unlimited, _ => { calls++; return true; }, cancellation.Token);
        });
        Assert.Equal(0, calls);
        fixture.AssertReleased(7);
        Assert.Single(HistoryFixture.Collect(store, initial.HeadId));
    }

    [Fact]
    public void VisitorExceptionPropagatesUnchangedAndReleasesTemporaryFileAndGuard() {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        var expected = new FormatException("application payload decoder");
        HeadEntry? saved = null;
        Assert.Same(expected, Assert.Throws<FormatException>(() => {
            store.ReadHeadHistory(initial.HeadId, HistoryFixture.Unlimited, entry => {
                saved = entry;
                throw expected;
            });
        }));
        Assert.Equal(new byte[] { 1 }, saved!.Payload.ToArray());
        fixture.AssertReleased(7);
        Assert.Single(HistoryFixture.Collect(store, initial.HeadId));
        Assert.True(store.PublishHead(initial.HeadId, [2], out var outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeFromLastCallbackInvalidatesResultReleasesRootLockAndPreservesOwnedEntry(bool continueReading) {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(7, [1])[0];
        using var store = fixture.Open();
        HeadEntry? saved = null;
        Assert.Throws<ObjectDisposedException>(() => {
            store.ReadHeadHistory(initial.HeadId, new(1, 5), entry => {
                saved = entry;
                store.Dispose();
                return continueReading;
            });
        });
        fixture.Data.EnsureUsable();
        fixture.AssertReleased(7);
        Assert.Equal(new byte[] { 1 }, saved!.Payload.ToArray());
        using var reopened = fixture.Open();
        Assert.Equal(initial, reopened.ReadHead(initial.HeadId).Unwrap().Id);
        Assert.Throws<ObjectDisposedException>(() => { store.ReadHeadHistory(default, default, null!); });
    }
}
