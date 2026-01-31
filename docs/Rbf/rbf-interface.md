---
docId: "rbf-interface"
title: "RBF Shape-Tier"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF Layer Interface Contract
**文档定位**：Layer 0/1 边界，定义 `IRbfFile` 门面与对外可见类型/行为契约。
文档层级与规范遵循见 [README.md](README.md)。

本文档只描述"对外外观 + 可观察行为"，不暴露内部实现与 wire-format 细节。

## 1. 概述

RBF 是"二进制信封"：只关心如何安全封装 payload，不解释 payload 语义。

**设计原则**：
- 上层只需依赖本文档，无需了解 RBF 内部实现细节
- 对外接口以门面 `IRbfFile` 为中心，管理资源生命周期并提供读写能力
- 接口设计支持 zero-copy 热路径（`RbfFrame`/`ReadOnlySpan<byte>`），但不强制

---

## 2. 术语表（Layer 0）
本节定义 RBF 层的独立术语。上层业务语义（如 FrameTag 取值）由上层文档定义。
## term `FrameTag` 帧类型标识符

**FrameTag** 是 4 字节（`uint`）的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。

**三层视角**：
- **存储层**：`uint`（线格式中的 4 字节 LE 字段）
- **接口层**：`uint`（本文档定义的 API 参数/返回类型）
- **应用层**：上层可自由选择 enum 或其他类型进行打包/解包

**保留值**：无。RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

## term `Tombstone` 墓碑帧
**Tombstone**（墓碑帧）是帧的有效性标记（Layer 0 元信息），表示该帧已被逻辑删除或是 Auto-Abort 的产物。
RbfFrame 通过 `bool IsTombstone` 属性暴露此状态。

### spec [S-RBF-SCANREVERSE-TOMBSTONE-FILTER] ScanReverse过滤Tombstone
`IRbfFile.ScanReverse(bool showTombstone = false)` 的行为：
- 当 `showTombstone == false`（默认）时：MUST 自动跳过 Tombstone 帧（`IsTombstone == true`），只产出有效业务帧。
- 当 `showTombstone == true` 时：MUST 产出所有通过 framing/CRC 校验的帧（含 Tombstone）。

### derived [H-TOMBSTONE-VISIBILITY-RATIONALE] 墓碑帧可见性设计理由
- **默认隐藏**：提升易用性，绝大多数日常场景不需要关心已被标记删除的数据。
- **可选暴露**：为了诊断、调试或特定审计需求，允许通过参数查看所有物理帧。
- **职责下沉**：Layer 0 负责过滤系统级标记（Tombstone），简化上层逻辑。

## derived `SizedPtr` 帧句柄
**引用**：[Atelia.Data.SizedPtr](../../src/Data/SizedPtr.cs)
@[S-RBF-DECISION-SIZEDPTR-CREDENTIAL](rbf-decisions.md)

**属性概要**（详见源文件）：
| 属性 | 类型 | 说明 |
|:-----|:-----|:-----|
| `Offset` | `long` | 以字节表示的起始偏移（4B 对齐） |
| `Length` | `int` | 以字节表示的区间长度（4B 对齐） |
| `EndOffsetExclusive` | `long` | 区间结束位置（不含） |

## term `Frame` 帧
**Frame** 是 RBF 的基本 I/O 单元。Frame 的内部结构（wire format）接口层无需关心。

上层只需知道：
- 每个 Frame 有一个 @`FrameTag`、`PayloadAndMeta` 和 `IsTombstone` 状态
- Frame 写入后返回其 @`SizedPtr`（包含 offset+length）
- Frame 读取通过 @`SizedPtr` 定位

---

## 3. 对外门面（Facade）(Layer 1 Interface)

### spec [A-RBF-IRBFFILE-SHAPE] IRbfFile接口定义

