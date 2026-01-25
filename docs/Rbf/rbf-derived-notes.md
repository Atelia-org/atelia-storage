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
    - HeadLen 与 TailLen 必须相等（@[F-FRAMEBYTES-LAYOUT](rbf-format.md)）
    - HeadLen 必须 >= 24（MinFrameLen，见 @[D-RBF-FORMAT-MIN-HEADLEN]）
    - HeadLen 必须是 4 的倍数（@[S-RBF-DECISION-4B-ALIGNMENT-ROOT](rbf-decisions.md)）
2. **值域合法性**
    - Fence 必须匹配 `RBF1`（@[F-FENCE-RBF1-ASCII-4B](rbf-format.md)）
    - FrameDescriptor 保留位必须为 0（@[F-FRAME-DESCRIPTOR-LAYOUT](rbf-format.md)）
    - Padding 字节必须全为 0（@[F-PADDING-CALCULATION](rbf-format.md)）
3. **布局约束**
    - Frame 起始位置必须 4 字节对齐（@[S-RBF-DECISION-4B-ALIGNMENT-ROOT](rbf-decisions.md)）
    - Frame 必须位于 HeaderFence 之后（@[F-FILE-STARTS-WITH-FENCE](rbf-decisions.md)）
4. **CRC 校验**
    - TrailerCrc32C 校验必须通过（@[F-TRAILER-CRC-COVERAGE](rbf-format.md)）
    - (Content 读取时) PayloadCrc32C 校验必须通过（@[F-PAYLOAD-CRC-COVERAGE](rbf-format.md)）

### derived [H-FILE-MINIMUM-LENGTH] 最小文件长度
由 @[F-FILE-STARTS-WITH-FENCE](rbf-decisions.md) 推导：有效 RBF 文件长度 >= 4（至少包含 HeaderFence）。

### derived [H-HEADLEN-FORMULA] HeadLen计算公式
由 @[F-FRAMEBYTES-LAYOUT](rbf-format.md) 推导：
`HeadLen = 4 (HeadLen) + PayloadLen + TailMetaLen + PaddingLen + 4 (PayloadCrc32C) + 16 (TrailerCodeword)`
即：`HeadLen = 24 + PayloadLen + TailMetaLen + PaddingLen`

### derived [D-RBF-FORMAT-MIN-HEADLEN] 最小HeadLen推导
see: @[H-HEADLEN-FORMULA], @[F-PADDING-CALCULATION](rbf-format.md)

- 当 `PayloadLen = 0` 且 `TailMetaLen = 0` 时：
    - `(PayloadLen + TailMetaLen) % 4 = 0`
    - `PaddingLen = (4 - 0) % 4 = 0`
- 代入 @[H-HEADLEN-FORMULA] 可得：
      $$\text{HeadLen} = 24 + 0 + 0 + 0 = 24$$

因此：最小 `HeadLen = 24`（Tombstone 帧或空 Payload 帧）。

### derived [D-RBF-FORMAT-PADDING-TABLE] PaddingLen值域枚举
**依据 SSOT**：`rbf-format.md` 的 @[F-PADDING-CALCULATION]

| (PayloadLen + TailMetaLen) % 4 | PaddingLen |
|--------------------------------|------------|
| 0 | 0 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

### derived [D-RBF-FORMAT-TOMBSTONE-EXAMPLE] Tombstone最小帧算例
see: @[F-FRAME-DESCRIPTOR-LAYOUT](rbf-format.md), @[D-RBF-FORMAT-MIN-HEADLEN]

场景：`PayloadLen = 0`, `TailMetaLen = 0`, `IsTombstone = 1`

- `HeadLen = 24`
- `FrameDescriptor`:
    - `IsTombstone = 1`
    - `PaddingLen = 0`
    - `TailMetaLen = 0`
    - Value = `0x80000000` (u32 LE)

### derived [D-RBF-FORMAT-FRAMEDESCRIPTOR-BITMASK] FrameDescriptor位域解析规则
see: @[F-FRAME-DESCRIPTOR-LAYOUT](rbf-format.md)

```csharp
uint descriptor = ...;
bool isTombstone = (descriptor & 0x80000000) != 0;
int paddingLen = (int)((descriptor >> 29) & 0x03);
int tailMetaLen = (int)(descriptor & 0xFFFF);
bool reservedIsZero = (descriptor & 0x1FFF0000) == 0;
```

