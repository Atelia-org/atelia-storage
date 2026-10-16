using Atelia.HeadStore.Tests.Tags;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.HeadStore.Tests.TagPublic;

public sealed class PublicTagReadScopeTests {
    [Fact]
    public void CanonicalOtherBucketContentsAreOutsideNamedReadAndCreateScope() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        fixture.WriteMirror("A", new TagPublicRecord("A", TagTestWire.Members(0xff)));
        File.WriteAllBytes(fixture.BucketPath("Release"), []); // Canonical path, invalid RBF content.
        byte[] untouched = File.ReadAllBytes(fixture.BucketPath("Release"));
        using (var store = fixture.Open()) {
            Assert.Equal(TagTestWire.Members(0xff), store.ReadTag("A").Unwrap().MemberBytes());
            string collision = TagPublicFixture.Collision("A");
            Assert.True(store.CreateTag(collision, [head], out var outcome).IsSuccess);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(store.ReadHead(head).Unwrap().Id, Assert.Single(store.ReadTag(collision).Unwrap().Entries).Value);
        }
        Assert.Equal(untouched, File.ReadAllBytes(fixture.BucketPath("Release")));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(TagTestWire.Members(0xff), cold.ReadTag("A").Unwrap().MemberBytes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrustedOtherNameWithBadEntriesCannotBlockHealthyTargetAndBadEntriesStillOccupiesItsName(bool readOnly) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        var tickets = fixture.WriteMirror("tag-48",
            new("tag-48", TagTestWire.Members(255)), new("tag-65", TagTestWire.Members(0x81)));
        fixture.FlipRoot("tag-48", tickets[0]);
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(TagTestWire.Members(0x81), store.ReadTag("tag-65").Unwrap().MemberBytes());
            var damaged = store.ReadTag("tag-48");
            Assert.True(damaged.IsFailure);
            Assert.StartsWith("Rbf.", damaged.Error!.ErrorCode);
            if (!readOnly) {
                Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("tag-48", [head], out var outcome).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            Assert.Equal(TagTestWire.Members(0x81), store.ReadTag("tag-65").Unwrap().MemberBytes());
        }
        fixture.AssertUnchanged(before);
        using var healthyWriter = fixture.Open();
        string fresh = TagPublicFixture.Collision("tag-48", "tag-65");
        Assert.True(healthyWriter.CreateTag(fresh, [head], out var confirmed).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        fixture.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HeadStore.MaxHeadEntryPayloadLength + 1)]
    public void InvalidEntriesLengthKeepsTrustedNameOccupiedWhileOtherNameAndWriterRemainUsable(int length) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        fixture.WriteMirror("tag-48", new("tag-48", new byte[length]), new("tag-65", TagTestWire.Members(0x81)));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            Assert.Equal("HeadStore.InvalidTagEntries", store.ReadTag("tag-48").Error!.ErrorCode);
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("tag-48", [head], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(TagTestWire.Members(0x81), store.ReadTag("tag-65").Unwrap().MemberBytes());
        }
        fixture.AssertUnchanged(before);
        using var next = fixture.Open();
        Assert.True(next.CreateTag(TagPublicFixture.Collision("tag-48", "tag-65"), [head], out var confirmed).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UniqueTargetSurvivesUnknownNameButMissingAndCreationRemainUncertain(bool targetFirst, bool readOnly) {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        byte[] badMetadata = TagPublicFixture.Metadata("tag-65");
        badMetadata[0] ^= 1;
        TagPublicRecord target = new("tag-48", TagTestWire.Members(0xff));
        TagPublicRecord unknown = new("tag-65", TagTestWire.Members(2), badMetadata);
        fixture.WriteMirror("tag-48", targetFirst ? [target, unknown] : [unknown, target]);
        string missing = TagPublicFixture.Collision("tag-48", "tag-65");
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(TagTestWire.Members(0xff), store.ReadTag("tag-48").Unwrap().MemberBytes());
            Assert.Equal("HeadStore.TagLookupUncertain", store.ReadTag(missing).Error!.ErrorCode);
            if (!readOnly) {
                Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("tag-48", [head], out var occupied).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, occupied);
                Assert.Equal("HeadStore.TagLookupUncertain", store.CreateTag(missing, [head], out var uncertain).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, uncertain);
            }
            Assert.Equal(TagTestWire.Members(0xff), store.ReadTag("tag-48").Unwrap().MemberBytes());
            Assert.Equal(new[] { head }, store.ListHeads().Unwrap());
        }
        fixture.AssertUnchanged(before);
        fixture.AssertBucketReleased("tag-48");
    }

    [Fact]
    public void DuplicateFullNameInDifferentLegalStringEncodingsRejectsEvenEqualTablesAndDoesNotBlockOtherNames() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        byte[] utf16Metadata = TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A"));
        string other = TagPublicFixture.Collision("A");
        fixture.WriteMirror("A", new("A", TagTestWire.Members(1)), new("A", TagTestWire.Members(1), utf16Metadata), new(other, TagTestWire.Members(2)));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            Assert.Equal("HeadStore.DuplicateTagName", store.ReadTag("A").Error!.ErrorCode);
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("A", [head], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(TagTestWire.Members(2), store.ReadTag(other).Unwrap().MemberBytes());
        }
        fixture.AssertUnchanged(before);
        using var next = fixture.Open();
        string fresh = TagPublicFixture.Collision("A", other);
        Assert.True(next.CreateTag(fresh, [head], out var confirmed).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Fact]
    public void CompletedRbfTailStillRequiresIndependentTagMetadataQualification() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        byte[] badMetadata = TagPublicFixture.Metadata("tag-48");
        badMetadata[^1] ^= 1;
        fixture.WriteMirror("tag-48", new TagPublicRecord("tag-48", TagTestWire.Members(1), badMetadata));
        string path = fixture.BucketPath("tag-48");
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]);
        using (var store = fixture.Open()) {
            Assert.Equal("HeadStore.TagLookupUncertain", store.ReadTag("tag-48").Error!.ErrorCode);
            Assert.Equal("HeadStore.TagLookupUncertain", store.CreateTag("tag-48", [head], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal("HeadStore.TagLookupUncertain", cold.ReadTag("tag-48").Error!.ErrorCode);
    }
}
