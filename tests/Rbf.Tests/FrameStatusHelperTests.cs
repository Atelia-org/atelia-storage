// FrameStatus 工具函数单元测试
// 规范引用: rbf-format.md
//   @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]
//   @[F-FRAMESTATUS-RESERVED-BITS-ZERO]
//   @[F-FRAMESTATUS-FILL]

using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

public class FrameStatusHelperTests {
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

    #region DecodeStatusByte Tests

    /// <summary>
    /// 验证正常解码（statusLen=1,2,3,4，非墓碑）。
    /// </summary>
    [Fact]
    public void DecodeStatusByte_ValidByte_ReturnsTrue() {
        // statusLen=1, notTombstone → 0x00
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x00, out bool tomb1, out int len1));
        Assert.False(tomb1);
        Assert.Equal(1, len1);

        // statusLen=2, notTombstone → 0x01
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x01, out bool tomb2, out int len2));
        Assert.False(tomb2);
        Assert.Equal(2, len2);

        // statusLen=3, notTombstone → 0x02
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x02, out bool tomb3, out int len3));
        Assert.False(tomb3);
        Assert.Equal(3, len3);

        // statusLen=4, notTombstone → 0x03
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x03, out bool tomb4, out int len4));
        Assert.False(tomb4);
        Assert.Equal(4, len4);
    }

    /// <summary>
    /// 验证保留位非零时返回 false。
    /// </summary>
    [Theory]
    [InlineData(0x04)]  // Bit2=1
    [InlineData(0x08)]  // Bit3=1
    [InlineData(0x10)]  // Bit4=1
    [InlineData(0x20)]  // Bit5=1
    [InlineData(0x40)]  // Bit6=1
    [InlineData(0x7C)]  // All reserved bits=1
    [InlineData(0x7F)]  // All reserved + StatusLen bits
    [InlineData(0xFC)]  // Tombstone + all reserved
    public void DecodeStatusByte_ReservedBitsNonZero_ReturnsFalse(byte invalidByte) {
        AteliaError? error = FrameStatusHelper.DecodeStatusByte(invalidByte, out bool isTombstone, out int statusLen);
        Assert.NotNull(error);
    }

    /// <summary>
    /// 验证墓碑帧正确解码（Bit7=1）。
    /// </summary>
    [Fact]
    public void DecodeStatusByte_Tombstone_DecodesCorrectly() {
        // statusLen=1, isTombstone → 0x80
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x80, out bool tomb1, out int len1));
        Assert.True(tomb1);
        Assert.Equal(1, len1);

        // statusLen=2, isTombstone → 0x81
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x81, out bool tomb2, out int len2));
        Assert.True(tomb2);
        Assert.Equal(2, len2);

        // statusLen=3, isTombstone → 0x82
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x82, out bool tomb3, out int len3));
        Assert.True(tomb3);
        Assert.Equal(3, len3);

        // statusLen=4, isTombstone → 0x83
        Assert.Null(FrameStatusHelper.DecodeStatusByte(0x83, out bool tomb4, out int len4));
        Assert.True(tomb4);
        Assert.Equal(4, len4);
    }

    /// <summary>
    /// 验证 Encode-Decode 往返一致性。
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
    public void DecodeStatusByte_RoundTrip(bool isTombstone, int statusLen) {
        // Arrange
        byte encoded = FrameStatusHelper.EncodeStatusByte(isTombstone, statusLen);

        // Act
        AteliaError? error = FrameStatusHelper.DecodeStatusByte(encoded, out bool decodedTombstone, out int decodedStatusLen);

        // Assert
        Assert.Null(error);
        Assert.Equal(isTombstone, decodedTombstone);
        Assert.Equal(statusLen, decodedStatusLen);
    }

    #endregion
}
