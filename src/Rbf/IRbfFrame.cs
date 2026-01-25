using Atelia.Data;

namespace Atelia.Rbf;

/// <summary>RBF 帧的公共属性契约。</summary>
public interface IRbfFrame {
    /// <summary>获取帧位置（凭据）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>获取帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>获取帧负载数据。</summary>
    ReadOnlySpan<byte> PayloadAndMeta { get; }

    /// <summary>用户元数据。</summary>
    int TailMetaLength { get; }

    /// <summary>获取是否为墓碑帧。</summary>
    bool IsTombstone { get; }
}
