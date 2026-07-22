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
- Event 的逻辑 payload 是任意二进制字节序列，可跨多个 RBF `PayloadFrame`。
- `HEAD` 与 named branch 只保存对 Event 的引用，不改变 Event 本身。
- Event、branch 与历史遍历均以跨 segment 稳定地址为基础。

`EventJournal` 不知道 payload 表示聊天消息、工具调用、游戏状态还是其他领域对象。领域 schema、序列化 codec、索引和查询语义全部属于上层。

## 3. 目标与非目标

### 3.1 目标

1. 保存不可变、append-only、可随机定位的二进制 Event。
2. 自动管理多 segment RBF 文件、目录分桶、轮转和打开文件池。
3. 支持沿 Parent 从任意 Event 逆序遍历，并通过临时容器提供顺序遍历。
4. 支持超过单个 RBF Frame 上限的逻辑 payload。
5. 提供 `HEAD`、named branch、ref move history 和 commit parent history。
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

`EventJournal` MUST 把 Event 的应用 payload 视为不透明字节序列。它只能解释自己的结构元数据，例如 Parent、payload part 地址和总长度；它 MUST NOT 根据应用 payload 的 kind、版本或字段改变存储行为。

### decision [S-EJ-EVENT-IMMUTABLE] Event 不可变

Event 一旦提交成功，其 Parent、payload part 列表和 payload bytes MUST NOT 原地修改。修订内容只能追加新 Event，并让新 Event 指向旧 Event。

### decision [S-EJ-SINGLE-PARENT] MVP 使用单 Parent

每个 Event MUST 有零个或一个 Parent。零 Parent 表示 root Event；一个 Parent 表示线性继承。多个 branch 可指向同一祖先并独立前进，从而形成版本树。

多 Parent merge commit 不进入 MVP。若未来引入，它必须是显式格式升级，不能把额外 Parent 偷塞进不透明 payload 后宣称底层已支持 merge。

### decision [S-EJ-ADDRESS-STABLE] 地址不受文件句柄与目录迁移影响

Event 的公开身份 MUST 由 `EventAddress` 表达。调用方不能持久化绝对路径、`FileStream`、`IRbfFile` 实例或进程内 pool handle 作为 Event 身份。

### decision [S-EJ-META-COMMITS-EVENT] MetaFrame 是 Event 可见性边界

一个 Event 由若干 `PayloadFrame` 和最后一个 `MetaFrame` 组成。只有完整、有效且已提交的 `MetaFrame` 才使该 Event 存在。没有对应 `MetaFrame` 的 payload parts 是 orphan data，不是半个 Event。

## 5. 地址模型

### 5.1 FrameAddress 与 EventAddress

推荐在类型系统中区分物理 frame 地址与 Event 身份：

```csharp
public readonly record struct FrameAddress(
    uint SegmentNumber,
    SizedPtr Ticket
);

public readonly record struct EventAddress(
    uint SegmentNumber,
    SizedPtr MetaFrameTicket
);
```

两者具有相同的物理坐标形状，但语义不同：

- `FrameAddress` 可以指向任意 EventJournal 内部 RBF frame。
- `EventAddress` 必须指向一个有效 `MetaFrame`，是 Event 的稳定身份。
- Parent、branch head、reflog old/new head 使用 `EventAddress`。
- MetaFrame 中的 payload part 列表使用 `FrameAddress`。

这与现有 `CommitAddress = { SegmentNumber, CommitTicket }` 的经验一致，但 `EventJournal` 不依赖 `StateJournal.CommitAddress`，也不继承它的业务语义。

#### 5.1.1 Address Hint 候选扩展

在常见 64-bit CLR ABI 下，`uint SegmentNumber + SizedPtr Ticket` 通常因 `SizedPtr` 的 8-byte 对齐而占用 16 bytes，其中包含 4 bytes padding。可以考虑显式利用该空间，把内存表示改为以下候选形状：

```csharp
public readonly record struct AddressHint(uint Packed);

public readonly record struct FrameAddress(
  uint SegmentNumber,
  AddressHint Hint,
  SizedPtr Ticket
);
```

