// FrameStatus 工具函数单元测试
// 规范引用: rbf-format.md
//   @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]
//   @[F-FRAMESTATUS-RESERVED-BITS-ZERO]
//   @[F-FRAMESTATUS-FILL]

using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

public class FrameStatusHelperTests {
    #region ComputeStatusLen Tests

    /// <summary>
    /// payloadLen=0: (0+1)%4=1, (4-1)%4=3, 1+3=4
    /// </summary>
    [Fact]
    public void ComputeStatusLen_PayloadLen0_Returns4() {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(0);

        // Assert
        Assert.Equal(4, result);
    }

    /// <summary>
    /// payloadLen=1: (1+1)%4=2, (4-2)%4=2, 1+2=3
    /// </summary>
    [Fact]
    public void ComputeStatusLen_PayloadLen1_Returns3() {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(1);

        // Assert
        Assert.Equal(3, result);
    }

    /// <summary>
    /// payloadLen=2: (2+1)%4=3, (4-3)%4=1, 1+1=2
    /// </summary>
    [Fact]
    public void ComputeStatusLen_PayloadLen2_Returns2() {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(2);

        // Assert
        Assert.Equal(2, result);
    }

    /// <summary>
    /// payloadLen=3: (3+1)%4=0, (4-0)%4=0, 1+0=1
    /// </summary>
    [Fact]
    public void ComputeStatusLen_PayloadLen3_Returns1() {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(3);

        // Assert
        Assert.Equal(1, result);
    }

    /// <summary>
    /// payloadLen=4: (4+1)%4=1, (4-1)%4=3, 1+3=4 (循环回 statusLen=4)
    /// </summary>
    [Fact]
    public void ComputeStatusLen_PayloadLen4_Returns4() {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(4);

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
    public void ComputeStatusLen_CyclicPattern(int payloadLen, int expectedStatusLen) {
        // Act
        int result = FrameStatusHelper.ComputeStatusLen(payloadLen);

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
    public void ComputeStatusLen_EnsuresAlignment(int payloadLen) {
        // Act
        int statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);

        // Assert: (payloadLen + statusLen) must be 4-aligned
        Assert.Equal(0, (payloadLen + statusLen) % 4);
    }

    #endregion

    #region EncodeStatusByte Tests

    /// <summary>
    /// Valid 帧位域：statusLen=4, notTombstone → 0b00000011 = 0x03
    /// </summary>
    [Fact]
    public void EncodeStatusByte_NotTombstone_CorrectBits() {
        // statusLen=1, notTombstone → Bit1-0=00, Bit7=0 → 0x00
        Assert.Equal(0x00, FrameStatusHelper.EncodeStatusByte(false, 1));

        // statusLen=2, notTombstone → Bit1-0=01, Bit7=0 → 0x01
        Assert.Equal(0x01, FrameStatusHelper.EncodeStatusByte(false, 2));

        // statusLen=3, notTombstone → Bit1-0=10, Bit7=0 → 0x02
        Assert.Equal(0x02, FrameStatusHelper.EncodeStatusByte(false, 3));

        // statusLen=4, notTombstone → Bit1-0=11, Bit7=0 → 0x03
        Assert.Equal(0x03, FrameStatusHelper.EncodeStatusByte(false, 4));
    }

    /// <summary>
    /// Tombstone 帧位域：Bit7=1
    /// </summary>
    [Fact]
    public void EncodeStatusByte_Tombstone_CorrectBits() {
        // statusLen=1, isTombstone → Bit1-0=00, Bit7=1 → 0x80
        Assert.Equal(0x80, FrameStatusHelper.EncodeStatusByte(true, 1));

        // statusLen=2, isTombstone → Bit1-0=01, Bit7=1 → 0x81
        Assert.Equal(0x81, FrameStatusHelper.EncodeStatusByte(true, 2));

        // statusLen=3, isTombstone → Bit1-0=10, Bit7=1 → 0x82
        Assert.Equal(0x82, FrameStatusHelper.EncodeStatusByte(true, 3));

        // statusLen=4, isTombstone → Bit1-0=11, Bit7=1 → 0x83
        Assert.Equal(0x83, FrameStatusHelper.EncodeStatusByte(true, 4));
    }

    /// <summary>
    /// 验证 Reserved 位 (Bit6-2) 始终为 0。
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public void EncodeStatusByte_ReservedBitsAlwaysZero(bool isTombstone, int statusLen) {
        // Act
        byte result = FrameStatusHelper.EncodeStatusByte(isTombstone, statusLen);

        // Assert: Bit6-2 (mask 0x7C) must be zero
        Assert.Equal(0, result & 0x7C);
    }

    #endregion

    #region FillStatus Tests

    /// <summary>
    /// 验证 FillStatus 填充所有字节为相同值。
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void FillStatus_AllBytesIdentical(bool isTombstone, int statusLen) {
        // Arrange
        byte[] buffer = new byte[statusLen];
        byte expectedByte = FrameStatusHelper.EncodeStatusByte(isTombstone, statusLen);

        // Act
        FrameStatusHelper.FillStatus(buffer, isTombstone, statusLen);

        // Assert
        for (int i = 0; i < statusLen; i++) {
            Assert.Equal(expectedByte, buffer[i]);
        }
    }

    /// <summary>
    /// 验证 FillStatus 正确覆盖现有数据。
    /// </summary>
    [Fact]
    public void FillStatus_OverwritesExistingData() {
        // Arrange
        byte[] buffer = [0xFF, 0xFF, 0xFF, 0xFF];

        // Act
        FrameStatusHelper.FillStatus(buffer, false, 4);

        // Assert: 应全部为 0x03（statusLen=4, notTombstone）
        Assert.All(buffer, b => Assert.Equal(0x03, b));
    }

    #endregion

    #region Invalid Input Tests

    /// <summary>
    /// 验证 ComputeStatusLen 对负数 payloadLen 抛出 ArgumentOutOfRangeException。
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void ComputeStatusLen_NegativePayloadLen_ThrowsArgumentOutOfRange(int payloadLen) {
        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameStatusHelper.ComputeStatusLen(payloadLen));
        Assert.Equal("payloadLen", ex.ParamName);
    }

    /// <summary>
    /// 验证 EncodeStatusByte 对无效 statusLen 抛出 ArgumentOutOfRangeException。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(100)]
    public void EncodeStatusByte_InvalidStatusLen_ThrowsArgumentOutOfRange(int statusLen) {
        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameStatusHelper.EncodeStatusByte(false, statusLen));
        Assert.Equal("statusLen", ex.ParamName);
    }

    /// <summary>
    /// 验证 FillStatus 在 dest.Length != statusLen 时抛出 ArgumentException。
    /// </summary>
    [Theory]
    [InlineData(2, 4)]  // dest 太小
    [InlineData(4, 2)]  // dest 太大
    [InlineData(0, 1)]  // dest 为空
    public void FillStatus_DestLengthMismatch_ThrowsArgumentException(int destLength, int statusLen) {
        // Arrange
        byte[] buffer = new byte[destLength];

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(
            () => FrameStatusHelper.FillStatus(buffer, false, statusLen));
        Assert.Equal("dest", ex.ParamName);
    }

    #endregion
}
