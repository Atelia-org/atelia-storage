using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// FrameLayout 单元测试（v0.40 格式）。
/// </summary>
/// <remarks>
/// v0.40 布局：[HeadLen][Payload][UserMeta][Padding][PayloadCrc][TrailerCodeword]
/// 规范引用：
/// - @[F-FRAMEBYTES-FIELD-OFFSETS]
/// - @[F-PADDING-CALCULATION]
/// </remarks>
public class FrameLayoutTests {
    #region PaddingLength Tests

    /// <summary>
    /// payloadLen=0, userMetaLen=0: (4 - (0+0) % 4) % 4 = 0
    /// </summary>
    [Fact]
    public void PaddingLength_PayloadLen0_Returns0() {
        // Act
        int result = new FrameLayout(0).PaddingLength;

        // Assert
        Assert.Equal(0, result);
    }

    /// <summary>
    /// payloadLen=1, userMetaLen=0: (4 - (1+0) % 4) % 4 = 3
    /// </summary>
    [Fact]
    public void PaddingLength_PayloadLen1_Returns3() {
        // Act
        int result = new FrameLayout(1).PaddingLength;

        // Assert
        Assert.Equal(3, result);
    }

    /// <summary>
    /// payloadLen=2, userMetaLen=0: (4 - (2+0) % 4) % 4 = 2
    /// </summary>
    [Fact]
    public void PaddingLength_PayloadLen2_Returns2() {
        // Act
        int result = new FrameLayout(2).PaddingLength;

        // Assert
        Assert.Equal(2, result);
    }

    /// <summary>
    /// payloadLen=3, userMetaLen=0: (4 - (3+0) % 4) % 4 = 1
    /// </summary>
    [Fact]
    public void PaddingLength_PayloadLen3_Returns1() {
        // Act
        int result = new FrameLayout(3).PaddingLength;

        // Assert
        Assert.Equal(1, result);
    }

    /// <summary>
    /// payloadLen=4, userMetaLen=0: (4 - (4+0) % 4) % 4 = 0
    /// </summary>
    [Fact]
    public void PaddingLength_PayloadLen4_Returns0() {
        // Act
        int result = new FrameLayout(4).PaddingLength;

        // Assert
        Assert.Equal(0, result);
    }

    /// <summary>
    /// 验证完整的 4 周期循环模式（v0.40 padding 公式）。
    /// PaddingLen = (4 - ((payloadLen + userMetaLen) % 4)) % 4
    /// </summary>
    [Theory]
    [InlineData(0, 0)]  // (4 - 0 % 4) % 4 = 0
    [InlineData(1, 3)]  // (4 - 1 % 4) % 4 = 3
    [InlineData(2, 2)]  // (4 - 2 % 4) % 4 = 2
    [InlineData(3, 1)]  // (4 - 3 % 4) % 4 = 1
    [InlineData(4, 0)]  // (4 - 0 % 4) % 4 = 0
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    [InlineData(7, 1)]
    [InlineData(100, 0)]  // 100 % 4 == 0
    [InlineData(101, 3)]  // 101 % 4 == 1
    [InlineData(102, 2)]  // 102 % 4 == 2
    [InlineData(103, 1)]  // 103 % 4 == 3
    public void PaddingLength_CyclicPattern(int payloadLen, int expectedPaddingLen) {
        // Act
        int result = new FrameLayout(payloadLen).PaddingLength;

        // Assert
        Assert.Equal(expectedPaddingLen, result);
    }

    /// <summary>
    /// 验证 (payloadLen + userMetaLen + paddingLen) % 4 == 0 的对齐不变量。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(100, 0)]
    [InlineData(1000, 0)]
    [InlineData(0, 10)]   // 带 userMeta
    [InlineData(5, 7)]
    public void PaddingLength_EnsuresAlignment(int payloadLen, int userMetaLen) {
        // Act
        var layout = new FrameLayout(payloadLen, userMetaLen);
        int paddingLen = layout.PaddingLength;

        // Assert: (payloadLen + userMetaLen + paddingLen) must be 4-aligned
        Assert.Equal(0, (payloadLen + userMetaLen + paddingLen) % 4);
    }

