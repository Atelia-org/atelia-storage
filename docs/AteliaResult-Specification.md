# AteliaResult 规范文档

> **版本**: 1.1  
> **日期**: 2025-12-26  
> **状态**: 正式规范（Normative）  
> **来源**: [LoadObject 命名与返回值设计畅谈会](../../agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md)  
> **修订**: [AteliaResult 适用边界畅谈会](../../agent-team/meeting/StateJournal/2025-12-26-ateliaresult-boundary.md)

---

## 1. 概述

### 1.1 定位

`AteliaResult<T>` 和 `AteliaError` 是 **Atelia 项目的基础错误处理机制**，类似于：
- Rust 的 `std::result::Result<T, E>`
- C# 的 `Nullable<T>`（但携带错误信息）
- Go 的 `(T, error)` 多返回值模式

它们作为 **跨组件统一的成功/失败协议**，被 StateJournal、DocUI、PipeMux 等所有 Atelia 组件共享。

### 1.2 设计目标

| 目标 | 说明 |
|------|------|
| **LLM-Native** | 错误信息默认面向 Agent（LLM 可读），包含因果链和恢复建议 |
| **机器可判定** | `ErrorCode` 作为稳定键，支持代码分支和自动化处理 |
| **跨组件一致** | StateJournal、DocUI、PipeMux 共享同一套错误协议 |
| **可序列化** | 支持 JSON 序列化，便于跨进程/跨语言通信 |

### 1.3 核心洞察

> **错误即示能（Error as Affordance）**：一个好的错误对象不应只告诉 Agent "你错了"（Stop），而应告诉 Agent "你可以怎么做"（Detour）。

---

## 2. 规范语言

本文使用 RFC 2119 / RFC 8174 定义的关键字表达规范性要求：

- **MUST / MUST NOT**：绝对要求/绝对禁止
- **SHOULD / SHOULD NOT**：推荐/不推荐
- **MAY**：可选

---

## 3. 类型定义

### 3.1 `AteliaResult<T>`

```csharp
namespace Atelia;

public readonly struct AteliaResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public AteliaError? Error { get; }
    
    // 工厂方法
    public static AteliaResult<T> Success(T value);
    public static AteliaResult<T> Failure(AteliaError error);
}
```

**设计决策**：
- 使用 `readonly struct` 避免装箱开销
- 单泛型参数 `T`（不是双泛型 `Result<T, E>`），降低使用复杂度
- Error 类型固定为 `AteliaError` 基类，通过派生类实现扩展

### 3.2 AteliaError

```csharp
namespace Atelia;

public abstract record AteliaError(
    string ErrorCode,                              // MUST
    string Message,                                // MUST
    string? RecoveryHint = null,                   // SHOULD
    IReadOnlyDictionary<string, string>? Details = null,  // MAY
    AteliaError? Cause = null                      // MAY
);
```

**字段说明**：

| 字段 | 级别 | 说明 |
|------|------|------|
| `ErrorCode` | MUST | 机器可判定的错误码，格式为 `{Component}.{ErrorName}` |
| `Message` | MUST | Agent-Friendly 的错误描述，包含因果链和上下文 |
| `RecoveryHint` | SHOULD | 恢复建议，告诉 Agent 下一步可以尝试什么 |
| `Details` | MAY | 键值对形式的上下文信息（最多 20 个 key） |
| `Cause` | MAY | 导致此错误的原因错误（最多 5 层深度） |

### 3.3 AteliaException

```csharp
namespace Atelia;

public abstract class AteliaException : Exception, IAteliaHasError
{
    public AteliaError Error { get; }
    public string ErrorCode => Error.ErrorCode;
    public string? RecoveryHint => Error.RecoveryHint;
    
    protected AteliaException(AteliaError error) : base(error.Message);
    protected AteliaException(AteliaError error, Exception? innerException);
}
```

**设计原则**：异常与 `AteliaResult.Error` **同源同表**——它们共享同一个 `AteliaError` 对象，保证错误协议的一致性。

### 3.4 IAteliaHasError

```csharp
namespace Atelia;

public interface IAteliaHasError
{
    AteliaError Error { get; }
}
```

用于统一异常和结构化错误的访问方式。

---

## 4. 规范条款

### 4.1 错误内容条款

#### [ATELIA-ERROR-CODE-MUST]

> **所有对外可见的失败载体（异常 + Result.Error）MUST 包含 `ErrorCode`。**

- `ErrorCode` MUST 是非空字符串
- `ErrorCode` MUST 遵循 [ATELIA-ERRORCODE-NAMING] 命名规范
- `ErrorCode` MUST 在对应组件的 ErrorCode Registry 中登记

