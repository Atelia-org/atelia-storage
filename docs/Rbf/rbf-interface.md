---
docId: "rbf-interface"
title: "RBF Shape-Tier"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF Layer Interface Contract
> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。
> 本文档遵循 [AI-Design-DSL](../../../agent-team/wiki/SoftwareDesignModeling/AI-Design-DSL.md) 规范。

> **Decision-Layer（AI 不可修改）**：本规范受 `rbf-decisions.md` 约束。

> **实现参考（非规范）**：核心类型骨架见 [rbf-type-bone.md](rbf-type-bone.md)。
> 本文档只描述“对外外观 + 可观察行为”，不暴露内部实现与 wire-format 细节。

## 1. 概述
本文档定义 RBF（Reversible Binary Framing）层与上层（应用/业务层）之间的接口契约（Shape-Tier）。
RBF 是“二进制信封”：只关心如何安全封装 payload，不解释 payload 语义。

**设计原则**：
- 上层只需依赖本文档，无需了解 RBF 内部实现细节
- 对外接口以门面 `IRbfFile` 为中心，管理资源生命周期并提供读写能力
- 接口设计支持 zero-copy 热路径（`RbfFrame`/`ReadOnlySpan<byte>`），但不强制

### 1.1 关键设计决策（Decision-Layer）

本规范的关键设计决策已上移至 **Decision-Layer**：`rbf-decisions.md`。
本文件仅保留接口契约与其 SSOT（API 签名/语义条款）。

**文档关系**：
- [rbf-format.md](rbf-format.md) **实现**本文档所约束的 framing/CRC/scan 行为（Layer 0 wire format）
- [rbf-type-bone.md](rbf-type-bone.md) 提供核心类型的建议形态（non-normative）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| `rbf-interface.md` | Layer 0/1 边界（本文档） | `IRbfFile` 门面与对外可见类型/行为契约 |
| `rbf-format.md` | Layer 0 (RBF) | RBF 二进制线格式规范（wire format） |
| `rbf-type-bone.md` | Craft-Tier (Internal) | 核心类型骨架、RbfRawOps 内部实现层设计 |

---

## 2. 术语表（Layer 0）
> 本节定义 RBF 层的独立术语。上层业务语义（如 FrameTag 取值）由上层文档定义。

## term `FrameTag` 帧类型标识符

> **FrameTag** 是 4 字节（`uint`）的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。
>
> **三层视角**：
> - **存储层**：`uint`（线格式中的 4 字节 LE 字段）
> - **接口层**：`uint`（本文档定义的 API 参数/返回类型）
> - **应用层**：上层可自由选择 enum 或其他类型进行打包/解包

**保留值**：无。RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

### derived [H-FRAMETAG-4B-ALIGNMENT] FrameTag 4字节对齐设计理由
> 4B 的 @`FrameTag` 保持 Payload 4B 对齐，并支持 fourCC 风格的类型标识（如 `META`, `OBJV`）。


## term `Tombstone` 墓碑帧

### spec [F-TOMBSTONE-DEFINITION] 墓碑帧定义
> **Tombstone**（墓碑帧）是帧的有效性标记（Layer 0 元信息），表示该帧已被逻辑删除或是 Auto-Abort 的产物。

RbfFrame 通过 `bool IsTombstone` 属性暴露此状态，上层无需关心底层编码细节（编码定义见 rbf-format.md）。

### spec [S-RBF-TOMBSTONE-VISIBLE] ScanReverse产出Tombstone
> `IRbfFile.ScanReverse()` MUST 产出所有通过 framing/CRC 校验的帧，包括 Tombstone 帧。

### spec [S-UPPERLAYER-TOMBSTONE-SKIP] 上层跳过Tombstone
> 上层记录读取逻辑 MUST 忽略 Tombstone 帧（`IsTombstone == true`），不将其解释为业务记录。

