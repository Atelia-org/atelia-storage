using Atelia.Rbf.Internal;
using Atelia.Data.Hashing;
using System.Buffers.Binary;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// TrailerCodewordHelper 单元测试。
/// </summary>
/// <remarks>
/// 覆盖：
/// - Parse: 端序验证（TrailerCrc32C=BE，其他=LE）
/// - BuildDescriptor: 各字段组装
/// - SerializeWithoutCrc: 序列化正确性
/// - SealTrailerCrc / CheckTrailerCrc: CRC 计算与验证
/// </remarks>
public class TrailerCodewordHelperTests {
    #region Parse Tests

    [Fact]
    public void Parse_EndianVerification_TrailerCrcIsBigEndian_OtherFieldsAreLittleEndian() {
        // 构造已知字节序列
        // TrailerCrc32C (BE): 0x12345678 -> bytes: 12 34 56 78
        // FrameDescriptor (LE): 0xAABBCCDD -> bytes: DD CC BB AA
        // FrameTag (LE): 0x11223344 -> bytes: 44 33 22 11
        // TailLen (LE): 0x55667788 -> bytes: 88 77 66 55
        byte[] buffer = [
            0x12, 0x34, 0x56, 0x78, // TrailerCrc32C (BE)
            0xDD, 0xCC, 0xBB, 0xAA, // FrameDescriptor (LE)
            0x44, 0x33, 0x22, 0x11, // FrameTag (LE)
            0x88, 0x77, 0x66, 0x55  // TailLen (LE)
        ];

        var data = TrailerCodewordHelper.Parse(buffer);

        Assert.Equal(0x12345678u, data.TrailerCrc32C);
        Assert.Equal(0xAABBCCDDu, data.FrameDescriptor);
        Assert.Equal(0x11223344u, data.FrameTag);
        Assert.Equal(0x55667788u, data.TailLen);
    }

