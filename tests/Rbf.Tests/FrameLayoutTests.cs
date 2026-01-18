using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

public class FrameLayoutTests {
    #region StatusLength Tests

    /// <summary>
    /// payloadLen=0: (0+1)%4=1, (4-1)%4=3, 1+3=4
    /// </summary>
    [Fact]
    public void StatusLength_PayloadLen0_Returns4() {
        // Act
        int result = new FrameLayout(0).StatusLength;

        // Assert
        Assert.Equal(4, result);
    }

    /// <summary>
    /// payloadLen=1: (1+1)%4=2, (4-2)%4=2, 1+2=3
    /// </summary>
    [Fact]
    public void StatusLength_PayloadLen1_Returns3() {
        // Act
        int result = new FrameLayout(1).StatusLength;

        // Assert
        Assert.Equal(3, result);
    }

    /// <summary>
    /// payloadLen=2: (2+1)%4=3, (4-3)%4=1, 1+1=2
    /// </summary>
    [Fact]
    public void StatusLength_PayloadLen2_Returns2() {
        // Act
        int result = new FrameLayout(2).StatusLength;

        // Assert
        Assert.Equal(2, result);
    }

    /// <summary>
    /// payloadLen=3: (3+1)%4=0, (4-0)%4=0, 1+0=1
    /// </summary>
    [Fact]
    public void StatusLength_PayloadLen3_Returns1() {
        // Act
        int result = new FrameLayout(3).StatusLength;

        // Assert
        Assert.Equal(1, result);
    }

    /// <summary>
    /// payloadLen=4: (4+1)%4=1, (4-1)%4=3, 1+3=4 (循环回 statusLen=4)
    /// </summary>
    [Fact]
    public void StatusLength_PayloadLen4_Returns4() {
        // Act
        int result = new FrameLayout(4).StatusLength;

        // Assert
        Assert.Equal(4, result);
    }

    /// <summary>
    /// 验证完整的 4 周期循环模式。
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    [InlineData(7, 1)]
    [InlineData(100, 4)]  // 100 % 4 == 0
    [InlineData(101, 3)]  // 101 % 4 == 1
    [InlineData(102, 2)]  // 102 % 4 == 2
    [InlineData(103, 1)]  // 103 % 4 == 3
    public void StatusLength_CyclicPattern(int payloadLen, int expectedStatusLen) {
        // Act
        int result = new FrameLayout(payloadLen).StatusLength;

        // Assert
        Assert.Equal(expectedStatusLen, result);
    }

    /// <summary>
    /// 验证 (payloadLen + statusLen) % 4 == 0 的对齐不变量。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(100)]
    [InlineData(1000)]
    public void StatusLength_EnsuresAlignment(int payloadLen) {
        // Act
        int statusLen = new FrameLayout(payloadLen).StatusLength;

        // Assert: (payloadLen + statusLen) must be 4-aligned
        Assert.Equal(0, (payloadLen + statusLen) % 4);
    }

    #endregion
}
