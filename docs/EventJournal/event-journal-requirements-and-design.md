# EventJournal 功能需求与粗粒度设计基线

> **状态**：Design Baseline / 待拆分为 Decision 与 Spec
> **日期**：2026-07-22
> **依赖**：[RBF Layer Interface Contract](../Rbf/rbf-interface.md)
> **上层路线图**：[ChatSession 事件源与长期上下文架构路线图](../ChatSession/event-sourced-session-architecture-roadmap.md)

## 1. 文档定位

本文固定 `EventJournal` 的职责边界、核心不变量和推荐施工顺序，使后续会话可以分别推进线格式、API、恢复协议和实现，而不必重新讨论系统属于哪一层。

本文还不是最终 wire-format SSOT。后续应按 [Atelia 规范约定](../spec-conventions.md) 拆分为：

- Decision-Layer：不可轻易推翻的边界与取舍。
- Spec-Layer：精确 frame schema、API、失败语义和恢复协议。
- Derived-Layer：示例、性能分析和操作指南。

本文中的 `MUST` 只用于已经足够稳定的需求。具体字段宽度、tag 值、目录分桶大小等仍属于后续设计任务。

## 2. 一句话模型

`EventJournal` 是建立在 RBF 之上的、payload 语义无关的 append-only 父链事件库：

- 每个 Event 是一个不可变 commit node。
- 每个 Event 最多指向一个 Parent，因此自然形成可分叉的版本树。
- MVP 中每个 Event 由单个 RBF `EventFrame` 表达，应用 payload inline 存在该 frame 的 `PayloadAndMeta` 前段，EventJournal header 存在该 frame 的 RBF `TailMeta`。
- Named branch / ref 只保存对 Event 的引用，不改变 Event 本身。
- Event、branch 与历史遍历均以跨 segment 稳定地址为基础。

`EventJournal` 不知道 payload 表示聊天消息、工具调用、游戏状态还是其他领域对象。领域 schema、序列化 codec、索引和查询语义全部属于上层。

## 3. 目标与非目标

### 3.1 目标

1. 保存不可变、append-only、可随机定位的二进制 Event。
2. 自动管理多 segment RBF 文件、目录分桶、轮转和打开文件池。
3. 支持沿 Parent 从任意 Event 逆序遍历，并通过临时容器提供顺序遍历。
4. MVP 支持单 RBF Frame 上限以内的逻辑 payload，并为未来 v2 多 frame payload 保留扩展路径。
5. 提供 named branch、ref move history 和 commit parent history；MVP 不提供 Git 风格全局 `HEAD`。
6. 在进程崩溃、active segment 尾部撕裂和 ref 尾部撕裂后恢复到最后一个完整状态。
7. 允许上层仅持有稳定地址，不感知 segment 路径和文件句柄生命周期。

### 3.2 非目标

`EventJournal` 不负责：

- 定义或解释应用 payload schema。
- 对 payload 建立全文、向量、图或字段索引。
- materialize 领域对象或维护可变对象图。
- 自动合并两个 Parent；MVP Event 只有零个或一个 Parent。
- 提供跨进程多 writer 事务。
- 在 MVP 中实现 GC、压缩、repack 或历史删除。
- 为外部副作用提供 exactly-once 语义。

## 4. 已锁定的核心边界

### decision [S-EJ-PAYLOAD-OPAQUE] Payload 对 EventJournal 不透明

`EventJournal` MUST 把 Event 的应用 payload 视为不透明字节序列。它只能解释自己的结构元数据，例如 Parent、payload 长度、hint 和可选 payload 校验字段；它 MUST NOT 根据应用 payload 的 kind、版本或字段改变存储行为。

### decision [S-EJ-EVENT-IMMUTABLE] Event 不可变

Event 一旦提交成功，其 Parent、EventJournal header 和 payload bytes MUST NOT 原地修改。修订内容只能追加新 Event，并让新 Event 指向旧 Event。

### decision [S-EJ-SINGLE-PARENT] MVP 使用单 Parent

每个 Event MUST 有零个或一个 Parent。零 Parent 表示 root Event；一个 Parent 表示线性继承。多个 branch 可指向同一祖先并独立前进，从而形成版本树。

多 Parent merge commit 不进入 MVP。若未来引入，它必须是显式格式升级，不能把额外 Parent 偷塞进不透明 payload 后宣称底层已支持 merge。

### decision [S-EJ-ADDRESS-STABLE] 地址不受文件句柄与目录迁移影响

Event 的公开身份 MUST 由 `EventAddress` 表达。调用方不能持久化绝对路径、`FileStream`、`IRbfFile` 实例或进程内 pool handle 作为 Event 身份。

### decision [S-EJ-EVENTFRAME-COMMITS-EVENT] EventFrame 是 Event 可见性边界

