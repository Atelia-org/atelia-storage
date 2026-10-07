# 2026-10-07：完整 RootMap 与单文件 ref 的下游适配评估

> 后续合同变更说明（同日）：用户已接受活跃 Builder 期间确认/随机读取此前已完成输出，且不相关 Builder 不再阻断 RootMap 发布。本文保留原始需求评估正文；新合同与独立源码验收另见[改进记录](2026-10-07-completed-output-improvements.md)，本评估不重标为运行时验证。

状态：**Informative / 设计评估完成；未实现新库，未运行下游、build/test 或真实进程中断实验**。
两个只读 subagent 分别评估 LLM tool-loop 与 Game Agent Gym；主线程核对关键源码和失败轨迹后裁决。兄弟仓是需求证据，不能作为新栈的实现或接入资格。
文档修订后另做独立只读复核，未发现功能阻碍；首次 tag 桶的步骤歧义已明确为“先发布 header-only 空桶，再普通追加/flush 首 tag”，与后续 tag 的结果证据一致。
当前规范分别见 [S0](../00-architecture-decisions.md)、[S4](../04-versionstore-publication.md)、[S5](../05-versionstore-names-and-indexes.md)；本文不建立第二套格式或运行协议。

## 评估结论与最小补充

两类需求均可由完整 `string => FrameAddress` 字典表达，未发现必须加入独立 Commit/Parent、全局 FrameLog、差分/checkpoint、ref 分段或通用 CAS 的关键缺陷。
每个 ref 一份 RBF3 文件；当前读取最后完整快照，历史仍保留。唯一需要明确补全的库能力是**历史选点与快照生命周期**：从固定完成上界逆序枚举完整 checked 快照，返回枚举结束后仍可使用的自有字典；活动期禁止同 owner mutation。
随机 ReadRefRevision、持久 cursor 与历史索引不是首版功能前置。应用可拿所选字典创建 ref/tag 或 rewind；跨重开的书签使用不可变 tag。

## LLM tool-loop：表达能力足够，外部副作用仍需应用协议

示例 Roots：`{runtime, history, tasks, context}`。runtime 保存 phase/attempt/operationId/游标，tasks 保存参数与结果，context 保存冻结请求及其依赖；所有值仍只是数据 FrameAddress。

| 窗口 | 应用恢复依据与下一步 |
| --- | --- |
| Prepared 已发布、尚未 Started | 用冻结请求恢复，不能按当前配置重新拼出另一份输入 |
| Started 已发布、外发结果未记录 | backend 查询/幂等重试，或显式暂停/新 attempt；不能默认宣称原操作未执行 |
| 结果数据完成、Roots 发布中断 | 重开实际 ref；旧/新完整字典决定阶段，不能按异常后的旧内存缓存重试 |
| ResultObserved 已发布 | 按已结算状态继续，不再次执行工具 |
| A/B 异步回包或旧 generation 回包 | 单 driver 按最新任务表与 operationId/generation 合并或拒绝，不用旧 Roots 覆盖新状态 |

外发不能早于 Started 的成功持久化。存储原子发布的是字典，不能让任意外部 tool 恰好执行一次，也不能找回尚未持久化且 provider 无查询能力的响应。精确执行身份可保存为应用字段，不需要变成 VersionStore 发布 token。

需求证据（2026-10-07 源码只读观察）：

- [SessionJournalEngine.cs](../../../../atelia/prototypes/SessionJournal/SessionJournalEngine.cs) 3120–3158：提交 Prepared/Started 后执行 completion；3325–3337：uncertain 默认 Refuse，可显式 RestartWithNewAttempt；3480–3510：工具恢复保留 operationId 和 reservation。
- 同文件 3275 附近：发布异常后不以旧内存 head 判定事实；3578 附近：回包接受检查当前 head。
- [SessionJournalEngineTests.cs](../../../../atelia/tests/SessionJournal.Tests/SessionJournalEngineTests.cs) 2876、3197 附近：结果观察后的恢复和 stale-head 回调测试。本轮仅阅读，未运行，也不将注入异常视为外部 Kill 证明。

