---
docId: "rbf-type-bone"
title: "RBF 核心类型骨架 (Type Bone)"
status: "Draft"
doc-type: "Implementation Guide"
normative: false
summary: "基于 RandomAccess 与共享 reader/cache 的核心读写组件和 Facade 设计（非规范性）"
depends_on:
  - "rbf-interface.md"
  - "rbf-decisions.md"
---

# RBF 核心类型骨架 (Type Bone)
**本文档性质**：Implementation Guide（实现指南），非规范性。
实现者 MAY 在不违反 [rbf-interface.md](rbf-interface.md) 契约的前提下采用不同实现路径。
本文档描述的是"推荐实现方案"，不是"唯一合法实现"。

本文档定义 RBF 子系统的核心类型设计。

2026-10-03接点同步：内部只保留RBF1/RBF3两组具体wire布局，不新增公共codec模式或assembly。工厂在owned handle一次Header分派，RBF1只读；RBF3在解析边界先验U再转换成byte布局，FrameInfo、cache范围、Data输入与ticket仍为bytes。详细合同以[接口](rbf-interface.md)、[格式](rbf-format.md)为准；源码0999851及独立复核的测试/正式证据见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)。

---

## 快速导航（引用的接口层定义）

本文档多处引用 [rbf-interface.md](rbf-interface.md) 中的公开类型定义，这里提供快速链接：

