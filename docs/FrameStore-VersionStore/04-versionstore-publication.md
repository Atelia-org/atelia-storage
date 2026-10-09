# S4：VersionStore 完整根字典与单文件 ref 发布

状态：**Draft；2026-10-07 采用完整 RootMap、每 ref 一个 RBF3 文件与统一 RefId 目录发布，允许独立完成闭包发布；2026-10-08 同步 S2 固定 12B 地址；API、其余 codec、路径编码与平台协议尚未实施/冻结，项目尚未创建**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)。名称与历史见 [S5](05-versionstore-names-and-indexes.md)。

## 目标、归属与范围

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份。借入一个 data FrameStore，拥有独立的 RBF3 发布目录及其资源；直接使用主线 FrameStore/Rbf，不引用冻结旧栈或业务库。
发布目录格式门持久绑定格式版本、VersionStoreId、DataStoreId。Open 在任何发布文件恢复/写入前核对借入 data 的身份与访问模式；发布位置不解释为 data FrameAddress，data 地址也不解释为 ref revision。
单 owner/driver 串行操作，包括借入 data 的相关操作；发布目录须排斥另一 writer。VersionStore Dispose 释放私有文件、枚举器和锁，不 Dispose 借入 data。data 在使用期间必须存活；首版可写 VersionStore 借入可写 data owner，这是模式准入，不表示每个 mutation 都调用 data 屏障。资源、只读模式配对、借用与 fault 的具体公开接口在 Ready 时定稿。

首版不创建独立 Commit 对象，不保存 Parent，不采用 Prepared handle、nonce 账本、默认 CAS 或精确 InspectPublication。不提供跨 ref 原子事务；业务谱系、随机数状态、tool-loop 阶段、operationId 和外部副作用协议由应用保存和解释。

## term `Root-Map` 应用命名的根地址字典

`RootMap = string => FrameAddress` 的完整值快照。多个根在同一发布帧内一起改变；地址指向同一个绑定的 data FrameStore。key 及其含义属于应用，VersionStore 不识别 State、树或图。

### spec [S-VS-ROOTS-OPAQUE] 完整根字典由应用解释

RootMap MUST 使用唯一的 Ordinal key，不做隐式大小写折叠或 Unicode 归一化。空字典合法，且不同于 missing ref/tag。发布 MUST 先私有复制输入并编码为完整快照，避免调用方后续修改输入改变本次请求或返回值。
库校验字典、key 和地址的格式/容量，并核对 VersionStore/data 格式门的身份及实例生命周期。FrameAddress 继承 S2 的局部地址合同：裸地址不携带原始 StoreId，库不能识别来自另一 store 的恰巧合法坐标。应用 MUST 保证所有引用属于该 data，所需新增依赖已完成，闭包不含 provisional/canceled 地址。数字地址本身不能证明完成来源；库 MUST NOT 把合法坐标或 data barrier 当成整个业务图已成立的证明。读取物理完整 orphan 后采用它，仍需由应用重新建立闭包资格。
读取返回自有 RootMap 值；不持有 RBF pooled frame、Span 或枚举器临时缓冲。地址背后的内容由应用按 FrameStore.ReadFrame 完整读取与解释，VersionStore 不预读/遍历业务图。

## term `Ref-Revision` 已接受快照的位置身份

RefId 是正式 ref 目录及其唯一 RBF3 文件的稳定身份，与 branch name 分离。RefRevision 是 opaque 值，绑定 VersionStoreId、RefId 和该文件中实际完整快照位置及长度；不可与 data 地址互换。
revision 只从完成发布或实际 checked-read 的当前/历史快照取得，不在输出前签发。不同完整物理快照即使 RootMap 同值也具有不同 revision。它不是某次未完成调用的尝试 token；首版不提供凭裸位置随机 ReadRevision，也不以字典相同证明某次调用成功 flush。未来分段扩展仍保持其 opaque 边界。

## 单文件存储与最小 codec

### spec [S-VS-REF-FILE-SINGLE] 每个 ref 保存一个完整快照序列

