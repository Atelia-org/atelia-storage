# Forward Route Artifact 设计基线

> **状态**：Design Baseline / 待拆分为 Spec
> **日期**：2026-07-23
> **依赖**：[EventJournal 功能需求与粗粒度设计基线](event-journal-requirements-and-design.md)、[EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)、[Event Ref Store 设计基线](event-ref-store-design.md)、[RbfSegmentStore 设计基线](rbf-segment-store-design.md)
> **思路来源**：[forward-route design talk](design-talk/forward-route.md)

## 1. 文档定位

本文设计 EventJournal 层的第一种 derived artifact：`ForwardRoute`。它为一个已存在的 Event head 保存可重建的“路标”，包括真正的 root 起点和少量岔道指示，使读取方可以沿物理 Event 顺序正向遍历到该 head，而不必先把完整 Parent chain 的 $n$ 个地址压入内存。

本文固定：

- Event、ref 与 `ForwardRoute` 的权威边界。
- 默认物理后继和 redirect 的精确定义。
- immutable route node、head catalog 与 store generation。
- create、fork、advance、move、lazy build 和 repair 的维护算法。
- checked traversal、范围裁剪、降级、崩溃恢复与存储布局。

本文不设计：

- 通用 artifact registry 或业务层 `DerivedContextArtifact` schema。
- checkpoint、chunked vector、GC、repack 或跨 journal 共享。
- 多 writer、跨文件原子事务或 multi-parent Event。
- 依靠 route 取代 Parent chain、ref head 或 reflog。

## 2. 核心决策

`ForwardRoute` 是 **head/path-scoped、可校验、可重建、可整体丢弃的导航 artifact**。

它不是 branch-local 的可变 child map，也不是第二份 commit history：

- `EventFrame.Parent` 仍是 Event 可达关系和路径的唯一事实源。
- `RefMoveFrame.NewTarget` 仍是 ref 状态的权威 head。
- ref move 与 route publication 没有跨文件原子关系，也不需要有。
- route 缺失、过期或损坏时，读取方必须保留 captured head，并退化到 Parent 逆走加临时容器；不得回退 ref、改变历史或接受另一条路径。
- route store 缺失或无法打开时，EventJournal 的 events/refs 仍必须可用；route capability 单独报告 unavailable/failure。

单 Parent 下，给定 head 后从真正 root 到 head 的路径唯一。因此 catalog 直接以 `TargetHead` 发现 route；两个 ref、两个历史 ref move 或 fork 只要指向同一 head，就共享同一 artifact。artifact 后来被修复不会改写当年的 reflog，只会让同一 head 获得更好的当前导航方式。

一句话表示：

> 把全局物理 Event 顺序视为隐式 forward chain，只为选中 Parent path 偏离该顺序的边持久化 `Redirect(From, ToChild)`。

## 3. 起点与遍历范围

“某个分支的起点”有两种含义，MVP 必须显式区分：

- `RootEvent`：从 head 沿 Parent 走到的 `Parent == null` Event，是完整 commit history 的起点，属于 head/path。
- `BranchStart`：ref 的首个 `Init.NewTarget`，可以是任意既有 Event；unborn ref 为 `null`。它属于 ref 生命周期，不属于共享 route。

`ForwardRouteDescriptor` 保存 `RootEvent`，因此描述与现有 `ReadChronologicalChain(head)` 相同的完整历史。ref replay 将首个 `Init.NewTarget` 派生为 `BranchStart`，后续 `Advance`、`Move`、move-to-null 和 `Close` 都不改变它。

候选遍历语义：

```text
EnumerateRefForward(refId, FullHistory)             // RootEvent .. captured Head
EnumerateRefForward(refId, AfterExclusive(event))   // event 的后一 Event .. captured Head
EnumerateRefForward(refId, FromBranchStart)          // BranchStart .. captured Head
```

范围规则：

- `FullHistory` 从 `RootEvent` 开始并包含 root。
- `AfterExclusive(event)` 要求 `event` 是 captured head 的祖先；若它等于 head，结果为空。该 mode 不使用 null 充当 `FullHistory`，避免把 root、unborn 与“未指定”混在一起。
- `FromBranchStart` 在 `BranchStart != null` 时要求它是 captured head 的祖先，并从 `BranchStart` 开始、包含 `BranchStart`。
- unborn ref 的 `BranchStart == null` 永久成立；它之后首次 `Advance` 到新 root，或先 `Move` 到既有 lineage，都不反向改写 `BranchStart`。此时 `FromBranchStart` 明确定义为 `FullHistory`。
- 若后续 `Move` 把 ref 指向与 non-null `BranchStart` 无祖先关系的 lineage，`FromBranchStart` 返回 `BranchStartNotAncestor`；不得按物理地址猜范围。move-to-null 后再 `Advance` 也适用同一规则。
- captured `Head == null` 时三种 mode 都返回空序列：不存在可供验证的 current lineage。仍须拒绝 range 的非法结构组合，但不解引用 `AfterExclusive` boundary，也不检查祖先关系；`FromBranchStart` 也不报祖先错误。
- `Close` 后默认公共 capture API 返回 `RefClosed`；诊断 API 若显式读取 archived snapshot，应捕获 `Close.OldTarget` 作为最后 head，再应用相同范围规则，而不能把 `Close.NewTarget == null` 误作完整生命周期为空。
- create/fork 的 start point 不得偷换为 root。完整 route 仍从 root 开始，读取时只裁剪产出范围。

## 4. 默认物理后继

### 4.1 EventPhysicalOrder

Event 的物理坐标只由下列二元组定义：

```text
PhysicalCoordinate(EventAddress) = (SegmentNumber, Ticket.Offset)
```

