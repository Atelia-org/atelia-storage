# EventJournal v2：task 级实施与调度工单

日期：2026-09-29。状态：**实施中；T00–T03 Passed；T04 待派发，依次放行任务**。
基线：`bb7c4fb3eb6477783c70ee61bc62b832be195d07`。设计基线保持不变；用户于本轮明确授权按本工单完整实施并按需 Git 提交。网络发布、真实数据迁移和兄弟仓修改不在范围内。

目标：保持 Event/Parent、exact RefId、CAS、reflog、不可变 tag 语义；固定当前工作集时，日常打开和 ref 更新不再扫描累计历史。新格式拒绝旧目录，完整校验独立离线执行。

设计依据：[综合方案](bounded-online-io-design.md)；取舍证据：[辩证审查](bounded-online-io-review.md)；本轮已形成的[合同冻结附件](bounded-online-io-contracts.md)；[可粘贴的源码阶段Goal](GOAL-bounded-online-io.md)。本工单管理执行顺序与验收，不复制全部设计理由；T00确认合同与基线一致后，附件成为后续任务的共同输入。

## 1. 权威、授权与起始状态

| 内容 | 来源及权威类型 |
| --- | --- |
| 日常不全历史校验；离线 toolkit；允许破坏格式，现有唯一数据另行迁移 | 当前用户明确决定 |
| 坏尾停维，首版仅 audit/健康事实索引重建，不含修复 | 本轮澄清的用户明确回答“沿用该取舍，保持本次范围” |
| 按既有方案形成 task 工单，普通实施交给 gpt-6-sol，设计难点可用 gpt-6-astra | 当前用户请求及本轮 AGENTS.md 指令 |
| 尾读、locator、catalog预算/缩容、cache减法等 | 用户指定用于制定实施计划的目标方案；具体合同冻结是 T00 的有界设计任务，不是已实施事实 |
| 实际API、格式、测试、依赖与打包能力 | 当前源码/脚本/工具读取证据 |
| 文档中的候选命令、状态标签或agent说明 | 导航/证据；不能自行产生实施、提交、发布或迁移授权 |

先遵守实际 system/developer/user/AGENTS 指令层级。未来只有用户明确启动实施任务或粘贴实施 Goal 后，才执行对应源码任务；本文件不自我授权。

起始已有变更必须保留：`src/EventJournal/README.md` 修改；`bounded-online-io-design.md`、`bounded-online-io-review.md` 未跟踪。本轮另加本工单及 Goal 文档，并对设计记录用户确认。后续执行者重新记录实际status，不能用stash/reset/clean/checkout或打包需求消除这些变更，也不能把它们未经授权混入提交。

环境读取：SDK `10.0.201`、`pwsh` 在当前Linux可用。未构建、未测试；Windows结果不存在。下面的调度表是唯一工作状态；验证证据追加于本文件，避免平行进度清单。

本轮工单校核：gpt-6-astra已收口合同候选，gpt-6-sol冷接手检查指出的snapshot初始化依赖、toolkit schema、第二个package smoke入口三项均已修正并复核闭合。已验证文档本地链接、JSON样例、5个独立CRC算例和manifest样例hash/length；未验证C#实现或实际包消费。Goal正文实测1714字符。

## 2. 完成边界与调度表

分两层交付：

- **源码候选**：T00–T07完成；当前平台验证通过，所有未运行平台/包门禁如实列出。这不等于v2已可交付给消费者。
- **完整v2交付门**：源码候选、支持平台证据及T08包消费证据全部通过。只有一个v2对外交付门；T08完成仍不授权网络发布或真实迁移。

