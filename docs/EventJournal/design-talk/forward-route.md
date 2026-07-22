更新了待办事项列表

这个方向是合理的，我先对照当前 EventJournal 对 branch、Parent 链、derived artifact 和 replay 的既有约束，确认“路标序列”应属于权威状态还是可重建索引。然后我会把它具体化成可维护的数据结构，并重点检查回退后再追加、跨 segment、崩溃恢复这三个容易改变结论的场景。

Searched for regex `branch|Branch|Parent|父链|replay|Replay|正向|逆向|derived|派生|索引|回退|分叉` (`**/docs/EventJournal/event-journal-requirements-and-design.md`), 70 results

Searched for regex `branch|Branch|Parent|父链|replay|Replay|正向|逆向|derived|派生|索引|回退|分叉` (`**/docs/ChatSession/event-sourced-session-architecture-roadmap.md`), 42 results

Searched for regex `ParentCommit|ParentVersion|Branch|branch|Checkout|Head|Revision|CommitAddress` (`**/src/StateJournal/**`), 293 results

Searched for regex `reverse|Reverse|forward|Forward|scan|Scan|FindLast|Enumerate` (`**/src/Rbf/**`), 22 results

现有规范已经给了这个结构一个很自然的位置：Parent 链仍是事实源，branch/ref 只是可变控制状态，正序遍历目前明确依赖“逆走后压栈”。你的路标因此最适合作为 **branch-scoped、可重建、可校验的 forward-navigation artifact**，而不是写进 Event 或取代 Parent；这样损坏时最多退化回旧算法，不会改变历史含义。下一步要厘清的是：路标究竟保存“每个分叉选哪个 child”，还是保存更紧凑的连续区间，因为这会决定能否真正做到流式正序 replay。

我再读一下当前遍历、ref 持久化和 unreachable event 的精确定义，并借现有 StateJournal 的 recent-head/reflog 机制检验更新原子性；这能把候选结构从直觉收敛成一组可验证的不变量。

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#318-318), lines 318 to 420

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#430-430), lines 430 to 465

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#140-140), lines 140 to 165

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#415-415), lines 415 to 455

