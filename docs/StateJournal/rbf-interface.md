# RBF Layer Interface Contract

> **状态**：Reviewed（复核通过）
> **版本**：0.10
> **创建日期**：2025-12-22
> **设计共识来源**：[agent-team/meeting/2025-12-21-rbf-layer-boundary.md](../../../agent-team/meeting/2025-12-21-rbf-layer-boundary.md)
> **复核会议**：[agent-team/meeting/2025-12-22-rbf-interface-review.md](../../../agent-team/meeting/2025-12-22-rbf-interface-review.md)

---

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 概述

本文档定义 RBF（Reversible Binary Framing）层与上层（StateJournal）之间的接口契约。

**设计原则**：
- RBF 是"二进制信封"——只关心如何安全封装 payload，不解释 payload 语义
- 上层只需依赖本接口文档，无需了解 RBF 内部实现细节
- 接口设计支持 zero-copy 热路径，但不强制

**文档关系**：

- `mvp-design-v2.md` **使用** `rbf-interface.md`（上层 StateJournal 使用接口契约）
- `rbf-format.md` **实现** `rbf-interface.md`（下层 RBF 线格式实现接口定义）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| `mvp-design-v2.md` | Layer 1 (StateJournal) | FrameTag 取值, ObjectKind 等业务语义 |
| `rbf-interface.md` | Layer 0/1 边界（本文档） | IRbfFramer, IRbfScanner 等接口契约 |
| `rbf-format.md` | Layer 0 (RBF) | RBF 二进制线格式规范（wire format） |

---

## 2. 术语表（Layer 0）

> 本节定义 RBF 层的独立术语。上层术语（如 FrameTag 取值）在 mvp-design-v2.md 中定义。

### 2.1 FrameTag

**`[F-FRAMETAG-DEFINITION]`**

> **FrameTag** 是 4 字节的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。

```csharp
public readonly record struct FrameTag(uint Value);
```

**保留值**：无。RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

> **设计理由**：4B 的 FrameTag 保持 Payload 4B 对齐，并支持 fourCC 风格的类型标识（如 `META`, `OBJV`）。

### 2.2 FrameStatus

**`[F-FRAMESTATUS-DEFINITION]`**

> **FrameStatus** 是帧的有效性标记（Layer 0 元信息），存储在帧尾部。

```csharp
public enum FrameStatus : byte
{
    Valid = 0x00,      // 正常帧
    Tombstone = 0xFF   // 墓碑帧（Auto-Abort / 逻辑删除）
}
```

**`[S-RBF-TOMBSTONE-VISIBLE]`**

> `IRbfScanner` MUST 产出所有通过 framing/CRC 校验的帧，包括 Tombstone 帧。

**`[S-STATEJOURNAL-TOMBSTONE-SKIP]`**

> 上层 Record Reader（StateJournal）MUST 忽略 `FrameStatus.Tombstone` 帧，不将其解释为业务记录。

> **设计理由**：
> - Scanner 是“原始帧扫描器”，职责边界清晰；
> - Tombstone 可见性对诊断/调试有价值；
> - 过滤责任在 Layer 1，不在 Layer 0。

### 2.3 Address64

**`[F-ADDRESS64-DEFINITION]`**

> **Address64** 是 8 字节 LE 编码的文件偏移量，指向一个 Frame 的起始位置（HeadLen 字段起点）。

```csharp
public readonly record struct Address64(ulong Value) {
    public static readonly Address64 Null = new(0);
    public bool IsNull => Value == 0;
}
```

**约束**：
- **`[F-ADDRESS64-ALIGNMENT]`**：有效 Address64 MUST 4 字节对齐（`Value % 4 == 0`）
- **`[F-ADDRESS64-NULL]`**：`Value == 0` 表示 null（无效地址）

### 2.4 Frame

> **Frame** 是 RBF 的基本 I/O 单元，由 Header + Payload + Trailer 组成。

本接口文档不定义 Frame 的内部结构（将在 rbf-format.md 中定义）。上层只需知道：

- 每个 Frame 有一个 `FrameTag` 和 `Payload`
- Frame 写入后返回其 `Address64`
- Frame 读取通过 `Address64` 定位

---

## 3. 写入接口

### 3.1 IRbfFramer

**`[A-RBF-FRAMER-INTERFACE]`**

