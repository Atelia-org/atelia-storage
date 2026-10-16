using System.Globalization;
using System.Reflection;
using Atelia.Data;
using Atelia.HeadStore.Tests.TagFaults;
using Atelia.HeadStore.Tests.TagPublicationFaults;
using Xunit;

namespace Atelia.HeadStore.Tests.TagMetadataPublic;

/// <summary>生产目录 backend 和真实 RBF 读取日志的窄接线证据；公开工厂闭环另由 Public 测试验证。</summary>
public sealed class TagMetadataReadEvidenceTests {
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
        var source = fixture.Heads.Opened[selected.HeadId.LocalId];
        int beforeFlush = source.FlushCalls;
        int beforeAppend = source.AppendCalls;
        fixture.SeedBucket();
        fixture.Events.Clear();
        HeadSetTag tag = fixture.Store.CreateTag(TagPublicationFaultFixture.Name, [selected.HeadId], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(selected, tag.Entries[selected.HeadId]);
        Assert.Equal(beforeFlush + 1, source.FlushCalls);
        Assert.Equal(beforeAppend, source.AppendCalls);
        Assert.Contains($"read:{selected.HeadId.LocalId}", fixture.Events);
        Assert.Contains($"flush:{selected.HeadId.LocalId}", fixture.Events);
        Assert.True(fixture.Events.IndexOf("data:confirm") < fixture.Events.IndexOf($"flush:{selected.HeadId.LocalId}"));
        PublicHeadSetTagTests.AssertInfo(fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap(), TagPublicationFaultFixture.Name, 1);
    }

    [Fact]
    public void DefaultForeignAndDuplicateHeadsRejectBeforePathsClockDataOrOutput() {
        using var fixture = new TagPublicationFaultFixture();
        using var foreignFixture = new TagPublicationFaultFixture();
        HeadId foreign = foreignFixture.SeedHead();
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
