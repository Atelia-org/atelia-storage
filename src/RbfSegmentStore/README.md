# RbfSegmentStore 冻结参考指南

main 保留 `Atelia.RbfSegmentStore` 及其测试作为 **RBF1 旧栈的冻结参考代码**。旧栈维护和公开发版归 `RBF1` 分支；main 新能力使用[FrameStore / VersionStore](../../docs/FrameStore-VersionStore/README.md)，不依赖本库。

本轮参考代码精确引用 Rbf/Data/Primitives `[0.2.0-rbf1-preview.1]`，不引用 main 底层源码，不新增 RBF3 运行时适配。依赖拆分状态为 **Implementing，实施中，待验收**，实际构建、测试及 assets 资格见[过渡方案](../../docs/rbf1-reference-transition.md)。

以下描述保留的 v2 segment 布局与 API；v2 是目录布局版本，不是 RBF 格式编号。具体已发布包行为以[根 README 的固定来源](../../README.md#已公开的-rbf1-包与历史事实)为准。v2 拒绝无 locator 的旧目录，没有自动迁移。原合同见[冻结附件](../../docs/EventJournal/bounded-online-io-contracts.md)，历史资格见[候选记录](../../docs/EventJournal/bounded-online-io-delivery.md)；[原始设计基线](../../docs/EventJournal/rbf-segment-store-design.md)保留为背景，不成为 main 新栈权威。

## 定位

`RbfSegmentStore` 是 RBF 文件之上的 segment 生命周期层。它负责：

- `SegmentNumber` 到 segment 文件路径的映射。
- 创建、打开、扫描和轮转 segment。
- active writer lease 与 reader lease。
- historical reader pool 与 lease 保护。
- locator 定点打开、严格尾帧校验与轮转发布。

它不负责：

- 调用 `Append` / `ReadFrame` 替上层读写 frame。
- 解释 frame tag、payload schema 或 EventJournal 语义。
- 构造 `FrameAddress` / `EventAddress`。
- 提供线程安全、多 writer 或跨进程事务。

## 文件布局

segment 文件固定使用 `.rbf` 扩展名，不带 EventJournal 等上层语义。store 支持两种 layout，由 store root 下的目录名区分。

Bucketed layout 适合长期增长的数据流：

```text
<store>/
  buckets/
    000000/
      00000001.rbf
      ...
      000003ff.rbf
    000001/
      00000400.rbf
```

Flat layout 适合很多小型独立对象：

```text
<store>/
  segments/
    00000001.rbf
    00000002.rbf
```

规则摘要：

- `SegmentNumber = 0` 保留，永远不是合法 segment。
- bucketed layout 的 bucket 目录名是 `segmentNumber >> 10` 的 6 位小写 hex。
- flat layout 没有 bucket 目录。
- segment 文件名是完整 `SegmentNumber` 的 8 位小写 hex 加 `.rbf`。
- `active.segment` 是精确 20B 的 locator，编码 layout 与 active segment number。
- 日常打开只探测相反 layout root、active 和 active+1；不枚举库存。全量连续性/命名审计归离线 toolkit。

## 打开模式

```csharp
using Atelia.RbfSegmentStore;

using var created = RbfSegmentStore.CreateNew(storePath);
using var opened = RbfSegmentStore.OpenExisting(storePath);
using var readOnly = RbfSegmentStore.OpenReadOnlyExisting(storePath);
using var store = RbfSegmentStore.OpenOrCreate(storePath);
```

入口语义：

| 方法 | 适用场景 | 缺失 store | 已存在 store | 空 layout root |
|:-----|:---------|:-----------|:-------------|:---------------|
| `CreateNew` | 明确创建新 store | 创建 | 抛异常 | 不适用 |
| `OpenExisting` | 只接受已有 store | 抛异常 | 打开 | 抛异常 |
| `OpenReadOnlyExisting` | 日常只读查询/只读挂载 | 抛异常 | 共享只读打开，不 recovery | 抛异常 |
| `OpenOrCreate` | CLI / 原型默认入口 | 创建 | 严格打开 | 拒绝缺 locator 的半成品 |

打开缺 locator 的旧目录或半成品抛 `StorageOpenException(FormatUnsupported, LegacyOrIncompleteLayout)`；locator 损坏、layout 冲突、next 已存在及坏尾返回对应可识别维护错误。权限和设备 I/O 保留原异常。
两个打开入口只校验必要 EOF 末帧 framing/CRC，不扫描历史、不 truncate；合法空 active 精确为 4B HeaderFence。active 空且编号大于 1 时最多访问前一段，前一段必须非空。历史 segment 缺失按地址访问时报告。

使用 `StorageOpenException.Kind` 区分 `FormatUnsupported` 与 `MaintenanceRequired`，再按 `ReasonCode`、`StoragePath`、可用 `Offset` / `ObservedVersion` 判定，不解析 message。日常打开成功不等于全历史健康；完整 EventJournal 历史/目录验证使用[离线 toolkit](../../tools/EventJournal.Toolkit/README.md)。
只读实例允许 `OpenReader()`，但 `OpenActiveWriter()` 会 fail-fast。

## Options

```csharp
var options = new RbfSegmentStoreOptions {
    NewStoreLayout = RbfSegmentStoreLayout.Bucketed,
    SegmentSizeThresholdBytes = 64L * 1024 * 1024 * 1024,
    HistoricalReaderPoolCapacity = 32,
    CacheMode = RbfCacheMode.Slots16
};
```

说明：

- `NewStoreLayout` 只影响创建新 store；打开已有 store 时从 locator 读取实际 layout。
- `SegmentSizeThresholdBytes` 是 soft threshold，只在 `OpenActiveWriter()` 借出前检查；必须 4-byte aligned、
  大于 header-only tail 且不超过 `SizedPtr.MaxOffset`。单个 frame 可能让 segment 最终超过阈值。
- `HistoricalReaderPoolCapacity = 0` 表示不保留 idle historical reader，但 live lease 仍不会被关闭。
- `CacheMode` 直接传给底层 `RbfFile`。
- `RecoverActiveTailOnOpen` 已移除；新 runtime 不自动恢复旧格式或坏尾。

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

- `RbfSegmentStore` 不替你调用 `Append` 或 `BeginAppend`，也不会在每次写入后自动落盘。
- 若调用方需要“最多 overshoot 一个 Frame”的界限，必须像上例一样每个逻辑写入重新借 lease，并在该 lease
  中只 append 一个 Frame；底层 `IRbfFile` 本身允许同一 lease 连续 append 多次。
- lease dispose 后不得继续使用其中的 `IRbfFile`，即使你提前把 `lease.File` 存到了局部变量。
- active segment 同一时刻只能有一个 live active lease。未释放 writer lease 时再打开 active reader/writer 会抛异常。

## 单文件 durable 确认

上层在发布依赖某个 segment 的 tag、ref 或其他元数据之前，可以显式调用：

```csharp
store.ConfirmDurable(segmentNumber);
```

它只对指定的一个已有 segment 调用 RBF 的 `DurableFlush`。确认 active segment 不会借 writer lease，
因此不会触发阈值轮转；确认 historical segment 会先拒绝 live historical reader lease，或关闭其 idle
pooled reader 后以临时可写句柄执行 flush。任意 live active lease 都会使确认失败。

该 API 不验证 frame、payload 或上层语义，不执行 tail recovery，不修改文件 bytes，不轮转 segment，
也不确认目录元数据。因此它不提供目录项在断电后的存在性承诺；上层若需要该保证，必须另行定义并实现。

## 读取范式

按地址里的 `SegmentNumber` 借 reader lease，再用 RBF 自己的读取 API：

```csharp
using var lease = store.OpenReader(segmentNumber);
using RbfPooledFrame frame = lease.File.ReadPooledFrame(ticket).Unwrap();

uint tag = frame.Tag;
byte[] payload = frame.PayloadAndMeta.ToArray();
```

active segment reader 复用 active `IRbfFile` 单例（只读 store 使用只读句柄）；historical segment reader 通过 read-only pool 打开。

## 轮转行为

`OpenActiveWriter()` 会在借出前检查当前 active segment 的 `TailOffset`：

1. 若 `TailOffset < SegmentSizeThresholdBytes`，继续返回当前 active segment。
2. 若 `TailOffset >= SegmentSizeThresholdBytes`，flush 旧 active → create-only 创建 next → flush 空 next → 同目录原子覆盖发布 locator → 关闭旧 active → 返回 next。

locator 使用同目录独占 temp，Flush(true) 后关闭，再 overwrite move；不先删除目标。新建也先 flush segment 1 再发布 locator。不保证目录项断电持久性。

轮转只发生在两次 append 之间。RBF frame 不会跨 segment；一个上层逻辑事件如果包含多个 frame，可以由上层决定是否允许这些 frame 分布在不同 segment。
`SegmentNumber` 耗尽时在关闭或替换 active file 前 fail closed，不 wrap 到 `0`。

## 单线程模型

MVP 固定为单写串读模型：

- `RbfSegmentStore` 实例不承诺线程安全。
- 不要并发使用同一个 store。
- active segment 读写顺序化。
- historical reader 可以有多个 live lease；pool eviction 不会关闭 live lease。
- 跨进程 writer 和共享写句柄不在 MVP 范围内。

## 严格打开与 fault 边界

日常打开拒绝坏尾，不自动 recovery。历史 segment 缺失在按地址访问时报告；日常成功不代表全历史健康。
旧 locator 指向 old 且规范 next 路径已存在时停维，不删除或接管 next。

本层 rotation/ConfirmDurable 的 owned operation 异常会 latch fault；此后数据入口拒绝，lease 释放与 Dispose 仍可执行。
参数、只读和 live lease 前置 guard 不 fault。调用方通过 `lease.File` 自行 Append/flush 的异常由调用方负责停止使用；本层不代理其 IRbfFile。
Dispose 标记关闭并尝试释放所有 owned handles；释放异常汇总报告。独立 RBF 仍保留显式 recovery API，但 v2 的日常打开和首版 toolkit 不调用它，也不提供 repair-tail。

## 常见任务

完整新旧两组构建与测试按[根入口](../../README.md#构建与主线包验证)串行执行。只验证本参考层时，先构建匹配的 Release 测试项目：

```powershell
dotnet build tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj -c Release
dotnet test tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj -c Release --no-build
```

只构建本层：

```bash
dotnet build src/RbfSegmentStore/RbfSegmentStore.csproj -c Release
```

格式化本层：

```bash
dotnet format src/RbfSegmentStore/RbfSegmentStore.csproj --no-restore
dotnet format tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj --no-restore
```

本目录 `IsPackable=false`；main Pack/Test-Package 仅交付三个新栈底层包。旧栈维护及包交付在 RBF1 分支进行，本参考测试不为 main 新栈或公开包消费出具资格。

## 参考实现的职责边界

- 在 EventJournal 层定义 `FrameAddress` / `EventAddress`，不要把它们下沉到 `RbfSegmentStore`。
- 在 EventJournal 层决定 frame tag 和 payload schema。
- 若需要 exactly-once、branch/ref、Parent 校验、orphan 处理或 event replay，从上层实现，不要扩张本层职责。
- 若需要跨线程或跨进程并发，先写新的设计基线；不要在当前 MVP API 上悄悄加锁语义。
