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
    - HeadLen 必须符合计算公式（@[H-HEADLEN-FORMULA](rbf-format.md)）
2. **值域合法性**
    - FrameStatus 保留位必须为 0（@[F-FRAMESTATUS-RESERVED-BITS-ZERO](rbf-format.md)）
    - FrameStatus 所有字节必须相同（@[F-FRAMESTATUS-FILL](rbf-format.md)）
    - Fence 必须匹配 `RBF1`（@[F-FENCE-VALUE-IS-RBF1-ASCII-4B](rbf-format.md)）
3. **布局约束**
    - Frame 起始位置必须 4 字节对齐（@[F-FRAME-4B-ALIGNMENT](rbf-format.md)）
    - Frame 必须位于 Genesis Fence 之后（@[F-FILE-STARTS-WITH-GENESIS-FENCE](rbf-decisions.md)）

### derived [H-FILE-MINIMUM-LENGTH] 最小文件长度
由 @[F-FILE-STARTS-WITH-GENESIS-FENCE](rbf-decisions.md) 推导：有效 RBF 文件长度 >= 4（至少包含 Genesis Fence）。

### derived [H-HEADLEN-FORMULA] HeadLen计算公式
由 @[F-FRAMEBYTES-FIELD-OFFSETS](rbf-format.md) 推导：`HeadLen = 4 (HeadLen) + 4 (FrameTag) + PayloadLen + StatusLen + 4 (TailLen) + 4 (CRC32C)`

### derived [D-RBF-FORMAT-MIN-HEADLEN] 最小HeadLen推导
see: @[H-HEADLEN-FORMULA](rbf-format.md), @[F-STATUSLEN-ENSURES-4B-ALIGNMENT](rbf-format.md)

- 当 `PayloadLen = 0` 时，`PayloadLen % 4 = 0`。
- 代入 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT] 可得 `StatusLen = 4`。
- 代入 @[H-HEADLEN-FORMULA] 可得：
      $$\text{HeadLen} = 16 + \text{PayloadLen} + \text{StatusLen} = 16 + 0 + 4 = 20$$

因此：最小 `HeadLen = 20`。

### derived [D-RBF-FORMAT-STATUSLEN-RANGE-TABLE] StatusLen值域枚举
```clause-matter
see: @[F-STATUSLEN-ENSURES-4B-ALIGNMENT](rbf-format.md)"
```
**依据 SSOT**：`rbf-format.md` 的 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]

| PayloadLen % 4 | StatusLen |
|----------------|----------|
| 0 | 4 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

### derived [D-RBF-FORMAT-STATUSLEN-FORMULA-PROPERTIES] StatusLen公式性质
see: @[F-STATUSLEN-ENSURES-4B-ALIGNMENT](rbf-format.md)

从公式：
$$\text{StatusLen} = 1 + \big((4 - ((\text{PayloadLen}+1) \bmod 4)) \bmod 4\big)$$

可知：
- `StatusLen ∈ {1, 2, 3, 4}`（因为括号内的值域为 `{0,1,2,3}`）。
- `(PayloadLen + StatusLen) % 4 == 0`：
      - 令 $r = (\text{PayloadLen}+1) \bmod 4$，则 $r \in \{0,1,2,3\}$。
      - 由公式可得 $\text{StatusLen}-1 = (4-r) \bmod 4$。
      - 因此：
            $$ (\text{PayloadLen}+\text{StatusLen}) \bmod 4 = (r-1 + 1 + (4-r)) \bmod 4 = 0 $$

### derived [D-RBF-FORMAT-TOMBSTONE-CRC-EXAMPLE] Tombstone最小帧CRC覆盖算例
see: @[F-CRC32C-COVERAGE](rbf-format.md), @[H-HEADLEN-FORMULA](rbf-format.md), @[F-FRAMESTATUS-RESERVED-BITS-ZERO](rbf-format.md)

场景：`PayloadLen = 0`（Tombstone 帧，最小帧）

- 由 @[D-RBF-FORMAT-MIN-HEADLEN] 得：`HeadLen = 20`，因此 `StatusLen = 4`。
- CRC 覆盖区间（半开区间）：
      - 起始（含）：`frameStart + 4`（从 FrameTag 开始）
      - 结束（不含）：`frameStart + HeadLen - 4 = frameStart + 16`
      - 覆盖长度：`HeadLen - 8 = 12` 字节

覆盖内容：`FrameTag(4B) + FrameStatus(4B) + TailLen(4B)`。

### derived [D-RBF-FORMAT-CRC-BYTE-OFFSET] CRC字节偏移推导
see: @[F-CRC32C-COVERAGE](rbf-format.md), @[F-FRAMEBYTES-FIELD-OFFSETS](rbf-format.md)

设 `frameStart` 为 FrameBytes 起始地址（即 HeadLen 字段位置），`frameEnd` 为 FrameBytes 末尾（即 CRC32C 字段末尾）：

```
CRC 输入区间 = [frameStart + 4, frameEnd - 4)   // 半开区间
             = [frameStart + 4, frameStart + HeadLen - 4)
```

**推导说明**：
- 由 @[F-CRC32C-COVERAGE] 可知 CRC 覆盖 `FrameTag + Payload + FrameStatus + TailLen`，不覆盖 `HeadLen` 和 `CRC32C` 本身。
- 由 @[F-FRAMEBYTES-FIELD-OFFSETS] 可知 `frameEnd = frameStart + HeadLen`。
- 因此 CRC 输入区间为 `[frameStart + 4, frameStart + HeadLen - 4)`。

