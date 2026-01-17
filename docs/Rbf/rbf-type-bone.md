---
docId: "rbf-type-bone"
title: "RBF 核心类型骨架 (Type Bone)"
status: "Draft"
doc-type: "Implementation Guide"
normative: false
summary: "基于 RandomAccess 的无状态核心读写组件与 Facade 设计（非规范性）"
depends_on:
  - "rbf-interface.md"
  - "rbf-decisions.md"
---

# RBF 核心类型骨架 (Type Bone)
**本文档性质**：Implementation Guide（实现指南），非规范性。
实现者 MAY 在不违反 [rbf-interface.md](rbf-interface.md) 契约的前提下采用不同实现路径。
本文档描述的是"推荐实现方案"，不是"唯一合法实现"。

本文档定义 RBF 子系统的核心类型设计。

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
| `RbfReverseSequence` | [rbf-interface.md#A-RBF-REVERSE-SEQUENCE](rbf-interface.md#A-RBF-REVERSE-SEQUENCE) | 逆向扫描序列 |

---

**设计主旨**：
- **底层**：采用 `System.IO.RandomAccess` API，围绕 `SafeFileHandle` 构建无状态（或临时状态）的静态操作原语。
- **并发**：核心读写组件线程安全（依赖 OS 的原子读写能力），无副作用。
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

## 2. 底层操作原语 (Low-Level Primitives)

`RbfReadImpl` 提供无状态的静态方法，直接操作文件句柄。
这是具体实现层（Implementation Layer），**便于进行基于临时文件的集成测试**。

```csharp
namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 读取操作实现。
/// </summary>
internal static class RbfReadImpl {
    // 读路径 (Read Path)

    /// <summary>
    /// 将帧读入调用方提供的 buffer。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ticket">帧位置凭据（SizedPtr 携带 offset + length）。</param>
    /// <param name="buffer">调用方提供的缓冲区，长度 MUST >= ticket.Length。</param>
    /// <returns>读取结果（成功含帧视图，失败含错误码）。</returns>
    /// <remarks>使用 RandomAccess.Read 实现，无状态，并发安全。</remarks>
    public static AteliaResult<RbfFrame> ReadFrame(
        SafeFileHandle file, SizedPtr ticket, Span<byte> buffer);

    /// <summary>
    /// 从 ArrayPool 借缓存读取帧。调用方 MUST 调用 Dispose() 归还 buffer。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ticket">帧位置凭据（SizedPtr 携带 offset + length）。</param>
    /// <returns>读取结果（成功含 pooled 帧，失败含错误码）。</returns>
    /// <remarks>
    /// <para>返回的 RbfPooledFrame 内部持有 ArrayPool buffer。</para>
    /// <para>调用方 MUST 调用 Dispose() 归还 buffer，否则内存泄漏。</para>
    /// </remarks>
    public static AteliaResult<RbfPooledFrame> ReadPooledFrame(
        SafeFileHandle file, SizedPtr ticket);

    /// <summary>
    /// 创建逆向扫描序列。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="scanOrigin">文件逻辑长度（扫描起点）。</param>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false。</param>
    /// <returns>逆向扫描序列结构。</returns>
    /// <remarks>
    /// <para>RawOps 层直接实现过滤逻辑，与 Facade 层 @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER] 保持一致。</para>
    /// </remarks>
    public static RbfReverseSequence ScanReverse(SafeFileHandle file, long scanOrigin, bool showTombstone = false);
}

/// <summary>
/// RBF 写入操作实现。
/// </summary>
internal static class RbfWriteImpl {
    // 写路径 (Write Path)

    /// <summary>
    /// 开始构建一个帧（Complex Path）。
    /// </summary>
    /// <remarks>
    /// <para><b>[Internal]</b>：仅限程序集内调用。</para>
    /// <para>返回的 Builder 内部持有 file 引用和 writeOffset。</para>
    /// </remarks>
    internal static RbfFrameBuilder _BeginFrame(SafeFileHandle file, long writeOffset, uint tag);
}
```

---

## 3. 读写组件 (Components)

### 3.1 写入构建器 (Writer Builder)

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-FRAME-BUILDER]

**实现说明**：
- 作为 `ref struct` 实现 Zero-Allocation on Hot Path
- 生命周期必须涵盖 Payload 写入过程
- 在 EndAppend 时一次性调用 RandomAccess.Write 刷入磁盘（或 Flush 内部 Buffer）
- 内部字段：`SafeFileHandle _file`、`long _offset`、`byte[] _buffer` 等

