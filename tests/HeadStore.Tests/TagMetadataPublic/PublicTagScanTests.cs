using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

public sealed class PublicTagScanTests {
    [Theory]
    [InlineData("missing")]
    [InlineData("empty-directory")]
    [InlineData("header-only")]
    public void HealthyEmptyLayoutsCompleteWithoutCreatingOrChangingFiles(string layout) {
        using var fixture = new TagPublicFixture();
        if (layout == "empty-directory") { Directory.CreateDirectory(fixture.TagsPath); }
        if (layout == "header-only") { fixture.WriteMirror("A"); }
        var before = fixture.CaptureFiles();
        foreach (bool readOnly in new[] { false, true }) {
            using (var store = fixture.Open(readOnly)) {
                Assert.Equal(TagScanEnd.Complete, store.ScanTags(_ => throw new InvalidOperationException("No records exist.")).Unwrap());
                Assert.Equal("HeadStore.TagNotFound", store.ReadTagInfo("A").Error!.ErrorCode);
            }
            fixture.AssertUnchanged(before);
        }
        Assert.Equal(layout != "missing", Directory.Exists(fixture.TagsPath));
    }

    [Fact]
    public void ScanVisitsEveryBuiltBucketAndDeliversEveryPhysicalDuplicateInRecordOrder() {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A", new("A", [1], TagTestWire.Metadata("A", time: 12)),
            new("A", [2], TagTestWire.Metadata("A", time: -7, nameWire: TagTestWire.Utf16Name("A"))),
            new("A", [3], TagTestWire.Metadata("A", time: 12, nameWire: TagTestWire.Utf8Name("A", headerWidth: 2))));
        fixture.WriteMirror("Release", new TagPublicRecord("Release", [4]));
        fixture.WriteMirror("release", new TagPublicRecord("release", [5]));
        string zeroBucketName = Enumerable.Range(0, 100_000).Select(value => $"zero-bucket-{value}")
            .First(name => TagPublicFixture.Bucket(name) == 0);
        fixture.WriteMirror(zeroBucketName, new TagPublicRecord(zeroBucketName, [6]));
        var before = fixture.CaptureFiles();
        List<TagInfo> retained = [];
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal(TagScanEnd.Complete, store.ScanTags(info => { retained.Add(info); return true; }).Unwrap());
            Assert.Equal(6, retained.Count);
            Assert.Equal(new long[] { 12, -7, 12 }, retained.Where(info => info.Name == "A").Select(info => info.RecordedAtUnixMs));
            Assert.Equal(new[] { "A", "A", "A", "Release", "release", zeroBucketName }.Order(StringComparer.Ordinal),
                retained.Select(info => info.Name).Order(StringComparer.Ordinal));
            Assert.Equal("HeadStore.DuplicateTagName", store.ReadTagInfo("A").Error!.ErrorCode);
            Assert.All(retained, info => { Assert.Null(info.RelatedRefId); Assert.Null(info.RelatedTicket); });
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(new long[] { 12, -7, 12 }, retained.Where(info => info.Name == "A").Select(info => info.RecordedAtUnixMs));
    }

    [Fact]
    public void ScanCompleteDoesNotProveRootCrcRootSizeOrRelatedSourceQualification() {
        using var fixture = new TagPublicFixture();
        string oversizedName = TagPublicFixture.Collision("tag-48", "tag-65");
        SizedPtr declared = SizedPtr.Create(0, 4);
        var tickets = fixture.WriteMirror("tag-48", new("tag-48", new byte[128]),
            new("tag-65", [], TagTestWire.Metadata("tag-65", id: uint.MaxValue, ticket: declared)),
            new(oversizedName, new byte[HeadStore.MaxHeadEntryPayloadLength + 1]));
        fixture.FlipRoot("tag-48", tickets[0]);
        var before = fixture.CaptureFiles();
        List<TagInfo> retained = [];
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal(TagScanEnd.Complete, store.ScanTags(info => { retained.Add(info); return true; }).Unwrap());
            Assert.Equal(new[] { "tag-48", "tag-65", oversizedName }, retained.Select(info => info.Name));
            Assert.Equal(uint.MaxValue, retained[1].RelatedRefId!.Value.LocalId);
            Assert.Equal(declared, retained[1].RelatedTicket);
            Assert.Empty(store.ListHeads().Unwrap());
            Assert.True(store.ResolveTag("tag-48").IsFailure);
            Assert.Equal("HeadStore.InvalidTagRoot", store.ResolveTag("tag-65").Error!.ErrorCode);
            Assert.Equal("HeadStore.InvalidTagRoot", store.ResolveTag(oversizedName).Error!.ErrorCode);
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(declared, retained[1].RelatedTicket);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("trailing")]
    [InlineData("bad-utf8")]
    [InlineData("empty-name")]
    [InlineData("overlong-name")]
    [InlineData("wrong-route")]
    [InlineData("default-ticket")]
    [InlineData("zero-length-ticket")]
    [InlineData("short-meta")]
    [InlineData("oversized-meta")]
    [InlineData("unknown-kind")]
    [InlineData("duplicate-header")]
    [InlineData("tombstone")]
    public void ScanRejectsNecessaryBadRecordInsteadOfIsolatingItOrReturningComplete(string damage) {
        using var fixture = new TagPublicFixture();
        byte[] metadata = TagPublicFixture.Metadata("tag-65");
        uint kind = 1;
        switch (damage) {
            case "crc": metadata[^1] ^= 1; break;
            case "trailing": metadata = TagTestWire.Seal([.. metadata[..^4], 0x57]); break;
            case "bad-utf8": metadata = TagTestWire.Metadata("tag-65", nameWire: [0x03, 0xff]); break;
            case "empty-name": metadata = TagPublicFixture.Metadata(""); break;
            case "overlong-name": metadata = TagPublicFixture.Metadata(new string('a', 1025)); break;
            case "wrong-route": metadata = TagPublicFixture.Metadata("Release"); break;
            case "default-ticket": metadata = TagTestWire.Metadata("tag-65", id: 1, ticket: default(SizedPtr)); break;
            case "zero-length-ticket": metadata = TagTestWire.Metadata("tag-65", id: 1, ticket: SizedPtr.Create(60, 0)); break;
            case "short-meta": metadata = new byte[14]; break;
            case "oversized-meta": metadata = new byte[3105]; break;
            case "unknown-kind": kind = 17; break;
            case "duplicate-header": kind = 0; break;
        }
        var tickets = fixture.WriteMirror("tag-48", new("tag-48", [1], TagPublicFixture.Metadata("tag-48", -17)),
            new("tag-65", [2], metadata, kind), new("tag-48", [3]));
        if (damage == "tombstone") { MakeTombstone(fixture.BucketPath("tag-48"), tickets[1]); }
        var before = fixture.CaptureFiles();
        List<TagInfo> retained = [];
        using (var store = fixture.Open(readOnly: true)) {
            var result = store.ScanTags(info => { retained.Add(info); return true; });
            Assert.True(result.IsFailure);
            Assert.Equal("HeadStore.InvalidTagRecord", result.Error!.ErrorCode);
            TagInfo delivered = Assert.Single(retained);
            Assert.Equal("tag-48", delivered.Name);
            Assert.Equal(-17, delivered.RecordedAtUnixMs);
            Assert.Equal("HeadStore.DuplicateTagName", store.ReadTagInfo("tag-48").Error!.ErrorCode);
        }
        Assert.Equal("tag-48", retained[0].Name);
        Assert.Equal(-17, retained[0].RecordedAtUnixMs);
        fixture.AssertUnchanged(before);
        fixture.AssertBucketReleased("tag-48");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("bucket")]
    [InlineData("trailing")]
    public void ScanChecksEveryNecessaryHeaderBeforeDeliveringItsRecords(string damage) {
        using var fixture = new TagPublicFixture();
        byte[] header = fixture.Header("A");
        switch (damage) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(header, 2); break;
            case "identity": header.AsSpan(4, 16).Clear(); break;
            case "bucket": BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), TagPublicFixture.Bucket("Release")); break;
            case "trailing": header = [.. header, 0]; break;
        }
        fixture.WriteMirrorHeader("A", header, new TagPublicRecord("A", [1]));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            int delivered = 0;
            Assert.Equal("HeadStore.InvalidTagBucketHeader", store.ScanTags(_ => { delivered++; return true; }).Error!.ErrorCode);
            Assert.Equal(0, delivered);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("unknown.txt", false)]
    [InlineData("00000106.RBF", false)]
    [InlineData("00000400.rbf", false)]
    [InlineData("00000abc.rbf", false)]
    [InlineData("0000010A.rbf", false)]
    [InlineData("00000000.rbf", true)]
    [InlineData("subdir", true)]
    public void ScanAndNamedMetadataRequireEveryDirectItemNameAndOrdinaryType(string name, bool directory) {
        using var fixture = new TagPublicFixture();
        Directory.CreateDirectory(fixture.TagsPath);
        string path = Path.Combine(fixture.TagsPath, name);
        if (directory) { Directory.CreateDirectory(path); }
        else { File.WriteAllBytes(path, [1]); }
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            int delivered = 0;
            Assert.Throws<InvalidDataException>(() => store.ScanTags(_ => { delivered++; return true; }));
            Assert.Equal(0, delivered);
            Assert.Throws<InvalidDataException>(() => store.ReadTagInfo("A"));
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void CanonicalUnrelatedBucketContentIsOutsideNamedInfoScopeAndInsideScanScope() {
        using var fixture = new TagPublicFixture();
        fixture.WriteMirror("A", new TagPublicRecord("A", [1]));
        File.WriteAllBytes(fixture.BucketPath("Release"), []);
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("A", store.ReadTagInfo("A").Unwrap().Name);
            Assert.Throws<InvalidDataException>(() => store.ScanTags(_ => true));
            Assert.Equal("A", store.ReadTagInfo("A").Unwrap().Name);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void LinkedBucketDirectoryIsRejectedWithoutFollowingItsContents() {
        using var fixture = new TagPublicFixture();
        Directory.CreateDirectory(fixture.TagsPath);
        string external = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "linked-target");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "sentinel"), [1, 2, 3]);
        string link = Path.Combine(fixture.TagsPath, "00000000.rbf");
        PublicHeadStoreFixture.CreateDirectoryLink(link, external);
        try {
            using var store = fixture.Open(readOnly: true);
            Assert.Throws<InvalidDataException>(() => store.ScanTags(_ => true));
            Assert.Throws<InvalidDataException>(() => store.ReadTagInfo("A"));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(external, "sentinel")));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        }
        finally { Directory.Delete(link); }
    }

    // Modify a closed public RBF3 frame, preserving framing and TrailerCRC while setting its tombstone bit.
    private static void MakeTombstone(string path, SizedPtr ticket) {
        byte[] frame = File.ReadAllBytes(path).AsSpan(checked((int)ticket.Offset), ticket.Length).ToArray();
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(frame.Length - 4));
        XorBody(frame, key);
        int trailer = frame.Length - 20;
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(trailer + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(trailer + 4), descriptor | 0x80000000u);
        RollingCrc.SealCodewordBackward(frame.AsSpan(trailer, 16));
        XorBody(frame, key);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = ticket.Offset;
        stream.Write(frame);
    }

    private static void XorBody(byte[] frame, uint key) {
        for (int offset = 4; offset < frame.Length - 4; offset += 4) {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), value ^ key);
        }
    }
}
