# FrameStore / VersionStore 分阶段设计入口

日期：2026-10-03。状态：**方向已确认；经过三轮辩证审阅的候选设计，尚未创建项目或实施运行时代码**。
源码观察基线：`main @ 70d1009e78a73342a0c0fdc8ffed7731dec58173`。本文档集供后续逐阶段细化、审阅和实施。

目标：**以 RBF3 的帧原子性为基础，让中层构建新状态，再通过统一的根发布使状态生效。**
恢复依据是完整事实帧及其有效发布记录。RBF 处理物理尾部；中层负责状态构建和依赖闭包；VersionStore 负责发布及 ref/branch/tag。

## 当前最小模型

FrameStore 每实例一个追加流，提供精确地址、跨段顺序 plan 和同步 ConfirmDurable。VersionStore 借入 data FrameStore，拥有私有 control FrameStore；不建通用多流平台，不向调用方签发耐久 receipt。
Prepare 在业务输出前给调用方精确记录 token；Commit 确认最新 data 完成前缀，再追加/确认单条控制记录、安装内部投影。应用收到 Confirmed 后再安装自己的状态。
RefId/revision/attempt 共用一个 RecordToken 表示；普通 head/tag 查询给事实地址，实际内容由 FrameStore.ReadFrame 一次完整读取。首次 Open/Inspect 允许显式完整控制回放；snapshot 的规模资格单独取得。
裁决、具体失败轨迹、保留的不同意见和延迟触发条件见 [本轮设计审阅](reviews/2026-10-03-dialectical-review.md)。这些技术默认仍是 Draft，不冒充新增用户决策。

## 阅读与状态规则

- 先读仓库根 [README](../../README.md)，再读 [S0 总体决策](00-architecture-decisions.md)。
- 本目录遵循 [规范约定](../spec-conventions.md)：`decision` 记录会话已确认方向；S1–S6 的 `spec` 是阶段候选要求，待该阶段审定后才成为实施合同；建议、算例、API 名称不自动冻结。
- 阶段状态使用 `Draft → Ready → Implementing → Accepted`。Ready 前定稿字段/API/算法及验收映射；可以对范围明确的必要子合同单独审定，未支持能力不得借整体标签宣称成立。
- 本次完成的是 Draft 文档。测试、进程中断验证、包消费及消费者接入均是后续工作，不在这里宣称通过。
- 旧库维护独立于这条演进链。其当前编译与合同不一致问题见 [原调查](../Rbf/rbf3-adaptation-baseline-investigation.md)；该调查基于 `e6ad4d2`，不冒充本次基线的新测试结果。

## 阶段顺序

下图的箭头表示合同依赖；下游使用的必要子合同必须先形成、实施并取得对应资格。它不要求整层横向完工后才能试验下一层；各阶段规范只依赖前序阶段。

```mermaid
flowchart LR
    S0["S0 总体边界"] --> S1["S1 RBF 已知尺寸追加"]
    S1 --> S2["S2 单流 FrameStore"]
    S2 --> S3["S3 跨段计划 / 同步屏障"]
    S3 --> S4["S4 双上下文根发布"]
    S4 --> S5["S5 命名 / 历史 / 可选索引"]
    S5 --> S6["S6 集成与交付"]
```

