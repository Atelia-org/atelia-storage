# 下一轮工程定稿：FrameStore 与根字典发布

日期：2026-10-04；2026-10-05 更新核心/扩展分离；2026-10-07 收缩 VersionStore、放宽已完成输出资格并确定命名 fork 的目录共同发布；2026-10-08 关闭基础地址宽度/字段编码选择。状态：**Informative / Derived；问题汇总，不是新增实施阶段或规范输入**。
输入为本轮会话和 S0–S6 的 Draft 合同。FrameStore/VersionStore 本轮仅修订文档，项目尚未创建；RBF 底座的活跃构建随机读取改进单独记录验收。S1 与 RBF1 参考隔离的已有 Accepted 资格保留原身份，不构成新模型已实施的证据。

## 已形成的方向

FrameStore 核心合同为不透明分配和随机读取；文件顺序只服务底层恢复。[FrameLog](extensions/framelog-candidate.md) 已独立为可选扩展候选，需求和去留仍待审定。VersionStore 使用完整 RootMap、每 ref 单个 RBF3 文件、分桶不可变 tag 和独立 branch 绑定，不依赖应用中间帧构建顺序或日志候选。
2026-10-04 已确认：三种 RBF 追加能力、单文件独占租借、多 Builder 可嵌套和交错完成、租借/归还串行、优先选择可分配的最低 active FileId、事后软轮转、active 目录与固定 1024 编号分桶归档，以及覆盖全部必要 active 输出的同步 owner barrier。首版不承诺多线程执行。
2026-10-05 追加确认：采用首帧 meta/header，内容交由 Coding Agent 研究决定；成功 EndAppend 自动归还文件；只设可配置的未归还 Builder 数量上限、超限立即拒绝，并通过 config 文件提供设置。
2026-10-07 追加确认：ConfirmDurable 确认调用时全部必要完成输出，未归还 Builder 不阻断屏障、不获得完成/耐久资格；本根依赖已完成时可独立发布。Builder 活跃期间同文件已完成前缀可随机读取，新帧仍不可读。串行、完整内容校验、生命周期与共享 fault 保持，扫描相关 guard 不变。
同日追加确认：命名 fork 在私有 RefId 容器同时准备 ref 与初始 binding，flush/close 后一次目录 rename 共同发布；所有 branch aliases 放同一种 names 文件。无公开 Creating 名称或永久 branch gate，私有残留不可查询，普通 CreateRef 与手工两步仍保留。正式定义见 S0 `[S-VS-NAMED-FORK-ATOMIC]`、S4 `[S-VS-REF-DIRECTORY-PUBLISH]` 与 S5 `[S-VS-NAMED-FORK-PUBLISH]`。
2026-10-08 用户确认：FrameAddress 首版固定编码为 12B，保持完整 uint FileId 与 SizedPtr；额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。S0 `[S-FS-ADDRESS-FIXED12]` 与 S2 `[F-FS-FRAME-ADDRESS-12B]` 已关闭基础地址宽度和二进制字段布局；内部 struct、公开 codec 入口、文本/错误及平台验证仍为工程定稿。
旧单 active locator、全 owner 活跃期读取禁令和 BeginNext 必选方案已被替代；FrameBatch 是待需求验证的大规模优化。2026-10-07 按用户要求延期 ref 分段/轮转；Commit/Parent、全控制日志回放、Prepared token 和 checkpoint 不再是首版前置。历史逆序枚举仍是核心功能，返回释放枚举器后可继续使用的自有字典。

## 本轮已关闭的方向问题

