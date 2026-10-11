# S4：VersionStore 不透明根 payload 与 ref 发布

状态：**既有普通 ref 等范围的独立 source-only 源码资格 Accepted；CU 已正式接纳并取得本轮独立源码验收 Accepted**。普通 ref、身份/编号、创建/打开、ForkOrigin、PublicationOutcome 与只读 Audit 的既有实现及限定证据见[最终源码验收](12-versionstore-final-acceptance.md)；CU 增量见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。V4-F 与 S6-C/D 的既有进程/本地候选资格各自保留，旧六包候选不含 CU。后续见[实施计划](12-versionstore-implementation-plan.md)。
本文负责发布协议、身份与 ref envelope；时间编码见 [08 元信息专项](08-versionstore-record-metadata.md)，bytes API/所有权见 [11 公面专项](11-versionstore-root-payload-bytes-api.md)，可选 RootMap codec 见 [10 专项](10-versionstore-rootmap-payload.md)。首版版本为 1，不兼容未实施的旧草案。
前置：[S0](../FrameStore-VersionStore/00-architecture-decisions.md)、[S1](../Rbf/01-rbf-sized-append.md)、[S2](../FrameStore/02-framestore-core.md)、[S3](../FrameStore/03-framestore-interleaved-builders-and-durability.md)。历史与不可变 tag 见 [S5](05-versionstore-names-and-indexes.md)。

## 目标、归属与范围

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份。借入一个 data FrameStore，拥有独立发布目录、其中的 RBF3 帧文件及其资源；格式门是独立普通控制文件。直接使用主线 FrameStore/Rbf 与 Binary 基元，不引用冻结旧栈或业务库。
发布目录格式门持久绑定格式版本、VersionStoreId、DataStoreId 与不可变 ReservedRefIdUpperBound。Create/Open/OpenReadOnly 先消费 `[S-VS-DATA-BORROW]` 的入口可用性与模式检查；Open 再在任何发布文件恢复/写入前核对借入 data 的持久身份。发布位置不解释为 data FrameAddress，data 地址也不解释为 ref revision。
单 owner/driver 串行操作，包括借入 data 的相关操作；发布目录独立消费 `[S-VS-OWNER-LOCK]`，writer 排斥其他全部 VS owner，只读 owner 可共享。VersionStore Dispose 释放 owned 文件、当前遍历资源和锁，不 Dispose 借入 data。根、创建/打开、工作区与停用清理按下面条款执行，实际资源及平台资格随实施验收。

活动历史读取及活动 tag 元信息扫描期间的 mutation/reentry guard 分别消费 [S5](05-versionstore-names-and-indexes.md) 与 [09 专项](09-versionstore-immutable-tags.md)。mutation 仍先初始化 outcome=NotAttempted，拒绝须先于目标 I/O、barrier 与输出；允许的同步 callback 读取和独立临时资源槽不在本篇复制定义。

首版不创建独立 Commit 对象，不为每条 Snapshot 保存 Parent，不采用 Prepared handle、nonce 账本、默认 CAS 或精确 InspectPublication。ref 的创建来源由首帧 ForkOrigin 表达，文件内发布顺序仍由实际帧链表达。多个同库既有 ref 的共同发布由 [07 CU](07-versionstore-cross-ref-conditional-update.md) 定义；单 ref 普通发布保持 Snapshot，不创建/命名/tag/跨库事务，也不提供跨调用快照隔离。业务谱系、随机数状态、tool-loop 阶段、operationId 和外部副作用协议由应用保存和解释。

## VersionStore 格式门与 data 绑定

### spec [S-VS-DATA-BORROW] 入口检查与持续借用分别承担责任

Create、Open、OpenReadOnly MUST 接收实际借入的非 null data FrameStore；完成纯参数预检后，在本次 VersionStore 的任何文件/目录 I/O 前，调用 S2 `[A-FS-ENSURE-USABLE]` 的 `data.EnsureUsable()`，再读取并检查 `IsReadOnly`、捕获其 canonical StoreId。包括根/锁 bootstrap、门创建、发布文件恢复和 private 清理在内的副作用都必须在入口资格成功之后；不另开 data 路径、实例或读取其门来替代借入 owner。

| VersionStore 入口 | data 模式准入 | 绑定消费 |
| --- | --- | --- |
| Create / 可写 Open | MUST 为可写 data；只读 data 在 VS I/O 前拒绝 | Create 保存其实际 StoreId；Open 与 checked 门 DataStoreId 比较 |
| OpenReadOnly | 允许可写或只读 data | 与 checked 门 DataStoreId 比较；不因借入可写 data 而取得 VS 写入资格 |

只读 VersionStore 不修改、恢复或确认 data；调用方自行使用借入可写 data 仍受串行合同约束。模式准入不要求 data 零活跃 Builder、不确认输出、不完成或取消 Builder；发布时的完成依赖与 data barrier 继续按既有合同判断。VS 自身的根/锁、门建立和工作区另消费本篇已定合同。

入口 guard 的异常原样传播，不签发 VersionStore owner，不产生本次 VS 持久化副作用；后续模式拒绝、门/绑定或 VS 初始化失败也不得 fault/Dispose 健康的借入 data。已打开 data 在借入前按自身合同执行的恢复或输出不属于本次 VS 入口检查。公共 guard 已在 V0 实施，生产 VS 工厂仍须按上述顺序消费它，不能仅凭 `StoreId` / `IsReadOnly` 宣称已经完成生命周期检查。

调用方 MUST 在 VersionStore 借用期间保持同一 data owner 未 Dispose、未 fault，并将两层相关操作和 Dispose 串行化；不得在同步调用或其输入/visitor 回调中插入不受合同允许的借入 data 操作。入口检查不维持租借、不监控后续状态，不为所有纯发布目录查询新增 data 探测或违约诊断保证；真正的 data 读/屏障继续消费 FrameStore 自己的生命周期与 fault guard。data 后续 fault 时停止依赖它的 VS 流程，并按两层既有规则清理/重开；VS 操作已经触发 data/源确认异常时，仍按 `[A-VS-PUBLICATION-EVIDENCE]` 停用 VS，不改变 PublicationOutcome。

VersionStore 另提供公共 `void EnsureUsable()`，先检查自身 disposed、再检查自身 fault，最后调用实际 borrowed data 的 EnsureUsable。该组合检查无 I/O、恢复或耐久确认，允许 RO/RW、活跃 data Builder 和现有 visitor 状态；只是调用时的寿命资格，不维持持续租借或证明内容健康。借入 data 的 guard 异常原样传播，纯检查失败不额外 fault/清理 VS。内部纯发布目录查询仍只检查自身 owner，不改为自动探测 data；真正发布的 data barrier 异常继续按既有停用与 PublicationOutcome 合同处理。VersionStoreId/DataStoreId/IsReadOnly 等保存值不是当前可用性证明。

VersionStore Dispose 不调用此借入 guard、不 Dispose data；即使 data 已 fault/Dispose，仍必须尝试清理 VS 自己的资源。已交付的自有快照和值继续遵循各自生命周期，不因 borrowed owner 后续失效而撤销。

### spec [F-VS-OWN-FORMAT] 固定记录绑定发布库与借入 data

VersionStore MUST 有独立、create-only 的格式门；普通 ref/tag 发布不改变它。正式名称为精确小写 `versionstore.format`，是恰为 44 bytes 的普通文件，不是 RBF 文件：

| byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | VersionStoreFormatVersion | uint32 LittleEndian，首版为 1，选择本层目录/header/发布 envelope schema 及允许的 TailMeta 形状 |
| `[4,20)` | VersionStoreId | 16 个 canonical opaque bytes，非全零，发布库的稳定身份 |
| `[20,36)` | DataStoreId | 16 个 canonical opaque bytes，逐字节复制借入 data 已资格化的持久 StoreId 编码 |
| `[36,40)` | ReservedRefIdUpperBound | uint32 LittleEndian；记为 L，允许 0..uint.MaxValue−1，创建后不变 |
| `[40,44)` | CRC32C | 前 40B 的 CRC32C，uint32 LittleEndian；init/finalXor 均为 0xffffffff |

两份身份按各自角色逐字节比较，不检查 UUID 结构、不重排字节或 dump CLR struct，不要求两者字节不同。DataStoreId 直接消费 S2 身份编码；VersionStoreId 在初次创建时随机生成非零 128-bit canonical 值，见 `[R-VS-STORE-CREATE]`；满足该合同的 CLR 表示与具体调用由实现决定。FrameStore 与 VersionStore 的格式版本分别解释各自 schema，绑定不要求两层版本号相等。
CRC MUST 使用现成 `RollingCrc.SealCodewordForward` / `CheckCodewordForward`，传入完整 44B codeword；这是普通门记录的唯一 CRC，不加 Magic、第二版本、flags/预留、RefId、路径、集合或可变分配状态。L 是不可变编号域边界，不是 next counter/占号账本；RBF header 不追加这份 CRC。

**共同只读检查。** Open 与 OpenReadOnly MUST 共用一个内部流程。在 `[S-VS-DATA-BORROW]`、`[S-VS-ROOT-ADMISSION]` 与已持有的 `[S-VS-OWNER-LOCK]` 资格下，使用已从实际借入 owner 捕获的持久 StoreId。以 `FileMode.Open`、`FileAccess.Read`、`FileShare.Read` 打开正式门的临时 FileStream；同一句柄上先要求 Length 恰为 44，再 `ReadExactly` 到固定 44B 栈 buffer。先校验整个 codeword，再解码受支持版本、两份非零身份及合法 L，最后比较 DataStoreId 与借入 data 的身份。不同即拒绝，不按门字段重新寻找或替换 data、不比较 data 路径或实例引用来替代持久身份。
版本/身份/L 只暂存在局部自有值中；绑定合格且临时句柄关闭成功后，才交给后续 owner 初始化。关闭前取出/清空资源槽，复用 S2 `[S-FS-OWNED-FAULT]` 的主错误优先/单次清理规则；不外泄 buffer、stream 或公开门 API。任一检查、绑定或必要清理失败 MUST 先于本次 VersionStore 打开中的发布文件恢复、私有清理与输出结束，不签发 owner。截短、尾随、CRC/字段坏、未知版本、缺门与身份不匹配明确拒绝；权限/I/O 保留原错误，不通过 File.Exists 猜缺失。本流程不调用 data barrier、恢复或写入 data，不因本层门失败而 fault/Dispose 健康的借入 data；调用方在借入前独立打开 data 所做的合法恢复不受此顺序约束。

正式门 MUST NOT 修尾、追加、替换或从 ref/tag/data header 补造身份；缺坏时不采用私有候选、其他文件或自动创建。身份相符只证明所借 data 与门绑定一致，不证明裸地址原始来源、完成/耐久或业务闭包，也不认证外部改写。初次建立、根/锁和工作区消费下述独立合同；公开身份表示不增加新的生成权威。协议已定不构成 VersionStore 实现或平台验收。
（Informative）一个 VersionStore 借入一个 data 不推出每份 data 只能有一个 VersionStore；独立 VersionStoreId 继续区分其 header/revision 上下文，不建立反向持久登记。单帧 RBF 承载同一字段也可正确，但仍需字段 schema 和 profile/首帧形状/完整 CRC/唯一后界检查。门没有追加、历史或 ticket 消费者，首版选择定长读取与现成 codeword，不建立通用 Gate 框架或双载体 fallback；没有实测性能优劣。依据为 [公开 CRC codeword](../../src/Data/Hashing/RollingCrc.cs)及[独立 CRC/损坏测试](../../tests/Data.Tests/Hashing/RollingCrcCodewordTests.cs)，不是 VersionStore 已实施的证据。