按 `SegmentNumber`、再按 `Ticket.Offset` 作字典序比较。不得使用：

- `Ticket.Packed`：其中混入 frame length，并非纯 offset。
- `AddressHint`：它不是排序字段。
- `SequenceNumber + 1`：sequence 允许空洞，只用于诊断和排序辅助。

本文后续 EventAddress 之间的 `<`、`<=`、“早于”和“前进”均指这个 `PhysicalCoordinate` 顺序；完整身份相等仍比较整个 canonical `EventAddress`（包括 hint）。

在当前单 writer、segment number 单调增加、closed segment 不改写、Parent 必须先 checked-readable 的约束下，合法 Parent edge 总是严格向更大的物理坐标前进，因此 Event graph 天然无环。

### 4.2 NextPhysicalEvent

`NextPhysicalEvent(E)` 定义为全局物理顺序中严格位于 `E` 之后的第一个 **RBF frame boundary 可解析、非 tombstone、`FrameTag == EventFrameTag` 的物理 slot**：

- 跳过同一 RBF 文件中的非 Event frame。
- 当前 segment 结束后继续到下一连续 segment；跨 segment 本身不构成路径中断。
- 明确的 tombstone 可以跳过；扫描遇到 framing / boundary CRC 错误或不可判定的 slot 时必须返回 cursor error，不得继续寻找更后的“有效 Event”。否则损坏会悄悄改变 implicit edge 的含义。
- `NextPhysicalEvent` 先返回该 slot 的 `FrameAddress`；随后尝试从 Event TailMeta 构造 canonical `EventAddress`。Event header/schema/hint 无法 preview 校验时同样返回 cursor error，不得跳过到后一个 Event。
- 候选 Event slot 的 `AddressHint` 必须取自 canonical Event header；`ResolveRoute` / `Prepare` 至少完成 L2 header preview。payload CRC 损坏不让这个物理 slot“消失”，也不能使 cursor 改选更后的 Event；若候选在选中 path 上，完整 payload 读取会在 replay 阶段按 canonical 规则失败。
- 一旦某地址之后已经存在物理 Event，append-only store 不得再在它之前插入 Event；因而既有 `NextPhysicalEvent(E)` 在 store 生命周期内稳定。

MVP 的 `events/` 当前只写 `EventFrame`，但定义仍按 Event-only cursor 给出，以免未来增加内部 frame 后改变 route 语义。若未来 repack 或重排 Event 物理位置，必须整体重建到新的 route generation。

### 4.3 Implicit Edge 与 Redirect

对于选中 Parent path 上的边 `P -> C`：

```text
Implicit(P, C) := NextPhysicalEvent(P) == C
```

若为真，该边无需持久化；否则 route 必须包含：

```text
Redirect(P, C)
```

是否写路标由物理 Event 后继决定，而不是由操作名是否为 rewind/fork 决定。另一个 branch、orphan Event 或旧未来先占据物理后继，都可能使一次合法 `Advance` 需要 redirect。

写入快路径可以使用“append 前最后一个物理 Event 恰好等于 Parent”作为无需 redirect 的充分条件，但这不是持久语义，也不能代替 reader/rebuilder 对 `NextPhysicalEvent` 的判断。

## 5. 逻辑数据模型

### 5.1 Store Stamp 与 Address

route 地址只在某个 artifact store generation 内有效：

```csharp
public readonly record struct ForwardRouteStoreStamp(
    Guid StoreId,
    ulong Generation
);

public readonly record struct ForwardRouteAddress(
    ForwardRouteStoreStamp Stamp,
    FrameAddress FrameAddress
);
```

`FrameAddress` wire format 仍是 `SizedPtr Ticket + u32 SegmentNumber`。`StoreId` 为非零 opaque 128-bit id，`Generation` 从 1 开始。segment rollover 不换 generation；同 generation 内已发布地址永不重编号或复用。

stamp 解决 derived store 删除/重建后的地址别名：旧 descriptor 即使碰巧与新 store 的 segment/ticket 相同，也会因 stamp 不同而失效。manifest 丢失且无法证明原 identity 时必须生成新 `StoreId`，不得从 1 号 segment 原地猜测恢复旧地址空间。

active-tail truncate 可能让后续 append 复用被截掉的 RBF offset，因此还需 publication watermark：一旦 catalog durable 地引用某个 node，恢复就不得在同 stamp 下截断或复用该 node 地址。MVP 若不能证明 recovery 保留了所有已发布 node/catalog prefix，必须把整代标为 `Unavailable`，在新 stamp 下 repair；不得在旧 generation 中继续 append。后续可以用 sealed publication segment 或持久 high-water mark 优化，但不能削弱此不变量。

### 5.2 ForwardRouteDescriptor

```csharp
public readonly record struct ForwardRouteDescriptor(
    ForwardRouteStoreStamp Stamp,
    EventAddress TargetHead,
    EventAddress RootEvent,
    ForwardRouteAddress? Tail,
    ulong RedirectCount
);
```

规则：

- descriptor 必须与 catalog key 的 `TargetHead` 完全相等。
- `Tail == null` 当且仅当 `RedirectCount == 0`。
- `TargetHead` 与 `RootEvent` 均 checked-readable；`RootEvent.Parent == null`、`RootEvent <= TargetHead`，且 root 是 target 的祖先。
- non-null tail 的 stamp 必须相同；tail node 的 `Ordinal == RedirectCount`、`RootEvent` 相同。
- descriptor 单独不能证明路径完整；最终必须从 root 精确走到 `TargetHead`。

`TargetHead` 显式存在，使 descriptor 离开 ref 上下文后仍能自证作用域，也避免 cache 仅凭 `{Root,Tail,Count}` 猜测绑定。

### 5.3 ForwardRouteNode

