# S4：VersionStore 完整根字典与单文件 ref 发布

状态：**Draft；完整 RootMap、单文件 ref、ForkOrigin、值/codec 与 PublicationOutcome 合同已定；2026-10-10 采用无 branch 名称、持久预留低号区及自动/预留区指定创建。初次建库、实际根/独立 owner 锁、唯一 pending 工作区、ListRefs 与停用清理合同已定，D3 设计阻塞关闭；公共借入 guard、VersionStore 项目及运行/平台验收尚未实施**。
2026-10-10 用户采用固定 4B LittleEndian LocalRefId；新 `[F-VS-REF-ID-4B]` 替代旧 8B 编码条款。格式门上界、ref 自身/来源/CU 成员中的本地编号与公开入口统一使用 uint32，路径同步采用 8hex。VersionStore 尚未实施，首版统一格式版本仍为 1，不提供旧 8B 草案兼容或宽度探测。
2026-10-10 RootMap 地址字段改为 S2 `[F-FS-FRAME-ADDRESS-VARINT]`，fixed12 公共 codec 保留；VersionStore 尚未实施，无旧 RootMap 格式兼容 fallback。本次变更不修改 FrameStore 文件格式或版本。
记录元信息唯一消费 [S4 元信息专项](08-versionstore-record-metadata.md)：固定 8B 时间与长度判形；本文负责宿主和公开结果，不另定义时间规则。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)。历史与不可变 tag 见 [S5](05-versionstore-names-and-indexes.md)。

## 目标、归属与范围

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份。借入一个 data FrameStore，拥有独立发布目录、其中的 RBF3 帧文件及其资源；格式门是独立普通控制文件。直接使用主线 FrameStore/Rbf 与 Binary 基元，不引用冻结旧栈或业务库。
发布目录格式门持久绑定格式版本、VersionStoreId、DataStoreId 与不可变 ReservedRefIdUpperBound。Create/Open/OpenReadOnly 先消费 `[S-VS-DATA-BORROW]` 的入口可用性与模式检查；Open 再在任何发布文件恢复/写入前核对借入 data 的持久身份。发布位置不解释为 data FrameAddress，data 地址也不解释为 ref revision。
单 owner/driver 串行操作，包括借入 data 的相关操作；发布目录独立消费 `[S-VS-OWNER-LOCK]`，writer 排斥其他全部 VS owner，只读 owner 可共享。VersionStore Dispose 释放 owned 文件、当前遍历资源和锁，不 Dispose 借入 data。根、创建/打开、工作区与停用清理按下面条款执行，实际资源及平台资格随实施验收。

首版不创建独立 Commit 对象，不为每条 Snapshot 保存 Parent，不采用 Prepared handle、nonce 账本、默认 CAS 或精确 InspectPublication。ref 的创建来源由首帧 ForkOrigin 表达，文件内发布顺序仍由实际帧链表达。不提供跨 ref 原子事务；业务谱系、随机数状态、tool-loop 阶段、operationId 和外部副作用协议由应用保存和解释。

## VersionStore 格式门与 data 绑定

### spec [S-VS-DATA-BORROW] 入口检查与持续借用分别承担责任

Create、Open、OpenReadOnly MUST 接收实际借入的非 null data FrameStore；完成纯参数预检后，在本次 VersionStore 的任何文件/目录 I/O 前，调用 S2 `[A-FS-ENSURE-USABLE]` 的 `data.EnsureUsable()`，再读取并检查 `IsReadOnly`、捕获其 canonical StoreId。包括根/锁 bootstrap、门创建、发布文件恢复和 private 清理在内的副作用都必须在入口资格成功之后；不另开 data 路径、实例或读取其门来替代借入 owner。

| VersionStore 入口 | data 模式准入 | 绑定消费 |
| --- | --- | --- |
| Create / 可写 Open | MUST 为可写 data；只读 data 在 VS I/O 前拒绝 | Create 保存其实际 StoreId；Open 与 checked 门 DataStoreId 比较 |
| OpenReadOnly | 允许可写或只读 data | 与 checked 门 DataStoreId 比较；不因借入可写 data 而取得 VS 写入资格 |

只读 VersionStore 不修改、恢复或确认 data；调用方自行使用借入可写 data 仍受串行合同约束。模式准入不要求 data 零活跃 Builder、不确认输出、不完成或取消 Builder；发布时的完成依赖与 data barrier 继续按既有合同判断。VS 自身的根/锁、门建立和工作区另消费本篇已定合同。

入口 guard 的异常原样传播，不签发 VersionStore owner，不产生本次 VS 持久化副作用；后续模式拒绝、门/绑定或 VS 初始化失败也不得 fault/Dispose 健康的借入 data。已打开 data 在借入前按自身合同执行的恢复或输出不属于本次 VS 入口检查。公共 guard 当前尚未实施，生产 VS 工厂消费它之前必须补齐该窄转发与对应验证，不能仅凭 `StoreId` / `IsReadOnly` 宣称已经完成生命周期检查。

调用方 MUST 在 VersionStore 借用期间保持同一 data owner 未 Dispose、未 fault，并将两层相关操作和 Dispose 串行化；不得在同步调用或其输入/visitor 回调中插入不受合同允许的借入 data 操作。入口检查不维持租借、不监控后续状态，不为所有纯发布目录查询新增 data 探测或违约诊断保证；真正的 data 读/屏障继续消费 FrameStore 自己的生命周期与 fault guard。data 后续 fault 时停止依赖它的 VS 流程，并按两层既有规则清理/重开；VS 操作已经触发 data/源确认异常时，仍按 `[A-VS-PUBLICATION-EVIDENCE]` 停用 VS，不改变 PublicationOutcome。

VersionStore Dispose 不调用此借入 guard、不 Dispose data；即使 data 已 fault/Dispose，仍必须尝试清理 VS 自己的资源。已交付的自有快照和值继续遵循各自生命周期，不因 borrowed owner 后续失效而撤销。

### spec [F-VS-OWN-FORMAT] 固定记录绑定发布库与借入 data

VersionStore MUST 有独立、create-only 的格式门；普通 ref/tag 发布不改变它。正式名称为精确小写 `versionstore.format`，是恰为 44 bytes 的普通文件，不是 RBF 文件：

| byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | VersionStoreFormatVersion | uint32 LittleEndian，首版为 1，选择本层目录/header/payload schema 及允许的 TailMeta 形状 |
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

## term `Root-Map` 应用命名的根地址字典

`RootMap = string => FrameAddress` 的完整值快照。多个根在同一发布帧内一起改变；地址指向同一个绑定的 data FrameStore。key 及其含义属于应用，VersionStore 不识别 State、树或图。

### spec [S-VS-ROOTS-OPAQUE] 完整根字典由应用解释

RootMap MUST 使用唯一的 Ordinal key，不做隐式大小写折叠或 Unicode 归一化。空字典合法，且不同于 missing ref/tag。发布 MUST 先私有复制输入并编码为完整快照，避免调用方后续修改输入改变本次请求或返回值。
库校验字典、key 和地址的格式/容量，核对 VersionStore/data 格式门身份及 VS 自身生命周期；借入 data 的入口检查与持续使用前提消费 `[S-VS-DATA-BORROW]`。FrameAddress 继承 S2 的局部地址合同：裸地址不携带原始 StoreId，库不能识别来自另一 store 的恰巧合法坐标。应用 MUST 保证所有引用属于该 data，所需新增依赖已完成，闭包不含 provisional/canceled 地址。数字地址本身不能证明完成来源；库 MUST NOT 把合法坐标或 data barrier 当成整个业务图已成立的证明。读取物理完整 orphan 后采用它，仍需由应用重新建立闭包资格。
读取返回自有 RootMap 值；不持有 RBF pooled frame、Span 或枚举器临时缓冲。地址背后的内容由应用按 FrameStore.ReadFrame 完整读取与解释，VersionStore 不预读/遍历业务图。
ReadRef 的成功值称为 RefSnapshot，提供只读 Revision、Roots 与本帧 RecordedAtUnixMs；由库内部构造，不持有 owner/reader、不需要 Dispose，字典无公开修改入口。S5 历史读取复用同一自有结果，不另立历史快照或结果租借。RefRevision 消费 `[A-VS-REF-REVISION-VALUE]`；RootMap/RefSnapshot 表示与 RootMap codec 分别消费下面的自有值和 BPV1 组合合同。

### spec [A-VS-ROOTS-OWNED] 公开复用集合接口，内部冻结完整内容

首版 RootMap MUST 仅作为逻辑名称，公开输入与输出使用 `IReadOnlyDictionary<string, FrameAddress>`，不新增 public RootMap、集合 Builder 或比较器类型。非 null 空字典合法；null 字典、null key、实际重复的 Ordinal key 和 default FrameAddress 为确定输入拒绝；key/wire/容量消费 `[F-VS-ROOTMAP-BPV1]`。接口自身不证明所有权、不可变性或比较政策，不能直接保存调用方集合或只读包装。

