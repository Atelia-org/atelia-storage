# S4：VersionStore 完整根字典与单文件 ref 发布

状态：**Draft；2026-10-07 采用完整 RootMap、每 ref 一个 RBF3 文件与统一 RefId 目录发布，允许独立完成闭包发布；2026-10-08 同步 S2 固定 12B 地址；2026-10-09 增加不可变 ForkOrigin 首帧链接，定稿同步发布证据与续跑边界、格式门记录/只读校验及 data 身份绑定、RefId 公开上下文值与内部字段编码、RefRevision 自有位置值、RootMap/RefSnapshot 自有表示；其余 API、codec、路径编码与平台协议尚未实施/冻结，项目尚未创建**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)。名称与历史见 [S5](05-versionstore-names-and-indexes.md)。

## 目标、归属与范围

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份。借入一个 data FrameStore，拥有独立发布目录、其中的 RBF3 帧文件及其资源；格式门是独立普通控制文件。直接使用主线 FrameStore/Rbf，不引用冻结旧栈或业务库。
发布目录格式门持久绑定格式版本、VersionStoreId、DataStoreId。Open 在任何发布文件恢复/写入前核对借入 data 的身份与访问模式；发布位置不解释为 data FrameAddress，data 地址也不解释为 ref revision。
单 owner/driver 串行操作，包括借入 data 的相关操作；发布目录须排斥另一 writer。VersionStore Dispose 释放 owned 文件、当前遍历资源和锁，不 Dispose 借入 data。data 在使用期间必须存活；首版可写 VersionStore 借入可写 data owner，这是模式准入，不表示每个 mutation 都调用 data 屏障。资源、只读模式配对、借用与 fault 的具体公开接口在 Ready 时定稿。

首版不创建独立 Commit 对象，不为每条 Snapshot 保存 Parent，不采用 Prepared handle、nonce 账本、默认 CAS 或精确 InspectPublication。ref 的创建来源由首帧 ForkOrigin 表达，文件内发布顺序仍由实际帧链表达。不提供跨 ref 原子事务；业务谱系、随机数状态、tool-loop 阶段、operationId 和外部副作用协议由应用保存和解释。

## VersionStore 格式门与 data 绑定

### spec [F-VS-OWN-FORMAT] 固定记录绑定发布库与借入 data

VersionStore MUST 有独立、create-only 的格式门；普通 ref/tag/name 发布不改变它。正式相对名称为精确小写 `versionstore.format`，是恰为 40 bytes 的普通文件，不是 RBF 文件：

| byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | VersionStoreFormatVersion | uint32 LittleEndian，首版为 1，统一选择本层目录与记录 schema |
| `[4,20)` | VersionStoreId | 16 个 canonical opaque bytes，非全零，发布库的稳定身份 |
| `[20,36)` | DataStoreId | 16 个 canonical opaque bytes，逐字节复制借入 data 已资格化的持久 StoreId 编码 |
| `[36,40)` | CRC32C | 前 36B 的 CRC32C，uint32 LittleEndian；init/finalXor 均为 0xffffffff |

两份身份按各自角色逐字节比较，不检查 UUID 结构、不重排字节或 dump CLR struct，不要求两者字节不同。DataStoreId 直接消费 S2 身份编码；VersionStoreId 的 16B 是首版工程选择，公开 CLR 表示与生成过程仍待定。FrameStore 与 VersionStore 的格式版本分别解释各自 schema，绑定不要求两层版本号相等。
CRC MUST 使用现成 `RollingCrc.SealCodewordForward` / `CheckCodewordForward`，传入完整 40B codeword；这是普通门记录的唯一 CRC，不加 Magic、GateSchemaVersion、flags/预留、RefId、路径、集合、配置或追加状态。RBF 发布文件的 header 仍由 RBF 完整 CRC 保护，不再附加这份门 CRC。

**共同只读检查。** Open 与 OpenReadOnly MUST 共用一个内部流程。在 S4-Q3 根/实际名称/普通文件/no-follow 及借入 owner 准入资格下，取得实际借入、此时可用的 data owner 的持久 StoreId；按所需模式检查，不能以另读 data pathname 的门替代此 owner。以 `FileMode.Open`、`FileAccess.Read`、`FileShare.Read` 打开正式门的临时 FileStream；同一句柄上先要求 Length 恰为 40，再 `ReadExactly` 到固定 40B 栈 buffer。先校验整个 codeword，再解码受支持版本与两份非零身份，最后比较 DataStoreId 与借入 data 的身份。不同即拒绝，不按门字段重新寻找或替换 data、不比较 data 路径或实例引用来替代持久身份。
版本/身份只暂存在局部自有值中；绑定合格且临时句柄关闭成功后，才交给后续 owner 初始化。关闭前取出/清空资源槽，复用 S2 `[S-FS-OWNED-FAULT]` 的主错误优先/单次清理规则；不外泄 buffer、stream 或公开门 API。任一检查、绑定或必要清理失败 MUST 先于本次 VersionStore 打开中的发布文件恢复、私有清理与输出结束，不签发 owner。截短、尾随、CRC/字段坏、未知版本、缺门与身份不匹配明确拒绝；权限/I/O 保留原错误，不通过 File.Exists 猜缺失。本流程不调用 data barrier、恢复或写入 data，不因本层门失败而 fault/Dispose 健康的借入 data；调用方在借入前独立打开 data 所做的合法恢复不受此顺序约束。

正式门 MUST NOT 修尾、追加、替换或从 ref/tag/data header 补造身份；缺坏时不采用私有候选、其他文件或自动创建。身份相符只证明所借 data 与门绑定一致，不证明裸地址原始来源、完成/耐久或业务闭包，也不认证外部改写。门内容资格不关闭初次 store 创建/门发布、根/锁、身份生成、私有残留及平台协议，这些仍在 S4-Q3 定稿或取证；模式配对及公开身份接口也保留原待定范围。
（Informative）一个 VersionStore 借入一个 data 不推出每份 data 只能有一个 VersionStore；独立 VersionStoreId 继续区分其 header/revision 上下文，不建立反向持久登记。单帧 RBF 承载同一字段也可正确，但仍需字段 schema 和 profile/首帧形状/完整 CRC/唯一后界检查。门没有追加、历史或 ticket 消费者，首版选择定长读取与现成 codeword，不建立通用 Gate 框架或双载体 fallback；没有实测性能优劣。依据为 [公开 CRC codeword](../../src/Data/Hashing/RollingCrc.cs)及[独立 CRC/损坏测试](../../tests/Data.Tests/Hashing/RollingCrcCodewordTests.cs)，不是 VersionStore 已实施的证据。

