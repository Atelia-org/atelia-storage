# RbfSegmentStore 使用指南

本文面向后续 LLM Coding Agent 会话，说明如何在上层模块中使用 `Atelia.RbfSegmentStore`。
设计背景见 [RbfSegmentStore 设计基线](../../docs/EventJournal/rbf-segment-store-design.md)。

## 定位

`RbfSegmentStore` 是 RBF 文件之上的 segment 生命周期层。它负责：

- `SegmentNumber` 到 segment 文件路径的映射。
- 创建、打开、扫描和轮转 segment。
- active writer lease 与 reader lease。
- historical reader pool 与 lease 保护。
- 打开现有 store 时的 active-tail recovery 编排。

它不负责：

- 调用 `Append` / `ReadFrame` 替上层读写 frame。
- 解释 frame tag、payload schema 或 EventJournal 语义。
- 构造 `FrameAddress` / `EventAddress`。
- 提供线程安全、多 writer 或跨进程事务。

## 文件布局

segment 文件固定使用 `.rbf` 扩展名，不带 EventJournal 等上层语义：

```text
<store>/
  segments/
    000000/
      00000001.rbf
      ...
      000003ff.rbf
    000001/
      00000400.rbf
```

规则摘要：

- `SegmentNumber = 0` 保留，永远不是合法 segment。
- bucket 目录名是 `segmentNumber >> 10` 的 6 位小写 hex。
- segment 文件名是完整 `SegmentNumber` 的 8 位小写 hex 加 `.rbf`。
- 打开 store 时要求 segment 编号从 `1` 到 active segment 连续。
- 当前没有 manifest；路径布局就是固定格式约定。

## 打开模式

```csharp
using Atelia.RbfSegmentStore;

using var created = RbfSegmentStore.CreateNew(storePath);
using var opened = RbfSegmentStore.OpenExisting(storePath);
using var store = RbfSegmentStore.OpenOrCreate(storePath);
```

入口语义：

| 方法 | 适用场景 | 缺失 store | 已存在 store | 空 `segments/` |
|:-----|:---------|:-----------|:-------------|:---------------|
| `CreateNew` | 明确创建新 store | 创建 | 抛异常 | 不适用 |
| `OpenExisting` | 只接受已有 store | 抛异常 | 打开 | 抛异常 |
| `OpenOrCreate` | CLI / 原型默认入口 | 创建 | 打开 | 创建 segment `1` |

结构错误、命名错误、编号缺口、corruption、权限和 I/O 错误都直接抛异常。当前 lease API 不使用 `AteliaResult`。

## Options

```csharp
var options = new RbfSegmentStoreOptions {
    SegmentSizeThresholdBytes = 64L * 1024 * 1024 * 1024,
    HistoricalReaderPoolCapacity = 32,
    CacheMode = RbfCacheMode.Slots16,
    RecoverActiveTailOnOpen = true
};
```

说明：

- `SegmentSizeThresholdBytes` 是 soft threshold，只在 `OpenActiveWriter()` 借出前检查；单个 frame 可能让 segment 最终超过阈值。
- `HistoricalReaderPoolCapacity = 0` 表示不保留 idle historical reader，但 live lease 仍不会被关闭。
- `CacheMode` 直接传给底层 `RbfFile`。
- `RecoverActiveTailOnOpen = true` 时，打开现有 store 会扫描 active segment 并截断到最后一个完整 frame 尾部。header-only 空 active segment 是合法状态。

测试里可以把 `SegmentSizeThresholdBytes` 设得很小来触发轮转。

## 写入范式

上层每次写入时现用现借 active writer lease，写完马上释放：

```csharp
using Atelia.Data;
using Atelia.RbfSegmentStore;

using var store = RbfSegmentStore.OpenOrCreate(storePath);

using var lease = store.OpenActiveWriter();
SizedPtr ticket = lease.File.Append(tag: 42, payload).Unwrap();
uint segmentNumber = lease.SegmentNumber;

// 上层可组合成自己的跨 segment 地址：
// var address = new FrameAddress(ticket, segmentNumber);
```

注意：

- `RbfSegmentStore` 不替你调用 `Append`、`BeginAppend` 或 `DurableFlush`。
- lease dispose 后不得继续使用其中的 `IRbfFile`，即使你提前把 `lease.File` 存到了局部变量。
- active segment 同一时刻只能有一个 live active lease。未释放 writer lease 时再打开 active reader/writer 会抛异常。

## 读取范式

按地址里的 `SegmentNumber` 借 reader lease，再用 RBF 自己的读取 API：

```csharp
using var lease = store.OpenReader(segmentNumber);
using RbfPooledFrame frame = lease.File.ReadPooledFrame(ticket).Unwrap();

uint tag = frame.Tag;
byte[] payload = frame.PayloadAndMeta.ToArray();
```

active segment reader 复用 active read/write `IRbfFile` 单例；historical segment reader 通过 read-only pool 打开。

## 轮转行为

`OpenActiveWriter()` 会在借出前检查当前 active segment 的 `TailOffset`：

1. 若 `TailOffset < SegmentSizeThresholdBytes`，继续返回当前 active segment。
2. 若 `TailOffset >= SegmentSizeThresholdBytes`，关闭当前 active segment，创建下一个 segment，然后返回新 segment。

轮转只发生在两次 append 之间。RBF frame 不会跨 segment；一个上层逻辑事件如果包含多个 frame，可以由上层决定是否允许这些 frame 分布在不同 segment。

## 单线程模型

MVP 固定为单写串读模型：

- `RbfSegmentStore` 实例不承诺线程安全。
- 不要并发使用同一个 store。
- active segment 读写顺序化。
- historical reader 可以有多个 live lease；pool eviction 不会关闭 live lease。
- 跨进程 writer 和共享写句柄不在 MVP 范围内。

## Recovery 边界

`OpenExisting` / `OpenOrCreate` 默认会对 active segment 执行 tail recovery。实现顺序是：

1. 用 `RbfRecovery.OpenReadOnly(path).ScanBackward()` 找第一个有效 `RbfRecoveryHit`。
2. 释放 scanner。
3. 调用 `RbfRecovery.TruncateToSuggestedTail(path, hit)`。
4. 用 `RbfFile.OpenExisting` 打开 active segment。

不要在 scanner 仍打开时调用 truncate；`TruncateToSuggestedTail` 需要独占打开文件。

closed historical segment 遇到损坏只报告 corruption，不自动截断。

## 常见任务

运行本层测试：

```bash
dotnet test tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj
```

只构建本层：

```bash
dotnet build src/RbfSegmentStore/RbfSegmentStore.csproj
```

格式化本层：

```bash
dotnet format src/RbfSegmentStore/RbfSegmentStore.csproj --no-restore
dotnet format tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj --no-restore
```

## 继续实现上层时的建议

- 在 EventJournal 层定义 `FrameAddress` / `EventAddress`，不要把它们下沉到 `RbfSegmentStore`。
- 在 EventJournal 层决定 frame tag 和 payload schema。
- 若需要 exactly-once、branch/ref、Parent 校验、orphan 处理或 event replay，从上层实现，不要扩张本层职责。
- 若需要跨线程或跨进程并发，先写新的设计基线；不要在当前 MVP API 上悄悄加锁语义。
