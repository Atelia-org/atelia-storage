# EventJournal 使用指南

`Atelia.EventJournal` 是建立在 `RbfSegmentStore` / `Rbf` 之上的 append-only 事件日志基础设施。它把每个事件保存为不可变 `EventFrame`，并通过 `EventFrameHeader.Parent` 形成一条可验证的 parent chain；在此之上，当前实现已经包含 branch/ref、reflog、反向/正向遍历，以及用于高效正序 replay 的 ForwardPlan 派生缓存。

这份 README 面向后续 Coding Agent：先看这里获得代码地图，再进入 `docs/EventJournal/` 的设计文档细读背景。

## 文件布局

典型 journal 根目录如下：

```text
<journal>/
  events/
    buckets/
      000000/
        00000001.rbf
        ...
  refs/
    ref-op-log.rbf
    objects/
      <ref-id-hex>/
        segments/
          00000001.rbf
          ...
  cache/
    forward-plans/
      v1/
        s00000001-t0000000000000004-h00000000.efplan
```

- `events/`：真正的 EventFrame 存储，默认使用 bucketed `RbfSegmentStore`。
- `refs/ref-op-log.rbf`：branch name 与稳定 `RefId` 的绑定、fork、archive 历史，以及不可变 tag 绑定。
- `refs/objects/<ref-id>/segments/*.rbf`：单个 ref 的 move chain / reflog，默认使用 flat segment store。
- `cache/forward-plans/v1/*.efplan`：ForwardPlan compiled cache，是可删除、可重建的派生产物，不是 correctness source。

离线审计应使用真正的只读入口：

```csharp
using var journal = EventJournal.OpenReadOnlyExisting(path);
RefId main = journal.OpenBranch("main").Unwrap();
EventAddress? head = journal.GetHead(main);
```

