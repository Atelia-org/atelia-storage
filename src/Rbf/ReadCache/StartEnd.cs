namespace Atelia.Rbf.ReadCache;

/// <summary>文件中的半开区间 [<see cref="Start"/>, <see cref="End"/>)，用于区间算术。</summary>
/// <param name="Start">区间起始位置（含）。</param>
/// <param name="End">区间结束位置（不含）。</param>
/// <remarks>
/// 与 <see cref="OffsetLength"/>（Offset+Length）互补：
/// <c>OffsetLength</c> 适合表示"从哪里读多少"，StartEnd 适合重叠检测、边界切分等区间运算。
/// </remarks>
internal readonly record struct StartEnd(long Start, long End) {
    /// <summary>区间长度。</summary>
    public long Length => End - Start;

    /// <summary>判断指定位置是否在半开区间 [Start, End) 内。</summary>
    public bool Contains(long position) => Start <= position && position < End;

    /// <summary>判断两个区间是否存在重叠。</summary>
    public bool Overlaps(StartEnd other) => Start < other.End && other.Start < End;

    /// <summary>转换为<see cref="OffsetLength"/>。</summary>
    public OffsetLength ToOffsetLength() => new(Start, Length);
}