每个 node 表示一个 redirect：

```csharp
public readonly record struct ForwardRouteNode(
    ForwardRouteStoreStamp Stamp,
    ForwardRouteAddress? Previous,
    ulong Ordinal,
    EventAddress RootEvent,
    EventAddress FromEvent,
    EventAddress ToChild
);
```

规则：

- `Ordinal >= 1`；`Previous == null` 当且仅当 `Ordinal == 1`。
- 非首 node 的 `Previous` 与当前 node stamp 相同，物理地址早于当前 node，且 `Previous.Ordinal == Ordinal - 1`。
- `RootEvent` 在整条 chain 中相同。
- `ToChild.Parent == FromEvent`，且 `FromEvent < ToChild`。
- 按 path 顺序 `FromEvent` 严格前进，不得重复或冲突。
- canonical builder 只为已证明的 non-implicit edge 写 node；reader MAY 接受冗余 redirect，但 canonical catalog publication 不接受它，以保持唯一的 sparse 表示。若 writer 无法廉价证明 edge 是否 implicit，可以暂不发布 route并依赖 fallback，或调用精确 builder；不能用“保守多写 redirect”冒充 canonical route。

node 重复保存 stamp 和 root 会增加空间，但 redirect 预期稀疏；它使 node 自描述，并能尽早发现跨 store、跨 generation 或跨路线误链。

### 5.4 ForwardRouteCatalogEntry

catalog 是 append-only 的 route discovery log：

```csharp
public readonly record struct ForwardRouteCatalogEntry(
    ForwardRouteStoreStamp Stamp,
    EventAddress TargetHead,
    ForwardRouteDescriptor Descriptor
);
```

逻辑 key 为 `{Stamp, TargetHead}`。entry 顶层重复这两个字段是为了无需先信任/解码整个 descriptor 就能 replay 和筛选候选；解码后重复值必须逐位相等。同 key 允许存在多条 entry，用于 repair、codec format 升级或等价 route；append 顺序定义新旧。lookup 从 newest supported candidate 开始校验，坏的新候选 MAY 继续尝试旧候选，全部失败则 fallback。

route semantics / policy 变化不是普通 codec upgrade：它必须切换 generation，并使旧 catalog entry 因 stamp 不同而不可见；不得在同 generation 中原地改变 `NextPhysicalEvent`、redirect 或验证语义。

catalog entry 只是 discovery hint：

- 它不证明任何 ref 指向该 head；ref CAS 失败后留下的 entry 也是合法 orphan。
- catalog 的 latest map 是 replay 派生 cache，不是事实源。
- 历史 reflog 仍只保存 head；读取历史 head 时可使用后来发布或修复的 route。
- `BranchStart` 不进入 catalog，因为同 head 的 refs 可以有不同生命周期起点。

## 6. 物理存储与 generation

```text
event-journal/
    events/...
    refs/...
    artifacts/
        forward-routes/
            manifest
            generations/
                <store-id>-<generation-id>/
                    nodes/
                        segments/...
                    catalog/
                        segments/...
```

nodes 与 catalog 分开使用 segment store：

- `nodes` 保存不可变 `ForwardRouteNodeFrame`。
- `catalog` 保存低频、小型 `ForwardRouteCatalogEntryFrame`，便于独立 replay 和恢复。
- 两者均不写入 `events/`，否则 artifact append 会扰动 `NextPhysicalEvent`。
- 不写入 ref object 或 `ref-op-log`，避免 derived 损坏影响 canonical head/name map，也避免 repair 污染 reflog。

manifest 固定：`Magic`、format version、declared length、flags、owning `JournalId`、`StoreId`、current generation、route semantics version、node/catalog format version 与 checksum。它不是 segment path table。generation 目录名同时包含 `StoreId` 和 `Generation`，因此 manifest identity 丢失后创建的新 store 不会与遗留的 `generation=1` 目录碰撞。

MVP 的 route store 是 owning EventJournal 目录内的私有 artifact，禁止单独复制到另一 journal 后继续使用。当前 canonical Event store 尚无持久 identity，因此实现 route publication 的硬前置是为 EventJournal 增加持久非零 `JournalId`（或等价 EventStore identity）；route open 必须比较 canonical 与 route manifest 中的 owner id。若无法证明 owner identity，route 必须 `Unavailable`，不能靠 segment/ticket/hint 恰好可读来接受外来 artifact。

generation 规则：

1. manifest 指向唯一 current generation；正常 append/rollover 不换代。
2. generation 目录、manifest、node 与 catalog frame 的 stamp 必须一致；route semantics/policy 升级必须换 generation。
3. 整库 rebuild/compaction 在新 generation 目录内构建；全部 durable 后，才发布新 manifest。完整顺序至少是：generation 文件与目录 durable -> 临时 manifest 文件 durable -> atomic replace -> manifest 父目录 durable。旧 generation 延迟 GC。
4. manifest/directory publication 需要平台级 directory durability；实现 Spec 必须声明并测试 fsync/等价能力。单个 RBF 文件的 `DurableFlush()` 与 atomic rename 都不等价于新文件名和目录项 durable。
5. manifest 缺失、损坏、未知版本或所指 generation 无法打开时，普通 open 将 artifact 标记为 `Unavailable`，而不是阻止 EventJournal 打开或自动破坏性重建。
6. repair 可创建 new store/new generation；不得在旧 stamp 下删除目录后从 segment 1 原地重用地址。
7. traversal/build 持有 generation lease；manifest 切换后新调用只进入新 generation，GC 不得删除仍被 reader/writer lease 使用的旧 generation。

每种 frame 还应有 `Magic`、format version、declared length、flags envelope，并受 RBF payload CRC 保护。精确 FourCC、offset 和 fixed length留给 wire-format Spec。

