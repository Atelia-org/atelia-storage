using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.Tags;

public sealed class TagRecordCapacityTests {
    [Fact]
    public void AppendCapacityUsesPublicRbfMeasureAndAllowsLastLegalStart() {
        var minimal = TagRecordCodec.MeasureAppend(16, 18, 60).Unwrap();
        Assert.Equal(RbfFile.MeasureWriteSize(16, 18).Unwrap(), minimal);
        int count = (RbfFile.MaxPayloadAndMetaLength - 3093) / 16;
        int length = TagRecordCodec.MeasurePayloadLength(count, 3093).Unwrap();
        var maximum = TagRecordCodec.MeasureAppend(length, 3093, SizedPtr.MaxOffset).Unwrap();
        Assert.Equal(RbfFile.MeasureWriteSize(length, 3093).Unwrap(), maximum);
        Assert.True(SizedPtr.Create(SizedPtr.MaxOffset, maximum.FrameLength).EndOffsetExclusive > SizedPtr.MaxOffset);
        Assert.True(TagRecordCodec.MeasurePayloadLength(count + 1, 3093).IsFailure);
    }

    [Theory]
    [InlineData(0, 18, 60L)]
    [InlineData(12, 18, 60L)]
    [InlineData(13, 18, 60L)]
    [InlineData(16, 17, 60L)]
    [InlineData(16, 3094, 60L)]
    [InlineData(16, 18, -4L)]
    [InlineData(16, 18, 61L)]
    [InlineData(16, 18, 1099511627776L)]
    public void InvalidCapacityOrActualStartCannotBecomeAnAppendPlan(int length, int metadataLength, long offset) {
        Assert.Equal("HeadStore.TagCapacityExceeded", TagRecordCodec.MeasureAppend(length, metadataLength, offset).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(32)]
    public void IdentityMetadataShapeDoesNotDependOnMembersShape(int length) {
        using var fixture = new TagBucketFixture();
        using var file = fixture.Create(0x351);
        SizedPtr ticket = TagBucketFixture.Append(file, "A", new byte[length]);
        RbfFrameInfo info = file.ReadFrameInfo(ticket).Unwrap();
        Assert.True(TagRecordCodec.IsMetadataShape(info));
        Assert.Equal(length > 0 && length % 16 == 0, TagRecordCodec.IsEntriesShape(info));
    }
}
