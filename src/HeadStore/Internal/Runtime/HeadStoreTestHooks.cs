using Atelia.Data;

namespace Atelia.HeadStore.Internal.Runtime;

/// <summary>只供内部故障测试；正常路径使用系统时钟与借入 data 的真实 ConfirmDurable。</summary>
internal sealed class HeadStoreTestHooks {
    internal Func<long>? GetRecordedAtUnixMs;
    internal Action? ConfirmDataDurable;
    // 私有文件已 flush/close，但正式 rename 尚未被调用。
    internal Action? BeforeNewHeadRename;
    // 私有 BucketHeader 已 flush/close；空桶 rename 尚未调用，tag 仍 NotAttempted。
    internal Action? BeforeNewTagBucketRename;
    // 合法空桶已公开，正式桶重开及 TagRecord Append 尚未开始。
    internal Action? AfterTagBucketPublished;
    // Real complete Append has returned a ticket; the corresponding flush has not begun.
    internal Action<SizedPtr>? AfterHeadRecordAppended;
    // CU-specific internal windows; the callback runs immediately before the real operation.
    internal Action<uint>? BeforeConditionalUpdateBegin;
    internal Action<uint>? BeforeConditionalUpdateEnd;
    internal Action<uint>? BeforeConditionalUpdateFlush;
    internal Action<SizedPtr>? AfterTagRecordAppended;
    // Selection has accepted one Entry and captured its complete publication dependencies.
    internal Action<HeadEntryId>? AfterTagHeadSelected;
    internal Action<uint>? BeforeTagDependencyFlush;
    // Data and all publication dependencies are confirmed; no TagRecord output has started.
    internal Action? AfterTagDependenciesConfirmed;
    // 专门证明 Confirmed 先于结果交付异常；不构成公开发布 callback。
    internal Action? AfterPublicationConfirmed;
}