MVP 中，一个 Event 由一个完整、有效且已提交的 RBF `EventFrame` 组成。`EventFrame` 的 RBF `TailMeta` 保存 EventJournal header，`PayloadAndMeta` 中除 TailMeta 之外的前段保存应用 payload。没有完整 `EventFrame` 就没有可见 Event；被 Auto-Abort 或 active-tail recovery 丢弃的未完成 frame 不是半个 Event。

## 5. 地址模型

### 5.1 FrameAddress 与 EventAddress

推荐在类型系统中区分物理 frame 地址与 Event 身份：

```csharp
public readonly record struct AddressHint(uint Packed);

public readonly record struct FrameAddress(
    SizedPtr Ticket,
    uint SegmentNumber
);

public readonly record struct EventAddress(
    SizedPtr Ticket,
    uint SegmentNumber,
    AddressHint Hint
) {
    public FrameAddress FrameAddress => new(Ticket, SegmentNumber);
}
```

三者形成前缀扩展关系：`SizedPtr` 是单 segment 内的 frame ticket；`FrameAddress` 在其后追加 `SegmentNumber`，成为跨 segment frame 地址；`EventAddress` 再追加 `AddressHint`，成为带预过滤 hint 的 Event 身份。

`FrameAddress` 与 `EventAddress` 共享相同的物理坐标前缀，但语义不同：

- `FrameAddress` 可以指向任意 EventJournal 内部 RBF frame。
- `EventAddress.FrameAddress` 必须指向一个有效 `EventFrame`，是 Event 的稳定身份。
- Parent、branch head、reflog old/new head 使用 `EventAddress`。
- 未来 v2 multi-frame payload 的 part list 或 descriptor 可使用 `FrameAddress`。

字段顺序采用 `Ticket -> SegmentNumber -> Hint` 是有意的：地址从“文件内寻址”逐层扩展到“跨文件寻址”和“带 hint 的 Event 身份”。这与现有 `CommitAddress` 的经验相近，但 `EventJournal` 不依赖 `StateJournal.CommitAddress`，也不继承它的业务语义。

#### 5.1.1 Address Hint 语义

`AddressHint` 是 32-bit smart-pointer hint，例如应用 event kind、粗粒度时间桶或染色标记。它属于 `EventAddress`，不属于通用 `FrameAddress`；未来 v2 的 payload part address list 默认保持无 hint，以避免大规模地址表无谓膨胀。

Hint 受以下边界约束：

1. `Hint` 只能作为免 I/O 的分派、过滤或预取提示；仅凭未经验证的外部地址，不能据此作存储正确性或安全决策。
2. 若 `Hint` 进入 `EventAddress` 身份，它必须在 Event 创建时固定，并由目标 `EventFrame` 的 EventJournal header 保存相同值或提供可确定推导的值。reader 解引用后必须校验两者一致，避免同一物理位置出现多个互相矛盾的地址。
3. 任意可变、与目标内容无关的用户标签不进入 canonical address；这类状态应使用上层 wrapper 或独立索引表达。
4. EventJournal 只保存和校验 hint 的一致性，不解释应用 bit schema，以维持 `[S-EJ-PAYLOAD-OPAQUE]`。
5. `Packed = 0` 应保留为“无 hint”；其余 bit 的命名空间和版本策略由后续 Spec 决定。
6. 不应为了获得更多 hint bits 而过早缩减 `SegmentNumber`。单调编号可能因按时间轮转、测试、导入或保留 tombstone 而比物理 segment 数增长更快。

#### 5.1.2 地址 wire format

MVP 地址持久化使用固定宽度 little-endian 编码，并与 CLR struct 布局解耦。字段顺序固定为前缀扩展形式：

| 类型 | 字节数 | Wire 字段顺序 |
|:-----|------:|:--------------|
| `SizedPtr` | 8 | `Ticket` |
| `FrameAddress` | 12 | `Ticket`, `SegmentNumber` |
| `EventAddress` | 16 | `Ticket`, `SegmentNumber`, `Hint.Packed` |

该前缀关系是 wire codec 的显式承诺：`EventAddress` 的前 12 bytes 可按 `FrameAddress` 解码，`FrameAddress` 的前 8 bytes 可按 `SizedPtr` 解码。但普通业务代码 MUST 使用显式属性、构造函数或 codec 方法完成投影，不得依赖 unsafe reinterpret cast、结构体截断或当前 CLR 内存布局。

`FrameAddress` 保持 12 bytes，使未来 v2 的大规模 payload part address list 不为 Event-level hint 付费。`EventAddress` 使用 16 bytes，额外 4 bytes 只出现在 Parent、branch head、reflog old/new head 等 Event 身份位置。

