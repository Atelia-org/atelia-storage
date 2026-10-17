using Atelia.HeadStore.Tests.History;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateIntegration;

public sealed class EntryHistoryConditionalTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameUncommittedCuIsRejectedAsAnchorOrOriginAndSkippedAsOlderRecord(bool readOnly) {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        f.SeedPublic(2, [2]);
        var uncommitted = ConditionalIntegrationRecords.AppendGroup(f,
            new Dictionary<uint, byte[]> { [1] = [3], [2] = [4] }, 1);
        HeadEntryId child = f.WriteMirror(3, uncommitted[1], new HistoryRecord([5]))[0];
        HeadEntryId newer;
        using (var writer = f.Open()) { newer = writer.PublishHead(initial.HeadId, [6], out _).Unwrap(); }
        using var store = f.Open(readOnly);
        int visits = 0;
        var invalidAnchor = store.ReadEntryHistory(uncommitted[1], HistoryFixture.Unlimited,
            _ => { visits++; return true; });
        Assert.Equal("HeadStore.InvalidHeadRecord", invalidAnchor.Error!.ErrorCode);
        Assert.Equal(0, visits);
        var delivered = new List<HeadEntryId>();
        var invalidOrigin = store.ReadEntryHistory(child, HistoryFixture.Unlimited,
            entry => { delivered.Add(entry.Id); return true; });
        Assert.Equal("HeadStore.InvalidForkOrigin", invalidOrigin.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        delivered.Clear();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadEntryHistory(newer, HistoryFixture.Unlimited,
            entry => { delivered.Add(entry.Id); return true; }).Unwrap());
        Assert.Equal(new[] { newer, initial }, delivered);
        f.AssertReleased(1, 2, 3);
    }

    [Fact]
    public void SameValueCuEntriesRemainDistinctAcrossForkAndInclusivePages() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        f.SeedPublic(2, [2]);
        HeadEntryId first;
        HeadEntryId second;
        HeadEntryId third;
        using (var writer = f.Open()) {
            first = Publish(writer, f, 1, 2)[f.Id(1)];
            second = Publish(writer, f, 1, 2)[f.Id(1)];
            HeadId fork = writer.ForkHead(3, second, out _).Unwrap();
            third = Publish(writer, f, 3, 2)[fork];
            writer.PublishHead(f.Id(1), [9], out _).Unwrap();
            writer.PublishHead(fork, [8], out _).Unwrap();
        }
        using var store = f.Open(readOnly: true);
        var firstPage = new List<HeadEntry>();
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadEntryHistory(third, HistoryFixture.Unlimited,
            entry => { firstPage.Add(entry); return firstPage.Count < 2; }).Unwrap());
        Assert.Equal(new[] { third, second }, firstPage.Select(e => e.Id));
        var nextPage = new List<HeadEntry>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadEntryHistory(second, HistoryFixture.Unlimited,
            entry => { nextPage.Add(entry); return true; }).Unwrap());
        Assert.Equal(new[] { second, first, initial }, nextPage.Select(e => e.Id));
        Assert.All(firstPage.Concat(nextPage), entry => Assert.Equal(new byte[] { 1 }, entry.Payload.ToArray()));
        Assert.Equal(firstPage[1].RecordedAtUnixMs, nextPage[0].RecordedAtUnixMs);
        Assert.Equal(4, firstPage.Concat(nextPage.Skip(1)).Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void NecessaryCuPeerBudgetAndCrcErrorsAreNeverChangedToInvalidAnchor() {
        using var f = new HistoryFixture();
        f.SeedPublic(1, [1]);
        f.SeedPublic(2, [2]);
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = f.Open()) { group = Publish(writer, f, 1, 2); }
        f.FlipPayload(group[f.Id(2)]);
        using var store = f.Open(readOnly: true);
        int visits = 0;
        Assert.Equal("HeadStore.HistoryBudgetExceeded",
            store.ReadEntryHistory(group[f.Id(1)], new(1, 7), _ => { visits++; return true; }).Error!.ErrorCode);
        Assert.Equal("Rbf.CrcMismatch",
            store.ReadEntryHistory(group[f.Id(1)], HistoryFixture.Unlimited, _ => { visits++; return true; }).Error!.ErrorCode);
        Assert.Equal(0, visits);
        f.AssertReleased(1, 2);
    }

    [Fact]
    public void RepeatingDeliveredAnchorCanExhaustBudgetBeforeAnyNewEntry() {
        using var f = new HistoryFixture();
        HeadEntryId initial = f.SeedPublic(1, [1])[0];
        f.SeedPublic(2, [2]);
        ConditionalIntegrationRecords.AppendGroup(f, new Dictionary<uint, byte[]> { [1] = [3], [2] = [4] }, 1);
        HeadEntryId anchor;
        using (var writer = f.Open()) { anchor = writer.PublishHead(initial.HeadId, [5], out _).Unwrap(); }
        using var store = f.Open(readOnly: true);
        for (int retry = 0; retry < 2; retry++) {
            var received = new List<HeadEntryId>();
            var result = store.ReadEntryHistory(anchor, new(2, 9), entry => { received.Add(entry.Id); return true; });
            Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
            Assert.Equal(new[] { anchor }, received);
        }
        var completed = new List<HeadEntryId>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadEntryHistory(anchor, HistoryFixture.Unlimited,
            entry => { completed.Add(entry.Id); return true; }).Unwrap());
        Assert.Equal(new[] { anchor, initial }, completed);
    }

    [Fact]
    public void CuCallbackReadQueriesAndTagScanRemainAllowedButTagScanCannotEnterHistory() {
        using var f = new HistoryFixture();
        f.SeedPublic(1, [1]);
        f.SeedPublic(2, [2]);
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = f.Open()) {
            group = Publish(writer, f, 1, 2);
            writer.CreateTag("saved", [f.Id(1)], out _).Unwrap();
        }
        using var store = f.Open();
        int visits = 0;
        int tags = 0;
        // Exact local qualification costs 7; committed same-ticket peer qualification costs 5.
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadEntryHistory(group[f.Id(1)], new(1, 12), entry => {
            visits++;
            Assert.Equal(group[f.Id(1)], store.ReadHead(f.Id(1)).Unwrap().Id);
            Assert.Equal(group[f.Id(2)], store.ReadEntry(group[f.Id(2)]).Unwrap().Id);
            Assert.Equal(HeadSetTagScanEnd.Complete, store.ScanTags(_ => {
                tags++;
                Assert.Equal(entry.Id, store.ReadEntry(entry.Id).Unwrap().Id);
                Assert.Equal("HeadStore.TagScanActive", store.ReadEntryHistory(entry.Id, HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
                Assert.Equal("HeadStore.TagScanActive", store.ReadHeadHistory(entry.Id.HeadId, HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
                return true;
            }).Unwrap());
            var blocked = store.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [f.Id(1)] = new byte[] { 9 }, [f.Id(2)] = new byte[] { 8 }
            }, out PublicationOutcome outcome);
            Assert.Equal("HeadStore.HistoryActive", blocked.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        }).Unwrap());
        Assert.Equal(1, visits);
        Assert.Equal(1, tags);
        Assert.Equal(HeadSetTagScanEnd.Complete, store.ScanTags(_ => {
            Assert.Equal("HeadStore.TagScanActive", store.ReadEntryHistory(group[f.Id(1)], HistoryFixture.Unlimited, _ => true).Error!.ErrorCode);
            return true;
        }).Unwrap());
    }

    private static IReadOnlyDictionary<HeadId, HeadEntryId> Publish(HeadStore store, HistoryFixture f, uint first, uint second) =>
        store.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [f.Id(first)] = new byte[] { 1 }, [f.Id(second)] = new byte[] { 2 }
        }, out _).Unwrap();
}
