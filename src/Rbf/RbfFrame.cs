namespace Atelia.Rbf;

/// <summary>
/// 表示一个已解析的 RBF 帧的元数据。
/// </summary>
/// <remarks>
/// <para><b>[F-FRAME-LAYOUT]</b>: FrameBytes 布局（不含前后 Fence）：</para>
/// <list type="table">
///   <item><term>偏移 0</term><description>HeadLen (u32 LE, 4B)</description></item>
///   <item><term>偏移 4</term><description>FrameTag (u32 LE, 4B)</description></item>
///   <item><term>偏移 8</term><description>Payload (N bytes)</description></item>
///   <item><term>偏移 8+N</term><description>FrameStatus (1-4B)</description></item>
///   <item><term>偏移 8+N+StatusLen</term><description>TailLen (u32 LE, 4B)</description></item>
///   <item><term>偏移 12+N+StatusLen</term><description>CRC32C (u32 LE, 4B)</description></item>
/// </list>
/// </remarks>
/// <param name="FileOffset">帧在文件中的起始偏移（HeadLen 字段位置）。</param>
/// <param name="FrameTag">帧类型标识符（4 字节 u32 LE）。</param>
/// <param name="PayloadOffset">Payload 在文件中的起始偏移。</param>
/// <param name="PayloadLength">Payload 长度（字节）。</param>
/// <param name="Status">帧状态（Valid 或 Tombstone）。</param>
public readonly record struct RbfFrame(
    long FileOffset,
    uint FrameTag,
    long PayloadOffset,
    int PayloadLength,
    FrameStatus Status)
{
    /// <summary>
    /// HeadLen/TailLen 的值（FrameBytes 总长度）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-HEADLEN-FORMULA]</b>:</para>
    /// <code>HeadLen = 16 + PayloadLen + StatusLen</code>
    /// </remarks>
    public int FrameLength => RbfLayout.CalculateFrameLength(PayloadLength);

    /// <summary>
    /// FrameStatus 字段的长度（1-4 字节）。
    /// </summary>
    public int StatusLength => RbfLayout.CalculateStatusLength(PayloadLength);
}
