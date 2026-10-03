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
| [rbf-interface.md](rbf-interface.md) | Layer 0/1边界 | RBF1只读/RBF3纯新writer、结构Open与单尾截断/补原后缀、byte API与读取资格管线 |
| [rbf-format.md](rbf-format.md) | Layer 0 (RBF) | RBF1旧byte布局及RBF3 units/Key/28B布局、原wire CRC与Header派发 |
| [rbf-type-bone.md](rbf-type-bone.md) | Plan-Tier (指导编码) | 核心类型骨架（非规范性实现指南） |
| [rbf-recovery.md](rbf-recovery.md) | Design Memo | 离线救援/分析用 Recovery Scan 设计备忘 |
| [rbf-tail-recovery-refactoring.md](rbf-tail-recovery-refactoring.md) | Implementation / RBF1 | 旧byte-length格式单个残缺尾帧自愈的历史记录；不支配当前RBF3普通打开 |
| [rbf-tail-recovery-review.md](rbf-tail-recovery-review.md) | Review | 三位 reviewer 两轮辩证复核的需求、裁决与证据记录 |
| [rbf-open-fast-path-refactoring.md](rbf-open-fast-path-refactoring.md) | Implementation / RBF3 | 0999851源码、670/670测试及独立复核的正式W:证据见§11；健康/异常Open、实际接入成本与边界，不重标旧byte实验 |
| [rbf3-code-design-review-0999851.md](rbf3-code-design-review-0999851.md) | Review / post-implementation | 四个独立工作包及W:反例探针；入口状态、默认FrameInfo、串行合同与分配优化建议，待后续修复 |
| [rbf-codec-implementation-study.md](rbf-codec-implementation-study.md) | Research / implementation notes | writer预处理与reader解码专项保留历史byte测量；Data与RBF3接入已实施，新生产证据归实施记录§11 |
| [ZeroThenTinyBitmapRandom基础方案](../Data/xor-escape-key-refactoring.md) | Implementation / Data foundation | 已实施：Fence≥2^26、Data窄API、共享选择/XOR核与具体writer fused接点；三spans/真实chunks同切片资格，byte输入/int长度域保留；结果见§9 |
| [rbf-derived-notes.md](rbf-derived-notes.md) | Derived | 推导、算例与答疑（允许滞后/可删改） |
| [rbf-test-vectors.md](rbf-test-vectors.md) | Test | RBF1原始byte向量保留；§8为独立RBF3 wire/CRC参考与本轮生产资格边界 |

## Decision-Layer 约束

本轮用户已授权G0/G2规范与源码同步：权威合同采用RBF1只读兼容及RBF3纯新writer，生产Header为RBF3以区别历史byte RBF2。源码、测试与正式证据已独立复核，具体状态由[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)记录；旧快照不是新units验收。4B根决策澄清的是物理L，wire U无需低2bits对齐；原根语义不变。

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
