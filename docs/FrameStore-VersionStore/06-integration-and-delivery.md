# S6：消费者验证、公共包与交付

状态：**S6-C/D Accepted（既有六包本地候选；Windows 与 WSL2/ext4 隔离包消费）；公开交付与真实接入未取得资格。** 实际候选版本、来源及限定平台见[本地交付记录](13-local-package-delivery.md)。该不可变候选不含本轮 CU；CU 已正式接纳，其新源码/进程/平台资格见[07 验收](../VersionStore/07-versionstore-cross-ref-conditional-update-acceptance.md)，本轮不自动重新打包或发布。FrameStore S2/S3 与 VersionStore S4/S5 的既有源码验收、V4-B/C/D 的组合/有限资源/应用策略、V4-F 的具名内部进程证据保留各自身份；V4-E 的反例修正及逐条裁决见[VersionStore 源码验收](../VersionStore/12-versionstore-final-acceptance.md)。公开发布与真实消费者切换另行确定范围。
前置：[S0](00-architecture-decisions.md)、[S1](../Rbf/01-rbf-sized-append.md)、[S2](../FrameStore/02-framestore-core.md)、[S3](../FrameStore/03-framestore-interleaved-builders-and-durability.md)、[S4](../VersionStore/04-versionstore-publication.md)、[S5](../VersionStore/05-versionstore-names-and-indexes.md)。本篇只负责组合、包与交付证据，不反向定义前序协议；ref/history/fork先验证，09 tag施工后补齐完整组合。FrameAddress变长增量资格见[实施记录](../FrameStore/02-framestore-varint-address-implementation.md)。

## 本阶段目标

证明两个新库承载不透明 Frame 分配、完整 root payload 发布、历史/fork 与不可变 tag；形成独立源码、进程中断、包消费和交付证据。应用 ref 名称/富元数据映射在应用层建模。
DurableGraph 是需求来源；S0 已记录兄弟仓的定位观察，实际接入仍须刷新当前源码/API/数据合同并按明确范围实施。定位及历史 tag 文档不替代接入证据。

## 候选验收模型

| 模型 | 构建内容 | 用来证明 |
| --- | --- | --- |
| 简单多根状态 | 两类自定义 binary records 与一个 RootMap | 不依赖 EventFrameHeader/应用 Parent；一次完整发布全部根 |
| 循环引用图 | A↔B/self-reference，跨文件依赖与多个根地址 | 已知尺寸租借的提前地址、交错完成与全集耐久确认；应用循环由 opaque 数据表达 |

