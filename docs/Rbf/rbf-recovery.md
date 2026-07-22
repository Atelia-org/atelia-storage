---
docId: "rbf-recovery"
title: "RBF Recovery Scan 设计备忘"
status: "Draft"
doc-type: "Design Memo"
normative: false
depends_on:
  - "rbf-format.md"
  - "rbf-interface.md"
  - "rbf-derived-notes.md"
---

# RBF Recovery Scan 设计备忘

**本文档性质**：非规范性设计备忘，用于指导实现与后续维护。规范性线格式与正常读取契约仍以 [rbf-format.md](rbf-format.md) 和 [rbf-interface.md](rbf-interface.md) 为准。

## 1. 背景

RBF 已有 `IRbfFile.ScanReverse()`，用于从可信的 `TailOffset` 逐帧逆向枚举 `RbfFrameInfo`。该路径服务正常 replay/recovery 热路径，语义是“trusted-tail enumeration”：当前位置必须恰好位于合法 `[Frame][Fence]` 之后；若当前候选损坏，枚举器硬停止并通过 `TerminationError` 暴露错误。

离线数据救援/分析需要另一类能力：当文件尾部有垃圾、未完成写入、损坏帧或偶然截断时，从不可信尾部向前搜索仍然可验证的 frame tail / `TrailerCodeword`，并给出可用于人工判断或工具截断的候选位置。

因此 Recovery Scan 是单独的 analyzer/rescue surface，不改变 `ScanReverse()` 的生产语义。

## 2. 能力边界

### 2.1 `ScanReverse()`：正常逆序枚举

- 输入假设：`TailOffset` 可信，指向当前逻辑文件尾。
- 算法：从 tail 前的 `TrailerCodeword + Fence` 读取元信息，用 `TailLen` 跳到上一帧。
- 校验：Fence、`TrailerCrc32C`、descriptor、`TailLen` 与基础边界；不校验 `PayloadCrc32C`。
- 损坏策略：遇到损坏候选即硬停止，不 resync。
- 输出语义：连续有效后缀中的业务帧元信息。

### 2.2 Recovery Scan：离线搜索/救援

- 输入假设：文件尾不可信；文件长度甚至可以非 4B 对齐。
- 算法：由 `BoundarySearchStrategy` 决定。默认从给定起点向前按 4B 步进寻找 Fence，再验证 Fence 前的 `TrailerCodeword`；也可用 Rolling CRC32C 直接搜索 `TrailerCodeword`。
- 校验层级：可选择只验证 tail，也可要求前置 Fence + HeadLen，或进一步校验完整 `PayloadCrc32C`。
- 损坏策略：候选失败时继续向前搜索。
- 输出语义：可验证的候选 frame hit，带置信等级和建议截断位置。

## 3. API 外观

初版公开入口：

```csharp
using var scanner = RbfRecovery.OpenReadOnly(path);

foreach (var hit in scanner.ScanBackward()) {
    Console.WriteLine($"Tag={hit.Info.Tag}, truncate={hit.SuggestedTruncateOffset}");
    break;
}
```

核心类型：

- `RbfRecovery.OpenReadOnly(path)`：打开只读离线 scanner。只校验 HeaderFence，不要求文件长度 4B 对齐。
- `RbfRecoveryScanner.ScanBackward(options)`：返回 duck-typed `RbfRecoverySequence`。
- `RbfRecoveryScanOptions.BoundarySearchStrategy`：配置候选边界搜索策略，默认 `Fence`，可切换为 `RollingCrc`。
- `RbfRecoveryHit`：包含 `RbfFrameInfo Info`、`FenceOffset`、`FenceEndOffset`、`SuggestedTruncateOffset` 与 `Confidence`。
- `RbfRecovery.TruncateToSuggestedTail(path, hit)`：显式按 hit 的 `SuggestedTruncateOffset` 截断文件。

`RbfRecoveryHit.Info` 绑定 scanner 内部 reader；若要调用 `Info.ReadPooledFrame()` 等读取方法，scanner 必须仍处于未 Dispose 状态。若只需要 `Ticket/Tag/Length` 等值字段，hit 可在 scanner 释放后继续用于展示或截断建议。

## 4. 边界搜索策略

`RbfRecoveryBoundarySearchStrategy`：