外部输入 MUST 逐项捕获到私有 `Dictionary<string, FrameAddress>(StringComparer.Ordinal)`：检查实际枚举的 key/地址与既定容量，通过 TryAdd 拒重，不继承输入 comparer，不以输入 Count 或 TryGetValue 决定正确性或分配规模；随后以显式 `StringComparer.Ordinal` 形成自有 `FrozenDictionary<string, FrameAddress>` 并编码。输入枚举器的必要清理、捕获、冻结、编码与确定检查 MUST 在本次 data barrier/发布输出前完成；纯输入捕获或分配失败保持 NotAttempted，不新增 owner fault。不得直接用 ToFrozenDictionary 代替拒重步骤，其重复键行为是覆盖而非拒绝。磁盘解码同样先按 codec 拒绝重复/非法条目，最后交付自有冻结内容，不保存 pooled bytes 或复用下一帧的可变 scratch 字典。

调用方 MUST 在同步输入捕获期间保持集合稳定，输入接口访问及枚举器清理不得重入本 VersionStore 或借入 data（包括 Dispose）。自定义输入仍可能执行调用方代码；这是既有 single-driver/no-interleaving 的使用前提，不新增库诊断违约枚举器的保证、OperationActive 或跨 API busy 框架。S5 的 HistoryActive 对获准历史 visitor 的动态限制继续照原合同执行，不能代替输入使用前提。

返回的 Roots MUST 持有上述自有冻结内容，通过正常集合接口不能改写；map、Keys、Values 不得暴露可变 backing 集合。string 与 FrameAddress 自身不可变，浅捕获足够；不复制或预读地址背后的业务对象。内部已捕获/解码的冻结字典可共享给结果与初始字典资格检查，交付不再复制内容，合法空字典也可共享。返回值在查询结束/提前停止/后续失败及 owner fault/Dispose 后仍有效，不需要结果 Dispose。

RefSnapshot MUST 为非 record 的 `public sealed class`，仅由库内部构造，公开 `RefRevision Revision { get; }`、`IReadOnlyDictionary<string, FrameAddress> Roots { get; }` 与 `long RecordedAtUnixMs { get; }`，无 setter/init、owner/reader、结果租借或 Dispose。库成功交付的对象及 Roots 均非 null；default(RefSnapshot) 是 null，不作为 missing 或空快照。普通对象相等保持引用语义，不提供 IEquatable、结构 hash 或内容相等运算；重复读取的对象是否复用不作承诺。CreateRef、ReadRef、PublishRef 的成功值统一为 RefSnapshot（返回 `AteliaResult<RefSnapshot>`），新 RefId 由 `snapshot.Revision.RefId` 取得，不另设重复 pair/创建 DTO；S5 其余操作的返回表示仍由各入口定稿。

时间属性消费元信息专项 `[S-VS-RECORD-TIME]` 与 `[F-VS-RECORD-TAILMETA]` 的原始有符号毫秒标量，保存本帧值、不在访问时重新取时/转换日期；不另立元信息 DTO。

库用于 ForkOrigin 字典一致性资格的内容比较 MUST 针对已经资格化的 Ordinal 自有字典执行：Count 相同，逐 key 查找并比较完整 FrameAddress 等值。不得用枚举顺序、Roots.Equals、Snapshot.Equals、编码 bytes、CRC 或 hash 代替；不因此合并同值 revision。首版只保留内部比较逻辑，不新增公开 ContentEquals 或结构 hash API，出现明确应用消费者再评估；应用仍可读取字典并自行比较。Snapshot 构造只保存已资格化 revision、冻结引用与时间标量，不再次验证、复制、编码或读回；正常发布仍先写 Confirmed 再交付。class 分配或后续安装可能失败，确认后异常依既有证据合同保留 Confirmed，不为免除一次分配引入可变占位壳或提前公开 revision。

（Informative）[OwnedRootMapProbe](../../experiments/OwnedRootMapProbe/README.md)在 .NET 10.0.5/Windows 上验证 0/1/4/100 项：ReadOnlyDictionary 的 map/Keys/Values 均可通过 ICollection.SyncRoot 取出真正 Dictionary 并改变可见内容；FrozenDictionary 在探针列出的普通集合入口无该路径，直接冻结会覆盖重复 key。首版选择标准 BCL 冻结集合以减少自写封装，不声称最少分配或性能最优；捕获与冻结有临时内存/构建成本，仅实现只读接口且安全投影 Keys/Values 的私有封装或其他不可变集合也可正确。class/struct 都可满足自有结果，普通 class 避免另一套非法 default/生成等值合同，是工程取舍。不可变保证不认证反射、Unsafe 或违反 immutable marshal 所有权约定的改写；探针不构成 RootMap wire、新库发布/恢复或包消费资格。

### spec [F-VS-ROOTMAP-BPV1] 字典复用无损基元，一个 codeword 容量上限

VersionStoreFormatVersion=1 MUST 选择 [BPV1](../Binary/bare-primitive-value.md) 的普通 VarUInt32/string 规则，内部 RootMap codec 使用现成 Atelia.Binary，不新增公开 codec、类型 tag、nullable/control、压缩或另一套字符串算法。RootMap codeword 仅包含下列字段；宿主版本/kind 与完整 RBF CRC 不在每份字典中重复：

| 顺序 | 字段 | 编码 |
| --- | --- | --- |
| 1 | 实际 entry count | BPV1 VarUInt32 |
| 2，重复 count 次 | 非 null key | BPV1 ordinary BareString，原样保留 UTF-16 code units |
| 3，每个 key 后 | FrameAddress | S2 `[F-FS-FRAME-ADDRESS-VARINT]`：VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())，无符号，合法地址 3..15B |

Key 的字符域 MUST 保留全部非 null CLR string 的 code units：empty、NUL、控制符、路径分隔符、U+FEFF 及未配对 surrogate 均无额外字符拒绝，长度受下述 codeword 预算限制；不剔除 BOM、不归一化或折叠。这是应用 key，不继承 tag/path 政策。writer 使用 PrepareString 的最短无损选择及 FrameAddress.WriteVarInt 的最短地址表示；reader 接受 BPV1 指定的另一种合法字符串编码、字符串/count 有界冗余前缀及 S2 允许的冗余地址表示，非法 UTF8 明确拒绝而非 replacement。条目无排序要求，reader 按解码后的 Ordinal key 拒重；同 key 不同字符串表示也重复。wire bytes/枚举次序不替代 `[A-VS-ROOTS-OWNED]` 的内容比较，也不承诺同字典产生同 bytes。

首版完整 RootMap codeword MUST 不超过 **1,048,576B（1MiB）**，包括 count、全部实际 string header/payload 与地址；读写使用同一固定上限，不增加独立 key/count 配额或配置。默认输出精确尺寸为 `M = MeasureVarUInt32(n) + Σ(StringEncodingPlan.EncodedLength + address.MeasureVarInt())`，逐项度量地址并用 checked long 累积，不能用 FrameAddress.EncodedSize 或固定变长宽度代替；捕获过程中按实际条目检查增长，不信任源 Count，超限不得先冻结/物化超限完整输出。过长 key 可用 code-unit 数的必然尺寸下界先拒绝，再用现成 plan 确认精确尺寸。全部确定拒绝在 data barrier/发布输出前、NotAttempted 且无新增 fault；不得截断 key、拆 map 或丢弃条目来适配。

内部 decoder MUST 在局部 cursor 上组合读取。count 前缀读完后，在集合容量分配前要求 `4L × count <= min(实际剩余输入, RootMap 剩余预算)`；每项至少 1B empty string codeword 加 3B 地址。字符串分配前还须检查其**实际** header 长度与声明 payload，同时给本项地址保留至少 3B、给后续每项保留至少 4B；若尚余 n 项，必需 suffix 为 `3L + 4L × n`，不是地址实际长度的预测。可窥读至多 5B 的 BPV string header，或先截出扣除必需 suffix 的借入窗口交给现成 reader，不新增 public API。检查 int 范围、实际剩余和预算后才物化 key；地址在本次剩余输入/预算内调用 S2 ReadVarInt，消费长度可为 3..15B，包括合法冗余表示，失败时不提交消费，且不得改为 default 继续。全部条目/拒重/冻结成功才提交本次 RootMap 消费；损坏、超限及资源异常沿宿主现有错误通道传播，不交付部分字典。

容量及消费 MUST 按 reader 的真实 ConsumedCount 计，包括合法冗余 count/string header 与地址字段，不能用 Measure(decoded) 重新推算已消费长度。RootMap 是可组合的 prefix，不增加 map byte-length：宿主在自己的局部 cursor 上继续解析其余字段，整条 payload 完全消费及所需 CRC/kind/身份资格通过后才交付公开结果。Snapshot、tag、后续 CU 候选复用这份 codeword；当前 Snapshot body 若只含 RootMap 就必须 exact-consume，其他宿主由自己的 schema 确定前后字段，不忽略 trailing。

