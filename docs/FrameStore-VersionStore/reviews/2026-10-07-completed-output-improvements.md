# 2026-10-07：活跃构建期间使用已完成输出

状态：**Informative / 本轮合同修订与底座实施验收记录；Windows Release 源码验收通过，FrameStore/VersionStore 项目尚未创建，S2–S6 保持 Draft**。
文档基线为 `6372557`。用户授权按需修改文档、代码及本地 Git 提交；本轮只实施 RBF 底座必要能力并修订上层合同，不扩大为两个新库的创建、包发布或下游接入。

## 已接受的边界

**ConfirmDurable 确认调用时已经完成的全部必要输出；未完成 Builder 保持原状，不因此次确认而获得完成或耐久资格。**
**Builder 活跃期间，允许随机读取已经完成的文件前缀；正在构建的新帧仍不可读。**

这两句话导航到正式合同，不新增第二套规范。FrameStore 屏障仍覆盖全部必要 active 完成输出，包括 leased 文件中的旧 dirty 帧及每个 reopened active 的首次确认；archive 仍以归档前 flush 取得资格。Builder 后续完成重新登记为未确认，失败停用整个 owned FrameStore，包括其他文件上的 Builder/Writer。
RootMap 仍由应用保证实际新增依赖全部完成、来源及闭包合法；VersionStore 仍 data-first，再追加/确认根。无关 Builder 可以保持构建，本轮不增加依赖图遍历、receipt、选定帧集合、日志或并发屏障。
随机读取的 Building 前缀判定包含帧后的 Fence，范围检查不证明真实主链成员或原始存储来源。完整内容校验、生命周期、共享 fault 与串行调用保留；新申请的扫描、扫描边界和物理后继查询的 Building guard 不变，已捕获旧完成上界的 sequence/枚举器仍按既有合同串行推进。

## 条款迁移与权威位置

| 原条款 | 本轮处理 | 当前权威 |
| --- | --- | --- |
| `[A-FS-DURABLE-OWNER]` | 保留旧锚点并标记 DEPRECATED，取消全局无 Builder 前置 | [S2](../02-framestore-core.md) `[A-FS-DURABLE-COMPLETED-OUTPUTS]` |
| `[S-VS-ROOTS-BARRIER]` | 保留旧锚点并标记 DEPRECATED，取消无关 data Builder 拒绝 | [S4](../04-versionstore-publication.md) `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` |
| RBF `[S-RBF-READ-DISALLOW-WHILE-BUILDER-ACTIVE]` | 底座规范保留历史锚点并废弃其随机读取禁令 | [RBF 接口](../../Rbf/rbf-interface.md) `[S-RBF-RANDOM-READ-COMPLETED-PREFIX]` |
| 活跃 Builder 期间 flush 的底座行为 | 明确保留构建内容及 borrowed/reservation 状态 | [RBF 接口](../../Rbf/rbf-interface.md) `[S-RBF-FLUSH-PRESERVES-BUILDER]` |
| S2 目标文件 Building 读取限制 | 从租借串行条款中移除，建立消费底座的独立条款 | [S2](../02-framestore-core.md) `[S-FS-RANDOM-READ-COMPLETED-PREFIX]` |

S0 记录会话决定，S2/S4 唯一定义核心规则，S3/S5/S6、README、问题索引与 FrameLog 候选消费这些规则。历史审阅及 S1 验收只补充后续变更范围说明，原始正文和检查数字保留。

## 后续新库实施的必要向量

