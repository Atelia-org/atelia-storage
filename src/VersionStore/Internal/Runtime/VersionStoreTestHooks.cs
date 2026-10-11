namespace Atelia.VersionStore.Internal.Runtime;

/// <summary>只供内部故障测试；正常路径使用系统时钟与借入 data 的真实 ConfirmDurable。</summary>
internal sealed class VersionStoreTestHooks {
    internal Func<long>? GetRecordedAtUnixMs;
    internal Action? ConfirmDataDurable;
    // 私有文件已 flush/close，但正式 rename 尚未被调用。
    internal Action? BeforeNewRefRename;
    // 私有 BucketHeader 已 flush/close；空桶 rename 尚未调用，tag 仍 NotAttempted。
    internal Action? BeforeNewTagBucketRename;
    // 合法空桶已公开，正式桶重开及 TagRecord Append 尚未开始。
    internal Action? AfterTagBucketPublished;
    // 专门证明 Confirmed 先于结果交付异常；不构成公开发布 callback。
    internal Action? AfterPublicationConfirmed;
}
