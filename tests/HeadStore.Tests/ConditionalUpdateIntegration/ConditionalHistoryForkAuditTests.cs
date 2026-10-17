using Atelia.Rbf;
using Atelia.HeadStore.Tests.History;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateIntegration;

public sealed class ConditionalHistoryForkAuditTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCuForkStoresOnlyHeaderAndExcludesLaterSourceHistory(bool specified) {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(1, [0x11, 0xff])[0];
        fixture.SeedPublic(2, [0x22]);
        byte[] root = [0, 0xff, 0x80, 0x7f, 3];
        HeadEntryId source;
        HeadId child;
        HeadEntry saved;
        using (var store = fixture.Open()) {
            var published = Publish(store, fixture, (1, root), (2, new byte[] { 0x32, 0x33 }));
            source = published[fixture.Id(1)];
            HeadEntryId later = store.PublishHead(initial.HeadId, [0x41], out _).Unwrap();
            child = (specified ? store.ForkHead(3, source, out _) : store.ForkHead(source, out _)).Unwrap();
            HeadEntry value = store.ReadHead(child).Unwrap();
            Assert.Equal(root, value.Payload.ToArray());
            var history = HistoryFixture.Collect(store, child);
            Assert.Equal(new[] { source, initial }, history.Select(entry => entry.Id));
            Assert.DoesNotContain(history, entry => entry.Id == later);
            Assert.Equal(root, history[0].Payload.ToArray());
            Assert.Equal(source, value.Id);
            saved = history[0];
            Assert.Equal(later, store.ReadHead(source.HeadId).Unwrap().Id);
        }