## 发布目录的创建、打开与工作区

### spec [S-VS-ROOT-ADMISSION] 实际根与受管组件准入

调用方选择 root 及可信父路径；实现将路径绝对化，只检查根末组件的普通目录/no-follow 资格，不建立祖先句柄链、物理根别名表或硬链接去重。Create 可创建缺失末级根，父目录须已存在且合格，不递归创建祖先。根/父路径大小写不是持久格式；精确拼写要求只适用于本层受管组件。

版本 1 的必要布局为 `versionstore.lock`、`versionstore.format`、普通目录 `refs` 与 `creating`。实际直接目录项须具有精确名称和 ordinary/no-follow 资格；拒绝受管 symlink、junction/reparse、设备、FIFO 等特殊项，不以宽容路径查找或过滤枚举认领名称。已从实际枚举取得 canonical 名称的项仅再检查类型，不为每个文件重扫父目录。根还允许精确小写的可选普通目录 `tags`；存在时取得相同 ordinary/no-follow 与同文件系统资格，不枚举或审计其桶内容。该目录与正式桶路径由 [S5-B 专项](09-versionstore-immutable-tags.md) 定义，初次建库不预建目录或桶；缺失不由 Open/OpenReadOnly 补造。

VS 的 root、受管目录及其叶文件 MUST 取得同文件系统/mount 资格；private→formal MUST 使用原生同文件系统、不覆盖 file rename，不退回复制/删除或覆盖。平台采用 S2 `[S-FS-ROOT-ADMISSION]` 已有的严格机制：Windows 普通 disk/no-reparse 与 volume GUID，Linux statx 类型/device/mount identity 及 renameat2 RENAME_NOREPLACE；无法取得资格或未支持平台明确拒绝。VS 根与借入 data 根不要求同文件系统。

本条依赖非对抗、准入及持锁期间无外部受管项改写/替换的模型；`creating` 是仅由本协议写入的可丢弃工作区，应用和外部工具不得在其中保存或导入文件。检查不认证外部复制或目录冒充，Open 不建立整个 root 其他内容的永久白名单。现有 FrameStore 平台实现为 internal 且锁名固定，VS 应局部复用或移植窄机制；不要求公共平台/Gate 框架、裸 handle 导入或新增 RBF 前缀能力。其源码证据不自动成为 VS 平台验收。

### spec [S-VS-OWNER-LOCK] 独立永久控制文件协调 VS owner

版本 1 使用精确小写 `versionstore.lock`：永久保留的普通空文件，不是 RBF，不存身份、PID、busy、generation 或日志。实际名称、ordinary/no-follow、同文件系统及持有的同一句柄 Length 恰为 0 均 MUST 合格；缺坏时不截断或修复。锁不证明身份、不参与 RefId/H 或查询结果，借入 data 的锁不能替代它。

Create/可写 Open MUST 取得排斥其他全部 VS owner 的独占锁；OpenReadOnly 取得与只读 owner 兼容、与 writer 互斥的共享锁。Windows writer 使用 FileShare.None、reader 使用 FileShare.Read；Linux 在长期持有的同一 fd 上取得 checked nonblocking flock EX/SH，不依赖 BCL best-effort 锁。不启用 Delete、Inheritable 或 DeleteOnClose。冲突、unsupported 或其他严格锁失败直接拒绝，不等待、轮询或降级。

Create 完成纯参数及借入资格、fresh 输入预检后，才可 bootstrap 缺失末级根，并以不截断的 OpenOrCreate/ReadWrite 取得控制候选和独占锁；根与锁是获锁前仅允许的创建动作。获锁后 MUST 重新检查 fresh 输入，不能沿用锁前空根观察。Open 只 Open/ReadWrite 既有锁；OpenReadOnly 只 Open/Read，缺失时均不补造。

同一锁句柄覆盖门检查、目录发现、工作区裁决、文件恢复、owner 运行及受控清理，初始化到交付不解锁重开，不按操作重取锁。factory 失败、Dispose 或停用后的完整受控 teardown 都先单次尝试清理其他全部 owned 资源，最后取出/清空锁槽并关闭；关闭失败不跳过其他清理或重试已尝试对象。仅清理操作临时资源时不得提前释放仍由 owner 持有的锁。停用类 mutation 的完整 teardown 见 `[A-VS-PUBLICATION-EVIDENCE]`。

任何退出都 MUST NOT 删除、rename 或替换锁文件，避免不同 inode 同时持锁；进程终止由 OS 释放句柄锁，文件存在不表示 busy。它只协调遵守协议的 owner，不提供实时 writer–reader 观察或外部改写协调。

### spec [R-VS-STORE-CREATE] 初次建立空发布库，最后直接写正式门

Create MUST 为 create-only。纯参数（含合法 L）及 `[S-VS-DATA-BORROW]` 检查先于 VS I/O；fresh 输入只接受不存在末级根、空根或仅有合格空 `versionstore.lock` 的 bootstrap 根。已有门（含短坏门）、初始化目录、发布文件、private 或未知内容均拒绝，不据缺门收养或续作。bootstrap 与获锁重检消费 `[S-VS-OWNER-LOCK]`。

在实际根资格及独占锁下先准备空 `refs`、`creating`，检查这两个目录及锁的资格，再创建正式门；不创建 ref、tag 桶、配置或编号账本。为本次创建生成随机非零 128-bit VSID（可用 RandomNumberGenerator.Fill，遇全零重取），与已捕获的 DataStoreId、L、版本 1 编成既定完整 44B codeword。以 FileMode.CreateNew、FileAccess.Write、FileShare.None 直接创建正式 `versionstore.format`，从长度 0 顺序写完整记录，不预扩长或补字段；Flush(flushToDisk: true) 及单次 close 成功后才交付持原锁的空 owner，H=L。此操作不签发 RefId/revision，不调用 data barrier，不使用私有门、门 rename、初始化日志或读回阶段。

可打开事实是完整合法门与必要布局均存在；正常 Create 返回另要求写入、flush/close 与 owner 交付全部成功。完整门可能在这些步骤失败前已经存在，后续 Open 按实际门身份/布局准入，不能由它证明旧 Create 正常返回。仅消费 ProcessCrashOnly，不假设 44B 写原子或断电后的目录耐久。

| 中断后实际事实 | 后续解释 |
| --- | --- |
| 只有合格空锁，无初始化目录/门 | 无 store 身份；fresh Create 可复用 bootstrap |
| 布局已部分或全部建立，门缺失 | 无可打开 store；普通 Create/Open 不补布局或续作 |
| 门短于/长于 44B，或 CRC/版本/身份/L 不合法 | 拒绝；不修复、覆盖或从其他文件补身份 |
| 完整合法门与必要布局，旧 flush/close/交付失败或未返回 | 可按实际资格 Open，不撤销门、不倒推旧调用成功 |

任何初始化/交付失败都不签发 owner，按锁最后规则清理资源；不得删除正式门、锁或递归删除根/布局作回滚。不完整建库需要另择 fresh root 或显式外部维护；普通入口不增加 reset/resume，也不把它解释成 ref 的 PublicationOutcome。

### spec [R-VS-STORE-OPEN] 工厂资格顺序与惰性文件访问

Open/OpenReadOnly MUST 依次完成纯参数与借入检查、实际根/既有 owner 锁、共同只读门及 data 绑定检查（含必要 close）、必要布局检查。随后可写 Open 按 `[S-VS-REF-ID-DOMAINS]` 完整发现正式 ref 并在枚举/清理成功后取得 H，再裁决/取消工作区，最后交付 owner。OpenReadOnly 检查工作区并保留合格 pending，不为发号或 pending 全扫正式 ref；ListRefs/ListForks 的完整发现由各查询自行承担。

两种工厂都不默认打开/恢复全部 ref 或 tag 桶，不扫描历史/data 图。正式目标访问时才按 `[R-VS-LOCAL-COMPLETE]` 或 [S5-B 专项](09-versionstore-immutable-tags.md)的正式桶合同打开和检查。门/绑定、正式发现、工作区资格或取消及必要清理失败均不交付 owner；不安装 partial H、不恢复 pending、不 Dispose/fault 健康的借入 data。锁随失败工厂最后清理。

### spec [S-VS-CREATING-WORKSPACE] 唯一 pending 仅承载可丢弃初始化

`creating` 是正式集合外的专用工作区，全部直接项（含隐藏项）只允许空集合或唯一精确小写普通文件 `pending.rbf`。ref、fork 和 S5 tag 空桶初始化串行共用 `creating/pending.rbf`；路径不编码 role/目标 ID，不新增持久计划或尝试记录/其他私有槽，不提供并行初始化。文件内仍按本次操作写入正式格式所需的 header 与内容。

正常创建先完成目标、输入、容量、data/源确认等既定准入，再以 CreateNew 写 pending，按本次操作计划完成所需 header/初始内容、flush/close，然后 no-overwrite rename 到已前检的正式目标。计划只由本次调用掌握，不提前交付私有身份/revision；初始化、flush/close、rename 或其后安装异常停用并受控清理，不在旧 owner 续作或重试。

**冷开资格。** 在 checked 门/data 绑定、根与 owner 锁资格下，完整枚举 creating，取得精确唯一名称、普通文件/no-follow 与同文件系统资格；枚举及必要清理必须完整成功。未知名称、多项、错误类型或资格失败保留并拒绝。资格不读取 pending 内容，不限制其 Length、不检查 CRC/身份/角色/目标，不与 H、L 或正式目标作动态对照；这些字节及计划均未建立发布对象。

可写 Open 在正式 ref 发现完成后仅删除该合格 pending；删除失败拒绝 owner，不当成功取消。OpenReadOnly 原样保留，不删除、恢复、写入或采用它。完整 pending 与任意中断前缀同样未发布，不补 rename、不认领旧请求，不计 H。rename 已生效时源 pending 消失，正式 ref 或合法空 tag 桶保留；冷开取消不修改任何正式对象。

本条统一覆盖自动/指定 ref、fork 与 tag 空桶，取消资格独立于正常创建的 H+1/预留域/目标未占规则。放弃的仅是未发布工作和其诊断线索；恢复 private、保留事故现场、外部导入或并行初始化出现真实要求时另立合同。本规则不改变 FrameStore 私有 data 的前缀资格或 S5 正式桶初始化保护。

## 完整 root payload 与应用 codec

### spec [S-VS-ROOTS-OPAQUE] 完整 root payload 由应用解释

VersionStore 接收/返回完整、不透明的 root-payload bytes，整体替换而非差分。RootMap 是 [专项](10-versionstore-rootmap-payload.md)定义的首个可选 codec，应用也可自行使用 JSON/UTF-8。VersionStore 不猜 codec、检验 JSON/字典结构或增加每记录 codec tag；应用负责选择、记录和演进其内容 schema，并保证必要 data 依赖已完成、属于绑定 data 且闭包合法。应用 codec 在发布前编码、在取得自有 bytes 后解码；内容错误由应用报告，不回退旧值或撤销发布事实。

