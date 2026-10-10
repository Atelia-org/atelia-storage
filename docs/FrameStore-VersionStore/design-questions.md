# 设计阶段待定问题：FrameStore 与 VersionStore

日期：2026-10-04–10；既有定稿移出设计队列；2026-10-10 采用无 branch 名称、持久预留低号区及自动/指定创建，相关合同写回 S0/S4/S5，D8 仅保留 tag 名称问题。状态：**Informative / Derived；问题导航，不是新增实施阶段或规范输入**。
输入为本轮会话及 S0–S6 合同；S2/S3 已完成[独立源码验收](02-framestore-final-acceptance.md)，本设计问题队列不重复展开其已关闭项。FrameStore 已实现[公开持久化闭环](02-framestore-persistence-implementation.md)，Inventory/Audit 已实施（见[源码验收](02-framestore-inspection-implementation.md)）；VersionStore 项目尚未创建；RBF 底座的活跃构建随机读取改进单独记录验收。S1 与 RBF1 参考隔离的已有 Accepted 资格保留原身份，不构成新模型已实施的证据。

## 设计阶段的研究边界

2026-10-09 用户确认：本阶段聚焦**设计与关键实现方案**。目标是关闭下一条可运行路径所需的设计阻塞；清单无需在开始编码前全部清空，下游问题只在对应切片需要时推进。普通细节优先用代码、测试和局部实现评审建模，文档记录行为合同、不可逆选择及必要依据。

| 类别 | 判断与当前例子 | 处理时机 |
| --- | --- | --- |
| 设计 / 关键实现方案 | 改变可观察行为、持久事实/格式、跨层职责或故障接受政策；或当前底座能否提供所需机制尚不明确。例如创建成立点、独占范围、私有残留可删除资格、恢复前初始化保护、名称相等与路由/碰撞合同 | 在编写受影响的持久化流程前形成最小可执行方案；关键可行性未知时做有界探针 |
| 实现时决定 | 在已定合同下可局部替换的表示或代码组织，例如内部集合/助手、普通结果封装、类型/方法签名的语法补齐，以及平台接口的具体选型 | 由实现者在对应切片中选定，以代码和必要测试接受评审；涉及持久格式的具体取值在对应格式交付前固定并回写所属规范 |
| 验收 / 条件优化 | 合同与机制已明确后的两平台中断验证、规模/资源测量和包消费证据；缓存等优化缺少实际需求 | 随实现取得必要验收证据；优化按具体成本或消费者需求触发，不反向增加设计前置 |

同一条问题可以同时含三类内容，应拆分归属。关键合同不能因暂时没有代码而降为细节，具体系统调用也不能仅因尚未选择就自动成为独立设计议题。若实现证明现有机制不可行，或局部选择将改变上述合同，再将具体缺口提升为设计问题；既有定稿只因真实反例或明确新要求重开。

每轮只选一个边界清楚、影响后续切片的问题，先说明：**暂不决定会留下什么具体错误行为，或阻塞哪条实施路径？** 方案达到“合同清楚、机制可执行、关键故障窗口可解释、剩余验证有归属”即可结束设计研究。无需继续展开字段/getter/辅助类型、每种集合和每个默认数字；没有行为差异或机制缺口的备选由实现者选择。停止条件是设计足够支持对应切片，不是文档覆盖所有实现分支。

若剩下的仅是实现细节、既定合同的验收，或依赖真实工作集的优化，结束本轮设计迭代并报告实施交接与缺失证据；不要为继续迭代制造新问题。必要探针只回答关键可行性/反例，未解决的问题保留具体阻碍。进入生产实现按具体任务授权，清单收缩本身不启动实现。

本表继续是 Derived 导航，不替代 S0–S6 规范。已定合同保持原资格；移出设计待定不代表实现、Ready/Accepted、恢复、平台或包验收通过，必要证据仍由所属阶段承担。

## 已形成的方向

