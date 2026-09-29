# EventJournal 使用指南

`Atelia.EventJournal` 是建立在 `RbfSegmentStore` / `Rbf` 之上的 append-only 事件日志基础设施。它把每个事件保存为不可变 `EventFrame`，并通过 `EventFrameHeader.Parent` 形成一条可验证的 parent chain；在此之上，当前实现已经包含 branch/ref、reflog、反向/正向遍历，以及用于高效正序 replay 的 ForwardPlan 派生缓存。

本文描述 main 的 **v2 未发布 breaking 候选**，不是 nuget.org 已发布 `Atelia.EventJournal 0.1.2-preview.1` 的指南。已发布包的旧格式行为以根 README 的固定版本链接为准；v2 不直接打开旧目录，没有自动迁移。

当前入口为 [有界日常 I/O 方案](../../docs/EventJournal/bounded-online-io-design.md)、[冻结合同](../../docs/EventJournal/bounded-online-io-contracts.md)和[候选交付记录](../../docs/EventJournal/bounded-online-io-delivery.md)。

## 文件布局

典型 journal 根目录如下：

```text
<journal>/
  journal.format
  events/
    active.segment
    buckets/
      000000/
        00000001.rbf
        ...
  refs/
    ref-op-log.rbf
    catalog.snapshot
    objects/
      <ref-id-hex>/
        active.segment
        segments/
          00000001.rbf
          ...
```

- `events/`：真正的 EventFrame 存储，默认使用 bucketed `RbfSegmentStore`。
- `refs/ref-op-log.rbf`：branch name 与稳定 `RefId` 的绑定、fork、archive 历史，以及不可变 tag 绑定。
- `refs/objects/<ref-id>/segments/*.rbf`：单个 ref 的 move chain / reflog，默认使用 flat segment store。
- `journal.format`：v2 格式门，创建时最后发布。
- 每个 store 的 `active.segment`：layout 与 active segment 的定位信息；打开只探测必要路径，不枚举全部 segment。
- `refs/catalog.snapshot`：精确 op-log boundary/anchor、active names 和所有 tags；不保存 heads 或 archived 全集。打开复用已解码 snapshot，直接扫描有限 suffix。

ForwardPlan 仅驻留内存；v2 不创建、读取、更新或删除旧 compiled cache 文件。

日常只读查询使用：

```csharp
using var journal = EventJournal.OpenReadOnlyExisting(path);
RefId main = journal.OpenBranch("main").Unwrap();
EventAddress? head = journal.GetHead(main);
```

`OpenExisting`、`OpenOrCreate` 和 `OpenReadOnlyExisting` 都严格验证必要 metadata/末帧，不 recovery/truncate。只读入口拒绝 mutation，适用于只读挂载。日常打开不会 eager 加载所有 refs，也不验证所有 tag targets；首次访问 ref 才 checked-read allocation、Init、末 move 和当前 target，`ResolveTag` 只 checked-read 所选 target。无关历史损坏可以不影响日常读取，打开成功不是全库健康证明。

factory 使用 `StorageOpenException` 的 `Kind`、`ReasonCode`、`StoragePath`、`Offset`、`ObservedVersion` 报告 `FormatUnsupported` 与 `MaintenanceRequired`；缺 v2 marker、未知格式和损坏 metadata/必要坏尾不会 fallback 到旧格式或更早 head。首次 ref 访问的 Result 保留 `EventJournal.FormatUnsupported` / `EventJournal.MaintenanceRequired` 及 Details；权限/设备 I/O 保留标准异常。`GetHead` 继续使用既有便利抛错语义。

完整历史校验使用独立 [离线 toolkit](../../tools/EventJournal.Toolkit/README.md)，输入必须停写或为稳定副本。它扫描全部事实，提供 `audit` 与健康事实的索引候选重建；没有 repair-tail、apply 或旧格式迁移。

## Catalog 预算与发布

令 snapshot 的 live 数为 `L0`、durable suffix 条数为 `d`，`Q=max(1024,L0)`。reader 最多回放 Q 条，达到 Q 尚未到物理 EOF 即 `SuffixBudgetExceeded`，不会读取下一条 payload。anchor/control frame 先检查 248B 长度上限，suffix 含 Fence 最多 252B；坏 snapshot、boundary 或未知控制记录不会触发全量回放。

Create/Fork 预留 `r=2`，Archive/tag 预留 `r=1`。完成输入/CAS/overflow、已知 buffer 与索引容量、整个操作空间预检后，若 `d+r>Q` 或 `L0>2*max(1024,Lnext)`，在本次业务首写前 checkpoint 操作前已发布状态：op-log durable → 精确 boundary → 流式 temp → Flush(true)/close → 同目录原子替换 → 安装计数。Archive 的 Close、Create 的 allocation 都在 checkpoint 之后；业务已 Confirmed 后没有额外强制 checkpoint。

