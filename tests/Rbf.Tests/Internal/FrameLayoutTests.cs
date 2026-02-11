using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>FrameLayout 单元测试（v0.40 格式）。</summary>
/// <remarks>
/// v0.40 布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
/// 规范引用：
/// - @[F-FRAMEBYTES-LAYOUT]
/// - @[F-PADDING-CALCULATION]
/// </remarks>
public class FrameLayoutTests {
    #region PaddingLength Tests

    /// <summary>验证完整的 4 周期循环模式（v0.40 padding 公式）。
    /// PaddingLen = (4 - ((payloadLen + tailMetaLen) % 4)) % 4</summary>
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

    /// <summary>验证 (payloadLen + tailMetaLen + paddingLen) % 4 == 0 的对齐不变量。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(100, 0)]
    [InlineData(1000, 0)]
    [InlineData(0, 10)]   // 带 tailMeta
    [InlineData(5, 7)]
    public void PaddingLength_EnsuresAlignment(int payloadLen, int tailMetaLen) {
        // Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);
        int paddingLen = layout.PaddingLength;

        // Assert: (payloadLen + tailMetaLen + paddingLen) must be 4-aligned
        Assert.Equal(0, (payloadLen + tailMetaLen + paddingLen) % 4);
    }

    #endregion

    #region FrameLength Tests

    /// <summary>验证最小帧长度：空 payload/tailMeta 时为 24 字节。
    /// FrameLength = HeadLen(4) + Payload(0) + TailMeta(0) + Padding(0) + PayloadCrc(4) + TrailerCodeword(16) = 24</summary>
    [Fact]
    public void FrameLength_EmptyPayload_Returns24() {
        // Act
        int result = new FrameLayout(0).FrameLength;

        // Assert
        Assert.Equal(24, result);
        Assert.Equal(FrameLayout.MinFrameLength, result);
    }

    /// <summary>验证帧长度计算：包含 payload 但无 tailMeta。</summary>
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

    /// <summary>验证帧长度始终是 4 的倍数（4B 对齐不变量）。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(17, 5)]
    [InlineData(100, 50)]
    public void FrameLength_AlwaysAligned(int payloadLen, int tailMetaLen) {
        // Act
        int result = new FrameLayout(payloadLen, tailMetaLen).FrameLength;

        // Assert
        Assert.Equal(0, result % 4);
    }

    #endregion

    #region Offset Tests

    /// <summary>验证各字段偏移的计算（空 payload/tailMeta）。</summary>
    [Fact]
    public void Offsets_EmptyPayload_CorrectValues() {
        // Arrange
        var layout = new FrameLayout(0);

        // Assert
        Assert.Equal(0, FrameLayout.HeadLenOffset);
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(4, layout.TailMetaOffset);    // 4 + 0
        Assert.Equal(4, layout.PaddingOffset);     // 4 + 0 + 0
        Assert.Equal(4, layout.PayloadCrcOffset);  // 4 + 0 + 0 + 0
        Assert.Equal(8, layout.TrailerCodewordOffset); // 4 + 0 + 0 + 0 + 4
    }

    /// <summary>验证各字段偏移的计算（带 payload）。</summary>
    [Fact]
    public void Offsets_WithPayload_CorrectValues() {
        // Arrange: payload=5, tailMeta=0, padding=3
        var layout = new FrameLayout(5);

        // Assert
        Assert.Equal(0, FrameLayout.HeadLenOffset);
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(9, layout.TailMetaOffset);     // 4 + 5
        Assert.Equal(9, layout.PaddingOffset);      // 4 + 5 + 0
        Assert.Equal(12, layout.PayloadCrcOffset);  // 4 + 5 + 0 + 3
        Assert.Equal(16, layout.TrailerCodewordOffset); // 12 + 4
    }

    /// <summary>验证各字段偏移的计算（带 payload 和 tailMeta）。</summary>
    [Fact]
    public void Offsets_WithPayloadAndMeta_CorrectValues() {
        // Arrange: payload=5, tailMeta=3, padding=(4-(5+3)%4)%4=0
        var layout = new FrameLayout(5, 3);

        // Assert
        Assert.Equal(4, FrameLayout.PayloadOffset);
        Assert.Equal(9, layout.TailMetaOffset);     // 4 + 5
        Assert.Equal(12, layout.PaddingOffset);     // 4 + 5 + 3
        Assert.Equal(0, layout.PaddingLength);
        Assert.Equal(12, layout.PayloadCrcOffset);  // 4 + 5 + 3 + 0
        Assert.Equal(16, layout.TrailerCodewordOffset); // 12 + 4
    }

    #endregion
}
