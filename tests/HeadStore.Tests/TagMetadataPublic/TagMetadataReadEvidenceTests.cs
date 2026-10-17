using System.Globalization;
using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore.Tests.TagFaults;
using Atelia.HeadStore.Tests.TagPublicationFaults;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

/// <summary>生产目录 backend 和真实 RBF 读取日志的窄接线证据；公开工厂闭环另由 Public 测试验证。</summary>
public sealed class TagMetadataReadEvidenceTests {
    [Theory]
    [InlineData(2)]
    [InlineData(4096)]
    public void ReadTagRejectsTrustedCountLengthMismatchBeforeRentingOrReadingTheCompleteFrame(int actualCount) {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        // Both the RBF frame and every independently assembled member are legal. Only
        // the trusted count disagrees, so a generic 16-byte alignment guard cannot reject it.
        byte[] members = TagTestWire.MemberSequence(actualCount);
        var record = new TagFaultRecord(members, TagFaultFixture.Metadata(entryCount: 1));
        SizedPtr ticket = Assert.Single(fixture.SeedBucket(record).Tickets);
        int completeFrameRents = 0;
        fixture.Tags.ConfigureOpen = file => file.BeforeRent = length => {
            if (length == ticket.Length) { completeFrameRents++; }
        };

        Assert.Equal(1, fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap().EntryCount);
        Assert.Equal(HeadSetTagScanEnd.Complete, fixture.Store.ScanTags(info => {
            Assert.Equal(TagPublicationFaultFixture.Name, info.Name);
            Assert.Equal(1, info.EntryCount);
            return true;
        }).Unwrap());

        var result = fixture.Store.ReadTag(TagPublicationFaultFixture.Name);

        Assert.Equal("HeadStore.InvalidTagEntries", result.Error!.ErrorCode);
        Assert.Equal(0, completeFrameRents);
        Assert.All(fixture.Tags.Opened, file => {
            Assert.Equal(1, file.DisposeCalls);
            Assert.DoesNotContain(ticket.Length, file.Rents);
            Assert.DoesNotContain(file.Reads, read => read.Offset == ticket.Offset && read.Length == ticket.Length);
        });
        fixture.Store.EnsureUsable();
    }

    [Fact]
    public void MatchingTrustedCountStillRequiresACompleteReadAndRejectsBadPayloadCrc() {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        const int count = 4096;
        byte[] members = TagTestWire.MemberSequence(count);
        var record = new TagFaultRecord(members, TagFaultFixture.Metadata(entryCount: count));
        SizedPtr ticket = Assert.Single(fixture.SeedBucket(record).Tickets);
        fixture.CorruptRoot(ticket); // Preserve framing and independent metadata, corrupt the table's RBF CRC.
        int completeFrameRents = 0;
        fixture.Tags.ConfigureOpen = file => file.BeforeRent = length => {
            if (length == ticket.Length) { completeFrameRents++; }
        };

        Assert.Equal(count, fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap().EntryCount);
        Assert.Equal(HeadSetTagScanEnd.Complete, fixture.Store.ScanTags(info => {
            Assert.Equal(count, info.EntryCount);
            return true;
        }).Unwrap());
        Assert.Equal(0, completeFrameRents);

        var result = fixture.Store.ReadTag(TagPublicationFaultFixture.Name);

        Assert.Equal("Rbf.CrcMismatch", result.Error!.ErrorCode);
        Assert.Equal(1, completeFrameRents);
        TagPublicationFile completeReader = fixture.Tags.Opened[^1];
        Assert.Contains(completeReader.Reads, read => read.Offset == ticket.Offset && read.Length == ticket.Length);
        Assert.All(fixture.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        fixture.Store.EnsureUsable();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1048577)]
    [InlineData(true, 0)]
    [InlineData(true, 1048577)]
    public void InfoAndScanOnlyReadSmallMetadataEvenWhenTheMembersIsOutsideItsEnvelope(bool scan, int rootLength) {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        var record = new TagFaultRecord(new byte[rootLength], TagFaultFixture.Metadata());
        TagBucketSeed seed = fixture.SeedBucket(record);

        if (scan) {
            Assert.Equal(HeadSetTagScanEnd.Complete, fixture.Store.ScanTags(info => {
                Assert.Equal(TagPublicationFaultFixture.Name, info.Name);
                return true;
            }).Unwrap());
        }
        else { Assert.Equal(TagPublicationFaultFixture.Name, fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap().Name); }

        TagPublicationFile file = Assert.Single(fixture.Tags.Opened);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Contains(file.LoggedReads(), read => read.Offset == seed.Tickets[0].Offset + 4 + rootLength
            && read.Length == record.Metadata.Length);
        Assert.DoesNotContain(file.LoggedReads(), read => read.Offset == seed.Tickets[0].Offset && read.Length == seed.Tickets[0].Length);
        Assert.NotEmpty(file.Rents);
        Assert.All(file.Rents, requested => Assert.InRange(requested, 1, 3093));
        Assert.Equal(0, file.AppendCalls);
        Assert.Equal(0, file.FlushCalls);
        Assert.DoesNotContain("data:confirm", fixture.Events);
    }