1MiB 不是完整 tag/CU payload、物理 FrameLength 或文件容量上限。宿主 MUST 将自己的前后字段、RootMap 与 TailMeta 按 checked 尺寸组合，转换到 int 前验范围，再用公共 MeasureWriteSize 校验完整帧，追加起点另验；完整读取前按 RbfFrameInfo 检查该宿主上限，不先租超限全帧再拒绝。ref header/普通 Snapshot 消费 `[F-VS-REF-FRAMES]`；tag 名称与完整记录容量见 [S5-B 专项](09-versionstore-immutable-tags.md)，CU Members 的组合上限仍由其候选定稿，不把本条解释为所有宿主已经成立。

（Informative）1MiB 是限制单次完整快照工作规模的 MVP 工程选择，会拒绝仍可由 RBF 表示的更大 map；仅继承 RBF 硬界也可正确。没有现成大 map 消费者或峰值/吞吐测量证明这个数字最优、足够所有业务或不会 OOM；捕获、冻结、编码及读取仍有额外内存。实际需求超过此界或资源成本不合目标时再评估，不能在已发布版本中默改 accepted set。复用 Binary 减少自写规则，实际增加 Binary 与其精确 K4os `[1.3.8]` 依赖；固定逐 code-unit UTF16LE 也可正确，不声称此方案最快。BPV1 [已实施验收](../Binary/bare-primitive-value-acceptance.md)及[基元独立 golden](../../tests/Binary.Tests/SchemaCompositionTests.cs)是底座证据；[RootMapCodecProbe](../../experiments/RootMapCodecProbe/README.md)原 fixed12 synthetic 地址版本通过 47 项组合检查，含当时的 1MiB 边界、宽容/重复 key、截短与 12/13B suffix guard。该结果保留历史身份，不构成本次变长 RootMap、尚未实施的新库、发布/恢复、平台或包资格；本次 FrameAddress 新增能力的独立源码验证另见[变长地址实施记录](02-framestore-varint-address-implementation.md)。

## RefId 值与内部字段编码

### spec [F-VS-REF-ID-4B] 公开身份绑定上下文，持久字段使用固定 4B

首版 RefId MUST 为不透明、自有的非 positional `public readonly struct RefId : IEquatable<RefId>`，提供 `Equals(RefId)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`。逻辑值为 `(VersionStoreId, LocalRefId)`：前者直接保存 `[F-VS-OWN-FORMAT]` 的 16B canonical 持久身份，后者为非零完整 uint。等值 MUST 比较两者全部 bits，相等值具有相同 hash；比较与 hash 是纯值操作，不读取 owner 或磁盘，hash 不持久化或保证跨进程稳定。同一 VS 的相同 local 值跨 owner 重开仍相等；不同 VSID 下的相同 local 值不相等，不因共借同一 data 而合并。
普通内部表示使用两个私有 ulong 保存 VSID，另一个私有 uint 保存 LocalRefId，不转储 CLR 内存或规定 Pack/Size/sizeof/数组步长。类型不持有 owner、epoch、buffer 或需 Dispose 的资源。仅增加纯值只读 `uint LocalId { get; }`，default 返回 0，其余值返回完整本地编号；不能用 LocalId 等值代替完整 RefId 等值。无 public 数值构造、Deconstruct、IsValid、Guid 转换、算术/排序或规范文本。default 为唯一非法值，自等/比较/hash/属性读取合法，但不能访问/编码，不作可选来源的持久 sentinel。

接受 RefId 的入口 MUST 在既有 owner disposed/fault 检查之后、任何目标发现 I/O 或 data barrier 之前，拒绝 default 及与本 owner 持久 VSID 不符的值。它们是确定参数拒绝，不新增 fault；mutation 仍先初始化 outcome=NotAttempted。MUST 比较持久身份，不以 owner 引用代替；先前取得的自有值可以交给同一 VS 的重开 owner。此检查仅拒绝不同 VSID 的误传，不证明目标存在、物理根唯一、身份生成无冲突或记录完成。

**唯一内部字段 codec。** header、自身/来源身份与后续 CU 成员表中的 RefId 字段 MUST 恰为 4B，按完整 LocalRefId 的 uint32 LittleEndian 编码；不在每个字段重复 VSID，不追加版本、CRC、flags 或预留。所有非零 32bit 位模式均数值合法，包括 1、高位及 uint.MaxValue；它不是 SizedPtr 或 FrameAddress，数值合法性不证明时间或分配顺序，不检查票据对齐/坐标或 UUID 位模式。
内部 decoder MUST 要求字段输入恰为 4B，失败输出 default；只在非零检查通过后，结合宿主的预期 VSID 构造值。普通打开/读取的预期身份来自已 checked 的门上下文，记录内的身份须与之比较，不能从被检查记录反向认领期望身份；创建阶段可使用私有初始化已预定的非零 VSID，不要求初次 Create 先读取已发布门。从持久记录字段解码并交付值时，磁盘字段与整个宿主的必要身份/CRC/codec 校验须全部通过；ListRefs 的路径身份另消费 `[A-VS-REFS-DISCOVERY]`，不据此绕过记录校验。
内部 encoder MUST 先检查值非 default、VSID 与宿主预期身份相符及目标容量至少 4B；失败不改变任何目标 bytes，成功只写前 4B，剩余目标不变。复合 reader 先保证剩余至少 4B、切出精确字段，成功才提交消费；截短/零字段失败不提交，宿主负责全部记录的剩余字段及 trailing bytes。两方向不执行 I/O、不新增堆 buffer 或专用错误族，宿主映射为自己的字段错误；可信私有构造只保存已资格化身份值，不在发布确认后的成功值交付中重新解码、读回自身文件或引入可拒绝校验。

RefId 可以来自正常创建结果、checked 当前/历史读取、checked 来源元数据，或 ListRefs 的 checked 门上下文与 canonical 正式路径。最后一种只提供正式占号/定位身份，不证明文件 header、Snapshot 或来源健康；持有 RefId 本身也不证明所声明源存在/成员/发布资格。私有 planned RefId 不在正式发布前交付应用或签发 revision；应用预先选择低号整数不等于取得已发布 RefId。RefRevision 直接复用 RefId 上下文，仍只按成员/发布合同签发。
首版不提供整体 RefId 的公开 EncodedSize、bytes/text codec 或自动序列化适配；应用在特定 VersionStore 的目录/配置上下文保存 LocalId，再按下面的 checked 查询恢复该库的 RefId。内部 4B 字段不是无上下文的完整身份编码，不承诺自由构造身份或辨认跨库混放的应用目录。
（Informative）固定 4B 是用户采用的当前容量/编码取舍：每库最多 4,294,967,295 个非零本地编号；不删除、不复用，容量约束终身累计正式创建的 ref，而非 Snapshot/数据帧或更新次数。自动区有 Max−L 个编号，预留区有 L 个指定槽。唯一分配/不复用/耗尽消费 `[S-VS-REF-ID-DOMAINS]`，不以随机碰撞概率或字段宽度代替证明；不预建 64bit 扩展、逃逸编码或动态宽度。公开值另携已有 VSID：A/B 各有 local=7 时，A 的值误传 B.PublishRef 会被拒绝，纯 local 查询则由调用方保证选对库。20B 逻辑字段之和不成为 CLR ABI，不承诺本次缩宽降低 struct 的内存步长或改善性能；不新增身份、通用 ID 框架或 public StoreId 类型。固定 LE 可直接用 BCL 基元，[已有 UInt32LE](../../src/Binary/BareValueWriter.cs)与[独立 golden](../../tests/Binary.Tests/ScalarTests.cs)仅佐证字段组合，不强加 Binary 依赖，也不构成新 RefId 实施资格。

### spec [A-VS-LOCAL-REF-LOOKUP] 本地编号在显式库上下文定位已发布 ref

VersionStore MUST 提供 `ReadRef(uint localRefId)`，与 `ReadRef(RefId)` 返回同一种自有 RefSnapshot。先检查 owner disposed/fault 与编号非零，再按 `[F-VS-REF-PATHS]` 直接定位并执行既有当前值资格；成功结果的 Revision.RefId 携带本 owner 已 checked 的 VSID。missing/损坏/I/O/模式与恢复语义与 RefId 入口相同，不以 default、空字典或自由构造 RefId 代替 checked 读取。查询可访问预留区或自动区的已发布 ref，不受指定创建的上界限制，不存在目标时不创建。

应用可用常量/枚举选择固定槽位，或保存自动创建结果的 LocalId，冷重开同一库后 ReadRef(local)。这次读取不恢复旧 revision 或证明旧尝试 Confirmed。纯 LocalId selector 由调用方显式选择库，不能检测将 A 的本地号误放入 B 的目录；应用 MUST 保持目录与正确库关联，跨库目录另核对其库归属。完整 RefId 入口仍在 I/O/barrier 前拒绝错误 VSID，不退化为仅 local 比较。

## term `Ref-Revision` 已接受快照的位置身份