## Game Agent Gym：历史选点足够，业务谱系由应用保存

示例 Roots：`{world, agent, runtime, trajectory}`。runtime / world 需覆盖模拟时间、cursor、RNG 坐标或内部状态、规则与环境版本、待执行程序和 Agent 工作区；trajectory 可保存 action/reward/业务前驱/fork origin。

| 流程 | 最小存储操作 |
| --- | --- |
| 同点反事实扇出 | 从 main 历史选旧字典，结束枚举；创建 left/right ref，再绑定名字，分别加载和推进 |
| rewind | R0→R1→R2 后追加 R0 的字典形成 R3；当前内容等于 R0，既有 R1/R2 不删除 |
| 永久书签 | CreateTag 保存所选完整字典；branch 后续推进不改变 tag |
| 轨迹分析 | 按所选 trajectory 地址读小型事件，必要时才展开 world；应用链不由 ref 发布次序推算 |
| 步骤发布中断 | 重开完整 Roots，按已持久的 pending/progress 决定下一步，不盲目重复计划或执行 |

新 ref 的首快照复制的是根字典，不自动复制源 ref 历史，也不提供跨分支谱系。需要该信息时，应用在 trajectory/experiment 数据内保存来源。
盘上帧共享是不可变共享；不同 rollout 的可变 CLR 对象隔离由图加载层负责。保存了 world 不代表已经保存全部可重现状态；确定性和分叉随机流政策由应用检验。

需求证据（不是现成新栈验收）：

- [DramaBoard PROJECT-STATE](../../../../drama-board/PROJECT-STATE.md) 33 附近明确盘上续局与可玩 fork 尚未实现。
- [DurableGraph rollout 用例](../../../../durable-graph/docs/design-branches/0083-checkpoint-api-user-stories/rollout.md) 是目标故事；58–85 描述同点 fork、应用 LineageId、独立工作副本和重复性边界。
- [KernelCursor.cs](../../../../drama-board/src/Kernel/Simulation/KernelCursor.cs)、[DeterministicRandom.cs](../../../../drama-board/src/Kernel/Random/DeterministicRandom.cs) 体现 cursor 与 seed/stream/generation/sampleIndex；[SimulationKernel.cs](../../../../drama-board/src/Kernel/Simulation/SimulationKernel.cs) 57 附近规定完成已发布 pending，不重新 Forecast/Plan/Player。

## 进入实施验收的向量

1. 多个根共同变更，在 data 写入/barrier/ref append/flush 窗口中断；恢复只选完整旧字典或完整新字典，不混搭 Key。
2. 不接受未完成/已取消地址作为应用发布闭包；完整未发布数据不自行成为 head/tag。
3. checked 当前快照损坏明确失败，不退到旧值；历史中间损坏由显式枚举报告，不伪装正常结束。
4. 历史固定完成上界，活动期 mutation 拒绝，释放后所选字典继续可用；重复同值字典产生不同完成 revision。
5. 从非末快照创建两 ref/tag，再分别推进、关闭重开；原 ref 与 tag 内容保持不变，rewind 保留旧完整历史。
6. 新 ref/branch 私有文件 flush 与正式发布分别中断；允许完整未命名 ref，不承诺跨文件事务或精确 Unknown 尝试追踪。
7. tool-loop 用冻结请求、Started uncertain 和已结算结果决定恢复；A/B 回包按最新应用身份合并。应用不从当前字典相同倒推某次发布曾确认。
8. Gym 完整状态、同动作确定性和可变工作副本隔离作为应用验收；没有存储层通用 Parent 或 RNG 字段。

库向量并入 [S6](../06-integration-and-delivery.md)，应用向量仅验证该边界能被表达，不宣称现有消费者已经迁移。
单文件仍受 [SizedPtr](../../../src/Data/SizedPtr.cs) 与 RBF 公共容量约束；接近上限显式拒绝。少量短 Key / 根地址的约 64B 估算支持延期分段，但实际记录大小还含字符串、codec 与 framing，实施必须用公共尺寸 API 计算。