FrameStore 核心是不透明分配/随机读取；VersionStore 使用完整 RootMap、单文件 ref/历史/fork 与分桶不可变 tag。应用管理语义/名称到 RefId 映射；编号仅自动或预留区指定，不依赖业务中间帧构建顺序。
2026-10-04 已确认：三种 RBF 追加能力、单文件独占租借、多 Builder 可嵌套和交错完成、租借/归还串行、优先选择可分配的最低 active FileId、事后软轮转、active 目录与固定 1024 编号分桶归档，以及覆盖全部必要 active 输出的同步 owner barrier。首版不承诺多线程执行。
2026-10-05 追加确认：采用首帧 meta/header，内容交由 Coding Agent 研究决定；成功 EndAppend 自动归还文件；只设可配置的未归还 Builder 数量上限、超限立即拒绝，并通过 config 文件提供设置。
2026-10-07 追加确认：ConfirmDurable 确认调用时全部必要完成输出，未归还 Builder 不阻断屏障、不获得完成/耐久资格；本根依赖已完成时可独立发布。Builder 活跃期间同文件已完成前缀可随机读取，新帧仍不可读。串行、完整内容校验、生命周期与共享 fault 保持，扫描相关 guard 不变。
2026-10-10 用户采用：无 branch 名称；创建时持久 L，低号仅指定 create-only，高号仅自动；两个创建/fork 入口共用单文件发布。S0 `[S-VS-REF-ID-ONLY]` 与 S4 `[S-VS-REF-ID-DOMAINS]`、`[A-VS-LOCAL-REF-LOOKUP]`、`[S-VS-REF-FILE-PUBLISH]` 唯一定义；应用目录登记是独立发布，不扩成默认跨 ref 事务。
2026-10-08 用户确认：FrameAddress 首版固定编码为 12B，保持完整 uint FileId 与 SizedPtr；额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。S0 `[S-FS-ADDRESS-FIXED12]` 与 S2 `[F-FS-FRAME-ADDRESS-12B]` 已关闭基础地址宽度和二进制字段布局；2026-10-09 已定公开值/两方向 bool codec、数值下界/默认/相等/失败规则和普通内部表示，不建立规范文本；2026-10-10 用户批准增加变长地址 codec 并让 RootMap 使用；固定 12B 仍服务预留回填，新增变长格式及失败合同见 S2 `[F-FS-FRAME-ADDRESS-VARINT]`，不重新打开选型问题。实现向量与实际运行时成本保留 S2。
2026-10-09 用户确认：[批量规划器与 API 设想](extensions/framestore-batch-planner-candidate.md)移至独立扩展草稿，需求与协议问题不列入 MVP 工程定稿或 Ready 条件。
同日用户要求 fork 后仍能遍历完整历史并查询全部分叉。本轮设计在 ref 首帧增加不可变 ForkOrigin（源 RefId + 源 SizedPtr），文件内顺序继续表达发布编辑历史；来源感知创建按 revision 重读源字典，ListForks 从全部正式 ref 首帧声明派生图。来源字段见 S4，跨文件定位、预算与查询资格见 S5；业务因果/merge 仍由应用解释。
旧单 active locator、全 owner 活跃期读取禁令已被替代。2026-10-07 按用户要求延期 ref 分段/轮转；Commit/Parent、全控制日志回放、Prepared token 和 checkpoint 不再是首版前置。历史逆序遍历仍是核心功能，经同步 visitor 交付调用结束后可继续使用的自有字典。

## 本轮已关闭的方向问题

