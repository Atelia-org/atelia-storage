---
docId: "rbf-derived-notes"
title: "RBF 推导与答疑（Derived / Informative）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 推导与答疑（Derived / Informative）

**文档定位**：Derived-Layer，容纳推导结论、算例、FAQ。与 SSOT 冲突时以 SSOT 为准。
文档层级与规范遵循见 [README.md](README.md)。

## 1. 示例占位（后续迁移）

这里将逐步迁移“可由 SSOT 推导”的重复信息，例如：
- 最小帧长度、由公式推出的边界值
- 常见 CRC 覆盖范围问答
- Reverse Scan 的具体算例

---

### derived [H-FRAMING-CHECKLIST] Framing校验清单
**依据 SSOT**：本清单汇总了 `rbf-format.md` §2 和 §3 中定义的用于 Framing 判定的约束。
Reader 在执行 @[F-FRAMING-FAIL-REJECT](rbf-format.md) 时应检查以下项目：

1. **结构一致性**
    - HeadLen 与 TailLen 必须相等（@[F-FRAMEBYTES-FIELD-OFFSETS](rbf-format.md)）
    - HeadLen 必须 >= 24（MinFrameLen，见 @[D-RBF-FORMAT-MIN-HEADLEN]）
    - HeadLen 必须是 4 的倍数（@[S-RBF-DECISION-4B-ALIGNMENT-ROOT](rbf-decisions.md)）
2. **值域合法性**
    - Fence 必须匹配 `RBF1`（@[F-FENCE-VALUE-IS-RBF1-ASCII-4B](rbf-format.md)）
    - FrameDescriptor 保留位必须为 0（@[F-FRAMEDESCRIPTOR-LAYOUT](rbf-format.md)）
    - Padding 字节必须全为 0（@[F-PADDING-CALCULATION](rbf-format.md)）
3. **布局约束**
    - Frame 起始位置必须 4 字节对齐（@[S-RBF-DECISION-4B-ALIGNMENT-ROOT](rbf-decisions.md)）
    - Frame 必须位于 HeaderFence 之后（@[F-FILE-STARTS-WITH-FENCE](rbf-decisions.md)）
4. **CRC 校验**
    - TrailerCrc32C 校验必须通过（@[F-TRAILERCRC-COVERAGE](rbf-format.md)）
    - (Content 读取时) PayloadCrc32C 校验必须通过（@[F-CRC32C-COVERAGE](rbf-format.md)）

### derived [H-FILE-MINIMUM-LENGTH] 最小文件长度
由 @[F-FILE-STARTS-WITH-FENCE](rbf-decisions.md) 推导：有效 RBF 文件长度 >= 4（至少包含 HeaderFence）。

### derived [H-HEADLEN-FORMULA] HeadLen计算公式
由 @[F-FRAMEBYTES-FIELD-OFFSETS](rbf-format.md) 推导：
`HeadLen = 4 (HeadLen) + PayloadLen + UserMetaLen + PaddingLen + 4 (PayloadCrc32C) + 16 (TrailerCodeword)`
即：`HeadLen = 24 + PayloadLen + UserMetaLen + PaddingLen`

### derived [D-RBF-FORMAT-MIN-HEADLEN] 最小HeadLen推导
see: @[H-HEADLEN-FORMULA], @[F-PADDING-CALCULATION](rbf-format.md)

- 当 `PayloadLen = 0` 且 `UserMetaLen = 0` 时：
    - `(PayloadLen + UserMetaLen) % 4 = 0`
    - `PaddingLen = (4 - 0) % 4 = 0`
- 代入 @[H-HEADLEN-FORMULA] 可得：
      $$\text{HeadLen} = 24 + 0 + 0 + 0 = 24$$

因此：最小 `HeadLen = 24`（Tombstone 帧或空 Payload 帧）。

### derived [D-RBF-FORMAT-PADDING-TABLE] PaddingLen值域枚举
**依据 SSOT**：`rbf-format.md` 的 @[F-PADDING-CALCULATION]

| (PayloadLen + UserMetaLen) % 4 | PaddingLen |
|--------------------------------|------------|
| 0 | 0 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