checkpoint 流式编码而不保存整文件副本，但现 codec 按 ordinal 排序并构造数组，实际 CPU 为 O(L log L)、辅助空间 O(L)；checkpoint 当次操作有明确延迟尖峰。大量 live tags/branches 仍需 O(L) 常驻 catalog，预算不等于整个进程 heap 上限。事实 op-log 不因 checkpoint 删除。

## 基本 EventFrame API

```csharp
using Atelia.EventJournal;

using var journal = EventJournal.CreateNew(path);

EventAddress root = journal.AppendEventFrame(
    parent: null,
    payload: "root"u8,
    opaqueEventKind: 1,
    hint: new AddressHint(0x1000)
).Unwrap();

EventAddress child = journal.AppendEventFrame(root, "child"u8).Unwrap();

using EventFrame frame = journal.ReadEvent(child).Unwrap();
ReadOnlySpan<byte> payload = frame.Payload;
EventFrameHeader header = frame.Header;
```

核心类型：

- `EventAddress(SizedPtr Ticket, uint SegmentNumber, AddressHint Hint)`：定位一个 EventFrame。`Hint` 会写入 header 并在读取时校验，用于降低误读概率。
- `EventFrameHeader`：固定 64 字节 TailMeta，当前为 v2，包含 `PayloadCodecId`、`SequenceNumber`、UTC 时间戳、`OpaqueEventKind`、logical `PayloadLength` 和 nullable `Parent`。
- `EventFrame`：checked read 后返回的 disposable frame；`Payload` 始终是 logical payload bytes，span 生命周期绑定在 `EventFrame` 上。

读取分两档：

- `ReadEventHeaderPreview(address)`：只读 RBF TailMeta，适合 parent walk、ForwardPlan 构建、轻量预筛选。
- `ReadEventHeaderChecked(address)`：完整读取 RBF frame 并校验 stored bytes CRC，但不解码 payload codec，适合接受外部地址和推进 refs 前验证。
- `ReadEvent(address)`：完整读取 RBF frame、校验 stored bytes CRC，并按 `PayloadCodecId` 透明解码出 logical payload。

`AppendEventFrame(parent, payload, ...)` 会在 parent 非空时 checked-read parent，确保不会向不存在或损坏的 parent 追加。

`AppendEventFrame` 接受的是 logical payload bytes。若启用 `EventJournalOptions.PayloadCodecPolicy`，EventJournal 会在写入 RBF 前选择 identity、brotli 或 zlib stored payload；读取时普通调用方仍只看到 logical payload。

## Traversal

```csharp
IReadOnlyList<EventAddress> reverse =
    journal.ReadAncestorChain(head).Unwrap();        // head -> parent -> ... -> root

IReadOnlyList<EventAddress> chronological =
    journal.ReadChronologicalChain(head).Unwrap();   // root -> ... -> head
```

公共参数：

- `checkedRead`：遍历时是否完整校验每个 frame。默认 `false`，即 preview header。
- `maxDepth`：可选深度上限；必须为正数。
- `detectCycles`：默认 `true`，反向 parent walk 时检测重复地址。
- `cancellationToken`：长链遍历可取消。

`ReadAncestorChain(head)` 直接沿 authoritative `Parent` 逆序读取。`ReadChronologicalChain(head)` 会先构建 ForwardPlan，再按 plan 正序 replay。

## Branch / Ref

Refs 使用两层结构：

1. `ref-op-log.rbf` 负责 branch name → `RefId` 的绑定历史。
2. 每个 ref object 保存自己的 `RefMoveFrame` 序列，也就是 reflog。

基本用法：

```csharp
using var journal = EventJournal.OpenOrCreate(path);

RefId main = journal.CreateBranch("main", startPoint: null).Unwrap();

CommitToRefOutcome first = journal.CommitToRef(
    refId: main,
    expectedHead: null,
    payload: "first"u8
).Unwrap();

CommitToRefOutcome second = journal.CommitToRef(
    refId: main,
    expectedHead: first.EventAddress,
    payload: "second"u8
).Unwrap();

EventAddress head = journal.GetHead(main)
    ?? throw new InvalidDataException("main has no head");
IReadOnlyList<RefMoveFrame> reflog = journal.ReadReflog(main).Unwrap();
IReadOnlyList<EventAddress> replay = journal.ReadChronologicalChain(head).Unwrap();
```

主要 ref API：

