// CRC32C 计算工具类
// 规范引用: rbf-format.md @[F-CRC-IS-CRC32C-CASTAGNOLI-REFLECTED]

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Atelia.Rbf.Internal;

/// <summary>
/// CRC32C (Castagnoli) 计算工具。
/// 采用 Reflected I/O 约定，兼容 IETF RFC 3720 (iSCSI CRC)。
/// </summary>
internal static class Crc32CHelper
{
    /// <summary>
    /// 计算给定数据的 CRC32C 校验和。
    /// </summary>
    /// <param name="data">待计算的数据。</param>
    /// <returns>CRC32C 校验和。</returns>
    /// <remarks>
    /// 性能优化：优先用 ulong 处理 8 字节块，再用 uint 处理 4 字节块，剩余用 byte 处理。
    /// 初始值：0xFFFFFFFF，最终异或：0xFFFFFFFF。
    /// 使用 Unsafe.ReadUnaligned 确保在所有架构上安全处理非对齐读。
    /// </remarks>
    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        if (data.IsEmpty)
        {
            return crc ^ 0xFFFFFFFF;
        }

        ref byte start = ref MemoryMarshal.GetReference(data);
        int i = 0;

        // 8 字节块处理
        while (i + 8 <= data.Length)
        {
            crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));
            i += 8;
        }

        // 4 字节块处理（剩余 0-7 字节时最多执行一次）
        if (i + 4 <= data.Length)
        {
            crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, i)));
            i += 4;
        }

        // 逐字节处理剩余（0-3 字节）
        while (i < data.Length)
        {
            crc = BitOperations.Crc32C(crc, Unsafe.Add(ref start, i++));
        }

        return crc ^ 0xFFFFFFFF;
    }
}