`EventAddress` 可采用相同的物理布局。这样可在不增大常见内存表示的前提下携带 32-bit smart-pointer hint，例如应用 event kind、粗粒度时间桶或染色标记。该候选尚未锁定，并受以下边界约束：

1. `Hint` 只能作为免 I/O 的分派、过滤或预取提示；仅凭未经验证的外部地址，不能据此作存储正确性或安全决策。
2. 若 `Hint` 进入 `EventAddress` 身份，它必须在 Event 创建时固定，并由目标 `MetaFrame` 保存相同值或提供可确定推导的值。reader 解引用后必须校验两者一致，避免同一物理位置出现多个互相矛盾的地址。
3. 任意可变、与目标内容无关的用户标签不进入 canonical address；这类状态应使用上层 wrapper 或独立索引表达。
4. EventJournal 只保存和校验 hint 的一致性，不解释应用 bit schema，以维持 `[S-EJ-PAYLOAD-OPAQUE]`。
5. `Packed = 0` 应保留为“无 hint”；其余 bit 的命名空间和版本策略由后续 Spec 决定。
6. 不应为了获得更多 hint bits 而过早缩减 `SegmentNumber`。即使保留完整 `uint` segment number，当前自然 padding 也已提供 32 bits；单调编号还可能因按时间轮转、测试、导入或保留 tombstone 而比物理 segment 数增长更快。

内存 padding 不等于 wire format 免费。当前两字段地址可以手工紧凑编码为 12 bytes；若持久化完整 32-bit hint，固定宽度将变为 16 bytes，使大规模 payload part address list 增长约三分之一。后续 wire spec 必须独立比较固定 16-byte、紧凑字段编码和 VarInt 编码，不能从 CLR 布局直接推导持久格式。

### 5.2 Null 与合法性

- `SegmentNumber` 从 `1` 开始，`0` 保留给 null/未设置编码。
- 有效 `EventAddress` 的 ticket 必须非 null，且必须能在对应 segment 中完整读取为 `MetaFrame`。
- API 层使用 `EventAddress?` 表达无 Parent 或 unborn branch，避免把可构造的无效地址传播到业务代码。
- 持久化格式若使用 `{ segment=0, ticket=0 }` 表示 null，reader 必须拒绝“仅一侧为零”的半空地址。

### 5.3 EventAddress 同时充当 EventId

MVP 不另造随机 UUID。`EventAddress` 已经是 store 内唯一、稳定、可解引用的 EventId。若未来需要跨 store 引用，应显式组合 `StoreId + EventAddress`，而不是扩大当前地址的隐含作用域。

## 6. Segment Store

### 6.1 职责

Segment Store 对上层隐藏以下细节：

- segment 编号分配与轮转。
- segment number 到相对路径的确定性映射。
- RBF 文件的创建、只读打开、active writer 和关闭。
- historical file 的 bounded pool。
- active tail 恢复。

上层拿到 `EventAddress` 后，只执行“解析 segment → 租用文件 → 按 `SizedPtr` 读取”，不自己拼路径。

### 6.2 推荐目录形状

下面仅是可读示例，不是最终文件名规范：

```text
<store>/
  manifest
  segments/
    00000001-00000400/
      00000001.ej.rbf
      ...
      00000400.ej.rbf
    00000401-00000800/
      ...
  control/
    refs.rbf
```

推荐规则：

1. 每个目录容纳固定数量 `N` 的 segment。
2. bucket 名由 `[bucketStart, bucketEnd]` 的 segment 编号范围确定。
3. segment 文件名仅由 segment number 确定。
4. `N`、布局版本和文件命名版本记录在 store manifest 中，并在 store 创建后保持不变。
5. 路径始终是可重建的派生信息，不写入 `EventAddress`。

`N = 1024` 可以作为首轮基准，但不应在性能与文件系统测试前写死为跨版本永久常量。

### 6.3 Segment 生命周期

- Segment number MUST 单调分配且不得复用。
- closed segment SHOULD 只读；正常运行时不得继续追加或原地修补。
- 任意单个 RBF frame 不跨 segment。
- 一个逻辑 Event MAY 跨 segment：不同 payload parts 和最终 MetaFrame 可以位于不同 segment。
- 轮转只能发生在 frame 边界之间。
- active segment 是唯一允许追加和尾部恢复性截断的 data segment。