## 7. Route 构建与维护

### 7.1 ResolveRoute(head)

`ResolveRoute(head)` 返回与 head 精确绑定的 descriptor：

1. 若当前 generation 的 catalog/cache 中已有对该 head 完整验证的 descriptor，可直接使用；cache entry 必须带当前 route semantics/policy identity 和 generation lease。
2. 否则从 head 沿 authoritative Parent chain 逆走到真正 root。
3. 反转得到 root-to-head 边序列。
4. 对每条边比较 `NextPhysicalEvent(parent)`，只为 non-implicit edge 追加 node。
5. 对本次触达的每个 node segment 完成 durable barrier。
6. 返回 `{Stamp, TargetHead, RootEvent, Tail, RedirectCount}`；是否发布 catalog 由调用方决定。

冷构建时间与临时地址空间均为 $O(n)$。只有在 exact-head descriptor 已完整 preflight、且 stamp/policy 仍匹配时，才可复用已验证的 node prefix；不能因为两个路线看起来有相同 root 或最后一个 redirect 就猜测 prefix 相同。其余 cache miss、repair 或任意 retarget 冷路径都从 authoritative Parent chain 重建，不应为了避免一次重建而削弱正确性。

若 node batch 跨 segment，不能只 flush 最后一个 active file。当前 `RbfSegmentStore.OpenActiveWriter()` 可能在借 writer 前先轮转并关闭旧 active file，因此本设计不假定存在“轮转前由调用方 flush”的 hook：MVP route wrapper 对每次 node append 后立即 `DurableFlush()`，并在 catalog publication 前确认所有引用 node 所在 segment 已完成 barrier；若要批量写入，必须先为 segment store 增加显式 store-level flush/rotation hook。segment 首次创建时还必须完成文件与必要目录项的 durable publication。

### 7.2 PublishRoute(head)

```text
ResolveRoute(head)
-> all referenced nodes durable
-> append catalog entry
-> catalog segment DurableFlush + new segment file/directory durable
```

catalog entry durable 是 route 对 reader 可发现的 publication point。先写 nodes 后崩溃只产生 orphan nodes；entry 撕裂则 recovery 忽略它。catalog 可在 ref move 之前、之后或完全不发布，因为 route 对 ref correctness 没有依赖。`PublishForwardRoute` 必须重新 checked-read descriptor 的 exact `TargetHead`、root、全部 node chain、当前 stamp/policy，并拒绝调用方伪造或来自旧 generation 的 descriptor；public descriptor 只是一种候选输入，不是 durable provenance 证明。

route 构建、flush 或 catalog publication 失败时：

- canonical ref mutation 仍可继续或已经成功。
- API 不得把 artifact failure 冒充 canonical ref failure，诱导调用方重复 ref mutation。
- 返回 `RouteStatus`、Warning 或诊断；forward traversal 走 fallback。组合 mutation API 必须返回独立的 `CanonicalCommitted` 与 `RouteStatus`，至少区分 `NotAttempted`、`AlreadyAvailable`、`Published`、`Failed`、`Indeterminate`；`Failed/Indeterminate` 不得被报告成 canonical CAS failure，也不得要求调用方重复 mutation。

### 7.3 CreateBranch 与 ForkBranch

canonical 两阶段发布保持不变：

```text
RefOp Create/Fork durable
-> RefMove Init durable
-> RefOp BindName durable
```

- `CreateBranch(name, null)` 不需要 route。
- `CreateBranch(name, H)` 和 `ForkBranch(..., H)` 可在任意时机 best-effort `PublishRoute(H)`。
- fork 不复制 nodes，也不需要复制 source ref 的 descriptor；新旧 ref 之后都按相同 `TargetHead` 查 catalog。
- artifact 任一步失败不得阻止 `Init` 或 `BindName`，也不得让已发布 branch 消失。

若应用希望首读就加速，可以在 `Init` 前构建 route；若偏好 ref 低延迟，可以在 `BindName` 后 lazy build。两者 correctness 相同。

### 7.4 First Commit 与 Advance

unborn ref 首次 `Advance` 必须产生 root Event：

```text
NewEvent.Parent == null
descriptor = { TargetHead=NewEvent, RootEvent=NewEvent, Tail=null, RedirectCount=0 }
```

普通 `Advance(oldHead, newHead)` 只有在以下条件同时成立时才能增量构建：

- writer 已 checked-read `newHead` 并验证 `newHead.Parent == oldHead`。
- old head 的 descriptor 已对 exact head 完成全路线 preflight，而不只是 catalog/frame 廉价检查。
- descriptor stamp 仍是当前 generation。

若 `NextPhysicalEvent(oldHead) == newHead`，复用 old tail/root；否则追加 `Redirect(oldHead,newHead)`。若 old route 缺失、未完整验证或损坏，则完整 `ResolveRoute(newHead)`，或跳过 artifact；不得在未经验证的 tail 上追加。无论选择哪条 artifact 路径，`AdvanceRef` 的 canonical success/failure 都独立返回。

`CommitToRef` 知道 Event 刚 append。在单 writer 下，若 append 前最后一个物理 Event 等于 old head，可直接证明新边 implicit；否则必须用 `NextPhysicalEvent` 精确判断并按结果写 redirect，或不发布 route。不能仅因“无法廉价证明 implicit”而保守写冗余 redirect。无论 fast path 如何，route failure 都不改变 canonical `AdvanceRef` 结果。

### 7.5 MoveRef 与 ancestor trim

