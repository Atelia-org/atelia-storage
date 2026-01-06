---
docId: "rbf-format"
title: "RBF Shape-Tier"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF Layer Interface Contract
> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 概述
本文档定义 RBF（Reversible Binary Framing）层与上层（应用/业务层）之间的接口契约。

**设计原则**：
- RBF 是"二进制信封"——只关心如何安全封装 payload，不解释 payload 语义
- 上层只需依赖本接口文档，无需了解 RBF 内部实现细节
- 接口设计支持 zero-copy 热路径，但不强制

**文档关系**：
- `rbf-format.md` **实现** `rbf-interface.md`（下层 RBF 线格式实现接口定义）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| `rbf-interface.md` | Layer 0/1 边界（本文档） | IRbfFramer, IRbfScanner 等接口契约 |
| `rbf-format.md` | Layer 0 (RBF) | RBF 二进制线格式规范（wire format） |

---

## 2. 术语表（Layer 0）
> 本节定义 RBF 层的独立术语。上层业务语义（如 FrameTag 取值）由上层文档定义。

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

**`[S-UPPERLAYER-TOMBSTONE-SKIP]`**

> 上层记录读取逻辑 MUST 忽略 Tombstone 帧（`IsTombstone == true`），不将其解释为业务记录。

> **设计理由**：
> - Scanner 是"原始帧扫描器"，职责边界清晰；
> - Tombstone 可见性对诊断/调试有价值；
> - 过滤责任在 Layer 1，不在 Layer 0；
> - 接口层隐藏编码细节，上层无需知道 wire format 的具体字节值。

### 2.3 SizedPtr

**`[F-SIZEDPTR-DEFINITION]`**

> **SizedPtr** 是 8 字节紧凑表示的 offset+length 区间，作为 RBF Interface 层的核心 Frame 句柄类型。

**来源**：`Atelia.Data.SizedPtr`（38:26 位分配方案）

| 属性 | 位数 | 范围 | 说明 |
|:-----|:-----|:-----|:-----|
| `OffsetBytes` | 38-bit | ~1TB | 指向 Frame 起点（HeadLen 字段位置） |
| `LengthBytes` | 26-bit | ~256MB | Frame 的字节长度（含 HeadLen 到 CRC32C） |

**约束**：
- 有效 SizedPtr MUST 4 字节对齐（`OffsetBytes % 4 == 0` 且 `LengthBytes % 4 == 0`）
- 超出范围的值在构造时抛出 `ArgumentOutOfRangeException`

**`[F-RBF-NULLPTR]`**

> `default(SizedPtr)`（即 `Packed == 0`）在 RBF 层表示"无效的 Frame 引用"。

```csharp
// RBF 层的 Null 约定
public static readonly SizedPtr NullPtr = default;

// 判等方式
if (ptr == default) { /* 无效引用 */ }
```

**语义**：
- `NullPtr.OffsetBytes == 0` 且 `NullPtr.LengthBytes == 0`
- 方法返回 `NullPtr` 表示"未找到"或操作失败
- `TryReadAt()` 接收 `NullPtr` 时立即返回 `false`

### 2.4 Frame

> **Frame** 是 RBF 的基本 I/O 单元。

上层只需知道：

- 每个 Frame 有一个 `FrameTag`、`Payload` 和 `IsTombstone` 状态
- Frame 写入后返回其 `SizedPtr`（包含 offset+length）
- Frame 读取通过 `SizedPtr` 定位

> Frame 的内部结构（wire format）在 rbf-format.md 中定义，接口层无需关心。

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
    /// <returns>写入的帧位置和长度</returns>
    SizedPtr Append(uint tag, ReadOnlySpan<byte> payload);
    
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
    /// <para><b>上层责任</b>：如需 durable commit（例如采用 data→meta 的提交顺序），
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
    /// 提交帧。回填 header/CRC，返回帧位置和长度。
    /// </summary>
    /// <returns>写入的帧位置和长度</returns>
    /// <exception cref="InvalidOperationException">重复调用 Commit</exception>
    public SizedPtr Commit();
    
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
> Fsync 策略（例如 data→meta 的提交顺序）由上层控制。

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
    /// 读取指定位置的帧。
    /// </summary>
    /// <param name="ptr">帧位置（offset+length）</param>
    /// <param name="frame">输出：帧内容（生命周期受限于底层缓冲区）</param>
    /// <returns>是否成功读取</returns>
    bool TryReadAt(SizedPtr ptr, out RbfFrame frame);
    
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

> 当扫描范围为空（空文件、仅 Genesis Fence、或无有效帧）时，`ScanReverse()` 返回序列 MUST 产生 0 个元素且不得抛出异常。
> 枚举器首次 `MoveNext()` 返回 `false`。

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
    
    /// <summary>帧位置（offset+length）</summary>
    public SizedPtr Ptr { get; }
    
    /// <summary>
    /// 拷贝 Payload 到新分配的数组（显式分配，生命周期脱离底层缓冲区）。
    /// </summary>
    public byte[] PayloadToArray() => Payload.ToArray();
}
```

---

## 5. 使用示例（Informative）

> 本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义。

```csharp
// 写入帧
public SizedPtr WriteFrame(IRbfFramer framer, uint tag, byte[] payload) {
    using var builder = framer.BeginFrame(tag);
    builder.Payload.Write(payload);  // IBufferWriter<byte>.Write 扩展方法
    return builder.Commit();
}

// 读取帧
public void ProcessFrame(IRbfScanner scanner, SizedPtr ptr) {
    if (!scanner.TryReadAt(ptr, out var frame)) return;
    
    // 先检查帧状态，跳过墓碑帧（上层策略）
    if (frame.IsTombstone) return;
    
    // 上层根据 frame.Tag 决定如何解析 frame.Payload
    // RBF 层不解释 FrameTag 的语义
    ProcessPayload(frame.Tag, frame.Payload);
}
```

---

## 7. 最近变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.18 | 2026-01-06 | **SizedPtr 替代 Address64**（[W-0006](../../../wish/W-0006-rbf-sizedptr/artifacts/)）：移除 Address64 类型，引入 SizedPtr 作为核心 Frame 句柄；新增 `[F-SIZEDPTR-DEFINITION]`、`[F-RBF-NULLPTR]`；移除 `[F-ADDRESS64-*]` 条款；`RbfFrame.Address` 改为 `RbfFrame.Ptr` |
| 0.17 | 2025-12-28 | **FrameTag 接口简化**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-wrapper-type-audit.md)）：移除 `FrameTag` record struct，接口层统一使用 `uint`；移除 `[F-FRAMETAG-DEFINITION]` 条款；§2.1 改为概念描述（三层视角：存储/接口/应用） |
| 0.16 | 2025-12-28 | **RbfFrameBuilder Payload 接口简化**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-rbf-builder-payload-simplification.

## 8. 待实现时确认

> 以下问题可在实现阶段确认：

- **错误处理**：TryReadAt 失败时是否需要 `RbfReadStatus`（P2）
- **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）

---
