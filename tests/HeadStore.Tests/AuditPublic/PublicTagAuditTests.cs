using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditPublic;

public sealed class PublicTagAuditTests {
    [Fact]
    public void OrdinalCaseDistinctNamesInTheSameBucketRemainSeparateHealthyTags() {
        using var fixture = new AuditPublicFixture();
        const string upper = "Case-2647";
        const string lower = "case-2647";
        Assert.Equal(TagPublicFixture.Bucket(upper), TagPublicFixture.Bucket(lower));
        using (var writer = fixture.Storage.Open()) {
            writer.CreateTag(upper, [1], out _).Unwrap();
            writer.CreateTag(lower, [2], out _).Unwrap();
        }
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag(upper).Unwrap().Bytes.ToArray());
            Assert.Equal(new byte[] { 2 }, readOnly.ResolveTag(lower).Unwrap().Bytes.ToArray());
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0, 1, 2);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void CompleteMetadataQueriesDoNotHideACorruptRootFromTheFullAudit() {
        using var fixture = new AuditPublicFixture();
        var tickets = fixture.Storage.WriteMirror("tag-48",
            new("tag-48", AuditPublicFixture.Bytes(), TagPublicFixture.Metadata("tag-48", -17)),
            new("tag-65", [2]));
        fixture.Storage.FlipRoot("tag-48", tickets[0]);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(-17, readOnly.ReadTagInfo("tag-48").Unwrap().RecordedAtUnixMs);
            List<string> names = [];
            Assert.Equal(TagScanEnd.Complete, readOnly.ScanTags(info => { names.Add(info.Name); return true; }).Unwrap());
            Assert.Equal(new[] { "tag-48", "tag-65" }, names);
            Assert.Equal(new byte[] { 2 }, readOnly.ResolveTag("tag-65").Unwrap().Bytes.ToArray());
            var audited = readOnly.Audit();
            Assert.True(audited.IsFailure);
            Assert.StartsWith("Rbf.", audited.Error!.ErrorCode);
            Assert.Equal(-17, readOnly.ReadTagInfo("tag-48").Unwrap().RecordedAtUnixMs);
        }
        fixture.AssertUnchanged(before);
        fixture.Storage.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HeadStore.MaxHeadEntryPayloadLength + 1)]
    public void TrustedMetadataAndScanCompleteDoNotQualifyInvalidRootLengths(int length) {
        using var fixture = new AuditPublicFixture();
        fixture.Storage.WriteMirror("A", new TagPublicRecord("A", new byte[length]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal("A", readOnly.ReadTagInfo("A").Unwrap().Name);
            Assert.Equal(TagScanEnd.Complete, readOnly.ScanTags(_ => true).Unwrap());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal("A", readOnly.ReadTagInfo("A").Unwrap().Name);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateDecodedOrdinalNameRejectsEvenWithDifferentLegalStringEncodings(bool equalRoots) {
        using var fixture = new AuditPublicFixture();
        string unique = TagPublicFixture.Collision("A");
        fixture.Storage.WriteMirror("A", new("A", [1]),
            new("A", equalRoots ? [1] : [2], TagTestWire.Metadata("A", nameWire: TagTestWire.Utf16Name("A"))),
            new(unique, [3]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            var delivered = new List<TagInfo>();
            Assert.Equal(TagScanEnd.Complete, readOnly.ScanTags(info => { delivered.Add(info); return true; }).Unwrap());
            Assert.Equal(3, delivered.Count);
            Assert.Equal(new byte[] { 3 }, readOnly.ResolveTag(unique).Unwrap().Bytes.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 3 }, readOnly.ResolveTag(unique).Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void UnknownNameRecordCannotBeIsolatedByAFullAuditEvenWhenNamedTargetResolves() {
        using var fixture = new AuditPublicFixture();
        byte[] corruptMetadata = TagPublicFixture.Metadata("tag-65");
        corruptMetadata[^1] ^= 1;
        fixture.Storage.WriteMirror("tag-48", new("tag-48", [1]), new("tag-65", [2], corruptMetadata));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
            Assert.Equal("tag-48", readOnly.ReadTagInfo("tag-48").Unwrap().Name);
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("duplicate-header")]
    [InlineData("tombstone")]
    [InlineData("wrong-route")]
    [InlineData("trailing-meta")]
    public void EveryPhysicalTagRecordMustPassItsEnvelopeAndMetadata(string defect) {
        using var fixture = new AuditPublicFixture();
        byte[] metadata = defect switch {
            "wrong-route" => TagPublicFixture.Metadata("Release"),
            "trailing-meta" => TagTestWire.Seal([.. TagPublicFixture.Metadata("tag-65")[..^4], 0x57]),
            _ => TagPublicFixture.Metadata("tag-65")
        };
        uint kind = defect == "unknown-kind" ? 17u : defect == "duplicate-header" ? 0u : 1u;
        var tickets = fixture.Storage.WriteMirror("tag-48", new("tag-48", [1]),
            new("tag-65", [2], metadata, kind));
        if (defect == "tombstone") { AuditPublicFixture.MakeTombstone(fixture.Storage.BucketPath("tag-48"), tickets[1]); }
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void AHealthyNamedBucketDoesNotHideAnInvalidUnrelatedBucketHeader() {
        using var fixture = new AuditPublicFixture();
        fixture.Storage.WriteMirror("A", new TagPublicRecord("A", [1]));
        byte[] badHeader = fixture.Storage.Header("Release");
        badHeader[4] ^= 1;
        fixture.Storage.WriteMirrorHeader("Release", badHeader, new TagPublicRecord("Release", [2]));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("A").Unwrap().Bytes.ToArray());
            Assert.True(readOnly.Audit().IsFailure);
            Assert.Equal(new byte[] { 1 }, readOnly.ResolveTag("A").Unwrap().Bytes.ToArray());
        }
        fixture.AssertUnchanged(before);
    }
}
