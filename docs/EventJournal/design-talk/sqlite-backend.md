更新了待办事项列表

我会从两条线核验：一条比较 EventJournal 与 Git、Kafka、EventStoreDB、Chronicle Queue、SQLite/WAL 等成熟日志/版本库；另一条调查超长 LLM Session 的现有存储与记忆模型，重点区分“原始事件账本”“派生产物”“上下文组装”和“可恢复 tool-loop”是否已有完整实现。



先做广度扫描，找出真正相邻的成熟系统和论文；筛选标准不是“也有 append-only log”或“也叫 memory”，而是它是否同时覆盖父链分叉、大 blob、崩溃恢复、派生产物 provenance、动态上下文与执行恢复。

正在 Web 上搜索“append-only segmented log parent pointer branches commit history binary blobs embedded library Git EventStoreDB Chronicle Queue NATS JetStream architecture”
Completed with input: {
  "query": "append-only segmented log parent pointer branches commit history binary blobs embedded library Git EventStoreDB Chronicle Queue NATS JetStream architecture"
}

正在 Web 上搜索“long-running LLM session storage architecture raw event log derived memory artifacts context assembly durable tool execution open source 2025 2026”
Completed with input: {
  "query": "long-running LLM session storage architecture raw event log derived memory artifacts context assembly durable tool execution open source 2025 2026"
}

正在 Web 上搜索“MemGPT Letta archival memory conversation history storage architecture context window virtual memory paper”
Completed with input: {
  "query": "MemGPT Letta archival memory conversation history storage architecture context window virtual memory paper"
}

正在 Web 上搜索“LangGraph persistence checkpoints threads store durable execution interrupts replay time travel documentation”
Completed with input: {
  "query": "LangGraph persistence checkpoints threads store durable execution interrupts replay time travel documentation"
}

正在 Web 上搜索“Zep temporal knowledge graph long term memory LLM agents architecture episodes facts provenance 2025 paper Graphiti”
Completed with input: {
  "query": "Zep temporal knowledge graph long term memory LLM agents architecture episodes facts provenance 2025 paper Graphiti"
}

第一轮已经看到一个很清楚的结论：EventJournal 的底层机制并不新，但“嵌入式 Git 式父链/分支 + 任意二进制分片 + RBF 尾恢复”这个组合没有显眼的现成 drop-in；LLM 侧则已有不少相邻成果，尤其 LangGraph、Letta/MemGPT、Zep/Graphiti。现在进一步核对官方文档和实现边界，避免只凭论文摘要作类比。

正在 Web 上搜索“site:git-scm.com/book Git internals objects commit parent refs reflog packfiles official”
Completed with input: {
  "query": "site:git-scm.com/book Git internals objects commit parent refs reflog packfiles official"
}

正在 Web 上搜索“site:docs.kurrent.io EventStoreDB streams expected revision append events projections official documentation”
Completed with input: {
  "query": "site:docs.kurrent.io EventStoreDB streams expected revision append events projections official documentation"
}

正在 Web 上搜索“site:docs.temporal.io durable execution event history activities retries idempotency .NET SDK official”
Completed with input: {
  "query": "site:docs.temporal.io durable execution event history activities retries idempotency .NET SDK official"
}

正在 Web 上搜索“site:docs.letta.com agents memory blocks archival memory messages conversation history official architecture”
Completed with input: {
  "query": "site:docs.letta.com agents memory blocks archival memory messages conversation history official architecture"
}

正在 Web 上搜索“site:help.getzep.com graphiti episodes provenance temporal facts invalidation official docs open source”
Completed with input: {
  "query": "site:help.getzep.com graphiti episodes provenance temporal facts invalidation official docs open source"
}

正在 Web 上搜索“Generative Agents interactive simulacra memory stream reflection retrieval recency relevance importance paper arxiv”
Completed with input: {
  "query": "Generative Agents interactive simulacra memory stream reflection retrieval recency relevance importance paper arxiv"
}

正在 Web 上搜索“Mem0 long term memory architecture memory history vector graph agent official docs paper”
Completed with input: {
  "query": "Mem0 long term memory architecture memory history vector graph agent official docs paper"
}

正在 Web 上搜索“LLM agent durable execution event sourcing tool calls crash recovery framework Restate Temporal LangGraph 2025”
Completed with input: {
  "query": "LLM agent durable execution event sourcing tool calls crash recovery framework Restate Temporal LangGraph 2025"
}

