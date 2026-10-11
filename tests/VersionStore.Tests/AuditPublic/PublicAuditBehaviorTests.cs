using System.Buffers.Binary;
using Atelia.Data;
using Atelia.FrameStore;
using Atelia.Rbf;
using Atelia.VersionStore.Tests.TagPublic;
using Atelia.VersionStore.Tests.Tags;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.AuditPublic;

public sealed class PublicAuditBehaviorTests {
    [Fact]
    public void PublicOpaqueForksAndOrdinalTagsAuditAcrossColdOwnersWithoutChangingEitherStore() {
        using var fixture = new AuditPublicFixture();
        byte[] opaque = [0xff, 0, 0xfe];
        Assert.True(RootMapCodec.Decode(opaque).IsFailure);
        byte[] addressWire = new byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(addressWire, uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(addressWire.AsSpan(4), SizedPtr.Create(8, 40).Packed);
        Assert.True(FrameAddress.TryRead(addressWire, out var missingAddress));
        byte[] rootWithAbsentData = RootMapCodec.Encode(new Dictionary<string, FrameAddress> {
            ["missing-data"] = missingAddress
        }).Unwrap();
        Assert.Equal(missingAddress, RootMapCodec.Decode(rootWithAbsentData).Unwrap()["missing-data"]);
        RefRevision initial;
        RefRevision child;
        RefRevision nested;
        string[] names = ["Release", "release", "associated"];
        using (var store = fixture.Storage.Open()) {
            initial = store.CreateRef(7, opaque, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishRef(initial.RefId, rootWithAbsentData, out _).Unwrap();
            child = store.ForkRef(12, initial, out _).Unwrap();
            store.PublishRef(child.RefId, "{\"phase\":\"done\"}"u8, out _).Unwrap();
            nested = store.ForkRef(13, child, out _).Unwrap();
            Assert.True(store.CreateTag(names[0], opaque, out _).Unwrap());
            Assert.True(store.CreateTag(names[1], rootWithAbsentData, out _).Unwrap());
            Assert.True(store.CreateTag(names[2], [0x81], initial, out _).Unwrap());
        }
        fixture.Data.Dispose();
        var before = fixture.Snapshot();
        VersionStoreAudit retained;
        using (var data = DataStore.OpenReadOnly(fixture.Storage.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(opaque, readOnly.ReadRef(nested.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(rootWithAbsentData, readOnly.ReadRef(initial.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(opaque, readOnly.ResolveTag("Release").Unwrap().Bytes.ToArray());
            Assert.Equal(rootWithAbsentData, readOnly.ResolveTag("release").Unwrap().Bytes.ToArray());
            retained = readOnly.Audit().Unwrap();
            long bucketCount = names.Select(TagPublicFixture.Bucket).Distinct().LongCount();
            AuditPublicFixture.AssertCounts(retained, 3, 5, 2, bucketCount, 3);
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 3, 5, 2, bucketCount, 3);
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(5L, retained.SnapshotCount);
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
    public void EmptyRefAndTagLayoutsProduceExactCountsWithoutCreatingFiles(string layout) {
        using var fixture = new AuditPublicFixture();
        if (layout == "empty-directory") { Directory.CreateDirectory(fixture.Storage.TagsPath); }
        if (layout == "header-only") { fixture.Storage.WriteMirror("A"); }
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0, layout == "header-only" ? 1 : 0);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void ReadOnlyAuditPreservesQualifiedPendingWithoutInspectingItsArbitraryContents() {
        using var fixture = new AuditPublicFixture();
        File.WriteAllBytes(fixture.Storage.PendingPath, [0xff, 0x00, 0x57]);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0);
        }
        fixture.AssertUnchanged(before);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x57 }, File.ReadAllBytes(fixture.Storage.PendingPath));
    }

    [Fact]
    public void TagRelatedCoordinatesRemainAnnotationsEvenWhenTheirSourceDoesNotExist() {
        using var fixture = new AuditPublicFixture();
        SizedPtr declared = SizedPtr.Create(SizedPtr.MaxOffset, 4);
        fixture.Storage.WriteMirror("A", new TagPublicRecord("A", [0xff],
            TagTestWire.Metadata("A", id: uint.MaxValue, ticket: declared)));
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            TagInfo info = readOnly.ReadTagInfo("A").Unwrap();
            Assert.Equal(uint.MaxValue, info.RelatedRefId!.Value.LocalId);
            Assert.Equal(declared, info.RelatedTicket);
            Assert.Empty(readOnly.ListRefs().Unwrap());
            AuditPublicFixture.AssertCounts(readOnly.Audit().Unwrap(), 0, 0, 0, 1, 1);
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
                store.CreateRef(7, AuditPublicFixture.Bytes(), out _).Unwrap();
                path = fixture.RefPath(7);
            }
            else {
                store.CreateTag("A", AuditPublicFixture.Bytes(), out _).Unwrap();
                path = fixture.Storage.BucketPath("A");
            }
        }
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, complete[..^8]);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Equal(target == "ref" ? 1 : 0, readOnly.ListRefs().Unwrap().Count);
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
        var before = fixture.Snapshot();
        using (var readOnly = fixture.OpenReadOnly()) {
            Assert.Throws<InvalidDataException>(() => { readOnly.Audit(); });
        }
        fixture.AssertUnchanged(before);
    }
}