| Task | 能力/产物 | 前置 | 默认执行者 | 强审阅点 | 初始状态 |
| --- | --- | --- | --- | --- | --- |
| T00 | 核验已冻结wire/API/error/fault及toolkit输出合同 | 读基线与设计 | gpt-6-astra | 调度主线程按反例核验 | Passed |
| T01 | RBF边界起扫与确定性尾帧读取 | T00 | gpt-6-sol | 不读prefix、不吞短尾 | Passed |
| T02 | locator生命周期、严格打开、轮转/fault | T01 | gpt-6-sol | gpt-6-astra审发布窗口 | Passed（Linux；Windows Pending） |
| T03 | EventJournal格式门、尾读sequence/ref、tag局部验证 | T02 | gpt-6-sol | gpt-6-astra审身份/故障语义 | Passed |
| T04 | catalog snapshot、有界suffix、缩容与发布 | T03 | gpt-6-sol | gpt-6-astra审预算/三态 | NotStarted |
| T05 | 有界ref entry与单一ForwardPlan缓存 | T04 | gpt-6-sol | 调度主线程检查强引用/Dispose | NotStarted |
| T06 | 离线audit与健康事实索引候选重建 | T04，集成前T05 | gpt-6-sol | gpt-6-astra审重建拒绝边界 | NotStarted |
| T07 | 故障/规模证据、消费者合同与源码收口 | T05、T06 | gpt-6-sol | astra一次跨层终审 | NotStarted |
| T08 | 干净已提交来源的包消费与平台交付门 | T07 + 单独提交/打包授权 | gpt-6-sol | 调度主线程核验manifest/版本闭包 | AwaitingAuthority |

默认按表串行实施，不自动并行修改同一仓库。T06仅表示可以提前准备只读测试资料，不自行派出与T05并行的写入worker。每个task可分为下列子步骤，但主线程只在整项验收完成后放行下一项。全量.NET build/test/pack始终串行。

模型使用规则：明确的编码/修测试/事实调查用sol；T00与协议反例裁决用astra。普通测试失败先由sol定位，不因一次失败就升级。只有出现本工单列出的合同冲突，或局部修复需要改变持久顺序/公共语义时升级，并附最小失败轨迹。允许astra接管T02/T04的困难内核，但不把整个后续实现一起升级。

## 3. 每次派发必须携带的任务卡

```text
任务：Txx及具体子步骤；执行模型：gpt-6-sol / gpt-6-astra。
仓库与基线：实际HEAD；起始dirty paths及已接受的上游变更。
必读：实际AGENTS、根README、本工单、目标设计相关节、T00冻结合同。
已完成前置：Task状态、测试证据路径、对外符号/格式。
允许修改：该Task列出的范围；共同文件的接口边界。
结果与不变量：按Task验收；不得自行改wire/error/发布顺序。
验证：本Task聚焦检查，结束后报告实际diff、命令、结果和未运行项。
停止条件：出现合同矛盾先给具体轨迹；不隐藏fallback、不扩架构。
权限：只执行本次明确授权的工作；不提交/发布/碰真实数据。
```

`spawn_agent`优先继承完整上下文；需要指定模型而当前工具不允许`fork_turns=all`同时override时，使用明确模型加自包含任务卡/文档路径的fresh上下文，不靠默认继承猜测模型。不可把没有给worker的聊天内容当验收合同。

worker返回：改动文件/符号、满足的行为、真实测试命令与结果、未运行项、剩余风险、是否需要下游适配。主线程读取diff和失败证据再更新状态；不只接受“已完成”文字。一次astra审阅可覆盖刚集成的T02/T03，减少重复加载上下文；T04和T06各自发布边界仍须有明确结论。

## 4. T00——将设计合同冻结到可施工粒度

**范围**：核验本轮已形成的 `docs/EventJournal/bounded-online-io-contracts.md`，补独立字节向量及基线差异结论；不重新从候选架构开始，不写生产实现。读取 `IRbfFile`、forward/reverse enumerators、`RbfFileImpl`、`IRbfSegmentStore`/lease、三类ref/tag codec、AteliaResult、当前故障测试及包脚本。未来HEAD未变化时复用本轮结论，仅补尚未执行的门禁，不重复全仓调查。

**T00.1 数据合同**：附件已给三个新记录完整offset/width/endianness表、magic/version/reserved规则、CRC覆盖和空值编码；核对snapshot的排序、计数/长度上限、empty anchor、boundary前frame见证、record长度先检验再分配。沿用现Event/ref/tag帧与身份；不得新增journal UUID、head checkpoint、generation搜索或目录fsync平台。发布文件和临时文件的职责必须唯一。

