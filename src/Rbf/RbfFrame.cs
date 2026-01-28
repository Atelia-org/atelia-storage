using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>RBF 帧数据结构。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrameInto 的 buffer）。
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// </remarks>
public readonly ref struct RbfFrame : IRbfFrame {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> PayloadAndMeta { get; }

    /// <inheritdoc/>
    public int TailMetaLength { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数（只能由验证路径调用）。</summary>
    internal RbfFrame(SizedPtr ticket, uint tag, ReadOnlySpan<byte> payloadAndMeta, int tailMetaLength, bool isTombstone) {
        Ticket = ticket;
        Tag = tag;
        PayloadAndMeta = payloadAndMeta;
        TailMetaLength = tailMetaLength;
        IsTombstone = isTombstone;
    }
}
