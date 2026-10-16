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
| [rbf-interface.md](rbf-interface.md) | Layer 0/1边界 | RBF3 读写与非 RBF3 Header 拒绝、结构Open与单尾截断/补原后缀、byte API与读取资格管线 |
| [rbf-format.md](rbf-format.md) | Layer 0 (RBF) | RBF3 units/Key/28B布局、原wire CRC与Header派发 |
| [01-rbf-sized-append.md](01-rbf-sized-append.md) | S1 / Accepted | 公共尺寸计算、已知尺寸 Builder 与提前 ticket；限定源码资格见[阶段验收](01-rbf-sized-append-acceptance.md) |
| [rbf-initial-frame-prefix.md](rbf-initial-frame-prefix.md) | Implemented / scoped evidence | 有界初始单帧前缀纯核验 public API 已实施；具体源码/平台证据见原记录，不重标 S1 |
| [rbf-reverse-boundary-plan.md](rbf-reverse-boundary-plan.md) | Implemented / scoped evidence | 已接通按 RbfScanBoundary 逆扫的公共重载；包含 anchor，资格与 V2-A 证据见原记录 |
| [rbf-type-bone.md](rbf-type-bone.md) | Plan-Tier (指导编码) | 核心类型骨架（非规范性实现指南） |
| [rbf-open-fast-path-refactoring.md](rbf-open-fast-path-refactoring.md) | Implementation / RBF3 | 0999851源码、670/670测试及独立复核的正式W:证据见§11；健康/异常Open、实际接入成本与边界，不重标旧byte实验 |
| [rbf3-code-design-review-0999851.md](rbf3-code-design-review-0999851.md) | Review / post-implementation | 0999851原始发现及反例探针保留；各finding的最终裁决与已完成修复另见处置和验收记录 |
| [rbf3-review-findings-disposition.md](rbf3-review-findings-disposition.md) | Finding disposition | 三方辩证裁决的最小模型、已收束finding与C1/pre-Push/MaxOffset/输出优化的延期触发条件 |
| [rbf3-review-repairs-acceptance.md](rbf3-review-repairs-acceptance.md) | Acceptance / review repairs | c4377fd修复及合同收尾；RBF677/677、Data288/288的实际构建身份与新证据；不重标历史crash/perf |
| [rbf3-resource-and-output-refactoring.md](rbf3-resource-and-output-refactoring.md) | Refactoring / implemented | pooled读唯一失败释放、RBF3 total≤8KiB打包已实施；已完成切片移入验收，保留后续触发条件 |
| [rbf3-resource-and-output-acceptance.md](rbf3-resource-and-output-acceptance.md) | Acceptance / resource and output | RBF696/696；W:两组共14对4/8KiB测量、JIT分支栈成本与真实部分写入/重开；源码和binary身份分别记录 |
| [rbf-codec-implementation-study.md](rbf-codec-implementation-study.md) | Research / implementation notes | writer预处理与reader解码专项保留历史byte测量；Data与RBF3接入已实施，新生产证据归实施记录§11 |
| [ZeroThenTinyBitmapRandom基础方案](../Data/xor-escape-key-refactoring.md) | Implementation / Data foundation | 已实施：Fence≥2^26、Data窄API、共享选择/XOR核与具体writer fused接点；三spans/真实chunks同切片资格，byte输入/int长度域保留；结果见§9 |
| [rbf-test-vectors.md](rbf-test-vectors.md) | Test | §8 为独立 RBF3 wire/CRC 参考；RBF1 向量见归档，历史生产证据保持原身份 |

## Decision-Layer 约束

2026-10-16 用户授权主线仅支持 RBF3，连同旧栈一起移除底层 RBF1 只读与离线救援能力。RBF1、RBF2 及未知 Header 在修改前拒绝。归档来源与本次验证见[退役记录](../rbf1-retirement.md)；历史实施/验收保持原 revision 身份。4B 根决策约束物理 L，wire U 无需低 2bits 对齐。

`rbf-decisions.md` 中的条款为 **AI 不可主动修改（MVP 固定）**：AI MUST NOT 修改任何 Decision 条款的语义
受 Decision-Layer 约束的文档：`rbf-interface.md`、`rbf-format.md`

## 文档间依赖关系

```
rbf-decisions.md (Decision-Layer)
       ↓ 约束
rbf-interface.md (Shape-Tier) ← rbf-type-bone.md (实现指南)
       ↓ 定义接口
rbf-format.md (Layer 0 Wire Format)
       ↓ 验证
rbf-test-vectors.md (Test)
```

## 历史资料

RBF1 的恢复方案、派生算例、旧字节向量和 EventJournal 设计由 Git 历史与 RBF1 维护分支承载，定位见[退役记录](../rbf1-retirement.md)。本目录保留跨 RBF3 演进的实施、审阅和验收记录，其当时的兼容性结论不覆盖当前合同。