RefId 由 `[F-VS-REF-ID-4B]` 定义，是正式 ref 文件的稳定身份；应用名称/业务标记不进入文件名或库内绑定。RefRevision 为库签发的自有位置值，绑定完整 RefId 及实际快照位置/长度，不与 data FrameAddress、仅 offset 或 owner 引用互换。
revision 只从完成发布或实际 checked-read 且已确认真实主链成员的当前/历史快照取得，不在输出前签发。不同完整物理快照即使 RootMap 同值也具有不同 revision。它不是某次未完成调用的尝试 token；首版不提供凭裸位置随机 ReadRevision，也不以字典相同证明某次调用成功 flush。S5 的按 revision fork 是内部重读与校验来源的窄创建入口，不开放任意位置读取。

### spec [A-VS-REF-REVISION-VALUE] 自有 revision 复用身份与完整 ticket

首版 MUST 使用非 positional `public readonly struct RefRevision : IEquatable<RefRevision>`，普通内部表示仅保存私有 RefId 与 SizedPtr，不另存 VSID、DataStoreId、owner、epoch、Reader、RootMap 或耐久标志。提供 `Equals(RefRevision)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`；等值 MUST 比较 RefId 的完整上下文及 ticket 的完整 Packed bits（含 length），相等值具有相同 hash。值操作不执行 I/O，不依赖 owner 健康/存活；hash 不持久化或保证跨进程稳定，不规定 CLR Pack/Size、sizeof 或数组步长。

公开只读属性为 `RefId RefId { get; }` 与 `SizedPtr Ticket { get; }`，返回快照实际所属 ref 与实际 ticket；继承历史的源快照不改绑到发起查询的子 ref。两者均为纯值投影，不触发读取或签发另一个 revision。default 是受支持公开构造路径中的唯一非法值，自等/比较/hash 和属性读取均合法，两属性分别返回 default；所有接受 RefRevision 的入口 MUST 在既有 owner 生命周期/模式 guard 后、任何目标发现 I/O 或 data barrier 前，拒绝 default 及不同持久 VSID 的值。确定参数拒绝不新增 fault，mutation 仍先初始化 outcome=NotAttempted。

正常发布结果只在所需 flush/发现边界确认并先写回 Confirmed 后对外签发；当前/历史读取只在本次必要 header、真实成员及完整 CRC/kind/codec 资格成立后交付。可信内部构造仅保存已资格化 RefId/ticket，不在确认后的值构造中再验证、分配、扫描或读回自身文件。结构扫描的 RbfFrameInfo、ForkOrigin 声明、planned ticket、私有未发布快照均不能单独签发 RefRevision；值本身不持有 RbfFrameInfo/reader。

已交付值在历史调用结束/提前停止/后续失败及 owner fault/Dispose 后仍可比较和保存；同一 VS 的重开 owner 可接受它，不按实例引用或 epoch 拒绝。实际 fork 仍消费 S5 `[S-VS-FORK-FROM-REVISION]` 的正式源定位、exact offset/length 主链成员与完整内容重读，再执行本次 data/源确认；持有旧值不免除此协议，不保证源仍存在/内容健康、当前 head 相同、本次耐久或旧调用曾 Confirmed。此复检是现有统一 fork 流程，不增另一份成员见证或构造时扫描；持久身份也不证明物理根唯一或认证同 VSID 拷贝。

不提供公开位置构造/转换、setter/init、Deconstruct、IsValid、算术/排序、规范文本或整个 RefRevision 的公开/持久 codec。ForkOrigin 仍组合宿主上下文、4B RefId 字段与 8B Packed LE ticket；不能从这些元数据直接构造 checked revision，也不新增完整 revision 记录、版本或预留字段。暂不支持匿名精确 revision 独立外存后跨进程直接导入；tag 仅保存 RootMap，不能透明替代其精确 ref 历史来源。出现明确消费者时再立带完整上下文的窄导入/导出合同。

（Informative）只读投影是使现有历史与 ListForks 来源边直接关联的工程选择，不是用户另行规定的 API；全私有值也能正确完成选点后 fork，声明边本身也能按 ticket 分组。A1/A2 同字典而分别分出 B/C 时，只比较 RefId 或 RootMap 不能识别选中点的孩子，两属性允许直接比较已有 source 字段，无专用 MatchesLocation API。它们只观察首版单文件位置，未来分段需重审定位/观察合同，不承诺此 tuple 永远足够。无公开构造是窄签发表面，不是不可伪造安全机制；公开构造加严格 fork 校验也可正确，但当前没有裸位置请求消费者。旧 [CheckpointAddress](../../../durable-graph/src/DurableGraph.Persistence/CheckpointAddress.cs)及[重开失效测试](../../../durable-graph/tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs)是实例绑定的另一模型，不成为本栈兼容义务；[结构扫描仍返回坏 payload 的测试](../../tests/Rbf.Tests/Internal/RbfScanReverseTests.cs)佐证内容资格不能只由 framing 代替，不构成新值类型或 VersionStore 实施资格。

## 单文件存储与最小 codec

### spec [S-VS-REF-FILE-SINGLE] 每个 ref 保存一个完整快照序列

每个 ref MUST 使用按唯一 RefId 命名的一个 RBF3 文件。首版不分段、轮转、差分或建立派生 checkpoint；每次更新追加一条完整 RootMap Snapshot。当前值校验本地 header、初始 Snapshot 及紧贴 EOF 的末 Snapshot，初始即末帧时复用读取；不扫描全部历史或递归祖先。
选尾 MUST 使用真实 RBF 逆向主链和 `showTombstone: true`，检查末帧紧贴实际尾部，不能将过滤结果或随机 ticket 当成末快照成员证明。未知 kind/格式版/meta 形状、tombstone、非 Snapshot 尾帧、完整坏 CRC/codec 皆报错，不向前寻找旧快照替代。首帧为本层 header，第二帧为初始 Snapshot；两者都必须存在且 checked。ReadRef 只校验本地必要内容，不沿 ForkOrigin 递归读取祖先；声明的源历史缺/坏在实际历史访问中报错，不据此回退本 ref 的当前字典。
单帧容量与下一帧起点硬界继承 RBF/SizedPtr；下一帧起点超过 SizedPtr.MaxOffset 时明确拒绝追加或要求维护，MUST NOT 隐式回绕、复用完整历史位置或自行拆成多帧发布。MaxOffset 只限制帧起点，最后一个合法帧的末端与尾 Fence 可以越过它，不额外要求文件总长度落在该起点上界内。容量预检使用 RBF 公共 Measure/追加预算 API，并分别检查追加起点；预算 API 不自行验证文件起点，不能把 MaxOffset - TailOffset 当作硬性追加字节预算。

### spec [S-VS-REF-FILE-PUBLISH] 新 ref 由完整单文件发布

所有 ref MUST 使用 `[F-VS-REF-PATHS]` 的单文件布局；正式文件出现是发现边界，不设 ref 容器或名称子目录：

| 路径角色 | 内容与资格 |
| --- | --- |
| `refs/<R>.rbf` | 唯一正式 ref 文件，含 header 与 Snapshot 序列 |
| `creating/pending.rbf` | 全部初始化共用的唯一未发布工作文件；冷开裁决消费 `[S-VS-CREATING-WORKSPACE]`，路径不编码 R |

自动/指定创建 MUST 在 `creating/pending.rbf` 完成 header 与完整初始 Snapshot，flush/close 后，以同文件系统、不覆盖的一次 file rename 发布。不得直接在正式路径写初始化、先公开 header-only、跨卷复制/删除或覆盖既有 ref。fork 的 ForkOrigin 在同一 header 内，不增加第二发布单元；私有 flush 不建立对象或提前交付已发布 RefId/revision。

ReadRef/PublishRef/ListRefs/历史只依据正式文件，不采用 creating。正式 ref 按 `[R-VS-LOCAL-COMPLETE]` 打开并执行允许的恢复后，仍缺 header/初始 Snapshot 或必要内容损坏 MUST 报错，不当空槽、不补空字典、不删除或改号；目录项资格不证明内容健康。异常/进程终止可留 private，但完整 private 不自行成为 ref 或续作旧请求。只读排除且不清理 private；可写仅在独占 owner 下取消已证明私有的文件，不能删除正式对象。rename 结果不明消费 `[R-VS-PUBLICATION-UNKNOWN]`，不以清理回滚。

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

### spec [F-VS-REF-FRAMES] 首帧判别来源，普通快照组合字典与记录元信息

VersionStoreFormatVersion=1 的 ref 文件 MUST 使用 RBF3；首物理帧 FrameTag=0（RefHeader），其后普通 Snapshot 的 FrameTag=1。两种帧均 MUST 非 tombstone；RefHeader MUST TailMetaLength=0，Snapshot 的 TailMeta 消费 `[F-VS-RECORD-TAILMETA]`，首版恰为8B。kind 由 RBF tag 表达，不在 payload 重复；本条不分配 CU kind，不把 07 候选并入核心。必要读取遇到未知 kind、后续 header 或非法组合报错，不能过滤或向前寻找可用替代帧。

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

