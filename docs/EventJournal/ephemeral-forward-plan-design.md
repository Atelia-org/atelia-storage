# Ephemeral Forward Plan 与进程内缓存设计基线

> **状态**：Design Baseline / 可作为首轮实施输入
> **日期**：2026-07-23
> **依赖**：[EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)、[Event Ref Store 设计基线](event-ref-store-design.md)、[RbfSegmentStore 设计基线](rbf-segment-store-design.md)
> **思路来源**：[forward-route design talk](design-talk/forward-route.md)
> **远期方案**：[持久化 Forward Route Artifact](backlog/forward-route-artifact-design.md)

## 1. 文档定位

本文设计 EventJournal 正向遍历的前两个实施阶段：

1. **EphemeralForwardPlan**：读取某个 captured head 前，沿 authoritative Parent chain 逆走一次，在内存中构造稀疏 redirect plan；遍历结束后可立即丢弃。
2. **Process-local cache**：以 exact `TargetHead` 为 key 缓存已构造的 immutable plan，并允许新 head 复用已缓存祖先 plan；缓存不落盘，进程重启后自然清空。

这两个阶段保留 Forward Route 思路中最有价值的部分：

> 把物理追加顺序视为默认正向路径，只记录选中 Parent path 无法直接沿物理顺序前进的边。

但它们暂不引入持久 route store、catalog、generation、publication、recovery 和跨文件 durable 协议。目标是在明显降低正向遍历导航内存的同时，让正确性仍完全依赖现有 EventFrame 与 Parent chain。

## 2. 核心决策

### 2.1 权威边界

- `EventFrame.Parent` 是 Event 历史路径的唯一事实源。
- `RefMoveFrame.NewTarget` 是 ref 当前 head 的唯一事实源。
- `EphemeralForwardPlan` 与其 cache 都是可丢弃、可重建的 process-local derived state。
- plan 缺失、构建失败、被驱逐或失效不改变任何 Event/ref，不要求写 reflog，也不影响 EventJournal reopen。
- traversal 始终绑定开始时捕获的 exact head；ref 随后发生 move/rewind 不改变已经捕获的 snapshot。

### 2.2 第一版采用“物理 RBF frame 直接相邻”

第一版不追求最少 redirect，而追求可以只靠 Parent reverse walk 判定的简单充分条件：

```text
IsImplicitEdge(parent, child) :=
    parent.SegmentNumber == child.SegmentNumber
    && child.Ticket.Offset == RbfPhysicalEndWithFence(parent.Ticket)
```

同时，builder 已从 `child.Parent` 验证 `parent -> child` 是 authoritative edge。

因此：

- 同 segment 中两个 EventFrame 物理直接相邻时，不写 redirect。
- 中间夹有任何 RBF frame（包括未来可能出现的非 Event frame）时，写 redirect。
- 跨 segment edge 第一版统一写 redirect。
- orphan/sibling Event 夹在 parent 与目标 child 之间时，写 redirect 并直接跳过它们。

该规则可能比“下一个物理 Event”产生更多 redirect，但不会漏 redirect。冗余只影响 plan 大小，不影响路径正确性。segment rollover 和内部 frame 预期远少于 Event 数，因此它仍然通常是稀疏表示。

不得使用以下方式猜测直接相邻：

- `Ticket.Packed` 数值连续：其中同时编码 offset 与 length。
- `SequenceNumber + 1`：当前 sequence 只承诺 store-local 排序辅助并允许空洞。
- timestamp 连续或相近。

RBF 应提供计算/读取“某 frame 后的直接物理 frame”的 helper；EventJournal 不应复制 Fence 大小等私有布局常量。

## 3. 逻辑数据模型

```csharp
internal readonly record struct RouteRedirect(
    EventAddress FromEvent,
    EventAddress ToChild
);

internal sealed class EphemeralForwardPlan {
    public required EventAddress RootEvent { get; init; }
    public required EventAddress TargetHead { get; init; }
    public required ulong EventCount { get; init; }
    public required IReadOnlyList<RouteRedirect> Redirects { get; init; }
}
```

