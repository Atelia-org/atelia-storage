using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>测试向量验证（对应 rbf-test-vectors.md v0.40）。</summary>
/// <remarks>
/// 本文件专注于"规范验证"，与 rbf-test-vectors.md 形成 1:1 对应。
/// </remarks>
public class RbfTestVectorTests {
    #region RBF_LEN_* 帧长度计算测试（§1.4）

    /// <summary>RBF_LEN_001：PayloadLen = 0,1,2,3,4 时，验证 PaddingLen 和 FrameLength。</summary>
    /// <remarks>
    /// 公式：
    /// - PaddingLen = (4 - ((PayloadLen + TailMetaLen) % 4)) % 4
    /// - FrameLength = 24 + PayloadLen + TailMetaLen + PaddingLen
    /// 其中 24 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16)
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 24)]  // PayloadLen=0 → PaddingLen=0, FrameLength=24
    [InlineData(1, 3, 28)]  // PayloadLen=1 → PaddingLen=3, FrameLength=28
    [InlineData(2, 2, 28)]  // PayloadLen=2 → PaddingLen=2, FrameLength=28
    [InlineData(3, 1, 28)]  // PayloadLen=3 → PaddingLen=1, FrameLength=28
    [InlineData(4, 0, 28)]  // PayloadLen=4 → PaddingLen=0, FrameLength=28
    public void RBF_LEN_001_PayloadLen_PaddingLen_FrameLength(
        int payloadLen,
        int expectedPaddingLen,
        int expectedFrameLength) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen);

        // Assert
        Assert.Equal(expectedPaddingLen, layout.PaddingLength);
        Assert.Equal(expectedFrameLength, layout.FrameLength);
    }

    /// <summary>RBF_LEN_002：验证 PaddingLen 取值 0,3,2,1 与 (PayloadLen + TailMetaLen) % 4 的关系。</summary>
    /// <remarks>
    /// 规范：@[F-PADDING-CALCULATION]
    /// | (PayloadLen + TailMetaLen) % 4 | PaddingLen |
    /// |--------------------------------|------------|
    /// | 0                              | 0          |
    /// | 1                              | 3          |
    /// | 2                              | 2          |
    /// | 3                              | 1          |
    /// </remarks>
    [Theory]
    // mod 4 == 0 → PaddingLen = 0
    [InlineData(0, 0, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(8, 8, 0)]
    // mod 4 == 1 → PaddingLen = 3
    [InlineData(1, 0, 3)]
    [InlineData(5, 0, 3)]
    [InlineData(0, 1, 3)]
    [InlineData(3, 2, 3)]
    // mod 4 == 2 → PaddingLen = 2
    [InlineData(2, 0, 2)]
    [InlineData(6, 0, 2)]
    [InlineData(0, 2, 2)]
    [InlineData(1, 1, 2)]
    // mod 4 == 3 → PaddingLen = 1
    [InlineData(3, 0, 1)]
    [InlineData(7, 0, 1)]
    [InlineData(0, 3, 1)]
    [InlineData(2, 1, 1)]
    public void RBF_LEN_002_PaddingLen_ModuloRelation(
        int payloadLen,
        int tailMetaLen,
        int expectedPaddingLen) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(expectedPaddingLen, layout.PaddingLength);
        // 验证不变量：(PayloadLen + TailMetaLen + PaddingLen) % 4 == 0
        Assert.Equal(0, (payloadLen + tailMetaLen + layout.PaddingLength) % 4);
    }

    /// <summary>RBF_LEN_003：PayloadLen=10, TailMetaLen=5 → FrameLength=40。</summary>
    /// <remarks>
    /// 计算过程：
    /// - 10 + 5 = 15
    /// - 15 % 4 = 3 → PaddingLen = 1
    /// - FrameLength = 24 + 10 + 5 + 1 = 40
    /// </remarks>
    [Fact]
    public void RBF_LEN_003_PayloadLen10_TailMetaLen5_FrameLength40() {
        // Arrange
        const int payloadLen = 10;
        const int tailMetaLen = 5;

        // Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(1, layout.PaddingLength);  // (10+5)%4=3 → padding=1
        Assert.Equal(40, layout.FrameLength);   // 24 + 10 + 5 + 1 = 40
    }

    /// <summary>验证 FrameLength 始终是 4B 对齐。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(10, 5)]
    [InlineData(17, 3)]
    [InlineData(100, 50)]
    public void FrameLength_Always4ByteAligned(int payloadLen, int tailMetaLen) {
        // Arrange & Act
        var layout = new FrameLayout(payloadLen, tailMetaLen);

        // Assert
        Assert.Equal(0, layout.FrameLength % 4);
    }

    #endregion
}