**T00.2 API/error合同**：使用附件冻结的boundary-start scan签名、尾读空/坏/正常结果shape；现有无参数ScanForward不变。核验Open抛异常与AteliaResult错误的映射、FormatUnsupported/MaintenanceRequired可程序识别的区别、fault后调用与Dispose行为。避免只约定异常message文本；不能新造跨仓通用异常框架。

**T00.3 publication/fault合同**：逐项给出Create/Fork/Archive/Move/Tag/checkpoint/rotation的预检、分配、durable、内存安装及失败后状态。尤其checkpoint在本次发布前，NotAttempted/Unknown/Confirmed不可混淆。分清EJ捕获业务写异常、SegmentStore捕获自己执行的rotation/locator/ConfirmDurable异常；lease.File直接暴露IRbfFile，不能假称SegmentStore自动拦截外部Append异常。若新方案需要扩大底层fault范围，必须给具体必要性并由astra裁决，sol不得自行套代理层。

**T00.4 toolkit合同**：核验附件的audit覆盖/退出码/内容安全报告、候选清单与拒绝条件；索引重建是候选输出而非原地替换。生产包不反向依赖toolkit。按附件将可复用扫描/codec helper留在所属库内部，通过显式friend assembly给非pack工具使用；不得为工具开放一组任意修改原始存储的公共API。

**通过证据**：合同逐字段无TBD；至少列出empty journal、unborn ref、非空head、1 branch+1 tag snapshot的手算/独立脚本格式见证，包含CRC算式和预期拒绝变体（后续T01–T04实现测试负责验证）。对坏短尾、old locator+next、snapshot超Q、百万→1缩容、tag flush后安装失败各走一遍状态轨迹。源码证明AteliaResult可容纳所选返回类型；所有public新增字段/方法列清依赖影响。

**gate-owned决策范围**：astra可在现有类型体系内选择一个窄API形状、字段偏移、错误名称与同卷原子替换实现候选，并以源码/小型独立probe/失败轨迹证明。不能改变用户已确认的停维政策、唯一事实源或首版范围。候选都无法满足合同则标注具体未决点，不把T00标Passed、不派发依赖任务。此门禁是明确分配给astra的设计工作，不能交给sol在编码时猜。

## 5. T01——RBF边界读取基础能力

**允许修改**：`src/Rbf/IRbfFile.cs`、`RbfForwardSequence.cs`、`RbfForwardEnumerator.cs`、`RbfReverseEnumerator.cs`、`Internal/RbfFileImpl.cs`与相关read helper；`tests/Rbf.Tests/Internal/RbfScanForwardTests.cs`、`RbfScanReverseTests.cs`及新聚焦测试；`docs/Rbf/rbf-interface.md`。不改RBF帧wire或恢复scanner的离线能力。

**步骤**：实现T00选定的boundary-start入口，复用dataStart；实现/复用只读取紧贴EOF一帧的能力；保留Tombstone通用语义，由EJ拒绝不允许的业务帧。必要观测只提供内部计数/测试probe，不记录payload或成为新public telemetry。

**验收**：4B合法空文件；5B、8B等短尾失败；对齐但非frame boundary失败；精确EOF起扫为空；有效boundary之后只读suffix；损坏的无关prefix不被偷偷扫描；坏末帧不跳过；tombstone显示/过滤合同不退化；read failure不改bytes。验证点落在实际reader调用，不能只给上层计数加一假装证明I/O。

**验证**：先build `tests/Rbf.Tests/Rbf.Tests.csproj` Release，再同项目`dotnet test -c Release --no-build --no-restore`。输出聚焦case与原扫描测试结果。T01完成只代表新读取原语，不代表EventJournal已改造。

## 6. T02——RbfSegmentStore定位与轮转