第一阶段实现不引入 `PhysicalLayoutEpoch` 字段，因为当前 EventJournal/RbfSegmentStore 尚无进程内 compaction、repack 或 segment 重写能力；plan 在本阶段只作为一次遍历的临时对象使用。第二阶段加入 process-local cache 时，再把 epoch 作为 cache entry 的失效边界引入。

不变量：

- `EventCount >= 1`。
- `RootEvent` 的 `Parent == null`。
- `TargetHead` 沿 Parent 最终抵达 `RootEvent`。
- redirects 按 root-to-head path 顺序排列，`FromEvent` 严格物理递增。
- 每条 redirect 满足 `ToChild.Parent == FromEvent`，且 `FromEvent` 物理早于 `ToChild`。
- builder 为每条非 `IsImplicitEdge` 写且只写一个 redirect。
- replay 最终精确抵达 `TargetHead`，且恰好消费所有 redirects 与 `EventCount` 个 Event。

`Redirects` 使用有序数组/list，而不是 hash map。replay 本身沿 path 单调前进，只需维护 `redirectIndex`；因此查找总成本为 $O(k)$，没有必要为每个 plan 再付出 hash table 开销。

plan 必须是 internal、immutable、由 builder 独占构造的可信对象。它不得持有：

- RBF reader/writer lease。
- pooled frame/TailMeta buffer。
- `IRbfFile` 或 `Stream`。
- ref 当前状态的可变引用。

## 4. 第一阶段：构建 EphemeralForwardPlan

### 4.1 输入快照

直接按 head 遍历时，调用方显式传入 `EventAddress head`。

按 ref 遍历时，开始时捕获：

```text
RefForwardSnapshot {
    RefId
    MoveSequenceNumber
    Head
}
```

- `Head == null` 时结果为空，不构建 plan。
- 默认采用 snapshot semantics：构建或 replay 期间 ref 再移动，不改变本次目标。
- 若某个 API 承诺“首次产出时仍是 current”，它应在 plan 准备完成、首次产出前比较一次 `MoveSequenceNumber + Head`；这不是 plan 自身职责。

### 4.2 单次 reverse walk 即可得到稀疏 plan

builder 不需要先保存完整的 $O(n)$ Parent 地址表。逆走时已经同时拿到 `child` 与 `child.Parent`，可以立即判断该 edge 是否物理直接相邻：

```text
BuildEphemeralForwardPlan(head):
    child = head
    eventCount = 0
    redirectsReverse = []

    loop:
        check cancellation and maxDepth
        childHeader = ReadEventHeaderPreview(child)
        eventCount += 1

        if childHeader.Parent is null:
            root = child
            break

        parent = childHeader.Parent
        require PhysicalCoordinate(parent) < PhysicalCoordinate(child)

        if !IsImplicitEdge(parent, child):
            redirectsReverse.Add(Redirect(parent, child))

        child = parent

    reverse redirectsReverse
    return Plan(root, head, eventCount, redirectsReverse)
```

`eventCount` 是从 head 到 root 逆走时访问到的 Event 数（包含 root 和 head）。`redirectsReverse` 只包含同一批 Event edge 中的 redirect；反转后即为 root-to-head 顺序。没有 redirect 的 plan 仍然需要保存 `RootEvent`、`TargetHead` 和 `EventCount`，因为 replay 必须知道起点和终止条件。

`PhysicalCoordinate` 固定为：

```text
(EventAddress.SegmentNumber, EventAddress.Ticket.Offset)
```

Parent 必须物理早于 child。该检查既符合现有 writer 的“Parent 先存在”约束，也使损坏数据不能通过 forward/self Parent 制造循环；配合 `maxDepth` 后，首轮 builder 不必额外分配 $O(n)$ 的 cycle-detection set。诊断模式仍 MAY 使用 `HashSet<EventAddress>` 给出更精确的 repeated-address 错误。

默认使用 `ReadEventHeaderPreview`：Event TailMeta 自带 Header CRC，且 Parent walk 的目标是证明路径与构建导航。若调用方要求在产出前验证所有 payload CRC，应使用单独的 full-frame preflight option；不要把这项成本悄悄混入默认 plan build。