两个模型用同一套 public API 写 data、自动或指定 CreateRef、PublishRef/ReadRef，枚举旧快照、创建 tag 并冷重开。固定角色用预留号，动态目录保存 LocalId 并在正确库 checked 定位。所选真实 revision 经自动/指定 ForkRef 建立精确 ForkOrigin 与初始 Snapshot，一次 file rename 发布；只传 roots 表示无来源起点。ReadRefHistory 遍历继承前缀，ListForks 查询全部声明边；无 CreateCommit/PreparedPublication 或默认多对象事务。
覆盖相同字典的重复发布、新旧 revision 分离与多个 tag；ReadRefHistory 固定完成上界，经同步 visitor 交付自有 RefBytesSnapshot，调用结束后旧字典仍可使用。rewind 追加旧字典，保留全部既有完整记录。不同 Builder 申请/完成次序构建相同逻辑图时，只按返回地址取回，不依靠文件选择或大小排序。
仓内已提供可独立运行的 [VersionStore.Smoke](../../tools/VersionStore.Smoke/README.md)：多根/循环图、历史选点/fork/rewind/tag 由新进程冷读验证。Windows 两层锁与六个公开停点的实际强杀见 [V4-B 记录](../VersionStore/12-versionstore-implementation-plan.md#v4-b-验证记录2026-10-11)。这不是 DurableGraph 接入、内部写入窗口或整张 S6 矩阵完成的证据。 WSL2/ext4与Windows的有限资源消费者、平台用例及原生锁实测见 [V4-C记录](../VersionStore/12-versionstore-linux-resource-acceptance.md)；冻结EventJournal单独tmpfs回归与新栈ext4资格分开。

## 两类需求的最小消费者

[2026-10-07 评估](reviews/2026-10-07-downstream-fit.md)依据兄弟仓源码与设计故事，没有运行新库或端到端实验。以下向量进入实施验收；应用特有部分不变成 VersionStore codec。

| 场景 | 仓内最小流程 | 能证明与不能证明 |
| --- | --- | --- |
| LLM tool-loop | RootMap 同时保存 runtime/history/tasks/context；发布 Prepared/Started 后模拟外发，发布结果后重开 | 冻结请求和执行阶段可恢复；Started 无结果进入应用 uncertain 路径，不证明任意外部调用 exactly-once |
| 异步任务合并 | A/B 结果交由单 driver，依据最新 operationId/generation 串行合并，再发布字典 | 过期回包不覆盖新状态；不宣称 VersionStore 提供通用 CAS 或多线程 writer |
| Gym 历史与扇出 | visitor 选中非末快照并停止，返回后分别创建两个自动或预留区指定 fork，再独立推进 | 每次仅一个新 ref 文件原子发布；两个 fork 不是整批事务，对象隔离由应用负责 |
| rewind/tag | 旧快照追加新 revision，再保存 tag、推进 ref 后冷重开 | 历史保留、同值不合并 revision、tag 字典固定，不把 ref 历史当业务因果 |

V4-D 已由 [public consumer 应用示例](../../tools/VersionStore.Smoke/README.md#应用策略示例)接通前两项：tool-loop 三个应用停点真实kill后冷恢复，generation 用受控Task完成顺序驱动串行合并；具体平台、反例与取证边界见[实施记录](../VersionStore/12-versionstore-implementation-plan.md#v4-d-验证记录2026-10-11)。外部receipt是模拟服务，结果结算是应用发布状态内的计入，不代表真实外部支付或任意backend的exactly-once。

确定性模拟另由应用检验完整 world、runtime/cursor、RNG 坐标或内部状态、规则与 Agent 工作区。存储保证原样保存/选择与精确 ref 创建来源；轨迹 root 可另表达 action/reward/实验条件，不能把发布分叉图当作业务推演因果。外部 tool 的幂等、receipt/query 或显式重复策略也由应用/backend 验收。
同步发布消费 S4 `[A-VS-PUBLICATION-EVIDENCE]`：每次初始化 out，成功 Result 为 Confirmed，确定失败为 NotAttempted，异常保存证据并停止旧流程。证据不代表 owner 健康，确认后安装失败仍重开。自动创建丢失返回值不按同值认领；指定目标可冷开 checked 查询，但已占用不证明旧请求 Confirmed。外部 operationId 仍在应用数据内。
tool-loop 首次/新 attempt 外发之前 MUST 正常确认该 attempt 的冻结请求、身份与 Started。重开已有完整 Started 但没有结果时，以实际应用 phase 走 backend 查询、幂等或显式 uncertain 策略；不能从 ReadRef 推断旧调用曾 Confirmed 或外部操作未执行。当前 ProcessCrashOnly 模型不要求为每次恢复再追加同值 Started；更强的“每次 dispatch 取得本轮新 flush”政策由应用明确选择，不新增存储层自动恢复动作。结果 phase 已成立时不再次执行或结算同一操作。

## 候选合同

### spec [S-INTEGRATION-PUBLIC-BOUNDARY] 集成通过 public API

新消费者 MUST 用两个新库的 public API，不能通过 InternalsVisibleTo、旧 EventJournal 类型或业务库引用补齐缺失能力。
如果失败暴露前序合同缺口，应回到相应 S1–S5 修订/复验。集成层不复制开销公式、创建第二个根权威或绕过耐久协议。

### spec [R-INTEGRATION-CRASH-EVIDENCE] 进程中断验证真实组合

各层字段、codec、局部恢复、错误/清理与独立golden的详细验收保持在所属文档；S6 MUST 引用实际来源/结果并验证跨层public接线，不再维护第二份逐字段测试清单：

| 验收权威 | S6消费范围 |
| --- | --- |
| [S2/S3最终源码验收](../FrameStore/02-framestore-final-acceptance.md)、[变长地址增量](../FrameStore/02-framestore-varint-address-implementation.md) | 三种追加/交错Builder、两份地址codec、路径/发号、门/锁/私有data、轮转/恢复、FrameRead及Inventory/Audit；已Accepted结果保持其来源身份 |
| [S4实施与验收](../VersionStore/04-versionstore-publication.md#实施与验收) | VS借入guard/模式/绑定、建库/锁/唯一pending、ref身份/路径/两域发号/ListRefs、实际需要的 header/head 与恢复后完整性、检查范围、data-first/Outcome及受控清理 |
| [S5实施与验收](../VersionStore/05-versionstore-names-and-indexes.md#实施与验收) | 固定历史上界/预算/停止/回调、直接精确fork来源及byte clone、指定边界继承历史、ListForks header/完整声明图/额度/自有结果 |
| [08时间](../VersionStore/08-versionstore-record-metadata.md)、[09tag](../VersionStore/09-versionstore-immutable-tags.md)、[10可选RootMap](../VersionStore/10-versionstore-rootmap-payload.md)、[11bytes](../VersionStore/11-versionstore-root-payload-bytes-api.md) | 所属格式向量、初始化/扫描、长度/捕获/结果生命周期与内容解释边界；ref固定meta与tag独立meta资格分别保持，tag元信息成功不证明root健康或精确来源 |

FrameStore既有资格不覆盖新增公共EnsureUsable或VS组合；先取得其新增源码/public证据再接入。S5继承历史使用已实施的[RBF 指定边界逆扫公共重载](../Rbf/rbf-reverse-boundary-plan.md)，V2-A 源码验证单独记录；既有Accepted不自动包含此增量，S6须补充其包消费与组合资格。public消费者不能借反射、internal入口或旧类型补能力；底座合成探针不代替新库/header/身份门或组合实证。

**进程中断矩阵。** 使用子进程终止及真实文件冷重开，覆盖以下跨层窗口；保留源commit、SDK、平台、failpoint、预期/实际结果与证据路径。单元fault injection、字节前缀镜像与真实进程中断分别报告，不冒充断电资格。

| 窗口 | 组合必须证明 |
| --- | --- |
| data构建/完成/barrier及active发布/归档/恢复 | 完整未发布data不改变ref/tag；A依赖完成而无关B Building仍可发布，leased文件旧dirty被确认，B后续完成需新屏障；B后来取消/中断不撤销A，data flush失败无根输出。跨文件/同文件历史随机读与交错构建只按实际地址取回，不依赖分配/完成次序 |
| ref创建/fork的pending、flush/close、rename前后 | 两种编号入口均为未创建或完整正式ref；private不公开/不续作，RO保留。正式缺/坏必要帧报错；Windows/Linux严格不覆盖rename由VS真实入口取证，不用FrameStore移动结果替代。源确认失败无子输出，不向源登记孩子 |
| 普通ref追加/flush与恢复 | 当前值为完整旧bytes或完整新bytes，RootMap例子不混搭Key；最新必要head坏时不回退；未访问的坏初始/旧记录不阻断健康head，独立完整校验仍报告。初始和后续残尾按S4接受恢复后完整性，RO需要修尾即拒绝；初始化中断与人为截短的格式恢复向量分开 |
| tag目录/空桶/pending/rename及首条或后续Append | 正式合法空桶与未创建tag可共存，空桶rename不确认首tag；header初始化保护、独立meta CRC及请求名称裁决按09。截断首body保留空桶，CompletedTail后重新扫描裁决；坏root的可信非目标不阻断健康目标，可信同名仍已占，Resolve命中才完整校验root；meta名称不确定时拒绝missing/Create，结构、I/O及close失败仍传播 |
| 发布确认前后、库内交付及应用安装 | public out在原异常/OOM/助手异常后可见，正常失败NotAttempted、未确认尝试Unknown、确认后结果/清理失败Confirmed。进程终止无out，重开只读实际事实；自动丢结果可发现而不能按同值认领，指定号码不证明旧attempt。健康借入data不因VS失败或Dispose被关闭 |
| 应用外发与独立目录登记 | Started无结果、外部已执行但结果未发布、结果已发布、过期generation分别走应用策略；不由存储盲重试/补发Started。固定槽/动态LocalId与正确库关联，独立目录二次发布可留未登记ref，不声称跨ref事务或exactly-once |

**public端到端组合。** 完成前述最小模型与应用场景，再验证Encode→bytes发布→自有读取→Decode、历史选点后fork/tag/rewind、旧结果跨查询/owner关闭保留及坏data的应用失败。禁用RootMap decoder验证核心路径，JSON/随机bytes同样可发布/读取/fork/tag；fork保留exact源bytes，历史实际校验来源一致性而ListForks只检查header与声明图。坏初始/健康head的ReadRef成功与历史提前停止、实际走到坏节点失败分别验证；源大量后续更新不增加旧fork点定位/历史接续的扫描步骤。合法末帧越过MaxOffset的末端与下一次追加拒绝分别验证；库绑定拒错data与裸地址来源的应用责任分开。

tag 元信息组合在[实施计划](../VersionStore/12-versionstore-implementation-plan.md)的V3-C接通后补齐：ReadTagInfo可读取root损坏但meta健康的记录；ScanTags以visitor完成名称枚举、时间段及Related ref筛选，不读root、不签发来源revision或证明bytes一致，也不证明整体名称唯一。所到坏meta失败，false立即VisitorStopped，不访问后缀；回调副作用不因后来失败回滚。Related创建仍只执行既定data-first发布，不重读/flush注记ref。

single-driver/HistoryActive/TagScanActive、取消/回调Dispose及必要清理规则按所属阶段在public调用中复验；历史callback可进入ScanTags并使用独立临时桶槽，tag扫描callback拒绝mutation、递归扫描与进入历史，允许的日常读取按09执行。这些串行轨迹不改标为并发资格。实际Open/正式发现成本、history源anchor及必要前缀读取、ListForks规模、tag请求范围桶扫描/连续创建、全桶元信息扫描、bytes复制与资源峰值另测，不把增桶预计记录下降称为端到端提速，也不用codec算术或预算替代性能/平台证据。[07 CU](../VersionStore/07-versionstore-cross-ref-conditional-update.md)已正式接纳，新增组合矩阵与实际结果独立列在[07 验收](../VersionStore/07-versionstore-cross-ref-conditional-update-acceptance.md)，不重标前轮 public 停点或历史探针。

CU 组合必须包含：全部 fresh 成员完成前缀/排列及至少两个既有 ref 的 public 冷重开、data-first 与逐文件完成/flush/确认后安装窗口、未提交 CU 跳过且历史不泄漏、普通覆盖后的旧组资格、同/异长位置复用与必要结构错误、lazy CompletedTail、CU 来源的普通子 Snapshot byte clone、peer 预算/清理、Audit 已发布计数。真实进程终止、受控异常、镜像截断各自标明证据类别；ProcessCrashOnly 不外推为断电。整组成功只共同确认发布资格，不保证跨调用快照隔离或当前 heads 永远同步。

### spec [S-DELIVERY-PACK-OWNER] 包入口有单一负责人

新增生产包清单及 pack 顺序 MUST 只在 `eng/Pack.ps1` 注册。当前 main 为 Primitives/Data/Rbf/Binary/FrameStore/VersionStore；Binary 独立于前三包，FrameStore 因变长地址直接依赖 Binary，VersionStore 继续依赖 FrameStore/Rbf/Binary。2026-10-11 用户明确进入本地包交付，两库包注册与候选资格见[交付记录](13-local-package-delivery.md)。不得重新将冻结参考 EventJournal/RbfSegmentStore 加入 main pack。
同时适配 package-mode 依赖、smoke、metadata/assets/Source Link 校验与 CI。按 main 实际 All/manifest 入口审查扩展，不沿用历史五包/selective 选择假设，也不能只增加项目名字就声称完成。
旧栈维护与公开发布使用 RBF1 分支；main 参考项目的固定包回归与新栈源码/包资格分别报告。

### spec [S-DELIVERY-ISOLATED-SMOKE] 隔离包消费必需

候选交付 MUST 运行扩展后的 `eng/Test-Package.ps1`，仓外独立 workspace/cache/config，仅通过 public PackageReference 消费 FrameStore/VersionStore。
验证实际资产闭包、精确逐包版本、manifest/StorageSourceRevision、包 hash、静态 Source Link 与本地源码 checksum；新包闭包不得带入 RbfSegmentStore/EventJournal 或 atelia 业务依赖。
新内容用新版本，pack 要求干净已提交树及显式版本/输出目录。公开发布另依当前会话授权和账号能力。

### spec [S-DELIVERY-SEPARATE-EVIDENCE] 交付状态分别报告

源码测试、子进程恢复、Windows/Linux、隔离候选包、公开签名包回读、消费者升级及真实实例切换 MUST 分别记录。
源级通过不代表 package API 可用，静态 Source Link 不代表远端下载成功，最小图消费者不代表 DurableGraph 领域正确性。
已有六包候选的版本/manifest/hash/来源不可变，不因当前 main 新增 CU 而自动拥有 PublishRefs。后续 CU 包交付必须使用新版本，从对应干净已提交树打包并重跑四个隔离 PackageReference smoke；本轮 CU 源码实施不自动授权这一步或网络发布。

## 仓库接入清单

| 入口 | 前序/本阶段责任 |
| --- | --- |
| 四个新 csproj、solution | S2/S4 创建和接入，S6 核对 build/test/package 身份 |
| RBF 新 API 与 XML | S1 完成；2026-10-07 活跃 Builder 随机读取底座改进另见[本轮记录](reviews/2026-10-07-completed-output-improvements.md)；V2-A 已补指定边界逆扫重载及源码验证；S6 核对公共包入口可消费 |
| 新库源码/测试与指南 | S2–S5 形成；S6 将 Accepted 合同映射到 public smoke |
| `eng/Pack.ps1` | S6 注册包和明确选择/版本算法 |
| `eng/Test-Package.ps1`、metadata helper | S6 在当前纯 Rbf 三包及纯 Binary 两包隔离验证基础上增加新闭包 smoke，不削弱来源/资产校验 |
| `eng/Verify-Published.ps1`、发布 workflow/CI | 若纳入公开交付，按实际授权和新 manifest 形态适配 |
| 根 README / AGENTS.md | 项目实际存在、交付事实变化后更新；FrameStore/VersionStore 与测试项目均已创建，两库已纳入本地候选包清单；VersionStore 已接通普通 ref 真实目录闭环，历史读取、精确 ForkRef 与 ListForks 已接通，tag 公开持久化闭环、关联注记及元信息公面已接通，V4-A 已补只读完整校验 Audit |
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
| S6-Q1 | 将 FrameStore/VersionStore 加入 main 包清单、独立版本与 manifest 闭包；保留 Binary 独立闭包及旧维护线隔离 |
| S6-Q2 | public smoke 项目/入口与 Source Link/资产校验扩展 |
| S6-Q3 | 两平台恢复/文件发布资格、资源预算、ref 发现与 tag 请求范围桶扫描规模成本及证据保存路径 |
| S6-Q4 | 若纳入真实接入：DurableGraph 实际 API/数据需求和切换范围；不阻塞独立库/包核心 |

最低出口：两个新库的 Accepted 源码闭包、组合恢复资格和隔离公共包消费证据。
公开发布或真实消费者切换若未纳入当前工作，交付记录明确停在哪一层；不将库可用与已切换混为一个状态。

V4-E取得两个新库的独立源码出口；V4-F补充8个内部发布停点、4个真实物理残尾停点、VS发布路径冲突、独立目录登记窗口和tag六个名称/路由golden冷进程回读。限定范围、两平台运行与证据身份见[验收记录](../VersionStore/12-versionstore-final-acceptance.md#s6-组合资格与后续最小分片)及[实施计划](../VersionStore/12-versionstore-implementation-plan.md)。源级既定施工队列到此收束；S6-C/D包注册/隔离包消费现按明确授权进入[本地交付片](13-local-package-delivery.md)，实际出口资格以该记录为准。TOCTOU原生rename冲突、任意字节切点、设备故障与断电不在本轮实测范围。
