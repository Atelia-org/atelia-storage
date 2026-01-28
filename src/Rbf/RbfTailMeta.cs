using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public readonly ref struct RbfTailMeta : IRbfTailMeta {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> TailMeta { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数（只能由验证路径调用）。</summary>
    internal RbfTailMeta(SizedPtr ticket, uint tag, ReadOnlySpan<byte> tailMeta, bool isTombstone) {
        Ticket = ticket;
        Tag = tag;
        TailMeta = tailMeta;
        IsTombstone = isTombstone;
    }
}
