# 设计阶段待定问题：FrameStore 与 VersionStore

日期：2026-10-04–10；既有定稿移出设计队列；D3 的建库/根锁/唯一 pending/ListRefs 与停用清理已定于 S4。2026-10-10 不可变 tag 的名称/接受容量及首版路由参数已定于 [S5-B 专项](09-versionstore-immutable-tags.md)，D8 移出待定队列；当前没有设计阶段待定项，后续转交相应实施片与验收。状态：**Informative / Derived；问题导航，不是新增实施阶段或规范输入**。
输入为本轮会话及 S0–S6 合同；S2/S3 已完成[独立源码验收](02-framestore-final-acceptance.md)，本设计问题队列不重复展开其已关闭项。FrameStore 已实现[公开持久化闭环](02-framestore-persistence-implementation.md)，Inventory/Audit 已实施（见[源码验收](02-framestore-inspection-implementation.md)）；VersionStore 已接通 source-only 的普通 ref 公开闭环（见[骨架与后续实施计划](12-versionstore-implementation-plan.md)）；RBF 底座的活跃构建随机读取改进单独记录验收。S1 与 RBF1 参考隔离的已有 Accepted 资格保留原身份，不构成新模型已实施的证据。

## 设计阶段的研究边界

2026-10-09 用户确认：本阶段聚焦**设计与关键实现方案**。目标是关闭下一条可运行路径所需的设计阻塞；清单无需在开始编码前全部清空，下游问题只在对应切片需要时推进。普通细节优先用代码、测试和局部实现评审建模，文档记录行为合同、不可逆选择及必要依据。

| 类别 | 判断与当前例子 | 处理时机 |
| --- | --- | --- |
| 设计 / 关键实现方案 | 改变可观察行为、持久事实/格式、跨层职责或故障接受政策；或当前底座能否提供所需机制尚不明确。例如创建成立点、独占范围、私有残留可删除资格、名称相等与路由/碰撞合同 | 在编写受影响的持久化流程前形成最小可执行方案；关键可行性未知时做有界探针 |
| 实现时决定 | 在已定合同下可局部替换的表示或代码组织，例如内部集合/助手、普通结果封装、类型/方法签名的语法补齐，以及平台接口的具体选型 | 由实现者在对应切片中选定，以代码和必要测试接受评审；涉及持久格式的具体取值在对应格式交付前固定并回写所属规范 |
| 验收 / 条件优化 | 合同与机制已明确后的两平台中断验证、规模/资源测量和包消费证据；缓存等优化缺少实际需求 | 随实现取得必要验收证据；优化按具体成本或消费者需求触发，不反向增加设计前置 |

同一条问题可以同时含三类内容，应拆分归属。关键合同不能因暂时没有代码而降为细节，具体系统调用也不能仅因尚未选择就自动成为独立设计议题。若实现证明现有机制不可行，或局部选择将改变上述合同，再将具体缺口提升为设计问题；既有定稿只因真实反例或明确新要求重开。

每轮只选一个边界清楚、影响后续切片的问题，先说明：**暂不决定会留下什么具体错误行为，或阻塞哪条实施路径？** 方案达到“合同清楚、机制可执行、关键故障窗口可解释、剩余验证有归属”即可结束设计研究。无需继续展开字段/getter/辅助类型、每种集合和每个默认数字；没有行为差异或机制缺口的备选由实现者选择。停止条件是设计足够支持对应切片，不是文档覆盖所有实现分支。

待定项入队须同时写清：**成立的前提及来源、具体行为/性能/存储/持久性影响、尚缺的决定、受阻的实施片**。已定约束、缺少代码或验收证据本身不构成新设计问题。有产品成本差异的持久格式参数仍须在首次写入前固定；机制已知且无目标工作集时，可由对应实施片选工程默认并记录取舍，不以“寻找最优值”延长设计研究，也不将其当作落盘后可自由替换的内部细节。

若剩下的仅是实现细节、既定合同的验收，或依赖真实工作集的优化，结束本轮设计迭代并报告实施交接与缺失证据；不要为继续迭代制造新问题。必要探针只回答关键可行性/反例，未解决的问题保留具体阻碍。进入生产实现按具体任务授权，清单收缩本身不启动实现。