**注**：FrameStatus 在 CRC 覆盖范围内，Tombstone 标记受 CRC 保护。
Tombstone 帧虽无 Payload，但其 FrameTag、FrameStatus（含 Tombstone 标记位）、TailLen 均受 CRC 保护。

### derived [D-RBF-FORMAT-STATUSLEN-REVERSE] Reader反推PayloadLen/StatusLen算法
see: @[F-FRAMEBYTES-FIELD-OFFSETS](rbf-format.md), @[F-FRAMESTATUS-RESERVED-BITS-ZERO](rbf-format.md), @[F-FRAMESTATUS-FILL](rbf-format.md), @[H-HEADLEN-FORMULA](rbf-format.md)

读取路径：
```
1. statusByteOffset = frameStart + HeadLen - 9   // TailLen(4) + CRC(4) + 1 = 9
2. statusByte = bytes[statusByteOffset]          // FrameStatus 最后一字节
3. StatusLen = (statusByte & 0x03) + 1           // 从位域提取
4. PayloadLen = HeadLen - 16 - StatusLen         // 反推
```

注：由于 @[F-FRAMESTATUS-FILL]（FrameStatus 全字节同值），Reader 读取 FrameStatus 的任意一个字节即可得到 `StatusLen`。

### derived [D-RBF-FORMAT-FRAMESTATUS-BITMASK] FrameStatus位域掩码判断规则
see: @[F-FRAMESTATUS-RESERVED-BITS-ZERO](rbf-format.md)

```
IsTombstone = (status & 0x80) != 0
IsValid     = (status & 0x80) == 0
StatusLen   = (status & 0x03) + 1
IsMvpValid  = (status & 0x7C) == 0   // Reserved bits must be zero
```

### derived [D-RBF-FORMAT-REVERSE-SCAN-PSEUDOCODE] ReverseScan参考伪代码
see: @[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD](rbf-format.md), @[R-RESYNC-SCAN-BACKWARD-4B-TO-GENESIS](rbf-format.md), @[F-FRAMING-FAIL-REJECT](rbf-format.md), @[F-CRC-FAIL-REJECT](rbf-format.md)"

本节提供一种可行的参考实现（伪代码），用于帮助实现者快速落地。
该伪代码 **不是** 唯一实现方式；实现 MAY 采用 mmap / 分块读取 / SIMD 搜索等技巧。
验收时以 SSOT 中的“可观察行为契约”和 framing/CRC 判定为准。

```
输入: fileLength
输出: 通过校验的 Frame 起始地址列表（从尾到头）
常量:
   GenesisLen = 4
   FenceLen   = 4
   MinFrameLen = 20  // @[D-RBF-FORMAT-MIN-HEADLEN]

辅助:
   alignDown4(x) = x - (x % 4)   // 前置条件: x >= 0（RBF 地址均为非负）

1) 若 fileLength < GenesisLen: 返回空   // 不完整文件，fail-soft
2) 若 fileLength == GenesisLen: 返回空  // 仅 Genesis Fence，无 Frame
3) fencePos = alignDown4(fileLength - FenceLen)
4) while fencePos >= 0:
       a) 若 fencePos == 0: 停止（到达 Genesis Fence）
       b) 若 bytes[fencePos..fencePos+4] != FenceValue:
               fencePos -= 4
               continue   // Resync: 寻找 Fence

       c) // 现在 fencePos 指向一个 Fence
            recordEnd = fencePos
            若 recordEnd < GenesisLen + MinFrameLen:
                  fencePos -= 4
                  continue

            读取 tailLen @ (recordEnd - 8)
            读取 storedCrc @ (recordEnd - 4)
            frameStart = recordEnd - tailLen

            若 frameStart < GenesisLen 或 frameStart % 4 != 0:
                  fencePos -= 4
                  continue

            prevFencePos = frameStart - FenceLen
            若 prevFencePos < 0 或 bytes[prevFencePos..prevFencePos+4] != FenceValue:
                  fencePos -= 4
                  continue

            读取 headLen @ frameStart
            若 headLen != tailLen 或 headLen % 4 != 0 或 headLen < MinFrameLen:
                  fencePos -= 4
                  continue

            // CRC 覆盖范围是 [frameStart+4, recordEnd-4)
            computedCrc = crc32c(bytes[frameStart+4 .. recordEnd-4])
            若 computedCrc != storedCrc:
                  fencePos -= 4
                  continue

            // FrameStatus 校验（见 @[F-FRAMESTATUS-RESERVED-BITS-ZERO] 和 @[F-FRAMESTATUS-FILL]）
            statusByteOffset = frameStart + headLen - 9
            statusByte = bytes[statusByteOffset]
            statusLen = (statusByte & 0x03) + 1
            若 (statusByte & 0x7C) != 0:   // Reserved bits MUST be zero
                  fencePos -= 4
                  continue
            // 验证 FrameStatus 所有字节一致
            for i in 1 .. statusLen-1:
                  若 bytes[statusByteOffset - i] != statusByte:
                        fencePos -= 4
                        continue outer   // 跳到外层 while

            输出 frameStart
            fencePos = prevFencePos
```
