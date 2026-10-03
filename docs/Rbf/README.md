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
- AI-Design-DSL（历史外部方法来源，原 `agent-team/wiki/SoftwareDesignModeling/AI-Design-DSL.md` 不在本仓；本仓所需约定见上一项）

## 文档层级（SSOT）

| 文档 | 层级 | 定义内容 |
|------|------|----------|
| [rbf-decisions.md](rbf-decisions.md) | **Decision-Layer** | 关键设计决策（AI 不可修改） |
| [rbf-interface.md](rbf-interface.md) | Layer 0/1 边界 | `IRbfFile` 门面与对外可见类型/行为契约 |
| [rbf-format.md](rbf-format.md) | Layer 0 (RBF) | 二进制线格式规范（wire format） |
| [rbf-type-bone.md](rbf-type-bone.md) | Plan-Tier (指导编码) | 核心类型骨架（非规范性实现指南） |
| [rbf-recovery.md](rbf-recovery.md) | Design Memo | 离线救援/分析用 Recovery Scan 设计备忘 |
| [rbf-tail-recovery-refactoring.md](rbf-tail-recovery-refactoring.md) | Implementation / RBF1 | 旧byte-length格式单个残缺尾帧自愈的实施记录；新profile后续方案见普通打开重构 |
| [rbf-tail-recovery-review.md](rbf-tail-recovery-review.md) | Review | 三位 reviewer 两轮辩证复核的需求、裁决与证据记录 |
| [rbf-open-fast-path-refactoring.md](rbf-open-fast-path-refactoring.md) | Plan-Tier | 已采纳、待实施：Head/Tail长度4B units、Fence≥2^26、尾Key单份、结构Open/内容ReadFrame、进程终止下截尾/补Key+Fence；新wire及RBF实施门禁 |
| [rbf-codec-implementation-study.md](rbf-codec-implementation-study.md) | Research / G1 | writer预处理与reader解码专项；C#原型/W:实测仍为历史byte-length证据，实施建议已同步units；生产接入待实施 |
| [ZeroThenTinyBitmapRandom基础方案](../Data/xor-escape-key-refactoring.md) | Plan-Tier / Data foundation | 已采纳、待实施：Fence≥2^26、Data窄API、共享选择/XOR核与具体writer fused接点；先同时资格三spans/真实chunks，byte输入/int长度域保留 |
| [rbf-derived-notes.md](rbf-derived-notes.md) | Derived | 推导、算例与答疑（允许滞后/可删改） |
| [rbf-test-vectors.md](rbf-test-vectors.md) | Test | 测试向量 |

## Decision-Layer 约束

现行规范和生产代码仍为RBF1 byte-length格式；上述已采纳方案定义后续新profile，实施时按主方案G0/G2同步规范与向量。历史实验使用的RBF2候选名称不代表新units生产格式已经落地。

`rbf-decisions.md` 中的条款为 **AI 不可主动修改（MVP 固定）**：AI MUST NOT 修改任何 Decision 条款的语义
受 Decision-Layer 约束的文档：`rbf-interface.md`、`rbf-format.md`

## 文档间依赖关系

```
rbf-decisions.md (Decision-Layer)
       ↓ 约束
rbf-interface.md (Shape-Tier) ← rbf-type-bone.md (实现指南)
       ↓ 定义接口
rbf-format.md (Layer 0 Wire Format)
       ↓ 派生救援策略
rbf-recovery.md (Design Memo)
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