- `MoveRef(..., null)` 与 `Close` 不发布 route；旧 head 的 catalog entry 不变。
- `MoveRef(..., H)` 优先复用 exact-head verified cache/catalog，否则 best-effort 完整 `ResolveRoute(H)`；route 构建失败不撤销也不阻止 canonical Move，结果中只报告独立的 `RouteStatus`。
- sibling/unrelated target 不能按物理地址截断 old route。

当且仅当已经通过 Parent walk 证明 `H` 是 old head 的祖先，且 old descriptor 已完整验证，才可 trim：

- 只保留 `ToChild` 位于 `Root..H` 路径上的 redirect；它们必构成旧 node chain 的 prefix。
- `H == node.ToChild` 时保留该 node。
- `H == node.FromEvent` 时该 node 已越过 H，必须删除。
- H 位于最后一个保留 redirect 后的 implicit run 中时，可原样复用该 prefix tail。

trim 后产生一个 `TargetHead=H` 的新 descriptor，并可追加同 head catalog entry；它不写 ref move 之外的任何 canonical 状态。若 publication 失败，已成功的 Move 不得重试或回滚；下次读取仍以 H 为 captured head 并 fallback。

### 7.6 Lazy Build 与 Repair

普通读取不隐式写盘，避免 read API 产生意外 mutation；catalog miss 只 fallback。显式 maintenance 可以：

```text
RepairForwardRoute(head)
    checked-read head
    ResolveRoute(head)
    append catalog entry for exact head
```

repair 不写 `RefMoveFrame`、不增加 move sequence、不污染 reflog，也不要求当前仍有 ref 指向 head。若 API 承诺“repair current ref”，则先 capture `{RefId, MoveSequenceNumber, Head}`，构建后再次比较；变化时报告 stale，但已经发布的 head-scoped route 仍是安全 artifact。

## 8. Checked Forward Traversal

### 8.1 Snapshot 与 lookup

ref 遍历开始时只捕获 canonical 状态：

```text
RefTraversalSnapshot {
    RefId
    MoveSequenceNumber
    Head
    BranchStart
}
```

随后按 captured `Head` 查询当前 route catalog。中途 ref 再移动或 route 被修复，不会改变本次目标；同 head 的任一完整 route 都描述同一 Parent lineage。若某 API 承诺“首次产出时仍是 current”，必须在 prepare 完成、第一次产出前重新检查 move sequence/head，变化时返回 stale 或重试。

### 8.2 为什么必须先 Preflight

每个 redirect 局部满足 `ToChild.Parent == FromEvent`，仍不能证明 route 没有漏路标。例如物理顺序为 `A, B, C, D, E`，目标路径为 `A -> B -> D -> E`。若 `Redirect(B,D)` 丢失，reader 会把同样以 B 为 Parent 的 sibling C 当作默认后继；事后 fallback 已无法撤销交给不可回滚 sink 的 C。

因此默认 checked API 必须在第一次 Event 产出前证明整条 route 精确抵达 captured head。

### 8.3 Prepare 算法

```text
PrepareForwardTraversal(snapshot, range):
    validate range mode and any supplied EventAddress shape
    if Head is null: return EmptyPreparedTraversal       // 不解引用 range boundary
    if range == AfterExclusive(event): checked-read event
    if range == FromBranchStart and BranchStart != null:
        require BranchStart is a checked-readable EventAddress
        require BranchStart will be proved ancestor of Head
    candidate = CatalogLookup(Head)
    if candidate missing: return PrepareCanonicalFallback(Head, range)

    require candidate.Stamp == current manifest stamp
    require candidate.TargetHead == Head
    require (Tail == null) iff (RedirectCount == 0)

    load exactly RedirectCount nodes from Tail through Previous
    validate full frame CRC/tag/schema/stamp, ordinal, root consistency,
             Previous physical monotonicity and hop bound
    require final Previous == null
    reverse nodes into path order                         // O(k)

    require RootEvent <= Head
    current = RootEvent
    checked-read current; require current.Parent == null

    while current != Head:
        if nextRedirect.FromEvent == current:
            next = nextRedirect.ToChild
            consume redirect
        else:
            reject if nextRedirect.FromEvent is already behind current
            next = NextPhysicalEvent(current, upperBound = Head)

        require current < next <= Head
        preview/decode next canonical Event header
        require next.Parent == current
        current = next

    require every redirect was consumed
    validate range boundary against the proven path:
        FullHistory => start at RootEvent
        AfterExclusive(event) => event occurs on proven path; start after it
        FromBranchStart => BranchStart == null means FullHistory,
                           otherwise BranchStart occurs on proven path; start there
    return PreparedForwardTraversal(snapshot, redirects, range)
```

从 root 经逐步 Parent 直连最终精确抵达 head，构成完整路径证明；单 Parent 图不能从错误 sibling 重新汇入正确 lineage。Event 物理坐标严格递增保证 Event path 终止；node `Previous` chain 仍需靠 count、ordinal、地址单调与预算独立防环。

Prepare 成功后，第二遍使用冻结的 redirect 数组流式读取完整 EventFrame/payload，并在范围起点前只导航、不产出。append-only 约束保证既有 frame 之前不会插入新 Event。

### 8.4 信任、预算与 fallback

以下 artifact-specific 情况在零产出时 fallback：

- store/catalog/descriptor 缺失或版本不支持。
- node 地址、CRC、tag、schema、stamp、count、ordinal 或 Previous chain 无效。
- root 不合法、redirect Parent 不匹配、漏 redirect、overshoot 或最终未抵达 head。

fallback 从 captured head 沿 Parent 逆走，把 $n$ 个地址压入临时容器，再正向产出；这与当前 `ReadChronologicalChain` 一致。fallback 成功只记录 route cache miss/corruption（推荐 `DebugUtil.Warning`），不把 ref/Event history 判坏；fallback 也失败才报告 authoritative history corruption。

