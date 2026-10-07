# S4：VersionStore 完整根字典与单文件 ref 发布

状态：**Draft；2026-10-07 按两个下游场景收缩为完整 RootMap 快照、每 ref 一个 RBF3 文件；API、codec 与目录协议尚未实施/冻结**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)。名称与历史见 [S5](05-versionstore-names-and-indexes.md)。

## 目标、归属与范围

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份。借入一个 data FrameStore，拥有独立的 RBF3 发布目录及其资源；直接使用主线 FrameStore/Rbf，不引用冻结旧栈或业务库。
发布目录格式门持久绑定格式版本、VersionStoreId、DataStoreId。Open 在任何发布文件恢复/写入前核对借入 data 的身份与访问模式；发布位置不解释为 data FrameAddress，data 地址也不解释为 ref revision。
单 owner/driver 串行操作，包括借入 data 的相关操作；发布目录须排斥另一 writer。VersionStore Dispose 释放私有文件、枚举器和锁，不 Dispose 借入 data。data 在使用期间必须存活；首版可写 VersionStore 借入可写 data owner，这是模式准入，不表示每个 mutation 都调用 data 屏障。资源、只读模式配对、借用与 fault 的具体公开接口在 Ready 时定稿。

首版不创建独立 Commit 对象，不保存 Parent，不采用全局 FrameLog、Prepared handle、nonce 账本、默认 CAS 或精确 InspectPublication。不提供跨 ref 原子事务；业务谱系、随机数状态、tool-loop 阶段、operationId 和外部副作用协议由应用保存和解释。

## term `Root-Map` 应用命名的根地址字典

`RootMap = string => FrameAddress` 的完整值快照。多个根在同一发布帧内一起改变；地址指向同一个绑定的 data FrameStore。key 及其含义属于应用，VersionStore 不识别 State、树或图。

### spec [S-VS-ROOTS-OPAQUE] 完整根字典由应用解释

RootMap MUST 使用唯一的 Ordinal key，不做隐式大小写折叠或 Unicode 归一化。空字典合法，且不同于 missing ref/tag。发布 MUST 先私有复制输入并编码为完整快照，避免调用方后续修改输入改变本次请求或返回值。
库校验字典、key 和地址的格式/容量，并核对 VersionStore/data 格式门的身份及实例生命周期。FrameAddress 继承 S2 的局部地址合同：裸地址不携带原始 StoreId，库不能识别来自另一 store 的恰巧合法坐标。应用 MUST 保证所有引用属于该 data，所需新增依赖已完成，闭包不含 provisional/canceled 地址。数字地址本身不能证明完成来源；库 MUST NOT 把合法坐标或 data barrier 当成整个业务图已成立的证明。读取物理完整 orphan 后采用它，仍需由应用重新建立闭包资格。
读取返回自有 RootMap 值；不持有 RBF pooled frame、Span 或枚举器临时缓冲。地址背后的内容由应用按 FrameStore.ReadFrame 完整读取与解释，VersionStore 不预读/遍历业务图。

## term `Ref-Revision` 已接受快照的位置身份

RefId 是正式 ref 文件的稳定唯一身份，与 branch name 分离。RefRevision 是 opaque 值，绑定 VersionStoreId、RefId 和该文件中实际完整快照位置及长度；不可与 data 地址互换。
revision 只从完成发布或实际 checked-read 的当前/历史快照取得，不在输出前签发。不同完整物理快照即使 RootMap 同值也具有不同 revision。它不是某次未完成调用的尝试 token；首版不提供凭裸位置随机 ReadRevision，也不以字典相同证明某次调用成功 flush。未来分段扩展仍保持其 opaque 边界。

## 单文件存储与最小 codec

### spec [S-VS-REF-FILE-SINGLE] 每个 ref 保存一个完整快照序列

每个 ref MUST 使用按唯一 RefId 命名的一个 RBF3 文件。首版不分段、轮转、差分或建立派生 checkpoint；每次更新追加一条完整 RootMap Snapshot。当前值只需校验文件 header 并完整 CRC 读取紧贴 EOF 的末 Snapshot，不扫描全部历史。
选尾 MUST 使用真实 RBF 逆向主链和 `showTombstone: true`，检查末帧紧贴实际尾部，不能将过滤结果或随机 ticket 当成末快照成员证明。未知 kind/version、tombstone、非 Snapshot 尾帧、完整坏 CRC/codec 皆报错，不向前寻找旧快照替代。header 与首次 Snapshot 的位置/绑定在工程定稿时明确。
单帧容量与下一帧起点硬界继承 RBF/SizedPtr；下一帧起点超过 SizedPtr.MaxOffset 时明确拒绝追加或要求维护，MUST NOT 隐式回绕、复用完整历史位置或自行拆成多帧发布。MaxOffset 只限制帧起点，最后一个合法帧的末端与尾 Fence 可以越过它，不额外要求文件总长度落在该起点上界内。容量预检使用 RBF 公共 Measure/追加预算 API，并分别检查追加起点；预算 API 不自行验证文件起点，不能把 MaxOffset - TailOffset 当作硬性追加字节预算。

