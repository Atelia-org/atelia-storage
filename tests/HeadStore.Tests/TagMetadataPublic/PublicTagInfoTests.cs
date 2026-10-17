using Atelia.Data;
using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

public sealed class PublicTagInfoTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentlyCheckedMetadataSurvivesBadMemberCrcAndKeepsNameOccupied(bool readOnly) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        var tickets = fixture.WriteMirror("tag-48", new("tag-48", TagTestWire.Members(1), TagTestWire.Metadata("tag-48", time: long.MinValue)),
            new("tag-65", TagTestWire.Members(2), TagTestWire.Metadata("tag-65", time: long.MaxValue)));
        fixture.FlipRoot("tag-48", tickets[0]);
        var before = fixture.CaptureFiles();
        HeadSetTagInfo retained;
        using (var store = fixture.Open(readOnly)) {
            retained = store.ReadTagInfo("tag-48").Unwrap();
            Assert.Equal("tag-48", retained.Name);
            Assert.Equal(long.MinValue, retained.RecordedAtUnixMs);
            Assert.Equal(1, retained.EntryCount);
            Assert.Equal(long.MaxValue, store.ReadTagInfo("tag-65").Unwrap().RecordedAtUnixMs);
            Assert.StartsWith("Rbf.", store.ReadTag("tag-48").Error!.ErrorCode);
            Assert.Equal(TagTestWire.Members(2), store.ReadTag("tag-65").Unwrap().MemberBytes(fixture.Identity));
            if (!readOnly) {
                Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("tag-48", [head], out var outcome).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(long.MinValue, retained.RecordedAtUnixMs);
        fixture.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HeadStore.MaxHeadEntryPayloadLength + 1)]
    public void EntriesLengthIsNotMetadataQualificationOrNameAvailability(int rootLength) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        fixture.WriteMirror("A", new TagPublicRecord("A", new byte[rootLength]));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            Assert.Equal("A", store.ReadTagInfo("A").Unwrap().Name);
            Assert.Equal("HeadStore.InvalidTagEntries", store.ReadTag("A").Error!.ErrorCode);
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("A", [head], out var outcome).Error!.ErrorCode);
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
        HeadId head = fixture.SeedHead();
        byte[] malformed = TagPublicFixture.Metadata("tag-65");
        malformed[^1] ^= 1;
        TagPublicRecord target = new("tag-48", TagTestWire.Members(1), TagPublicFixture.Metadata("tag-48", -42));
        TagPublicRecord unknown = new("tag-65", TagTestWire.Members(2), malformed);
        fixture.WriteMirror("tag-48", targetFirst ? [target, unknown] : [unknown, target]);
        string missing = TagPublicFixture.Collision("tag-48", "tag-65");
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(-42, store.ReadTagInfo("tag-48").Unwrap().RecordedAtUnixMs);
            Assert.Equal("HeadStore.TagLookupUncertain", store.ReadTagInfo(missing).Error!.ErrorCode);
            Assert.Equal("HeadStore.TagLookupUncertain", store.ReadTagInfo("tag-65").Error!.ErrorCode);
            Assert.Equal(new[] { head }, store.ListHeads().Unwrap());
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void DuplicateDecodedFullNameRejectsInfoButOtherNameDuplicatesDoNotBlockUniqueTarget() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        string target = TagPublicFixture.Collision("A");
        fixture.WriteMirror("A", new("A", TagTestWire.Members(1)),
            new("A", TagTestWire.Members(1), TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A"))),
            new("A", TagTestWire.Members(2), TagTestWire.Metadata("A", nameWire: TagTestWire.Utf8Name("A", headerWidth: 2))),
            new(target, TagTestWire.Members(3), TagPublicFixture.Metadata(target, 17)));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.DuplicateTagName", store.ReadTagInfo("A").Error!.ErrorCode);
            Assert.Equal(target, store.ReadTagInfo(target).Unwrap().Name);
            Assert.Equal(17, store.ReadTagInfo(target).Unwrap().RecordedAtUnixMs);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void DeclaredEntryCountIsMetadataWithoutQualificationOfTheActualTable() {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A", new TagPublicRecord("A", [], TagTestWire.Metadata("A", time: -17, count: 2)));
        HeadSetTagInfo retained;
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            retained = store.ReadTagInfo("A").Unwrap();
            Assert.Equal(2, retained.EntryCount);
            Assert.Equal(-17, retained.RecordedAtUnixMs);
            Assert.Empty(store.ListHeads().Unwrap());
            Assert.Equal("HeadStore.InvalidTagEntries", store.ReadTag("A").Error!.ErrorCode);
        }
        Assert.Equal(2, retained.EntryCount);
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
    public void PublicInfoRetainsOriginalUtf16NameAndCreationSharesNameContract(string caseName) {
        string name = caseName switch {
            "whitespace" => " ", "nul" => "\0", "separators" => "/\\", "bom" => "\ufeff",
            "lone-high" => "\ud800", "lone-low" => "\udc00", "paired" => "\ud83d\ude00",
            "maximum" => new string('\ud800', HeadStore.MaxTagNameCodeUnits),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        using (var store = fixture.Open()) {
            HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
            Assert.True(store.CreateTag(name, [source.HeadId], out _).IsSuccess);
            Assert.Equal(name.ToCharArray(), store.ReadTagInfo(name).Unwrap().Name.ToCharArray());
        }
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(name.ToCharArray(), cold.ReadTagInfo(name).Unwrap().Name.ToCharArray());
        Assert.Equal(HeadSetTagScanEnd.Complete, cold.ScanTags(info => {
            Assert.Equal(name.ToCharArray(), info.Name.ToCharArray());
            return true;
        }).Unwrap());
    }

    [Fact]
    public void MissingMetadataQueryAndEmptyTagDirectoryDoNotCreateAnything() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", store.ReadTagInfo("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(fixture.TagsPath));
        Directory.CreateDirectory(fixture.TagsPath);
        before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", store.ReadTagInfo("A").Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
    }
}
