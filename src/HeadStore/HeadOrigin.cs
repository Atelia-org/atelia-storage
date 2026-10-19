namespace Atelia.HeadStore;

/// <summary>指定 Head 的已检查 header 声明的自有创建来源值。</summary>
/// <remarks>
/// 只由成功的 <see cref="HeadStore.ReadHeadOrigin"/> 取得所选 Head 的声明；
/// 不证明来源 Head 或 Entry 存在、发布成立、历史或全图健康。
/// 值可在 owner 关闭后保留；default 本身不证明成功查询过独立根。
/// </remarks>
public readonly struct HeadOrigin {
    internal HeadOrigin(HeadEntryId? sourceEntryId) {
        SourceEntryId = sourceEntryId;
    }

    /// <summary>固定来源的完整定位值；null 表示所查询 Head 为没有来源的独立根。</summary>
    /// <remarks>这是声明位置；后续 ReadEntry 或 ForkHead 仍须独立检查实际记录和发布资格。</remarks>
    public HeadEntryId? SourceEntryId { get; }
}
