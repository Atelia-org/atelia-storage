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
- `refs/ref-op-log.rbf`：branch name 与稳定 `RefId` 的绑定、fork、archive 历史。
- `refs/objects/<ref-id>/segments/*.rbf`：单个 ref 的 move chain / reflog，默认使用 flat segment store。
- `cache/forward-plans/v1/*.efplan`：ForwardPlan compiled cache，是可删除、可重建的派生产物，不是 correctness source。

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
    branchName: "main",
    expectedHead: null,
    payload: "first"u8
).Unwrap();

CommitToRefOutcome second = journal.CommitToRef(
    branchName: "main",
    expectedHead: first.EventAddress,
    payload: "second"u8
).Unwrap();

EventAddress? head = journal.GetHead(main);
IReadOnlyList<RefMoveFrame> reflog = journal.ReadReflog(main).Unwrap();
IReadOnlyList<EventAddress> replay = journal.ReadChronologicalChain(main).Unwrap();
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
- `CommitToRef(branchName, expectedHead, payload, ...)`：append event 后调用 `AdvanceRef`，CAS 失败时会保留刚 append 的 orphan event。

`AdvanceRef` / `MoveRef` 都是 CAS 风格：当前 head 必须等于 `expectedOldHead`，否则不会写入 move。

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
- `EventJournal.ForwardPlan.cs`：ForwardPlan 构建、replay、memory cache、compiled disk cache、tail-merge 增量编译。
- `EventFrameHeader.cs` / `EventAddresses.cs`：核心固定宽度 codec。
- `RefMoveFrame.cs` / `RefOpFrame.cs`：ref/reflog 固定格式 codec。
- `RefMoveStore.cs`：单个 ref object 的 append/read。
- `EventJournalOptions.cs` / `RefOpLogOptions.cs`：配置入口。

## 当前边界与注意事项

- EventFrame append-only；没有 event deletion / compaction / repack 语义。
- ForwardPlan 是 disposable cache，不应作为持久事实源。
- `ReadChronologicalChain(...)` 当前返回 `IReadOnlyList<EventAddress>`，超长历史未来需要 streaming API。
- `CommitToRef` 的 append 与 ref advance 不是事务：CAS 失败会留下 orphan event，这是当前设计可接受的派生产物。
- `RefId` 来源于 ref-op-log 中 Create/Fork frame 的 RBF ticket packed value；不要手写 default `RefId(0)`。