| 类型 | SSOT | 描述 |
|------|------|------|
| `IRbfFile` | [rbf-interface.md#A-RBF-IRBFFILE-SHAPE](rbf-interface.md#A-RBF-IRBFFILE-SHAPE) | RBF 文件对象门面 |
| `IRbfFrame` | [rbf-interface.md#A-RBF-IFRAME](rbf-interface.md#A-RBF-IFRAME) | 帧公共属性契约 |
| `RbfFrame` | [rbf-interface.md#A-RBF-FRAME-STRUCT](rbf-interface.md#A-RBF-FRAME-STRUCT) | 帧数据结构 |
| `RbfPooledFrame` | [rbf-interface.md#A-RBF-POOLED-FRAME](rbf-interface.md#A-RBF-POOLED-FRAME) | 携带 ArrayPool buffer 的帧 |
| `RbfFrameBuilder` | [rbf-interface.md#A-RBF-FRAME-BUILDER](rbf-interface.md#A-RBF-FRAME-BUILDER) | 帧构建器 |
| `RbfFrameInfo` | [rbf-interface.md#A-RBF-FRAME-INFO](rbf-interface.md#A-RBF-FRAME-INFO) | 帧元信息 |
| `RbfReverseSequence` | [rbf-interface.md#A-RBF-REVERSE-SEQUENCE](rbf-interface.md#A-RBF-REVERSE-SEQUENCE) | 逆向扫描元信息序列 |

---

**设计主旨**：
- **底层**：采用 `System.IO.RandomAccess` API 与静态操作原语；RBF 调用还会经过携带 cache/fault 状态的 `RandomAccessReader`。
- **并发**：RandomAccess 单次操作不使持有共享 `RandomAccessReader` 的 RBF 调用成为无状态或可并发调用；同一 File 派生对象的共享状态访问按接口合同串行。
- **Facade**：通过薄层对象 `IRbfFile` 管理文件句柄生命周期与写入游标（Tail Offset）。

---

## 1. 核心数据结构 (Core Types)

### 1.1 帧定义 (Frame)

`RbfFrame` 是 RBF 的核心数据单元。

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-FRAME-STRUCT]

**实现说明**：
- 作为 `ref struct` 设计以避免 GC 分配
- 仅仅是对底层内存（栈上 buffer 或 pooled array）的一个视图（View）
- 生命周期受限于产生它的 Scope（如 ReadFrame 的 buffer）

**接口抽象**：
- `RbfFrame` 实现 `IRbfFrame` 接口（见 @[A-RBF-IFRAME]），定义帧的公共属性契约
- 新增 `RbfPooledFrame` 作为 class 实现（见 @[A-RBF-POOLED-FRAME]），携带 ArrayPool buffer 并支持 `IDisposable`
- 两者通过 `IRbfFrame` 接口统一，但生命周期管理不同：
  - `RbfFrame`：调用方管理 buffer 生命周期（栈分配或手动传入）
  - `RbfPooledFrame`：调用方 MUST 调用 `Dispose()` 归还 ArrayPool buffer

---

### 1.2 帧元信息 (FrameInfo)

`RbfFrameInfo` 是逆向扫描的产出类型，用于“只看元信息、不读 payload”的场景。

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-FRAME-INFO]

**实现说明**：
- 以 `record struct` 表达值语义，避免 GC 分配
- 包含 `Ticket`/`Tag`/`PayloadLength`/`TailMetaLength`/`IsTombstone`
- **不含 Payload**：读取完整帧需使用 `ReadFrame`/`ReadPooledFrame`（这些路径执行完整 CRC 校验）

---

## 2. 底层操作原语 (Low-Level Primitives)

`RbfReadImpl` 以静态方法提供读取原语，但方法使用调用方传入的 `RandomAccessReader`，其 cache 与 fault 状态可能由同一 File 的其他对象共享。这是具体实现层（Implementation Layer），**便于进行基于临时文件的集成测试**。

**核心职责**：
- **ReadFrame**: 将帧读入调用方提供的 buffer，执行完整 CRC 校验（Payload + Trailer）。
- **ReadPooledFrame**: 从 ArrayPool 借缓存读取帧，携带 IDisposable 语义。
- **ReadInfoBefore**: 仅读取帧元信息（读取 Trailer + FrameDescriptor），只做 Framing 校验和 TrailerCrc32C 校验，**不校验 Payload**。
- **ScanReverse**: 反向扫描原语，使用 "Tail-Only Reverse Scan" 策略，依赖 Trailer 结构进行快速定位。

**并发模型**：
- 访问同一个 reader/cache 的操作由拥有该 reader 的 File 合同要求调用方串行；静态方法不等同于纯函数或并发安全操作。
- 独立只读实例各自使用独立 reader 时可并行；不要求绑定固定 OS 线程。

```csharp
namespace Atelia.Rbf.Internal;

/// <summary>RBF 读取操作实现。</summary>
internal static class RbfReadImpl {
    // 方法实现见源码 atelia/src/Rbf/Internal/RbfReadImpl.cs
}

/// <summary>RBF 写入操作实现。</summary>
internal static class RbfWriteImpl {
    // 方法实现见源码 atelia/src/Rbf/Internal/RbfRawOps.cs
}
```

---

## 3. 读写组件 (Components)

### 3.1 写入构建器 (Writer Builder)

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-FRAME-BUILDER]

**实现说明**：
- 使用readonly struct携带epoch与owner，按现有借用/取消边界工作
- 生命周期必须涵盖 Payload 写入过程
- EndAppend先完成plaintext CRC/Trailer（TailLenUnits=U），对owned pending chunks选Key并原地XOR，最后追加raw Key/Fence、回填raw HeadLenUnits并Commit/Push；一次API调用不是不可中断的磁盘事务
- 内部字段：`SafeFileHandle _file`、`long _offset`、`byte[] _buffer` 等

### 3.1.1 Auto-Abort 实现路径

### spec [I-RBF-BUILDER-AUTO-ABORT-IMPL] Auto-Abort物理实现
```clause-matter
depends: "@[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME](rbf-interface.md)"
```
本条款定义如何实现 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME] 的逻辑语义。

**物理实现双路径**：

下表保留旧骨架的备选说明。本轮RBF3绑定SinkReservableWriter，以唯一pending头reservation阻止发布，未提交owned chunks由Reset取消，使用Zero I/O路径；不采用Tombstone fallback。资源归还正常完成时健康取消回到Idle且不输出；Return/OOM等系统异常可传播，异常后不保证同一File继续可写。Auto-Abort不解除已有fault。最终Commit开始尝试发布后，任何异常均保守触发永久writer fault；这不表示已证明写出字节。

| 路径 | 条件 | 机制 |
|------|------|------|
| **Zero I/O** (SHOULD) | 实现能保证 open builder 期间 payload 不外泄、且在 `Dispose()` 时可丢弃未提交 payload | 通过 reservation rollback、内部 reset 或其他等价机制 |
| **Tombstone** (MUST fallback) | 无法实现 Zero I/O | 将帧标记为 Tombstone（`IsTombstone == true`），完成帧写入 |

**Tombstone 路径细节**：
- Tombstone 帧 SHOULD 保留原 FrameTag 值（供诊断用）
- Tombstone 帧 MUST 通过 framing/CRC 校验
- ScanReverse 默认过滤 Tombstone 帧；只有 `showTombstone=true` 时才产出（由 interface.md @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER] 约束）

**选择逻辑**：
- MVP 实现 SHOULD 优先尝试 Zero I/O
- 若内部 Buffer 实现为 `SinkReservableWriter`，则 Zero I/O 可通过 chunk rollback 实现

**重要说明**：`PayloadAndMeta` 的类型为 `IReservableBufferWriter` 并不承诺 Zero I/O 必然可用。Zero I/O 是实现优化，不是类型承诺。

### 3.2 逆向扫描序列 (Reverse Scanner)

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-REVERSE-SEQUENCE]

**实现说明**：
- 作为 `ref struct` 实现（如同 Span）
- 内部持有 handle 和 range/游标
- 产出 `RbfFrameInfo`，不读取 payload；如需 payload，调用 `ReadFrame`/`ReadPooledFrame`
- MVP 可直接用多次 `RandomAccess.Read` 完成 framing 校验；缓存层可后置
- 仅做framing与TrailerCRC校验，不执行PayloadCRC；RBF3读取Trailer16+TailKey4后先验U再得到byte L，仍不读头
- `Current` 为值语义快照（不依赖内部 buffer 生命周期）
- `MoveNext()` 返回 `false` 时，`TerminationError` 为空表示正常结束；非空表示硬停止错误

---

## 4. 门面 (Facade: Layer 1 Interface)

`IRbfFile` 是应用层（Layer 2）主要交互的对象，负责维护"有状态"的信息（如文件路径、打开的 Handle、当前的 TailOffset）。

**接口定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-IRBFFILE-SHAPE]