- `OpenBranch(name)`：从当前 active branch table 取 `RefId`。
- `ListBranches()`：返回 active branch names，按 ordinal 排序。
- `CreateBranch(name, startPoint)`：创建新的 named ref；同名 active branch 不允许重复。
- `ForkBranch(name, sourceRefId, sourceHead)`：从已有 ref 的指定 head fork 一个新 named ref。
- `GetHead(refId)`：读取当前 head；closed ref 会抛 `InvalidOperationException`。
- `AdvanceRef(refId, expectedOldHead, newHead)`：fast-forward 风格推进，要求 `newHead.Parent == expectedOldHead`。
- `MoveRef(refId, expectedOldHead, newHead)`：reset / rewind / retarget；允许 `newHead == null`。
- `ArchiveRef(refId, expectedOldHead)`：关闭 ref 并移除 active branch name 绑定。
- `ReadReflog(refId)`：读取 ref object 中的 move chain。
- `CommitToRef(refId, expectedHead, payload, ...)`：对 lifetime-bound exact ref append event 后调用
  `AdvanceRef`；default/missing/closed ref 在 event append 前失败，CAS 失败时会保留刚 append 的
  orphan event。
- `CommitToRef(branchName, ...)`：便利 overload，只执行一次 `OpenBranch(name)` 后委托给 RefId
  overload。长期运行的上层对象应在自身 open 时保存 `RefId`，不要在每次 commit 时重新解析名字。

`AdvanceRef` / `MoveRef` 都是 CAS 风格：当前 head 必须等于 `expectedOldHead`，否则不会写入 move。

## 不可变 tag

```csharp
journal.CreateTag("before-experiment", head).Unwrap();
// 关闭后，使用 OpenExisting 或 OpenReadOnlyExisting 严格重开。
EventAddress selected = reopened.ResolveTag("before-experiment").Unwrap();
```

`CreateTag(name, target)` 返回 `AteliaResult<bool>`，成功为 true；`ResolveTag(name)` 返回
`AteliaResult<EventAddress>`。tag 与 branch 使用独立 ordinal 名称空间，可以同名，名称字符/长度约束相同。
同名 tag 一律拒绝（`TagAlreadyExists`），即使目标相同；不存在返回 `TagNotFound`，非法名称为
`TagNameInvalid`，坏目标为 `TagTargetInvalid`。tag 没有 move、delete、archive 或 checkout。

创建和解析均 checked-read 目标帧；重开校验 snapshot/有限 suffix 中的持久绑定，目标按需校验。所选目标坏时解析失败，无关 tag 目标坏不阻止日常打开。
branch 后续移动、归档不会改变 tag。只读打开允许解析、拒绝创建；不会物化任何领域模型。
`EventAddress` 只是本地物理坐标，不能识别另一个仓库碰巧相同的坐标；上层负责地址来源与生命周期。

普通校验失败不会追加，driver 仍可使用。创建先准备记录和索引容量，确认目标 segment 耐久，再向
ref-op-log 追加一条 tag frame，日志 `DurableFlush` 成功后安装内存绑定。目标确认或后续发布异常抛
`TagPublicationException`，其 `Outcome` 区分 `NotAttempted`、`Unknown` 与 `Confirmed`；此时整个
journal 拒绝后续数据操作，只能 Dispose。严格重开并 Resolve 查看实际结果，不能盲目重试 Create。
确认目标只覆盖目标所在文件；payload 引用的外部数据和 parent 依赖由调用方先确认。

查询不确定发布结果时，严格重开并解析对应 tag。所有打开入口均不自动修尾；前置 checkpoint 异常时 tag 为 `NotAttempted`，Append 开始后为 `Unknown`，业务 flush 返回后为 `Confirmed`，随后内存安装异常不降类。坏尾要求离线维护，不能盲目重试可能已经发布的 tag。