它以共享只读方式打开 active event segment、ref-op-log 和 live ref object，验证但不 recovery/truncate
任何 active tail，也不创建、读取后回写或删除 compiled ForwardPlan cache。所有 mutation API 都
fail-fast，因此可在只读挂载上运行；坏尾只会报告 corruption。

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
// 关闭后，使用严格可写选项或 OpenReadOnlyExisting 重开。
EventAddress selected = reopened.ResolveTag("before-experiment").Unwrap();
```

`CreateTag(name, target)` 返回 `AteliaResult<bool>`，成功为 true；`ResolveTag(name)` 返回
`AteliaResult<EventAddress>`。tag 与 branch 使用独立 ordinal 名称空间，可以同名，名称字符/长度约束相同。
同名 tag 一律拒绝（`TagAlreadyExists`），即使目标相同；不存在返回 `TagNotFound`，非法名称为
`TagNameInvalid`，坏目标为 `TagTargetInvalid`。tag 没有 move、delete、archive 或 checkout。

创建和解析均 checked-read 目标帧；所有重开入口校验持久绑定及其目标，损坏记录或目标使打开失败。
branch 后续移动、归档不会改变 tag。只读打开允许解析、拒绝创建；不会物化任何领域模型。
`EventAddress` 只是本地物理坐标，不能识别另一个仓库碰巧相同的坐标；上层负责地址来源与生命周期。

普通校验失败不会追加，driver 仍可使用。创建先准备记录和索引容量，确认目标 segment 耐久，再向
ref-op-log 追加一条 tag frame，日志 `DurableFlush` 成功后安装内存绑定。目标确认或后续发布异常抛
`TagPublicationException`，其 `Outcome` 区分 `NotAttempted`、`Unknown` 与 `Confirmed`；此时整个
journal 拒绝后续数据操作，只能 Dispose。严格重开并 Resolve 查看实际结果，不能盲目重试 Create。
确认目标只覆盖目标所在文件；payload 引用的外部数据和 parent 依赖由调用方先确认。

查询不确定发布结果时，使用 `OpenReadOnlyExisting`，或将三个 options 的
`RecoverActiveTailOnOpen` 全部设为 false；默认可写打开仍保留既有自动修尾行为。不得为查询结果而默认修尾。

新 reader 可读旧格式；首次写入 tag 后，旧版本 reader 会因未知 frame tag 明确拒绝打开。
本功能不增加文件/目录或额外断电保证。完整格式、故障结果和下游依赖顺序见
[不可变 tag 设计](../../docs/EventJournal/immutable-tags-design.md)。

## ForwardPlan：正序 replay 的派生计划

EventJournal 的事实源只有 `EventFrameHeader.Parent`。ForwardPlan 是为了高效正序遍历而“编译”出来的派生 artifact，可以随时删除并重建。

ForwardPlan 的核心模型是：

- `RootEvent`
- `TargetHead`
- `EventCount`
- sparse `Redirects`

正序 replay 时，默认沿物理地址读取下一个 EventFrame；只有遇到非物理连续边、跨 segment 边、或需要跳过 orphan/sibling 时，才依赖 `Redirects` 指向真正的 child。

当前实现包含三层优化：

1. **EphemeralForwardPlan 构建**：从 `head` 沿 Parent 逆走，收集 sparse redirects，然后反向组成正序 plan。
2. **Process-local exact-head cache + ancestor prefix reuse**：同一进程内 exact head 命中直接复用；cold build 逆走时如果遇到已缓存 ancestor plan，会复用该 prefix。
3. **Compiled disk cache + ref-local tail merge**：
   - exact-head plan 会保存到 `cache/forward-plans/v1/*.efplan`，下次打开 journal 后可直接加载。
   - `ReadChronologicalChain(RefId)` 会维护 process-local `RefId -> ForwardPlan` binding。
   - 当 ref head 改变且 exact cache miss 时，会尝试“双 tail 游标”增量编译：`oldPlan.TargetHead` 与 `newHead` 同时沿 Parent 逆走，每次推进物理坐标更晚的一侧，直到两个 cursor 的 `EventAddress` 真实相等；这个相遇点才是可复用旧 prefix 与新 suffix 的连接点。
   - tail merge 成功后会生成完整 new plan，并写入 memory cache 和 compiled disk cache；失败则回落全量构建。

缓存失效策略保持粗粒度：

- compiled cache 文件损坏、格式不匹配、head 不匹配、replay 失败时删除并重建；
- ref binding 只作为读取优化，不参与 correctness；
- 不做多 ref 共享 prefix DAG，也不在 ref 写路径同步维护 plan。

相关设计文档：

- `docs/EventJournal/ephemeral-forward-plan-design.md`
- `docs/EventJournal/forward-plan-compiled-cache-design.md`
- `docs/EventJournal/forward-plan-tail-merge-incremental-design.md`

## Options

`EventJournalOptions` 会规范化底层 store layout：

- `MaxLogicalPayloadLength`：单 Event logical payload 上限，默认是 RBF 单帧 payload+TailMeta 上限减去 64 字节 EventFrame header。
- `PayloadCodecPolicy`：EventFrame payload 写入策略，默认 identity；可设为 `EventPayloadCodecPolicy.Zlib` 或 `EventPayloadCodecPolicy.Brotli` 启用保守的压缩自动选择。
- `EventSegmentStoreOptions`：强制使用 bucketed layout，默认 segment threshold 为 `64 GiB`。
- `RefSegmentStoreOptions`：强制使用 flat layout，默认 segment threshold 为 `64 MiB`。
- `RefOpLogOptions`：配置 ref-op-log 的 RBF cache mode 与打开时 tail recovery。

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
- `EventJournal.Refs.cs`：branch/ref API、ref-op-log replay、ref object state loading。
- `EventJournal.Tags.cs` / `TagBindingFrame.cs` / `TagPublicationException.cs`：不可变 tag、独立记录格式与发布故障证据。
- `EventJournal.ForwardPlan.cs`：ForwardPlan 构建、replay、memory cache、compiled disk cache、tail-merge 增量编译。
- `EventFrameHeader.cs` / `EventAddresses.cs`：核心固定宽度 codec。
- `RefMoveFrame.cs` / `RefOpFrame.cs`：ref/reflog 固定格式 codec。
- `RefMoveStore.cs`：单个 ref object 的 append/read。
- `EventJournalOptions.cs` / `RefOpLogOptions.cs`：配置入口。

## 当前边界与注意事项

长期运行性能的下一版设计建议见 [有界日常 I/O 重构方案](../../docs/EventJournal/bounded-online-io-design.md)及[辩证简化审查记录](../../docs/EventJournal/bounded-online-io-review.md)。该方案尚未实施；下列条目和本文 API 说明仍描述当前版本。
后续施工使用 [task级工单](../../docs/EventJournal/bounded-online-io-work-order.md)与[合同冻结附件](../../docs/EventJournal/bounded-online-io-contracts.md)；[源码阶段Goal](../../docs/EventJournal/GOAL-bounded-online-io.md)仅为待启动提示词。

- EventFrame append-only；没有 event deletion / compaction / repack 语义。
- `OpenExisting` / `OpenOrCreate` 是可恢复的 read-write 入口；严格审计必须使用
  `OpenReadOnlyExisting`，避免把 recovery mutation 混入 validation。
- ForwardPlan 是 disposable cache，不应作为持久事实源。
- `ReadChronologicalChain(...)` 当前返回 `IReadOnlyList<EventAddress>`，超长历史未来需要 streaming API。
- `CommitToRef` 的 append 与 ref advance 不是事务：CAS 失败会留下 orphan event，这是当前设计可接受的派生产物。
- `RefId` 来源于 ref-op-log 中 Create/Fork frame 的 RBF ticket packed value；不要手写 default `RefId(0)`。
- ref state 与 active branch table 在一个 `EventJournal` instance 内使用 process-local cache；当前是
  single-driver、非线程安全模型。不要在该 instance 生命周期内用另一个 `EventJournal` instance
  live move/archive 同一 ref，并期待前者自动 refresh 或提供跨 instance CAS。需要外部 move/archive
  时，先 dispose owning driver，完成 ref 操作后再 reopen。
