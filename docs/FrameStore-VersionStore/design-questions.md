# 下一轮工程定稿：FrameStore 与根字典发布

日期：2026-10-04；2026-10-05 更新核心/扩展分离；2026-10-07 收缩 VersionStore、放宽已完成输出资格并确定命名 fork 的目录共同发布；2026-10-08 关闭基础地址宽度/字段编码选择；2026-10-09 同步 ForkOrigin；资源、header、owned 租借/归档维护、读结果/同步 inventory/audit 及 FileId 编号恢复定稿写回 S2，发布调用证据与续跑边界写回 S4/S6，同步历史 visitor/预算/清理及 tag 桶初始化/组合 schema/每次完整扫描写回 S5；已定设计移出本问题表，实施验证保留所属阶段。状态：**Informative / Derived；问题汇总，不是新增实施阶段或规范输入**。
输入为本轮会话和 S0–S6 的 Draft 合同。FrameStore/VersionStore 本轮仅修订文档，项目尚未创建；RBF 底座的活跃构建随机读取改进单独记录验收。S1 与 RBF1 参考隔离的已有 Accepted 资格保留原身份，不构成新模型已实施的证据。

## 已形成的方向

FrameStore 核心合同为不透明分配和随机读取；文件顺序只服务底层恢复。VersionStore 使用完整 RootMap、每 ref 单个 RBF3 文件、分桶不可变 tag 和独立 branch 绑定，不依赖应用中间帧构建顺序。
2026-10-04 已确认：三种 RBF 追加能力、单文件独占租借、多 Builder 可嵌套和交错完成、租借/归还串行、优先选择可分配的最低 active FileId、事后软轮转、active 目录与固定 1024 编号分桶归档，以及覆盖全部必要 active 输出的同步 owner barrier。首版不承诺多线程执行。
2026-10-05 追加确认：采用首帧 meta/header，内容交由 Coding Agent 研究决定；成功 EndAppend 自动归还文件；只设可配置的未归还 Builder 数量上限、超限立即拒绝，并通过 config 文件提供设置。
2026-10-07 追加确认：ConfirmDurable 确认调用时全部必要完成输出，未归还 Builder 不阻断屏障、不获得完成/耐久资格；本根依赖已完成时可独立发布。Builder 活跃期间同文件已完成前缀可随机读取，新帧仍不可读。串行、完整内容校验、生命周期与共享 fault 保持，扫描相关 guard 不变。
同日追加确认：命名 fork 在私有 RefId 容器同时准备 ref 与初始 binding，flush/close 后一次目录 rename 共同发布；所有 branch aliases 放同一种 names 文件。无公开 Creating 名称或永久 branch gate，私有残留不可查询，普通 CreateRef 与手工两步仍保留。正式定义见 S0 `[S-VS-NAMED-FORK-ATOMIC]`、S4 `[S-VS-REF-DIRECTORY-PUBLISH]` 与 S5 `[S-VS-NAMED-FORK-PUBLISH]`。
2026-10-08 用户确认：FrameAddress 首版固定编码为 12B，保持完整 uint FileId 与 SizedPtr；额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。S0 `[S-FS-ADDRESS-FIXED12]` 与 S2 `[F-FS-FRAME-ADDRESS-12B]` 已关闭基础地址宽度和二进制字段布局；内部 struct、公开 codec 入口、文本/错误及平台验证仍为工程定稿。
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
| 可写集合是否另存 manifest | active 目录是事实，archive 位置表示已按协议 flush 并封存 |
| 归档寻址是否要全历史分段表 | 1024 编号一个桶，按 FileId 高低位计算 |
| 如何恢复下一个 FileId | S2 已定完整流式正式名称发现，取 active/archive 实际最大编号；只保留内存 max，空桶/私有槽不计、缺口不补，耗尽只拒绝新文件；目录成本与实施向量留在 S2 |
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
| 是否需要持久反向分叉索引 | ListForks 扫全部正式首帧声明，含匿名 ref；只可派生内存图，不写源孩子表 |
| tool-loop 是否需要存储精确尝试 token / CAS | 单 driver + 应用 phase/operationId/generation；首版重开读实际状态 |
| 发布失败证据和应用续跑如何衔接 | S4 已定 Result/必选 out 三态、公开尝试/确认边界及故障清理；重开恢复实际状态，不精确认领匿名旧调用，S6 保留外部 uncertain 政策 |
| 无关 Builder 是否阻断根发布 | 不阻断；应用保证本根新增依赖完成，data 先确认全部必要完成输出，根后发布 |
| 活跃 Builder 是否阻断旧帧随机读取 | 不阻断已完成前缀内的指定地址读取；未完成/跨边界先拒绝，扫描相关资格未放宽 |
| 读结果与物理检查如何交付 | S2 已定自有 FrameRead、纯值 inventory、完整 CRC audit 与同步 visitor guard；不外泄 reader/枚举器，不以部分回调或结构扫描称全库内容健康 |
| 历史选点与遍历预算如何交付 | S5 已定同步 bool visitor、自有 RefSnapshot、返回数/工作步预算与单次调用清理；正常 Complete/VisitorStopped，预算专用失败，不外泄 sequence/epoch/cursor |
| tag 桶如何初始化/校验、是否需要索引 | S5 已定完整 header 初始化边界与恢复保护、统一版本组合 schema、每次全桶 checked 扫描及临时 fullname 集合；不保留跨调用索引，基础名称/RootMap codec 仍消费 S5-Q1/S4-Q2 |
| 命名 fork 是否允许公开未绑定新 ref | 组合入口一次发布完整 RefId 目录，新 ref/初始名称一起可见；手工两步仍不是事务 |
| 是否先持久占上 Creating 名称 | 不采用；全局名称预检至提交均串行，初始/alias 绑定使用统一 names 表示 |
| 基础地址是否保持 16B 或带 SmartPointer 见证 | 固定 12B codec，完整 uint FileId + SizedPtr；无预留/内容 CRC，内存布局独立选择 |