输入及结果所有权、最大长度与 API 唯一见 [11 公面专项](11-versionstore-root-payload-bytes-api.md)。FrameStore 借入、身份门与 data-first 仍保持；本轮不拆程序集、不解除绑定。

### spec [A-VS-ROOT-PAYLOAD] 发布记录的 root payload 与 codec 分离

普通 Snapshot 的整个 RBF payload 是 root-payload bytes；[CU](07-versionstore-cross-ref-conditional-update.md)由自身 envelope 定位 root bytes，排除 Members。发布资格仅检查格式/身份、kind、meta、真实主链位置、完整 CRC、11 专项的长度界及必要 CU 成员判据，不解码内容。无变更 fork 的 exact byte clone 与返回值由 S5 `[S-VS-FORK-FROM-REVISION]` 定义；继承历史的逐 byte 来源比较由 S5 `[A-VS-REF-HISTORY-CHECKED]` 定义。

### spec [A-VS-VALIDATION-SCOPE] 按操作依赖校验，完整检查独立施工

日常访问 MUST 只为本次结果建立所需资格，不将完整库/文件审计放入共同打开前置：

| 操作 | 必要内容与保证 |
| --- | --- |
| ReadRef | checked header 身份与逆向所访问 head 的完整资格；普通 Snapshot 直接交付，CU 按07共同判据成立才交付，未成立继续向前；不预读无关初始/中间 payload |
| ReadRefHistory / ForkRef | S5 的按需历史节点、精确源与必要来源 bytes 比较；历史早停不访问更旧节点 |
| ListForks | 全部 header 声明与来源集合/环检查；不读初始或末 payload |
| ResolveTag / CreateTag / ReadTagInfo | 09 的独立 checked 元信息与请求名称裁决；Resolve 命中另需完整 root CRC，元信息成功不证明 root 健康 |
| ScanTags | 09 的逐桶元信息扫描、Complete/VisitorStopped 与回调合同；不读 root 或证明全部名称唯一 |
| 独立完整校验 | 只读检查初始化、全部帧 CRC/envelope、全桶名称重复与 fork 来源 bytes 一致性；不解释应用 payload/data 业务闭包 |

独立完整校验使用下述同步 Audit 入口，不建立通用审计框架。日常成功不得宣称未访问内容健康；完整校验也不自动修复、丢坏帧、回退 head 或改变发布事实。结构不可安全推进、权限/I/O、必要恢复/清理及生命周期错误继续走各操作的失败合同。CU 的完整成员判据决定发布生效，当前值、历史、fork 和 Audit 共用07判据，不能推迟到独立校验。

### spec [A-VS-AUDIT] 独立只读完整检查

```csharp
AteliaResult<VersionStoreAudit> Audit(CancellationToken cancellationToken = default);
public sealed class VersionStoreAudit {
    public long RefCount { get; }
    public long SnapshotCount { get; }
    public long ForkCount { get; }
    public long TagBucketCount { get; }
    public long TagCount { get; }
}
```

结果由库内部构造，只含自有计数，无 setter、owner、lease 或修复能力。Audit MUST 在 `OpenReadOnly` 获得的 owner 上调用；可写 owner 在任何本次 I/O 前返回 `VersionStore.ReadOnlyRequired`。这使完整检查不会经可写惰性打开触发 RBF 恢复。已有 history/tag 扫描 callback 内调用分别返回 HistoryActive/TagScanActive；本操作没有 visitor、递归入口或部分成功报告。owner disposed/fault 先检查，取消抛 OperationCanceledException。

检查覆盖全部正式 ref 的合格 header、直接第二帧普通初始 Snapshot、每条后续 Snapshot/CU 的完整 CRC/envelope，以及每个文件的正常 EOF；拒绝未知 kind、tombstone、重复 header 和坏尺寸。物理完整 CU 无论其组是否成立都必须完整检查，合法未提交 CU 可通过但不计作发布快照。检查完整声明图的缺源与环；每条 ForkOrigin 的 exact SizedPtr（offset 和 length）MUST 命中源文件真实主链中的普通 Snapshot 或共同判据成立 CU，再比较子初始与源 root bytes 的全部 bytes；未提交 CU 来源直接失败。局部随机读 CRC 或尾 Fence 合格不证明主链成员，不接受嵌在其他 payload 内的伪帧。记录时间及 CU Members 不参与 clone 比较，应用 payload 不解码。

全部实际 tag 桶都检查 header、每条记录的完整 CRC/root envelope 和独立 meta（含名称路由），按解码后的 Ordinal 名称拒绝重复；不能采用 named 查询的 unknownName 隔离。相同名字必须路由到同桶，因此去重集合可以逐桶释放。Related 坐标仅按09检查声明格式，不成为必须存在或 bytes 相等的来源依赖；tag 不是 fork。

RefCount 包含所有正式 ref，SnapshotCount 按各文件本地已发布记录计数，即普通 Snapshot（含每个 ref 的初始记录）加共同判据成立 CU，未提交 CU 不计，不重复展开继承历史；ForkCount 为来源边数，TagBucketCount 包含合法空桶，TagCount 为实际 TagRecord 数。不新增 CU 专用公开计数。只有所有检查、正常 EOF 与必要清理均成功才交付统计。首次内容失败返回失败 Result；结构无法打开、权限/I/O、OOM 等按既有异常合同传播，不汇总为健康计数。纯读或取消不新增 fault，owned 清理失败仍停用并确保锁最后释放。取消可中断检查，但不承诺时间或内存配额。

检查从工厂已准入的身份/布局出发，只审正式 ref/tag 内容；private pending 仍由既有只读打开规则保留，不解释或收养。它不遍历 FrameStore data，也不证明应用依赖闭包、关联源有效、旧调用曾 Confirmed 或断电耐久性。实现应使用短期只读句柄，避免将全库句柄或所有 payload 留存在 owner 中；不增加修复、索引或后台工作。

## RefId 值与内部字段编码

### spec [F-VS-REF-ID-4B] 公开身份绑定上下文，持久字段使用固定 4B

首版 RefId MUST 为不透明、自有的非 positional `public readonly struct RefId : IEquatable<RefId>`，提供 `Equals(RefId)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`。逻辑值为 `(VersionStoreId, LocalRefId)`：前者直接保存 `[F-VS-OWN-FORMAT]` 的 16B canonical 持久身份，后者为非零完整 uint。等值 MUST 比较两者全部 bits，相等值具有相同 hash；比较与 hash 是纯值操作，不读取 owner 或磁盘，hash 不持久化或保证跨进程稳定。同一 VS 的相同 local 值跨 owner 重开仍相等；不同 VSID 下的相同 local 值不相等，不因共借同一 data 而合并。
普通内部表示使用两个私有 ulong 保存 VSID，另一个私有 uint 保存 LocalRefId，不转储 CLR 内存或规定 Pack/Size/sizeof/数组步长。类型不持有 owner、epoch、buffer 或需 Dispose 的资源。仅增加纯值只读 `uint LocalId { get; }`，default 返回 0，其余值返回完整本地编号；不能用 LocalId 等值代替完整 RefId 等值。无 public 数值构造、Deconstruct、IsValid、Guid 转换、算术/排序或规范文本。default 为唯一非法值，自等/比较/hash/属性读取合法，但不能访问/编码，不作可选来源的持久 sentinel。

接受 RefId 的入口 MUST 在既有 owner disposed/fault 检查之后、任何目标发现 I/O 或 data barrier 之前，拒绝 default 及与本 owner 持久 VSID 不符的值。它们是确定参数拒绝，不新增 fault；mutation 仍先初始化 outcome=NotAttempted。MUST 比较持久身份，不以 owner 引用代替；先前取得的自有值可以交给同一 VS 的重开 owner。此检查仅拒绝不同 VSID 的误传，不证明目标存在、物理根唯一、身份生成无冲突或记录完成。

**ref 内部字段 codec。** ref header、自身/ForkOrigin 来源身份与后续 CU 成员表中的 RefId 字段 MUST 恰为 4B，按完整 LocalRefId 的 uint32 LittleEndian 编码；不在每个字段重复 VSID，不追加版本、CRC、flags 或预留。所有非零 32bit 位模式均数值合法，包括 1、高位及 uint.MaxValue；它不是 SizedPtr 或 FrameAddress，数值合法性不证明时间或分配顺序，不检查票据对齐/坐标或 UUID 位模式。tag Related 注记的独立字段 codec 由09定义，不改变这里的 ref/CU 格式。
内部 decoder MUST 要求字段输入恰为 4B，失败输出 default；只在非零检查通过后，结合宿主的预期 VSID 构造值。普通打开/读取的预期身份来自已 checked 的门上下文，记录内的身份须与之比较，不能从被检查记录反向认领期望身份；创建阶段可使用私有初始化已预定的非零 VSID，不要求初次 Create 先读取已发布门。从持久记录字段解码并交付值时，磁盘字段与整个宿主的必要身份/CRC/codec 校验须全部通过；ListRefs 的路径身份另消费 `[A-VS-REFS-DISCOVERY]`，不据此绕过记录校验。
内部 encoder MUST 先检查值非 default、VSID 与宿主预期身份相符及目标容量至少 4B；失败不改变任何目标 bytes，成功只写前 4B，剩余目标不变。复合 reader 先保证剩余至少 4B、切出精确字段，成功才提交消费；截短/零字段失败不提交，宿主负责全部记录的剩余字段及 trailing bytes。两方向不执行 I/O、不新增堆 buffer 或专用错误族，宿主映射为自己的字段错误；可信私有构造只保存已资格化身份值，不在发布确认后的成功值交付中重新解码、读回自身文件或引入可拒绝校验。

RefId 可以来自正常创建结果、checked 当前/历史读取、checked 来源元数据，或 ListRefs 的 checked 门上下文与 canonical 正式路径。最后一种只提供正式占号/定位身份，不证明文件 header、Snapshot 或来源健康；持有 RefId 本身也不证明所声明源存在/成员/发布资格。私有 planned RefId 不在正式发布前交付应用或签发 revision；应用预先选择低号整数不等于取得已发布 RefId。RefRevision 直接复用 RefId 上下文，仍只按成员/发布合同签发。
首版不提供整体 RefId 的公开 EncodedSize、bytes/text codec 或自动序列化适配；应用在特定 VersionStore 的目录/配置上下文保存 LocalId，再按下面的 checked 查询恢复该库的 RefId。内部 4B 字段不是无上下文的完整身份编码，不承诺自由构造身份或辨认跨库混放的应用目录。
（Informative）固定 4B 是用户采用的当前容量/编码取舍：每库最多 4,294,967,295 个非零本地编号；不删除、不复用，容量约束终身累计正式创建的 ref，而非 Snapshot/数据帧或更新次数。自动区有 Max−L 个编号，预留区有 L 个指定槽。唯一分配/不复用/耗尽消费 `[S-VS-REF-ID-DOMAINS]`，不以随机碰撞概率或字段宽度代替证明；不预建 64bit 扩展、逃逸编码或动态宽度。公开值另携已有 VSID：A/B 各有 local=7 时，A 的值误传 B.PublishRef 会被拒绝，纯 local 查询则由调用方保证选对库。20B 逻辑字段之和不成为 CLR ABI，不承诺本次缩宽降低 struct 的内存步长或改善性能；不新增身份、通用 ID 框架或 public StoreId 类型。固定 LE 可直接用 BCL 基元，[已有 UInt32LE](../../src/Binary/BareValueWriter.cs)与[独立 golden](../../tests/Binary.Tests/ScalarTests.cs)仅佐证字段组合，不强加 Binary 依赖，也不构成新 RefId 实施资格。