### 4.3 构建即是路线 preflight

builder 已读取从 head 到 root 的每个 Parent header，并为所有“不保证直接物理相邻”的 edge 写 redirect。因此 plan 完整构建成功后，已经证明：

- 每个选中 edge 来自 authoritative Parent。
- 所有未写 redirect 的 edge 都是物理 RBF frame 直接相邻。
- 从 root 按 implicit edge/redirect 前进会精确复现同一条 root-to-head path。

所以 cache miss 后不需要再做一次完整 forward header pass 才能首次产出。必须先完成整个 reverse build，再开始向 sink 产出；不能边逆走边输出。

## 5. 使用 plan 正向遍历

### 5.1 最小 RBF 能力

现有 `IRbfFile.ScanForward()` 只能从单个 RBF 文件的开头开始。第一阶段只需增加一个更小的能力：

```text
ReadPhysicalFrameImmediatelyAfter(SizedPtr previous)
    -> OptionalRbfFrameInfo / error
```

或等价的：

```text
ScanForwardAfter(SizedPtr previous)
```

第一版所有跨 segment edge 都有 redirect，因此这个 helper 只需处理同一 RBF 文件，不需要先实现跨 segment Event-only cursor。它应以已验证的 `SizedPtr` 的 `Offset + Length` 加 RBF fence 规则定位；具体 fence 大小和边界校验留在 RBF 层，EventJournal 不复制 wire-format 常量。

### 5.2 Replay 算法

```text
Replay(plan):
    current = plan.RootEvent
    redirectIndex = 0
    previous = null

    for producedCount in 1 .. plan.EventCount:
        frame = ReadEvent(current)                 // canonical full-frame read
        require frame.Header.Parent == previous
        emit frame

        if current == plan.TargetHead:
            require producedCount == plan.EventCount
            require redirectIndex == plan.Redirects.Count
            return success

        if redirectIndex < plan.Redirects.Count
           && plan.Redirects[redirectIndex].FromEvent == current:
            next = plan.Redirects[redirectIndex].ToChild
            redirectIndex += 1
        else:
            info = ReadPhysicalFrameImmediatelyAfter(current.Ticket)
            require info is non-tombstone EventFrame
            next = DecodeCanonicalEventAddress(info)

        previous = current
        current = next

    fail PlanDidNotReachTargetHead
```

实现 MAY 把“读取 next”和下一轮的 full read 合并，避免重复 I/O。无论怎样组织，都必须在交给 sink 前校验当前完整 EventFrame，并验证其 Parent 等于上一个 path Event。

因为 direct adjacency 是 conservative predicate：

- replay 不扫描被抛弃的 sibling/orphan 区间，而是通过 redirect 一次 seek 跳过。
- replay 不必扫描非 Event frame；它们会使对应 edge 在 build 时成为 redirect。
- segment 边界通过 redirect 跨越。

### 5.3 错误与副作用边界

- plan build 失败：零产出，返回 authoritative Parent/header 错误。
- cache entry epoch 不匹配：零产出地 evict，然后重新 build。
- replay 在首次产出前发现 plan 结构错误：零产出地 evict，并 MAY rebuild 一次；若重建仍失败则返回错误。
- replay 已经产出后遇到 I/O、payload CRC 或外部物理修改：立即返回错误，并使该 plan/epoch 不再命中；不得静默从头重放到同一个非幂等 sink。
- 默认方案保证不会因漏 redirect 选择 sibling；它不承诺“未来某个 payload 损坏时此前事件也零产出”。需要该承诺时使用 full-frame preflight、staging sink 或幂等 consumer。

## 6. 遍历范围

核心 cache 始终保存完整 `RootEvent .. TargetHead` plan，不为不同输出范围复制 plan。

候选范围：

```text
FullHistory
AfterExclusive(EventAddress boundary)
FromInclusive(EventAddress boundary)
```

范围边界必须被证明位于 captured head 的 Parent lineage。cache miss 的 reverse build 可以顺便记录“是否遇到 boundary”；cache hit 时不能仅按物理地址猜祖先关系，可选择：

