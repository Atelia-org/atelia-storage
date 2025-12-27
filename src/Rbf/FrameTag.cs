namespace Atelia.Rbf;

/// <summary>
/// RBF 帧类型标识符。
/// </summary>
/// <remarks>
/// <para><b>[F-FRAMETAG-DEFINITION]</b>: FrameTag 是 4 字节的帧类型标识符。
/// RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。</para>
/// <para><b>[F-FRAMETAG-WIRE-ENCODING]</b>: FrameTag 在 wire format 上为 u32 LE。</para>
/// <para>RBF 层不保留任何 FrameTag 值，全部值域由上层定义。</para>
/// </remarks>
/// <param name="Value">帧类型标识符的原始值。</param>
public readonly record struct FrameTag(uint Value) {
    /// <summary>
    /// 从 4 个 ASCII 字符创建 FrameTag（fourCC 风格）。
    /// </summary>
    /// <remarks>
    /// 例如 <c>FrameTag.FromChars('M', 'E', 'T', 'A')</c> 创建 "META" 标签。
    /// </remarks>
    /// <param name="c0">第一个字符（最低字节）。</param>
    /// <param name="c1">第二个字符。</param>
    /// <param name="c2">第三个字符。</param>
    /// <param name="c3">第四个字符（最高字节）。</param>
    /// <returns>FrameTag 实例。</returns>
    public static FrameTag FromChars(char c0, char c1, char c2, char c3) {
        uint value = (uint)c0 | ((uint)c1 << 8) | ((uint)c2 << 16) | ((uint)c3 << 24);
        return new FrameTag(value);
    }

    /// <summary>
    /// 隐式转换为 uint。
    /// </summary>
    public static implicit operator uint(FrameTag tag) => tag.Value;

    /// <summary>
    /// 显式从 uint 转换。
    /// </summary>
    public static explicit operator FrameTag(uint value) => new(value);
}
