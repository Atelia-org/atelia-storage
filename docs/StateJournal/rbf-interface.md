# RBF Layer Interface Contract

> **状态**：Reviewed（复核通过）
> **版本**：0.4
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

```
┌─────────────────────────────────────┐
│  mvp-design-v2.md (StateJournal)    │
│  - 依赖本接口文档                    │
│  - 定义 RecordKind, ObjectKind 等   │
└─────────────────┬───────────────────┘
                  │ 依赖
┌─────────────────▼───────────────────┐
│  rbf-interface.md (本文档)           │
│  - Layer 0/1 的对接契约              │
│  - 定义 FrameTag, Address64 等       │
└─────────────────┬───────────────────┘
                  │ 待拆分后
┌─────────────────▼───────────────────┐
│  rbf-format.md (Layer 0)             │
│  - RBF 二进制线格式规范（wire）       │
│  - 作为 Layer 0 的 SSOT              │
└─────────────────────────────────────┘
```

---

## 2. 术语表（Layer 0）

> 本节定义 RBF 层的独立术语。上层术语（如 RecordKind）在 mvp-design-v2.md 中定义。

### 2.1 FrameTag

**`[F-FRAMETAG-DEFINITION]`**

> **FrameTag** 是 4 字节的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。

```csharp
public readonly record struct FrameTag(uint Value);
```

**保留值**：

| 值 | 名称 | 语义 |
|----|------|------|
| `0x00000000` | Padding | 可丢弃帧（用于 Auto-Abort 落盘） |
| `0x00000001`-`0xFFFFFFFF` | — | 由上层定义 |

> **设计理由**：4B 的 FrameTag 保持 Payload 4B 对齐，并支持 fourCC 风格的类型标识（如 `META`, `OBJV`）。

**`[F-FRAMETAG-PADDING-VISIBLE]`**：`IRbfScanner` MUST 产出所有帧，包括 `FrameTag.Padding`。

**`[S-STATEJOURNAL-PADDING-SKIP]`**：上层 Record Reader（StateJournal）MUST 忽略 `FrameTag.Padding` 帧，不将其解释为业务记录。

> **设计理由**：Scanner 是"原始帧扫描器"，职责边界清晰；Padding 可见性对诊断/调试有价值。

### 2.2 Address64

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

### 2.3 Frame

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
/// 或写入 Padding 墓碑帧（否则）。</para>
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
> - **MUST（Padding 墓碑）**：否则，将帧的 FrameTag 覆写为 `Padding (0x00000000)`，完成帧写入
>
> **后置条件**：
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

## 5. 上层映射（StateJournal）

> 本节描述上层如何使用 RBF 接口，但不属于 RBF 层的职责。

### 5.1 FrameTag ↔ RecordKind 映射

**`[S-STATEJOURNAL-FRAMETAG-MAPPING]`**

StateJournal 定义以下 FrameTag 值：

| FrameTag | Record 类型 | 描述 |
|----------|-------------|------|
| `0x00000000` | Padding | RBF 保留，上层 MUST 跳过 |
| `0x00000001` | ObjectVersionRecord | 对象版本记录（data payload） |
| `0x00000002` | MetaCommitRecord | 提交元数据记录（meta payload） |
| `0x00000003`-`0xFFFFFFFF` | — | 未来扩展 |

> **FrameTag 是独立字段**：
> - FrameTag 是 FrameBytes 的独立 4B 字段（参见 rbf-format.md `[F-FRAMETAG-WIRE-ENCODING]`）
> - StateJournal 通过 FrameTag 区分 Record 类型，Payload 内不再包含额外的类型字节
> - 此设计与 mvp-design-v2.md §3.2.1/§3.2.2 的定义一致（2025-12-24 对齐）

### 5.2 使用示例

```csharp
// 写入 ObjectVersionRecord
public Address64 WriteObjectVersion(IRbfFramer framer, ObjectVersionPayload payload)
{
    using var frame = framer.BeginFrame(new FrameTag(0x00000001));
    
    // 使用 ReservablePayload 实现 PairCount 回填
    if (frame.ReservablePayload is { } reservable)
    {
        payload.SerializeTo(reservable);
    }
    else
    {
        // fallback: 预先序列化
        payload.SerializeTo(frame.Payload);
    }
    
    return frame.Commit();
}

// 读取
public void ProcessFrame(IRbfScanner scanner, Address64 addr)
{
    if (!scanner.TryReadAt(addr, out var frame)) return;
    
    if (frame.Tag.Value == 0x00000000) return; // 跳过 Padding
    
    switch (frame.Tag.Value)
    {
        case 0x00000001:
            ProcessObjectVersion(frame.Payload);
            break;
        case 0x00000002:
            ProcessCommit(frame.Payload);
            break;
    }
}
```

---

## 6. 条款索引

| 条款 ID | 名称 | 类别 |
|---------|------|------|
| `[F-FRAMETAG-DEFINITION]` | FrameTag 定义 | 术语 |
| `[F-FRAMETAG-PADDING-VISIBLE]` | Padding 帧可见 | 语义 |
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
| `[S-STATEJOURNAL-FRAMETAG-MAPPING]` | FrameTag 映射 | 映射 |
| `[S-STATEJOURNAL-PADDING-SKIP]` | 上层跳过 Padding | 映射 |

---

## 7. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.1 | 2025-12-22 | 初稿，基于畅谈会共识 |
| 0.2 | 2025-12-22 | P0/P1 修订：Auto-Abort 改为 Optimistic Clean Abort；Padding 责任边界明确；Flush 不承诺 durability |
| 0.3 | 2025-12-22 | 命名重构：ELOG → RBF (Reversible Binary Framing) |
| 0.4 | 2025-12-24 | **Breaking**：FrameTag 从 1B 扩展为 4B（`byte` → `uint`）；Padding 值从 `0x00` 变为 `0x00000000` |

---

## 8. 已解决问题

> 复核会议解决的问题：

1. ~~**Flush 语义**~~：已明确 `Flush()` 不承诺 durability，由上层控制 fsync 顺序
2. ~~**Auto-Abort 机制**~~：已改为 Optimistic Clean Abort（Zero I/O 优先，Padding 保底）
3. ~~**Padding 责任边界**~~：已明确 Scanner 产出所有帧，上层负责忽略 Padding

## 9. 待实现时确认

> 以下问题可在实现阶段确认：

1. **FrameTag 具体值**：与 mvp-design-v2.md 的 RecordKind 如何精确对齐？
2. **错误处理**：TryReadAt 失败时是否需要 `RbfReadStatus`（P2）
3. **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）
4. **Address64 高位保留**：是否需要预留高 8 位供多文件扩展？

---