### derived [H-TOMBSTONE-VISIBILITY-RATIONALE] 墓碑帧可见性设计理由
> - `ScanReverse()` 是"原始帧扫描器"，职责边界清晰；
> - Tombstone 可见性对诊断/调试有价值；
> - 过滤责任在 Layer 1，不在 Layer 0；
> - 接口层隐藏编码细节，上层无需知道 wire format 的具体字节值。

## term `SizedPtr` 帧句柄

### spec [F-SIZEDPTR-DEFINITION] SizedPtr定义
> **SizedPtr** 是 8 字节紧凑表示的 offset+length 区间，作为 RBF Interface 层的核心 Frame 句柄类型。

**来源（SSOT）**：`Atelia.Data.SizedPtr`（实现位于 `atelia/src/Data/SizedPtr.cs`）

### spec [S-RBF-SIZEDPTR-CREDENTIAL] SizedPtr凭据语义
- depends: @[S-RBF-DECISION-SIZEDPTR-CREDENTIAL](rbf-decisions.md)
> 在 RBF Interface 中，`SizedPtr` 作为"可再次读取的凭据（ticket）"。凭据语义由 Decision-Layer 锁定，详见 @[S-RBF-DECISION-SIZEDPTR-CREDENTIAL](rbf-decisions.md)。

### derived [H-SIZEDPTR-WIRE-MAPPING] SizedPtr与FrameBytes映射
> **Wire Mapping**：`SizedPtr` 与 FrameBytes 的跨层对应关系由 `rbf-format.md` 的 @[S-RBF-SIZEDPTR-WIRE-MAPPING] 定义。
>
> **位分配与范围**：见 SSOT `atelia/src/Data/SizedPtr.cs`（采用 38:26 位分配方案，4B 对齐）。

### spec [S-SIZEDPTR-ALIGNMENT-CONSTRAINT] SizedPtr对齐约束
> - 有效 SizedPtr MUST 4 字节对齐（`OffsetBytes % 4 == 0` 且 `LengthBytes % 4 == 0`）
> - 超出范围的值在构造时抛出 `ArgumentOutOfRangeException`

## term `Frame` 帧

> **Frame** 是 RBF 的基本 I/O 单元。

上层只需知道：

- 每个 Frame 有一个 @`FrameTag`、`Payload` 和 `IsTombstone` 状态
- Frame 写入后返回其 @`SizedPtr`（包含 offset+length）
- Frame 读取通过 @`SizedPtr` 定位

> Frame 的内部结构（wire format）在 rbf-format.md 中定义，接口层无需关心。

---

## 3. 对外门面（Facade）与写入 (Layer 1 Interface)

### spec [A-RBF-FILE-FACADE] IRbfFile接口定义

```csharp
/// <summary>
/// RBF 文件对象门面。
/// </summary>
/// <remarks>
/// <para>职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。</para>
/// <para><b>并发约束</b>：同一实例在任一时刻最多 1 个 open Builder。</para>
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>
    /// 获取当前文件逻辑长度（也是下一个写入 Offset）。
    /// </summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    SizedPtr Append(uint tag, ReadOnlySpan<byte> payload);

    /// <summary>
    /// 复杂帧构建（流式写入 payload / payload 内回填）。
    /// </summary>
    /// <remarks>
    /// <para>注意：在 Builder Dispose/Commit 前，TailOffset 不会更新。</para>
    /// <para>注意：存在 open Builder 时，不应允许并发 Append/BeginAppend。</para>
    /// </remarks>
    RbfFrameBuilder BeginAppend(uint tag);

    /// <summary>随机读。</summary>
    AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr);

    /// <summary>逆向扫描。</summary>
    RbfReverseSequence ScanReverse();

    /// <summary>
    /// durable flush（落盘）。
    /// </summary>
    /// <remarks>
    /// <para>用于上层 commit 顺序（例如 data→meta）的 durable 边界。</para>
    /// </remarks>
    void DurableFlush();

    /// <summary>
    /// 截断（恢复用）。
    /// </summary>
    void Truncate(long newLengthBytes);
}

public static class RbfFile {
    public static IRbfFile CreateNew(string path);       // FailIfExists
    public static IRbfFile OpenExisting(string path);    // 验证 Genesis
}
```