v2 整体布局与已发布旧格式不兼容；不可变 tag API 与坐标语义保持。当前故障合同见[冻结附件](../../docs/EventJournal/bounded-online-io-contracts.md#4-生命周期与操作前置协议)，旧 tag 发布设计作为历史记录保留。

## ForwardPlan：正序 replay 的派生计划

EventJournal 的事实源只有 `EventFrameHeader.Parent`。ForwardPlan 是为了高效正序遍历而“编译”出来的派生 artifact，可以随时删除并重建。

ForwardPlan 的核心模型是：

- `RootEvent`
- `TargetHead`
- `EventCount`
- sparse `Redirects`

正序 replay 时，默认沿物理地址读取下一个 EventFrame；只有遇到非物理连续边、跨 segment 边、或需要跳过 orphan/sibling 时，才依赖 `Redirects` 指向真正的 child。

当前实现仅保留一份 process-local ForwardPlan LRU：exact head 命中直接复用；cold build 遇到缓存 ancestor 时复用 prefix。预算为 4096 项 / 16 MiB 估算值。没有 compiled disk cache、ref-local plan binding 或双 tail merge；旧相关设计仅为历史记录。

进程重启后的 cold request 需要沿 authoritative Parent walk 构建计划；完整 N 个事件的请求仍需要 O(N) 读取及返回列表，redirects、cycle detection 和结果可能使用 O(N) 临时空间。缓存预算只限制保留的计划，不限制一次全量请求的真实内存。`ReadChronologicalChain(...)` 返回 `IReadOnlyList<EventAddress>`，尚无 streaming API。

[EphemeralForwardPlan 原始设计](../../docs/EventJournal/ephemeral-forward-plan-design.md)保留模型背景；[v2 方案](../../docs/EventJournal/bounded-online-io-design.md)规定当前缓存范围。

## Options

`EventJournalOptions` 会规范化底层 store layout：

- `MaxLogicalPayloadLength`：单 Event logical payload 上限，默认是 RBF 单帧 payload+TailMeta 上限减去 64 字节 EventFrame header。
- `PayloadCodecPolicy`：EventFrame payload 写入策略，默认 identity；可设为 `EventPayloadCodecPolicy.Zlib` 或 `EventPayloadCodecPolicy.Brotli` 启用保守的压缩自动选择。
- `EventSegmentStoreOptions`：强制使用 bucketed layout，默认 segment threshold 为 `64 GiB`。
- `RefSegmentStoreOptions`：强制使用 flat layout，默认 segment threshold 为 `64 MiB`。
- `RefOpLogOptions`：仅配置 ref-op-log 的 RBF cache mode。
- `RefStoreCacheCapacity`：默认 32，必须 ≥0；一个 LRU entry 同时拥有 ref state 与 store，eviction/Archive/Dispose 释放句柄；0 表示每次操作结束不保留 entry。entry 不持有跨调用 writer lease。active-only RefId→name 反查与 catalog 同步，不保留历史 knownRefs。

所有层的 `RecoverActiveTailOnOpen` 已移除。

示例：

```csharp
var options = new EventJournalOptions {
    PayloadCodecPolicy = EventPayloadCodecPolicy.Zlib,
    EventSegmentStoreOptions = new RbfSegmentStoreOptions {
        SegmentSizeThresholdBytes = 8 * 1024 * 1024
    },
    RefSegmentStoreOptions = new RbfSegmentStoreOptions {
        SegmentSizeThresholdBytes = 1024 * 1024
    }
};

using var journal = EventJournal.OpenOrCreate(path, options);
```

## 代码地图

- `EventJournal.cs`：journal 生命周期、event append/read、ancestor traversal。
- `EventJournal.Refs.cs`：branch/ref API、有界 catalog suffix、ref object 局部 state loading。
- `EventJournal.Tags.cs` / `TagBindingFrame.cs` / `TagPublicationException.cs`：不可变 tag、独立记录格式与发布故障证据。
- `EventJournal.ForwardPlan.cs`：ForwardPlan 构建、replay、单一 memory LRU 与 ancestor prefix reuse。
- `EventFrameHeader.cs` / `EventAddresses.cs`：核心固定宽度 codec。
- `RefMoveFrame.cs` / `RefOpFrame.cs`：ref/reflog 固定格式 codec。
- `RefMoveStore.cs` / `EventJournal.RefCache.cs`：单个 ref object 首末读取、append 与有界 entry 生命周期。
- `JournalFormat.cs` / `CatalogSnapshot.cs`：v2 格式门、共享 snapshot codec 与原子发布。
- `EventJournalOptions.cs` / `RefOpLogOptions.cs`：配置入口。

## 当前边界与注意事项

T00–T06 源码实现已通过当前 Linux 验证；性能/进程中断、Windows 和隔离包消费的交付状态见[候选交付记录](../../docs/EventJournal/bounded-online-io-delivery.md)。源码实现不等于已发布包、消费者升级或真实实例迁移。

- EventFrame append-only；没有 event deletion / compaction / repack 语义。
- 所有日常打开均严格、不修尾；全库审计显式使用离线 toolkit。
- ForwardPlan 是可丢弃缓存，不应作为持久事实源。
- `ReadChronologicalChain(...)` 当前返回 `IReadOnlyList<EventAddress>`，超长历史未来需要 streaming API。
- `CommitToRef` 的 append 与 ref advance 不是事务：CAS 失败会留下 orphan event，这是当前设计可接受的派生产物。
- `RefId` 来源于 ref-op-log 中 Create/Fork frame 的 RBF ticket packed value；不要手写 default `RefId(0)`。
- ref state 与 active branch table 在一个 `EventJournal` instance 内使用 process-local cache；当前是
  single-driver、非线程安全模型。不要在该 instance 生命周期内用另一个 `EventJournal` instance
  live move/archive 同一 ref，并期待前者自动 refresh 或提供跨 instance CAS。需要外部 move/archive
  时，先 dispose owning driver，完成 ref 操作后再 reopen。