| 阶段文档 | 本阶段形成的合同 | 主要实施范围 | 出口 |
| --- | --- | --- | --- |
| [S0 总体边界与决策](00-architecture-decisions.md) | 项目边界、事实归属、恢复模型、兼容政策 | 文档决策 | 后续阶段无需反向依赖消费者语义 |
| [S1 RBF 已知尺寸追加](01-rbf-sized-append.md) | 精确尺寸、提前 ticket、格式信息、Builder 生命周期 | `src/Rbf`、`tests/Rbf.Tests` | 单文件能力可独立使用和验证 |
| [S2 FrameStore 核心](02-framestore-core.md) | FrameAddress、文件布局、segment 生命周期、读写入口 | 新建 FrameStore 与其测试项目 | 多段 Frame 存储可独立使用、恢复及重开 |
| [S3 批次与耐久确认](03-framestore-batches-and-durability.md) | 跨段地址规划、循环引用、完成与同步屏障 | FrameStore 与其测试项目 | 消费者无需管理 segment flush，最终跨段能力保留 |
| [S4 VersionStore 根发布](04-versionstore-publication.md) | data/control 归属、Prepare/Commit、token CAS 与精确查询 | 新建 VersionStore 与其测试项目 | 单记录根发布、Unknown/Confirmed 和重开可独立验收 |
| [S5 ref/branch/tag 与索引](05-versionstore-names-and-indexes.md) | 命名/历史核心，一份 snapshot 独立增强 | VersionStore 与其测试项目 | 核心正确性与启动规模资格分别报告 |
| [S6 集成与交付](06-integration-and-delivery.md) | 第二种状态模型、公共包消费、交付边界 | examples、eng、CI；消费者接入另有明确范围 | 源码、包及消费者证据分别齐备 |

S4 使用显式完整控制回放作为正确性基线；S5 先完成命名，真实规模需要时单独取得 snapshot 资格。全量回放原型不冒充启动 SLA，控制流分离也不自动证明规模预算。

## 最小纵向实施顺序

| 片 | 必要前序合同与证明 | 范围限制 |
| --- | --- | --- |
| V0 | S1 尺寸/格式/early ticket，双 RBF 文件互引读回 | 单文件能力，不声称新库已实现 |
| V1 | S2 单段 owned 存储 + S3 基础 barrier + S4 双上下文 unnamed ref；Prepare→Commit→冷重开 | 只接受已验收单段子合同，小控制历史 |
| V2 | S3 同段 plan，A↔B/self-reference 后发布，取消/reuse/Unknown/ABA | 不用同段例子代替跨段交付 |
| V3 | S2 rotation/boundary + S3 跨段 plan，根文件外依赖耐久与进程中断 | 完成后才声明 S2/S3 多段资格 |
| V4 | S5 Branch/Tag/current/history 核心 | snapshot/Archive/provenance 分别选范围取得资格 |
| V5 | S6 平台、公共包、真实消费者边界 | 包 smoke/消费者接入仍各自出证据 |

两个新生产项目和两个测试项目仍分别在 S2/S4 创建；V1 可以只创建和实现这些阶段的必要部分。S6 汇总组合与交付，不垄断第一次 public API 纵向验证。

## 新项目落点

| 项目路径 | 身份 | 首次创建阶段 |
| --- | --- | --- |
| `src/FrameStore/FrameStore.csproj` | `Atelia.FrameStore`，生产库 | S2 |
| `tests/FrameStore.Tests/FrameStore.Tests.csproj` | `Atelia.FrameStore.Tests`，非 pack 测试 | S2 |
| `src/VersionStore/VersionStore.csproj` | `Atelia.VersionStore`，生产库 | S4 |
| `tests/VersionStore.Tests/VersionStore.Tests.csproj` | `Atelia.VersionStore.Tests`，非 pack 测试 | S4 |

项目创建时使用仓库的 .NET 10 SDK、命名与测试依赖约定。新增生产包注册及 pack 顺序仍由 `eng/Pack.ps1` 唯一维护；源码阶段先明确候选包尚未交付的状态。S6 完成包入口后才能声明包消费可用。

本目录不固定后续实施的 Git 提交、包版本或公开发布；这些取决于实际实施会话的授权及验收结果。

## 每阶段的迭代方式

每阶段先补齐：外部入口、格式字段、状态转移、确定拒绝与未知结果、恢复步骤、资源归属、复杂度预算，以及可独立完成的实施片。
一次 Coding Agent 任务只处理其中一个有边界的工作包；一个阶段可以包含多轮设计、实现、评审和证据修复。

阶段需要新的下层能力时，在相应前序文档提出变更并重新验收，再继续下游；不在下游复制长度公式或增加隐含协议。导航索引可以链接全部阶段，规范输入保持单向。

目前最高优先级是细化 S1：精确尺寸 API 与提前 ticket 的生命周期。后续阶段已给出候选边界和具体阻断项，便于沿依赖链继续讨论。