#### [ATELIA-ERROR-MESSAGE-MUST]

> **所有失败载体 MUST 包含 `Message`。**

- `Message` MUST 是非空字符串
- `Message` SHOULD 面向 Agent（LLM 可读），包含：
  - 发生了什么错误
  - 错误的上下文（如相关的 ObjectId、文件路径等）
  - 可能的原因

**好的示例**：
```
Object 42 not found in VersionIndex. The object may have been deleted 
or never committed. Last known checkpoint: epoch 15.
```

**坏的示例**：
```
Invalid operation.
```

#### [ATELIA-ERROR-RECOVERY-HINT-SHOULD]

> **失败载体 SHOULD 包含 `RecoveryHint`。**

- `RecoveryHint` 告诉调用方（尤其是 LLM Agent）下一步可以尝试什么
- 格式：以动词开头的祈使句

**示例**：
```
Call CreateObject() to create a new object, or verify the ObjectId using ListObjects().
```

#### [ATELIA-ERROR-DETAILS-MAY]

> **失败载体 MAY 包含 `Details` 键值对。**

约束：
- `Details` 的 key 数量 MUST NOT 超过 **20 个**
- Value 类型为 `string`；复杂结构 SHOULD 使用 JSON-in-string

#### [ATELIA-ERROR-CAUSE-MAY]

> **失败载体 MAY 包含 `Cause` 链。**

约束：
- `Cause` 链深度 MUST NOT 超过 **5 层**
- 实现 SHOULD 使用 `AteliaError.IsCauseChainTooDeep()` 方法验证

### 4.2 ErrorCode 命名规范

#### [ATELIA-ERRORCODE-NAMING]

> **ErrorCode 格式 MUST 为 `{Component}.{ErrorName}`。**

规则：
- `Component`：组件名，使用 PascalCase（如 `StateJournal`、`DocUI`、`PipeMux`）
- `ErrorName`：错误名，使用 PascalCase（如 `ObjectNotFound`、`CorruptedRecord`）
- 分隔符：单个英文句点 `.`

**合法示例**：
```
StateJournal.ObjectNotFound
StateJournal.CorruptedRecord
DocUI.AnchorNotFound
PipeMux.ConnectionTimeout
```

**非法示例**：
```
OBJECT_NOT_FOUND          ❌ 缺少 Component 前缀
statejournal.objectnotfound  ❌ 未使用 PascalCase
StateJournal-ObjectNotFound  ❌ 使用了错误的分隔符
```

### 4.3 ErrorCode Registry 规范

#### [ATELIA-ERRORCODE-REGISTRY]

> **各组件 MUST 在各自文档目录维护 ErrorCode 注册表。**

注册表位置约定：
```
atelia/docs/{Component}/ErrorCodes.md
```

注册表格式：

| ErrorCode | 触发条件 | 处理方式 | RecoveryHint 示例 |
|-----------|----------|----------|-------------------|
| `{Component}.{ErrorName}` | 描述触发条件 | 返回错误 / 抛异常 | 恢复建议示例 |

**示例（StateJournal）**：

| ErrorCode | 触发条件 | 处理方式 | RecoveryHint 示例 |
|-----------|----------|----------|-------------------|
| `StateJournal.ObjectNotFound` | ObjectId 不在 VersionIndex | 返回错误 | "Call CreateObject() to create a new object" |
| `StateJournal.CorruptedRecord` | CRC 校验失败 | 抛异常 | — |
| `StateJournal.InvalidObjectId` | ObjectId 在保留区 (0..15) | 抛异常 | — |
| `StateJournal.ObjectDetached` | 对象已 Detach | 抛异常 | "Object was never committed. Call CreateObject() to create a new object." |

---

## 5. 使用规范

### 5.1 何时使用 `bool + out` vs `AteliaResult<T>` vs 异常

本节定义三类失败表达模式：

| 模式 | 签名形式 | 适用场景 |
|------|----------|----------|
| **Classic Try-pattern** | `bool TryX(..., out T value)` | 失败原因单一，无需诊断 payload |
| **Result-pattern** | `AteliaResult<T> X(...)` | 失败原因多元，需要 ErrorCode/RecoveryHint |
| **Exception-pattern** | 抛出 `AteliaException` | 不变量破坏或需要上层介入的故障 |

#### [ATELIA-FAILURE-CLASSIFICATION]

> **对外可见的失败 MUST 被归类为以下三类之一：**

