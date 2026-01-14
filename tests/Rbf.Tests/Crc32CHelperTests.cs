// CRC32C 工具函数单元测试
// 规范引用: rbf-format.md @[F-CRC-IS-CRC32C-CASTAGNOLI-REFLECTED]

using System.Numerics;
using System.Text;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

public class Crc32CHelperTests
{
    /// <summary>
    /// 空输入测试：空数据的 CRC32C 应为 0x00000000。
    /// 原因：初始值 0xFFFFFFFF 异或 0xFFFFFFFF = 0x00000000。
    /// </summary>
    [Fact]
    public void Compute_EmptyInput_ReturnsExpected()
    {
        // Arrange
        ReadOnlySpan<byte> empty = [];

        // Act
        uint result = Crc32CHelper.Compute(empty);

        // Assert
        Assert.Equal(0x00000000u, result);
    }

    /// <summary>
    /// 已知测试向量：ASCII "123456789" 的 CRC32C 应为 0xE3069283。
    /// 这是 IETF RFC 3720 中定义的标准测试向量。
    /// </summary>
    [Fact]
    public void Compute_KnownVector_ReturnsExpected()
    {
        // Arrange
        byte[] data = Encoding.ASCII.GetBytes("123456789");

        // Act
        uint result = Crc32CHelper.Compute(data);

        // Assert
        Assert.Equal(0xE3069283u, result);
    }

    /// <summary>
    /// 4 字节对齐输入测试：验证 RBF 典型场景（帧数据通常 4B 对齐）。
    /// </summary>
    [Fact]
    public void Compute_4ByteAligned_Efficient()
    {
        // Arrange: 12 字节数据（4B 对齐）
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C];

        // Act
        uint result = Crc32CHelper.Compute(data);

        // Assert: 验证结果非零且可重复
        Assert.NotEqual(0u, result);

        // 再次计算验证一致性
        uint result2 = Crc32CHelper.Compute(data);
        Assert.Equal(result, result2);
    }

    /// <summary>
    /// 8 字节对齐输入测试：验证 ulong 优化路径。
    /// </summary>
    [Fact]
    public void Compute_8ByteAligned_UsesUlongPath()
    {
        // Arrange: 16 字节数据（8B 对齐）
        byte[] data = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        uint result = Crc32CHelper.Compute(data);

        // Assert: 验证结果非零
        Assert.NotEqual(0u, result);
    }

    /// <summary>
    /// 非对齐输入测试：验证各种长度的处理。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(15)]
    public void Compute_VariousLengths_Succeeds(int length)
    {
        // Arrange
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        // Act
        uint result = Crc32CHelper.Compute(data);

        // Assert: 验证结果非零且可重复
        Assert.NotEqual(0u, result);

        uint result2 = Crc32CHelper.Compute(data);
        Assert.Equal(result, result2);
    }

    /// <summary>
    /// 单字节输入测试。
    /// </summary>
    [Fact]
    public void Compute_SingleByte_ReturnsExpected()
    {
        // Arrange
        byte[] data = [0x00];

        // Act
        uint result = Crc32CHelper.Compute(data);

        // Assert: 验证结果非零
        Assert.NotEqual(0u, result);
    }

    /// <summary>
    /// 基准对比测试：验证优化实现与逐字节基准实现结果一致。
    /// 覆盖边界情况：0, 1, 3, 4, 7, 8, 9, 15, 16, 17, 100 字节。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(100)]
    public void Compute_MatchesBaseline(int length)
    {
        // Arrange
        var data = new byte[length];
        Random.Shared.NextBytes(data);

        // Act
        uint actual = Crc32CHelper.Compute(data);
        uint expected = ComputeBaseline(data);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 纯逐字节基准实现（仅用于测试对比）。
    /// </summary>
    private static uint ComputeBaseline(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc = BitOperations.Crc32C(crc, b);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
