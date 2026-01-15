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
internal static class Crc32CHelper {
    /// <summary>
    /// CRC32C 初始值。
    /// </summary>
    private const uint InitialValue = 0xFFFFFFFF;

    /// <summary>
    /// CRC32C 最终异或值。
    /// </summary>
    private const uint FinalXorValue = 0xFFFFFFFF;

    /// <summary>
    /// 初始化 CRC32C 计算状态。
    /// </summary>
    /// <returns>初始 CRC 状态值。</returns>
    internal static uint Init() => InitialValue;

    /// <summary>
    /// 增量更新 CRC32C 状态（不执行最终异或）。
    /// </summary>
    /// <param name="crc">当前 CRC 状态（来自 <see cref="Init"/> 或前一次 <see cref="Update"/>）。</param>
    /// <param name="data">待计算的数据块。</param>
    /// <returns>更新后的 CRC 状态。</returns>
    /// <remarks>
    /// 性能优化：优先用 ulong 处理 8 字节块，再用 uint 处理 4 字节块，剩余用 byte 处理。
    /// 使用 Unsafe.ReadUnaligned 确保在所有架构上安全处理非对齐读。
    /// </remarks>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data) {
        if (data.IsEmpty) {
            return crc;
        }

        ref byte start = ref MemoryMarshal.GetReference(data);
        int i = 0;

        // 8 字节块处理
        while (i + 8 <= data.Length) {
            crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, i)));
            i += 8;
        }

        // 4 字节块处理（剩余 0-7 字节时最多执行一次）
        if (i + 4 <= data.Length) {
            crc = BitOperations.Crc32C(crc, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, i)));
            i += 4;
        }

        // 逐字节处理剩余（0-3 字节）
        while (i < data.Length) {
            crc = BitOperations.Crc32C(crc, Unsafe.Add(ref start, i++));
        }

        return crc;
    }

    /// <summary>
    /// 完成 CRC32C 计算（执行最终异或）。
    /// </summary>
    /// <param name="crc">CRC 状态（来自 <see cref="Update"/>）。</param>
    /// <returns>最终的 CRC32C 校验和。</returns>
    internal static uint Finalize(uint crc) => crc ^ FinalXorValue;

    /// <summary>
    /// 计算给定数据的 CRC32C 校验和（一次性计算）。
    /// </summary>
    /// <param name="data">待计算的数据。</param>
    /// <returns>CRC32C 校验和。</returns>
    /// <remarks>
    /// 等价于 <c>Finalize(Update(Init(), data))</c>。
    /// </remarks>
    internal static uint Compute(ReadOnlySpan<byte> data)
        => Finalize(Update(Init(), data));
}