    [Fact]
    public void ScanActuallyReadsEverySmallMetaAndNeverFullyReadsAnyRecordTable() {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        TagBucketSeed seed = fixture.SeedBucket(
            TagPublicationFaultFixture.Record(payload: TagFaultFixture.Payload(64 * 1024)),
            TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName, TagFaultFixture.Payload(128 * 1024)));
        int callbacks = 0;

        Assert.Equal(HeadSetTagScanEnd.Complete, fixture.Store.ScanTags(_ => { callbacks++; return true; }).Unwrap());

        Assert.Equal(2, callbacks);
        TagPublicationFile file = Assert.Single(fixture.Tags.Opened);
        var reads = file.LoggedReads();
        for (int index = 0; index < seed.Tickets.Length; index++) {
            SizedPtr ticket = seed.Tickets[index];
            TagFaultRecord record = seed.Records[index];
            Assert.Contains(reads, read => read.Offset == ticket.Offset + 4 + record.Payload.Length && read.Length == record.Metadata.Length);
            Assert.DoesNotContain(reads, read => read.Offset == ticket.Offset && read.Length == ticket.Length);
        }
        Assert.All(file.Rents, requested => Assert.InRange(requested, 1, 3093));
    }

    [Fact]
    public void CaptureReadsAndConfirmsItsCurrentHeadBeforePublishingTheName() {
        using var fixture = new TagPublicationFaultFixture();
        HeadEntryId selected = fixture.Store.ReadHead(fixture.SeedHead()).Unwrap().Id;
        var source = fixture.Heads.Opened[selected.HeadId.GetLocalId(fixture.Identity)];
        int beforeFlush = source.FlushCalls;
        int beforeAppend = source.AppendCalls;
        fixture.SeedBucket();
        fixture.Events.Clear();
        HeadSetTag tag = fixture.Store.CreateTag(TagPublicationFaultFixture.Name, [selected.HeadId], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(selected, tag.Entries[selected.HeadId]);
        Assert.Equal(beforeFlush + 1, source.FlushCalls);
        Assert.Equal(beforeAppend, source.AppendCalls);
        Assert.Contains($"read:{selected.HeadId.GetLocalId(fixture.Identity)}", fixture.Events);
        Assert.Contains($"flush:{selected.HeadId.GetLocalId(fixture.Identity)}", fixture.Events);
        Assert.True(fixture.Events.IndexOf("data:confirm") < fixture.Events.IndexOf($"flush:{selected.HeadId.GetLocalId(fixture.Identity)}"));
        PublicHeadSetTagTests.AssertInfo(fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap(), TagPublicationFaultFixture.Name, 1);
    }

    [Fact]
    public void DefaultForeignAndDuplicateHeadsRejectBeforePathsClockDataOrOutput() {
        using var fixture = new TagPublicationFaultFixture();
        HeadId foreign = HeadIdTestExtensions.ForeignHeadId(fixture.Identity);
        HeadId local = fixture.SeedHead();
        HeadId[][] invalid = [[], [default], [foreign], [local, local]];
        fixture.Events.Clear();
        foreach (HeadId[] heads in invalid) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(fixture.Store.CreateTag(TagPublicationFaultFixture.Name, heads, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Empty(fixture.Events);
        }
        Assert.Equal(0, fixture.Tags.ResolveCalls);
        Assert.Equal(0, fixture.Tags.OpenCalls);
        Assert.Equal(0, fixture.Heads.PendingCreateCalls);
        Assert.Equal(0, fixture.Heads.OpenCalls);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.BucketPath)));
        Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("overlong")]
    public void InfoAndMutationShareNameAdmissionBeforeAnyPathOrBarrier(string? name) {
        using var fixture = new TagPublicationFaultFixture();
        HeadEntryId source = fixture.Store.ReadHead(fixture.SeedHead()).Unwrap().Id;
        fixture.Events.Clear();
        if (name == "overlong") { name = new string('a', HeadStore.MaxTagNameCodeUnits + 1); }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Equal("HeadStore.InvalidTagName", fixture.Store.ReadTagInfo(name!).Error!.ErrorCode);
        Assert.Equal("HeadStore.InvalidTagName", fixture.Store.CreateTag(name!, [source.HeadId], out outcome).Error!.ErrorCode);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Tags.ResolveCalls);
        Assert.Equal(0, fixture.Tags.OpenCalls);
        Assert.False(File.Exists(fixture.PendingPath));
    }
}
