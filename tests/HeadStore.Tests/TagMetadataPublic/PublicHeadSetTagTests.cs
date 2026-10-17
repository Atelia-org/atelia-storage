using System.Reflection;
using Atelia.HeadStore.Tests.Public;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

public sealed class PublicHeadSetTagTests {
    [Fact]
    public void TagCapturesCurrentSelectedEntriesAndMetadataAcrossBothOwnersColdReopen() {
        using var fixture = new TagPublicFixture();
        HeadEntryId source;
        HeadSetTagInfo retained;
        HeadSetTag tag;
        using (var store = fixture.Open()) {
            source = store.CreateHead(7, [0x81, 0xff], out _).Unwrap();
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            tag = store.CreateTag("selected", [source.HeadId], out var outcome).Unwrap();
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            retained = store.ReadTagInfo("selected").Unwrap();
            AssertInfo(retained, "selected", 1);
            Assert.InRange(retained.RecordedAtUnixMs, before, after);
            Assert.Equal(source, tag.Entries[source.HeadId]);
            store.PublishHead(source.HeadId, [0x82], out _).Unwrap();
            Assert.Equal(source, store.ReadTag("selected").Unwrap().Entries[source.HeadId]);
        }
        fixture.Data.Dispose();
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var cold = Store.Open(fixture.Root, data)) {
            HeadSetTagInfo info = cold.ReadTagInfo("selected").Unwrap();
            AssertInfo(info, "selected", 1);
            Assert.Equal(retained.RecordedAtUnixMs, info.RecordedAtUnixMs);
            Assert.Equal(source, cold.ReadTag("selected").Unwrap().Entries[source.HeadId]);
            Assert.Equal(new byte[] { 0x81, 0xff }, cold.ReadEntry(source).Unwrap().Payload.ToArray());
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var cold = Store.OpenReadOnly(fixture.Root, data)) {
            var scanned = new List<HeadSetTagInfo>();
            Assert.Equal(HeadSetTagScanEnd.Complete, cold.ScanTags(info => { scanned.Add(info); return true; }).Unwrap());
            AssertInfo(Assert.Single(scanned), "selected", 1);
            Assert.Equal(retained.RecordedAtUnixMs, scanned[0].RecordedAtUnixMs);
        }
        Assert.Equal(source, tag.Entries[source.HeadId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionCannotCaptureAnUnavailableCurrentHead(bool missingSource) {
        using var fixture = new TagPublicFixture();
        HeadEntryId source;
        using (var store = fixture.Open()) {
            source = store.CreateHead(7, Enumerable.Repeat((byte)0x57, 128).ToArray(), out _).Unwrap();
        }
        if (missingSource) { File.Delete(fixture.HeadPath(7)); }
        else { PublicHeadStoreFixture.FlipPayloadByte(fixture.HeadPath(7), source.Ticket); }
        byte[]? sourceImage = missingSource ? null : File.ReadAllBytes(fixture.HeadPath(7));
        using (var store = fixture.Open()) {
            Assert.True(store.CreateTag("source-unavailable", [source.HeadId], out var outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal("HeadStore.TagNotFound", store.ReadTag("source-unavailable").Error!.ErrorCode);
        }
        if (missingSource) { Assert.False(File.Exists(fixture.HeadPath(7))); }
        else { Assert.Equal(sourceImage, File.ReadAllBytes(fixture.HeadPath(7))); }
        Assert.False(File.Exists(fixture.BucketPath("source-unavailable")));
    }

    [Fact]
    public void TagMetadataDeclaresTheSelectedCountAndDoesNotAllocateHeadIds() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId first = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId second = store.CreateHead(8, [2], out _).Unwrap();
        store.CreateTag("one", [first.HeadId], out _).Unwrap();
        store.CreateTag("two", [second.HeadId, first.HeadId], out _).Unwrap();
        AssertInfo(store.ReadTagInfo("one").Unwrap(), "one", 1);
        AssertInfo(store.ReadTagInfo("two").Unwrap(), "two", 2);
        Assert.Equal(new uint[] { 7, 8 }, store.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)));
        Assert.Equal(257u, store.CreateHead([4], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
    }

    [Fact]
    public void NamesAreCreateOnlyEvenWhenTheSelectionIsEqualOrChanged() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId first = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId second = store.CreateHead(8, [2], out _).Unwrap();
        store.CreateTag("fixed", [first.HeadId], out _).Unwrap();
        foreach (HeadId selected in new[] { first.HeadId, second.HeadId }) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("fixed", [selected], out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.Equal(first, Assert.Single(store.ReadTag("fixed").Unwrap().Entries).Value);
    }

    [Fact]
    public void InfoPublicShapeContainsOnlyNameTimeAndDeclaredCountAndOldApiIsAbsent() {
        Type type = typeof(HeadSetTagInfo);
        Assert.True(type.IsSealed);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(new[] { "EntryCount", "Name", "RecordedAtUnixMs" },
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.GetSetMethod(false)));
        foreach (string old in new[] { "RootPayload", "TagInfo", "TagScanEnd" }) {
            Assert.Null(type.Assembly.GetType("Atelia.HeadStore." + old));
        }
        Assert.Null(typeof(Store).GetMethod("ResolveTag"));
        MethodInfo create = Assert.Single(typeof(Store).GetMethods(), method => method.Name == "CreateTag");
        Assert.Equal(typeof(AteliaResult<HeadSetTag>), create.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(ReadOnlySpan<HeadId>), typeof(PublicationOutcome).MakeByRefType() },
            create.GetParameters().Select(parameter => parameter.ParameterType));
    }

    internal static void AssertInfo(HeadSetTagInfo info, string name, int count) {
        Assert.Equal(name, info.Name);
        Assert.Equal(count, info.EntryCount);
    }
}
