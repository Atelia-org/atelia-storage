using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfCrc CRC32C 计算测试。
/// </summary>
/// <remarks>
/// 验证 [F-CRC32C-ALGORITHM], [F-CRC32C-COVERAGE]。
/// </remarks>
public class RbfCrcTests {
    /// <summary>
    /// 空数据的 CRC32C。
    /// </summary>
    [Fact]
    public void Compute_EmptyData_ReturnsCorrectCrc() {
        var crc = RbfCrc.Compute([]);
        // CRC32C of empty data: init=0xFFFFFFFF, no data, final_xor=0xFFFFFFFF → 0x00000000
        Assert.Equal(0x00000000u, crc);
    }

    /// <summary>
    /// 已知测试向量："123456789" 的 CRC32C = 0xE3069283。
    /// </summary>
    [Fact]
    public void Compute_KnownTestVector_ReturnsCorrectCrc() {
        // "123456789" 的 CRC32C (Castagnoli) 标准测试向量
        byte[] data = "123456789"u8.ToArray();
        var crc = RbfCrc.Compute(data);
        Assert.Equal(0xE3069283u, crc);
    }

    /// <summary>
    /// 单字节 0x00 的 CRC32C。
    /// </summary>
    [Fact]
    public void Compute_SingleByte_ReturnsCorrectCrc() {
        byte[] data = [0x00];
        var crc = RbfCrc.Compute(data);
        // 使用 Castagnoli 多项式计算的结果
        Assert.Equal(0x527D5351u, crc);
    }

    /// <summary>
    /// Verify 返回 true 当 CRC 匹配。
    /// </summary>
    [Fact]
    public void Verify_MatchingCrc_ReturnsTrue() {
        byte[] data = [0x01, 0x02, 0x03];
        var crc = RbfCrc.Compute(data);
        Assert.True(RbfCrc.Verify(data, crc));
    }

    /// <summary>
    /// Verify 返回 false 当 CRC 不匹配。
    /// </summary>
    [Fact]
    public void Verify_MismatchedCrc_ReturnsFalse() {
        byte[] data = [0x01, 0x02, 0x03];
        Assert.False(RbfCrc.Verify(data, 0xDEADBEEF));
    }
}