## term `Root-Map` 应用命名的根地址字典

`RootMap = string => FrameAddress` 的完整值快照。多个根在同一发布帧内一起改变；地址指向同一个绑定的 data FrameStore。key 及其含义属于应用，VersionStore 不识别 State、树或图。

### spec [S-VS-ROOTS-OPAQUE] 完整根字典由应用解释

RootMap MUST 使用唯一的 Ordinal key，不做隐式大小写折叠或 Unicode 归一化。空字典合法，且不同于 missing ref/tag。发布 MUST 先私有复制输入并编码为完整快照，避免调用方后续修改输入改变本次请求或返回值。
库校验字典、key 和地址的格式/容量，并核对 VersionStore/data 格式门的身份及实例生命周期。FrameAddress 继承 S2 的局部地址合同：裸地址不携带原始 StoreId，库不能识别来自另一 store 的恰巧合法坐标。应用 MUST 保证所有引用属于该 data，所需新增依赖已完成，闭包不含 provisional/canceled 地址。数字地址本身不能证明完成来源；库 MUST NOT 把合法坐标或 data barrier 当成整个业务图已成立的证明。读取物理完整 orphan 后采用它，仍需由应用重新建立闭包资格。
读取返回自有 RootMap 值；不持有 RBF pooled frame、Span 或枚举器临时缓冲。地址背后的内容由应用按 FrameStore.ReadFrame 完整读取与解释，VersionStore 不预读/遍历业务图。
ReadRef 的成功值称为 RefSnapshot，提供只读 Revision 与 Roots；由库内部构造，不持有 owner/reader、不需要 Dispose，字典无公开修改入口。S5 历史读取复用同一自有结果，不另立历史快照或结果租借。RefRevision 消费 `[A-VS-REF-REVISION-VALUE]`；RootMap/RefSnapshot 表示消费下面的自有值合同，RootMap codec 仍由 S4-Q2 定稿。

### spec [A-VS-ROOTS-OWNED] 公开复用集合接口，内部冻结完整内容

首版 RootMap MUST 仅作为逻辑名称，公开输入与输出使用 `IReadOnlyDictionary<string, FrameAddress>`，不新增 public RootMap、集合 Builder 或比较器类型。非 null 空字典合法；null 字典、null key、实际重复的 Ordinal key 和 default FrameAddress 为确定输入拒绝，其余 key 字符/长度、条目与记录限额及 wire 顺序仍由 S4-Q2 定稿。接口自身不证明所有权、不可变性或比较政策，不能直接保存调用方集合或只读包装。

外部输入 MUST 逐项捕获到私有 `Dictionary<string, FrameAddress>(StringComparer.Ordinal)`：检查实际枚举的 key/地址与既定容量，通过 TryAdd 拒重，不继承输入 comparer，不以输入 Count 或 TryGetValue 决定正确性或分配规模；随后以显式 `StringComparer.Ordinal` 形成自有 `FrozenDictionary<string, FrameAddress>` 并编码。输入枚举器的必要清理、捕获、冻结、编码与确定检查 MUST 在本次 data barrier/发布输出前完成；纯输入捕获或分配失败保持 NotAttempted，不新增 owner fault。不得直接用 ToFrozenDictionary 代替拒重步骤，其重复键行为是覆盖而非拒绝。磁盘解码同样先按 codec 拒绝重复/非法条目，最后交付自有冻结内容，不保存 pooled bytes 或复用下一帧的可变 scratch 字典。

调用方 MUST 在同步输入捕获期间保持集合稳定，输入接口访问及枚举器清理不得重入本 VersionStore 或借入 data（包括 Dispose）。自定义输入仍可能执行调用方代码；这是既有 single-driver/no-interleaving 的使用前提，不新增库诊断违约枚举器的保证、OperationActive 或跨 API busy 框架。S5 的 HistoryActive 对获准历史 visitor 的动态限制继续照原合同执行，不能代替输入使用前提。

返回的 Roots MUST 持有上述自有冻结内容，通过正常集合接口不能改写；map、Keys、Values 不得暴露可变 backing 集合。string 与 FrameAddress 自身不可变，浅捕获足够；不复制或预读地址背后的业务对象。内部已捕获/解码的冻结字典可共享给结果与初始字典资格检查，交付不再复制内容，合法空字典也可共享。返回值在查询结束/提前停止/后续失败及 owner fault/Dispose 后仍有效，不需要结果 Dispose。

RefSnapshot MUST 为非 record 的 `public sealed class`，仅由库内部构造，公开 `RefRevision Revision { get; }` 与 `IReadOnlyDictionary<string, FrameAddress> Roots { get; }`，无 setter/init、owner/reader、结果租借或 Dispose。库成功交付的对象及 Roots 均非 null；default(RefSnapshot) 是 null，不作为 missing 或空快照。普通对象相等保持引用语义，不提供 IEquatable、结构 hash 或内容相等运算；重复读取的对象是否复用不作承诺。CreateRef、ReadRef、PublishRef 的成功值统一为 RefSnapshot（返回 `AteliaResult<RefSnapshot>`），新 RefId 由 `snapshot.Revision.RefId` 取得，不另设重复 pair/创建 DTO；S5 其余操作的返回表示仍由各入口定稿。

库用于 ForkOrigin 字典一致性资格的内容比较 MUST 针对已经资格化的 Ordinal 自有字典执行：Count 相同，逐 key 查找并比较完整 FrameAddress 等值。不得用枚举顺序、Roots.Equals、Snapshot.Equals、编码 bytes、CRC 或 hash 代替；不因此合并同值 revision。首版只保留内部比较逻辑，不新增公开 ContentEquals 或结构 hash API，出现明确应用消费者再评估；应用仍可读取字典并自行比较。Snapshot 构造只保存已资格化 revision 和冻结引用，不再次验证、复制、编码或读回；正常发布仍先写 Confirmed 再交付。class 分配或后续安装可能失败，确认后异常依既有证据合同保留 Confirmed，不为免除一次分配引入可变占位壳或提前公开 revision。