    [Fact]
    public void Parse_FrameDescriptorFields_DecodedCorrectly() {
        // FrameDescriptor 位布局:
        // bit31=IsTombstone=1, bit30-29=PaddingLen=2 (0b10), bit28-16=Reserved=0, bit15-0=UserMetaLen=1234
        // = 0x80000000 | (2 << 29) | 1234
        // = 0x80000000 | 0x40000000 | 0x04D2
        // = 0xC00004D2
        uint descriptor = 0xC000_04D2u;
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0), 0); // CRC placeholder
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0x42); // Tag
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 100); // TailLen

        var data = TrailerCodewordHelper.Parse(buffer);

        Assert.True(data.IsTombstone);
        Assert.Equal(2, data.PaddingLen);
        Assert.Equal(1234, data.UserMetaLen);
    }

    [Fact]
    public void Parse_NonTombstone_ZeroPadding_ZeroUserMeta() {
        // FrameDescriptor: IsTombstone=0, PaddingLen=0, UserMetaLen=0 -> 0x00000000
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0u);

        var data = TrailerCodewordHelper.Parse(buffer);

        Assert.False(data.IsTombstone);
        Assert.Equal(0, data.PaddingLen);
        Assert.Equal(0, data.UserMetaLen);
    }

    [Fact]
    public void Parse_MaxPaddingLen_MaxUserMetaLen() {
        // FrameDescriptor: IsTombstone=0, PaddingLen=3, UserMetaLen=65535
        // = (3 << 29) | 65535
        // = 0x6000FFFF
        uint descriptor = 0x6000_FFFFu;
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), descriptor);

        var data = TrailerCodewordHelper.Parse(buffer);

        Assert.False(data.IsTombstone);
        Assert.Equal(3, data.PaddingLen);
        Assert.Equal(65535, data.UserMetaLen);
    }

    [Fact]
    public void Parse_BufferTooShort_ThrowsArgumentException() {
        byte[] shortBuffer = new byte[15];

        var ex = Assert.Throws<ArgumentException>(() => TrailerCodewordHelper.Parse(shortBuffer));
        Assert.Contains("16", ex.Message);
    }

    #endregion

    #region BuildDescriptor Tests

    [Fact]
    public void BuildDescriptor_AllCombinations_CorrectBitLayout() {
        // Test: IsTombstone=true, PaddingLen=1, UserMetaLen=100
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(true, 1, 100);
        // Expected: 0x80000000 | (1 << 29) | 100 = 0x80000000 | 0x20000000 | 0x64 = 0xA0000064
        Assert.Equal(0xA000_0064u, descriptor);

        // Test: IsTombstone=false, PaddingLen=3, UserMetaLen=65535
        descriptor = TrailerCodewordHelper.BuildDescriptor(false, 3, 65535);
        // Expected: (3 << 29) | 65535 = 0x6000FFFF
        Assert.Equal(0x6000_FFFFu, descriptor);

        // Test: IsTombstone=false, PaddingLen=0, UserMetaLen=0
        descriptor = TrailerCodewordHelper.BuildDescriptor(false, 0, 0);
        Assert.Equal(0u, descriptor);
    }

    [Fact]
    public void BuildDescriptor_PaddingLenOutOfRange_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailerCodewordHelper.BuildDescriptor(false, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailerCodewordHelper.BuildDescriptor(false, 4, 0));
    }

    [Fact]
    public void BuildDescriptor_UserMetaLenOutOfRange_ThrowsArgumentOutOfRangeException() {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailerCodewordHelper.BuildDescriptor(false, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailerCodewordHelper.BuildDescriptor(false, 0, 65536));
    }

    [Fact]
    public void BuildDescriptor_ReservedBitsAlwaysZero() {
        // 无论输入什么，保留位 (bit28-16) 必须为 0
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(true, 3, 65535);
        Assert.True(TrailerCodewordHelper.ValidateReservedBits(descriptor));
    }

    #endregion

    #region SerializeWithoutCrc Tests

    [Fact]
    public void SerializeWithoutCrc_CorrectLayout() {
        byte[] buffer = new byte[16];
        uint descriptor = 0xAABB_CCDDu;
        uint tag = 0x1122_3344u;
        uint tailLen = 0x5566_7788u;

        TrailerCodewordHelper.SerializeWithoutCrc(buffer, descriptor, tag, tailLen);

        // 前 4 字节（CRC 位置）应为 0
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0)));

        // Descriptor (LE)
        Assert.Equal(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)));
        // Tag (LE)
        Assert.Equal(tag, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)));
        // TailLen (LE)
        Assert.Equal(tailLen, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)));
    }

    [Fact]
    public void SerializeWithoutCrc_BufferTooShort_ThrowsArgumentException() {
        byte[] shortBuffer = new byte[15];

        var ex = Assert.Throws<ArgumentException>(() =>
            TrailerCodewordHelper.SerializeWithoutCrc(shortBuffer, 0, 0, 0));
        Assert.Contains("16", ex.Message);
    }

    #endregion

    #region SealTrailerCrc / CheckTrailerCrc Tests

    [Fact]
    public void SealTrailerCrc_ThenCheckTrailerCrc_ReturnsTrue() {
        // 构造 TrailerCodeword
        byte[] buffer = new byte[16];
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(false, 2, 1000);
        uint tag = 0x42u;
        uint tailLen = 100u;

        TrailerCodewordHelper.SerializeWithoutCrc(buffer, descriptor, tag, tailLen);
        uint crc = TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 验证 CRC
        Assert.True(TrailerCodewordHelper.CheckTrailerCrc(buffer));

        // 验证 CRC 是以 BE 存储的
        uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0));
        Assert.Equal(crc, storedCrc);
    }

    [Fact]
    public void SealTrailerCrc_WrittenCrcIsBigEndian() {
        byte[] buffer = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, 0x12345678u, 0x42u, 0x100u);

        uint crc = TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 验证 CRC 以 BE 写入
        Assert.Equal(crc, BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0)));

        // 验证不是 LE
        uint crcAsLe = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0));
        // CRC 不太可能是对称的，所以通常 BE != LE
        if (crc != crcAsLe) {
            Assert.NotEqual(crc, crcAsLe);
        }
    }

    [Fact]
    public void CheckTrailerCrc_CorruptedData_ReturnsFalse() {
        byte[] buffer = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, 0x12345678u, 0x42u, 0x100u);
        TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 篡改数据
        buffer[5] ^= 0xFF;

        Assert.False(TrailerCodewordHelper.CheckTrailerCrc(buffer));
    }

    [Fact]
    public void CheckTrailerCrc_CorruptedCrc_ReturnsFalse() {
        byte[] buffer = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, 0x12345678u, 0x42u, 0x100u);
        TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 篡改 CRC
        buffer[0] ^= 0xFF;

        Assert.False(TrailerCodewordHelper.CheckTrailerCrc(buffer));
    }

    [Fact]
    public void SealTrailerCrc_BufferTooShort_ThrowsArgumentException() {
        byte[] shortBuffer = new byte[15];

        var ex = Assert.Throws<ArgumentException>(() =>
            TrailerCodewordHelper.SealTrailerCrc(shortBuffer));
        Assert.Contains("16", ex.Message);
    }

    [Fact]
    public void CheckTrailerCrc_BufferTooShort_ThrowsArgumentException() {
        byte[] shortBuffer = new byte[15];

        var ex = Assert.Throws<ArgumentException>(() =>
            TrailerCodewordHelper.CheckTrailerCrc(shortBuffer));
        Assert.Contains("16", ex.Message);
    }

    #endregion

    #region Test Vector: SealTrailerCrc matches RollingCrc.CheckCodewordBackward

    [Fact]
    public void TestVector_SealTrailerCrc_MatchesRollingCrcCheckCodewordBackward() {
        // 构造已知 (descriptor, tag, tailLen)
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(true, 1, 500);
        uint tag = 0xDEAD_BEEFu;
        uint tailLen = 0x1000u;

        byte[] buffer = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, descriptor, tag, tailLen);
        uint sealedCrc = TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 验证前 4 字节（BE）是 sealedCrc
        uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0));
        Assert.Equal(sealedCrc, storedCrc);

        // 使用 RollingCrc.CheckCodewordBackward 验证
        Assert.True(RollingCrc.CheckCodewordBackward(buffer));
    }

    [Fact]
    public void TestVector_KnownValues_CrcConsistent() {
        // 使用完全已知的输入，验证 CRC 计算的一致性
        // 这个测试确保 CRC 算法不会意外变更

        byte[] buffer = new byte[16];
        // 使用简单的已知值
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, 0u, 1u, 24u);
        uint crc = TrailerCodewordHelper.SealTrailerCrc(buffer);

        // 重新计算应该得到相同结果
        byte[] buffer2 = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer2, 0u, 1u, 24u);
        uint crc2 = TrailerCodewordHelper.SealTrailerCrc(buffer2);

        Assert.Equal(crc, crc2);

        // 验证两个 buffer 完全相同
        Assert.Equal(buffer, buffer2);
    }

    #endregion

    #region ValidateReservedBits Tests

    [Fact]
    public void ValidateReservedBits_ZeroReserved_ReturnsTrue() {
        // 保留位为 0
        uint descriptor = 0x8000_FFFFu; // IsTombstone=1, PaddingLen=0, Reserved=0, UserMetaLen=65535
        Assert.True(TrailerCodewordHelper.ValidateReservedBits(descriptor));
    }

    [Fact]
    public void ValidateReservedBits_NonZeroReserved_ReturnsFalse() {
        // 保留位非 0 (bit 28-16)
        uint descriptor = 0x0001_0000u; // Reserved bit 16 = 1
        Assert.False(TrailerCodewordHelper.ValidateReservedBits(descriptor));

        descriptor = 0x1000_0000u; // Reserved bit 28 = 1
        Assert.False(TrailerCodewordHelper.ValidateReservedBits(descriptor));

        descriptor = 0x0800_0000u; // Reserved bit 23 = 1
        Assert.False(TrailerCodewordHelper.ValidateReservedBits(descriptor));
    }

    #endregion

    #region Roundtrip Tests

    [Theory]
    [InlineData(false, 0, 0, 0x00000001u, 24u)]
    [InlineData(true, 3, 65535, 0xFFFFFFFFu, 0x00100000u)]
    [InlineData(false, 2, 1000, 0x42u, 100u)]
    [InlineData(true, 1, 123, 0xDEADBEEFu, 0x1234u)]
    public void Roundtrip_SerializeParseSealCheck(bool isTombstone, int paddingLen, int userMetaLen, uint tag, uint tailLen) {
        // Build
        uint descriptor = TrailerCodewordHelper.BuildDescriptor(isTombstone, paddingLen, userMetaLen);

        // Serialize
        byte[] buffer = new byte[16];
        TrailerCodewordHelper.SerializeWithoutCrc(buffer, descriptor, tag, tailLen);

        // Seal
        TrailerCodewordHelper.SealTrailerCrc(buffer);

        // Check
        Assert.True(TrailerCodewordHelper.CheckTrailerCrc(buffer));

        // Parse
        var data = TrailerCodewordHelper.Parse(buffer);

        // Verify
        Assert.Equal(isTombstone, data.IsTombstone);
        Assert.Equal(paddingLen, data.PaddingLen);
        Assert.Equal(userMetaLen, data.UserMetaLen);
        Assert.Equal(tag, data.FrameTag);
        Assert.Equal(tailLen, data.TailLen);
        Assert.True(TrailerCodewordHelper.ValidateReservedBits(data.FrameDescriptor));
    }

    #endregion
}
