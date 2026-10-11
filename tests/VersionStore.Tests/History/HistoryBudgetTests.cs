using Xunit;
using Atelia.Rbf;

namespace Atelia.VersionStore.Tests.History;

public sealed class HistoryBudgetTests {
    [Theory]
    [InlineData(1, 5L)]
    [InlineData(2, 7L)]
    [InlineData(3, 9L)]
    public void ExactOrdinaryWorkAndSnapshotBudgetsCompleteWithoutAnExtraHeaderOrFalseMove(int count, long exactWorkSteps) {
        using var fixture = new HistoryFixture();
        byte[][] payloads = Enumerable.Range(1, count).Select(value => new byte[] { (byte)value }).ToArray();
        RefRevision[] revisions = fixture.SeedPublic(7, payloads);
        using var store = fixture.Open();
        // enter 1 + first header MoveNext/full 2 + one MoveNext/full per Snapshot 2 * count.
        Assert.Equal(3 + 2L * count, exactWorkSteps);
        Assert.Equal(revisions.Reverse(), HistoryFixture.Collect(store, fixture.Id(7), new(count, exactWorkSteps)).Select(snapshot => snapshot.Revision));
        var delivered = new List<RefRevision>();
        var insufficient = store.ReadRefHistory(fixture.Id(7), new(count, exactWorkSteps - 1), snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
        Assert.Equal(revisions.Skip(1).Reverse(), delivered);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void VisitorFalseAtExactHeadBudgetWinsBeforeAnyOlderQualificationOrSnapshotExhaustion() {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.SeedPublic(7, HistoryFixture.Bytes(), [2]);
        fixture.FlipPayload(revisions[0]);
        using var store = fixture.Open();
        RefBytesSnapshot? saved = null;
        var result = store.ReadRefHistory(fixture.Id(7), new(1, 5), snapshot => { saved = snapshot; return false; });
        Assert.Equal(HistoryEnd.VisitorStopped, result.Unwrap());
        Assert.Equal(revisions[1], saved!.Revision);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void ExhaustedSnapshotBudgetDoesNotProbeAnOlderInvalidRecord() {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.WriteMirror(7, null, new HistoryRecord([1], Tag: 2), new HistoryRecord([2]));
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(fixture.Id(7), new(1, long.MaxValue), snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { revisions[1] }, delivered);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void SnapshotBudgetAtChildInitialDoesNotProbeItsMissingSource() {
        using var fixture = new HistoryFixture();
        RefRevision child = fixture.WriteMirror(2, fixture.DeclaredInitial(19, 1), new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        var result = store.ReadRefHistory(child.RefId, new(1, long.MaxValue), _ => true);
        Assert.Equal("VersionStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        fixture.AssertReleased(2);
    }

    [Fact]
    public void InheritedAnchorRepeatedCompleteReadsAreChargedAndExactTwelveStepsComplete() {
        using var fixture = new HistoryFixture();
        RefRevision source = fixture.SeedPublic(1, [1])[0];
        RefRevision child = fixture.WriteMirror(2, source, new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        // Child: enter/header 3 + Snapshot MoveNext/full 2 = 5.
        // Source: enter/header 3 + GetScanBoundaryAfter full 1 + boundary ScanReverse full 1
        // + anchor MoveNext 1 + Snapshot full 1 = 7. The three actual anchor reads each cost a step.
        Assert.Equal(new[] { child, source }, HistoryFixture.Collect(store, child.RefId, new(2, 12)).Select(snapshot => snapshot.Revision));
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, new(2, 11), snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        fixture.AssertReleased(1, 2);
    }

    [Fact]
    public void WorkBudgetAtChildInitialDoesNotResolveMissingSourceAndStopStillWins() {
        using var fixture = new HistoryFixture();
        RefRevision child = fixture.WriteMirror(2, fixture.DeclaredInitial(19, 1), new HistoryRecord([1]))[0];
        using var store = fixture.Open();
        Assert.Equal("VersionStore.HistoryBudgetExceeded", store.ReadRefHistory(child.RefId, new(2, 5), _ => true).Error!.ErrorCode);
        Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(child.RefId, new(2, 5), _ => false).Unwrap());
        fixture.AssertReleased(2);
    }

    [Fact]
    public void FalseForwardMoveAttemptIsChargedAndMissingHeaderCannotBeReportedWithOnlyEntryBudget() {
        using var fixture = new HistoryFixture();
        using (IRbfFile empty = RbfFile.CreateNew(fixture.RefPath(7), RbfCacheMode.Off)) { empty.DurableFlush(); }
        using var store = fixture.Open();
        Assert.Equal("VersionStore.HistoryBudgetExceeded", store.ReadRefHistory(fixture.Id(7), new(1, 1), _ => true).Error!.ErrorCode);
        Assert.Equal("VersionStore.InvalidRefHeader", store.ReadRefHistory(fixture.Id(7), new(1, 2), _ => true).Error!.ErrorCode);
        fixture.AssertReleased(7);
    }

    [Fact]
    public void FalseReverseMoveAttemptIsChargedAndItsTerminationErrorIsPropagated() {
        using var fixture = new HistoryFixture();
        RefRevision[] revisions = fixture.SeedPublic(7, HistoryFixture.Bytes(), [2]);
        fixture.FlipTrailer(revisions[0]);
        using var store = fixture.Open();
        var delivered = new List<RefRevision>();
        // Five steps deliver the head; the sixth MoveNext attempts the bad older TrailerCodeword.
        var tooSmall = store.ReadRefHistory(fixture.Id(7), new(2, 5), snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", tooSmall.Error!.ErrorCode);
        Assert.Equal(new[] { revisions[1] }, delivered);
        delivered.Clear();
        var terminated = store.ReadRefHistory(fixture.Id(7), new(2, 6), snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.True(terminated.IsFailure);
        Assert.StartsWith("Rbf.", terminated.Error!.ErrorCode);
        Assert.Equal(new[] { revisions[1] }, delivered);
        fixture.AssertReleased(7);
    }
}
