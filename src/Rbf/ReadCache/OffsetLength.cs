namespace Atelia.Rbf.ReadCache;

/// <summary>文件中的一段连续区域，表示半开区间 [<see cref="Offset"/>, <see cref="Offset"/> + <see cref="Length"/>)。</summary>
/// <param name="Offset">起始字节偏移（非负）。</param>
/// <param name="Length">区段长度（字节，非负）。</param>
internal readonly record struct OffsetLength(long Offset, long Length) {
    /// <summary>区间结束位置（不含）。</summary>
    public long End => Offset + Length;

    /// <summary>判断指定位置是否在半开区间 [Offset, End) 内。</summary>
    public bool Contains(long position) {
        if (position < Offset) { return false; }
        return (position - Offset) < Length;
    }

    /// <summary>转换为 <see cref="StartEnd"/>（Start+End 视角）。</summary>
    public StartEnd ToStartEnd() => new(Offset, End);
}