### spec [S-RBF-READFRAME-RESULTPATTERN] ReadFrame使用Result-Pattern
```clause-matter
depends: "@[S-RBF-DECISION-READFRAME-RESULTPATTERN](rbf-decisions.md)"
```
> 随机读取 API MUST 使用 Result-Pattern（返回 `AteliaResult<RbfFrame>`），不得使用 bool 模式。

### spec [S-RBF-TAILOFFSET-DEFINITION] TailOffset语义
> `IRbfFile.TailOffset` MUST 表示“当前逻辑文件长度（byte length）”，并且等于下一次 `Append/BeginAppend` 的写入起点（byte offset）。

### spec [S-RBF-TAILOFFSET-UPDATE] TailOffset更新规则
> `TailOffset` MUST 只在以下时刻推进：
> - `Append()` 成功返回后；
> - `BeginAppend()` 返回的 `RbfFrameBuilder.Commit()` 成功返回后；
> - `Truncate()` 成功返回后。
>
> 在 open Builder 的生命周期内（`Commit/Dispose` 之前），`TailOffset` MUST NOT 提前更新。

### spec [S-RBF-DURABLEFLUSH-SEMANTICS] DurableFlush语义
> `IRbfFile.DurableFlush()` MUST 尝试将“已提交写入”的数据持久化到物理介质。
>
> - 若发生不可恢复的 I/O 错误，允许抛出异常（具体异常类型由实现选择）。
> - 本方法不对“未提交的 Builder 写入”做任何可观察承诺。

### spec [S-RBF-TRUNCATE-SEMANTICS] Truncate语义
> `IRbfFile.Truncate(long newLengthBytes)` MUST 将文件逻辑长度设置为 `newLengthBytes`。
>
> - `newLengthBytes` MUST 为非负且满足 4B 对齐（否则 MUST throw `ArgumentOutOfRangeException`）。
> - 截断是恢复路径能力；调用方 MUST 自行确保截断点符合其恢复语义（例如指向 Fence 位置）。

### spec [A-RBF-FRAME-BUILDER] RbfFrameBuilder定义

```csharp
/// <summary>
/// 帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：调用方 MUST 调用 <see cref="Commit"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。</para>
/// <para><b>Auto-Abort（Optimistic Clean Abort）</b>：若未 Commit 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-AUTO-ABORT-SEMANTICS]。</para>
/// </remarks>
public ref struct RbfFrameBuilder {
    /// <summary>
    /// Payload 写入器。
    /// </summary>
    /// <remarks>
    /// <para>该写入器实现 <see cref="IBufferWriter{Byte}"/>，因此可用于绝大多数序列化场景。</para>
    /// <para>此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。</para>
    /// <para>接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。</para>
    /// <para><b>注意</b>：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-AUTO-ABORT-SEMANTICS]。</para>
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
    /// （除非出现不可恢复的不变量破坏），并且必须让 File Facade 回到可继续写状态。</para>
    /// </remarks>
    public void Dispose();
}
```

**关键语义**：

### spec [S-RBF-BUILDER-AUTO-ABORT-SEMANTICS] Auto-Abort逻辑语义
```clause-matter
see-also: "@[I-RBF-BUILDER-AUTO-ABORT-IMPL](rbf-type-bone.md)"
```
> 若 `RbfFrameBuilder` 未调用 `Commit()` 就执行 `Dispose()`：
>
> **逻辑语义**：
> - 该帧视为**逻辑不存在**（logical non-existence）
> - 上层 Record Reader 遍历时 MUST NOT 看到此帧作为业务记录
>
> **后置条件**：
> - `Dispose()` 后，底层 MUST 可继续写入后续帧
> - 后续 `Append()` / `BeginAppend()` 调用 MUST 成功
> - `Dispose()` 在此分支 MUST NOT 抛出异常（除非出现不可恢复的不变量破坏）
>
> **实现路径**：见 [rbf-type-bone.md](rbf-type-bone.md) @[I-RBF-BUILDER-AUTO-ABORT-IMPL]