SourceSnapshotTicket 的纯数值检查 MUST 使用当前普通 Snapshot 的必要范围：`Offset >= Empty.EndExclusive + MeasureWriteSize(24,0).AppendLength`，且 `MeasureWriteSize(1,8).FrameLength <= Length <= MeasureWriteSize(1MiB,8).FrameLength`；当前为起点至少60、长度40..1MiB+36。Packed 已表达对齐/可表示范围，不额外要求末端或尾 Fence <= MaxOffset。实际源 header 可能使初始起点更大，数值合法仍不证明源存在、具体成员或字典一致；这些由 S5 实际访问建立。未来接纳 CU 时 MUST 重审可接受源 kind 和这份普通 Snapshot 上界，不能原样限制更大的 CU 宿主。

Snapshot decoded payload MUST **仅为 `[F-VS-ROOTMAP-BPV1]` 的完整 codeword**，actual payload长度为1..1,048,576B，exact-consume 后才交付；不加外层版本、身份、Parent、map byte-length 或 CRC。每个地址复用 S2 的变长 codec，tag/history复用同一RootMap；字典健康不赋予 data 来源/业务闭包资格。输出前用公共 `MeasureWriteSize(M,8)` 校验整帧及实际追加起点；读取前按 RbfFrameInfo 核对 kind/meta/tombstone、payload范围及与Measure对应的Ticket.Length，再租完整帧并执行完整CRC/codec。不将“少量根约64B”写成固定记录保证。

**共同首两帧检查。** 在已取得的 Idle RBF句柄上，按期望上下文要求 Format=Rbf3，用 `ScanForward(showTombstone:true)` 取首物理header，先检查首Offset=Empty.EndExclusive、上述形状和 `MeasureWriteSize(L,0).FrameLength`，再完整读/解码身份与来源。下一次MoveNext必须取得直接物理第二帧，按普通Snapshot全部形状/CRC/codec检查；其Offset须等于公共 `GetPhysicalOffsetImmediatelyAfter(headerTicket)`。两次MoveNext=false均先传播TerminationError，再报缺必需帧；不跳过墓碑、不用随机CRC成功代替顺序证明。保存实际初始ticket、checked ForkOrigin与自有初始字典；初始即末Snapshot时可复用，不递归源或审计本地全部历史。
令 `H=MeasureWriteSize(L,0)`、`S=MeasureWriteSize(M,8)`，初始起点为 `Empty.EndExclusive+H.AppendLength`，初始化后界 `I=初始起点+S.AppendLength`，须与实际checked物理后界一致。当前header FrameLength=52/64B、初始起点60/72；空map最小Snapshot FrameLength=40B、初始化文件104/116B，最大普通Snapshot FrameLength=1MiB+36B。这些是公共尺寸推导，不复制wire开销，不证明预约、耐久或平台资格。

**恢复后完整性准入已定。** 2026-10-10 用户采用 `[R-VS-LOCAL-COMPLETE]`：创建仍先完整 private 初始化、flush/close，再 rename 发布；正式 ref 的读取独立按 RBF 恢复后的实际文件执行上述首两帧检查。不要求恢复前的初始 Snapshot 已闭合，不追溯创建调用历史，也不增加恢复前前缀入口、初始化长度字段或账本。上述最小初始化长度只描述完整格式尺寸，不能替代实际内容检查。

（Informative）专用1B presence flag（25/37B）同样可正确，但当前两种header会因对齐各多4B物理占用；复用已有精确形状省去一个字段。正常36B帧截短不会变成24B：原HeadLen/descriptor/CRC仍约束原形状，CompletedTail只补Key/Fence，不重写来源/长度。恶意重写并重新seal不属于格式认证能力，flag也不提供它。[RefFrameSchemaProbe](../../experiments/RefFrameSchemaProbe/README.md)原 8B LocalRefId/fixed12 synthetic RootMap/meta0 版本的75项检查在W:验证字段golden、真实RBF形状/CRC与112→104→112→再次None现象，结果及原始JSON保持历史身份，未按本次 4B LocalRefId/变长RootMap/meta8重跑。该现象只反驳原先“最小长度＋事后拒绝能保护恢复前初始化”的政策；当前接受恢复后完整记录，无需保留旧报告。正常private flush/close→rename发布不会产生半初始化正式ref；该探针不构成正常发布的崩溃轨迹、本次格式、新VersionStore、进程终止、rename、两平台或包资格。

### spec [S-VS-REF-FORK-ORIGIN] 首帧保存一次性的 ref 创建来源

header MUST 保存明确的无来源/有来源判别；有来源时 `ForkOrigin = (SourceRefId, SourceSnapshotTicket)`，ticket 为源 ref 文件中的 SizedPtr，包含 offset 与 length，MUST NOT 使用 data FrameAddress。来源判别、固定字段及坐标范围唯一消费 `[F-VS-REF-FRAMES]`，数值合法不证明成员资格。来源限同一个 VersionStore 及绑定 data，上下文由格式门与两份 ref header 校验，不重复存 SourceStoreId，不从 CLR 内存布局推导编码。

ForkOrigin MUST 在私有创建时写入，发布后不可改变。无来源表示独立历史起点；复制相同 RootMap 不推断来源。有来源表示本 ref 的初始 Snapshot 按值复制该 exact 源快照，包含相同 Ordinal key 与 FrameAddress 值；它仍是子 ref 自己的新 revision。S5 创建入口负责建立源快照资格与两份字典一致性，后续历史跳转重新校验必要内容。

源 ref MUST 已正式发布，且源 ticket 必须是已接受快照的真实主链位置，不能指向 header、残尾或仅数值合法的位置。新 ref 使用 fresh RefId，禁止自链接；有效来源只能从新 ref 指向已经存在的 ref，故创建流程形成森林，无需持久 generation 或拓扑编号。跨文件历史另做 visited/cycle 检查，发现缺源、非法成员或循环报错，不截断为正常历史结束；全图声明查询的较窄资格见 S5。完整历史及正式 RefId 不删除、不搬迁、不复用；未来 GC/删除/分段需重新审定链接保留协议。

声明来源与实际读取源快照是不同资格：checked header 可返回 ForkOrigin 元数据，但不能仅据其中的坐标签发 checked RefRevision。当前值访问保持本地路径；S5 定义跨文件历史与全分叉查询的访问范围、预算和错误。

## 最小 API 与发布步骤

| 候选操作 | 输入 | 成功结果 |
| --- | --- | --- |
| CreateRef(roots) | 完整初始 RootMap；自动编号、无来源 | 自有初始 RefSnapshot，独立历史起点 |
| CreateRef(destLocalRefId, roots) | 预留区空闲目标与完整初始 RootMap | 同上，指定目标 create-only |
| ReadRef(refId) / ReadRef(localRefId) | 完整 RefId 或显式当前库的非零本地号 | 自有 RefSnapshot：RootMap 与实际末 revision |
| PublishRef | RefId、完整新/旧 RootMap | 自有已确认新 RefSnapshot |
| ListRefs | 本 VersionStore | 全部 canonical 正式路径对应的自有 RefId 集合，含预留区与自动区；不签发快照健康资格 |

创建/读取/更新的字典与成功表示消费 `[A-VS-ROOTS-OWNED]`；创建编号消费 `[S-VS-REF-ID-DOMAINS]`，本地定位消费 `[A-VS-LOCAL-REF-LOOKUP]`。同步 mutation 仍用 AteliaResult/必选 out PublicationOutcome，不增加 Prepared handle。PublishRef 无 expected revision，串行 driver 自行管理流程；未来 CAS 另审，不以 RootMap 同值代替 revision。

### spec [A-VS-REFS-DISCOVERY] ListRefs 交付全部正式路径身份

ListRefs MUST 在 owner disposed/fault guard 后，完整执行 `[F-VS-REF-PATHS]` 的实际直接项发现、名称、普通类型/no-follow/同文件系统及必要枚举清理。使用 owner 已 checked 的门 VSID 与路径 local 构造 bound RefId，包含预留区/自动区全部正式文件，排除 creating；空正式集合正常结束才返回空集合。

本调用不打开 ref 内容、不恢复文件、不读取 header/Snapshot、不修改或取消工作区、不执行 data I/O/barrier。canonical 正式文件即占号，即使其内容损坏或旧创建未交付也 MUST 列出；名称/类型/枚举/权限/I/O 或必要清理失败显式传播，不能过滤成 absent 或交部分成功。只读与可写 owner 使用同一发现合同。

全部发现及必要清理成功后，一次性交付不依赖 owner/枚举器的自有只读 RefId 集合；不外泄 raw sequence/目录枚举器，不承诺稳定顺序，具体集合包装/方法语法随实现选择。ReadRef 才签发当前内容/revision，ListForks 则另执行 S5 的首两帧与图资格；ListRefs 不证明快照/来源/data 图健康，不按同值认领旧自动创建或倒推旧 outcome。完整发现和结果存储成本为 O(正式 ref 数)，无持久 ID/path 表或健康索引。

### spec [S-VS-SINGLE-RECORD] 一次 ref 变更由一个 frame 生效