### derived [D-RBF-FORMAT-TOMBSTONE-EXAMPLE] Tombstone最小帧算例
see: @[F-FRAMEDESCRIPTOR-LAYOUT](rbf-format.md), @[D-RBF-FORMAT-MIN-HEADLEN]

场景：`PayloadLen = 0`, `UserMetaLen = 0`, `IsTombstone = 1`

- `HeadLen = 24`
- `FrameDescriptor`:
    - `IsTombstone = 1`
    - `PaddingLen = 0`
    - `UserMetaLen = 0`
    - Value = `0x80000000` (u32 LE)

### derived [D-RBF-FORMAT-FRAMEDESCRIPTOR-BITMASK] FrameDescriptor位域解析规则
see: @[F-FRAMEDESCRIPTOR-LAYOUT](rbf-format.md)

```csharp
uint descriptor = ...;
bool isTombstone = (descriptor & 0x80000000) != 0;
int paddingLen = (int)((descriptor >> 29) & 0x03);
int userMetaLen = (int)(descriptor & 0xFFFF);
bool reservedIsZero = (descriptor & 0x1FFF0000) == 0;
```

### derived [D-RBF-FORMAT-REVERSE-SCAN-PSEUDOCODE] ReverseScan参考伪代码
see: @[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD](rbf-format.md), @[R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE](rbf-format.md)

```
输入: fileLength
输出: 通过校验的 Frame 起始地址列表（从尾到头）
常量:
   HeaderFenceLen = 4
   FenceLen   = 4
   MinFrameLen = 24  // @[D-RBF-FORMAT-MIN-HEADLEN]
   TrailerSize = 16

辅助:
   alignDown4(x) = x - (x % 4)   // 前置条件: x >= 0

1) 若 fileLength < HeaderFenceLen: 返回空
2) 若 fileLength == HeaderFenceLen: 返回空
3) fencePos = alignDown4(fileLength - FenceLen)
4) while fencePos >= 0:
       a) 若 fencePos == 0: 停止（到达 HeaderFence）
       b) 若 bytes[fencePos..fencePos+4] != FenceValue:
               fencePos -= 4
               continue   // Resync

       c) // 尝试识别 Frame
            recordEnd = fencePos
            若 recordEnd < HeaderFenceLen + MinFrameLen:
                  fencePos -= 4
                  continue

            // 读取 TrailerCodeword (16 bytes)
            // Layout: TrailerCrc(4) + FrameDescriptor(4) + FrameTag(4) + TailLen(4)
            trailerBytes = read(recordEnd - 16, 16)
            tailLen = read_u32_le(trailerBytes, 12)
            frameTag = read_u32_le(trailerBytes, 8)
            descriptor = read_u32_le(trailerBytes, 4)
            storedTrailerCrc = read_u32_be(trailerBytes, 0) // 注意 BE

            frameStart = recordEnd - tailLen

            // 1. 基础对齐与长度检查
            若 frameStart < HeaderFenceLen 或 frameStart % 4 != 0:
                  fencePos -= 4
                  continue

            // 2. HeadLen 匹配检查
            headLen = read_u32_le(frameStart)
            若 headLen != tailLen 或 headLen < MinFrameLen:
                  fencePos -= 4
                  continue

            // 3. 前置 Fence 检查
            prevFencePos = frameStart - FenceLen
            若 prevFencePos < 0 或 bytes[prevFencePos..prevFencePos+4] != FenceValue:
                  fencePos -= 4
                  continue

            // 4. Trailer CRC 检查
            // 覆盖: Descriptor(4) + Tag(4) + TailLen(4)
            computedCrc = crc32c(trailerBytes[4..16]) // 12 bytes
            若 computedCrc != storedTrailerCrc:
                  fencePos -= 4
                  continue

            // 5. Descriptor 合法性检查
            // 保留位必须为 0
            若 (descriptor & 0x1FFF0000) != 0:
                  fencePos -= 4
                  continue

            输出 frameStart (yield return)
            fencePos = prevFencePos
```
