# FrameStore 文档入口

FrameStore 是独立的多文件不可变 Frame 分配器，负责地址、文件生命周期、随机读取与同步耐久确认。公开用法见 [Atelia.FrameStore](../../src/FrameStore/README.md)。本页只做导航，不增加合同；实现 FrameStore 无须先通读 VersionStore 文档。

新增用户 Length 与 Payload/TailMeta 切片的待实施方案见[下游公面完善计划](../FrameStore-VersionStore/14-downstream-api-refinement-plan.md)。该增量已完成方案审查，尚未实现，不沿用下方既有验收资格。

## 最小阅读路径

1. 先看[收尾计划](02-framestore-completion-plan.md)的完成边界与[最终源码验收](02-framestore-final-acceptance.md)的范围：R1–R3 已完成，S2/S3 独立源码资格已 Accepted。
2. 修改核心行为时读 [S2 核心合同](02-framestore-core.md)，按任务定位条款；涉及交错 Builder、循环引用或耐久消费时再读 [S3](03-framestore-interleaved-builders-and-durability.md)。
3. 下沉到 RBF 或地址编码时，再读下表对应底层合同；通过最终验收的条款映射定位源码与测试，无须按时间顺序重读所有实施记录。

## 合同归属

| 任务 | 规范入口与归属 |
| --- | --- |
| owner、地址、追加、文件生命周期、恢复、确认、Inventory/Audit | [S2：核心、地址与文件生命周期](02-framestore-core.md) |
| 提前地址、交错构建、循环引用与同步屏障消费 | [S3：交错 Builder 与耐久](03-framestore-interleaved-builders-and-durability.md)；消费 S2 屏障，不另定义一套确认协议 |
| RBF 尺寸计算、已知尺寸 Builder 与提前 ticket | [S1：公共尺寸与提前 ticket](../Rbf/01-rbf-sized-append.md)；RBF 身份、writer/recovery/Dispose 仍以 [RBF 接口](../Rbf/rbf-interface.md)及[格式](../Rbf/rbf-format.md)为准 |
| FrameAddress 变长 codec 的基元编码 | 地址合同在 S2；底层编码见 [Bare Primitive Value](../Binary/bare-primitive-value.md) |

共同边界由 [S0：总体决策](../FrameStore-VersionStore/00-architecture-decisions.md)记录；VersionStore 如何发布根不属于 FrameStore 合同。

## 当前状态与验收

[收尾计划](02-framestore-completion-plan.md)是已完成队列的调度与追溯入口，不重新开启 R1–R4。既有 source-only 记录保持当轮身份；后续包资格单独记录。

| 需要核对的资格 | 证据入口 |
| --- | --- |
| S2/S3 最终独立源码出口及条款映射 | [R3 最终源码验收](02-framestore-final-acceptance.md) |
| public 跨进程锁、强杀与冷重开 | [R1 过程验收](02-framestore-process-acceptance.md) |
| 独立 public 消费、资源与规模观察 | [R2 消费与资源验收](02-framestore-consumer-resource-acceptance.md) |
| 收尾后新增的 FrameAddress 变长 codec | [变长编码实施验证](02-framestore-varint-address-implementation.md)，不沿用旧 R3 的 hash 或平台结果 |
| 为上层借入新增的 `EnsureUsable()` 消费验证 | [VersionStore 实施计划 V0](../VersionStore/12-versionstore-implementation-plan.md#v0已完成骨架和关键局部2026-10-10)，合同仍在 S2 |

较早的实施切片按需查阅：[格式与运行核心](02-framestore-core-implementation.md)、[公开持久化闭环](02-framestore-persistence-implementation.md)、[同步物理检查](02-framestore-inspection-implementation.md)。它们保留历史范围，不替代最终验收。

## 包、组合与历史材料

- 跨层消费者和交付要求见 [S6](../FrameStore-VersionStore/06-integration-and-delivery.md)；已有六包本地候选及限定平台隔离消费结果见[交付记录](../FrameStore-VersionStore/13-local-package-delivery.md)。源码 Accepted、包消费与公开交付各有独立资格。
- 本目录 [evidence](evidence/) 的 JSON 是不可变历史快照；内含原路径、revision、hash 与运行事实按当轮解释，移动文档不改写证据内容。
- [FrameBatch 规划器](extensions/framestore-batch-planner-candidate.md)与 [FrameLog](extensions/framelog-candidate.md)均为未接纳、未实施的候选；核心实施及验收不等待它们。
- 跨模块评审和未决讨论仍在[共享目录](../FrameStore-VersionStore/README.md)，按任务查阅，不作为本模块的第二份规范。
