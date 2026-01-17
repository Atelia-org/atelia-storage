using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// RBF 帧数据结构。
/// </summary>
/// <remarks>
/// <para>只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrameInto 的 buffer）。</para>
/// <para><b>属性契约</b>：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。</para>
/// </remarks>
public readonly ref struct RbfFrame : IRbfFrame {
    /// <inheritdoc/>
    public SizedPtr Ptr { get; init; }

    /// <inheritdoc/>
    public uint Tag { get; init; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload { get; init; }

    /// <inheritdoc/>
    public bool IsTombstone { get; init; }
}