**允许修改**：`src/RbfSegmentStore/RbfSegmentStore.cs`、`RbfSegmentStoreOptions.cs`、路径/lease helper、新locator codec及同目录发布helper；`tests/RbfSegmentStore.Tests`；本层README。T00若决定helper可供EJ复用，保持依赖方向EJ→SegmentStore，不让SegmentStore引用EJ。

**步骤**：Create完成segment1+locator；Open在任何write前验证locator并定点probe next；去掉在线目录全枚举及自动recovery选项；rotation顺序固定为old flush→new empty flush→locator replace→允许new append；实现本层owned-operation fault与完整资源释放。

**验收**：Flat/Bucketed都支持；缺locator旧目录拒绝且bytes不变；未知版本不落盘；old+next即停维；active空只允许合同定义的情况；缺被访问segment报错，无全目录扫描；SegmentNumber耗尽不wrap。异常发生在每个发布阶段时，句柄/lease可Dispose且不能继续危险写入。保留`ConfirmDurable`不轮转、live lease保护与历史reader池行为。

**旧测试迁移**：`OpenExisting_RecoversTornActiveTail`改为严格拒绝；目录全库存非法名/缺口检查转T06 audit，保留打开所需locator/active/next局部检查。不能简单删除坏数据覆盖。

**验证与强审**：本层Release build/test；astra审阅rotation故障矩阵与fault所有者。Windows/Linux替换语义用实际文件/进程中断case验证，单平台不可冒充双平台；未有另一平台证据写PendingPlatform。

## 7. T03——EventJournal局部打开和末态读取

**允许修改**：`EventJournal.cs`、`.Refs.cs`、`.Tags.cs`、`RefMoveStore.cs`、options/errors、新format gate/helper、`tests/EventJournal.Tests`。

**T03.1**：先按附件实现共享catalog snapshot纯codec（包括empty与live表的编码/流式解码及独立单测），并在Create初始化写入durable empty snapshot；根format最后发布。这里尚不接入快照恢复/周期checkpoint，由T04消费同一个codec，不能让初始化依赖后续任务。已有未知/legacy目录在可写open前拒绝；`ComputeNextSequenceNumber`改为active或唯一前段末event恢复，序号递增溢出拒绝；中途创建失败不得把旧目录当新目录重建。

**T03.2**：`LoadRefState`改为allocation直读、Init校验、末move局部语义及仅当前target checked-read；保留Close/unborn区别。跨segment、rewind之后的head、同名新RefId、fork来源与CAS全部不变。保留`ReadReflog`显式全量入口，但普通open/head不调用它。

**T03.3**：删除constructor全tag target验证与只读open全部ref急切加载；ResolveTag仍checked-read自己的target；实施T00的EJ fault latch和Dispose清理。保留CommitToRef现有“CAS失败留下orphan”语义，不顺手改成事务或提前CAS的新行为。

**验收**：无关中间event/ref历史损坏而本次必要记录良好，daily读取成功、T06 audit失败；当前head/Init/allocation/末event等必要记录坏则对应读取失败、不回退。tag目标坏不阻止无关open/resolve，解析该tag失败；尚未访问的ref坏尾不要求open立刻失败，但访问该ref必须失败。old reader测试与坏历史测试按新合同明确重归属。

**验证与强审**：EventJournal Release build/test；astra检查最后move局部规则、关闭/未发布对象、tag三态和fault后的全部数据入口。此时catalog仍可临时全量replay，状态仅T03Passed，不宣称性能目标已完成，不发布中间版本。

## 8. T04——Catalog快照和有限后缀

**允许修改**：EJ新的catalog codec/reader/writer；`.Refs.cs`、`.Tags.cs`的catalog更新入口；相关tests。不得分段/压缩ref-op-log或改变RefId。

**T04.1**：接入T03已有的快照codec与实际catalog状态，补真实日志anchor/重复或非法字段的集成验证；不再复制一份序列化实现。快照只保存active names、所有tags；不保存heads、archived全集或历史knownRefs。

**T04.2**：Open读snapshot后直接seek suffix；allocation用RefId ticket直达；reader硬限Q及record bytes；任何坏snapshot/超额旧snapshot/坏suffix都返回维护错误，不从零扫描、不忽略未知record、不错误返回TagNotFound。

