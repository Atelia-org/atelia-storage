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

> **本文档性质**：Implementation Guide（实现指南），非规范性。
> 实现者 MAY 在不违反 [rbf-interface.md](rbf-interface.md) 契约的前提下采用不同实现路径。
> 本文档描述的是"推荐实现方案"，不是"唯一合法实现"。

本文档定义 RBF 子系统的核心类型设计。

---

## 快速导航（引用的接口层定义）

本文档多处引用 [rbf-interface.md](rbf-interface.md) 中的公开类型定义，这里提供快速链接：

| 类型 | SSOT | 描述 |
|------|------|------|
| `IRbfFile` | [rbf-interface.md#A-RBF-FILE-FACADE](rbf-interface.md#A-RBF-FILE-FACADE) | RBF 文件对象门面 |
| `RbfFrame` | [rbf-interface.md#A-RBF-FRAME-STRUCT](rbf-interface.md#A-RBF-FRAME-STRUCT) | 帧数据结构 |
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

---

## 2. 底层操作原语 (Low-Level Primitives)

`RbfRawOps` 提供无状态的静态方法，直接操作文件句柄。
这是具体实现层（Implementation Layer），**便于进行基于临时文件的集成测试**。

```csharp
namespace Atelia.Rbf.Internal;

/// <summary>
/// RBF 原始操作集。
/// </summary>
public static class RbfRawOps {
    // 读路径 (Read Path)

    /// <summary>
    /// 随机读取指定位置的帧。
    /// </summary>
    /// <param name="file">文件句柄（需具备 Read 权限）。</param>
    /// <param name="ptr">帧位置凭据。</param>
    /// <returns>读取结果（成功含帧，失败含错误码）。</returns>
    /// <remarks>使用 RandomAccess.Read 实现，无状态，并发安全。</remarks>
    public static AteliaResult<RbfFrame> ReadFrame(SafeFileHandle file, SizedPtr ptr);

    /// <summary>
    /// 创建逆向扫描序列。
    /// </summary>
    /// <param name="file">文件句柄。</param>
    /// <param name="fileLength">文件逻辑长度（扫描起点）。</param>
    /// <returns>逆向扫描序列结构。</returns>
    public static RbfReverseSequence ScanReverse(SafeFileHandle file, long fileLength);

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
- 在 Commit 时一次性调用 RandomAccess.Write 刷入磁盘（或 Flush 内部 Buffer）
- 内部字段：`SafeFileHandle _file`、`long _offset`、`byte[] _buffer` 等

### 3.1.1 Auto-Abort 实现路径

### spec [I-RBF-BUILDER-AUTO-ABORT-IMPL] Auto-Abort物理实现
```clause-matter
depends: "@[S-RBF-BUILDER-AUTO-ABORT-SEMANTICS](rbf-interface.md)"
```
> 本条款定义如何实现 @[S-RBF-BUILDER-AUTO-ABORT-SEMANTICS] 的逻辑语义。

**物理实现双路径**：

| 路径 | 条件 | 机制 |
|------|------|------|
| **Zero I/O** (SHOULD) | 实现能保证 open builder 期间 payload 不外泄、且在 `Dispose()` 时可丢弃未提交 payload | 通过 reservation rollback、内部 reset 或其他等价机制 |
| **Tombstone** (MUST fallback) | 无法实现 Zero I/O | 将帧标记为 Tombstone（`IsTombstone == true`），完成帧写入 |

**Tombstone 路径细节**：
- Tombstone 帧 SHOULD 保留原 FrameTag 值（供诊断用）
- Tombstone 帧 MUST 通过 framing/CRC 校验
- ScanReverse MUST 产出 Tombstone 帧（由 interface.md @[S-RBF-TOMBSTONE-VISIBLE] 约束）

**选择逻辑**：
- MVP 实现 SHOULD 优先尝试 Zero I/O
- 若内部 Buffer 实现为 `ChunkedReservableWriter`，则 Zero I/O 可通过 chunk rollback 实现

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

**接口定义（SSOT）**：见 [rbf-interface.md](rbf-interface.md) @[A-RBF-FILE-FACADE]

**实现说明**：
- 职责：资源管理 (IDisposable)、状态维护 (TailOffset)、调用转发
- 写入方法转发到 `RbfRawOps` 并更新内部状态
- 读取方法直接转发到 `RbfRawOps`（无状态调用）
- 在 Builder Dispose/Commit 前，TailOffset 不会更新，也不应允许并发 Append

**工厂方法**：
- `RbfFile.CreateNew(string path)` — 创建新文件（FailIfExists）
- `RbfFile.OpenExisting(string path)` — 打开已有文件（验证 Genesis）

---

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.2 | 2026-01-11 | **文档职能分离**：移除与 interface.md 重复的公开类型定义，改为引用；新增 Auto-Abort 实现路径条款 @[I-RBF-BUILDER-AUTO-ABORT-IMPL]；增加快速导航区块；明确为非规范性实现指南 |
| 0.1 | 2026-01-11 | 初始版本（Type Bone 骨架） |
