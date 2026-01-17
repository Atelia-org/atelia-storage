# AteliaResult

`AteliaResult<T>` 是 Atelia 项目的标准错误处理机制，用于替代异常控制流，提供明确的成功/失败语义和结构化的错误信息。

## 快速导航

| 文档 | 描述 | 适用人群 |
|:-----|:-----|:---------|
| [⚡️ 快速上手 (Guide)](guide.md) | **LLM Agent 必读**。包含"什么时候用什么"、代码示例、常用模式。 | 所有开发者/Agent |
| [📜 正式规范 (Spec)](specification.md) | 定义了 MUST/SHOULD 条款、ErrorCode 格式、Schema 约束。 | 架构师、代码审阅者 |
| [🎨 设计与历史 (Design)](design.md) | 解释了"为什么是双类型"、"为什么不用双泛型"等背后的思考。 | 维护者、好奇者 |

## 类型家族

| 类型 | 用途 | 特点 |
|:-----|:-----|:-----|
| `AteliaResult<T>` | 同步场景 | `ref struct`，零分配，支持 `Span<T>` |
| `AsyncAteliaResult<T>` | 异步场景 | `readonly struct`，可用于 `Task`/`ValueTask` |
| `DisposableAteliaResult<T>` | 带资源所有权 | `class`，自动 Dispose，用于池化资源 |
| `IAteliaResult<T>` | 公共接口 | 统一契约，便于泛型编程 |

## 核心价值

> **错误即示能 (Error as Affordance)**
> 一个好的错误不应只说"你错了"(Stop)，而应说"你可以怎么做"(Detour)。

`AteliaResult` 强制要求错误包含 `RecoveryHint`，帮助调用者（尤其是 Agent）自主恢复。