**T04.3**：实现预检与发布。`Q=max(1024,L0)`；若`d+r>Q`或`L0>2*max(1024,Lnext)`，先对操作前已发布状态checkpoint，再执行业务。Create/Fork预留2条，Archive/tag预留1条；容量预检覆盖完整操作。快照前先确认op-log durable；不在业务已Confirmed后做强制checkpoint；失败后不继续用旧缓存写入。

**验收**：Q−1/Q/Q+1与每个r边界；L0刚好2倍与刚超过2倍；unborn/tag/branch同名；snapshot损坏、boundary>EOF/错anchor、声明巨型record；checkpoint后崩溃重开；Create已分配但未Bind；Close已写未Archive。输入无效不触发checkpoint/新业务写入；前置checkpoint I/O异常时tag=NotAttempted；业务flush后内存安装异常仍Confirmed。

**性能证明**：固定当前live下反复churn，重开只访问snapshot+suffix；另测先大live再降至1，不把旧峰值藏在snapshot中。原审查计数模型的百万→1结果仅是对照，不能代替真实codec/文件测试。

**验证与强审**：EventJournal tests及内部计数；astra只审核预算算术、持久顺序、Unknown处理、跨进程重开证据。任何wire/error变化先回T00，不能让T04自己发布第二版草案。

## 9. T05——移除自动历史缓存积累

**允许修改**：`.Refs.cs`的ref entry拥有关系、`RefMoveStore`生命周期、`.ForwardPlan.cs`及相应tests/options。

**步骤**：一个有容量上限的ref entry LRU同时拥有state/store；evict/Archive/Dispose释放；维护内存RefId→name反查。保留memory exact-head cache与cached-prefix reuse；删除compiled disk cache codec/load/save及ref-local binding/tail-merge，不新建替代的强引用字典或持久route DAG。

**验收**：访问ref数超过capacity、容量边界、archive/recreate/evict后冷读同一真实head；存储句柄数量和常驻引用受预算约束；Dispose即使单项失败也尝试释放其余。读写后不存在新cache文件；branch交错、orphan、跨segment、rewind的全量读取结果与authoritative Parent一致。删除disk-hit/tail-merge-hit机制测试，保留其覆盖的遍历正确性case。

**验证**：EventJournal tests；独立记录cold full replay的额外Parent walk与O(N)临时空间，不通过放宽语义或把缓存预算说成进程总内存上限来通过验收。

## 10. T06——离线toolkit

**计划新增**：`tools/EventJournal.Toolkit/EventJournal.Toolkit.csproj`、`tests/EventJournal.Toolkit.Tests/EventJournal.Toolkit.Tests.csproj`，加入solution；`IsPackable=false`，不加入`eng/Pack.ps1`生产包列表。T00冻结内部helper共享方式；不依赖atelia业务仓。

**T06.1 audit**：从只读RbfFile和目录事实开始，不调用daily open；按T00覆盖目录、event framing/CRC/codec有效性、Parent与物理sequence、全部ref-op/tag记录、全部move链及目标。一个坏对象应能报告精确位置，不能吞掉错误后输出整体Passed。报告包含覆盖范围、计数、错误码和完成状态，不输出payload；取消或I/O中断是Incomplete，不是healthy。

**T06.2 rebuild-indexes**：仅健康且唯一可解释的事实生成新目录中的locator/catalog候选及来源manifest；create-only，不碰source；候选完成标记最后发布，失败产物不可被当成完整修复。源不变由前后文件摘要核对，输出也记录hash。不得截断、补Archive/Bind、选择较老head、猜测空next的归属或实现旧格式迁移。

**验收**：缺catalog/locator但事实可验证时audit仍能运行；坏尾/缺段/重复tag/非法move序列/多义active候选必须失败；健康候选在测试副本安装后daily读取一致；源树bytes/hash不变；只读挂载audit；输出已存在/位于source内拒绝；无真实实例操作。astra审阅“可重建”与“必须拒绝”的分界。