Read [](file:///repos/focus/atelia/src/StateJournal/Repository.BranchRefs.cs#1-1), lines 1 to 190

局部约束揭示了一个关键修正：不能把“需要路标”等同于“branch 发生 rewind”。即使 branch 从自己的 head 正常追加，只要另一个 branch 或 orphan 已经先从该 head 追加过 child，新 child 也是后出现的 sibling；反过来，某些跨支写入即使没有真正分叉，也可以安全地多记一个路标。因此最稳的触发条件应基于 **物理追加是否连续**，而不是上层操作名称。接下来我会确认 RBF 是否具备廉价的正向 frame 边界解析；如果没有，路标只解决了选路问题，还需要一个 forward scanner 才能完整解决离线 replay。

Searched for regex `forward scan|正向扫描|正序|逆序|HeadLen|TailLen|leading|trailing|FrameLength|TotalLength|PayloadLength|backward|reversible|双向` (`**/docs/Rbf/**`), 104 results

RBF 格式本身已经在每个 frame 头部保存可信前需校验的 `HeadLen`，因此从已知 segment 起点做顺序解析在格式上可行；目前只是公开 API 只实现了 `ScanReverse`。这意味着你的路标确实能补上“选哪条父链”的短板，但最终方案应明确包含一个 `ScanForward`/event-level forward cursor，而不能只增加 branch 元数据。现在可以比较三种表示：fork choice、连续 run，以及两者组合。

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#90-90), lines 90 to 215

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#230-230), lines 230 to 315

Read [](file:///repos/focus/atelia/docs/Rbf/rbf-format.md#35-35), lines 35 to 125

Read [](file:///repos/focus/atelia/docs/Rbf/rbf-format.md#145-145), lines 145 to 204

合理，而且和当前 EventJournal 的单 Parent、append-only 约束非常契合。

我会把这个想法稍微重新表述为：

> **以物理 Event 追加顺序作为默认正向路径，仅持久化偏离该默认路径的少量 splice/redirect。**

它不只是“分叉索引”，更准确地说是 **稀疏正向路径索引**，暂可命名为 `ForwardRoute`，其中每块路标叫 `RouteRedirect` 或 `PathSplice`。

**为什么可行**

当前 Event 只保存 Parent，因此：

- 逆序遍历天然是 $O(1)$ 额外空间；
- 正序遍历要么保存整条 Parent 链；
- 要么持久化某种 child/forward 信息；
- 要么利用物理追加顺序，并只记录例外。

你的方案正是第三种，而且对于“绝大多数时间沿一个 branch 连续推进、偶尔 rewind/reroll”的 Agent workload，例外数量远小于 Event 数量。

例如物理追加顺序是：

```text
A -> B -> C       // 原未来
         \
          D -> E  // rewind 到 B 后产生的替代未来
```

当前 branch 为 `A, B, D, E`。只需保存：

```text
Redirect(B, D)
```

正序 replay 时：

1. 从 `A` 按物理顺序读到 `B`；
2. 看到 `B -> D` 路标，直接跳到 `D`；
3. 再按物理顺序读取 `E`；
4. 在 branch head `E` 停止。

因此额外空间从 $O(n)$ 的全路径地址列表，降为 $O(k)$ 的路标列表，其中 $k$ 是路径不连续次数。

**一个关键修正**

路标触发条件不应只定义为“发生了 rewind”，而应定义为：

> 新 Event 是否能够作为当前 Event 在全局物理 Event 顺序中的隐式后继。

可以保守地定义：

```text
SimpleAdvance :=
    newEvent.Parent == branch.Head
    && branch.Head == store.PhysicalEventTailBeforeAppend
```

满足时，新 Event 一定是当前 Event 的物理直接后继，不需要路标。

不满足时，即使尚不能证明真的存在多个 children，也写一条：

```text
RouteRedirect {
    From = parent,
    To = newEvent
}
```

这样不需要维护庞大的全局 `Parent -> Children` 索引。冗余路标只会出现在 branch 交错写入等非连续场景，换来非常简单可靠的不变量。

PayloadFrame 不计入这个“直接后继”判断；这里比较的是 **MetaFrame/Event 提交顺序**。跨 segment 也不自动构成中断。

**它实际上属于 Head，而非 Branch**

由于 MVP 是单 Parent，一个 Event 的 `root -> head` 路径是唯一的。因此从语义上说：

- `ForwardRoute` 是某个 head/path 的派生信息；
- branch 只是引用 `{ Head, ForwardRouteHint }`；
- 两个 branch 指向同一 head 时可以共享同一份 route；
- reflog 中每次历史 ref update 也可以保留当时的 route pointer。

这比把 route 做成 branch 名下的可变文件更好。branch rename、复制和历史回放都不会丢失路线。

**推荐的持久结构**

MVP 可以用不可变链：

```text
ForwardRouteNode {
    PreviousRouteNode?
    FromEvent
    ToChild
}
```

branch/ref update 携带：

```text
BranchRefUpdate {
    ...
    NewHead
    RootEvent
    ForwardRouteTail?
}
```

操作规则：

- 普通连续 append：复用原 `ForwardRouteTail`。
- 非连续 append：追加一个 `ForwardRouteNode`。
- reset 到祖先：把 route tail 截回该祖先之前的有效前缀。
- 从其他 head 创建 branch：共享那个 head 的 route。
- CAS 失败：Event 和 route node 都可以成为安全 orphan。

读取时只把 $k$ 个 route nodes 反转为小数组，而不再保存 $n$ 个 EventAddress。若以后 $k$ 也很大，再增加 route checkpoint/chunked vector，不必在 MVP 处理。

**必须保持 Derived 身份**

当前规范已经规定 Parent history 是事实历史，branch/ref history 是控制历史，见 event-journal-requirements-and-design.md 和 event-journal-requirements-and-design.md。

`ForwardRoute` 应当是可重建的导航 artifact：

- branch head 仍是权威目标；
- Parent 链仍是权威路径；
- route 不能创造或改变可达关系；
- route 缺失或损坏时，允许退化为 Parent 逆走加临时存储；
- route 可以由 Parent 链重新构建并修复。

每个 redirect 至少验证：

```text
ToChild.Parent == FromEvent
FromEvent <physical-order ToChild
所有 redirect 按选中路径单调向前
最终正好抵达 branch Head
```

依靠物理地址严格递增，可以在正常 replay 中防止循环，而不需要额外 `HashSet`。

**还差一个 RBF 能力**

RBF 格式已经有头部 `HeadLen` 和尾部 `TailLen`，因此健康文件的正向扫描在格式上完全可行，见 rbf-format.md。但当前 API 只有 `ScanReverse`。

完整方案需要补上类似：

```csharp
RbfForwardSequence ScanForward(...);
```

正向扫描可以：

1. 从已知 frame boundary 读取 `HeadLen`；
2. 跳到 trailer；
3. 校验 `TrailerCrc32C`；
4. 校验 `TailLen == HeadLen`；
5. 产出 frame metadata，不读取 payload；
6. EventJournal 过滤出 `MetaFrame`。

恢复和 resync 仍然以逆向能力为主；`ScanForward` 服务健康数据 replay，两者职责不冲突。

**崩溃提交顺序**

建议 control journal 中采用：

```text
Event durable
-> optional ForwardRouteNode durable
-> BranchRefUpdate durable
```

route node 和 ref update 可以同批 flush，但必须保持 route 在 ref 之前。由于 route 是 advisory，即使 route 丢失，已经 durable 的 head 仍然合法，只是下次需要重建导航信息。

**最终判断**

这个设计值得进入 EventJournal 基线。它最有价值的形式不是“为每个 fork 保存选中 child 的普通字典”，而是：

> **把物理 MetaFrame 顺序视为隐式 forward chain，用少量 `(From, To)` splice 描述 branch path 对这条 chain 的偏离。**

这样同时得到：

- 常见线性历史几乎零额外空间；
- 正序 replay 为 $O(n)$ 时间、$O(k)$ 导航内存；
- 可直接跳过被遗弃未来；
- route 可共享、可校验、可重建；
- 不动摇 Parent 链和 branch head 的权威性；
- 将来可自然升级到 checkpoint、压缩 route block 或冷归档索引。

已创建 4 个待办事项
