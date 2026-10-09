# FrameStore / VersionStore 分阶段设计入口

日期：2026-10-03；2026-10-05 确认 FrameStore header、归还、config 与核心/扩展分离；2026-10-07 更新完整字典、单文件 ref、已完成输出资格及命名 fork 的目录共同发布；2026-10-08 确认 FrameAddress 固定 12B 编码；2026-10-09 分离批量规划器设想、收敛 MVP 组合，增加 ForkOrigin 完整历史与全分叉设计，并定稿资源、header 及 owned 租借/归档维护。状态：**S1 Accepted；S2–S6 Draft，两个新项目尚未创建；参考依赖拆分 Accepted**。
初始设计源码观察基线：`main @ 70d1009e78a73342a0c0fdc8ffed7731dec58173`；S1 验收记录的核对基线为 `f6f1eb38557863ba5ea1634844a90f0cbe5774cf`；前轮设计阅读基线为 `4175a46`，本轮租借/目录修订核对 `50e8e28`。本文档集供逐阶段细化、审阅和实施，不把文档修订视为新实现或验收。

目标：**以 RBF3 的帧原子性为基础，让中层构建新状态，再发布完整根地址字典使状态生效。**
MVP 设计组合由 RBF3、FrameStore 核心与 VersionStore 的发布、历史和命名能力组成；格式、API、实施顺序与验收以 S0–S6 为准。
恢复依据是完整事实帧及其有效发布记录。RBF 处理物理尾部；FrameStore 分配不可变帧并按地址读取；中层负责状态构建和业务依赖闭包；VersionStore 管理字典快照、ref 历史与 branch/tag。

基础编码候选见 [Bare Primitive Value 草案](../Binary/bare-primitive-value.md)：独立 BCL-only `Atelia.Binary`、FixedLE/VarInt、自适应 string、宽容 reader 与精确 Measure。它可供后续 header/RootMap 复用，但尚未审定/实施，不替代各阶段的记录 schema、版本、限额与资格；既定 FrameAddress 12B 保持。[Tagged Value](../Binary/tagged-value-intent.md) 仅记录意向，不是本栈前置。

main 当前演进主线为 RBF3 / FrameStore / VersionStore。旧 EventJournal/RbfSegmentStore、toolkit 及测试保留为冻结参考，底层精确 PackageReference `[0.2.0-rbf1-preview.1]`；维护和公开交付归 RBF1 分支。依赖隔离、solution 共存与本轮仅三包交付的实施及证据见[过渡方案](../rbf1-reference-transition.md)。本轮不删除旧目录、不创建新项目，也不改变 main 的 RBF1 只读兼容。

## 当前最小模型