**验证**：新增toolkit test项目build/test，随后全solution。新CLI参数、退出码与报告schema应在T00固定，不能以目前项目不存在为由现场发明宽泛repair命令。

## 11. T07——集成、成本证据与源码候选收口

**范围**：三层及toolkit集成测试、必要内部计数/失败probe、README/规格更新、两个public smoke示例、`eng/Test-Package.ps1`的下述窄验证扩展、复现脚本与精简证据摘要。不增加第五种业务组件或新production包。

**T07.1 成本矩阵**：按设计§12分别放大move数、event/orphan数、catalog churn、先扩后缩live、segment数、tag数、cache访问集合；保存真实操作计数、I/O bytes、句柄、分配以及计时分位数。标准聚焦tests覆盖小规模边界；10^3/10^5/10^6数据在显式长测脚本中执行，分开fixture生成和测量。可用codec生成合法大fixture以隔离读成本，但必须和小规模public写路径对照，报告合成方式；不能关闭生产DurableFlush后把结果冒充写延迟。

**T07.2 故障矩阵**：append/flush/rotation/replace各阶段的进程退出与异常；只读源hash不变；清晰区分test probe、真实句柄失败与进程kill。测试只针对临时目录。不同OS文件发布语义分别取证；无法运行的OS记录PendingPlatform。

**T07.3 消费者合同**：本仓smoke覆盖Create→branch/ref/tag→rotation→strict reopen；只读核查兄弟SessionJournal中recovery选项、exact RefId、selected-chain validator及rewind入口，输出适配清单。兄弟仓源码改动/包版本升级需单独明确纳入授权；本Goal不自行修改它，也不运行其真实数据。现有领域selected-chain audit保留，全storage audit不成为每次rewind前置。DurableGraph实际项目未定位则明确未验证，不以旧文档代替实证。

**T07.4 收口**：运行下述全套Release检查；逐项映射设计到测试，更新根README、两层README、RBF接口规格、ref/tag/ForwardPlan旧设计的现行状态，不保留两个相互冲突的当前合同。包依赖准备必须包含本次新RBF API；记录源码候选与尚未满足的包/平台/消费者门禁。

**T07.5 包验证入口准备**：为`eng/Test-Package.ps1`增加明确的`[switch]$AdditionalSegmentSmoke`，只接受Project=All。保留原五包manifest和EventJournal smoke/metadata验证流程；开启时再复制`examples/RbfSegmentStoreSmoke`进入该次仓外隔离workspace，使用同一明确feed/private NuGet配置与candidate version执行restore/run，核对其assets中的四包闭包及版本与All manifest对应项匹配，保留assets/日志。它是直接PackageReference消费，不能改成ProjectReference或从源码目录run。不要改变Pack、manifest schema或selective模式；非All携带该开关为参数错误。用合成manifest/命令调用测试验证脚本分支，真正包消费仍由T08执行。

**通过**：所有当前平台的in-scope行为和失败证据齐备，实际diff无无关变更；astra跨层终审没有未处置的语义问题。当前平台源码候选可标Passed；完整交付状态必须继续显示T08或平台未验证，不能写“全面完成”。

## 12. T08——独立授权后的完整交付门

本任务不由本轮写计划请求或默认源码Goal授权。开始前需要用户授权提交/打包，来源checkout干净且已提交；保留并解释此前dirty文档，不能自动合并进commit。

**已核对的脚本约束**：`eng/Pack.ps1 -Project`目前只支持EventJournal/RbfSegmentStore，并从nuget.org取得它们的已发布依赖。本次新增RBF API，所以不能将EventJournal单包candidate配旧RBF依赖。使用现有**五包同版本地candidate**模式验证此次闭包，不扩建选择性发布基础设施、不覆盖既有版本。五包的顺序仍只由Pack脚本定义。

**包证据**：使用T07新增的`AdditionalSegmentSmoke`开关运行All模式，一次隔离验证同时取得EventJournal的全闭包manifest/PDB Source Link证据和直接SegmentStore的四包assets/API smoke证据。不要拿All模式的五包manifest冒充已生成的schema2 selective manifest；开关只扩验证流程，不改变包清单/发布算法。