取消、调用方预算耗尽、权限/I/O 暂态失败和进程资源错误不等同于“artifact invalid”，不得静默改走成本可能更高的 fallback 以绕过限制。实现必须区分 `ArtifactUnavailableOrInvalid` 与 `OperationAborted/ResourceFailure`：缺失、旧版本、CRC/codec/schema 损坏可在本次 prepare 零产出时 fallback；权限拒绝、底层 I/O 错误、取消和预算耗尽直接上抛/返回对应失败。若 optional-open 阶段只是无法打开 route store，后续调用应把它分类为 `ArtifactUnavailable`；一旦已经开始 canonical Event 读取而发生 I/O 错误，不得伪装成 route miss。

route preflight 只保证不会因错误 artifact 选择错误历史；header preview 不保证随后所有 payload 都可读。payload 损坏属于 authoritative Event 读取失败，不能通过跳过该 Event 或 route fallback 隐藏。若调用方要求 payload 损坏时也零副作用，需要 full-frame preflight、transactional/staging sink 或幂等 consumer 协议之一。

## 9. Event-level Forward Cursor

当前 RBF 已公开 `IRbfFile.ScanForward()`；[design talk](design-talk/forward-route.md) 中“只有 `ScanReverse`”的判断已经过时。现有缺口是：

- `ScanForward()` 只能从单个 RBF 文件的第一帧开始。
- RBF 不负责跨 EventJournal segment。
- RBF 不知道哪些 tag 是 EventFrame。

如果 redirect 跳到 segment 中部后每次从文件头重扫，最坏可能退化为 $O(n^2)$。完整实现需要：

1. RBF 提供从已验证 frame boundary 开始的 forward scan，如 `ScanForwardAfter(SizedPtr previous)`。
2. EventJournal 提供跨 segment 的 `EventPhysicalCursor`，负责过滤 tag/tombstone、构造并校验 canonical hint、应用 head 地址上界和管理 reader lease。

在该能力落地前，writer 只能对能廉价证明的 edge 做增量维护；其余情况不发布 route并 fallback，或使用正确但可能因反复重扫 segment 前缀而退化到 $O(n^2)$ 的精确 builder。不得把冗余 all-redirect 路线发布成 canonical sparse route，也不得把反复重扫的实现宣称达到目标复杂度。

## 10. Durable 顺序与恢复

### 10.1 三条彼此独立的提交链

canonical ref commit：

```text
EventFrame append -> Event segment DurableFlush
-> RefMoveFrame append -> ref object DurableFlush
```

route publication：

```text
ForwardRouteNode append(s)
-> every touched node segment + required directory entries durable
-> CatalogEntry append
-> catalog segment + required file/directory entries durable
```

branch create/fork：

```text
RefOp Create/Fork durable -> RefMove Init durable -> RefOp BindName durable
```

route publication 可以插在 canonical 步骤之间、之后异步完成，或完全失败。唯一硬顺序是 catalog entry 不得先于其引用 nodes durable。由于 catalog 以 Event head 为 key，ref CAS 失败后留下针对 orphan Event 的 route 也不悬空到错误对象：该 Event 若 checked-readable，route 仍是该 head 的合法 derived artifact。

### 10.2 Active-tail recovery

node/catalog control record 的 active-tail recovery 必须使用 `RbfRecoveryValidationLevel.FullFrame`，但“找到最后一个完整 frame”还不够：恢复器必须从 segment header 或持久 publication watermark 向前验证连续 prefix，确认其中没有 framing/CRC 断裂；否则整代标记 `Unavailable`，不得在同 stamp 下继续 append。只有在确认未越过任何已发布 node/catalog frame 后，才可按最后一个连续完整 frame 截断并 durable flush。默认 `FrameBoundary` 只校验 Fence/HeadLen/Trailer，不能排除“trailer 已落盘但 payload 撕裂”，因此当前基础实现的默认恢复策略是 route artifact 的前置缺口。

canonical `events/` 同样必须在 reopen 后再次 append 前完成 `FullFrame` + 连续 prefix recovery；否则一个 boundary 完整但 payload 撕裂的 Event slot 可能被保留并永久阻断所有跨过它的 `NextPhysicalEvent`/repair。该修正不是为了 route 才改变 Event history，而是确保 append-only canonical Event 序列只在最后一个连续完整 frame 之后继续写。

同一修正也适用于 canonical `RefObjectStore` / `ref-op-log` 的控制记录恢复；这是现有实现与 ref 设计基线的前置缺口，不应由 route 层假装已经满足。replay 对 CRC 完整但 codec/tag/sequence malformed 的 canonical ref frame，还需在 ref store Spec 中明确 last-valid-move 或 corrupted-ref 策略。

### 10.3 Optional open

推荐打开顺序：

1. 恢复并打开 canonical events。
2. 恢复/replay `ref-op-log`；具体 ref object 继续按现有实现 lazy open/replay。
3. best-effort 打开 route manifest、current generation、nodes 和 catalog。
4. route 任一步缺失、未知版本、owner mismatch 或 artifact-specific 损坏则隔离为 `Unavailable` 并记录 Warning；不截断 refs、不回退 head、不阻止 EventJournal open。artifact open 是隔离的 lazy/sub-result：即使权限拒绝、资源耗尽或 I/O failure，也只让 route capability 返回对应 failure/Warning，canonical `EventJournal.Open*` 仍返回可用 journal。随后 traversal 若显式要求 route 且遇到新的资源/I/O failure，则直接返回该失败，不自动 fallback；调用方可稍后重试。
5. catalog 可 lazy replay 或构建内存 latest map；node 完整语义校验延迟到首次 traversal/verify。catalog replay 遇到 malformed frame 时丢弃该 entry 及其之后无法证明顺序的候选，标记对应 route unavailable；不得把坏 entry 解码成 latest descriptor。