```csharp
/// <summary>RBF 文件对象门面。</summary>
/// <remarks>
/// 职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。
/// 并发约束：同一实例在任一时刻最多 1 个 open Builder。
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>获取当前文件逻辑长度（也是下一个写入 Offset）。</summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    /// <remarks>
    /// 失败场景（返回 AteliaResult.IsFailure）：
    /// - TailMeta 超长（> 64KB）
    /// - Payload + TailMeta 超长（> MaxPayloadAndMetaLength）
    /// - TailOffset 非 4B 对齐或超出 SizedPtr 可表示范围
    /// I/O 错误（磁盘满、权限等）仍抛出异常。
    /// </remarks>
    AteliaResult<SizedPtr> Append(uint tag, scoped ReadOnlySpan<byte> payload, scoped ReadOnlySpan<byte> tailMeta = default);

    /// <summary>复杂帧构建（流式写入 payload / payload 内回填）。</summary>
    /// <remarks>
    /// 注意：在 Builder Dispose/EndAppend 前，TailOffset 不会更新。
    /// 注意：存在 open Builder 时，不应允许并发 Append/BeginAppend。
    /// </remarks>
    RbfFrameBuilder BeginAppend();

    /// <summary>读取指定位置的帧到提供的 buffer 中（zero-copy）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">目标缓冲区，长度必须 &gt;= ticket.Length。</param>
    /// <returns>成功时返回帧视图（指向 buffer 内部），失败返回错误。</returns>
    AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer);

    /// <summary>随机读（从 ArrayPool 借缓存）。</summary>
    /// <remarks>
    /// 调用方 MUST 调用返回值的 Dispose() 归还 buffer。
    /// 失败时 buffer 已自动归还。
    /// </remarks>
    AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket);

    /// <summary>逆向扫描，返回帧元信息序列。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    /// <remarks>
    /// CRC 职责分离：ScanReverse 只做 Framing 校验，不校验 PayloadCRC32C。
    /// 如需完整校验，请对返回的 Ticket 调用 ReadFrame/ReadPooledFrame。
    /// </remarks>
    RbfReverseSequence ScanReverse(bool showTombstone = false);

    /// <summary>从 SizedPtr 获取帧元信息（只读 TrailerCodeword，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败返回错误。</returns>
    /// <remarks>
    /// I/O：只读取 TrailerCodeword（16B），不读 Payload。
    /// 信任级别：L2（TrailerCrc 校验通过）。
    /// 此方法允许从持久化的 SizedPtr 恢复完整的帧元信息。
    /// </remarks>
    AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 内部），失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// 若需完整数据完整性保证，请使用 <see cref="ReadFrame(SizedPtr ptr, Span{byte})"/>。
    /// </remarks>
    AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任，自动租用 buffer，从 SizedPtr）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledTailMeta，失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// I/O：读取 TrailerCodeword（16B）+ TailMeta 区域。
    /// 此方法是 ReadFrameInfo(ticket) + ReadPooledTailMeta(info) 的便捷组合。
    /// </remarks>
    AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket);

    /// <summary>durable flush（落盘）。</summary>
    /// <remarks>
    /// 用于上层 commit 顺序（例如 data→meta）的 durable 边界。
    /// </remarks>
    void DurableFlush();

    /// <summary>截断（恢复用）。</summary>
    void Truncate(long newLengthBytes);
}

public static class RbfFile {
    public static IRbfFile CreateNew(string path);       // FailIfExists
    public static IRbfFile OpenExisting(string path);    // 验证 HeaderFence
}
```

*@[S-RBF-DECISION-READFRAME-RESULTPATTERN]，随机读取 API Result-Pattern（返回 `AteliaResult<RbfFrame>`）。*

### spec [S-RBF-APPEND-RESULTPATTERN] Append使用Result模式
`IRbfFile.Append` MUST 返回 `AteliaResult<SizedPtr>`，使用 Result-Pattern 表达可预见的操作拒绝。

**返回失败的场景**（前置校验，不产生 I/O）：
- `tailMeta.Length > MaxTailMetaLength`（64KB）
- `payload.Length + tailMeta.Length > MaxPayloadAndMetaLength`
- `TailOffset` 非 4B 对齐
- 写入后 `EndOffset > SizedPtr.MaxOffset`

**抛出异常的场景**（系统级故障）：
- 磁盘满、权限不足、设备 I/O 错误等底层异常
- 句柄已释放（`ObjectDisposedException`）

