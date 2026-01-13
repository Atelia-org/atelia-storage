using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧数据结构。
/// </summary>
/// <remarks>
/// <para>只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrame 的 buffer）。</para>
/// </remarks>
public readonly ref struct RbfFrame {
    /// <summary>帧位置（凭据）。</summary>
    public SizedPtr Ptr { get; init; }

    /// <summary>帧类型标识符。</summary>
    public uint Tag { get; init; }

    /// <summary>帧负载数据。</summary>
    public ReadOnlySpan<byte> Payload { get; init; }

    /// <summary>是否为墓碑帧。</summary>
    public bool IsTombstone { get; init; }
}