以上各项统一见 [S0](00-architecture-decisions.md) 与相应 [S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。本表只导航，不建立第二套规范。

## 优先讨论的问题

### D6：精确目录、独占与私有残留协议如何闭合？

**涉及阶段：** S2-Q1/Q2/Q3。

**已确认：** creating 内部初始化 → active；停止分配、flush/close → archive；三者同一文件系统、目标不覆盖；每桶 1024 个编号，位置变化不改变 FrameAddress。私有创建槽位最多一个，当前已租出状态只在内存。

**待定：** 精确 canonical 路径/名称规范、store 独占入口、创建残留允许前缀和拒绝规则、Windows/Linux rename 实现与中断验收。路径 codec 必须为编号给出唯一位置并拒绝等值别名/错桶；残留资格必须证明只是从未签发地址的私有初始化，不能仅凭合法 header 删除未知内容。
编号恢复、空桶/缺口/交错、发布后 max 登记及耗尽边界已定于 S2 `[S-FS-DIRECTORY-STATES]`，不再作为待定算法；O(A+B+H) 的完整正式名称枚举及两平台/实际规模验证留在 S2-Q2/Q3，不继承旧 locator 的历史规模无关 Open 承诺。

**收敛标准：** 创建、flush、关闭和移动各窗口只有一个可解释的文件位置；同编号两处同时存在明确拒绝；独占与残留范围可执行，精确路径消费已定编号算法，冷启动与平台资格各自取证。

### D3：根地址与自有历史快照的最小合同如何定稿？

**涉及阶段：** S2-Q1/Q4、S3-Q4；[S4](04-versionstore-publication.md) 的字典 codec / 身份 / 单文件创建；消费 [S5](05-versionstore-names-and-indexes.md) 已定历史合同。

**已确认：** 字典按值保存，地址解释于一个绑定的 data FrameStore，复用 S2 固定 12B codec。发布时应用保证全部必要对象已经完成，不能采用取消/未完成 ticket；data ConfirmDurable 不解析业务图，无关 Builder 未归还不阻断发布。屏障确认 leased 文件旧输出，不赋予其正在构建的新帧资格。旧 ref/tag 快照可以复用数据地址，不需要重新创建 Commit 或提交 parent。

**工程定稿：** RootMap / RefId / RefRevision 的 public 值类型、Key 编码/长度，以及消费 S2 地址 codec 的校验；VersionStore 格式门与 data StoreId 绑定；统一 RefId 容器的路径编码、私有残留、一次目录 rename 的平台入口与资格。基础地址布局和发布目录单位已确定，不再开放地址宽度或“先文件后补名字”的选择。每条记录容量用 RBF 公共尺寸 API，不承诺“总是 64B”。

历史枚举固定起始完成上界，沿不可变 ForkOrigin 接续各源选中位置及其更早历史；活动期间禁止同 owner mutation，返回的完整 RootMap 自有。RefRevision 只从完成发布/真实成员 checked-read 取得；来源元数据不自动签发 checked revision。来源感知 fork 先重读源字典，在子输出前完成 data 屏障与源文件 flush；只传 roots 的创建无来源。
剩余工程定稿为 header 来源判别/RefId codec 及与固定 8B SizedPtr 的组合。历史入口、自有值交付、跨文件资源/visited、返回/定位工作预算和终止已定于 S5 `[A-VS-REF-HISTORY-CHECKED]`，直接消费而非重新选择。现有 RBF 只有 EOF 逆扫，旧 fork 点的源后缀定位可能增长并支付工作步；无关后缀 payload 不作历史审计，已观察的结构错误仍传播。完整无来源起点、正常选点停止、预算失败、取消与错误按 S5 区分。

**收敛标准：** 选中非末快照，结束枚举后 fork/tag/rewind；源继续更新、fork-of-fork、同值初始与源不同 revision，完整继承前缀保持正确。覆盖源真实成员、错 length、缺源/循环/字典不符、源 flush 失败、定位预算/错误和跨文件释放；不自动读取全部应用图或从 data 地址大小推算历史。

### D8：统一名称发现的成本与局部工程协议如何定稿？

**涉及阶段：** S4-Q3、S5-Q1/Q4、S6-Q3。

**已确认：** 每个正式 ref 容器的 names 保存同一种 branch binding，初始名称与 alias 不分两套来源；命名 fork 由一个目录共同发布，给既有 ref 的 alias 只发布一个绑定文件。S4 不读取 branch gate，S5 消费其私有初始化/目录发布步骤。所有名称创建全程串行查重，不需要正式 Creating 状态或通用日志。

**实际代价：** 冷名称解析、ListBranches 与全局重名检查要遍历全部正式 ref 目录和名称记录，包括未命名 ref；不能只检查目标目录，也不能遇到一个匹配就忽略重复/坏记录/枚举错误。无需加载全部 Snapshot 历史。允许完整 checked 扫描建立可重建内存表，但它不制造发布事实、不成为持久 catalog；具体是否采用由工作集/测量决定。
ListForks 同样覆盖全部正式 ref，但读 header/初始边界而非 names；图存在性/环检查与完整集合资格须明确。返回首帧声明边不审计每条源 ticket，不能宣称源历史健康；声明边与实际历史跳转分别验收，避免廉价图发现隐含全部历史扫描。

**剩余工程定稿：** 名称比较/安全路径/hash 碰撞、统一 checked codec、扫描与预算/失败/内存表生命周期，组合创建 API 与内部准备步骤复用；目录 rename 与 alias file rename 的 no-overwrite/进程中断资格分别获取。真实实验使用 W:，记录大量未命名 ref/少量 branch 的首查成本，不将未经测量的 O(1) 名称入口列为能力。

**收敛标准：** 同名创建先于 barrier/输出拒绝；组合发布前后 Read/List 的 ref 与 name 共同不可见/可见；alias 失败保留既有对象；只读不清理或采用私有残留。字段/API/平台默认由 Coding Agent 工程定稿，不能把逻辑原子解释为零残留或消除 Unknown。

## 其余定稿项与条件扩展

固定 12B 地址 codec 的公开入口、文本/错误与内部布局、格式门、阈值默认/变更、目录命名及平台 rename、名称规则仍在各阶段 Ready 表中定稿；基础地址宽度/字段端序、owned 租借/维护/清理及读结果/同步 inventory/audit 已由 S2 锁定，历史入口/两项预算/终止及 tag 桶初始化/组合 schema/每次完整扫描已由 S5 锁定，已确认软阈值语义不表示所有参数都已冻结。
FrameAddress 不透明不等于支持帧重定位。目录归档只改变同一文件的位置；GC/compaction、逻辑 ID 映射、多线程执行、多个 data owner、跨实例 CAS 和更强断电模型在有需求时单独设计。多个 active 已是首版设计范围，不再列为未来条件扩展。
地址见证的重评触发条件分别为：裸地址跨 store 流转且需要概率性误用检测时考虑 store/address fingerprint；要求库自动拒绝取消预约的旧引用时另设计每帧持久 token 与重开发号/碰撞规则；仅引用已完成对象且需要预期内容见证时考虑内容 CRC。最终内容 CRC 在 Begin 尚不可确定，A↔B 会引入 checksum 依赖，不能作为当前提前稳定地址字段。上述方案均无首版预留字段或实施前置；无碰撞的来源、完成或耐久保证不能由额外 4B 自行推出。
ref 分段/轮转、随机 revision 读取、持久 cursor、差分/checkpoint、派生 tag 索引、名称 rename/unbind/delete/reuse、跨 ref 事务和精确尝试追踪不作为核心前置。业务 merge/provenance 由应用决定；出现无法用 RootMap 表达的明确需求后再扩展，不能从旧草案恢复候选功能。

## 下一轮建议顺序

已确定的 header、config、资源、owned 租借/归档维护及读结果/同步物理检查直接消费 S2，历史 visitor/预算/清理和 tag 桶初始化/全扫描直接消费 S5；实现验收保留在相应阶段，不把已定字段和流程重新列为待定设计。
S2 核心可直接围绕下表的工程问题推进；S4/S5 同样按已经收缩的模型关闭局部 codec / 路径 / 生命周期选择。

| 工程定稿 | 剩余内容 |
| --- | --- |
| D6 / S2-Q1–Q3 | 固定 12B codec 的公开入口/验证与内部布局、格式门、精确路径、store 独占/私有残留与两平台 rename 实证；编号恢复直接消费 S2 已定合同 |
| D3 / S4-Q1–Q3、Q5–Q6 | RootMap/身份/codec、ForkOrigin、单文件创建和末读取、源成员及确认边界 |
| D8 / S5-Q1、Q4 | 全局名称/ListForks 发现与规模成本、统一 binding/hash/路径、来源感知匿名/命名创建 |
| S5-Q1（tag 局部） | 名称政策/编码/限额、与比较相等关系一致的固定 hash/桶路由/规范路径；RootMap/身份 codec 消费 D3，桶初始化/组合/扫描合同不再重选 |

其余工程选择仍须在 Ready 前形成可操作协议；已定资源、文件 header、owned 生命周期及读/同步物理检查合同见 S2，S2-Q4/Q5/Q7、S3-Q1–Q3 保留对应实现验收，不重复开放其字段和流程。已定发布证据载体、Append/flush/rename 边界、清理和续跑责任见 S4/S6，S4-Q4 保留故障与冷重开实施验收，不再作为待定设计。S5 的同步历史 visitor、预算、终止与清理，以及 tag 桶初始化/组合 schema/每次完整扫描直接进入实现验证；S5-Q2/Q3 不再列为待定设计，基础名称/hash/路径与 RootMap/身份编码仍分别在 S5-Q1/D3 定稿。无需把每个默认值或集合类型都升级为新的需求讨论；FrameStore/VersionStore 仍未开始新项目实施，文档定稿不代表恢复、性能或平台资格。RBF 底座改进的源码/测试资格单独报告。

下一轮核心优先解决 D6 的精确目录/独占/私有残留，下游 codec/历史/名称问题按表推进；编号恢复与资源、header、owned 租借/维护和读/同步物理检查按 S2 进入实施验证。D3 是随后字典/发布阶段的局部工程问题，不反向要求分配器理解应用 codec；不再等待 Commit 或全局日志协议。
结论写回所属阶段，按单向依赖复核；已确认方向不重复作为开放问题，本汇总不建立第二套规范。S2–S6 仍为 Draft，本次没有新项目或联合方案的恢复实证。