| 原问题 | 已达成的方向 |
| --- | --- |
| 三种追加是否都保留 | buffer Append、未知尺寸 Begin、分立长度已知尺寸 Begin；提升为 FrameAddress |
| 第二个 Builder 如何获得位置 | 保持已有租借，使用其他空闲文件，没有合适文件才新建 |
| active 中多个空闲文件如何选择 | 选择数值 FileId 最小者；忙/停止分配项跳过，不等待、不轮询、不按帧长度试配 |
| 已知/未知尺寸是否分别轮转 | 统一成功完成后 TailOffset > 阈值；目标阈值不限制最大合法帧 |
| 软阈值默认/变更如何处理 | S2 已定 Create/Open 单一 long 参数、64GiB 工程默认、合法范围/实例固定；重开只按恢复后 active tail 重算，archive 永不解封，无阈值 config 或持久状态 |
| 可写集合是否另存 manifest | active 目录是事实，archive 位置表示已按协议 flush 并封存 |
| 归档寻址是否要全历史分段表 | 1024 编号一个桶，按 FileId 高低位计算 |
| 如何恢复下一个 FileId | S2 已定完整流式正式名称发现，取 active/archive 实际最大编号；只保留内存 max，空桶/私有槽不计、缺口不补，耗尽只拒绝新文件；目录成本与实施向量留在 S2 |
| 正式文件/桶如何规范命名 | S2 已定 active/archive 共用 8 位完整 FileId 文件名、6 位桶名与高 22 bits 范围、小写 ASCII 精确比较和全部直接项语法；根准入/实际类型/平台资格继续留 S2 |
| 如何构建循环引用 | 预算内通过多个实际已知尺寸租借取得地址，交错填充并独立完成 |
| 并发是否现在实施 | 首版串行；保留独立文件/Builder 边界，未来并行另行验收 |
| 首帧 meta/header 如何解释 | S2 已定固定 24B 版本/StoreId/FileId、首位置识别与完整校验；用户 tag 不保留禁用区 |
| 成功提交何时归还 | EndAppend 正常成功返回前自动归还，不等后续 Dispose |
| 如何控制未归还 Builder 数量 | config 文件提供可调整上限，超限立即拒绝；不做精确总内存账本 |
| 资源与句柄基线如何选 | S2 已定 Builder-only 配额、满额合法 Append、保留 active 句柄且 Off、只读按操作开关；实现成本与清理仍需验收 |
| 成功完成后何时归档 | End/Append 只登记并归还；下一合法写准入、ConfirmDurable、可写 Open 共用内部归档，无独立 Maintain 前置 |
| 如何保护旧副本及报告失败 | 一次性共享 Lease、普通 borrow 前检、未知委派异常保守 shared fault、全部资源清理与异常汇总；正式合同见 S2 |
| VersionStore 是否消费应用帧构建顺序 | 不依赖中间帧申请/完成/物理顺序；自己保存完整根字典 |
| 发布目标是否必须经过 Commit | 直接发布 string → FrameAddress 完整字典，应用解释 Key |
| ref 是否首版分段/轮转 | 每 ref 一个 RBF3 文件，容量硬界显式拒绝；分段为后续局部片 |
| 当前值是否必须 replay / checkpoint | 完整读取末快照即可；无差分和派生缓存前置 |
| Gym 历史选点是否要求每帧 parent / 随机 revision 读 | 文件顺序 + 首帧 ForkOrigin 接续完整发布前缀；按 revision fork 内部重读，不开放随机 ReadRevision |
| 是否需要持久反向分叉索引 | S5 已定 ListForks 单次完整首两帧/全图检查、自有只读边列表与全部正式节点计费的 maxRefs；不写源孩子表/持久索引，跨调用反向缓存延期 |
| tool-loop 是否需要存储精确尝试 token / CAS | 单 driver + 应用 phase/operationId/generation；首版重开读实际状态 |
| 发布失败证据和应用续跑如何衔接 | S4 已定 Result/必选 out 三态、公开尝试/确认边界及故障清理；重开恢复实际状态，不精确认领自动创建旧调用，S6 保留外部 uncertain 政策 |
| 无关 Builder 是否阻断根发布 | 不阻断；应用保证本根新增依赖完成，data 先确认全部必要完成输出，根后发布 |
| 活跃 Builder 是否阻断旧帧随机读取 | 不阻断已完成前缀内的指定地址读取；未完成/跨边界先拒绝，扫描相关资格未放宽 |
| 读结果与物理检查如何交付 | S2 已定自有 FrameRead、纯值 inventory、完整 CRC audit 与同步 visitor guard；不外泄 reader/枚举器，不以部分回调或结构扫描称全库内容健康 |
| 历史选点与遍历预算如何交付 | S5 已定同步 bool visitor、自有 RefSnapshot、返回数/工作步预算与单次调用清理；正常 Complete/VisitorStopped，预算专用失败，不外泄 sequence/epoch/cursor |
| tag 桶如何初始化/校验、是否需要索引 | S5 已定完整 header 初始化边界与恢复保护、统一版本组合 schema、每次全桶 checked 扫描及临时 fullname 集合；不保留跨调用索引，RootMap 原样消费 S4 已定 codeword，名称/组合容量仍在 S5-Q1 |
| ref 是否承担应用名称映射 | 不承担；应用常量使用预留号，动态目录保存 LocalId 并在正确库 checked 定位；命名 tag 独立保留 |
| 指定编号是否影响自动发号 | 仅允许 1..L 指定，自动高号与其不交叠；低号不推进 H，L 创建时持久且不可变，不预创建对象 |
| 基础地址是否保持 16B 或带 SmartPointer 见证 | 固定 12B 与显式变长 codec，完整 uint FileId + SizedPtr；无预留/内容 CRC，wire 与内存成本分离 |
| FrameAddress 公开值/codec 如何交付 | S2 已定 readonly 不透明值、固定 TryRead/TryWrite 与变长 MeasureVarInt/WriteVarInt/ReadVarInt、共同数值下界及 default/完整等值；变长读取失败不推进，无额外错误族/规范文本 |
| FrameStore 格式门记录与初次建立 | S2 已定 framestore.format 普通24B记录/唯一CRC/共同只读校验；`[R-FS-STORE-CREATE]` 已定空store、正式门直接create-only写入及成立/返回/失败边界，无私有门或门rename；`[S-FS-OWNER-LOCK]` 已定永久0B控制设施/门前bootstrap与重检、writer独占/readers共享及最后关锁；私有data初始化残留语法/候选/全字节前缀、可写取消与只读保留已定，实际根准入已定于 `[S-FS-ROOT-ADMISSION]`，RBF纯入口/目录清理已实施，平台与系统资格按实施记录区分 |
| VersionStore 格式门/data/预留区 | S4 普通 48B 门/双身份/持久 L/唯一 CRC 与共同只读资格已定；初次门/根锁/模式等仍待定 |
| VersionStore 身份/定位/编号 | 完整 RefId、LocalId 导出与 checked 库内定位、RefRevision/RootMap/ref 格式、单文件 16hex 与两域分配已定；精确 revision codec 延期，实际准入/private/初始化待定 |