候选编码只保留必要字段：

| 单元 | 必要内容 | 工程定稿 |
| --- | --- | --- |
| 根格式门 | version、VersionStoreId、DataStoreId | create-only 文件编码、模式、独占 owner 锁 |
| ref 首帧 meta/header | kind/version、ref 与 store 的身份绑定 | tag、字段、初始化边界及首次 Snapshot 定位 |
| Snapshot | kind/version、entry count、每项 key 与 FrameAddress | UTF-8/长度编码、端序、条目顺序、地址编码 |

计数、key bytes、总记录尺寸必须有界，解码先检查剩余容量和重复 key，再分配；未知字段版本明确拒绝。上下文由格式门/header 绑定，不要求在每个地址内重复 StoreId。具体上限采用工程默认并写入接口/格式合同，不将“少量根约 64B”写成固定记录尺寸保证。

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

### spec [S-VS-ROOTS-BARRIER] 根发布前同步确认数据

PublishRef 及 S5 CreateTag MUST 先完成确定输入、目标/名称准入、私有拷贝与编码、记录/文件容量和“data 无未归还 Builder”的 guard，再同步调用 `data.ConfirmDurable()`，然后向发布 RBF 追加完整记录并 `DurableFlush()`，最后安装内存状态/正常返回。确定拒绝不追加、不调用 data barrier，也不因 guard fault。
库不接受“以前已 flush”标志代替本次 barrier；旧 RootMap、历史 rewind、fork 初始快照和 tag 同样走屏障，无需复制 data。调用过程中不执行应用 callback，不允许同 driver 的其他应用操作交错。data flush 失败不得进入本次发布输出；发生异常按所涉 owner/fault 合同停用 VersionStore，不虚称未输出的另一个 owner 同样故障。
CreateRef MUST 先完成相同的输入/编码/容量及无 data Builder guard，调用 `data.ConfirmDurable()`，再在私有临时文件中完成 header 与初始 Snapshot、flush/close，最后以同文件系统 no-overwrite rename 发布唯一正式 RefId 文件。私有文件未公开时不是已创建 ref；正式路径成功安装才是发现边界。清理不补造 ref，不另写 allocation/Init/Bind 账本。目录/锁/生成唯一 RefId 的具体算法在 Ready 定稿。

## term `Publication-Outcome` 本次调用的证据

`NotAttempted / Unknown / Confirmed` 分别表示尚未尝试改变公开发布事实、公开事实可能已生效但无法确认、所需发布 flush 与发现边界均成功返回。它是调用证据；重开读到快照不能倒推过去调用是否成功返回或曾得到 Confirmed。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

更新/创建 tag 进入公开文件最终 Append 尝试后的异常 MUST 保守保留 Unknown，除非实际证据能证明 pre-I/O 拒绝；MUST 停用 VersionStore、释放私有资源并重开读取实际状态，不盲目重试、不回滚。
CreateRef 与 S5 CreateBranch 在私有文件阶段失败仍未尝试公开事实；尝试正式 rename 后无法证明结果时为 Unknown。私有 flush 成功不能提前设置 Confirmed。tag 桶的 header-only 初始化（包括空桶 rename）只准备 metadata；即使其结果不确定，尚未尝试 tag Append 时本次 tag 仍为 NotAttempted，停用后重开裁决桶状态。更新/tag 在发布文件 DurableFlush 返回后、创建 ref/branch 在 flush/close 与正式 rename 成功后，必须先记录 Confirmed，再进行可能失败的内存安装/清理/正常返回；后续异常不得降级该证据。
精确调用证据的载体/异常获取方式在 S4-Q4 定稿；不承诺崩溃后查询某个旧尝试的 Present/Absent。Unknown 后相同 RootMap 也不能证明该次调用曾 flush；应用以重开取得的实际状态续行，并自行处理外部 tool 操作身份和重试规则。

### spec [R-VS-LOCAL-COMPLETE] 重开接受局部完整发布事实

可写重开先执行所属 RBF3 的结构恢复，再完整 CRC 读取并验证所需 header、实际末 Snapshot 或 S5 选中的 tag/name 记录。完整合法新 Snapshot 作为当前值；真正未完成残尾截掉后使用剩余末 Snapshot。正式新 ref 若已无初始 Snapshot，不得补成空字典。
完整坏 frame/未知版本/错误身份/非法 codec MUST 报错，不能当残尾、missing 或回退旧值。只读模式不隐式修尾。被引用 data 缺失/损坏在应用实际读取时报错，不据此改写已发布 RootMap。
Open 不默认扫描所有 ref 历史或 data 图；按需打开/校验目标文件，不将成功 Open 宣称为全库审计或全部根图健康证明。所有必要错误传播显式；资源/I/O fault 不被吞成缺对象。

## 生命周期与故障验收

