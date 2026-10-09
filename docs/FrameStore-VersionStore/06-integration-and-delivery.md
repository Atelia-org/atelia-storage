# S6：消费者验证、公共包与交付

状态：**Draft；2026-10-07 同步完整字典、单文件 ref、命名 fork 的目录共同发布与两类下游评估；2026-10-09 同步 ForkOrigin、跨文件历史与全分叉验收；新库实施、包发布和消费者切换均未执行**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。
本阶段验证前序合同的组合，不作为前序层运行正确性的反向依赖。
依赖 S5 的历史选点与命名核心；首版不纳入 ref 分段/轮转、差分/checkpoint、名称修改或精确发布尝试查询。
本阶段验证单文件 ref、分桶 tag 和独立 branch 绑定的实际协议。

## 本阶段目标

证明两个新库承载不透明 Frame 分配、完整根地址字典发布、历史选点与命名；完成独立源码、进程中断、包消费和交付证据。
DurableGraph 是需求来源；S0 已记录兄弟仓的定位观察，实际接入仍须刷新当前源码/API/数据合同并按明确范围实施。定位及历史 tag 文档不替代接入证据。

## 候选验收模型

| 模型 | 构建内容 | 用来证明 |
| --- | --- | --- |
| 简单多根状态 | 两类自定义 binary records 与一个 RootMap | 不依赖 EventFrameHeader/应用 Parent；一次完整发布全部根 |
| 循环引用图 | A↔B/self-reference，跨文件依赖与多个根地址 | 已知尺寸租借的提前地址、交错完成与全集耐久确认；应用循环由 opaque 数据表达 |

两个模型用同一套 public API 写 data、CreateRef/PublishRef/ReadRef，枚举旧快照、创建 branch/tag，关闭并冷重开。匿名 fork 使用所选真实 revision 调用 ForkRef；命名 fork 用 CreateBranchFromRevision，在私有容器同时准备 ForkOrigin、初始 Snapshot 与 binding，一次目录 rename 共同发布；只传 roots 的创建表示独立历史起点。通过 ReadRefHistory 遍历完整继承前缀，通过 ListForks 查询全部声明分叉。普通 CreateBranch 仍为既有 ref 添加 alias，手工两步不是事务；没有 CreateCommit / PreparedPublication 或通用多对象事务步骤。
覆盖相同字典的重复发布、新旧 revision 分离与多个 tag；ReadRefHistory 固定完成上界，经同步 visitor 交付自有 RefSnapshot，调用结束后旧字典仍可使用。rewind 追加旧字典，保留全部既有完整记录。不同 Builder 申请/完成次序构建相同逻辑图时，只按返回地址取回，不依靠文件选择或大小排序。
这些是仓内可独立运行的最小消费者，不是 DurableGraph 已接入的证据。

## 两类需求的最小消费者

[2026-10-07 评估](reviews/2026-10-07-downstream-fit.md)依据兄弟仓源码与设计故事，没有运行新库或端到端实验。以下向量进入实施验收；应用特有部分不变成 VersionStore codec。

| 场景 | 仓内最小流程 | 能证明与不能证明 |
| --- | --- | --- |
| LLM tool-loop | RootMap 同时保存 runtime/history/tasks/context；发布 Prepared/Started 后模拟外发，发布结果后重开 | 冻结请求和执行阶段可恢复；Started 无结果进入应用 uncertain 路径，不证明任意外部调用 exactly-once |
| 异步任务合并 | A/B 结果交由单 driver，依据最新 operationId/generation 串行合并，再发布字典 | 过期回包不覆盖新状态；不宣称 VersionStore 提供通用 CAS 或多线程 writer |
| Gym 历史与扇出 | visitor 选中非末快照并正常停止，调用结束后分别原子创建两个命名 fork，再独立推进 | 单个新 ref/初始名称共同发布；两个 fork 不是整批事务，可变对象隔离由应用加载层负责 |
| rewind/tag | 从旧快照追加新 revision，再保存 tag，推进 branch 后冷重开 | 旧完整历史保留、重复字典不合并 revision、tag 字典固定；不把 ref 发布序列当跨分支因果谱系 |