（Informative）[OwnedRootMapProbe](../../experiments/OwnedRootMapProbe/README.md)在 .NET 10.0.5/Windows 上验证 0/1/4/100 项：ReadOnlyDictionary 的 map/Keys/Values 均可通过 ICollection.SyncRoot 取出真正 Dictionary 并改变可见内容；FrozenDictionary 在探针列出的普通集合入口无该路径，直接冻结会覆盖重复 key。首版选择标准 BCL 冻结集合以减少自写封装，不声称最少分配或性能最优；捕获与冻结有临时内存/构建成本，仅实现只读接口且安全投影 Keys/Values 的私有封装或其他不可变集合也可正确。class/struct 都可满足自有结果，普通 class 避免另一套非法 default/生成等值合同，是工程取舍。不可变保证不认证反射、Unsafe 或违反 immutable marshal 所有权约定的改写；探针不构成 RootMap wire、新库发布/恢复或包消费资格。

## RefId 值与内部字段编码

### spec [F-VS-REF-ID-8B] 公开身份绑定上下文，持久字段使用固定 8B

首版 RefId MUST 为不透明、自有的非 positional `public readonly struct RefId : IEquatable<RefId>`，提供 `Equals(RefId)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`。逻辑值为 `(VersionStoreId, LocalRefId)`：前者直接保存 `[F-VS-OWN-FORMAT]` 的 16B canonical 持久身份，后者为非零完整 ulong。等值 MUST 比较两者全部 bits，相等值具有相同 hash；比较与 hash 是纯值操作，不读取 owner 或磁盘，hash 不持久化或保证跨进程稳定。同一 VS 的相同 local 值跨 owner 重开仍相等；不同 VSID 下的相同 local 值不相等，不因共借同一 data 而合并。
普通内部表示使用三个私有 ulong 保存上述字段，不转储 CLR 内存、不规定 Pack/Size、sizeof 或数组步长。类型不持有 owner 引用、epoch、buffer 或需要 Dispose 的资源，不提供公开数值构造/投影、Deconstruct、IsValid、Guid 转换、算术、排序或规范文本。default 是受支持公开构造路径中的唯一非法值；自等、比较及 hash 均合法，但不能访问或编码，不作为可选身份的持久 sentinel。无来源的判别仍由 header schema 定义。

接受 RefId 的入口 MUST 在既有 owner disposed/fault 检查之后、任何目标发现 I/O 或 data barrier 之前，拒绝 default 及与本 owner 持久 VSID 不符的值。它们是确定参数拒绝，不新增 fault；mutation 仍先初始化 outcome=NotAttempted。MUST 比较持久身份，不以 owner 引用代替；先前取得的自有值可以交给同一 VS 的重开 owner。此检查仅拒绝不同 VSID 的误传，不证明目标存在、物理根唯一、身份生成无冲突或记录完成。

**唯一内部字段 codec。** header、自身/来源身份、branch binding 与后续 CU 成员表中的 RefId 字段 MUST 恰为 8B，按完整 LocalRefId 的 uint64 LittleEndian 编码；不在每个字段重复 VSID，不追加版本、CRC、flags 或预留。所有非零 64bit 位模式均数值合法，包括 1、高位及 ulong.MaxValue；它不是 SizedPtr、FrameAddress、时间或分配顺序，不检查票据对齐/坐标或 UUID 位模式。
内部 decoder MUST 要求字段输入恰为 8B，失败输出 default；只在非零检查通过后，结合宿主的预期 VSID 构造值。普通打开/读取的预期身份来自已 checked 的门上下文，记录内的身份须与之比较，不能从被检查记录反向认领期望身份；创建阶段可使用私有初始化已预定的非零 VSID，不要求初次 Create 先读取已发布门。磁盘字段与整个宿主的必要身份/CRC/codec 校验通过后，才向调用方交付值。
内部 encoder MUST 先检查值非 default、VSID 与宿主预期身份相符及目标容量至少 8B；失败不改变任何目标 bytes，成功只写前 8B，剩余目标不变。复合 reader 先保证剩余至少 8B、切出精确字段，成功才提交消费；截短/零字段失败不提交，宿主负责全部记录的剩余字段及 trailing bytes。两方向不执行 I/O、不新增堆 buffer 或专用错误族，宿主映射为自己的字段错误；可信私有构造只保存已资格化身份值，不在发布确认后的成功值交付中重新解码、读回自身文件或引入可拒绝校验。

RefId 可以来自正常创建结果、checked 对象/名称读取或 checked 来源元数据；绑定上下文并不赋予被声明源的存在、主链或发布资格。私有 planned RefId 不在正式发布前交付应用，也不签发 RefRevision；成员、发现与发布证据仍消费既有条款。RefRevision 直接复用 RefId 已携带的上下文，其值合同见 `[A-VS-REF-REVISION-VALUE]`。
首版不提供公开 EncodedSize、bytes/text codec 或自动序列化适配。因此不承诺把匿名 RefId 独立保存到应用外部记录再跨进程直接导入；名称/业务标记定位不是该能力的透明替代。出现这一明确消费者时，再定义带完整上下文的窄导入/导出合同，不能把内部 8B 字段当作无上下文的公开身份编码。
（Informative）8B 是当前 VS 内稳定身份域的工程选择；4B/16B 也可能正确。它确实排除了原样 128bit local 发号，后续唯一分配/不复用/耗尽必须在此域内由 S4-Q3 成立，不以随机碰撞概率或字段宽度代替证明。公开值另携已有 VSID 是本轮工程防错选择：A/B 各有 local=7 时，A 的值误传 B.PublishRef 会被拒绝，而纯 local 值加正常 B header 校验会更新 B:7；局部 selector 加调用方选库责任也是可行备选。多 16 个逻辑身份 bytes 与一次上下文比较的成本未测量，24B 不成为 CLR ABI；不新增身份、通用 ID 框架或 public StoreId 类型。固定 LE 可直接用 BCL 基元，[已有 UInt64LE](../../src/Binary/BareValueWriter.cs)与[独立 golden](../../tests/Binary.Tests/ScalarTests.cs)仅佐证字段组合，不强加 Binary 依赖，也不构成新 RefId 实施资格。

