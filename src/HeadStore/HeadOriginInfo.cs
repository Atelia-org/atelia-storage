using Atelia.Data;

namespace Atelia.HeadStore;

/// <summary>已检查的子 head header 声明的自有创建来源；不证明源 ticket 或历史内容健康。</summary>
public readonly struct HeadOriginInfo {
    internal HeadOriginInfo(HeadId childHeadId, HeadId sourceHeadId, SizedPtr sourceEntryTicket) {
        ChildHeadId = childHeadId;
        SourceHeadId = sourceHeadId;
        SourceEntryTicket = sourceEntryTicket;
    }

    /// <summary>声明此来源的正式子 head。</summary>
    public HeadId ChildHeadId { get; }

    /// <summary>本次完整 header 集合中存在的正式来源 head。</summary>
    public HeadId SourceHeadId { get; }

    /// <summary>header 保存的完整来源位置；本查询不读取来源 Entry 或检查其发布资格。</summary>
    public SizedPtr SourceEntryTicket { get; }
}
