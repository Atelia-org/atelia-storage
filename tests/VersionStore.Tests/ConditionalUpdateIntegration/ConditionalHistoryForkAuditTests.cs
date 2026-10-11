using Atelia.Rbf;
using Atelia.VersionStore.Tests.History;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateIntegration;

public sealed class ConditionalHistoryForkAuditTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCuForkClonesOnlyRootIntoOrdinaryInitialAndExcludesLaterSourceHistory(bool specified) {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(1, [0x11, 0xff])[0];
        fixture.SeedPublic(2, [0x22]);
        byte[] root = [0, 0xff, 0x80, 0x7f, 3];
        RefRevision source;
        RefRevision child;
        RefBytesSnapshot saved;
        using (var store = fixture.Open()) {
            var published = Publish(store, fixture, (1, root), (2, new byte[] { 0x32, 0x33 }));
            source = published[fixture.Id(1)];
            RefRevision later = store.PublishRef(initial.RefId, [0x41], out _).Unwrap();
            child = (specified ? store.ForkRef(3, source, out _) : store.ForkRef(source, out _)).Unwrap();
            RefBytesSnapshot value = store.ReadRef(child.RefId).Unwrap();
            Assert.Equal(root, value.Payload.ToArray());
            var history = HistoryFixture.Collect(store, child.RefId);
            Assert.Equal(new[] { child, source, initial }, history.Select(snapshot => snapshot.Revision));
            Assert.DoesNotContain(history, snapshot => snapshot.Revision == later);
            Assert.Equal(root, history[1].Payload.ToArray());
            saved = history[1];
            Assert.Equal(later, store.ReadRef(source.RefId).Unwrap().Revision);
        }

