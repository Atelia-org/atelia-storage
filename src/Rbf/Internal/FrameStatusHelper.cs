// FrameStatus 编码工具类
// 规范引用: rbf-format.md
//   @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]：StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
//   @[F-FRAMESTATUS-RESERVED-BITS-ZERO]：Bit7=Tombstone, Bit6-2=Reserved(0), Bit1-0=StatusLen-1
//   @[F-FRAMESTATUS-FILL]：全字节同值

namespace Atelia.Rbf.Internal;

/// <summary>
/// FrameStatus 编码工具。
/// </summary>
internal static class FrameStatusHelper
{
    /// <summary>
    /// 计算 StatusLen（状态区字节数）。
    /// </summary>
    /// <param name="payloadLen">Payload 长度。</param>
    /// <returns>StatusLen，范围 1-4。</returns>
    /// <remarks>
    /// 公式确保 (payloadLen + statusLen) % 4 == 0，即 4 字节对齐。
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">payloadLen 为负数时抛出。</exception>
    internal static int ComputeStatusLen(int payloadLen)
    {
        if (payloadLen < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLen), payloadLen, "payloadLen must be non-negative.");
        }

        // StatusLen = 1 + ((4 - ((payloadLen + 1) % 4)) % 4)
        return 1 + ((4 - ((payloadLen + 1) % 4)) % 4);
    }

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
    /// <exception cref="ArgumentOutOfRangeException">statusLen 不在 [1,4] 范围时抛出。</exception>
    internal static byte EncodeStatusByte(bool isTombstone, int statusLen)
    {
        if (statusLen < 1 || statusLen > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(statusLen), statusLen, "statusLen must be in range [1,4].");
        }

        // Bit1-0 = statusLen - 1
        int bits = statusLen - 1;

        // Bit7 = isTombstone ? 1 : 0
        if (isTombstone)
        {
            bits |= 0x80;
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
    internal static void FillStatus(Span<byte> dest, bool isTombstone, int statusLen)
    {
        if (dest.Length != statusLen)
        {
            throw new ArgumentException($"dest.Length ({dest.Length}) must equal statusLen ({statusLen}).", nameof(dest));
        }

        byte statusByte = EncodeStatusByte(isTombstone, statusLen);
        dest.Fill(statusByte);
    }
}
