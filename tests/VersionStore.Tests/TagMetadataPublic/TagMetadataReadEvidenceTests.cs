using System.Globalization;
using System.Reflection;
using Atelia.Data;
using Atelia.VersionStore.Tests.TagFaults;
using Atelia.VersionStore.Tests.TagPublicationFaults;
using Xunit;

namespace Atelia.VersionStore.Tests.TagMetadataPublic;

/// <summary>生产目录 backend 和真实 RBF 读取日志的窄接线证据；公开工厂闭环另由 Public 测试验证。</summary>
public sealed class TagMetadataReadEvidenceTests {
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1048577)]
    [InlineData(true, 0)]
    [InlineData(true, 1048577)]
    public void InfoAndScanOnlyReadSmallMetadataEvenWhenTheOpaqueRootIsOutsideItsEnvelope(bool scan, int rootLength) {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        var record = new TagFaultRecord(new byte[rootLength], TagFaultFixture.Metadata());
        TagBucketSeed seed = fixture.SeedBucket(record);

        if (scan) {
            Assert.Equal(TagScanEnd.Complete, fixture.Store.ScanTags(info => {
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
        Assert.All(file.Rents, requested => Assert.InRange(requested, 1, 3104));
        Assert.Equal(0, file.AppendCalls);
        Assert.Equal(0, file.FlushCalls);
        Assert.DoesNotContain("data:confirm", fixture.Events);
    }

    [Fact]
    public void ScanActuallyReadsEverySmallMetaAndNeverFullyReadsAnyRecordRoot() {
        using var fixture = new TagPublicationFaultFixture(readOnly: true);
        TagBucketSeed seed = fixture.SeedBucket(
            TagPublicationFaultFixture.Record(payload: TagFaultFixture.Payload(64 * 1024)),
            TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName, TagFaultFixture.Payload(128 * 1024)));
        int callbacks = 0;

        Assert.Equal(TagScanEnd.Complete, fixture.Store.ScanTags(_ => { callbacks++; return true; }).Unwrap());

        Assert.Equal(2, callbacks);
        TagPublicationFile file = Assert.Single(fixture.Tags.Opened);
        var reads = file.LoggedReads();
        for (int index = 0; index < seed.Tickets.Length; index++) {
            SizedPtr ticket = seed.Tickets[index];
            TagFaultRecord record = seed.Records[index];
            Assert.Contains(reads, read => read.Offset == ticket.Offset + 4 + record.Payload.Length && read.Length == record.Metadata.Length);
            Assert.DoesNotContain(reads, read => read.Offset == ticket.Offset && read.Length == ticket.Length);
        }
        Assert.All(file.Rents, requested => Assert.InRange(requested, 1, 3104));
    }

    [Fact]
    public void RelatedAnnotationDoesNotReadOrFlushItsAlreadyOpenedSourceAndUsesActualInputBytes() {
        using var fixture = new TagPublicationFaultFixture();
        RefRevision related = fixture.Store.ReadRef(fixture.SeedRef()).Unwrap().Revision;
        var source = fixture.Refs.Opened[related.RefId.LocalId];
        string log = Path.Combine(fixture.Storage.BasePath, "related-source-reads.csv");
        source.SetupReadLog(log);
        var sourceReads = new List<(long Offset, int Length)>();
        const BindingFlags diagnosticMembers = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object reader = source.Inner.GetType().GetField("_reader", diagnosticMembers)!.GetValue(source.Inner)!;
        reader.GetType().GetProperty("ReadObserver", diagnosticMembers)!.SetValue(reader,
            (Action<long, int, bool>)((offset, length, raw) => {
                if (!raw) { sourceReads.Add((offset, length)); }
            }));
        // SetupReadLog is applied on the next actual read. Establish a nonempty real baseline,
        // then keep the same reader/logger active throughout all tag operations.
        using (var baseline = source.ReadPooledFrame(related.Ticket).Unwrap()) {
            Assert.Equal(related.Ticket, baseline.Ticket);
        }
        Assert.NotEmpty(sourceReads);
        Assert.Contains(sourceReads, read => read.Offset == related.Ticket.Offset && read.Length == related.Ticket.Length);
        var baselineReads = sourceReads.ToArray();
        int beforeFlush = source.FlushCalls;
        int beforeAppend = source.AppendCalls;
        fixture.Events.Clear();
        fixture.SeedBucket();
        byte[] input = [0xff, 0x81, 0, 0x57];

        Assert.True(fixture.Store.CreateTag(TagPublicationFaultFixture.Name, input, related, out var outcome).Unwrap());

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(beforeFlush, source.FlushCalls);
        Assert.Equal(beforeAppend, source.AppendCalls);
        Assert.DoesNotContain($"read:{related.RefId.LocalId}", fixture.Events);
        Assert.DoesNotContain($"flush:{related.RefId.LocalId}", fixture.Events);
        PublicRelatedTagTests.AssertInfo(fixture.Store.ReadTagInfo(TagPublicationFaultFixture.Name).Unwrap(), TagPublicationFaultFixture.Name, related);
        Assert.Equal(input, fixture.Store.ResolveTag(TagPublicationFaultFixture.Name).Unwrap().Bytes.ToArray());
        Assert.Equal(baselineReads, sourceReads.ToArray());
        fixture.Store.Dispose();
        var loggedReads = File.ReadAllLines(log)
            .Where(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal))
            .Select(line => line.Split(','))
            .Select(fields => (Offset: long.Parse(fields[1], CultureInfo.InvariantCulture),
                Length: int.Parse(fields[2], CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(baselineReads, loggedReads);
    }

    [Fact]
    public void DefaultForeignAndStructurallyInvalidRelatedValuesRejectBeforePathsClockDataOrOutput() {
        using var fixture = new TagPublicationFaultFixture();
        using var foreignFixture = new TagPublicationFaultFixture();
        RefRevision foreign = foreignFixture.Store.ReadRef(foreignFixture.SeedRef()).Unwrap().Revision;
        RefId local = fixture.SeedRef();
        RefRevision[] invalid = [default, foreign, RefRevision.Create(local, default),
            RefRevision.Create(local, SizedPtr.Create(60, 0)), RefRevision.Create(default, SizedPtr.Create(60, 40))];
        fixture.Events.Clear();
        foreach (RefRevision related in invalid) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.ArgumentError",
                fixture.Store.CreateTag(TagPublicationFaultFixture.Name, [1], related, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Empty(fixture.Events);
        }
        Assert.Equal(0, fixture.Tags.ResolveCalls);
        Assert.Equal(0, fixture.Tags.OpenCalls);
        Assert.Equal(0, fixture.Refs.PendingCreateCalls);
        Assert.Equal(0, fixture.Refs.OpenCalls);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.BucketPath)));
        Assert.False(File.Exists(fixture.PendingPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("overlong")]
    public void InfoAndRelatedMutationShareNameAdmissionBeforeAnyPathOrBarrier(string? name) {
        using var fixture = new TagPublicationFaultFixture();
        RefRevision source = fixture.Store.ReadRef(fixture.SeedRef()).Unwrap().Revision;
        fixture.Events.Clear();
        if (name == "overlong") { name = new string('a', VersionStore.MaxTagNameCodeUnits + 1); }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Equal("VersionStore.InvalidTagName", fixture.Store.ReadTagInfo(name!).Error!.ErrorCode);
        Assert.Equal("VersionStore.InvalidTagName", fixture.Store.CreateTag(name!, [1], source, out outcome).Error!.ErrorCode);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Tags.ResolveCalls);
        Assert.Equal(0, fixture.Tags.OpenCalls);
        Assert.False(File.Exists(fixture.PendingPath));
    }
}