以上各项统一见 [S0](00-architecture-decisions.md) 与相应 [S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。本表只导航，不建立第二套规范。

## 设计与关键实现方案待定

### D3：根地址与自有历史快照的最小合同如何定稿？

**涉及阶段：** S2-Q1/Q4、S3-Q4；[S4](04-versionstore-publication.md) 的字典 codec / 身份 / 单文件创建；消费 [S5](05-versionstore-names-and-indexes.md) 已定历史合同。

**已确认：** 字典按值保存，地址解释于一个绑定的 data FrameStore，复用 S2 变长地址 codec；固定 12B 入口继续服务预留回填。发布时应用保证全部必要对象已经完成，不能采用取消/未完成 ticket；data ConfirmDurable 不解析业务图，无关 Builder 未归还不阻断发布。屏障确认 leased 文件旧输出，不赋予其正在构建的新帧资格。旧 ref/tag 快照可以复用数据地址，不需要重新创建 Commit 或提交 parent。

**设计 / 关键方案：** VersionStore 初次 store 创建/门发布、根独占与私有残留裁决，以及可变初始 Snapshot 的恢复前初始化保护；明确身份生成与访问模式所需的行为合同，保持实际借入 data 的绑定及资源责任。未定查询如 ListRefs 的结果范围/资格属于对应接口合同，CLR 包装与签名语法随实现定稿。基础地址值/codec、RootMap codeword、ref header/普通 Snapshot 组合格式及单文件发布单位/正式路径已确定，不再开放地址宽度、key/wire/限额、header 来源判别或应用名称映射层的选择。地址数值规则、default 预检及 RootMap 变长字段直接消费 S2，每条完整记录仍用 RBF 公共尺寸 API。

**实现 / 验收：** 公开身份的 CLR 表示及满足既定身份合同的生成调用、内部 codec 组合、初始化/读取助手、具体私有路径/锁/平台入口，由对应实现片决定；在行为合同确定后，ReadRef/ListRefs、末帧成员/源成员、屏障和清理按合同编码并验收，不逐一展开辅助类型、属性或内部错误分支。文件发布和初始化保护所需入口若无可执行路径，仍作为关键方案问题处理。
VersionStore 门记录/共同只读检查与 data 持久身份绑定已定于 S4 `[F-VS-OWN-FORMAT]`，不再作为待定 codec 或载体选择；16B canonical VersionStoreId 由下游直接消费。内容资格不关闭初次门发布、根/锁或平台协议，也不证明裸地址来源/应用闭包。
RefId/内部 8B 字段消费 S4 `[F-VS-REF-ID-8B]`，LocalId 保存与正确库 checked 查询消费 `[A-VS-LOCAL-REF-LOOKUP]`；单文件路径消费 `[F-VS-REF-PATHS]`，持久 L/两域创建/水位/不复用消费 `[S-VS-REF-ID-DOMAINS]`。这些不再待定；冷开 O(ref 数量)路径发现/private 独立资格继续 S4-Q3，指定残留不可统一要求 H+1。编号与查询不证明 Unknown 旧尝试 Confirmed，不重开整体身份或精确 revision codec 议题。

RefRevision 自有公开值、完整等值/default/上下文前检、只读实际位置及同 VS 重开使用已定于 S4 `[A-VS-REF-REVISION-VALUE]`，不再作为待定表示或整体 codec 选择；未提供匿名精确 revision 的跨进程导入导出，出现消费者再立窄合同，tag 字典不能透明替代其精确来源书签。
RootMap/RefSnapshot 表示直接消费 `[A-VS-ROOTS-OWNED]`；key/wire/容量直接消费 `[F-VS-ROOTMAP-BPV1]`，不再列为待定设计。tag/history/CU 候选原样复用；真实 FrameAddress 组合验证见[变长编码实施记录](02-framestore-varint-address-implementation.md)；探针不构成 VersionStore 新库/发布协议、峰值或性能资格。
历史枚举固定起始完成上界，沿不可变 ForkOrigin 接续各源选中位置及其更早历史；活动期间禁止同 owner mutation，返回的完整 RootMap 自有。RefRevision 只从完成发布/真实成员 checked-read 取得；来源元数据不自动签发 checked revision。来源感知 fork 先重读源字典，在子输出前完成 data 屏障与源文件 flush；只传 roots 的创建无来源。
ref header 的 28/44B 来源判别、固定字段、Header/Snapshot 的 0/1 kind、普通 Snapshot 纯 RootMap/完整容量及共同首两帧检查已定于 S4 `[F-VS-REF-FRAMES]`，不再列为待定 codec。剩余具体缺口是**可写恢复前的初始化准入**：[公面探针](../../experiments/RefFrameSchemaProbe/README.md)在此前固定 12B 地址草案上复现 header28+初始 15B 完整 112B，缺末 8B 仍为 104B≥empty 最小 100B；RBF 会 CompletedTail 补齐，事后拒绝后再次 None 可认领。现 public 只读工厂又会拒绝合法更新残尾，不能提供一般前缀预检；需单独选定可执行保护/底座入口或重审原有正式坏初始化的接受政策，不能将最小长度＋事后 report 宣称 Ready。正常私有 flush/close→文件发布不产生该半初始，反例不扩大 ProcessCrashOnly 模型。
历史入口、自有值交付、跨文件资源/visited、返回/定位工作预算和终止已定于 S5 `[A-VS-REF-HISTORY-CHECKED]`，直接消费而非重新选择。现有 RBF 只有 EOF 逆扫，旧 fork 点的源后缀定位可能增长并支付工作步；无关后缀 payload 不作历史审计，已观察的结构错误仍传播。完整无来源起点、正常选点停止、预算失败、取消与错误按 S5 区分。

**实现验收目标：** 选中非末快照，结束枚举后 fork/tag/rewind；源继续更新、fork-of-fork、同值初始与源不同 revision，完整继承前缀保持正确。覆盖源真实成员、错 length、缺源/循环/字典不符、源 flush 失败、定位预算/错误和跨文件释放；不自动读取全部应用图或从 data 地址大小推算历史。

### D8：不可变 tag 的名称与路由合同如何定稿？

**涉及阶段：** S5-Q1/Q3、S6 对应验收；branch 名称/绑定/全局查名已从范围移除，不再研究。

**已确认：** tag 为命名不可变 RootMap，每桶一个 RBF3 文件；header 组合 schema/初始化前检、每次完整 checked 扫描/唯一性与临时全名集合、自有结果/清理、data-first 及 PublicationOutcome 已定于 S5。不建立跨调用索引或默认缓存。

**设计 / 关键方案：** TagName 相等规则、字符/容量与持久表示，以及相等必同桶、稳定路由/安全路径、hash 碰撞按完整名字裁决。其余名称不存在全局 branch 绑定资格或目录共同发布要求。

**实现 / 验收：** 在上述合同下固定具体 hash/桶数/codec/路径/签名，验证同名先于 barrier/输出拒绝、完整坏记录/枚举/关闭失败不当 absent、合法碰撞、空桶初始化/private/file rename 与只读行为。重复同桶扫描/连续创建的规模成本随实现测量，不以可选索引延长设计队列。ListForks 结果/预算/图资格继续直接消费已定合同，交对应实现验收。

## 其余定稿项与条件扩展

FrameStore 初次空集合/直接门建立合同已定；FrameStore owner锁/门前bootstrap/模式互斥已定；FrameStore私有data残留协议已定；FrameStore 实际根准入已定于 S2 `[S-FS-ROOT-ADMISSION]`；VersionStore 实际根准入与私有发布残留、VersionStore根独占、VersionStore 初次门建立及恢复前初始化保护和名称行为仍按上述设计问题推进；身份 CLR 表示、具体平台调用及局部接口/codec 代码随对应实现定稿。基础地址、格式门记录/只读校验、资源、header、owned 生命周期、正式路径/编号及历史/tag/ListForks 已定合同直接消费 S2/S4/S5；其测试向量、运行时成本和平台/包证据保留所属阶段，不转回新的字段、默认值或表示选择。
FrameAddress 不透明不等于支持帧重定位。目录归档只改变同一文件的位置；GC/compaction、逻辑 ID 映射、多线程执行、多个 data owner、跨实例 CAS 和更强断电模型在有需求时单独设计。多个 active 已是首版设计范围，不再列为未来条件扩展。
地址见证的重评触发条件分别为：裸地址跨 store 流转且需要概率性误用检测时考虑 store/address fingerprint；要求库自动拒绝取消预约的旧引用时另设计每帧持久 token 与重开发号/碰撞规则；仅引用已完成对象且需要预期内容见证时考虑内容 CRC。最终内容 CRC 在 Begin 尚不可确定，A↔B 会引入 checksum 依赖，不能作为当前提前稳定地址字段。上述方案均无首版预留字段或实施前置；无碰撞的来源、完成或耐久保证不能由额外 4B 自行推出。
ref 分段/轮转、随机 revision 读取、持久 cursor、差分/checkpoint、派生 tag 索引、跨 ref 事务和精确尝试追踪不作为核心前置。业务 merge/provenance 由应用决定；出现无法用 RootMap 表达的明确需求后再扩展，不能从旧草案恢复候选功能。

## 下一轮研究顺序与实施交接

D6 已由 S2 `[S-FS-ROOT-ADMISSION]` 与真实目录工厂关闭：调用方根/可信父路径、受管精确组件、普通类型/no-follow、严格 owner 锁及同文件系统不覆盖移动已有具体机制。公开持久化闭环的实际验证见[实施记录](02-framestore-persistence-implementation.md)；Inventory/Audit、进程中断和资源/规模证据留实施验收。后续设计研究聚焦 D3、D8，不把这些已定机制的测试细节重新放回设计队列。

| 范围 | 设计阶段聚焦 | 随实施决定或验证 |
| --- | --- | --- |
| D3 / S4 | 可变初始帧的恢复前准入、初次门发布/根独占/private 协议、身份/模式和未定公开查询的必要行为 | CLR 表示/接口语法/助手及读取实现；已定 codec、成员、屏障/Outcome/清理与冷重开验收 |
| D8 / S5 | 仅 tag 名称相等/字符/容量、稳定表示与路由/碰撞 | 具体编码/hash/路径参数与代码；桶发布/完整扫描/资源/规模验收 |
| S2–S6 已定合同 | 仅在真实反例、机制不可行或明确新要求时重开 | 各阶段 Ready 表的实现测试、进程中断、平台及隔离包消费证据；条件优化另看实际需求 |

每轮结论写回所属阶段，只从本表移出已关闭的设计子题；普通实现选择和已定合同验收交给对应实施片，允许少量导航，不建立越来越细的研究队列。关键方案若须实验，先用最小探针取证；若只缺完整实现/测量，说明证据缺口并结束设计轮，不用文档代替代码模型。

涉及外部业务语义、接受风险或部署假设的未决取舍，列出具体选项交用户处理；符合既定合同的局部工程选择由实现者负责。FrameStore 分配器继续不理解应用 codec，不等待 Commit 或全局日志；不反向增加已延期的批量规划、索引或事务能力。

S2/S3 源码资格已 Accepted，S4–S6 仍为 Draft；FrameStore 已实施公开持久化闭环，Inventory/Audit 已实施（见[源码验收](02-framestore-inspection-implementation.md)），VersionStore 尚未创建。设计闭合支持实施交接；Ready/Accepted 及源码、恢复、性能、平台、包资格仍分别按所属阶段的实际证据报告。研究队列继续聚焦设计与关键方案；源码资格与剩余范围见[最终源码验收](02-framestore-final-acceptance.md)。
