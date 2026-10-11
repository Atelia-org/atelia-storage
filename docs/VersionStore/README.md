# VersionStore 文档入口

VersionStore 借入一个 FrameStore，发布应用解释的不透明 root-payload bytes，并提供 ref、历史、fork、tag 与只读 Audit。公开用法见 [Atelia.VersionStore](../../src/VersionStore/README.md)。本页只做导航，不增加合同或重标验收范围。

当前新增公面施工队列见[下游公面完善计划](../FrameStore-VersionStore/14-downstream-api-refinement-plan.md)：精确 revision 读取、公共组合 guard 与惰性恢复报告已完成方案审查，尚未实现。跨层待办只在该篇维护，不重复加入旧阶段队列。

## 最小阅读路径

1. 先看[实施计划](12-versionstore-implementation-plan.md)的当前完成项，再看[既有最终源码验收](12-versionstore-final-acceptance.md)与 [CU 独立源码验收](07-versionstore-cross-ref-conditional-update-acceptance.md)的范围；原 source-only 队列已收束，`PublishRefs` / CU 已 Accepted。
2. 修改发布或公共 API 时读 [S4](04-versionstore-publication.md)与 [11 bytes 公面](11-versionstore-root-payload-bytes-api.md)；再按任务读下表专项，避免把实施记录当作合同。
3. 涉及 data-first、借入 owner 或底层边界时，按需补读 [S0](../FrameStore-VersionStore/00-architecture-decisions.md)、[FrameStore S2](../FrameStore/02-framestore-core.md) / [S3](../FrameStore/03-framestore-interleaved-builders-and-durability.md)及 [RBF 接口](../Rbf/rbf-interface.md)；通过验收条款映射定位源码与测试。

## 合同归属

| 任务 | 唯一负责该内容的规范入口 |
| --- | --- |
| 身份、ref envelope、创建/打开、借入 data、发布顺序与 `PublicationOutcome` | [S4：不透明根 payload 与 ref 发布](04-versionstore-publication.md) |
| 有界历史、精确 fork 与声明分叉图 | [S5：历史、fork 与施工导航](05-versionstore-names-and-indexes.md) |
| CU 记录、共同判据、位置复用与成员读取算法 | [07：ConditionalUpdate](07-versionstore-cross-ref-conditional-update.md) |
| ref/CU 的记录时间与固定 TailMeta | [08：记录元信息](08-versionstore-record-metadata.md) |
| 不可变 tag、名称/桶、独立元信息、关联注记与扫描 | [09：tag 专项](09-versionstore-immutable-tags.md) |
| 可选 RootMap 的 key、wire、容量与自有集合 | [10：RootMap payload](10-versionstore-rootmap-payload.md) |
| bytes API、输入捕获、长度与结果所有权，含 `PublishRefs` | [11：bytes 公面与所有权](11-versionstore-root-payload-bytes-api.md) |

RootMap 是应用显式选择的 codec，普通 ref、历史、fork、tag 与 CU 不通过 RootMap decoder 判定发布健康。底层 codec 需要时读 [Bare Primitive Value](../Binary/bare-primitive-value.md)；指定边界逆扫的增量见 [RBF 边界计划与实施](../Rbf/rbf-reverse-boundary-plan.md)。

## 当前状态与源码资格

[实施计划](12-versionstore-implementation-plan.md)维护分片完成项和续接：普通 ref 公开持久化闭环、历史/fork/声明图、tag/元信息、只读完整 Audit、V4-F 具名内部窗口及 CU 均已实施。各轮中的“当时未实现”保留历史语境，不是当前待办。

| 需要核对的资格 | 证据入口 |
| --- | --- |
| S4/S5 与 08–11 的既有独立源码资格，含 V4-E/V4-F | [最终源码验收](12-versionstore-final-acceptance.md)；该 Accepted 不含 CU |
| `PublishRefs` / CU 的独立源码、具名进程窗口与两平台增量 | [07 CU 验收](07-versionstore-cross-ref-conditional-update-acceptance.md) |
| 已实施分片、历史命令及组合验证 | [实施计划](12-versionstore-implementation-plan.md) |
| V4-C 的限定 Linux 平台与资源观察 | [Linux / 资源验收](12-versionstore-linux-resource-acceptance.md)，保持其当轮范围 |

## 包、组合与历史材料

- 组合与分层交付要求由 [S6](../FrameStore-VersionStore/06-integration-and-delivery.md)负责；[S6-C/D 本地交付记录](../FrameStore-VersionStore/13-local-package-delivery.md)只证明其中固定来源的旧六包候选及限定隔离消费。
- **旧六包候选不含 `PublishRefs` / CU。** CU 的源码 Accepted 不改变旧候选的版本、manifest、hash 或来源；新增包资格须另用新版本和对应来源取得，公开发布与真实消费者迁移另行验收。
- 本目录 [evidence](evidence/) 的 JSON 是不可变历史快照；原路径、revision、hash 与运行结果按当轮解释，不能把文档迁移或当前实现写回旧证据。
- [Prepare + Commit 历史候选](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)仅供比较与追溯，现行跨 ref 合同为 07 CU；历史研究探针不替代生产源码、进程、平台或包消费证据。
- 跨模块评审和未决讨论见[共享目录](../FrameStore-VersionStore/README.md)，按任务查阅，不形成第二份模块规范。
