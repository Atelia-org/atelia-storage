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
///   <item><see cref="Create"/>/<see cref="TryCreate"/> 做完整校验：对齐、MaxOffset/MaxLength、offset+length 溢出。</item>
///   <item><see cref="Contains"/> 使用差值比较避免溢出。</item>
///   <item><see cref="EndOffsetExclusive"/> 使用 checked 算术。</item>
/// </list>
/// </remarks>
public readonly record struct SizedPtr(ulong Packed) {
    /// <summary>偏移量 bit 数（38 bit，存储 4B 对齐后的值）。</summary>
    public const int OffsetBits = 38;

    /// <summary>长度 bit 数（26 bit，存储 4B 对齐后的值）。</summary>
    public const int LengthBits = 64 - OffsetBits; // 26

    /// <summary>对齐位移（4B 对齐 = 2 bit）。</summary>
    private const int AlignmentShift = 2;

    /// <summary>对齐掩码（0b11）。</summary>
    private const ulong AlignmentMask = (1UL << AlignmentShift) - 1UL; // 0x3

    /// <summary>长度字段掩码。</summary>
    private const ulong LengthMask = (1UL << LengthBits) - 1UL;

    /// <summary>最大可表示偏移量（字节），约 1TB。</summary>
    public const ulong MaxOffset = ((1UL << OffsetBits) - 1UL) << AlignmentShift;

    /// <summary>最大可表示长度（字节），约 256MB。</summary>
    public const uint MaxLength = (uint)(((1UL << LengthBits) - 1UL) << AlignmentShift);

    /// <summary>以字节表示的起始偏移（4B 对齐）。</summary>
    public ulong OffsetBytes => (Packed >> LengthBits) << AlignmentShift;

    /// <summary>以字节表示的区间长度（4B 对齐）。</summary>
    public uint LengthBytes => (uint)((Packed & LengthMask) << AlignmentShift);

    /// <summary>
    /// 区间结束位置（不含），使用 checked 算术。
    /// </summary>
    /// <exception cref="OverflowException">当 OffsetBytes + LengthBytes 溢出时抛出。</exception>
    public ulong EndOffsetExclusive => checked(OffsetBytes + (ulong)LengthBytes);

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
    /// <param name="offsetBytes">以字节表示的起始偏移，必须 4B 对齐且 &lt;= MaxOffset。</param>
    /// <param name="lengthBytes">以字节表示的长度，必须 4B 对齐且 &lt;= MaxLength。</param>
    /// <returns>SizedPtr 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数不满足对齐、范围或溢出约束时抛出。</exception>
    public static SizedPtr Create(ulong offsetBytes, uint lengthBytes) {
        if ((offsetBytes & AlignmentMask) != 0) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes, "offsetBytes must be 4B-aligned."); }
        if ((lengthBytes & AlignmentMask) != 0) { throw new ArgumentOutOfRangeException(nameof(lengthBytes), lengthBytes, "lengthBytes must be 4B-aligned."); }
        if (offsetBytes > MaxOffset) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes, $"offsetBytes exceeds MaxOffset ({MaxOffset})."); }
        if (lengthBytes > MaxLength) { throw new ArgumentOutOfRangeException(nameof(lengthBytes), lengthBytes, $"lengthBytes exceeds MaxLength ({MaxLength})."); }
        // 检查 offset + length 是否溢出
        if (offsetBytes > ulong.MaxValue - (ulong)lengthBytes) { throw new ArgumentOutOfRangeException(nameof(offsetBytes), "offsetBytes + lengthBytes would overflow."); }

        return new SizedPtr(PackUnchecked(offsetBytes, lengthBytes));
    }

    /// <summary>
    /// 尝试从 (offsetBytes, lengthBytes) 创建 SizedPtr，失败时返回 false。
    /// </summary>
    /// <param name="offsetBytes">以字节表示的起始偏移，必须 4B 对齐且 &lt;= MaxOffset。</param>
    /// <param name="lengthBytes">以字节表示的长度，必须 4B 对齐且 &lt;= MaxLength。</param>
    /// <param name="ptr">成功时返回的 SizedPtr；失败时为 default。</param>
    /// <returns>是否创建成功。</returns>
    public static bool TryCreate(ulong offsetBytes, uint lengthBytes, out SizedPtr ptr) {
        if ((offsetBytes & AlignmentMask) != 0 ||
            (lengthBytes & AlignmentMask) != 0 ||
            offsetBytes > MaxOffset ||
            lengthBytes > MaxLength ||
            offsetBytes > ulong.MaxValue - (ulong)lengthBytes) {
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
    public bool Contains(ulong position) {
        var offset = OffsetBytes;
        // 差值比较：若 position < offset，直接 false；否则检查差值是否在 LengthBytes 内
        if (position < offset) { return false; }
        // position >= offset，计算差值不会溢出
        return (position - offset) < (ulong)LengthBytes;
    }

    /// <summary>
    /// 解构为 (offsetBytes, lengthBytes)。
    /// </summary>
    public void Deconstruct(out ulong offsetBytes, out uint lengthBytes) {
        offsetBytes = OffsetBytes;
        lengthBytes = LengthBytes;
    }

    /// <summary>
    /// 内部打包方法，不做校验。
    /// </summary>
    private static ulong PackUnchecked(ulong offsetBytes, uint lengthBytes) {
        ulong offsetPart = (offsetBytes >> AlignmentShift) << LengthBits;
        ulong lengthPart = ((ulong)lengthBytes >> AlignmentShift) & LengthMask;
        return offsetPart | lengthPart;
    }
}