**实现说明**：
- 职责：资源管理 (IDisposable)、状态维护 (TailOffset)、调用转发
- 写入方法转发到 `RbfWriteImpl` 并更新内部状态
- 读取方法转发到 `RbfReadImpl`，并使用与 File 共享的 reader/cache 状态
- 同一 File 及其派生对象对共享状态或 I/O 的访问由调用方串行；Dispose 与枚举 MoveNext 也包括在内，不要求固定 OS 线程
- 门面在 Builder 活跃期间拒绝读取与扫描；此前取得的 FrameInfo 可串行读取历史帧

**工厂方法**：
- `RbfFile.CreateNew(string path)` — 创建纯RBF3（FailIfExists）
- `RbfFile.OpenExisting(string path, out RbfTailRecoveryReport recovery, RbfCacheMode cacheMode = RbfCacheMode.Slots16)` — 只接受RBF3，独占结构打开及单尾截断/补原Key+Fence；不校验PayloadCRC、不补墓碑
- `RbfFile.OpenReadOnlyExisting(string path, RbfCacheMode cacheMode = RbfCacheMode.Slots16)` — RBF1完整结构主链，RBF3局部结构快开；需恢复时拒绝，不修改

HeadLen的4B reservation仅存最终raw U，不属于XOR范围。Data fused操作的范围是reservation之后的完整已写body；调用前不能追加raw Key/Fence。全部可预见拒绝在首次footer修改前结束；从首次padding/footer修改到最终Commit调用前的准备异常尝试Reset取消当前Builder，禁止同Builder重试。Reset正常完成时健康File可开始新Builder；Reset自身的资源异常不保证同一File可继续，也不保证保留原异常。最终Commit开始尝试发布后，任何异常都由外层catch保守标记永久fault；即使Push尚未证明写出，或Push完成但Return回收失败且TailOffset未推进，也不能认为安全恢复。普通ticket完整读每次两CRC；FrameInfo完整读复用创建时已验证Trailer/Key，每次检查PayloadCRC，仍受owner生命周期/fault约束；离线共享Write输入另须冻结。

