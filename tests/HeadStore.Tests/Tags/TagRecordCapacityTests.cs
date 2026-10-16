using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class TagRecordCapacityTests {
    [Fact]
    public void AppendCapacityUsesPublicRbfMeasureAndAllowsLastLegalStart() {
        var minimal = TagRecordCodec.MeasureAppend(1, 15, 60).Unwrap();
        Assert.Equal(RbfFile.MeasureWriteSize(1, 15).Unwrap(), minimal);
        var maximum = TagRecordCodec.MeasureAppend(HeadStore.MaxHeadEntryPayloadLength, 3104, SizedPtr.MaxOffset).Unwrap();
        Assert.Equal(1051680, HeadStore.MaxHeadEntryPayloadLength + TagMetadataCodec.MaxEncodedSize);
        Assert.Equal(RbfFile.MeasureWriteSize(1048576, 3104).Unwrap(), maximum);
        Assert.True(SizedPtr.Create(SizedPtr.MaxOffset, maximum.FrameLength).EndOffsetExclusive > SizedPtr.MaxOffset);
    }

    [Theory]
    [InlineData(0, 15, 60L)]
    [InlineData(1048577, 15, 60L)]
    [InlineData(1, 14, 60L)]
    [InlineData(1, 3105, 60L)]
    [InlineData(1, 15, -4L)]
    [InlineData(1, 15, 61L)]
    [InlineData(1, 15, 1099511627776L)]
    public void InvalidCapacityOrActualStartCannotBecomeAnAppendPlan(int rootLength, int metadataLength, long offset) {
        Assert.Equal("HeadStore.TagCapacityExceeded", TagRecordCodec.MeasureAppend(rootLength, metadataLength, offset).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1048576)]
    [InlineData(1048577)]
    public void IdentityMetadataShapeDoesNotDependOnRootCapacity(int rootLength) {
        using var fixture = new TagBucketFixture();
        using var file = fixture.Create(0x351);
        SizedPtr ticket = TagBucketFixture.Append(file, "A", new byte[rootLength]);
        RbfFrameInfo info = file.ReadFrameInfo(ticket).Unwrap();
        Assert.True(TagRecordCodec.IsMetadataShape(info));
        Assert.Equal(rootLength is >= 1 and <= 1048576, TagRecordCodec.IsRootShape(info));
    }
}
