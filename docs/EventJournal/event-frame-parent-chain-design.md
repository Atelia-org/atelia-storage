# EventFrame Parent Chain 设计基线

> **状态**：Design Baseline / 待拆分为 Spec
> **日期**：2026-07-23
> **依赖**：[EventJournal 功能需求与粗粒度设计基线](event-journal-requirements-and-design.md)、[RbfSegmentStore 设计基线](rbf-segment-store-design.md)、[RBF Layer Interface Contract](../Rbf/rbf-interface.md)
> **Payload Codec 扩展**：[EventJournal Payload Codec 设计方案](event-payload-codec-design.md) 建议将 EventFrame header 升级到 v2，以表达 logical payload length 与 payload codec id；stored payload length 从 RBF frame 派生，不写入 TailMeta。
> **实现现状**：代码已按 payload codec 方案落地 EventFrame header v2。本文 §3 的 v1 fixed prefix 仅作为 parent-chain 设计历史基线保留；新实现与后续 wire format 以 [EventJournal Payload Codec 设计方案](event-payload-codec-design.md) 为准，不应再照 v1 的 `Flags/HasParent/u64 PayloadLength` 实作。

## 1. 文档定位

本文设计建立在 `RbfSegmentStore` 之上的 `EventFrame` header codec 与父链遍历层。它只回答：给定一个 `EventAddress` / `EventFrame`，如何读取 header、校验父指针、沿 Parent 遍历历史，以及如何追加一个指向既有 Parent 的新 `EventFrame`。

本文不设计：

- branch、refs、HEAD、reflog 或 compare-and-swap ref update。
- payload schema、payload 索引或领域对象 materialization。
- v2 multi-frame payload 的具体 schema。
- 跨线程、跨进程 writer 协议。

换句话说，本层只形成“不可变 EventFrame 节点 + 单 Parent 链”的能力。初始 head 从哪里来，属于 refs/branch 或上层调用者的职责。

## 2. 一句话模型

`EventFrame` 是一个 RBF frame：应用 payload inline 存在 `PayloadAndMeta` 前段，EventJournal header 以私有二进制格式存在 RBF `TailMeta`。Parent chain traversal 只依赖 `EventAddress`、`RbfSegmentStore.OpenReader()` 和 `EventFrameHeaderCodec`。

```text
EventAddress
  -> RbfSegmentStore.OpenReader(SegmentNumber)
  -> IRbfFile.ReadPooledTailMeta(Ticket) 或 ReadPooledFrame(Ticket)
  -> EventFrameHeaderCodec.Decode(...)
  -> Parent: EventAddress?
```

## 3. TailMeta Wire Format 决策

MVP 使用私有固定二进制 header，而不是 JSON 或 CBOR。

理由：

- header 字段少、类型固定、读取频率高，适合零分配或低分配二进制 decode。
- Parent walk 的热路径只需要读取 TailMeta，不应为通用文档格式付出字段名、动态类型和解析分支成本。
- 可读性放到工具层解决：提供 debug dump / JSON view，而不是让 JSON 成为持久格式。
- CBOR 作为通用二进制格式仍需要 canonical subset、key 策略和未知字段策略；本层 fixed prefix 更简单。

### 3.1 Fixed Prefix v1

所有整数使用 little-endian。v1 header 固定 64 bytes，`HeaderLength` 必须为 `64`。未来若需要扩展，可在 `HeaderLength > 64` 时追加 extension area；MVP reader MUST 拒绝未知 `FormatVersion`，并可跳过或拒绝未来版本的 extension area，具体策略留给 v2 Spec。

| Offset | Size | 字段 | 类型 | 说明 |
|------:|-----:|:-----|:-----|:-----|
| 0 | 4 | `Magic` | `u32` | 固定 magic，建议字节为 `EJH1` |
| 4 | 2 | `FormatVersion` | `u16` | v1 固定为 `1` |
| 6 | 2 | `HeaderLength` | `u16` | v1 固定为 `64` |
| 8 | 4 | `Flags` | `u32` | bit flags，见 §3.2 |
| 12 | 8 | `SequenceNumber` | `u64` | store-local 单调序号 |
| 20 | 8 | `UtcUnixTimeMilliseconds` | `i64` | UTC Unix time milliseconds |
| 28 | 4 | `OpaqueEventKind` | `u32` | 应用定义的 opaque event kind |
| 32 | 4 | `Hint` | `u32` | 与 `EventAddress.Hint.Packed` 一致或可确定推导 |
| 36 | 8 | `PayloadLength` | `u64` | 应用 payload bytes 长度 |
| 44 | 16 | `Parent` | `EventAddress` | null parent 使用全零地址 |
| 60 | 4 | `HeaderCrc32C` | `u32` | 覆盖 offset `[0, 60)` 的 Castagnoli CRC32C |

