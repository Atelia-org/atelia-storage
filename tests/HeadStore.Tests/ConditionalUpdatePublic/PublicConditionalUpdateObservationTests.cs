using Atelia.FrameStore;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ConditionalUpdatePublic;

/// <summary>公开 CU 的观察组合；离线截尾镜像不是 kill、断电或设备故障证据。</summary>
public sealed class PublicConditionalUpdateObservationTests {
    [Fact]
    public void ReadOnlyNecessaryPeerWithCompleteBodyMissingSuffixCannotBecomeUncommittedFallback() {
        using var fixture = new ConditionalUpdatePublicFixture();
        Group group = SeedCompletedGroup(fixture);
        var (_, partial) = RemoveLastKeyAndFence(fixture, group.B);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            // A is locally complete. Its necessary B member needs physical repair, so neither
            // current nor history may reinterpret that error as an absent member and return A0.
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(group.A.HeadId); });
            var delivered = new List<HeadEntryId>();
            Assert.Throws<InvalidDataException>(() => {
                readOnly.ReadHeadHistory(group.A.HeadId, ConditionalUpdatePublicFixture.Unlimited, entry => {
                    delivered.Add(entry.Id);
                    return true;
                });
            });
            Assert.Empty(delivered);
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(group.B.HeadId); });
        }
        Assert.Equal(partial, File.ReadAllBytes(fixture.HeadPath(group.B.HeadId.LocalId)));
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void OrdinaryCoverDoesNotRecoverPeerUntilNecessaryAccessAndRecoveredOldGroupRemainsForkable() {
        using var fixture = new ConditionalUpdatePublicFixture();
        Group group = SeedCompletedGroup(fixture);
        var (complete, partial) = RemoveLastKeyAndFence(fixture, group.B);
        HeadEntryId a2;
        using (var writer = fixture.Open()) {
            Assert.Equal(partial, File.ReadAllBytes(fixture.HeadPath(group.B.HeadId.LocalId)));
            a2 = writer.PublishHead(group.A.HeadId, [0xa2], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(a2, writer.ReadHead(group.A.HeadId).Unwrap().Id);
            Assert.Equal(partial, File.ReadAllBytes(fixture.HeadPath(group.B.HeadId.LocalId)));
            // A's local preflight does not keep a handle to B or trigger B's recovery.
            using var untouchedPeer = new FileStream(fixture.HeadPath(group.B.HeadId.LocalId),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        var beforeReadOnly = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal(a2, readOnly.ReadHead(group.A.HeadId).Unwrap().Id);
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(group.B.HeadId); });
            var delivered = new List<HeadEntryId>();
            Assert.Throws<InvalidDataException>(() => {
                readOnly.ReadHeadHistory(group.A.HeadId, ConditionalUpdatePublicFixture.Unlimited, entry => {
                    delivered.Add(entry.Id);
                    return true;
                });
            });
            Assert.Equal(new[] { a2 }, delivered);
        }
        fixture.AssertUnchanged(beforeReadOnly);

        HeadEntryId clone;
        using (var writer = fixture.Open()) {
            Assert.Equal(partial, File.ReadAllBytes(fixture.HeadPath(group.B.HeadId.LocalId)));
            HeadEntry recovered = writer.ReadHead(group.B.HeadId).Unwrap();
            Assert.Equal(group.B, recovered.Id);
            Assert.Equal(group.RootB, recovered.Payload.ToArray());
            Assert.Equal(a2, writer.ReadHead(group.A.HeadId).Unwrap().Id);
            Assert.Equal(new[] { a2, group.A, group.AInitial },
                ConditionalUpdatePublicFixture.History(writer, group.A.HeadId).Select(entry => entry.Id));
            clone = writer.ForkHead(23, group.A, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group.RootA, writer.ReadHead(clone.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { clone, group.A, group.AInitial },
                ConditionalUpdatePublicFixture.History(writer, clone.HeadId).Select(entry => entry.Id));
        }
        // Compare after releasing the writable ref's FileShare.None handle. Public target
        // access completed the original suffix without truncating or replacing its CU body.
        Assert.Equal(complete, File.ReadAllBytes(fixture.HeadPath(group.B.HeadId.LocalId)));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(a2, cold.ReadHead(group.A.HeadId).Unwrap().Id);
        Assert.Equal(group.B, cold.ReadHead(group.B.HeadId).Unwrap().Id);
        Assert.Equal(new[] { clone, group.A, group.AInitial },
            ConditionalUpdatePublicFixture.History(cold, clone.HeadId).Select(entry => entry.Id));
        Assert.Equal(6, cold.Audit().Unwrap().EntryCount);
    }

    [Fact]
    public void UnrelatedActiveKnownSizeDataBuilderDoesNotBlockCuConfirmationOfCompletedDependencies() {
        using var fixture = new ConditionalUpdatePublicFixture();
        byte[] bytesA = "completed-cu-data-a"u8.ToArray();
        byte[] bytesB = "completed-cu-data-b"u8.ToArray();
        IReadOnlyDictionary<HeadId, HeadEntryId> entryIds;
        HeadEntryId a;
        HeadEntryId b;
        FrameAddress addressA;
        FrameAddress addressB;
        using (var store = fixture.Create()) {
            a = store.CreateHead(7, [1], out _).Unwrap();
            b = store.CreateHead(19, [2], out _).Unwrap();
            addressA = fixture.Data.Append(17, bytesA).Unwrap();
            addressB = fixture.Data.Append(23, bytesB).Unwrap();
            byte[] rootA = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressA }).Unwrap();
            byte[] rootB = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressB }).Unwrap();
            using (var unrelated = fixture.Data.BeginAppend(65, 0, out var uncompleted)) {
                var writer = unrelated.PayloadAndMeta;
                writer.GetSpan(3)[..3].Fill(0xee);
                writer.Advance(3);
                entryIds = store.PublishHeads(ConditionalUpdatePublicFixture.Changes(
                    (a.HeadId, rootA), (b.HeadId, rootB)), out var outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
                Assert.Equal(entryIds[a.HeadId], store.ReadHead(a.HeadId).Unwrap().Id);
                Assert.Equal(entryIds[b.HeadId], store.ReadHead(b.HeadId).Unwrap().Id);
                // The unrelated address is still uncompleted; no dependency points to it.
                Assert.Throws<InvalidOperationException>(() => { fixture.Data.ReadFrame(uncompleted); });
            }
        }
        fixture.Data.Dispose();
        using var data = DataStore.OpenReadOnly(fixture.DataRoot);
        using var cold = Store.OpenReadOnly(fixture.Root, data);
        HeadEntry currentA = cold.ReadHead(a.HeadId).Unwrap();
        HeadEntry currentB = cold.ReadHead(b.HeadId).Unwrap();
        Assert.Equal(entryIds[a.HeadId], currentA.Id);
        Assert.Equal(entryIds[b.HeadId], currentB.Id);
        FrameAddress coldAddressA = NamedFrameMapCodec.Decode(currentA.Payload).Unwrap()["state"];
        FrameAddress coldAddressB = NamedFrameMapCodec.Decode(currentB.Payload).Unwrap()["state"];
        Assert.Equal(addressA, coldAddressA);
        Assert.Equal(addressB, coldAddressB);
        using var frameA = data.ReadFrame(coldAddressA).Unwrap();
        using var frameB = data.ReadFrame(coldAddressB).Unwrap();
        Assert.Equal(bytesA, frameA.PayloadAndMeta.ToArray());
        Assert.Equal(bytesB, frameB.PayloadAndMeta.ToArray());
    }

    private static Group SeedCompletedGroup(ConditionalUpdatePublicFixture fixture) {
        using var store = fixture.Create();
        HeadEntryId a = store.CreateHead(7, [0xa0], out _).Unwrap();
        HeadEntryId b = store.CreateHead(19, [0xb0], out _).Unwrap();
        byte[] rootA = Enumerable.Repeat((byte)0xa1, 129).ToArray();
        byte[] rootB = Enumerable.Repeat((byte)0xb1, 193).ToArray();
        var published = store.PublishHeads(ConditionalUpdatePublicFixture.Changes(
            (a.HeadId, rootA), (b.HeadId, rootB)), out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return new Group(a, b, published, rootA, rootB);
    }

    private static (byte[] Complete, byte[] Partial) RemoveLastKeyAndFence(
        ConditionalUpdatePublicFixture fixture, HeadEntryId entryId) {
        string path = fixture.HeadPath(entryId.HeadId.LocalId);
        byte[] complete = File.ReadAllBytes(path);
        int suffixWordLength;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)) {
            long physicalEnd = file.GetPhysicalOffsetImmediatelyAfter(entryId.Ticket);
            Assert.Equal(complete.LongLength, physicalEnd);
            suffixWordLength = checked((int)(physicalEnd - entryId.Ticket.Offset - entryId.Ticket.Length));
            Assert.Equal(sizeof(uint), suffixWordLength);
        }
        // The public RBF3 format fixes TailKey to the same 4B width as the exposed trailing
        // Fence. Remove exactly those two words, retaining the original complete body.
        int bodyEnd = checked((int)(entryId.Ticket.Offset + entryId.Ticket.Length - suffixWordLength));
        Assert.Equal(complete.Length - 2 * suffixWordLength, bodyEnd);
        byte[] partial = complete[..bodyEnd];
        File.WriteAllBytes(path, partial);
        return (complete, partial);
    }

    private sealed record Group(HeadEntryId AInitial, HeadEntryId BInitial,
        IReadOnlyDictionary<HeadId, HeadEntryId> Published, byte[] RootA, byte[] RootB) {
        internal HeadEntryId A => Published[AInitial.HeadId];
        internal HeadEntryId B => Published[BInitial.HeadId];
    }
}
