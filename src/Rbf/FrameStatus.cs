namespace Atelia.Rbf;

/// <summary>
/// RBF 帧状态标记。
/// </summary>
/// <remarks>
/// <para><b>[F-FRAMESTATUS-VALUES]</b>: FrameStatus 是 1-4 字节的帧状态标记，所有字节 MUST 填相同值。</para>
/// <para>FrameStatus 承载帧有效性（Layer 0 元信息），与 FrameTag（Layer 1 业务分类）职责分离。</para>
/// </remarks>
public enum FrameStatus : byte
{
    /// <summary>
    /// 正常帧（业务数据）。
    /// </summary>
    Valid = 0x00,

    /// <summary>
    /// 墓碑帧（Auto-Abort / 逻辑删除）。
    /// </summary>
    Tombstone = 0xFF,
}
