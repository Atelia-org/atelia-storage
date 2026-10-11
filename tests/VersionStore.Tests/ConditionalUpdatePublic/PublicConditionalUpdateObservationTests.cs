using Atelia.FrameStore;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.ConditionalUpdatePublic;

/// <summary>公开 CU 的观察组合；离线截尾镜像不是 kill、断电或设备故障证据。</summary>
public sealed class PublicConditionalUpdateObservationTests {
    [Fact]
    public void ReadOnlyNecessaryPeerWithCompleteBodyMissingSuffixCannotBecomeUncommittedFallback() {
        using var fixture = new ConditionalUpdatePublicFixture();
        Group group = SeedCompletedGroup(fixture);
        var (_, partial) = RemoveLastKeyAndFence(fixture, group.B);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            // A is locally complete. Its necessary B member needs physical repair, so neither
            // current nor history may reinterpret that error as an absent member and return A0.
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(group.A.RefId); });
            var delivered = new List<RefRevision>();
            Assert.Throws<InvalidDataException>(() => {
                readOnly.ReadRefHistory(group.A.RefId, ConditionalUpdatePublicFixture.Unlimited, snapshot => {
                    delivered.Add(snapshot.Revision);
                    return true;
                });
            });
            Assert.Empty(delivered);
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(group.B.RefId); });
        }
        Assert.Equal(partial, File.ReadAllBytes(fixture.RefPath(group.B.RefId.LocalId)));
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void OrdinaryCoverDoesNotRecoverPeerUntilNecessaryAccessAndRecoveredOldGroupRemainsForkable() {
        using var fixture = new ConditionalUpdatePublicFixture();
        Group group = SeedCompletedGroup(fixture);
        var (complete, partial) = RemoveLastKeyAndFence(fixture, group.B);
        RefRevision a2;
        using (var writer = fixture.Open()) {
            Assert.Equal(partial, File.ReadAllBytes(fixture.RefPath(group.B.RefId.LocalId)));
            a2 = writer.PublishRef(group.A.RefId, [0xa2], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(a2, writer.ReadRef(group.A.RefId).Unwrap().Revision);
            Assert.Equal(partial, File.ReadAllBytes(fixture.RefPath(group.B.RefId.LocalId)));
            // A's local preflight does not keep a handle to B or trigger B's recovery.
            using var untouchedPeer = new FileStream(fixture.RefPath(group.B.RefId.LocalId),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        var beforeReadOnly = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal(a2, readOnly.ReadRef(group.A.RefId).Unwrap().Revision);
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(group.B.RefId); });
            var delivered = new List<RefRevision>();
            Assert.Throws<InvalidDataException>(() => {
                readOnly.ReadRefHistory(group.A.RefId, ConditionalUpdatePublicFixture.Unlimited, snapshot => {
                    delivered.Add(snapshot.Revision);
                    return true;
                });
            });
            Assert.Equal(new[] { a2 }, delivered);
        }
        fixture.AssertUnchanged(beforeReadOnly);

        RefRevision clone;
        using (var writer = fixture.Open()) {
            Assert.Equal(partial, File.ReadAllBytes(fixture.RefPath(group.B.RefId.LocalId)));
            RefBytesSnapshot recovered = writer.ReadRef(group.B.RefId).Unwrap();
            Assert.Equal(group.B, recovered.Revision);
            Assert.Equal(group.RootB, recovered.Payload.ToArray());
            Assert.Equal(a2, writer.ReadRef(group.A.RefId).Unwrap().Revision);
            Assert.Equal(new[] { a2, group.A, group.AInitial },
                ConditionalUpdatePublicFixture.History(writer, group.A.RefId).Select(snapshot => snapshot.Revision));
            clone = writer.ForkRef(23, group.A, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(group.RootA, writer.ReadRef(clone.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { clone, group.A, group.AInitial },
                ConditionalUpdatePublicFixture.History(writer, clone.RefId).Select(snapshot => snapshot.Revision));
        }
        // Compare after releasing the writable ref's FileShare.None handle. Public target
        // access completed the original suffix without truncating or replacing its CU body.
        Assert.Equal(complete, File.ReadAllBytes(fixture.RefPath(group.B.RefId.LocalId)));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(a2, cold.ReadRef(group.A.RefId).Unwrap().Revision);
        Assert.Equal(group.B, cold.ReadRef(group.B.RefId).Unwrap().Revision);
        Assert.Equal(new[] { clone, group.A, group.AInitial },
            ConditionalUpdatePublicFixture.History(cold, clone.RefId).Select(snapshot => snapshot.Revision));
        Assert.Equal(6, cold.Audit().Unwrap().SnapshotCount);
    }

    [Fact]
    public void UnrelatedActiveKnownSizeDataBuilderDoesNotBlockCuConfirmationOfCompletedDependencies() {
        using var fixture = new ConditionalUpdatePublicFixture();
        byte[] bytesA = "completed-cu-data-a"u8.ToArray();
        byte[] bytesB = "completed-cu-data-b"u8.ToArray();
        IReadOnlyDictionary<RefId, RefRevision> revisions;
        RefRevision a;
        RefRevision b;
        FrameAddress addressA;
        FrameAddress addressB;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [1], out _).Unwrap();
            b = store.CreateRef(19, [2], out _).Unwrap();
            addressA = fixture.Data.Append(17, bytesA).Unwrap();
            addressB = fixture.Data.Append(23, bytesB).Unwrap();
            byte[] rootA = RootMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressA }).Unwrap();
            byte[] rootB = RootMapCodec.Encode(new Dictionary<string, FrameAddress> { ["state"] = addressB }).Unwrap();
            using (var unrelated = fixture.Data.BeginAppend(65, 0, out var uncompleted)) {
                var writer = unrelated.PayloadAndMeta;
                writer.GetSpan(3)[..3].Fill(0xee);
                writer.Advance(3);
                revisions = store.PublishRefs(ConditionalUpdatePublicFixture.Changes(
                    (a.RefId, rootA), (b.RefId, rootB)), out var outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
                Assert.Equal(revisions[a.RefId], store.ReadRef(a.RefId).Unwrap().Revision);
                Assert.Equal(revisions[b.RefId], store.ReadRef(b.RefId).Unwrap().Revision);
                // The unrelated address is still uncompleted; no dependency points to it.
                Assert.Throws<InvalidOperationException>(() => { fixture.Data.ReadFrame(uncompleted); });
            }
        }
        fixture.Data.Dispose();
        using var data = DataStore.OpenReadOnly(fixture.DataRoot);
        using var cold = Store.OpenReadOnly(fixture.Root, data);
        RefBytesSnapshot currentA = cold.ReadRef(a.RefId).Unwrap();
        RefBytesSnapshot currentB = cold.ReadRef(b.RefId).Unwrap();
        Assert.Equal(revisions[a.RefId], currentA.Revision);
        Assert.Equal(revisions[b.RefId], currentB.Revision);
        FrameAddress coldAddressA = RootMapCodec.Decode(currentA.Payload).Unwrap()["state"];
        FrameAddress coldAddressB = RootMapCodec.Decode(currentB.Payload).Unwrap()["state"];
        Assert.Equal(addressA, coldAddressA);
        Assert.Equal(addressB, coldAddressB);
        using var frameA = data.ReadFrame(coldAddressA).Unwrap();
        using var frameB = data.ReadFrame(coldAddressB).Unwrap();
        Assert.Equal(bytesA, frameA.PayloadAndMeta.ToArray());
        Assert.Equal(bytesB, frameB.PayloadAndMeta.ToArray());
    }

    private static Group SeedCompletedGroup(ConditionalUpdatePublicFixture fixture) {
        using var store = fixture.Create();
        RefRevision a = store.CreateRef(7, [0xa0], out _).Unwrap();
        RefRevision b = store.CreateRef(19, [0xb0], out _).Unwrap();
        byte[] rootA = Enumerable.Repeat((byte)0xa1, 129).ToArray();
        byte[] rootB = Enumerable.Repeat((byte)0xb1, 193).ToArray();
        var published = store.PublishRefs(ConditionalUpdatePublicFixture.Changes(
            (a.RefId, rootA), (b.RefId, rootB)), out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        return new Group(a, b, published, rootA, rootB);
    }

    private static (byte[] Complete, byte[] Partial) RemoveLastKeyAndFence(
        ConditionalUpdatePublicFixture fixture, RefRevision revision) {
        string path = fixture.RefPath(revision.RefId.LocalId);
        byte[] complete = File.ReadAllBytes(path);
        int suffixWordLength;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off)) {
            long physicalEnd = file.GetPhysicalOffsetImmediatelyAfter(revision.Ticket);
            Assert.Equal(complete.LongLength, physicalEnd);
            suffixWordLength = checked((int)(physicalEnd - revision.Ticket.Offset - revision.Ticket.Length));
            Assert.Equal(sizeof(uint), suffixWordLength);
        }
        // The public RBF3 format fixes TailKey to the same 4B width as the exposed trailing
        // Fence. Remove exactly those two words, retaining the original complete body.
        int bodyEnd = checked((int)(revision.Ticket.Offset + revision.Ticket.Length - suffixWordLength));
        Assert.Equal(complete.Length - 2 * suffixWordLength, bodyEnd);
        byte[] partial = complete[..bodyEnd];
        File.WriteAllBytes(path, partial);
        return (complete, partial);
    }

    private sealed record Group(RefRevision AInitial, RefRevision BInitial,
        IReadOnlyDictionary<RefId, RefRevision> Published, byte[] RootA, byte[] RootB) {
        internal RefRevision A => Published[AInitial.RefId];
        internal RefRevision B => Published[BInitial.RefId];
    }
}
