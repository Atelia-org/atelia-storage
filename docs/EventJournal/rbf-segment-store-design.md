# RbfSegmentStore 设计基线

> **状态**：Design Baseline / 可作为独立 C# Project 的施工输入
> **日期**：2026-07-22
> **上层依赖者**：[EventJournal 功能需求与粗粒度设计基线](event-journal-requirements-and-design.md)
> **底层依赖**：[RBF Layer Interface Contract](../Rbf/rbf-interface.md)

## 1. 文档定位

`RbfSegmentStore` 是 EventJournal 之下的轻量基础层。它不把 store 文件夹伪装成一个新的跨 segment RBF 文件，也不封装 RBF 的 frame append/read API；它只负责 RBF segment 文件的路径、创建、打开、轮转、lease、打开池和 active-tail 恢复编排。

上层拿到已打开的 RBF 文件后，仍然直接使用 RBF 自身的 `Append`、`BeginAppend`、`ReadFrame`、`ReadPooledFrame` 和 tail recovery 能力（`RbfRecoveryScanner`）。`FrameAddress` 由上层把 RBF 返回的 `SizedPtr Ticket` 与 lease 中的 `SegmentNumber` 组合得到。

## 2. 职责边界

`RbfSegmentStore` 负责：

- `SegmentNumber` 到 segment 文件路径的确定性映射。
- 创建新的 RBF segment 文件。
- 每次写入时按需借出 active writer lease。
- 按 `SegmentNumber` 借出 reader lease；目标可以是 active segment 或 closed historical segment。
- historical RBF 文件的 bounded pool 与 lease 保护。
- active segment 达到大小阈值后的轮转。
- 打开 store 时通过目录扫描发现现有 segments 与 active segment。
- 编排 active segment 的有效尾恢复；closed segment 损坏只报告，不截断。

`RbfSegmentStore` 不负责：

- 调用 `IRbfFile.Append` 或 `IRbfFile.ReadFrame` 替上层读写 frame。
- 解释 RBF frame tag。
- 构造或校验 `EventAddress` / `AddressHint`。
- 解释 EventJournal 的 `EventFrame` / TailMeta header schema。
- 验证 Parent、维护 branch/ref、分析 Event-level orphan。
- 对外提供 exactly-once 或跨进程多 writer 事务。

## 3. Segment 路径布局

MVP 固定使用按 `SegmentNumber` 高低位拆分的 hex 路径布局。每个 bucket 对应 1024 个 segment 编号槽位，bucket 目录名来自 `SegmentNumber` 去掉低 10 bit 后的高位，segment 文件名使用完整 `SegmentNumber` 的 8 位小写 hex 表示。

```csharp
const int SegmentBucketBits = 10;
const uint SegmentBucketMask = (1u << SegmentBucketBits) - 1;

uint bucketNumber = segmentNumber >> SegmentBucketBits;
uint segmentSlot = segmentNumber & SegmentBucketMask;
```

```text
<store>/
  segments/
    000000/
      00000001.rbf
      ...
      000003ff.rbf
    000001/
      00000400.rbf
      ...
      000007ff.rbf
```

规则：

1. `SegmentBucketBits = 10`，每个 bucket 覆盖 1024 个 segment 编号槽位。
2. bucket 目录名为 `bucketNumber` 的 6 位小写 hex 编码。
3. segment 文件名为完整 `SegmentNumber` 的 8 位小写 hex 编码，后缀为 `.rbf`。
4. `SegmentNumber = 0` 保留给 null/未设置编码，因此 `segments/000000/00000000.rbf` 永远不存在；第一个 bucket 只有 1023 个有效 segment。
5. `segmentSlot` 仅用于校验、诊断或未来局部索引；文件名仍使用完整 `segmentNumber`，避免文件脱离目录后失去自描述性。
6. 路径始终是可重建的派生信息，不写入 `FrameAddress` 或 `EventAddress`。

MVP 不引入 manifest 作为路径布局的权威来源。布局是代码与版本的固定约定；打开 store 时通过目录扫描验证现有 segment 文件是否符合命名规则和连续性要求。

## 4. 打开、创建与发现

MVP 固定区分三种 store 入口，避免“打开”动作隐式创建或覆盖数据：

```csharp
public sealed class RbfSegmentStore : IRbfSegmentStore {
    public static RbfSegmentStore CreateNew(string storePath, RbfSegmentStoreOptions? options = null);
    public static RbfSegmentStore OpenExisting(string storePath, RbfSegmentStoreOptions? options = null);
    public static RbfSegmentStore OpenOrCreate(string storePath, RbfSegmentStoreOptions? options = null);
}
```

入口语义：

