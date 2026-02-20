using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Atelia.Data;

/// <summary>
/// SizedPtr 是一个紧凑的 Fat Pointer，将 Offset 和 Length 压缩存储在一个 ulong (64-bit) 中。
/// 表达半开区间 [OffsetBytes, OffsetBytes + LengthBytes)，Offset/Length 都要求 4B 对齐。
/// 默认采用 38:26 bit 分配（AlignmentShift=2），支持约 1TB 偏移和 256MB 长度。
/// </summary>
/// <remarks>
/// 设计要点：
/// - 不定义 Null/Empty 等特殊值语义，由上层按需约定。
/// - <see cref="FromPacked"/> 不做校验，任意 ulong 都可解包。
/// - <see cref="Create"/>/<see cref="TryCreate"/> 做完整校验：对齐、MaxOffset/MaxLength、非负性。
/// - <see cref="Contains"/> 使用差值比较避免溢出。
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

    /// <summary>区间结束位置（不含）。</summary>
    public long EndOffsetExclusive => Offset + Length; // 由位分配保证不会溢出，最大`1TB - 4B + 256MB - 4B`。

    /// <summary>从 packed ulong 直接构造，不做任何校验。用于反序列化或从已知合法来源恢复。</summary>
    /// <param name="packed">压缩后的 64-bit 值。</param>
    /// <returns>SizedPtr 实例。</returns>
    public static SizedPtr FromPacked(ulong packed) => new(packed);

    /// <summary>从 (offsetBytes, lengthBytes) 创建 SizedPtr，执行完整校验。</summary>
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

    /// <summary>尝试从 (offsetBytes, lengthBytes) 创建 SizedPtr，失败时返回 false。</summary>
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

    /// <summary>解构为 (offsetBytes, lengthBytes)。</summary>
    public void Deconstruct(out long offsetBytes, out int lengthBytes) {
        offsetBytes = Offset;
        lengthBytes = Length;
    }

    /// <summary>内部打包方法，不做校验。</summary>
    private static ulong PackUnchecked(long offsetBytes, int lengthBytes) {
        ulong offsetPackedPart = ((ulong)offsetBytes >> AlignmentShift) << LengthPackedBits;
        ulong lengthPackedPart = ((ulong)lengthBytes >> AlignmentShift) & LengthPackedMask;
        return offsetPackedPart | lengthPackedPart;
    }

    /// <summary>
    /// 序列化交错掩码：Length 位（26 个 '1'）。
    /// 字节分组方案 1C+4A+3B（LSB→MSB）：
    ///   byte 0: C [OOOOOO LL]  6:2     byte 4: A [OOOOO LLL] 5:3
    ///   byte 1: A [OOOOO LLL]  5:3     byte 5: B [OOOO LLLL] 4:4
    ///   byte 2: A [OOOOO LLL]  5:3     byte 6: B [OOOO LLLL] 4:4
    ///   byte 3: A [OOOOO LLL]  5:3     byte 7: B [OOOO LLLL] 4:4
    /// 递减比率 6:2→5:3→4:4 使得小值处偏重 Offset，大值处 Length 追赶回来。
    /// 字节对齐的分组结构使得软件 fallback 只需逐字节 shift+mask。
    /// </summary>
    private const ulong SerializeLengthMask = 0x0F0F_0F07_0707_0703;
    private const ulong SerializeOffsetMask = ~SerializeLengthMask;

    /// <summary>
    /// 将 SizedPtr 序列化为交错排列的紧凑值。
    /// 当 Offset 和 Length 都较小时，结果值也较小（前导零较多），
    /// 适合与 VarInt 或 TagedPointer 配合使用。
    /// </summary>
    /// <remarks>
    /// Bits  Offset Max  Length Max
    ///   63      512 GB      256 MB
    ///   62      256 GB      256 MB
    ///   56       64 GB       16 MB
    ///   49     4096 MB     2048 KB
    ///   48     4096 MB     1024 KB
    ///   42      256 MB      256 KB
    ///   35     8192 KB       64 KB
    ///   32     8192 KB      8188 B
    ///   28      512 KB      8188 B
    ///   21       32 KB      1020 B
    ///   16      8188 B       124 B
    ///   14      2044 B       124 B
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Serialize() {
        ulong offsetPacked = Packed >> LengthPackedBits;
        ulong lengthPacked = Packed & LengthPackedMask;
        if (Bmi2.X64.IsSupported) {
            return Bmi2.X64.ParallelBitDeposit(offsetPacked, SerializeOffsetMask)
                 | Bmi2.X64.ParallelBitDeposit(lengthPacked, SerializeLengthMask);
        }
        return SerializeSoftware(offsetPacked, lengthPacked);
    }

    /// <summary>从交错序列化值恢复 SizedPtr。</summary>
    /// <param name="serialized">由 <see cref="Serialize"/> 产生的交错值。</param>
    /// <returns>恢复后的 SizedPtr 实例。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SizedPtr Deserialize(ulong serialized) {
        if (Bmi2.X64.IsSupported) {
            ulong offsetPacked = Bmi2.X64.ParallelBitExtract(serialized, SerializeOffsetMask);
            ulong lengthPacked = Bmi2.X64.ParallelBitExtract(serialized, SerializeLengthMask);
            return new SizedPtr((offsetPacked << LengthPackedBits) | lengthPacked);
        }
        return DeserializeSoftware(serialized);
    }

    /// <summary>
    /// 软件 fallback：逐字节 shift+mask 实现 PDEP 等效操作。
    /// 字节分组 1C+4A+3B，从 LSB 到 MSB 逐字节消费 offset/length 的低位。
    /// </summary>
    internal static ulong SerializeSoftware(ulong off, ulong len) {
        // Byte 0: C (6:2)
        ulong result = ((off & 0x3F) << 2) | (len & 0x3);
        off >>= 6;
        len >>= 2;
        // Bytes 1–4: A (5:3) × 4
        result |= (((off & 0x1F) << 3) | (len & 0x7)) << 8;
        off >>= 5;
        len >>= 3;
        result |= (((off & 0x1F) << 3) | (len & 0x7)) << 16;
        off >>= 5;
        len >>= 3;
        result |= (((off & 0x1F) << 3) | (len & 0x7)) << 24;
        off >>= 5;
        len >>= 3;
        result |= (((off & 0x1F) << 3) | (len & 0x7)) << 32;
        off >>= 5;
        len >>= 3;
        // Bytes 5–7: B (4:4) × 3
        result |= (((off & 0xF) << 4) | (len & 0xF)) << 40;
        off >>= 4;
        len >>= 4;
        result |= (((off & 0xF) << 4) | (len & 0xF)) << 48;
        off >>= 4;
        len >>= 4;
        result |= (((off & 0xF) << 4) | (len & 0xF)) << 56;
        return result;
    }

    /// <summary>
    /// 软件 fallback：逐字节 shift+mask 实现 PEXT 等效操作。
    /// 从 MSB 到 LSB 逐字节提取 offset/length 分量，左移累加。
    /// </summary>
    internal static SizedPtr DeserializeSoftware(ulong s) {
        // Bytes 7–5: B (4:4) × 3 — MSB first
        ulong off = (s >> 60) & 0xF;
        ulong len = (s >> 56) & 0xF;
        off = (off << 4) | ((s >> 52) & 0xF);
        len = (len << 4) | ((s >> 48) & 0xF);
        off = (off << 4) | ((s >> 44) & 0xF);
        len = (len << 4) | ((s >> 40) & 0xF);
        // Bytes 4–1: A (5:3) × 4
        off = (off << 5) | ((s >> 35) & 0x1F);
        len = (len << 3) | ((s >> 32) & 0x7);
        off = (off << 5) | ((s >> 27) & 0x1F);
        len = (len << 3) | ((s >> 24) & 0x7);
        off = (off << 5) | ((s >> 19) & 0x1F);
        len = (len << 3) | ((s >> 16) & 0x7);
        off = (off << 5) | ((s >> 11) & 0x1F);
        len = (len << 3) | ((s >> 8) & 0x7);
        // Byte 0: C (6:2)
        off = (off << 6) | ((s >> 2) & 0x3F);
        len = (len << 2) | (s & 0x3);
        return new SizedPtr((off << LengthPackedBits) | len);
    }
}
