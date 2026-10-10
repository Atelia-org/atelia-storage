using Atelia.Rbf;

namespace Atelia.VersionStore.Internal.Runtime;

/// <summary>已由 backend 按正式 local 路径取得 canonical、ordinary/no-follow 资格的目标。</summary>
/// <remarks>这是内部接线 token；它不自行取得平台资格，也不对普通 consumer 开放路径导入。</remarks>
internal readonly struct QualifiedRefTarget(uint localId, string path) {
    internal uint LocalId { get; } = localId;
    internal string Path { get; } = path;
}

/// <summary>正式目标定位与 public RBF 工厂之间的窄内部接线。</summary>
/// <remarks>
/// backend 本身不保留 owned 资源。ResolveRef 只检查正式路径资格，不恢复或写入。
/// OpenRef 依 owner 模式调用 public OpenExisting/OpenReadOnlyExisting，并把 Idle 句柄所有权移交 owner。
/// 若失败前取得临时资源，backend 必须先清空资源槽并单次清理；实际清理失败调用 markOwnedCleanupFault。
/// ResolveRef 的必要枚举器/临时资源清理失败也使用同一 marker；调用 marker 后必须抛出观察的失败。
/// 本轮仅有测试 backend，不能据此宣称目录、no-follow、锁或平台工厂已经实施。
/// </remarks>
internal interface IVersionStoreFiles {
    AteliaResult<QualifiedRefTarget> ResolveRef(uint localId, Action markOwnedCleanupFault);
    IRbfFile OpenRef(QualifiedRefTarget target, bool readOnly, Action markOwnedCleanupFault);
}