```csharp
/// <summary>
/// RBF 帧写入器。负责将 payload 封装为 Frame 并追加到日志文件。
/// </summary>
/// <remarks>
/// <para><b>线程安全</b>：非线程安全，单生产者使用。</para>
/// <para><b>并发约束</b>：同一时刻最多 1 个 open RbfFrameBuilder。</para>
/// </remarks>
public interface IRbfFramer
{
    /// <summary>
    /// 追加一个完整的帧（简单场景：payload 已就绪）。
    /// </summary>
    /// <param name="tag">帧类型标识符</param>
    /// <param name="payload">帧负载（可为空）</param>
    /// <returns>写入的帧起始地址</returns>
    Address64 Append(FrameTag tag, ReadOnlySpan<byte> payload);
    
    /// <summary>
    /// 开始构建一个帧（高级场景：流式写入或需要 payload 内回填）。
    /// </summary>
    /// <param name="tag">帧类型标识符</param>
    /// <returns>帧构建器（必须 Commit 或 Dispose）</returns>
    RbfFrameBuilder BeginFrame(FrameTag tag);
    
    /// <summary>
    /// 将 RBF 缓冲数据推送到底层 Writer/Stream。
    /// </summary>
    /// <remarks>
    /// <para><b>不承诺 Durability</b>：本方法仅保证 RBF 层的缓冲被推送到下层，
    /// 不保证数据持久化到物理介质。</para>
    /// <para><b>上层责任</b>：如需 durable commit（如 StateJournal 的 data→meta 顺序），
    /// 由上层在其持有的底层句柄上执行 durable flush。</para>
    /// </remarks>
    void Flush();
}
```

### 3.2 RbfFrameBuilder

**`[A-RBF-FRAME-BUILDER]`**

```csharp
/// <summary>
/// 帧构建器。支持流式写入 payload，完成后自动回填 header/CRC。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：必须调用 <see cref="Commit"/> 或 <see cref="Dispose"/>。</para>
/// <para><b>Auto-Abort（Optimistic Clean Abort）</b>：若未 Commit 就 Dispose，
/// 逻辑上该帧视为不存在。物理实现可能是 Zero I/O（若底层支持 Reservation 回滚）
/// 或写入 Tombstone 帧（否则）。</para>
/// </remarks>
public ref struct RbfFrameBuilder
{
    /// <summary>
    /// Payload 写入器（标准接口，满足大多数序列化需求）。
    /// </summary>
    public IBufferWriter<byte> Payload { get; }
    
    /// <summary>
    /// 可预留的 Payload 写入器（可选，供需要 payload 内回填的 codec 使用）。
    /// </summary>
    /// <remarks>
    /// <para>若底层实现不支持，返回 null。上层 codec（如 DiffPayload）可用此接口
    /// 实现 PairCount 等字段的延后回填。</para>
    /// <para><b>与 Auto-Abort 的关系</b>：若非 null 且底层支持 Reservation 回滚，
    /// Abort 时可实现 Zero I/O（不写任何字节）。</para>
    /// </remarks>
    public IReservableBufferWriter? ReservablePayload { get; }
    
    /// <summary>
    /// 提交帧。回填 header/CRC，返回帧起始地址。
    /// </summary>
    /// <returns>写入的帧起始地址</returns>
    /// <exception cref="InvalidOperationException">重复调用 Commit</exception>
    public Address64 Commit();
    
    /// <summary>
    /// 释放构建器。若未 Commit，自动执行 Auto-Abort。
    /// </summary>
    public void Dispose();
}
```

**关键语义**：

**`[S-RBF-BUILDER-AUTO-ABORT]`**（Optimistic Clean Abort）

> 若 `RbfFrameBuilder` 未调用 `Commit()` 就执行 `Dispose()`：
>
> **逻辑语义**（对外可观测）：
> - 该帧视为**不存在**（logical non-existence）
> - 上层 Record Reader 遍历时 MUST 不会看到此帧作为业务记录
>
> **物理实现**（双路径）：
> - **SHOULD（Zero I/O）**：若底层支持 Reservation 且未发生数据外泄（HeadLen 作为首个 Reservation 未提交），丢弃未提交数据，不写入任何字节
> - **MUST（Tombstone 墓碑帧）**：否则，将帧的 `FrameStatus` 设为 `Tombstone (0xFF)`，完成帧写入
>
> **Tombstone 帧的 FrameTag**：
> - SHOULD 保留原 FrameTag 值（供诊断用）
> - 上层 MUST NOT 依赖 Tombstone 帧的 FrameTag 值
>
> **后置条件**：
> - Abort 产生的帧 MUST 通过 framing/CRC 校验
> - `Dispose()` 后，底层 Writer MUST 可继续写入后续帧
> - 后续 `Append()` / `BeginFrame()` 调用 MUST 成功