### spec [S-RBF-APPEND-PRECONDITION-CHECK] Append前置校验
`IRbfFile.Append` 的参数校验 MUST 在任何 I/O 操作之前完成：
- 校验失败时 MUST NOT 产生任何文件写入
- 校验失败时 MUST NOT 更新 `TailOffset`
- 校验失败时返回包含 `RecoveryHint` 的 `AteliaError`

### spec [S-RBF-TAILOFFSET-IS-NEXT-WRITE-OFFSET] TailOffset语义
`IRbfFile.TailOffset` MUST 表示“当前逻辑文件长度（byte length）”，并且等于下一次 `Append/BeginAppend` 的写入起点（byte offset）。

### spec [S-RBF-TAILOFFSET-UPDATE] TailOffset更新规则
`TailOffset` MUST 只在以下时刻推进：
- `Append()` 成功返回后；
- `BeginAppend()` 返回的 `RbfFrameBuilder.EndAppend()` 成功返回后；
- `Truncate()` 成功返回后。

在 open Builder 的生命周期内（`Commit/Dispose` 之前），`TailOffset` MUST NOT 提前更新。

### spec [S-RBF-DURABLEFLUSH-DURABILIZE-COMMITTED-ONLY] DurableFlush语义
`IRbfFile.DurableFlush()` MUST 尝试将“已提交写入”的数据持久化到物理介质。

- 若发生不可恢复的 I/O 错误，允许抛出异常（具体异常类型由实现选择）。
- 本方法不对“未提交的 Builder 写入”做任何可观察承诺。

### spec [S-RBF-TRUNCATE-REQUIRES-NONNEGATIVE-4B-ALIGNED] Truncate语义
`IRbfFile.Truncate(long newLengthBytes)` MUST 将文件逻辑长度设置为 `newLengthBytes`。

- `newLengthBytes` MUST 为非负且满足 4B 对齐（否则 MUST throw `ArgumentOutOfRangeException`）。
- 截断是恢复路径能力；调用方 MUST 自行确保截断点符合其恢复语义（例如指向 Fence 位置）。

### spec [A-RBF-FRAME-BUILDER] RbfFrameBuilder定义

*see:`atelia/src/Data/IReservableBufferWriter.cs`，此类型扩展了标准的`System.Buffers.IBufferWriter<byte>`*

```csharp
/// <summary>帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。</summary>
/// <remarks>
/// 生命周期：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。
/// Auto-Abort（Optimistic Clean Abort）：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
/// 类型选择：采用 sealed class 而非 ref struct，因为内部组件（SinkReservableWriter 等）
/// 本就是堆分配，ref struct 外壳无实际收益；sealed class 更简单且支持未来 Reset 复用优化。
/// </remarks>
public sealed class RbfFrameBuilder : IDisposable {
    /// <summary>Payload 写入器。</summary>
    /// <remarks>
    /// 该写入器实现 <see cref="IBufferWriter{T}"/>，因此可用于绝大多数序列化场景。
    /// 此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。
    /// 接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。
    /// 注意：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
    /// </remarks>
    public IReservableBufferWriter PayloadAndMeta { get; }

    /// <summary>提交帧。回填 header/CRC，返回帧位置和长度。</summary>
    /// <returns>写入的帧位置和长度</returns>
    /// <exception cref="InvalidOperationException">重复调用 EndAppend</exception>
    public SizedPtr EndAppend(uint tag, int tailMetaLength = 0);

    /// <summary>释放构建器。若未 EndAppend，自动执行 Auto-Abort。</summary>
    /// <remarks>
    /// Auto-Abort 分支约束：<see cref="Dispose"/> 在 Auto-Abort 分支 MUST NOT 抛出异常
    /// （除非出现不可恢复的不变量破坏），并且必须让 File Facade 回到可继续写状态。
    /// </remarks>
    public void Dispose();
}
```

**关键语义**：

### spec [S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME] Auto-Abort逻辑语义
若 `RbfFrameBuilder` 未调用 `EndAppend()` 就执行 `Dispose()`：

**逻辑语义**：
- 该帧视为**逻辑不存在**（logical non-existence）
- 上层 Record Reader 遍历时 MUST NOT 看到此帧作为业务记录

**后置条件**：
- `Dispose()` 后，底层 MUST 可继续写入后续帧
- 后续 `Append()` / `BeginAppend()` 调用 MUST 成功
- `Dispose()` 在此分支 MUST NOT 抛出异常（除非出现不可恢复的不变量破坏）