确定性模拟另由应用检验完整 world、runtime/cursor、RNG 坐标或内部状态、规则与 Agent 工作区。存储保证原样保存/选择与精确 ref 创建来源；轨迹 root 可另表达 action/reward/实验条件，不能把发布分叉图当作业务推演因果。外部 tool 的幂等、receipt/query 或显式重复策略也由应用/backend 验收。
同步发布消费 S4 `[A-VS-PUBLICATION-EVIDENCE]`：调用方每次先初始化 out 变量，成功 Result 表示 Confirmed，失败 Result 表示确定未发布，异常时保存 out 证据并停止旧流程。NotAttempted 不能推出无 I/O/健康，Confirmed 后安装失败也必须重开；匿名创建没有返回新 RefId 时，不承诺从同值字典精确找回旧调用。需要稳定恢复入口的示例使用命名创建，外部 operationId 仍在应用数据内。
tool-loop 首次/新 attempt 外发之前 MUST 正常确认该 attempt 的冻结请求、身份与 Started。重开已有完整 Started 但没有结果时，以实际应用 phase 走 backend 查询、幂等或显式 uncertain 策略；不能从 ReadRef 推断旧调用曾 Confirmed 或外部操作未执行。当前 ProcessCrashOnly 模型不要求为每次恢复再追加同值 Started；更强的“每次 dispatch 取得本轮新 flush”政策由应用明确选择，不新增存储层自动恢复动作。结果 phase 已成立时不再次执行或结算同一操作。

## 候选合同

### spec [S-INTEGRATION-PUBLIC-BOUNDARY] 集成通过 public API

新消费者 MUST 用两个新库的 public API，不能通过 InternalsVisibleTo、旧 EventJournal 类型或业务库引用补齐缺失能力。
如果失败暴露前序合同缺口，应回到相应 S1–S5 修订/复验。集成层不复制开销公式、创建第二个根权威或绕过耐久协议。

### spec [R-INTEGRATION-CRASH-EVIDENCE] 进程中断验证真实组合

