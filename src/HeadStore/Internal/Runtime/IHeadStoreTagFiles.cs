using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Runtime;

/// <summary>backend 已取得正式桶路径资格，或已证明 canonical 将建路径健康缺失的内部目标。</summary>
/// <remarks>缺失目录由 barrier 后的 EnsureTagsDirectory 取得普通目录与同文件系统资格。</remarks>
internal readonly struct QualifiedTagBucketTarget(uint bucketId, string path) {
    internal uint BucketId { get; } = bucketId;
    internal string Path { get; } = path;
}

/// <summary>可选 tags 目录、正式目标桶与唯一 pending 的窄接线；不解释桶内容或拥有长期资源。</summary>
/// <remarks>
/// Resolve/Prepare 完成已建 tags 目录的全部直接项名称、类型和同文件系统资格，不读取桶内容。
/// 只有 Open 打开请求桶，其他桶内容不由本次证明。
/// writable Open 在公共 RBF 工厂前保护初始化长度，并拒绝触及初始化前缀的恢复报告。
/// 路径和长度前检失败不调用 marker；真正 writable 工厂失败、违规恢复及 owned 清理失败调用 marker。
/// CreatePending 复用 IHeadStoreFiles；空桶 rename 不代表 TagRecord 已发布。
/// </remarks>
internal interface IHeadStoreTagFiles {
    /// <summary>只取得目标路径资格；null 仅表示已证明目录或目标桶健康缺失，不创建或读取桶内容。</summary>
    QualifiedTagBucketTarget? ResolveTagBucket(uint bucketId, Action markOwnedCleanupFault);
    /// <summary>流式发现实际已建桶；将枚举器所有权移交 owner，缺目录交付空枚举器。</summary>
    /// <remarks>每次 MoveNext 先后检查状态并取得当前项路径资格；必要 Dispose 失败调用 marker。</remarks>
    IEnumerator<QualifiedTagBucketTarget> EnumerateTagBucketTargets(Action checkState, Action markOwnedCleanupFault);
    /// <summary>公共工厂打开目标并移交 Idle 句柄；失败前取得的资源由 backend 单次关闭。</summary>
    IRbfFile OpenTagBucket(QualifiedTagBucketTarget target, bool readOnly, Action markOwnedCleanupFault);
    /// <summary>仅在 data barrier 后调用；建立或资格化精确小写 tags 普通目录。</summary>
    void EnsureTagsDirectory(Action markOwnedCleanupFault);
    /// <summary>不创建目录或文件；取得指定健康缺失目标的发布路径。</summary>
    AteliaResult<QualifiedTagBucketTarget> PrepareNewTagBucket(uint bucketId, Action markOwnedCleanupFault);
    /// <summary>发布已关闭的 header-only pending；native 同文件系统 rename 不覆盖正式文件。</summary>
    void PublishPendingTagBucket(QualifiedTagBucketTarget target, Action markOwnedCleanupFault);
}