### derived [D-RBF-FORMAT-REVERSE-SCAN-PSEUDOCODE] ReverseScan逻辑流程
see: @[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD](rbf-format.md), @[R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE](rbf-format.md)

**核心策略**：从文件末尾向前搜索 Fence 标记，并验证 Trailer 结构。

1. **初始化**：
   - 从 `FileLength - 4` 开始向前搜索（4 为 Fence 长度）。
   - 确保起始位置 4 字节对齐（`alignDown4`）。

2. **主循环 (Scan Loop)**：
   - **Step 1: 定位 Fence**
     - 检查当前位置是否为 Fence (`RBF1`)。
     - 若不是或位置 < `HeaderFence`：向前移动 4 字节，继续下一轮。
     - 若位置 == 0（HeaderFence）：扫描结束。

   - **Step 2: 读取 Trailer**
     - 读取 Fence 前的 16 字节 (`TrailerCodeword`)。
     - 解析出 `TailLen`, `FrameTag`, `FrameDescriptor`, `TrailerCrc32C`。

   - **Step 3: 候选帧定位**
     - 计算 `FrameStart = FenceEnd - TailLen`。
     - 验证 `FrameStart` 合法性（>= 4 且 4B 对齐）。

   - **Step 4: 完整性校验 (Framing Check)**
     - **HeadLen**：读取 `FrameStart` 处的 HeadLen，必须等于 `TailLen` 且 >= 24。
     - **Pre-Fence**：检查 `FrameStart - 4` 处是否存在前置 Fence。
     - **TrailerCRC**：计算 `TrailerCodeword` 后 12 字节的 CRC32C，必须匹配 `TrailerCrc32C` (Big-Endian)。
     - **Descriptor**：保留位必须为 0。

   - **Step 5: 产出与迭代**
     - 若所有校验通过：产出该帧信息，将扫描游标移动到 `Pre-Fence` 位置。
     - 若任一校验失败：视为损坏或偶然匹配，仅将扫描游标向前移动 4 字节（Resync）。

**注意**：此流程完全依赖元信息，不读取 `Payload` 内容，也不校验 `PayloadCrc32C`。

### derived [R-REVERSE-SCAN-FRAMING-CHECKLIST] 逆向扫描Framing校验清单
Reverse Scan MUST 按如下清单执行 framing 校验（规范性）：

**MUST 读取**：
- TrailerCodeword（固定 16 字节，从帧末尾 -16B 位置开始）

**MUST 验证**：
- `TrailerCrc32C` 校验通过（覆盖 FrameDescriptor + FrameTag + TailLen）
- `FrameDescriptor` 保留位（bit 28-16）为 0
- `TailLen` 满足：`MinFrameLength <= TailLen <= MaxFrameLength` 且 4B 对齐
- `PaddingLen`（bit 30-29）在 0-3 范围内（位宽天然保证）

**MUST NOT 读取/验证**：
- `HeadLen`（位于 PayloadCodeword 头部）
- `HeadLen == TailLen` 交叉校验
- `PayloadCrc32C`（完整帧校验由随机读取路径负责）

### derived [S-RBF-PAYLOADLENGTH-FORMULA] PayloadLength计算公式
从 TrailerCodeword 解码 `PayloadLength` 时，MUST 使用如下公式：

```
PayloadLength = TailLen - FixedOverhead - TailMetaLen - PaddingLen
```

其中：
- `FixedOverhead = 4 (HeadLen) + 4 (PayloadCrc32C) + 16 (TrailerCodeword) = 24`
- `TailMetaLen = FrameDescriptor.TailMetaLen`（bit 15-0）
- `PaddingLen = FrameDescriptor.PaddingLen`（bit 30-29）

**后置条件**：`PayloadLength >= 0`，否则视为 framing 校验失败。

*示例（Informative）*：
| TailLen | TailMetaLen | PaddingLen | PayloadLength |
|---------|-------------|------------|---------------|
| 28      | 0           | 0          | 4             |
| 32      | 0           | 0          | 8             |
| 48      | 8           | 0          | 16            |
| 36      | 4           | 3          | 5             |
