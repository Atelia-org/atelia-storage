using Xunit;
using Atelia.Rbf;

namespace Atelia.HeadStore.Tests.History;

public sealed class HeadHistoryBudgetTests {
    [Theory]
    [InlineData(1, 5L)]
    [InlineData(2, 7L)]
    [InlineData(3, 9L)]
    public void ExactOrdinaryWorkAndEntryBudgetsCompleteWithoutAnExtraHeaderOrFalseMove(int count, long exactWorkSteps) {
        using var fixture = new HistoryFixture();
        byte[][] payloads = Enumerable.Range(1, count).Select(value => new byte[] { (byte)value }).ToArray();
        HeadEntryId[] entryIds = fixture.SeedPublic(7, payloads);
        using var store = fixture.Open();
        // enter 1 + first header MoveNext/full 2 + one MoveNext/full per Entry 2 * count.
        Assert.Equal(3 + 2L * count, exactWorkSteps);
        Assert.Equal(entryIds.Reverse(), HistoryFixture.Collect(store, fixture.Id(7), new(count, exactWorkSteps)).Select(entry => entry.Id));
        var delivered = new List<HeadEntryId>();
        var insufficient = store.ReadHeadHistory(fixture.Id(7), new(count, exactWorkSteps - 1), entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
        Assert.Equal(entryIds.Skip(1).Reverse(), delivered);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void VisitorFalseAtExactHeadBudgetWinsBeforeAnyOlderQualificationOrEntryExhaustion() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.SeedPublic(7, HistoryFixture.Bytes(), [2]);
        fixture.FlipPayload(entryIds[0]);
        using var store = fixture.Open();
        HeadEntry? saved = null;
        var result = store.ReadHeadHistory(fixture.Id(7), new(1, 5), entry => { saved = entry; return false; });
        Assert.Equal(HeadHistoryEnd.VisitorStopped, result.Unwrap());
        Assert.Equal(entryIds[1], saved!.Id);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void ExhaustedEntryBudgetDoesNotProbeAnOlderInvalidRecord() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.WriteMirror(7, null, new HistoryRecord([1], Tag: 2), new HistoryRecord([2]));
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(fixture.Id(7), new(1, long.MaxValue), entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { entryIds[1] }, delivered);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void EntryBudgetAtChildInitialDoesNotProbeItsMissingSource() {
        using var fixture = new HistoryFixture();
        HeadEntryId child = fixture.WriteMirror(2, fixture.DeclaredInitial(19, 1), new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        var result = store.ReadHeadHistory(child.HeadId, new(1, long.MaxValue), _ => true);
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        fixture.AssertReleased(2);
    }

    [Fact]
    public void InheritedAnchorRepeatedCompleteReadsAreChargedAndExactTwelveStepsComplete() {
        using var fixture = new HistoryFixture();
        HeadEntryId source = fixture.SeedPublic(1, [1])[0];
        HeadEntryId child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        // Child: enter/header 3 + Entry MoveNext/full 2 = 5.
        // Source: enter/header 3 + GetScanBoundaryAfter full 1 + boundary ScanReverse full 1
        // + anchor MoveNext 1 + Entry full 1 = 7. The three actual anchor reads each cost a step.
        Assert.Equal(new[] { child, source }, HistoryFixture.Collect(store, child.HeadId, new(2, 12)).Select(entry => entry.Id));
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, new(2, 11), entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void WorkBudgetAtChildInitialDoesNotResolveMissingSourceAndStopStillWins() {
        using var fixture = new HistoryFixture();
        HeadEntryId child = fixture.WriteMirror(2, fixture.DeclaredInitial(19, 1), new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal("HeadStore.HistoryBudgetExceeded", store.ReadHeadHistory(child.HeadId, new(2, 5), _ => true).Error!.ErrorCode);
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(child.HeadId, new(2, 5), _ => false).Unwrap());
        fixture.AssertReleased(2);
    }

    [Fact]
    public void FalseForwardMoveAttemptIsChargedAndMissingHeaderCannotBeReportedWithOnlyEntryBudget() {
        using var fixture = new HistoryFixture();
        using (IRbfFile empty = RbfFile.CreateNew(fixture.HeadPath(7), RbfCacheMode.Off)) { empty.DurableFlush(); }
        using var store = fixture.Open();
        Assert.Equal("HeadStore.HistoryBudgetExceeded", store.ReadHeadHistory(fixture.Id(7), new(1, 1), _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.InvalidHeadHeader", store.ReadHeadHistory(fixture.Id(7), new(1, 2), _ => true).Error!.ErrorCode);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void FalseReverseMoveAttemptIsChargedAndItsTerminationErrorIsPropagated() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entryIds = fixture.SeedPublic(7, HistoryFixture.Bytes(), [2]);
        fixture.FlipTrailer(entryIds[0]);
        using var store = fixture.Open();
        var delivered = new List<HeadEntryId>();
        // Five steps deliver the head; the sixth MoveNext attempts the bad older TrailerCodeword.
        var tooSmall = store.ReadHeadHistory(fixture.Id(7), new(2, 5), entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", tooSmall.Error!.ErrorCode);
        Assert.Equal(new[] { entryIds[1] }, delivered);
        delivered.Clear();
        var terminated = store.ReadHeadHistory(fixture.Id(7), new(2, 6), entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.True(terminated.IsFailure);
        Assert.StartsWith("Rbf.", terminated.Error!.ErrorCode);
        Assert.Equal(new[] { entryIds[1] }, delivered);
        fixture.AssertReleased(7);
    }
}
