# 升级 Address64 为 FramePtr

核心相关文档
- `atelia/docs/Rbf/rbf-interface.md`

次要相关文档
- 底层存储：`atelia/docs/Rbf/rbf-format.md`
- 测试向量：`atelia/docs/Rbf/rbf-test-vectors.md`
- 上层用户：`atelia/docs/StateJournal/mvp-design-v2.md.original`

## 收益

| 维度 | Address64（现有） | FramePtr（提议） |
|:-----|:------------------|:-----------------|
| **内容** | 仅偏移量 | 偏移量 + 长度 |
| **读取效率** | 需两次：先读头（获长度）→ 再读体 | 一次：直接读 offset..offset+len |
| **写入流程** | 写入时知道偏移量 | 写入完成后知道偏移量+长度 |
| **序列化** | 8 字节 | 8 字节（打包） |

**收益明确的场景**：
- ✅ **随机读取**（热路径）：一次系统调用 vs 两次
- ✅ **Lazy Load**：FramePtr 自描述，无需额外查找

## Bit 分配方案

**约束**：
- 总共 64 bit
- 偏移量需要 4B 对齐 → 可节省 2 bit
- 帧长度也要求 4B 对齐 → 再节省 2 bit

**候选方案**：

| 方案 | 偏移量 bit | 长度 bit | 最大文件 | 最大帧 |
|:-----|:-----------|:---------|:---------|:-------|
| **大文件** | 40 | 24 | 4 TB | 64 MB |
| **大帧** | 36 | 28 | 256 GB | 1 GB |

**编码示意**（**大帧**方案，36:28 分配）：

**实现约定（偏易用、偏宽松）**：
- `Packed == 0` 代表 **Empty**（无指针 / None）——它是合法值
- `offset/length` 以 **字节** 暴露给使用者，但编码时按 4B 对齐打包（值域天然保证 4B 对齐）
- 允许 `length == 0`（表示空区间）；`Empty` 仍仅由 `Packed == 0` 表达
- 从 `Packed` 构造不需要 `Parse/TryParse` 校验（任何 `ulong` 都能解包成一个确定的区间）
- 从 `(offset,length)` 构造提供 `Create/TryCreate`，仅用于帮助调用方在写入时做参数校验
- `EndOffsetExclusive` 使用 `checked`；`Contains` 使用差值比较避免溢出

```csharp
using System;

public readonly record struct FramePtr(ulong Packed) {
    // 偏移量 bit 数（4B 对齐，实际值 = 字段值 × 4）
    public const int OffsetBits = 36;

    // 长度 bit 数（4B 对齐，实际值 = 字段值 × 4）
    public const int LengthBits = 64 - OffsetBits;

    private const int AlignmentShift = 2; // 4B 对齐
    private const ulong LengthMask = LengthBits == 64 ? ulong.MaxValue : ((1UL << LengthBits) - 1UL);

    // 最大可表示范围（注意：字段最大值是 2^bits - 1）
    public const ulong MaxOffset = ((1UL << OffsetBits) - 1UL) << AlignmentShift;
    public const uint MaxLength = (uint)(((1UL << LengthBits) - 1UL) << AlignmentShift);

    // Empty/None 语义：Packed == 0（合法值）
    public bool IsEmpty => Packed == 0;

    public static FramePtr Empty => default;

    // 面向调用者的字节语义（bytes）
    public ulong OffsetBytes => (Packed >> LengthBits) << AlignmentShift;
    public uint LengthBytes => (uint)((Packed & LengthMask) << AlignmentShift);

    public ulong EndOffsetExclusive => checked(OffsetBytes + (ulong)LengthBytes);

    // 从 packed 直接构造/反序列化：不做校验（校验在 Create/TryCreate 完成）
    public static FramePtr FromPacked(ulong packed) => new(packed);

    public FramePtr(ulong offsetBytes, uint lengthBytes) : this(CreatePacked(offsetBytes, lengthBytes)) {
    }

    public static FramePtr Create(ulong offsetBytes, uint lengthBytes) => new(offsetBytes, lengthBytes);

    public static bool TryCreate(ulong offsetBytes, uint lengthBytes, out FramePtr ptr) {
        if (!IsValidComponents(offsetBytes, lengthBytes)) {
            ptr = default;
            return false;
        }

        ptr = new FramePtr(CreatePackedUnchecked(offsetBytes, lengthBytes));
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

        // 允许 lengthBytes == 0（空区间）
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
