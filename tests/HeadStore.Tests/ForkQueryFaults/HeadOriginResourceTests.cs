using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.ForkQueryFaults;

public sealed class HeadOriginResourceTests {
    private static readonly HeadHistoryBudget Unlimited = new(long.MaxValue, long.MaxValue);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyTargetHeaderIsReadWithoutDiscoverySourceOrEntryAccess(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed source = f.Seed(1);
        ForkQuerySeed target = f.Seed(18, new ForkOrigin(source.HeadId, source.Tickets[0]), source.Payloads[0]);
        ForkQuerySeed unrelated = f.Seed(19);
        f.CorruptPayload(target, 0);
        File.WriteAllBytes(f.HeadPath(1), [0]); // A source open would throw before reaching its header.
        File.WriteAllBytes(f.HeadPath(19), [0]);
        f.Order(unrelated, source, target);

        HeadEntryId declared = HeadEntryId.Create(source.HeadId, source.Tickets[0]);
        Assert.Equal(declared, f.Store.ReadHeadOrigin(target.HeadId).Unwrap().SourceEntryId);

        Assert.Equal(0, f.Files.ListCalls);
        Assert.Equal(0, f.Files.VisitCalls);
        Assert.Empty(f.Operations.Enumerators);
        ForkQueryHeadFile accessed = Assert.Single(f.Files.Opened);
        Assert.Equal(18u, accessed.LocalId);
        Assert.True(accessed.CountCompleteReads(target.HeaderTicket) > 0);
        Assert.Equal(0, accessed.CountCompleteReads(target.Tickets[0]));
        Assert.Equal(0, accessed.ReverseCalls);
        Assert.Equal(0, accessed.FrameInfoCalls);
        Assert.Equal(0, accessed.BoundaryCalls);
        Assert.Equal(1, accessed.DisposeCalls);
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Store.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedReaderIsBorrowedWithoutReopenOrClose(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        f.Store.ReadHead(seed.HeadId).Unwrap();
        ForkQueryHeadFile owned = Assert.Single(f.Files.Opened);
        Assert.Null(f.Store.ReadHeadOrigin(seed.HeadId).Unwrap().SourceEntryId);
        Assert.Null(f.Store.ReadHeadOrigin(seed.HeadId).Unwrap().SourceEntryId);
        Assert.Single(f.Files.Opened);
        Assert.Equal(0, owned.DisposeCalls);
        Assert.Equal(0, f.Files.VisitCalls);
        Assert.Equal(0, f.Files.ListCalls);
        Assert.Equal(seed.Tickets[0], f.Store.ReadHead(seed.HeadId).Unwrap().Id.Ticket);
        f.Store.Dispose();
        Assert.Equal(1, owned.DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryReaderIsBorrowedAndTemporaryQueryDoesNotReplaceHistorySlot(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed source = f.Seed(1, null, ForkQueryFaultFixture.Payload(), ForkQueryFaultFixture.Payload(1));
        ForkQuerySeed child = f.Seed(18, new ForkOrigin(source.HeadId, source.Tickets[0]), source.Payloads[0]);
        int visits = 0;
        Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(source.HeadId, Unlimited, entry => {
            visits++;
            ForkQueryHeadFile history = f.Files.Opened[0];
            Assert.Null(f.Store.ReadHeadOrigin(source.HeadId).Unwrap().SourceEntryId);
            Assert.Equal(HeadEntryId.Create(source.HeadId, source.Tickets[0]),
                f.Store.ReadHeadOrigin(child.HeadId).Unwrap().SourceEntryId);
            Assert.Single(f.Files.Opened, file => file.LocalId == 1);
            Assert.Equal(0, history.DisposeCalls);
            Assert.Equal(1, f.Files.ActiveFiles);
            Assert.All(f.Files.Opened.Where(file => file.LocalId == 18), file => Assert.Equal(1, file.DisposeCalls));
            if (!readOnly) {
                Assert.Equal("HeadStore.HistoryActive", f.Store.PublishHead(source.HeadId, [1], out var outcome).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            return true;
        }).Unwrap());
        Assert.Equal(2, visits);
        Assert.Equal(2, f.Files.MaximumActiveFiles);
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(0, f.Files.VisitCalls);
        Assert.Equal(0, f.Files.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FactoryFailureFaultsOnlyWritableRecoveryPath(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        var primary = new IOException("target open");
        f.Files.OpenFailure = primary;
        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ReadHeadOrigin(seed.HeadId)));
        Assert.Empty(f.Files.Opened);
        Assert.Equal(readOnly ? 0 : 1, f.Lock.DisposeCalls);
        f.Files.OpenFailure = null;
        if (readOnly) { Assert.Null(f.Store.ReadHeadOrigin(seed.HeadId).Unwrap().SourceEntryId); }
        else { Assert.Throws<InvalidOperationException>(() => f.Store.ReadHeadOrigin(seed.HeadId)); }
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderReadExceptionWithSuccessfulCleanupKeepsOwnerHealthy(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        var primary = new IOException("target header");
        f.Files.ConfigureOpen = file => file.ScanFailure = primary;
        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ReadHeadOrigin(seed.HeadId)));
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ConfigureOpen = null;
        Assert.Null(f.Store.ReadHeadOrigin(seed.HeadId).Unwrap().SourceEntryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryCloseFailurePreventsSuccessAndFaultsOwner(bool readOnly) {
        using var f = new ForkQueryFaultFixture(readOnly);
        ForkQuerySeed seed = f.Seed(1);
        var close = new IOException("target close");
        f.Files.ConfigureOpen = file => file.DisposeFailure = close;
        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadHeadOrigin(seed.HeadId)));
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(new[] { "ref:close:1", "lock:close" }, ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadHeadOrigin(seed.HeadId));
        f.Store.Dispose();
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void PrimaryHeaderErrorPrecedesTemporaryOwnedAndLockCleanupErrors() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed target = f.Seed(1);
        ForkQuerySeed cached = f.Seed(2);
        f.Store.ReadHead(cached.HeadId).Unwrap();
        var primary = new IOException("target header");
        var temporaryClose = new IOException("target close");
        var ownedClose = new IOException("owned close");
        var lockClose = new IOException("lock close");
        f.Files.Opened[0].DisposeFailure = ownedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Files.ConfigureOpen = file => { file.ScanFailure = primary; file.DisposeFailure = temporaryClose; };
        var observed = Assert.Throws<AggregateException>(() => f.Store.ReadHeadOrigin(target.HeadId));
        Assert.Equal(new Exception[] { primary, temporaryClose, ownedClose, lockClose },
            ForkQueryDiscoveryFaultTests.Flatten(observed));
        Assert.Equal(new[] { "ref:close:1", "ref:close:2", "lock:close" }, ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ReadHeadOrigin(target.HeadId));
        f.Store.Dispose();
        f.Data.EnsureUsable();
    }

    [Fact]
    public void QueryCloseFailureInsideHistoryReleasesBothReadersAndOwnerLockExactlyOnce() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed history = f.Seed(1);
        ForkQuerySeed target = f.Seed(2);
        var close = new IOException("nested origin close");
        f.Files.ConfigureOpen = file => { if (file.LocalId == 2) { file.DisposeFailure = close; } };
        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadHeadHistory(history.HeadId, Unlimited, _ => {
            f.Store.ReadHeadOrigin(target.HeadId).Unwrap();
            return true;
        })));
        Assert.Equal(new[] { "ref:close:2", "ref:close:1", "lock:close" }, ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Store.Dispose();
        f.Data.EnsureUsable();
    }
}