### 5.2 Null 与合法性

- `SegmentNumber` 从 `1` 开始，`0` 保留给 null/未设置编码。
- 有效 `EventAddress` 的 ticket 必须非 null，且必须能在对应 segment 中完整读取为 `EventFrame`。
- API 层使用 `EventAddress?` 表达无 Parent 或 unborn branch，避免把可构造的无效地址传播到业务代码。
- 持久化格式若使用 `{ ticket=0, segment=0 }` 表示 null，reader 必须拒绝“仅一侧为零”的半空地址；`EventAddress` 的 null 编码还要求 `hint=0`。

### 5.3 EventAddress 同时充当 EventId

MVP 不另造随机 UUID。`EventAddress` 已经是 store 内唯一、稳定、可解引用的 EventId。若未来需要跨 store 引用，应显式组合 `StoreId + EventAddress`，而不是扩大当前地址的隐含作用域。

## 6. RbfSegmentStore

### 6.1 定位

`RbfSegmentStore` 是 EventJournal 之下的轻量基础层，完整职责边界与 API 设计见 [RbfSegmentStore 设计基线](rbf-segment-store-design.md)。它不封装 RBF 的 frame append/read API，只负责 RBF segment 文件的生命周期管理，并通过 lease 向上层提供已打开的 `IRbfFile`。

上层写入时现用现借 active writer lease，直接调用 RBF 的 append API，并把返回的 `SizedPtr Ticket` 与 lease 中的 `SegmentNumber` 组合成 `FrameAddress`。上层读取时按 `FrameAddress.SegmentNumber` 借出 reader lease，再用 `FrameAddress.Ticket` 调用 RBF 读取 API。调用方不得在 lease dispose 后继续使用其中的 `IRbfFile`。

### 6.2 Segment 路径布局