推荐根据“下一 frame 的预计长度 + 当前 tail”决定是否先轮转。对于无法提前得知长度的流式 builder，后续 Spec 必须明确采用预估、临时缓冲还是允许 segment 超过软阈值。

#### 6.3.1 Segment 阈值选择原则

主流 64-bit 部署中的 ext4、XFS 和 OpenZFS 均能承载 1 TiB 量级单文件；因此 segment threshold 不应以文件系统“能否寻址”为主要依据。它首先是 EventJournal 的恢复、校验、备份、迁移和故障隔离单元。

阈值设计至少考虑：

1. **恢复与 scrub 预算**：限制单次完整校验、隔离损坏或重建 catalog 时需要处理的数据量。正常 active-tail 恢复仍应由最大未完成 frame 大小约束，不能依赖扫描整个 segment。
2. **备份与复制增量**：closed segment 不再变化，可被一次复制、校验和归档；active segment 越大，长期可变的备份对象也越大。
3. **故障域**：RBF CRC 可定位 frame 级损坏，但 inode、extent tree、介质区域或操作失误仍可能影响整个文件。segment 提供应用层隔离边界。
4. **文件与句柄数量**：segment 太小会放大目录项、manifest/catalog、打开文件池、备份对象和 GC/repack 调度成本。目录分桶只能缓解目录查找，不能消除对象数量成本。
5. **frame 几何**：soft threshold 应显著大于常用 payload part 大小，并明确单个最大 frame 是否可越过阈值。轮转判断应保证 frame 不跨 segment。
6. **分配与碎片**：长时间顺序追加通常能被 extent/delayed-allocation 良好处理；接近满盘、频繁快照、CoW 和混合写入可能增加碎片与元数据成本，需要按目标文件系统实测。
7. **运维时间边界**：低吞吐 Agent 可能多年达不到 size threshold，因此还应支持 max-age 轮转，使 active segment 能定期 sealed、归档和备份。

推荐将策略建模为 `size soft threshold OR age threshold`，而不是唯一硬上限。首轮基准可比较 16 GiB、64 GiB、256 GiB 三档，并以 64 GiB + 每日或每周 age rotation 作为实验起点；1 TiB 可作为压力测试档，不宜直接作为默认值。最终默认值必须由恢复扫描、随机读、持续 append、durable flush、备份和 segment catalog 基准共同决定。

### 6.4 Opened RBF File Pool

文件池推荐采用：

- active write file：常驻且独占写入。
- historical files：read-only、按需打开、LRU 或等价 bounded eviction。
- file lease：一次读取持有 lease，防止文件正在使用时被 pool 关闭。
- 最大打开数：由 options 配置，并提供合理默认值。
- 异常淘汰：文件读取出现不可恢复错误时，从 pool 移除并在下次读取时重新打开，以区分句柄故障与持久损坏。

pool key 是 `SegmentNumber`，不是路径。路径解析集中在 Segment Catalog 中。

MVP 采用单 writer、多 reader 进程内模型。并发读取必须与 pool eviction 协调；跨进程 writer 和共享写句柄不在范围内。

## 7. Event 物理组成

### 7.1 两类内部 Frame

`EventJournal` 在 RBF 的 `FrameTag` 空间中至少定义：

- `PayloadFrame`：保存应用 payload 的一个原始字节分片。
- `MetaFrame`：保存 EventJournal 结构元数据，并提交一个 Event。

这里的 `MetaFrame` 是一个普通、需要完整 CRC 校验的 RBF frame，不是 RBF 的 `TailMeta`。RBF `TailMeta` 只具有 trailer-level 信任，不得作为 Parent、payload part 列表或 branch target 的权威来源。

### 7.2 推荐 MetaFrame 逻辑字段

首轮 wire spec 应至少表达：