此机制防止上层异常导致 Writer 死锁，同时在可能时优化为零 I/O。


### spec [S-RBF-BUILDER-SINGLE-OPEN] 单Builder约束
> 同一 `IRbfFile` 实例同时最多允许 1 个 open `RbfFrameBuilder`。
> 在前一个 Builder 完成（Commit 或 Dispose）前调用 `BeginAppend()` MUST 抛出 `InvalidOperationException`。

---

## 4. 读取与扫描（通过 IRbfFile 暴露）

> `ReadFrame()` 与 `ScanReverse()` 的 framing/CRC 与 Resync 行为由 [rbf-format.md](rbf-format.md) 定义。
> 本节只约束上层可观察到的结果形态与序列语义。

### spec [A-RBF-REVERSE-SEQUENCE] RbfReverseSequence定义

```csharp
/// <summary>
/// 逆向扫描序列（duck-typed 枚举器，支持 foreach）。
/// </summary>
/// <remarks>
/// <para><b>设计说明</b>：返回 ref struct 而非 IEnumerable，因为 RbfFrame 是 ref struct。</para>
/// <para>上层通过 foreach 消费，不依赖 LINQ。</para>
/// </remarks>
public ref struct RbfReverseSequence {
    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfReverseEnumerator GetEnumerator();
}

/// <summary>
/// 逆向扫描枚举器。
/// </summary>
public ref struct RbfReverseEnumerator {
    /// <summary>当前帧。</summary>
    public RbfFrame Current { get; }
    
    /// <summary>移动到下一帧。</summary>
    public bool MoveNext();
}
```

### spec [S-RBF-SCANREVERSE-NO-IENUMERABLE] ScanReverse不实现IEnumerable
> `RbfReverseSequence` MUST NOT 实现 `IEnumerable<RbfFrame>`。
>
> **原因**：`RbfFrame` 是 `ref struct`，无法作为泛型参数。

### spec [S-RBF-SCANREVERSE-EMPTY-IS-OK] 空序列合法
> 空文件（仅含 Genesis Fence）或无有效帧的文件，`ScanReverse()` MUST 返回空序列（0 元素），MUST NOT 抛出异常。

### spec [S-RBF-SCANREVERSE-CURRENT-LIFETIME] Current生命周期约束
> `RbfReverseEnumerator.Current` 的生命周期 MUST NOT 超过下次 `MoveNext()` 调用。
> 上层如需持久化帧数据，MUST 在 `MoveNext()` 前显式复制。

### spec [A-RBF-FRAME-STRUCT] RbfFrame定义

```csharp
/// <summary>
/// RBF 帧数据结构。
/// </summary>
/// <remarks>
/// <para>只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrame 的 buffer）。</para>
/// </remarks>
public readonly ref struct RbfFrame {
    /// <summary>帧位置（凭据）。</summary>
    public SizedPtr Ptr { get; init; }
    
    /// <summary>帧类型标识符。</summary>
    public uint Tag { get; init; }
    
    /// <summary>帧负载数据。</summary>
    public ReadOnlySpan<byte> Payload { get; init; }
    
    /// <summary>是否为墓碑帧。</summary>
    public bool IsTombstone { get; init; }
}
```

---

## 5. 使用示例（Informative）

> 本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义。

```csharp
TODO:
```

---

