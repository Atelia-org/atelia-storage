# AteliaResult 设计文档

> **版本**: 1.0  
> **日期**: 2026-01-06  
> **状态**: 档案（Archived Design）  
> **使用规范**: [specification.md](specification.md)

---

## 1. 核心设计：双类型架构

### 1.1 问题背景：为什么不能只用一个 `Result`?

我们希望 `Result` 类型能尽量高效（struct），并且能包裹像 `Span<T>` 这样的现代 C# 高性能类型。
但是，`Span<T>` 是 `ref struct`。
- 如果泛型 `T` 是 `ref struct`，那么 `AteliaResult<T>` 也必须是 `ref struct`。
- `ref struct` 有很多限制：不能作为类字段、不能装箱、不能用于 `Task<T>`、不能用于异步方法参数。

因此，我们无法用同一个类型同时满足"高性能同步场景（支持 Span）"和"异步场景（支持 Task）"。

### 1.2 解决方案

我们将世界一分为二：

| 类型 | 约束 | 适用场景 |
|:-----|:-----|:---------|
| `AteliaResult<T>` | `ref struct` (allows ref struct) | 同步代码、栈上操作、解析器、底层 IO |
| `AteliaAsyncResult<T>` | 普通 `struct` | 异步方法 (`Task/ValueTask`)、保存到堆上 |

这两个类型在 API 使用体验上保持 99% 的一致性（`IsSuccess`, `Value`, `Error`）。只有底层约束不同。

---

## 2. 决策记录 (Decision Log)

### 2.1 命名：为什么用 `Async` 后缀？

| 候选方案 | 评价 |
|:---------|:-----|
| `RefResult` / `ValueResult` | ❌ `ValueResult` 容易与 `ValueTask` 混淆 |
| `LocalResult` / `SharedResult` | ❌ `Shared` 暗示线程共享，容易误导 |
| `AteliaResult` / `AteliaAsyncResult` | ✅ 符合 .NET 惯例（`Read`/`ReadAsync`），语义直观 |

**决策**：`AteliaResult` 作为基础名（最紧约束版本），`AteliaAsyncResult` 作为异步/堆兼容版本。

### 2.2 泛型设计：为什么不用 `Result<T, E>`?

Rust 的 `Result<T, E>` 很强大，但在 C# 中，双泛型甚至三泛型极其啰嗦：
`Result<Dictionary<string, List<int>>, ConfigError> result = ...`

我们决定采用 **单泛型** 策略：
- `AteliaResult<T>` 只有一个 `T`。
- 错误类型永远是 `AteliaError`（及其派生类）。

**权衡**：
- 🟢 优点：签名极其简洁。
- 🔴 缺点：无法在类型签名中静态声明"这个方法只会抛出 ConfigError"。
- 💊 补救：通过文档注释和 ErrorCode Registry 弥补。

### 2.3 `IsSuccess` 实现细节

```csharp
public bool IsSuccess => _error is null;
```

**决策**：不使用单独的 `bool _isSuccess` 字段。
- 🟢 优点：节省内存（更小的 struct），避免状态不一致。
- 🟢 优点：`default` 状态自然是有风险的（null error, default value），这在设计上推动必须通过工厂方法创建。

### 2.4 允许 `Success(null)`

| 状态 | 含义 |
|:-----|:-----|
| `Success(null)` | 操作成功，结果为空（如查询返回空结果集） |
| `Failure(error)` | 操作失败，有错误原因 |

我们明确区分了 `null` 和 `Failure`。`null` 是有效的值，`Failure` 是流程的中断。

### 2.5 为什么删除了函数式 API (`Map`, `FlatMap`)?

在早期原型中，我们实现了 `Map` 等函数式操作符。
但在 `ref struct` 的世界里，这行不通。
`ref struct` 不能作为泛型参数传递给 `Func<T, U>`（因为委托是类，涉及装箱逃逸）。
直到 C# 编译器完全支持 `ref struct` 在委托中的受限用法前，我们移除了这些 API，鼓励使用简单的 `if (res.IsFailure) return ...`。

### 2.6 `ToAsync()` 转换

由于 `ref struct` 不能隐式转换为普通 struct（如果 T 是 ref struct 就会炸）。
我们实现为扩展方法 `ToAsync()`，并利用编译器约束检查：
- 用户只能对非 `ref struct` 的 `T` 调用 `.ToAsync()`。
- 如果 `T` 是 `Span<byte>`，调用 `ToAsync()` 会在编译时报错（符合预期，不能把 Span 放到 Task 里）。

---

## 3. 历史设计摘要

以下表格汇总了我们在演进过程中的关键权衡：

| 议题 | 决策 | 理由 |
|------|------|------|
| 机制级别 | Atelia 项目基础机制 | 跨组件统一成功/失败协议，而不是让每个库造轮子 |
| Error 类型 | `AteliaError` 基类 + 派生类 | 协议面稳定（只认基类），库内部可享用强类型便利 |
| 协议层键 | `string ErrorCode` | 可序列化、跨语言、可作文档索引 |
| Message 语义 | 默认 Agent-Friendly | Atelia 是 LLM-Native 框架 |
| Details 类型 | `IReadOnlyDictionary<string, string>` | 复杂结构用 JSON-in-string，保持 Payload 扁平 |
| ErrorCode 命名 | `{Component}.{ErrorName}` | 命名空间隔离，便于查找 |
