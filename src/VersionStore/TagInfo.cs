using Atelia.Data;

namespace Atelia.VersionStore;

/// <summary>自有且不可变的 tag 元信息；不授予 root、来源读取或 RefRevision 能力。</summary>
public sealed class TagInfo {
    internal TagInfo(string name, long recordedAtUnixMs, RefId? relatedRefId, SizedPtr? relatedTicket) {
        Name = name;
        RecordedAtUnixMs = recordedAtUnixMs;
        RelatedRefId = relatedRefId;
        RelatedTicket = relatedTicket;
    }

    /// <summary>原样保留的 tag 名称。</summary>
    public string Name { get; }

    /// <summary>创建私有准备时取得的 UTC Unix 毫秒；不保证单调。</summary>
    public long RecordedAtUnixMs { get; }

    /// <summary>可选关联的同库 ref 坐标；不证明来源仍可用。</summary>
    public RefId? RelatedRefId { get; }

    /// <summary>可选关联的完整 ticket，与 RelatedRefId 同时存在或同时缺失。</summary>
    public SizedPtr? RelatedTicket { get; }
}