一次 ref 创建/更新 MUST 由一条完整 Snapshot 表达全部根值，不分成多个根的独立更新。data 构建/确认、私有初始化或纯容量试算均不改变已发布 ref。新 ref 另有正式文件的发现边界，遵循下面的创建协议。

### spec [S-VS-ROOTS-AFTER-DATA-CONFIRM] 根发布在必要完成数据确认之后

PublishRef 及 S5 CreateTag MUST 先完成确定输入、目标/名称及 owner 生命周期准入、私有拷贝与编码、记录/文件容量检查，再同步调用 `data.ConfirmDurable()`，然后向发布 RBF 追加完整记录并 `DurableFlush()`，最后安装内存状态/正常返回。MUST NOT 仅因 data 有未归还 Builder 而拒绝；未完成 Builder 不获完成或耐久资格。确定拒绝不追加、不调用 data barrier，也不因 guard fault。
data 屏障消费 S2 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`，覆盖 leased 文件内旧完成输出。应用仍 MUST 保证本次 RootMap 所需新增依赖全部已完成、属于绑定 data 且闭包不含 provisional/canceled 地址；屏障成功不证明该闭包，库不遍历业务图验证依赖闭包。无关 B 仍 Building 时，已完成的独立 A 可以发布；A 真正依赖未完成 B 时不能发布。
库不接受“以前已 flush”标志代替本次 barrier；旧 RootMap、历史 rewind、fork 初始快照和 tag 同样走屏障，无需复制 data。库不提供发布 callback，不允许同 driver 的其他应用操作交错；输入访问消费 `[A-VS-ROOTS-OWNED]` 的稳定/不重入前提。data flush 失败不得进入本次发布输出；发生异常按所涉 owner/fault 合同停用 VersionStore，不虚称未输出的另一个 owner 同样故障。
CreateRef 两种入口 MUST 先完成编号/目标、输入/编码/容量及 owner 准入，调用 `data.ConfirmDurable()`，再按 `[S-VS-REF-FILE-PUBLISH]` 初始化/发布完整文件；无关 Builder 不阻断。S5 两种 fork 入口同样 data-first，另确认源 ref 耐久，不先公开 CreateRef 再补来源；清理不补造 ref，不另写 allocation/Init 账本。

## term `Publication-Outcome` 本次调用的证据

`NotAttempted / Unknown / Confirmed` 分别表示尚未尝试改变公开发布事实、公开事实可能已生效但无法确认、所需发布 flush 与发现边界均成功返回。它是本次调用的非持久证据。ref 创建的发布点就是 no-overwrite file rename；rename 正常返回后立即写回 Confirmed，再安装内存状态或交付结果，没有第二次提交。rename 已生效后进程终止，可以留下已发布 ref 而调用方没有结果；重开校验合格的正式 ref 即接受当前发布事实，不要求恢复旧 outcome 或证明旧调用曾交付 Confirmed。

### spec [A-VS-PUBLICATION-EVIDENCE] 同步 mutation 通过 out 保留调用证据

首版所有公开的同步发布 mutation（完整 roots 发布、ref 创建或 tag 创建）MUST 返回相应成功值的 AteliaResult，并接受必选的 `out PublicationOutcome outcome`；包括两种 CreateRef、PublishRef 及 S5 的 tag 与两种 ForkRef。成功值类型由所属 API 定义，不增加通用 Mutation 方法、receipt 或恢复句柄。公开枚举如下，不是持久记录字段：

```csharp
public enum PublicationOutcome {
    NotAttempted = 0,
    Unknown = 1,
    Confirmed = 2
}
```

入口第一条语句 MUST 把 outcome 设为 NotAttempted，先于生命周期/模式/参数检查、输入复制、编码及任何 I/O。调用方每次先在 try 外初始化独立局部变量，异常路径仍可读取；以下是候选 API 的消费示意，不是已实施 API：

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
（Informative）只靠返回值无法传出抛异常时的证据；后置异常包装需要分配，确认后遇到 OOM 时，包装再失败会丢掉 Confirmed 和原异常。enum 的 out 写回无需分配，适配当前同步串行接口；异步接口另立合同，不为它预建载体。[RBF 故障测试](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs)已覆盖完整输出后异常而 TailOffset 未推进，以及 flush 前/后异常，不能按异常本身猜发布相位；本条是上层待实施合同。
证据赋值与 fault latch MUST 只更新现有字段/引用，不依赖新分配。公开协调入口直接更新这一 out；内部助手不得重置已推进的本次证据，或仅在正常返回后把局部 outcome 拷回外层。不调用其他公开 mutation 拼接本次事务。Confirmed 先写回，再进行内存投影/成功值安装、释放或正常返回；以后不能降级。

Outcome 与实例健康是两个事实：NotAttempted 不表示零 I/O 或可直接重试，Confirmed 不表示当前实例还能使用。确定前置拒绝不新增 fault；data/源确认、私有输出及公开输出/flush/close/rename 或确认后安装异常保守停用 VersionStore，并启动一次完整受控 teardown。先无分配地使实例失效，再单次尝试清理全部非锁 owned 资源，最后关闭根锁；旧 owner 终止，后续操作拒绝，再次 Dispose 不重复关闭已尝试对象。仅按各 owner 自己的合同处理其 fault，不 Dispose 借入 data，不把健康 data 一并宣称故障。确定参数/guard 或 pre-I/O 失败 Result 不 teardown；普通查询内容/codec/预算失败继续按既有读合同处理，实际恢复/owned 清理等已要求停用的失败才进入受控 teardown。
异常清理 MUST 先无分配地停用实例，out 不被 finally/Dispose 改写；资源槽先取出/清空再逐一单次尝试关闭，根锁按 `[S-VS-OWNER-LOCK]` 最后关闭，不因前项失败跳过后项。遵循 S2 `[S-FS-OWNED-FAULT]` 的主异常优先、单原异常/多 Aggregate 和汇总分配失败仍完成清理规则。已归还资源不由外层 finally 或再次 Dispose 重关。无新清理错误时保留原异常对象与原栈；多个错误的汇总不充当发布证据，不声称找回底层 finally 已遮蔽的异常。正常成功 Result 不替代应用状态安装；应用安装失败后仍从已发布事实加载。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

调用证据由本条的公开事实边界推进；data 屏障、源 ref flush 与 metadata 准备不提前推进本次业务结果：

| 操作 | 写回 Unknown 的位置 | 写回 Confirmed 的位置 |
| --- | --- | --- |
| PublishRef（含 rewind）/CreateTag | 调用正式发布文件 Append 之前 | 该记录 Append 成功且发布文件 DurableFlush 正常返回后，立即写回 |
| CreateRef/ForkRef（自动或指定） | 私有 ref 文件 flush/close 后，调用正式 file rename 之前 | 同一次 no-overwrite file rename 正常返回后立即写回 |
| data ConfirmDurable / fork 源 ref DurableFlush | 不推进，保持 NotAttempted | 不确认本次发布 |
| tag 桶 header-only 初始化、flush/close/空桶 rename | 不推进本次 tag，保持 NotAttempted | 只建立桶 metadata，不确认首条 tag |

更新/tag 进入公开 Append 调用后的异常 MUST 保守保留 Unknown，即使该异常可能发生于内部准备阶段；不依据异常类型/消息、旧 TailOffset 或私有 flush 推断没有公开输出。公共 RBF `[S-RBF-APPEND-RESULTPATTERN]` 正常返回的失败 Result 则有 pre-I/O 合同：若它证明本次全部公开发布输出均未发生，MUST 在返回失败 Result 前恢复 NotAttempted。这是唯一的已知拒绝例外，不是输出相位猜测；已有公开输出尝试时，后续局部 pre-I/O 拒绝不能给整个调用降档。未来多 ref 扩展消费同一原则，不为此引入通用输出计数框架。
私有 ref 准备阶段失败为 NotAttempted；正式 rename 调用后的异常保守为 Unknown，不以事后 Exists 检查或清理回滚猜该调用是否确认。tag 空桶 rename 结果不明但尚未 Append tag 时仍为本次 tag 的 NotAttempted，实例停用，重开裁决桶 metadata。所有 Confirmed 必须先于可能失败的内存安装、清理与返回值交付；后续异常只能保留该证据。停用后重开 checked-read，不盲目重试、不回滚正式对象。
不承诺崩溃后查询某个旧尝试的 Present/Absent；out 保留此次同步调用返回/抛出时的证据，库不持久化或跨调用自动追踪它。Unknown 后同值 RootMap 不能证明该次调用曾 flush，RefRevision 也不是旧尝试身份。

### spec [R-VS-LOCAL-COMPLETE] 重开接受局部完整发布事实

**正式 ref 接受恢复后初始化完整。** 通过门/data 绑定、正式路径/类型及访问模式/独占准入后，可写访问 MUST 先用 public `RbfFile.OpenExisting` 按 RBF3 单尾合同执行结构恢复，再按 `[F-VS-REF-FRAMES]` 校验实际首两帧，并完整 CRC 读取/验证本次所需 Snapshot。初始化与后续更新统一消费这份恢复合同；MUST NOT 仅因恢复作用于初始 Snapshot、Action 非 None 或旧恢复报告已不存在而拒绝。无需恢复前初始前缀资格或额外 RBF 公共能力。

初始 Snapshot body 已完整而仅缺 RBF 可合法补齐的 Key/Fence 时，CompletedTail 后通过必要身份、kind/meta、CRC 与 RootMap codec 检查即可接受。完整合法的末 Snapshot 作为当前值；后续更新残尾被 Truncated 后，若首两帧仍完整，则检查并使用剩余末 Snapshot。若已无必要 header/初始 Snapshot，MUST 报错，不补造空字典。恢复后的完整坏内容 MUST 报错，不通过丢弃该记录或回退旧值消除错误。RBF 结构恢复可能已经修改文件，随后 VersionStore 才拒绝坏内容；不撤销这次底层恢复，也不承诺所有内容拒绝均保持原字节不变。只读访问使用 public `OpenReadOnlyExisting`，需要修尾时仍拒绝且不修改。

tag 桶继续先执行 [S5-B 专项](09-versionstore-immutable-tags.md)的既定初始化长度前检，再执行允许的 RBF 恢复与目标桶全部记录/名称校验，不把 header 修尾认领为初始化；惰性访问不默认扫描全部桶。本条的 ref 政策不改变 tag 或 FrameStore 的初始化保护。
新 ref 文件发现/private 消费 `[S-VS-REF-FILE-PUBLISH]`；没有正式文件时 private 完整 Snapshot 不建立 ref。fork 来源与初始 Snapshot 随同一文件发布，不从 private 推断或续作旧请求。
完整坏 frame/未知版本/错误身份/非法 codec MUST 报错，不能当残尾、missing 或回退旧值。只读模式不隐式修尾。被引用 data 缺失/损坏在应用实际读取时报错，不据此改写已发布 RootMap。
Open 不默认扫描所有 ref 历史或 data 图；按需打开/校验目标文件，不将成功 Open 宣称为全库审计或全部根图健康证明。所有必要错误传播显式；资源/I/O fault 不被吞成缺对象。
重开后已知 RefId 或当前库本地号用 ReadRef，tag 名用 checked ResolveTag；检查合格的正式 ref 即接受其当前发布事实，无需追溯旧调用结果，也不机械重复 create-only。当前同号/同值不改写某次旧调用的 outcome。指定号码可由应用预先保存，重开 checked 查询实际目标，但不成为尝试 token。自动创建 rename 后交付失败可留下 ref 而调用方无新号；ListRefs 可发现集合，不能按同值精确认领旧调用。动态恢复关联由应用在 Roots 闭包保存业务身份并核对，不增加预交付 RefId 或自动重试。

## 生命周期与故障验收

| 位置 | 证据/状态 | 必要行为 |
| --- | --- | --- |
| 参数、owner 生命周期、历史枚举 mutation guard、编码/容量确定拒绝 | NotAttempted | 无发布写入/额外 barrier，健康实例可继续；无关 data Builder 不构成拒绝 |
| data ConfirmDurable 异常 | NotAttempted | data 按自身 fault，VersionStore 停止，无本次发布追加 |
| fork 的源 ref DurableFlush 异常 | NotAttempted | 源 RBF 按自身 fault，VersionStore 停止，无子输出 |
| 本次唯一公开 Append 正常返回 pre-I/O 失败 Result | NotAttempted | 写回证据后返回确定拒绝；不把这个 Result 当未知输出异常 |
| 本次更新/tag 的发布 Append/flush 异常 | Unknown | 停止；重开 checked-read，不自动 retry/rollback |
| 私有 ref 文件初始化/flush/close 异常 | NotAttempted | 停用；允许 private 残留，不删除正式对象 |
| tag 桶 header-only 初始化、flush/close 或空桶 rename 异常，尚未尝试 tag Append | NotAttempted（本次 tag） | 停止；重开检查桶 metadata，不删除已正式发布的合法空桶 |
| ref 文件 rename 结果不确定 | Unknown | 检查实际正式对象，不以 private flush 确认或回滚文件 |
| 发布确认后内存安装/释放异常 | Confirmed | 停止、重开恢复；不撤销发布事实 |
| 正常返回后应用安装失败 | 已 Confirmed | 应用放弃旧 materialized state，从实际 RootMap 加载 |

## 实施片、Ready 工程定稿与出口

1. S4-A：项目、RootMap/RefId/RefRevision/结果值、格式门/header/codec 与 owned 生命周期。
2. S4-B：自动/预留区指定创建、private 初始化与单文件发布、RefId/local 读取、PublishRef/ListRefs 与 data-first；先做固定槽位与动态 ref 的 public 纵向例子。
3. S4-C：末帧成员检查、CRC/未知版本、容量 guard；实施已定 Result/out 证据、公开 Append/flush/rename 边界、故障清理、两种内存安装失败与冷重开。
4. S4-D：进程终止 failpoint、多根同时更新、空字典、同值新 revision、循环数据闭包与旧快照重新发布。真实文件实验使用 W:。

| Ready 项 | 需定稿或实施验证 |
| --- | --- |
| S4-Q1 | RefId 上下文/LocalId 导出与 checked 本地查询、RefRevision、冻结 RootMap/RefSnapshot、ListRefs 路径身份/自有全集合已定；实施值/集合与查询向量，不将 ListRefs 等同内容健康审计 |
| S4-Q2 | 格式门内容/只读绑定、RefId内部4B、RootMap BPV1/变长地址/1MiB及4B最小entry与3B地址suffix、ref header24/36与Header/Snapshot的0/1 kind、payload schema/专项meta8与长度判形/完整宿主容量、共同首两帧检查及恢复后完整性准入已定；实施独立向量、按实际消费含冗余地址计预算，并验证允许的初始/后续残尾恢复和必要内容拒绝 |
| S4-Q3 | 初次空库/直接44B门、随机非零VSID、根/组件/no-follow/同文件系统、独立永久锁/模式、唯一pending取消与完整停用teardown已定，D3不再阻塞设计；S2公共EnsureUsable及VS窄平台复用待实施。验证fresh竞争/门失败事实、锁矩阵/最后清理、W发现H先于取消/RO保留、未知项/取消失败、file rename及真实冷开/两平台资格，不重开私有目标/前缀证明机制 |
| S4-Q4 | Result/必选 out PublicationOutcome、入口初始化、公开尝试/确认写回、pre-I/O Result 例外、异常清理及自动/指定创建续跑边界已定；实施拒绝/部分与完整输出/确认后安装故障及冷重开向量，不再开放证据载体设计 |
| S4-Q5 | checked 末读取实现、资源预算、错误分类、已完成输出屏障对接及有无关活跃 Builder 的 public 发布轨迹 |
| S4-Q6 | 新/旧/orphan 地址的应用闭包责任示例与冷重开资格；不增加图遍历来源证明 |

验收覆盖错误 DataStoreId 在写入/恢复前拒绝、空/重复/超限 key、多根原子更新、旧/新帧混合、Builder 未完成/取消地址的应用合同、末帧/tombstone/TerminationError、完整坏 CRC 不回退、create-only 冲突、正式创建前后进程终止、合法末帧末端越过 MaxOffset / 下一次追加确定拒绝，以及借用 data 不被 Dispose。裸地址原始来源错误是应用合同向量，不宣称能由数值格式检查自动检测。源码与包/平台 qualification 分开，阶段仍 Draft。
借入准入验收覆盖三个工厂的 disposed/faulted data，以及两个可写入口的只读 data 拒绝：即使其纯属性可读也不签发 owner，VS 根/锁/门/发布文件无本次 I/O 或改动。OpenReadOnly 分别借入只读和可写 data；合法活跃 Builder 不阻止入口，借入检查不确认输出或改变 Builder。错绑定或 VS 初始化失败不关闭/停用健康 data；关闭 VS 后 data 仍可使用，先失效 data 后 VS 清理仍归还自身资源。持续串行/不提前 Dispose 是调用方前提，不以故意违约后的纯查询拒绝作为验收保证。
格式门独立向量：version=1、VSID bytes=`01..10`、DataID bytes=`11..20`（十六进制）、L=256；L 为 `00 01 00 00`，44B 记录末 4B 为 `9E 31 2A 6A`（CRC32C=`0x6a2a319e`）。不用同一 encoder round-trip 替代。覆盖全部短前缀/尾随、身份/L/CRC 损坏、重新 seal 的未知版本/零身份/L=Max、错借入 data；所有拒绝/close 失败先于恢复/private 清理/输出。两角色身份可同值；两模式不改门，重开不覆盖 L，不按 header 补门；Create 非法 L 先于 I/O 拒绝，0/Max−1 合法，初次发布/模式/平台另取证。
RootMap 独立 empty=`00`，一项 A 使用 FileId=1、ticket offset=4/length=28 的完整 codeword=`01 03 41 01 87 02`；地址复用 S2 独立变长向量，不由相同 encoder round-trip 代替 golden。覆盖多个不同长度地址、完整高位/15B 上界、非法/截短地址、全部 code-unit key、另一种合法 string 编码、count/string 及地址合法冗余表示、不同表示的重复 key、overflow/巨大声明在分配前拒绝、每个短前缀、实际 1MiB 等于/超过及冗余地址 byte 使超限、4B 最小 entry 与 `3 + 4 × 后续项数` suffix guard、组合宿主后字段与 trailing。确定输入超限先于 barrier/output，完整坏末 map 不回退；cold history/tag 复用同一格式。基元探针及原 fixed12 synthetic 地址证据不替代本次真实 public 变长地址、完整 RBF/宿主、新库或峰值内存验收。
自有字典/结果向量从真实 public 地址构建输入：修改原字典不影响成功值；输入 comparer 不改变 Ordinal 语义，A/a 分别保留、不同插入顺序不影响来源内容资格。覆盖 null 字典/key、default 地址、实际枚举重复/超限、输入 Count 不可信及枚举/清理/冻结失败；确定拒绝或纯捕获失败先于 barrier/输出且 NotAttempted、无新增 fault。返回 map/Keys/Values 的常规接口不能改变保留初始字典；历史结束/后续失败/owner 关闭不撤销已保存项，旧项可交给合格的同 VS 重开 owner 作 tag/rewind。三操作成功都交付非 null RefSnapshot，新 RefId 从 Revision 取得，不以对象引用或字典内容判断同 revision。构造不重新校验/读回，确认后结果分配/安装失败仍 Confirmed；BCL 探针仅提供实现选择证据，不代替这些新库或宿主编码/容量验收。
RefId 内部字段使用独立 local=0x89ABCDEF → `EF CD AB 89` 向量；覆盖 1、高位及全 FF 合法、零与 0/3/5B 拒绝、失败 default/不提交消费、encoder 短目标/错上下文/默认值失败无写入及长目标只改前 4B。公开等值/hash 覆盖同 VS 同 local、不同 VS 相同 local、同 VS 不同 local 与 default；旧自有值在同 VS 重开后可用，关 owner 不撤销值。两 VS 共借 data 且 local 相同，错误值在 ReadRef/PublishRef/History/Fork 的 I/O/barrier 前确定拒绝、不新增 fault，mutation 保持 NotAttempted。checked 来源元数据可产生 bound 值但不证明源存在；坏宿主字段不外泄值，无来源使用 `[F-VS-REF-FRAMES]` 的 24B 形状，不借零值代替来源判别。格式、public 类型与真实平台/发号资格分开验收，不拿现有 Binary 测试或候选算术称为新库通过。
RefRevision 等值的内部独立向量覆盖完整 VSID/local/ticket、同 offset 不同 length、同 ref 同值重发及同值子初始/源不同 revision；default 的比较/hash/两属性合法，fork 准入拒绝 default/不同 VSID 先于 I/O/barrier 且 NotAttempted、不新增 fault。public 消费从正常发布/checked 当前与历史取得值，保存后结束查询/关闭并重开同 VS，再 fork 仍按现有协议重读与确认；继承快照的 RefId/Ticket 必须为实际源位置，能关联 ListForks 的 exact 来源边。缺源/坏内容/错成员不因持有旧值免检；ForkOrigin 元数据和提前 ticket 不签发 revision。确认后构造只保存值，后续交付异常保留 Confirmed；不以投影/default 或旧栈测试称为公开 codec、防伪、匿名跨进程书签或新库已实施。
文件发布向量覆盖 pending header/Snapshot/flush/close、file rename 前后、正式缺初始/坏身份、只读排除且不清理 pending、重复 ID 不覆盖。固定槽/自动 ref 使用同一工作文件/格式与发布点；fork 不增加第二文件或目录发布依赖。
建库/打开向量覆盖缺失末级根/缺父目录、空根/仅合法空锁、未知内容或初始化布局拒绝、获锁后fresh重检；必要目录布局先于44B门输出、全部短门/坏门/缺布局重复拒绝、完整门已成立后的flush/close/交付失败可按实际Open，拒绝时不删除门/锁/root回滚。检查VS/data跨文件系统允许而VS受管项同FS、严格writer/reader矩阵/缺坏锁/进程终止释放、锁文件永不删除或替换、一次完整teardown锁最后且重复Dispose不重关，borrowed data不被Dispose。FrameStore模板与原平台结果不替代这些VS验证。
工作区向量覆盖空creating及唯一pending的0B/任意中断字节/完整内容；cold不读取payload，不以Length/身份/角色/H或正式target作为取消条件。两域ref/fork及tag空桶共用fixed pending；可写取消后重新正常规划，不续旧请求；RO保留且不为pending发现H。隐藏未知项/大小写别名/多项/特殊类型/跨FS/枚举及必要清理失败保留拒绝，删除失败不交owner；W正式发现错误先于删除，rename后正式ref/空tag桶保留。此工作区策略不改FS前缀或正式tag初始化保护。
ListRefs向量覆盖空集合、两编号域/高位/Max、自有无顺序全集合、pending排除、canonical但坏header/Snapshot仍占号且列出，随后ReadRef明确报错；未知名/类型/枚举/必要清理/结果分配失败不交部分成功。不打开内容/不修尾/无data或private副作用，保存结果在后续查询/owner关闭后仍有效；自动rename已成立但返回值丢失可被发现，不按同值认领旧尝试，列表值不签发RefRevision。
路径向量消费 `[F-VS-REF-PATHS]`，覆盖零/高位/Max、大小写/宽度/Unicode/空白尾点/错后缀/层级、直接项类型/枚举失败。正式 refs/00000008.rbf 占 8，header 自称 9 报错而不改号；精确定位无 ID→path 表。codec 不替代根/组件/no-follow/private/rename/规模资格。
编号向量消费 `[S-VS-REF-ID-DOMAINS]`：L=0/256/Max−1、首自动 L+1、指定 0/越界/已占拒绝、低号不推进 H、完整发现/高位/Max、坏内容占号/private 不计 H；自动耗尽仍可建空闲预留槽。两种创建/fork 都验证确定拒绝先于 barrier/输出、Unknown 及确认后交付失败冷开、不复用来源。LocalId 保存后同库重开 ReadRef(local)取得 checked 值；local 可查自动区而不隐式创建，本地 selector 选库责任与完整 RefId 跨 VSID 防错分别验证。
证据向量覆盖 mutation 最早重置 out、正常成功/失败、助手异常直接写回、Append pre-I/O Result 恢复 NotAttempted 与 OOM/输出/flush 保持 Unknown；data/源 flush/tag 空桶维护为 NotAttempted 但停用。file rename 后立即 Confirmed，再注入结果/投影/清理异常，不降档或 Dispose 借入 data。自动丢失返回值后 ListRefs 可见但同值不能认领；指定重开可查号码，已占不证明旧尝试 Confirmed。真实进程终止无 out 的恢复与同步证据分别取证。
header 独立golden：version=1、VSID=`01..10`、自身local=`0x89ABCDEF`的无来源payload为 `01 00 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 EF CD AB 89`；有来源追加source local=`0x76543210`与SizedPtr(offset60,length40)，suffix=`10 32 54 76 0A 00 00 3C 00 00 00 00`。覆盖24/36之外长度、完整帧中的截短/尾随、零/错身份、未知版、Source=self及非法ticket；36B的前24B若人为另封为合法无来源帧属于另一份记录，不把未framed的切片当完整截短检验。
宿主向量覆盖Rbf3/首两帧真实成员、header meta0/Snapshot meta8、tombstone/重复header/未知kind与必要记录未知meta形状、Snapshot exact-consume与1MiB尺寸前检、初始即末复用及两种header物理后界。普通 ReadRef不扫描源历史；来源范围不能签发revision。旧探针112→104→112→再次None现象保留原格式身份；当前4B LocalRefId/变长RootMap/meta8验证长初始body仅缺Key/Fence时可写补齐并接受、再次None仍接受，以及已完整初始化后的后续残尾补齐/截短。初始body未完成被截掉后拒绝缺帧；补尾后发现坏CRC/身份/codec仍拒绝，允许已发生的底层恢复保留；只读残尾拒绝且不改字节。创建另验证private flush/close→rename的中断窗口，不将人为截短正式文件称作正常创建轨迹；不以旧探针替代新库验收。显式来源遍历缺/坏时报错且不改本地head。
元信息向量直接消费专项的独立 golden、必要记录精确长度/完整CRC、受控时钟重复/回退及自有时间生命周期；时间不参与 revision 等值或来源 Roots 比较。旧 meta0 草案无兼容；新的最小初始化长度只描述格式尺寸，仍须执行恢复后完整内容检查。
CreateRef/PublishRef/CreateTag 各覆盖 A 依赖全部完成而无关 B 仍 Building 时成功发布、B 所租文件中的旧 dirty 依赖在根写出前被确认、B 后续完成必须重新确认，以及任一 data flush 失败后不尝试根输出并停用 data 的全部 Builder/Writer。根真正依赖未完成 B 的负例属于应用闭包责任，不能把“库自动识别并拒绝任意未完成图”写成单测承诺。

