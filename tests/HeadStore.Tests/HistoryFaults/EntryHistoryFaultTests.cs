using Atelia.HeadStore.Tests.ConditionalUpdateFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.HistoryFaults;

public sealed class EntryHistoryFaultTests {
    private static readonly HeadHistoryBudget Unlimited = new(long.MaxValue, long.MaxValue);
    private static HeadEntryId Id(HistorySeed seed, int index = 0) => HeadEntryId.Create(seed.HeadId, seed.Tickets[index]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothHistoryEntrypointsRejectBothNestedEntrypoints(bool outerExact, bool innerExact) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, [1]);
        HeadEntryId anchor = Id(seed);
        bool Visit(HeadEntry entry) {
            var nested = innerExact
                ? f.Store.ReadEntryHistory(anchor, Unlimited, _ => throw new InvalidOperationException())
                : f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => throw new InvalidOperationException());
            Assert.Equal("HeadStore.HistoryActive", nested.Error!.ErrorCode);
            return true;
        }
        var result = outerExact
            ? f.Store.ReadEntryHistory(anchor, Unlimited, Visit)
            : f.Store.ReadHeadHistory(seed.HeadId, Unlimited, Visit);
        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Single(f.Files.Opened);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
    }

    [Fact]
    public void InvalidArgumentsAndPreCancellationNeverAccessFiles() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, [1]);
        HeadEntryId anchor = Id(seed);
        Assert.Equal("HeadStore.HeadNotFound", f.Store.ReadEntryHistory(default, Unlimited, _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.ArgumentError", f.Store.ReadEntryHistory(anchor, Unlimited, null!).Error!.ErrorCode);
        Assert.Equal("HeadStore.ArgumentError", f.Store.ReadEntryHistory(anchor, new(0, 1), _ => true).Error!.ErrorCode);
        Assert.Equal("HeadStore.ArgumentError", f.Store.ReadEntryHistory(anchor, new(1, 0), _ => true).Error!.ErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observed = Assert.Throws<OperationCanceledException>(() =>
            f.Store.ReadEntryHistory(anchor, Unlimited, _ => throw new InvalidOperationException(), cancellation.Token));
        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.Empty(f.Operations);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadEntryHistory(anchor, Unlimited, _ => true).Unwrap());
    }

    [Theory]
    [InlineData(3, 0, 0, 0)]
    [InlineData(4, 1, 0, 1)]
    [InlineData(5, 1, 1, 2)]
    [InlineData(6, 1, 1, 2)]
    [InlineData(7, 1, 1, 3)]
    public void ExactAnchorReadsAreChargedBeforeEachActualOperation(long work, int boundaryCalls,
        int reverseCalls, int completeReads) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload(), HistoryFaultFixture.Payload(1));
        int visits = 0;
        var result = f.Store.ReadEntryHistory(Id(seed), new(1, work), _ => { visits++; return true; });
        if (work == 7) { Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap()); }
        else { Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode); }
        Assert.Equal(work == 7 ? 1 : 0, visits);
        var opened = Assert.Single(f.Files.Opened);
        Assert.Equal(boundaryCalls, opened.GetBoundaryCalls);
        Assert.Equal(reverseCalls, opened.BoundaryReverseCalls);
        Assert.Equal(work == 3 ? 0 : 1, opened.FrameInfoCalls);
        Assert.Equal(completeReads, opened.CountCompleteReads(seed.Tickets[0]));
        Assert.Equal(0, opened.CountCompleteReads(seed.Tickets[1]));
        Assert.Equal(1, opened.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackReadQueriesBorrowSameFileAndKeepOriginalAnchor(bool readOnly) {
        using var f = new HistoryFaultFixture(readOnly);
        HistorySeed seed = f.Seed(1, [1], [2]);
        HeadEntryId anchor = Id(seed);
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadEntryHistory(anchor, Unlimited, entry => {
            Assert.Equal(anchor, entry.Id);
            Assert.Equal(Id(seed, 1), f.Store.ReadHead(seed.HeadId).Unwrap().Id);
            Assert.Equal(anchor, f.Store.ReadEntry(anchor).Unwrap().Id);
            Assert.Single(f.Files.Opened);
            Assert.Equal(0, f.Files.Opened[0].DisposeCalls);
            return true;
        }).Unwrap());
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LastCallbackCancellationAndDisposeCannotReturnNormally(bool keepGoing) {
        using (var f = new HistoryFaultFixture()) {
            var seed = f.Seed(1, [1]);
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => f.Store.ReadEntryHistory(Id(seed), Unlimited,
                _ => { cancellation.Cancel(); return keepGoing; }, cancellation.Token));
            Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
            Assert.Equal(0, f.Lock.DisposeCalls);
            Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadEntryHistory(Id(seed), Unlimited, _ => true).Unwrap());
        }
        using (var f = new HistoryFaultFixture()) {
            var seed = f.Seed(1, [1]);
            Assert.Throws<ObjectDisposedException>(() => f.Store.ReadEntryHistory(Id(seed), Unlimited,
                _ => { f.Store.Dispose(); return keepGoing; }));
            Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
            Assert.Equal(1, f.Lock.DisposeCalls);
            f.Data.EnsureUsable();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalCloseFailurePreventsNormalEndAndReleasesLockLast(bool keepGoing) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, [1]);
        HistorySeed cached = f.Seed(2, [2]);
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var failure = new IOException("exact history temporary close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() =>
            f.Store.ReadEntryHistory(Id(seed), Unlimited, _ => keepGoing)));
        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        f.Data.EnsureUsable();
    }

    [Fact]
    public void LastVisitorExceptionPrecedesCleanupFailuresAndOwnsItsRetainedEntry() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, [1]);
        var primary = new IOException("visitor");
        var close = new IOException("close");
        var lockClose = new IOException("lock");
        f.Files.ConfigureOpen = file => file.DisposeFailure = close;
        f.Lock.DisposeFailure = lockClose;
        HeadEntry? retained = null;
        var observed = Assert.Throws<AggregateException>(() => f.Store.ReadEntryHistory(Id(seed), Unlimited,
            entry => { retained = entry; throw primary; }));
        Assert.Equal(new Exception[] { primary, close, lockClose }, observed.InnerExceptions);
        Assert.Equal(new[] { "close:1", "lock:close" }, Closes(f));
        Assert.Equal(new byte[] { 1 }, retained!.Payload.ToArray());
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationDuringFinalCloseCannotReturnNormally(bool keepGoing) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, [1]);
        using var cancellation = new CancellationTokenSource();
        f.Files.ConfigureOpen = file => file.AfterDispose = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() =>
            f.Store.ReadEntryHistory(Id(seed), Unlimited, _ => keepGoing, cancellation.Token));
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryCuPeerCloseFailureRejectsAnchorAndClosesEveryResourceOnce(bool failReadToo) {
        using var f = new ConditionalUpdateFaultFixture();
        var group = ConditionalUpdateReuseAndMemberTests.CreateCompleteMirror(f);
        var store = f.Open(readOnly: true);
        store.ReadHead(3u).Unwrap();
        var primary = new IOException("exact history peer read");
        var close = new IOException("exact history peer close");
        f.Operations.ConfigureOpen = file => {
            if (file.Id == 2) {
                file.DisposeFailure = close;
                if (failReadToo) { file.ReadFailure = primary; }
            }
        };
        int visits = 0;
        Exception observed = Assert.ThrowsAny<Exception>(() =>
            store.ReadEntryHistory(HeadEntryId.Create(f.Id(1), group.A), Unlimited,
                _ => { visits++; return true; }));
        if (failReadToo) {
            Assert.Equal(new Exception[] { primary, close }, Assert.IsType<AggregateException>(observed).Flatten().InnerExceptions);
        }
        else { Assert.Same(close, observed); }
        Assert.Equal(0, visits);
        f.AssertFaultCleanup();
        Assert.True(f.Events.IndexOf("close:2") < f.Events.IndexOf("close:3"));
        Assert.Equal("lock:close", f.Events[^1]);
    }

    [Fact]
    public void BudgetFailureAfterOpeningCuPeerReleasesBothTemporaryHandlesWithoutFaultingOwner() {
        using var f = new ConditionalUpdateFaultFixture();
        var group = ConditionalUpdateReuseAndMemberTests.CreateCompleteMirror(f);
        var store = f.Open(readOnly: true);
        HeadEntryId anchor = HeadEntryId.Create(f.Id(1), group.A);
        int visits = 0;
        // Local exact qualification costs 7; step 8 opens the peer, before its header work.
        var result = store.ReadEntryHistory(anchor, new(1, 8), _ => { visits++; return true; });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(0, visits);
        Assert.Equal(new uint[] { 1, 2 }, f.Operations.Files.Select(file => file.Id));
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Empty(ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(0, f.Operations.Locks[^1].DisposeCalls);
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadEntryHistory(anchor, Unlimited, _ => false).Unwrap());
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        f.Data.EnsureUsable();
    }

    private static string[] Closes(HistoryFaultFixture f) => f.Operations
        .Where(op => op.StartsWith("close:", StringComparison.Ordinal) || op == "lock:close").ToArray();
}