    #endregion

    #region FrameLength Tests

    /// <summary>
    /// 验证最小帧长度：空 payload/userMeta 时为 24 字节。
    /// FrameLength = HeadLen(4) + Payload(0) + UserMeta(0) + Padding(0) + PayloadCrc(4) + TrailerCodeword(16) = 24
    /// </summary>
    [Fact]
    public void FrameLength_EmptyPayload_Returns24() {
        // Act
        int result = new FrameLayout(0).FrameLength;

        // Assert
        Assert.Equal(24, result);
        Assert.Equal(FrameLayout.MinFrameLength, result);
    }

    /// <summary>
    /// 验证帧长度计算：包含 payload 但无 userMeta。
    /// </summary>
    [Theory]
    [InlineData(1, 28)]   // 4 + 1 + 3(padding) + 4 + 16 = 28
    [InlineData(4, 28)]   // 4 + 4 + 0(padding) + 4 + 16 = 28
    [InlineData(100, 124)] // 4 + 100 + 0(padding) + 4 + 16 = 124
    public void FrameLength_WithPayload_CalculatesCorrectly(int payloadLen, int expectedFrameLen) {
        // Act
        int result = new FrameLayout(payloadLen).FrameLength;

        // Assert
        Assert.Equal(expectedFrameLen, result);
    }

    /// <summary>
    /// 验证帧长度始终是 4 的倍数（4B 对齐不变量）。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(17, 5)]
    [InlineData(100, 50)]
    public void FrameLength_AlwaysAligned(int payloadLen, int userMetaLen) {
        // Act
        int result = new FrameLayout(payloadLen, userMetaLen).FrameLength;

        // Assert
        Assert.Equal(0, result % 4);
    }

    #endregion

    #region Offset Tests

    /// <summary>
    /// 验证各字段偏移的计算（空 payload/userMeta）。
    /// </summary>
    [Fact]
    public void Offsets_EmptyPayload_CorrectValues() {
        // Arrange
        var layout = new FrameLayout(0);

        // Assert
        Assert.Equal(0, FrameLayout.HeadLenOffset);
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(4, layout.UserMetaOffset);    // 4 + 0
        Assert.Equal(4, layout.PaddingOffset);     // 4 + 0 + 0
        Assert.Equal(4, layout.PayloadCrcOffset);  // 4 + 0 + 0 + 0
        Assert.Equal(8, layout.TrailerCodewordOffset); // 4 + 0 + 0 + 0 + 4
    }

    /// <summary>
    /// 验证各字段偏移的计算（带 payload）。
    /// </summary>
    [Fact]
    public void Offsets_WithPayload_CorrectValues() {
        // Arrange: payload=5, userMeta=0, padding=3
        var layout = new FrameLayout(5);

        // Assert
        Assert.Equal(0, FrameLayout.HeadLenOffset);
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(9, layout.UserMetaOffset);     // 4 + 5
        Assert.Equal(9, layout.PaddingOffset);      // 4 + 5 + 0
        Assert.Equal(12, layout.PayloadCrcOffset);  // 4 + 5 + 0 + 3
        Assert.Equal(16, layout.TrailerCodewordOffset); // 12 + 4
    }

    /// <summary>
    /// 验证各字段偏移的计算（带 payload 和 userMeta）。
    /// </summary>
    [Fact]
    public void Offsets_WithPayloadAndUserMeta_CorrectValues() {
        // Arrange: payload=5, userMeta=3, padding=(4-(5+3)%4)%4=0
        var layout = new FrameLayout(5, 3);

        // Assert
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(9, layout.UserMetaOffset);     // 4 + 5
        Assert.Equal(12, layout.PaddingOffset);     // 4 + 5 + 3
        Assert.Equal(0, layout.PaddingLength);
        Assert.Equal(12, layout.PayloadCrcOffset);  // 4 + 5 + 3 + 0
        Assert.Equal(16, layout.TrailerCodewordOffset); // 12 + 4
    }

    #endregion
}