### spec [A-VS-LOCAL-REF-LOOKUP] 本地编号在显式库上下文定位已发布 ref

VersionStore MUST 提供 `ReadRef(uint localRefId)`，与 `ReadRef(RefId)` 返回同一种自有 RefBytesSnapshot。先检查 owner disposed/fault 与编号非零，再按 `[F-VS-REF-PATHS]` 直接定位并执行既有当前值资格；成功结果的 Revision.RefId 携带本 owner 已 checked 的 VSID。missing/损坏/I/O/模式与恢复语义与 RefId 入口相同，不以 default、空字典或自由构造 RefId 代替 checked 读取。查询可访问预留区或自动区的已发布 ref，不受指定创建的上界限制，不存在目标时不创建。

应用可用常量/枚举选择固定槽位，或保存自动创建结果的 LocalId，冷重开同一库后 ReadRef(local)。这次读取不恢复旧 revision 或证明旧尝试 Confirmed。纯 LocalId selector 由调用方显式选择库，不能检测将 A 的本地号误放入 B 的目录；应用 MUST 保持目录与正确库关联，跨库目录另核对其库归属。完整 RefId 入口仍在 I/O/barrier 前拒绝错误 VSID，不退化为仅 local 比较。

## term `Ref-Revision` 已接受快照的位置身份

RefId 由 `[F-VS-REF-ID-4B]` 定义，是正式 ref 文件的稳定身份；应用名称/业务标记不进入文件名或库内绑定。RefRevision 为库签发的自有位置值，绑定完整 RefId 及实际快照位置/长度，不与 data FrameAddress、仅 offset 或 owner 引用互换。
revision 只从完成发布或实际 checked-read 且已确认真实主链成员的快照取得，不在输出前签发。不同完整物理快照即使 RootMap 同值也具有不同 revision。它不是某次未完成调用的尝试 token；ReadRevision 只接受库签发值，不提供凭裸位置读取，也不以字典相同证明某次调用成功 flush。S5 的按 revision fork 与精确读取共用来源资格，不开放任意位置构造或导入。

### spec [A-VS-REF-REVISION-VALUE] 自有 revision 复用身份与完整 ticket

首版 MUST 使用非 positional `public readonly struct RefRevision : IEquatable<RefRevision>`，普通内部表示仅保存私有 RefId 与 SizedPtr，不另存 VSID、DataStoreId、owner、epoch、Reader、RootMap 或耐久标志。提供 `Equals(RefRevision)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`；等值 MUST 比较 RefId 的完整上下文及 ticket 的完整 Packed bits（含 length），相等值具有相同 hash。值操作不执行 I/O，不依赖 owner 健康/存活；hash 不持久化或保证跨进程稳定，不规定 CLR Pack/Size、sizeof 或数组步长。

公开只读属性为 `RefId RefId { get; }` 与 `SizedPtr Ticket { get; }`，返回快照实际所属 ref 与实际 ticket；继承历史的源快照不改绑到发起查询的子 ref。两者均为纯值投影，不触发读取或签发另一个 revision。default 是受支持公开构造路径中的唯一非法值，自等/比较/hash 和属性读取均合法，两属性分别返回 default；所有接受 RefRevision 的入口 MUST 在既有 owner 生命周期/模式 guard 后、任何目标发现 I/O 或 data barrier 前，拒绝 default 及不同持久 VSID 的值。确定参数拒绝不新增 fault，mutation 仍先初始化 outcome=NotAttempted。

正常发布结果只在所需 flush/发现边界确认并先写回 Confirmed 后对外签发；当前/历史读取只在本次必要 header、真实成员及完整 CRC/kind/envelope/长度资格成立后交付。可信内部构造仅保存已资格化 RefId/ticket，不在确认后的值构造中再验证、分配、扫描或读回自身文件。结构扫描的 RbfFrameInfo、ForkOrigin 声明、planned ticket、私有未发布快照均不能单独签发 RefRevision；值本身不持有 RbfFrameInfo/reader。

已交付值在历史调用结束/提前停止/后续失败及 owner fault/Dispose 后仍可比较和保存；同一 VS 的重开 owner 可接受它，不按实例引用或 epoch 拒绝。实际 fork 消费 S5 `[S-VS-FORK-FROM-REVISION]` 的正式源/header 资格及 exact offset/length 完整内容重读与尾 Fence 检查，再执行本次 data/源确认。成员来源由已签发 revision 与完整位置不删除/复用、无外部改写的协议保证，不从源 EOF 再定位旧点；持有旧值仍不保证源存在/内容健康、当前 head 相同、本次耐久或旧调用曾 Confirmed。此规则不接受裸 SizedPtr 或增另一份成员见证；持久身份也不证明物理根唯一或认证同 VSID 拷贝。

不提供公开位置构造/转换、setter/init、Deconstruct、IsValid、算术/排序、规范文本或整个 RefRevision 的公开/持久 codec。ForkOrigin 仍组合宿主上下文、4B RefId 字段与 8B Packed LE ticket；不能从这些元数据直接构造 checked revision，也不新增完整 revision 记录、版本或预留字段。暂不支持匿名精确 revision 独立外存后跨进程直接导入；tag 可保存09定义的 Related 关联注记，但不签发 revision、不证明 root bytes 与该点一致，不能透明替代精确 ref 历史来源。出现明确消费者时再立带完整上下文的窄导入/导出合同。

（Informative）只读投影是使现有历史与 ListForks 来源边直接关联的工程选择，不是用户另行规定的 API；全私有值也能正确完成选点后 fork，声明边本身也能按 ticket 分组。A1/A2 同字典而分别分出 B/C 时，只比较 RefId 或 RootMap 不能识别选中点的孩子，两属性允许直接比较已有 source 字段，无专用 MatchesLocation API。它们只观察首版单文件位置，未来分段需重审定位/观察合同，不承诺此 tuple 永远足够。无公开构造是窄签发表面，不是不可伪造安全机制；公开构造加严格 fork 校验也可正确，但当前没有裸位置请求消费者。旧 [CheckpointAddress](../../../durable-graph/src/DurableGraph.Persistence/CheckpointAddress.cs)及[重开失效测试](../../../durable-graph/tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs)是实例绑定的另一模型，不成为本栈兼容义务；[结构扫描仍返回坏 payload 的测试](../../tests/Rbf.Tests/Internal/RbfScanReverseTests.cs)佐证内容资格不能只由 framing 代替，不构成新值类型或 VersionStore 实施资格。

### spec [A-VS-READ-REVISION] 精确重读库签发的快照

VersionStore MUST 提供 `AteliaResult<RefBytesSnapshot> ReadRevision(RefRevision revision)`。先检查自身 owner disposed/fault，再在目标发现 I/O 前拒绝 default/不同持久 VSID 的值；支持 RO/RW，按既有模式复用或惰性打开正式 ref。没有新发布、data barrier、源 flush 或 PublicationOutcome。

本次 MUST 检查必要 header、header 后完成范围、目标 ReadFrameInfo 形状、完整 CRC/kind/envelope/meta/尺寸，以及成功目标的 GetScanBoundaryAfter 尾 Fence；CU 还须通过07共同判据。仅交付 root bytes、原 RecordedAtUnixMs 与等于输入的 Revision，结果自有 bytes/生命周期消费11。合法未提交 exact CU 返回本层记录错误 VersionStore.InvalidRefRecord，不能回退到别的节点；必要结构、CRC、peer、I/O 及清理错误按原合同传播，不能伪装为未提交。普通校验失败不新增 fault，实际 owned 可写打开恢复/清理失败仍按既有规则停用。

目标成员来源消费库签发值、完整位置不删除/复用与无外部改写的协议；不为重新定位本地旧点扫描来源后缀或重验前序链。完整帧 CRC 与后 Fence 本身不证明真实主链成员，不能据此开放裸 ticket。底层工厂当前 EOF 资格及 CU peer 必要消歧仍保留，不承诺 O(1) I/O。保存值可交给同库重开 owner，不新增跨进程匿名 revision 导入/导出。

历史/tag visitor 内允许串行调用并复用匹配已有句柄。visitor 自行发起的 ReadRevision 是独立操作，不自动计入外层 HistoryBudget；历史内核仍对自身资格工作逐步计费，不借此入口绕开预算。Dispose、取消、异常与必要清理继续消费既有 callback/owner 合同。

## 单文件存储与最小 codec

### spec [S-VS-REF-FILE-SINGLE] 每个 ref 保存一个完整快照序列

每个 ref MUST 使用按唯一 RefId 命名的一个 RBF3 文件。首版不分段、轮转、差分或建立派生 checkpoint；普通更新追加一条完整 root-payload Snapshot，多个既有 ref 的批量更新分别追加一条07定义的 CU。当前值完整校验本地 header 与本次逆向所需记录，不预读无关初始 Snapshot、扫描全部历史或递归祖先；初始即末帧时自然校验该记录。
选尾 MUST 使用真实 RBF 逆向主链和 `showTombstone: true`，检查物理末帧紧贴实际尾部且所访问记录位于 checked header 后界之后或正好从该后界开始，不能将过滤结果或随机 ticket 当成发布成员证明。普通 Snapshot 直接接受；完整合法 CU 的共同判据成立才接受，未成立跳过并继续向前。只有07的明确不完整见证允许跳过；本次必要 header/record/peer 的未知 kind/格式版/meta 形状、tombstone、坏 CRC/envelope、结构或 I/O 错误皆报错，不以错误回退。header-only 文件不能提供当前值，直接第二帧永远为普通 Snapshot。第二帧初始化资格不作为每次 ReadRef 的内容前检。未访问初始或中间记录的损坏不撤销健康当前 head；声明的源历史缺/坏在实际历史访问中报错，不据此回退本 ref 的当前 payload。
普通 PublishRef 的覆盖前检只检查本地物理 head 的合法形状/完整内容，不强制读取上一组 CU 的全部 peer；后续 lazy CompletedTail 的观察解释按07执行。批量 PublishRefs 的输入与结果由11定义，所有目标/确定容量检查及私有捕获先于 data barrier。
单帧容量与下一帧起点硬界继承 RBF/SizedPtr；下一帧起点超过 SizedPtr.MaxOffset 时明确拒绝追加或要求维护，MUST NOT 隐式回绕、复用完整历史位置或自行拆成多帧发布。MaxOffset 只限制帧起点，最后一个合法帧的末端与尾 Fence 可以越过它，不额外要求文件总长度落在该起点上界内。容量预检使用 RBF 公共 Measure/追加预算 API，并分别检查追加起点；预算 API 不自行验证文件起点，不能把 MaxOffset - TailOffset 当作硬性追加字节预算。

### spec [S-VS-REF-FILE-PUBLISH] 新 ref 由完整单文件发布

所有 ref MUST 使用 `[F-VS-REF-PATHS]` 的单文件布局；正式文件出现是发现边界，不设 ref 容器或名称子目录：

