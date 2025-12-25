namespace Atelia.Rbf;

/// <summary>
/// RBF CRC32C 计算工具。
/// </summary>
/// <remarks>
/// <para><b>[F-CRC32C-ALGORITHM]</b>: CRC 算法为 CRC32C（Castagnoli）。</para>
/// <list type="bullet">
///   <item>初始值：0xFFFFFFFF</item>
///   <item>最终异或：0xFFFFFFFF</item>
///   <item>Reflected 多项式：0x82F63B78（Normal 形式：0x1EDC6F41）</item>
/// </list>
/// <para>使用查找表优化的软件实现。</para>
/// </remarks>
public static class RbfCrc
{
    // CRC32C (Castagnoli) 查找表，Reflected 多项式 0x82F63B78
    private static readonly uint[] _table = GenerateTable();

    private static uint[] GenerateTable()
    {
        const uint polynomial = 0x82F63B78; // Reflected Castagnoli polynomial
        var table = new uint[256];
        
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        
        return table;
    }

    /// <summary>
    /// 计算 CRC32C 校验和。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-CRC32C-COVERAGE]</b>:</para>
    /// <code>CRC32C = crc32c(FrameTag + Payload + FrameStatus + TailLen)</code>
    /// <para>注意：调用方需确保传入的数据包含正确的覆盖范围。</para>
    /// </remarks>
    /// <param name="data">要计算校验和的数据。</param>
    /// <returns>CRC32C 校验和（u32）。</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF; // 初始值
        
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ _table[(byte)((crc ^ b) & 0xFF)];
        }
        
        return crc ^ 0xFFFFFFFF; // 最终异或
    }

    /// <summary>
    /// 验证 CRC32C 校验和。
    /// </summary>
    /// <param name="data">数据。</param>
    /// <param name="expectedCrc">期望的 CRC32C 值。</param>
    /// <returns>校验和是否匹配。</returns>
    public static bool Verify(ReadOnlySpan<byte> data, uint expectedCrc)
    {
        return Compute(data) == expectedCrc;
    }
}
