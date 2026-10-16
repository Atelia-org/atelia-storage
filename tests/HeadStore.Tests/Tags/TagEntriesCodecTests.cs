using Atelia.Data;
using Atelia.HeadStore.Internal.Tags;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class TagEntriesCodecTests {
    [Fact]
    public void CanonicalTableUsesFullPackedTicketsAndRetainsItsCheckedStoreIdentity() {
        byte[] wire = Convert.FromHexString("010000000A00003C00000000FFFFFFFFFFFFFFFFFFFFFFFF");
        SizedPtr firstTicket = SizedPtr.Create(60, 40);
        Assert.Equal(wire, TagTestWire.Join(TagTestWire.Members(1, firstTicket),
            TagTestWire.Members(uint.MaxValue, SizedPtr.FromPacked(ulong.MaxValue))));
        Assert.Equal(12, TagEntriesCodec.MemberEncodedSize);
        Assert.True(TagEntriesCodec.TryRead(wire, TagTestWire.Identity, 2, out var entries));
        HeadId first = HeadId.Create(TagTestWire.Identity, 1);
        HeadId last = HeadId.Create(TagTestWire.Identity, uint.MaxValue);
        Assert.Equal(firstTicket, entries[first].Ticket);
        Assert.Equal(ulong.MaxValue, entries[last].Ticket.Packed);
        Assert.All(entries, pair => Assert.Equal(pair.Key, pair.Value.HeadId));
        HeadEntryId[] ordered = entries.Values.OrderBy(entry => entry.HeadId.LocalId).ToArray();
        Assert.Equal(wire, TagEntriesCodec.Encode(ordered, TagTestWire.Identity));
        Array.Clear(wire);
        Assert.Equal(firstTicket, entries[first].Ticket);
        Assert.False(TagEntriesCodec.TryRead(TagTestWire.Members(), default, 1, out _));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("reverse")]
    [InlineData("zero-id")]
    [InlineData("default-ticket")]
    [InlineData("zero-length-ticket")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("wrong-count")]
    public void CanonicalTableRejectsMalformedMembersEvenWhenIndependentMetadataAndNameAreValid(string damage) {
        (byte[] wire, int count) = InvalidTable(damage);
        Assert.False(TagEntriesCodec.TryRead(wire, TagTestWire.Identity, count, out _));
        using var fixture = new TagPublicFixture();
        HeadId selected = fixture.SeedHead();
        fixture.WriteMirror("A", new TagPublicRecord("A", wire, TagPublicFixture.Metadata("A", entryCount: count)));
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            Assert.Equal(count, store.ReadTagInfo("A").Unwrap().EntryCount);
            Assert.Equal("HeadStore.InvalidTagEntries", store.ReadTag("A").Error!.ErrorCode);
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("A", [selected], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(HeadSetTagScanEnd.Complete, store.ScanTags(info => {
                Assert.Equal(count, info.EntryCount);
                return true;
            }).Unwrap());
        }
        fixture.AssertUnchanged(before);
    }

    private static (byte[] Wire, int Count) InvalidTable(string damage) => damage switch {
        "duplicate" => (TagTestWire.Join(TagTestWire.Members(1), TagTestWire.Members(1)), 2),
        "reverse" => (TagTestWire.Join(TagTestWire.Members(2), TagTestWire.Members(1)), 2),
        "zero-id" => (TagTestWire.Members(0), 1),
        "default-ticket" => (TagTestWire.Members(1, default(SizedPtr)), 1),
        "zero-length-ticket" => (TagTestWire.Members(1, SizedPtr.Create(60, 0)), 1),
        "short" => (TagTestWire.Members()[..^1], 1),
        "extra" => ([.. TagTestWire.Members(), 0], 1),
        "wrong-count" => (TagTestWire.Members(), 2),
        _ => throw new ArgumentOutOfRangeException(nameof(damage))
    };
}
