namespace Atelia.Rbf;

/// <summary>
/// RBF 帧布局计算工具。
/// </summary>
/// <remarks>
/// <para>提供 FrameBytes 长度、对齐、偏移量的计算方法。</para>
/// <para>规范依据：[F-HEADLEN-FORMULA], [F-STATUSLEN-FORMULA], [F-FRAME-4B-ALIGNMENT]</para>
/// </remarks>
public static class RbfLayout {
    /// <summary>
    /// 固定开销：HeadLen(4) + FrameTag(4) + TailLen(4) + CRC32C(4) = 16 字节。
    /// </summary>
    public const int FixedOverhead = 16;

    /// <summary>
    /// 最小帧长度：当 PayloadLen = 0 时，StatusLen = 4，故 HeadLen = 16 + 0 + 4 = 20。
    /// </summary>
    public const int MinFrameLength = 20;

    /// <summary>
    /// HeadLen 字段偏移（相对于 FrameBytes 起点）。
    /// </summary>
    public const int HeadLenOffset = 0;

    /// <summary>
    /// FrameTag 字段偏移（相对于 FrameBytes 起点）。
    /// </summary>
    public const int FrameTagOffset = 4;

    /// <summary>
    /// Payload 起始偏移（相对于 FrameBytes 起点）。
    /// </summary>
    public const int PayloadOffset = 8;

    /// <summary>
    /// 计算 FrameStatus 字段的长度（1-4 字节）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-STATUSLEN-FORMULA]</b>:</para>
    /// <code>StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)</code>
    /// <para>保证 (PayloadLen + StatusLen) % 4 == 0。</para>
    /// </remarks>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>StatusLen ∈ {1, 2, 3, 4}。</returns>
    public static int CalculateStatusLength(int payloadLength) {
        // StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
        return 1 + ((4 - ((payloadLength + 1) % 4)) % 4);
    }

    /// <summary>
    /// 计算 FrameBytes 总长度（HeadLen/TailLen 的值）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-HEADLEN-FORMULA]</b>:</para>
    /// <code>HeadLen = 16 + PayloadLen + StatusLen</code>
    /// </remarks>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>FrameBytes 总长度（字节）。</returns>
    public static int CalculateFrameLength(int payloadLength) {
        return FixedOverhead + payloadLength + CalculateStatusLength(payloadLength);
    }

    /// <summary>
    /// 计算 FrameStatus 字段的文件偏移。
    /// </summary>
    /// <param name="frameStart">帧起始偏移（HeadLen 字段位置）。</param>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>FrameStatus 字段的文件偏移。</returns>
    public static long CalculateStatusOffset(long frameStart, int payloadLength) {
        return frameStart + PayloadOffset + payloadLength;
    }

    /// <summary>
    /// 计算 TailLen 字段的文件偏移。
    /// </summary>
    /// <param name="frameStart">帧起始偏移（HeadLen 字段位置）。</param>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>TailLen 字段的文件偏移。</returns>
    public static long CalculateTailLenOffset(long frameStart, int payloadLength) {
        return frameStart + PayloadOffset + payloadLength + CalculateStatusLength(payloadLength);
    }

    /// <summary>
    /// 计算 CRC32C 字段的文件偏移。
    /// </summary>
    /// <param name="frameStart">帧起始偏移（HeadLen 字段位置）。</param>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>CRC32C 字段的文件偏移。</returns>
    public static long CalculateCrcOffset(long frameStart, int payloadLength) {
        return CalculateTailLenOffset(frameStart, payloadLength) + 4;
    }

    /// <summary>
    /// 计算帧结束后 Fence 的文件偏移。
    /// </summary>
    /// <param name="frameStart">帧起始偏移（HeadLen 字段位置）。</param>
    /// <param name="payloadLength">Payload 长度。</param>
    /// <returns>尾部 Fence 的文件偏移。</returns>
    public static long CalculateTrailingFenceOffset(long frameStart, int payloadLength) {
        return frameStart + CalculateFrameLength(payloadLength);
    }

    /// <summary>
    /// 检查地址是否 4 字节对齐。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-FRAME-4B-ALIGNMENT]</b>: Frame 起点 MUST 4B 对齐。</para>
    /// </remarks>
    /// <param name="offset">文件偏移。</param>
    /// <returns>是否 4 字节对齐。</returns>
    public static bool Is4ByteAligned(long offset) => (offset & 3) == 0;

    /// <summary>
    /// 将偏移量向下对齐到 4 字节边界。
    /// </summary>
    /// <param name="offset">文件偏移。</param>
    /// <returns>对齐后的偏移。</returns>
    public static long AlignDown4(long offset) => offset & ~3L;
}
