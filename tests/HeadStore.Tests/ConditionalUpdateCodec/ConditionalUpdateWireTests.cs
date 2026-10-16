using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Tests.Format;
using Atelia.HeadStore.Tests.Public;
using Xunit;
using Codec = Atelia.HeadStore.Internal.Records.ConditionalUpdateCodec;

namespace Atelia.HeadStore.Tests.ConditionalUpdateCodec;

public sealed class ConditionalUpdateWireTests {
    // Independent LE vector: count 2, local 1 => (104,68), local 7 => (148,72), root FA.
    private const string GoldenHex = "02000000010000001100006800000000070000001200009400000000FA";
    private static readonly SizedPtr s_self = SizedPtr.Create(104, 68);
    private static readonly SizedPtr s_peer = SizedPtr.Create(148, 72);

    [Fact]
    public void IndependentGoldenUsesFixedLocalIdAndRawPackedTickets() {
        byte[] golden = Convert.FromHexString(GoldenHex);
        Assert.True(Codec.TryRead(golden, 1, s_self, out int rootOffset));
        Assert.Equal(28, rootOffset);
        Assert.Equal(new byte[] { 0xfa }, golden[rootOffset..]);
        Assert.Equal(2, Codec.GetMemberCount(golden));
        Codec.GetMember(golden, 0, out uint firstId, out SizedPtr firstTicket);
        Codec.GetMember(golden, 1, out uint secondId, out SizedPtr secondTicket);
        Assert.Equal(1u, firstId);
        Assert.Equal(s_self, firstTicket);
        Assert.Equal(7u, secondId);
        Assert.Equal(s_peer, secondTicket);
        Assert.NotEqual(s_self.Serialize(), s_self.Packed);
        Assert.Equal(golden[..28], Codec.EncodeMembers([
            new ConditionalUpdateMember(1, s_self), new ConditionalUpdateMember(7, s_peer)
        ]));
    }

    [Fact]
    public void GroupEqualityIgnoresRootsAndRequiresAllPackedBits() {
        byte[] first = Convert.FromHexString(GoldenHex);
        byte[] second = [.. first[..28], 1, 2, 3, 4, 5];
        Assert.True(Codec.TryRead(second, 7, s_peer, out int rootOffset));
        Assert.Equal(28, rootOffset);
        Assert.True(Codec.MembersEqual(first, second));
        BinaryPrimitives.WriteUInt64LittleEndian(second.AsSpan(8), SizedPtr.Create(108, 68).Packed);
        Assert.True(Codec.TryRead(second, 7, s_peer, out _));
        Assert.False(Codec.MembersEqual(first, second));
    }