| 字段 | 含义 |
|:-----|:-----|
| `FormatVersion` | EventJournal MetaFrame schema 版本 |
| `Parent` | 可空 `EventAddress` |
| `PayloadPartCount` | payload part 数量 |
| `PayloadParts` | 按逻辑 payload 顺序排列的 `FrameAddress` 列表 |
| `TotalPayloadLength` | 所有 payload part bytes 的总长度 |

可选但尚未锁定的字段：

- 整体 payload digest，用于在 part-level RBF CRC 之外校验拼接顺序和整体内容。
- 创建时间；它不是存储正确性所需字段，且可能制造不确定性。
- payload codec id；原则上应留在应用 payload envelope，而不是下沉到 EventJournal。

### 7.3 Payload 分片

单个 RBF Frame 的 `PayloadAndMeta` 上限约为 256 MiB，因此 EventJournal 必须支持逻辑 payload 分片。

推荐 MVP 使用“MetaFrame 内的有序 part list”：

1. 按输入顺序写入一个或多个 `PayloadFrame`。
2. 收集每个 frame 的 `FrameAddress`。
3. 在 MetaFrame 中按原顺序写入地址列表和总长度。
4. 读取时依次读取并拼接或流式暴露各 part。

选择 list 而不是反向链的原因：

- payload 顺序读取不需要先回溯再反转。
- 可以 O(1) 定位任意 chunk descriptor。
- MetaFrame 自身也有很大的 payload 上限；在实际可存储的数据量下，地址表通常不是瓶颈。

若未来出现“不能在写 MetaFrame 前保留 part address list”的真实流式需求，再增加间接 descriptor frame 或 payload chain。不要在 MVP 同时维护 list 与 chain 两套规范。

MVP 的逻辑 Event 大小并非无上限。`PayloadPartCount`、part address list 和其余 Meta 字段必须共同容纳在一个 MetaFrame 中；首轮 Spec 必须由 RBF 的 `MaxPayloadAndMetaLength` 推导并公开：

- 最大 payload part 数。
- 最大逻辑 payload 总长度。
- part count、单 part 长度和总长度计算的 checked overflow 规则。

reader 必须逐项验证 part list：

- 每个地址都能完整读取且 tag 为 `PayloadFrame`，不得引用 `MetaFrame` 或未知内部 frame。
- 同一 Event 的 part list 不得包含重复地址。
- part 顺序以 list 顺序为准，不得按物理地址重排。
- 每个 part 的 payload 长度之和必须恰好等于 `TotalPayloadLength`。
- 任一 part 缺失、损坏、类型错误或长度不一致时，整个 Event 读取失败，不得返回部分 payload 冒充完整结果。

空 payload 使用空 part list 表达。MVP SHOULD 让小 payload 也走 `PayloadFrame + MetaFrame`，先换取统一恢复语义；是否允许把小 payload inline 到 MetaFrame 留作性能实验。

### 7.4 Event 提交协议

安全基线是：

1. 验证 Parent 已存在且是完整 Event。
2. 依次写入全部 PayloadFrame，必要时在 frame 边界轮转。
3. 对承载新 payload parts 的 segment 执行 `DurableFlush`。
4. 写入 MetaFrame。
5. 对 MetaFrame 所在 segment 执行 `DurableFlush`。
6. 只有此后才返回成功的 `EventAddress`，或推进 branch ref。

### spec [R-EJ-PAYLOAD-BEFORE-META] Payload 必须先于 Meta 持久化

在 MetaFrame 可能成为 durable 之前，它引用的全部 PayloadFrame MUST 已 durable。这样恢复后只可能出现无 Meta 的 orphan payload，不应出现完整 Meta 指向未持久化 payload 的正常崩溃状态。

### spec [S-EJ-PARENT-PREEXISTS] Parent 必须先存在

新 Event 的 Parent MUST 是已提交 Event，且在物理追加顺序上早于新 MetaFrame。该约束使单 Parent 图天然无环。

### 7.5 批量优化边界

上述协议的朴素实现需要 payload flush 和 meta flush。未来 MAY 提供 batch commit：多个 Event 的 payload 一起 flush，再写一批 MetaFrame 并 flush。但 batch API 必须明确哪些 Event 同时成功可见，不能为了减少 fsync 模糊单 Event 的结果语义。

