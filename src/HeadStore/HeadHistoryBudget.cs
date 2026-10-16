namespace Atelia.HeadStore;

/// <summary>本次历史查询的交付项与实际遍历工作上限；两个值都必须为正数。</summary>
/// <remarks>工作步包含进入 head、扫描 MoveNext 尝试和每次完整帧读取；不是 bytes、内存或时限上界。</remarks>
public readonly record struct HeadHistoryBudget(long MaxEntries, long MaxWorkSteps);
