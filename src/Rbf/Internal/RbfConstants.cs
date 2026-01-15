namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 常量定义（internal 避免过早暴露 API surface）。
/// </summary>
/// <remarks>
/// 规范引用：@[F-FENCE-VALUE-IS-RBF1-ASCII-4B]
/// </remarks>
internal static class RbfConstants {
    // === Fence / Genesis ===

    /// <summary>
    /// Fence / Genesis 值：ASCII "RBF1" (0x52 0x42 0x46 0x31)。
    /// </summary>
    public static ReadOnlySpan<byte> Fence => "RBF1"u8;

    /// <summary>
    /// Fence 长度（字节）。
    /// </summary>
    public const int FenceLength = 4;

    /// <summary>
    /// Genesis Fence 长度（字节）—— 语义别名，等于 <see cref="FenceLength"/>。
    /// </summary>
    public const int GenesisLength = FenceLength;

    // === 字段长度 (Field Lengths) ===

    /// <summary>
    /// HeadLen 字段长度（4 字节）。
    /// </summary>
    public const int HeadLenFieldLength = 4;

    /// <summary>
    /// Tag 字段长度（4 字节）。
    /// </summary>
    public const int TagFieldLength = 4;

    /// <summary>
    /// TailLen 字段长度（4 字节）。
    /// </summary>
    public const int TailLenFieldLength = 4;

    /// <summary>
    /// CRC32C 字段长度（4 字节）。
    /// </summary>
    public const int CrcFieldLength = 4;

    // === 偏移量 (Field Offsets) ===

    /// <summary>
    /// Tag 字段相对帧起点的偏移量。
    /// </summary>
    public const int TagFieldOffset = HeadLenFieldLength;

    /// <summary>
    /// Payload 字段相对帧起点的偏移量。
    /// </summary>
    public const int PayloadFieldOffset = HeadLenFieldLength + TagFieldLength;

    // === 固定开销 (Fixed Overhead) ===

    /// <summary>
    /// FrameBytes 固定开销 = HeadLen(4) + Tag(4) + TailLen(4) + CRC(4) = 16 字节。
    /// </summary>
    /// <remarks>
    /// 帧总长度 = FrameBytesFixedOverhead + PayloadLength + StatusLength。
    /// </remarks>
    public const int FrameFixedOverheadBytes = HeadLenFieldLength + TagFieldLength + TailLenFieldLength + CrcFieldLength;

    public const int MinStatusLen = 1;
    public const int FrameMiniOverheadBytes = MinStatusLen + FrameFixedOverheadBytes;

    /// <summary>
    /// 帧尾固定部分长度 = TailLen(4) + CRC(4) = 8 字节。
    /// </summary>
    /// <remarks>
    /// 用于从 HeadLen 反向计算 TailLen/CRC 偏移：
    /// - TailLen 偏移 = HeadLen - TailSuffixLength
    /// - CRC 偏移 = HeadLen - CrcFieldLength
    /// </remarks>
    public const int TailSuffixLength = TailLenFieldLength + CrcFieldLength;

    /// <summary>
    /// 从帧尾到 FrameStatus 最后一字节的偏移 = TailLen(4) + CRC(4) + 1 = 9 字节。
    /// </summary>
    /// <remarks>
    /// StatusByte 偏移 = HeadLen - StatusByteFromTailOffset
    /// </remarks>
    public const int StatusByteFromTailOffset = TailSuffixLength + FrameStatusHelper.MinStatusLength;

    // === 对齐 (Alignment) ===

    /// <summary>
    /// 帧 4 字节对齐要求。
    /// </summary>
    public const int FrameAlignment = 4;
    // 帧布局计算 (Frame Layout)
    /// <summary>
    /// 计算 FrameBytes 总长度（HeadLen 字段值）。
    /// </summary>
    /// <param name="payloadLen">Payload 字节数。</param>
    /// <param name="statusLen">输出参数：计算得到的 StatusLen（1-4 字节）。</param>
    /// <returns>HeadLen = 16 + payloadLen + statusLen（其中 16 = HeadLen(4) + Tag(4) + TailLen(4) + CRC(4)）</returns>
    /// <remarks>
    /// <para>布局：HeadLen(4) + Tag(4) + Payload(N) + Status(1-4) + TailLen(4) + CRC(4)</para>
    /// <para>参见 @[F-FRAMEBYTES-FIELD-OFFSETS]。</para>
    /// <para>StatusLen 由 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT] 定义的公式计算，保证 4 字节对齐。</para>
    /// </remarks>
    public static int ComputeFrameLen(int payloadLen, out int statusLen) {
        statusLen = FrameStatusHelper.ComputeStatusLen(payloadLen);
        int headLen = FrameFixedOverheadBytes + payloadLen + statusLen;
        return headLen;
    }
}