---

## 5. RandomAccess → IByteSink 适配器 (Layer Adapter)

### 5.1 问题与设计目标

**问题**：`SinkReservableWriter` 接受 `IByteSink sink`，而 RBF 底层使用 `System.IO.RandomAccess.Write(SafeFileHandle, ReadOnlySpan<byte>, long offset)`。需要适配器桥接。

**设计目标**（来自 2026-01-11 重构）：
1. **无二级 buffer**：`IByteSink` 是推式接口，调用者持有数据，适配器只做转发
2. **最小状态**：仅需持有 `_writeOffset`，无需 ArrayPool 管理
3. **延迟写入**：依赖 `SinkReservableWriter` 的 reservation gating 控制 flush 时机
4. **尽量简单**：~25 行代码，直白语义

**畅谈会记录**：[2026-01-11-rbf-randomaccess-adapter.md](../../../agent-team/meeting/2026-01-11-rbf-randomaccess-adapter.md)

### 5.2 RandomAccessByteSink（推荐实现）

### spec [I-RBF-BYTESINK-IS-MINIMAL-FORWARDER] RandomAccessByteSink类型定义
see: @[A-RBF-IRBFFILE-SHAPE](rbf-interface.md)

`RandomAccessByteSink` 是 RandomAccess → IByteSink 的最小适配器，职责边界：
- **Push Forwarding**：将 `Push(ReadOnlySpan<byte>)` 直接转发到 `RandomAccess.Write`
- **Sequential Offset Accounting**：维护顺序写入游标 `_writeOffset`，每次 `Push` 后推进
- **不做**：reservation/backfill、合并、flush gating、buffer 管理（这些由 SinkReservableWriter 负责）

**实现参考**：
- 代码位置：`atelia/src/Rbf/Internal/RandomAccessByteSink.cs`
- **Concurrency**: 不提供并发保护；同一 File 的共享状态访问由调用方串行。单 Builder 限制本身不保证并发安全。
- **Error Handling**: I/O 异常直接抛出。

### 5.3 关键实现约束

### spec [I-RBF-SEQWRITER-HEADLEN-GUARD] HeadLen必须立即reserve
see: @[I-RBF-BUILDER-AUTO-ABORT-IMPL]
`RbfFrameBuilder` 创建时 MUST 立即 reserve HeadLen（4 字节），以确保 `SinkReservableWriter` 进入 buffered mode。
若先写 payload 后 reserve，会进入 passthrough mode 导致提前 flush（破坏 Zero I/O Abort）。

### spec [I-RBF-BYTESINK-PUSH-FORWARDS-AND-ADVANCES-OFFSET] Push推送语义
`Push(ReadOnlySpan<byte> data)` MUST 调用 `RandomAccess.Write(_file, data, _writeOffset)` 并推进 `_writeOffset += data.Length`。

**实现模式**：
在 `BeginFrame` 时，需立即通过 `SinkReservableWriter.ReserveSpan(4)` 预留 HeadLen 空间（填入 placeholder），确保 Writer 进入 buffered 模式，从而支持后续的 Zero I/O Abort。
注意：FrameTag 不在此处写入，已随 Wire Format 变更移至 Trailer。

### 5.4 错误处理

### spec [I-RBF-BYTESINK-ERROR-THROW] I/O异常直接抛出
`Push` 中的 `RandomAccess.Write` 失败时 MUST 直接抛出异常（`IOException` 或 `UnauthorizedAccessException`）。
符合 AteliaResult 规范的 Infra Fault 策略（基础设施故障用异常）。