现在最关键的分界也浮现了：长期记忆与可恢复执行在业界通常是两套系统。MemGPT/Letta、Zep、Mem0 解决“记住并召回什么”；LangGraph、Temporal、Restate 解决“执行到哪一步以及如何恢复”；很少有系统把二者连同不可变原始历史统一起来。还需要补查 Letta 的实际存储抽象、Git/事件库可复用程度，以及几类较新的 session API。

正在 Web 上搜索“site:docs.letta.com "memory blocks" "archival memory" "conversation history" agents”
Completed with input: {
  "query": "site:docs.letta.com \"memory blocks\" \"archival memory\" \"conversation history\" agents"
}

正在 Web 上搜索“site:docs.letta.com "messages" "archival" agent memory blocks context window”
Completed with input: {
  "query": "site:docs.letta.com \"messages\" \"archival\" agent memory blocks context window"
}

正在 Web 上搜索“site:openai.github.io/openai-agents-python sessions SQLite session history compaction official”
Completed with input: {
  "query": "site:openai.github.io/openai-agents-python sessions SQLite session history compaction official"
}

正在 Web 上搜索“site:docs.llamaindex.ai agent memory long term memory chat store static memory blocks fact extraction official”
Completed with input: {
  "query": "site:docs.llamaindex.ai agent memory long term memory chat store static memory blocks fact extraction official"
}

正在 Web 上搜索“site:learn.microsoft.com semantic-kernel chat history reducers summarization truncation long conversations official”
Completed with input: {
  "query": "site:learn.microsoft.com semantic-kernel chat history reducers summarization truncation long conversations official"
}

正在 Web 上搜索“site:docs.restate.dev AI agents durable execution journal LLM tool calls official”
Completed with input: {
  "query": "site:docs.restate.dev AI agents durable execution journal LLM tool calls official"
}

正在 Web 上搜索“site:docs.langchain.com/oss/python/langgraph "time travel" fork checkpoint replay official”
Completed with input: {
  "query": "site:docs.langchain.com/oss/python/langgraph \"time travel\" fork checkpoint replay official"
}

正在 Web 上搜索“embedded .NET event store append only streams optimistic concurrency SQLite library event sourcing branches”
Completed with input: {
  "query": "embedded .NET event store append only streams optimistic concurrency SQLite library event sourcing branches"
}

正在 Web 上搜索“Hypercore append-only log fork sparse replication random access official documentation”
Completed with input: {
  "query": "Hypercore append-only log fork sparse replication random access official documentation"
}

正在 Web 上搜索“Dolt version controlled database branches commits embedded library Git-like SQL database official”
Completed with input: {
  "query": "Dolt version controlled database branches commits embedded library Git-like SQL database official"
}

Compacted conversation

已创建 3 个待办事项

对 Atelia 本身，我会用两个判据收束：第一，现有组件能否在不扭曲语义的情况下直接成为 SSOT；第二，即便不能替代，哪些成熟不变量必须抄作业。接下来把现有两份设计的承诺逐项映射过去，并做一次反方审阅，重点查“其实 SQLite/Git/LangGraph 已经够了”这种可能推翻自研结论的情形。

反证 EventJournal 自研

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#1-1), lines 1 to 220

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#221-221), lines 221 to 547

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#261-261), lines 261 to 640

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#1-1), lines 1 to 500

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#1-1), lines 1 to 500

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#501-501), lines 501 to 548

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#501-501), lines 501 to 641

Search complete: 查找既有 prior art

Semantic workspace search is not currently available

Semantic workspace search is not currently available

Semantic workspace search is not currently available

Searched for files matching `**/EventJournal*`, no matches

Searched for files matching `**/ChatSession*`, 21 matches

Searched for regex `EventJournal|ChatSession`, 876 results

Searched for files matching `**/event*source*.{rs,js,ts,py,go,java,cpp,c}`, no matches

Searched for files matching `**/git*.{rs,js,ts,py,go,java,cpp,c}`, no matches

Searched for files matching `**/sqlite*.{rs,js,ts,py,go,java,cpp,c}`, no matches

Searched for files matching `**/EventStoreDB*`, no matches

Searched for files matching `**/Marten*`, no matches