普通 open 不自动删除或重建坏 generation。显式 verify/repair 在 canonical journal 已打开后创建新 generation，再原子切换 manifest。这避免“先读 refs 才知引用、先修 route 才能读 refs”的循环，也避免错误重用旧地址。

### 10.4 Crash Matrix

| 崩溃点 | 恢复结果 |
|:-------|:---------|
| EventFrame 未完整 | event active tail 以 FullFrame + 连续 prefix 恢复到最后一个完整 Event；ref 不变 |
| Event durable，RefMove 未写 | orphan Event；ref 不变 |
| RefMove 撕裂 | ref object 以 FullFrame 恢复到上一完整 move |
| RefMove durable | 新 head 生效，与 route 是否存在无关 |
| node 撕裂 | 若无法证明已发布 node watermark 仍完整，则整代标记 unavailable；不得在同 stamp 下复用被截掉的地址 |
| nodes durable，catalog 未写/撕裂 | orphan nodes；head 遍历 fallback |
| catalog durable | 对应 route 可被发现；仍须 checked preflight；catalog 新 segment 的文件与目录项也必须 durable |
| catalog/node/manifest 后来损坏 | artifact unavailable/invalid；保留 head并 fallback |
| 新 generation 构建未发布 | orphan generation；旧 manifest 仍生效 |
| 新 manifest durable | 新 generation 生效；旧 generation 可延迟 GC |

## 11. 复杂度与空间账

令：

- $n$：root-to-head path 的 Event 数。
- $k$：该 route 的 non-implicit edge / redirect 数。
- $q$：默认连续 run 中被 cursor 跳过的非 Event RBF frame 数。
- $h$：已发布 catalog entry 的 distinct head 数（含 repair/旧 entry 时按物理记录计）。

健康 route：

- immutable node 持久空间：共享 route prefixes 后，每个新 non-implicit edge 通常新增一个 node，整体由实际 redirect publication 数决定。
- catalog 持久空间：每个被加速的 head 至少一条 $O(1)$ entry，总计 $O(h)$；即使 $k=0$ 也不是“零 artifact 空间”。
- Prepare 时间：$O(k+n+q)$；replay 为 $O(n)$ 次 Event 读取。
- 导航内存：$O(k)$，不再是 $O(n)$ 地址栈。
- implicit `Advance` 可复用 node tail，但若要让新 head可发现，仍追加一条小 catalog entry；non-implicit `Advance` 再多一个 node。
- fork 到已有 head：零 node 写；catalog 已存在时也零写。

catalog latest map 若全量驻内存为 $O(h)$；MVP 可接受，也可后续增加 sparse index/checkpoint。损坏 route 在受 head upper bound、最大 Event/physical-step 与 redirect count 限制下失败，再执行 canonical fallback。

preflight 是一次 $O(n)$ header pass，随后 replay 再做一次 $O(n)$ payload pass；仍为线性时间，换取对不可回滚 consumer 的零错误路径产出。

## 12. API 候选

用于约束职责而非锁定最终签名：

```csharp
public readonly record struct RefTraversalSnapshot(
    RefId RefId,
    ulong MoveSequenceNumber,
    EventAddress? Head,
    EventAddress? BranchStart
);

public enum ForwardTraversalRangeKind {
    FullHistory,
    AfterExclusive,
    FromBranchStart
}

public readonly struct ForwardTraversalRange {
    public ForwardTraversalRangeKind Kind { get; }
    public EventAddress? Boundary { get; }

    public static ForwardTraversalRange FullHistory { get; }
    public static ForwardTraversalRange FromBranchStart { get; }
    public static ForwardTraversalRange AfterExclusive(EventAddress boundary);

    private ForwardTraversalRange(ForwardTraversalRangeKind kind, EventAddress? boundary);
}

public enum RouteStatus {
    NotAttempted,
    AlreadyAvailable,
    Published,
    Failed,
    Indeterminate
}

public readonly record struct RoutePublicationOutcome(
    RouteStatus Status,
    ForwardRouteStoreStamp? Stamp,
    AteliaError? Diagnostic
);

// Opaque、process-local；持有当前 generation lease 与已验证 node provenance。
public sealed class ForwardRouteBuildHandle : IDisposable {
    public ForwardRouteDescriptor Descriptor { get; }
}

public AteliaResult<RefTraversalSnapshot> CaptureRef(RefId refId);

public AteliaResult<PreparedForwardTraversal> PrepareForwardTraversal(
    RefTraversalSnapshot snapshot,
    ForwardTraversalRange range,
    ForwardTraversalOptions? options = null,
    CancellationToken cancellationToken = default
);

public AteliaResult<ForwardRouteBuildHandle> BuildForwardRoute(
    EventAddress head,
    CancellationToken cancellationToken = default
);

public AteliaResult<RoutePublicationOutcome> PublishForwardRoute(
    ForwardRouteBuildHandle build,
    CancellationToken cancellationToken = default
);

public AteliaResult<RoutePublicationOutcome> RepairForwardRoute(
    EventAddress head,
    CancellationToken cancellationToken = default
);
```

`PreparedForwardTraversal` 必须携带 captured head、范围边界和 fallible sequence。它可以持有 $O(k)$ redirect 数组，但不得长期霸占所有 segment reader lease。

`ForwardTraversalRange` 采用闭合构造：只有 `AfterExclusive` 可携带 non-null `Boundary`，且它必须携带；`FullHistory` 与 `FromBranchStart` 的 `Boundary` 必须为 null。反序列化/内部 default 值仍必须经过 validator，不能把可构造的非法组合直接送入 traversal。

