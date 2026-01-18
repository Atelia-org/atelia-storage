namespace Atelia.Data;

/// <summary>
/// SizedPtr 是一个紧凑的 Fat Pointer，将 Offset 和 Length 压缩存储在一个 ulong (64-bit) 中。
/// 表达半开区间 [OffsetBytes, OffsetBytes + LengthBytes)，Offset/Length 都要求 4B 对齐。
/// 默认采用 38:26 bit 分配（AlignmentShift=2），支持约 1TB 偏移和 256MB 长度。
/// </summary>
/// <remarks>
/// <para>设计要点：</para>
/// <list type="bullet">
///   <item>不定义 Null/Empty 等特殊值语义，由上层按需约定。</item>
///   <item><see cref="FromPacked"/> 不做校验，任意 ulong 都可解包。</item>
///   <item><see cref="Create"/>/<see cref="TryCreate"/> 做完整校验：对齐、MaxOffset/MaxLength、非负性。</item>
///   <item><see cref="Contains"/> 使用差值比较避免溢出。</item>
///   <item><see cref="EndOffsetExclusive"/> 使用 checked 算术。</item>
/// </list>
/// </remarks>
public readonly record struct SizedPtr(ulong Packed) {
    /// <summary>偏移量 bit 数（38 bit，存储 4B 对齐后的值）。</summary>
    public const int OffsetPackedBits = 38;

    /// <summary>长度 bit 数（26 bit，存储 4B 对齐后的值）。</summary>
    public const int LengthPackedBits = sizeof(ulong) * 8 - OffsetPackedBits; // 26

    /// <summary>对齐位移（4B 对齐 = 2 bit）。</summary>
    public const int AlignmentShift = 2;

    public const int Alignment = 1 << AlignmentShift;

    /// <summary>对齐掩码（0b11）。</summary>
    public const int AlignmentMask = Alignment - 1; // 0x3

    /// <summary>长度字段掩码。</summary>
    private const ulong LengthPackedMask = (1UL << LengthPackedBits) - 1UL;

    /// <summary>最大可表示偏移量（字节），1TB - 4B。</summary>
    public const long MaxOffset = (long)(((1UL << OffsetPackedBits) - 1UL) << AlignmentShift);

    /// <summary>最大可表示长度（字节），256MB - 4B。</summary>
    public const int MaxLength = (int)(((1UL << LengthPackedBits) - 1UL) << AlignmentShift);

    /// <summary>以字节表示的起始偏移（4B 对齐）。</summary>
    public long Offset => (long)((Packed >> LengthPackedBits) << AlignmentShift);

    /// <summary>以字节表示的区间长度（4B 对齐）。</summary>
    public int Length => (int)((Packed & LengthPackedMask) << AlignmentShift);

    /// <summary>
    /// 区间结束位置（不含），使用 checked 算术。
    /// </summary>
    public long EndOffsetExclusive => Offset + Length; // 由位分配保证不会溢出，最大`1TB - 4B + 256MB - 4B`。

    /// <summary>
    /// 从 packed ulong 直接构造，不做任何校验。
    /// 用于反序列化或从已知合法来源恢复。
    /// </summary>
    /// <param name="packed">压缩后的 64-bit 值。</param>
    /// <returns>SizedPtr 实例。</returns>
    public static SizedPtr FromPacked(ulong packed) => new(packed);

    /// <summary>
    /// 从 (offsetBytes, lengthBytes) 创建 SizedPtr，执行完整校验。
    /// </summary>
    /// <param name="offsetBytes">以字节表示的起始偏移，必须非负、4B 对齐且 &lt;= MaxOffset。</param>
    /// <param name="lengthBytes">以字节表示的长度，必须非负、4B 对齐且 &lt;= MaxLength。</param>
    /// <returns>SizedPtr 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数不满足对齐、范围或溢出约束时抛出。</exception>
    public static SizedPtr Create(long offsetBytes, int lengthBytes) {
        if (offsetBytes < 0) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes, "offsetBytes must be non-negative."); }
        if (lengthBytes < 0) { throw new ArgumentOutOfRangeException(nameof(lengthBytes), lengthBytes, "lengthBytes must be non-negative."); }
        if ((offsetBytes & AlignmentMask) != 0) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes, "offsetBytes must be 4B-aligned."); }
        if ((lengthBytes & AlignmentMask) != 0) { throw new ArgumentOutOfRangeException(nameof(lengthBytes), lengthBytes, "lengthBytes must be 4B-aligned."); }
        if (offsetBytes > MaxOffset) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes, $"offsetBytes exceeds MaxOffset ({MaxOffset})."); }
        if (lengthBytes > MaxLength) { throw new ArgumentOutOfRangeException(nameof(lengthBytes), lengthBytes, $"lengthBytes exceeds MaxLength ({MaxLength})."); }
        // 检查 offset + length 是否溢出
        if (offsetBytes > long.MaxValue - lengthBytes) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), "offsetBytes + lengthBytes would overflow."); }

        return new SizedPtr(PackUnchecked(offsetBytes, lengthBytes));
    }

    /// <summary>
    /// 尝试从 (offsetBytes, lengthBytes) 创建 SizedPtr，失败时返回 false。
    /// </summary>
    /// <param name="offsetBytes">以字节表示的起始偏移，必须非负、4B 对齐且 &lt;= MaxOffset。</param>
    /// <param name="lengthBytes">以字节表示的长度，必须非负、4B 对齐且 &lt;= MaxLength。</param>
    /// <param name="ptr">成功时返回的 SizedPtr；失败时为 default。</param>
    /// <returns>是否创建成功。</returns>
    public static bool TryCreate(long offsetBytes, int lengthBytes, out SizedPtr ptr) {
        if (offsetBytes < 0 ||
            lengthBytes < 0 ||
            (offsetBytes & AlignmentMask) != 0 ||
            (lengthBytes & AlignmentMask) != 0 ||
            offsetBytes > MaxOffset ||
            lengthBytes > MaxLength ||
            offsetBytes > long.MaxValue - lengthBytes) {
            ptr = default;
            return false;
        }

        ptr = new SizedPtr(PackUnchecked(offsetBytes, lengthBytes));
        return true;
    }

    /// <summary>
    /// 判断指定位置是否在半开区间 [OffsetBytes, OffsetBytes + LengthBytes) 内。
    /// 使用差值比较避免溢出。
    /// </summary>
    /// <param name="position">要检查的位置（字节偏移）。</param>
    /// <returns>位置在区间内返回 true，否则返回 false。</returns>
    public bool Contains(long position) {
        var offset = Offset;
        // 差值比较：若 position < offset，直接 false；否则检查差值是否在 LengthBytes 内
        if (position < offset) { return false; }
        // position >= offset (且 offset >= 0)
        return (position - offset) < (long)Length;
    }

    /// <summary>
    /// 解构为 (offsetBytes, lengthBytes)。
    /// </summary>
    public void Deconstruct(out long offsetBytes, out int lengthBytes) {
        offsetBytes = Offset;
        lengthBytes = Length;
    }

    /// <summary>
    /// 内部打包方法，不做校验。
    /// </summary>
    private static ulong PackUnchecked(long offsetBytes, int lengthBytes) {
        ulong offsetPackedPart = ((ulong)offsetBytes >> AlignmentShift) << LengthPackedBits;
        ulong lengthPackedPart = ((ulong)lengthBytes >> AlignmentShift) & LengthPackedMask;
        return offsetPackedPart | lengthPackedPart;
    }
}