本表继续是 Derived 导航，不替代 S0–S6 及其阶段内专项规范。已定合同保持原资格；移出设计待定不代表实现、Ready/Accepted、恢复、平台或包验收通过，必要证据仍由所属阶段承担。

## 已形成的方向

FrameStore 核心是不透明分配/随机读取；VersionStore 发布完整 opaque root bytes（RootMap为可选应用codec）、单文件 ref/历史/fork 与分桶不可变 tag。应用管理语义/名称到 RefId 映射；编号仅自动或预留区指定，不依赖业务中间帧构建顺序。
2026-10-04 已确认：三种 RBF 追加能力、单文件独占租借、多 Builder 可嵌套和交错完成、租借/归还串行、优先选择可分配的最低 active FileId、事后软轮转、active 目录与固定 1024 编号分桶归档，以及覆盖全部必要 active 输出的同步 owner barrier。首版不承诺多线程执行。
2026-10-05 追加确认：采用首帧 meta/header，内容交由 Coding Agent 研究决定；成功 EndAppend 自动归还文件；只设可配置的未归还 Builder 数量上限、超限立即拒绝，并通过 config 文件提供设置。
2026-10-07 追加确认：ConfirmDurable 确认调用时全部必要完成输出，未归还 Builder 不阻断屏障、不获得完成/耐久资格；本根依赖已完成时可独立发布。Builder 活跃期间同文件已完成前缀可随机读取，新帧仍不可读。串行、完整内容校验、生命周期与共享 fault 保持，扫描相关 guard 不变。
2026-10-10 用户采用：无 branch 名称；创建时持久 L，低号仅指定 create-only，高号仅自动；两个创建/fork 入口共用单文件发布。S0 `[S-VS-REF-ID-ONLY]` 与 S4 `[S-VS-REF-ID-DOMAINS]`、`[A-VS-LOCAL-REF-LOOKUP]`、`[S-VS-REF-FILE-PUBLISH]` 唯一定义；应用目录登记是独立发布，不扩成默认跨 ref 事务。
2026-10-08 用户确认：FrameAddress 首版固定编码为 12B，保持完整 uint FileId 与 SizedPtr；额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。S0 `[S-FS-ADDRESS-FIXED12]` 与 S2 `[F-FS-FRAME-ADDRESS-12B]` 已关闭基础地址宽度和二进制字段布局；2026-10-09 已定公开值/两方向 bool codec、数值下界/默认/相等/失败规则和普通内部表示，不建立规范文本；2026-10-10 用户批准增加变长地址 codec 并让 RootMap 使用；固定 12B 仍服务预留回填，新增变长格式及失败合同见 S2 `[F-FS-FRAME-ADDRESS-VARINT]`，不重新打开选型问题。实现向量与实际运行时成本保留 S2。
2026-10-09 用户确认：[批量规划器与 API 设想](extensions/framestore-batch-planner-candidate.md)移至独立扩展草稿，需求与协议问题不列入 MVP 工程定稿或 Ready 条件。
同日用户要求 fork 后仍能遍历完整历史并查询全部分叉。本轮设计在 ref 首帧增加不可变 ForkOrigin（源 RefId + 源 SizedPtr），文件内顺序继续表达发布编辑历史；来源感知创建按 revision 重读源发布记录并原样 clone root-payload bytes，ListForks 从全部正式 ref 首帧声明派生图。来源字段见 S4，跨文件定位、预算与查询资格见 S5；业务因果/merge 仍由应用解释。
旧单 active locator、全 owner 活跃期读取禁令已被替代。2026-10-07 按用户要求延期 ref 分段/轮转；Commit/Parent、全控制日志回放、Prepared token 和 checkpoint 不再是首版前置。历史逆序遍历仍是核心功能，经同步 visitor 交付调用结束后可继续使用的自有 bytes snapshot。

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
| VersionStore 是否消费应用帧构建顺序 | 不依赖中间帧申请/完成/物理顺序；自己保存完整 root bytes |
| 发布目标是否必须经过 Commit | 直接发布完整 root bytes，应用选择codec并解释内容 |
| ref 是否首版分段/轮转 | 每 ref 一个 RBF3 文件，容量硬界显式拒绝；分段为后续局部片 |
| 当前值是否必须 replay / checkpoint | 完整读取末快照即可；无差分和派生缓存前置 |
| Gym 历史选点是否要求每帧 parent / 随机 revision 读 | 文件顺序 + 首帧 ForkOrigin 接续完整发布前缀；按 revision fork 内部重读，不开放随机 ReadRevision |
| 是否需要持久反向分叉索引 | S5 已定 ListForks 单次完整 header/全图检查、自有只读边列表与全部正式节点计费的 maxRefs；不写源孩子表/持久索引，跨调用反向缓存延期 |
| tool-loop 是否需要存储精确尝试 token / CAS | 单 driver + 应用 phase/operationId/generation；首版重开读实际状态 |
| 发布失败证据和应用续跑如何衔接 | S4 已定 Result/必选 out 三态、公开尝试/确认边界及完整停用teardown/锁最后；重开恢复实际状态，不精确认领自动创建旧调用，S6 保留外部 uncertain 政策 |
| 无关 Builder 是否阻断根发布 | 不阻断；应用保证本根新增依赖完成，data 先确认全部必要完成输出，根后发布 |
| 活跃 Builder 是否阻断旧帧随机读取 | 不阻断已完成前缀内的指定地址读取；未完成/跨边界先拒绝，扫描相关资格未放宽 |
| 读结果与物理检查如何交付 | S2 已定自有 FrameRead、纯值 inventory、完整 CRC audit 与同步 visitor guard；不外泄 reader/枚举器，不以部分回调或结构扫描称全库内容健康 |
| 历史选点与遍历预算如何交付 | S5 已定同步 bool visitor、自有 RefBytesSnapshot、返回数/工作步预算与单次调用清理；正常 Complete/VisitorStopped，预算专用失败，不外泄 sequence/epoch/cursor |
| tag 名称/容量/路由与桶协议 | [S5-B 专项](09-versionstore-immutable-tags.md)已定用户采用的 non-null/non-empty Ordinal 名称与1024 UTF-16 code units上限、BPV分配前限界，以及固定 FNV64/fold/256桶/8hex 路径；保留初始化保护及 data-first/Outcome，日常按请求名称裁决，健康命中隔离局部坏项，名称不确定时拒绝缺失/创建，靠后施工，不保留跨调用索引 |
| ref 是否承担应用名称映射 | 不承担；应用常量使用预留号，动态目录保存 LocalId 并在正确库 checked 定位；命名 tag 独立保留 |
| 指定编号是否影响自动发号 | 仅允许 1..L 指定，自动高号与其不交叠；低号不推进 H，L 创建时持久且不可变，不预创建对象 |
| 基础地址是否保持 16B 或带 SmartPointer 见证 | 固定 12B 与显式变长 codec，完整 uint FileId + SizedPtr；无预留/内容 CRC，wire 与内存成本分离 |
| FrameAddress 公开值/codec 如何交付 | S2 已定 readonly 不透明值、固定 TryRead/TryWrite 与变长 MeasureVarInt/WriteVarInt/ReadVarInt、共同数值下界及 default/完整等值；变长读取失败不推进，无额外错误族/规范文本 |
| FrameStore 格式门记录与初次建立 | S2 已定 framestore.format 普通24B记录/唯一CRC/共同只读校验；`[R-FS-STORE-CREATE]` 已定空store、正式门直接create-only写入及成立/返回/失败边界，无私有门或门rename；`[S-FS-OWNER-LOCK]` 已定永久0B控制设施/门前bootstrap与重检、writer独占/readers共享及最后关锁；私有data初始化残留语法/候选/全字节前缀、可写取消与只读保留已定，实际根准入已定于 `[S-FS-ROOT-ADMISSION]`，RBF纯入口/目录清理已实施，平台与系统资格按实施记录区分 |
| VersionStore 建库/门/根锁/工作区 | S4 `[R-VS-STORE-CREATE]`、`[S-VS-ROOT-ADMISSION]`、`[S-VS-OWNER-LOCK]`、`[R-VS-STORE-OPEN]` 与 `[S-VS-CREATING-WORKSPACE]` 已定：直接44B门、随机非零VSID、永久空锁、唯一pending可写取消/只读保留；不续作半初始化，不做私有内容/目标对照。代码接线与平台/中断验收留实施 |
| 借入 data 的入口可用性与持续责任 | S4 `[S-VS-DATA-BORROW]` 已定 VS 工厂先于自身 I/O 检查、模式配对及调用方持续责任；S2 `[A-FS-ENSURE-USABLE]` 仅转发现有内部 disposed/fault guard，公共转发与集成证据留实施，不重开健康查询框架 |
| ref记录是否需要独立version/时间机制 | [S4元信息专项](08-versionstore-record-metadata.md)已定固定8B原始Unix毫秒、精确长度判形/末尾扩展、私有一次取时与完整内容资格；无独立version/时钟水位，S4/S5/CU分别消费，实施向量不重开设计 |
| 可变初始 Snapshot 是否需要恢复前保护 | 2026-10-10 用户采用 S4 `[R-VS-LOCAL-COMPLETE]`：private 完整 flush/close→rename 发布与正式 ref 读取各自承担责任；先按现有 public RBF 恢复，再检查 header 与本次实际需要的记录；不以初始 payload 健康作为所有日常读取的准入。创建/实际到达初始节点/独立完整校验仍检查初始化，接受恢复后初始化完整，不增加前缀 API/初始化账本；原阻塞移出，运行时验收随实施 |
| VersionStore 身份/定位/编号及集合发现 | 完整 RefId/LocalId、RefRevision/bytes 公面/ref envelope 格式、8hex/两域分配与目录生命周期已定；S4 `[A-VS-REFS-DISCOVERY]` 交付正式路径的自有 RefId 全集合，不审计内容或签 revision；精确 revision codec 延期 |