| 位置 | 证据/状态 | 必要行为 |
| --- | --- | --- |
| 参数、owner、Builder、编码/容量确定拒绝 | NotAttempted | 无发布写入/额外 barrier，健康实例可继续 |
| data ConfirmDurable 异常 | NotAttempted | data 按自身 fault，VersionStore 停止，无本次发布追加 |
| 已公开文件 Append/flush 异常 | Unknown，除可证明 pre-I/O 拒绝 | 停止；重开 checked-read，不自动 retry/rollback |
| 私有 ref/branch 初始化或其 flush/close 异常 | NotAttempted | 尚未尝试本次业务事实；停止并按私有资源范围清理 |
| tag 桶 header-only 初始化、flush/close 或空桶 rename 异常，尚未尝试 tag Append | NotAttempted（本次 tag） | 停止；重开检查桶 metadata，不删除已正式发布的合法空桶 |
| ref/branch 业务对象的正式 rename 结果不确定 | Unknown | 检查正式路径；不因私有 flush 声称 Confirmed |
| 发布确认后内存安装/释放异常 | Confirmed | 停止、重开恢复；不撤销发布事实 |
| 正常返回后应用安装失败 | 已 Confirmed | 应用放弃旧 materialized state，从实际 RootMap 加载 |

## 实施片、Ready 工程定稿与出口

1. S4-A：项目、RootMap/RefId/RefRevision/结果值、格式门/header/codec 与 owned 生命周期。
2. S4-B：私有初始化及正式创建、ReadRef、PublishRef、ListRefs、data-first 屏障；先做一个 unnamed ref 的 public 纵向例子。
3. S4-C：末帧成员检查、CRC/未知版本、容量 guard、NotAttempted/Unknown/Confirmed、两种内存安装失败与冷重开。
4. S4-D：进程终止 failpoint、多根同时更新、空字典、同值新 revision、循环数据闭包与旧快照重新发布。真实文件实验使用 W:。

| Ready 项 | 需定稿 |
| --- | --- |
| S4-Q1 | RootMap/RefId/RefRevision 公开类型、比较与上下文；ListRefs 范围 |
| S4-Q2 | 格式门/header/Snapshot tag 与 codec、字符串/条目/总尺寸限额、地址验证 |
| S4-Q3 | 目录、唯一文件命名、私有初始化/发现边界、独占锁、只读/可写/Dispose/fault |
| S4-Q4 | 结果与异常证据载体、确定拒绝及 Append/flush/rename 不确定边界 |
| S4-Q5 | checked 末读取实现、资源预算、错误分类、无 Builder guard 对接及 public 消费轨迹 |
| S4-Q6 | 新/旧/orphan 地址的应用闭包责任示例与冷重开资格；不增加图遍历来源证明 |

验收覆盖错误 DataStoreId 在写入/恢复前拒绝、空/重复/超限 key、多根原子更新、旧/新帧混合、Builder 未完成/取消地址的应用合同、末帧/tombstone/TerminationError、完整坏 CRC 不回退、create-only 冲突、正式创建前后进程终止、合法末帧末端越过 MaxOffset / 下一次追加确定拒绝，以及借用 data 不被 Dispose。裸地址原始来源错误是应用合同向量，不宣称能由数值格式检查自动检测。源码与包/平台 qualification 分开，阶段仍 Draft。

## 废弃合同导航

以下只保留历史锚点，不是首版能力；旧完整草案可从 Git 历史追溯。

### spec [S-VS-COMMIT-IMMUTABLE] 提交创建不改变 ref（DEPRECATED）

DEPRECATED；独立 Commit 对象退出首版，替代为 `[S-VS-ROOTS-OPAQUE]` 与 `[S-VS-SINGLE-RECORD]`。

### spec [S-VS-PARENT-LOGICAL] 提交祖先使用逻辑引用（DEPRECATED）

DEPRECATED；业务谱系归应用，替代为 `[S-VS-ROOTS-OPAQUE]`。

### spec [S-VS-PUBLICATION-ORDER] 控制顺序来自显式日志（DEPRECATED）

DEPRECATED；无全局控制日志，替代为 `[S-VS-REF-FILE-SINGLE]`。

### spec [A-VS-PREPARE-TOKEN] 输出前交付精确 token（DEPRECATED）

DEPRECATED；无输出前尝试 token/Prepared handle，替代为 `[S-VS-ROOTS-BARRIER]` 和 Publication-Outcome。

### spec [A-VS-TOKEN-CAS] CAS 与 handle guard 在 IO 前（DEPRECATED）

DEPRECATED；首版无默认 CAS/Prepared handle，确定 guard 改由 `[S-VS-ROOTS-BARRIER]` 定义。

### spec [S-VS-COMMIT-BARRIER] Commit 同步确认最新依赖再发布（DEPRECATED）

DEPRECATED；Commit/control 双层协议改由 `[S-VS-ROOTS-BARRIER]` 直接发布完整根字典。

### spec [R-VS-REPLAY-COMPLETE] 重开只解释完整控制事实（DEPRECATED）

DEPRECATED；全局回放改为 `[R-VS-LOCAL-COMPLETE]` 的局部 checked 读取。

### spec [A-VS-INSPECT-MAIN-CHAIN] 精确查询来自真实主链（DEPRECATED）

DEPRECATED；精确历史尝试查询退出首版，按 `[R-VS-PUBLICATION-UNKNOWN]` 重开读取实际状态。
