# S6：消费者验证、公共包与交付

状态：**Draft；2026-10-07 同步完整字典、单文件 ref 与两类下游评估；新库实施、包发布和消费者切换均未执行**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。
本阶段验证前序合同的组合，不作为前序层运行正确性的反向依赖。
依赖 S5 的历史选点与命名核心；首版不纳入 ref 分段/轮转、差分/checkpoint、名称修改或精确发布尝试查询。
FrameLog 是[独立可选扩展](extensions/framelog-candidate.md)，不属于 S2/S3 核心验收，也不是 S4/S5 的前置。下文只验证单文件 ref、分桶 tag 和独立 branch 绑定的实际协议。

## 本阶段目标

证明两个新库承载不透明 Frame 分配、完整根地址字典发布、历史选点与命名；完成独立源码、进程中断、包消费和交付证据。
DurableGraph 是需求来源；S0 已记录兄弟仓的定位观察，实际接入仍须刷新当前源码/API/数据合同并按明确范围实施。定位及历史 tag 文档不替代接入证据。

## 候选验收模型

| 模型 | 构建内容 | 用来证明 |
| --- | --- | --- |
| 简单多根状态 | 两类自定义 binary records 与一个 RootMap | 不依赖 EventFrameHeader/应用 Parent；一次完整发布全部根 |
| 循环引用图 | A↔B/self-reference，跨文件依赖与多个根地址 | 已知尺寸租借的提前地址、交错完成与全集耐久确认；应用循环由 opaque 数据表达 |

两个模型用同一套 public API 写 data、CreateRef/PublishRef/ReadRef，枚举旧快照、创建 branch/tag，关闭并冷重开。fork 使用枚举所得的自有 RootMap 创建新 ref，再绑定名称；第二步失败允许留下未命名 ref，不虚称跨文件原子事务。没有 CreateCommit / PreparedPublication 步骤。
覆盖相同字典的重复发布、新旧 revision 分离与多个 tag；ReadRefHistory 固定完成上界，枚举结束后旧字典仍可使用。rewind 追加旧字典，保留全部既有完整记录。不同 Builder 申请/完成次序构建相同逻辑图时，只按返回地址取回，不依靠文件选择或大小排序。FrameBatch 尚未纳入首版核心，不以该优化缺失判定基本互引失败。
这些是仓内可独立运行的最小消费者，不是 DurableGraph 已接入的证据。

## 两类需求的最小消费者

[2026-10-07 评估](reviews/2026-10-07-downstream-fit.md)依据兄弟仓源码与设计故事，没有运行新库或端到端实验。以下向量进入实施验收；应用特有部分不变成 VersionStore codec。

| 场景 | 仓内最小流程 | 能证明与不能证明 |
| --- | --- | --- |
| LLM tool-loop | RootMap 同时保存 runtime/history/tasks/context；发布 Prepared/Started 后模拟外发，发布结果后重开 | 冻结请求和执行阶段可恢复；Started 无结果进入应用 uncertain 路径，不证明任意外部调用 exactly-once |
| 异步任务合并 | A/B 结果交由单 driver，依据最新 operationId/generation 串行合并，再发布字典 | 过期回包不覆盖新状态；不宣称 VersionStore 提供通用 CAS 或多线程 writer |
| Gym 历史与扇出 | 枚举非末快照，释放枚举器后创建两个 ref，绑定不同 branch 并分别推进 | 历史选点与盘上数据共享；可变对象隔离由应用加载层负责 |
| rewind/tag | 从旧快照追加新 revision，再保存 tag，推进 branch 后冷重开 | 旧完整历史保留、重复字典不合并 revision、tag 字典固定；不把 ref 发布序列当跨分支因果谱系 |

确定性模拟另由应用检验完整 world、runtime/cursor、RNG 坐标或内部状态、规则与 Agent 工作区。存储只保证原样保存/选择；轨迹 root 可表达 action/reward/fork origin。外部 tool 的幂等、receipt/query 或显式重复策略也由应用/backend 验收。

## 候选合同

### spec [S-INTEGRATION-PUBLIC-BOUNDARY] 集成通过 public API

新消费者 MUST 用两个新库的 public API，不能通过 InternalsVisibleTo、旧 EventJournal 类型或业务库引用补齐缺失能力。
如果失败暴露前序合同缺口，应回到相应 S1–S5 修订/复验。集成层不复制开销公式、创建第二个根权威或绕过耐久协议。

