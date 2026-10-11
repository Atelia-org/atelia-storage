using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.VersionStore.Tests.TagPublic;

public sealed class PublicTagReadScopeTests {
    [Fact]
    public void CanonicalOtherBucketContentsAreOutsideNamedReadAndCreateScope() {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A", new TagPublicRecord("A", [0xff]));
        File.WriteAllBytes(fixture.BucketPath("Release"), []); // Canonical path, invalid RBF content.
        byte[] untouched = File.ReadAllBytes(fixture.BucketPath("Release"));
        using (var store = fixture.Open()) {
            Assert.Equal(new byte[] { 0xff }, store.ResolveTag("A").Unwrap().Bytes.ToArray());
            string collision = TagPublicFixture.Collision("A");
            Assert.True(store.CreateTag(collision, [0x81], out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new byte[] { 0x81 }, store.ResolveTag(collision).Unwrap().Bytes.ToArray());
        }
        Assert.Equal(untouched, File.ReadAllBytes(fixture.BucketPath("Release")));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal(new byte[] { 0xff }, cold.ResolveTag("A").Unwrap().Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrustedOtherNameWithBadRootCannotBlockHealthyTargetAndBadRootStillOccupiesItsName(bool readOnly) {
        using var fixture = new TagPublicFixture();
        var tickets = fixture.WriteMirror("tag-48",
            new("tag-48", Enumerable.Repeat((byte)0xff, 128).ToArray()), new("tag-65", [0x81]));
        fixture.FlipRoot("tag-48", tickets[0]);
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(new byte[] { 0x81 }, store.ResolveTag("tag-65").Unwrap().Bytes.ToArray());
            var damaged = store.ResolveTag("tag-48");
            Assert.True(damaged.IsFailure);
            Assert.StartsWith("Rbf.", damaged.Error!.ErrorCode);
            if (!readOnly) {
                Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("tag-48", [0xff], out var outcome).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            Assert.Equal(new byte[] { 0x81 }, store.ResolveTag("tag-65").Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
        using var healthyWriter = fixture.Open();
        string fresh = TagPublicFixture.Collision("tag-48", "tag-65");
        Assert.True(healthyWriter.CreateTag(fresh, [0x42], out var confirmed).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        fixture.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(VersionStore.MaxRootPayloadLength + 1)]
    public void InvalidRootLengthKeepsTrustedNameOccupiedWhileOtherNameAndWriterRemainUsable(int length) {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("tag-48", new("tag-48", new byte[length]), new("tag-65", [0x81]));
        var before = fixture.Snapshot();
        using (var store = fixture.Open()) {
            Assert.Equal("VersionStore.InvalidTagRoot", store.ResolveTag("tag-48").Error!.ErrorCode);
            Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("tag-48", [1], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(new byte[] { 0x81 }, store.ResolveTag("tag-65").Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
        using var next = fixture.Open();
        Assert.True(next.CreateTag(TagPublicFixture.Collision("tag-48", "tag-65"), [2], out var confirmed).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UniqueTargetSurvivesUnknownNameButMissingAndCreationRemainUncertain(bool targetFirst, bool readOnly) {
        using var fixture = new TagPublicFixture();
        byte[] badMetadata = TagPublicFixture.Metadata("tag-65");
        badMetadata[0] ^= 1;
        TagPublicRecord target = new("tag-48", [0xff, 0, 0x81]);
        TagPublicRecord unknown = new("tag-65", [2], badMetadata);
        fixture.WriteMirror("tag-48", targetFirst ? [target, unknown] : [unknown, target]);
        string missing = TagPublicFixture.Collision("tag-48", "tag-65");
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly)) {
            Assert.Equal(new byte[] { 0xff, 0, 0x81 }, store.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
            Assert.Equal("VersionStore.TagLookupUncertain", store.ResolveTag(missing).Error!.ErrorCode);
            if (!readOnly) {
                Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("tag-48", [1], out var occupied).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, occupied);
                Assert.Equal("VersionStore.TagLookupUncertain", store.CreateTag(missing, [1], out var uncertain).Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, uncertain);
            }
            Assert.Equal(new byte[] { 0xff, 0, 0x81 }, store.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
            Assert.Empty(store.ListRefs().Unwrap());
        }
        fixture.AssertUnchanged(before);
        fixture.AssertBucketReleased("tag-48");
    }

    [Fact]
    public void DuplicateFullNameInDifferentLegalStringEncodingsRejectsEvenEqualRootsAndDoesNotBlockOtherNames() {
        using var fixture = new TagPublicFixture();
        byte[] utf16Metadata = new byte[16]; // time0/id0/ordinary UTF16LE A/MetaCRC.
        utf16Metadata[9] = 2;
        utf16Metadata[10] = 0x41;
        RollingCrc.SealCodewordForward(utf16Metadata);
        string other = TagPublicFixture.Collision("A");
        fixture.WriteMirror("A", new("A", [1]), new("A", [1], utf16Metadata), new(other, [2]));
        var before = fixture.Snapshot();
        using (var store = fixture.Open()) {
            Assert.Equal("VersionStore.DuplicateTagName", store.ResolveTag("A").Error!.ErrorCode);
            Assert.Equal("VersionStore.DuplicateTagName", store.CreateTag("A", [1], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(new byte[] { 2 }, store.ResolveTag(other).Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
        using var next = fixture.Open();
        string fresh = TagPublicFixture.Collision("A", other);
        Assert.True(next.CreateTag(fresh, [3], out var confirmed).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Fact]
    public void CompletedRbfTailStillRequiresIndependentTagMetadataQualification() {
        using var fixture = new TagPublicFixture();
        byte[] badMetadata = TagPublicFixture.Metadata("tag-48");
        badMetadata[^1] ^= 1;
        fixture.WriteMirror("tag-48", new TagPublicRecord("tag-48", [1], badMetadata));
        string path = fixture.BucketPath("tag-48");
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]);
        using (var store = fixture.Open()) {
            Assert.Equal("VersionStore.TagLookupUncertain", store.ResolveTag("tag-48").Error!.ErrorCode);
            Assert.Equal("VersionStore.TagLookupUncertain", store.CreateTag("tag-48", [2], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = fixture.Open(readOnly: true);
        Assert.Equal("VersionStore.TagLookupUncertain", cold.ResolveTag("tag-48").Error!.ErrorCode);
    }
}