FrameStore 普通合同是不透明地址分配与随机读取，不提供业务全局顺序。保留 RBF 三种追加方式；每个 Builder 独占一个文件，owner 可以嵌套租借多个文件、交错构建并乱序完成，首版调用仍串行。三种追加统一在成功完成后按 TailOffset 大于软阈值触发轮转。
文件分配优先选择 active 中当前可分配的数值最低 FileId；忙文件和已停止分配文件跳过，没有候选才新建。不因已知帧尺寸提前试配，也不引入轮询或负载均衡。
FrameAddress 首版固定编码为 12B，保持完整 uint FileId 与 SizedPtr；额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。格式权威为 S2 `[F-FS-FRAME-ADDRESS-12B]`，三种追加、互引和 VersionStore RootMap 复用同一 codec。地址仍是局部定位值，StoreId 由上下文绑定；CLR 内存布局另行验证，不从 sizeof 推导 wire 容量。
私有 creating 槽位完成必需首帧 meta/header 后发布到 active。S2 已定稿固定 24B header，保存统一格式版本与 StoreId/FileId；按首位置识别，完整保留用户 uint tag 范围。可写打开先检查初始化长度下界，再消费公共 RBF 恢复并完整校验首帧。active 目录表达可写集合，flush/close 后移入固定 1024 编号分桶的 archive 并只读，不维护 active manifest 或全历史分段表。屏障确认全部必要 active 输出。
2026-10-07 用户确认：**ConfirmDurable 确认调用时已经完成的全部必要输出；未完成 Builder 保持原状，不因此次确认而获得完成或耐久资格。** leased 文件中的旧完成输出也必须覆盖，Builder 后续完成重新登记为未确认；flush 失败停用整个 owner 及其他 Builder/Writer。
**Builder 活跃期间，允许随机读取已经完成的文件前缀；正在构建的新帧仍不可读。** RBF 指定 ticket 随机入口的 Building 范围检查包含帧后 Fence；普通读取仍校验内容、生命周期和共享 fault，不扩大扫描相关入口或并发合同。正式核心要求见 S2。
成功 EndAppend 自动归还文件和数量配额，不等后续 Dispose。S2 定稿可选 framestore.config.json、默认 MaxOutstandingBuilders=32、严格校验、可写打开一次读取且实例固定；超限 Begin 立即拒绝。完整 Append 不占 Builder 配额，单 driver 最多另占一个短期租借，不引入精确总内存账本。首版保留可写 active 句柄并显式使用 RbfCacheMode.Off，archive 随机读按操作开关；成本按实际 active 和构建/读结果占用计算，降低配置不抹掉旧高峰。
S2 已定 ReadFrame 返回独立拥有 buffer 的 FrameRead，关闭 reader/owner 后仍可使用至结果 Dispose。Inventory/Audit 使用同步 visitor：分别提供真实主链结构发现和全部实际帧完整 CRC 检查，回调期间拒绝同 owner mutation、允许随机读取；不公开扫描器或延迟 Reader-bound 结果，不把前缀回调、结构扫描或物理 CRC 健康当作业务闭包健康。
S2 已定稿一次性共享 Lease 与 owned Builder/Writer：成功完成只登记并归还；下一合法 Append/Begin、ConfirmDurable 或可写 Open 统一归档停止文件。已完成帧不因维护失败撤销，可纠正拒绝保留租借，无法辨相位的委派异常保守停用 owner；Dispose 只尝试清理全部资源。目录/编号/锁及两平台 rename 资格继续待定；上述读写与物理检查规则尚未实施。
循环引用通过多个已知尺寸 Builder 的提前地址形成。FrameStore core 与交错构建/耐久确认可以独立定稿、实施和验收。
2026-10-07 VersionStore 收缩为完整 `RootMap = string => FrameAddress` 快照：每个 ref 一个 RBF3 文件，每次更新追加完整字典；tag 保存命名不可变字典，按固定名称哈希分桶；branch 为 name → 稳定 RefId 的 create-only 绑定。首版不分段/轮转 ref，不引入差分、checkpoint 或独立 Commit/Parent。
VersionStore 借入一个 data FrameStore，拥有自己的 RBF3 发布目录。CreateRef / PublishRef / CreateTag 及命名 fork 先完成本根所需的新增依赖，再同步 data ConfirmDurable，随后确认字典输出及其必要发现边界并安装内存投影；无关 Builder 未归还不阻断独立闭包的发布。应用收到成功确认后按字典加载状态。CreateBranch 只给既有 ref 加 alias、确认自己的绑定文件，不重新发布 RootMap 或调用 data 屏障。所有 Key 的业务语义由应用解释，不消费中间帧的构建顺序。
2026-10-07 用户确认：**命名 fork 成功发布时，新 ref 与 branch 一起可见；发布前，普通查询两者都不可见。失败可以留下私有准备文件，但不会留下公开的未绑定 ref。** S4 统一 `refs/<RefId>/<RefId>.rbf` 与 `names` 容器，在 creating 内准备完成后一次目录 rename 发布；S5 的组合入口在同一容器加入初始 binding，所有后加 alias 也采用相同 names 文件表示。普通 CreateRef 仍可创建未命名 ref，手工 CreateRef+CreateBranch 仍不是事务。
branch 名称解析和查重覆盖全部正式 ref 的 names；首查可扫描或建立可重建内存表，不承诺 O(1)，不写持久名称索引。全局名称准入依赖单 writer/driver 不交错操作；S4 Ref 读写不解释名称 codec，不增加永久 branch gate。正式目录/文件 rename 的平台资格仍待实施，Unknown 与私有残留不因共同发布而消失。
当前值校验本地 header/初始快照并完整读取最后快照，不递归祖先；历史从固定完成上界逆序枚举，沿首帧 `ForkOrigin = 源 RefId + 源 SizedPtr` 接续前序发布前缀，返回结束枚举后仍可使用的自有字典。ForkRef / CreateBranchFromRevision 内部确认源成员、复制源字典，仍保存子初始完整快照；只传 roots 的创建是无来源起点。rewind 追加旧字典成为新 revision，tag 冻结所选字典。RefRevision 来自完整发布/实际历史枚举，不是输出前尝试 token；跨重开书签仍可用 tag。
ListForks 扫描全部正式 ref 的 checked 首帧声明，包括匿名 ref，派生全部创建分叉图；不写持久孩子表，不据此宣称全部源历史健康。实际跳转再校验源快照与子初始字典一致。现有 RBF 逆扫只从 EOF 开始，定位旧 fork 点可能扫描源后续帧；S5 明确工作预算与成本，不假设随机起扫能力。
输出异常停止实例并重开读取实际状态；完整快照损坏报错，不回退旧值。同步 mutation 的 Result/必选 out PublicationOutcome、公开尝试与确认边界已在 S4 定稿，异常路径不依赖新建证据包装；NotAttempted/Confirmed 均不代替实例健康。匿名创建丢失返回值后不按同值精确认领，首版不提供通用 CAS 或精确 Unknown 尝试查询。数据闭包、工具 operationId、外部 uncertain 策略、模拟器 RNG 和应用谱系由应用负责；[两类下游评估](reviews/2026-10-07-downstream-fit.md)未发现必须扩大核心的需求。本轮仍只修订设计，未实施发布与续跑协议。