1. 用 plan 做一次不产出的导航直到 boundary/head；或
2. 单独调用 Parent walk / `IsAncestor`。

首轮若只需要完整 history，可以暂不实现 range API；不要为预想中的范围查询把所有 path addresses 塞回 plan，否则会失去 $O(k)$ 导航内存目标。

ref 的初始 `Init.NewTarget`（若需要称为 `BranchStart`）属于 ref snapshot，而不属于 head-keyed plan。同一个 head 可被不同起点的 refs 共享。

## 7. 第二阶段：Process-local cache

### 7.1 两层索引

推荐区分：

```text
ExactHeadPlanCache:
    EventAddress TargetHead -> EphemeralForwardPlan

RefPlanBinding:
    RefId -> { MoveSequenceNumber, Head, EphemeralForwardPlan }
```

`ExactHeadPlanCache` 是真正的共享 cache。在单 Parent 模型下，exact head 唯一决定 root-to-head path；两个 ref、fork 或历史 ref move 指向同一 head 时可以复用同一个 plan。

`RefPlanBinding` 只是避免每次先解析 ref 再查 exact-head cache 的可选快捷绑定。它不是 plan ownership，也不是事实源。

### 7.2 Cache hit

命中必须同时满足：

- key 与 plan 的 `TargetHead` 完全相等，包括 AddressHint。
- plan `PhysicalLayoutEpoch` 等于当前 EventJournal instance 的 epoch。
- plan 来自本 instance 的 internal builder，不接受调用方构造或反序列化对象。

普通 append 不使旧 exact-head plan 失效：新 frame 只会出现在 captured head 之后，而 replay 在 exact head 停止。

### 7.3 复用已缓存祖先

构建新 head 时，可以在 reverse walk 中寻找第一个 exact-head cache hit：

```text
BuildWithCachedPrefix(newHead):
    if cache contains newHead: return hit

    reverse-walk newHead toward root
    collect redirects and suffix event count; suffix count excludes cached ancestor

    if parent/current matches cached ancestor plan:
        combine cached prefix + reversed suffix
        return plan for newHead

    otherwise continue to root
```

组合规则：

- `RootEvent` 取 cached prefix 的 root。
- `EventCount = prefix.EventCount + suffixEventCount`；由于 suffix count 不包含 cached ancestor，共享祖先只计数一次。
- `Redirects = copy(prefix.Redirects) + reverse(suffixRedirects)`。
- 若连接 cached ancestor 到第一个 suffix child 的 edge 非 implicit，suffix 中必须包含该 redirect。

首轮选择复制小型 redirect array，而不是引入 persistent vector 或进程内 node chain。常见 $k$ 很小，复制成本可控；若实测大量 heads 共享很大的 redirect prefix，再考虑 chunked immutable vector。

这使常见连续 advance 的新 plan 构建接近 $O(1)$ Parent/header 工作：若 old head plan 已缓存，只需分析 `oldHead -> newHead` 一条 edge。它不需要在 ref mutation 时维护 plan，仍然是下次读取时 lazy build。

### 7.4 容量与驱逐

cache 必须有界，推荐按 estimated bytes 做 LRU：

```text
EstimatedPlanBytes = object overhead
                   + Redirects.Count * sizeof(RouteRedirect)
```

可以同时设置最大 entry 数，防止大量 `k=0` heads 只靠对象开销占满内存。

- eviction 只移除 cache 引用；已交给某个 prepared traversal 的 immutable plan 继续有效。
- 不缓存 build failure、cancellation、I/O failure 或 corruption 结果。
- plan 不持有 lease/buffer，因此 eviction 不需要复杂资源回收。
- cache 命中、miss、build depth、redirect count、prefix reuse depth 与 eviction 用 `DebugUtil.Trace/Info` 记录；异常失效用 `DebugUtil.Warning`。

## 8. Rewind、Move 与失效语义

### 8.1 必须失效的是 ref-local binding

任何成功的 `Advance`、`Move`、move-to-null 或 `Close` 都会改变 move sequence，因此旧 `RefPlanBinding` 必须失效。最简单的实现是不主动维护 binding，而把 `{MoveSequenceNumber, Head}` 放入 binding key；ref 一移动就自然 miss。