`ForwardRouteDescriptor` 不作为可无条件信任的 public write token：`BuildForwardRoute` 返回的 `ForwardRouteBuildHandle` 绑定 checked-read 的 exact head、当前 stamp/policy、node provenance 和 generation lease；`PublishForwardRoute` 必须重新验证这些内容后才可发布。若实现确实暴露 descriptor 作为序列化输入，也必须把它视为 untrusted candidate，按 §7.2 的完整 preflight 重建/核对，而不是接受调用方拼出的 `Tail`。

组合 mutation（如 `AdvanceRef`、`MoveRef`、`CreateBranch`）的结果应包含独立字段：

```text
CanonicalCommitted: bool
RouteStatus: NotAttempted | AlreadyAvailable | Published | Failed | Indeterminate
```

`CanonicalCommitted=true` 且 route status 为 `Failed/Indeterminate` 是合法结果；调用方不得因为 route publication 失败而重放同一 canonical mutation。独立的 `RepairForwardRoute` 则可以把 publication failure 作为自身失败返回，因为它没有 canonical mutation 要隐藏。

## 13. 方案比较

### 13.1 Branch-local Mutable Route

不采用。它会引入 fork copy/COW、同 head route 重复、reset truncate、generation 协调和 branch ownership；最终仍会重新发明 persistent data structure。

### 13.2 Descriptor 写入 RefMoveFrame

不采用。RBF CRC 覆盖整个 frame；descriptor 字节损坏会让同 frame 的 authoritative head 一并不可读，无法兑现“只丢加速、保留 head”的故障隔离。它还会让 route repair 需要伪造 same-head ref move并污染 reflog/CAS sequence。

### 13.3 Per-ref-move Binding Log

可行但不采用。`{RefId, MoveSequenceNumber, Head}` 能精确关联 snapshot，却没有增加路径信息：在单 Parent 下，`Head` 已唯一决定路线。全局 head catalog 更小、更易共享，也允许历史 move 使用后来修复的 artifact。未来若引入 multi-parent 或不同 route policy，catalog key 必须相应扩展。

### 13.4 Per-Event Child Index

不采用作为首个 artifact。全局 `Parent -> Children` index 对分支分析有价值，但正向遍历仍需知道选哪个 child；它为所有 child 付费，无法利用“多数边与物理顺序一致”的 workload。

### 13.5 Immutable Sparse Route + Head Catalog

采用。它让 node prefix 可共享、fork 自然共享、repair 与 reflog 解耦，CAS/crash 只产生 orphan artifact，损坏可完全退化到 Parent chain。代价是每个被加速 head 有 catalog entry，读取前还需反转 $k$ 个 node；这是后续 checkpoint/index 可以优化而无需在 MVP 预支的复杂度。

## 14. 验收标准

- 物理连续的 root-to-head 历史具有 `RedirectCount == 0`，正向结果与 `ReadChronologicalChain` 一致。
- 另一个 branch 或 orphan Event 插入后，后续选中 path 为被跳过的物理 Event 写 redirect。
- 跨 segment 的真正物理 Event 后继不因 segment 边界产生 redirect。
- 同 head refs/fork 通过 `TargetHead` 共享 route，不复制 nodes，不依赖 ref move descriptor。
- Move 到 ancestor、sibling、unrelated head 后均只使用与 exact target 绑定的 descriptor；ancestor trim 的 From/To 边界正确。
- node 满足 Parent 直连、物理递增、ordinal/count 连续、stamp/root 一致。
- checked traversal 在第一次产出前完成全路线 preflight；删除关键 redirect 时不得向 sink 产出 sibling Event。
- `AfterExclusive(event)` 与 non-null `BranchStart` 必须经已证明 path 验证，非祖先不得按物理地址裁剪；null head、unborn 和 closed ref 语义固定。
- manifest/catalog/node 缺失、CRC/tag/schema/stamp/Previous chain 损坏或 route 未抵达 head时，保留 ref head并 fallback。
- route store 整体缺失或打不开不阻止 EventJournal 打开；repair 使用新 stamp/generation，不产生地址别名。
- route 构建或 publication 失败不阻止 Create/Fork/Advance/Move 的 canonical 成功。
- node 跨 segment publication 在 catalog entry 前对每个 touched segment 和必要目录项完成 durable barrier。
- active tail 使用 `FullFrame` 并验证连续 prefix；无法证明未越过 publication watermark 时整代 unavailable，不在旧 stamp 下复用地址。
- 取消/预算耗尽不被 artifact fallback 吞掉。
- 健康 route 的导航内存为 $O(k)$；具备 startable/cross-segment cursor 后，prepare 为 $O(k+n+q)$。

## 15. 后续施工入口

建议按以下顺序实施：

0. 为 canonical Event store 增加持久非零 `JournalId`，并把 owner identity 写入 route manifest；没有它不得宣称 route 可安全跨目录打开。
1. 修正 canonical events、ref control record 的 `FullFrame` tail recovery / 连续 prefix / last-valid replay 契约。
2. 为 RBF 增加从已验证 frame boundary 开始的 forward scan。
3. 在 EventJournal 实现跨 segment、Event-only、带 upper bound 的 `EventPhysicalCursor`。
4. 实现 route manifest/stamp、generation publication 和 optional-open wrapper。
5. 实现 node/catalog codec、各自的 segment store wrapper与跨 segment durable barrier。
6. 实现 `ResolveRoute`、incremental advance、ancestor trim、lazy build/repair。
7. 实现 `PrepareForwardTraversal`、范围裁剪、canonical fallback 与故障注入测试。

checkpoint、route blocks、catalog checkpoint/index、verify/repair CLI 与 GC 在收集真实 $k/h$ 分布和性能数据后再设计。
