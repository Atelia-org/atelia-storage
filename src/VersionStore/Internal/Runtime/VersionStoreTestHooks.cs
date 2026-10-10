namespace Atelia.VersionStore.Internal.Runtime;

/// <summary>只供内部故障测试；正常路径使用系统时钟与借入 data 的真实 ConfirmDurable。</summary>
internal sealed class VersionStoreTestHooks {
    internal Func<long>? GetRecordedAtUnixMs;
    internal Action? ConfirmDataDurable;
    // 专门证明 Confirmed 先于结果交付异常；不构成公开发布 callback。
    internal Action? AfterPublicationConfirmed;
}