## 8. 读取与遍历

### 8.1 读取 Event

`ReadEvent(EventAddress)` 的最小流程：

1. 通过 Segment Catalog 定位 MetaFrame 所在文件。
2. 使用 `ReadFrame` / `ReadPooledFrame` 完整校验 MetaFrame。
3. 校验 tag、内部 schema、Parent 和 payload part 地址。
4. 返回结构 header，并允许调用方按需读取 payload。

推荐把“读取 header”和“读取 payload bytes”分开，避免遍历 Parent 时无谓读取大 payload。

### 8.2 逆序 Parent 遍历

`EnumerateAncestors(head)` 按以下顺序产出：

```text
head, head.parent, head.parent.parent, ..., root
```

其空间复杂度应为 $O(1)$，每步只需读取当前 MetaFrame。API 应支持 cancellation 和可选最大深度。

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

## 9. HEAD、Branch 与 Commit History

### 9.1 概念区分

- Event：不可变 commit node。
- Commit history：从 branch head 沿 Parent 形成的有效历史。
- Branch：名字到 `EventAddress?` 的可变 ref。
- HEAD：当前选中的 symbolic branch，或 detached `EventAddress`。
- Ref history / reflog：branch 或 HEAD 的移动历史，不等于 Event Parent history。

必须保持这两类历史的区别：Parent 描述内容继承，reflog 描述 ref 曾经如何移动。reset、rewind 或切换 branch 会产生 reflog 记录，但不会改写 Event Parent。

### 9.2 最小 Branch 能力

MVP 应提供：

- 创建 unborn branch 或从现有 Event 创建 branch。
- 查询和枚举 branch。
- 以 compare-and-swap 方式推进 branch。
- 移动或删除 branch，并记录 ref history。
- symbolic HEAD 与 detached HEAD。
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

branch / HEAD ref 的 durable 更新 MUST 发生在目标 Event 完整持久化之后。恢复时若最新 ref update 指向无效 Event，reader MUST 回退到更早的有效 ref update，而不是接受悬空 head。

### 9.4 Ref Store 推荐形态

推荐使用独立的小型 append-only RBF control journal 作为 refs 的 Canonical Source：

- 每次 create / advance / move / delete 追加 ref update frame。
- update 记录 ref name、old target、new target 和可选 opaque note。
- 最后一个有效 update 决定当前 ref 值。
- reflog 天然由 control journal 保留。
- 当前 ref map 可做 checkpoint 或 cache，但不是唯一事实源。

这样 data Event segments 永远不需要因 branch move 而修改。具体 ref frame schema、HEAD 表达和 checkpoint 机制留给独立 Spec 会话。

## 10. 故障恢复

### 10.1 RBF 前置能力

EventJournal 依赖 RBF 提供“从文件尾部向前找到首个完整有效 frame，并返回其安全结束位置”的能力。当前 [RollingCrc scanner](../../src/Data/Hashing/RollingCrc.Scanner.cs) 已具备快速逆序滑窗寻找 CRC codeword 的基础，但尚需接入 RBF。

关键边界：

- Rolling CRC 命中只表示“发现候选 codeword”。
- 候选 frame 仍 MUST 校验长度、边界、tag、trailer codeword 和完整 payload CRC。
- 只有完整 RBF 校验通过的候选才能成为恢复锚点。
- CRC 偶然碰撞不得直接被当作有效 frame。

建议 RBF 暴露类似以下能力，具体命名另定：

```csharp
AteliaResult<RbfFrameInfo?> FindLastValidFrame(...);
AteliaResult<long> FindRecoverableTailOffset(...);
```

### 10.2 Active Segment 恢复

打开 store 时：

1. 验证 manifest 和 segment catalog 连续性。
2. 对 active data segment 执行有效尾查找。
3. 将撕裂字节截断到最后一个完整 frame 的结束位置。
4. 对 refs control journal 执行相同的有效尾恢复。
5. 重建 branch / HEAD 状态。
6. 验证每个可见 ref target 至少能完整读取 MetaFrame；失败则沿 ref history 回退。

closed historical segment 遇到损坏时 MUST 报告 corruption，不得静默截断。自动截断只适用于明确认定为 active append tail 的区域。