## term `Ref-Revision` 已接受快照的位置身份

RefId 由 `[F-VS-REF-ID-8B]` 定义，是正式 ref 目录及其唯一 RBF3 文件的稳定身份，与 branch name 分离。RefRevision 是由库签发的自有位置值，绑定该 RefId（含 VersionStoreId）和该文件中实际完整快照位置及长度；不可与 data 地址互换。
revision 只从完成发布或实际 checked-read 且已确认真实主链成员的当前/历史快照取得，不在输出前签发。不同完整物理快照即使 RootMap 同值也具有不同 revision。它不是某次未完成调用的尝试 token；首版不提供凭裸位置随机 ReadRevision，也不以字典相同证明某次调用成功 flush。S5 的按 revision fork 是内部重读与校验来源的窄创建入口，不开放任意位置读取。

### spec [A-VS-REF-REVISION-VALUE] 自有 revision 复用身份与完整 ticket

首版 MUST 使用非 positional `public readonly struct RefRevision : IEquatable<RefRevision>`，普通内部表示仅保存私有 RefId 与 SizedPtr，不另存 VSID、DataStoreId、owner、epoch、Reader、RootMap 或耐久标志。提供 `Equals(RefRevision)`、`Equals(object?)`、`GetHashCode()`、`==` 与 `!=`；等值 MUST 比较 RefId 的完整上下文及 ticket 的完整 Packed bits（含 length），相等值具有相同 hash。值操作不执行 I/O，不依赖 owner 健康/存活；hash 不持久化或保证跨进程稳定，不规定 CLR Pack/Size、sizeof 或数组步长。

公开只读属性为 `RefId RefId { get; }` 与 `SizedPtr Ticket { get; }`，返回快照实际所属 ref 与实际 ticket；继承历史的源快照不改绑到发起查询的子 ref。两者均为纯值投影，不触发读取或签发另一个 revision。default 是受支持公开构造路径中的唯一非法值，自等/比较/hash 和属性读取均合法，两属性分别返回 default；所有接受 RefRevision 的入口 MUST 在既有 owner 生命周期/模式 guard 后、任何目标发现 I/O 或 data barrier 前，拒绝 default 及不同持久 VSID 的值。确定参数拒绝不新增 fault，mutation 仍先初始化 outcome=NotAttempted。

正常发布结果只在所需 flush/发现边界确认并先写回 Confirmed 后对外签发；当前/历史读取只在本次必要 header、真实成员及完整 CRC/kind/codec 资格成立后交付。可信内部构造仅保存已资格化 RefId/ticket，不在确认后的值构造中再验证、分配、扫描或读回自身文件。结构扫描的 RbfFrameInfo、ForkOrigin 声明、planned ticket、私有未发布快照均不能单独签发 RefRevision；值本身不持有 RbfFrameInfo/reader。

已交付值在历史调用结束/提前停止/后续失败及 owner fault/Dispose 后仍可比较和保存；同一 VS 的重开 owner 可接受它，不按实例引用或 epoch 拒绝。实际 fork 仍消费 S5 `[S-VS-FORK-FROM-REVISION]` 的正式源定位、exact offset/length 主链成员与完整内容重读，再执行本次 data/源确认；持有旧值不免除此协议，不保证源仍存在/内容健康、当前 head 相同、本次耐久或旧调用曾 Confirmed。此复检是现有统一 fork 流程，不增另一份成员见证或构造时扫描；持久身份也不证明物理根唯一或认证同 VSID 拷贝。

不提供公开位置构造/转换、setter/init、Deconstruct、IsValid、算术/排序、规范文本或整个 RefRevision 的公开/持久 codec。ForkOrigin 仍组合宿主上下文、8B RefId 字段与 8B Packed LE ticket；不能从这些元数据直接构造 checked revision，也不新增完整 revision 记录、版本或预留字段。暂不支持匿名精确 revision 独立外存后跨进程直接导入；tag 仅保存 RootMap，不能透明替代其精确 ref 历史来源。出现明确消费者时再立带完整上下文的窄导入/导出合同。

（Informative）只读投影是使现有历史与 ListForks 来源边直接关联的工程选择，不是用户另行规定的 API；全私有值也能正确完成选点后 fork，声明边本身也能按 ticket 分组。A1/A2 同字典而分别分出 B/C 时，只比较 RefId 或 RootMap 不能识别选中点的孩子，两属性允许直接比较已有 source 字段，无专用 MatchesLocation API。它们只观察首版单文件位置，未来分段需重审定位/观察合同，不承诺此 tuple 永远足够。无公开构造是窄签发表面，不是不可伪造安全机制；公开构造加严格 fork 校验也可正确，但当前没有裸位置请求消费者。旧 [CheckpointAddress](../../../durable-graph/src/DurableGraph.Persistence/CheckpointAddress.cs)及[重开失效测试](../../../durable-graph/tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs)是实例绑定的另一模型，不成为本栈兼容义务；[结构扫描仍返回坏 payload 的测试](../../tests/Rbf.Tests/Internal/RbfScanReverseTests.cs)佐证内容资格不能只由 framing 代替，不构成新值类型或 VersionStore 实施资格。

## 单文件存储与最小 codec

### spec [S-VS-REF-FILE-SINGLE] 每个 ref 保存一个完整快照序列