此机制防止上层异常导致 Writer 死锁，同时在可能时优化为零 I/O。


### spec [S-RBF-BUILDER-SINGLE-OPEN] 单Builder约束
同一 `IRbfFile` 实例同时最多允许 1 个 open `RbfFrameBuilder`。
在前一个 Builder 完成（Commit 或 Dispose）前调用 `BeginAppend()` MUST 抛出 `InvalidOperationException`。

---

## 4. 读取与扫描（通过 IRbfFile 暴露）

`ReadFrame()` 的 framing/CRC 校验由 [rbf-format.md](rbf-format.md) 定义。
`ScanReverse()` 仅做 Framing 校验（含 `TrailerCrc32C`）并输出元信息，不做 `PayloadCrc32C` 校验。
本节只约束上层可观察到的结果形态与序列语义。

### spec [A-RBF-FRAME-INFO] RbfFrameInfo定义

```csharp
/// <summary>已验证的帧元信息句柄（不含 Payload）。</summary>
/// <remarks>
/// 用于 ScanReverse 产出，支持不读取 payload 的元信息迭代。
/// PayloadLength 与 TailMetaLength 从 TrailerCodeword 解码得出。
/// 句柄语义：构造时已完成 TrailerCrc、reserved bits、TailLen 一致性等验证，
/// 后续读取方法只做 I/O 级校验（buffer length、short read），不重复结构性验证。
/// 生命周期：File 为非拥有引用，调用方 MUST 确保 File 在使用期间有效。
/// </remarks>
public readonly struct RbfFrameInfo : IEquatable<RbfFrameInfo> {
    /// <summary>帧位置凭据。</summary>
    public SizedPtr Ticket { get; }

    /// <summary>帧标签。</summary>
    public uint Tag { get; }

    /// <summary>Payload 长度（字节）。</summary>
    public int PayloadLength { get; }

    /// <summary>TailMeta 长度（字节）。</summary>
    public int TailMetaLength { get; }

    /// <summary>是否为墓碑帧。</summary>
    public bool IsTombstone { get; }

    #region Read Methods（成员方法）

    /// <summary>读取 TailMeta 到调用方提供的 buffer（L2 信任，不校验 PayloadCrc）。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 最小化 I/O：只读取 TailMeta 区域，不读 Payload 或 TrailerCodeword。
    /// L2 信任：依赖构造时已完成的 TrailerCrc 校验，不做 PayloadCrc。
    /// 生命周期：返回的 TailMeta 直接引用 buffer，调用方 MUST 确保 buffer 有效。
    /// </remarks>
    public AteliaResult<RbfTailMeta> ReadTailMeta(Span<byte> buffer);

    /// <summary>读取 TailMeta（自动租用 buffer，L2 信任）。</summary>
    /// <returns>成功时返回 RbfPooledTailMeta，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
    /// TailMetaLength = 0：不租 buffer，返回无 buffer 的 RbfPooledTailMeta。
    /// 生命周期：成功时调用方拥有 buffer 所有权，MUST 调用 Dispose。
    /// </remarks>
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta();

    /// <summary>读取完整帧到调用方提供的 buffer 中。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= Ticket.Length。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 执行完整 framing + CRC 校验（L3 信任级别）。
    /// </remarks>
    public AteliaResult<RbfFrame> ReadFrame(Span<byte> buffer);

    /// <summary>读取完整帧（自动租用 buffer）。</summary>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 执行完整 framing + CRC 校验（L3 信任级别）。
    /// </remarks>
    public AteliaResult<RbfPooledFrame> ReadPooledFrame();

    #endregion

    #region Equality

    public bool Equals(RbfFrameInfo other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public static bool operator ==(RbfFrameInfo left, RbfFrameInfo right);
    public static bool operator !=(RbfFrameInfo left, RbfFrameInfo right);

    #endregion
}
```

### spec [S-RBF-FRAMEINFO-USERMETALEN-RANGE] TailMetaLength值域
`RbfFrameInfo.TailMetaLength` MUST 满足：`0 <= TailMetaLength <= 65535`。