1. **Expected Domain Failure（可预期域内失败）**：由输入/状态/权限/业务条件导致，调用方可能据此采取下一步动作。
2. **Invariant Breach（不变量破坏）**：实现错误、契约被破坏、内部状态不可能、参数违反"必须成立"的前置条件。
3. **Infrastructure Fault（基础设施故障）**：IO/存储/网络/进程环境等导致的失败，通常需要上层策略介入或重试。

#### [ATELIA-BOOL-OUT-WHEN]

> **API MAY 使用 `bool + out` 形式，当且仅当同时满足：**

- 对调用方而言，失败空间在语义上等价为**单一原因**（即：调用方不需要也不应区分失败原因）；并且
- 失败时不需要提供结构化诊断 payload（`ErrorCode` / `RecoveryHint` / `Details`）；并且
- 操作不涉及需要显式表达的 Infrastructure Fault。

约束：
- `TryX(..., out T value)` 在返回 `false` 时 MUST 将 `value` 置为 `default`。
- `TryX` 在面对 Expected Domain Failure 时 MUST NOT 以异常表达控制流。

**适用示例**：
- `Dictionary.TryGetValue` — 失败原因只有"键不存在"
- `int.TryParse` — 失败原因只有"格式错误"
- `DurableDict.TryGetValue` — 失败原因只有"键不存在"

#### [ATELIA-RESULT-WHEN]

> **API MUST 使用 `AteliaResult<T>` 形式，当任一条件成立：**

- 失败原因**多元**，且调用方需要区分以选择不同恢复/分支策略；或
- 失败需要携带可机器判定的键（`ErrorCode`）或可操作的恢复建议（`RecoveryHint`）；或
- 操作可能遭遇 Infrastructure Fault 且需要以结构化方式呈现给调用方。

约束：
- `AteliaResult<T>.Failure` MUST 携带 `AteliaError`，且满足本规范 §4.1 的所有 MUST 条款。

**适用示例**：
- `TryLoadObject` — 失败可能是"不存在"/"已 Detach"/"数据损坏"
- `TryCommit` — 失败可能是"冲突"/"磁盘满"/"损坏"

#### [ATELIA-EXCEPTION-WHEN]

> **API MUST 抛异常（推荐抛 `AteliaException`）当任一条件成立：**

- 发生 Invariant Breach；或
- 发生 Infrastructure Fault 且该故障在当前抽象层不应被调用方以"正常分支"处理。

**适用示例**：
- 数据损坏（CRC 校验失败）
- 不变量破坏（实现缺陷）
- 参数违反前置条件（如 null 参数）

#### 直觉测试（Informative）

> 当操作失败时，调用者的第一反应是：
> - "哦，没有就算了" → `bool + out`
> - "为什么失败？我需要决定下一步" → `AteliaResult<T>`
> - "这不应该发生！" → 异常

### 5.2 命名约定

#### [ATELIA-TRY-PREFIX-NONTHROWING]

> **方法名以 `Try` 开头时，对 Expected Domain Failure，方法 MUST NOT 通过异常表达失败。**

允许返回 `false`（Classic Try-pattern）或 `AteliaResult.Failure`（Result-pattern）。

#### [ATELIA-TRY-BOOL-SIGNATURE]

> **若方法返回类型为 `bool` 且用于表达"尝试"，则：**

- 方法名 MUST 以 `Try` 开头
- 方法 MUST 至少包含一个 `out` 参数承载成功产物

**示例**：
```csharp
// ✅ 正确
bool TryGetValue(ulong key, out TValue? value);
bool TryParse(string input, out int result);

// ❌ 错误：缺少 out 参数
bool TryValidate(string input);
```

#### [ATELIA-RESULT-NAMING-SHOULD]

> **返回 `AteliaResult<T>` 的方法 SHOULD 使用不带 `Try` 的动词名。**

**示例**：
```csharp
// ✅ 推荐
AteliaResult<IDurableObject> LoadObject(ulong objectId);
AteliaResult<CommitResult> Commit();

// ⚠️ 允许但不推荐
AteliaResult<IDurableObject> TryLoadObject(ulong objectId);
```

若出于一致性或历史原因需要保留 `TryXxx(): AteliaResult<T>` 形式：
- 方法 MUST NOT 使用 `out` 参数承载成功产物（避免与 Classic Try-pattern 混淆）
- 组件的 ErrorCode Registry MUST 覆盖该方法可能返回的主要 `ErrorCode`

---

### 5.3 派生类使用模式

**库内部（强类型便利）**：

