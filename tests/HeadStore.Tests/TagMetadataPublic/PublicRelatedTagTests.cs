using System.Buffers;
using System.Reflection;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

public sealed class PublicRelatedTagTests {
    [Fact]
    public void RelatedTagFreezesActualSliceAndCoordinatesAcrossBothOwnersColdReopen() {
        using var fixture = new TagPublicFixture();
        byte[] rented = ArrayPool<byte>.Shared.Rent(256);
        HeadEntryId source;
        TagInfo retained;
        RootPayload root;
        long before;
        long after;
        byte[] expected = Enumerable.Range(0, 97).Select(index => (byte)(index + 1)).ToArray();
        try {
            rented.AsSpan().Fill(0xef);
            expected.CopyTo(rented, 13);
            using (var store = fixture.Open()) {
                source = store.CreateHead(7, [0x81, 0xff], out _).Unwrap();
                before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Assert.True(store.CreateTag("related", rented.AsSpan(13, expected.Length), source, out var outcome).Unwrap());
                after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
                rented.AsSpan().Clear();
                retained = store.ReadTagInfo("related").Unwrap();
                root = store.ResolveTag("related").Unwrap();
                AssertInfo(retained, "related", source);
                Assert.InRange(retained.RecordedAtUnixMs, before, after);
                Assert.Equal(expected, root.Bytes.ToArray());
                Assert.NotEqual(store.ReadHead(source.HeadId).Unwrap().Payload.ToArray(), root.Bytes.ToArray());
                store.PublishHead(source.HeadId, [0x82], out _).Unwrap();
                AssertInfo(store.ReadTagInfo("related").Unwrap(), "related", source);
                Assert.Equal(expected, store.ResolveTag("related").Unwrap().Bytes.ToArray());
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }

        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            TagInfo info = cold.ReadTagInfo("related").Unwrap();
            AssertInfo(info, "related", source);
            Assert.Equal(retained.RecordedAtUnixMs, info.RecordedAtUnixMs);
            Assert.Equal(expected, cold.ResolveTag("related").Unwrap().Bytes.ToArray());
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            var scanned = new List<TagInfo>();
            Assert.Equal(TagScanEnd.Complete, cold.ScanTags(info => { scanned.Add(info); return true; }).Unwrap());
            AssertInfo(Assert.Single(scanned), "related", source);
            Assert.Equal(retained.RecordedAtUnixMs, scanned[0].RecordedAtUnixMs);
            Assert.Equal(expected, cold.ResolveTag("related").Unwrap().Bytes.ToArray());
        }
        AssertInfo(retained, "related", source);
        Assert.Equal(expected, root.Bytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IssuedCoordinateCanAnnotateNewBytesWhenItsSourceIsAlreadyUnavailable(bool missingSource) {
        using var fixture = new TagPublicFixture();
        HeadEntryId source;
        using (var store = fixture.Open()) {
            source = store.CreateHead(7, Enumerable.Repeat((byte)0x57, 128).ToArray(), out _).Unwrap();
        }
        // Controlled damage belongs to this fixture. The issued value remains a same-store note.
        if (missingSource) { File.Delete(fixture.HeadPath(7)); }
        else { PublicHeadStoreFixture.FlipPayloadByte(fixture.HeadPath(7), source.Ticket); }
        byte[]? sourceImage = missingSource ? null : File.ReadAllBytes(fixture.HeadPath(7));
        using (var store = fixture.Open()) {
            Assert.True(store.CreateTag("source-unavailable", [0xff, 0x81, 0], source, out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            AssertInfo(store.ReadTagInfo("source-unavailable").Unwrap(), "source-unavailable", source);
            Assert.Equal(new byte[] { 0xff, 0x81, 0 }, store.ResolveTag("source-unavailable").Unwrap().Bytes.ToArray());
        }
        if (missingSource) { Assert.False(File.Exists(fixture.HeadPath(7))); }
        else { Assert.Equal(sourceImage, File.ReadAllBytes(fixture.HeadPath(7))); }
        using var cold = fixture.Open(readOnly: true);
        AssertInfo(cold.ReadTagInfo("source-unavailable").Unwrap(), "source-unavailable", source);
        Assert.Equal(new byte[] { 0xff, 0x81, 0 }, cold.ResolveTag("source-unavailable").Unwrap().Bytes.ToArray());
        Assert.Equal(TagScanEnd.Complete, cold.ScanTags(info => {
            AssertInfo(info, "source-unavailable", source);
            return true;
        }).Unwrap());
    }

    [Fact]
    public void AnonymousAndRelatedMetadataKeepBothCoordinateFieldsTogetherAndDoNotAllocateHeadIds() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
        Assert.True(store.CreateTag("anonymous", [2], out _).Unwrap());
        Assert.True(store.CreateTag("related", [3], source, out _).Unwrap());
        TagInfo anonymous = store.ReadTagInfo("anonymous").Unwrap();
        Assert.Equal("anonymous", anonymous.Name);
        Assert.Null(anonymous.RelatedRefId);
        Assert.Null(anonymous.RelatedTicket);
        AssertInfo(store.ReadTagInfo("related").Unwrap(), "related", source);
        Assert.Equal(new uint[] { 7 }, store.ListHeads().Unwrap().Select(id => id.LocalId));
        Assert.Equal(257u, store.CreateHead([4], out _).Unwrap().HeadId.LocalId);
    }

    [Fact]
    public void RelatedAndAnonymousCreateAreOneCreateOnlyNameSpaceEvenForEqualBytes() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
        store.CreateTag("anonymous-first", [2], out _).Unwrap();
        store.CreateTag("related-first", [2], source, out _).Unwrap();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("anonymous-first", [2], source, out outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        outcome = PublicationOutcome.Confirmed;
        Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("related-first", [2], out outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Null(store.ReadTagInfo("anonymous-first").Unwrap().RelatedRefId);
        AssertInfo(store.ReadTagInfo("related-first").Unwrap(), "related-first", source);
    }

    [Fact]
    public void TagInfoPublicShapeCannotIssueEntryIdOrExposeRootAndHasNoMutableMembers() {
        Type type = typeof(TagInfo);
        Assert.True(type.IsSealed);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(new[] { "Name", "RecordedAtUnixMs", "RelatedRefId", "RelatedTicket" },
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => {
            Assert.Null(property.GetSetMethod(nonPublic: false));
            Assert.NotEqual(typeof(HeadEntryId), property.PropertyType);
            Assert.NotEqual(typeof(RootPayload), property.PropertyType);
        });
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.ReturnType == typeof(HeadEntryId));
    }

    internal static void AssertInfo(TagInfo info, string name, HeadEntryId source) {
        Assert.Equal(name, info.Name);
        Assert.Equal(source.HeadId, info.RelatedRefId);
        Assert.Equal(source.Ticket, info.RelatedTicket);
    }
}