| 路径角色 | 内容与资格 |
| --- | --- |
| `refs/<R>.rbf` | 唯一正式 ref 文件，含 header、普通初始 Snapshot 与后续 Snapshot/CU 序列 |
| `creating/pending.rbf` | 全部初始化共用的唯一未发布工作文件；冷开裁决消费 `[S-VS-CREATING-WORKSPACE]`，路径不编码 R |

自动/指定创建 MUST 在 `creating/pending.rbf` 完成 header 与完整初始 Snapshot，flush/close 后，以同文件系统、不覆盖的一次 file rename 发布。不得直接在正式路径写初始化、先公开 header-only、跨卷复制/删除或覆盖既有 ref。fork 的 ForkOrigin 在同一 header 内，不增加第二发布单元；私有 flush 不建立对象或提前交付已发布 RefId/revision。

ReadRef/PublishRef/ListRefs/历史只依据正式文件，不采用 creating。正式 ref 按 `[R-VS-LOCAL-COMPLETE]` 打开并执行允许的恢复后，缺本次所需 header/发布记录或其内容损坏 MUST 报错，不当空槽、不补空字典、不删除或改号；不借访问目标预审初始或全部历史，目录项资格不证明内容健康。异常/进程终止可留 private，但完整 private 不自行成为 ref 或续作旧请求。只读排除且不清理 private；可写仅在独占 owner 下取消已证明私有的文件，不能删除正式对象。rename 结果不明消费 `[R-VS-PUBLICATION-UNKNOWN]`，不以清理回滚。

自动/指定创建统一消费 `[S-VS-CREATING-WORKSPACE]`；冷开不从 pending 重建目标计划，不把 H+1、预留目标未占、私有内容身份/完整性或与正式对象的对照增加为取消前置。实际创建的编号域与目标拒绝继续消费 `[S-VS-REF-ID-DOMAINS]`，不允许覆盖正式对象。根/组件/no-follow 与锁合同已定，private 取消和 no-overwrite rename 的两平台中断资格仍须实施；FrameStore 普通文件移动不自动成为 VS 协议验收。

### spec [F-VS-REF-PATHS] 正式 ref 文件使用唯一 local 编码

VersionStoreFormatVersion=1 MUST 使用平面布局 `refs/<R>.rbf`。R 为非零完整 LocalRefId 的恰好 8 个小写 ASCII hex 字符 `[0-9a-f]`，高位在前，不是内部 4B LE 的逐字节 hex。`refs`、`.rbf`为精确小写组件；VSID 来自所属上下文，不在文件名重复。文件名不含应用名称，不新增 bucket/layout 配置或第二位置，不持久化 OS 分隔符。

生成/checked 解析 MUST 共用一个内部 codec，供正式发现、目标与历史来源定位复用。解析实际完整文件名，检查精确 8hex/后缀/非零 unsigned 范围，再回编作 Ordinal 比较。不 trim/case-fold/归一化或按 header 改认路径；错大小写/宽度/符号/0x/非 ASCII/空白尾点/后缀/零/额外层级拒绝。codec 不签发 checked 值或公开 hex/path 接口。

**完整正式发现。** `refs`全部直接项（含隐藏项）只能为上述普通文件；不用 glob、仅文件枚举或忽略不可访问项过滤输入。未知/非规范/错误类型、枚举/权限/I/O 及必要清理错误传播，不递归找合法后代、不修名/删除/迁移。canonical 正式文件即占号；求水位和 ListRefs 不默认打开全部 header/Snapshot，也不证明全库健康。ReadRef/每次创建/OpenReadOnly 不因共用 codec 或 pending 增加全库扫描；全集合查询消费 `[A-VS-REFS-DISCOVERY]` 或 S5 的 ListForks 合同。

**必要目标访问。** 由本地整数直接生成精确路径，取得实际组件拼写/普通目录文件/no-follow 资格，拒绝受管 symlink/junction/reparse/设备/FIFO，再核对 header 的预期 VSID/local 与内容。不试别名、第二布局、首个*.rbf 或 creating；损坏/权限/I/O 不当 missing 或空槽。codec 不证明根独占、同文件系统或物理别名唯一；实际准入消费 `[S-VS-ROOT-ADMISSION]` 与 `[S-VS-OWNER-LOCK]`，平台和成本随实施验证。

| LocalRefId | 正式相对路径 |
| --- | --- |
| `1` | `refs/00000001.rbf` |
| `0x89ABCDEF` | `refs/89abcdef.rbf` |
| `0x80000000` | `refs/80000000.rbf` |
| `uint.MaxValue` | `refs/ffffffff.rbf` |

（Informative）整数与文件名一一对应，直接定位无需 RefId→path 索引或应用名称唯一性检查；完整发现仍为 O(ref 数量)，不声称无限规模或已获平台资格。分桶在实际目标需要时另审。

### spec [S-VS-REF-ID-DOMAINS] 预留低号指定创建，自动分配高号

Create VersionStore MUST 接收并持久保存 `ReservedRefIdUpperBound`（类型为 uint，记为 L；下文 Max 表示 uint.MaxValue），在任何创建 I/O 前检查 `0 <= L < uint.MaxValue`。Open/OpenReadOnly 只从 checked 门取 L，owner 提供其只读值，不允许 Open 参数覆盖、运行时扩缩、任意预留区间或修改门。预留只排除自动发号，不预创建 ref/文件，不设 allocator 或预留账本。

| 本地编号域 | 创建规则 |
| --- | --- |
| `0` | 无效 |
| `1..L` | 仅应用指定 create-only；L=0 时为空 |
| `L+1..uint.MaxValue` | 仅自动分配，指定入口不能跳号 |

CreateRef 与 ForkRef MUST 各有自动/指定入口，共用后续初始化/发布。指定入口接收 uint destLocalRefId，在 mutation guard 后拒绝 0 或大于 L，再检查精确正式目标不存在；已存在即确定拒绝，完整坏内容或旧调用未交付也不能当空槽，权限/I/O 不能猜成不存在。上述检查先于 data/源 flush、私有输出与发布，out=NotAttempted，纯参数/占用拒绝不新增 fault。不能隐式更新/覆盖/幂等认领旧创建/改变既有 ForkOrigin；更新用 PublishRef。

**可写冷开。** 门/根/独占/正式目录准入后，完整流式发现全部正式文件，按 unsigned 取 `H=max(L, 全部正式 local 值)`，空集合 H=L。未知项/非规范/错误类型/枚举/权限/I/O/必要清理错误阻止 owner 交付，不安装 partial H。发现先于本次恢复/pending 取消/输出；creating 不计 H，其独立资格消费 `[S-VS-CREATING-WORKSPACE]`。正式 canonical 文件即占号，不因未收结果/坏内容腾号，不为求 H 审计全部内容。OpenReadOnly 不因发号或 pending 全扫描。

**自动候选与耗尽。** owner 内只保留一个 `uint AutoRefIdHighWatermark`。健康自动创建先检查 H!=uint.MaxValue，再取 checked uint 加法 H+1，不补洞、不从源 ID 推算、不用 Count+1/signed/RNG 或持久 next counter。H=uint.MaxValue 时，仅自动创建确定失败 `VersionStore.RefIdExhausted`，先于 data/源 flush 与输出、NotAttempted 且不新增 fault；空闲预留槽仍可指定创建，查询/更新/tag 不因自动耗尽拒绝。

**发布后登记。** no-overwrite file rename 正常返回后先 Confirmed；自动创建无分配地置 H=candidate，指定创建不变 H（dest<=L<=H）；再执行可失败的打开/结果/投影/清理。不在每次创建全扫；异常停用，Unknown 后不在原 owner 换号或循环 retry。冷开按实际正式集合重建 H，已发布未交付仍占号。

已正式发布的身份/完整历史不删除、不复用；预留槽成立后只可更新，不能删除重建。未发布且未交付的 planned local 可在冷开完成 private 资格后重新规划，不授权覆盖/接续残留。指定目标与自动候选不是尝试身份，已知号码不证明 Unknown 旧调用 Confirmed。

| 配置/窗口 | 结果 |
| --- | --- |
| L=0，空集合 | 自动 1；所有指定创建拒绝 |
| L=256，空集合或仅预留号 1、7 | 自动 257，不制造 256 个对象 |
| L=256，H=300，指定空闲 20 | 成功后 H 仍 300，下个自动 301 |
| dest=0、dest>L 或正式目标已占 | 指定在屏障/输出前拒绝，不烧自动号 |
| L=Max−1，空集合 | 唯一自动号 Max；随后只自动入口耗尽 |
| H=Max，仍有空闲预留号 | 指定可创建，自动不补低号 |
| pending 未正式发布 | 不计 H/正式占用，统一按工作区资格裁决，不还原 planned local |
| rename 成立后 Unknown 或 Confirmed 后交付失败 | 冷开正式发现占号，不按同值认领旧调用 |

（Informative）自动候选大于全部正式编号；指定只查确定路径并由 no-overwrite 发布保护，两域不相交，无需全 ID 表。L=256 仅为应用永久槽位示例，不规定默认值或必建 main。L 是固定容量，不支持后续扩区；编号大小不代表跨 ref 时间/来源顺序/成员资格。冷开 O(ref 数量)路径发现，健康候选计算/指定定位无需全扫；实际资源/I/O/规模资格仍随实施取得。

### spec [F-VS-REF-FRAMES] 首帧判别来源，发布帧组合不透明 payload 与记录元信息

VersionStoreFormatVersion=1 的 ref 文件 MUST 使用 RBF3；首物理帧 FrameTag=0（RefHeader），普通 Snapshot 的 FrameTag=1，ConditionalUpdate 的 FrameTag=2。全部帧 MUST 非 tombstone；RefHeader MUST TailMetaLength=0，Snapshot/CU 的 TailMeta 消费 `[F-VS-RECORD-TAILMETA]`，首版恰为8B。直接第二帧 MUST 为普通 Snapshot，CU 只用于既有 ref 的后续追加。kind 由 RBF tag 表达，不在 payload 重复；CU payload/capacity/共同判据唯一见07。格式门版本仍为1，旧 Snapshot/header 编码不变，旧 reader 对新增 kind 拒绝。必要读取遇到未知 kind、后续 header 或非法组合报错，不能过滤或向前寻找可用替代帧。

RefHeader 的 RBF decoded payload MUST 恰为 **24B 或 36B**，字段如下：

| payload byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | VersionStoreFormatVersion | uint32 LE，首版1，须与 checked 格式门的统一版本相等 |
| `[4,20)` | VersionStoreId | `[F-VS-OWN-FORMAT]` 的16B canonical身份，须与期望VSID逐字节相等 |
| `[20,24)` | 本 ref 的 RefId | `[F-VS-REF-ID-4B]` 的非零4B LE，须等于期望目标RefId |
| `[24,28)`，仅36B形状 | SourceRefId | 同一内部4B codec，非零且不同于本ref，绑定期望VSID |
| `[28,36)`，仅36B形状 | SourceSnapshotTicket | 完整 SizedPtr.Packed 的8B LE，不使用 Serialize/Deserialize 的交错表示 |

