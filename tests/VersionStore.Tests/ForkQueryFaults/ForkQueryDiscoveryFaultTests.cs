using Xunit;

namespace Atelia.VersionStore.Tests.ForkQueryFaults;

public sealed class ForkQueryDiscoveryFaultTests {
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveBudgetRejectsBeforeAnyDiscovery(int maxRefs) {
        using var f = new ForkQueryFaultFixture();
        Assert.Equal("VersionStore.ArgumentError", f.Store.ListForks(maxRefs).Error!.ErrorCode);
        Assert.Empty(f.Events);
        Assert.Equal(0, f.Files.VisitCalls);
        Assert.Equal(0, f.Files.ListCalls);
    }

    [Fact]
    public void PrecancelledTokenAndDisposedOwnerRejectBeforeAnyDiscovery() {
        using var f = new ForkQueryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.ListForks(1, cancellation.Token)).CancellationToken);
        Assert.Empty(f.Events);
        f.Store.Dispose();
        f.Events.Clear();
        Assert.Throws<ObjectDisposedException>(() => f.Store.ListForks(0, cancellation.Token));
        Assert.Empty(f.Events);
    }

    [Fact]
    public void CompleteEmptyDirectoryProvesEofAndClosesEnumeratorOnce() {
        using var f = new ForkQueryFaultFixture();
        Assert.Empty(f.Store.ListForks(int.MaxValue).Unwrap());
        ForkQueryEnumerator enumerator = Assert.Single(f.Operations.Enumerators);
        Assert.Equal(1, enumerator.MoveNextCalls);
        Assert.Equal(0, enumerator.CurrentCalls);
        Assert.Equal(1, enumerator.DisposeCalls);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(0, f.Files.ListCalls);
    }

    [Fact]
    public void ExactlyFullBudgetStillProvesActualEofAndDoesNotTreatRootsAsFree() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed first = f.Seed(1);
        ForkQuerySeed second = f.Seed(18);
        f.Order(first, second);

        Assert.Empty(f.Store.ListForks(2).Unwrap());

        ForkQueryEnumerator enumerator = Assert.Single(f.Operations.Enumerators);
        Assert.Equal(3, enumerator.MoveNextCalls);
        Assert.Equal(2, enumerator.CurrentCalls);
        Assert.Equal(1, enumerator.DisposeCalls);
        Assert.Equal(2, f.Files.Opened.Count);
        Assert.Equal(1, f.Files.MaximumActiveFiles);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(0, f.Files.ListCalls);
    }

    [Fact]
    public void ExtraQualifiedFormalRefFailsBudgetWithoutOpeningOrReadingItsDamagedContent() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed first = f.Seed(1);
        ForkQuerySeed second = f.Seed(18);
        string excess = f.RefPath(uint.MaxValue);
        File.WriteAllBytes(excess, [0]);
        f.Operations.EntryPaths = [f.RefPath(1), f.RefPath(18), excess];

        var result = f.Store.ListForks(2);

        Assert.Equal("VersionStore.ForkQueryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(new uint[] { 1, 18 }, f.Files.Opened.Select(file => file.LocalId));
        Assert.DoesNotContain($"open:{uint.MaxValue}:rw", f.Events);
        Assert.Equal(1, f.Files.MaximumActiveFiles);
        Assert.Equal(0, f.Files.ListCalls);
        ForkQueryEnumerator enumerator = Assert.Single(f.Operations.Enumerators);
        Assert.Equal(3, enumerator.MoveNextCalls);
        Assert.Equal(3, enumerator.CurrentCalls);
        Assert.Equal(1, enumerator.DisposeCalls);
        Assert.True(f.Files.Opened[0].CountCompleteReads(first.HeaderTicket) > 0);
        Assert.True(f.Files.Opened[1].CountCompleteReads(second.HeaderTicket) > 0);
        // Query failure retains a healthy owner and does not publish any partial edge set.
        Assert.True(f.Store.PublishRef(first.RefId, [9], out PublicationOutcome outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverBudgetNameOrTypeErrorIsReportedInsteadOfBudgetOrGuessedEof(bool wrongType) {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed first = f.Seed(1);
        string bad = wrongType ? f.RefPath(18) : Path.Combine(f.Root, "refs", "unknown.rbf");
        if (wrongType) { Directory.CreateDirectory(bad); }
        else { File.WriteAllBytes(bad, [0]); }
        f.Operations.EntryPaths = [f.RefPath(1), bad];

        Assert.Throws<InvalidDataException>(() => f.Store.ListForks(1));

        Assert.Single(f.Files.Opened);
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.PublishRef(first.RefId, [7], out _).IsSuccess);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("eof")]
    public void EnumerationAccessFailureIsPrimaryAndHealthyCloseKeepsWriterUsable(string failurePoint) {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        var primary = new IOException("directory " + failurePoint);
        f.Operations.ConfigureEnumerator = enumerator => {
            if (failurePoint == "current") { enumerator.CurrentFailure = primary; }
            else {
                enumerator.MoveNextFailure = primary;
                enumerator.FailMoveNextAt = failurePoint == "eof" ? 2 : 1;
            }
        };

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ListForks(1)));

        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        f.Operations.ConfigureEnumerator = null;
        Assert.Empty(f.Store.ListForks(1).Unwrap());
        Assert.True(f.Store.PublishRef(seed.RefId, [8], out _).IsSuccess);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("eof")]
    public void EnumerationAndCloseFailuresPreservePrimaryThenCachedCloseThenLockOrder(string failurePoint) {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        ForkQuerySeed cached = f.Seed(2);
        f.Store.ReadRef(cached.RefId).Unwrap();
        ForkQueryRefFile cachedFile = Assert.Single(f.Files.Opened);
        f.Order(seed, cached);
        var primary = new IOException("directory " + failurePoint);
        var enumeratorClose = new IOException("enumerator close");
        var cachedClose = new IOException("cached close");
        var lockClose = new IOException("lock close");
        cachedFile.DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Operations.ConfigureEnumerator = enumerator => {
            enumerator.DisposeFailure = enumeratorClose;
            if (failurePoint == "current") { enumerator.CurrentFailure = primary; }
            else {
                enumerator.MoveNextFailure = primary;
                enumerator.FailMoveNextAt = failurePoint == "eof" ? 3 : 1;
            }
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.ListForks(2));

        Assert.Equal(new Exception[] { primary, enumeratorClose, cachedClose, lockClose }, Flatten(observed));
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.True(f.Events.IndexOf("directory:close") < f.Events.IndexOf("ref:close:2"));
        Assert.Equal("lock:close", f.Events[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListForks(2));
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Fact]
    public void NormalEofEnumeratorCloseFailurePreventsSuccessfulResultAndFaultsOwner() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        var close = new IOException("enumerator close after eof");
        f.Operations.ConfigureEnumerator = enumerator => enumerator.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ListForks(1)));

        ForkQueryEnumerator enumerator = Assert.Single(f.Operations.Enumerators);
        Assert.Equal(2, enumerator.MoveNextCalls);
        Assert.Equal(1, enumerator.DisposeCalls);
        Assert.Equal(new[] { "ref:close:1", "directory:close", "lock:close" }, Closes(f));
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void BudgetFailureDoesNotHideNecessaryEnumeratorCloseFailureOrOpenExcessRef() {
        using var f = new ForkQueryFaultFixture();
        ForkQuerySeed seed = f.Seed(1);
        string excess = f.RefPath(18);
        File.WriteAllBytes(excess, [0]);
        f.Operations.EntryPaths = [f.RefPath(1), excess];
        var close = new IOException("enumerator close after budget failure");
        f.Operations.ConfigureEnumerator = enumerator => enumerator.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ListForks(1)));

        ForkQueryRefFile file = Assert.Single(f.Files.Opened);
        Assert.Equal(seed.RefId.LocalId, file.LocalId);
        Assert.Equal(1, file.DisposeCalls);
        Assert.DoesNotContain("open:18:rw", f.Events);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(new[] { "ref:close:1", "directory:close", "lock:close" }, Closes(f));
        Assert.Throws<InvalidOperationException>(() => f.Store.ListForks(1));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    internal static string[] Closes(ForkQueryFaultFixture f) => f.Events.Where(item => item.Contains(":close", StringComparison.Ordinal)).ToArray();

    internal static Exception[] Flatten(Exception observed) {
        var errors = new List<Exception>();
        Append(observed);
        return errors.ToArray();
        void Append(Exception error) {
            if (error is AggregateException aggregate) {
                foreach (Exception inner in aggregate.InnerExceptions) { Append(inner); }
            }
            else { errors.Add(error); }
        }
    }
}