1. `CreateNew` 要求 `storePath` 不存在；它创建目录结构与 `SegmentNumber = 1` 的 active segment。若目录或文件已存在，MUST 抛出异常，不得复用已有内容。
2. `OpenExisting` 要求 `storePath/segments/` 已存在且至少包含一个合法 segment；它只打开和恢复现有 store，不创建 segment。若 store 为空或缺失，MUST 抛出异常。
3. `OpenOrCreate` 在 store 不存在或没有任何 segment 时创建 `SegmentNumber = 1`；若发现任何不合法 segment 文件或编号缺口，MUST 抛出异常，不得“修复后继续”。

打开已有 store 时，`RbfSegmentStore` 扫描 `segments/`：

1. 忽略不存在的 bucket 目录。
2. 拒绝无法按固定 hex bit-split 规则解析的 segment 文件。
3. 拒绝 `SegmentNumber = 0` 对应的 segment 文件。
4. 验证 segment 文件所在 bucket 与文件名中的完整 `SegmentNumber` 一致。
5. 验证已存在 segment 编号从 `1` 到最大值连续；MVP 不允许中间缺口。
6. 最大 `SegmentNumber` 是 active segment；更小的 segment 是 closed historical segment。
7. 若没有任何 segment，只有 `CreateNew` 与 `OpenOrCreate` 可以创建 `SegmentNumber = 1`；`OpenExisting` 必须报告空 store。

该策略减少 manifest 与目录状态之间的不一致面。未来若需要 store 级配置、校验或迁移记录，可另行增加 manifest，但不应让 manifest 成为 segment path table。

## 4.1 Options

首轮 `RbfSegmentStoreOptions` 保持最小形状，只承载 store 生命周期需要的参数：

```csharp
public sealed class RbfSegmentStoreOptions {
    public long SegmentSizeThresholdBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public int HistoricalReaderPoolCapacity { get; init; } = 32;
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;
}
```

约束：

1. `SegmentSizeThresholdBytes` 是 soft threshold，只在 `OpenActiveWriter()` 借出前检查；它 MUST 为正数且 4B 对齐。测试可以设置很小的值触发轮转。
2. `HistoricalReaderPoolCapacity` 表示最多保留多少个 idle historical reader；`0` 表示不缓存 idle reader，但仍必须尊重 live lease。
3. `CacheMode` 直接传给底层 `RbfFile.CreateNew` / `OpenExisting` / `OpenReadOnlyExisting`。
4. `RecoverActiveTailOnOpen` 控制 `OpenExisting` / `OpenOrCreate` 是否在打开现有 active segment 时执行 §8 的 active-tail recovery。`CreateNew` 创建的空 segment 不需要 recovery。

## 5. Lease API 形状

MVP API：

```csharp
public interface IRbfSegmentStore : IDisposable {
    RbfSegmentWriterLease OpenActiveWriter();
    RbfSegmentReaderLease OpenReader(uint segmentNumber);

    uint ActiveSegmentNumber { get; }
    RbfSegmentStoreOptions Options { get; }
}

public readonly struct RbfSegmentWriterLease : IDisposable {
    public uint SegmentNumber { get; }
    public IRbfFile File { get; }
}

public readonly struct RbfSegmentReaderLease : IDisposable {
    public uint SegmentNumber { get; }
    public IRbfFile File { get; }
}
```

错误模型：

- store 结构错误、命名规则错误、编号缺口、corruption、路径权限、底层 I/O 错误、句柄已释放等，MUST 直接抛异常。
- `OpenReader(0)`、读取不存在的 future segment、lease dispose 后继续使用 `IRbfFile` 等调用方契约违例，MUST 抛异常。
- 当前 lease API 不使用 `AteliaResult`。未来若新增“操作可正常失败，但需要返回比 `false` 更丰富原因”的方法，可以按 Result-Pattern 返回 `AteliaResult<T>`。

写路径不长期持有 active writer lease。上层每次写入时现用现借，写完马上释放：

```csharp
using var lease = store.OpenActiveWriter();
SizedPtr ticket = lease.File.Append(tag, payload);
FrameAddress address = new(ticket, lease.SegmentNumber);
```

读路径按地址中的 `SegmentNumber` 借出 reader lease；若目标是 active segment，store 返回同一个 active `IRbfFile` 实例的顺序化 reader lease，若目标是 closed segment，则通过 historical reader pool 返回 read-only lease：

```csharp
using var lease = store.OpenReader(address.SegmentNumber);
using RbfPooledFrame frame = lease.File.ReadPooledFrame(address.Ticket).Value;
```

lease 的存在是 API 契约的一部分。调用方不得在 lease dispose 后继续使用其中的 `IRbfFile`；pool eviction 必须尊重活跃 lease，不能关闭仍被借出的 historical reader。

MVP 固定为单写串读模型：同一个 `RbfSegmentStore` 实例不承诺线程安全，不使用跨线程锁协调并发访问。每个打开的 segment 在同一个 store 实例内最多对应一个 live `IRbfFile` 对象：active segment 使用一个 read/write `IRbfFile` 单例，historical segment 使用 pool 中的 read-only `IRbfFile` 单例。active writer lease 与 active reader lease 对同一个 active `IRbfFile` 顺序访问；调用方不得并发使用同一 store 或同一 lease 中的 `IRbfFile`。