| 原问题 | 已达成的方向 |
| --- | --- |
| 三种追加是否都保留 | buffer Append、未知尺寸 Begin、分立长度已知尺寸 Begin；提升为 FrameAddress |
| 第二个 Builder 如何获得位置 | 保持已有租借，使用其他空闲文件，没有合适文件才新建 |
| active 中多个空闲文件如何选择 | 选择数值 FileId 最小者；忙/停止分配项跳过，不等待、不轮询、不按帧长度试配 |
| 已知/未知尺寸是否分别轮转 | 统一成功完成后 TailOffset > 阈值；目标阈值不限制最大合法帧 |
| 可写集合是否另存 manifest | active 目录是事实，archive 位置表示已按协议 flush 并封存 |
| 归档寻址是否要全历史分段表 | 1024 编号一个桶，按 FileId 高低位计算 |
| 循环引用是否必须先做 batch planner | 预算内通过实际已知尺寸租借取得地址，交错完成；planner 可单独评估 |
| 并发是否现在实施 | 首版串行；保留独立文件/Builder 边界，未来并行另行验收 |
| 是否采用首帧 meta/header | 必须采用并保留版本解释；字段/codec/校验规则交由 Coding Agent 定稿 |
| 成功提交何时归还 | EndAppend 正常成功返回前自动归还，不等后续 Dispose |
| 如何控制未归还 Builder 数量 | config 文件提供可调整上限，超限立即拒绝；不做精确总内存账本 |
| 日志候选是否属于核心 | 已分离为独立可选扩展；S2 核心定稿、实施与验收不等待其去留 |
| VersionStore 是否消费应用帧构建顺序 | 不依赖中间帧申请/完成/物理顺序；自己保存完整根字典 |
| 发布目标是否必须经过 Commit | 直接发布 string → FrameAddress 完整字典，应用解释 Key |
| ref 是否首版分段/轮转 | 每 ref 一个 RBF3 文件，容量硬界显式拒绝；分段为后续局部片 |
| 当前值是否必须 replay / checkpoint | 完整读取末快照即可；无差分和派生缓存前置 |
| Gym 历史选点是否要求 parent / 随机 revision 读 | 固定上界逆序枚举完整自有字典，够用；跨重开书签用 tag |
| tool-loop 是否需要存储精确尝试 token / CAS | 单 driver + 应用 phase/operationId/generation；首版重开读实际状态 |
| 无关 Builder 是否阻断根发布 | 不阻断；应用保证本根新增依赖完成，data 先确认全部必要完成输出，根后发布 |
| 活跃 Builder 是否阻断旧帧随机读取 | 不阻断已完成前缀内的指定地址读取；未完成/跨边界先拒绝，扫描相关资格未放宽 |
| 命名 fork 是否允许公开未绑定新 ref | 组合入口一次发布完整 RefId 目录，新 ref/初始名称一起可见；手工两步仍不是事务 |
| 是否先持久占上 Creating 名称 | 不采用；全局名称预检至提交均串行，初始/alias 绑定使用统一 names 表示 |
| 基础地址是否保持 16B 或带 SmartPointer 见证 | 固定 12B codec，完整 uint FileId + SizedPtr；无预留/内容 CRC，内存布局独立选择 |

