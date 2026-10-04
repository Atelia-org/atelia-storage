# FrameStore / VersionStore 分阶段设计入口

日期：2026-10-03；2026-10-04 更新 S1 与旧栈参考边界。状态：**S1 Accepted；S2–S6 Draft，两个新项目尚未创建；参考依赖拆分 Implementing，待验收**。
初始设计源码观察基线：`main @ 70d1009e78a73342a0c0fdc8ffed7731dec58173`；S1 本轮核对基线为 `f6f1eb38557863ba5ea1634844a90f0cbe5774cf`。本文档集供逐阶段细化、审阅和实施。

目标：**以 RBF3 的帧原子性为基础，让中层构建新状态，再通过统一的根发布使状态生效。**
恢复依据是完整事实帧及其有效发布记录。RBF 处理物理尾部；中层负责状态构建和依赖闭包；VersionStore 负责发布及 ref/branch/tag。

main 当前演进主线为 RBF3 / FrameStore / VersionStore。旧 EventJournal/RbfSegmentStore、toolkit 及测试保留为冻结参考，底层精确 PackageReference `[0.2.0-rbf1-preview.1]`；维护和公开交付归 RBF1 分支。依赖隔离、solution 共存与本轮仅三包交付的实施及证据见[过渡方案](../rbf1-reference-transition.md)。本轮不删除旧目录、不创建新项目，也不改变 main 的 RBF1 只读兼容。

## 当前最小模型

FrameStore 每实例一个追加流，提供精确地址、跨段顺序 plan 和同步 ConfirmDurable。VersionStore 借入 data FrameStore，拥有私有 control FrameStore；不建通用多流平台，不向调用方签发耐久 receipt。
Prepare 在业务输出前给调用方精确记录 token；Commit 确认最新 data 完成前缀，再追加/确认单条控制记录、安装内部投影。应用收到 Confirmed 后再安装自己的状态。
RefId/revision/attempt 共用一个 RecordToken 表示；普通 head/tag 查询给事实地址，实际内容由 FrameStore.ReadFrame 一次完整读取。首次 Open/Inspect 允许显式完整控制回放；snapshot 的规模资格单独取得。
初始裁决、具体失败轨迹、保留的不同意见和延迟触发条件见 [2026-10-03 设计审阅](reviews/2026-10-03-dialectical-review.md)。S1 的后续分立长度选择已获用户确认并写入 [S0](00-architecture-decisions.md)；其余阶段的技术默认仍是 Draft。

## 阅读与状态规则

- 先读仓库根 [README](../../README.md)，再读 [S0 总体决策](00-architecture-decisions.md)。
- 本目录遵循 [规范约定](../spec-conventions.md)：`decision` 记录会话已确认方向；S1 的 `spec` 与签名已成为实施合同，S2–S6 仍是候选要求；未审定阶段的建议、算例、API 名称不自动冻结。
- 阶段状态使用 `Draft → Ready → Implementing → Accepted`。Ready 前定稿字段/API/算法及验收映射；可以对范围明确的必要子合同单独审定，未支持能力不得借整体标签宣称成立。
- S1 的实施合同提交为 `c940ed6`，实现提交为 `8ab98bf`；RBF 818/818、Data 288/288 和 W: public 源码消费已验收，实际工作树和二进制身份见[阶段验收记录](01-rbf-sized-append-acceptance.md)。本片不宣称新增包消费、性能或进程实杀通过，也不宣称下游已接入。
- 旧库维护归 RBF1 分支，main 仅保留冻结参考，不要求它们适配新栈。[原调查](../Rbf/rbf3-adaptation-baseline-investigation.md)基于 `e6ad4d2`，记录旧源码跟随新底座时的断点，不冒充本轮冻结依赖后的测试结果。

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
| [S1 RBF 已知尺寸追加（Accepted）](01-rbf-sized-append.md) | 正向/预算尺寸试算、分立长度 Begin + out ticket、格式信息、Builder 生命周期 | `src/Rbf`、`tests/Rbf.Tests`、RBF public 源码 smoke | [单文件独立验收](01-rbf-sized-append-acceptance.md)已闭合 |
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

项目创建时使用仓库的 .NET 10 SDK、命名与测试依赖约定。新增生产包注册及 pack 顺序仍由 `eng/Pack.ps1` 唯一维护；当前主线仅注册 Primitives/Data/Rbf，旧参考库 IsPackable=false。后续新项目不得借旧库包名交付，源码阶段先明确候选包尚未交付的状态。S6 完成对应包入口后才能声明新库包消费可用。

本目录不固定后续实施的 Git 提交、包版本或公开发布；这些取决于实际实施会话的授权及验收结果。

## 每阶段的迭代方式

每阶段先补齐：外部入口、格式字段、状态转移、确定拒绝与未知结果、恢复步骤、资源归属、复杂度预算，以及可独立完成的实施片。
一次 Coding Agent 任务只处理其中一个有边界的工作包；一个阶段可以包含多轮设计、实现、评审和证据修复。

阶段需要新的下层能力时，在相应前序文档提出变更并重新验收，再继续下游；不在下游复制长度公式或增加隐含协议。导航索引可以链接全部阶段，规范输入保持单向。

S1-A/B/C 已实施并独立验收：公共尺寸计算、分立长度的提前 ticket 入口，以及双文件互引和生命周期证据已闭合。后续阶段继续沿依赖链关闭各自阻断项，不把本片源码资格扩展为新库、包或下游资格。