对 rewind：

```text
old head: H
new head: A    // A 是 H 的祖先
```

下一次读取 A 时：

- 若 exact-head cache 已有 `Plan(A)`，直接复用。
- 否则从 A reverse-build；不尝试原地裁剪 `Plan(H)`。

这就是第一轮所需的“尾部回退后，受影响范围在下次读取时重建”：ref 对 H 的当前绑定立即失效，新 target A 的 plan 按需取得或重建，写路径不承担 route 维护工作。

### 8.2 不应误删仍正确的 exact-head plan

`Plan(H)` 描述的是不可变 exact head H 的历史。ref rewind 到 A 不会让 H 的 Parent chain 变错；其他 ref、历史查询或未来 move 仍可能读取 H。因此 rewind 默认不从全局 `ExactHeadPlanCache` 删除 `Plan(H)`。

应区分：

- **ref-view invalidation**：当前 ref 不再绑定旧 plan，必须发生。
- **exact-head cache eviction**：纯内存策略，可发生但不是 correctness 要求。
- **physical-layout invalidation**：底层地址语义改变，必须清空所有 plan。

若未来增加 per-ref range/chunk cache，rewind 到祖先 A 时可以把该 ref view 中 `(A, H]` 的 suffix 标记失效；共享 exact-head entries 仍保持可用。retarget 到 sibling/unrelated lineage 时，丢弃整个 ref-local binding，在新 head 下 lazy build。

### 8.3 全局失效条件

以下操作改变或可能改变既有物理地址/相邻关系，必须 bump `PhysicalLayoutEpoch` 并清空整个 cache：

- active-tail truncate/recovery 发生在当前 instance 生命周期内。
- repack、compaction、import 或重写 segment。
- 测试/维护入口替换底层 events store。
- 检测到外部文件修改，且无法证明只是在当前 tail 后合法 append。

当前进程重启会自然丢弃 cache；因此首轮不需要持久 `JournalId`、generation 或 cache recovery。

## 9. API 候选

以下 API 用于约束职责，不锁定最终命名：

```csharp
internal interface IForwardPlanBuilder {
    AteliaResult<EphemeralForwardPlan> Build(
        EventAddress head,
        ForwardPlanBuildOptions? options = null,
        CancellationToken cancellationToken = default
    );
}

internal interface IForwardPlanCache {
    bool TryGet(EventAddress targetHead, out EphemeralForwardPlan plan);

    AteliaResult<EphemeralForwardPlan> GetOrBuild(
        EventAddress targetHead,
        CancellationToken cancellationToken = default
    );

    void InvalidateRefBinding(RefId refId);
    void ClearForPhysicalLayoutChange();
}

public AteliaResult<PreparedForwardTraversal> PrepareForwardTraversal(
    EventAddress head,
    ForwardTraversalRange? range = null,
    CancellationToken cancellationToken = default
);
```

`PreparedForwardTraversal` 持有 immutable plan 或其强引用，但不长期持有任何 segment reader lease。实际枚举器按需借用并及时释放 lease。

`FromInclusive` 在首轮可以先作为内部范围抽象保留；若正式 API 尚未需要 ref 起点裁剪，应不暴露该模式，避免产生没有实现语义的公共入口。

首轮 `EventJournal` 仍是单写串读、非线程安全模型，cache 不必先引入 lock 与 in-flight build coalescing。若以后允许并发读取，再按 exact head 合并同时发生的 build；取消某个 waiter 不应取消其他 waiter 共享的 build。

## 10. 复杂度与收益

令：

- $n$：root-to-head path 的 Event 数。
- $k$：按保守物理相邻规则产生的 redirect 数。
- $s$：新 head 到最近 cached ancestor 的 suffix Event 数。

