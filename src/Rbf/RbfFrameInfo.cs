using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>
/// 帧元信息（不含 Payload）。
/// </summary>
/// <remarks>
/// <para>用于 ScanReverse 产出，支持不读取 payload 的元信息迭代。</para>
/// <para>PayloadLength 与 UserMetaLength 从 TrailerCodeword 解码得出。</para>
/// <para>规范引用：@[A-RBF-FRAME-INFO]</para>
/// </remarks>
public readonly record struct RbfFrameInfo(
    SizedPtr Ticket,
    uint Tag,
    int PayloadLength,
    int UserMetaLength,
    bool IsTombstone
);