资格 MUST 覆盖提前地址签发、交错构建各帧、必需首帧 header 初始化/校验与 active 发布、归档 flush/close/rename、多个 active 恢复、data barrier、ref 私有容器初始化与目录发布、命名 fork 的附加绑定及共同发布、ref 快照追加/flush、tag 桶初始化/追加、既有 ref 的 alias 文件发布、库内投影及应用状态安装窗口。补成功 EndAppend 自动归还、数量 config 超限拒绝与配额计数；未来多线程优化另行取得资格。不测试首版不存在的 ref 轮转或 Commit/control 日志。
使用子进程中断/真实磁盘镜像验证冷重开，并保留源 commit、SDK、平台、阶段和预期/实际结果；单元 fault injection 不能冒充进程中断实证。
恢复结果只能来自完整 checked 快照和正式发布的文件/名字。已有 ref 更新中断后只能得到完整旧字典或完整新字典，不混搭 Key；CreateRef 中断后则为未创建或已正式发布的完整初始 ref，不补造空字典。完整坏快照不回退。Unknown 重开读取实际状态，不承诺精确尝试的 Present/Absent，也不从同值字典倒推旧调用曾确认成功。
命名 fork MUST 覆盖所有私有文件创建/flush/close、同卷目录 rename 前后及 Confirmed 后安装失败：普通名称查询与 ref 查询共同不可见或共同可见，私有残留不公开 unbound ref、不自动续作，readonly 不清理。正式目录缺/坏必要 ref 内容或完整坏绑定报错，不按未完成创建删除；最终 rename 结果不明仍 Unknown。目录 no-overwrite/单点发布的 Windows/Linux 资格须单独记录，不以普通 file rename 通过代替。
补确定 guard 先于 data IO 拒绝、data / VersionStore 身份混用与 fault 范围、发布屏障覆盖调用时最新完成 data，以及应用安装失败不撤销 Confirmed。无关 data Builder 活跃不构成拒绝；其声明、构建内容、epoch、租借和配额在屏障后不变。数据帧写出中断与根发布中断分开裁决；完整未发布数据不自行改变任何 ref/tag。新文件私有 flush 后、正式 no-overwrite rename 前不算对象成立；rename 后调用未返回可能留下已成立对象。
public 消费另验证 out 证据在原异常/OOM及内部助手转发后仍可观察，确认后成功值/投影安装异常仍为 Confirmed；正常失败 Result 为 NotAttempted，不混同健康资格。进程终止不留下调用证据，冷重开只读实际对象。匿名创建丢失返回值不得按同值认领；tool-loop 的 Started 无结果、已完成外部操作但结果未发布、已发布结果和旧 generation 回包分别走应用路径，不由存储库盲重试或补发 Started。
组合向量包含 A 的全部依赖完成而 B 不相关仍 Building 时 CreateRef/PublishRef/CreateTag 成功、A 已发布后 B 中断/取消不撤销 A、B 所租文件中旧 dirty 依赖被 flush、B 后续 EndAppend 重新登记并须新屏障、健康取消/位置复用不赋予旧地址完成来源、任一数据 flush 失败后所有 owned Builder/Writer 停用且根不输出。真正依赖未完成 B 的 RootMap 不能发布，这是应用闭包责任，不能从数值地址或屏障成功推导库已自动验证。
同文件 Builder 活跃时，经普通指定地址随机读取此前已完成帧，配合嵌套构建验证应用行为不依赖文件分配；未完成提前地址和连同帧后 Fence 的跨边界范围先于读取拒绝。仍校验完整内容、生命周期与共享 fault，扫描/扫描边界/物理后继 guard 不变；不把本轮串行轨迹重标为并发资格。
编号 public 消费覆盖低号仍 active/高号先 archive、空最高桶/全部已归档/缺口/uint 高位、双位置和非规范错桶拒绝。创建中断后以正式位置重算 max，私有 header 不认领编号；active header-only 文件也占号。uint 耗尽时仍可复用空闲低号、读/确认/结束既有 Builder，仅需新文件时在维护/创建前确定拒绝。冷 Open 测量全部正式名称枚举成本；它不构成 archive header/内容审计或两平台 rename 已取得资格。
正式路径 public 消费按 S2 的独立文本向量验证 6/8 位小写 hex、1024 边界/slot=0、高位/最大编号和桶范围；发现与随机定位一致，归档保持 basename/header/地址不变。非规范大小写/宽度/后缀、错桶/层级、隐藏未知项和错误 entry 类型不得被过滤或规范化接纳。实际固定组件拼写、普通类型/no-follow 的两平台资格与 metadata 成本另取证，不以字符串 codec 检查替代根独占、同文件系统或 rename 验收。
格式门 public 消费使用 S2 已定 raw 24B 独立向量，验证完整 CRC 后才认领统一版本/非零 StoreId，严格拒绝短/长/损坏/未知版/旧 marker/RBF 门；可写与只读 Open 均不改门，缺坏门即使旁边 data header 合法也不恢复/补造。必要 close 失败不继续 active 恢复或签发 owner。门记录的 CRC 不与数据 header 的 RBF CRC 重叠；初次建立消费 S2 `[R-FS-STORE-CREATE]`，验证空必要布局先于正式门写入、完整门出现在失败调用后仍可依实际资格重开，以及坏/短门重复拒绝；任何初始化/交付失败不删正式门或根作回滚。Create 无data文件，空扫描/确认不建文件，首个实际追加才建FileId=1；owner 生命周期锁与门前 bootstrap 消费 S2 `[S-FS-OWNER-LOCK]`：覆盖writer/reader互斥、reader共享、获锁后fresh重检、control-only复用、缺坏control拒绝、不删除/替换和fault至Dispose保留；清理全部其他owned资源后才单次关锁。Windows 28项原语探针不代替完整factory、实际根/type/no-follow或Linux严格锁资格；两平台初始化中断/资源资格另取证，不以文档闭合当实现通过。
FrameStore 私有data残留消费 S2 `[R-FS-CREATION-PRIVATE]`：正式发现后独立max+1、唯一文件/完整枚举/0..I全byte前缀先于active恢复及删除；允许合法非默认Key，不认证短前缀作者。取得必要close成功后writer只删该文件，RO原样保留且不计正式集合；unknown/错号/多项/超长/CRC或identity矛盾保留拒绝，取消中断重新裁决，不恢复或补发布。先取得 [RBF公共纯前缀入口](../Rbf/rbf-initial-frame-prefix.md) 的源码/public消费资格，再验证完整Open/取消/关闭失败/ordinary/no-follow/两平台中断；内部有界探针不是这些资格。
VersionStore 格式门消费 S4 已定独立 raw 40B 向量，两身份/CRC/未知版/长度严格检查；CRC 合格后仍必须比较实际借入 data 的持久身份。错 data 或必要 close 失败时，本次 VS 打开不恢复发布文件、不清理私有内容、不输出、不签发 owner，也不 fault/Dispose 健康的借入 data。覆盖共用 data 的不同 VS 上下文及两角色 byte 同值；缺坏门不采用邻近 header/私有门。公开身份/模式接口、初次门发布与平台资格仍需另取证，不将候选位算法或现有 Data 测试称为 VS 实施证据。
RefId 消费 S4 的固定内部 8B 独立向量及公开上下文值，不通过反射/公开数值构造补齐 API。验证完整上下文等值/hash、default 前检、同 VS 重开旧值可用，以及共借 data 的不同 VS/local 同值误传在 Read/Publish/History/alias 的 I/O/barrier 前拒绝，mutation 保持 NotAttempted。字段编码不重复 VSID、不外泄坏宿主值，checked 来源元数据不成为源成员证明；不把逻辑 24B 身份大小当作 CLR ABI 或宣称已提供外部匿名 RefId bookmark codec。发号消费 `[S-VS-REF-ID-ALLOCATION]`：验证包含匿名ref的正式max、丢失返回值/rename异常后的冷发现、私有候选不计max但必须经过残留资格，以及耗尽只先于屏障/输出拒绝新ref；高位/Max用内部或正式格式fixture取证，不增公开数值构造。记录可写冷开O(ref数量)发现成本；正式路径消费 `[F-VS-REF-PATHS]`，独立文本及拒绝/定位向量在新库验证；实际组件拼写/类型/残留、中断与平台成本仍单独取证。
RefRevision public 消费只从正常发布/checked 当前与历史取得值，验证完整等值/hash、同字典重发/子初始与源不同 revision、default 属性/比较合法而输入前检拒绝、不同 VSID 拒绝先于 I/O/barrier 且 NotAttempted。保存值结束查询并关闭/重开同 VS 后仍可 fork，实际重读/确认不省略；继承源的 RefId/Ticket 可直接关联 ListForks 的 exact 声明点，位置匹配不等于源内容健康。无公开数值构造、反射或元数据转 revision 的绕行；不把只读投影称为完整 codec、跨进程导入、防伪或旧调用耐久证据，未来分段观察合同另审。
RootMap/RefSnapshot public 消费按 S4 `[A-VS-ROOTS-OWNED]`：用真实 public 地址组成普通字典输入，三操作取得非 null 自有结果，新 RefId 从 Revision.RefId 读取；输入后改、结果 map/Keys/Values 的常规集合接口不能修改已保存快照或内部初始字典。对不同插入顺序、Ordinal A/a 及同字典不同 revision 分别验内容与身份；历史项在查询结束/关 owner 后仍可保存，并交给合格的同 VS 重开 owner 作 tag/rewind。输入捕获失败先于 barrier/输出且 NotAttempted，无新增 fault；输入稳定/不重入是串行前提，既有 HistoryActive 另验。确认后结果分配/安装失败仍保留 Confirmed，不用引用 Equals/编码顺序替代来源内容检查；RootMap wire/限额按已定合同实施验证，集合探针不代替新库和性能资格。
RootMap codec public 消费按 S4 `[F-VS-ROOTMAP-BPV1]` 的独立 golden 与真实 12B 地址，覆盖全部 code-unit key、宽容字符串/冗余前缀、解码后重复 key、count/声明长度及地址 suffix 的分配前拒绝、1MiB 等于/超限、宿主后字段/trailing 和 cold history/tag 复用。超限先于 data barrier/output 且 NotAttempted；容量按 actual consumed，而非 Measure(decoded)，完整坏末 map 不回退。后续包验证须核对 VersionStore 复用 Binary 与精确 K4os 的实际闭包；组合探针不是 FrameAddress/VersionStore 实施、恢复、平台或包证据，峰值/RSS 与默认适用性另测。
ref 宿主 public 消费按 S4 `[F-VS-REF-FRAMES]` 的28/44B header独立golden、0/1 kind、meta0/非墓碑与Snapshot纯RootMap；验证期望版本/VSID/自身身份、来源Packed LE及必要坐标、首两帧物理成员/full CRC/exact-consume和整帧尺寸前检。来源元数据不签发revision，不借源健康保证本地当前值。可写恢复前初始化保护尚未定稿，112→104→112→再次None反例必须单独关闭；既有完整初始化之后的合法更新残尾与异常正式初始缺尾分开验收，不以synthetic公面探针或事后report冒充新库Ready。
public 消费另验证 FrameRead 在 reader/owner 关闭后仍可用、独立 Dispose 与未移交结果失败清理。同步 Inventory/Audit 的纯值回调、header 报告、结构/完整 CRC 区别、mutation 准入先于维护、回调随机读/Dispose、取消及全部退出清理直接消费 S2；只有全部实际集合检查与必要关闭成功才有全库完成资格，不将它升级为业务图或未知历史文件存在证明。
首次 tag 桶的 header-only 初始化覆盖 flush/close/空桶 rename 故障及进程中断；尚未尝试 tag Append 时本次 tag 保持 NotAttempted，重开允许合法空桶但不存在该 tag，不清理已正式发布的合法桶。CreateBranch 单独验证只确认绑定文件、不调用 data 屏障，无关 data Builder 未结束不阻断纯命名；匿名/命名 fork 都消费完整 RootMap 屏障，A 依赖完成而无关 B 仍 Building 时组合创建可以成功。
tag public 消费按 S5 每次完整校验目标桶，不依赖跨调用缓存。覆盖首帧初始化 I 前检与重复打开不认领半 header、首 tag 两种合法残尾恢复、命中后坏非目标 RootMap/重复全名/错桶/墓碑/TerminationError 的失败、必要 close 失败，以及查询返回的自有 RootMap。统一 header 版本与两种 FrameTag 消费基础 codec，不增加 tag 专用版本/CRC；测量重复解析和连续同桶创建成本，故障与恢复测试不替代性能或平台 rename 资格。
名称向量覆盖跨不同 ref 的全局同名拒绝且先于 barrier/输出、初始绑定与 alias 同一表示、同 ref 多 alias、绑定父容器/目标身份错误、hash 碰撞、坏 CRC/codec 和目录枚举失败不当 absent、重复 fullname 不 first-wins。standalone 未命名 ref 仍可列举，手工 CreateRef+CreateBranch 第二步失败保留第一步，alias 输出异常不撤销既有 ref/原名称。补大量未命名 ref/少量 branch 的冷发现成本，以及若采用内存表，其完整建立/正常结束和 Confirmed 后安装失败。
容量边界分别验证单帧尺寸与起始 offset：ref / tag 桶最后合法记录的末端和尾 Fence 可以越过 SizedPtr.MaxOffset，随后追加在输出和额外 data barrier 前确定拒绝；不拿末端越界当作既有记录损坏。FrameStore 同样遵守起点规则，成功追加后按软阈值停止分配并归档。裸 FrameAddress 的原始 store 来源由应用保证，错误 data owner 的格式门绑定则必须在恢复/写入前由库拒绝，二者不能合并成自动来源检测能力。
软阈值 public 消费使用 S2 的唯一 Create/Open 参数，以小 T 验证 header-only、三种追加等于/越过、取消、实例固定及升降阈值重开；非法范围先于文件系统访问拒绝。分类必须消费恢复后的完成 tail，覆盖截短变小/补尾变大；尚未移档 active 可在提高阈值后继续，archive 不重新启用。默认 64GiB 仅为工程起点，参数不扩 Builder config、门/header 或地址字段；这些向量仍待新 FrameStore 实施，不由现有 RBF 测试代替。
同步历史 visitor 向量 MUST 覆盖固定上界、递归/全部 mutation 的 pre-I/O 拒绝、调用后自有字典继续使用、重复相同字典的不同 revision、损坏与 TerminationError。覆盖第 k 项正常停止不再读取旧坏记录，MaxSnapshots/MaxWorkSteps 的资格/定位/完整读计费，恰好到无来源初始时 Complete、有来源而额度不足时专用预算失败。最后回调 Dispose owner、取消或抛异常及最后临时关闭失败不得返回正常终止；跳转/所有退出归还一次并清 guard。普通当前值读取不自动审计全部历史；显式历史传播已观察的必要错误，VisitorStopped/预算失败不称未访问前缀健康。
来源感知 fork 另覆盖源真实成员重读、data 屏障后源 ref flush、私有 ForkOrigin/初始 Snapshot/名称共同发布及每个中断窗口。源 flush 失败时不输出子容器，源 head 不增加孩子登记。跨文件历史包含子初始与 exact 源点的不同 revision，覆盖 fork-of-fork、源后续更新不混入、rewind 发布史、缺源/错 length/循环/初始字典不符、后缀定位预算与错误、栈内扫描器切换与临时句柄清理。
ListForks public 消费 `[A-VS-FORKS-CHECKED]` 的正 int maxRefs、自有 ForkInfo/只读列表及单次完整扫描：必须包含匿名与命名、多层及兄弟分叉，alias 不新增边、无来源同值复制不推断来源、creating 排除。全部正式节点包括无来源根计费；零/负限额先于 I/O 拒绝，恰好额度且正常 EOF 可成功，超额节点在打开/读取/扩表前专用失败。完整首两帧、集合及源存在/迭代环检查、必要关闭与最终检查成功后才交全部关系；缺源/坏末 ref/环/关闭失败/取消/分配失败不能交部分或空成功。深链/共享祖先不递归或二次方重走；验证正常集合接口不能改变保存结果，后续查询/owner 关闭仍有效，来源保留 full ticket 而不签发 checked revision，顺序无保证。历史 visitor 内查询复用匹配句柄且不覆盖历史临时槽，其他 owned 资源只清理一次。声明边不重验所有源 payload，实际历史跳转则必须校验，两种资格分别验证；数量预算不替代 raw factory 初始化保护、平台或物理内存/时间资格。普通 ReadRef 仍只检查本地必要内容。
public 消费复用 S2 FrameAddress.EncodedSize/TryWrite/TryRead，在 Begin 前计算自引用/双文件互引的 stored 尺寸，核对 Begin/End 编码一致并冷重开读回；RootMap、ref 历史与 tag 均复用同一 12B 地址格式。独立 bytes 验证完整 FileId/Packed 高位、公共数值下界与最大起点/长度、精确 slice/失败不推进、TryRead 失败 out=default 和 TryWrite 失败无写入/成功只改前 12B；header 坐标保持中性数值资格，裸值不证明真实帧。验证 default 可比较/哈希但在读取 I/O 前拒绝、成功追加后的可信构造无新增失败路径。不把内部 struct 大小或 native ABI 当成 wire 长度；目标运行时成本与 codec/包消费分别记录，旧地址/Serialize 编码不自动认领为兼容输入。

### spec [S-DELIVERY-PACK-OWNER] 包入口有单一负责人

新增生产包清单及 pack 顺序 MUST 只在 `eng/Pack.ps1` 注册。当前 main 仅 Primitives/Data/Rbf，后续加入 FrameStore/VersionStore。不得重新将冻结参考 EventJournal/RbfSegmentStore 加入 main pack。
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
| S6-Q3 | 两平台恢复与目录/文件发布分别资格、资源预算、全局名称发现规模成本及证据保存路径 |
| S6-Q4 | 若纳入真实接入：DurableGraph 实际 API/数据需求和切换范围；不阻塞独立库/包核心 |

最低出口：两个新库的 Accepted 源码闭包、组合恢复资格和隔离公共包消费证据。
公开发布或真实消费者切换若未纳入当前工作，交付记录明确停在哪一层；不将库可用与已切换混为一个状态。
