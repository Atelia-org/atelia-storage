namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 常量定义（internal 避免过早暴露 API surface）。
/// </summary>
/// <remarks>
/// 规范引用：@[F-FENCE-VALUE-IS-RBF1-ASCII-4B]
/// </remarks>
internal static class RbfConstants {
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
    public const int GenesisLength = 4;
}