**上限来源**：`FrameDescriptor.TailMetaLen` 字段为 16-bit（SSOT：[rbf-format.md](rbf-format.md) @[F-FRAME-DESCRIPTOR-LAYOUT]）。

### spec [S-RBF-FRAMEINFO-PAYLOADLEN-RANGE] PayloadLength值域
`RbfFrameInfo.PayloadLength` MUST 满足：`0 <= PayloadLength <= MaxPayloadLength`。

**上限来源**：受 `TailLen(u32)` 与 `SizedPtr.Length(int)` 共同约束；具体计算见 [rbf-format.md](rbf-format.md) @[S-RBF-PAYLOADLENGTH-FORMULA]。

### derived [H-RBF-FRAMEINFO-USERMETA-READING] 读取TailMeta
调用方可通过 `RbfFrameInfo` 的成员方法直接读取数据：
- `info.ReadTailMeta(buffer)` / `info.ReadPooledTailMeta()` — L2 信任级别
- `info.ReadFrame(buffer)` / `info.ReadPooledFrame()` — L3 完整校验

### derived [H-RBF-TRUST-LEVELS] 信任级别模型
RBF 读取 API 按 CRC 校验程度分为三个信任级别：

| 级别 | 校验内容 | API | 信任断言 |
|:-----|:---------|:----|:---------|
| **L1: Framing** | TrailerCodeword 可解码 | `ScanReverse` | "这是一个结构合法的帧" |
| **L2: Meta** | TrailerCrc 通过 | `info.ReadTailMeta`, `info.ReadPooledTailMeta` | "帧元信息（含 TailMeta）未被篡改" |
| **L3: Full** | PayloadCrc + TrailerCrc | `info.ReadFrame`, `info.ReadPooledFrame` | "整个帧内容完整" |

调用方根据场景选择适当的信任级别：预览/筛选场景用 L2，业务处理用 L3。

### spec [A-RBF-REVERSE-SEQUENCE] RbfReverseSequence定义

```csharp
/// <summary>逆向扫描序列（duck-typed 枚举器，支持 foreach）。</summary>
/// <remarks>
/// 设计说明：返回 ref struct 以避免堆分配并满足栈上生命周期约束。
/// 上层通过 foreach 消费，不依赖 LINQ。
/// </remarks>
public ref struct RbfReverseSequence {
    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfReverseEnumerator GetEnumerator();
}

/// <summary>逆向扫描枚举器。</summary>
public ref struct RbfReverseEnumerator {
    /// <summary>当前帧元信息。</summary>
    public RbfFrameInfo Current { get; }

    /// <summary>移动到下一帧。</summary>
    public bool MoveNext();

    /// <summary>迭代终止原因。null 表示正常结束。</summary>
    public AteliaError? TerminationError { get; }
}
```

### derived [S-RBF-SCANREVERSE-NO-IENUMERABLE] ScanReverse不实现IEnumerable
`RbfReverseSequence` MUST NOT 实现 `IEnumerable<T>`。
**原因**：该序列与枚举器为 `ref struct`，以避免堆分配并满足栈上生命周期约束。

### spec [S-RBF-READFRAME-ALWAYS-CRC] ReadFrame始终执行CRC校验
`ReadFrame(...)` / `ReadPooledFrame(...)` MUST 对帧执行完整 CRC 校验（`PayloadCrc32C` + `TrailerCrc32C`）。
当 CRC 校验失败时，MUST 通过 `AteliaResult` 返回失败（错误类型由实现定义）。

### spec [S-RBF-SCANREVERSE-NO-PAYLOADCRC] ScanReverse不进行PayloadCRC校验
`ScanReverse(...)` MUST NOT 执行 `PayloadCrc32C` 校验。
`ScanReverse(...)` MUST 执行 framing 校验 + 尾部元信息校验（`TrailerCrc32C`）并输出 `RbfFrameInfo`。

**CRC 职责分离（Normative）**：
- **ScanReverse**：只校验 `TrailerCrc32C`（覆盖 FrameDescriptor + FrameTag + TailLen）
- **ReadFrame / ReadPooledFrame**：校验 `PayloadCrc32C` + `TrailerCrc32C`

如需完整校验，调用方 MUST 使用返回的 `Ticket` 调用 `ReadFrame` / `ReadPooledFrame`。