### 10.3 Orphan 处理

以下 orphan 均允许存在：

- 已 durable、但 MetaFrame 未提交的 PayloadFrame。
- 已提交、但 branch ref 尚未推进的 Event。
- branch 后续 reset 后不再从任何 ref 可达的 Event 子树。

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
    EventJournalHead Head { get; }

    AteliaResult<EventAddress> AppendEvent(
        EventAddress? parent,
        Stream payload,
        CancellationToken cancellationToken = default
    );

    AteliaResult<EventHeader> ReadEventHeader(EventAddress address);
    AteliaResult<EventPayloadReader> OpenPayload(EventAddress address);

    EventAncestorSequence EnumerateAncestors(EventAddress head);
    EventChronologicalSequence EnumerateChronological(EventAddress head);

    AteliaResult CommitToBranch(
        string branchName,
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

### EJ-0：RBF 有效尾恢复

范围：把 `RollingCrc.BackwardScanner` 接入 RBF，暴露首个完整有效 frame / recoverable tail 能力。

验收：

- 完整文件返回真实最后 frame。
- 尾部追加任意垃圾、截断 trailer、截断 payload 时能找到前一个完整 frame。
- scanner 命中伪候选但完整 CRC 失败时继续搜索。
- 空 RBF 文件返回无 frame 的安全 tail。

### EJ-1：地址、Segment Catalog 与 File Pool

范围：`FrameAddress`、`EventAddress`、目录分桶、active segment、historical pool。

验收：

- 跨至少两个 bucket 定位 segment。
- pool 上限与 lease 行为可测试。
- segment 轮转后旧地址仍可读取。
- store reopen 后地址映射不变。

### EJ-2：Meta/Payload Append 与 Random Read

范围：内部 frame tags、Meta schema v1、多 part payload、提交顺序。

验收：

- 空、小型和跨多个 PayloadFrame 的 payload 往返一致。
- Event 跨 segment 仍可读取。
- Meta 提交前崩溃只留下不可见 orphan payload。
- 有效 Meta 不会指向未 durable payload。

### EJ-3：Parent Traversal

范围：root、单链、分叉、reverse 与 chronological iteration。

验收：

- reverse 结果为 head 到 root。
- chronological 结果为 root 到 head。
- 两个 branch 可共享祖先后独立前进。
- malformed Parent 不导致无限循环。

### EJ-4：Refs、HEAD 与 Reflog

范围：control journal、named branch、symbolic/detached HEAD、CAS update。

验收：

- create / advance / move / delete branch 可重放恢复。
- CAS 旧 head 不匹配时不推进 branch。
- reset 改变 ref history，不改写 Event Parent。
- event durable、ref 未写入的崩溃只产生 orphan Event。

### EJ-5：Crash Matrix 与端到端恢复

范围：在 payload append、payload flush、meta append、meta flush、ref append、ref flush 各阶段注入失败。

验收：

- reopen 只暴露完整 Event 和有效 ref。
- active tail 被截到有效边界。
- closed segment corruption 明确报错。
- 所有恢复结果均可再次 append 并稳定 reopen。

## 13. 待后续会话决策的问题

1. store manifest 使用校验二进制、RBF control frame 还是简单文本；其原子更新协议是什么。
2. segment bucket size、soft size threshold 和 segment number 上限。
3. MetaFrame v1 的精确字段编码与 FrameTag 值。
4. payload part 是否记录整体 digest；若记录，使用何种 hash。
5. 小 payload 是否允许 inline MetaFrame，以及收益是否值得增加分支。
6. refs control journal 的 checkpoint 与加速打开策略。
7. `CommitToBranch` 在 ref CAS 失败时如何暴露已产生的 orphan EventAddress。
8. durable append 与 buffered append 是否需要两档 API。
9. 同进程并发 reader 与单 writer 的锁粒度。
10. store 复制、备份、repack、可达性 GC 与跨 store import/export。

这些问题不阻塞当前架构判断。应按 EJ-0 到 EJ-5 的顺序逐个形成 Decision/Spec，而不是在首个实现会话一次性定完。
