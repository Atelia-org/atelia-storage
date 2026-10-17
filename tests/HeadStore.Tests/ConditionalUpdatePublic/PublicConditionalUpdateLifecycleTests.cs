using System.Buffers.Binary;
using Atelia.FrameStore;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ConditionalUpdatePublic;

public sealed class PublicConditionalUpdateLifecycleTests {
    [Fact]
    public void MemberTableOrdersTheCompleteUnsignedLocalIdDomain() {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId low;
        HeadEntryId high;
        HeadEntryId highest;
        IReadOnlyDictionary<HeadId, HeadEntryId> published;
        using (var store = fixture.Create(HeadStore.MaxLocalHeadId - 1)) {
            highest = store.CreateHead(HeadStore.MaxLocalHeadId - 1, [1], out _).Unwrap();
            high = store.CreateHead(0x800000, [2], out _).Unwrap();
            low = store.CreateHead(7, [3], out _).Unwrap();
            published = store.PublishHeads(ConditionalUpdatePublicFixture.Changes(
                (highest.HeadId, [4]), (high.HeadId, [5]), (low.HeadId, [6])), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        fixture.AssertWire(published[highest.HeadId], published, [4], long.MinValue, long.MaxValue);
        fixture.AssertWire(published[high.HeadId], published, [5], long.MinValue, long.MaxValue);
        fixture.AssertWire(published[low.HeadId], published, [6], long.MinValue, long.MaxValue);
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal(6, readOnly.Audit().Unwrap().EntryCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void DifferentOpaqueRootsHaveOneSortedMemberTableSharedTimeAndFreshTicketsAcrossColdOwners(int count) {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId[] initial = new HeadEntryId[count];
        byte[][] payloads = Enumerable.Range(0, count).Select(index => Enumerable.Repeat((byte)(0x81 + index), 1 + index * 137).ToArray()).ToArray();
        IReadOnlyDictionary<HeadId, HeadEntryId> first;
        IReadOnlyDictionary<HeadId, HeadEntryId> second;
        List<HeadEntry>[] saved;
        long before;
        long after;
        long time;
        using (var store = fixture.Create()) {
            uint[] locals = [19, 7, 23];
            for (int index = 0; index < count; index++) { initial[index] = store.CreateHead(locals[index], [0xff], out _).Unwrap(); }
            var changes = ConditionalUpdatePublicFixture.Changes(initial.Select((entryId, index) => (entryId.HeadId, payloads[index])).ToArray());
            before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            first = store.PublishHeads(changes, out var outcome).Unwrap();
            after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(count, first.Count);
            time = store.ReadHead(initial[0].HeadId).Unwrap().RecordedAtUnixMs;
            Assert.InRange(time, before, after);
            foreach (HeadEntryId entryId in initial) {
                Assert.NotEqual(entryId, first[entryId.HeadId]);
                Assert.Equal(time, store.ReadHead(entryId.HeadId).Unwrap().RecordedAtUnixMs);
            }
            // Equal payloads are still a new publication at fresh physical positions.
            second = store.PublishHeads(changes, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            saved = initial.Select(entryId => ConditionalUpdatePublicFixture.History(store, entryId.HeadId)).ToArray();
            for (int index = 0; index < count; index++) {
                HeadId id = initial[index].HeadId;
                Assert.NotEqual(first[id], second[id]);
                Assert.True(second[id].Ticket.Offset > first[id].Ticket.Offset);
                Assert.Equal(new[] { second[id], first[id], initial[index] }, saved[index].Select(entry => entry.Id));
                Assert.Equal(payloads[index], saved[index][0].Payload.ToArray());
                Assert.Equal(payloads[index], saved[index][1].Payload.ToArray());
            }
        }
        for (int index = 0; index < count; index++) {
            fixture.AssertWire(first[initial[index].HeadId], first, payloads[index], before, after, time);
        }
        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            for (int index = 0; index < count; index++) {
                Assert.Equal(second[initial[index].HeadId], cold.ReadHead(initial[index].HeadId).Unwrap().Id);
                Assert.Equal(saved[index].Select(entry => entry.Id), ConditionalUpdatePublicFixture.History(cold, initial[index].HeadId).Select(entry => entry.Id));
            }
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            var audit = cold.Audit().Unwrap();
            Assert.Equal(count, audit.HeadCount);
            Assert.Equal(3L * count, audit.EntryCount);
            for (int index = 0; index < count; index++) {
                Assert.Equal(payloads[index], cold.ReadHead(initial[index].HeadId).Unwrap().Payload.ToArray());
            }
        }
        Assert.Equal(payloads[0], saved[0][0].Payload.ToArray());
        Assert.Equal(initial[0].HeadId, first[initial[0].HeadId].HeadId);
    }

    [Fact]
    public void MinimumAndMaximumRootSlicesAreOpaqueOwnedBytesAndSingleItemUsesOrdinaryEntry() {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId a;
        HeadEntryId b;
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        HeadEntryId single;
        byte[] buffer = Enumerable.Repeat((byte)0xf7, Store.MaxHeadEntryPayloadLength + 2).ToArray();
        byte[] expectedMaximum = buffer.AsSpan(1, Store.MaxHeadEntryPayloadLength).ToArray();
        HeadEntry saved;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [a.HeadId] = new byte[] { 0xff },
                [b.HeadId] = buffer.AsMemory(1, Store.MaxHeadEntryPayloadLength)
            };
            group = store.PublishHeads(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            buffer.AsSpan().Clear();
            changes.Clear();
            saved = store.ReadHead(b.HeadId).Unwrap();
            Assert.Equal(expectedMaximum, saved.Payload.ToArray());
            Assert.Equal(new byte[] { 0xff }, store.ReadHead(a.HeadId).Unwrap().Payload.ToArray());
            Assert.True(NamedFrameMapCodec.Decode(saved.Payload).IsFailure);
            single = store.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [0xee])), out outcome).Unwrap()[a.HeadId];
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(7), RbfCacheMode.Off))
        using (var frame = file.ReadPooledFrame(single.Ticket).Unwrap()) {
            Assert.Equal(1u, frame.Tag);
            Assert.Equal(8, frame.TailMetaLength);
            Assert.Equal(new byte[] { 0xee }, frame.PayloadAndMeta[..^8].ToArray());
        }
        Assert.Equal(group[b.HeadId], saved.Id);
        Assert.Equal(expectedMaximum, saved.Payload.ToArray());
    }

    [Fact]
    public void EstablishedGroupSurvivesSingleHeadCoverAndOldCuForkReferencesExactRootWithDataTagsAndAudit() {
        using var fixture = new ConditionalUpdatePublicFixture();
        HeadEntryId a0;
        HeadEntryId b0;
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        HeadEntryId a2;
        HeadId child;
        FrameAddress addressA;
        FrameAddress addressB;
        byte[] rootA;
        byte[] rootB;
        byte[] dataA = "application-data-a"u8.ToArray();
        byte[] dataB = "application-data-b"u8.ToArray();
        using (var store = fixture.Create()) {
            a0 = store.CreateHead(7, [0xff], out _).Unwrap();
            b0 = store.CreateHead(19, [0xff], out _).Unwrap();
            addressA = fixture.Data.Append(17, dataA).Unwrap();
            addressB = fixture.Data.Append(23, dataB).Unwrap();
            rootA = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressA }).Unwrap();
            rootB = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressB }).Unwrap();
            group = store.PublishHeads(ConditionalUpdatePublicFixture.Changes((b0.HeadId, rootB), (a0.HeadId, rootA)), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.CreateTag("old-cu", [a0.HeadId], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            a2 = store.PublishHead(a0.HeadId, [0xa2], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group[b0.HeadId], store.ReadHead(b0.HeadId).Unwrap().Id);
            Assert.Equal(new[] { a2, group[a0.HeadId], a0 }, ConditionalUpdatePublicFixture.History(store, a0.HeadId).Select(entry => entry.Id));

        }
        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            // Both the entryId and the group's exact peer positions survive replacement of A's current head.
            HeadEntryId selected = default;
            Assert.Equal(HeadHistoryEnd.VisitorStopped, cold.ReadHeadHistory(a0.HeadId, ConditionalUpdatePublicFixture.Unlimited, entry => {
                if (entry.Id != group[a0.HeadId]) { return true; }
                selected = entry.Id;
                return false;
            }).Unwrap());
            Assert.Equal(group[a0.HeadId], selected);
            child = cold.ForkHead(23, selected, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(rootA, cold.ReadHead(child).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { group[a0.HeadId], a0 }, ConditionalUpdatePublicFixture.History(cold, child).Select(entry => entry.Id));
            HeadOriginInfo edge = Assert.Single(cold.ListHeadOrigins(3).Unwrap());
            Assert.Equal(child, edge.ChildHeadId);
            Assert.Equal(a0.HeadId, edge.SourceHeadId);
            Assert.Equal(group[a0.HeadId].Ticket, edge.SourceEntryTicket);
        }
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(23), RbfCacheMode.Off)) {
            var frames = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(frames.MoveNext());
            Assert.Equal(0u, frames.Current.Tag);
            Assert.False(frames.MoveNext());
            Assert.Null(frames.TerminationError);
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(a2, cold.ReadHead(a0.HeadId).Unwrap().Id);
            Assert.Equal(group[b0.HeadId], cold.ReadHead(b0.HeadId).Unwrap().Id);
            Assert.Equal(rootB, cold.ReadHead(b0.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(group[a0.HeadId], cold.ReadTag("old-cu").Unwrap().Entries[a0.HeadId]);
            Assert.Equal(rootA, cold.ReadEntry(cold.ReadTag("old-cu").Unwrap().Entries[a0.HeadId]).Unwrap().Payload.ToArray());
            HeadSetTagInfo info = cold.ReadTagInfo("old-cu").Unwrap();
            Assert.Equal(1, info.EntryCount);
            Assert.Equal(HeadSetTagScanEnd.Complete, cold.ScanTags(tag => {
                Assert.Equal(info.Name, tag.Name);
                Assert.Equal(info.EntryCount, tag.EntryCount);
                return true;
            }).Unwrap());
            using var frameA = data.ReadFrame(NamedFrameMapCodec.Decode(cold.ReadHead(child).Unwrap().Payload).Unwrap()["state"]).Unwrap();
            using var frameB = data.ReadFrame(NamedFrameMapCodec.Decode(cold.ReadHead(b0.HeadId).Unwrap().Payload).Unwrap()["state"]).Unwrap();
            Assert.Equal(dataA, frameA.PayloadAndMeta.ToArray());
            Assert.Equal(dataB, frameB.PayloadAndMeta.ToArray());
            Assert.Equal(addressA, NamedFrameMapCodec.Decode(rootA).Unwrap()["state"]);
            Assert.Equal(addressB, NamedFrameMapCodec.Decode(rootB).Unwrap()["state"]);
            var audit = cold.Audit().Unwrap();
            Assert.Equal(3, audit.HeadCount);
            Assert.Equal(5, audit.EntryCount);
            Assert.Equal(1, audit.OriginCount);
            Assert.Equal(1, audit.TagCount);
        }
    }

    [Fact]
    public void CuHistoryHonorsEntryBudgetAndCancellationWithoutDeliveringUnqualifiedFallback() {
        using var fixture = new ConditionalUpdatePublicFixture();
        using var store = fixture.Create();
        HeadEntryId a = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId b = store.CreateHead(19, [2], out _).Unwrap();
        var first = store.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [3]), (b.HeadId, [4])), out _).Unwrap();
        var second = store.PublishHeads(ConditionalUpdatePublicFixture.Changes((a.HeadId, [5]), (b.HeadId, [6])), out _).Unwrap();
        var bounded = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(a.HeadId, new HeadHistoryBudget(2, long.MaxValue), entry => {
            bounded.Add(entry.Id);
            return true;
        });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", result.Error!.ErrorCode);
        Assert.Equal(new[] { second[a.HeadId], first[a.HeadId] }, bounded);
        using var cancellation = new CancellationTokenSource();
        HeadEntry? held = null;
        var cancelled = Assert.Throws<OperationCanceledException>(() => store.ReadHeadHistory(a.HeadId,
            ConditionalUpdatePublicFixture.Unlimited, entry => {
                held = entry;
                cancellation.Cancel();
                return true;
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        Assert.NotNull(held);
        Assert.Equal(second[a.HeadId], held.Id);
        Assert.Equal(new byte[] { 5 }, held.Payload.ToArray());
        Assert.Equal(second[a.HeadId], store.ReadHead(a.HeadId).Unwrap().Id);
        store.PublishHead(a.HeadId, [7], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 5 }, held.Payload.ToArray());
    }
}
