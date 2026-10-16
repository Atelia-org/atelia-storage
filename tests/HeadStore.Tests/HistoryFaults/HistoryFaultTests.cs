using Xunit;

namespace Atelia.HeadStore.Tests.HistoryFaults;

public sealed class HistoryFaultTests {
    private static readonly HeadHistoryBudget Unlimited = new(long.MaxValue, long.MaxValue);

    [Fact]
    public void RealPayloadCrcFailureReleasesTemporaryReaderWithoutFaultingWriter() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload(), HistoryFaultFixture.Payload(1));
        f.CorruptPayload(seed, 0);
        var received = new List<HeadEntry>();

        var result = f.Store.ReadHeadHistory(seed.HeadId, Unlimited, entry => { received.Add(entry); return true; });

        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Single(received);
        Assert.Equal(seed.Tickets[1], received[0].Id.Ticket);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        // A checked current read does not touch the corrupt initial payload, and a new publication still works.
        Assert.Equal(seed.Tickets[1], f.Store.ReadHead(seed.HeadId).Unwrap().Id.Ticket);
        Assert.True(f.Store.PublishHead(seed.HeadId, [7], out PublicationOutcome outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(seed.Payloads[1], received[0].Payload.ToArray());
    }

    [Fact]
    public void ScanEntryExceptionIsPrimaryAndLeavesWriterAndHistoryGuardHealthy() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        var failure = new IOException("read scan entry");
        f.Files.ConfigureOpen = file => file.ScanFailure = failure;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true)));

        Assert.Contains("forward:1", f.Operations);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ConfigureOpen = null;
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true).Unwrap());
        Assert.True(f.Store.PublishHead(seed.HeadId, [8], out PublicationOutcome outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void VisitorExceptionDoesNotFaultOrRetainTemporaryReader() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        var primary = new InvalidOperationException("application decoder");
        HeadEntry? retained = null;

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() =>
            f.Store.ReadHeadHistory(seed.HeadId, Unlimited, entry => { retained = entry; throw primary; })));

        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(seed.Payloads[0], retained!.Payload.ToArray());
        Assert.Equal(HeadHistoryEnd.VisitorStopped, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => false).Unwrap());
        Assert.True(f.Store.PublishHead(seed.HeadId, [9], out _).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LastVisitorCancellationReleasesReaderAndKeepsOwnerHealthy(bool visitorResult) {
        using var f = new HistoryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());

        var observed = Assert.Throws<OperationCanceledException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited,
            _ => { cancellation.Cancel(); return visitorResult; }, cancellation.Token));

        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true).Unwrap());
        Assert.True(f.Store.PublishHead(seed.HeadId, [10], out _).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NecessaryTemporaryCloseFailurePreventsNormalEndAndCleansOwnerLockLast(bool visitorResult) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var closeFailure = new IOException("temporary reader close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = closeFailure;

        Assert.Same(closeFailure, Assert.Throws<IOException>(() =>
            f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => visitorResult)));

        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadHead(seed.HeadId));
        f.Data.EnsureUsable();
        f.Store.Dispose();
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void FailedCloseDuringSourceHopDoesNotOpenSourceOrRetryTemporary() {
        using var f = new HistoryFaultFixture();
        HistorySeed source = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed child = f.SeedFork(2, source);
        HistorySeed cached = f.Seed(3, HistoryFaultFixture.Payload(3));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var closeFailure = new IOException("child hop close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = closeFailure;
        var received = new List<HeadEntry>();

        Assert.Same(closeFailure, Assert.Throws<IOException>(() => f.Store.ReadHeadHistory(child.HeadId, Unlimited,
            entry => { received.Add(entry); return true; })));

        Assert.Single(received);
        Assert.Equal(child.HeadId, received[0].Id.HeadId);
        Assert.DoesNotContain(f.Files.Opened, file => file.LocalId == 1);
        Assert.DoesNotContain("resolve:1", f.Operations);
        Assert.Equal(new[] { "close:2", "close:3", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOrVisitorExceptionPrecedesEveryCleanupFailure(bool throwInVisitor) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var primary = new IOException(throwInVisitor ? "visitor" : "scan");
        var temporaryClose = new IOException("temporary close");
        var cachedClose = new IOException("cached close");
        var lockClose = new IOException("lock close");
        f.Files.Opened[0].DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Files.ConfigureOpen = file => {
            file.DisposeFailure = temporaryClose;
            if (!throwInVisitor) { file.ScanFailure = primary; }
        };

        var observed = Assert.Throws<AggregateException>(() =>
            f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => throw primary));

        Assert.Equal(new Exception[] { primary, temporaryClose, cachedClose, lockClose }, observed.InnerExceptions);
        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void CancellationPrecedesTemporaryCloseFailureAndFaultsOnlyForCleanup() {
        using var f = new HistoryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        var closeFailure = new IOException("temporary close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = closeFailure;

        var observed = Assert.Throws<AggregateException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited,
            _ => { cancellation.Cancel(); return false; }, cancellation.Token));

        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(observed.InnerExceptions[0]).CancellationToken);
        Assert.Same(closeFailure, observed.InnerExceptions[1]);
        Assert.Equal(new[] { "close:1", "lock:close" }, Closes(f));
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadHead(seed.HeadId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallbackDisposeClearsTemporarySlotSoFinallyCannotCloseItAgain(bool visitorResult) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();

        Assert.Throws<ObjectDisposedException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited,
            _ => { f.Store.Dispose(); return visitorResult; }));

        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        f.Data.EnsureUsable();
    }

    [Fact]
    public void CallbackDisposeCleanupAggregateIsPreservedWithoutAnotherClose() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var temporaryClose = new IOException("temporary close");
        var cachedClose = new IOException("cached close");
        var lockClose = new IOException("lock close");
        f.Files.Opened[0].DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Files.ConfigureOpen = file => file.DisposeFailure = temporaryClose;

        var observed = Assert.Throws<AggregateException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited,
            _ => { f.Store.Dispose(); return false; }));

        Assert.Equal(new Exception[] { temporaryClose, cachedClose, lockClose }, observed.InnerExceptions);
        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Store.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisitorCurrentReadReusesMatchingTemporaryHandle(bool readOnly) {
        using var f = new HistoryFaultFixture(readOnly);
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload(), HistoryFaultFixture.Payload(1));
        int visits = 0;

        var result = f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => {
            visits++;
            Assert.Equal(seed.Tickets[1], f.Store.ReadHead(seed.HeadId).Unwrap().Id.Ticket);
            Assert.Single(f.Files.Opened);
            Assert.Equal(0, f.Files.Opened[0].DisposeCalls);
            return true;
        });

        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Equal(2, visits);
        Assert.Single(f.Files.Opened);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Store.ReadHead(seed.HeadId).Unwrap();
        Assert.Equal(2, f.Files.Opened.Count);
        Assert.Equal(0, f.Files.Opened[1].DisposeCalls);
    }

    [Fact]
    public void HistoryBorrowsExistingCacheWithoutClosingIt() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload(), HistoryFaultFixture.Payload(1));
        f.Store.ReadHead(seed.HeadId).Unwrap();

        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited,
            _ => { f.Store.ReadHead(seed.HeadId).Unwrap(); return true; }).Unwrap());

        Assert.Single(f.Files.Opened);
        Assert.Equal(0, f.Files.Opened[0].DisposeCalls);
        f.Store.ReadHead(seed.HeadId).Unwrap();
        Assert.Single(f.Files.Opened);
        f.Store.Dispose();
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void MultiHeadTraversalRetainsOnlyCurrentTemporaryHandleAndDoesNotPopulatePermanentCache() {
        using var f = new HistoryFaultFixture();
        HistorySeed source = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed child = f.SeedFork(2, source);
        HistorySeed grandchild = f.SeedFork(3, child);
        HistorySeed tip = f.SeedFork(4, grandchild);
        f.Files.BeforeOpen = (_, _) => Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        int visits = 0;

        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(tip.HeadId, Unlimited, entry => {
            visits++;
            Assert.Single(f.Files.Opened, file => file.DisposeCalls == 0);
            f.Store.ReadHead(entry.Id.HeadId).Unwrap();
            Assert.Equal(visits, f.Files.Opened.Count);
            return true;
        }).Unwrap());

        Assert.Equal(4, visits);
        Assert.Equal(4, f.Files.Opened.Count);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        var scans = f.Files.Opened.SelectMany(file => file.ScanArguments).ToArray();
        Assert.Contains(scans, scan => scan.Operation == "forward");
        Assert.Contains(scans, scan => scan.Operation == "reverse");
        Assert.Contains(scans, scan => scan.Operation == "boundary-reverse");
        Assert.All(scans, scan => Assert.True(scan.ShowTombstone));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Store.ReadHead(tip.HeadId).Unwrap();
        Assert.Equal(5, f.Files.Opened.Count);
        Assert.Equal(0, f.Files.Opened[4].DisposeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationDuringNecessaryCloseCannotProduceNormalEnd(bool visitorResult) {
        using var f = new HistoryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        f.Files.ConfigureOpen = file => file.AfterDispose = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() =>
            f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => visitorResult, cancellation.Token));

        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ConfigureOpen = null;
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true).Unwrap());
    }

    [Fact]
    public void OwnerDisposeDuringTemporaryCloseSeesAnAlreadyClearedSlot() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        f.Files.ConfigureOpen = file => file.AfterDispose = f.Store.Dispose;

        Assert.Throws<ObjectDisposedException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => false));

        Assert.Equal(new[] { "close:1", "close:2", "lock:close" }, Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void ZeroBudgetIsRejectedBeforeOpeningAnyReader(long entries, long workSteps) {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        int visits = 0;

        var result = f.Store.ReadHeadHistory(seed.HeadId, new HeadHistoryBudget(entries, workSteps),
            _ => { visits++; return true; });

        Assert.True(result.IsFailure);
        Assert.Empty(f.Files.Opened);
        Assert.Empty(f.Operations);
        Assert.Equal(0, visits);
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true).Unwrap());
    }

    [Theory]
    [InlineData(8, 0, 0, 0)]
    [InlineData(9, 1, 0, 1)]
    [InlineData(10, 1, 1, 2)]
    [InlineData(11, 1, 1, 2)]
    [InlineData(12, 1, 1, 3)]
    public void SourceHelperCompleteReadsArePaidBeforeRealRbfCalls(long workSteps, int boundaryCalls,
        int boundaryReverseCalls, int completeReads) {
        using var f = new HistoryFaultFixture();
        HistorySeed source = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed child = f.SeedFork(2, source);
        var received = new List<HeadEntry>();

        var result = f.Store.ReadHeadHistory(child.HeadId, new HeadHistoryBudget(2, workSteps),
            entry => { received.Add(entry); return true; });

        if (workSteps == 12) {
            Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
            Assert.Equal(2, received.Count);
        }
        else {
            Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
            Assert.Single(received);
        }
        HistoryFaultHeadFile sourceFile = Assert.Single(f.Files.Opened, file => file.LocalId == 1);
        Assert.Equal(1, sourceFile.DisposeCalls);
        Assert.Equal(boundaryCalls, sourceFile.GetBoundaryCalls);
        Assert.Equal(boundaryReverseCalls, sourceFile.BoundaryReverseCalls);
        Assert.Equal(workSteps == 8 ? 0 : 1, sourceFile.FrameInfoCalls);
        // These are actual reader requests, including the two helper-internal complete anchor reads.
        Assert.Equal(completeReads, sourceFile.CountCompleteReads(source.Tickets[0]));
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Fact]
    public void BackendOwnedCleanupMarkerFaultsOwnerAndClosesCachedResourcesOnce() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());
        HistorySeed cached = f.Seed(2, HistoryFaultFixture.Payload(2));
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var primary = new IOException("backend owned cleanup");
        f.Files.OpenCleanupFailure = primary;
        f.Files.OpenCleanupFailureId = 1;

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => true)));

        Assert.Single(f.Files.Opened);
        Assert.Equal(new[] { "close:2", "lock:close" }, Closes(f));
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadHead(cached.HeadId));
        f.Store.Dispose();
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void CallbackMutationGuardRejectsBeforeDataBarrierOrBackendOutput() {
        using var f = new HistoryFaultFixture();
        HistorySeed seed = f.Seed(1, HistoryFaultFixture.Payload());

        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(seed.HeadId, Unlimited, _ => {
            Assert.True(f.Store.PublishHead(seed.HeadId, [1], out PublicationOutcome publish).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, publish);
            Assert.True(f.Store.CreateHead([2], out PublicationOutcome automatic).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, automatic);
            Assert.True(f.Store.CreateHead(3, [3], out PublicationOutcome specified).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, specified);
            return true;
        }).Unwrap());

        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.DoesNotContain("prepare", f.Operations);
        Assert.DoesNotContain("pending:create", f.Operations);
        Assert.DoesNotContain(f.Operations, operation => operation.StartsWith("append:", StringComparison.Ordinal));
        Assert.True(f.Store.PublishHead(seed.HeadId, [4], out _).IsSuccess);
        Assert.Contains("data:confirm", f.Operations);
        Assert.Contains("append:1", f.Operations);
    }

    private static string[] Closes(HistoryFaultFixture fixture) => fixture.Operations
        .Where(operation => operation.StartsWith("close:", StringComparison.Ordinal) || operation == "lock:close").ToArray();
}
