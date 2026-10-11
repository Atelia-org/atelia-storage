using Atelia.VersionStore.Internal.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.ForkQueryFaults;

public sealed class ForkQueryResourceFaultTests {
    private static readonly HistoryBudget UnlimitedHistory = new(long.MaxValue, long.MaxValue);

    [Fact]
    public void RealRbfFactoryRejectionFaultsWriterAndDoesNotChangePreviouslyDeliveredGraph() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed source = f.Seed(1);
        ForkQuerySeed child = f.Seed(18, new ForkOrigin(source.RefId, source.Tickets[0]), source.Payloads[0]);
        f.Order(source, child);
        IReadOnlyList<ForkInfo> retained = f.Store.ListForks(2).Unwrap();
        // Both query readers have already closed; no active FileShare.None writer is reopened or overwritten.
        Assert.Equal(0, f.Files.ActiveFiles);
        File.WriteAllBytes(f.RefPath(1), [0]);

        Assert.Throws<InvalidDataException>(() => f.Store.ListForks(2));

        Assert.Throws<InvalidOperationException>(() => f.Store.ListForks(2));
        ForkInfo edge = Assert.Single(retained);
        Assert.Equal(child.RefId, edge.ChildRefId);
        Assert.Equal(source.RefId, edge.SourceRefId);
        Assert.Equal(source.Tickets[0], edge.SourceSnapshotTicket);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Enumerators, enumerator => Assert.Equal(1, enumerator.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Events[^1]);
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal(source.Tickets[0], retained[0].SourceSnapshotTicket);
    }

    [Fact]
    public void RealReadOnlyEofRejectionCannotBeBypassedByHeaderQueryAndKeepsOwnerHealthy() {
        using var f = new ForkQueryFaultFixture(readOnly: true);
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        string path = f.RefPath(1);
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]); // Complete body without final Key/Fence.

        Assert.Throws<InvalidDataException>(() => f.Store.ListForks(1));

        Assert.Empty(f.Files.Opened);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(complete[..^8], File.ReadAllBytes(path));
        File.WriteAllBytes(path, complete);
        Assert.Empty(f.Store.ListForks(1).Unwrap());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void RealCorruptSnapshotsDoNotBlockHeaderGraphOrCauseAnySnapshotRead() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed source = f.Seed(1, null, ForkQueryFaultFixture.Payload(), ForkQueryFaultFixture.Payload(1));
        var origin = new ForkOrigin(source.RefId, source.Tickets[0]);
        ForkQuerySeed child = f.Seed(18, origin, source.Payloads[0]);
        f.CorruptPayload(source, 0);
        f.CorruptPayload(source, 1);
        f.CorruptPayload(child, 0);
        f.Order(child, source);

        ForkInfo edge = Assert.Single(f.Store.ListForks(2).Unwrap());

        Assert.Equal(child.RefId, edge.ChildRefId);
        Assert.Equal(source.RefId, edge.SourceRefId);
        Assert.Equal(source.Tickets[0], edge.SourceSnapshotTicket);
        Assert.Equal(2, f.Files.Opened.Count);
        Assert.Equal(1, f.Files.MaximumActiveFiles);
        foreach (ForkQueryRefFile file in f.Files.Opened) {
            ForkQuerySeed seed = file.LocalId == 1 ? source : child;
            Assert.True(file.CountCompleteReads(seed.HeaderTicket) > 0);
            Assert.All(seed.Tickets, ticket => Assert.Equal(0, file.CountCompleteReads(ticket)));
            Assert.Equal(0, file.ReverseCalls);
            Assert.Equal(0, file.FrameInfoCalls);
            Assert.Equal(0, file.BoundaryCalls);
            Assert.Equal(1, file.DisposeCalls);
        }
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderAccessExceptionWithHealthyCleanupDoesNotFaultOwner(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        var primary = new IOException("header scan");
        f.Files.ConfigureOpen = file => file.ScanFailure = primary;

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ListForks(1)));

        Assert.Equal(new[] { "directory:close", "ref:close:1" }, ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ConfigureOpen = null;
        Assert.Empty(f.Store.ListForks(1).Unwrap());
        Assert.Equal(seed.Tickets[0], f.Store.ReadRef(seed.RefId).Unwrap().Revision.Ticket);
        if (!readOnly) { Assert.True(f.Store.PublishRef(seed.RefId, [5], out _).IsSuccess); }
    }

    [Fact]
    public void HeaderAccessErrorPrecedesEnumeratorTemporaryCachedAndLockCloseErrors() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        ForkQuerySeed cached = f.Seed(2);
        f.Store.ReadRef(cached.RefId).Unwrap();
        ForkQueryRefFile cachedFile = Assert.Single(f.Files.Opened);
        f.Order(seed, cached);
        var primary = new IOException("header scan");
        var enumClose = new IOException("enumerator close");
        var temporaryClose = new IOException("query temporary close");
        var cachedClose = new IOException("cached close");
        var lockClose = new IOException("lock close");
        cachedFile.DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Operations.ConfigureEnumerator = enumerator => enumerator.DisposeFailure = enumClose;
        f.Files.ConfigureOpen = file => { file.ScanFailure = primary; file.DisposeFailure = temporaryClose; };

        var observed = Assert.Throws<AggregateException>(() => f.Store.ListForks(2));

        Assert.Equal(new Exception[] { primary, enumClose, temporaryClose, cachedClose, lockClose },
            ForkQueryDiscoveryFaultTests.Flatten(observed));
        Assert.Equal(new[] { "directory:close", "ref:close:1", "ref:close:2", "lock:close" },
            ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadRef(seed.RefId));
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void LastSuccessfulHeaderCloseFailurePreventsResultAndClosesBeforeEnumeratorAndOwnerLock() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        ForkQuerySeed cached = f.Seed(2);
        f.Store.ReadRef(cached.RefId).Unwrap();
        f.Order(seed, cached);
        var close = new IOException("query temporary close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ListForks(2)));

        Assert.Equal(new[] { "ref:close:1", "directory:close", "ref:close:2", "lock:close" },
            ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Throws<InvalidOperationException>(() => f.Store.ListForks(2));
        f.Store.Dispose();
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryBorrowsCachedReaderWithoutReopeningOrClosingIt(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        f.Store.ReadRef(seed.RefId).Unwrap();
        ForkQueryRefFile cached = Assert.Single(f.Files.Opened);

        Assert.Empty(f.Store.ListForks(1).Unwrap());
        Assert.Empty(f.Store.ListForks(1).Unwrap());

        Assert.Single(f.Files.Opened);
        Assert.Equal(0, cached.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(seed.Tickets[0], f.Store.ReadRef(seed.RefId).Unwrap().Revision.Ticket);
        f.Store.Dispose();
        Assert.Equal(1, cached.DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryCallbackBorrowsItsCurrentTemporaryAndQueryOwnsAtMostOneOtherReader(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed source = f.Seed(1, null, ForkQueryFaultFixture.Payload(), ForkQueryFaultFixture.Payload(1));
        ForkQuerySeed child = f.Seed(18, new ForkOrigin(source.RefId, source.Tickets[0]), source.Payloads[0]);
        ForkQuerySeed other = f.Seed(19);
        f.Order(child, other, source);
        var visited = new List<RefBytesSnapshot>();

        HistoryEnd end = f.Store.ReadRefHistory(source.RefId, UnlimitedHistory, snapshot => {
            visited.Add(snapshot);
            ForkQueryRefFile history = f.Files.Opened[0];
            Assert.Equal(1u, history.LocalId);
            Assert.Equal(0, history.DisposeCalls);
            ForkInfo edge = Assert.Single(f.Store.ListForks(3).Unwrap());
            Assert.Equal(child.RefId, edge.ChildRefId);
            Assert.Equal(0, history.DisposeCalls);
            Assert.Equal(1, f.Files.ActiveFiles);
            Assert.Single(f.Files.Opened, file => file.LocalId == 1);
            Assert.All(f.Files.Opened.Where(file => file.LocalId != 1), file => Assert.Equal(1, file.DisposeCalls));
            return true;
        }).Unwrap();

        Assert.Equal(HistoryEnd.Complete, end);
        Assert.Equal(new[] { source.Tickets[1], source.Tickets[0] }, visited.Select(snapshot => snapshot.Revision.Ticket));
        Assert.Equal(2, f.Files.MaximumActiveFiles); // One history reader plus at most one independent query reader.
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(HistoryEnd.Complete, f.Store.ReadRefHistory(source.RefId, UnlimitedHistory, _ => true).Unwrap());
    }

    [Fact]
    public void CancelledQueryInsideHistoryCallbackKeepsHistorySlotAndContinuesBothSnapshots() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed source = f.Seed(1, null, ForkQueryFaultFixture.Payload(), ForkQueryFaultFixture.Payload(1));
        ForkQuerySeed other = f.Seed(2);
        f.Order(other, source);
        int visits = 0;

        HistoryEnd end = f.Store.ReadRefHistory(source.RefId, UnlimitedHistory, _ => {
            visits++;
            using var cancellation = new CancellationTokenSource();
            f.Files.ConfigureOpen = file => {
                if (file.LocalId == 2) { file.AfterDispose = cancellation.Cancel; }
            };
            Assert.Throws<OperationCanceledException>(() => f.Store.ListForks(2, cancellation.Token));
            Assert.Equal(0, f.Files.Opened[0].DisposeCalls);
            Assert.Equal(1, f.Files.ActiveFiles);
            f.Files.ConfigureOpen = null;
            return true;
        }).Unwrap();

        Assert.Equal(HistoryEnd.Complete, end);
        Assert.Equal(2, visits);
        Assert.Equal(2, f.Files.MaximumActiveFiles);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.True(f.Store.PublishRef(source.RefId, [6], out _).IsSuccess);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void QueryCloseFailureInsideHistoryCallbackReleasesBothSlotsOnceAndLockLast() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed source = f.Seed(1, null, ForkQueryFaultFixture.Payload(), ForkQueryFaultFixture.Payload(1));
        ForkQuerySeed other = f.Seed(2);
        f.Order(other, source);
        var close = new IOException("nested query close");
        f.Files.ConfigureOpen = file => { if (file.LocalId == 2) { file.DisposeFailure = close; } };

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadRefHistory(source.RefId, UnlimitedHistory,
            _ => { f.Store.ListForks(2).Unwrap(); return true; })));

        Assert.Equal(new[] { "ref:close:2", "directory:close", "ref:close:1", "lock:close" },
            ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(2, f.Files.MaximumActiveFiles);
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactoryOpenExceptionFaultsOnlyWritableRecoveryPath(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        var primary = new IOException("factory open");
        f.Files.OpenFailure = primary;

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ListForks(1)));

        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(readOnly ? 0 : 1, f.Lock.DisposeCalls);
        f.Files.OpenFailure = null;
        if (readOnly) { Assert.Empty(f.Store.ListForks(1).Unwrap()); }
        else { Assert.Throws<InvalidOperationException>(() => f.Store.ListForks(1)); }
        f.Data.EnsureUsable();
    }
}