### spec [S-RBF-SCANREVERSE-EMPTY-IS-OK] 空序列合法
当文件为空（仅含 HeaderFence）或 **根据过滤条件无可见帧** 时，`ScanReverse()` MUST 返回空序列（0 元素），MUST NOT 抛出异常。

### spec [S-RBF-SCANREVERSE-TERMINATION-ERROR] 终止错误语义
当 `MoveNext()` 返回 `false` 时：
- 若 `TerminationError` 为 `null`，表示正常结束；
- 若 `TerminationError` 非 `null`，表示因 framing 损坏或读取失败而提前终止。

### spec [S-RBF-SCANREVERSE-CURRENT-LIFETIME] DEPRECATED
该条款源于 `Current` 返回 `RbfFrame` 的旧契约。
现已改为 `RbfFrameInfo` 值语义，不再受 buffer 生命周期约束。

### spec [S-RBF-SCANREVERSE-CURRENT-VALUE] Current为值快照
`RbfReverseEnumerator.Current` MUST 返回 `RbfFrameInfo` 的值快照，
其生命周期不依赖底层 buffer，可安全跨越后续 `MoveNext()` 调用。

### spec [A-RBF-IFRAME] IRbfFrame接口定义

```csharp
/// <summary>RBF 帧的公共属性契约。</summary>
public interface IRbfFrame {
    /// <summary>帧位置（凭据）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>帧负载数据。</summary>
    ReadOnlySpan<byte> Payload { get; }

    /// <summary>用户元数据。</summary>
    ReadOnlySpan<byte> TailMeta { get; }

    /// <summary>是否为墓碑帧。</summary>
    bool IsTombstone { get; }
}
```

### spec [A-RBF-FRAME-STRUCT] RbfFrame定义

```csharp
/// <summary>RBF 帧数据结构。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrame 的 buffer）。
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// </remarks>
public readonly ref struct RbfFrame : IRbfFrame {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; init; }

    /// <inheritdoc/>
    public uint Tag { get; init; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload { get; init; }

    /// <inheritdoc/>
    public bool IsTombstone { get; init; }
}
```

### spec [A-RBF-POOLED-FRAME] RbfPooledFrame定义

```csharp
/// <summary>携带 ArrayPool buffer 的 RBF 帧。</summary>
/// <remarks>
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 Payload 变为 dangling，不可再访问。
/// </remarks>
public sealed class RbfPooledFrame : IRbfFrame, IDisposable {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose();
}
```

### spec [A-RBF-IRBTAILMETA] IRbfTailMeta接口定义

```csharp
/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public interface IRbfTailMeta {
    /// <summary>帧位置凭据（支持"预览→完整读取"工作流）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>TailMeta 数据（可能为 <see cref="ReadOnlySpan{T}.Empty"/>）。</summary>
    ReadOnlySpan<byte> TailMeta { get; }

    /// <summary>是否为墓碑帧。</summary>
    bool IsTombstone { get; }

    // 注意：根据"诚实地贫瘠"原则，不暴露 PayloadLength
}
```

### spec [A-RBF-TAILMETA-FRAME] RbfTailMeta定义

```csharp
/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public readonly ref struct RbfTailMeta : IRbfTailMeta {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> TailMeta { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数（只能由验证路径调用）。</summary>
    internal RbfTailMeta(SizedPtr ticket, uint tag, ReadOnlySpan<byte> tailMeta, bool isTombstone);
}
```

### spec [A-RBF-POOLED-TAILMETA] RbfPooledTailMeta定义

```csharp
/// <summary>携带 ArrayPool buffer 的 TailMeta 预览结果（L2 信任）。</summary>
/// <remarks>
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 TailMeta 变为 dangling，不可再访问。
/// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// </remarks>
public sealed class RbfPooledTailMeta : IDisposable, IRbfTailMeta {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">对象已 Dispose（且原有 buffer）。</exception>
    public ReadOnlySpan<byte> TailMeta { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose();
}
```

### spec [S-RBF-READTAILMETA-L2-TRUST] ReadTailMeta信任语义
`ReadTailMeta` / `ReadPooledTailMeta` MUST 提供 L2 信任级别：
- 依赖 `ScanReverse` 已完成的 `TrailerCrc32C` 校验
- MUST NOT 执行 `PayloadCrc32C` 校验
- 只读取 TailMeta 区域（最小化 I/O）

