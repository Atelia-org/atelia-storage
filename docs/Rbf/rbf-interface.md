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
**引用**：[Atelia.Data.SizedPtr](atelia/src/Data/SizedPtr.cs)
@[S-RBF-DECISION-SIZEDPTR-CREDENTIAL](rbf-decisions.md)

## term `Frame` 帧
**Frame** 是 RBF 的基本 I/O 单元。Frame 的内部结构（wire format）接口层无需关心。

上层只需知道：
- 每个 Frame 有一个 @`FrameTag`、`Payload` 和 `IsTombstone` 状态
- Frame 写入后返回其 @`SizedPtr`（包含 offset+length）
- Frame 读取通过 @`SizedPtr` 定位

---

## 3. 对外门面（Facade）(Layer 1 Interface)

### spec [A-RBF-IRBFFILE-SHAPE] IRbfFile接口定义

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
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    RbfReverseSequence ScanReverse(bool showTombstone = false);

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

*@[S-RBF-DECISION-READFRAME-RESULTPATTERN]，随机读取 API Result-Pattern（返回 `AteliaResult<RbfFrame>`）。*

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
/// <summary>
/// 帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。
/// </summary>
/// <remarks>
/// <para><b>生命周期</b>：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。</para>
/// <para><b>Auto-Abort（Optimistic Clean Abort）</b>：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。</para>
/// </remarks>
public ref struct RbfFrameBuilder {
    /// <summary>
    /// Payload 写入器。
    /// </summary>
    /// <remarks>
    /// <para>该写入器实现 <see cref="IBufferWriter<byte>"/>，因此可用于绝大多数序列化场景。</para>
    /// <para>此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。</para>
    /// <para>接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。</para>
    /// <para><b>注意</b>：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// Zero I/O 是否可用由实现决定，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。</para>
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

`ReadFrame()` 与 `ScanReverse()` 的 framing/CRC 与 Resync 行为由 [rbf-format.md](rbf-format.md) 定义。
本节只约束上层可观察到的结果形态与序列语义。

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

### derived [S-RBF-SCANREVERSE-NO-IENUMERABLE] ScanReverse不实现IEnumerable
`RbfReverseSequence` MUST NOT 实现 `IEnumerable<RbfFrame>`。
**原因**：`RbfFrame` 是 `ref struct`，无法作为泛型参数。

### spec [S-RBF-SCANREVERSE-EMPTY-IS-OK] 空序列合法
当文件为空（仅含 Genesis Fence）或 **根据过滤条件无可见帧** 时，`ScanReverse()` MUST 返回空序列（0 元素），MUST NOT 抛出异常。

### spec [S-RBF-SCANREVERSE-CURRENT-LIFETIME] Current生命周期约束
`RbfReverseEnumerator.Current` 的生命周期 MUST NOT 超过下次 `MoveNext()` 调用。
上层如需持久化帧数据，MUST 在 `MoveNext()` 前显式复制。

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

本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义。

### 5.1 简单写入 (Append)

适用于数据已在内存中准备好的简单场景。

```csharp
void SimpleWrite(IRbfFile file, uint myTag, byte[] data) {
    // 直接写入，原子性（要么全写进，要么全不写进）
    SizedPtr ptr = file.Append(myTag, data);
    
    Console.WriteLine($"Written at: {ptr.Offset}, Length: {ptr.Length}");
    // 此时 TailOffset 已自动推进
}
```

### 5.2 流式/复杂写入 (BeginAppend)

适用于数据量大、需要流式生成或零拷贝拼接的场景。
建议配合`System.Buffers.BufferExtensions`使用`RbfFrameBuilder.Payload`

```csharp
void StreamingWrite(IRbfFile file, uint myTag, IEnumerable<byte[]> chunks) {
    // 1. 开启事务
    // 注意：builder 是 ref struct，最好配合 using 确保即使异常也能 Dispose
    using var builder = file.BeginAppend(myTag);
    
    // 2. 获取 Writer (IBufferWriter<byte>)
    var writer = builder.Payload;
    
    try {
        foreach (var chunk in chunks) {
            // IBufferWriter 标准范式：GetSpan -> CopyTo -> Advance
            // 不依赖额外的扩展方法，手动管理内存与推进
            var span = writer.GetSpan(chunk.Length);
            chunk.CopyTo(span);
            writer.Advance(chunk.Length);
        }
        
        // 3. 结束追加 (EndAppend)
        // 只有 EndAppend 后，数据才对 Read 即刻可见，TailOffset 才会推进
        SizedPtr ptr = builder.EndAppend();
        
        // EndAppend 后不能再写入，Dispose 变为无操作
    }
    catch (Exception ex) {
        // 4. 自动回滚 (Auto-Abort)
        // 若发生异常导致 EndAppend 未被调用，
        // 退出 using 块触发 Dispose 时，会自动执行 Auto-Abort。
        // 该帧在物理上可能由部分脏数据残留，但在逻辑上视为“不存在”。
        Console.WriteLine("Write aborted explicitly or by exception.");
        throw; 
    }
}
```

### 5.3 随机读取 (ReadFrame)

适用于根据索引（SizedPtr）回查数据的场景。

```csharp
void RandomAccess(IRbfFile file, SizedPtr ptr) {
    // ReadFrame 返回 AteliaResult<RbfFrame>
    var result = file.ReadFrame(ptr);

    if (result.IsFailure) {
        // 处理错误：可能是 CRC 校验失败、越界或 Magic 损坏
        Console.WriteLine($"Read failed: {result.Error.Message}");
        return;
    }

    // 获取帧（ref struct，生命周期受限于 result 所在作用域）
    RbfFrame frame = result.Value;
    
    // 如需长期持有数据，必须在此拷贝
    byte[] safePayload = frame.Payload.ToArray();
}
```

### 5.4 逆向扫描 (ScanReverse)

适用于恢复、重放或查找最新记录。

```csharp
void RecoverState(IRbfFile file) {
    // 默认不显示 Tombstone
    var sequence = file.ScanReverse(showTombstone: false);

    // 只能使用 foreach (duck-typed)
    foreach (RbfFrame frame in sequence) {
        // 这里的 frame 是“经过校验的有效帧”
        // 损坏的帧已被 Resync 机制自动跳过
        
        Console.WriteLine($"Found Frame: Tag={frame.Tag}, Len={frame.Payload.Length}");
        
        if (IsStateRestored(frame)) break;
    }
}
```

---

## 6. 最近变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.28 | 2026-01-12 | **方法重命名**：`RbfFrameBuilder.Commit()` →  `EndAppend()`，与 `BeginAppend()` 形成对称配对，最大化 LLM 可预测性；详见[命名讨论会](../../../../agent-team/meeting/2026-01-12-rbf-builder-lifecycle-naming.md) |
| 0.27 | 2026-01-12 | **Tombstone 默认隐藏**：修改 `ScanReverse` 接口增加 `bool showTombstone = false` 参数；废弃 `[S-RBF-TOMBSTONE-VISIBLE]` 改为 `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]`，确立默认过滤 Tombstone 的行为 |
| 0.26 | 2026-01-11 | **文档职能分离**：拆分 Auto-Abort 条款为逻辑语义（本文档 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]）+ 实现路径（type-bone.md @[I-RBF-BUILDER-AUTO-ABORT-IMPL]）；明确本文档为规范性契约，type-bone.md 为非规范性实现指南 |
| 0.25 | 2026-01-11 | **接口细节对齐**：`Truncate` 参数类型改为 `long`（与 `TailOffset` 一致）；更新文档关系表中 `rbf-type-bone.md` 层级描述；§3 标题增加层级标注；统一 `RbfFrame` 生命周期注释；设计原则位置调整 |

## 7. 待实现时确认

以下问题可在实现阶段确认：

- **错误处理**：`ReadFrame()` 的错误码集合与分层边界（P2）
- **ScanReverse 终止条件**：遇到损坏数据时的策略（P2）

---