字段说明：

- `SequenceNumber` 是 store-local append order，主要用于诊断、排序辅助和后续索引构建。Parent 正确性不依赖它；若 Parent header 可读，新 Event 的 `SequenceNumber` SHOULD 大于 Parent 的 `SequenceNumber`。
- `UtcUnixTimeMilliseconds` 是记录时间，不是因果顺序权威。因果顺序以 Parent 链和物理提交顺序为准。
- `OpaqueEventKind` 由上层定义 bit schema；EventJournal 可保存、返回和用于免 I/O 过滤，但不解释其业务含义。
- `Hint` 是 canonical address hint 的持久化镜像。若 `EventAddress.Hint` 与 header `Hint` 不一致，reader MUST 拒绝该地址。
- `PayloadLength` 必须等于完整 `EventFrame` 中应用 payload 的长度，不能包括 TailMeta。
- `Parent` 为全零 `EventAddress` 表示 root Event。非零 Parent 必须通过地址合法性校验。

### 3.2 Flags

v1 `Flags` 初始定义：

| Bit | 名称 | 说明 |
|----:|:-----|:-----|
| 0 | `HasParent` | 为 `0` 时 `Parent` MUST 为全零；为 `1` 时 `Parent` MUST 为有效非零地址 |
| 1..31 | reserved | v1 MUST 写 `0`；reader MUST 拒绝未知置位 |

`HasParent` 与全零 Parent 双重表达是有意的：它让 root 判断便宜，同时让半空地址和 flag/address 不一致变成可检测错误。

### 3.3 Header CRC

`HeaderCrc32C` 是 TailMeta 内部自校验，不替代 RBF 完整 frame 校验。它的作用是让 L2 preview 读取 TailMeta 时更早发现 header 损坏或误解码。

Decode 顺序：

1. 校验 TailMeta length 至少为 fixed prefix 长度，v1 必须恰好为 64 bytes。
2. 校验 `Magic`、`FormatVersion`、`HeaderLength`。
3. 计算 offset `[0, 60)` 的 CRC32C，并与 `HeaderCrc32C` 比较。
4. 校验 reserved flags、Parent null 规则、payload length 范围、hint 一致性所需字段。

完整 Event 的最终权威仍是 `IRbfFile.ReadFrame` / `ReadPooledFrame` 通过完整 RBF payload CRC 后得到的 `EventFrame`。`ReadTailMeta` / `ReadPooledTailMeta` 得到的是 preview header。

## 4. 候选类型与 Codec

候选内部类型：

```csharp
public readonly record struct EventFrameHeader(
    ulong SequenceNumber,
    long UtcUnixTimeMilliseconds,
    uint OpaqueEventKind,
    AddressHint Hint,
    ulong PayloadLength,
    EventAddress? Parent
);
```

Codec 形态：

```csharp
public static class EventFrameHeaderCodec {
    public const int FixedLength = 64;

    public static void Encode(
        in EventFrameHeader header,
        Span<byte> destination
    );

    public static AteliaResult<EventFrameHeader> Decode(
        ReadOnlySpan<byte> tailMeta
    );
}
```

业务代码不得手写 byte offset。所有 TailMeta encode/decode、CRC、flags 和地址合法性检查都收束在 codec 层。

调试工具 MAY 提供：

```csharp
string ToDebugJson(EventFrameHeader header);
```

该 JSON 只用于日志、测试 snapshot 或人工查看，不是持久 wire format。

## 5. Append EventFrame

本层提供 raw append，不负责推进 branch/ref：

```text
AppendEventFrame(parent, payload, opaqueEventKind, hint, timestamp) -> EventAddress
```

最小流程：

1. 若 `parent` 非 null，完整读取并校验 Parent 指向的 `EventFrame`。
2. 分配 `SequenceNumber`，它 SHOULD 为当前 store 已知最大序号加一。
3. 构造 `EventFrameHeader`，其中 `PayloadLength` 等于 payload bytes 长度。
4. 通过 `RbfSegmentStore.OpenActiveWriter()` 借出 writer lease。
5. 调用 RBF `Append(EventFrameTag, payload, tailMeta)`。
6. 对 `EventFrame` 所在 segment 执行 `DurableFlush`。
7. 返回 `EventAddress(ticket, segmentNumber, hint)`。