| 策略 | 机制 | 优点 | 限制 |
|------|------|------|------|
| `Fence` | 以 4B 步进寻找 `RBF1` TailFence，再验证 Fence 前的 `TrailerCodeword` | 低误报、直观、适合恢复合法 RBF tail | 若 TailFence 损坏或缺失，会跳过该帧 |
| `RollingCrc` | 使用 `RollingCrc.BackwardScanner(16)` 直接寻找满足 `TrailerCrc32C` residual 的 `TrailerCodeword` | TailFence 损坏/缺失时仍可救回 FrameBytes；单 pass，可利用硬件 CRC32C | 需要后续 HeadLen/前置 Fence/FullFrame 校验降低偶然命中风险 |

`RollingCrc` 策略产出的 hit 可能没有 TailFence，此时 `hit.HasTailFence == false`，`Info.ReadPooledFrame()` 仍可读取完整 FrameBytes，但不能直接把文件截断成合法 RBF tail。调用方需要决定是导出 payload、重写新 RBF 文件，还是手动补写 TailFence。

## 5. 置信等级

`RbfRecoveryConfidence`：

| 等级 | 含义 | 适用场景 |
|------|------|----------|
| `TrailerOnly` | `TrailerCodeword` 可解析且 `TrailerCrc32C` 通过；Fence 策略下同时意味着 TailFence 存在 | 快速发现疑似帧尾；人工分析 |
| `FrameBoundary` | `TrailerOnly` + 前置 Fence 存在 + `HeadLen == TailLen` | 默认救援候选；建议截断点 |
| `FullFrame` | `FrameBoundary` + 完整 `PayloadCrc32C` 通过 | 自动修复前的强校验 |

初版 `RbfRecoveryScanOptions.ValidationLevel` 默认为 `FrameBoundary`。这是比 `TrailerOnly` 更抗误报、又避免读取大 payload 的折中。

## 6. 截断语义

`SuggestedTruncateOffset = hit.Info.Ticket.Offset + hit.Info.Ticket.Length + FenceSize`。

当 `hit.HasTailFence == true` 时，该位置指向被接受帧的尾部 Fence 之后，即一个合法 RBF 文件的逻辑尾。工具可以将文件截断到此位置，以删除其后的垃圾、未完成写入或损坏后缀。

当 `hit.HasTailFence == false` 时，`SuggestedTruncateOffset` 退化为 `FrameEndOffset`，只表示可读 FrameBytes 的结束位置；它不是合法 RBF tail。

截断 helper 的边界：

- MUST 验证建议 offset 非负且 4B 对齐。
- MUST 验证建议 offset 不超过当前文件长度，避免意外扩展文件。
- MUST 验证 HeaderFence。
- MUST 拒绝 `hit.HasTailFence == false` 的候选。
- MUST 验证建议 offset 前方紧邻 TailFence，避免按 stale/wrong hit 截断到非 RBF 边界。
- 对 `RollingCrc` 找到但 `HasTailFence == false` 的 hit，截断 helper 会拒绝；这类 hit 用于数据导出或人工修复，不用于直接生成合法 RBF tail。
- 不负责重新扫描全文件；调用方应先用 scanner 获取 hit，并根据业务风险选择 `FrameBoundary` 或 `FullFrame`。

## 7. 实现路线

默认 `Fence` 策略使用 4B 步进 Fence 搜索：

1. `start = alignDown4((StartOffsetExclusive ?? FileLength) - FenceSize)`。
2. 从 `start` 向前每次减 4，读取 4B 判断是否为 `RBF1`。
3. 命中 Fence 且不是 HeaderFence 时，调用 `ReadTrailerBefore(reader, fenceEnd)` 复用现有 tail 校验。
4. 按 options 执行额外校验：前置 Fence、HeadLen、完整 frame。
5. 接受 hit 后，下一次搜索跳到该帧前置 Fence，避免在已接受 frame 的 payload 内继续误扫。
6. 候选失败时继续向前 4B 搜索。

`RollingCrc` 策略使用 `RollingCrc.BackwardScanner(TrailerCodewordHelper.Size)` 直接搜索 `TrailerCodeword`：

1. 从 `StartOffsetExclusive ?? FileLength` 开始向前分块读取。
2. 将普通顺序的 chunk 交给 `BackwardScanner(16)`，scanner 内部逆向消费。
3. 命中 codeword 后，用 `trailerOffset = searchStartOffset - scanner.Processed` 映射回文件 offset。
4. 解析 `TrailerCodeword`，用 `TailLen` 回推出 `frameStart = trailerEnd - TailLen`。
5. 按 options 执行额外校验：前置 Fence、HeadLen、完整 frame。
6. 接受 hit 后，下一次搜索跳到该帧起点之前，避免在已接受 frame 的 payload 内继续误扫。
