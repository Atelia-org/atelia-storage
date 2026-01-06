---
docId: "rbf-interface"
title: "RBF Layer Interface Contract"
produce_by:
    - "wish/W-0006-rbf-sizedptr/wish.md"
---

# RBF Layer Interface Contract

> **状态**：Reviewed（复核通过）
> **版本**：0.19
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

- [`mvp-design-v2.md`](../StateJournal/mvp-design-v2.md) **使用** `rbf-interface.md`（上层 StateJournal 使用接口契约）
- `rbf-format.md` **实现** `rbf-interface.md`（下层 RBF 线格式实现接口定义）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| [`mvp-design-v2.md`](../StateJournal/mvp-design-v2.md) | Layer 1 (StateJournal) | FrameTag 取值, ObjectKind 等业务语义 |
| `rbf-interface.md` | Layer 0/1 边界（本文档） | IRbfFramer, IRbfScanner 等接口契约 |
| `rbf-format.md` | Layer 0 (RBF) | RBF 二进制线格式规范（wire format） |

---

## 2. 术语表（Layer 0）

> 本节定义 RBF 层的独立术语。上层术语（如 FrameTag 取值）在 mvp-design-v2.md 中定义。

### 2.1 FrameTag

> **FrameTag** 是 4 字节（`uint`）的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。
>
> **三层视角**：
> - **存储层**：`uint`（线格式中的 4 字节 LE 字段）
> - **接口层**：`uint`（本文档定义的 API 参数/返回类型）
> - **应用层**：上层可自由选择 enum 或其他类型进行打包/解包

**保留值**：无。RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

> **设计理由**：4B 的 FrameTag 保持 Payload 4B 对齐，并支持 fourCC 风格的类型标识（如 `META`, `OBJV`）。


### 2.2 Tombstone（墓碑帧）

**`[F-TOMBSTONE-DEFINITION]`**

> **Tombstone**（墓碑帧）是帧的有效性标记（Layer 0 元信息），表示该帧已被逻辑删除或是 Auto-Abort 的产物。

RbfFrame 通过 `bool IsTombstone` 属性暴露此状态，上层无需关心底层编码细节（编码定义见 rbf-format.md）。

**`[S-RBF-TOMBSTONE-VISIBLE]`**

> `IRbfScanner` MUST 产出所有通过 framing/CRC 校验的帧，包括 Tombstone 帧。

> **设计理由**：
> - Scanner 是"原始帧扫描器"，职责边界清晰；
> - Tombstone 可见性对诊断/调试有价值；
> - 过滤责任在 Layer 1（StateJournal），不在 Layer 0（RBF）；
> - 接口层隐藏编码细节，上层无需知道 wire format 的具体字节值。

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

> **Frame** 是 RBF 的基本 I/O 单元。

上层只需知道：

- 每个 Frame 有一个 `FrameTag`、`Payload` 和 `IsTombstone` 状态
- Frame 写入后返回其 `Address64`
- Frame 读取通过 `Address64` 定位

> Frame 的内部结构（wire format）在 rbf-format.md 中定义，接口层无需关心。

### 2.5 RbfReadError

**`[F-RBF-READ-ERROR-DEFINITION]`**

> **RbfReadError** 是 `ReadAt` 操作的失败原因类型，继承自 `AteliaError`。

```csharp
/// <summary>
/// RBF 读取操作失败的错误类型。
/// </summary>
public class RbfReadError : AteliaError {
    /// <summary>错误类别</summary>
    public RbfReadErrorKind Kind { get; }
    
    /// <summary>相关地址（如适用）</summary>
    public Address64? Address { get; }
    
    // 静态工厂方法
    public static RbfReadError AddressOutOfRange(Address64 address);
    public static RbfReadError CrcMismatch(Address64 address);
    public static RbfReadError FramingError(Address64 address, string detail);
}

/// <summary>
/// RBF 读取错误的类别。
/// </summary>
public enum RbfReadErrorKind {
    /// <summary>地址越界（超出文件范围或未对齐）</summary>
    AddressOutOfRange,
    
    /// <summary>CRC32C 校验失败</summary>
    CrcMismatch,
    
    /// <summary>帧结构损坏（HeadLen/TailLen 不一致、Magic 错误等）</summary>
    FramingError
}
```

