using Atelia.Data;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateFaults;

public sealed class ConditionalUpdateReadResourceTests {
    [Theory]
    [InlineData(5L)]
    [InlineData(12L)]
    public void WorkBudgetExhaustionDuringMemberQualificationIsNotAnUncommittedWitness(long workSteps) {
        using var f = new ConditionalUpdateFaultFixture();
        ConditionalUpdateReuseAndMemberTests.CreatePartialMirror(f, 256);
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xb0, 128));
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xc0, 128));
        var store = f.Open(readOnly: true);
        var delivered = new List<HeadEntry>();

        var result = store.ReadHeadHistory(f.Id(1), new(10, workSteps), value => { delivered.Add(value); return true; });

        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Empty(delivered);
        Assert.All(f.Operations.Files, file => Assert.Equal(0, file.SuccessorCalls));
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Empty(ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(0, f.Operations.Locks[^1].DisposeCalls);
        Assert.Equal(f.Initial[1], Assert.Single(ConditionalUpdateFaultFixture.History(store, f.Id(1))).Id);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("successor")]
    public void CancellationObservedDuringExactOrStructuralMemberReadPreventsOldEntryDeliveryAndReleasesPeer(string phase) {
        using var f = new ConditionalUpdateFaultFixture();
        var group = ConditionalUpdateReuseAndMemberTests.CreatePartialMirror(f, 256);
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xb0, 128));
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xc0, 128));
        var store = f.Open(readOnly: true);
        using var cancellation = new CancellationTokenSource();
        f.Operations.ConfigureOpen = file => {
            if (file.Id != 2) { return; }
            if (phase == "exact") { file.BeforeCompleteRead = ticket => { if (ticket == group.B) { cancellation.Cancel(); } }; }
            else { file.BeforeSuccessor = cancellation.Cancel; }
        };
        var delivered = new List<HeadEntry>();

        var observed = Assert.Throws<OperationCanceledException>(() => store.ReadHeadHistory(f.Id(1), new(10, 1000),
            value => { delivered.Add(value); return true; }, cancellation.Token));

        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.Empty(delivered);
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Empty(ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(0, f.Operations.Locks[^1].DisposeCalls);
        f.Operations.ConfigureOpen = null;
        Assert.Equal(f.Initial[1], Assert.Single(ConditionalUpdateFaultFixture.History(store, f.Id(1))).Id);
    }

    [Fact]
    public void RepeatedCurrentAndHistoryQualificationKeepsPeersTemporaryAndBorrowsAlreadyOwnedFiles() {
        using var f = new ConditionalUpdateFaultFixture();
        var group = ConditionalUpdateReuseAndMemberTests.CreateCompleteMirror(f);
        var store = f.Open(readOnly: true);

        for (int i = 0; i < 3; i++) { Assert.Equal(group.A, store.ReadHead(1u).Unwrap().Id.Ticket); }

        Assert.Equal(new uint[] { 1 }, ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(3, f.Operations.Files.Count(file => file.Id == 2));
        Assert.All(f.Operations.Files.Where(file => file.Id == 2), file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, Assert.Single(f.Operations.Files, file => file.Id == 1).DisposeCalls);
        int openedBefore = f.Operations.Files.Count;
        Assert.Equal(2, ConditionalUpdateFaultFixture.History(store, f.Id(1)).Length);
        Assert.Equal(openedBefore + 1, f.Operations.Files.Count);
        Assert.Equal(new uint[] { 1 }, ConditionalUpdateFaultFixture.OwnedHeadIds(store));

        Assert.Equal(group.B, store.ReadHead(2u).Unwrap().Id.Ticket);
        Assert.Equal(new uint[] { 1, 2 }, ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        openedBefore = f.Operations.Files.Count;
        Assert.Equal(group.A, store.ReadHead(1u).Unwrap().Id.Ticket);
        Assert.Equal(openedBefore, f.Operations.Files.Count); // Existing owned peer is borrowed and never closed here.
        Assert.Equal(0, f.Operations.Files.Last(file => file.Id == 2).DisposeCalls);
        f.Close();
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal("lock:close", f.Events[^1]);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("successor")]
    public void PeerIoFailureStopsQualificationAndCannotActAsAFalseWitness(string phase) {
        using var f = new ConditionalUpdateFaultFixture();
        ConditionalUpdateReuseAndMemberTests.CreatePartialMirror(f, 256);
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xb0, 128));
        f.Append(2, 1, ConditionalUpdateFaultFixture.Bytes(0xc0, 128));
        var store = f.Open(readOnly: true);
        var primary = new IOException("CU designated peer " + phase);
        f.Operations.ConfigureOpen = file => {
            if (file.Id != 2) { return; }
            if (phase == "read") { file.ReadFailure = primary; }
            else { file.SuccessorFailure = primary; }
        };
        var delivered = new List<HeadEntry>();

        Assert.Same(primary, Assert.Throws<IOException>(() => store.ReadHeadHistory(f.Id(1), new(10, 1000), value => { delivered.Add(value); return true; })));

        Assert.Empty(delivered);
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Empty(ConditionalUpdateFaultFixture.OwnedHeadIds(store));
        Assert.Equal(0, f.Operations.Locks[^1].DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryPeerCloseFailureClosesAllOwnedResourcesExactlyOnceAndReleasesRootLast(bool failReadToo) {
        using var f = new ConditionalUpdateFaultFixture();
        ConditionalUpdateReuseAndMemberTests.CreateCompleteMirror(f);
        var store = f.Open(readOnly: true);
        store.ReadHead(3u).Unwrap(); // Unrelated owned handle must join fault cleanup.
        var peerClose = new IOException("CU temporary peer close");
        var primary = new IOException("CU temporary peer read");
        f.Operations.ConfigureOpen = file => {
            if (file.Id == 2) { file.DisposeFailure = peerClose; if (failReadToo) { file.ReadFailure = primary; } }
        };

        Exception observed = Assert.ThrowsAny<Exception>(() => store.ReadHead(1u));

        if (failReadToo) {
            var aggregate = Assert.IsType<AggregateException>(observed);
            Assert.Equal(new Exception[] { primary, peerClose }, aggregate.Flatten().InnerExceptions);
        }
        else { Assert.Same(peerClose, observed); }
        f.AssertFaultCleanup();
        Assert.True(f.Events.IndexOf("close:2") < f.Events.IndexOf("close:3"));
        Assert.Equal("lock:close", f.Events[^1]);
    }
}