        // Read the physical child after the writer owner is closed: header then one ordinary root frame.
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(child.RefId.LocalId), RbfCacheMode.Off)) {
            var forward = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(forward.MoveNext());
            Assert.Equal(0u, forward.Current.Tag);
            Assert.True(forward.MoveNext());
            Assert.Equal(1u, forward.Current.Tag);
            Assert.Equal(root.Length, forward.Current.PayloadLength);
            Assert.Equal(child.Ticket, forward.Current.Ticket);
            Assert.False(forward.MoveNext());
            Assert.Null(forward.TerminationError);
        }
        using (var cold = fixture.Open(readOnly: true)) {
            var audit = cold.Audit().Unwrap();
            Assert.Equal(3, audit.RefCount);
            Assert.Equal(6, audit.SnapshotCount); // 3 initial + both CU members + later ordinary A.
            Assert.Equal(1, audit.ForkCount);
            Assert.Equal(new[] { child, source, initial },
                HistoryFixture.Collect(cold, child.RefId).Select(snapshot => snapshot.Revision));
        }
        Assert.Equal(root, saved.Payload.ToArray());
    }

    [Fact]
    public void CuPeerDoesNotJoinTheForkOriginVisitedPath() {
        using var fixture = new HistoryFixture();
        RefRevision ancestor = fixture.SeedPublic(1, [1])[0];
        using var store = fixture.Open();
        RefRevision fork = store.ForkRef(2, ancestor, out _).Unwrap();
        var group = Publish(store, fixture, (1, new byte[] { 0xa1 }), (2, new byte[] { 0xb1 }));
        var history = HistoryFixture.Collect(store, fork.RefId);
        // A is checked as B's CU peer before the inherited exact A0 is entered.
        Assert.Equal(new[] { group[fork.RefId], fork, ancestor }, history.Select(snapshot => snapshot.Revision));
    }

    [Fact]
    public void CuHistoryVisitorCanReadCurrentLocalAndPeerWithTheHistoryHandleBorrowed() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<RefId, RefRevision> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        }
        using var store = fixture.Open();
        int deliveries = 0;
        var result = store.ReadRefHistory(fixture.Id(1), HistoryFixture.Unlimited, snapshot => {
            deliveries++;
            Assert.Equal(group[fixture.Id(1)], snapshot.Revision);
            Assert.Equal(group[fixture.Id(1)], store.ReadRef(fixture.Id(1)).Unwrap().Revision);
            Assert.Equal(group[fixture.Id(2)], store.ReadRef(fixture.Id(2)).Unwrap().Revision);
            var mutation = store.PublishRefs(new Dictionary<RefId, ReadOnlyMemory<byte>> {
                [fixture.Id(1)] = new byte[] { 9 }, [fixture.Id(2)] = new byte[] { 8 }
            }, out PublicationOutcome outcome);
            Assert.Equal("VersionStore.HistoryActive", mutation.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return false;
        });
        Assert.Equal(HistoryEnd.VisitorStopped, result.Unwrap());
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void SameValueAndRewindCuRemainDifferentHistoricalRevisions() {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        using var store = fixture.Open();
        var first = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        var same = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }));
        var rewind = Publish(store, fixture, (1, new byte[] { 1 }), (2, new byte[] { 2 }));
        var history = HistoryFixture.Collect(store, initial.RefId);
        Assert.Equal(new[] { rewind[initial.RefId], same[initial.RefId], first[initial.RefId], initial },
            history.Select(snapshot => snapshot.Revision));
        Assert.Equal(new byte[] { 1, 3, 3, 1 }, history.Select(snapshot => snapshot.Payload[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalUncommittedCuDoesNotConsumeSnapshotBudgetOrAuditPublishedCount(bool readOnly) {
        using var fixture = new HistoryFixture();
        RefRevision initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        var declared = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = [7], [2] = [8] }, 1);
        using (var store = fixture.Open(readOnly)) {
            var history = HistoryFixture.Collect(store, initial.RefId, new(1, 10));
            Assert.Equal(new[] { initial }, history.Select(snapshot => snapshot.Revision));
            Assert.DoesNotContain(history, snapshot => snapshot.Revision == declared[1]);
            var delivered = new List<RefRevision>();
            var insufficient = store.ReadRefHistory(initial.RefId, new(1, 9), snapshot => {
                delivered.Add(snapshot.Revision);
                return true;
            });
            Assert.Equal("VersionStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
            Assert.Empty(delivered); // Peer entry/header consumed work even though the CU was skipped.
            fixture.AssertReleased(1, 2);
        }
        using var cold = fixture.Open(readOnly: true);
        var audit = cold.Audit().Unwrap();
        Assert.Equal(2, audit.RefCount);
        Assert.Equal(2, audit.SnapshotCount);
    }

    [Fact]
    public void UncommittedExactForkOriginIsAnErrorInsteadOfSkippingToOlderSource() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        var group = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = [1], [2] = [2] }, 1);
        RefRevision child = fixture.WriteMirror(3, group[1], new HistoryRecord([1]))[0];
        using var store = fixture.Open(readOnly: true);
        Assert.Single(store.ListForks(3).Unwrap()); // The query checks the declaration only.
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidForkOrigin", result.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        Assert.Equal("VersionStore.InvalidForkOrigin", store.Audit().Error!.ErrorCode);
    }

    [Fact]
    public void EvenACompleteCuGroupCannotReplaceTheDirectSecondOrdinaryInitialSnapshot() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(2, [2]);
        RefRevision initial = ConditionalIntegrationRecords.WriteCompleteGroupWithForbiddenCuInitial(fixture, 3, 2);
        using var store = fixture.Open(readOnly: true);
        Assert.Equal("VersionStore.InvalidRefRecord", store.ReadRef(initial.RefId).Error!.ErrorCode);
        int deliveries = 0;
        var history = store.ReadRefHistory(initial.RefId, HistoryFixture.Unlimited, _ => { deliveries++; return true; });
        Assert.Equal("VersionStore.InvalidRefRecord", history.Error!.ErrorCode);
        Assert.Equal(0, deliveries);
        Assert.Equal("VersionStore.InvalidRefRecord", store.Audit().Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CuSourceRequiresExactChildRootBytesAndLength(bool sameLength) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        RefRevision source;
        using (var writer = fixture.Open()) {
            source = Publish(writer, fixture, (1, new byte[] { 3, 4 }), (2, new byte[] { 5 }))[fixture.Id(1)];
        }
        byte[] wrong = sameLength ? [3, 9] : [3];
        RefRevision child = fixture.WriteMirror(3, source, new HistoryRecord(wrong))[0];
        using var store = fixture.Open(readOnly: true);
        var delivered = new List<RefRevision>();
        var history = store.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.InvalidForkOrigin", history.Error!.ErrorCode);
        Assert.Equal(new[] { child }, delivered);
        Assert.Equal("VersionStore.InvalidForkOrigin", store.Audit().Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryCuSourceCrcAndFollowingFenceFailuresRejectExactForkBeforeOutput(bool fence) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        RefRevision source;
        using (var store = fixture.Open()) {
            source = Publish(store, fixture, (1, new byte[] { 0x11 }), (2, new byte[] { 0x22 }))[fixture.Id(1)];
            store.PublishRef(source.RefId, [0x31], out _).Unwrap();
            store.PublishRef(source.RefId, [0x41], out _).Unwrap();
        }
        if (fence) { fixture.FlipFollowingFence(source); }
        else { fixture.FlipPayload(source); }
        using var reopened = fixture.Open();
        Assert.Equal(new byte[] { 0x41 }, reopened.ReadRef(source.RefId).Unwrap().Payload.ToArray());
        var rejected = reopened.ForkRef(3, source, out PublicationOutcome outcome);
        Assert.True(rejected.IsFailure);
        Assert.StartsWith("Rbf.", rejected.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.RefPath(3)));
        Assert.DoesNotContain(reopened.ListRefs().Unwrap(), id => id.LocalId == 3);
    }

    [Fact]
    public void BadCuPayloadRemainsAnAuditFailureBehindAHealthyOrdinaryHead() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        RefRevision cu;
        using (var store = fixture.Open()) {
            cu = Publish(store, fixture, (1, new byte[] { 3 }), (2, new byte[] { 4 }))[fixture.Id(1)];
            store.PublishRef(cu.RefId, [5], out _).Unwrap();
        }
        fixture.FlipPayload(cu);
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(new byte[] { 5 }, cold.ReadRef(cu.RefId).Unwrap().Payload.ToArray());
        Assert.Equal("Rbf.CrcMismatch", cold.Audit().Error!.ErrorCode);
        var delivered = new List<RefRevision>();
        var result = cold.ReadRefHistory(cu.RefId, HistoryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
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
        var result = store.ReadRefHistory(fixture.Id(1), new(2, 5), _ => { deliveries++; return true; });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
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
            Assert.Throws<OperationCanceledException>(() => store.ReadRefHistory(fixture.Id(1), HistoryFixture.Unlimited,
                _ => { deliveries++; cancellation.Cancel(); return true; }, cancellation.Token));
            Assert.Equal(1, deliveries);
            Assert.Equal(2, HistoryFixture.Collect(store, fixture.Id(1)).Count);
        }
        using (var store = fixture.Open(readOnly: true)) {
            RefBytesSnapshot? saved = null;
            Assert.Throws<ObjectDisposedException>(() => store.ReadRefHistory(fixture.Id(1), HistoryFixture.Unlimited,
                snapshot => { saved = snapshot; store.Dispose(); return false; }));
            Assert.Equal(new byte[] { 3 }, saved!.Payload.ToArray());
        }
    }

    private static IReadOnlyDictionary<RefId, RefRevision> Publish(VersionStore store, HistoryFixture fixture,
        params (uint LocalId, byte[] Payload)[] changes) {
        var inputs = changes.ToDictionary(change => fixture.Id(change.LocalId), change => (ReadOnlyMemory<byte>)change.Payload);
        var result = store.PublishRefs(inputs, out PublicationOutcome outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return result;
    }
}
