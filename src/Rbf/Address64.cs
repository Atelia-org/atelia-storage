namespace Atelia.Rbf;

/// <summary>
/// RBF 帧地址（8 字节文件偏移）。
/// </summary>
/// <remarks>
/// <para><b>[F-ADDRESS64-DEFINITION]</b>: Address64 是 8 字节 LE 编码的文件偏移量，
/// 指向一个 Frame 的起始位置（HeadLen 字段起点）。</para>
/// <para><b>[F-ADDRESS64-ALIGNMENT]</b>: 有效 Address64 MUST 4 字节对齐（Value % 4 == 0）。</para>
/// <para><b>[F-ADDRESS64-NULL]</b>: Value == 0 表示 null（无效地址）。</para>
/// </remarks>
/// <param name="Value">文件偏移量。</param>
public readonly record struct Address64(ulong Value)
{
    /// <summary>
    /// 空地址（表示无效/不存在）。
    /// </summary>
    public static readonly Address64 Null = new(0);

    /// <summary>
    /// 检查地址是否为空。
    /// </summary>
    public bool IsNull => Value == 0;

    /// <summary>
    /// 检查地址是否有效（非空且 4 字节对齐）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-ADDRESS64-ALIGNMENT]</b>: 有效地址 MUST 4 字节对齐。</para>
    /// </remarks>
    public bool IsValid => !IsNull && (Value % 4 == 0);

    /// <summary>
    /// 隐式转换为 ulong。
    /// </summary>
    public static implicit operator ulong(Address64 addr) => addr.Value;

    /// <summary>
    /// 显式从 ulong 转换。
    /// </summary>
    public static explicit operator Address64(ulong value) => new(value);

    /// <summary>
    /// 从 long 创建 Address64（方便与文件偏移交互）。
    /// </summary>
    /// <param name="offset">文件偏移（必须非负）。</param>
    /// <returns>Address64 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">offset 为负数时抛出。</exception>
    public static Address64 FromOffset(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new Address64((ulong)offset);
    }
}