每个 ref MUST 使用按唯一 RefId 命名的一个 RBF3 文件。首版不分段、轮转、差分或建立派生 checkpoint；每次更新追加一条完整 RootMap Snapshot。当前值只需校验文件 header 并完整 CRC 读取紧贴 EOF 的末 Snapshot，不扫描全部历史。
选尾 MUST 使用真实 RBF 逆向主链和 `showTombstone: true`，检查末帧紧贴实际尾部，不能将过滤结果或随机 ticket 当成末快照成员证明。未知 kind/version、tombstone、非 Snapshot 尾帧、完整坏 CRC/codec 皆报错，不向前寻找旧快照替代。header 与首次 Snapshot 的位置/绑定在工程定稿时明确。
单帧容量与下一帧起点硬界继承 RBF/SizedPtr；下一帧起点超过 SizedPtr.MaxOffset 时明确拒绝追加或要求维护，MUST NOT 隐式回绕、复用完整历史位置或自行拆成多帧发布。MaxOffset 只限制帧起点，最后一个合法帧的末端与尾 Fence 可以越过它，不额外要求文件总长度落在该起点上界内。容量预检使用 RBF 公共 Measure/追加预算 API，并分别检查追加起点；预算 API 不自行验证文件起点，不能把 MaxOffset - TailOffset 当作硬性追加字节预算。

### spec [S-VS-REF-DIRECTORY-PUBLISH] 新 ref 由完整容器目录发布

本条遵循 S0 `[S-VS-NAMED-FORK-ATOMIC]`。所有 ref MUST 使用统一的 RefId 容器目录；结构角色如下，RefId 的路径编码在 Ready 定稿：

| 路径角色 | 内容与资格 |
| --- | --- |
| `refs/<RefId>/` | 唯一正式容器；目录发布为该 ref 的发现边界 |
| `refs/<RefId>/<RefId>.rbf` | 本 ref 的唯一 RBF3 header 与 Snapshot 序列 |
| `refs/<RefId>/names/` | 为后续名称记录保留的子目录；可为空，内容由名称层解释 |
| `creating/<RefId>/` | 正式 refs 集合外的私有创建槽位；不得作为 ref 查询或列举结果 |

创建 MUST 在私有槽位完成本层 header 与初始 Snapshot，以及本次组合初始化的所有附加文件；各文件均须 flush/close 后，才能以同文件系统、不覆盖的一次目录 rename 发布完整容器。不得先创建正式空目录再逐个移动文件，不接受跨卷复制/删除替代该发布步骤。普通 CreateRef 的 names 为空；后序层可在同一私有容器准备其初始名称文件，再消费同一个目录发布步骤，不公开 Prepared handle 或应用 callback。
ReadRef / PublishRef / ListRefs 及后序历史访问 MUST 仅依据正式 RefId 目录定位，不探测或采用 creating 内容。ListRefs 仍可列出由普通 CreateRef 成立的未命名 ref；列举不是全部历史/名称健康审计。正式容器缺少必需 ref 文件/header/初始 Snapshot，或必要内容损坏，MUST 报错，不能当私有准备、补成空字典或删除正式容器。Ref 访问不依赖 names 文件或 branch gate；S4 不解释后序名称 codec。
实例异常或进程终止 MAY 留下私有槽位；其完整文件不自行成为 ref，也不自动补完或发布名称。只读打开 MUST 排除私有槽位且不清理；可写实例只可在独占 owner 下按可证明私有的路径/身份规则清理，清理不能跨入正式 refs。正式 rename 结果不确定时必须先按 `[R-VS-PUBLICATION-UNKNOWN]` 裁决实际位置，不以清理回滚已发布目录。
私有槽位布局、唯一 RefId 分配、残留清理及单次目录 rename 的 Windows/Linux no-overwrite/中断资格在 S4-Q3 定稿；不以 `Directory.Move` 方法名或普通 file rename 的资格代替目录实证。本轮故障模型仍限进程终止、OS/FS 继续运行。

候选编码只保留必要字段：

