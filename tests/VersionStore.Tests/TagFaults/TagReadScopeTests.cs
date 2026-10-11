using Atelia.Data;
using Atelia.VersionStore.Internal.Tags;
using Xunit;

namespace Atelia.VersionStore.Tests.TagFaults;

public sealed class TagReadScopeTests {
    [Fact]
    public void NamedScanReadsOnlySmallMetadataAndResolveReadsOnlyTheUniqueTargetCompletely() {
        byte[] root = TagFaultFixture.Payload(16384, 11);
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(32768), TagFaultFixture.Metadata(TagFaultFixture.OtherName)),
            new TagFaultRecord(root, TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.Equal(1, lookup.MatchCount);
        Assert.False(lookup.UnknownName);
        Assert.Equal(TagFaultFixture.TargetName, lookup.GetUniqueMetadata().Unwrap().Name);
        Assert.DoesNotContain(f.ObservedReads, read => !read.Raw && IsComplete(read, f.Records.ToArray()));
        Assert.Equal(new[] { 20, 20 }, f.Rents);
        Assert.All(f.ObservedReads.Where(read => !read.Raw), read => Assert.True(read.Length <= 24));
        Assert.True(f.File.TailOffset > f.Records[^1].EndOffsetExclusive); // Reader still borrows the live file.
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records.ToArray()));
        Assert.Contains(f.LoggedReads(), read => IsMetadata(read, f.Records[0], 32768, 20));
        Assert.Contains(f.LoggedReads(), read => IsMetadata(read, f.Records[1], 16384, 20));

        f.Open();
        header = f.CheckHeader();
        f.BeginObservation();
        RootPayload result = TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.Equal(root, result.Bytes.ToArray());
        Assert.Equal(new[] { 20, 20, f.Records[1].Length }, f.Rents);
        f.Close();
        TagReadRequest[] reads = f.LoggedReads();
        Assert.DoesNotContain(reads, read => IsComplete(read, f.Records[0]));
        Assert.Single(reads, read => IsComplete(read, f.Records[1]));
        Assert.Equal(root, result.Bytes.ToArray());
    }

    [Fact]
    public void TrustedOtherNameWithCorruptRootDoesNotBlockAHealthyTargetOrTriggerACompleteRead() {
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(TagFaultFixture.OtherName)),
            new TagFaultRecord(TagFaultFixture.Payload(1024, 3), TagFaultFixture.Metadata()));
        // The healthy successor keeps factory EOF qualification separate from the bad root.
        f.FlipClosedByte(f.Records[0].Offset + 4);
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.False(lookup.UnknownName);
        Assert.Equal(1, lookup.MatchCount);
        Assert.True(TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).IsSuccess);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records[0]));
        Assert.Single(f.LoggedReads(), read => IsComplete(read, f.Records[1]));
    }

    [Fact]
    public void TrustedTargetWithCorruptRootStillOccupiesItsNameAndOnlyResolveReportsItsCrcFailure() {
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()),
            new TagFaultRecord(TagFaultFixture.Payload(128), TagFaultFixture.Metadata(TagFaultFixture.OtherName)));
        f.FlipClosedByte(f.Records[0].Offset + 4);
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        Assert.Equal(TagFaultFixture.TargetName,
            TagRecordReader.ReadNamedMetadata(f.File, header, TagFaultFixture.TargetName).Unwrap().Name);
        Assert.DoesNotContain(f.ObservedReads, read => !read.Raw && IsComplete(read, f.Records.ToArray()));
        var result = TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        f.Close();
        Assert.Single(f.LoggedReads(), read => IsComplete(read, f.Records[0]));
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records[1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    public void TrustedOtherNameWithInvalidRootLengthStillAllowsHealthyMissing(int payloadLength) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(payloadLength),
            TagFaultFixture.Metadata(TagFaultFixture.OtherName)));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.False(lookup.UnknownName);
        Assert.Equal(0, lookup.MatchCount);
        Assert.Equal("VersionStore.TagNotFound", lookup.GetUniqueMetadata().Error!.ErrorCode);
        Assert.Equal(new[] { 20 }, f.Rents);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records[0]));
        Assert.Contains(f.LoggedReads(), read => IsMetadata(read, f.Records[0], payloadLength, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    public void InvalidRootLengthDoesNotSkipTrustedMetadataOrRentACompleteFrame(int payloadLength) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(payloadLength), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        Assert.Equal(TagFaultFixture.TargetName,
            TagRecordReader.ReadNamedMetadata(f.File, header, TagFaultFixture.TargetName).Unwrap().Name);
        Assert.Equal("VersionStore.InvalidTagRoot",
            TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).Error!.ErrorCode);
        Assert.Equal(new[] { 20, 20 }, f.Rents);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records[0]));
        Assert.Equal(2, f.LoggedReads().Count(read => IsMetadata(read, f.Records[0], payloadLength, 20)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(3105)]
    public void MetadataShapeOutsideBoundsSetsUnknownNameBeforeAnyMetadataRentOrRead(int metadataLength) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), new byte[metadataLength]));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var result = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.True(result.UnknownName);
        Assert.Equal(0, result.MatchCount);
        Assert.Equal("VersionStore.TagLookupUncertain", result.GetUniqueMetadata().Error!.ErrorCode);
        Assert.Empty(f.Rents);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records[0]));
        Assert.DoesNotContain(f.LoggedReads(), read => IsMetadata(read, f.Records[0], 4096, metadataLength));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void UnknownRecordKindSetsUnknownNameWithoutReadingMetadata(uint tag) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(), tag));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        Assert.True(TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap().UnknownName);
        Assert.Empty(f.Rents);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsMetadata(read, f.Records[0], 4096, 20));
    }

    [Fact]
    public void DuplicateTargetCompletesTheMetadataScanWithoutRentingEitherRoot() {
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        Assert.Equal("VersionStore.DuplicateTagName",
            TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).Error!.ErrorCode);
        Assert.Equal(new[] { 20, 20 }, f.Rents);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => IsComplete(read, f.Records.ToArray()));
    }

    private static bool IsMetadata(TagReadRequest read, SizedPtr ticket, int payloadLength, int metadataLength) =>
        read.Offset == ticket.Offset + 4 + payloadLength && read.Length == metadataLength;
    private static bool IsComplete(TagReadRequest read, params SizedPtr[] tickets) =>
        tickets.Any(ticket => read.Offset == ticket.Offset && read.Length == ticket.Length);
}