**来源判别就是完整 payload 的精确长度：24B=无来源，36B=有来源。** 其余长度、截短来源及 trailing bytes 均拒绝；只在真实 RbfFrameInfo 形状与完整 CRC 通过后解码整个 payload，不能按“目前可读到多少 bytes”猜分支。无来源不写零 SourceRefId/ticket sentinel；不增 presence flag、预留空间、payload Magic/CRC、DataStoreId、HeaderSchemaVersion、初始位置或名称。DataStoreId 由格式门绑定；header 版本选择该文件的记录格式规则、payload schema 与允许的 kind/meta 形状；元信息扩展按专项的末尾追加规则定稿，当前 Snapshot 仅允许8B。Snapshot 不重复整库版本/身份，不以 meta 长度推断不兼容 payload。
期望版本/VSID/自身 RefId 来自已 checked 的门和正式目标定位；创建时来自本次私有初始化预定值，随后正式门/目标须使用同值，不要求私有文件已发布。MUST NOT 从 header 自身反向认领期望身份。完整 RBF CRC 与全部字段通过后才交付自有元数据；来源元数据不签发 checked RefRevision，不递归检查源存在/内容。

SourceSnapshotTicket 保留字段名，其纯数值检查 MUST 覆盖普通 Snapshot 与完整 CU frame：`Offset >= Empty.EndExclusive + MeasureWriteSize(24,0).AppendLength`，且 `MeasureWriteSize(1,8).FrameLength <= Length <= SizedPtr.MaxLength`；当前为起点至少60、长度40..268435452。Packed 已表达对齐/可表示范围，不额外要求末端或尾 Fence <= MaxOffset。实际源 header 可能使初始起点更大，数值合法仍不证明源存在、具体成员、CU 共同资格或原样复制一致；这些由 S5 实际访问建立。普通 Snapshot 自身仍受1MiB root 的宿主尺寸界，CU 的成员 ticket 尺寸由07按实际 count 检查。

Snapshot 的 RBF payload MUST 为完整 root-payload bytes，actual 长度为1..1,048,576B，无外层版本/身份/Parent/map byte-length/CRC。发布层按 `[A-VS-ROOT-PAYLOAD]`检查边界、kind/meta、实际主链及完整 CRC，不解释 bytes。采用 RootMap 的应用使用专项完整 codeword 并 exact-consume；发布记录健康不赋予 payload 语义或 data 来源/业务闭包资格。输出前用公共 MeasureWriteSize(M,8) 校验整帧及实际追加起点，完整帧租用前先验 RbfFrameInfo 形状和范围。

**共同 header 检查。** 在已取得的 Idle RBF 句柄上，按期望上下文要求 Format=Rbf3，用 `ScanForward(showTombstone:true)` 只取首物理 header，先检查首 Offset=Empty.EndExclusive、上述形状和 `MeasureWriteSize(L,0).FrameLength`，再完整读/解码身份与来源。MoveNext=false 先传播 TerminationError，再报缺 header；不跳过墓碑或寻找后续替代。保存 checked header ticket、ForkOrigin 及公共 `GetPhysicalOffsetImmediatelyAfter(headerTicket)` 给出的预期初始起点，不预读初始 payload。ReadRef、精确 fork 与 ListForks 共用这一窄资格；Snapshot 自身没有宿主身份，不能省略它。

**初始化格式检查。** 创建的完整文件与独立完整校验另要求直接物理第二帧为普通 Snapshot，Offset 等于上述初始起点，并完整校验形状、CRC 与 payload 边界；缺帧先传播 TerminationError。S5 历史在逆扫实际抵达该起点时才校验该初始节点，不提前保存其 bytes。日常 ReadRef 成功只证明本次 header/head 健康，不证明初始化、全部历史或来源一致性。
令 `H=MeasureWriteSize(L,0)`、`S=MeasureWriteSize(M,8)`，初始起点为 `Empty.EndExclusive+H.AppendLength`，初始化后界 `I=初始起点+S.AppendLength`，须与实际checked物理后界一致。当前header FrameLength=52/64B、初始起点60/72；空map最小Snapshot FrameLength=40B、初始化文件104/116B，最大普通Snapshot FrameLength=1MiB+36B。这些是公共尺寸推导，不复制wire开销，不证明预约、耐久或平台资格。

正式 ref 按 `[R-VS-LOCAL-COMPLETE]` 接受本次所需的恢复后完整记录，包括初始 Snapshot；最小初始化长度只描述完整格式尺寸，不成为日常读取初始内容的前检。

（Informative）专用1B presence flag（25/37B）同样可正确，但当前两种header会因对齐各多4B物理占用；复用已有精确形状省去一个字段。正常36B帧截短不会变成24B：原HeadLen/descriptor/CRC仍约束原形状，CompletedTail只补Key/Fence，不重写来源/长度。恶意重写并重新seal不属于格式认证能力，flag也不提供它。[RefFrameSchemaProbe](../../experiments/RefFrameSchemaProbe/README.md)原 8B LocalRefId/fixed12 synthetic RootMap/meta0 版本的75项检查在W:验证字段golden、真实RBF形状/CRC与112→104→112→再次None现象，结果及原始JSON保持历史身份，未按本次 4B LocalRefId/变长RootMap/meta8重跑。该现象只反驳原先“最小长度＋事后拒绝能保护恢复前初始化”的政策；当前接受恢复后完整记录，无需保留旧报告。正常private flush/close→rename发布不会产生半初始化正式ref；该探针不构成正常发布的崩溃轨迹、本次格式、新VersionStore、进程终止、rename、两平台或包资格。

### spec [S-VS-REF-FORK-ORIGIN] 首帧保存一次性的 ref 创建来源

header MUST 保存明确的无来源/有来源判别；有来源时 `ForkOrigin = (SourceRefId, SourceSnapshotTicket)`，ticket 为源 ref 文件中的 SizedPtr，包含 offset 与 length，MUST NOT 使用 data FrameAddress。来源判别、固定字段及坐标范围唯一消费 `[F-VS-REF-FRAMES]`，数值合法不证明成员资格。来源限同一个 VersionStore 及绑定 data，上下文由格式门与两份 ref header 校验，不重复存 SourceStoreId，不从 CLR 内存布局推导编码。

ForkOrigin MUST 在私有创建时写入，发布后不可改变。无来源表示独立历史起点；相同 payload 不推断来源。有来源表示子初始 Snapshot 原样复制 exact 源发布记录的 root-payload bytes；子时间和 revision 独立。S5 创建时建立源发布资格，历史跳转重新检查 bytes 一致性；均不通过 RootMap 解码或字典同值证明来源。

源 ref MUST 已正式发布，且源 ticket 必须是已接受快照的真实主链位置，不能指向 header、残尾或仅数值合法的位置。新 ref 使用 fresh RefId，禁止自链接；有效来源只能从新 ref 指向已经存在的 ref，故创建流程形成森林，无需持久 generation 或拓扑编号。跨文件历史另做 visited/cycle 检查，发现缺源、非法成员或循环报错，不截断为正常历史结束；全图声明查询的较窄资格见 S5。完整历史及正式 RefId 不删除、不搬迁、不复用；未来 GC/删除/分段需重新审定链接保留协议。

声明来源与实际读取源快照是不同资格：checked header 可返回 ForkOrigin 元数据，但不能仅据其中的坐标签发 checked RefRevision。当前值访问保持本地路径；S5 定义跨文件历史与全分叉查询的访问范围、预算和错误。

## 最小 API 与发布步骤

| 候选操作 | 输入 | 成功结果 |
| --- | --- | --- |
| CreateRef(payload) | ReadOnlySpan<byte>；自动编号、无来源 | 已发布初始 RefRevision |
| CreateRef(destLocalRefId, payload) | 预留区空闲目标与 ReadOnlySpan<byte> | 同上，指定目标 create-only |
| ReadRef(refId) / ReadRef(localRefId) | 完整 RefId 或当前库的非零本地号 | 自有 RefBytesSnapshot：revision、时间与 payload |
| PublishRef | RefId、ReadOnlySpan<byte> | 已发布新 RefRevision |
| ListRefs | 本 VersionStore | canonical 正式路径对应的自有 RefId 全集合；不签发内容资格 |

API 和自有结果消费 [11 专项](11-versionstore-root-payload-bytes-api.md)，创建编号/本地定位消费本篇既定合同。mutation 使用 AteliaResult/必选 out PublicationOutcome，不增加 Prepared handle。PublishRef 无 expected revision，串行 driver 管理流程；未来 CAS 另审。

### spec [A-VS-REFS-DISCOVERY] ListRefs 交付全部正式路径身份

ListRefs MUST 在 owner disposed/fault guard 后，完整执行 `[F-VS-REF-PATHS]` 的实际直接项发现、名称、普通类型/no-follow/同文件系统及必要枚举清理。使用 owner 已 checked 的门 VSID 与路径 local 构造 bound RefId，包含预留区/自动区全部正式文件，排除 creating；空正式集合正常结束才返回空集合。

本调用不打开 ref 内容、不恢复文件、不读取 header/Snapshot、不修改或取消工作区、不执行 data I/O/barrier。canonical 正式文件即占号，即使其内容损坏或旧创建未交付也 MUST 列出；名称/类型/枚举/权限/I/O 或必要清理失败显式传播，不能过滤成 absent 或交部分成功。只读与可写 owner 使用同一发现合同。

全部发现及必要清理成功后，一次性交付不依赖 owner/枚举器的自有只读 RefId 集合；不外泄 raw sequence/目录枚举器，不承诺稳定顺序，具体集合包装/方法语法随实现选择。ReadRef 才签发当前内容/revision，ListForks 则另执行 S5 的 header 与图资格；ListRefs 不证明快照/来源/data 图健康，不按同值认领旧自动创建或倒推旧 outcome。完整发现和结果存储成本为 O(正式 ref 数)，无持久 ID/path 表或健康索引。

### spec [S-VS-SINGLE-RECORD] 一次 ref 变更由一个 frame 生效

一次 ref 创建 MUST 由一条普通初始 Snapshot 表达全部根值；普通更新由一条完整 Snapshot 表达全部根值，批量更新则由07的一条本地 CU 加共同成员判据表达，不分成同 ref 多个根的独立更新。data 构建/确认、私有初始化或纯容量试算均不改变已发布 ref。新 ref 另有正式文件的发现边界，遵循下面的创建协议，不加入 CU。

### spec [S-VS-ROOTS-AFTER-DATA-CONFIRM] 根发布在必要完成数据确认之后

