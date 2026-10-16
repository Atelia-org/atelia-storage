using Xunit;

namespace Atelia.HeadStore.Tests.ForkQueryFaults;

public sealed class ForkQueryCancellationTests {
    [Theory]
    [InlineData("move")]
    [InlineData("current")]
    [InlineData("eof")]
    [InlineData("before-close")]
    [InlineData("after-close")]
    public void DirectoryCancellationAtEachBoundaryDoesNotDeliverSuccessOrFaultWriter(string cancellationPoint) {
        using var f = new ForkQueryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        f.Operations.ConfigureEnumerator = enumerator => {
            if (cancellationPoint == "move") { enumerator.AfterMoveNext = (_, present) => { if (present) { cancellation.Cancel(); } }; }
            if (cancellationPoint == "current") { enumerator.AfterCurrent = cancellation.Cancel; }
            if (cancellationPoint == "eof") { enumerator.AfterMoveNext = (_, present) => { if (!present) { cancellation.Cancel(); } }; }
            if (cancellationPoint == "before-close") { enumerator.BeforeDispose = cancellation.Cancel; }
            if (cancellationPoint == "after-close") { enumerator.AfterDispose = cancellation.Cancel; }
        };

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.ListHeadOrigins(1, cancellation.Token)).CancellationToken);

        ForkQueryEnumerator enumerator = Assert.Single(f.Operations.Enumerators);
        Assert.Equal(1, enumerator.DisposeCalls);
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        if (cancellationPoint is "move" or "current") { Assert.Empty(f.Files.Opened); }
        if (cancellationPoint == "move") { Assert.Equal(0, enumerator.CurrentCalls); }
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Operations.ConfigureEnumerator = null;
        Assert.Empty(f.Store.ListHeadOrigins(1).Unwrap());
        Assert.True(f.Store.PublishHead(seed.HeadId, [2], out PublicationOutcome outcome).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("format")]
    [InlineData("forward")]
    [InlineData("successor")]
    [InlineData("close")]
    public void FileCancellationBeforeNextAccessOrAfterLastCloseDoesNotOpenNextHead(string cancellationPoint) {
        using var f = new ForkQueryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        ForkQuerySeed seed = f.Seed(1);
        ForkQuerySeed next = f.Seed(2);
        f.Order(seed, next);
        f.Files.AfterOpen = (localId, _) => {
            if (localId == 1 && cancellationPoint == "open") { cancellation.Cancel(); }
        };
        f.Files.ConfigureOpen = file => {
            if (file.LocalId != 1) { return; }
            file.AfterAccess = access => { if (access == cancellationPoint) { cancellation.Cancel(); } };
            if (cancellationPoint == "close") { file.AfterDispose = cancellation.Cancel; }
        };

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.ListHeadOrigins(2, cancellation.Token)).CancellationToken);

        ForkQueryHeadFile queried = Assert.Single(f.Files.Opened);
        Assert.Equal(1u, queried.LocalId);
        Assert.Equal(1, queried.DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        if (cancellationPoint is "open" or "format" or "forward") {
            // Real requests include reads made through RbfFrameInfo; proxy invocation counts are insufficient.
            Assert.Equal(0, queried.CountReadRequests());
        }
        else { Assert.True(queried.CountCompleteReads(seed.HeaderTicket) > 0); }
        f.Files.AfterOpen = null;
        f.Files.ConfigureOpen = null;
        Assert.Empty(f.Store.ListHeadOrigins(2).Unwrap());
        Assert.True(f.Store.PublishHead(seed.HeadId, [3], out _).IsSuccess);
    }

    [Fact]
    public void CancellationRemainsPrimaryWhenEnumeratorAndTemporaryCloseAlsoFail() {
        using var f = new ForkQueryFaultFixture();
        using var cancellation = new CancellationTokenSource();
        ForkQuerySeed seed = f.Seed(1);
        f.Order(seed);
        var enumClose = new IOException("directory close");
        var headClose = new IOException("temporary close");
        f.Operations.ConfigureEnumerator = enumerator => enumerator.DisposeFailure = enumClose;
        f.Files.ConfigureOpen = file => {
            file.AfterAccess = access => { if (access == "forward") { cancellation.Cancel(); } };
            file.DisposeFailure = headClose;
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.ListHeadOrigins(1, cancellation.Token));

        Exception[] errors = ForkQueryDiscoveryFaultTests.Flatten(observed);
        Assert.Equal(3, errors.Length);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(errors[0]).CancellationToken);
        Assert.Same(enumClose, errors[1]);
        Assert.Same(headClose, errors[2]);
        Assert.Equal(new[] { "directory:close", "ref:close:1", "lock:close" }, ForkQueryDiscoveryFaultTests.Closes(f));
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeadOrigins(1));
        f.Data.EnsureUsable();
        f.Store.Dispose();
        Assert.Equal(1, f.Files.Opened[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
    }
}