### 3.1.1 Auto-Abort 实现路径

### spec [I-RBF-BUILDER-AUTO-ABORT-IMPL] Auto-Abort物理实现
```clause-matter
depends: "@[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME](rbf-interface.md)"
```
本条款定义如何实现 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME] 的逻辑语义。

**物理实现双路径**：

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

**重要说明**：`Payload` 的类型为 `IReservableBufferWriter` 并不承诺 Zero I/O 必然可用。Zero I/O 是实现优化，不是类型承诺。

### 3.2 逆向扫描序列 (Reverse Scanner)

**类型定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-REVERSE-SEQUENCE]

**实现说明**：
- 作为 `ref struct` 实现（如同 Span）
- 内部持有 handle 和 range
- 内部实现：维护一个 Read Window (e.g. 64KB)，在窗口内反向解析
- 调用 RandomAccess.Read 填充窗口
- `Current` 指向内部 Window Buffer 的切片

---

## 4. 门面 (Facade: Layer 1 Interface)

`IRbfFile` 是应用层（Layer 2）主要交互的对象，负责维护"有状态"的信息（如文件路径、打开的 Handle、当前的 TailOffset）。

**接口定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-IRBFFILE-SHAPE]

**实现说明**：
- 职责：资源管理 (IDisposable)、状态维护 (TailOffset)、调用转发
- 写入方法转发到 `RbfRawOps` 并更新内部状态
- 读取方法直接转发到 `RbfRawOps`（无状态调用）
- 在 Builder Dispose/EndAppend 前，TailOffset 不会更新，也不应允许并发 Append

**工厂方法**：
- `RbfFile.CreateNew(string path)` — 创建新文件（FailIfExists）
- `RbfFile.OpenExisting(string path)` — 打开已有文件（验证 Genesis）

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

**类型签名**：

```csharp
namespace Atelia.Rbf.Internal;

using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Data;

/// <summary>
/// RandomAccess → IByteSink 适配器
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：仅做 Push → RandomAccess.Write 转发 + offset 记账。</para>
/// <para>
/// <b>设计简化</b>：由于 <see cref="IByteSink"/> 是推式接口（调用者持有数据），
/// 无需持有 buffer、无需 ArrayPool 管理、无需三步舞（GetSpan/GetMemory/Advance）。
/// </para>
/// <para>
/// <b>并发</b>：非线程安全，依赖 <c>[S-RBF-BUILDER-SINGLE-OPEN]</c> 契约
/// （同一时刻只有一个活跃 Builder）。
/// </para>
/// </remarks>
internal sealed class RandomAccessByteSink : IByteSink {
    private readonly SafeFileHandle _file;
    private long _writeOffset;

    /// <summary>
    /// 创建 RandomAccess 写入适配器
    /// </summary>
    /// <param name="file">文件句柄（需具备 Write 权限）</param>
    /// <param name="startOffset">起始写入位置（byte offset）</param>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> 为 null</exception>
    public RandomAccessByteSink(SafeFileHandle file, long startOffset) {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _writeOffset = startOffset;
    }

    /// <summary>当前写入位置（byte offset）</summary>
    /// <remarks>
    /// Builder 层可用于计算已写入字节数（CurrentOffset - StartOffset）
    /// 以及最终 HeadLen 回填。
    /// </remarks>
    public long CurrentOffset => _writeOffset;

    /// <summary>推送数据到文件</summary>
    /// <remarks>
    /// 调用 <see cref="RandomAccess.Write(SafeFileHandle, ReadOnlySpan{byte}, long)"/>
    /// 写入数据并推进 offset。
    ///
    /// <para><b>错误处理</b>：I/O 异常直接抛出（符合 Infra Fault 策略）。</para>
    /// </remarks>
    public void Push(ReadOnlySpan<byte> data) {
        if (data.IsEmpty) return;

        RandomAccess.Write(_file, data, _writeOffset);
        _writeOffset += data.Length;
    }
}
```

### 5.3 关键实现约束

### spec [I-RBF-SEQWRITER-HEADLEN-GUARD] HeadLen必须立即reserve
see: @[I-RBF-BUILDER-AUTO-ABORT-IMPL]
`RbfFrameBuilder` 创建时 MUST 立即 reserve HeadLen（4 字节），以确保 `SinkReservableWriter` 进入 buffered mode。
若先写 payload 后 reserve，会进入 passthrough mode 导致提前 flush（破坏 Zero I/O Abort）。