PublishRef、PublishRefs 及 S5 CreateTag MUST 先完成确定输入、目标/名称及 owner 生命周期准入、私有拷贝与编码、记录/文件容量检查，再同步调用 `data.ConfirmDurable()`，然后向发布 RBF 追加完整记录并 `DurableFlush()`，最后安装内存状态/正常返回。PublishRefs 的 CU 路径先预测全部实际 tickets、私有编码完整 canonical Members 并为整批取时一次，再进入一轮 data barrier、逐成员 Begin/End 和逐文件 flush；详细顺序唯一见07。MUST NOT 仅因 data 有未归还 Builder 而拒绝；未完成 Builder 不获完成或耐久资格。确定拒绝不追加、不调用 data barrier，也不因 guard fault。
data 屏障消费 S2 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`，覆盖 leased 文件内旧完成输出。应用仍 MUST 保证本次 root payload 所需新增依赖全部已完成、属于绑定 data 且闭包不含 provisional/canceled 地址；屏障成功不证明该闭包，库不遍历业务图验证依赖闭包。无关 B 仍 Building 时，已完成的独立 A 可以发布；A 真正依赖未完成 B 时不能发布。
库不接受“以前已 flush”标志代替本次 barrier；旧 root payload、历史 rewind、fork 初始快照和 tag 同样走屏障，无需复制 data。库不提供发布 callback，不允许同 driver 的其他应用操作交错；输入访问消费 `[A-VS-BYTES-INPUT]` 的稳定/不重入前提。data flush 失败不得进入本次发布输出；发生异常按所涉 owner/fault 合同停用 VersionStore，不虚称未输出的另一个 owner 同样故障。
CreateRef 两种入口 MUST 先完成编号/目标、输入/编码/容量及 owner 准入，调用 `data.ConfirmDurable()`，再按 `[S-VS-REF-FILE-PUBLISH]` 初始化/发布完整文件；无关 Builder 不阻断。S5 两种 fork 入口同样 data-first，另确认源 ref 耐久，不先公开 CreateRef 再补来源；清理不补造 ref，不另写 allocation/Init 账本。

## term `Publication-Outcome` 本次调用的证据

`NotAttempted / Unknown / Confirmed` 分别表示尚未尝试改变公开发布事实、公开事实可能已生效但无法确认、所需发布 flush 与发现边界均成功返回。它是本次调用的非持久证据。ref 创建的发布点就是 no-overwrite file rename；rename 正常返回后立即写回 Confirmed，再安装内存状态或交付结果，没有第二次提交。rename 已生效后进程终止，可以留下已发布 ref 而调用方没有结果；重开校验合格的正式 ref 即接受当前发布事实，不要求恢复旧 outcome 或证明旧调用曾交付 Confirmed。

### spec [A-VS-PUBLICATION-EVIDENCE] 同步 mutation 通过 out 保留调用证据

首版所有公开的同步发布 mutation（完整 roots 发布、ref 创建或 tag 创建）MUST 返回相应成功值的 AteliaResult，并接受必选的 `out PublicationOutcome outcome`；包括两种 CreateRef、PublishRef、PublishRefs 及 S5 的 tag 与两种 ForkRef。成功值类型由所属 API 定义；PublishRefs 全体共享一个 Outcome，不增加通用 Mutation 方法、receipt 或恢复句柄。公开枚举如下，不是持久记录字段：

```csharp
public enum PublicationOutcome {
    NotAttempted = 0,
    Unknown = 1,
    Confirmed = 2
}
```

入口第一条语句 MUST 把 outcome 设为 NotAttempted，先于生命周期/模式/参数检查、输入复制、编码及任何 I/O。调用方每次先在 try 外初始化独立局部变量，异常路径仍可读取；以下示意消费已实施的普通 PublishRef 公面；其他发布操作按其实施状态判断：

```csharp
var outcome = PublicationOutcome.NotAttempted;
try {
    var result = store.PublishRef(refId, roots, out outcome);
    // IsSuccess => Confirmed；IsFailure => NotAttempted。
} catch {
    // outcome 已写回；停止当前流程，按实际状态重开续行，不盲重试。
    throw;
}
```

正常成功 Result MUST 对应 Confirmed；正常失败 Result MUST 对应可证明本次公开发布未发生的 NotAttempted。确定拒绝沿用 Result，生命周期等既有 guard 和运行期异常仍抛出；不得把 Unknown 或 Confirmed 后的异常改成普通失败 Result。out 是唯一调用证据通道，不要求构造 PublicationException、修改 Exception.Data 或查询实例 LastOutcome；原操作异常不为携带证据另行包装。调用方 MAY 用 `out _` 放弃证据，但不能据异常本身推断发布状态。
（Informative）只靠返回值无法传出抛异常时的证据；后置异常包装需要分配，确认后遇到 OOM 时，包装再失败会丢掉 Confirmed 和原异常。enum 的 out 写回无需分配，适配当前同步串行接口；异步接口另立合同，不为它预建载体。[RBF 故障测试](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs)已覆盖完整输出后异常而 TailOffset 未推进，以及 flush 前/后异常，不能按异常本身猜发布相位；普通 ref、fork 与匿名 tag 的上层接线及源码验证见[实施计划](12-versionstore-implementation-plan.md)。
证据赋值与 fault latch MUST 只更新现有字段/引用，不依赖新分配。公开协调入口直接更新这一 out；内部助手不得重置已推进的本次证据，或仅在正常返回后把局部 outcome 拷回外层。不调用其他公开 mutation 拼接本次事务。Confirmed 先写回，再进行内存投影/成功值安装、释放或正常返回；以后不能降级。

Outcome 与实例健康是两个事实：NotAttempted 不表示零 I/O 或可直接重试，Confirmed 不表示当前实例还能使用。确定前置拒绝不新增 fault；data/源确认、私有输出及公开输出/flush/close/rename 或确认后安装异常保守停用 VersionStore，并启动一次完整受控 teardown。先无分配地使实例失效，再单次尝试清理全部非锁 owned 资源，最后关闭根锁；旧 owner 终止，后续操作拒绝，再次 Dispose 不重复关闭已尝试对象。仅按各 owner 自己的合同处理其 fault，不 Dispose 借入 data，不把健康 data 一并宣称故障。确定参数/guard 或 pre-I/O 失败 Result 不 teardown；普通查询发布 envelope/长度/预算失败继续按既有读合同处理，实际恢复/owned 清理等已要求停用的失败才进入受控 teardown。
异常清理 MUST 先无分配地停用实例，out 不被 finally/Dispose 改写；资源槽先取出/清空再逐一单次尝试关闭，根锁按 `[S-VS-OWNER-LOCK]` 最后关闭，不因前项失败跳过后项。遵循 S2 `[S-FS-OWNED-FAULT]` 的主异常优先、单原异常/多 Aggregate 和汇总分配失败仍完成清理规则。已归还资源不由外层 finally 或再次 Dispose 重关。无新清理错误时保留原异常对象与原栈；多个错误的汇总不充当发布证据，不声称找回底层 finally 已遮蔽的异常。正常成功 Result 不替代应用状态安装；应用安装失败后仍从已发布事实加载。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

调用证据由本条的公开事实边界推进；data 屏障、源 ref flush 与 metadata 准备不提前推进本次业务结果：

| 操作 | 写回 Unknown 的位置 | 写回 Confirmed 的位置 |
| --- | --- | --- |
| PublishRef（含 rewind）/CreateTag | 调用正式发布文件 Append 之前 | 该记录 Append 成功且发布文件 DurableFlush 正常返回后，立即写回 |
| PublishRefs（至少两个成员） | 首次 CU 最终输出调用之前；后续局部 pre-I/O 拒绝不能降低整批证据 | 全部 CU 完成且全部参与 ref 的 DurableFlush 正常返回后，立即写回；再安装全部结果 |
| CreateRef/ForkRef（自动或指定） | 私有 ref 文件 flush/close 后，调用正式 file rename 之前 | 同一次 no-overwrite file rename 正常返回后立即写回 |
| data ConfirmDurable / fork 源 ref DurableFlush | 不推进，保持 NotAttempted | 不确认本次发布 |
| tag 桶 header-only 初始化、flush/close/空桶 rename | 不推进本次 tag，保持 NotAttempted | 只建立桶 metadata，不确认首条 tag |

更新/tag 进入公开 Append 调用后的异常 MUST 保守保留 Unknown，即使该异常可能发生于内部准备阶段；不依据异常类型/消息、旧 TailOffset 或私有 flush 推断没有公开输出。公共 RBF `[S-RBF-APPEND-RESULTPATTERN]` 正常返回的失败 Result 则有 pre-I/O 合同：若它证明本次全部公开发布输出均未发生，MUST 在返回失败 Result 前恢复 NotAttempted。这是唯一的已知拒绝例外，不是输出相位猜测；已有公开输出尝试时，后续局部 pre-I/O 拒绝不能给整个调用降档。多 ref CU 消费同一原则，不为此引入通用输出计数框架。
私有 ref 准备阶段失败为 NotAttempted；正式 rename 调用后的异常保守为 Unknown，不以事后 Exists 检查或清理回滚猜该调用是否确认。tag 空桶 rename 结果不明但尚未 Append tag 时仍为本次 tag 的 NotAttempted，实例停用，重开裁决桶 metadata。所有 Confirmed 必须先于可能失败的内存安装、清理与返回值交付；后续异常只能保留该证据。停用后重开 checked-read，不盲目重试、不回滚正式对象。
不承诺崩溃后查询某个旧尝试的 Present/Absent；out 保留此次同步调用返回/抛出时的证据，库不持久化或跨调用自动追踪它。Unknown 后同值 RootMap 不能证明该次调用曾 flush，RefRevision 也不是旧尝试身份。

### spec [R-VS-LOCAL-COMPLETE] 重开接受局部完整发布事实

**正式 ref 接受恢复后的必要完整事实。** 通过门/data 绑定、正式路径/类型及访问模式/独占准入后，可写访问 MUST 先用 public `RbfFile.OpenExisting` 按 RBF3 单尾合同执行结构恢复，再按 `[F-VS-REF-FRAMES]` 校验 header 及本次实际需要的发布记录。ReadRef 按前述选尾合同处理普通 Snapshot/成立 CU/未成立 CU；精确 fork、历史和 ListForks 分别消费 S5 的访问范围，不把初始内容或完整历史校验放在共同打开前置中。CU 的本地完整不单独建立发布，成员共同资格由07判断；物理恢复不补写旧业务成员。初始化与后续更新统一消费这份恢复合同；MUST NOT 仅因恢复作用于初始 Snapshot、Action 非 None 或旧恢复报告已不存在而拒绝。无需恢复前初始前缀资格；S5 所需的公共边界逆扫及历史接线已在 V2-A 实施，源码资格见[实施计划](12-versionstore-implementation-plan.md)。

初始 Snapshot body 已完整而仅缺 RBF 可合法补齐的 Key/Fence 时，CompletedTail 后通过必要身份、kind/meta、CRC 与 payload 边界检查即可建立发布资格；应用取得 bytes 后自行解码。后续残尾被 Truncated 后，从剩余实际完成尾按前述普通 Snapshot/CU 选值规则检查，不另预读无关初始 payload。若缺必要 header 或已无当前 Snapshot，MUST 报错，不补造空字典。当前 head 自己损坏 MUST 报错，不丢弃它或回退旧值；未访问历史的坏内容不连带拒绝健康 head。RBF 结构恢复可能已经修改文件，随后 VersionStore 才拒绝本次必要记录；不撤销底层恢复。校验本身不修内容，独立完整校验使用只读路径且不承担修复。只读访问使用 public `OpenReadOnlyExisting`，需要修尾时仍拒绝且不修改；旧点直接读取也不能绕过底座工厂对当前 EOF 的结构准入。

tag 桶继续先执行 [S5-B 专项](09-versionstore-immutable-tags.md)的初始化长度前检，再执行允许的 RBF 恢复与请求名称的损坏裁决，不把 header 修尾认领为初始化；惰性访问不默认扫描全部桶。本条的 ref 政策不改变 tag 或 FrameStore 的初始化保护。
新 ref 文件发现/private 消费 `[S-VS-REF-FILE-PUBLISH]`；没有正式文件时 private 完整 Snapshot 不建立 ref。fork 来源与初始 Snapshot 随同一文件发布，不从 private 推断或续作旧请求。
本次必要 ref 记录的完整坏 frame/未知版本/错误身份/非法 envelope codec MUST 报错；tag 的局部坏记录按专项隔离，但不能由未知名称证明缺失。内容 codec 的错误由应用处理，不能当残尾、missing 或回退旧值。只读模式不隐式修尾。被引用 data 缺失/损坏在应用实际读取时报错，不据此改写已发布 payload。
Open 不默认扫描所有 ref 历史或 data 图；按需打开/校验目标文件，不将成功 Open 宣称为全库审计或全部根图健康证明。所有必要错误传播显式；资源/I/O fault 不被吞成缺对象。
重开后已知 RefId 或当前库本地号用 ReadRef，tag 名用 checked ResolveTag；检查合格的正式 ref 即接受其当前发布事实，无需追溯旧调用结果，也不机械重复 create-only。当前同号/同值不改写某次旧调用的 outcome。指定号码可由应用预先保存，重开 checked 查询实际目标，但不成为尝试 token。自动创建 rename 后交付失败可留下 ref 而调用方无新号；ListRefs 可发现集合，不能按同值精确认领旧调用。动态恢复关联由应用在 payload 或其数据闭包保存业务身份并核对，不增加预交付 RefId 或自动重试。

### spec [A-VS-RECOVERY-REPORTS] 惰性打开的成功物理恢复动作

VersionStore MUST 提供以下诊断公面，恢复策略仍消费 `[R-VS-LOCAL-COMPLETE]` 和 RBF 的成功打开报告：

```csharp
public enum VersionStoreRecoveryFileKind { Ref, TagBucket }
public readonly record struct VersionStoreRecoveryReport(
    VersionStoreRecoveryFileKind FileKind,
    uint LocalId,
    RbfTailRecoveryReport Recovery);
