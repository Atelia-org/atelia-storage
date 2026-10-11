namespace Atelia.VersionStore;

/// <summary>一次完整只读校验成功后的自有统计；不证明应用 payload 或 data 业务闭包。</summary>
public sealed class VersionStoreAudit {
    internal VersionStoreAudit(long refCount, long snapshotCount, long forkCount, long tagBucketCount, long tagCount) {
        RefCount = refCount;
        SnapshotCount = snapshotCount;
        ForkCount = forkCount;
        TagBucketCount = tagBucketCount;
        TagCount = tagCount;
    }

    /// <summary>已完整校验的正式 ref 数。</summary>
    public long RefCount { get; }

    /// <summary>全部正式 ref 的本地 Snapshot 数，包含各自初始节点。</summary>
    public long SnapshotCount { get; }

    /// <summary>已证明 exact 主链来源与初始 bytes 一致的 fork 数。</summary>
    public long ForkCount { get; }

    /// <summary>已完整校验的实际已建 tag 桶数，包含 header-only 桶。</summary>
    public long TagBucketCount { get; }

    /// <summary>已完整校验 root、元信息及 Ordinal 名称唯一性的 tag 数。</summary>
    public long TagCount { get; }
}