**完成**：候选版本、source revision、所有包hash、独立消费assets、支持平台结果和本次授权范围均可复查。没有Windows实证就不能宣布Windows原子替换/坏尾门通过；没有实际消费者接入就不能宣布它已升级。网络发布、真实迁移、部署始终另行授权。

## 13. 验证命令与结果记录

当前仓库已确认的入口（task局部build选择其test项目，solution用于集成）：

```bash
dotnet build tests/Rbf.Tests/Rbf.Tests.csproj -c Release -m:1 -nr:false
dotnet test tests/Rbf.Tests/Rbf.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet build tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj -c Release -m:1 -nr:false
dotnet test tests/RbfSegmentStore.Tests/RbfSegmentStore.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet build tests/EventJournal.Tests/EventJournal.Tests.csproj -c Release -m:1 -nr:false
dotnet test tests/EventJournal.Tests/EventJournal.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false
dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false
dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false
git diff --check
```

T06新项目在创建并加入solution后才运行其build/test；新性能脚本的路径、参数、数据规模及随机种子在T07产物中固定，不在这里伪造尚不存在的命令。改变源码后先匹配配置build再`--no-build`测试，不将旧binary结果算作本次验证。

仅T08满足授权、T07验证开关已落地及干净已提交来源后执行。`AdditionalSegmentSmoke`是本工单要求新增的参数，当前基线尚不存在；其余脚本参数已核对。以下PowerShell变量使用每次唯一版本/目录：

```powershell
$candidateVersion = "0.2.0-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$candidateFeed = Join-Path ([IO.Path]::GetTempPath()) ("atelia-v2-feed-" + [Guid]::NewGuid().ToString('N'))
$candidateSmoke = Join-Path ([IO.Path]::GetTempPath()) ("atelia-v2-smoke-" + [Guid]::NewGuid().ToString('N'))
./eng/Pack.ps1 -Version $candidateVersion -OutputDirectory $candidateFeed
./eng/Test-Package.ps1 -Version $candidateVersion -FeedDirectory $candidateFeed -WorkDirectory $candidateSmoke -AdditionalSegmentSmoke
```

上述`0.2.0-dev.*`是隔离候选版本方案，不是公开发布版本决定。生产中已有版本不得覆写；本轮没有执行这些命令。

## 14. 覆盖表、升级条件与全局停止条件

| 设计要求 | Task | 必须看见的证据 |
| --- | --- | --- |
| 新format拒旧、只读不写 | T00/T02/T03/T07 | pre-write拒绝、源hash不变 |
| 不扫描segment目录/event历史/reflog | T01/T02/T03/T07 | 实际读取/枚举计数随历史规模不增长 |
| catalog只按当前live+常数余量增长 | T04/T07 | Q读侧上限、缩容反例、实文件重开 |
| 末态局部语义、CAS、RefId/tag不变 | T03/T04 | exact ref、Close/unborn、tag三态与orphan |
| writer复用且缓存不永久积累 | T05/T07 | capacity/eviction/Dispose与无新磁盘cache |
| audit与健康事实候选重建 | T06/T07 | 缺索引可审计，坏事实拒重建，源不变 |
| 全量读取真实O(N)与冷cache取舍 | T05/T07 | 结果等价及冷启动成本报告 |
| 跨平台/包消费 | T07/T08 | 各平台实际结果、fresh PackageReference和manifest |

升级给astra时必须提交：Task/基线、最小输入或故障时点、当前实际行为、违反哪条冻结合同、已排除的局部原因。astra只处理该协议问题并给结论；若需要改变用户决定/事实源/恢复可用性/依赖边界/破坏范围，由主线程向用户提问。一般编译错误、测试适配、命名调整、环境缺依赖不自动变成设计问题。

