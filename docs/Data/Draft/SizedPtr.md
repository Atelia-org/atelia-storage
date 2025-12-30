# SizedPtr 数据结构设计

核心相关文档
- `atelia/docs/Rbf/rbf-interface.md`

次要相关文档
- 底层存储：`atelia/docs/Rbf/rbf-format.md`
- 测试向量：`atelia/docs/Rbf/rbf-test-vectors.md`
- 上层用户：`atelia/docs/StateJournal/mvp-design-v2.md.original`

## 定位

**SizedPtr** 本质上是一个 **Packed Fat Pointer**（胖指针）数据结构。
它将 `Offset`（偏移量）和 `Length`（长度）压缩存储在一个 `ulong` (64-bit) 中。
该结构专注于数据的**紧凑存储与位操作算法**，与具体的上层业务语义（如 Null 值、空文件等）解耦。

## Bit 分配方案

**约束**：
- 总共 64 bit
- 偏移量需要 4B 对齐 → 可节省 2 bit
- 帧长度也要求 4B 对齐 → 再节省 2 bit

**范围估计**：

| 方案 | 偏移量 bit | 长度 bit | 寻址范围 (约数) | 帧长度范围 (约数) |
|:-----|:-----------|:---------|:---------|:-------|
| **大文件** | 40 | 24 | ~4 TB | ~64 MB |
| **大帧** | 36 | 28 | ~256 GB | ~1 GB |

> 注：表中的“寻址范围”和“长度范围”仅用于直观估算值域规模，精确的最大值由 Bit 数决定（见代码常量）。

**编码示意**（**大帧**方案，36:28 分配）：

**实现约定**：
- 纯粹的值类型，不包含任何业务状态（如 Empty/Null）的判断逻辑。
- `offset/length` 以 **字节** 暴露给使用者，但编码时按 4B 对齐打包（值域天然保证 4B 对齐）。
- 从 `Packed` 构造不需要 `Parse/TryParse` 校验（任何 `ulong` 都能解包成一个确定的区间）。
- 从 `(offset,length)` 构造提供 `Create/TryCreate`，仅用于帮助调用方在写入时做参数校验。
- `EndOffsetExclusive` 使用 `checked`；`Contains` 使用差值比较避免溢出。

```csharp
public readonly record struct SizedPtr(ulong Packed) {
    // 偏移量 bit 数（4B 对齐，实际值 = 字段值 × 4）
    public const int OffsetBits = 36;

    // 长度 bit 数（4B 对齐，实际值 = 字段值 × 4）
    public const int LengthBits = 64 - OffsetBits;

    private const int AlignmentShift = 2; // 4B 对齐
    private const ulong LengthMask = LengthBits == 64 ? ulong.MaxValue : ((1UL << LengthBits) - 1UL);

    // 最大可表示范围（注意：字段最大值是 2^bits - 1）
    public const ulong MaxOffset = ((1UL << OffsetBits) - 1UL) << AlignmentShift;
    public const uint MaxLength = (uint)(((1UL << LengthBits) - 1UL) << AlignmentShift);

    // 面向调用者的字节语义（bytes）
    public ulong OffsetBytes => (Packed >> LengthBits) << AlignmentShift;
    public uint LengthBytes => (uint)((Packed & LengthMask) << AlignmentShift);

    public ulong EndOffsetExclusive => checked(OffsetBytes + (ulong)LengthBytes);

    // 从 packed 直接构造/反序列化：不做校验（校验在 Create/TryCreate 完成）
    public static SizedPtr FromPacked(ulong packed) => new(packed);

    public SizedPtr(ulong offsetBytes, uint lengthBytes) : this(CreatePacked(offsetBytes, lengthBytes)) {
    }

    public static SizedPtr Create(ulong offsetBytes, uint lengthBytes) => new(offsetBytes, lengthBytes);

    public static bool TryCreate(ulong offsetBytes, uint lengthBytes, out SizedPtr ptr) {
        if (!IsValidComponents(offsetBytes, lengthBytes)) {
            ptr = default;
            return false;
        }

        ptr = new SizedPtr(CreatePackedUnchecked(offsetBytes, lengthBytes));
        return true;
    }

    public bool Contains(ulong position) {
        var offset = OffsetBytes;
        if (position < offset)
            return false;
        return (position - offset) < (ulong)LengthBytes;
    }

    public void Deconstruct(out ulong offsetBytes, out uint lengthBytes) {
        offsetBytes = OffsetBytes;
        lengthBytes = LengthBytes;
    }

    private static ulong CreatePacked(ulong offsetBytes, uint lengthBytes) {
        if ((offsetBytes & ((1UL << AlignmentShift) - 1UL)) != 0)
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), "offsetBytes must be 4B-aligned.");
        if ((lengthBytes & ((1U << AlignmentShift) - 1U)) != 0)
            throw new ArgumentOutOfRangeException(nameof(lengthBytes), "lengthBytes must be 4B-aligned.");

        if (offsetBytes > MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), $"offsetBytes exceeds MaxOffset={MaxOffset}.");
        if (lengthBytes > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(lengthBytes), $"lengthBytes exceeds MaxLength={MaxLength}.");

        if (offsetBytes > ulong.MaxValue - lengthBytes)
            throw new ArgumentOutOfRangeException(nameof(offsetBytes), "offsetBytes+lengthBytes overflows.");

        return CreatePackedUnchecked(offsetBytes, lengthBytes);
    }

    private static bool IsValidComponents(ulong offsetBytes, uint lengthBytes) {
        if ((offsetBytes & ((1UL << AlignmentShift) - 1UL)) != 0) return false;
        if ((lengthBytes & ((1U << AlignmentShift) - 1U)) != 0) return false;
        if (offsetBytes > MaxOffset) return false;
        if (lengthBytes > MaxLength) return false;
        if (offsetBytes > ulong.MaxValue - lengthBytes) return false;
        return true;
    }

    private static ulong CreatePackedUnchecked(ulong offsetBytes, uint lengthBytes) {
        ulong offsetPart = (offsetBytes >> AlignmentShift) << LengthBits;
        ulong lengthPart = ((ulong)lengthBytes >> AlignmentShift) & LengthMask;
        return offsetPart | lengthPart;
    }
}
```
