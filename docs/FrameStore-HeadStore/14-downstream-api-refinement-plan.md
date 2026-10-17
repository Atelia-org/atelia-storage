# 下游反馈驱动的 FrameStore / HeadStore 公面完善计划

命名更新：2026-10-16 活跃合同采用 HeadStore/Head/Entry；既有验收与历史包仍保持 VersionStore 来源身份，本轮验证见[重命名记录](../HeadStore/15-headstore-renaming-plan.md)。

状态：**获准的两片公面增量已全部实施，当前没有剩余施工项。** 更新：2026-10-11。独立源码复核及实际运行资格统一见[增量验收](14-downstream-api-refinement-acceptance.md)。原方案经两轮 dialectical-simplification 收敛，设计基线为 `e989ba6`，第一片实施起点为 `89d4acd`，恢复报告片实施起点为 `ac97ad3`。

本文只保留续接入口和延后事项的重访条件，不再保存已完成的候选 API、施工队列或第二份合同。既有源码验收、CU 验收和旧六包候选保持各自来源；这些新公面尚未重新打包或迁移 DG。

## 1. 已落地合同的唯一入口

| 下游需求 | 权威与实际实施 |
| --- | --- |
| 精确 EntryId 读取 | [S4](../HeadStore/04-versionstore-publication.md) `[A-VS-READ-REVISION]`、[11 bytes 公面](../HeadStore/11-versionstore-root-payload-bytes-api.md)；ReadEntry 返回自有 root bytes，fork 共用精确内核并删除第二份复制 |
| 用户 Length 与读取切片 | [FS S2](../FrameStore/02-framestore-core.md)；Length 从0累计用户 bytes，FrameRead.Payload/TailMeta 零复制且继承 Dispose guard |
| 窄公共可用性检查 | S4 `[S-VS-DATA-BORROW]`；公共 EnsureUsable 组合检查 HS/data，内部纯查询继续 owner-only |
| 惰性物理恢复报告 | S4 `[A-VS-RECOVERY-REPORTS]`，tag 覆盖由[09](../HeadStore/09-versionstore-immutable-tags.md)引用；所有正式可写打开共用非 None 成功动作台账，保存的只读追加视图跨 fault/Dispose 可读 |

公开用法见 [FS](../../src/FrameStore/README.md)、[HS](../../src/HeadStore/README.md)。恢复算法、ProcessCrashOnly、CU 共同资格、历史预算、发布证据和单次清理/锁最后不因新增报告改变。报告缺失不证明失败工厂未修改 bytes；完整失败取证不属于已交付保证。

原审查选择私有薄 view，避免 BCL ReadOnlyCollection 经 ICollection.SyncRoot 暴露可写 backing；需要冻结由调用方复制。容量准备先于可写工厂，成功返回立即无分配入账，早于上层验证；不建立 reservation/commit 或事件框架。源码与两平台增量证据见验收，不把原 BCL 探针或离线损坏镜像当作崩溃/断电实证。

## 2. 延后事项与重访条件

| 事项 | 当前裁决及触发条件 |
| --- | --- |
| 无新增历史的确认 | defer：先明确 DG 要确认全库、当前 Head、exact EntryId、tag 或继承历史，以及启动/Unknown 后/每次保存的时机，再设计最窄入口。仅确认已缓存 Head 不是全库；若承诺确认完整 CU 发布，须确认全部成员。现有 fork 只确认 exact source，符合 S5 的 ProcessCrashOnly 合同，不借反馈扩大为断电保证 |
| 有界 Head 缓存 | defer：当前 16/64/256 资源基线证明增长来自缓存、重复稳定且 Dispose 释放；长期 owner 的上千/上万 Head 或明确句柄预算出现时再实施。CU 批次和内部借用须 pin，缓存容量不能直接承诺总 handles 硬上限；不先指定 LRU 或公开 lease |
| EntryId 导入/导出 | defer：用户或 DG 明确跨进程匿名选点需求后，另立完整上下文和真实成员资格合同；ReadEntry 不扩大到裸位置 |
| writable 禁止恢复 | defer：消费者明确需要“继续写但遇残尾拒绝”才设计；现有严格 RO/Audit 足以承担当前只读取证入口 |
| CAS、命名目录、业务 Parent | 保持下游职责；本轮反馈没有要求上移，HS 发布历史不替代 DG 业务链 |

这些不是后续自动施工项，不重新写入已清空的 design-questions.md。DG 生产入口仍基于旧栈，反馈不是新栈迁移验收；等待真实需求/试用证据后再选片。

## 3. 后续交付

本计划已收束。只有实际准备新本地包时，才以新版本、干净已提交来源运行 pack 和四个隔离 PackageReference smoke；旧候选不含 CU 及本计划增量，不覆盖既有版本/manifest/hash。本轮不自动打包、发布、迁移 DG 或重跑全部进程窗口/规模矩阵。