### spec [R-INTEGRATION-CRASH-EVIDENCE] 进程中断验证真实组合

资格 MUST 覆盖提前地址签发、交错构建各帧、必需首帧 header 初始化/校验与 active 发布、归档 flush/close/rename、多个 active 恢复、data barrier、ref 私有初始化与正式发布、ref 快照追加/flush、tag 桶初始化/追加、branch 私有写入与名称发布、库内投影及应用状态安装窗口。补成功 EndAppend 自动归还、数量 config 超限拒绝与配额计数；未来 batch/多线程优化另行取得资格。不测试首版不存在的 ref 轮转或 Commit/control 日志。
使用子进程中断/真实磁盘镜像验证冷重开，并保留源 commit、SDK、平台、阶段和预期/实际结果；单元 fault injection 不能冒充进程中断实证。
恢复结果只能来自完整 checked 快照和正式发布的文件/名字。已有 ref 更新中断后只能得到完整旧字典或完整新字典，不混搭 Key；CreateRef 中断后则为未创建或已正式发布的完整初始 ref，不补造空字典。完整坏快照不回退。Unknown 重开读取实际状态，不承诺精确尝试的 Present/Absent，也不从同值字典倒推旧调用曾确认成功。
补确定 guard 先于 data IO 拒绝、data / VersionStore 身份混用与 fault 范围、发布屏障覆盖调用时最新完成 data，以及应用安装失败不撤销 Confirmed。无关 data Builder 活跃不构成拒绝；其声明、构建内容、epoch、租借和配额在屏障后不变。数据帧写出中断与根发布中断分开裁决；完整未发布数据不自行改变任何 ref/tag。新文件私有 flush 后、正式 no-overwrite rename 前不算对象成立；rename 后调用未返回可能留下已成立对象。
组合向量包含 A 的全部依赖完成而 B 不相关仍 Building 时 CreateRef/PublishRef/CreateTag 成功、A 已发布后 B 中断/取消不撤销 A、B 所租文件中旧 dirty 依赖被 flush、B 后续 EndAppend 重新登记并须新屏障、健康取消/位置复用不赋予旧地址完成来源、任一数据 flush 失败后所有 owned Builder/Writer 停用且根不输出。真正依赖未完成 B 的 RootMap 不能发布，这是应用闭包责任，不能从数值地址或屏障成功推导库已自动验证。
同文件 Builder 活跃时，经普通指定地址随机读取此前已完成帧，配合嵌套构建验证应用行为不依赖文件分配；未完成提前地址和连同帧后 Fence 的跨边界范围先于读取拒绝。仍校验完整内容、生命周期与共享 fault，扫描/扫描边界/物理后继 guard 不变；不把本轮串行轨迹重标为并发资格。
首次 tag 桶的 header-only 初始化覆盖 flush/close/空桶 rename 故障及进程中断；尚未尝试 tag Append 时本次 tag 保持 NotAttempted，重开允许合法空桶但不存在该 tag，不清理已正式发布的合法桶。CreateBranch 单独验证只确认绑定文件、不调用 data 屏障，无关 data Builder 未结束不阻断纯命名；fork 中 CreateRef 仍遵循完整 RootMap 屏障。
容量边界分别验证单帧尺寸与起始 offset：ref / tag 桶最后合法记录的末端和尾 Fence 可以越过 SizedPtr.MaxOffset，随后追加在输出和额外 data barrier 前确定拒绝；不拿末端越界当作既有记录损坏。FrameStore 同样遵守起点规则，成功追加后按软阈值停止分配并归档。裸 FrameAddress 的原始 store 来源由应用保证，错误 data owner 的格式门绑定则必须在恢复/写入前由库拒绝，二者不能合并成自动来源检测能力。
历史枚举向量 MUST 覆盖固定上界、活动期 mutation 拒绝、释放后自有字典继续使用、重复相同字典的不同 revision、损坏与 TerminationError。普通当前值读取不自动审计全部历史；显式历史遇到坏记录必须报告错误。

### spec [S-DELIVERY-PACK-OWNER] 包入口有单一负责人

新增生产包清单及 pack 顺序 MUST 只在 `eng/Pack.ps1` 注册。当前 main 仅 Primitives/Data/Rbf，后续加入 FrameStore/VersionStore；若接纳 FrameLog，其候选落点为 FrameStore 包，不另造生产包。不得重新将冻结参考 EventJournal/RbfSegmentStore 加入 main pack。
同时适配 package-mode 依赖、smoke、metadata/assets/Source Link 校验与 CI。按 main 实际 All/manifest 入口审查扩展，不沿用历史五包/selective 选择假设，也不能只增加项目名字就声称完成。
旧栈维护与公开发布使用 RBF1 分支；main 参考项目的固定包回归与新栈源码/包资格分别报告。