## 6. 最近变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.26 | 2026-01-11 | **文档职能分离**：拆分 Auto-Abort 条款为逻辑语义（本文档 @[S-RBF-BUILDER-AUTO-ABORT-SEMANTICS]）+ 实现路径（type-bone.md @[I-RBF-BUILDER-AUTO-ABORT-IMPL]）；明确本文档为规范性契约，type-bone.md 为非规范性实现指南 |
| 0.25 | 2026-01-11 | **接口细节对齐**：`Truncate` 参数类型改为 `long`（与 `TailOffset` 一致）；更新文档关系表中 `rbf-type-bone.md` 层级描述；§3 标题增加层级标注；统一 `RbfFrame` 生命周期注释；设计原则位置调整 |
| 0.24 | 2026-01-11 | **对齐 Type Bone**：以 `IRbfFile` 作为对外 Shape；将复杂写入入口命名为 `BeginAppend`；将落盘语义收敛为 `DurableFlush`；更新 Tombstone/ScanReverse 文字以匹配门面暴露方式；移除对写入路径内部实现绑定的对外暴露 |
| 0.23 | 2026-01-09 | **一致性修复**：将 @[S-RBF-SIZEDPTR-CREDENTIAL]、@[H-SIZEDPTR-WIRE-MAPPING] 改为纯引用模式，消除与 Decision-Layer 的语义重复；移除易漂移的数值表格（参见 [2026-01-09-rbf-consistency-fix](../../../agent-team/handoffs/implementer/2026-01-09-rbf-consistency-fix.md)） |
| 0.22 | 2026-01-09 | **AI-Design-DSL 完整迁移**：修复读取接口损坏内容；补全 RbfReverseSequence/RbfFrame 定义；新增 @[A-RBF-REVERSE-SEQUENCE]、@[S-RBF-SCANREVERSE-NO-IENUMERABLE]、@[S-RBF-SCANREVERSE-EMPTY-IS-OK]、@[H-SCANREVERSE-CURRENT-LIFETIME]、@[A-RBF-FRAME-STRUCT] 条款 |
| 0.21 | 2026-01-09 | **AI-Design-DSL 格式迁移**：将条款标识符转换为 DSL 格式（decision/design/hint + clause-matter）；将设计理由拆分为独立 hint 条款；增加术语定义（term）格式 |
| 0.20 | 2026-01-07 | **移除特殊值语义**：RBF Interface 不再为任何 `SizedPtr` 取值定义特殊语义；读取失败通过 `ReadFrame` 的 `AteliaResult` Failure 表达 |
| 0.19 | 2026-01-07 | **决策分层 + ReadFrame Result-Pattern**：新增 Decision-Layer 文件 `rbf-decisions.md`（锁定关键决策）；随机读取 API 改为 `ReadFrame` 并返回 `AteliaResult<RbfFrame>`；补齐 `SizedPtr`/`IReservableBufferWriter`/`AteliaResult<T>` 的 SSOT 引用；为 `SizedPtr` 增加 ticket 语义（凭据） |
| 0.18 | 2026-01-06 | **SizedPtr 替代旧版地址占位类型**（[W-0006](../../../wish/W-0006-rbf-sizedptr/artifacts/)）：移除旧版地址占位类型，引入 SizedPtr 作为核心 Frame 句柄；新增 `[F-SIZEDPTR-DEFINITION]`；移除旧版地址相关条款；`RbfFrame.Address` 改为 `RbfFrame.Ptr` |
| 0.17 | 2025-12-28 | **FrameTag 接口简化**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-wrapper-type-audit.md)）：移除 `FrameTag` record struct，接口层统一使用 `uint`；移除 `[F-FRAMETAG-DEFINITION]` 条款；§2.1 改为概念描述（三层视角：存储/接口/应用） |
| 0.16 | 2025-12-28 | **RbfFrameBuilder Payload 接口简化**：`RbfFrameBuilder.Payload` 统一为 `IReservableBufferWriter`（SSOT：`atelia/src/Data/IReservableBufferWriter.cs`） |

## 7. 待实现时确认

> 以下问题可在实现阶段确认：

- **错误处理**：`ReadFrame()` 的错误码集合与分层边界（P2）
- **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）

---