### 5.5 Tradeoff 汇总

| 维度 | 优势 | 限制/风险 | 缓解措施 |
|------|------|----------|----------|
| **简单性** | 最小状态（~25 行代码） | - | - |
| **无 Buffer** | 消除 ArrayPool 管理 | 依赖 `SinkReservableWriter` 的 chunked buffer | `SinkReservableWriter` 已成熟 |
| **推式语义** | 与 `RandomAccess.Write` 完美匹配 | - | - |
| **Zero I/O Abort** | 由 `SinkReservableWriter` reservation 保证 | 需要 HeadLen 必须立即 reserve | 见 @[I-RBF-SEQWRITER-HEADLEN-GUARD] |
| **无 IDisposable** | 生命周期简化 | - | 由 Builder 管理 |
| **串行访问** | 同一 File/派生对象共享状态访问由调用方串行 | 多线程写入会破坏 offset 一致性 | 单 Builder 限制本身不是线程安全保证 |

### 5.6 当前实现与延期能力

1. **CRC32C计算时机**：EndAppend在XOR前遍历plaintext chunks计算PayloadCRC，完成含原wire U的TrailerCRC，再fused选键/变换并Commit；不是本轮待实现问题。
2. **异步版本**：未纳入本轮；真实需求另案，不预留新adapter或公共异步协议。

---

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.6 | 2026-01-24 | **Format对齐**：更新 `ScanReverse` 描述（TrailerCrc32C）；更新 `FrameInfo` 字段（TailMetaLength）；修正 `_BeginFrame` 伪代码（移除 FrameTag 头部写入） |
| 0.5 | 2026-01-17 | **ReadFrame 重构**：更新 §2 底层原语签名（RbfRawOps → RbfReadImpl/RbfWriteImpl）；新增 IRbfFrame/RbfPooledFrame 引用；参数名 ptr → ticket |
| 0.4 | 2026-01-11 | **适配器简化**：将 §5 从 `IBufferWriter` 适配器改为 `IByteSink` 适配器；删除 `SequentialRandomAccessBufferWriter`（~80 行）；新增 `RandomAccessByteSink`（~25 行）；删除 `@[I-RBF-SEQWRITER-TYPE]`、`@[I-RBF-SEQWRITER-ADVANCE-IMMEDIATE]`、`@[I-RBF-SEQWRITER-BUFFER-POOL]`、`@[I-RBF-SEQWRITER-DISPOSE-NOEXCEPT]`；新增 `@[I-RBF-BYTESINK-IS-MINIMAL-FORWARDER]`、`@[I-RBF-BYTESINK-PUSH-FORWARDS-AND-ADVANCES-OFFSET]`、`@[I-RBF-BYTESINK-ERROR-THROW]`；来自 [设计报告](../../../agent-team/handoffs/2026-01-11-randomaccess-bytesink-design.md) |
| 0.3 | 2026-01-11 | **RandomAccess 适配器设计**：新增 §5（SequentialRandomAccessBufferWriter）；定义 @[I-RBF-SEQWRITER-TYPE]、@[I-RBF-SEQWRITER-ADVANCE-IMMEDIATE]、@[I-RBF-SEQWRITER-BUFFER-POOL]、@[I-RBF-SEQWRITER-HEADLEN-GUARD]、@[I-RBF-SEQWRITER-ERROR-THROW]、@[I-RBF-SEQWRITER-DISPOSE-NOEXCEPT]；来自 [畅谈会](../../../agent-team/meeting/2026-01-11-rbf-randomaccess-adapter.md) 决议 |
| 0.2 | 2026-01-11 | **文档职能分离**：移除与 interface.md 重复的公开类型定义，改为引用；新增 Auto-Abort 实现路径条款 @[I-RBF-BUILDER-AUTO-ABORT-IMPL]；增加快速导航区块；明确为非规范性实现指南 |
| 0.1 | 2026-01-11 | 初始版本（Type Bone 骨架） |
