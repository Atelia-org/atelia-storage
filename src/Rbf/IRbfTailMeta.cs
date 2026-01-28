using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public interface IRbfTailMeta {
    /// <summary>帧位置凭据（支持"预览→完整读取"工作流）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>TailMeta 数据（可能为 <see cref="ReadOnlySpan{T}.Empty"/>）。</summary>
    ReadOnlySpan<byte> TailMeta { get; }

    /// <summary>是否为墓碑帧。</summary>
    bool IsTombstone { get; }

    // 注意：根据"诚实地贫瘠"原则，不暴露 PayloadLength
}
