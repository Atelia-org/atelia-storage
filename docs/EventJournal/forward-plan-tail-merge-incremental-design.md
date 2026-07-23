# ForwardPlan Tail-Merge Incremental Build 设计备忘

> **状态**：Design Memo / 后续实施输入
> **日期**：2026-07-24
> **前置**：[Ephemeral Forward Plan 与进程内缓存设计基线](ephemeral-forward-plan-design.md)、[ForwardPlan Compiled Cache 设计备忘](forward-plan-compiled-cache-design.md)

## 1. 目标

当前 ForwardPlan 的持久缓存按 exact head 命中：ref tail 改变后，新 head 的 compiled cache miss，会从新 head 沿 Parent chain 全量 cold build。

离线 replay / 回测里常见的变化模式是：

- 普通 append：新 head 是旧 head 的后代。
- rewind：新 head 是旧 plan 内的祖先。
- rewind 后重新分叉：新 head 是旧 plan 某个祖先之后的新 suffix。
- retarget 到旧 plan 附近的 sibling/orphan，再继续 append。

这些场景的共同点是：旧 plan 的大部分 prefix 仍可复用，真正变化的只是尾部。本文设计一个轻量的 tail-merge 增量构建算法，目标是在不 replay 旧 prefix、不维护复杂持久 DAG 的前提下，复用旧 ForwardPlan 的 prefix。

## 2. 核心观察

EventJournal 的合法 Parent edge 满足：

```text
PhysicalCoordinate(parent) < PhysicalCoordinate(child)
```

其中：

```text
PhysicalCoordinate(event) = (SegmentNumber, Ticket.Offset)
```

因此任意单 Parent lineage 从 head 沿 Parent 逆走时，物理坐标严格递减。这使“旧 head 链”和“新 head 链”可以看作两条按同一顺序递减的链表。

如果两条 lineage 有共同 prefix，那么从两个 tail 逆走，按物理坐标较大的一侧先后退，最终会遇到第一个共同 Event：

```text
old path: root ... A ... oldHead
new path: root ... A ... newHead

common = A
```

找到 `A` 后：

- 旧 plan 的 `root..A` prefix 可复用。
- `A..newHead` suffix 已经在逆走 new 链时收集。
- 新 plan = 旧 prefix + 新 suffix。

这比“replay old plan forward 建 membership set”更轻，因为它只读取旧 tail 中被剪掉的那段，而不读取旧 prefix。

## 3. 为什么只靠 new 链不够

“从 new head 往前爬，直到首次跨到 old tail 之前”能快速发现可能的分叉区间，但仅凭 `candidate < oldHead` 不能证明 candidate 在旧 plan 上。

反例：

```text
physical order: A, B, C(orphan), D, E, X
old path:       A -> B -> D -> E
new path:       A -> B -> C -> X
```

从 `X` 往前会先到 `C`，且 `C < E`。但 `C` 不是旧 path 成员，不能把旧 prefix 接到 `C`。正确的公共点是 `B`。

因此第一版应采用“双 tail 游标”：

- new cursor 从 `newHead` 逆走，收集新 suffix。
- old cursor 从 `oldPlan.TargetHead` 逆走，统计旧 suffix 被剪掉的 Event 数。
- 每次推进物理坐标较大的 cursor。
- 两个 cursor 相等时，得到真实 common event。

## 4. 算法

```text
TryTailMerge(oldPlan, newHead):
    if oldPlan.TargetHead == newHead:
        return oldPlan

    old = oldPlan.TargetHead
    new = newHead
    oldRemovedCount = 0
    newSuffixCount = 0
    newRedirectsReverse = []

    loop:
        if old == new:
            common = old
            return Combine(oldPlan, common, oldRemovedCount,
                           newSuffixCount, newRedirectsReverse)

        if old is null or new is null:
            return CannotMerge

        require both headers readable
        require parent physical coordinate is strictly earlier than child

        if PhysicalCoordinate(new) > PhysicalCoordinate(old):
            parent = new.Parent
            if parent is null: return CannotMerge
            if !IsImplicitEdge(parent, new):
                newRedirectsReverse.Add(Redirect(parent, new))
            newSuffixCount += 1
            new = parent
        else:
            parent = old.Parent
            if parent is null: return CannotMerge
            oldRemovedCount += 1
            old = parent
```

`oldRemovedCount` 表示旧 tail 中位于 `common` 之后、需要丢弃的 Event 数。

`newSuffixCount` 表示新 suffix 中位于 `common` 之后、需要追加的 Event 数。

组合：

```text
oldPrefixCount = oldPlan.EventCount - oldRemovedCount
newEventCount = oldPrefixCount + newSuffixCount

oldPrefixRedirects =
    oldPlan.Redirects where redirect.ToChild <= common in physical/path order

newRedirects = reverse(newRedirectsReverse)

newPlan = {
    RootEvent = oldPlan.RootEvent
    TargetHead = newHead
    EventCount = newEventCount
    Redirects = oldPrefixRedirects + newRedirects
}
```

保留旧 redirect 的条件使用 `ToChild <= common`，因为 redirect 表示 `FromEvent -> ToChild` 这条 edge；prefix 截到 `common` 时，只有 child 已经进入 prefix 的 redirect 才应保留。若旧 path 在 `common` 处之后立刻有一个 redirect，该 redirect 的 `ToChild` 会晚于 `common`，必须丢弃。