### spec [I-RBF-BYTESINK-PUSH-FORWARDS-AND-ADVANCES-OFFSET] Push推送语义
`Push(ReadOnlySpan<byte> data)` MUST 调用 `RandomAccess.Write(_file, data, _writeOffset)` 并推进 `_writeOffset += data.Length`。

**实现模式**（伪代码）：
```csharp
// RbfRawOps._BeginFrame()
internal static RbfFrameBuilder _BeginFrame(SafeFileHandle file, long writeOffset, uint tag) {
    var sink = new RandomAccessByteSink(file, writeOffset);
    var chunkedWriter = new SinkReservableWriter(sink);

    // 关键：立即 reserve HeadLen（4 字节）
    var headLenSpan = chunkedWriter.ReserveSpan(4, out int headLenToken);
    BinaryPrimitives.WriteUInt32LittleEndian(headLenSpan, 0); // placeholder

    // 写 FrameTag（紧跟 HeadLen）
    var tagSpan = chunkedWriter.GetSpan(4);
    BinaryPrimitives.WriteUInt32LittleEndian(tagSpan, tag);
    chunkedWriter.Advance(4);

    return new RbfFrameBuilder {
        _chunkedWriter = chunkedWriter,
        _headLenToken = headLenToken,
        _startOffset = writeOffset,
        _sink = sink,  // 保留引用以便查询 CurrentOffset
        // ...
    };
}
```

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
| **并发安全** | 依赖 @[S-RBF-BUILDER-SINGLE-OPEN] | 多线程写入会破坏 offset 一致性 | 接口契约保证单 Builder |

### 5.6 待实现阶段确认（P2）

1. **CRC32C 计算时机**：EndAppend 时遍历 chunks vs 增量计算
2. ~~异步版本~~：如需异步，实现 `RandomAccessByteSinkAsync` 配合 `RandomAccess.WriteAsync`

---

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.5 | 2026-01-17 | **ReadFrame 重构**：更新 §2 底层原语签名（RbfRawOps → RbfReadImpl/RbfWriteImpl）；新增 IRbfFrame/RbfPooledFrame 引用；参数名 ptr → ticket |
| 0.4 | 2026-01-11 | **适配器简化**：将 §5 从 `IBufferWriter` 适配器改为 `IByteSink` 适配器；删除 `SequentialRandomAccessBufferWriter`（~80 行）；新增 `RandomAccessByteSink`（~25 行）；删除 `@[I-RBF-SEQWRITER-TYPE]`、`@[I-RBF-SEQWRITER-ADVANCE-IMMEDIATE]`、`@[I-RBF-SEQWRITER-BUFFER-POOL]`、`@[I-RBF-SEQWRITER-DISPOSE-NOEXCEPT]`；新增 `@[I-RBF-BYTESINK-IS-MINIMAL-FORWARDER]`、`@[I-RBF-BYTESINK-PUSH-FORWARDS-AND-ADVANCES-OFFSET]`、`@[I-RBF-BYTESINK-ERROR-THROW]`；来自 [设计报告](../../../agent-team/handoffs/2026-01-11-randomaccess-bytesink-design.md) |
| 0.3 | 2026-01-11 | **RandomAccess 适配器设计**：新增 §5（SequentialRandomAccessBufferWriter）；定义 @[I-RBF-SEQWRITER-TYPE]、@[I-RBF-SEQWRITER-ADVANCE-IMMEDIATE]、@[I-RBF-SEQWRITER-BUFFER-POOL]、@[I-RBF-SEQWRITER-HEADLEN-GUARD]、@[I-RBF-SEQWRITER-ERROR-THROW]、@[I-RBF-SEQWRITER-DISPOSE-NOEXCEPT]；来自 [畅谈会](../../../agent-team/meeting/2026-01-11-rbf-randomaccess-adapter.md) 决议 |
| 0.2 | 2026-01-11 | **文档职能分离**：移除与 interface.md 重复的公开类型定义，改为引用；新增 Auto-Abort 实现路径条款 @[I-RBF-BUILDER-AUTO-ABORT-IMPL]；增加快速导航区块；明确为非规范性实现指南 |
| 0.1 | 2026-01-11 | 初始版本（Type Bone 骨架） |