路径布局（hex bit-split 公式、bucket 目录、文件命名规则、`SegmentNumber=0` 保留、无 manifest 策略）详见 [RbfSegmentStore §3 Segment 路径布局](rbf-segment-store-design.md#3-segment-路径布局)。

路径始终是可重建的派生信息，不写入 `EventAddress`。该布局不需要运行时维护 segment range table——根据 `SegmentNumber` 可直接计算目标目录与文件路径。

### 6.3 Segment 生命周期

Segment 编号分配、轮转触发机制（size-only rotation）和 active/closed segment 状态管理详见 [RbfSegmentStore §4 打开与发现](rbf-segment-store-design.md#4-打开与发现) 和 [§6 Active Writer 与轮转](rbf-segment-store-design.md#6-active-writer-与轮转)。

与 EventJournal 相关的关键点：

- 任意单个 RBF frame 不跨 segment；每次 append 只使用一个 active segment writer lease，轮转只发生在两次 append 之间。
- MVP 中一个逻辑 Event 不跨 segment：单个 `EventFrame` 由一次 append 写入一个 active segment。未来 v2 multi-frame payload MAY 允许不同 payload parts 和最终 descriptor 位于不同 segment。
- active segment 是唯一允许追加和尾部恢复性截断的 data segment。

#### 6.3.1 Segment 阈值选择原则

主流 64-bit 部署中的 ext4、XFS 和 OpenZFS 均能承载 1 TiB 量级单文件；因此 segment threshold 不应以文件系统“能否寻址”为主要依据。它首先是 EventJournal 的恢复、校验、备份、迁移和故障隔离单元。

阈值设计至少考虑：

1. **恢复与 scrub 预算**：限制单次完整校验、隔离损坏或重建 segment inventory 时需要处理的数据量。正常 active-tail 恢复仍应由最大未完成 frame 大小约束，不能依赖扫描整个 segment。
2. **备份与复制增量**：closed segment 不再变化，可被一次复制、校验和归档；active segment 越大，长期可变的备份对象也越大。
3. **故障域**：RBF CRC 可定位 frame 级损坏，但 inode、extent tree、介质区域或操作失误仍可能影响整个文件。segment 提供应用层隔离边界。
4. **文件与句柄数量**：segment 太小会放大目录项、目录扫描、路径校验、打开文件池、备份对象和 GC/repack 调度成本。目录分桶只能缓解目录查找，不能消除对象数量成本。
5. **frame 几何**：soft threshold 应显著大于常用 EventFrame 大小，并明确单个最大 frame 是否可越过阈值。轮转判断应保证 frame 不跨 segment。
6. **分配与碎片**：长时间顺序追加通常能被 extent/delayed-allocation 良好处理；接近满盘、频繁快照、CoW 和混合写入可能增加碎片与元数据成本，需要按目标文件系统实测。
7. **运维时间边界**：低吞吐 Agent 可能多年达不到 size threshold；MVP 接受这种状态，不为此增加 max-age rotation。

首轮基准可比较 16 GiB、64 GiB、256 GiB 三档，并以 64 GiB 作为实验起点；1 TiB 可作为压力测试档，不宜直接作为默认值。最终默认值必须由恢复扫描、随机读、持续 append、durable flush、备份和 segment 文件池基准共同决定。

### 6.4 文件池

Reader lease、historical reader pool 的 lease 保护、LRU 淘汰、异常淘汰和 pool key（`SegmentNumber`）策略详见 [RbfSegmentStore §7 Reader Lease 与 Historical Pool](rbf-segment-store-design.md#7-reader-lease-与-historical-pool)。

上层使用方面的关键约束：active writer lease 不长期持有（每次写入现用现借）；一次读取持有 lease 防止文件被 pool 关闭。MVP 采用单 writer、多 reader 进程内模型；跨进程 writer 不在范围内。

## 7. Event 物理组成

### 7.1 MVP 使用单个 EventFrame

`EventJournal` 在 RBF 的 `FrameTag` 空间中至少定义 `EventFrame`。MVP 中每个 Event 恰好对应一个完整 RBF frame：

```text
RBF EventFrame:
    FrameTag = EventFrame
    PayloadAndMeta 前段 = 应用 payload bytes
    TailMeta = EventJournal header
```

`EventAddress.FrameAddress` 指向这个 `EventFrame`。应用 payload 与 EventJournal header 位于同一个 RBF frame 内，因此一次完整 `ReadFrame` / `ReadPooledFrame` 可同时校验 frame tag、payload bytes、TailMeta 和 RBF payload CRC。

选择 single-frame inline payload 作为 MVP 的原因：

- LLM tool-loop 状态机步进、模型输出、工具返回等常见事件通常远小于单 RBF frame 上限。
- 一个 Event 只写一个 RBF frame，避免为小 payload 额外写 sidecar payload / descriptor frame、地址表和第二次随机读。
- Event 的可见性边界与 RBF frame 的提交边界一致，崩溃恢复与 orphan 处理更简单。
- RBF `TailMeta` 已能廉价读取 header，适合 Parent walk、过滤、hint 校验和预取。

### 7.2 EventJournal TailMeta Header

`EventFrame` TailMeta header 的 fixed binary v1 格式、字段 offset、CRC、codec 和父链遍历 API 详见 [EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)。首轮 wire spec 应在 `EventFrame` 的 RBF `TailMeta` 中至少表达：

| 字段 | 含义 |
|:-----|:-----|
| `FormatVersion` | EventJournal EventFrame schema 版本 |
| `SequenceNumber` | store-local 单调序号 |
| `UtcUnixTimeMilliseconds` | UTC Unix time milliseconds 记录时间 |
| `OpaqueEventKind` | 应用定义的 opaque event kind |
| `Parent` | 可空 `EventAddress` |
| `Hint` | 与 `EventAddress.Hint` 一致或可确定推导的 hint |
| `PayloadLength` | 应用 payload bytes 长度 |

可选但尚未锁定的字段：

- payload digest，用于在完整读取 frame 后，在 RBF CRC 之外提供应用层整体校验或去重基础。
- payload codec id；若 EventJournal 提供透明写入时压缩，codec id 应下沉到 EventFrame TailMeta，而不是混入应用 payload envelope。stored payload length 从 RBF frame 派生，不写入 TailMeta，避免双真源。详见 [EventJournal Payload Codec 设计方案](event-payload-codec-design.md)。

RBF `TailMeta` 的读取是 L2 preview：`ReadTailMeta` / `ReadPooledTailMeta` 只校验 trailer codeword，不校验完整 `PayloadCrc32C`。因此 TailMeta header 可以用于便宜路由和预览，但在接受外部地址、返回 payload、推进 ref 或执行强一致校验时，reader MUST 使用完整 `ReadFrame` / `ReadPooledFrame` 校验目标 `EventFrame`。

TailMeta 内部可以采用轻量自校验，以降低误解码和损坏传播风险。候选方式包括：

- 对 JSON / CBOR / 自定义二进制 header 做结构性验证、必填字段验证和版本验证。
- 在 TailMeta header 内放置 header-level CRC，以便在不读取 payload 的情况下更早发现 header 损坏。
- 若采用 JSON 作为早期调试格式，必须仍保留明确 `FormatVersion`，并在稳定后迁移到更紧凑的二进制 schema。

首轮 Spec 必须明确 TailMeta header 的自校验策略；它可以只是结构性验证，也可以包含显式 header-level CRC。

这些自校验只提升 TailMeta preview 的可靠性，不改变最终信任边界：完整 Event 的权威仍是通过 RBF 完整 frame 校验后的 `EventFrame`。

### 7.3 Payload 容量与 v2 多 Frame 扩展

MVP 的逻辑 Event 大小受单个 RBF frame 的 `MaxPayloadAndMetaLength` 限制。首轮 Spec 必须由 RBF 的公开容量契约推导并公开：

- 最大 inline payload 长度。
- TailMeta header 最大长度、RBF `MaxTailMetaLength` 与 payload 长度之间的扣减关系。
- payload length、TailMeta length 和总长度计算的 checked overflow 规则。

MVP 不支持 multi-frame Event。超出单 frame 上限的 payload 应返回明确的可预见错误，而不是自动切换到另一种物理格式。

未来 v2 可以增加 multi-frame payload schema，但它必须是显式格式升级。可选路线包括：

- 在 `EventFrame` 的 TailMeta 中保存有序 `FrameAddress` list，payload bytes 存在多个 payload part frame 中。
- 使用 payload chain 或 descriptor frame 表达流式大 payload。
- 引入独立 large-object store，并让 EventFrame 保存外部对象地址与 digest。

v2 设计必须重新定义 part 顺序、重复地址、part tag、整体长度、整体 digest、durable 顺序和崩溃恢复矩阵。MVP 不同时维护 inline 与 multi-frame 两套提交路径。

### 7.4 Event 提交协议

安全基线是：

1. 验证 Parent 已存在且是完整 Event。
2. 构造应用 payload 与 EventJournal TailMeta header。
3. 通过 active writer lease 写入单个 `EventFrame`。
4. 对 `EventFrame` 所在 segment 执行 `DurableFlush`。
5. 只有此后才返回成功的 `EventAddress`，或推进 branch ref。

### spec [S-EJ-PARENT-PREEXISTS] Parent 必须先存在

新 Event 的 Parent MUST 是已提交 Event，且在物理追加顺序上早于新 `EventFrame`。该约束使单 Parent 图天然无环。

### 7.5 批量优化边界

上述协议的朴素实现需要每个 EventFrame 后 durable flush。未来 MAY 提供 batch commit：写入多个 EventFrame 后一起 flush。但 batch API 必须明确哪些 Event 同时成功可见，不能为了减少 fsync 模糊单 Event 的结果语义。

## 8. 读取与遍历

### 8.1 读取 Event

`ReadEvent(EventAddress)` 的最小流程：

1. 通过 `RbfSegmentStore.OpenReader(address.SegmentNumber)` 借出 `EventFrame` 所在 segment 的 reader lease。
2. 使用 `ReadFrame` / `ReadPooledFrame` 完整校验 `EventFrame`。
3. 校验 tag、TailMeta header schema、Parent、Hint 和 payload 长度。
4. 返回结构 header，并允许调用方按需读取 payload。

推荐把“预览 header”和“完整读取 payload bytes”分开。Parent 遍历、hint 过滤和路由 MAY 使用 `ReadTailMeta` / `ReadPooledTailMeta` 的 L2 preview；任何需要接受外部地址、返回 payload 或推进 ref 的路径 MUST 完整读取并校验目标 `EventFrame`。更详细的 parent-chain API 设计见 [EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)。

### 8.2 逆序 Parent 遍历

`EnumerateAncestors(head)` 按以下顺序产出：

```text
head, head.parent, head.parent.parent, ..., root
```

其空间复杂度应为 $O(1)$，每步只需读取当前 `EventFrame` 的 TailMeta header preview；需要强一致验证时再完整读取目标 `EventFrame`。API 应支持 cancellation 和可选最大深度。

虽然 writer 通过“Parent 必须先存在”保证无环，reader 仍应在诊断模式提供 cycle / repeated-address 防御，以免损坏元数据导致无限循环。

### 8.3 顺序遍历

`EnumerateChronological(head)` 使用临时容器：

1. 沿 Parent 逆序遍历，把 `EventAddress` 压入容器。
2. 从容器尾部向前产出 root 到 head。

默认可使用内存中的 `List<EventAddress>` 或 stack。为超长历史预留调用方提供临时存储的扩展点，但不要在第一版引入复杂外部排序。

时间复杂度为 $O(n)$，额外空间为 $O(n)$。该代价必须在 API 文档中显式呈现，不能用 `IEnumerable` 外观隐藏。

### 8.4 范围与祖先关系

后续可在基础 Parent walk 上提供：

- `IsAncestor(ancestor, descendant)`。
- `EnumerateAfter(ancestorExclusive, headInclusive)`。
- `FindCommonAncestor(left, right)`。

这些都是单 Parent 树上的派生算法，不需要改变存储格式。MVP 可先实现前两项，common ancestor 等出现分支合并或比较需求时再加。

## 9. Ref Store、Branch 与 Commit History

EventJournal 的 MVP 目录结构固定为：

```text
event-journal/
    events/
        buckets/
            000000/
                00000001.rbf

    refs/
        ref-op-log.rbf
        objects/
            0123456789abcdef/
                segments/
                    00000001.rbf
                    00000002.rbf
```

其中 `events/` 是 bucketed `RbfSegmentStore` root，使用 `buckets/`；`refs/objects/<ref-id>/` 是 flat `RbfSegmentStore` root，使用 `segments/`，segment 文件名不再重复 `RefId`。

### 9.1 概念区分

- Event：不可变 commit node。
- Commit history：从 branch head 沿 Parent 形成的有效历史。
- Branch / named ref：名字到 `EventAddress?` 的可变 ref。
- Ref history / reflog：某条 named ref 的 move chain，不等于 Event Parent history。

MVP 不提供 Git 风格全局 `HEAD`。EventJournal 是 OOP API，调用方必须显式传入 ref name 或 `EventAddress`；“当前 branch”若存在，属于上层应用状态。

必须保持这两类历史的区别：Parent 描述内容继承，reflog 描述 ref 曾经如何移动。reset、rewind 或删除 branch 会产生 reflog 记录，但不会改写 Event Parent。

### 9.2 最小 Branch 能力

MVP 应提供：

- 创建 unborn branch 或从现有 Event 创建 branch。
- 查询和枚举 branch。
- 以 compare-and-swap 方式推进 branch。
- 移动或删除 branch，并记录 ref history。
- 从任意 Event 创建新 branch，形成替代未来。

branch update SHOULD 接收 `expectedOldHead`。即便 MVP 只有单 writer，CAS 也能防止调用方基于过期 head 静默覆盖更新。

### 9.3 Commit 操作

推荐把底层与便利操作分开：

```text
AppendEvent(parent, payload) -> EventAddress

Commit(branch, expectedHead, payload):
  append Event(parent = expectedHead)
  compare-and-swap branch from expectedHead to new EventAddress
```

Event durable 后、ref durable 前崩溃会留下 orphan Event，这是安全且可诊断的状态。ref 更新失败时，API 应报告 branch 未推进；是否同时返回 orphan EventAddress 供救援，由后续 Result 类型设计决定。

### spec [R-EJ-REF-AFTER-EVENT] Ref 只能指向 durable Event

branch ref 的 durable 更新 MUST 发生在目标 Event 完整持久化之后。恢复时若最新 ref update 指向无效 Event，reader MUST 回退到更早的有效 ref update，而不是接受悬空 head。

### 9.4 Ref Store 推荐形态

详细设计见 [Event Ref Store 设计基线](event-ref-store-design.md)。MVP 推荐使用 `BranchName -> RefId -> RefSegmentFile` 两级映射：

- `refs/ref-op-log.rbf` 记录 create / fork / bind-name / archive 等低频元操作，并维护 active branch name 到 active `RefId` 的绑定。
- `RefId` 是 RefOpLog 中 create/fork frame 的外部 RBF ticket（`SizedPtr.Packed`），不写入该 frame 自身 payload，是 ref instance 的稳定身份。
- create/fork 先分配 `RefId` 并初始化 ref object，随后通过 `BindName` 发布 branch name，避免 half-created branch 被 `OpenBranch` 看见。
- 公共 API 先 `OpenBranch(name) -> RefId`；后续 head move / reflog 操作使用 `RefId`，不再使用 branch name。
- 每个 ref object 使用以 `RefId` 派生的分段 RBF move chain；这些 move segment 本身就是该 ref 的 reflog。
- archive 追加 RefOpLog 记录并关闭 active name 绑定；不以物理删除文件作为唯一语义。

这样 data Event segments 永远不需要因 branch move 而修改。同名 branch 删除/归档后重建会得到新的 `RefId`。

## 10. 故障恢复

### 10.1 RBF 前置能力

EventJournal 依赖 RBF 提供"从文件尾部向前找到首个完整有效 frame，并返回其安全结束位置"的能力。该能力已通过 `RbfRecoveryScanner` 实现（`src/Rbf/RbfRecovery.cs`）：

- `RbfRecovery.OpenReadOnly(path)` 打开只读 recovery scanner。
- `scanner.ScanBackward(options)` 从尾部逆向搜索，产出 `RbfRecoveryHit` 序列；默认使用 Fence 步进策略（`RbfRecoveryBoundarySearchStrategy.Fence`），也可切换为 `RollingCrc` 策略以在尾部 Fence 损坏或缺失时救回完整 frame。
- 每个 `RbfRecoveryHit` 包含候选帧元信息（`Info`）、尾部 Fence 位置、置信等级（`Confidence`：`TrailerOnly` / `FrameBoundary` / `FullFrame`）和建议截断偏移量（`SuggestedTruncateOffset`）。
- `RbfRecovery.TruncateToSuggestedTail(path, hit)` 将文件截断到 hit 建议的逻辑尾，截断前会二次校验 HeaderFence 和尾部 Fence。

关键边界：

- Rolling CRC 命中只表示“发现候选 codeword”。
- 候选 frame 仍 MUST 校验长度、边界、tag、trailer codeword 和完整 payload CRC。
- 只有完整 RBF 校验通过的候选才能成为恢复锚点。
- CRC 偶然碰撞不得直接被当作有效 frame。

`RbfRecoveryValidationLevel` 控制校验严格度：`TrailerOnly`（仅 TrailerCodeword + Fence）、`FrameBoundary`（额外校验前置 Fence + HeadLen == TailLen，默认）、`FullFrame`（额外校验完整 PayloadCrc32C）。EventJournal 的 active-tail 恢复使用默认 `FrameBoundary` 即可——它已保证 frame 边界完整，无需为截断目的重算 payload CRC。

### 10.2 Active Segment 恢复

打开 store 时：

1. 由 `RbfSegmentStore` 扫描 layout root 并验证 segment 命名、bucket 一致性和编号连续性。
2. 对 active data segment 通过 `RbfRecovery.OpenReadOnly(path).ScanBackward()` 获取首个 `RbfRecoveryHit`（使用默认 `FrameBoundary` 校验等级和 `Fence` 搜索策略）。
3. 通过 `RbfRecovery.TruncateToSuggestedTail(path, hit)` 将 active segment 截断到最后一个完整 frame 的尾部 Fence 之后。
4. 对每个 ref RBF move chain 执行相同的有效尾恢复。
5. 重建 named ref 状态。
6. 验证每个可见 ref target 至少能完整读取 `EventFrame`；失败则沿 ref history 回退。

closed historical segment 遇到损坏时 MUST 报告 corruption，不得静默截断。自动截断只适用于明确认定为 active append tail 的区域。

### 10.3 Orphan 处理

以下 orphan 均允许存在：

- 已提交、但 branch ref 尚未推进的 Event。
- branch 后续 reset 后不再从任何 ref 可达的 Event 子树。
- 未来 v2 multi-frame payload 中，已 durable、但 descriptor / EventFrame 未提交的 payload part frame。

MVP 读取路径忽略 orphan。诊断器可以扫描并报告；GC/repack 在未来版本基于“从所有 refs 做可达性遍历”处理。

### 10.4 损坏分类

至少区分：

- address malformed：segment/ticket 编码非法。
- segment missing：地址指向不存在的 segment。
- RBF frame corrupted：framing 或 CRC 失败。
- meta malformed：RBF 完整，但 EventJournal Meta schema 非法。
- payload missing/corrupted：Meta 有效但引用 part 无法完整读取。
- ref target invalid：ref update 有效但 target Event 无效。

错误应保留 `EventAddress` / `FrameAddress` 和恢复建议，避免把所有情况折叠成 `InvalidDataException` 字符串。

## 11. 候选 API 面

下面用于约束职责，不是最终签名：

```csharp
public interface IEventJournalStore : IDisposable {
    AteliaResult<EventAddress> AppendEvent(
        EventAddress? parent,
        Stream payload,
        CancellationToken cancellationToken = default
    );

    AteliaResult<EventHeader> ReadEventHeader(EventAddress address);
    AteliaResult<EventPayloadReader> OpenPayload(EventAddress address);

    EventAncestorSequence EnumerateAncestors(EventAddress head);
    EventChronologicalSequence EnumerateChronological(EventAddress head);

    AteliaResult<RefId> OpenBranch(string branchName);
    AteliaResult<RefId> CreateBranch(string branchName, EventAddress? startPoint);
    IReadOnlyList<string> ListBranches();
    EventAddress? GetRefHead(RefId refId);

    AteliaResult CommitToRef(
        RefId refId,
        EventAddress? expectedOldHead,
        Stream payload,
        out EventAddress newHead
    );
}
```

API 设计时应继续遵循：

- 可预见拒绝使用 `AteliaResult`。
- I/O 与资源故障可抛异常。
- 大 payload 读取使用 stream、sequence 或 chunk iterator，不能强制 materialize 单个 `byte[]`。
- reverse / chronological iterator 要明确资源 lease 生命周期。
- branch mutation 和 raw append 是两种不同能力，不用一个含大量 mode flags 的方法混合。

## 12. 施工阶段与验收

### ✅ EJ-0：RBF 有效尾恢复（已完成）

> **实现**：`src/Rbf/RbfRecovery.cs`（`RbfRecoveryScanner` + `RbfRecovery.TruncateToSuggestedTail`）。
> 同时交付了正向迭代能力：`src/Rbf/RbfForwardEnumerator.cs`、`src/Rbf/RbfForwardSequence.cs`。

范围：把 `RollingCrc.BackwardScanner` 接入 RBF，暴露首个完整有效 frame / recoverable tail 能力。正向 ScanForward 作为附带交付。

验收：

- 完整文件返回真实最后 frame。
- 尾部追加任意垃圾、截断 trailer、截断 payload 时能找到前一个完整 frame。
- scanner 命中伪候选但完整 CRC 失败时继续搜索。
- 空 RBF 文件返回无 frame 的安全 tail。

### RSS-1：RbfSegmentStore 独立层

范围：独立 C# project 形态的 `RbfSegmentStore`。完整施工目标与验收标准见 [RbfSegmentStore §9 作为独立 Project](rbf-segment-store-design.md#9-作为独立-project)。

### EJ-1：地址与 Frame IO Helper

范围：`FrameAddress`、`EventAddress`、Address wire format，以及基于 `RbfSegmentStore` lease 组合 `SizedPtr Ticket + SegmentNumber` 的轻量 helper。

验收：

- `FrameAddress` 与 `EventAddress` 固定宽度 little-endian 编码往返一致。
- `EventAddress` 前缀可按 `FrameAddress` 解码，`FrameAddress` 前缀可按 `SizedPtr` 解码。
- 写入 helper 每次现借 active writer lease，写完释放，不长期持有 writer lease。
- 读取 helper 按 `FrameAddress.SegmentNumber` 借 reader lease，并用 `FrameAddress.Ticket` 调用 RBF read API。
- segment 轮转后旧 `FrameAddress` 仍可读取。

### EJ-2：EventFrame Header Codec、Append 与 Random Read

范围：`EventFrame` tag、TailMeta fixed binary header schema v1、inline payload、提交顺序。详细施工目标见 [EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)。

验收：

- 空 payload、小 payload 和接近单 frame 上限的 payload 往返一致。
- `ReadTailMeta` preview 可读取 Parent、Hint 和 payload 长度；完整读取会校验 `EventFrame`。
- 写入 `EventFrame` 前崩溃不会产生可见 Event。
- active-tail recovery 后只暴露完整 `EventFrame`。

### EJ-3：Parent Chain Traversal

范围：不涉及 branch/refs 的 root、单链、分叉、reverse 与 chronological iteration。详细施工目标见 [EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)。

验收：

- reverse 结果为 head 到 root。
- chronological 结果为 root 到 head。
- 两个 branch 可共享祖先后独立前进。
- malformed Parent 不导致无限循环。

### EJ-4：Ref Store 与 Reflog

范围：RefOpLog、`BranchName -> RefId -> RefSegmentFile` 映射、per-ref RBF move chain、CAS update、reflog。详细施工目标见 [Event Ref Store 设计基线](event-ref-store-design.md)。

验收：

- create / fork / archive branch 可通过 RefOpLog 重放恢复。
- advance / move ref 可通过 `RefId` 对应的 move chain 重放恢复。
- CAS 旧 head 不匹配时不推进 branch。
- reset 改变 ref history，不改写 Event Parent。
- event durable、ref 未写入的崩溃只产生 orphan Event。

### EJ-5：Crash Matrix 与端到端恢复

范围：在 EventFrame append、EventFrame flush、ref append、ref flush 各阶段注入失败。

验收：

- reopen 只暴露完整 Event 和有效 ref。
- active tail 被截到有效边界。
- closed segment corruption 明确报错。
- 所有恢复结果均可再次 append 并稳定 reopen。

## 13. 待后续会话决策的问题

1. 未来是否需要 store manifest；若需要，它记录哪些非路径表信息，以及原子更新协议是什么。
2. segment soft size threshold 和 segment number 上限。
3. EventFrame v1 的精确 TailMeta header 编码与 FrameTag 值。
4. TailMeta header 自校验使用结构性验证、header-level CRC，还是二者组合。
5. payload digest 是否进入 EventFrame v1；若记录，使用何种 hash。
6. per-ref move chain 的 checkpoint/cache 与加速打开策略。
7. `CommitToRef` 在 ref CAS 失败时如何暴露已产生的 orphan EventAddress。
8. v2 multi-frame payload 使用 payload chain、TailMeta address list、descriptor frame，还是外部 large-object store。
9. durable append 与 buffered append 是否需要两档 API。
10. 同进程并发 reader 与单 writer 的锁粒度。
11. store 复制、备份、repack、可达性 GC 与跨 store import/export。

这些问题不阻塞当前架构判断。应按 EJ-0 到 EJ-5 的顺序逐个形成 Decision/Spec，而不是在首个实现会话一次性定完。