| 单元 | 必要内容 | 工程定稿 |
| --- | --- | --- |
| 根格式门 | version、VersionStoreId、DataStoreId | create-only 文件编码、模式、独占 owner 锁 |
| ref 首帧 meta/header | kind/version、ref 与 store 的身份绑定 | tag、字段、初始化边界及首次 Snapshot 定位 |
| Snapshot | kind/version、entry count、每项 key 与 S2 固定 12B FrameAddress | key 字符串/长度编码、其他字段端序及条目顺序；地址复用 S2 codec |

计数、key bytes、总记录尺寸必须有界，解码先检查剩余容量和重复 key，再分配；未知字段版本明确拒绝。上下文由格式门/header 绑定，不要求在每个地址内重复 StoreId。具体上限采用工程默认并写入接口/格式合同，不将“少量根约 64B”写成固定记录尺寸保证。
Snapshot 每个地址字段消费 S2 `[F-FS-FRAME-ADDRESS-12B]` 的唯一 codec，不重新选择地址宽度/端序，不添加未来预留或内容 CRC；tag 与历史快照复用相同 RootMap codec。条目尺寸包括 key 与完整 12B 地址，总记录容量使用 RBF 公共 Measure API；12B 地址不赋予对象闭包或原始 store 来源资格。

key 编码此前的 UTF-8 方向尚未冻结；Ready 时评估采用 [Bare Primitive Value](../Binary/bare-primitive-value.md) 的公共 string/VarInt 规则，并由 Snapshot 版本明确选择。该基础草案仍为 Draft，不在本轮冻结 RootMap 完整 wire，也不引入 Tagged 前置。

## 最小 API 与发布步骤

| 候选操作 | 输入 | 成功结果 |
| --- | --- | --- |
| CreateRef | 完整初始 RootMap；空字典可用 | 新 RefId 与初始快照/revision |
| ReadRef | 已发布 RefId | 自有 RootMap 与实际末 revision |
| PublishRef | RefId、完整新/旧 RootMap | 已确认新快照/revision |
| ListRefs | 本 VersionStore | 已发布 RefId，可发现未绑定 branch 的 ref |

具体 Result/异常和 PublicationOutcome 载体属于 Ready 工程定稿，不增加输出前 Prepared handle。正常 PublishRef 无 expected revision；串行 driver 自行管理应用工作流。今后若有实际冲突检测需要，可独立增加 revision CAS，不能用 RootMap 内容相等替代 revision。

### spec [S-VS-SINGLE-RECORD] 一次 ref 变更由一个 frame 生效

一次 ref 创建/更新 MUST 由一条完整 Snapshot 表达全部根值，不分成多个根的独立更新。data 构建/确认、私有初始化或纯容量试算均不改变已发布 ref。新 ref 另有正式命名的发现边界，遵循下面的创建协议。

### spec [S-VS-ROOTS-AFTER-DATA-CONFIRM] 根发布在必要完成数据确认之后

