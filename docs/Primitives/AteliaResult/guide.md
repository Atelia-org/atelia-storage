# AteliaResult 快速上手指南

> **面向 LLM Agent 优化**：阅读本文以快速掌握如何在代码中使用 `AteliaResult`。

## 1. 决策矩阵：我该用什么？

在编写新方法时，根据以下逻辑选择返回值模式：

| 场景特点 | 模式 | 签名示例 |
|:---------|:-----|:---------|
| 失败只是"没有" (如字典查不到) | **Try-Pattern** | `bool TryGet(Key key, out Val val)` |
| 失败有多种原因 (如"不存在" vs "被占用") | **Result-Pattern** | `AteliaResult<Val> Get(Key key)` |
| 失败是 Bug 或严重故障 (如数组越界) | **Exception** | `val GetOrThrow(Key key)` |

## 2. 代码速查表

### 2.1 返回成功

```csharp
// 方法签名
public AteliaResult<MyObject> CreateObject(string name) {
    var obj = new MyObject(name);

    // 方式 A: 隐式转换 (推荐)
    return obj;

    // 方式 B: 显式调用
    return AteliaResult<MyObject>.Success(obj);
}
```

### 2.2 返回失败

必须提供一个非 null 的 `AteliaError` 实例。`AteliaError` 是抽象 record，使用已定义的具体错误类型，或先派生自己的错误类型：

```csharp
public sealed record ObjectNotFoundError(ulong Id)
    : AteliaError(
        ErrorCode: "MyComp.NotFound",
        Message: $"Id {Id} missing",
        RecoveryHint: "Check the identifier, or create the object before loading it."
    );

public AteliaResult<MyObject> LoadObject(ulong id) {
    if (!Exists(id)) {
        // 隐式转换；也可显式 return AteliaResult<MyObject>.Failure(error)。
        var error = new ObjectNotFoundError(id);
        return error;
    }
    // ...
}
```

### 2.3 处理结果

```csharp
var result = service.LoadObject(123);

// 检查失败 (推荐模式)
if (result.IsFailure) {
    // result.Error 包含 ErrorCode、Message，以及可空的 RecoveryHint。
    logger.LogError($"Load failed: {result.Error.Message}");
    return;
}

// 获取值 (result.Value 在失败时为 default!)
var obj = result.Value;
// 或者
var obj = result.Unwrap();
```

## 3. 错误定义规范

定义新的 `AteliaError` 派生类时，字段等级以[正式规范](specification.md#36-ateliaerror-字段说明)为准：

1.  **ErrorCode（MUST）**: `{Component}.{ErrorName}` (PascalCase)
2.  **Message（MUST）**: 发生了什么 + 上下文
3.  **RecoveryHint（SHOULD）**: 有明确恢复建议时告诉 Agent 下一步做什么；没有合适建议时可为 null。

**模板：**

```csharp
public sealed record ConfigurationMissingError(string ConfigPath)
    : AteliaError(
        ErrorCode: "MyComponent.ConfigurationMissing",
        Message: $"Configuration file not found at '{ConfigPath}'",
        RecoveryHint: "Check if the file exists, or run 'init' to create default config."
    );
```

## 4. 带资源所有权的结果

当返回值是需要 `Dispose` 的资源（如池化的 buffer）时，使用 `DisposableAteliaResult<T>`：

```csharp
// 典型使用模式
using var result = api.GetResource().ToDisposable();
if (result.IsFailure) {
    Console.WriteLine(result.Error.Message);
    return;
}
var resource = result.Value;  // 安全使用
// scope 结束自动 Dispose
```

**Dispose 语义**：
- 成功时：调用 `Value.Dispose()`
- 失败时：静默无操作（Value 为 null）

**从 `AteliaResult<T>` 转换**：
```csharp
AteliaResult<MyResource> syncResult = GetResource();
using var disposable = syncResult.ToDisposable();
```

## 5. 常见问题 (FAQ)

- **Q: 为什么不能用 `var`?**
  A: `AteliaResult` 是 `ref struct` (在同步版中)，使用时限制较多，但在栈上零分配。

- **Q: 异步方法怎么办?**
  A: 使用 `AsyncAteliaResult<T>`。除了名字带 `Async` 且不是 `ref struct` 外，用法完全一致。

- **Q: 怎么把同步 Result 转异步?**
  A: `.ToAsync()` 扩展方法。

- **Q: 什么时候用 `DisposableAteliaResult<T>`?**
  A: 当 `T` 实现 `IDisposable` 且你希望用 `using` 语法管理生命周期时。典型场景：从对象池借用的 buffer。
