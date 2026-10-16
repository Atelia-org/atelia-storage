using Atelia.Rbf;

namespace Atelia.HeadStore.Internal.Runtime;

/// <summary>已由 backend 按正式 local 路径取得 canonical、ordinary/no-follow 资格的目标。</summary>
/// <remarks>这是内部接线 token；它不自行取得平台资格，也不对普通 consumer 开放路径导入。</remarks>
internal readonly struct QualifiedHeadTarget(uint localId, string path) {
    internal uint LocalId { get; } = localId;
    internal string Path { get; } = path;
}

/// <summary>正式目标定位与 public RBF 工厂之间的窄内部接线。</summary>
/// <remarks>
/// backend 本身不保留 owned 资源。ResolveHead 只检查正式路径资格，不恢复或写入。
/// OpenHead 依 owner 模式调用 public OpenExisting/OpenReadOnlyExisting，并把 Idle 句柄所有权移交 owner。
/// 若失败前取得临时资源，backend 必须先清空资源槽并单次清理；实际清理失败调用 markOwnedCleanupFault。
/// 所有必要枚举器/临时资源清理失败也使用同一 marker；调用 marker 后必须抛出观察的失败。
/// </remarks>
internal interface IHeadStoreFiles {
    AteliaResult<QualifiedHeadTarget> ResolveHead(uint localId, Action markOwnedCleanupFault);
    IRbfFile OpenHead(QualifiedHeadTarget target, bool readOnly, Action markOwnedCleanupFault);
    /// <summary>完整发现全部正式路径身份；返回前完成必要资格及枚举资源清理，不读取 head 内容。</summary>
    AteliaResult<IReadOnlyList<uint>> ListHeadIds(Action markOwnedCleanupFault);
    /// <summary>逐项资格化正式目标；callback 的非 null 错误停止发现，null 仅在正常 EOF 及清理成功后返回。</summary>
    /// <remarks>每项名称、类型和同文件系统资格先于 callback；不物化全集。状态回调覆盖发现、逐项与必要关闭边界。</remarks>
    AteliaError? VisitHeadTargets(Func<QualifiedHeadTarget, AteliaError?> visit, Action checkState,
        Action markOwnedCleanupFault);
    /// <summary>只为指定正式目标取得资格；存在时拒绝，不创建或恢复文件。</summary>
    AteliaResult<QualifiedHeadTarget> PrepareNewHead(uint localId, Action markOwnedCleanupFault);
    /// <summary>CreateNew 唯一 pending，成功后移交新 RBF3 Idle 句柄；失败资源由 backend 单次清理。</summary>
    IRbfFile CreatePending(Action markOwnedCleanupFault);
    /// <summary>执行资格前检及 native 同文件系统、不覆盖 rename；原生移动成功后立即返回，不读回或安装状态。</summary>
    void PublishPending(QualifiedHeadTarget target, Action markOwnedCleanupFault);
}