此机制防止上层异常导致 Writer 死锁，同时在可能时优化为零 I/O。

**`[S-RBF-FRAMER-NO-FSYNC]`**

> `IRbfFramer.Flush()` MUST NOT 执行 fsync 操作。
> Fsync 策略（如 StateJournal 的 data→meta 顺序）由上层控制。

**`[S-RBF-BUILDER-SINGLE-OPEN]`**

> 同一 `IRbfFramer` 实例同时最多允许 1 个 open `RbfFrameBuilder`。
> 在前一个 Builder 完成（Commit 或 Dispose）前调用 `BeginFrame()` MUST 抛出 `InvalidOperationException`。

---

## 4. 读取接口

### 4.1 IRbfScanner

**`[A-RBF-SCANNER-INTERFACE]`**

```csharp
/// <summary>
/// RBF 帧扫描器。支持随机读取和逆向扫描。
/// </summary>
public interface IRbfScanner
{
    /// <summary>
    /// 读取指定地址的帧。
    /// </summary>
    /// <param name="address">帧起始地址</param>
    /// <param name="frame">输出：帧内容（生命周期受限于底层缓冲区）</param>
    /// <returns>是否成功读取</returns>
    bool TryReadAt(Address64 address, out RbfFrame frame);
    
    /// <summary>
    /// 从文件尾部逆向扫描所有帧。
    /// </summary>
    /// <returns>帧枚举器（从尾到头）</returns>
    RbfReverseEnumerable ScanReverse();
}
```

### 4.2 RbfFrame

**`[A-RBF-FRAME-REF-STRUCT]`**

```csharp
/// <summary>
/// 帧视图（ref struct，生命周期受限）。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：Payload 的 Span 生命周期绑定到底层缓冲区（如 mmap view）。
/// 若需持久化，必须显式拷贝。</para>
/// <para><b>设计理由</b>：ref struct 的限制是"护栏"，防止 use-after-free。</para>
/// </remarks>
public readonly ref struct RbfFrame
{
    /// <summary>帧类型标识符</summary>
    public FrameTag Tag { get; }
    
    /// <summary>帧状态（Valid / Tombstone）</summary>
    public FrameStatus Status { get; }
    
    /// <summary>帧负载（生命周期受限）</summary>
    public ReadOnlySpan<byte> Payload { get; }
    
    /// <summary>帧起始地址</summary>
    public Address64 Address { get; }
    
    /// <summary>
    /// 拷贝 Payload 到新分配的数组（显式分配，生命周期脱离底层缓冲区）。
    /// </summary>
    public byte[] PayloadToArray() => Payload.ToArray();
}
```

---

## 5. 使用示例（Informative）

> 本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义，详见 [mvp-design-v2.md](mvp-design-v2.md)。

```csharp
// 写入帧
public Address64 WriteFrame(IRbfFramer framer, uint tagValue, byte[] payload)
{
    using var frame = framer.BeginFrame(new FrameTag(tagValue));
    payload.CopyTo(frame.Payload);
    return frame.Commit();
}

// 读取帧
public void ProcessFrame(IRbfScanner scanner, Address64 addr)
{
    if (!scanner.TryReadAt(addr, out var frame)) return;
    
    // 先检查帧状态，跳过墓碑帧（上层策略）
    if (frame.Status == FrameStatus.Tombstone) return;
    
    // 上层根据 frame.Tag.Value 决定如何解析 frame.Payload
    // RBF 层不解释 FrameTag 的语义
    ProcessPayload(frame.Tag.Value, frame.Payload);
}
```

---

## 6. 条款索引