## 6. Active Writer 与轮转

`OpenActiveWriter()` 内部负责最简单的 size-based rotation：

1. 检查当前 active segment 文件大小。
2. 若大小已达到或超过 `SegmentSizeThresholdBytes`，关闭并 sealed 当前 active segment。
3. 创建下一个单调递增的 `SegmentNumber` 作为新的 active segment。
4. 返回新 active segment 的 writer lease。
5. 若未达到阈值，返回当前 active segment 的 writer lease。

MVP 不使用时间阈值。低吞吐 Agent 让同一个 active segment 使用多年是可接受状态。

轮转判断发生在写入前，因此单个 frame 可能使 segment 最终大小超过阈值；最大超出量约为一个 RBF frame 的最大大小。该简化被接受，以换取 `RbfSegmentStore` 不需要预估下一 frame 大小，也不需要理解上层写入策略。RBF 仍保证单个 frame 不跨 segment。

推荐首轮 `SegmentSizeThresholdBytes` 仍可从 64 GiB 起步，但这是 options 默认值，不影响路径和地址格式。

## 7. Reader Lease 与 Historical Pool

`OpenReader(segmentNumber)` 必须同时支持 active segment 与 closed historical segment。historical segment 以 read-only 方式按需打开并进入 pool；active segment reader 不进入 historical pool，而是复用 active writer 所持有的同一个 read/write `IRbfFile` 单例。pool key 是 `SegmentNumber`，不是路径。

MVP 行为：

- active writer 独占 active segment 的写入权；同一 store 实例内 active segment 的读写都是顺序化访问。
- historical reader 使用 bounded LRU 或等价淘汰策略。
- lease 存活期间，对应 `IRbfFile` 不得被 pool 关闭。
- 读取出现不可恢复的句柄错误时，从 pool 中移除该 reader；下次读取重新打开，以区分句柄故障与持久化损坏。
- 跨进程 writer 和共享写句柄不进入 MVP。

## 8. Tail Recovery 编排

RBF 层提供 `RbfRecoveryScanner` 负责单文件"寻找最后一个完整有效 frame / recoverable tail offset"，并提供 `RbfRecovery.TruncateToSuggestedTail` 执行截断。`RbfSegmentStore` 只编排 active segment 的恢复：

1. 打开 store 并完成目录扫描（§4）。
2. 若 active segment 只有 RBF HeaderFence（header-only 空文件），它是合法空 active segment，不需要截断。
3. 否则通过 `RbfRecovery.OpenReadOnly(path).ScanBackward()` 获取首个 `RbfRecoveryHit`（使用默认 `FrameBoundary` 校验等级和 `Fence` 搜索策略）。若未找到任何有效 frame，则 active segment 视为不可恢复，报告 corruption。
4. 通过 `RbfRecovery.TruncateToSuggestedTail(path, hit)` 将 active segment 截断到 hit 的 `SuggestedTruncateOffset`（即最后一个完整 frame 的尾部 Fence 之后）。
5. closed historical segment 遇到损坏时报告 corruption，不自动截断。

因为 `RbfSegmentStore` 不解释 frame tag，它不能判断 Event 是否可见，也不处理 orphan payload / orphan event。这些属于 EventJournal。

## 9. 作为独立 Project

该层可以作为独立 C# project 施工，例如 `src/RbfSegmentStore` / `Atelia.RbfSegmentStore`。它依赖 RBF 与基础 primitives，但不依赖 EventJournal、ChatSession 或 StateJournal。

首轮测试目标：

- `SegmentNumber` 到路径的 bit-split 映射，覆盖至少两个 bucket。
- `CreateNew` / `OpenExisting` / `OpenOrCreate` 对缺失、空 store、已有 store 的语义区分。
- `RbfSegmentStoreOptions` 默认值与非法值校验，尤其是小 `SegmentSizeThresholdBytes` 触发测试轮转。
- `CreateNew` 与 `OpenOrCreate` 创建空 store 后产生 segment `1`；`OpenExisting` 拒绝空 store。
- 目录扫描拒绝错误 bucket、错误文件名、segment `0` 和编号缺口。
- `OpenActiveWriter()` 达到阈值后创建新 segment，旧 segment 不再写入。
- `OpenReader(activeSegmentNumber)` 可返回与 active writer 协调的 reader lease。
- `OpenReader(closedSegmentNumber)` 通过 pool 返回 read-only historical reader lease。
- 活跃 historical reader lease 阻止 pool eviction 关闭底层 RBF 文件。
- active segment 尾部撕裂后可通过 `RbfRecoveryScanner.ScanBackward()` + `RbfRecovery.TruncateToSuggestedTail()` 截断到最后一个完整 frame 的尾部 Fence 之后。
