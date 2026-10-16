using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.HeadStore.Internal.Tags;
using Atelia.HeadStore.Tests.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class TagHeaderAndMetadataTests {
    [Fact]
    public void BucketHeaderMatchesIndependentCanonicalIdentityGolden() {
        byte[] golden = Convert.FromHexString("010000000102030405060708090A0B0C0D0E0F1074010000");
        Assert.Equal(24, TagBucketHeaderCodec.PayloadSize);
        Assert.True(TagBucketHeaderCodec.TryRead(golden, TagTestWire.Identity, 0x174, out TagBucketHeader header));
        Assert.Equal(0x174u, header.BucketId);
        byte[] output = Enumerable.Repeat((byte)0xa5, 27).ToArray();
        Assert.True(TagBucketHeaderCodec.TryWrite(TagTestWire.Identity, 0x174, output));
        Assert.Equal(golden, output[..24]);
        Assert.Equal(new byte[] { 0xa5, 0xa5, 0xa5 }, output[24..]);
    }

    [Fact]
    public void BucketHeaderRequiresExactShapeVersionIdentityAndPathBucket() {
        byte[] golden = TagTestWire.Header(0x351);
        Assert.False(TagBucketHeaderCodec.TryRead(golden[..23], TagTestWire.Identity, 0x351, out _));
        Assert.False(TagBucketHeaderCodec.TryRead(TagTestWire.Join(golden, [0]), TagTestWire.Identity, 0x351, out _));
        Assert.False(TagBucketHeaderCodec.TryRead(golden, default, 0x351, out _));
        Assert.False(TagBucketHeaderCodec.TryRead(golden, FormatTestValues.DataIdentity(), 0x351, out _));
        Assert.False(TagBucketHeaderCodec.TryRead(golden, TagTestWire.Identity, 0x350, out _));
        foreach (int fieldOffset in new[] { 0, 4, 20 }) {
            byte[] changed = golden.ToArray();
            changed[fieldOffset] ^= 1;
            Assert.False(TagBucketHeaderCodec.TryRead(changed, TagTestWire.Identity, 0x351, out _));
        }
        byte[] zeroIdentity = golden.ToArray();
        zeroIdentity.AsSpan(4, 16).Clear();
        Assert.False(TagBucketHeaderCodec.TryRead(zeroIdentity, TagTestWire.Identity, 0x351, out _));
        byte[] badBucket = golden.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badBucket.AsSpan(20), 1024);
        Assert.False(TagBucketHeaderCodec.TryRead(badBucket, TagTestWire.Identity, 1024, out _));
        byte[] destination = Enumerable.Repeat((byte)0xa5, 24).ToArray();
        Assert.False(TagBucketHeaderCodec.TryWrite(default, 0, destination));
        Assert.False(TagBucketHeaderCodec.TryWrite(TagTestWire.Identity, 1024, destination));
        Assert.False(TagBucketHeaderCodec.TryWrite(TagTestWire.Identity, 0, destination.AsSpan(0, 23)));
        Assert.All(destination, value => Assert.Equal((byte)0xa5, value));
    }

    [Theory]
    [InlineData(false, TagTestWire.AnonymousAHex, 0x2d2680deu)]
    [InlineData(true, TagTestWire.RelatedAHex, 0xc3834569u)]
    public void MetadataMatchesIndependentAnonymousAndRelatedCrcGoldens(bool related, string hex, uint expectedCrc) {
        byte[] golden = Convert.FromHexString(hex);
        Assert.Equal(expectedCrc, BinaryPrimitives.ReadUInt32LittleEndian(golden.AsSpan(golden.Length - 4)));
        Assert.True(RollingCrc.CheckCodewordForward(golden));
        Assert.True(TagMetadataCodec.TryRead(golden, TagTestWire.Identity, 0x351, out TagMetadata metadata));
        Assert.Equal("A", metadata.Name);
        Assert.Equal(0, metadata.RecordedAtUnixMs);
        HeadId? relatedId = related ? HeadId.Create(TagTestWire.Identity, 1) : null;
        SizedPtr? relatedTicket = related ? SizedPtr.Create(60, 40) : null;
        Assert.Equal(relatedId, metadata.RelatedRefId);
        Assert.Equal(relatedTicket, metadata.RelatedTicket);
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, relatedId, relatedTicket);
        Assert.Equal(golden.Length, plan.EncodedLength);
        Assert.Equal(golden, TagMetadataCodec.Encode(in plan));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void MetadataTimeUsesFullSignedInt64AndDoesNotParticipateInRoute(long time) {
        byte[] wire = TagTestWire.Metadata("A", time);
        Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out TagMetadata metadata));
        Assert.Equal(time, metadata.RecordedAtUnixMs);
        Assert.Equal("A", metadata.Name);
    }

    [Fact]
    public void EveryMetadataFieldAndStoredChecksumIsProtectedByIndependentCrc() {
        byte[] golden = Convert.FromHexString(TagTestWire.RelatedAHex);
        for (int index = 0; index < golden.Length; index++) {
            byte[] changed = golden.ToArray();
            changed[index] ^= 1;
            Assert.False(TagMetadataCodec.TryRead(changed, TagTestWire.Identity, 0x351, out _));
        }
    }

    [Fact]
    public void ReaderAcceptsAlternateNameAndBoundedRedundantFieldPrefixes() {
        SizedPtr ticket = SizedPtr.Create(60, 40);
        foreach (byte[] name in new[] { TagTestWire.Utf16Name("A"), TagTestWire.Utf8Name("A", 5) }) {
            byte[] wire = TagTestWire.Metadata("A", -9, uint.MaxValue, ticket, name,
                TagTestWire.VarUInt(uint.MaxValue, 5), TagTestWire.VarUInt(ticket.Serialize(), 10));
            Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out TagMetadata metadata));
            Assert.Equal("A", metadata.Name);
            Assert.Equal(-9, metadata.RecordedAtUnixMs);
            Assert.Equal(HeadId.Create(TagTestWire.Identity, uint.MaxValue), metadata.RelatedRefId);
            Assert.Equal(ticket, metadata.RelatedTicket);
        }
        byte[] anonymous = TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A", 5), idWire: TagTestWire.VarUInt(0, 5));
        Assert.True(TagMetadataCodec.TryRead(anonymous, TagTestWire.Identity, 0x351, out TagMetadata noRelated));
        Assert.Null(noRelated.RelatedRefId);
        Assert.Null(noRelated.RelatedTicket);
    }

    [Fact]
    public void ReaderAcceptsExact3104BytesUsingActualUtf8AndRedundantWidths() {
        string name = new('\u0800', 1024);
        SizedPtr maximumTicket = SizedPtr.Deserialize(ulong.MaxValue);
        byte[] nameWire = TagTestWire.Utf8Name(name, 5);
        byte[] wire = TagTestWire.Metadata(name, long.MaxValue, uint.MaxValue, maximumTicket, nameWire,
            TagTestWire.VarUInt(uint.MaxValue, 5), TagTestWire.VarUInt(ulong.MaxValue, 10));
        Assert.Equal(3077, nameWire.Length);
        Assert.Equal(3104, wire.Length);
        Assert.Equal(3072, TagMetadataCodec.MaxNamePayloadByteCount);
        Assert.Equal(3077, TagMetadataCodec.MaxNameCodewordLength);
        Assert.Equal(15, TagMetadataCodec.MinEncodedSize);
        Assert.Equal(3104, TagMetadataCodec.MaxEncodedSize);
        uint bucket = TagNameCodec.GetBucketId(name);
        Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, bucket, out TagMetadata metadata));
        Assert.Equal(name, metadata.Name);
        Assert.Equal(maximumTicket, metadata.RelatedTicket);
        TagMetadataEncodingPlan writerPlan = TagMetadataCodec.Prepare(name, long.MaxValue, TagTestWire.Identity,
            HeadId.Create(TagTestWire.Identity, uint.MaxValue), maximumTicket);
        Assert.True(writerPlan.EncodedLength < wire.Length); // Writer chooses shorter UTF-16; reader must accept the larger actual codeword.
    }

    [Fact]
    public void RelatedTicketAcceptsPublicValuesWithoutEntryOrSourceQualification() {
        foreach (SizedPtr ticket in new[] { SizedPtr.Create(0, 4), SizedPtr.Create(4, 4), SizedPtr.Create(SizedPtr.MaxOffset, SizedPtr.MaxLength) }) {
            byte[] wire = TagTestWire.Metadata("A", id: 1, ticket: ticket);
            Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out TagMetadata metadata));
            Assert.Equal(ticket, metadata.RelatedTicket);
        }
        byte[] zeroLength = TagTestWire.Metadata("A", id: 1, ticket: SizedPtr.Create(60, 0));
        byte[] defaultTicket = TagTestWire.Metadata("A", id: 1, ticket: default(SizedPtr));
        Assert.False(TagMetadataCodec.TryRead(zeroLength, TagTestWire.Identity, 0x351, out _));
        Assert.False(TagMetadataCodec.TryRead(defaultTicket, TagTestWire.Identity, 0x351, out _));
    }

    [Fact]
    public void MetadataRejectsCheckedCrcButInvalidFieldsAndNonExactConsumption() {
        byte[] golden = Convert.FromHexString(TagTestWire.AnonymousAHex);
        byte[][] invalid = [
            TagTestWire.Seal(TagTestWire.Join(golden[..^4], [0])),
            TagTestWire.Metadata("", nameWire: [0]),
            TagTestWire.Metadata("A", nameWire: [3, 0xff]),
            TagTestWire.Metadata("A", nameWire: [5, 0xc0, 0x80]), // Overlong UTF-8.
            TagTestWire.Metadata("A", nameWire: [0x83, 0x80]), // 15B total; passes size guard before the incomplete string prefix fails.
            TagTestWire.Metadata("A", idWire: [0x80, 0x80, 0x80, 0x80, 0x80, 0]),
            TagTestWire.Metadata("A", idWire: [0xff, 0xff, 0xff, 0xff, 0x10]),
            TagTestWire.Metadata("A", id: 1, ticket: SizedPtr.Create(60, 40), ticketWire: [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 2]),
            TagTestWire.Metadata("A", id: 1, ticket: SizedPtr.Create(60, 40), ticketWire: TagTestWire.VarUInt(574, 11)),
            TagTestWire.Metadata(new string('a', 3072), nameWire: TagTestWire.Utf8Name(new string('a', 3072))),
            TagTestWire.Metadata("A", nameWire: TagTestWire.VarUInt(3073 * 2 + 1))
        ];
        foreach (byte[] wire in invalid) {
            Assert.True(RollingCrc.CheckCodewordForward(wire));
            Assert.False(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out _));
        }
        Assert.False(TagMetadataCodec.TryRead(golden, TagTestWire.Identity, 0x350, out _));
        Assert.False(TagMetadataCodec.TryRead(golden, default, 0x351, out _));
        Assert.False(TagMetadataCodec.TryRead(new byte[14], TagTestWire.Identity, 0x351, out _));
        Assert.False(TagMetadataCodec.TryRead(new byte[3105], TagTestWire.Identity, 0x351, out _));
    }

    [Fact]
    public void MetadataWriterRequiresRelatedFieldsTogetherAndInCheckedIdentity() {
        HeadId local = HeadId.Create(TagTestWire.Identity, 1);
        SizedPtr ticket = SizedPtr.Create(60, 40);
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, default));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, local));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, null, ticket));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, default(HeadId), ticket));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity,
            HeadId.Create(FormatTestValues.DataIdentity(), 1), ticket));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, local, default(SizedPtr)));
    }
}