    [Theory]
    [InlineData(0)] // Count zero.
    [InlineData(1)] // Single member is not CU.
    [InlineData(2)] // Host absent.
    [InlineData(3)] // Local zero.
    [InlineData(4)] // Duplicate locals.
    [InlineData(5)] // Unsorted locals.
    [InlineData(6)] // Default peer ticket.
    [InlineData(7)] // Peer before minimum header successor.
    [InlineData(8)] // Peer shorter than a CU for this count.
    [InlineData(9)] // Peer larger than this count plus a maximum root.
    [InlineData(10)] // Self has wrong location.
    [InlineData(11)] // Untrusted huge count must fail before indexing/allocation.
    public void IllegalTablesCannotProduceRootBoundary(int mutation) {
        byte[] payload = Convert.FromHexString(GoldenHex);
        uint host = 1;
        switch (mutation) {
            case 0: BinaryPrimitives.WriteUInt32LittleEndian(payload, 0); break;
            case 1: BinaryPrimitives.WriteUInt32LittleEndian(payload, 1); break;
            case 2: host = 3; break;
            case 3: BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 1); break;
            case 5: BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 8); break;
            case 6: payload.AsSpan(20, 8).Clear(); break;
            case 7: BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), SizedPtr.Create(56, 68).Packed); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), SizedPtr.Create(148, 64).Packed); break;
            case 9:
                int tooLarge = RbfFile.MeasureWriteSize(28 + HeadStore.MaxHeadEntryPayloadLength, 8).Value.FrameLength + 4;
                BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), SizedPtr.Create(148, tooLarge).Packed);
                break;
            case 10: BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), SizedPtr.Create(108, 68).Packed); break;
            case 11: BinaryPrimitives.WriteUInt32LittleEndian(payload, uint.MaxValue); break;
        }
        int rootOffset = 123;
        Assert.False(Codec.TryRead(payload, host, s_self, out rootOffset));
        Assert.Equal(0, rootOffset);
    }

    [Fact]
    public void TruncationAndOversizedRootDoNotLeaveAValidatedBoundary() {
        byte[] golden = Convert.FromHexString(GoldenHex);
        for (int length = 0; length < golden.Length; length++) {
            Assert.False(Codec.TryRead(golden.AsSpan(0, length), 1, s_self, out int offset));
            Assert.Equal(0, offset);
        }
        byte[] oversized = new byte[28 + HeadStore.MaxHeadEntryPayloadLength + 1];
        golden.AsSpan(0, 28).CopyTo(oversized);
        Assert.False(Codec.TryRead(oversized, 1, s_self, out _));
        Assert.False(Codec.TryRead(golden, 0, s_self, out _));
        Assert.False(Codec.TryRead(golden, 1, default, out _));
        // The member table alone does not legitimize a mismatched actual frame length.
        Assert.False(Codec.TryRead([.. golden[..28], 1, 2, 3, 4, 5], 1, s_self, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1048576)]
    public void ActualRootsAcceptBothLengthEndpoints(int rootLength) {
        int payloadLength = 28 + rootLength;
        SizedPtr self = SizedPtr.Create(104, RbfFile.MeasureWriteSize(payloadLength, 8).Value.FrameLength);
        byte[] payload = new byte[payloadLength];
        byte[] members = Codec.EncodeMembers([new ConditionalUpdateMember(1, self), new ConditionalUpdateMember(7, s_peer)]);
        members.CopyTo(payload, 0);
        Assert.True(Codec.TryRead(payload, 1, self, out int rootOffset));
        Assert.Equal(rootLength, payload.Length - rootOffset);
    }

    [Fact]
    public void MemberCountIsBoundedByTheActualPayloadRatherThanAnArbitraryCeiling() {
        const int count = 4097;
        int payloadLength = 4 + 12 * count + 1;
        SizedPtr ticket = SizedPtr.Create(SizedPtr.MaxOffset,
            RbfFile.MeasureWriteSize(payloadLength, 8).Value.FrameLength);
        var members = new ConditionalUpdateMember[count];
        for (int index = 0; index < count; index++) { members[index] = new ConditionalUpdateMember((uint)index + 1, ticket); }
        byte[] payload = [.. Codec.EncodeMembers(members), 0];
        Assert.True(Codec.TryRead(payload, count, ticket, out int rootOffset));
        Assert.Equal(payloadLength - 1, rootOffset);
        Assert.Equal(count, Codec.GetMemberCount(payload));
        Assert.True(ticket.EndOffsetExclusive > SizedPtr.MaxOffset);
    }

    [Fact]
    public void EncoderRejectsInvalidCanonicalTablesBeforeProducingBytes() {
        ConditionalUpdateMember[][] rejected = [
            [], [new(1, s_self)],
            [new(0, s_self), new(7, s_peer)],
            [new(7, s_peer), new(1, s_self)],
            [new(1, s_self), new(1, s_peer)],
            [new(1, s_self), new(7, default)]
        ];
        foreach (ConditionalUpdateMember[] members in rejected) {
            Assert.Throws<ArgumentException>(() => Codec.EncodeMembers(members));
        }
    }

    [Fact]
    public void ForkOriginCoordinatesAllowCUAndTheFullSizedPtrLengthRange() {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId destination = HeadId.Create(identity, 1);
        HeadId source = HeadId.Create(identity, 7);
        foreach (int length in new[] { 68, 1048640, SizedPtr.MaxLength }) {
            ForkOrigin origin = new(source, SizedPtr.Create(SizedPtr.MaxOffset, length));
            byte[] header = new byte[HeadHeaderCodec.WithOriginPayloadSize];
            Assert.True(HeadHeaderCodec.TryWrite(identity, destination, origin, header));
            Assert.True(HeadHeaderCodec.TryRead(header, identity, destination, out HeadHeader checkedHeader));
            Assert.Equal(origin, checkedHeader.Origin);
        }
    }

    [Fact]
    public void PublicationShapeBoundsDimensionsWithoutClaimingCUEnvelopeOrCommit() {
        using var fixture = new PublicHeadStoreFixture();
        using var file = RbfFile.CreateNew(Path.Combine(fixture.Sandbox, "shape.rbf"), RbfCacheMode.Off);
        SizedPtr ordinary = file.Append(HeadRecordCodec.SingleEntryTag, [0xff], new byte[8]).Unwrap();
        SizedPtr malformedCU = file.Append(HeadRecordCodec.ConditionalUpdateTag, new byte[29], new byte[8]).Unwrap();
        Assert.True(HeadRecordCodec.IsPublicationShape(file.ReadFrameInfo(ordinary).Unwrap()));
        Assert.True(HeadRecordCodec.IsPublicationShape(file.ReadFrameInfo(malformedCU).Unwrap()));
        Assert.False(Codec.TryRead(new byte[29], 1, malformedCU, out _));
        Assert.False(HeadRecordCodec.IsSingleEntryShape(file.ReadFrameInfo(malformedCU).Unwrap()));
        foreach (var (tag, payloadLength, metaLength) in new[] { (2u, 28, 8), (2u, 29, 7), (3u, 29, 8), (1u, 0, 8) }) {
            SizedPtr invalid = file.Append(tag, new byte[payloadLength], new byte[metaLength]).Unwrap();
            Assert.False(HeadRecordCodec.IsPublicationShape(file.ReadFrameInfo(invalid).Unwrap()));
        }
    }
}