**设计理由**：
- 调用者可通过 `Kind` 属性区分失败原因，做出不同处理
- `Address` 属性便于诊断和日志记录
- 继承 `AteliaError` 融入统一错误体系

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
public interface IRbfFramer {
    /// <summary>
    /// 追加一个完整的帧（简单场景：payload 已就绪）。
    /// </summary>
    /// <param name="tag">帧类型标识符（4 字节）</param>
    /// <param name="payload">帧负载（可为空）</param>
    /// <returns>写入的帧起始地址</returns>
    Address64 Append(uint tag, ReadOnlySpan<byte> payload);
    
    /// <summary>
    /// 开始构建一个帧（高级场景：流式写入或需要 payload 内回填）。
    /// </summary>
    /// <param name="tag">帧类型标识符（4 字节）</param>
    /// <returns>帧构建器（必须 Commit 或 Dispose）</returns>
    RbfFrameBuilder BeginFrame(uint tag);
    
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
/// 帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：调用方 MUST 调用 <see cref="Commit"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。</para>
/// <para><b>Auto-Abort（Optimistic Clean Abort）</b>：若未 Commit 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 <b>[S-RBF-BUILDER-AUTO-ABORT]</b>。</para>
/// </remarks>
public ref struct RbfFrameBuilder {
    /// <summary>
    /// Payload 写入器。
    /// </summary>
    /// <remarks>
    /// <para>该写入器实现 <see cref="IBufferWriter{Byte}"/>，因此可用于绝大多数序列化场景。</para>
    /// <para>此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。</para>
    /// <para>接口定义见 <c>atelia/src/Data/IReservableBufferWriter.cs</c>。</para>
    /// <para><b>注意</b>：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 <b>[S-RBF-BUILDER-AUTO-ABORT]</b>。</para>
    /// </remarks>
    public IReservableBufferWriter Payload { get; }
    
    /// <summary>
    /// 提交帧。回填 header/CRC，返回帧起始地址。
    /// </summary>
    /// <returns>写入的帧起始地址</returns>
    /// <exception cref="InvalidOperationException">重复调用 Commit</exception>
    public Address64 Commit();
    
    /// <summary>
    /// 释放构建器。若未 Commit，自动执行 Auto-Abort。
    /// </summary>
    /// <remarks>
    /// <para><b>Auto-Abort 分支约束</b>：<see cref="Dispose"/> 在 Auto-Abort 分支 MUST NOT 抛出异常
    /// （除非出现不可恢复的不变量破坏），并且必须让 framer 回到可继续写状态。</para>
    /// </remarks>
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
> - **SHOULD（Zero I/O）**：若实现能保证 open builder 期间 payload 不外泄、且在 `Dispose()` 时可丢弃未提交 payload（无论通过 reservation rollback、内部 reset或其他等价机制），则 SHOULD 走 Zero I/O
> - **MUST（Tombstone 墓碑帧）**：否则，将帧标记为 Tombstone（`IsTombstone == true`），完成帧写入
>
> **重要说明**：`Payload` 的类型为 `IReservableBufferWriter` 并不承诺 Zero I/O 必然可用。
> Zero I/O 是实现优化，不是类型承诺。
>
> **Tombstone 帧的 FrameTag**：
> - SHOULD 保留原 FrameTag 值（供诊断用）
> - 上层 MUST NOT 依赖 Tombstone 帧的 FrameTag 值
>
> **后置条件**：
> - Abort 产生的帧 MUST 通过 framing/CRC 校验
> - `Dispose()` 后，底层 Writer MUST 可继续写入后续帧
> - 后续 `Append()` / `BeginFrame()` 调用 MUST 成功
> - `Dispose()` 在 Auto-Abort 分支 MUST NOT 抛出异常（除非出现不可恢复的不变量破坏）

此机制防止上层异常导致 Writer 死锁，同时在可能时优化为零 I/O。

**`[S-RBF-BUILDER-FLUSH-NO-LEAK]`**

> 当存在 open `RbfFrameBuilder` 时，`IRbfFramer.Flush()` MUST NOT 使任何未 `Commit()` 的字节对下游/扫描器可观测。

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
public interface IRbfScanner {
    /// <summary>
    /// 读取指定地址的帧。
    /// </summary>
    /// <param name="address">帧起始地址</param>
    /// <returns>
    /// 成功时返回帧内容（生命周期受限于底层缓冲区）；
    /// 失败时返回 <see cref="RbfReadError"/> 说明原因。
    /// </returns>
    /// <remarks>
    /// 可能的失败原因：
    /// <list type="bullet">
    ///   <item><description>地址越界（<see cref="RbfReadError.AddressOutOfRange"/>）</description></item>
    ///   <item><description>CRC 校验失败（<see cref="RbfReadError.CrcMismatch"/>）</description></item>
    ///   <item><description>帧结构损坏（<see cref="RbfReadError.FramingError"/>）</description></item>
    /// </list>
    /// </remarks>
    AteliaResult<RbfFrame> ReadAt(Address64 address);
    
    /// <summary>
    /// 从文件尾部逆向扫描所有帧。
    /// </summary>
    /// <returns>帧序列（从尾到头）</returns>
    /// <remarks>
    /// <para>返回 duck-typed 可枚举序列，支持 foreach。</para>
    /// <para><b>不实现 IEnumerable</b>：因 RbfFrame 是 ref struct，无法作为泛型接口类型参数。</para>
    /// <para><b>LINQ 不可用</b>：若需持久化数据，请调用 <see cref="RbfFrame.PayloadToArray"/>。</para>
    /// </remarks>
    RbfReverseSequence ScanReverse();
}
```

### 4.2 RbfReverseSequence

**`[A-RBF-REVERSE-SEQUENCE]`**

```csharp
/// <summary>
/// 逆向扫描的帧序列（瞬态，stack-only）。
/// </summary>
/// <remarks>
/// <para>实现 duck-typed 枚举器模式，支持 foreach 语法。</para>
/// <para><b>不实现 IEnumerable&lt;T&gt;</b>：因 RbfFrame 是 ref struct。</para>
/// <para><b>生命周期</b>：序列本身是 ref struct，不能存储到字段或跨 await 边界。</para>
/// </remarks>
public readonly ref struct RbfReverseSequence {
    /// <summary>
    /// 获取枚举器（duck-typed，支持 foreach）。
    /// </summary>
    public Enumerator GetEnumerator();
    
    /// <summary>
    /// 逆向扫描枚举器。
    /// </summary>
    public ref struct Enumerator {
        /// <summary>当前帧（生命周期受限于下次 MoveNext 调用）</summary>
        public RbfFrame Current { get; }
        
        /// <summary>移动到下一帧（实际是前一帧，因逆向扫描）</summary>
        public bool MoveNext();
    }
}
```

**`[S-RBF-SCANREVERSE-NO-IENUMERABLE]`**

> 由于 `RbfFrame` 为 `ref struct`，`ScanReverse()` 返回类型 MUST NOT 承诺或实现 `IEnumerable<RbfFrame>` / `IEnumerator<RbfFrame>`。
> 规范文本 MUST NOT 要求调用方使用 LINQ 直接消费。

**`[S-RBF-SCANREVERSE-EMPTY-IS-OK]`**

> 当扫描范围无有效帧时，`ScanReverse()` 返回序列 MUST 产生 0 个元素且不得抛出异常。
> 枚举器首次 `MoveNext()` 返回 `false`。
>
> **适用场景**：
> - `fileLength < 4`（不完整文件，fail-soft 返回空）
> - `fileLength == 4`（仅 Genesis Fence，无 Frame）
> - 所有 Frame 均校验失败（损坏数据，Resync 后无有效帧）

**`[S-RBF-SCANREVERSE-CURRENT-LIFETIME]`**

> 枚举器 `Current` 返回的 `RbfFrame` 视图的生命周期 MUST 不超过下一次 `MoveNext()` 调用。
> 调用方若需持久化数据 MUST 显式拷贝（例如调用 `PayloadToArray()`）。

**`[S-RBF-SCANREVERSE-CONCURRENT-MUTATION]`**

> 若底层文件/映射在枚举期间被修改，行为为**未定义**。
> 调用方 MUST 在稳定快照上使用 `ScanReverse()`。
> 实现 MAY 选择 fail-fast（抛出异常）但不作为规范要求。

**`[S-RBF-SCANREVERSE-MULTI-GETENUM]`**

> 对同一个 `ScanReverse()` 返回值，多次调用 `GetEnumerator()` MUST 返回互不干扰的枚举器实例。
> 每个枚举器实例从同一扫描窗口的尾部开始。

### 4.3 RbfFrame

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
public readonly ref struct RbfFrame {
    /// <summary>帧类型标识符（4 字节）</summary>
    public uint Tag { get; }
    
    /// <summary>是否为墓碑帧（逻辑删除 / Auto-Abort 产物）</summary>
    public bool IsTombstone { get; }
    
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

> 本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义，详见 [mvp-design-v2.md](../StateJournal/mvp-design-v2.md)。

```csharp
// 写入帧
public Address64 WriteFrame(IRbfFramer framer, uint tag, byte[] payload) {
    using var builder = framer.BeginFrame(tag);
    builder.Payload.Write(payload);  // IBufferWriter<byte>.Write 扩展方法
    return builder.Commit();
}

// 读取帧（使用 AteliaResult）
public void ProcessFrame(IRbfScanner scanner, Address64 addr) {
    var result = scanner.ReadAt(addr);
    
    // 检查读取是否成功
    if (result.IsFailure) {
        // 可区分失败原因
        if (result.Error is RbfReadError { Kind: RbfReadErrorKind.CrcMismatch }) {
            Log.Warn($"CRC mismatch at {addr}");
        }
        return;
    }
    
    var frame = result.Value;
    
    // 先检查帧状态，跳过墓碑帧（上层策略）
    if (frame.IsTombstone) return;
    
    // 上层根据 frame.Tag 决定如何解析 frame.Payload
    // RBF 层不解释 FrameTag 的语义
    ProcessPayload(frame.Tag, frame.Payload);
}
```

---

## 6. 条款索引

| 条款 ID | 名称 | 类别 |
|---------|------|------|
| `[F-TOMBSTONE-DEFINITION]` | Tombstone 定义 | 术语 |
| `[F-ADDRESS64-DEFINITION]` | Address64 定义 | 术语 |
| `[F-ADDRESS64-ALIGNMENT]` | Address64 对齐 | 格式 |
| `[F-ADDRESS64-NULL]` | Address64 空值 | 格式 |
| `[F-RBF-READ-ERROR-DEFINITION]` | RbfReadError 定义 | 术语 |
| `[A-RBF-FRAMER-INTERFACE]` | IRbfFramer 接口 | API |
| `[A-RBF-FRAME-BUILDER]` | RbfFrameBuilder 接口 | API |
| `[A-RBF-SCANNER-INTERFACE]` | IRbfScanner 接口 | API |
| `[A-RBF-REVERSE-SEQUENCE]` | RbfReverseSequence 结构 | API |
| `[A-RBF-FRAME-REF-STRUCT]` | RbfFrame 结构 | API |
| `[S-RBF-BUILDER-AUTO-ABORT]` | Builder Auto-Abort (Optimistic Clean Abort) | 语义 |
| `[S-RBF-BUILDER-FLUSH-NO-LEAK]` | open builder 期间 Flush 不外泄 | 语义 |
| `[S-RBF-BUILDER-SINGLE-OPEN]` | Builder 单开 | 语义 |
| `[S-RBF-FRAMER-NO-FSYNC]` | Flush 不含 Fsync | 语义 |
| `[S-RBF-TOMBSTONE-VISIBLE]` | Tombstone 帧可见 | 语义 |
| `[S-RBF-SCANREVERSE-NO-IENUMERABLE]` | ScanReverse 不实现 IEnumerable | 语义 |
| `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` | 空序列合法 | 语义 |
| `[S-RBF-SCANREVERSE-CURRENT-LIFETIME]` | Current 生命周期 | 语义 |
| `[S-RBF-SCANREVERSE-CONCURRENT-MUTATION]` | 并发修改行为未定义 | 语义 |
| `[S-RBF-SCANREVERSE-MULTI-GETENUM]` | 多次 GetEnumerator 独立 | 语义 |

> **已移除的条款**：`[S-STATEJOURNAL-FRAMETAG-MAPPING]` 和 `[S-STATEJOURNAL-TOMBSTONE-SKIP]` 已移至 [mvp-design-v2.md](../StateJournal/mvp-design-v2.md)，因为它们是上层（StateJournal）的语义定义，不属于 RBF 接口契约。

---

## 7. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.19 | 2026-01-06 | **ReadAt API 重构**：`bool TryReadAt(out RbfFrame)` → `AteliaResult<RbfFrame> ReadAt()`；新增 `[F-RBF-READ-ERROR-DEFINITION]` 条款和 `RbfReadError`/`RbfReadErrorKind` 类型定义；调用者可区分失败原因（地址越界/CRC错误/帧结构损坏）|
| 0.18 | 2026-01-06 | §4.1 `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` 明确三种适用场景：不完整文件、仅 Genesis Fence、所有帧校验失败 |
| 0.17 | 2025-12-28 | **FrameTag 接口简化**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-wrapper-type-audit.md)）：移除 `FrameTag` record struct，接口层统一使用 `uint`；移除 `[F-FRAMETAG-DEFINITION]` 条款；§2.1 改为概念描述（三层视角：存储/接口/应用） |
| 0.16 | 2025-12-28 | **RbfFrameBuilder Payload 接口简化**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-rbf-builder-payload-simplification.md)）：合并 `Payload` 和 `ReservablePayload` 为单一的 `IReservableBufferWriter Payload`；明确 Zero I/O 是实现优化而非类型承诺；新增 `[S-RBF-BUILDER-FLUSH-NO-LEAK]` 条款；`Dispose()` 在 Auto-Abort 分支 MUST NOT 抛异常 |
| 0.15 | 2025-12-28 | **ScanReverse 返回类型重构**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-scan-reverse-return-type.md)）：`RbfReverseEnumerable` 改为 `RbfReverseSequence`（ref struct）；移除 `IEnumerable<RbfFrame>` 继承（因 RbfFrame 是 ref struct，不能作为泛型参数）；新增 5 条 ScanReverse 语义条款 |
| 0.14 | 2025-12-28 | **文档修订**：修复条款索引（FrameStatus→Tombstone）；修复§5示例代码；简化§2.4 Frame定义；添加 IReservableBufferWriter/RbfReverseEnumerable 引用 |
| 0.13 | 2025-12-28 | **移除 FrameStatus 类型**：接口层不再定义 FrameStatus struct，RbfFrame 直接暴露 `bool IsTombstone` 属性（简化接口，消除只有一个属性的中间类型）|
| 0.12 | 2025-12-28 | FrameStatus 接口重构：从 `enum { 0x00/0xFF }` 改为 `readonly struct { bool IsTombstone }`（已被 v0.13 进一步简化）|
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
