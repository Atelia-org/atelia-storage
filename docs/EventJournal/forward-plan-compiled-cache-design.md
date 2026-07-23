# ForwardPlan Compiled Cache 设计备忘

> **状态**：Implementation Memo / 用于离线 replay 工况
> **日期**：2026-07-23
> **前置**：[Ephemeral Forward Plan 与进程内缓存设计基线](ephemeral-forward-plan-design.md)

## 1. 定位

ForwardPlan 持久缓存不是 EventJournal 的事实源，而是 Parent chain 的可丢弃编译产物。

典型工况是离线 replay / 回测：例如 LLM Agent 自动上下文摘要功能要对同一条 branch/ref 反复正向遍历。第一次读取时沿 Parent chain 构建 ForwardPlan，随后把 plan 保存到磁盘；后续进程重启或重复任务可以直接加载 plan，再用 replay 的 Parent 校验保证正确性。

这类似编译缓存：

- source：EventFrame Parent chain 与 ref 当前 head。
- compiled artifact：ForwardPlan。
- validation：requested exact head 必须等于 cached `TargetHead`。
- rebuild：任何不匹配、格式不兼容、读取失败或 replay 验证失败，都丢弃整份缓存并全量重建。

第一版不做增量编译，不维护局部失效区间，也不做多 ref 共享前缀的持久结构。

## 2. Cache key 与文件位置

第一版按 exact head 保存完整 plan：

```text
<journal>/cache/forward-plans/v1/
  s{segment}-t{ticket}-h{hint}.efplan
```

文件名由 `TargetHead` 决定，包括 `SegmentNumber`、`Ticket.Packed` 与 `Hint.Packed`。这使普通 append 不会影响旧 head 的缓存；ref rewind/move 后只要 head 改变，就自然读取不同 cache entry 或 miss。

按 ref replay 时，流程是：

```text
state = LoadRefState(refId)
head = state.Head
if head == null: return empty
plan = GetOrBuildForwardPlan(head)
```

因此“缓存尾部与 ref 尾部一致”的验证落在 `head == plan.TargetHead`。如果 ref 已经移动，旧 plan 的文件名与当前 head 不同，不会命中；如果误读到内容不一致的文件，也会在 decode 后视为失效并重建。

## 3. 文件格式

采用固定二进制格式，避免 JSON 带来的体积与解析歧义：

```text
uint32 Magic          // "EJFP"
uint32 FormatVersion // 1
uint32 PolicyVersion // 1, 对应当前 IsImplicitEdge 策略
uint32 Reserved
EventAddress TargetHead
EventAddress RootEvent
uint64 EventCount
uint32 RedirectCount
RouteRedirect[RedirectCount]
uint32 Crc32         // 覆盖前面全部字节
```

`RouteRedirect` 编码为：

```text
EventAddress FromEvent
EventAddress ToChild
```

`PolicyVersion` 是编译策略版本。只要 implicit edge 判定、redirect 语义或 replay 契约发生不兼容变化，就 bump 版本并让旧缓存自然 miss。

## 4. 加载与保存

加载只做便宜验证：

- 文件存在。
- magic/version/policy 匹配。
- 文件长度与 `RedirectCount` 一致。
- CRC 匹配。
- `TargetHead == requested head`。
- `EventCount >= 1`。
- `TargetHead` 当前仍可 header-preview read。

加载成功后写入 process-local exact-head cache，然后正常 replay。replay 仍逐 Event 验证 `Parent == previous`，并检查 redirects 与 `EventCount` 被精确消费。

保存采用 temp file + flush + atomic rename：

```text
write <final>.<guid>.tmp
flushToDisk
move temp to final, overwrite
```

保存失败不影响 traversal；cache 是性能优化，不是 commit 协议的一部分。

## 5. 失效策略

第一版只做全量失效：

- exact head 不一致：miss/rebuild。
- 文件损坏、格式不兼容、CRC 不匹配：删除该文件并 rebuild。
- replay 失败：evict process-local entry，删除对应磁盘文件，下一次全量 rebuild。
- 未来若引入 compaction/repack/truncate/recovery：清空整个 `cache/forward-plans`，或引入 physical layout epoch 后按 epoch miss。

这条路线把复杂度压在离线读取路径，完全不进入 append/ref move 写路径。

## 6. 暂不做的事

- 不做 suffix patch / incremental update。
- 不维护 per-ref chunk cache。
- 不把多个 refs 的共享 prefix 持久化为 DAG。
- 不把 plan 作为恢复 EventJournal 正确性的必要条件。

这些都可以以后按 profiling 引入；在 Agent history 尚未非常长之前，粗粒度全量 rebuild 的性价比更高。