| 场景 | 必要结果 |
| --- | --- |
| A 及其新增依赖完成，B 不相关仍 Building | ConfirmDurable 成功，A 可 CreateRef/PublishRef/CreateTag；B 构建状态和租借不变 |
| 独立 A 已合法发布后，无关 B 中断或取消 | A 的已确认发布不被撤销；不能把 A 普遍解释为未发布 orphan |
| B 的 leased 文件中还有 A 引用的旧 dirty 帧 D | D 必须在根输出前被 flush；不能跳过 leased 文件 |
| A 真正依赖未完成 B | 应用不得发布；屏障返回不证明闭包完整，库不承诺自动解析并识别此误用 |
| B 在此前屏障之后成功完成 | 新输出重新登记为未确认，须新的 ConfirmDurable |
| B 取消后位置被 C 复用 | 旧 B 地址不因此成为合法完成来源；应用放弃旧构建状态 |
| Builder 活跃时读取同文件旧帧 | 普通随机读取成功；未完成提前地址或连同尾 Fence 越过已完成前缀的范围先拒绝 |
| 任一文件 data flush 失败 | 全 FrameStore owner 及其 Builder/Writer 停止；本次根不输出，按现有结果证据处理 |

上述是 S2–S6 后续实施向量，不能以本轮 RBF 单文件测试替代。扫描 guard、完整坏内容、Dispose/fault、地址来源、软轮转、目录恢复和取消资格仍分别验收。

## 本轮证据与未覆盖范围

RBF 实现位于 [RbfFileImpl.cs](../../../src/Rbf/Internal/RbfFileImpl.cs)，公开合同同步于 [IRbfFile.cs](../../../src/Rbf/IRbfFile.cs)。五个指定 ticket 的随机读取入口共用 Building 前缀 guard，检查包含尾 Fence；Idle 参数/Result、扫描入口、reader/cache、已有 FrameInfo 与 fault 机制保持原路径。没有新增文件句柄或 FrameInfo 缓存绕过。

新增 [RbfCompletedPrefixReadTests.cs](../../../tests/Rbf.Tests/Internal/RbfCompletedPrefixReadTests.cs) 的 17 个展开用例：已知/未知 Builder × cache Off/Slots16、非零 EscapeKey 的历史完整读、五个随机入口、提前 ticket/FrameBytes 末端等于 TailOffset 但尾 Fence 越界的 pre-I/O/no-rent 拒绝、取消/reuse、完成后短尾页刷新、Payload/Trailer CRC、扫描 guard、fault/Dispose，以及未 Advance 的真实借用和 pending reservation 跨历史读取与 flush 后继续完成。成功 flush 不输出当前 Builder；随后 EndAppend 的输出仍另行 flush。

| 验证 | 本轮结果 |
| --- | --- |
| 文档与源码独立复核 | 无阻断；主线程核对 15 份文档、146 个本地链接（含 3 个锚点）；原 88 个 Clause-ID 全保留，现 92 项唯一；RBF 接口原 47 项全保留，现 50 项唯一；围栏、表格、whitespace 和 `git diff --check` 通过 |
| RBF 指定 ticket 读取、未完成/跨边界 pre-I/O 拒绝、scan guard 与生命周期/fault | RBF 835/835 通过，其中本轮新增 17/17；TRX 的实际结果已核对 |
| solution Release build 与匹配 `--no-build` 测试 | build 成功，0 error、41 个既有 XML 注释 warning；完整测试 1542/1542，无失败或跳过 |
| 新旧栈实际依赖隔离 | `eng/Test-Rbf1ReferenceAssets.ps1` 通过，旧栈七组 assets 精确引用指定来源的 RBF1 包；Rbf.Tests assets 中 Primitives/Data/Rbf 均为 main project |
| FrameStore/VersionStore runtime、组合恢复、平台及包消费 | 未实施；新项目尚未创建，不属于本轮验收 |

实际执行 `dotnet build Atelia.Storage.slnx -c Release --nologo -v minimal`，随后 `dotnet test Atelia.Storage.slnx -c Release --no-build --nologo --logger trx --results-directory <results>`；没有并行执行其他 .NET build/test/pack。测试 TEMP/TMP 为 `W:/atelia-storage-validation/builder-prefix-20261007-ae230700980a476fa9bd2807311d3d0a/temp`，六组 TRX 保留于同一目录的 `results`，完整计数由这些结果文件汇总。

现有 RBF DurableFlush 在 Builder 活跃期间确认旧输出的底座行为保留；FrameStore 的 leased 登记及跨文件 owned fault 尚待 S2 实施。没有公开包发布或更强断电模型资格。