源码阶段停止于T00–T07当前平台候选证据和文档收口；T08属于后续单独授权门。全部变更有说明、验证和明确去向，不要求制造干净工作树。不得把Skipped/NotRun/PendingPlatform/未获得授权改成Passed。使用环境实际Goal状态规则，难度高、单次失败或尚有工作都不是自动blocked/complete的理由。

## 15. 实施证据（2026-09-29 起）

- 用户已授权完整实施及按需提交；原有六份文档属于本任务设计工件，先独立提交以保留设计基线。
- 起始 HEAD：`bb7c4fb3eb6477783c70ee61bc62b832be195d07`；起始 dirty 仅上述设计文档与 EventJournal README 导航。
- Linux / .NET SDK 10.0.201：Release solution build 通过（45 个既有 XML 文档警告，0 error）；匹配配置全量测试 777/777 通过（Data 212、EventJournal 79、Primitives 75、Rbf 388、RbfSegmentStore 23）。
- Windows 平台门仍待实际环境证据。

- T00：astra 对当前源码启动核验 Passed；AteliaResult 支持 ref struct，五个冻结 CRC 向量及 tag anchor 复算一致，控制 frame 上限 248/252B 一致。额外 unborn/nonempty-head 向量见合同附件。5/8B 短尾、old locator+next、suffix 超 Q、百万→1缩容、tag flush 后安装失败反例均有明确处理；实现与平台验证留给所属任务。

- T07 消费者只读预查（不代表适配完成）：兄弟仓 `eng/StorageDependency.props` 仍固定 `0.1.1-preview.2` / `976aa345f923da09e2a5cf1dc25ba592b3818b63`；`prototypes/SessionJournal.Cli/BranchRewindCommand.cs:14` 三处 `RecoverActiveTailOnOpen=false` 必须在消费者升级时去除。`prototypes/SessionJournal.Offline/SessionJournalOfflineValidator.cs` 已经调用领域 `ScanCheckedAuditEvents`，保留该审计，不把 daily open 成功当全库健康。当前兄弟仓未检出 DurableGraph 项目；未修改或升级任何消费者。

- T01：冻结 boundary API 已编译，反向扫描拒绝非空短尾；实际 reader probe 证明 Off 模式不读 anchor 之前，缓存模式仅有有界相邻页预读。Release Rbf.Tests 403/403（新增 boundary 15；原 forward 9/reverse 26）；build 0 errors，25 个既有 XML warnings；`git diff --check`通过。日志 `/tmp/t01-build.log`、`/tmp/t01-test.log`，TRX `tests/Rbf.Tests/TestResults/t01-rbf.trx`（本地证据，不提交产物）。

- T02：locator/严格打开/轮转/fault 实施完成，astra发布边界审阅两项finding（非法SizedPtr尾边界、next冲突后未fault）均已修正并回归。主线程另收口坏头/短尾异常映射、active存在性检查不吞IO错误。8个rotation异常probe时点、closed-owned-handle flush、active空时最多读前一段、缺段按需失败通过。Release SegmentStore 47/47，RBF 405/405；SegmentStore增量build 0 warning/0 error，`git diff --check`通过。日志 `/tmp/t02-{build,test,rbf-build,rbf-test}.log`。实际进程kill留T07；Windows PendingPlatform。

- T03 调度细分：同一任务内按不重叠文件分片，sol负责format/snapshot及EventJournal主入口，主线程负责RefMoveStore首末读取及独立测试；主线程分片已交接。只有sol运行.NET，未并行构建。

- T03：v2 format最后发布、共享流式snapshot codec、真实public branch/tag向量、event尾序号、ref allocation+Init+末move、当前target懒验证及统一fault完成。astra两项finding（未知ref格式降类、CommitToRef已知move耗尽仍append）已修并有针对性回归；主线程核验实际diff。Release EventJournal 110/110，增量build 0 warnings/errors，`git diff --check`通过；日志 `/tmp/t03-{build,test}.log`。历史坏move/target不阻止无关daily读取，显式reflog或直接target读取仍失败。旧自动recovery与eager helper已删。catalog仍全量回放，仅T03阶段通过，不代表成本目标完成；T04须复用snapshot解码结果。
