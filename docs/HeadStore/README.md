# HeadStore 文档入口

持久 Handle 与准确 Entry 起点历史已实施：HeadId/HeadEntryId提供4B/12B无owner codec，ReadEntryHistory提供准确inclusive起点；导入值在使用时执行正常RBF3局部资格检查。合同归[04](04-versionstore-publication.md)/[05](05-versionstore-names-and-indexes.md)/[11](11-versionstore-root-payload-bytes-api.md)，实际新增源码、跨进程与包资格见[12验收](12-versionstore-final-acceptance.md)。临时方案已归档进现有合同并删除，旧轮次资格不自动覆盖本增量。

HeadStore 借入一个 FrameStore，发布应用解释的不透明 root-payload bytes，并提供Head、历史、fork、HeadSetTag与只读Audit。公开用法见 [Atelia.HeadStore](../../src/HeadStore/README.md)。本页只做导航，不增加合同或重标验收范围。

新增 `ReadHeadOrigin(headId)` 只读指定 header 的固定来源声明，返回 `HeadOrigin.SourceEntryId`（独立根为 null）；不枚举其他 Head 或读取来源 Entry。合同归[S5 定向来源](05-versionstore-names-and-indexes.md#指定-head-的创建来源)，既有全集查询资格不自动覆盖此增量，运行验收另行记录。

紧凑Handle合同见[04](04-versionstore-publication.md)：HeadId为4B上下文编码，本地编号上限0x00FFFFFF；跨库误用检测允许碰撞，GetLocalHeadId替代HeadId.LocalId。内部字段仍为raw local，完整128bit库身份保留在低频门/header；旧轮次资格不代表格式4已验证。

当前采用引用初始化 fork：只在 header 保存准确来源，不生成克隆 Entry，ForkHead 返回新 HeadId；HeadSetTag 的16B成员独立保存 selected Head 与实际 Entry owner。统一格式4拒绝1/2/3；当前合同见04/05/07/09/11，实际新增验证见下表，不沿用旧Accepted。临时施工清单已完成并删除，合同与验收归入既有文档。

精确 EntryId 读取、公共组合 guard 与惰性物理 RecoveryReports 已实施，新增资格见[下游公面增量验收](../FrameStore-HeadStore/14-downstream-api-refinement-acceptance.md)。[下游公面完善计划](../FrameStore-HeadStore/14-downstream-api-refinement-plan.md)的获准施工队列已收束，只保留延后事项的重访条件，不重复加入旧阶段队列。

2026-10-16 已完成[HeadStore 行为保持重命名](15-headstore-renaming-plan.md)：活跃公面与工程统一采用 HeadStore/Head/Entry，验证状态以该篇为准；[HeadSetTag成员表替换](16-headset-tags-plan.md)已实施，统一格式2、删除旧bytes/Related公面；目标合同见09，新增资格见[16验收记录](16-headset-tags-acceptance.md)，Windows源码、进程与本地包消费已通过。既有VersionStore验收与包来源不重标。文档目录已改为 `docs/HeadStore/`，重命名记录与 HeadSetTag 专项分别调整为 15、16 号；其余 basename 和既有 Clause-ID 保持。

## 最小阅读路径

1. 先看[实施计划](12-versionstore-implementation-plan.md)的当前完成项，再看[既有最终源码验收](12-versionstore-final-acceptance.md)与 [CU 独立源码验收](07-versionstore-cross-ref-conditional-update-acceptance.md)的范围；原 source-only 队列已收束，`PublishHeads` / CU 已 Accepted。
2. 修改发布或公共 API 时读 [S4](04-versionstore-publication.md)与 [11 bytes 公面](11-versionstore-root-payload-bytes-api.md)；再按任务读下表专项，避免把实施记录当作合同。
3. 涉及 data-first、借入 owner 或底层边界时，按需补读 [S0](../FrameStore-HeadStore/00-architecture-decisions.md)、[FrameStore S2](../FrameStore/02-framestore-core.md) / [S3](../FrameStore/03-framestore-interleaved-builders-and-durability.md)及 [RBF 接口](../Rbf/rbf-interface.md)；通过验收条款映射定位源码与测试。

## 合同归属

| 任务 | 唯一负责该内容的规范入口 |
| --- | --- |
| 身份、Head envelope、创建/打开、借入 data、发布顺序与 `PublicationOutcome` | [S4：不透明根 payload 与 Head 发布](04-versionstore-publication.md) |
| 有界历史、精确 fork 与声明分叉图 | [S5：历史、fork 与施工导航](05-versionstore-names-and-indexes.md) |
| CU 记录、共同判据、位置复用与成员读取算法 | [07：ConditionalUpdate](07-versionstore-cross-ref-conditional-update.md) |
| Head/CU 的记录时间与固定 TailMeta | [08：记录元信息](08-versionstore-record-metadata.md) |
| HeadSetTag成员表、名称/桶、独立元信息、依赖确认与扫描 | [09：tag 专项](09-versionstore-immutable-tags.md) |
| 可选 NamedFrameMap 的 key、wire、容量与自有集合 | [10：NamedFrameMap payload](10-versionstore-rootmap-payload.md) |
| bytes API、输入捕获、长度与结果所有权，含 `PublishHeads` | [11：bytes 公面与所有权](11-versionstore-root-payload-bytes-api.md) |

NamedFrameMap 是应用显式选择的 codec，普通 Head、历史、fork、tag 与 CU 不通过 NamedFrameMap decoder 判定发布健康。底层 codec 需要时读 [Bare Primitive Value](../Binary/bare-primitive-value.md)；指定边界逆扫的增量见 [RBF 边界计划与实施](../Rbf/rbf-reverse-boundary-plan.md)。

## 当前状态与源码资格

[实施计划](12-versionstore-implementation-plan.md)维护分片完成项和续接：普通 Head 公开持久化闭环、历史/fork/声明图、tag/元信息、只读完整 Audit、V4-F 具名内部窗口及 CU 均已实施。各轮中的“当时未实现”保留历史语境，不是当前待办。

| 需要核对的资格 | 证据入口 |
| --- | --- |
| S4/S5 与 08–11 的既有独立源码资格，含 V4-E/V4-F | [最终源码验收](12-versionstore-final-acceptance.md)；该 Accepted 不含 CU |
| `PublishHeads` / CU 的独立源码、具名进程窗口与两平台增量 | [07 CU 验收](07-versionstore-cross-ref-conditional-update-acceptance.md) |
| HeadSetTag格式2首轮、正式成员引用、故障/取消及具名进程窗口（历史） | [16验收记录](16-headset-tags-acceptance.md)，Windows源码、进程与本地包消费通过 |
| 引用初始化fork/格式3与16B独立key/value映射（历史轮次） | [12本轮新增验收](12-versionstore-final-acceptance.md)、[07 CU](07-versionstore-cross-ref-conditional-update-acceptance.md)、[16 tag](16-headset-tags-acceptance.md)；仅按新增轮次实际结果判定 |
| 紧凑Handle/格式4、24bit本地编号与上下文解码 | [12增量验收](12-versionstore-final-acceptance.md)；不沿用此前源码或包资格 |
| 已实施分片、历史命令及组合验证 | [实施计划](12-versionstore-implementation-plan.md) |
| V4-C 的限定 Linux 平台与资源观察 | [Linux / 资源验收](12-versionstore-linux-resource-acceptance.md)，保持其当轮范围 |

## 包、组合与历史材料

- 组合与分层交付要求由 [S6](../FrameStore-HeadStore/06-integration-and-delivery.md)负责；[S6-C/D 本地交付记录](../FrameStore-HeadStore/13-local-package-delivery.md)只证明其中固定来源的旧六包候选及限定隔离消费。
- **旧六包候选不含 `PublishHeads` / CU。** CU 的源码 Accepted 不改变旧候选的版本、manifest、hash 或来源；新增包资格须另用新版本和对应来源取得，公开发布与真实消费者迁移另行验收。
- 本目录 [evidence](evidence/) 的 JSON 是不可变历史快照；原路径、revision、hash 与运行结果按当轮解释，不能把文档迁移或当前实现写回旧证据。
- [Prepare + Commit 历史候选](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)仅供比较与追溯，现行跨 Head 合同为 07 CU；历史研究探针不替代生产源码、进程、平台或包消费证据。
- 跨模块评审和未决讨论见[共享目录](../FrameStore-HeadStore/README.md)，按任务查阅，不形成第二份模块规范。
