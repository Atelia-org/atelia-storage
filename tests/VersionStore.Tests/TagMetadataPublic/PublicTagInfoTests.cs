using Atelia.Data;
using Atelia.VersionStore.Tests.TagPublic;
using Atelia.VersionStore.Tests.Tags;
using Xunit;

namespace Atelia.VersionStore.Tests.TagMetadataPublic;

public sealed class PublicTagInfoTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentlyCheckedMetadataSurvivesBadRootCrcAndKeepsNameOccupied(bool readOnly) {
        using var fixture = new TagPublicFixture();
        var tickets = fixture.WriteMirror("tag-48", new("tag-48", new byte[128], TagTestWire.Metadata("tag-48", time: long.MinValue)),
            new("tag-65", [2], TagTestWire.Metadata("tag-65", time: long.MaxValue)));
        fixture.FlipRoot("tag-48", tickets[0]);
        var before = fixture.Snapshot();
        TagInfo retained;
        using (var store = fixture.Open(readOnly)) {
            retained = store.ReadTagInfo("tag-48").Unwrap();
            Assert.Equal("tag-48", retained.Name);
            Assert.Equal(long.MinValue, retained.RecordedAtUnixMs);
            Assert.Null(retained.RelatedRefId);
            Assert.Null(retained.RelatedTicket);
            Assert.Equal(long.MaxValue, store.ReadTagInfo("tag-65").Unwrap().RecordedAtUnixMs);
            Assert.True(store.ResolveTag("tag-48").IsFailure);
            Assert.Equal(new byte[] { 2 }, store.ResolveTag("tag-65").Unwrap().Bytes.ToArray());
            if (!readOnly) {
                Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("tag-48", [1], out var outcome).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(long.MinValue, retained.RecordedAtUnixMs);
        fixture.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(VersionStore.MaxRootPayloadLength + 1)]
    public void RootLengthIsNotMetadataQualificationOrNameAvailability(int rootLength) {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A", new TagPublicRecord("A", new byte[rootLength]));
        var before = fixture.Snapshot();
        using (var store = fixture.Open()) {
            Assert.Equal("A", store.ReadTagInfo("A").Unwrap().Name);
            Assert.Equal("VersionStore.InvalidTagRoot", store.ResolveTag("A").Error!.ErrorCode);
            Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("A", [1], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal("A", cold.ReadTagInfo("A").Unwrap().Name);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NamedMetadataIsolatesUnknownRecordBeforeOrAfterUniqueTargetAndNeverGuessesMissing(bool targetFirst, bool readOnly) {
        using var fixture = new TagPublicFixture();
        byte[] malformed = TagPublicFixture.Metadata("tag-65");
        malformed[^1] ^= 1;
        TagPublicRecord target = new("tag-48", [1], TagPublicFixture.Metadata("tag-48", -42));
        TagPublicRecord unknown = new("tag-65", [2], malformed);
        fixture.WriteMirror("tag-48", targetFirst ? [target, unknown] : [unknown, target]);
        string missing = TagPublicFixture.Collision("tag-48", "tag-65");
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(-42, store.ReadTagInfo("tag-48").Unwrap().RecordedAtUnixMs);
            Assert.Equal("VersionStore.TagLookupUncertain", store.ReadTagInfo(missing).Error!.ErrorCode);
            Assert.Equal("VersionStore.TagLookupUncertain", store.ReadTagInfo("tag-65").Error!.ErrorCode);
            Assert.Empty(store.ListRefs().Unwrap());
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void DuplicateDecodedFullNameRejectsInfoButOtherNameDuplicatesDoNotBlockUniqueTarget() {
        using var fixture = new TagPublicFixture();
        string target = TagPublicFixture.Collision("A");
        fixture.WriteMirror("A", new("A", [1]),
            new("A", [1], TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A"))),
            new("A", [2], TagTestWire.Metadata("A", nameWire: TagTestWire.Utf8Name("A", headerWidth: 2))),
            new(target, [3], TagPublicFixture.Metadata(target, 17)));
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.DuplicateTagName", store.ReadTagInfo("A").Error!.ErrorCode);
            Assert.Equal(target, store.ReadTagInfo(target).Unwrap().Name);
            Assert.Equal(17, store.ReadTagInfo(target).Unwrap().RecordedAtUnixMs);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void DeclaredRelatedCoordinatesAreValuesWithNoSourceExistenceOrSnapshotSizeRequirement() {
        using var fixture = new TagPublicFixture();
        SizedPtr declaredTicket = SizedPtr.Create(SizedPtr.MaxOffset, 4);
        fixture.WriteMirror("A", new TagPublicRecord("A", [],
            TagTestWire.Metadata("A", time: -17, id: uint.MaxValue, ticket: declaredTicket,
                ticketWire: TagTestWire.VarUInt(declaredTicket.Serialize(), width: 10))));
        TagInfo retained;
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            retained = store.ReadTagInfo("A").Unwrap();
            Assert.Equal(uint.MaxValue, retained.RelatedRefId!.Value.LocalId);
            Assert.Equal(declaredTicket, retained.RelatedTicket);
            Assert.Equal(-17, retained.RecordedAtUnixMs);
            Assert.Empty(store.ListRefs().Unwrap());
            Assert.Equal("VersionStore.InvalidTagRoot", store.ResolveTag("A").Error!.ErrorCode);
        }
        Assert.Equal(declaredTicket, retained.RelatedTicket);
        fixture.AssertUnchanged(before);
    }

    public static IEnumerable<object[]> OriginalNames() {
        // Only ASCII labels cross xUnit's serialization boundary. Names are built inside the test
        // so lone surrogate values remain original code units and receive distinct case IDs.
        foreach (string label in new[] { "whitespace", "nul", "separators", "bom", "lone-high", "lone-low", "paired", "maximum" }) {
            yield return [label];
        }
    }

    [Theory]
    [MemberData(nameof(OriginalNames))]
    public void PublicInfoRetainsOriginalUtf16NameAndRelatedOverloadSharesNameContract(string caseName) {
        string name = caseName switch {
            "whitespace" => " ", "nul" => "\0", "separators" => "/\\", "bom" => "\ufeff",
            "lone-high" => "\ud800", "lone-low" => "\udc00", "paired" => "\ud83d\ude00",
            "maximum" => new string('\ud800', VersionStore.MaxTagNameCodeUnits),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };
        using var fixture = new TagPublicFixture();
        using (var store = fixture.Open()) {
            RefRevision source = store.CreateRef(7, [1], out _).Unwrap();
            Assert.True(store.CreateTag(name, [2], source, out _).Unwrap());
            Assert.Equal(name.ToCharArray(), store.ReadTagInfo(name).Unwrap().Name.ToCharArray());
        }
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(name.ToCharArray(), cold.ReadTagInfo(name).Unwrap().Name.ToCharArray());
        Assert.Equal(TagScanEnd.Complete, cold.ScanTags(info => {
            Assert.Equal(name.ToCharArray(), info.Name.ToCharArray());
            return true;
        }).Unwrap());
    }

    [Fact]
    public void MissingMetadataQueryAndEmptyTagDirectoryDoNotCreateAnything() {
        using var fixture = new TagPublicFixture();
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", store.ReadTagInfo("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(fixture.TagsPath));
        Directory.CreateDirectory(fixture.TagsPath);
        before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", store.ReadTagInfo("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
    }
}
