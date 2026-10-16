namespace Atelia.HeadStore;

/// <summary>一次完整只读校验成功后的自有统计；不证明应用 payload 或 data 业务闭包。</summary>
public sealed class HeadStoreAudit {
    internal HeadStoreAudit(long headCount, long entryCount, long originCount, long tagBucketCount, long tagCount) {
        HeadCount = headCount;
        EntryCount = entryCount;
        OriginCount = originCount;
        TagBucketCount = tagBucketCount;
        TagCount = tagCount;
    }

    /// <summary>已完整校验的正式 head 数。</summary>
    public long HeadCount { get; }

    /// <summary>全部正式 head 的本地已发布Entry数：普通 SingleEntry 与成立 CU，包含初始节点，不含未成立 CU。</summary>
    public long EntryCount { get; }

    /// <summary>已证明 exact 主链来源与初始 bytes 一致的 fork 数。</summary>
    public long OriginCount { get; }

    /// <summary>已完整校验的实际已建 tag 桶数，包含 header-only 桶。</summary>
    public long TagBucketCount { get; }

    /// <summary>已完整校验 root、元信息及 Ordinal 名称唯一性的 tag 数。</summary>
    public long TagCount { get; }
}
