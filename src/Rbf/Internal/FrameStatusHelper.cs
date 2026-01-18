// FrameStatus 编码工具类
// 规范引用: rbf-format.md
//   @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]：StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
//   @[F-FRAMESTATUS-RESERVED-BITS-ZERO]：Bit7=Tombstone, Bit6-2=Reserved(0), Bit1-0=StatusLen-1
//   @[F-FRAMESTATUS-FILL]：全字节同值

using System.Diagnostics;

namespace Atelia.Rbf.Internal;

/// <summary>
/// FrameStatus 编码工具。
/// </summary>
internal static class FrameStatusHelper {
    /// <summary>
    /// Tombstone 标志位掩码（Bit7）。
    /// </summary>
    internal const byte TombstoneMask = 0x80;

    /// <summary>
    /// 保留位掩码（Bit6-2）。
    /// </summary>
    internal const byte ReservedMask = 0x7C;

    /// <summary>
    /// StatusLen 字段掩码（Bit1-0）。
    /// </summary>
    internal const byte StatusLenMask = 0x03;

    /// <summary>
    /// 编码单个状态字节。
    /// </summary>
    /// <param name="isTombstone">是否为 Tombstone 帧。</param>
    /// <param name="statusLen">StatusLen 值（1-4）。</param>
    /// <returns>编码后的状态字节。</returns>
    /// <remarks>
    /// 位布局：
    ///   Bit7     = Tombstone 标志（1=墓碑，0=有效）
    ///   Bit6-2   = Reserved（必须为 0）
    ///   Bit1-0   = StatusLen-1（0-3 表示 1-4 字节）
    /// </remarks>
    internal static byte EncodeStatusByte(bool isTombstone, int statusLen) {
        const int MinStatusLength = FrameLayout.MinStatusLength, MaxStatusLength = FrameLayout.MaxStatusLength;
        Debug.Assert(MinStatusLength <= statusLen && statusLen <= MaxStatusLength, $"statusLen must be in range [{MinStatusLength},{MaxStatusLength}].");

        // Bit1-0 = statusLen - 1
        int bits = statusLen - MinStatusLength;

        // Bit7 = isTombstone ? 1 : 0
        if (isTombstone) {
            bits |= TombstoneMask;
        }

        return (byte)bits;
    }

    /// <summary>
    /// 填充状态区（所有字节同值）。
    /// </summary>
    /// <param name="dest">目标缓冲区，长度必须等于 statusLen。</param>
    /// <param name="isTombstone">是否为 Tombstone 帧。</param>
    /// <param name="statusLen">StatusLen 值（1-4）。</param>
    /// <exception cref="ArgumentException">dest.Length 不等于 statusLen 时抛出。</exception>
    internal static void FillStatus(Span<byte> dest, bool isTombstone, int statusLen) {
        Debug.Assert(dest.Length == statusLen, $"dest.Length ({dest.Length}) must equal statusLen ({statusLen}).", nameof(dest));

        byte statusByte = EncodeStatusByte(isTombstone, statusLen);
        dest.Fill(statusByte);
    }

    /// <summary>
    /// 从 FrameStatus 字节解码信息。
    /// </summary>
    /// <param name="statusByte">FrameStatus 的任意一个字节（@[F-FRAMESTATUS-FILL] 保证全字节同值）。</param>
    /// <param name="isTombstone">输出：是否为墓碑帧（Bit7）。</param>
    /// <param name="statusLen">输出：StatusLen（1-4，来自 Bit1-0 + 1）。</param>
    internal static AteliaError? DecodeStatusByte(byte statusByte, out bool isTombstone, out int statusLen) {
        // Bit7 = Tombstone 标志
        isTombstone = (statusByte & TombstoneMask) != 0;

        // Bit1-0 = StatusLen - 1，所以 StatusLen = (Bit1-0) + 1
        statusLen = (statusByte & StatusLenMask) + FrameLayout.MinStatusLength;

        // 后置检查，以多返回一些诊断信息。
        return CheckStatusByte(statusByte);
    }

    internal static AteliaError? CheckStatusByte(byte statusByte) {
        // 检查保留位 Bit6-2 是否为零
        if ((statusByte & ReservedMask) != 0) {
            return new RbfFramingError($"Invalid status byte 0x{statusByte:X2}: reserved bits are non-zero.");
        }
        return null;
    }
}
