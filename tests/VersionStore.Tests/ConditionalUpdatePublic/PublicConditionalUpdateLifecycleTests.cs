using System.Buffers.Binary;
using Atelia.FrameStore;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.ConditionalUpdatePublic;

public sealed class PublicConditionalUpdateLifecycleTests {
    [Fact]
    public void MemberTableOrdersTheCompleteUnsignedLocalIdDomain() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision low;
        RefRevision high;
        RefRevision highest;
        IReadOnlyDictionary<RefId, RefRevision> published;
        using (var store = fixture.Create(uint.MaxValue - 1)) {
            highest = store.CreateRef(uint.MaxValue - 1, [1], out _).Unwrap();
            high = store.CreateRef(0x80000000, [2], out _).Unwrap();
            low = store.CreateRef(7, [3], out _).Unwrap();
            published = store.PublishRefs(ConditionalUpdatePublicFixture.Changes(
                (highest.RefId, [4]), (high.RefId, [5]), (low.RefId, [6])), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        fixture.AssertWire(published[highest.RefId], published, [4], long.MinValue, long.MaxValue);
        fixture.AssertWire(published[high.RefId], published, [5], long.MinValue, long.MaxValue);
        fixture.AssertWire(published[low.RefId], published, [6], long.MinValue, long.MaxValue);
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal(6, readOnly.Audit().Unwrap().SnapshotCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DifferentOpaqueRootsHaveOneSortedMemberTableSharedTimeAndFreshTicketsAcrossColdOwners(int count) {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision[] initial = new RefRevision[count];
        byte[][] payloads = Enumerable.Range(0, count).Select(index => Enumerable.Repeat((byte)(0x81 + index), 1 + index * 137).ToArray()).ToArray();
        IReadOnlyDictionary<RefId, RefRevision> first;
        IReadOnlyDictionary<RefId, RefRevision> second;
        List<RefBytesSnapshot>[] saved;
        long before;
        long after;
        long time;
        using (var store = fixture.Create()) {
            uint[] locals = [19, 7, 23];
            for (int index = 0; index < count; index++) { initial[index] = store.CreateRef(locals[index], [0xff], out _).Unwrap(); }
            var changes = ConditionalUpdatePublicFixture.Changes(initial.Select((revision, index) => (revision.RefId, payloads[index])).ToArray());
            before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            first = store.PublishRefs(changes, out var outcome).Unwrap();
            after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(count, first.Count);
            time = store.ReadRef(initial[0].RefId).Unwrap().RecordedAtUnixMs;
            Assert.InRange(time, before, after);
            foreach (RefRevision revision in initial) {
                Assert.NotEqual(revision, first[revision.RefId]);
                Assert.Equal(time, store.ReadRef(revision.RefId).Unwrap().RecordedAtUnixMs);
            }
            // Equal payloads are still a new publication at fresh physical positions.
            second = store.PublishRefs(changes, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            saved = initial.Select(revision => ConditionalUpdatePublicFixture.History(store, revision.RefId)).ToArray();
            for (int index = 0; index < count; index++) {
                RefId id = initial[index].RefId;
                Assert.NotEqual(first[id], second[id]);
                Assert.True(second[id].Ticket.Offset > first[id].Ticket.Offset);
                Assert.Equal(new[] { second[id], first[id], initial[index] }, saved[index].Select(snapshot => snapshot.Revision));
                Assert.Equal(payloads[index], saved[index][0].Payload.ToArray());
                Assert.Equal(payloads[index], saved[index][1].Payload.ToArray());
            }
        }
        for (int index = 0; index < count; index++) {
            fixture.AssertWire(first[initial[index].RefId], first, payloads[index], before, after, time);
        }
        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            for (int index = 0; index < count; index++) {
                Assert.Equal(second[initial[index].RefId], cold.ReadRef(initial[index].RefId).Unwrap().Revision);
                Assert.Equal(saved[index].Select(snapshot => snapshot.Revision), ConditionalUpdatePublicFixture.History(cold, initial[index].RefId).Select(snapshot => snapshot.Revision));
            }
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            var audit = cold.Audit().Unwrap();
            Assert.Equal(count, audit.RefCount);
            Assert.Equal(3L * count, audit.SnapshotCount);
            for (int index = 0; index < count; index++) {
                Assert.Equal(payloads[index], cold.ReadRef(initial[index].RefId).Unwrap().Payload.ToArray());
            }
        }
        Assert.Equal(payloads[0], saved[0][0].Payload.ToArray());
        Assert.Equal(initial[0].RefId, first[initial[0].RefId].RefId);
    }

    [Fact]
    public void MinimumAndMaximumRootSlicesAreOpaqueOwnedBytesAndSingleItemUsesOrdinarySnapshot() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        IReadOnlyDictionary<RefId, RefRevision> group;
        RefRevision single;
        byte[] buffer = Enumerable.Repeat((byte)0xf7, Store.MaxRootPayloadLength + 2).ToArray();
        byte[] expectedMaximum = buffer.AsSpan(1, Store.MaxRootPayloadLength).ToArray();
        RefBytesSnapshot saved;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            var changes = new Dictionary<RefId, ReadOnlyMemory<byte>> {
                [a.RefId] = new byte[] { 0xff },
                [b.RefId] = buffer.AsMemory(1, Store.MaxRootPayloadLength)
            };
            group = store.PublishRefs(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            buffer.AsSpan().Clear();
            changes.Clear();
            saved = store.ReadRef(b.RefId).Unwrap();
            Assert.Equal(expectedMaximum, saved.Payload.ToArray());
            Assert.Equal(new byte[] { 0xff }, store.ReadRef(a.RefId).Unwrap().Payload.ToArray());
            Assert.True(RootMapCodec.Decode(saved.Payload).IsFailure);
            single = store.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [0xee])), out outcome).Unwrap()[a.RefId];
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(7), RbfCacheMode.Off))
        using (var frame = file.ReadPooledFrame(single.Ticket).Unwrap()) {
            Assert.Equal(1u, frame.Tag);
            Assert.Equal(8, frame.TailMetaLength);
            Assert.Equal(new byte[] { 0xee }, frame.PayloadAndMeta[..^8].ToArray());
        }
        Assert.Equal(group[b.RefId], saved.Revision);
        Assert.Equal(expectedMaximum, saved.Payload.ToArray());
    }

    [Fact]
    public void EstablishedGroupSurvivesSingleRefCoverAndOldCuForkCopiesOnlyRootWithDataTagsAndAudit() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a0;
        RefRevision b0;
        IReadOnlyDictionary<RefId, RefRevision> group;
        RefRevision a2;
        RefRevision child;
        FrameAddress addressA;
        FrameAddress addressB;
        byte[] rootA;
        byte[] rootB;
        byte[] dataA = "application-data-a"u8.ToArray();
        byte[] dataB = "application-data-b"u8.ToArray();
        using (var store = fixture.Create()) {
            a0 = store.CreateRef(7, [0xff], out _).Unwrap();
            b0 = store.CreateRef(19, [0xff], out _).Unwrap();
            addressA = fixture.Data.Append(17, dataA).Unwrap();
            addressB = fixture.Data.Append(23, dataB).Unwrap();
            rootA = RootMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressA }).Unwrap();
            rootB = RootMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressB }).Unwrap();
            group = store.PublishRefs(ConditionalUpdatePublicFixture.Changes((b0.RefId, rootB), (a0.RefId, rootA)), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            a2 = store.PublishRef(a0.RefId, [0xa2], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group[b0.RefId], store.ReadRef(b0.RefId).Unwrap().Revision);
            Assert.Equal(new[] { a2, group[a0.RefId], a0 }, ConditionalUpdatePublicFixture.History(store, a0.RefId).Select(snapshot => snapshot.Revision));
            Assert.True(store.CreateTag("old-cu", rootA, group[a0.RefId], out outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            // Both the revision and the group's exact peer positions survive replacement of A's current head.
            RefRevision selected = default;
            Assert.Equal(HistoryEnd.VisitorStopped, cold.ReadRefHistory(a0.RefId, ConditionalUpdatePublicFixture.Unlimited, snapshot => {
                if (snapshot.Revision != group[a0.RefId]) { return true; }
                selected = snapshot.Revision;
                return false;
            }).Unwrap());
            Assert.Equal(group[a0.RefId], selected);
            child = cold.ForkRef(23, selected, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(rootA, cold.ReadRef(child.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { child, group[a0.RefId], a0 }, ConditionalUpdatePublicFixture.History(cold, child.RefId).Select(snapshot => snapshot.Revision));
            ForkInfo edge = Assert.Single(cold.ListForks(3).Unwrap());
            Assert.Equal(child.RefId, edge.ChildRefId);
            Assert.Equal(a0.RefId, edge.SourceRefId);
            Assert.Equal(group[a0.RefId].Ticket, edge.SourceSnapshotTicket);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(23), RbfCacheMode.Off))
        using (var frame = file.ReadPooledFrame(child.Ticket).Unwrap()) {
            Assert.Equal(1u, frame.Tag);
            Assert.Equal(8, frame.TailMetaLength);
            Assert.Equal(rootA, frame.PayloadAndMeta[..^8].ToArray());
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(a2, cold.ReadRef(a0.RefId).Unwrap().Revision);
            Assert.Equal(group[b0.RefId], cold.ReadRef(b0.RefId).Unwrap().Revision);
            Assert.Equal(rootB, cold.ReadRef(b0.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(rootA, cold.ResolveTag("old-cu").Unwrap().Bytes.ToArray());
            TagInfo info = cold.ReadTagInfo("old-cu").Unwrap();
            Assert.Equal(a0.RefId, info.RelatedRefId);
            Assert.Equal(group[a0.RefId].Ticket, info.RelatedTicket);
            Assert.Equal(TagScanEnd.Complete, cold.ScanTags(tag => {
                Assert.Equal(info.Name, tag.Name);
                Assert.Equal(info.RelatedTicket, tag.RelatedTicket);
                return true;
            }).Unwrap());
            using var frameA = data.ReadFrame(RootMapCodec.Decode(cold.ReadRef(child.RefId).Unwrap().Payload).Unwrap()["state"]).Unwrap();
            using var frameB = data.ReadFrame(RootMapCodec.Decode(cold.ReadRef(b0.RefId).Unwrap().Payload).Unwrap()["state"]).Unwrap();
            Assert.Equal(dataA, frameA.PayloadAndMeta.ToArray());
            Assert.Equal(dataB, frameB.PayloadAndMeta.ToArray());
            Assert.Equal(addressA, RootMapCodec.Decode(rootA).Unwrap()["state"]);
            Assert.Equal(addressB, RootMapCodec.Decode(rootB).Unwrap()["state"]);
            var audit = cold.Audit().Unwrap();
            Assert.Equal(3, audit.RefCount);
            Assert.Equal(6, audit.SnapshotCount);
            Assert.Equal(1, audit.ForkCount);
            Assert.Equal(1, audit.TagCount);
        }
    }

    [Fact]
    public void CuHistoryHonorsSnapshotBudgetAndCancellationWithoutDeliveringUnqualifiedFallback() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        RefRevision a = store.CreateRef(7, [1], out _).Unwrap();
        RefRevision b = store.CreateRef(19, [2], out _).Unwrap();
        var first = store.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [3]), (b.RefId, [4])), out _).Unwrap();
        var second = store.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, [5]), (b.RefId, [6])), out _).Unwrap();
        var bounded = new List<RefRevision>();
        var result = store.ReadRefHistory(a.RefId, new HistoryBudget(2, long.MaxValue), snapshot => {
            bounded.Add(snapshot.Revision);
            return true;
        });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { second[a.RefId], first[a.RefId] }, bounded);
        using var cancellation = new CancellationTokenSource();
        RefBytesSnapshot? held = null;
        var cancelled = Assert.Throws<OperationCanceledException>(() => store.ReadRefHistory(a.RefId,
            ConditionalUpdatePublicFixture.Unlimited, snapshot => {
                held = snapshot;
                cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.NotNull(held);
        Assert.Equal(second[a.RefId], held.Revision);
        Assert.Equal(new byte[] { 5 }, held.Payload.ToArray());
        Assert.Equal(second[a.RefId], store.ReadRef(a.RefId).Unwrap().Revision);
        store.PublishRef(a.RefId, [7], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 5 }, held.Payload.ToArray());
    }
}
