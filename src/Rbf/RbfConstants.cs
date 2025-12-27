namespace Atelia.Rbf;

/// <summary>
/// RBF (Reversible Binary Framing) 核心常量定义。
/// </summary>
/// <remarks>
/// <para>RBF 是一种 crash-safe 的 append-only 二进制帧格式。</para>
/// <para>规范文档: atelia/docs/StateJournal/rbf-format.md</para>
/// </remarks>
public static class RbfConstants {
    /// <summary>
    /// RBF 魔数 "RBF1" 的 little-endian 表示。
    /// 用于帧边界识别和崩溃恢复时的重同步。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-FENCE-DEFINITION]</b>: Fence = 0x31464252 ('RBF1' in ASCII, little-endian)</para>
    /// <para>ASCII: 'R'=0x52, 'B'=0x42, 'F'=0x46, '1'=0x31</para>
    /// <para>Little-endian uint32: 0x31464252</para>
    /// </remarks>
    public const uint Fence = 0x31464252;

    /// <summary>
    /// Fence 的字节序列表示（用于写入和扫描）。
    /// </summary>
    /// <remarks>
    /// 字节顺序为 little-endian，即 ['R', 'B', 'F', '1']。
    /// </remarks>
    public static ReadOnlySpan<byte> FenceBytes => [0x52, 0x42, 0x46, 0x31];

    /// <summary>
    /// Fence 的字节长度（4 字节）。
    /// </summary>
    public const int FenceLength = 4;
}
