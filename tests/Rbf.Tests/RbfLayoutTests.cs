using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfLayout 布局计算测试。
/// </summary>
/// <remarks>
/// 验证 [F-HEADLEN-FORMULA], [F-STATUSLEN-FORMULA], [F-FRAME-4B-ALIGNMENT]。
/// </remarks>
public class RbfLayoutTests
{
    #region StatusLen 计算测试 [F-STATUSLEN-FORMULA]

    /// <summary>
    /// [F-STATUSLEN-FORMULA]: PayloadLen % 4 == 0 → StatusLen = 4
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 4)]
    [InlineData(8, 4)]
    [InlineData(100, 4)]
    public void CalculateStatusLength_PayloadMod4Is0_Returns4(int payloadLength, int expectedStatusLen)
    {
        Assert.Equal(expectedStatusLen, RbfLayout.CalculateStatusLength(payloadLength));
    }

    /// <summary>
    /// [F-STATUSLEN-FORMULA]: PayloadLen % 4 == 1 → StatusLen = 3
    /// </summary>
    [Theory]
    [InlineData(1, 3)]
    [InlineData(5, 3)]
    [InlineData(101, 3)]
    public void CalculateStatusLength_PayloadMod4Is1_Returns3(int payloadLength, int expectedStatusLen)
    {
        Assert.Equal(expectedStatusLen, RbfLayout.CalculateStatusLength(payloadLength));
    }

    /// <summary>
    /// [F-STATUSLEN-FORMULA]: PayloadLen % 4 == 2 → StatusLen = 2
    /// </summary>
    [Theory]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    [InlineData(102, 2)]
    public void CalculateStatusLength_PayloadMod4Is2_Returns2(int payloadLength, int expectedStatusLen)
    {
        Assert.Equal(expectedStatusLen, RbfLayout.CalculateStatusLength(payloadLength));
    }

    /// <summary>
    /// [F-STATUSLEN-FORMULA]: PayloadLen % 4 == 3 → StatusLen = 1
    /// </summary>
    [Theory]
    [InlineData(3, 1)]
    [InlineData(7, 1)]
    [InlineData(103, 1)]
    public void CalculateStatusLength_PayloadMod4Is3_Returns1(int payloadLength, int expectedStatusLen)
    {
        Assert.Equal(expectedStatusLen, RbfLayout.CalculateStatusLength(payloadLength));
    }

    /// <summary>
    /// [F-STATUSLEN-FORMULA]: (PayloadLen + StatusLen) % 4 == 0
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    [InlineData(1000)]
    public void CalculateStatusLength_PayloadPlusStatusAlignedTo4(int payloadLength)
    {
        var statusLen = RbfLayout.CalculateStatusLength(payloadLength);
        Assert.Equal(0, (payloadLength + statusLen) % 4);
    }

    #endregion

    #region FrameLength 计算测试 [F-HEADLEN-FORMULA]

    /// <summary>
    /// [F-HEADLEN-FORMULA]: 最小帧长度 = 16 + 0 + 4 = 20
    /// </summary>
    [Fact]
    public void CalculateFrameLength_EmptyPayload_Returns20()
    {
        Assert.Equal(20, RbfLayout.CalculateFrameLength(0));
        Assert.Equal(20, RbfLayout.MinFrameLength);
    }

    /// <summary>
    /// [F-HEADLEN-FORMULA]: HeadLen = 16 + PayloadLen + StatusLen
    /// </summary>
    [Theory]
    [InlineData(0, 20)]   // 16 + 0 + 4 = 20
    [InlineData(1, 20)]   // 16 + 1 + 3 = 20
    [InlineData(2, 20)]   // 16 + 2 + 2 = 20
    [InlineData(3, 20)]   // 16 + 3 + 1 = 20
    [InlineData(4, 24)]   // 16 + 4 + 4 = 24
    [InlineData(5, 24)]   // 16 + 5 + 3 = 24
    [InlineData(100, 120)] // 16 + 100 + 4 = 120
    public void CalculateFrameLength_VariousPayloads_ReturnsCorrectLength(int payloadLength, int expectedFrameLen)
    {
        Assert.Equal(expectedFrameLen, RbfLayout.CalculateFrameLength(payloadLength));
    }

    /// <summary>
    /// [F-FRAME-4B-ALIGNMENT]: FrameLength % 4 == 0
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    [InlineData(1000)]
    public void CalculateFrameLength_AlwaysAlignedTo4(int payloadLength)
    {
        var frameLen = RbfLayout.CalculateFrameLength(payloadLength);
        Assert.Equal(0, frameLen % 4);
    }

    #endregion

    #region 偏移量计算测试

    [Fact]
    public void FixedOverhead_Is16()
    {
        Assert.Equal(16, RbfLayout.FixedOverhead);
    }

    [Fact]
    public void PayloadOffset_Is8()
    {
        Assert.Equal(8, RbfLayout.PayloadOffset);
    }

    [Theory]
    [InlineData(100, 0, 108)]   // frameStart + 8 + payloadLen
    [InlineData(100, 10, 118)]
    public void CalculateStatusOffset_ReturnsCorrectOffset(long frameStart, int payloadLen, long expected)
    {
        Assert.Equal(expected, RbfLayout.CalculateStatusOffset(frameStart, payloadLen));
    }

    [Theory]
    [InlineData(100, 0, 112)]   // frameStart + 8 + 0 + 4 = 112
    [InlineData(100, 3, 112)]   // frameStart + 8 + 3 + 1 = 112
    [InlineData(100, 4, 116)]   // frameStart + 8 + 4 + 4 = 116
    public void CalculateTailLenOffset_ReturnsCorrectOffset(long frameStart, int payloadLen, long expected)
    {
        Assert.Equal(expected, RbfLayout.CalculateTailLenOffset(frameStart, payloadLen));
    }

    [Theory]
    [InlineData(100, 0, 116)]   // TailLenOffset + 4
    [InlineData(100, 4, 120)]
    public void CalculateCrcOffset_ReturnsCorrectOffset(long frameStart, int payloadLen, long expected)
    {
        Assert.Equal(expected, RbfLayout.CalculateCrcOffset(frameStart, payloadLen));
    }

    [Theory]
    [InlineData(100, 0, 120)]   // frameStart + FrameLength(0) = 100 + 20
    [InlineData(100, 4, 124)]   // frameStart + FrameLength(4) = 100 + 24
    public void CalculateTrailingFenceOffset_ReturnsCorrectOffset(long frameStart, int payloadLen, long expected)
    {
        Assert.Equal(expected, RbfLayout.CalculateTrailingFenceOffset(frameStart, payloadLen));
    }

    #endregion

    #region 对齐测试 [F-FRAME-4B-ALIGNMENT]

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(100, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(101, false)]
    public void Is4ByteAligned_ReturnsCorrectResult(long offset, bool expected)
    {
        Assert.Equal(expected, RbfLayout.Is4ByteAligned(offset));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(100, 100)]
    [InlineData(103, 100)]
    public void AlignDown4_ReturnsCorrectResult(long offset, long expected)
    {
        Assert.Equal(expected, RbfLayout.AlignDown4(offset));
    }

    #endregion
}