2026-10-03 的初始裁决及失败轨迹见[历史设计审阅](reviews/2026-10-03-dialectical-review.md)；旧单流、单 active/locator、纯 batch planner，以及后来 Commit/control 草案均已被后续会话修订，不作为当前模型的实现证据。S1 已 Accepted；已确认方向见 S0，S2–S6 的剩余工程选择仍是 Draft。

## 顺序与职责

| 顺序 | 权威合同 | 对外用途 |
| --- | --- | --- |
| 单个 RBF 文件的物理追加顺序 | RBF；S2 后端调度 | 原子帧恢复，不解释数据谱系 |
| 同一 ref 的快照追加顺序 | S4 单文件；S5 checked 历史枚举 | 选择当前值或旧快照；不建立跨 ref 全序 |
| ref 创建来源与继承前缀 | S4 ForkOrigin；S5 跨文件历史及 ListForks | 固定分叉点、遍历发布历史、查询全部创建分叉；不按同值去重 |
| 应用因果/实验谱系 | 应用数据 codec | 显式引用轨迹/来源，不由 ref 历史或 data 地址推算 |
| branch/tag 的名称唯一性 | S5 名称政策与持久记录 | 定位对象/不可变字典，不要求不同名称具有全序 |

下一轮重点讨论见[设计难题汇总](design-questions.md)。该文是问题索引与推导说明，不是额外阶段或前序层的规范输入；正式合同仍在相应 S0–S6。

## 阅读与状态规则