## 5. 正确性直觉

### 普通 append

```text
old: root ... H
new: root ... H -> X -> Y
```

初始 `new > old`，new cursor 逆走 `Y, X` 后到达 `H`，与 old cursor 相遇。结果保留完整 old plan，追加 `H..Y` suffix。

### rewind

```text
old: root ... A -> B -> C
new: root ... A
```

初始 `old > new`，old cursor 逆走 `C, B` 后到达 `A`。结果裁掉旧 suffix `(A, C]`，不追加新 suffix。

### rewind 后分叉

```text
old: root ... A -> B -> C
new: root ... A -> X -> Y
```

new cursor 先从 `Y` 退到 `A`，old cursor 从 `C` 退到 `A`，在 `A` 相遇。结果复用 `root..A`，追加 `A..Y`。

### sibling/orphan retarget

```text
physical: A, B, C(orphan), D, E, X
old:      A -> B -> D -> E
new:      A -> B -> C -> X
```

new cursor 到 `C` 后，old cursor 会从 `E` 退到 `D`，再退到 `B`；此时 `new=C` 仍晚于 `old=B`，new cursor 再退到 `B`。相遇点是正确 common ancestor `B`，而不是物理上较早出现的 orphan `C`。

## 6. 复杂度

令：

- `r_old`：旧 plan 中被剪掉的 tail Event 数。
- `s_new`：新 plan 中需要追加的 suffix Event 数。
- `k_old_prefix`：旧 prefix 中保留的 redirect 数。
- `k_new_suffix`：新 suffix 中新增的 redirect 数。

则 tail-merge 构建成本为：

```text
O(r_old + s_new + k_old_prefix + k_new_suffix)
```

如果旧 redirects 仍是普通数组，裁剪 prefix redirects 需要一次线性扫描 `O(k_old)`。通常 `k` 很小，可以接受。若 profiling 显示 redirects 很多，可把 redirects 保持物理/path 有序后用 binary search 找到最后一个 `ToChild <= common`，把裁剪降到 `O(log k + kept k copy)`。

相比 replay old prefix 的 membership-set 方案：

- 不需要 `O(n_old)` 临时 HashSet。
- 不 replay 旧 prefix 的大量 Event。
- 大 rewind 到 root 时仍会读 `O(n_old)` old tail，这是不可避免的；此时与 cold build 同阶，可直接 fallback。

## 7. 安全边界

tail-merge 是优化，不是事实源。以下情况应返回 `CannotMerge` 并 cold build：

- 任一 cursor 读 header 失败。
- Parent 不是物理更早，说明链损坏或不满足当前约束。
- 两条链最终没有共同 Event。
- `oldRemovedCount > oldPlan.EventCount`。
- 组合后的 plan 在 replay preflight 中失败。
- 超过调用方给定的 maxDepth / mergeBudget。

为了避免优化路径在极端 retarget 时读太久，建议加入预算：

```text
TailMergeBudgetEvents = min(configuredLimit, oldPlan.EventCount + safetyMargin)
```

预算耗尽则 `CannotMerge`，走 cold build。

## 8. 与 compiled cache 的关系

compiled cache 仍按 exact head 保存完整 plan。tail-merge 的输入可以是：

- process-local exact-head old plan。
- 某个 ref 上次成功读取时绑定的 old plan。
- 未来从 disk 读取的 old exact-head plan。

第一版最自然的接入点是 ref-local binding：

```text
RefForwardBinding:
    RefId
    BoundHead
    Plan
```

当 ref 当前 head 与 `BoundHead` 不同时，尝试：

```text
TryTailMerge(Binding.Plan, currentHead)
```

成功后：

- 得到 current head 的新 exact-head plan。
- 写入 process-local exact-head cache。
- 保存为 compiled cache。
- 更新 ref-local binding。

失败后：

- cold build current head。
- 保存与更新 binding。

这仍不需要多 ref 共享前缀的持久 DAG。不同 refs 可以各自持有完整 exact-head plan，浪费一点内存/磁盘，换取实现简单。

## 9. 实施建议

1. 给 `EphemeralForwardPlan` 增加内部 helper：按 `common` 裁剪 prefix redirects。
2. 实现 `TryTailMergeForwardPlan(oldPlan, newHead, budget)`，只返回 plan 或 `CannotMerge`，不改变 canonical 状态。
3. 增加 ref-local binding，仅作为读取优化，不作为 ref 事实源。
4. 在 `ReadChronologicalChain(RefId)` 中，当 current head 与 binding head 不一致时先尝试 tail-merge。
5. 成功后仍用现有 replay 校验；失败 evict binding 并 cold build。
6. 为 append、rewind、rewind 后分叉、orphan retarget、无共同 root、budget exhausted 增加测试。

## 10. 暂不做

- 不持久化共享 prefix DAG。
- 不维护 per-ref chunk cache。
- 不在 append/ref move 写路径同步更新 plan。
- 不用物理坐标猜测 membership；共同点必须由两个 Parent cursor 实际相遇证明。

这个方案可以视为“轻量增量编译”：它利用 Parent 坐标单调性避免 replay 旧 prefix，但仍把正确性锚定在 authoritative Parent chain 上。
