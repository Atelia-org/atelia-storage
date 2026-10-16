using Atelia.Rbf;
using Atelia.Data.Hashing;
using Atelia.HeadStore.Internal.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagFaults;

public sealed class TagErrorPropagationTests {
    [Fact]
    public void HeaderOnlyBucketEndsNormallyAndReportsHealthyMissingWithoutMetadataReads() {
        using var f = new TagFaultFixture();
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.Equal(0, lookup.MatchCount);
        Assert.False(lookup.UnknownName);
        Assert.Equal("HeadStore.TagNotFound", lookup.GetUniqueMetadata().Error!.ErrorCode);
        Assert.Empty(f.Rents);
        f.Close();
        Assert.All(f.LoggedReads(), read => Assert.True(read.Length <= 24));
    }

    [Fact]
    public void BadMetadataBufferIsLocalUnknownNameButDoesNotHideTheUniqueHealthyTarget() {
        byte[] corrupt = TagFaultFixture.Metadata(TagFaultFixture.OtherName);
        corrupt[^1] ^= 1;
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), corrupt),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.True(lookup.UnknownName);
        Assert.Equal(1, lookup.MatchCount);
        Assert.Equal(TagFaultFixture.TargetName, lookup.GetUniqueMetadata().Unwrap().Name);
        Assert.True(TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).IsSuccess);
        Assert.Equal("HeadStore.TagLookupUncertain",
            TagRecordReader.ReadNamedMetadata(f.File, header, TagFaultFixture.OtherName).Error!.ErrorCode);
    }

    [Fact]
    public void CodecTruncationInsideACompletelyReadMetadataBufferIsLocalUnknownName() {
        byte[] truncated = new byte[15];
        truncated[9] = 13; // UTF-8 BPV declares six name bytes; only one precedes the CRC.
        truncated[10] = (byte)'t';
        RollingCrc.SealCodewordForward(truncated);
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), truncated));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        var lookup = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap();
        Assert.True(lookup.UnknownName);
        Assert.Equal("HeadStore.TagLookupUncertain", lookup.GetUniqueMetadata().Error!.ErrorCode);
        f.Close();
        Assert.Contains(f.LoggedReads(), read => read.Offset == f.Records[0].Offset + 4 + 4096
            && read.Length == 15 && read.Returned == 15);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("trailer")]
    [InlineData("fence")]
    public void FramingTerminationErrorAfterAnUnknownRecordAndAUniqueHitCannotBecomeSuccess(string region) {
        byte[] badMetadata = TagFaultFixture.Metadata(TagFaultFixture.OtherName);
        badMetadata[^1] ^= 1;
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), badMetadata),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(TagFaultFixture.OtherName)),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(TagFaultFixture.OtherName)),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(TagFaultFixture.OtherName)));
        // The last frame stays healthy so this test reaches the forward scanner after factory/header qualification.
        long damaged = region switch {
            "head" => f.Records[2].Offset,
            "trailer" => f.Records[2].EndOffsetExclusive - 8,
            _ => f.Records[2].EndOffsetExclusive
        };
        f.FlipClosedByte(damaged);
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        AteliaError? expected = ForwardTermination(f.File);
        Assert.NotNull(expected);
        f.BeginObservation();
        var scan = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName);
        Assert.True(scan.IsFailure);
        Assert.Equal(expected, scan.Error);
        Assert.Equal(expected, TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).Error);
        f.Close();
        Assert.DoesNotContain(f.LoggedReads(), read => read.Offset == f.Records[1].Offset
            && read.Length == f.Records[1].Length);
    }

    [Fact]
    public void HeaderFramingTerminationCannotBeSkippedToALaterHeaderOrRecord() {
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata(TagFaultFixture.OtherName)));
        f.FlipClosedByte(f.HeaderTicket.Offset);
        f.Open();
        AteliaError? expected = ForwardTermination(f.File);
        Assert.NotNull(expected);
        Assert.Equal(expected, TagRecordReader.CheckHeader(f.File, f.Identity, TagFaultFixture.BucketId).Error);
    }

    [Fact]
    public void ActualMetadataShortReadIsAnRbfReadFailureRatherThanUnknownName() {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open(writable: true);
        CheckedTagBucketHeader header = f.CheckHeader();
        long metaOffset = f.Records[0].Offset + 4 + 4096;
        bool injected = false;
        f.BeginObservation();
        f.BeforeRead = (offset, length, raw) => {
            if (!raw && offset == metaOffset && length == 20 && !injected) {
                injected = true;
                f.TruncateOwnedFile(metaOffset + 7);
            }
        };
        var result = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName);
        Assert.True(injected);
        Assert.Equal("Rbf.ArgumentError", result.Error!.ErrorCode);
        Assert.Contains("Short read for TailMeta", result.Error.Message);
        f.Close();
        Assert.Contains(f.LoggedReads(), read => read.Offset == metaOffset && read.Length == 20 && read.Returned == 7);
    }

    [Fact]
    public void ShortReadAtTheFramingReaderBecomesTerminationErrorAndCannotBecomeHealthyMissing() {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open(writable: true);
        CheckedTagBucketHeader header = f.CheckHeader();
        bool injected = false;
        f.BeforeRead = (offset, _, raw) => {
            if (!raw && offset == f.Records[0].Offset && !injected) {
                injected = true;
                f.TruncateOwnedFile(offset + 2);
            }
        };
        var result = TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName);
        Assert.True(injected);
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
    }

    [Fact]
    public void ActualCompleteFrameShortReadAfterUniqueMetadataQualificationRemainsAnRbfFailure() {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open(writable: true);
        CheckedTagBucketHeader header = f.CheckHeader();
        bool injected = false;
        f.BeginObservation();
        f.BeforeRead = (offset, length, raw) => {
            if (!raw && offset == f.Records[0].Offset && length == f.Records[0].Length && !injected) {
                injected = true;
                f.TruncateOwnedFile(offset + 64);
            }
        };
        var result = TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName);
        Assert.True(injected);
        Assert.Equal("Rbf.ArgumentError", result.Error!.ErrorCode);
        Assert.Contains("Short read", result.Error.Message);
        f.Close();
        Assert.Contains(f.LoggedReads(), read => read.Offset == f.Records[0].Offset
            && read.Length == f.Records[0].Length && read.Returned == 64);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("oom")]
    [InlineData("eof-exception")]
    public void ExceptionAtActualMetadataReadPropagatesAfterLocalUnknownName(string kind) {
        byte[] badMetadata = TagFaultFixture.Metadata(TagFaultFixture.OtherName);
        badMetadata[^1] ^= 1;
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), badMetadata),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        Exception primary = kind switch {
            "io" => new IOException("actual meta read"),
            "eof-exception" => new EndOfStreamException("actual meta read"),
            _ => new OutOfMemoryException("actual meta read")
        };
        long targetMeta = f.Records[1].Offset + 4 + 4096;
        f.BeforeRead = (offset, length, raw) => {
            if (raw && offset == targetMeta && length == 20) { throw primary; }
        };
        Assert.Same(primary, Assert.ThrowsAny<Exception>(() =>
            TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName)));
        // Propagating the injected read exception does not give this internal reader ownership of the borrower.
        f.BeforeRead = null;
        Assert.Equal(1, TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName).Unwrap().MatchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OomAtActualMetadataOrCompleteFrameRentCannotBeClassifiedAsBadMetadata(bool fullFrame) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        var primary = new OutOfMemoryException(fullFrame ? "full frame rent" : "meta rent");
        f.BeforeRent = length => {
            if (length == (fullFrame ? f.Records[0].Length : 20)) { throw primary; }
        };
        Assert.Same(primary, Assert.Throws<OutOfMemoryException>(() =>
            TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName)));
        f.BeforeRent = null;
        Assert.True(TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName).IsSuccess);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("oom")]
    public void ExceptionAtTheActualCompleteReadPropagatesAfterUniqueMetadataQualification(string kind) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        Exception primary = kind == "io" ? new IOException("actual full read") : new OutOfMemoryException("actual full read");
        f.BeforeRead = (offset, length, raw) => {
            if (raw && offset == f.Records[0].Offset && length == f.Records[0].Length) { throw primary; }
        };
        Assert.Same(primary, Assert.ThrowsAny<Exception>(() =>
            TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName)));
        f.BeforeRead = null;
        Assert.True(TagRecordReader.ReadNamedMetadata(f.File, header, TagFaultFixture.TargetName).IsSuccess);
    }

    [Fact]
    public void ActiveBuilderAndDisposedBorrowerRejectAtTheRealScanEntryWithoutReads() {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open(writable: true);
        CheckedTagBucketHeader header = f.CheckHeader();
        f.BeginObservation();
        using (RbfFrameBuilder builder = f.File.BeginAppend(1, 0, out _)) {
            Assert.Throws<InvalidOperationException>(() => TagRecordReader.ScanNamed(f.File, header, TagFaultFixture.TargetName));
            Assert.Empty(f.ObservedReads);
        }
        IRbfFile borrowed = f.File;
        f.Close();
        Assert.Throws<ObjectDisposedException>(() => TagRecordReader.ScanNamed(borrowed, header, TagFaultFixture.TargetName));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("disposed")]
    [InlineData("fault")]
    public void StateGuardExceptionIsPreservedEvenAfterARecordWasLocallyIsolated(string kind) {
        byte[] corrupt = TagFaultFixture.Metadata(TagFaultFixture.OtherName);
        corrupt[^1] ^= 1;
        using var f = new TagFaultFixture(
            new TagFaultRecord(TagFaultFixture.Payload(), corrupt),
            new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        Exception primary = kind switch {
            "cancel" => new OperationCanceledException("owner state"),
            "disposed" => new ObjectDisposedException("owner state"),
            _ => new InvalidOperationException("owner state")
        };
        bool sawNextRecord = false;
        f.BeforeRead = (offset, length, raw) => {
            // Reaching the next head proves the first completed metadata was decoded and isolated.
            if (raw && offset == f.Records[1].Offset && length == 4) { sawNextRecord = true; }
        };
        Assert.Same(primary, Assert.ThrowsAny<Exception>(() => TagRecordReader.ScanNamed(f.File, header,
            TagFaultFixture.TargetName, () => { if (sawNextRecord) { throw primary; } })));
        Assert.True(sawNextRecord);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("resolve")]
    public void GuardAfterContentReadPreventsQualifiedResult(string operation) {
        using var f = new TagFaultFixture(new TagFaultRecord(TagFaultFixture.Payload(), TagFaultFixture.Metadata()));
        f.Open();
        CheckedTagBucketHeader header = f.CheckHeader();
        var primary = new ObjectDisposedException("owner after last IO");
        bool completedLastRead = false;
        f.BeforeRead = (offset, length, raw) => {
            if (!raw) { return; }
            if (operation == "resolve" && offset == f.Records[0].Offset && length == f.Records[0].Length) {
                completedLastRead = true;
            }
            else if (operation == "metadata" && offset == f.Records[0].Offset + 4 + 4096 && length == 20) {
                completedLastRead = true;
            }
        };
        Action guard = () => { if (completedLastRead) { throw primary; } };
        if (operation == "resolve") {
            Assert.Same(primary, Assert.Throws<ObjectDisposedException>(() =>
                TagRecordReader.ResolveNamed(f.File, header, TagFaultFixture.TargetName, guard)));
        }
        else {
            Assert.Same(primary, Assert.Throws<ObjectDisposedException>(() =>
                TagRecordReader.ReadNamedMetadata(f.File, header, TagFaultFixture.TargetName, guard)));
        }
    }

    private static AteliaError? ForwardTermination(IRbfFile file) {
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        while (forward.MoveNext()) { }
        return forward.TerminationError;
    }
}