Searched for files matching `**/Dolt*`, no matches

Searched for files matching `**/Hypercore*`, no matches

Searched for files matching `**/Kafka*`, no matches

Searched for files matching `**/Chronicle*`, no matches

Searched for files matching `**/NATS*`, no matches

Searched for files matching `**/Temporal*`, no matches

Searched for files matching `**/Restate*`, no matches

Searched for files matching `**/LangGraph*`, no matches

Searched for files matching `**/Letta*`, no matches

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/docs/Galatea/backlog/done/task-04e-chat-session-event-source-replay-migration.md#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionLegacyEventSource.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionLegacyRecapRecovery.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionLegacyUpgradeExporter.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionRecoverySidecarExporter.cs)

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionLegacyEventSource.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionLegacyEventSourceImporter.cs)

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionEngine.State.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/MessageRecord.cs#1-1), lines 1 to 260

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionStorageSchema.cs)

Read [](file:///repos/focus/atelia/prototypes/ChatSession/ChatSessionContracts.cs)

正在 Web 上搜索“official SQLite documentation WAL atomic commit incremental blob I/O limits BLOB embedded transactions crash recovery”
Completed with input: {
  "query": "official SQLite documentation WAL atomic commit incremental blob I/O limits BLOB embedded transactions crash recovery"
}

正在 Web 上搜索“official Git documentation objects blobs commits refs reflog transactions fsync durability libgit2 odb stream reflog .NET LibGit2Sharp”
Completed with input: {
  "query": "official Git documentation objects blobs commits refs reflog transactions fsync durability libgit2 odb stream reflog .NET LibGit2Sharp"
}

正在 Web 上搜索“official KurrentDB EventStoreDB documentation stream expected revision event size embedded server Marten PostgreSQL event sourcing projections”
Completed with input: {
  "query": "official KurrentDB EventStoreDB documentation stream expected revision event size embedded server Marten PostgreSQL event sourcing projections"
}

正在 Web 上搜索“official Dolt documentation embedded library branches reflog BLOB limits Hypercore append-only log fork documentation”
Completed with input: {
  "query": "official Dolt documentation embedded library branches reflog BLOB limits Hypercore append-only log fork documentation"
}

正在 Web 上搜索“official Apache Kafka documentation maximum message size append log retention partition branching NATS JetStream max payload embedded Chronicle Queue documentation roll cycle durability”
Completed with input: {
  "query": "official Apache Kafka documentation maximum message size append log retention partition branching NATS JetStream max payload embedded Chronicle Queue documentation roll cycle durability"
}

正在 Web 上搜索“official Temporal documentation durable execution event history payload limit .NET SDK activity idempotency Restate durable execution idempotency journal .NET SDK”
Completed with input: {
  "query": "official Temporal documentation durable execution event history payload limit .NET SDK activity idempotency Restate durable execution idempotency journal .NET SDK"
}

正在 Web 上搜索“official LangGraph persistence checkpoints time travel branching durable execution side effects tasks documentation”
Completed with input: {
  "query": "official LangGraph persistence checkpoints time travel branching durable execution side effects tasks documentation"
}

正在 Web 上搜索“official Letta MemGPT memory blocks archival memory documentation Zep Graphiti temporal knowledge graph Mem0 memory OpenAI Agents SDK Sessions SQLiteSession documentation”
Completed with input: {
  "query": "official Letta MemGPT memory blocks archival memory documentation Zep Graphiti temporal knowledge graph Mem0 memory OpenAI Agents SDK Sessions SQLiteSession documentation"
}

Searched for regex `^#{1,4} |EJ-[0-9]|EventAddress|MetaFrame|DurableFlush|refs|reflog|SQLite|prior art|复用` (`**/docs/EventJournal/event-journal-requirements-and-design.md`), 121 results

Searched for regex `^#{1,4} |CS-[0-9]|五层|Raw Event|Derived Artifact|Retrieval|Context Planner|Execution|canonical request|tool intent|tool result|uncertain|Prior Art|复用` (`**/docs/ChatSession/event-sourced-session-architecture-roadmap.md`), 112 results

正在 Web 上搜索“site:docs.kurrent.io expected revision append events optimistic concurrency event size limit KurrentDB official docs”
Completed with input: {
  "query": "site:docs.kurrent.io expected revision append events optimistic concurrency event size limit KurrentDB official docs"
}

正在 Web 上搜索“site:docs.dolthub.com OR site:www.dolthub.com/docs branches commits reflog embedded server .NET official Dolt docs”
Completed with input: {
  "query": "site:docs.dolthub.com OR site:www.dolthub.com/docs branches commits reflog embedded server .NET official Dolt docs"
}

正在 Web 上搜索“Chronicle Queue official docs append only persisted queue roll cycle replication indexing Java embedded”
Completed with input: {
  "query": "Chronicle Queue official docs append only persisted queue roll cycle replication indexing Java embedded"
}

正在 Web 上搜索“Hypercore official docs append-only log fork sparse replication Merkle tree Node.js storage”
Completed with input: {
  "query": "Hypercore official docs append-only log fork sparse replication Merkle tree Node.js storage"
}

Read [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md#20-20), lines 20 to 430

Read [](file:///repos/focus/atelia/docs/ChatSession/event-sourced-session-architecture-roadmap.md#23-23), lines 23 to 490

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/chat-session-resources/6cdca606-68a9-4467-a1f4-186ac2cf9ff2/call_QxwfxLLPoZBvYF4qAVJogktK__vscode-1784627394361/content.txt)

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/chat-session-resources/6cdca606-68a9-4467-a1f4-186ac2cf9ff2/call_ls2lAD0mTUjKRRpb0Ol4JmhD__vscode-1784627394362/content.txt)

Searched for regex `class ChatSessionLegacyEventSource|record .*Event|Replay|Import|Commit|StateJournal|EventKind|Apply` (`**/prototypes/ChatSession/ChatSessionLegacyEventSource.cs`), 53 results

Searched for regex `class ChatSessionLegacyEventSourceImporter|Import|Replay|Commit|Revision|Repository|event` (`**/prototypes/ChatSession/ChatSessionLegacyEventSourceImporter.cs`), 44 results

Searched for regex `Commit|Revision|Repository|MessageRecord|Tool|Action|round|turn` (`**/prototypes/ChatSession/ChatSessionEngine.State.cs`), 60 results

Searched for regex `LegacyEventSource|Importer|Replay|Round|Commit|Recovery|QuickStart` (`**/tests/FamilyChat.Server.Tests/ChatSessionQuickStartSamplesTests.cs`), 4 results

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1-1), lines 1 to 90