| 场景 | 构建时间 | 导航内存 | Replay |
|:-----|:---------|:---------|:-------|
| 第一阶段 cold build | $O(n)$ 次 header read | $O(k)$ | $O(n)$ full Event read |
| 第二阶段 exact-head hit | $O(1)$ lookup | $O(k)$ shared | $O(n)$ full Event read |
| 第二阶段 cached-prefix build | $O(s)$ header read + $O(k)$ redirect copy | $O(k)$ | $O(n)$ full Event read |

相较现有 `ReadChronologicalChain` 的 $O(n)$ 地址 list：

- cold build 时间量级不变，仍需沿 Parent 证明历史。
- 导航常驻内存从 $O(n)$ 降为预期的 $O(k)$。
- replay 可通过 redirect 直接跳过 abandoned futures，而不是保存每个目标 EventAddress。
- repeated head 与连续 advance 可复用进程内结果。

最坏情况下每条 edge 都不物理相邻，$k = n - 1$，方案退化到 $O(n)$ 地址空间，但不会比完整 path address list 更差一个量级。此时性能数据会提示 workload 不适合稀疏 route，或需要改用“下一个物理 Event”cursor 来减少 redirect。

## 11. 实施顺序

1. 在 RBF 增加从已验证 frame boundary 读取直接物理后继的 API，并覆盖 frame/tombstone/EOF/error 测试。
2. 实现 `IsImplicitEdge`，覆盖同 segment 相邻、不相邻、跨 segment 和 Ticket length/Fence 边界。
3. 实现 cold `EphemeralForwardPlan` builder：reverse walk、物理单调、budget/cancellation、redirect 逆转。
4. 实现 plan replay：implicit step、redirect seek、Parent 验证、exact-head/计数终止。
5. 用现有 `ReadChronologicalChain` 作为 oracle 做随机分叉/rewind 对照测试。
6. 加入 bounded exact-head LRU cache 与 metrics。
7. 加入 cached-ancestor prefix reuse。
8. 接入 ref snapshot/binding；ref move 后只失效 binding，下一次读取 lazy get/build。

第一阶段验收后即可独立使用；第二阶段每一步都是不改变遍历结果的性能优化。

## 12. 验收标准

- 纯线性、同 segment、物理连续 history 的 `Redirects.Count == 0`。
- sibling/orphan 插在 Parent 与目标 child 之间时生成 redirect，并在 replay 中跳过错误未来。
- 跨 segment edge 生成 redirect，正向结果仍与 `ReadChronologicalChain(checkedRead: true)` 一致。
- 中间存在非 Event/tombstone frame 时保守生成 redirect，不把它误当目标 Event。
- builder 只保留 redirects，不保存完整 Parent path；健康稀疏 workload 的导航内存为 $O(k)$。
- malformed/forward/self Parent、maxDepth 与 cancellation 在首次产出前失败。
- replay 精确消费 `EventCount`、全部 redirects 并抵达 exact `TargetHead`。
- 两个 ref 指向同一 head 时复用 exact-head plan。
- 普通 append 不使旧 head plan 失效；同一 head 重读命中 cache。
- 连续 advance 可从 cached ancestor 构建 suffix，结果与 cold build 完全一致。
- rewind/move 后旧 ref binding 不可命中；新 head 在下次读取时命中 exact-head cache 或重建。
- rewind 不因单个 ref 移动而删除其他 ref 仍可使用的 exact-head plan。
- physical epoch 变化后任何旧 plan 都不能继续 replay。
- cache eviction、cache miss 或 build failure 不改变 canonical Event/ref 状态。

## 13. 暂不实施的演化

只有 profiling 证明当前两阶段不足时，再依次考虑：

1. 用 Event-only physical cursor 识别跨内部 frame/segment 的 implicit edge，进一步降低 $k$。
2. 用 chunked immutable vector 减少大量 heads 复制相同 redirect prefix。
3. 对 per-ref range/chunk cache 实施基于 LCA 的 suffix invalidation。
4. 最后才考虑 [持久化 Forward Route Artifact](backlog/forward-route-artifact-design.md)；届时需要重新引入 owner identity、generation、catalog、durable publication 与 recovery 设计。

这些演化只能优化 plan 的获取或表示，不能改变 Parent/ref 的权威边界，也不能让 route 成为正确读取历史的前置条件。
