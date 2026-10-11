using Atelia.Data;

namespace Atelia.VersionStore;

/// <summary>已检查的子 ref header 声明的自有创建来源；不证明源 ticket 或历史内容健康。</summary>
public readonly struct ForkInfo {
    internal ForkInfo(RefId childRefId, RefId sourceRefId, SizedPtr sourceSnapshotTicket) {
        ChildRefId = childRefId;
        SourceRefId = sourceRefId;
        SourceSnapshotTicket = sourceSnapshotTicket;
    }

    /// <summary>声明此来源的正式子 ref。</summary>
    public RefId ChildRefId { get; }

    /// <summary>本次完整 header 集合中存在的正式来源 ref。</summary>
    public RefId SourceRefId { get; }

    /// <summary>header 保存的完整来源位置；未包装为已检查的 RefRevision。</summary>
    public SizedPtr SourceSnapshotTicket { get; }
}