每个 ref MUST 使用按唯一 RefId 命名的一个 RBF3 文件。首版不分段、轮转、差分或建立派生 checkpoint；每次更新追加一条完整 RootMap Snapshot。当前值校验本地 header、初始 Snapshot 及紧贴 EOF 的末 Snapshot，初始即末帧时复用读取；不扫描全部历史或递归祖先。
选尾 MUST 使用真实 RBF 逆向主链和 `showTombstone: true`，检查末帧紧贴实际尾部，不能将过滤结果或随机 ticket 当成末快照成员证明。未知 kind/version、tombstone、非 Snapshot 尾帧、完整坏 CRC/codec 皆报错，不向前寻找旧快照替代。首帧为本层 header，第二帧为初始 Snapshot；两者都必须存在且 checked。ReadRef 只校验本地必要内容，不沿 ForkOrigin 递归读取祖先；声明的源历史缺/坏在实际历史访问中报错，不据此回退本 ref 的当前字典。
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
| 根格式门 | `[F-VS-OWN-FORMAT]` 的唯一 40B 记录 | 内容/只读检查/借入身份绑定已定；初次发布、模式与独占仍 S4-Q3 |
| ref 首帧 meta/header | kind/version、ref 与 store 的身份绑定、可空 ForkOrigin | RefId 字段消费 `[F-VS-REF-ID-8B]`；tag、来源判别及其余 header 编码、第二帧初始 Snapshot 的校验仍待定 |
| Snapshot | kind/version、entry count、每项 key 与 S2 固定 12B FrameAddress | key 字符串/长度编码、其他字段端序及条目顺序；地址复用 S2 codec |

计数、key bytes、总记录尺寸必须有界，解码先检查剩余容量和重复 key，再分配；未知字段版本明确拒绝。上下文由格式门/header 绑定，不要求在每个地址内重复 StoreId。具体上限采用工程默认并写入接口/格式合同，不将“少量根约 64B”写成固定记录尺寸保证。
Snapshot 每个地址字段消费 S2 `[F-FS-FRAME-ADDRESS-12B]` 的唯一 codec，不重新选择地址宽度/端序，不添加未来预留或内容 CRC；tag 与历史快照复用相同 RootMap codec。条目尺寸包括 key 与完整 12B 地址，总记录容量使用 RBF 公共 Measure API；12B 地址不赋予对象闭包或原始 store 来源资格。

key 编码此前的 UTF-8 方向尚未冻结；Ready 时评估采用 [Bare Primitive Value](../Binary/bare-primitive-value.md) 的公共 string/VarInt 规则，并由 Snapshot 版本明确选择。该基础草案仍为 Draft，不在本轮冻结 RootMap 完整 wire，也不引入 Tagged 前置。

### spec [S-VS-REF-FORK-ORIGIN] 首帧保存一次性的 ref 创建来源

header MUST 保存明确的无来源/有来源判别；有来源时 `ForkOrigin = (SourceRefId, SourceSnapshotTicket)`，ticket 为源 ref 文件中的 SizedPtr，包含 offset 与 length，MUST NOT 使用 data FrameAddress。源 RefId 与 ticket 必须通过编码/坐标校验，拒绝 default、零长度、非法起点与截短字段；数值合法不证明成员资格。来源限同一个 VersionStore 及绑定 data，上下文由格式门与两份 ref header 校验，不重复存一份 SourceStoreId。RefId 字段采用 `[F-VS-REF-ID-8B]` 的固定 8B LE，公开来源值绑定本 VS 上下文；SizedPtr 采用固定 8B `Packed` LittleEndian。来源判别及其余 header wire 仍在 Ready 定稿，不从 CLR 内存布局推导编码。

ForkOrigin MUST 在私有创建时写入，发布后不可改变。无来源表示独立历史起点；复制相同 RootMap 不推断来源。有来源表示本 ref 的初始 Snapshot 按值复制该 exact 源快照，包含相同 Ordinal key 与 FrameAddress 值；它仍是子 ref 自己的新 revision。S5 创建入口负责建立源快照资格与两份字典一致性，后续历史跳转重新校验必要内容。

源 ref MUST 已正式发布，且源 ticket 必须是已接受快照的真实主链位置，不能指向 header、残尾或仅数值合法的位置。新 ref 使用 fresh RefId，禁止自链接；有效来源只能从新 ref 指向已经存在的 ref，故创建流程形成森林，无需持久 generation 或拓扑编号。跨文件历史另做 visited/cycle 检查，发现缺源、非法成员或循环报错，不截断为正常历史结束；全图声明查询的较窄资格见 S5。完整历史及正式 RefId 不删除、不搬迁、不复用；未来 GC/删除/分段需重新审定链接保留协议。

声明来源与实际读取源快照是不同资格：checked header 可返回 ForkOrigin 元数据，但不能仅据其中的坐标签发 checked RefRevision。当前值访问保持本地路径；S5 定义跨文件历史与全分叉查询的访问范围、预算和错误。

## 最小 API 与发布步骤

| 候选操作 | 输入 | 成功结果 |
| --- | --- | --- |
| CreateRef | 完整初始 RootMap；空字典可用，无 ForkOrigin | 自有初始 RefSnapshot；Revision.RefId 是新 RefId，独立历史起点 |
| ReadRef | 已发布 RefId | 自有 RefSnapshot：RootMap 与实际末 revision |
| PublishRef | RefId、完整新/旧 RootMap | 自有已确认新 RefSnapshot |
| ListRefs | 本 VersionStore | 已发布 RefId，可发现未绑定 branch 的 ref |

前三个操作的输入字典与成功表示消费 `[A-VS-ROOTS-OWNED]`；同步 mutation 的 Result/异常与 PublicationOutcome 载体由 `[A-VS-PUBLICATION-EVIDENCE]` 定义，不增加输出前 Prepared handle。正常 PublishRef 无 expected revision；串行 driver 自行管理应用工作流。今后若有实际冲突检测需要，可独立增加 revision CAS，不能用 RootMap 内容相等替代 revision。

### spec [S-VS-SINGLE-RECORD] 一次 ref 变更由一个 frame 生效

一次 ref 创建/更新 MUST 由一条完整 Snapshot 表达全部根值，不分成多个根的独立更新。data 构建/确认、私有初始化或纯容量试算均不改变已发布 ref。新 ref 另有正式命名的发现边界，遵循下面的创建协议。

### spec [S-VS-ROOTS-AFTER-DATA-CONFIRM] 根发布在必要完成数据确认之后

