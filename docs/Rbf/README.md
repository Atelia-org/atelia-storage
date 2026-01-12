---
docId: "rbf-index"
title: "RBF 文档集索引"
produce_by:
  - "wish/W-0009-rbf/wish.md"
---

# RBF 文档集索引

RBF（Reversible Binary Framing）是 Atelia 的二进制信封格式，用于安全封装 payload。

## 规范遵循

本文档集遵循：
- [Atelia 规范约定](../spec-conventions.md)
- [AI-Design-DSL](../../../agent-team/wiki/SoftwareDesignModeling/AI-Design-DSL.md)

## 文档层级（SSOT）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| [rbf-decisions.md](rbf-decisions.md) | **Decision-Layer** | 关键设计决策（AI 不可修改） |
| [rbf-interface.md](rbf-interface.md) | Layer 0/1 边界 | `IRbfFile` 门面与对外可见类型/行为契约 |
| [rbf-format.md](rbf-format.md) | Layer 0 (RBF) | 二进制线格式规范（wire format） |
| [rbf-type-bone.md](rbf-type-bone.md) | Plan-Tier (指导编码) | 核心类型骨架（非规范性实现指南） |
| [rbf-derived-notes.md](rbf-derived-notes.md) | Derived | 推导、算例与答疑（允许滞后/可删改） |
| [rbf-test-vectors.md](rbf-test-vectors.md) | Test | 测试向量 |

## Decision-Layer 约束

`rbf-decisions.md` 中的条款为 **AI 不可主动修改（MVP 固定）**：AI MUST NOT 修改任何 Decision 条款的语义
受 Decision-Layer 约束的文档：`rbf-interface.md`、`rbf-format.md`

## 文档间依赖关系

```
rbf-decisions.md (Decision-Layer)
       ↓ 约束
rbf-interface.md (Shape-Tier) ← rbf-type-bone.md (实现指南)
       ↓ 定义接口
rbf-format.md (Layer 0 Wire Format)
       ↓ 推导
rbf-derived-notes.md (Derived)
       ↓ 验证
rbf-test-vectors.md (Test)
```

## Derived-Layer 说明

`rbf-derived-notes.md` 的内容：
- 从 SSOT（interface/format）推导得到的结论
- 澄清、算例、FAQ
- **当与 SSOT 冲突时，MUST 以 SSOT 为准**
- MAY 被删除、重写或暂时缺失，不构成规范缺陷