以上各项统一见 [S0](00-architecture-decisions.md) 与相应 [S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。本表只导航，不建立第二套规范。

## 设计与关键实现方案待定

当前为空。tag 的名称/容量/路由、请求范围损坏裁决与发布合同已内聚于 [S5-B 专项](09-versionstore-immutable-tags.md)，原 D8 不再保留为待定问题。后续只在真实反例、底座机制不可行或明确新产品要求出现时，按上面的入队标准重开具体缺口；缺实现或验收证据本身不重建问题。

## 其余定稿项与条件扩展

FrameStore 初次空集合/直接门建立、owner锁/门前bootstrap/模式、私有data残留与实际根准入已定于 S2；VersionStore 初次空库/直接门、根/独立锁/自身模式、唯一pending与ListRefs/停用清理已定于 S4，D3关闭。tag 名称/接受容量与路由格式参数已定于 S5-B 专项，不重开稳定路由/碰撞机制；后续在 ref、历史/fork 后实施独立 tag 片。身份 CLR 表示、具体平台调用及局部接口/codec 代码随对应实现定稿。基础地址、格式门记录/只读校验、资源、header、owned 生命周期、正式路径/编号、ref 恢复后完整性准入及历史/tag/ListForks 已定合同直接消费 S2/S4/S5；其测试向量、运行时成本和平台/包证据保留所属阶段，不转回新的字段、默认值或表示选择。
FrameAddress 不透明不等于支持帧重定位。目录归档只改变同一文件的位置；GC/compaction、逻辑 ID 映射、多线程执行、多个 data owner、跨实例 CAS 和更强断电模型在有需求时单独设计。多个 active 已是首版设计范围，不再列为未来条件扩展。
地址见证的重评触发条件分别为：裸地址跨 store 流转且需要概率性误用检测时考虑 store/address fingerprint；要求库自动拒绝取消预约的旧引用时另设计每帧持久 token 与重开发号/碰撞规则；仅引用已完成对象且需要预期内容见证时考虑内容 CRC。最终内容 CRC 在 Begin 尚不可确定，A↔B 会引入 checksum 依赖，不能作为当前提前稳定地址字段。上述方案均无首版预留字段或实施前置；无碰撞的来源、完成或耐久保证不能由额外 4B 自行推出。
ref 分段/轮转、随机 revision 读取、持久 cursor、差分/checkpoint、派生 tag 索引、跨 ref 事务和精确尝试追踪不作为核心前置。业务 merge/provenance 由应用决定；出现当前 bytes/应用 schema 边界无法满足的明确需求后再扩展，不能从旧草案恢复候选功能。

## 实施交接与重开条件

D6 已由 S2 `[S-FS-ROOT-ADMISSION]` 与真实目录工厂关闭：调用方根/可信父路径、受管精确组件、普通类型/no-follow、严格 owner 锁及同文件系统不覆盖移动已有具体机制。公开持久化闭环的实际验证见[实施记录](02-framestore-persistence-implementation.md)；Inventory/Audit、进程中断和资源/规模证据留实施验收。D3 与 tag 专项均已形成可执行最小合同；当前无待定设计阻塞，不把已定机制的代码接线、测试细节或无目标的参数最优研究重新放回设计队列。

| 范围 | 设计阶段聚焦 | 随实施决定或验证 |
| --- | --- | --- |
| S4 已定，交实施 | 初次建库/根锁/唯一pending/ListRefs/停用清理及恢复后完整性准入已定，不再有D3设计阻塞 | V1已接通CreateRef→ReadRef→PublishRef→冷重开及Windows局部验证；后续补历史/fork、tag、完整检查与两平台进程中断，保持VS整体Draft，局部验证与系统资格分开 |
| S5 历史已定，交实施 | 已签发 revision/不可变 ForkOrigin 直接精确读取；日常 header/逐项检查与声明图检查分离，详见 S4/S5 | 先补[RBF 指定边界逆扫公共重载](../Rbf/rbf-reverse-boundary-plan.md)及独立 public 验收，再接续旧 fork 点之前的历史；不把已定公共增量列为待定设计 |
| S5-B 已定，靠后施工 | [09 专项](09-versionstore-immutable-tags.md)的名称/BPV/组合容量与稳定路由参数已定，不再有 D8 设计阻塞 | 先完成 ref、历史/fork，随后实施 CreateTag→ResolveTag→冷重开及桶/损坏/Outcome/资源/平台/组合验收 |
| S2–S6 已定合同 | 仅在真实反例、机制不可行或明确新要求时重开 | 所属阶段的实施/验收要求、进程中断、平台及隔离包消费证据；条件优化另看实际需求 |

每轮结论写回所属阶段，只从本表移出已关闭的设计子题；D3/D8 已移出，不因其实现尚缺而重建待定项。普通实现选择和已定合同验收交给对应实施片，允许少量导航，不建立越来越细的研究队列。关键方案若须实验，先用最小探针取证；若只缺完整实现/测量，说明证据缺口并结束设计轮，不用文档代替代码模型。

涉及外部业务语义、接受风险或部署假设的未决取舍，列出具体选项交用户处理；符合既定合同的局部工程选择由实现者负责。FrameStore 分配器继续不理解应用 codec，不等待 Commit 或全局日志；不反向增加已延期的批量规划、索引或事务能力。

S2/S3 源码资格已 Accepted，S4–S6 仍为 Draft；FrameStore 已实施公开持久化闭环，Inventory/Audit 已实施（见[源码验收](02-framestore-inspection-implementation.md)），VersionStore 已接通普通 ref 公开闭环。设计闭合支持实施交接；Ready/Accepted 及源码、恢复、性能、平台、包资格仍分别按所属阶段的实际证据报告。研究队列继续聚焦设计与关键方案；源码资格与剩余范围见[最终源码验收](02-framestore-final-acceptance.md)。

2026-10-10 fork 化简的操作与来源比较唯一见 S5 `[S-VS-FORK-FROM-REVISION]` / `[A-VS-REF-HISTORY-CHECKED]`，发布层边界见 S4 `[A-VS-ROOT-PAYLOAD]`；原样 clone bytes，ForkRef 返回 RefRevision，不解码 RootMap，CU 候选独立定位 payload。通用 raw payload 公面已定于 [11专项](11-versionstore-root-payload-bytes-api.md)：同步Span输入、屏障前捕获、GC自有只读结果及1..1MiB；可选RootMapCodec内聚于10。FrameStore程序集依赖拆分仍为后续议题，不作为既定bytes/fork的实施阻塞。