| 条款 ID | 名称 | 类别 |
|---------|------|------|
| `[F-FRAMETAG-DEFINITION]` | FrameTag 定义 | 术语 |
| `[F-FRAMESTATUS-DEFINITION]` | FrameStatus 定义 | 术语 |
| `[F-ADDRESS64-DEFINITION]` | Address64 定义 | 术语 |
| `[F-ADDRESS64-ALIGNMENT]` | Address64 对齐 | 格式 |
| `[F-ADDRESS64-NULL]` | Address64 空值 | 格式 |
| `[A-RBF-FRAMER-INTERFACE]` | IRbfFramer 接口 | API |
| `[A-RBF-FRAME-BUILDER]` | RbfFrameBuilder 接口 | API |
| `[A-RBF-SCANNER-INTERFACE]` | IRbfScanner 接口 | API |
| `[A-RBF-FRAME-REF-STRUCT]` | RbfFrame 结构 | API |
| `[S-RBF-BUILDER-AUTO-ABORT]` | Builder Auto-Abort (Optimistic Clean Abort) | 语义 |
| `[S-RBF-BUILDER-SINGLE-OPEN]` | Builder 单开 | 语义 |
| `[S-RBF-FRAMER-NO-FSYNC]` | Flush 不含 Fsync | 语义 |
| `[S-RBF-TOMBSTONE-VISIBLE]` | Tombstone 帧可见 | 语义 |

> **已移除的条款**：`[S-STATEJOURNAL-FRAMETAG-MAPPING]` 和 `[S-STATEJOURNAL-TOMBSTONE-SKIP]` 已移至 [mvp-design-v2.md](mvp-design-v2.md)，因为它们是上层（StateJournal）的语义定义，不属于 RBF 接口契约。

---

## 7. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.11 | 2025-12-25 | §1 文档关系：ASCII 框图改为关系列表+表格；修正语义错误（接口不依赖实现）；删除过时"待拆分"标注（[畅谈会决议](../../../agent-team/meeting/2025-12-25-llm-friendly-notation-field-test.md)）|
| 0.10 | 2025-12-24 | §9 移除过时条目（FrameTag 对齐）；有序列表改为无序列表 |
| 0.9 | 2025-12-24 | **术语统一**：Padding 统一为 Tombstone，消除混用（Auto-Abort 描述、已解决问题条目） |
| 0.8 | 2025-12-24 | 修复 RbfFrame 结构遗漏的 `Status` 属性（配合 v0.5 的 FrameStatus 引入） |
| 0.7 | 2025-12-24 | **重构**：移除 `[S-STATEJOURNAL-FRAMETAG-MAPPING]` 和 `[S-STATEJOURNAL-TOMBSTONE-SKIP]` 到 mvp-design-v2.md；§5 改为纯示例（Informative）；保持 RBF 层语义无关性 |
| 0.6 | 2025-12-24 | FrameTag 采用 16/16 位段编码（RecordType/SubType）；ObjectKind 从 Payload 移至 FrameTag 高 16 位 |
| 0.5 | 2025-12-24 | **Breaking**：墓碑帧机制从 FrameTag=0 改为 FrameStatus；新增 `[F-FRAMESTATUS-DEFINITION]`/`[S-RBF-TOMBSTONE-VISIBLE]`；RbfFrame 增加 Status 属性；Auto-Abort 改为写 Tombstone 状态 |
| 0.4 | 2025-12-24 | **Breaking**：FrameTag 从 1B 扩展为 4B（`byte` → `uint`） |
| 0.3 | 2025-12-22 | 命名重构：ELOG → RBF (Reversible Binary Framing) |
| 0.2 | 2025-12-22 | P0/P1 修订：Auto-Abort 改为 Optimistic Clean Abort |
| 0.1 | 2025-12-22 | 初稿，基于畅谈会共识 |

---

## 8. 已解决问题

> 复核会议解决的问题：

1. ~~**Flush 语义**~~：已明确 `Flush()` 不承诺 durability，由上层控制 fsync 顺序
2. ~~**Auto-Abort 机制**~~：已改为 Optimistic Clean Abort（Zero I/O 优先，Tombstone 保底）
3. ~~**Tombstone 责任边界**~~：已明确 Scanner 产出所有帧，上层负责忽略 Tombstone 帧

## 9. 待实现时确认

> 以下问题可在实现阶段确认：

- **错误处理**：TryReadAt 失败时是否需要 `RbfReadStatus`（P2）
- **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）
- **Address64 高位保留**：是否需要预留高 8 位供多文件扩展？

---
