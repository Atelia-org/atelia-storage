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
- 算法：从给定起点向前按 4B 步进寻找 Fence，再验证 Fence 前的 `TrailerCodeword`。
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
- `RbfRecoveryHit`：包含 `RbfFrameInfo Info`、`FenceOffset`、`FenceEndOffset`、`SuggestedTruncateOffset` 与 `Confidence`。
- `RbfRecovery.TruncateToSuggestedTail(path, hit)`：显式按 hit 的 `SuggestedTruncateOffset` 截断文件。

`RbfRecoveryHit.Info` 绑定 scanner 内部 reader；若要调用 `Info.ReadPooledFrame()` 等读取方法，scanner 必须仍处于未 Dispose 状态。若只需要 `Ticket/Tag/Length` 等值字段，hit 可在 scanner 释放后继续用于展示或截断建议。

## 4. 置信等级

`RbfRecoveryConfidence`：

| 等级 | 含义 | 适用场景 |
|------|------|----------|
| `TrailerOnly` | Fence 存在，`TrailerCodeword` 可解析且 `TrailerCrc32C` 通过 | 快速发现疑似帧尾；人工分析 |
| `FrameBoundary` | `TrailerOnly` + 前置 Fence 存在 + `HeadLen == TailLen` | 默认救援候选；建议截断点 |
| `FullFrame` | `FrameBoundary` + 完整 `PayloadCrc32C` 通过 | 自动修复前的强校验 |

初版 `RbfRecoveryScanOptions.ValidationLevel` 默认为 `FrameBoundary`。这是比 `TrailerOnly` 更抗误报、又避免读取大 payload 的折中。

## 5. 截断语义

`SuggestedTruncateOffset = hit.Info.Ticket.Offset + hit.Info.Ticket.Length + FenceSize`。

该位置指向被接受帧的尾部 Fence 之后，即一个合法 RBF 文件的逻辑尾。工具可以将文件截断到此位置，以删除其后的垃圾、未完成写入或损坏后缀。

截断 helper 的边界：

- MUST 验证建议 offset 非负且 4B 对齐。
- MUST 验证建议 offset 不超过当前文件长度，避免意外扩展文件。
- MUST 验证 HeaderFence。
- MUST 验证建议 offset 前方紧邻 TailFence，避免按 stale/wrong hit 截断到非 RBF 边界。
- 不负责重新扫描全文件；调用方应先用 scanner 获取 hit，并根据业务风险选择 `FrameBoundary` 或 `FullFrame`。

## 6. 实现路线

MVP 使用 4B 步进 Fence 搜索：

1. `start = alignDown4((StartOffsetExclusive ?? FileLength) - FenceSize)`。
2. 从 `start` 向前每次减 4，读取 4B 判断是否为 `RBF1`。
3. 命中 Fence 且不是 HeaderFence 时，调用 `ReadTrailerBefore(reader, fenceEnd)` 复用现有 tail 校验。
4. 按 options 执行额外校验：前置 Fence、HeadLen、完整 frame。
5. 接受 hit 后，下一次搜索跳到该帧前置 Fence，避免在已接受 frame 的 payload 内继续误扫。
6. 候选失败时继续向前 4B 搜索。

后续可用 `RollingCrc.BackwardScanner(TrailerCodewordHelper.Size)` 加速 `TrailerCodeword` 搜索，但不作为初版必要条件。当前选择 Fence-first 的原因是更直观、易审计，并且完全贴合 `[Fence] ([Frame] [Fence])*` 布局。
