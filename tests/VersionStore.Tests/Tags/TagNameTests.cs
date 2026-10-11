using System.Buffers;
using Atelia.Binary;
using Atelia.VersionStore.Internal.Tags;
using Xunit;

namespace Atelia.VersionStore.Tests.Tags;

public sealed class TagNameTests {
    [Theory]
    [InlineData("A", 0x0908a207b5a15704UL, 0x351u)]
    [InlineData("Release", 0xc5566333f1504164UL, 0x174u)]
    [InlineData("release", 0xbc7f822919cbbb44UL, 0x1aau)]
    public void RoutingMatchesIndependentUtf16FnvGoldens(string name, ulong expectedHash, uint expectedBucket) {
        Assert.Equal(expectedHash, TagNameCodec.GetHash(name));
        Assert.Equal(expectedBucket, TagNameCodec.GetBucketId(name));
        Assert.Equal($"{expectedBucket:x8}.rbf", TagNameCodec.GetBucketFileName(expectedBucket));
        Assert.Equal(1024, TagNameCodec.BucketCount);
    }

    [Fact]
    public void BucketCollisionDoesNotImplyNameEquality() {
        Assert.Equal(0x106u, TagNameCodec.GetBucketId("tag-48"));
        Assert.Equal(0x106u, TagNameCodec.GetBucketId("tag-65"));
        Assert.False(StringComparer.Ordinal.Equals("tag-48", "tag-65"));
        Assert.NotEqual(TagNameCodec.GetHash("tag-48"), TagNameCodec.GetHash("tag-65"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\0\n\t/\\")]
    [InlineData("\uFEFF")]
    [InlineData("e\u0301")]
    [InlineData("\u00E9")]
    public void NamePlanPreservesAcceptedCodeUnitsWithoutNormalization(string name) {
        AssertLosslessPlan(name);
    }

    [Fact]
    public void UnpairedSurrogatesAreConstructedAsOriginalRuntimeCodeUnits() {
        // Attribute/xUnit serialization may replace lone surrogates; keep this input out of InlineData.
        string high = new((char)0xd800, 1);
        string low = new((char)0xdc00, 1);
        Assert.Equal((char)0xd800, high[0]);
        Assert.Equal((char)0xdc00, low[0]);
        Assert.False(StringComparer.Ordinal.Equals(high, low));
        Assert.Equal(0x0831b007b4ea00e5UL, TagNameCodec.GetHash(high));
        Assert.Equal(0x265u, TagNameCodec.GetBucketId(high));
        AssertLosslessPlan(high);
        AssertLosslessPlan(low);
        AssertLosslessPlan(new string(new[] { (char)0xd800, 'x', (char)0xdc00 }));
    }

    private static void AssertLosslessPlan(string name) {
        Assert.True(TagNameCodec.IsValidName(name));
        StringEncodingPlan plan = TagNameCodec.PrepareName(name);
        var output = new ArrayBufferWriter<byte>();
        new BareValueWriter(output).WriteString(in plan);
        var reader = new BareValueReader(output.WrittenSpan);
        Assert.Equal(name, reader.ReadString(3072));
        Assert.Equal(output.WrittenCount, reader.ConsumedCount);
    }

    [Fact]
    public void NameBoundCountsUtf16UnitsIncludingSurrogates() {
        Assert.Equal(1024, VersionStore.MaxTagNameCodeUnits);
        Assert.False(TagNameCodec.IsValidName(null));
        Assert.False(TagNameCodec.IsValidName(""));
        Assert.True(TagNameCodec.IsValidName(new string('\uD800', 1024)));
        Assert.True(TagNameCodec.IsValidName(string.Concat(Enumerable.Repeat("\uD83D\uDE00", 512))));
        Assert.False(TagNameCodec.IsValidName(new string('a', 1025)));
        Assert.False(TagNameCodec.IsValidName(string.Concat(Enumerable.Repeat("\uD83D\uDE00", 513))));
        Assert.ThrowsAny<ArgumentException>(() => TagNameCodec.PrepareName(null!));
        Assert.ThrowsAny<ArgumentException>(() => TagNameCodec.PrepareName(""));
        Assert.ThrowsAny<ArgumentException>(() => TagNameCodec.PrepareName(new string('a', 1025)));
    }

    [Fact]
    public void AnotherLegalStringRepresentationRoutesByDecodedName() {
        foreach (byte[] wire in new[] { new byte[] { 3, 65 }, new byte[] { 2, 65, 0 }, new byte[] { 0x83, 0, 65 } }) {
            var reader = new BareValueReader(wire);
            string decoded = reader.ReadString(3072);
            Assert.Equal("A", decoded);
            Assert.Equal(wire.Length, reader.ConsumedCount);
            Assert.Equal(0x351u, TagNameCodec.GetBucketId(decoded));
        }
    }
}