        // A fork is physically complete with only its checked origin-bearing header.
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(child.GetLocalId(fixture.Identity)), RbfCacheMode.Off)) {
            var forward = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(forward.MoveNext());
            Assert.Equal(0u, forward.Current.Tag);
            Assert.False(forward.MoveNext());
            Assert.Null(forward.TerminationError);
        }
        using (var cold = fixture.Open(readOnly: true)) {
            var audit = cold.Audit().Unwrap();
            Assert.Equal(3, audit.HeadCount);
            Assert.Equal(5, audit.EntryCount); // 2 initial + both CU members + later ordinary A.
            Assert.Equal(1, audit.OriginCount);
            Assert.Equal(new[] { source, initial },
                HistoryFixture.Collect(cold, child).Select(entry => entry.Id));
        }
        Assert.Equal(root, saved.Payload.ToArray());
    }

    [Fact]
    public void CuPeerDoesNotJoinTheForkOriginVisitedPath() {
        using var fixture = new HistoryFixture();
        HeadEntryId ancestor = fixture.SeedPublic(1, [1])[0];
        using var store = fixture.Open();
        HeadId fork = store.ForkHead(2, ancestor, out _).Unwrap();
        var group = Publish(store, fixture, (1, new byte[] { 0xa1 }), (2, new byte[] { 0xb1 }));
        var history = HistoryFixture.Collect(store, fork);
        // A is checked as B's CU peer before the inherited exact A0 is entered.
        Assert.Equal(new[] { group[fork], ancestor }, history.Select(entry => entry.Id));
    }

    [Fact]
    public void CuHistoryVisitorCanReadCurrentLocalAndPeerWithTheHistoryHandleBorrowed() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        }
        using var store = fixture.Open();
        int deliveries = 0;
        var result = store.ReadHeadHistory(fixture.Id(1), HistoryFixture.Unlimited, entry => {
            deliveries++;
            Assert.Equal(group[fixture.Id(1)], entry.Id);
            Assert.Equal(group[fixture.Id(1)], store.ReadHead(fixture.Id(1)).Unwrap().Id);
            Assert.Equal(group[fixture.Id(2)], store.ReadHead(fixture.Id(2)).Unwrap().Id);
            var mutation = store.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [fixture.Id(1)] = new byte[] { 9 }, [fixture.Id(2)] = new byte[] { 8 }
            }, out PublicationOutcome outcome);
            Assert.Equal("HeadStore.HistoryActive", mutation.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        });
        Assert.Equal(HeadHistoryEnd.VisitorStopped, result.Unwrap());
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void SameValueAndRewindCuRemainDifferentHistoricalEntryIds() {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        using var store = fixture.Open();
        var first = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        var same = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        var rewind = Publish(store, fixture, (1, new byte[] { 1 }), (2, new byte[] { 2 }));
        var history = HistoryFixture.Collect(store, initial.HeadId);
        Assert.Equal(new[] { rewind[initial.HeadId], same[initial.HeadId], first[initial.HeadId], initial },
            history.Select(entry => entry.Id));
        Assert.Equal(new byte[] { 1, 3, 3, 1 }, history.Select(entry => entry.Payload[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalUncommittedCuDoesNotConsumeEntryBudgetOrAuditPublishedCount(bool readOnly) {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        var declared = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = [7], [2] = [8] }, 1);
        using (var store = fixture.Open(readOnly)) {
            var history = HistoryFixture.Collect(store, initial.HeadId, new(1, 10));
            Assert.Equal(new[] { initial }, history.Select(entry => entry.Id));
            Assert.DoesNotContain(history, entry => entry.Id == declared[1]);
            var delivered = new List<HeadEntryId>();
            var insufficient = store.ReadHeadHistory(initial.HeadId, new(1, 9), entry => {
                delivered.Add(entry.Id);
                return true;
            });
            Assert.Equal("HeadStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
            Assert.Empty(delivered); // Peer entry/header consumed work even though the CU was skipped.
            fixture.AssertReleased(1, 2);
        }
        using var cold = fixture.Open(readOnly: true);
        var audit = cold.Audit().Unwrap();
        Assert.Equal(2, audit.HeadCount);
        Assert.Equal(2, audit.EntryCount);
    }

    [Fact]
    public void UncommittedExactForkOriginIsAnErrorInsteadOfSkippingToOlderSource() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        var group = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = [1], [2] = [2] }, 1);
        HeadEntryId child = fixture.WriteMirror(3, group[1], new HistoryRecord([1]))[0];
        using var store = fixture.Open(readOnly: true);
        Assert.Single(store.ListHeadOrigins(3).Unwrap()); // The query checks the declaration only.
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        Assert.Equal("HeadStore.InvalidForkOrigin", store.Audit().Error!.ErrorCode);
    }

    [Fact]
    public void EvenACompleteCuGroupCannotReplaceTheDirectSecondOrdinaryInitialEntry() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(2, [2]);
        HeadEntryId initial = ConditionalIntegrationRecords.WriteCompleteGroupWithForbiddenCuInitial(fixture, 3, 2);
        using var store = fixture.Open(readOnly: true);
        Assert.Equal("HeadStore.InvalidHeadRecord", store.ReadHead(initial.HeadId).Error!.ErrorCode);
        int deliveries = 0;
        var history = store.ReadHeadHistory(initial.HeadId, HistoryFixture.Unlimited, _ => { deliveries++; return true; });
        Assert.Equal("HeadStore.InvalidHeadRecord", history.Error!.ErrorCode);
        Assert.Equal(0, deliveries);
        Assert.Equal("HeadStore.InvalidHeadRecord", store.Audit().Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CuSourceAllowsIndependentFirstLocalPayload(bool sameLength) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        HeadEntryId source;
        using (var writer = fixture.Open()) {
            source = Publish(writer, fixture, (1, new byte[] { 3, 4 }), (2, new byte[] { 5 }))[fixture.Id(1)];
        }
        byte[] wrong = sameLength ? [3, 9] : [3];
        HeadEntryId child = fixture.WriteMirror(3, source, new HistoryRecord(wrong))[0];
        using var store = fixture.Open(readOnly: true);
        var delivered = new List<HeadEntryId>();
        var history = store.ReadHeadHistory(child.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal(HeadHistoryEnd.Complete, history.Unwrap());
        Assert.Equal(child, delivered[0]);
        Assert.Equal(source, delivered[1]);
        Assert.Equal(3, delivered.Count);
        Assert.Equal(5, store.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryCuSourceCrcAndFollowingFenceFailuresRejectExactForkBeforeOutput(bool fence) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        HeadEntryId source;
        using (var store = fixture.Open()) {
            source = Publish(store, fixture, (1, new byte[] { 0x11 }), (2, new byte[] { 0x22 }))[fixture.Id(1)];
            store.PublishHead(source.HeadId, [0x31], out _).Unwrap();
            store.PublishHead(source.HeadId, [0x41], out _).Unwrap();
        }
        if (fence) { fixture.FlipFollowingFence(source); }
        else { fixture.FlipPayload(source); }
        using var reopened = fixture.Open();
        Assert.Equal(new byte[] { 0x41 }, reopened.ReadHead(source.HeadId).Unwrap().Payload.ToArray());
        var rejected = reopened.ForkHead(3, source, out PublicationOutcome outcome);
        Assert.True(rejected.IsFailure);
        Assert.StartsWith("Rbf.", rejected.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.HeadPath(3)));
        Assert.DoesNotContain(reopened.ListHeads().Unwrap(), id => id.GetLocalId(fixture.Identity) == 3);
    }

    [Fact]
    public void BadCuPayloadRemainsAnAuditFailureBehindAHealthyOrdinaryHead() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        HeadEntryId cu;
        using (var store = fixture.Open()) {
            cu = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }))[fixture.Id(1)];
            store.PublishHead(cu.HeadId, [5], out _).Unwrap();
        }
        fixture.FlipPayload(cu);
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(new byte[] { 5 }, cold.ReadHead(cu.HeadId).Unwrap().Payload.ToArray());
        Assert.Equal("Rbf.CrcMismatch", cold.Audit().Error!.ErrorCode);
        var delivered = new List<HeadEntryId>();
        var result = cold.ReadHeadHistory(cu.HeadId, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        });
        Assert.Equal("Rbf.CrcMismatch", result.Error!.ErrorCode);
        Assert.Single(delivered);
    }

    [Fact]
    public void CuGroupPeerWorkBudgetFailureNeverBecomesAnUncommittedWitness() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        using (var writer = fixture.Open()) {
            Publish(writer, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        }
        using var store = fixture.Open(readOnly: true);
        int deliveries = 0;
        // 5 steps check the local CU; no budget remains to enter its necessary peer.
        var result = store.ReadHeadHistory(fixture.Id(1), new(2, 5), _ => { deliveries++; return true; });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(0, deliveries);
        Assert.Equal(2, HistoryFixture.Collect(store, fixture.Id(1)).Count);
    }

    [Fact]
    public void VisitorCancellationAndDisposeStopBeforeAnotherCuHistoryNode() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        using (var writer = fixture.Open()) {
            Publish(writer, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        }
        using var cancellation = new CancellationTokenSource();
        using (var store = fixture.Open(readOnly: true)) {
            int deliveries = 0;
            Assert.Throws<OperationCanceledException>(() => store.ReadHeadHistory(fixture.Id(1), HistoryFixture.Unlimited,
                _ => { deliveries++; cancellation.Cancel(); return true; }, cancellation.Token));
            Assert.Equal(1, deliveries);
            Assert.Equal(2, HistoryFixture.Collect(store, fixture.Id(1)).Count);
        }
        using (var store = fixture.Open(readOnly: true)) {
            HeadEntry? saved = null;
            Assert.Throws<ObjectDisposedException>(() => store.ReadHeadHistory(fixture.Id(1), HistoryFixture.Unlimited,
                entry => { saved = entry; store.Dispose(); return false; }));
            Assert.Equal(new byte[] { 3 }, saved!.Payload.ToArray());
        }
    }

    private static IReadOnlyDictionary<HeadId, HeadEntryId> Publish(HeadStore store, HistoryFixture fixture,
        params (uint LocalId, byte[] Payload)[] changes) {
        var inputs = changes.ToDictionary(change => fixture.Id(change.LocalId), change => (ReadOnlyMemory<byte>)change.Payload);
        var result = store.PublishHeads(inputs, out PublicationOutcome outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return result;
    }
}