public IReadOnlyList<VersionStoreRecoveryReport> RecoveryReports { get; }
```

Ref 的 LocalId 为非零本地编号，TagBucket 为 0..1023 桶号；列表属于本 owner 的 VersionStoreId，独立导出日志时调用方一并保存该身份。报告只含诊断值，不公开路径、文件、lease 或业务发布凭据，不能签发 RefRevision。

初始列表为空。所有可写 ref/tag 的惰性打开，包括普通读取/发布、精确读取/fork、history/ListForks 的临时 ref、CU peer、named tag 与 ScanTags 的桶，MUST 经共同通道累计 RBF 工厂成功返回的非 None 动作。缓存或已有扫描句柄借用不新增动作；后续健康打开的 None 不覆盖旧动作。只保留实际修尾动作，内存 O(动作数)，不建立所有健康打开的统计目录或承诺累计量硬上限。RO 不修尾、需恢复时拒绝；借入 FS 的独立恢复仍仅由 FS 自己报告。

getter 返回只读、只追加的 live view，不复制全集。Count 可增长，已有项及其顺序 MUST 保持；fault/Dispose 后 getter、Count/indexer 和保存的 view 仍可读，不调用可用性 guard、不清台账。view 只持有动作值集合，不持有 owner/backend、文件、锁、lease 或捕获 owner 的 delegate，也不通过普通集合 cast/SyncRoot 暴露可写 backing。串行调用方可在操作前后保存 beforeCount/afterCount，再读取 `[beforeCount, afterCount)`；枚举不保证跨调用快照，需要冻结集合或异步交付时由调用方复制。

实现 MUST 在可写 RBF 工厂前准备一项追加容量，成功返回句柄/report 后立即无分配追加非 None 动作，早于 tag 初始化恢复保护、VS 必要校验、资源移交与清理。底层修尾已经发生，上层 header/CU 校验失败、visitor 异常、取消、关闭失败或 owner 停用不撤销动作；仍沿原资源槽单次关闭、根锁最后释放的规则，不另建事件或 per-open reservation/commit 协议。

报告不证明发布成立、root/data 图健康、某次旧调用 Confirmed 或全库已经检查。NotAttempted 可以伴随目标惰性修尾。RBF 工厂在修尾写入/截短、flush 或复检期间失败，可能已修改 bytes 而未成功交付报告；缺报告不证明未修改，不伪造 None 或完整失败取证。此 API 不增加 data 探测或耐久确认，不改变 PublicationOutcome、历史预算、CU 共同资格与 owned 清理合同。



## 实施与验收

1. S4-A：项目、RefId/RefRevision/bytes 结果、格式门/header/内部 codec 与 owned 生命周期；先补 S2 公共 EnsureUsable 及 VS 窄平台入口。
2. S4-B：自动/预留区指定创建、private 单文件发布、RefId/local 读取、PublishRef/ListRefs 与 data-first，形成固定槽位和动态 ref 的 public 冷重开例子。
3. S4-C：必要成员/CRC/容量、Result/out 证据、异常清理及确认后交付失败。
4. S4-D：进程终止 failpoint、任意 bytes 整体替换、同值新 revision、应用 JSON/空 RootMap/循环闭包与旧 bytes 重发；真实文件实验使用 W:。

前文全部合同须逐项映射到测试；下表集中列出特殊边界与组合。bytes 所有权/输入向量、时间与可选 RootMap codec 的详细验收分别由 [11](11-versionstore-root-payload-bytes-api.md)、[08](08-versionstore-record-metadata.md)、[10](10-versionstore-rootmap-payload.md)维护，本篇只验证其接入。

| 验证组 | 特有边界与组合 |
| --- | --- |
| 借入资格 | 三个工厂在本次 VS I/O 前拒绝 disposed/faulted data，可写入口拒绝只读；RO 接受两种 data 模式。活跃 Builder 不被确认/取消；错绑定、初始化失败及 VS Dispose 不停用健康 data，失效 data 不妨碍 VS 自身清理。持续串行是调用方前提 |
| 建库/锁/布局 | 缺失末级根、缺父目录、空根/仅空锁与获锁 fresh 重检；空布局先于直接门输出，半布局/短坏门不续作，完整门已存在而 flush/close/交付失败后可实际 Open；不删门/锁/root 回滚。两平台 ordinary/no-follow/同 FS 与严格 writer/readers 矩阵，VS/data 可跨 FS；进程终止释放锁，停用完整 teardown 锁最后、资源单次关闭及重复 Dispose |
| 门检查 | 全部短前缀/尾随、CRC/双身份/L/未知版本、重新 seal 的零身份或 L=Max、错 data；检查及必要 close 失败先于恢复/pending 清理/输出。两角色身份可同值，L=0/Max−1 合法，重开不改门/L、不按 header 补门 |
| pending | 空或唯一 pending 的0B/任意中断字节/完整内容均不读回，不用 Length/身份/role/H/正式目标判取消；可写正式发现 H 成功后删除，RO 保留且不为 pending 全扫。隐藏未知项/别名/多项/特殊类型/跨 FS/枚举/清理或删除失败保留拒绝；ref/fork/tag 共用槽，rename 后正式对象保留，不续旧计划 |
| 路径/发现/发号 | 规范路径独立文本向量及大小写/宽度/Unicode/空白尾点/后缀/层级/类型错误；路径称8而 header称9拒绝。ListRefs 两域/高位/Max/空集合、自有无顺序全集合、坏内容仍列出占号，随后 ReadRef 报错；无内容/data/private 副作用，失败不交部分集合。L=0/256/Max−1，指定非法/已占拒绝先于 barrier，低号不推进 H，自动耗尽仍可占空闲低号；pending 不计 H |
| ref 宿主与恢复 | 两种 header 后界、header-only 拒绝、实际末 Snapshot 的成员/墓碑/未知 kind/meta/CRC/TerminationError 与 pooled 租用前尺寸检查；普通 ReadRef 不访问初始或源历史。初始 CRC 坏但 head 健康时当前值成功，初始即 head 时坏记录仍失败；独立初始化检查必须拒绝坏第二帧。初始 body 完整但缺 Key/Fence 可写补齐并接受、再次 None 仍接受；body 被截掉且只余 header 则拒绝。后续更新补齐/截短后检查实际末 Snapshot；坏 head 不回退，允许已发生恢复保留；只读残尾拒绝且不改 bytes |
| 创建与证据 | pending 初始化/flush/close、file rename 前后及 Confirmed 后结果/投影/清理失败；正式对象未创建或完整创建，private 不可见/不续作，正式缺必要帧不作空槽。out 最早初始化、助手异常直接写回、Append pre-I/O Result 例外与 OOM/输出/flush 的 Unknown；data/源确认及空 tag 桶维护保持 NotAttempted 但异常停用；确认后不降档、不 Dispose data。自动丢失结果可 ListRefs 发现而不能按同值认领，指定号码不证明旧尝试确认 |
| data-first 与应用 | A 全依赖已完成、无关 B Building 时发布成功；B 租用文件中的旧 dirty 输出须确认，B 后续完成重新确认。data flush 失败不尝试根输出，按 FrameStore 合同停用其 Builder/Writer。旧/新/orphan 地址混合与循环闭包由应用负责，不声称自动识别未完成图；应用解码/数据读取及状态安装失败不撤销发布 |
| 容量 | 0B/超限 root bytes、JSON/任意 bytes 整体更新；最后合法帧末端/Fence 可越过 MaxOffset，下一次追加先于额外 barrier/输出拒绝，不回绕、分帧或复用完整位置 |

RefId 值/字段独立向量：local=`0x89ABCDEF` → `EF CD AB 89`；1、高位、全 FF 合法，零及0/3/5B拒绝；失败 default/不提交消费、encoder 短目标/默认/错上下文无写入，长目标只改前4B。等值/hash/default/LocalId、同库重开与不同 VS 共借 data 的误传分别验证；Read/Publish/History/Fork 的跨 VSID 拒绝先于 I/O/barrier，mutation 为 NotAttempted。checked 来源元数据/路径身份不证明源健康或签发 revision。

RefRevision 向量覆盖完整 VSID/local/ticket、同 offset 不同 length、同值重发及子初始/源的不同 revision；default 属性/比较/hash 合法而作为输入拒绝。保存值结束查询、关 owner 并重开同 VS 后仍可 fork，直接重读 exact 内容并执行本次确认，不重新扫描源后缀；继承记录返回实际源 RefId/Ticket 并能关联声明边。确认后构造只保存已资格化值，不重读自身；来源声明/提前 ticket 不能签发 revision。LocalId 持久保存与正确库 checked 查询独立验证。

格式门独立 golden：version=1，VSID bytes=`01..10`、DataID bytes=`11..20`（十六进制），L=256 编为 `00 01 00 00`；44B codeword 的末4B=`9E 31 2A 6A`（CRC32C=`0x6a2a319e`）。不以相同 encoder round-trip 替代。

header 独立 golden：version=1、VSID=`01..10`、自身 local=`0x89ABCDEF` 的无来源 payload 为 `01 00 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 EF CD AB 89`；有来源追加 source local=`0x76543210` 与 SizedPtr(offset60,length40)，suffix=`10 32 54 76 0A 00 00 3C 00 00 00 00`。覆盖24/36之外长度、截短/尾随、零/错身份/未知版、自来源及非法 ticket；另行重新封装的合法24B帧不是原36B帧截短。

人为截短正式文件的恢复向量与 private 完整初始化后 rename 的正常创建中断分别取证。旧 RefFrameSchemaProbe 的8B LocalRefId/fixed12/meta0与112→104→112→None现象保持历史身份，不能替代当前格式/新库验收。源码、进程终止、Windows/Linux 与包资格分别报告；组合与交付唯一见 [S6](../FrameStore-VersionStore/06-integration-and-delivery.md)。