```csharp
// StateJournal 派生类
public sealed record StateJournalObjectNotFoundError(ulong ObjectId)
    : AteliaError(
        ErrorCode: "StateJournal.ObjectNotFound",
        Message: $"Object {ObjectId} not found in VersionIndex",
        RecoveryHint: "Call CreateObject() to create a new object, or verify the ObjectId");
```

**库边界（协议面）**：

```csharp
// API 返回值只依赖 AteliaError 基类字段
public AteliaResult<IDurableObject> TryLoadObject(ulong objectId)
{
    if (!versionIndex.TryGetValue(objectId, out var ptr))
    {
        return AteliaResult<IDurableObject>.Failure(
            new StateJournalObjectNotFoundError(objectId));
    }
    // ...
}
```

**调用方处理**：

```csharp
var result = heap.TryLoadObject(objectId);

// 方式 1：通过 ErrorCode 判定
if (result.IsFailure && result.Error!.ErrorCode == "StateJournal.ObjectNotFound")
{
    // 创建新对象
    var newObj = heap.CreateObject<MyObject>();
}

// 方式 2：模式匹配（如果需要访问派生类字段）
if (result.Error is StateJournalObjectNotFoundError notFound)
{
    Console.WriteLine($"Object {notFound.ObjectId} not found");
}

// 方式 3：使用 Match 方法
result.Match(
    onSuccess: obj => ProcessObject(obj),
    onFailure: err => LogError(err));
```

### 5.4 JSON 序列化示例

`AteliaError` 可直接序列化为 JSON，用于跨进程/跨语言通信：

```json
{
  "errorCode": "StateJournal.ObjectNotFound",
  "message": "Object 42 not found in VersionIndex. The object may have been deleted or never committed.",
  "recoveryHint": "Call CreateObject() to create a new object, or verify the ObjectId using ListObjects().",
  "details": {
    "objectId": "42",
    "lastCheckpoint": "15"
  },
  "cause": null
}
```

---

## 6. 与 StateJournal 规范的关系

本规范是从 [StateJournal MVP 设计 v2](StateJournal/mvp-design-v2.md) §3.4.8 Error Affordance 部分**提升**为全项目范围的规范。

### 6.1 条款映射

| StateJournal 原条款 | 提升后的 Atelia 条款 |
|---------------------|---------------------|
| [A-ERROR-CODE-MUST] | [ATELIA-ERROR-CODE-MUST] |
| [A-ERROR-MESSAGE-MUST] | [ATELIA-ERROR-MESSAGE-MUST] |
| [A-ERROR-RECOVERY-HINT-SHOULD] | [ATELIA-ERROR-RECOVERY-HINT-SHOULD] |
| [A-ERROR-CODE-REGISTRY] | [ATELIA-ERRORCODE-REGISTRY] |

### 6.2 向后兼容

StateJournal 的 `[A-*]` 条款仍然有效，作为 `[ATELIA-*]` 条款在 StateJournal 组件内的具体实例化。

---

## 7. 代码位置

```
atelia/
└── src/
    └── Primitives/              # netstandard2.0
        ├── AteliaResult.cs      # AteliaResult<T> 定义
        ├── AteliaError.cs       # AteliaError 基类定义
        ├── AteliaException.cs   # AteliaException 基类定义
        └── IAteliaHasError.cs   # 接口定义
```

---

## 附录 A：设计决策摘要

| 议题 | 决策 | 理由 |
|------|------|------|
| 机制级别 | Atelia 项目基础机制 | 跨组件统一成功/失败协议 |
| 泛型设计 | 单泛型 `AteliaResult<T>` | 避免 C# 双泛型的使用负担 |
| Error 类型 | `AteliaError` 基类 + 派生类 | 协议面稳定，库内可强类型 |
| 协议层键 | `string ErrorCode` | 可序列化、跨语言、可作文档索引 |
| Message 语义 | 默认 Agent-Friendly | Atelia 是 LLM-Native 框架 |
| Details 类型 | `IReadOnlyDictionary<string, string>` | 复杂结构用 JSON-in-string |
| ErrorCode 命名 | `{Component}.{ErrorName}` | 命名空间隔离，便于查找 |

---

## 附录 B：参考资料

- [RFC 7807: Problem Details for HTTP APIs](https://tools.ietf.org/html/rfc7807)
- [Rust std::result](https://doc.rust-lang.org/std/result/)
- [LoadObject 命名与返回值设计畅谈会](../../agent-team/meeting/StateJournal/2025-12-21-hideout-loadobject-naming.md)
- [StateJournal MVP 设计 v2](StateJournal/mvp-design-v2.md)
