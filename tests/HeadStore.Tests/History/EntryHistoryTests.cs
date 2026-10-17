using Xunit;

namespace Atelia.HeadStore.Tests.History;

public sealed class EntryHistoryTests {
    [Theory]
    [InlineData(true, HeadHistoryEnd.Complete)]
    [InlineData(false, HeadHistoryEnd.VisitorStopped)]
    public void RootAnchorCompletesAtExactBudgetWithoutAnotherRead(bool keepGoing, HeadHistoryEnd expected) {
        using var f = new HistoryFixture();
        HeadEntryId anchor = f.SeedPublic(1, [1], [2], [3])[0];
        using var store = f.Open();
        int visits = 0;
        Assert.Equal(expected, store.ReadEntryHistory(anchor, new(1, 7), entry => {
            Assert.Equal(anchor, entry.Id);
            visits++;
            return keepGoing;
        }).Unwrap());
        Assert.Equal(1, visits);
        var shortBudget = store.ReadEntryHistory(anchor, new(1, 6), _ => { visits++; return true; });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", shortBudget.Error!.ErrorCode);
        Assert.Equal(1, visits);
        f.AssertReleased(1);
    }

    [Fact]
    public void InclusivePagesKeepFixedPrefixAndOriginalMetadataAcrossMultipleForksAndNewSuffixes() {
        using var f = new HistoryFixture();
        HeadEntryId[] roots = f.SeedPublic(1, [1], [2], [2]);
        HeadEntryId tip;
        List<HeadEntry> expected;
        using (var writer = f.Open()) {
            HeadId child = writer.ForkHead(2, roots[1], out _).Unwrap();
            HeadEntryId childEntry = writer.PublishHead(child, [2], out _).Unwrap();
            HeadId grandchild = writer.ForkHead(3, childEntry, out _).Unwrap();
            tip = writer.PublishHead(grandchild, [2], out _).Unwrap();
            expected = HistoryFixture.Collect(writer, grandchild);
            writer.PublishHead(roots[0].HeadId, [9], out _).Unwrap();
            writer.PublishHead(child, [8], out _).Unwrap();
            writer.PublishHead(grandchild, [7], out _).Unwrap();
            Assert.Equal(new[] { tip, childEntry, roots[1], roots[0] }, expected.Select(e => e.Id));
        }
        byte[] encoded = new byte[HeadEntryId.EncodedSize];
        Assert.True(tip.TryWrite(encoded));
        Assert.True(HeadEntryId.TryRead(encoded, out HeadEntryId cursor));
        using var reader = f.Open(readOnly: true);
        var received = new List<HeadEntry>();
        bool firstPage = true;
        while (true) {
            var page = new List<HeadEntry>();
            int physicalEntries = 0;
            HeadEntryId pageAnchor = cursor;
            var end = reader.ReadEntryHistory(pageAnchor, new(2, 100), entry => {
                physicalEntries++;
                if (firstPage || physicalEntries != 1) { page.Add(entry); }
                else { Assert.Equal(pageAnchor, entry.Id); }
                cursor = entry.Id;
                return physicalEntries < 2;
            }).Unwrap();
            received.AddRange(page);
            firstPage = false;
            if (end == HeadHistoryEnd.Complete) {
                Assert.Empty(page); // Final inclusive root-only page repeats the last delivered anchor.
                break;
            }
        }
        Assert.Equal(expected.Select(e => e.Id), received.Select(e => e.Id));
        Assert.Equal(expected.Select(e => e.RecordedAtUnixMs), received.Select(e => e.RecordedAtUnixMs));
        Assert.Equal(expected.Select(e => e.Payload.ToArray()), received.Select(e => e.Payload.ToArray()));
    }

    [Fact]
    public void AccurateHistoryMatchesForkedPrefixAndCallbackForkIsRejectedUntilExit() {
        using var f = new HistoryFixture();
        HeadEntryId anchor = f.SeedPublic(1, [1], [2], [3])[1];
        using var store = f.Open();
        var ids = new List<HeadEntryId>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadEntryHistory(anchor, HistoryFixture.Unlimited, entry => {
            ids.Add(entry.Id);
            var blocked = store.ForkHead(2, entry.Id, out PublicationOutcome outcome);
            Assert.Equal("HeadStore.HistoryActive", blocked.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return true;
        }).Unwrap());
        HeadId fork = store.ForkHead(2, anchor, out _).Unwrap();
        Assert.Equal(ids, HistoryFixture.Collect(store, fork).Select(e => e.Id));
    }

    [Fact]
    public void FirstLocalAnchorStopAndEntryExhaustionDoNotAccessMissingOrigin() {
        using var f = new HistoryFixture();
        HeadEntryId child = f.WriteMirror(2, f.DeclaredInitial(19, 1), new HistoryRecord([1]))[0];
        using var store = f.Open();
        Assert.Equal(HeadHistoryEnd.VisitorStopped,
            store.ReadEntryHistory(child, new(1, 7), _ => false).Unwrap());
        Assert.Equal("HeadStore.HistoryBudgetExceeded",
            store.ReadEntryHistory(child, new(1, 100), _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.HistoryBudgetExceeded",
            store.ReadEntryHistory(child, new(2, 8), _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.HeadNotFound",
            store.ReadEntryHistory(child, new(2, 9), _ => true).Error!.ErrorCode);
    }

    [Fact]
    public void RepeatedAnchorAndSourceTransitionConsumeWorkOnEveryPage() {
        using var f = new HistoryFixture();
        HeadEntryId source = f.SeedPublic(1, [1])[0];
        HeadEntryId child = f.WriteMirror(2, source, new HistoryRecord([2]))[0];
        using var store = f.Open();
        foreach (long work in new long[] { 14, 15 }) {
            var ids = new List<HeadEntryId>();
            var result = store.ReadEntryHistory(child, new(2, work), entry => { ids.Add(entry.Id); return true; });
            if (work == 14) {
                Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
                Assert.Equal(new[] { child }, ids);
            }
            else {
                Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
                Assert.Equal(new[] { child, source }, ids);
            }
        }
    }
}
