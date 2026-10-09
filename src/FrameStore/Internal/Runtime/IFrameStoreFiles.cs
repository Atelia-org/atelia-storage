using Atelia.Rbf;

namespace Atelia.FrameStore.Internal.Runtime;

/// <summary>
/// 目录生命周期的窄集成边界；生产实现消费工厂取得的根、锁、门与路径资格。
/// </summary>
internal interface IFrameStoreFiles {
    /// <summary>
    /// 完成 private 初始化及 active 发布后移交只含合法 header、显式 CacheMode.Off 的已资格化可写 RBF3 handle。
    /// 正常返回即表示编号已正式发布；异常时实现负责全部尚未移交资源的一次性清理，不盲删正式文件。
    /// </summary>
    IRbfFile CreateActive(uint fileId);

    /// <summary>
    /// 将 core 已 fresh flush 且尝试关闭的 active 文件无覆盖移动到正式 archive。
    /// 实现负责实际根、同文件系统、规范路径和平台 rename 资格；异常不得隐藏或自动重试。
    /// </summary>
    void ArchiveClosed(uint fileId);

    /// <summary>
    /// 从未拥有的正式文件完整读取：核对身份/header、物化结果、正常关闭临时 reader 后才移交。
    /// 实现须保持主错误并清理未移交 buffer；仅实际 owned 清理失败调用预建的内部同步回调，
    /// 纯读取错误或异常不调用它。该回调不是公开扩展点。
    /// </summary>
    AteliaResult<FrameRead> ReadUnowned(FrameAddress address, Action markOwnedCleanupFault);
}