### spec [S-DELIVERY-ISOLATED-SMOKE] 隔离包消费必需

候选交付 MUST 运行扩展后的 `eng/Test-Package.ps1`，仓外独立 workspace/cache/config，仅通过 public PackageReference 消费 FrameStore/VersionStore。
验证实际资产闭包、精确逐包版本、manifest/StorageSourceRevision、包 hash、静态 Source Link 与本地源码 checksum；新包闭包不得带入 RbfSegmentStore/EventJournal 或 atelia 业务依赖。
新内容用新版本，pack 要求干净已提交树及显式版本/输出目录。公开发布另依当前会话授权和账号能力。

### spec [S-DELIVERY-SEPARATE-EVIDENCE] 交付状态分别报告

源码测试、子进程恢复、Windows/Linux、隔离候选包、公开签名包回读、消费者升级及真实实例切换 MUST 分别记录。
源级通过不代表 package API 可用，静态 Source Link 不代表远端下载成功，最小图消费者不代表 DurableGraph 领域正确性。

## 仓库接入清单

| 入口 | 前序/本阶段责任 |
| --- | --- |
| 四个新 csproj、solution | S2/S4 创建和接入，S6 核对 build/test/package 身份 |
| RBF 新 API 与 XML | S1 完成；2026-10-07 活跃 Builder 随机读取底座改进另见[本轮记录](reviews/2026-10-07-completed-output-improvements.md)；S6 核对公共包入口可消费 |
| 新库源码/测试与指南 | S2–S5 形成；S6 将 Accepted 合同映射到 public smoke |
| `eng/Pack.ps1` | S6 注册包和明确选择/版本算法 |
| `eng/Test-Package.ps1`、metadata helper | S6 在当前三包隔离验证基础上增加新闭包 smoke，不削弱来源/资产校验 |
| `eng/Verify-Published.ps1`、发布 workflow/CI | 若纳入公开交付，按实际授权和新 manifest 形态适配 |
| 根 README / AGENTS.md | 项目实际存在、交付事实变化后更新；FrameStore/VersionStore 仍只有设计修订，未创建项目 |
| 旧库维护 | RBF1 分支工作包；main 冻结参考及固定包合同不得为新栈改写 |

## 实施片

1. S6-A：完善从 V1 起已有的 public API 消费者与跨冷进程 golden 资格，验证多根/循环图模型、data / 发布目录生命周期、历史选点和两类下游的最小流程。
2. S6-B：串行构建/测试、集成进程中断和 Windows/Linux 平台证据；旧基线问题明确分开处理。
3. S6-C：扩展唯一 pack 清单及选择算法、候选 manifest、隔离 smoke、CI。
4. S6-D：按授权提交/打包，完成隔离候选包消费，形成可审查交付记录。
5. S6-E：定位并评估 DurableGraph 接缝；只有接入范围已明确时修改消费者、固定包/revision 和验证领域恢复。

每片允许独立评审/返工。所有 .NET 重命令串行；先 build Release，再同配置 `dotnet test ... -c Release --no-build`。package smoke 使用既有脚本的独立输出边界。
最终应运行完整 solution 的适当验收；若任一组失败，记录其实际依赖与失败范围，不删除项目、不替换测试含义、不制造干净树。旧参考回归失败按固定 RBF1 基线或维护线处理，不在 main 为它引入 RBF3 适配。

## Ready 阻断项与出口

| ID | 需审定 |
| --- | --- |
| S6-Q1 | main 三包到五包的扩展、独立版本与 manifest 闭包；旧维护线保持隔离 |
| S6-Q2 | public smoke 项目/入口与 Source Link/资产校验扩展 |
| S6-Q3 | 两平台恢复资格、资源/规模阈值及证据保存路径 |
| S6-Q4 | 若纳入真实接入：DurableGraph 实际 API/数据需求和切换范围；不阻塞独立库/包核心 |

最低出口：两个新库的 Accepted 源码闭包、组合恢复资格和隔离公共包消费证据。
公开发布或真实消费者切换若未纳入当前工作，交付记录明确停在哪一层；不将库可用与已切换混为一个状态。