边界：

- payload + TailMeta 超过 RBF 单 frame 上限时返回可预见错误。
- `SequenceNumber` 不作为强一致事务号；崩溃后若出现 ref 未指向的 orphan Event，序号可能有空洞。
- 没有 refs/branch 层时，`SequenceNumber` allocator 可在打开本层时扫描现有 `EventFrame` headers，取最大有效 `SequenceNumber + 1` 作为下一值。
- timestamp 由调用方或 store clock 提供；EventJournal 不用 timestamp 决定 Parent 合法性。

## 6. Read Header 与 Read Event

推荐分成 preview 与 checked 两档：

```text
ReadEventHeaderPreview(address) -> EventFrameHeader
ReadEventHeaderChecked(address) -> EventFrameHeader
ReadEvent(address) -> EventFrameHeader + payload reader/frame
```

`ReadEventHeaderPreview`：

1. `OpenReader(address.SegmentNumber)`。
2. `ReadPooledTailMeta(address.Ticket)`。
3. `EventFrameHeaderCodec.Decode(tailMeta)`。
4. 校验 header `Hint` 与 address hint 一致。

该路径只具备 TailMeta L2 preview 信任，适合 Parent walk、过滤和预取。

`ReadEventHeaderChecked` / `ReadEvent`：

1. `OpenReader(address.SegmentNumber)`。
2. `ReadPooledFrame(address.Ticket)`。
3. 校验 `FrameTag == EventFrameTag`。
4. 从完整 frame 的 TailMeta decode header。
5. 校验 payload length 与 header `PayloadLength` 一致。
6. 校验 header `Hint` 与 address hint 一致。

该路径完成 RBF payload CRC 校验，可用于接受外部地址、推进 ref 前验证和返回 payload。

## 7. Parent Chain Traversal

### 7.1 Reverse Walk

`EnumerateAncestors(head)` 产出：

```text
head, head.parent, head.parent.parent, ..., root
```

默认实现可使用 preview header，每步只读取当前 EventFrame 的 TailMeta。需要强一致模式时，每步改用 checked header。

遍历必须支持：

- cancellation。
- 可选 `maxDepth`。
- 诊断模式下的 repeated-address / cycle 防御。
- 遇到 malformed parent、missing segment、RBF corruption 或 header CRC mismatch 时停止并返回明确错误。

### 7.2 Chronological Walk

`EnumerateChronological(head)` 可作为派生算法：

1. reverse walk 收集 `EventAddress` 到临时容器。
2. 反向产出 root 到 head。

默认临时容器是内存 `List<EventAddress>`。API 文档必须明确额外空间为 $O(n)$。

### 7.3 范围查询

本层可基于 Parent walk 提供：

- `IsAncestor(ancestor, descendant)`。
- `EnumerateAfter(ancestorExclusive, headInclusive)`。

这些能力不需要 branch/ref。它们只比较和遍历 EventAddress。

## 8. 与 Branch/Refs 的边界

本层不保存“当前 head”。调用方必须显式传入 head address。

后续 refs/branch 层可以复用本层能力：

- ref target 写入前，用 checked read 验证目标 EventFrame。
- branch history 展示时，用 parent traversal 生成 commit history。
- reset / detached HEAD 不改变任何 EventFrame，只改变上层 ref。

## 9. 验收标准

- fixed binary TailMeta v1 encode/decode 往返一致，长度固定 64 bytes。
- `HeaderCrc32C` 能发现单 bit header 损坏。
- root Event 的 `HasParent=0` 且 Parent 全零；半空 Parent 编码被拒绝。
- `EventAddress.Hint` 与 header `Hint` 不一致时读取失败。
- append 后可通过 returned `EventAddress` 完整读取 payload 与 header。
- Parent 必须先存在；不存在或损坏的 Parent 不能被提交为新 Event 的 Parent。
- reverse walk 产出 head 到 root；chronological walk 产出 root 到 head。
- malformed Parent 不导致无限循环。
- segment 轮转后跨 segment Parent chain 仍可遍历。
- preview header 与 checked header 的信任边界在 API 文档中明确区分。