PublishRef 及 S5 CreateTag MUST 先完成确定输入、目标/名称及 owner 生命周期准入、私有拷贝与编码、记录/文件容量检查，再同步调用 `data.ConfirmDurable()`，然后向发布 RBF 追加完整记录并 `DurableFlush()`，最后安装内存状态/正常返回。MUST NOT 仅因 data 有未归还 Builder 而拒绝；未完成 Builder 不获完成或耐久资格。确定拒绝不追加、不调用 data barrier，也不因 guard fault。
data 屏障消费 S2 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`，覆盖 leased 文件内旧完成输出。应用仍 MUST 保证本次 RootMap 所需新增依赖全部已完成、属于绑定 data 且闭包不含 provisional/canceled 地址；屏障成功不证明该闭包，库不遍历业务图验证依赖闭包。无关 B 仍 Building 时，已完成的独立 A 可以发布；A 真正依赖未完成 B 时不能发布。
库不接受“以前已 flush”标志代替本次 barrier；旧 RootMap、历史 rewind、fork 初始快照和 tag 同样走屏障，无需复制 data。调用过程中不执行应用 callback，不允许同 driver 的其他应用操作交错。data flush 失败不得进入本次发布输出；发生异常按所涉 owner/fault 合同停用 VersionStore，不虚称未输出的另一个 owner 同样故障。
CreateRef MUST 先完成相同的输入/编码/容量及 owner 准入，调用 `data.ConfirmDurable()`，再按 `[S-VS-REF-DIRECTORY-PUBLISH]` 私有初始化并发布完整 RefId 容器；同样不要求无关 data Builder 归还。S5 的命名 fork 消费相同 data-first 准入与目录发布步骤，不得先调用公开 CreateRef 再补名称。清理不补造 ref，不另写 allocation/Init/Bind 账本。

## term `Publication-Outcome` 本次调用的证据

`NotAttempted / Unknown / Confirmed` 分别表示尚未尝试改变公开发布事实、公开事实可能已生效但无法确认、所需发布 flush 与发现边界均成功返回。它是调用证据；重开读到快照不能倒推过去调用是否成功返回或曾得到 Confirmed。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

更新/创建 tag 进入公开文件最终 Append 尝试后的异常 MUST 保守保留 Unknown，除非实际证据能证明 pre-I/O 拒绝；MUST 停用 VersionStore、释放私有资源并重开读取实际状态，不盲目重试、不回滚。
CreateRef、S5 命名 fork 与既有 ref 的 CreateBranch 在私有准备阶段失败仍未尝试公开事实；尝试正式目录/绑定文件 rename 后无法证明结果时为 Unknown。私有 flush 成功不能提前设置 Confirmed。tag 桶的 header-only 初始化（包括空桶 rename）只准备 metadata；即使其结果不确定，尚未尝试 tag Append 时本次 tag 仍为 NotAttempted，停用后重开裁决桶状态。更新/tag 在发布文件 DurableFlush 返回后、创建 ref/命名 fork/alias 在所需文件 flush/close 与最终正式 rename 成功后，必须先记录 Confirmed，再进行可能失败的内存安装/清理/正常返回；后续异常不得降级该证据。
精确调用证据的载体/异常获取方式在 S4-Q4 定稿；不承诺崩溃后查询某个旧尝试的 Present/Absent。Unknown 后相同 RootMap 也不能证明该次调用曾 flush；应用以重开取得的实际状态续行，并自行处理外部 tool 操作身份和重试规则。

### spec [R-VS-LOCAL-COMPLETE] 重开接受局部完整发布事实

可写重开先执行所属 RBF3 的结构恢复，再完整 CRC 读取并验证所需 header、实际末 Snapshot 或 S5 选中的 tag/name 记录。完整合法新 Snapshot 作为当前值；真正未完成残尾截掉后使用剩余末 Snapshot。正式新 ref 若已无初始 Snapshot，不得补成空字典。
新 ref 的目录发现与私有残留遵循 `[S-VS-REF-DIRECTORY-PUBLISH]`；没有正式容器时，私有完整 Snapshot 也不建立 ref。命名 fork 的初始名称随同一容器发布，名称层按该正式容器的记录读取，不能从私有槽位推断或续作旧调用。
完整坏 frame/未知版本/错误身份/非法 codec MUST 报错，不能当残尾、missing 或回退旧值。只读模式不隐式修尾。被引用 data 缺失/损坏在应用实际读取时报错，不据此改写已发布 RootMap。
Open 不默认扫描所有 ref 历史或 data 图；按需打开/校验目标文件，不将成功 Open 宣称为全库审计或全部根图健康证明。所有必要错误传播显式；资源/I/O fault 不被吞成缺对象。

## 生命周期与故障验收

| 位置 | 证据/状态 | 必要行为 |
| --- | --- | --- |
| 参数、owner 生命周期、历史枚举 mutation guard、编码/容量确定拒绝 | NotAttempted | 无发布写入/额外 barrier，健康实例可继续；无关 data Builder 不构成拒绝 |
| data ConfirmDurable 异常 | NotAttempted | data 按自身 fault，VersionStore 停止，无本次发布追加 |
| 已公开文件 Append/flush 异常 | Unknown，除可证明 pre-I/O 拒绝 | 停止；重开 checked-read，不自动 retry/rollback |
| 私有 ref 容器、命名 fork 的任一文件或 alias 绑定初始化/flush/close 异常 | NotAttempted | 尚未尝试本次业务事实；停止，允许私有残留，不删除正式对象 |
| tag 桶 header-only 初始化、flush/close 或空桶 rename 异常，尚未尝试 tag Append | NotAttempted（本次 tag） | 停止；重开检查桶 metadata，不删除已正式发布的合法空桶 |
| ref/命名 fork 的目录 rename 或既有 ref 的 alias 文件 rename 结果不确定 | Unknown | 检查实际正式对象；不因私有 flush 声称 Confirmed，不回滚目录 |
| 发布确认后内存安装/释放异常 | Confirmed | 停止、重开恢复；不撤销发布事实 |
| 正常返回后应用安装失败 | 已 Confirmed | 应用放弃旧 materialized state，从实际 RootMap 加载 |

## 实施片、Ready 工程定稿与出口

1. S4-A：项目、RootMap/RefId/RefRevision/结果值、格式门/header/codec 与 owned 生命周期。
2. S4-B：统一 RefId 容器的私有初始化及单次目录发布、ReadRef、PublishRef、ListRefs、data-first 屏障；先做一个 unnamed ref 的 public 纵向例子，名称内容由 S5 添加。
3. S4-C：末帧成员检查、CRC/未知版本、容量 guard、NotAttempted/Unknown/Confirmed、两种内存安装失败与冷重开。
4. S4-D：进程终止 failpoint、多根同时更新、空字典、同值新 revision、循环数据闭包与旧快照重新发布。真实文件实验使用 W:。

| Ready 项 | 需定稿 |
| --- | --- |
| S4-Q1 | RootMap/RefId/RefRevision 公开类型、比较与上下文；ListRefs 范围 |
| S4-Q2 | 格式门/header/Snapshot tag 与 codec、字符串/条目/总尺寸限额、消费 S2 固定 12B 地址 codec 的校验 |
| S4-Q3 | 统一 RefId 容器/creating 的路径编码、唯一身份与残留清理、目录 no-overwrite rename 两平台资格、独占/模式/Dispose/fault |
| S4-Q4 | 结果与异常证据载体、确定拒绝及 Append/flush/rename 不确定边界 |
| S4-Q5 | checked 末读取实现、资源预算、错误分类、已完成输出屏障对接及有无关活跃 Builder 的 public 发布轨迹 |
| S4-Q6 | 新/旧/orphan 地址的应用闭包责任示例与冷重开资格；不增加图遍历来源证明 |

验收覆盖错误 DataStoreId 在写入/恢复前拒绝、空/重复/超限 key、多根原子更新、旧/新帧混合、Builder 未完成/取消地址的应用合同、末帧/tombstone/TerminationError、完整坏 CRC 不回退、create-only 冲突、正式创建前后进程终止、合法末帧末端越过 MaxOffset / 下一次追加确定拒绝，以及借用 data 不被 Dispose。裸地址原始来源错误是应用合同向量，不宣称能由数值格式检查自动检测。源码与包/平台 qualification 分开，阶段仍 Draft。
Snapshot codec 向量包含多个连续 12B 地址、完整 FileId/Packed 高位、非法/截短地址拒绝和 cold round-trip；自有历史 RootMap 与 tag codec 消费同一格式，不从 CLR struct 大小计算 wire 容量。
目录向量覆盖私有 header/Snapshot/flush/close 各阶段、目录 rename 前后、正式目录缺少必要文件或坏身份、只读不采用/清理私有残留、重复 RefId 不覆盖。纯 S4 ref 消费不读取 names，也不需要 S5 codec；S5 的组合创建另验同一发布点的名称资格。
CreateRef/PublishRef/CreateTag 各覆盖 A 依赖全部完成而无关 B 仍 Building 时成功发布、B 所租文件中的旧 dirty 依赖在根写出前被确认、B 后续完成必须重新确认，以及任一 data flush 失败后不尝试根输出并停用 data 的全部 Builder/Writer。根真正依赖未完成 B 的负例属于应用闭包责任，不能把“库自动识别并拒绝任意未完成图”写成单测承诺。

