using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfWriteSizeTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-write-size-{Guid.NewGuid():N}.rbf");

    public void Dispose() => File.Delete(_path);

    // Literal RBF3 wire vectors stay independent of the production layout calculation.
    [Theory]
    [InlineData(0, 0, 28, 32)]
    [InlineData(1, 0, 32, 36)]
    [InlineData(2, 0, 32, 36)]
    [InlineData(3, 0, 32, 36)]
    [InlineData(4, 0, 32, 36)]
    [InlineData(5, 0, 36, 40)]
    [InlineData(0, 1, 32, 36)]
    [InlineData(2, 3, 36, 40)]
    [InlineData(0, 65535, 65564, 65568)]
    [InlineData(1, 65535, 65564, 65568)]
    [InlineData(2, 65535, 65568, 65572)]
    [InlineData(268435424, 0, 268435452, 268435456)]
    [InlineData(268369889, 65535, 268435452, 268435456)]
    public void MeasureWriteSize_MatchesIndependentFormatVectors(int payloadLength, int metaLength, int frameLength, int appendLength) {
        var result = RbfFile.MeasureWriteSize(payloadLength, metaLength);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        RbfWriteSize size = result.Unwrap();
        Assert.Equal(frameLength, size.FrameLength);
        Assert.Equal(appendLength, size.AppendLength);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, -1)]
    [InlineData(0, int.MinValue)]
    [InlineData(0, 65536)]
    [InlineData(0, int.MaxValue)]
    [InlineData(268435425, 0)]
    [InlineData(268369890, 65535)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(int.MaxValue, 65535)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void MeasureWriteSize_InvalidLengthsReturnArgumentError(int payloadLength, int metaLength) {
        var result = RbfFile.MeasureWriteSize(payloadLength, metaLength);

        Assert.True(result.IsFailure);
        Assert.IsType<RbfArgumentError>(result.Error);
        Assert.Equal("Rbf.ArgumentError", result.Error!.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.RecoveryHint));
        if (payloadLength >= 0 && (metaLength < 0 || metaLength > RbfFile.MaxTailMetaLength)) {
            Assert.Contains("tailMetaLength", result.Error.Message);
            Assert.Contains("MaxTailMetaLength", result.Error.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void MeasureWriteSize_MaximumCombinedLengthIsAcceptedAndNextByteIsRejected(int metaLength) {
        int payloadLength = RbfFile.MaxPayloadAndMetaLength - metaLength;

        RbfWriteSize size = RbfFile.MeasureWriteSize(payloadLength, metaLength).Unwrap();

        Assert.Equal(268435424, RbfFile.MaxPayloadAndMetaLength);
        Assert.Equal(SizedPtr.MaxLength, size.FrameLength);
        Assert.Equal(268435456, size.AppendLength);
        Assert.IsType<RbfArgumentError>(RbfFile.MeasureWriteSize(payloadLength + 1, metaLength).Error);
    }

    [Fact]
    public void SharedLayoutValidation_PreservesLegacyCapacityWithoutExposingLegacyMeasure() {
        var legacy = FrameLayout.TryCreate(RbfProfile.Rbf1, 268435428, 0);

        Assert.True(legacy.IsSuccess);
        Assert.Equal(268435452, legacy.Value.FrameLength);
        Assert.IsType<RbfArgumentError>(FrameLayout.TryCreate(RbfProfile.Rbf1, 268435429, 0).Error);
        Assert.IsType<RbfArgumentError>(FrameLayout.TryCreate(RbfProfile.Rbf3, 268435428, 0).Error);
        Assert.IsType<RbfArgumentError>(RbfFile.MeasureWriteSize(268435428).Error);
    }

    // The budget includes the closing Fence, unlike SizedPtr.Length.
    [Theory]
    [InlineData(0L, 0, false, 0)]
    [InlineData(27L, 0, false, 0)]
    [InlineData(28L, 0, false, 0)]
    [InlineData(31L, 0, false, 0)]
    [InlineData(32L, 0, true, 0)]
    [InlineData(35L, 0, true, 0)]
    [InlineData(35L, 1, false, 0)]
    [InlineData(36L, 0, true, 4)]
    [InlineData(36L, 1, true, 3)]
    [InlineData(36L, 4, true, 0)]
    [InlineData(65568L, 65535, true, 1)]
    [InlineData(65567L, 65535, false, 0)]
    [InlineData(268435455L, 0, true, 268435420)]
    [InlineData(268435456L, 0, true, 268435424)]
    [InlineData(268435457L, 0, true, 268435424)]
    [InlineData(long.MaxValue, 0, true, 268435424)]
    [InlineData(long.MaxValue, 65535, true, 268369889)]
    public void AppendBudget_MatchesIndependentFormatVectors(long byteBudget, int metaLength, bool expectedFit, int expectedPayloadLength) {
        int payloadLength = -123;

        bool fits = RbfFile.TryGetMaxPayloadLengthForAppendBudget(byteBudget, metaLength, out payloadLength);

        Assert.Equal(expectedFit, fits);
        Assert.Equal(expectedPayloadLength, payloadLength);
        AssertBudgetMaximum(byteBudget, metaLength, fits, payloadLength);
    }

    [Theory]
    [InlineData(-1L, 0, "byteBudget")]
    [InlineData(long.MinValue, 0, "byteBudget")]
    [InlineData(0L, -1, "tailMetaLength")]
    [InlineData(long.MaxValue, int.MinValue, "tailMetaLength")]
    [InlineData(0L, 65536, "tailMetaLength")]
    [InlineData(long.MaxValue, int.MaxValue, "tailMetaLength")]
    public void AppendBudget_InvalidArgumentsThrowInsteadOfReportingInsufficientBudget(long byteBudget, int metaLength, string parameterName) {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RbfFile.TryGetMaxPayloadLengthForAppendBudget(byteBudget, metaLength, out _));

        Assert.Equal(parameterName, error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(65535)]
    public void AppendBudget_ResultIsMaximalAcrossAlignmentAndCapacityBoundaries(int metaLength) {
        int minimum = RbfFile.MeasureWriteSize(0, metaLength).Unwrap().AppendLength;
        int maximum = RbfFile.MeasureWriteSize(RbfFile.MaxPayloadAndMetaLength - metaLength, metaLength).Unwrap().AppendLength;
        for (long byteBudget = minimum - 4L; byteBudget <= minimum + 32L; byteBudget++) {
            bool fits = RbfFile.TryGetMaxPayloadLengthForAppendBudget(byteBudget, metaLength, out int payloadLength);
            AssertBudgetMaximum(byteBudget, metaLength, fits, payloadLength);
        }
        for (long byteBudget = maximum - 8L; byteBudget <= maximum + 8L; byteBudget++) {
            bool fits = RbfFile.TryGetMaxPayloadLengthForAppendBudget(byteBudget, metaLength, out int payloadLength);
            AssertBudgetMaximum(byteBudget, metaLength, fits, payloadLength);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(8195, 3)]
    [InlineData(1, 65535)]
    public void MeasureWriteSize_MatchesPublicAppendTicketAndTailAdvance(int payloadLength, int metaLength) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        // An existing prefix proves that AppendLength excludes Header and previous frames.
        file.Append(1, new byte[] { 7 }).Unwrap();
        long start = file.TailOffset;
        RbfWriteSize size = RbfFile.MeasureWriteSize(payloadLength, metaLength).Unwrap();

        SizedPtr ticket = file.Append(2, new byte[payloadLength], new byte[metaLength]).Unwrap();

        Assert.Equal(start, ticket.Offset);
        Assert.Equal(size.FrameLength, ticket.Length);
        Assert.Equal(start + size.AppendLength, file.TailOffset);
        Assert.Equal(file.TailOffset, new FileInfo(_path).Length);
        var info = file.ReadFrameInfo(ticket).Unwrap();
        Assert.Equal(payloadLength, info.PayloadLength);
        Assert.Equal(metaLength, info.TailMetaLength);
    }

    [Fact]
    public void DefaultWriteSize_IsZeroAndDiffersFromMeasuredEmptyFrame() {
        RbfWriteSize size = default;

        Assert.Equal(0, size.FrameLength);
        Assert.Equal(0, size.AppendLength);
        RbfWriteSize emptyFrame = RbfFile.MeasureWriteSize(0).Unwrap();
        Assert.Equal(28, emptyFrame.FrameLength);
        Assert.Equal(32, emptyFrame.AppendLength);
    }

    private static void AssertBudgetMaximum(long byteBudget, int metaLength, bool fits, int payloadLength) {
        if (!fits) {
            Assert.Equal(0, payloadLength);
            Assert.True(RbfFile.MeasureWriteSize(0, metaLength).Unwrap().AppendLength > byteBudget);
            return;
        }

        Assert.InRange(payloadLength, 0, RbfFile.MaxPayloadAndMetaLength - metaLength);
        RbfWriteSize size = RbfFile.MeasureWriteSize(payloadLength, metaLength).Unwrap();
        Assert.True(size.AppendLength <= byteBudget);
        if (payloadLength < RbfFile.MaxPayloadAndMetaLength - metaLength) {
            Assert.True(RbfFile.MeasureWriteSize(payloadLength + 1, metaLength).Unwrap().AppendLength > byteBudget);
        }
    }
}