- 先读仓库根 [README](../../README.md)，再读 [S0 总体决策](00-architecture-decisions.md)。
- 本目录遵循 [规范约定](../spec-conventions.md)：`decision` 记录会话已确认方向；S1 的 `spec` 与签名已成为实施合同，S2–S6 仍是候选要求；未审定阶段的建议、算例、API 名称不自动冻结。
- 本组设计正文按用户要求只保留当前有效条款，被替代的草案条款直接移除，作为上述约定中保留废弃条款要求的局部例外。现行 Clause-ID 不重命名或复用；历史迁移保留在 `reviews/` 与 Git 历史，审阅记录的当时结论及检查数字不代表当前状态。
- 阶段状态使用 `Draft → Ready → Implementing → Accepted`。Ready 前定稿字段/API/算法及验收映射；可以对范围明确的必要子合同单独审定，未支持能力不得借整体标签宣称成立。
- S1 的实施合同提交为 `c940ed6`，实现提交为 `8ab98bf`；RBF 818/818、Data 288/288 和 W: public 源码消费已验收，实际工作树和二进制身份见[阶段验收记录](01-rbf-sized-append-acceptance.md)。本片不宣称新增包消费、性能或进程实杀通过，也不宣称下游已接入。
- 后续参考依赖拆分轮次已取得 RBF3 三包候选的 W: 隔离 PackageReference 消费资格；候选版本、实现提交和 76 个源码 checksum 见[过渡验收记录](../rbf1-reference-transition.md#6-验收记录)。它不重标 S1 的历史工件，也不代表公开发布或 FrameStore/VersionStore 已交付。
- 旧库维护归 RBF1 分支，main 仅保留冻结参考，不要求它们适配新栈。[原调查](../Rbf/rbf3-adaptation-baseline-investigation.md)基于 `e6ad4d2`，记录旧源码跟随新底座时的断点，不冒充本轮冻结依赖后的测试结果。

## 阶段顺序

下图的箭头表示合同依赖；下游使用的必要子合同必须先形成、实施并取得对应资格。它不要求整层横向完工后才能试验下一层；各阶段规范只依赖前序阶段。

```mermaid
flowchart LR
    S0["S0 总体边界"] --> S1["S1 RBF 已知尺寸追加"]
    S1 --> S2["S2 FrameStore 核心"]
    S2 --> S3["S3 交错构建 / 同步屏障"]
    S3 --> S4["S4 完整字典 / 单文件 ref 发布"]
    S4 --> S5["S5 历史选点 / branch / tag"]
    S5 --> S6["S6 集成与交付"]
```

| 阶段文档 | 本阶段形成的合同 | 主要实施范围 | 出口 |
| --- | --- | --- | --- |
| [S0 总体边界与决策](00-architecture-decisions.md) | 项目边界、事实归属、恢复模型、兼容政策 | 文档决策 | 后续阶段无需反向依赖消费者语义 |
| [S1 RBF 已知尺寸追加（Accepted）](01-rbf-sized-append.md) | 正向/预算尺寸试算、分立长度 Begin + out ticket、格式信息、Builder 生命周期 | `src/Rbf`、`tests/Rbf.Tests`、RBF public 源码 smoke | [单文件独立验收](01-rbf-sized-append-acceptance.md)已闭合 |
| [S2 FrameStore 核心](02-framestore-core.md) | 三种追加、不透明地址与固定 12B codec、独占租借、软轮转、目录生命周期与基础耐久确认 | 新建 FrameStore 与其测试项目 | 分配器可独立使用，多个 active 恢复及重开 |
| [S3 交错构建与耐久确认](03-framestore-interleaved-builders-and-durability.md) | 提前地址、跨文件互引、交错完成，消费 S2 核心屏障的组合资格 | FrameStore 与其测试项目 | 消费者无需管理文件 flush，预算内互引与完成可独立验收 |
| [S4 VersionStore 字典与发布](04-versionstore-publication.md) | RootMap codec、单文件 ref、统一容器发布、数据屏障、恢复与结果证据 | 新建 VersionStore 与其测试项目 | 当前值无历史 replay；目录发布与冷重开可独立验收 |
| [S5 历史、branch 与 tag](05-versionstore-names-and-indexes.md) | owned 跨文件历史、全分叉发现、全局名称发现、alias 与来源感知原子命名 fork、分桶 tag | VersionStore 与其测试项目 | fork/rewind/tag、链接与历史生命周期可独立验收 |
| [S6 集成与交付](06-integration-and-delivery.md) | 第二种状态模型、公共包消费、交付边界 | examples、eng、CI；消费者接入另有明确范围 | 源码、包及消费者证据分别齐备 |

S4 当前值只读所访问 ref 的末快照，S5 回溯才枚举历史。branch 冷发现/全局查重扫描正式 ref 容器及名称记录；tag 首版可扫描目标桶，不据此宣称全库 Open 或名称查找固定 O(1)。分段、随机历史读、持久索引与差分只在真实规模需要时另立实施片。

[07：ConditionalUpdate 跨 ref 事务](07-versionstore-cross-ref-conditional-update.md)是单独的专题设计稿，作为多 ref 联合提交的优先发展方向。它细化完整成员表、精确读与实际槽位消歧、fresh/self 和整批 Outcome；协议审阅与 public RBF3 研究支持正式选型建议，但尚未并入 S4/S5 的实施合同，VersionStore 尚未实施。

| 候选扩展 | 内容 | 接纳与实施 |
| --- | --- | --- |
| [FrameBatch 批量规划器](extensions/framestore-batch-planner-candidate.md) | 先规划大量地址、再以少量 Builder 逐帧填充的资源优化及 API 草图 | 设想阶段；需求未验证、API 未冻结，不属于 MVP 实施或 Ready 条件 |

[Prepare + Commit 历史比较方案](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)保留作 07 的比较基线，不列入当前候选扩展。

S2–S6 的 MVP 定稿、实施和验收不等待上述候选扩展的需求确认或 API 定稿；扩展如接纳，另行形成合同与资格。

2026-10-07 的整组一致性复核与修正见[复核记录](reviews/2026-10-07-consistency-review.md)。该复核轮次只修订合同边界、权威归属与验收向量，S2–S6 仍为 Draft，不构成实现或平台资格。
其后用户接受解除两项过宽 guard；[活跃构建期间已完成输出的改进记录](reviews/2026-10-07-completed-output-improvements.md)区分本轮 RBF 底座代码验收与 FrameStore/VersionStore 合同修订，不把底座通过视为新项目已实现。

## 最小纵向实施顺序

| 片 | 必要前序合同与证明 | 范围限制 |
| --- | --- | --- |
| V0 | S1 尺寸/格式/early ticket，双 RBF 文件互引读回 | 单文件能力，不声称新库已实现 |
| V1 | S2 owned 三种追加/嵌套租借与核心 barrier + S4 CreateRef/PublishRef/ReadRef；冷重开 | 单文件 ref，完整 RootMap；S3 验证交错构建组合，不引入分段或全局日志 |
| V2 | S3 双文件 A↔B、self-reference 与乱序完成后同时发布多个根，取消/reuse/Unknown | 本根所需依赖完成并确认，无关 Builder 可继续构建，旧/新字典不能混搭 |
| V3 | S2 creating/active/archive、软轮转/编号恢复 + S3 多 active barrier 组合验收与进程中断 | 完成后才声明完整多文件生命周期和恢复资格 |
| V4 | S5 历史选点及 ForkOrigin 接续、匿名/原子命名 fork、ListForks、alias/名称发现、rewind/tag | 复用 S4 目录发布；分段/轮转、差分、随机 revision 读与名称修改后置 |
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
