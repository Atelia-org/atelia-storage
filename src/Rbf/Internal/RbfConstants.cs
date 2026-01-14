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
    public const int FrameBytesFixedOverhead = HeadLenFieldLength + TagFieldLength + TailLenFieldLength + CrcFieldLength;

    // === 对齐 (Alignment) ===

    /// <summary>
    /// 帧 4 字节对齐要求。
    /// </summary>
    public const int FrameAlignment = 4;
}
