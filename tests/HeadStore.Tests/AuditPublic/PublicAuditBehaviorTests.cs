using System.Buffers.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.TagPublic;
using Atelia.HeadStore.Tests.Tags;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.AuditPublic;

public sealed class PublicAuditBehaviorTests {
    [Fact]
    public void PublicOpaqueForksAndOrdinalTagsAuditAcrossColdOwnersWithoutChangingEitherStore() {
        using var fixture = new AuditPublicFixture();
        byte[] opaque = [0xff, 0, 0xfe];
        Assert.True(NamedFrameMapCodec.Decode(opaque).IsFailure);
        byte[] addressWire = new byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(addressWire, uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(addressWire.AsSpan(4), SizedPtr.Create(8, 40).Packed);
        Assert.True(FrameAddress.TryRead(addressWire, out var missingAddress));
        byte[] rootWithAbsentData = NamedFrameMapCodec.Encode(new Dictionary<string, FrameAddress> {
            ["missing-data"] = missingAddress
        }).Unwrap();
        Assert.Equal(missingAddress, NamedFrameMapCodec.Decode(rootWithAbsentData).Unwrap()["missing-data"]);
        HeadEntryId initial;
        HeadEntryId child;
        HeadEntryId nested;
        string[] names = ["Release", "release", "associated"];
        using (var store = fixture.Storage.Open()) {
            initial = store.CreateHead(7, opaque, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishHead(initial.HeadId, rootWithAbsentData, out _).Unwrap();
            child = store.ForkHead(12, initial, out _).Unwrap();
            store.PublishHead(child.HeadId, "{\"phase\":\"done\"}"u8, out _).Unwrap();
            nested = store.ForkHead(13, child, out _).Unwrap();
            store.CreateTag(names[0], [nested.HeadId], out _).Unwrap();
            store.CreateTag(names[1], [initial.HeadId], out _).Unwrap();
            store.CreateTag(names[2], [initial.HeadId, child.HeadId], out _).Unwrap();
        }
        fixture.Data.Dispose();
        var before = fixture.CaptureFiles();
        HeadStoreAudit retained;
        using (var data = DataStore.OpenReadOnly(fixture.Storage.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(opaque, readOnly.ReadHead(nested.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(rootWithAbsentData, readOnly.ReadHead(initial.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(opaque, readOnly.ReadEntry(readOnly.ReadTag("Release").Unwrap().Entries[nested.HeadId]).Unwrap().Payload.ToArray());
            Assert.Equal(rootWithAbsentData, readOnly.ReadEntry(readOnly.ReadTag("release").Unwrap().Entries[initial.HeadId]).Unwrap().Payload.ToArray());
            retained = readOnly.Audit().Unwrap();
            long bucketCount = names.Select(TagPublicFixture.Bucket).Distinct().LongCount();
            AuditPublicFixture.AssertCounts(retained, 3, 5, 2, bucketCount, 3);
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 3, 5, 2, bucketCount, 3);
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(5L, retained.EntryCount);
        using (var data = DataStore.OpenReadOnly(fixture.Storage.DataRoot))
        using (var reopened = Store.OpenReadOnly(fixture.Root, data)) {
            AuditPublicFixture.AssertCounts(reopened.Audit().Unwrap(), 3, 5, 2,
                names.Select(TagPublicFixture.Bucket).Distinct().LongCount(), 3);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty-directory")]
    [InlineData("header-only")]
    public void EmptyHeadAndTagLayoutsProduceExactCountsWithoutCreatingFiles(string layout) {
        using var fixture = new AuditPublicFixture();
        if (layout == "empty-directory") { Directory.CreateDirectory(fixture.Storage.TagsPath); }
        if (layout == "header-only") { fixture.Storage.WriteMirror("A"); }
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0, layout == "header-only" ? 1 : 0);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void ReadOnlyAuditPreservesQualifiedPendingWithoutInspectingItsArbitraryContents() {
        using var fixture = new AuditPublicFixture();
        File.WriteAllBytes(fixture.Storage.PendingPath, [0xff, 0x00, 0x57]);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0);
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x57 }, File.ReadAllBytes(fixture.Storage.PendingPath));
    }

    [Fact]
    public void TagMembersAreFormalDependenciesEvenWhenMetadataAndTableRemainReadable() {
        using var fixture = new AuditPublicFixture();
        SizedPtr declared = SizedPtr.Create(SizedPtr.MaxOffset, 4);
        fixture.Storage.WriteMirror("A", new TagPublicRecord("A", TagTestWire.Members(uint.MaxValue, declared)));
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            HeadSetTagInfo info = readOnly.ReadTagInfo("A").Unwrap();
            Assert.Equal(1, info.EntryCount);
            var member = Assert.Single(readOnly.ReadTag("A").Unwrap().Entries);
            Assert.Equal(uint.MaxValue, member.Key.LocalId);
            Assert.Equal(declared, member.Value.Ticket);
            Assert.True(readOnly.ReadEntry(member.Value).IsFailure);
            Assert.Empty(readOnly.ListHeads().Unwrap());
            Assert.True(readOnly.Audit().IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("tag")]
    public void MissingKeyAndFenceAreRejectedWithoutCompletingTheTail(string target) {
        using var fixture = new AuditPublicFixture();
        string path;
        using (var store = fixture.Storage.Open()) {
            if (target == "ref") {
                store.CreateHead(7, AuditPublicFixture.Bytes(), out _).Unwrap();
                path = fixture.HeadPath(7);
            }
            else {
                var member = store.CreateHead(7, AuditPublicFixture.Bytes(), out _).Unwrap();
                store.CreateTag("A", [member.HeadId], out _).Unwrap();
                path = fixture.Storage.BucketPath("A");
            }
        }
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Single(readOnly.ListHeads().Unwrap());
            Assert.Throws<InvalidDataException>(() => { readOnly.Audit(); });
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(complete[..^8], File.ReadAllBytes(path));
    }

    [Fact]
    public void FormalBucketShorterThanItsHeaderInitializationIsNotRepairedOrSkipped() {
        using var fixture = new AuditPublicFixture();
        fixture.Storage.WriteMirror("A");
        string path = fixture.Storage.BucketPath("A");
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^1]);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Throws<InvalidDataException>(() => { readOnly.Audit(); });
        }
        fixture.AssertUnchanged(before);
    }
}
