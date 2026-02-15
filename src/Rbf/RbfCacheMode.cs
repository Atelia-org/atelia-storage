namespace Atelia.Rbf;

/// <summary>
/// 控制 RBF 文件的读缓存策略。
/// 值等于内部 SlotCountShift（2^n 个 4KB 页槽位），<see cref="Off"/> 表示关闭缓存。
/// </summary>
public enum RbfCacheMode {
    /// <summary>关闭读缓存，每次读取直接访问文件。</summary>
    Off = 0,
    /// <summary>2 slots × 4KB = 8KB。</summary>
    Slots2 = 1,
    /// <summary>4 slots × 4KB = 16KB。</summary>
    Slots4 = 2,
    /// <summary>8 slots × 4KB = 32KB。</summary>
    Slots8 = 3,
    /// <summary>16 slots × 4KB = 64KB（默认）。</summary>
    Slots16 = 4,
    /// <summary>32 slots × 4KB = 128KB。</summary>
    Slots32 = 5,
    /// <summary>64 slots × 4KB = 256KB。</summary>
    Slots64 = 6,
}