PublishRef 及 S5 CreateTag MUST 先完成确定输入、目标/名称及 owner 生命周期准入、私有拷贝与编码、记录/文件容量检查，再同步调用 `data.ConfirmDurable()`，然后向发布 RBF 追加完整记录并 `DurableFlush()`，最后安装内存状态/正常返回。MUST NOT 仅因 data 有未归还 Builder 而拒绝；未完成 Builder 不获完成或耐久资格。确定拒绝不追加、不调用 data barrier，也不因 guard fault。
data 屏障消费 S2 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`，覆盖 leased 文件内旧完成输出。应用仍 MUST 保证本次 RootMap 所需新增依赖全部已完成、属于绑定 data 且闭包不含 provisional/canceled 地址；屏障成功不证明该闭包，库不遍历业务图验证依赖闭包。无关 B 仍 Building 时，已完成的独立 A 可以发布；A 真正依赖未完成 B 时不能发布。
库不接受“以前已 flush”标志代替本次 barrier；旧 RootMap、历史 rewind、fork 初始快照和 tag 同样走屏障，无需复制 data。库不提供发布 callback，不允许同 driver 的其他应用操作交错；输入访问消费 `[A-VS-ROOTS-OWNED]` 的稳定/不重入前提。data flush 失败不得进入本次发布输出；发生异常按所涉 owner/fault 合同停用 VersionStore，不虚称未输出的另一个 owner 同样故障。
CreateRef MUST 先完成相同的输入/编码/容量及 owner 准入，调用 `data.ConfirmDurable()`，再按 `[S-VS-REF-DIRECTORY-PUBLISH]` 私有初始化并发布完整 RefId 容器；同样不要求无关 data Builder 归还。S5 的匿名/命名 fork 消费相同 data-first 准入与目录发布步骤，另确认所链接的源 ref 文件耐久；不得先调用公开 CreateRef 再补来源或名称。清理不补造 ref，不另写 allocation/Init/Bind 账本。

## term `Publication-Outcome` 本次调用的证据

`NotAttempted / Unknown / Confirmed` 分别表示尚未尝试改变公开发布事实、公开事实可能已生效但无法确认、所需发布 flush 与发现边界均成功返回。它是调用证据；重开读到快照不能倒推过去调用是否成功返回或曾得到 Confirmed。

### spec [A-VS-PUBLICATION-EVIDENCE] 同步 mutation 通过 out 保留调用证据

首版所有公开的同步发布 mutation（完整 roots 发布、ref 创建或名称创建）MUST 返回相应成功值的 AteliaResult，并接受必选的 `out PublicationOutcome outcome`；包括 CreateRef、PublishRef 及 S5 的 tag、alias、匿名/命名创建。成功值类型由所属 API 定义，不增加通用 Mutation 方法、receipt 或恢复句柄。公开枚举如下，不是持久记录字段：

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

Outcome 与实例健康是两个事实：NotAttempted 不表示零 I/O 或可直接重试，Confirmed 不表示当前实例还能使用。确定前置拒绝不新增 fault；data/源确认、私有输出及公开输出/flush/close/rename 或确认后安装异常保守停用 VersionStore，再受控清理。仅按各 owner 自己的合同处理其 fault，不 Dispose 借入 data，不把健康 data 一并宣称故障。
异常清理 MUST 先无分配地停用实例，out 不被 finally/Dispose 改写；逐一尝试尚未清理的 owned 资源，遵循 S2 `[S-FS-OWNED-FAULT]` 的主异常优先、单原异常/多 Aggregate 和汇总分配失败仍完成清理规则。无新清理错误时保留原异常对象与原栈；多个错误的汇总不充当发布证据，不声称找回底层 finally 已遮蔽的异常。正常成功 Result 不替代应用状态安装；应用安装失败后仍从已发布事实加载。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

调用证据由本条的公开事实边界推进；data 屏障、源 ref flush 与 metadata 准备不提前推进本次业务结果：

| 操作 | 写回 Unknown 的位置 | 写回 Confirmed 的位置 |
| --- | --- | --- |
| PublishRef（含 rewind）/CreateTag | 调用正式发布文件 Append 之前 | 该记录 Append 成功且发布文件 DurableFlush 正常返回后，立即写回 |
| CreateRef/ForkRef/命名创建 | 全部私有文件 flush/close 后，调用正式目录 rename 之前 | 同一次 no-overwrite 目录 rename 正常返回后，立即写回 |
| 既有 ref 的 CreateBranch alias | 私有绑定文件 flush/close 后，调用正式文件 rename 之前 | 同一次 no-overwrite 文件 rename 正常返回后，立即写回 |
| data ConfirmDurable / fork 源 ref DurableFlush | 不推进，保持 NotAttempted | 不确认本次发布 |
| tag 桶 header-only 初始化、flush/close/空桶 rename | 不推进本次 tag，保持 NotAttempted | 只建立桶 metadata，不确认首条 tag |

更新/tag 进入公开 Append 调用后的异常 MUST 保守保留 Unknown，即使该异常可能发生于内部准备阶段；不依据异常类型/消息、旧 TailOffset 或私有 flush 推断没有公开输出。公共 RBF `[S-RBF-APPEND-RESULTPATTERN]` 正常返回的失败 Result 则有 pre-I/O 合同：若它证明本次全部公开发布输出均未发生，MUST 在返回失败 Result 前恢复 NotAttempted。这是唯一的已知拒绝例外，不是输出相位猜测；已有公开输出尝试时，后续局部 pre-I/O 拒绝不能给整个调用降档。未来多 ref 扩展消费同一原则，不为此引入通用输出计数框架。
私有 ref/名称准备阶段失败为 NotAttempted；正式 rename 调用后的异常保守为 Unknown，不以事后 Exists 检查或清理回滚猜该调用是否确认。tag 空桶 rename 结果不明但尚未 Append tag 时仍为本次 tag 的 NotAttempted，实例停用，重开裁决桶 metadata。所有 Confirmed 必须先于可能失败的内存安装、清理与返回值交付；后续异常只能保留该证据。停用后重开 checked-read，不盲目重试、不回滚正式对象。
不承诺崩溃后查询某个旧尝试的 Present/Absent；out 保留此次同步调用返回/抛出时的证据，库不持久化或跨调用自动追踪它。Unknown 后同值 RootMap 不能证明该次调用曾 flush，RefRevision 也不是旧尝试身份。

### spec [R-VS-LOCAL-COMPLETE] 重开接受局部完整发布事实

可写重开先按所属初始化保护执行 RBF3 结构恢复，再完整 CRC 读取并验证所需 header、本地初始/实际末 Snapshot，或 S5 所需的目标 tag 桶全部记录/名称记录。tag 桶访问先执行 S5 的初始化长度前检，不把 header 修尾认领为初始化；惰性访问不默认扫描全部桶。完整合法新 Snapshot 作为当前值；真正未完成残尾截掉后使用剩余末 Snapshot。正式新 ref 若已无初始 Snapshot，不得补成空字典。
新 ref 的目录发现与私有残留遵循 `[S-VS-REF-DIRECTORY-PUBLISH]`；没有正式容器时，私有完整 Snapshot 也不建立 ref。命名 fork 的初始名称随同一容器发布，名称层按该正式容器的记录读取，不能从私有槽位推断或续作旧调用。
完整坏 frame/未知版本/错误身份/非法 codec MUST 报错，不能当残尾、missing 或回退旧值。只读模式不隐式修尾。被引用 data 缺失/损坏在应用实际读取时报错，不据此改写已发布 RootMap。
Open 不默认扫描所有 ref 历史或 data 图；按需打开/校验目标文件，不将成功 Open 宣称为全库审计或全部根图健康证明。所有必要错误传播显式；资源/I/O fault 不被吞成缺对象。
重开后，已知 RefId 用 ReadRef，已知 tag/branch 名称用相应 checked 解析；对象已成立时不机械重复 create-only 调用，同名或同值不证明旧调用曾确认。匿名 CreateRef/ForkRef 若在 rename 后、交付成功值前抛错，可能留下正式 ref 而调用方没有新 RefId；Unknown 同样可能如此。ListRefs 可发现实际集合，但不能仅按同值 RootMap 精确认领某次旧调用，也不保证自动返回其丢失的结果。应用需要稳定恢复定位时使用命名创建，或在自己的 Roots 闭包保存业务身份并自行核对；不增加预交付 RefId、尝试 token 或匿名创建自动重试。

## 生命周期与故障验收

| 位置 | 证据/状态 | 必要行为 |
| --- | --- | --- |
| 参数、owner 生命周期、历史枚举 mutation guard、编码/容量确定拒绝 | NotAttempted | 无发布写入/额外 barrier，健康实例可继续；无关 data Builder 不构成拒绝 |
| data ConfirmDurable 异常 | NotAttempted | data 按自身 fault，VersionStore 停止，无本次发布追加 |
| fork 的源 ref DurableFlush 异常 | NotAttempted | 源 RBF 按自身 fault，VersionStore 停止，无子输出 |
| 本次唯一公开 Append 正常返回 pre-I/O 失败 Result | NotAttempted | 写回证据后返回确定拒绝；不把这个 Result 当未知输出异常 |
| 本次更新/tag 的发布 Append/flush 异常 | Unknown | 停止；重开 checked-read，不自动 retry/rollback |
| 私有 ref 容器、命名 fork 的任一文件或 alias 绑定初始化/flush/close 异常 | NotAttempted | 尚未尝试本次业务事实；停止，允许私有残留，不删除正式对象 |
| tag 桶 header-only 初始化、flush/close 或空桶 rename 异常，尚未尝试 tag Append | NotAttempted（本次 tag） | 停止；重开检查桶 metadata，不删除已正式发布的合法空桶 |
| ref/命名 fork 的目录 rename 或既有 ref 的 alias 文件 rename 结果不确定 | Unknown | 检查实际正式对象；不因私有 flush 声称 Confirmed，不回滚目录 |
| 发布确认后内存安装/释放异常 | Confirmed | 停止、重开恢复；不撤销发布事实 |
| 正常返回后应用安装失败 | 已 Confirmed | 应用放弃旧 materialized state，从实际 RootMap 加载 |

## 实施片、Ready 工程定稿与出口

1. S4-A：项目、RootMap/RefId/RefRevision/结果值、格式门/header/codec 与 owned 生命周期。
2. S4-B：统一 RefId 容器的私有初始化及单次目录发布、ReadRef、PublishRef、ListRefs、data-first 屏障；先做一个 unnamed ref 的 public 纵向例子，名称内容由 S5 添加。
3. S4-C：末帧成员检查、CRC/未知版本、容量 guard；实施已定 Result/out 证据、公开 Append/flush/rename 边界、故障清理、两种内存安装失败与冷重开。
4. S4-D：进程终止 failpoint、多根同时更新、空字典、同值新 revision、循环数据闭包与旧快照重新发布。真实文件实验使用 W:。

| Ready 项 | 需定稿或实施验证 |
| --- | --- |
| S4-Q1 | RefId/RefRevision 自有公开值、完整等值/default/跨 VSID 前检、revision 只读位置及同 VS 重开使用，RootMap 集合公面/冻结拥有权/内部内容比较、RefSnapshot 普通类及三操作成功值已定，实施向量；ListRefs 的具体公开结果/范围仍待定 |
| S4-Q2 | 格式门内容/只读检查/借入身份绑定、RefId 内部固定 8B 字段 codec 已定，实施独立向量；header/Snapshot tag 与 codec、ForkOrigin 判别及 RefId/SizedPtr 固定字段的组合、首两帧校验、字符串/条目/总尺寸限额、消费 S2 固定 12B 地址 codec 的校验仍需定稿 |
| S4-Q3 | 初次 store 创建/格式门发布、根准入与身份生成；统一 RefId 容器/creating 的路径编码、唯一身份与残留清理、目录 no-overwrite rename 两平台资格、独占/模式/Dispose/fault |
| S4-Q4 | Result/必选 out PublicationOutcome、入口初始化、公开尝试/确认写回、pre-I/O Result 例外、异常清理及匿名续跑边界已定；实施拒绝/部分与完整输出/确认后安装故障及冷重开向量，不再开放证据载体设计 |
| S4-Q5 | checked 末读取实现、资源预算、错误分类、已完成输出屏障对接及有无关活跃 Builder 的 public 发布轨迹 |
| S4-Q6 | 新/旧/orphan 地址的应用闭包责任示例与冷重开资格；不增加图遍历来源证明 |

验收覆盖错误 DataStoreId 在写入/恢复前拒绝、空/重复/超限 key、多根原子更新、旧/新帧混合、Builder 未完成/取消地址的应用合同、末帧/tombstone/TerminationError、完整坏 CRC 不回退、create-only 冲突、正式创建前后进程终止、合法末帧末端越过 MaxOffset / 下一次追加确定拒绝，以及借用 data 不被 Dispose。裸地址原始来源错误是应用合同向量，不宣称能由数值格式检查自动检测。源码与包/平台 qualification 分开，阶段仍 Draft。
格式门独立向量固定 version=1、VersionStoreId bytes=`01..10`、DataStoreId bytes=`11..20`（均十六进制），末 4B 必须为 `FA 60 31 CD`（CRC32C=`0xcd3160fa`）；不得只用同一 encoder round-trip。覆盖全部短前缀/尾随、两身份及 CRC 的损坏、重新 seal 的未知版本/零身份、合法但不匹配的 DataStoreId；所有拒绝和必要 close 失败先于发布恢复/私有清理/输出，借入 data 保持其原资格。两角色 byte 同值合法；同 data 的两个 VS 仍区分 header/revision 身份。两打开模式不改门，缺坏门不因旁边合法 header 或私有门而补造；模式准入、初次发布和平台向量另取证。
Snapshot codec 向量包含多个连续 12B 地址、完整 FileId/Packed 高位、非法/截短地址拒绝和 cold round-trip；自有历史 RootMap 与 tag codec 消费同一格式，不从 CLR struct 大小计算 wire 容量。
自有字典/结果向量从真实 public 地址构建输入：修改原字典不影响成功值；输入 comparer 不改变 Ordinal 语义，A/a 分别保留、不同插入顺序不影响来源内容资格。覆盖 null 字典/key、default 地址、实际枚举重复/超限、输入 Count 不可信及枚举/清理/冻结失败；确定拒绝或纯捕获失败先于 barrier/输出且 NotAttempted、无新增 fault。返回 map/Keys/Values 的常规接口不能改变保留初始字典；历史结束/后续失败/owner 关闭不撤销已保存项，旧项可交给合格的同 VS 重开 owner 作 tag/rewind。三操作成功都交付非 null RefSnapshot，新 RefId 从 Revision 取得，不以对象引用或字典内容判断同 revision。构造不重新校验/读回，确认后结果分配/安装失败仍 Confirmed；BCL 探针仅提供实现选择证据，不代替这些新库验收或未定 wire/限额。
RefId 内部字段使用独立 local=0x0123456789ABCDEF → `EF CD AB 89 67 45 23 01` 向量；覆盖 1、高位及全 FF 合法、零与 0/7/9B 拒绝、失败 default/不提交消费、encoder 短目标/错上下文/默认值失败无写入及长目标只改前 8B。公开等值/hash 覆盖同 VS 同 local、不同 VS 相同 local、同 VS 不同 local 与 default；旧自有值在同 VS 重开后可用，关 owner 不撤销值。两 VS 共借 data 且 local 相同，错误值在 ReadRef/PublishRef/History/alias 的 I/O/barrier 前确定拒绝、不新增 fault，mutation 保持 NotAttempted。checked 来源元数据可产生 bound 值但不证明源存在；坏宿主字段不外泄值、无来源不借零值代替未定判别。格式、public 类型与真实平台/发号资格分开验收，不拿现有 Binary 测试或候选算术称为新库通过。
RefRevision 等值的内部独立向量覆盖完整 VSID/local/ticket、同 offset 不同 length、同 ref 同值重发及同值子初始/源不同 revision；default 的比较/hash/两属性合法，fork 准入拒绝 default/不同 VSID 先于 I/O/barrier 且 NotAttempted、不新增 fault。public 消费从正常发布/checked 当前与历史取得值，保存后结束查询/关闭并重开同 VS，再 fork 仍按现有协议重读与确认；继承快照的 RefId/Ticket 必须为实际源位置，能关联 ListForks 的 exact 来源边。缺源/坏内容/错成员不因持有旧值免检；ForkOrigin 元数据和提前 ticket 不签发 revision。确认后构造只保存值，后续交付异常保留 Confirmed；不以投影/default 或旧栈测试称为公开 codec、防伪、匿名跨进程书签或新库已实施。
目录向量覆盖私有 header/Snapshot/flush/close 各阶段、目录 rename 前后、正式目录缺少必要文件或坏身份、只读不采用/清理私有残留、重复 RefId 不覆盖。纯 S4 ref 消费不读取 names，也不需要 S5 codec；S5 的组合创建另验同一发布点的名称资格。
证据向量覆盖每个 mutation 最早重置 out、正常成功/失败 Result 的对应状态、内部助手异常时直接写回、公开 Append 的已证明 pre-I/O Result 拒绝恢复 NotAttempted，以及无法辨相位的 OOM/输出/flush 异常保持 Unknown。覆盖 data/源 flush、tag 空桶维护失败为 NotAttempted 但实例停用；目录/文件 rename 返回后立即 Confirmed，再分别注入成功值/投影安装和资源清理异常。清理不降档、不遮蔽原错误，汇总分配失败仍尝试其他资源；借入 data 不被 Dispose。匿名 rename 成立却无成功值交付时，ListRefs 能见对象但同值不能精确认领；正常/异常调用证据测试与真实进程终止后没有 out 的恢复测试分开，不把 C# byref 或单元注入当平台 rename 资格。
header 向量覆盖无来源/有来源、非法/截短源 ticket、自链接、源 store/ref 身份以及第二帧初始化边界。普通 ReadRef 不扫描源历史；返回本地当前字典不宣称 ForkOrigin 目标健康，显式来源遍历缺/坏时报错且不改本地 head。
CreateRef/PublishRef/CreateTag 各覆盖 A 依赖全部完成而无关 B 仍 Building 时成功发布、B 所租文件中的旧 dirty 依赖在根写出前被确认、B 后续完成必须重新确认，以及任一 data flush 失败后不尝试根输出并停用 data 的全部 Builder/Writer。根真正依赖未完成 B 的负例属于应用闭包责任，不能把“库自动识别并拒绝任意未完成图”写成单测承诺。

