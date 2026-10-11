using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdatePublic;

/// <summary>完整 public RBF Append 构造的离线记录；只证明协议读取，不声称真实 crash/flush 故障。</summary>
public sealed class PublicConditionalUpdateMirrorTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteLocalCuWithMissingPeerIsSkippedAndFreshRetryCannotResurrectOldGroup(bool sameLength) {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [0xa0], out _).Unwrap();
            b = store.CreateRef(19, [0xb0], out _).Unwrap();
        }
        byte[] root = Enumerable.Repeat((byte)0xe1, 129).ToArray();
        SizedPtr qa = fixture.Predict(7, 2, root.Length);
        SizedPtr qb = fixture.Predict(19, 2, root.Length);
        byte[] envelope = ConditionalUpdatePublicFixture.EncodeEnvelope([(7, qa), (19, qb)], root);
        Assert.Equal(qa, fixture.AppendEnvelope(7, envelope));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Equal(a, readOnly.ReadRef(a.RefId).Unwrap().Revision);
            Assert.Equal(b, readOnly.ReadRef(b.RefId).Unwrap().Revision);
            Assert.Equal(new[] { a }, ConditionalUpdatePublicFixture.History(readOnly, a.RefId).Select(snapshot => snapshot.Revision));
            VersionStoreAudit audit = readOnly.Audit().Unwrap();
            Assert.Equal(2, audit.RefCount);
            Assert.Equal(2, audit.SnapshotCount);
            Assert.Equal(0, audit.ForkCount);
        }
        fixture.AssertUnchanged(before);
        IReadOnlyDictionary<RefId, RefRevision> fresh;
        byte[] freshA = Enumerable.Repeat((byte)0xa1, sameLength ? root.Length : 1).ToArray();
        byte[] freshB = Enumerable.Repeat((byte)0xb1, sameLength ? root.Length : 1).ToArray();
        using (var reopened = fixture.Open()) {
            fresh = reopened.PublishRefs(ConditionalUpdatePublicFixture.Changes((a.RefId, freshA), (b.RefId, freshB)), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.True(fresh[a.RefId].Ticket.Offset > qa.Offset);
            Assert.Equal(qb.Offset, fresh[b.RefId].Ticket.Offset);
            Assert.Equal(sameLength, qb == fresh[b.RefId].Ticket);
            Assert.Equal(new[] { fresh[a.RefId], a }, ConditionalUpdatePublicFixture.History(reopened, a.RefId).Select(snapshot => snapshot.Revision));
            Assert.Equal(new[] { fresh[b.RefId], b }, ConditionalUpdatePublicFixture.History(reopened, b.RefId).Select(snapshot => snapshot.Revision));
        }
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(fresh[a.RefId], cold.ReadRef(a.RefId).Unwrap().Revision);
        Assert.Equal(fresh[b.RefId], cold.ReadRef(b.RefId).Unwrap().Revision);
        Assert.Equal(new[] { fresh[a.RefId], a }, ConditionalUpdatePublicFixture.History(cold, a.RefId).Select(snapshot => snapshot.Revision));
        Assert.Equal(4, cold.Audit().Unwrap().SnapshotCount);
    }

    [Theory]
    [InlineData("count-zero")]
    [InlineData("count-one")]
    [InlineData("count-max")]
    [InlineData("truncated-table")]
    [InlineData("no-root")]
    [InlineData("oversize-root")]
    [InlineData("duplicate-local")]
    [InlineData("unsorted-local")]
    [InlineData("zero-local")]
    [InlineData("default-ticket")]
    [InlineData("zero-length-ticket")]
    [InlineData("short-ticket")]
    [InlineData("wrong-self")]
    [InlineData("meta-seven")]
    [InlineData("meta-nine")]
    public void IllegalLocalCuIsHardFailureEvenWhenPeerWasNeverAppended(string kind) {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [0xa0], out _).Unwrap();
            b = store.CreateRef(19, [0xb0], out _).Unwrap();
        }
        int rootLength = kind == "oversize-root" ? VersionStore.MaxRootPayloadLength + 1 : 129;
        byte[] root = Enumerable.Repeat((byte)0xf1, rootLength).ToArray();
        SizedPtr qa = fixture.Predict(7, 2, rootLength);
        SizedPtr qb = fixture.Predict(19, 2, rootLength);
        byte[] envelope = ConditionalUpdatePublicFixture.EncodeEnvelope([(7, qa), (19, qb)], root);
        byte[] metadata = new byte[8];
        switch (kind) {
            case "count-zero": BinaryPrimitives.WriteUInt32LittleEndian(envelope, 0); break;
            case "count-one": BinaryPrimitives.WriteUInt32LittleEndian(envelope, 1); break;
            case "count-max": BinaryPrimitives.WriteUInt32LittleEndian(envelope, uint.MaxValue); break;
            case "truncated-table": envelope = envelope[..20]; break;
            case "no-root": envelope = envelope[..28]; break;
            case "duplicate-local": BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(16), 7); break;
            case "unsorted-local":
                envelope = ConditionalUpdatePublicFixture.EncodeEnvelope([(19, qb), (7, qa)], root);
                break;
            case "zero-local": BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(16), 0); break;
            case "default-ticket": BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(20), 0); break;
            case "zero-length-ticket":
                BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(20), SizedPtr.Create(qb.Offset, 0).Packed);
                break;
            case "short-ticket":
                BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(20), SizedPtr.Create(qb.Offset, 4).Packed);
                break;
            case "wrong-self": BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(8), a.Ticket.Packed); break;
            case "meta-seven": metadata = new byte[7]; break;
            case "meta-nine": metadata = new byte[9]; break;
        }
        fixture.AppendEnvelope(7, envelope, metadata);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.True(readOnly.ReadRef(a.RefId).IsFailure);
            var delivered = new List<RefRevision>();
            var history = readOnly.ReadRefHistory(a.RefId, ConditionalUpdatePublicFixture.Unlimited, snapshot => {
                delivered.Add(snapshot.Revision);
                return true;
            });
            Assert.True(history.IsFailure);
            Assert.Empty(delivered);
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(b, readOnly.ReadRef(b.RefId).Unwrap().Revision);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void AuditValidatesUncommittedCuHiddenBelowHealthyOrdinaryCurrentHead() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [0xa0], out _).Unwrap();
            b = store.CreateRef(19, [0xb0], out _).Unwrap();
        }
        byte[] root = Enumerable.Repeat((byte)0xe1, 129).ToArray();
        SizedPtr qa = fixture.Predict(7, 2, root.Length);
        SizedPtr qb = fixture.Predict(19, 2, root.Length);
        byte[] bad = ConditionalUpdatePublicFixture.EncodeEnvelope([(7, qa), (19, qb)], root);
        // Invalid peer item is part of the local codec, before any Missing witness can short circuit.
        BinaryPrimitives.WriteUInt64LittleEndian(bad.AsSpan(20), 0);
        fixture.AppendEnvelope(7, bad);
        SizedPtr latest;
        using (IRbfFile file = RbfFile.OpenExisting(fixture.RefPath(7), out _, RbfCacheMode.Off)) {
            latest = file.Append(1, new byte[] { 0xa2 }, new byte[8]).Unwrap();
            file.DurableFlush();
        }
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            RefBytesSnapshot current = readOnly.ReadRef(a.RefId).Unwrap();
            Assert.Equal(latest, current.Revision.Ticket);
            Assert.Equal(new byte[] { 0xa2 }, current.Payload.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(latest, readOnly.ReadRef(a.RefId).Unwrap().Revision.Ticket);
            Assert.Equal(b, readOnly.ReadRef(b.RefId).Unwrap().Revision);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void SameTicketPeerMustBeFullyCheckedRatherThanAcceptedFromFramingOrMemberCount() {
        using var fixture = new ConditionalUpdatePublicFixture();
        RefRevision a;
        RefRevision b;
        using (var store = fixture.Create()) {
            a = store.CreateRef(7, [0xa0], out _).Unwrap();
            b = store.CreateRef(19, [0xb0], out _).Unwrap();
        }
        byte[] root = Enumerable.Repeat((byte)0xe1, 129).ToArray();
        SizedPtr qa = fixture.Predict(7, 2, root.Length);
        SizedPtr qb = fixture.Predict(19, 2, root.Length);
        byte[] envelope = ConditionalUpdatePublicFixture.EncodeEnvelope([(7, qa), (19, qb)], root);
        fixture.AppendEnvelope(7, envelope);
        byte[] badPeer = envelope.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(badPeer.AsSpan(20), b.Ticket.Packed);
        Assert.Equal(qb, fixture.AppendEnvelope(19, badPeer));
        using var readOnly = fixture.Open(readOnly: true);
        Assert.True(readOnly.ReadRef(a.RefId).IsFailure);
        Assert.True(readOnly.ReadRef(b.RefId).IsFailure);
        Assert.True(readOnly.Audit().IsFailure);
    }
}