以上各项统一见 [S0](00-architecture-decisions.md) 与相应 [S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。本表只导航，不建立第二套规范。

## 优先讨论的问题

### D1：独立 FrameLog 扩展是否接纳？

**已关闭的边界选择：** FrameLog 已从 S2 核心分离。候选合同、factory/用途字段、顺序/cursor、资源复用与验收要求统一见[扩展文档 FSLOG-Q1–Q6](extensions/framelog-candidate.md)。这些不是 S2-Q1/Q4 的日志阻断项。

**仍待讨论：** 是否存在独立有序帧序列的实际需求、是否接纳和何时实施。S4/S5 的首版无需该能力，不能因旧草案引用 FrameLog 就把它变成新栈必需能力。

**收敛标准：** 如接纳，用不认识 VersionStore 的最小消费者证明稳定追加序列和续读，保持核心独立；VersionStore 始终不消费具体应用中间帧的构建顺序。

### D2：已确认的构建自由如何映射到资源预算？

**涉及阶段：** [S2](02-framestore-core.md) 的 S2-Q4/Q5/Q7；[S3](03-framestore-batches-and-durability.md) 的 S3-Q1/Q2。

**已确认：** 多个活跃 Builder 各占一个 RBF 文件，支持嵌套、交错填充与不同申请次序的完成；同文件历史帧可在已完成前缀内串行随机读取，不因文件分配结果改变业务读取资格。未完成提前地址及包含帧后 Fence 的跨边界范围先于 I/O 拒绝；扫描/扫描边界/物理后继仍受各自 RBF Building guard。可分配文件中优先最低 FileId，忙/停止分配项跳过；无需 BeginNext 或提前规划尚未创建文件的位置。

**剩余难点：** 当前 RBF Builder 保留整帧缓冲，多个 Builder 的内存成本相加，同时占用文件句柄。大量提前地址不能假定低成本；取消后的文件复用、空闲文件选择和 reader 缓存也是资源策略，不是持久状态账本。

**已关闭的准入选择：** 未归还 Builder 数量上限由 config 文件提供，超限立即拒绝，不引入精确总内存或总句柄配额账本。成功 EndAppend 自动归还并释放配额，不等 Dispose。
**剩余工程定稿：** config 的位置/格式/默认/缺失或非法值/生效规则、一次性 Append 的短期准入、空闲句柄关闭策略和 owned Writer 类型。最低编号选择已定，不再讨论轮询/均衡。将来按 Builder 并行执行时，租借/归还、fault 与 barrier 的同步如何单独取得资格；本轮不提前实现这些并发机制。数量限制不等于总内存硬保证。

**收敛标准：** A、B 同时取得地址、先完成 B 后完成 A、再构建 Root 和统一确认的 public 轨迹；另覆盖 A 已完成而无关 B 仍 Building 时确认并发布 A，同文件历史随机读取与新帧拒绝、超预算、取消/reuse。若真实规模需要 planner，再形成独立扩展，不把它重新塞回首版核心前置。

### D5：必需首帧 meta/header 的最小内容与校验如何定稿？

**涉及阶段：** S2-Q6，连同 S2-Q1/Q3。

**当前状态：** 2026-10-05 用户确认采用，至少提供版本解释；具体内容授权 Coding Agent 研究决定，不再需要讨论是否采用。优先评估最小版本与 StoreId/FileId 绑定，暂不加入业务关系、发布序号、时间戳、可变状态或索引。

**剩余工程定稿：** 版本及识别字段、是否加入 StoreId/FileId、codec/tag、原生 RBF 扫描可见与 FrameStore inventory/audit 是否过滤；如何保留消费方 data tag 范围。日志扩展的扫描展示在该扩展单独定稿。
初始化必须在 creating 内完成，不能向 active 发布裸 RBF Header 后再补首帧。还需证明最小初始化边界、完整 CRC 检查、未知版本拒绝，以及 header 校验与 active 尾恢复的 public API 顺序。缺失/损坏必需 header 不能被自动补造，header 不替代 store 格式门或裸地址来源约束。可调整 Builder 数量不进入不可变 header。

**收敛标准：** 在已采用的决定下给出最小 wire/API、创建中断与冷重开轨迹；不增加未经需要的业务字段，也不把尚未研究的 codec 宣称为已经冻结。

### D6：目录协议与递增编号如何完整闭合？

**涉及阶段：** S2-Q1/Q2/Q3。

**已确认：** creating 内部初始化 → active；停止分配、flush/close → archive；三者同一文件系统、目标不覆盖；每桶 1024 个编号，位置变化不改变 FrameAddress。私有创建槽位最多一个，当前已租出状态只在内存。

**待定：** 精确路径/名称规范、store 独占入口、创建残留允许前缀和拒绝规则、Windows/Linux rename 实现与中断验收。选择递增 uint 的具体编号算法，证明最高归档桶为空、active/archive 编号交错、缺号、创建残留与编号耗尽时不会覆盖或误用既有文件；最大已发布编号必须覆盖两个集合，不假定 active 编号都高于 archive。
不存计数器的方向倾向从目录恢复最大已发布编号；成本含 active 与桶目录枚举和必要桶内检查。仍需比较实际预算，不能继承原单 locator 的历史规模无关 Open 承诺。

**收敛标准：** 创建、flush、关闭和移动各窗口只有一个可解释的文件位置；同编号两处同时存在明确拒绝；编号生成由实际文件证明且不回绕，冷启动成本公开。

### D7：成功提交、文件归还与归档失败如何对外表达？

**涉及阶段：** S2-Q4、S3-Q2/Q3。

**难点：** RBF EndAppend 已经成功，后续 flush/关闭/rename 仍可能失败。帧完成不能被撤销，也不能把归档失败伪装成安全可重试的未输出拒绝。Builder 值拷贝和旧 Writer 同样不能二次归还已被复用的文件。

**已确认：** EndAppend 正常成功返回前自动归还并释放数量配额，后续 Dispose 不二次归还；可纠正拒绝仍持有原租借。超阈值文件立即停止新分配，归还不把它重新加入可分配集合。
**剩余工程定稿：** 立即归档还是把物理维护放到后续受控入口、wrapper/错误签名、取消资源异常、共享 fault、Dispose 汇总和旧 epoch guard。屏障需要确认的 active/leased 文件登记与归档前 flush 证据如何衔接；leased 文件旧输出成功确认后，Builder 后续 EndAppend 必须重新登记新输出为未确认。自动归还的决定不单独决定维护时机。

**收敛标准：** 用一条完成后归档失败轨迹证明调用方不会误判帧撤销、自动重复追加或丢失完整事实；屏障覆盖全部必要完成输出且保留未完成 Builder，任一 flush 失败停用所有 owned Builder/Writer，旧 wrapper 不影响新租借。

### D3：根地址与自有历史快照的最小合同如何定稿？

**涉及阶段：** S2-Q1/Q4、S3-Q4；[S4](04-versionstore-publication.md) 的字典 codec / 身份 / 单文件创建；[S5](05-versionstore-names-and-indexes.md) 的历史生命周期。

**已确认：** 字典按值保存，地址解释于一个绑定的 data FrameStore，复用 S2 固定 12B codec。发布时应用保证全部必要对象已经完成，不能采用取消/未完成 ticket；data ConfirmDurable 不解析业务图，无关 Builder 未归还不阻断发布。屏障确认 leased 文件旧输出，不赋予其正在构建的新帧资格。旧 ref/tag 快照可以复用数据地址，不需要重新创建 Commit 或提交 parent。

**工程定稿：** RootMap / RefId / RefRevision 的 public 值类型、Key 编码/长度，以及消费 S2 地址 codec 的校验；VersionStore 格式门与 data StoreId 绑定；统一 RefId 容器的路径编码、私有残留、一次目录 rename 的平台入口与资格。基础地址布局和发布目录单位已确定，不再开放地址宽度或“先文件后补名字”的选择。每条记录容量用 RBF 公共尺寸 API，不承诺“总是 64B”。

历史枚举固定起始完成上界、活动期间禁止同 owner mutation；返回的完整 RootMap 自有，不依附池化 frame 或枚举器。RefRevision 只从完成发布/真实枚举取得；跨重开书签用 tag，不提前设计精确尝试身份或完整成员索引。

**收敛标准：** 选中非末快照，结束枚举后创建两 ref/tag/rewind；旧字典不随源 ref 更新变化。覆盖错误存储上下文、非法字段、完整坏 CRC、取消地址不能作为完成来源与中断恢复；不自动读取全部应用图或从 data 地址大小推算历史。

### D8：统一名称发现的成本与局部工程协议如何定稿？

**涉及阶段：** S4-Q3、S5-Q1/Q4、S6-Q3。

**已确认：** 每个正式 ref 容器的 names 保存同一种 branch binding，初始名称与 alias 不分两套来源；命名 fork 由一个目录共同发布，给既有 ref 的 alias 只发布一个绑定文件。S4 不读取 branch gate，S5 消费其私有初始化/目录发布步骤。所有名称创建全程串行查重，不需要正式 Creating 状态或通用日志。

**实际代价：** 冷名称解析、ListBranches 与全局重名检查要遍历全部正式 ref 目录和名称记录，包括未命名 ref；不能只检查目标目录，也不能遇到一个匹配就忽略重复/坏记录/枚举错误。无需加载全部 Snapshot 历史。允许完整 checked 扫描建立可重建内存表，但它不制造发布事实、不成为持久 catalog；具体是否采用由工作集/测量决定。

**剩余工程定稿：** 名称比较/安全路径/hash 碰撞、统一 checked codec、扫描与预算/失败/内存表生命周期，组合创建 API 与内部准备步骤复用；目录 rename 与 alias file rename 的 no-overwrite/进程中断资格分别获取。真实实验使用 W:，记录大量未命名 ref/少量 branch 的首查成本，不将未经测量的 O(1) 名称入口列为能力。

**收敛标准：** 同名创建先于 barrier/输出拒绝；组合发布前后 Read/List 的 ref 与 name 共同不可见/可见；alias 失败保留既有对象；只读不清理或采用私有残留。字段/API/平台默认由 Coding Agent 工程定稿，不能把逻辑原子解释为零残留或消除 Unknown。

### D4：发布未知与应用续跑如何衔接？

**涉及阶段：** S4 的 NotAttempted/Unknown/Confirmed 和局部恢复；S6 的应用最小流程。

**已确认：** 首版不提供 Prepare / 精确 Inspect。输出异常停止实例，重开读取实际当前字典、tag 或 branch；不能从同值字典推断某次旧调用曾得到 Confirmed。完整未发布数据可保留，但不会自行变成当前状态。

**工程定稿：** 普通 ref/tag 追加、新 ref/命名 fork 的目录发布与既有 ref alias 的文件发布具有不同确认边界。私有文件 flush 尚未建立可发现对象；最终 no-overwrite rename 后异常可能留下已成立对象，命名 fork 的两个公开资格仍共同成立。tag 桶 header-only 初始化只准备 metadata；空桶 rename 不确认首 tag，也不让尚未尝试 Append 的 tag 变成 Unknown。重开验收分别覆盖这些窗口，不能把目录维护异常伪装成安全可重试的未输出拒绝。

tool-loop 应用把 phase、冻结请求、operationId 和结果放在 Roots 闭包；外发之前确认 Started，Started 无结果使用 backend 查询/幂等或显式 uncertain 策略。Gym 把 cursor/RNG/规则/轨迹来源放在应用对象。异步回包由单 driver 按最新 generation 合并，不要求存储通用 CAS。

**收敛标准：** 根字典只恢复完整旧值或新值，结果已发布后不重复处理；应用未发布结果仍可能需要外部 uncertain 恢复。存储故障与外部调用的不确定性分开，不声称任意 tool exactly-once 或自动恢复丢失 LLM 响应。需求证据见[两类下游评估](reviews/2026-10-07-downstream-fit.md)。

## 其余定稿项与条件扩展

固定 12B 地址 codec 的公开入口、文本/错误与内部布局、格式门、阈值默认/变更、目录命名及平台 rename、资源释放、名称规则和分页预算仍在各阶段 Ready 表中定稿；基础地址宽度/字段端序已由 S2 锁定，已确认软阈值语义不表示所有参数都已冻结。
FrameAddress 不透明不等于支持帧重定位。目录归档只改变同一文件的位置；GC/compaction、逻辑 ID 映射、多线程执行、多个 data owner、跨实例 CAS 和更强断电模型在有需求时单独设计。多个 active 已是首版设计范围，不再列为未来条件扩展。
地址见证的重评触发条件分别为：裸地址跨 store 流转且需要概率性误用检测时考虑 store/address fingerprint；要求库自动拒绝取消预约的旧引用时另设计每帧持久 token 与重开发号/碰撞规则；仅引用已完成对象且需要预期内容见证时考虑内容 CRC。最终内容 CRC 在 Begin 尚不可确定，A↔B 会引入 checksum 依赖，不能作为当前提前稳定地址字段。上述方案均无首版预留字段或实施前置；无碰撞的来源、完成或耐久保证不能由额外 4B 自行推出。
ref 分段/轮转、随机 revision 读取、持久 cursor、差分/checkpoint、派生 tag 索引、名称 rename/unbind/delete/reuse、跨 ref 事务和精确尝试追踪不作为核心前置。业务 merge/provenance 由应用决定；出现无法用 RootMap 表达的明确需求后再扩展，不能从旧草案恢复候选功能。

## 下一轮建议顺序

2026-10-05 已关闭 header 是否采用、成功提交是否自动归还、是否设置 Builder 数量上限及超限拒绝/等待的选择。新增明确要求是通过 config 文件提供上限；字段和配置格式交给 Coding Agent 定稿。
FrameLog 去留在独立扩展中继续讨论，已不再是 VersionStore 首版依赖。S2 核心可直接围绕下表的工程问题推进；S4/S5 同样按已经收缩的模型关闭局部 codec / 路径 / 生命周期选择。

| 工程定稿 | 剩余内容 |
| --- | --- |
| D5 / S2-Q6 | 必需首帧 header 的最小字段/codec、识别与扫描规则、初始化及 CRC/修尾顺序 |
| D2 / S2-Q7 | config 文件位置/格式/默认/缺失或非法值/生效时机、配额计数与短期 Append 租借 |
| D7 / S2-Q4 | 已自动归还条件下的归档维护入口、错误与旧副本/epoch 行为 |
| D6 / S2-Q1–Q3 | 固定 12B codec 的公开入口/验证与内部布局、格式门、路径、编号恢复、store 独占与两平台 rename 实证 |
| D3 / S4-Q1–Q3、Q5–Q6 | RootMap/身份/codec、单文件 ref 创建和末读取、地址闭包责任示例 |
| D4 / S4-Q4 | append/flush/正式 rename 的结果证据与故障签名 |
| D8 / S5-Q1、Q4 | 全局名称发现/规模成本、统一 binding/hash/路径、alias 与命名 fork 组合发布 |
| S5-Q3 | tag 桶初始化/codec/扫描或惰性内存表，与命名 fork 独立 |
| S5-Q2 | 历史固定上界、owned 字典、枚举终止/预算/Dispose 与 mutation guard |

这些仍须在 Ready 前形成可操作协议，但属于可由 Coding Agent 负责提出并复核的工程定稿。无需把每个默认值或集合类型都升级为新的需求讨论；FrameStore/VersionStore 仍未开始新项目实施，不宣告已冻结 codec/config 或取得平台资格。RBF 底座改进的源码/测试资格单独报告。

核心工程定稿围绕 D5 的必需 header、D2 的 config、D6 的目录/编号和 D7 的维护错误展开。D3/D4 是随后字典/发布阶段的局部工程问题，不反向要求分配器理解应用 codec；不再等待 Commit 或全局日志协议。
结论写回所属阶段，按单向依赖复核；已确认方向不重复作为开放问题，本汇总不建立第二套规范。S2–S6 仍为 Draft，本次没有新项目或联合方案的恢复实证。
