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
        byte[] golden = Convert.FromHexString("040000000102030405060708090A0B0C0D0E0F1074010000");
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

    [Fact]
    public void MetadataMatchesIndependentFixedWidthCountAndNameWire() {
        byte[] golden = Convert.FromHexString("000000000000000002000000034114A1102A");
        Assert.Equal(Convert.FromHexString("0000000000000000020000000341"), golden[..^4]);
        Assert.True(RollingCrc.CheckCodewordForward(golden));
        Assert.True(TagMetadataCodec.TryRead(golden, TagTestWire.Identity, 0x351, out TagMetadata metadata));
        Assert.Equal("A", metadata.Name);
        Assert.Equal(0, metadata.RecordedAtUnixMs);
        Assert.Equal(2, metadata.EntryCount);
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, 2);
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
        byte[] golden = Convert.FromHexString("000000000000000002000000034114A1102A");
        for (int index = 0; index < golden.Length; index++) {
            byte[] changed = golden.ToArray();
            changed[index] ^= 1;
            Assert.False(TagMetadataCodec.TryRead(changed, TagTestWire.Identity, 0x351, out _));
        }
    }

    [Fact]
    public void ReaderAcceptsAlternateNameAndBoundedRedundantStringPrefixes() {
        foreach (byte[] name in new[] { TagTestWire.Utf16Name("A"), TagTestWire.Utf8Name("A", 5) }) {
            byte[] wire = TagTestWire.Metadata("A", -9, 2, name);
            Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out TagMetadata metadata));
            Assert.Equal("A", metadata.Name);
            Assert.Equal(-9, metadata.RecordedAtUnixMs);
            Assert.Equal(2, metadata.EntryCount);
        }
    }

    [Fact]
    public void ReaderAcceptsExact3093BytesUsingActualUtf8AndRedundantStringWidth() {
        string name = new('\u0800', 1024);
        byte[] nameWire = TagTestWire.Utf8Name(name, 5);
        byte[] wire = TagTestWire.Metadata(name, long.MaxValue, 1, nameWire);
        Assert.Equal(3077, nameWire.Length);
        Assert.Equal(3093, wire.Length);
        Assert.Equal(3072, TagMetadataCodec.MaxNamePayloadByteCount);
        Assert.Equal(3077, TagMetadataCodec.MaxNameCodewordLength);
        Assert.Equal(18, TagMetadataCodec.MinEncodedSize);
        Assert.Equal(3093, TagMetadataCodec.MaxEncodedSize);
        uint bucket = TagNameCodec.GetBucketId(name);
        Assert.True(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, bucket, out TagMetadata metadata));
        Assert.Equal(name, metadata.Name);
        Assert.Equal(1, metadata.EntryCount);
        TagMetadataEncodingPlan plan = TagMetadataCodec.Prepare(name, long.MaxValue, TagTestWire.Identity, 1);
        Assert.True(plan.EncodedLength < wire.Length);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void InvalidCountRejectsWholeMetadataEvenWithHealthyNameAndCrc(uint count) {
        byte[] wire = TagTestWire.Metadata("A", count: count);
        Assert.True(RollingCrc.CheckCodewordForward(wire));
        Assert.False(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out _));
    }

    [Fact]
    public void MetadataRejectsCheckedCrcButInvalidFieldsAndNonExactConsumption() {
        byte[] golden = TagTestWire.Metadata("A");
        byte[][] invalid = [
            TagTestWire.Seal(TagTestWire.Join(golden[..^4], [0])),
            TagTestWire.Metadata("", nameWire: [0]),
            TagTestWire.Metadata("A", nameWire: [3, 0xff]),
            TagTestWire.Metadata("A", nameWire: [5, 0xc0, 0x80]), // Overlong UTF-8.
            TagTestWire.Metadata("A", nameWire: [0x83, 0x80]), // 18B total; passes size guard before the incomplete string prefix fails.
            TagTestWire.Metadata(new string('a', 3072), nameWire: TagTestWire.Utf8Name(new string('a', 3072))),
            TagTestWire.Metadata("A", nameWire: TagTestWire.VarUInt(3073 * 2 + 1))
        ];
        foreach (byte[] wire in invalid) {
            Assert.True(RollingCrc.CheckCodewordForward(wire));
            Assert.False(TagMetadataCodec.TryRead(wire, TagTestWire.Identity, 0x351, out _));
        }
        Assert.False(TagMetadataCodec.TryRead(golden, TagTestWire.Identity, 0x350, out _));
        Assert.False(TagMetadataCodec.TryRead(golden, default, 0x351, out _));
        Assert.False(TagMetadataCodec.TryRead(new byte[17], TagTestWire.Identity, 0x351, out _));
        Assert.False(TagMetadataCodec.TryRead(new byte[3094], TagTestWire.Identity, 0x351, out _));
    }

    [Fact]
    public void MetadataWriterRequiresCheckedIdentityAndPositiveNaturalCapacityCount() {
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, default, 1));
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, 0));
        TagMetadataEncodingPlan oversized = TagMetadataCodec.Prepare("A", 0, TagTestWire.Identity, int.MaxValue);
        Assert.True(TagRecordCodec.MeasurePayloadLength(int.MaxValue, oversized.EncodedLength).IsFailure);
        Assert.ThrowsAny<ArgumentException>(() => TagMetadataCodec.Encode(in oversized));
    }
}