No matches found.

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#850-850), lines 850 to 920

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1080-1080), lines 1080 to 1140

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1330-1330), lines 1330 to 1385

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1280-1280), lines 1280 to 1335

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1410-1410), lines 1410 to 1470

Read [](file:///root/.vscode-server/data/User/workspaceStorage/db2353d1cca272156a9c0cf9eeef554b/GitHub.copilot-chat/transcripts/6cdca606-68a9-4467-a1f4-186ac2cf9ff2.jsonl#1540-1540), lines 1540 to 1572

反方审阅提出了一个比“列 prior art”更重要的改动：当前文档把领域合同和 RBF 物理地址绑得太紧。若不先做 backend 对照实验，`EventAddress = SegmentNumber + SizedPtr` 会在证据出现前就把“要不要自研存储”替我们决定掉。最后再核对 .NET 可用性，避免推荐一个概念成熟但无法自然接入当前技术栈的 runtime。

已分析 `EventAddress` 的使用情况，无结果

已分析 `SizedPtr` 的使用情况，无结果

已分析 `EventJournal` 的使用情况，无结果

已分析 `EventAddress` 的使用情况，无结果

已分析 `ArtifactSetCommitted` 的使用情况，无结果

已分析 `CompletionRequestPrepared` 的使用情况，无结果

已分析 `tool-execution-uncertain` 的使用情况，无结果

已分析 `Dynamic Retrieval` 的使用情况，1 个结果

已分析 `CS-7` 的使用情况，1 个结果

已分析 `EJ-0` 的使用情况，1 个结果

已分析 `IEventJournalStore` 的使用情况，无结果

已分析 `StateJournal` 的使用情况，1 个结果

已分析 `RewriteMemoryBlockMaintainer` 的使用情况，无结果

已分析 `ContextPlan` 的使用情况，无结果

已分析 `MemoryPack` 的使用情况，1 个结果

已分析 `RBF` 的使用情况，无结果

已分析 `Ref Store` 的使用情况，1 个结果

已分析 `DurableFlush` 的使用情况，无结果

已分析 `EventJournal` 的使用情况，1 个结果

已分析 `Execution State Machine` 的使用情况，1 个结果

正在 Web 上搜索“site:docs.temporal.io/develop/dotnet Temporal .NET SDK official workflow activity”
Completed with input: {
  "query": "site:docs.temporal.io/develop/dotnet Temporal .NET SDK official workflow activity"
}

正在 Web 上搜索“site:docs.restate.dev C# .NET SDK Restate supported SDK official”
Completed with input: {
  "query": "site:docs.restate.dev C# .NET SDK Restate supported SDK official"
}

正在 Web 上搜索“site:docs.langchain.com langgraph .NET C# SDK official”
Completed with input: {
  "query": "site:docs.langchain.com langgraph .NET C# SDK official"
}

正在 Web 上搜索“site:sqlite.org atomic commit transactions power failure official SQLite BLOB incremental I/O”
Completed with input: {
  "query": "site:sqlite.org atomic commit transactions power failure official SQLite BLOB incremental I/O"
}

正在 Web 上搜索“site:learn.microsoft.com Microsoft.Data.Sqlite SqliteBlob streaming official”
Completed with input: {
  "query": "site:learn.microsoft.com Microsoft.Data.Sqlite SqliteBlob streaming official"
}

正在 Web 上搜索“site:github.com/temporalio/sdk-dotnet official Temporal .NET SDK repository”
Completed with input: {
  "query": "site:github.com/temporalio/sdk-dotnet official Temporal .NET SDK repository"
}

正在 Web 上搜索“site:docs.restate.dev supported SDKs TypeScript Java Kotlin Python Go Rust .NET”
Completed with input: {
  "query": "site:docs.restate.dev supported SDKs TypeScript Java Kotlin Python Go Rust .NET"
}

正在 Web 上搜索“site:github.com/langchain-ai/langgraph supported Python JavaScript no C# official”
Completed with input: {
  "query": "site:github.com/langchain-ai/langgraph supported Python JavaScript no C# official"
}

已完成: *Compare EventJournal with existing systems* (1/4)

**结论**

`EventJournal` **不是基础机制上的新发明**。不可变事件、Parent ancestry、branch/ref/reflog、append-only log、optimistic concurrency、崩溃恢复都已有成熟先例。

但目前也没有一个现成系统能完整替代 Atelia 的组合：

- 不可变 raw experience。
- Git 式替代未来与 branch-aware lineage。
- 带 provenance 的派生 Artifact。
- 精确保存实际 completion request。
- 可恢复 tool-loop 与 `uncertain` 副作用状态。
- 可重建 retrieval read model。

所以更准确的定位是：

> **EventJournal 的领域合同有独立价值，但尚无证据证明需要自研存储引擎。**

**Prior Art 对照**

| 系统 | 已解决的部分 | 主要缺口 | 建议 |
|---|---|---|---|
| Git/libgit2 | Parent、refs、HEAD、reflog、object-before-ref、分叉 | 高频大 payload、会话查询和永久 reflog 不自然 | 语义上的主要老师 |
| SQLite | embedded、ACID、崩溃恢复、BLOB、CAS、索引、FTS5 | 不原生理解 Parent/branch | 最强 MVP substrate 候选 |
| Kurrent/EventStoreDB | event stream、expected revision、幂等 EventId、projection | 服务化；线性 stream；无任意 Parent/ref/reflog | event-store 行为对照 |
| Kafka/JetStream/Chronicle/Hypercore | 成熟 segmented append log | 基本没有版本树和 ref 控制面 | 不适合作为完整替代 |
| Temporal | durable replay、Activity、retry、idempotency、人工等待；有稳定 .NET SDK | 需要独立服务；不负责长期语义记忆 | 唯一值得直接评估的 .NET workflow runtime |
| Restate | 对 LLM/tool step 的 durable journal 很贴近目标 | 当前官方 SDK 无 .NET | 借鉴，不宜直接采用 |
| LangGraph | checkpoint、fork/time travel、thread/store 分层 | 主要是 Python/TS；checkpoint 不是永久 raw truth | 借鉴执行模型 |
| Letta/MemGPT | memory block、archival memory、conversation fork、compaction | 偏可变 Agent state，不是审计级 event journal | 借鉴 Context/Memory 模型 |
| Generative Agents | experience stream、reflection、retrieval、planning | 研究架构，缺少 durable execution | 与五层架构最接近的概念先例 |
| Graphiti/Zep | episode provenance、bitemporal facts、失效处理、混合召回 | 是派生知识层，不是原始历史 | 候选 retrieval read model |
| Mem0 | salient memory extraction、冲突更新、vector+graph | 有损派生记忆，不能充当 SSOT | 可插拔 memory producer |

因此，“为超长 LLM Session 设计的存储模型”确实已经存在很多，但通常只覆盖以下一部分：

1. Conversation history 与 compaction，例如 OpenAI Agents Sessions。
2. Working/core/archival memory，例如 Letta。
3. Reflection 与动态召回，例如 Generative Agents。
4. Temporal knowledge graph，例如 Graphiti。
5. Execution checkpoints，例如 LangGraph。
6. Durable workflow，例如 Temporal/Restate。

**没有发现一个系统把这六部分统一成当前 event-sourced-session-architecture-roadmap.md。**

**最重要的设计调整**

当前 event-journal-requirements-and-design.md过早锁定了：

```text
EventAddress = SegmentNumber + SizedPtr
EventJournal = RBF-based store
```

这让领域身份、物理地址和存储后端变成了同一件事。建议解锁为：

- 公开层：opaque、稳定的 `EventId` / `EventAddress`。
- RBF backend：内部编码为 `SegmentNumber + SizedPtr`。
- SQLite backend：内部编码为稳定主键。
- ChatSession 不观察物理坐标。

同样，[payload → meta → ref 的两阶段 flush 协议](docs/EventJournal/event-journal-requirements-and-design.md#L244)应降为 **RBF backend 的恢复协议**，而不是 EventJournal 的普遍语义。SQLite 可以在一个事务中完成：

```text
insert payload chunks
insert event
CAS update branch
insert reflog
commit
```

这样大量 orphan 和 flush 崩溃窗口会直接消失。领域合同只需规定成功、CAS 冲突和可见性，不必强迫所有后端复制 RBF 的物理行为。

**推荐路线**

在现有 `EJ-0` 前增加 `EJ-A0: Backend Spike`：

1. 固定 backend-neutral EventJournal 行为测试。
2. 做最小 SQLite backend。
3. 将现有 StateJournal/RBF 路径作为对照 backend。
4. 共用 branch、CAS、reflog、reopen 和 crash matrix。
5. 用真实 payload 分布测试吞吐、空间、流式读写和启动时间。
6. 只有 SQLite 被明确淘汰后，才继续完整建设 RBF EventJournal。

SQLite 的淘汰条件应是可测量的，例如：

- 大 payload 流式写入或读取无法满足需求。
- 文件增长、写放大或吞吐明显不可接受。
- 必须独立归档、搬移、修复 segment。
- 必须依靠 `SizedPtr` 进行零额外索引的物理随机读取。
- fault-injection 结果不满足耐久性要求。

不要采用“SQLite metadata + RBF payload”的混合方案作为起点。它会重新引入跨文件事务，恰好把已经交给 SQLite 的难题再发明一遍。

**哪些仍值得自研**

ChatSession 路线图的主体不需要推翻。真正属于 Atelia 的部分是：

- versioned raw event schema 与 deterministic reducer。
- branch-aware artifact cursor。
- Artifact provenance 和 coherent `ArtifactSet`。
- exact canonical request 作为恢复 SSOT。
- ContextPlan 的可解释选择与精确 manifest。
- completion attempt identity。
- `tool-result` / `tool-uncertain` 状态机。
- raw、artifact、retrieval、execution 之间清晰的权威边界。

Temporal 是执行层最值得做 spike 的候选，因为它有正式 .NET SDK；Restate 和 LangGraph目前更适合作为设计参考。Graphiti、Mem0、Letta可以作为实验性 read model 或 producer 接入，但都不应成为 raw history 的第二事实源。

最终建议是：**保留 EventJournal 抽象，暂停把它等同于 RBF 存储引擎；默认验证 SQLite，借鉴 Git，单独评估 Temporal，并继续自研 Atelia 特有的 provenance、ContextPlan 和可恢复 Agent 语义。** 本轮只做了研究与反方审阅，没有修改文件。