### spec [S-RBF-READTAILMETA-EMPTY-OK] 空TailMeta合法
当 `info.TailMetaLength == 0` 时，`ReadTailMeta` MUST 返回成功 + 空 `ReadOnlySpan<byte>`。
`ReadPooledTailMeta` 在此场景 MUST NOT 租用 ArrayPool buffer。

---

## 5. 使用示例（Informative）

*已移动至 [rbf-guide.md](rbf-guide.md)*

---

## 6. 最近变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.34 | 2026-01-28 | **ReadFrameInfo 接口**：新增 `ReadFrameInfo(SizedPtr)` 从 ticket 恢复帧元信息；新增 `ReadPooledTailMeta(SizedPtr)` 便捷重载；补齐"SizedPtr → TailMeta"的读取路径 |
| 0.33 | 2026-01-26 | **ReadTailMeta 接口**：新增 `ReadTailMeta` / `ReadPooledTailMeta` 方法用于大帧预览场景；新增 `RbfTailMeta` / `RbfPooledTailMeta` 类型；定义三层信任模型（L1/L2/L3） |
| 0.32 | 2026-01-24 | **CRC 术语澄清**：修复"ScanReverse 不做 CRC"的歧义表述，明确 ScanReverse 必须做 `TrailerCrc32C` 校验；增加 `TailMetaLength` 和 `PayloadLength` 的值域上限约束；条款 `[S-RBF-SCANREVERSE-NO-CRC]` 更名为 `[S-RBF-SCANREVERSE-NO-PAYLOADCRC]` |
| 0.31 | 2026-01-24 | **Format对齐**：`RbfFrameInfo` 字段重命名 `MetaTrailerLength` -> `TailMetaLength` 以适配 wire-format；更新 ScanReverse 校验描述（适配 `TrailerCrc32C`）；修正 `RbfFrameBuilder` 签名为 `EndAppend`；废弃 PayloadTrailer 相关描述 |
| 0.30 | 2026-01-17 | **ReadFrame 重构**：移除旧签名 `ReadFrame(SizedPtr)`，新增 `ReadFrame(ticket, buffer)` + `ReadPooledFrame(ticket)`；`RbfFrame.Ptr` → `Ticket`；新增 `IRbfFrame` 接口和 `RbfPooledFrame` 类型；更新 `SizedPtr` 属性引用（`Offset`/`Length`） |
| 0.29 | 2026-01-14 | **IDisposable 显式声明**：`RbfFrameBuilder` 添加 `: IDisposable` 声明，明确类型系统语义；来自团队设计讨论 |
| 0.28 | 2026-01-12 | **方法重命名**：`RbfFrameBuilder.Commit()` →  `EndAppend()`，与 `BeginAppend()` 形成对称配对，最大化 LLM 可预测性；详见[命名讨论会](../../../../agent-team/meeting/2026-01-12-rbf-builder-lifecycle-naming.md) |
| 0.27 | 2026-01-12 | **Tombstone 默认隐藏**：修改 `ScanReverse` 接口增加 `bool showTombstone = false` 参数；废弃 `[S-RBF-TOMBSTONE-VISIBLE]` 改为 `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]`，确立默认过滤 Tombstone 的行为 |
| 0.26 | 2026-01-11 | **文档职能分离**：拆分 Auto-Abort 条款为逻辑语义（本文档 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]）+ 实现路径（type-bone.md @[I-RBF-BUILDER-AUTO-ABORT-IMPL]）；明确本文档为规范性契约，type-bone.md 为非规范性实现指南 |
| 0.25 | 2026-01-11 | **接口细节对齐**：`Truncate` 参数类型改为 `long`（与 `TailOffset` 一致）；更新文档关系表中 `rbf-type-bone.md` 层级描述；§3 标题增加层级标注；统一 `RbfFrame` 生命周期注释；设计原则位置调整 |

## 7. 待实现时确认

以下问题可在实现阶段确认：

- **错误处理**：`ReadFrame()` 的错误码集合与分层边界（P2）
- **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）

---
