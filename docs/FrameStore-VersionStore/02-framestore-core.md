# S2：FrameStore 核心、地址与文件生命周期

状态：**Draft；2026-10-05 确认首帧 header、自动归还与 config 数量上限；2026-10-07 允许活跃 Builder 期间确认和随机读取已完成输出；2026-10-08 确认地址固定 12B codec；2026-10-09 定稿资源基线、文件 header、owned 租借/归档维护、读结果/同步 inventory/audit、FileId 编号恢复、正式 active/archive 路径、FrameAddress 值/公开 codec、格式门记录/只读校验及软阈值参数/重开规则；初次空 store/正式门直接 create-only 写入与成立/返回边界，以及 owner 生命周期锁/门前 bootstrap/模式互斥已定；私有 data 初始化前缀/取消与只读保留合同已定；实际根准入已定；公开工厂、目录生命周期与 RBF 公共前缀核验已实施；Inventory/Audit 已实施；完整 S2 系统验收待后续切片**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)。本阶段独立于发布和命名层。

当前代码与验证边界见[同步物理检查记录](02-framestore-inspection-implementation.md)及[公开持久化闭环记录](02-framestore-persistence-implementation.md)，前片格式/运行内核证据见[首个源码切片](02-framestore-core-implementation.md)；下文仍是完整 S2 合同，不以功能实现替代完整系统验收。

后续过程证据见[跨进程与中断取证 R1](02-framestore-process-acceptance.md)，包含真实 public 停点强杀与冷重开，以及仍须补足的三个内部窗口；独立循环图消费者与有界资源/规模测量见 [R2 记录](02-framestore-consumer-resource-acceptance.md)。当前实施进度以[收尾计划](02-framestore-completion-plan.md)为准。

## 本阶段目标

创建 `src/FrameStore/FrameStore.csproj`、`tests/FrameStore.Tests/FrameStore.Tests.csproj`，采用 `Atelia.FrameStore` 身份。
提供独立的多文件不可变 Frame 分配器：Create/Open/OpenReadOnly、申请并完成单帧、随机读取、不透明地址、恢复及同步耐久确认。普通分配不提供业务上的全局顺序。
首版支持多个 active 文件、每文件独占租给一个 Builder，文件申请/归还串行且可嵌套；成功完成或健康取消后可以复用文件。普通帧可以交错构建、按不同于申请次序的顺序完成。达到软阈值后文件归档只读。
按需引用 Rbf/Data/Primitives，不引用旧库或业务项目。本阶段的定稿、实施与验收只包含分配、读取、文件生命周期和耐久确认。

## 已确认与待定的边界

| 事项 | 当前结论 |
| --- | --- |
| 单帧追加能力 | 保留 RBF 的 buffer Append、未知尺寸 Builder、分立长度的已知尺寸 Builder；签名细节待定 |
| 构建自由 | 每文件一个 Builder；owner 可持多个未完成 Builder；租借可嵌套；首版调用仍串行 |
| 文件选择 | 从 active 当前可分配文件中选择数值 FileId 最小者；低编号忙时跳过，不等待 |
| 轮转 | 三种追加成功后 TailOffset 大于阈值才停止；Create/Open 单一 long 参数、默认 64GiB、实例固定；重开按恢复后 TailOffset 重算，archive 不解封 |
| 磁盘生命周期 | 私有 creating → active → 按编号分桶的 archive；不维护 active manifest/status |
| 归档桶 | 固定 1024 个编号一个桶；版本 1 使用 6 位小写 hex 桶名与 8 位完整 FileId 文件名，active/archive 共用文件名 codec |
| 编号恢复 | 完整流式检查正式目录名称，取 active/archive 实际文件最大 FileId；无持久计数器或全历史 ID 表，不填缺口；耗尽仅拒绝需新文件的请求 |
| 地址编码 | 固定 12B，完整 uint FileId 与 SizedPtr；基础地址无预留或内容见证字段 |
| 耐久 | 确认调用时全部必要已完成 active 输出，包括 leased 文件；未完成 Builder 不获得资格，archive 资格来自移动前 flush |
| 活跃构建期间随机读取 | 同文件已完成前缀可以读；未完成新帧不可读，扫描相关入口仍遵循 RBF guard |
| 读结果与物理检查 | ReadFrame 返回自有完整帧；Inventory/Audit 为同步 visitor，扫描期间禁止同 owner mutation，随机读仍合法；结构发现与完整内容校验分别报告 |
| 首帧 meta/header | 固定 24B payload：格式版本 1、16B StoreId、uint FileId；按首物理帧位置识别，不占用用户 tag 范围 |
| 格式门 | framestore.format 为固定 24B 普通控制记录：版本、StoreId、唯一 CRC32C；所有打开模式共用只读完整校验，不恢复或补造 |
| 成功 EndAppend | 正常成功返回前仅登记完成/停止资格并自动归还；归档统一在后续合法写准入、ConfirmDurable 和可写 Open 中处理 |
| Builder 数量准入 | 可选 config 的 MaxOutstandingBuilders，默认 32；只计已签发且未结束的 Builder，超限立即拒绝；同步 Append 最多另占一个短期租借 |
| 句柄与缓存基线 | 可写 owner 保留 active 句柄，显式使用 RbfCacheMode.Off；archive 随机读按操作打开/关闭，不引入 idle 淘汰或 reader pool |

本表是状态导航，具体合同以下文条款为准；Draft 不表示已实现或已有平台资格。

## term `FrameStore-Context` 存储上下文

一个持有持久 StoreId、访问模式及实例生命周期的存储 owner。它拥有文件组织、所有读写句柄和 fault 事实；首版由调用方串行使用。整个 store 的 owner 协调消费 `[S-FS-OWNER-LOCK]`：一个可写 owner 排斥其他全部 owner；没有可写 owner 时可共享多个只读 owner，各实例的调用仍串行。不能仅靠某个 RBF 文件的独占打开替代整个 store 锁。
StoreId 为创建时生成的随机非零 128-bit 值，以 16 个 opaque bytes 作为 canonical 编码，在 create-only 格式门中持久化；各文件 header 逐字节复制并比较同一值，不重新生成、不转储 CLR Guid 内存或依赖其默认字节序。格式门的记录与读取合同见 `[F-FS-OWN-FORMAT]`，初次空 store 的建立与返回边界见 `[R-FS-STORE-CREATE]`；门成立前的锁 bootstrap 见 `[S-FS-OWNER-LOCK]`；实际根资格消费 `[S-FS-ROOT-ADMISSION]`，平台证据见实施记录。路径移动不改变身份；复制与外部改写不由本协议自动协调。

### spec [S-FS-OWNER-LOCK] 同一控制文件协调整个 owner 生命周期

**控制角色。** 格式版本 1 使用根内精确小写 `framestore.lock`：永久保留的普通空文件，不是 RBF，不存 StoreId、PID、busy、generation 或日志。实际组件拼写、ordinary/no-follow 资格及持有的同一句柄 Length 恰为 0 均 MUST 合格；非空、特殊项或无法取得资格时拒绝，不截断或修复。它仅为协调设施，不证明历史创建者、根身份或 store 身份，也不授权清理其他项；StoreId 仍只由完整合法门认领。控制文件不参加 FileId/max、地址、Inventory/Audit 或 data dirty 集合。

**模式与生命周期。** Create/可写 Open MUST 取得排斥其他全部 owner 的独占锁；OpenReadOnly MUST 取得与其他只读 owner 兼容、与 writer 互斥的共享锁。锁覆盖正式门检查、目录发现、恢复/残留裁决、实例运行及 owned 资源清理；同一已取得的句柄直接交给 owner 持有，不能在初始化与交付间解锁重开，也不按操作反复加锁。锁冲突或无法严格取得锁即拒绝本次准入，不等待、轮询、偷锁或交付降级实例。每个 owner 仍按既定串行合同使用；正常同 owner 随机读取复用原有资格，不再申请第二个 store owner。

| 入口 | 控制设施的打开与前置 |
| --- | --- |
| Create | 参数/config 与初始输入预检后，按实际根准入准备必要的空根；以不截断的 OpenOrCreate、ReadWrite 取得控制候选并申请独占锁。根/控制设施 bootstrap 是唯一允许先于该锁的创建动作；初始化目录及正式门输出必须在获锁后 |
| 可写 Open | 只以 Open、ReadWrite 打开既有控制文件并取得独占锁；缺失/不合格不补造 |
| OpenReadOnly | 只以 Open、Read 打开既有控制文件并取得共享锁；缺失/不合格不补造、不写入 |

Create MUST 在获锁后重新检查初始输入，不能沿用锁前空根的观察：另一调用可能先获锁并已建立门或初始化树。fresh 输入只允许已合格的控制设施及合法可选 config；已有门（包括短/坏门）、布局/数据/私有残留或未知内容仍按 `[R-FS-STORE-CREATE]` 拒绝。仅有合格控制文件（可带 config）的 bootstrap 中断，不建立 store 身份或初始化事实，可供下一次 Create 复用控制设施；这不续作已有初始化树，不增加 reset/resume 入口。CreateNew-only 控制文件不能满足这种复用，而非截断 OpenOrCreate 不改写既有内容；创建控制文件的调用也不必是最终获锁者。

**严格平台机制。** Windows 的最小入口为上述同句柄打开，writer 使用 FileShare.None、reader 使用 FileShare.Read，均不启用 Delete/Inheritable/DeleteOnClose。Linux MUST 对将长期持有的同一 fd 显式取得 checked、nonblocking 的 flock（writer EX、reader SH）；只有原生锁明确成功才继续，冲突、unsupported 或其他失败均拒绝。即使先用上述 FileShare 打开，也不能把其返回成功作为锁证明，或在失败后退回 best-effort；.NET Unix 自动锁可被禁用且会忽略部分错误。具体 ordinary/no-follow 打开/句柄组合按 S2-Q3 实施，不因此宣称实际根/类型或 Linux 已获资格。

**退出与失败。** fault 立即失效操作资格但 MUST 保留锁，直到受控 Dispose；factory 失败与 owner Dispose 先尝试清理全部其他 owned 数据/临时资源，最后在 finally 中取出并清空控制句柄槽位，单次尝试关闭。错误消费 `[S-FS-OWNED-FAULT]` 的主错误优先/全部清理规则，不因前项关闭失败跳过锁关闭，不重试已尝试对象。进程终止由 OS 释放其句柄锁；文件存在不表示 busy。任何健康退出、fault 清理或 bootstrap 失败 MUST NOT 删除、rename、替换控制文件；否则不同 inode 上可各自成功持锁。独立 FrameRead 的自有 buffer 可在 owner 关闭后保留，不延长锁。

本条只协调遵守相同协议、通过根准入的参与者；不协调裸 RBF/外部改写、活跃期间根/控制设施的外部替换或复制。它不提供物理根别名检测、实时 writer–reader 观察或更强故障模型，也不能替代 VersionStore 自己的发布根锁。
（Informative）全 owner 一律独占也正确；共享只读是复用既有访问模式的工程选择，无新公开并发 API。未协调的只读 owner 可在健康归档的跨目录查找/扫描中误报缺失或重复，并撞上 retained RBF FileShare.None；当前不引入 live-view/重试协议。依据为 [RBF 工厂](../../src/Rbf/RbfFile.cs)、[.NET Unix handle](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)与 [flock](https://man7.org/linux/man-pages/man2/flock.2.html)。[Windows 公面探针](../../experiments/StoreOwnerLockProbe/README.md)在 .NET 10.0.5 / E: 通过 28 项同/跨进程、kill 释放、bootstrap/坏控制文件检查；只证明该锁原语，不证明完整 FrameStore、根/no-follow、Linux、rename 或包资格。

### spec [S-FS-ROOT-ADMISSION] 实际根与受管组件准入

调用方选择 root 及可信父路径；实现将路径绝对化，只检查根末组件的普通目录/no-follow 资格，不建立祖先句柄链、物理根别名表或硬链接去重。调用方路径的大小写不属于持久格式；Windows 正常大小写宽容的根/父路径不得仅因拼写不同被拒绝。Create 可以创建缺失的末级根，父目录必须已存在且合格；不递归创建缺失祖先。

精确拼写要求适用于本协议的受管组件：固定控制文件与 `active`/`archive`/`creating`、桶及数据文件名。以实际直接目录项取得名称资格，不能把宽容路径查找成功当作精确拼写；所有受管目录必须是普通目录，叶项必须是普通文件，拒绝 symlink、junction/reparse、设备、FIFO 等特殊项。已经从实际枚举取得 canonical 名称的项只需类型检查，不为每个文件重新扫描父目录。完整正式名称发现保持 O(A+B+H)；active 排序/运行台账初始化成本另计。

root、creating、active、archive 及实际归档桶 MUST 位于同一文件系统；受管叶文件亦须与其父目录具有相同文件系统/mount 资格。数据移动必须使用原生同文件系统、不覆盖 rename；不能退回复制后删除或覆盖目标。Windows 使用普通 disk/no-reparse 句柄资格及实际 volume GUID，Linux 使用 statx 的普通类型、device 与 mount identity；无法提供所需资格时拒绝。当前 Windows 不支持无法取得 volume GUID 的位置，Linux 要求可提供所需 statx 字段及 renameat2 RENAME_NOREPLACE 的环境；未支持的平台不降级打开。owner 锁另消费 `[S-FS-OWNER-LOCK]`。

这些入口消费已定的非对抗性、无外部并发改写/替换假设：在准入及持锁期间，根和受管项由遵守本协议的 owner 管理。由此可以在类型/名称资格后调用现有 public RBF 路径工厂，不需要新增裸 handle 导入或敌对 TOCTOU 防护。检查不是对任意祖先、外部复制或裸 RBF 写入的协调；Open 不建立根其他内容的永久白名单。实际平台与失败向量证据见[公开持久化闭环记录](02-framestore-persistence-implementation.md)，不由机制选择推定完整 S2 Accepted。

## 地址表示与固定编码

FrameAddress 对外是不透明、可持久编码的局部地址；首版采用 `FileId + SizedPtr`，正整数 `uint` 文件编号、0/default 非法，ticket 的范围由 RBF/Data 公共 API 校验。不向普通调用方提供字段拆解、地址算术、大小排序或相邻帧推算合同。
StoreId 属于上下文，不必重复塞进每个数据引用。地址相等只在同一个 store 中有意义；相同数值在另一个 store 可能恰好也合法，**裸地址无法检测调用方原始来源错误**。
跨 owner 的 Builder 拒绝由实例生命周期负责；持久上层绑定负责选择正确 store。二进制格式、公开值/codec 与失败规则由下条锁定；S2-A 实施，不另建规范文本或地址错误族。
成功完成的帧在首版存续期间不重新分配、不改写、不删除。整个文件从 active 移到 archive 只改变容器路径，不改变 FileId、ticket 或帧字节，不属于帧重定位。地址不透明不等于已经提供可搬迁的逻辑对象 ID；未来改变定位编码必须另行处理兼容性。取消或截断的未完成预约没有稳定身份保证。

### spec [F-FS-FRAME-ADDRESS-12B] 基础地址使用固定 12B codec

本条遵循 S0 `[S-FS-ADDRESS-FIXED12]`。首版 FrameAddress 的 canonical 二进制编码 MUST 恰为 12 bytes，字段布局如下：

| byte 范围（半开） | 字段 | 编码 |
| --- | --- | --- |
| `[0,4)` | FileId | 完整 uint32，LittleEndian，0 非法 |
| `[4,12)` | Ticket | 完整 `SizedPtr.Packed` ulong，LittleEndian |

MUST 按字段显式编码与解码，不转储 CLR struct 内存，不追加对齐 padding；Ticket 使用 Packed 的完整 64 bits，不改为 `SizedPtr.Serialize()` 的交错值或 varint，不将 offset/length 各压成 uint。解码须精确消费一条 12B 地址，拒绝截短/尾随输入、非法 FileId 及不满足下述数值下界的 ticket；容量与帧资格消费 RBF/Data 公共合同，恢复 Packed 本身不产生真实帧、主链成员或来源证明。FileId 与完整 Packed 决定同一上下文内的值相等。
MUST 保留 SizedPtr 当前完整可表示范围，不新增单文件 4GiB/16GiB 硬界，不因地址编码把 MaxOffset 改成帧末端上界。基础地址不编码 StoreId、预留字段、CRC、fingerprint 或 generation。已知尺寸 Begin 签发的 FileId、ticket 及其 12B 编码在正常 End 后 MUST 保持相同；健康取消或修尾后的地址复用仍按既有定位合同处理，不承诺取消尝试身份。

**公开值与唯一 codec。** 首版入口如下；FrameAddress 已在首个源码切片实施：

```csharp
public readonly struct FrameAddress : IEquatable<FrameAddress> {
    public const int EncodedSize = 12;
    public static bool TryRead(ReadOnlySpan<byte> source, out FrameAddress address);
    public bool TryWrite(Span<byte> destination);
    // Equals(FrameAddress), Equals(object?), GetHashCode(), ==, !=
}
```

类型 MUST 非 positional，不提供公开数值构造、FileId/Ticket 投影、Deconstruct、算术或排序。private uint FileId + private SizedPtr Ticket 为普通内部表示，不指定 Pack/Size 属性。default 是受支持公开构造/解码路径中唯一可取得的非法值；其自等、与其他值比较及哈希均合法，但不可编码或读取，也不自动解释为 nullable/空引用的持久表示。上层在 barrier/输出前可用 `address == default` 预检，不增加公有 IsValid 或第二份状态。Equals MUST 比较完整 FileId/Packed，equal 值有相同 hash；hash 不进入持久格式，不保证跨进程/版本稳定，也不证明两个值来自同一 store。

TryRead MUST 要求 source.Length 恰为 EncodedSize。先设 out 为 default，再用局部值解码；只有全部检查成功才签发值。检查为 FileId != 0、ticket.Offset >= `RbfScanBoundary.Empty.EndExclusive`、ticket.Length >= `RbfFile.MeasureWriteSize(0, 0).Value.FrameLength`，消费公共值而不复制 RBF wire 常量。Packed 的表示已保证非负、对齐及 SizedPtr 上界；`FromPacked` 不提供非空帧票校验，`TryCreate` 也接受零长度，不能代替这些下界。不得额外要求 EndOffsetExclusive <= MaxOffset，或以 FrameStore 用户区初始化边界 I 排除 header 坐标。长度或数值拒绝返回 false、out 保持 default，不抛参数/格式异常、不执行 I/O。
复合记录 MUST 先保证剩余长度至少 EncodedSize，再传入恰好 12B 的 slice，成功才推进 12B；宿主 codec 负责完整记录的剩余字段/尾随检查。无需 bytesRead、ref cursor 或前缀解码重载。

TryWrite MUST 先检查 default 和 destination.Length >= EncodedSize；拒绝返回 false，整个目标保持不变。成功只按上述 LE 格式写前 12B，剩余目标保持不变。输入精确宽度与输出容量是不同合同，不增加 TryWrite 的写入计数。两方向不分配输出 buffer、专用错误或 wrapper；调用层把 false 映射为所属 schema/字段错误。FrameStore ReadFrame 仍先检查 owner 生命周期/fault，再在 I/O 前拒绝 default；bool codec 失败不改变 owner 健康资格。

内部可信构造 MUST 仅保存已取得资格的 FileId/Ticket；Append/End 成功后不再调用公开 decoder、Measure 或可拒绝的构造校验，不引入新堆分配/I/O/回调。这保持 `[S-FS-END-AUTO-RETURN]` 的成功交付边界。纯 codec 数值成功不检查文件存在、真实帧、CRC、主链、原始 store、完成或耐久，也不签发取消尝试身份；提前地址同样可编码。
首版不提供独立 FrameAddressCodec 类型、地址专用 Result/error、规范文本 Parse/Format 或自动序列化适配。若工具需要 roundtrip 文本，可由调用方显示成功编码的 12B hex 并恢复 bytes 后调用 TryRead；一般 ToString/日志不成为持久协议。出现真实调用需求再增加窄入口。
（Informative）普通两字段表示是减少拆/拼及布局配置的工程默认，三 uint 等内存优化等实际成本证据出现后评估。内存大小、数组步长、嵌套容器及调用成本在目标运行时记录，不从 wire 推导固定 12B/16B 内存 ABI 或速度保证。独立数值来源见 [SizedPtr](../../src/Data/SizedPtr.cs)、[Data 测试](../../tests/Data.Tests/SizedPtrTests.cs)、[RBF 公共尺寸](../../src/Rbf/RbfFile.cs)与[尺寸向量](../../tests/Rbf.Tests/Internal/RbfWriteSizeTests.cs)；冻结 EventJournal 及兄弟仓的不同地址/Serialize wire 不产生新格式兼容义务。

## term `Completed-Frame` 已完成帧

成功 append 或重开后存在的完整 frame 事实。Completed 不等于本次确认耐久，也不等于业务状态已发布；数值地址本身不提供这些资格。
RBF ticket checked read 证明 CRC，不独自证明主链成员；正常签发来源、扫描发现和消费者记录的来源约束不能省略。

## 候选入口与职责

| 入口 | 本层负责 | 消费者负责 |
| --- | --- | --- |
| Create/Open/OpenReadOnly | 格式门、目录发现、owned handles、恢复 | 选择 store/mode；不把 Open 当全库 audit |
| Append/两种 BeginAppend | 文件租借、提前地址、RBF 完成及 fault | tag、stored payload/meta 编码 |
| ReadFrame | 寻址、完整 CRC、buffer 生命周期 | 解码及业务合法性 |
| Inventory / Audit | 结构发现 / 完整 CRC 检查、同步 visitor 与资源清理 | 不以枚举顺序推断业务关系；业务 codec/依赖由消费者验证 |
| ConfirmDurable | 同步确认 owner 的全部完成输出 | opaque 引用闭包及业务生效 |

普通分配器不承诺 ScanForward/ScanReverse 的全局语义，也不提供“地址之后”的业务范围。Inventory/Audit 扫描实际正式文件，其文件枚举次序不构成发布顺序；签名、信任与成本由下两条定义。

只提供 owned 分配/read/Builder，不公开可绕过本层 bookkeeping 的裸 `IRbfFile`。本层不认识上层控制记录或提交 codec。首帧识别、完整用户 tag 范围与扫描分类遵循 `[F-FS-META-FIRST]`。

### spec [A-FS-READ-OWNED] 随机读取返回独立拥有的完整帧

首版完整读取入口如下；这是待实施合同，不是已存在的 API：

```csharp
AteliaResult<FrameRead> ReadFrame(FrameAddress address);
```

FrameRead MUST 为 sealed class，提供只读 Address、Tag、PayloadAndMeta（ReadOnlySpan<byte>）、TailMetaLength、IsTombstone 和幂等 Dispose；构造入口不公开。它私有持有一个 RbfPooledFrame，消费其完整 CRC 与 buffer 所有权，不复制 payload、不暴露 SizedPtr、IRbfFile、Reader 或后续 I/O 方法。失败 Result 不签发结果，清理正常时 RBF 错误原样传播；本层非法地址/缺失文件按所属层分类，不把权限、I/O、格式或 CRC 错误当成不存在。
调用顺序 MUST 为 owner disposed/shared fault → 地址/定位资格 → 取得 reader → RBF 完整读取 → 必要临时 reader 关闭 → 移交 FrameRead。已有 owned active 直接复用先前 Open/Create 已取得的身份/header 资格，不为随机读再起首帧扫描；未拥有的文件临时只读打开后先做身份/header 检查。Building 时继续消费 `[S-FS-RANDOM-READ-COMPLETED-PREFIX]`，不为读结果追加扫描或全 owner Idle 前置。仅数值地址与 CRC 成功仍不证明主链成员或原始来源。
包装分配、读取或临时关闭失败时，MUST 释放尚未移交的底层帧；清理遵循 `[S-FS-OWNED-FAULT]`，不让关闭异常遮蔽主异常。成功结果不持有 reader/owner，后续归档、owner fault/Dispose 不撤销已移交的 bytes；调用方负责逐个 Dispose，不由 owner 回收它们。结果 Dispose 只终结自身 buffer 所有权一次，所有引用别名共享此状态；之后 PayloadAndMeta getter 抛 ObjectDisposedException，已取得的 span 不能再用，纯元信息值仍可保存。
首版不提供 caller-buffer/Span 读取、Reader-bound FrameInfo、TailMeta-only Preview 或独立结果 Lease。出现实际分配成本或大 payload 预筛选需求后再设计相应窄入口，不为不存在的新栈消费者增加尺寸查询和多套结果类型。

### spec [A-FS-INVENTORY-AUDIT] 物理检查使用同步 visitor

首版入口和结果形态如下；不返回 sequence、公开枚举器或持久 cursor：

```csharp
AteliaResult<long> Inventory(Action<FrameInfo> visitor, CancellationToken cancellationToken = default);
AteliaResult<long> Audit(Action<FrameFileAudit> visitor, CancellationToken cancellationToken = default);
```

| 返回给 visitor 的类型 | 最小字段与资格 |
| --- | --- |
| FrameInfo：readonly struct，内部构造 | Address、Tag、PayloadLength、TailMetaLength、IsTombstone；纯值快照，无 Reader/read 方法。来源是本次实际主链扫描，已具备 framing/TrailerCRC 资格，不承诺用户 PayloadCRC |
| FrameFileAudit：readonly struct，内部构造 | uint FileId、ReadOnlyMemory<byte> HeaderPayload、long UserFrameCount；HeaderPayload 是已完整校验的 24B decoded header 的独立副本，无 header FrameAddress。仅在该文件全部实际帧完整 CRC、正常 EOF 及必要关闭成功后回调 |

Inventory MUST 遍历全部实际正式 active/archive 文件，各文件沿公共 `ScanForward(showTombstone: true)` 的真实主链推进；首帧按 `[F-FS-META-FIRST]` 完整检查身份/header，只省略这个 checked 首 ticket。之后的用户帧包含 tag=0 和墓碑，不按 tag 或业务 codec 过滤；不校验其 PayloadCRC。Audit 复用同一内部文件/主链遍历，额外对每个用户帧执行完整读取并及时释放临时帧，按文件报告 header 与用户帧数；header 不计入用户帧数。两入口不审计应用 schema、引用闭包或业务发布，不把结构 inventory 的正常结束称作内容健康。

**准入与重入。** 两入口 MUST 先检查 owner disposed/shared fault、非 null visitor、无正在运行的物理检查及 OutstandingBuilders=0，再做目录枚举或文件读取。零 Builder 是全库逐文件 Scan 的 Idle 条件；这不改变 RBF 既有 sequence 在后来 Building 下仍可推进的合同。首版无并发调用和外部改写，扫描不另建跨进程 snapshot 协议。
通过准入后只设置 owner 的一个 ScanActive 位，整次同步调用期间拒绝 Append、两种 BeginAppend、ConfirmDurable 和递归 Inventory/Audit；拒绝 MUST 先于维护、租借、创建、flush/close/rename，且不 fault。它防止回调中的 DrainStopped 关闭当前扫描 reader，无需 pin、迁移 reader 或改变 RBF 共享规则。visitor 可串行 ReadFrame，保留纯值 metadata 或独立读结果；active 复用 owned 句柄，archive 随机读仍按操作另开/关只读句柄。此窄 guard 不收紧其他调用期间的 completed-prefix 随机读和 Building 下 ConfirmDurable。

**资源与终止。** raw RBF sequence/枚举器只留在方法栈内，不把 ref struct 放入 class 或逃逸到延迟结果。当前临时扫描句柄保持到该文件结束，并登记在 owner 可清理的单一槽位；不积累历史 reader。正常文件结束、错误清理及 owner Dispose 都 MUST 在调用 public Dispose 前先取出并清空临时槽位，关闭抛错也不重试。owner Dispose 仍是合法受控清理，先失效 owner，再逐项尝试临时及其他 owned 资源；扫描 finally 只清理尚未取出的临时句柄，不重复 Dispose。每次 visitor 返回后、继续 I/O 前及最终成功返回前 MUST 重检 owner disposed/shared fault 与 cancellationToken；回调在最后一项 Dispose owner 或取消 token 也不能获得全库成功。
目录迭代、文件打开/header 检查、每次 MoveNext/完整读及回调之间检查 cancellationToken；取消抛 OperationCanceledException，清理仍继续。不承诺打断正在执行的一次同步 I/O/CRC 或限制其耗时、单帧 buffer 与 RSS。每个 raw MoveNext=false MUST 检查 TerminationError，非 null 原样返回失败而非空/正常 EOF。文件定位、目录枚举、header/帧错误、I/O、取消或 visitor 异常均不得返回成功；不会跳过坏文件、找后继帧或自动修复 archive。
“最后释放控制锁”针对 owner 台账中的临时扫描 reader、retained 数据文件及控制句柄。目录枚举器由同步调用栈清理；回调 Dispose owner 后，栈中枚举器只允许释放，MUST NOT 再次推进、访问新目录项或继续校验。调用方可以在 Dispose 返回后立即重开同一 store；旧扫描只收尾并拒绝成功，不再观察新 owner 的目录变化。
正常成功 Result 的 long MUST 为完整访问/校验的用户帧总数，checked 累加，且只有全部正式目录枚举、全部文件 EOF、必要关闭及最终 guard 均通过后才能返回。visitor 已接收的前缀或单文件报告不等于全库完成。无正常早停 bool、partial-success 或 Complete/Stopped 枚举；需要取消时使用 token，visitor 自身异常直接传播。普通读/扫描错误 Result、纯读异常、visitor 异常及取消不自行 fault 健康 writer；actual owned 关闭/释放失败按 `[S-FS-OWNED-FAULT]` 停用 owner 并汇总，主异常优先。所有退出路径的 finally MUST 清除 ScanActive，即使清理抛错。

**覆盖与成本。** 文件发现、规范路径/重复编号及创建残留资格消费本阶段目录协议，不在读 API 建立第二份 manifest。成本含实际目录项、全部文件 header 与 framing；Audit 再含全部用户 payload bytes，单次只保留当前文件 reader 和当前完整帧，visitor 自行累积结果的成本另计。无持久应有集合且允许编号缺口时，只能报告实际发现/定位中已观察的缺失；无法凭剩余目录证明一个已整体消失、没有已知引用的历史文件曾存在。全库完成只覆盖本次实际正式集合，不等于所有历史依赖健康或“所有曾发布文件未丢失”。正常分页、暂停/恢复 cursor、硬 I/O/内存预算及持续写入期间的全库扫描，等真实消费者要求这些能力后再设计。

本合同复用当前 [RBF pooled 结果](../../src/Rbf/RbfPooledFrame.cs)、[栈内正向扫描](../../src/Rbf/RbfForwardEnumerator.cs)及[扫描信任级别测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)；这些是底座源码/测试证据，不是 FrameStore 实施验收。

三种追加入口均已实施，公开签名如下：

```csharp
AteliaResult<FrameAddress> Append(
    uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);
FrameBuilder BeginAppend();
FrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out FrameAddress address);
```

未知尺寸模式在正常 EndAppend 时签发地址；已知尺寸模式在 Begin 时取得绑定地址。FrameBuilder 保留 PayloadAndMeta、reservation/回填及 RBF 的 EndAppend 用法；owned Builder/Writer 的类型、共享租借身份与 guard 由下条定义，不返回可绕过本层生命周期的裸 Builder。

已知尺寸入口按 S1 保留 payloadLength、tailMetaLength 两个声明：调用公共尺寸 API 校验/试算，再通过 BeginAppend(payloadLength, tailMetaLength, out ticket) 取得当前帧的绑定地址。尺寸不用于提前轮转。
正常完成调用 EndAppend(tag)，使用 Begin 的 meta 声明；显式 meta 参数只能核对一致，不能改变划分。不在本层退化为仅保存合计长度。

### spec [A-FS-OWNED-BUILDER] Builder 与 Writer 共用一次性租借身份

首版 owned façade 使用 readonly struct，签名如下；这些类型已在首个源码切片实施，并由公开 store 工厂取得的 owner 签发：

```csharp
public readonly struct FrameBuilder : IDisposable {
    public FramePayloadWriter PayloadAndMeta { get; }
    public AteliaResult<FrameAddress> EndAppend(uint tag);
    public AteliaResult<FrameAddress> EndAppend(uint tag, int tailMetaLength);
    public void Dispose();
}
```

FramePayloadWriter MUST 为实现 IReservableBufferWriter 的 readonly struct，转发其全部方法与 Length；声明预算、reservation token、CRC、finalize 和内层 epoch 仍由 RBF/Data 负责，不重新实现。Length 沿用 RBF 的既有投影，不自动改成应用 payload 已用量。
每次 Begin MUST 在签发内层 Builder 之前分配一份新的共享 Lease 对象；Builder/Writer 的所有副本只持有这份租借身份。Lease 不池化、不复用、不持久化；FileEntry.CurrentLease 的引用相等与 Lease 的活跃状态共同证明当前资格，不另加外层数值 epoch 或尝试 ID。文件资源在用户输出之前进入 owner 台账。

任何有 owner 的 End、PayloadAndMeta getter 或 Writer 方法 MUST 先检查 owner disposed/shared fault，再检查租借；不得先访问内层或按 RBF epoch 猜 outer 资格。owner 已释放抛 ObjectDisposedException，fault 抛 InvalidOperationException。健康 owner 上 default/已结束/非当前 Builder 的 End 返回 FrameStoreStateError（AteliaError 子类型）；无效 Writer/getter 抛 InvalidOperationException。内层可纠正 End Result 拒绝原样传播，活跃租借及额度保持。default 或已终结副本的 Dispose 为 no-op；Dispose 属于受控清理入口，不用普通 fault guard 阻断清理。

Lease 只额外镜像一个 HasUnadvancedBorrow 位：GetSpan/GetMemory 正常返回后置 true，Advance（包括 0）正常返回后清 false；失败 Advance 保持原位。ReserveSpan、Commit、TryGetReservedSpan 和 Length 不改变此位，普通 borrow 期间 reservation 的 Commit/TryGetReservedSpan 继续合法。End 在委派前发现此位为 true 时 MUST 抛 InvalidOperationException，保留同一租借供 Advance/Advance(0) 纠正，不 fault、不取消、不修改内层。
这份最小镜像用于识别当前 RBF 的可纠正非 Result guard；其他拒绝仍消费 RBF，不按异常类型、消息或 TailOffset 猜提交相位。已交出的 Span/Memory 无法被方法 guard 撤销，调用方 MUST 遵守其借用有效期，结束租借或 owner fault 后不得继续使用。

正常 End 或健康取消只终结这份 Lease 一次，清除 CurrentLease 和内层 Builder 引用、释放一次计数；旧副本不得再委派到 RBF，不能取消或修改后来的租借。个体 Dispose 在尝试内层取消前先让 Lease 不可再操作；健康取消返回后才恢复文件分配资格。取消异常按共享 fault 处理，当前文件不得重租，重复副本 Dispose 不重试取消，文件资源留给 owner 清理。

## 分配与租借合同

### spec [S-FS-ALLOCATOR-UNORDERED] 普通分配不赋予业务顺序

普通 FrameStore MUST 以独立地址表达帧引用，不承诺分配次序与业务顺序一致。调用方 MUST NOT 用文件编号、offset 或物理相邻关系推断 Parent、版本新旧或当前状态。
每个文件仍顺序追加；不同文件可以交错完成。首版的串行调用约束不建立业务上的全局排序。应用通过帧内引用表达关系，其构建中间帧的申请、完成和物理排列不形成上层发布协议的输入。

### spec [S-FS-LEASE-EXCLUSIVE] 每个 Builder 独占一个文件

FrameStore MUST 在 BeginAppend 成功前租借一个可分配文件，直到正常完成或健康取消后归还。同文件不得同时接受第二个 Builder 或完整 Append；再次申请 MUST 选择其他空闲文件，没有合适文件时才创建新文件，不能隐式提交/取消已有 Builder。
归还后未达到轮转条件的文件可以再次分配，不能把“暂时被占用”当成永久 sealed。可纠正短写、meta 冲突、未提交 reservation 等拒绝按 S1 保留同一个 Builder 和租借；参数错误不借机更换文件或地址。
文件选择按下面的最低编号规则；未归还 Builder 的数量准入由 `[A-FS-BUILDER-LIMIT]` 定义，句柄策略由 `[A-FS-HANDLE-BASELINE]` 定义。不增加精确总资源配额，也不承诺无限嵌套。

### spec [S-FS-END-AUTO-RETURN] 成功提交自动结束租借

EndAppend MUST 在正常成功返回前自动结束相应 Builder 的租借，释放未归还 Builder 配额并登记新的未确认完成输出；调用方无需再调用 Dispose 才能申请新的 Builder。ConfirmDurable 本身不要求租借已归还，遵循 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`。
可纠正短写、meta 冲突、未提交 reservation 等拒绝不归还文件、不释放配额；健康 Dispose/取消归还。成功后 Dispose、Builder 的值副本与旧 Writer 不得二次计数或影响新的租借。
成功完成后超过软阈值的文件必须立即停止新分配，自动归还不意味着重新变为可租借。三种追加在 RBF 正常成功后 MUST 只修改预先存在的 FileEntry/Lease 字段：登记 dirty、按新 TailOffset 派生停止资格、结束对应租借，再以值结果返回地址；仅 Builder 释放其数量配额，完整 Append 不修改 Builder 计数。不新建 dirty 集合项、入队待归档对象、调用外部回调或执行 flush/close/rename。FrameAddress 成功构造/Result 包装不引入新的堆分配或参数失败路径。归档维护统一见 `[S-FS-ARCHIVE-AFTER-FLUSH]`；完成和归还不代替 durable flush。

### spec [A-FS-BUILDER-LIMIT] config 文件限制未归还 Builder 数量

FrameStore MUST 按本条从可选 config 文件或缺失时的默认值取得可调整的未归还 Builder 上限。额度已满时，新的 BeginAppend MUST 在租借/创建文件与输出之前立即确定拒绝，不等待已有 Builder、不抢占、不产生 writer fault。
计数按实际签发的 Builder 租借，不按值副本、Writer 副本或后续 Dispose 次数重复计算。Begin 初始化失败不占用成功签发配额；可纠正提交拒绝保留配额，正常成功 EndAppend 或健康取消释放一次。
对当前返回 Builder 的签名，超限使用 InvalidOperationException，诊断包含有效上限和当前占用数量；不得签发可用 Builder 或提前地址。若底层 Builder 已准备但 owned wrapper 无法签发，必须取消内部准备并归还租借；清理失败遵循共享 fault。已完整初始化并发布到 active 的文件不会因此被盲删，文件事实与 Builder 配额不同。
本条只计对外签发的 Builder。完整 buffer Append MUST NOT 消耗此额度，也不通过 BeginAppend 转写；它消费 RBF 的完整 Append 能力，同步借用输入并完成一个短期独占文件租借。首版单 driver 串行且不允许重入，因此最多额外存在一个这样的租借；设有效上限为 M，用户帧构建/输出的同时租借数最多为 M + 1。Append 仍遵循 owner/模式/fault/参数、最低编号选择与 RBF 容量规则，不等待或结束已有 Builder。
额度满时仍可合法 Append、完成/取消已有 Builder、随机读取已完成前缀及 ConfirmDurable；不保留特殊 Root 槽位，也不使未完成依赖获得发布资格。真正需要同时持有 N 个提前地址的轨迹仍须 N 个 Builder 槽位；完整 Append 不提供提前地址，不能由它推导任意互引图都能在有限槽位内构建。
计数只建立 Builder 数量准入，不承诺总文件数、打开句柄或总内存硬上限，也不维护精确总字节数账本。确定 End guard 与未知委派异常分别遵循 `[A-FS-OWNED-BUILDER]`、`[S-FS-OWNED-FAULT]`，不能把所有 End 失败都当成可纠正拒绝；不增加第二套生命周期。

配置使用 store 根目录的可选 `framestore.config.json`，UTF-8 JSON object，首版只接受大小写精确匹配的 `MaxOutstandingBuilders`：

```json
{ "MaxOutstandingBuilders": 32 }
```

| 输入/入口 | 首版行为 |
| --- | --- |
| 文件不存在，或 object 中省略该属性 | 使用默认 32；空 object 合法 |
| 属性存在 | 必须是 `[1, int.MaxValue]` 内的 JSON 整数；不预分配相应数量的文件、数组或 Builder |
| 0、负数、溢出、小数、字符串、null、非 object、重复/未知属性、非法 JSON | 确定拒绝；不静默回退默认，不接受注释或尾逗号 |
| 权限/I/O 等读取失败 | 传播相应错误；仅文件确实不存在才作为缺失配置 |
| 可写 Create/Open | 在任何数据恢复、创建清理或初始化/追加输出前一次读取并校验；有效值固定于整个 owner 生命周期 |
| 运行期间修改 | 当前 owner 不受影响，下次可写打开生效；不做热更新 |
| OpenReadOnly | 不消费写入准入配置，不因该文件缺失或非法而拒绝只读数据访问 |

Create/Open 不自动补写缺失 config；32 是便于小规模交错构建的可调工程起点，不是测量得出的最优值或资源保证。配置是运行策略，不存 StoreId、active 集合、租借状态或下一编号，不进入不可变 header、数据输出登记或 ConfirmDurable 范围；它存在也不证明 store 已创建。修改配置无需新的持久发布协议。
配置预检不替代 store 独占资格；任何恢复、私有残留清理或布局/门/data 初始化输出前仍须取得 `[S-FS-OWNER-LOCK]` 的独占，唯一门前例外是本条参数/config 与初始输入预检之后的根/控制设施 bootstrap。Create 面对尚不存在的根目录时按缺失 config 处理，不为了读取配置先创建 store；初始根的语义准入消费 `[R-FS-STORE-CREATE]`，实际根/type/no-follow 消费 `[S-FS-ROOT-ADMISSION]`，平台锁消费 `[S-FS-OWNER-LOCK]`。不能把根目录缺失作为 Open 缺失 store 的成功回退。

### spec [A-FS-HANDLE-BASELINE] 首版保留 active 句柄且不建立 reader pool

可写 owner MUST 完成全部 active 的 RBF 恢复与必需 header 检查，成功打开后保留其 owned RBF 句柄，直到归档或 owner Dispose；健康 idle active 不做容量淘汰、超时关闭或按需重开。各数据文件的 RBF 工厂调用 MUST 显式使用 RbfCacheMode.Off，不隐式继承默认的每文件读缓存，也不增加缓存配置或长期 FrameInfo 表。同文件随机读取直接复用这一句柄，Building 时沿用 completed-prefix 资格。
仅只读访问且尚无 owned 句柄的文件，包括 archive 随机读取，按操作打开、检查身份/header、完整物化读结果后关闭，再向调用方移交结果；结果不借用 reader。读取或关闭异常时必须释放所有尚未移交的自有结果，不能因已读成功但关闭失败而泄漏 buffer；成功移交的结果独立于 reader 生命周期。扫描若需该文件句柄，保持到当前文件枚举结束或 Dispose，并在取消/错误时清理；不签发引用已关闭 reader 的延迟结果，不建立历史 reader pool。只读 owner 的目录/编号/header 检查可逐文件关闭，不适用可写 active 全保留策略。
文件结束 Builder 租借不等于文件句柄关闭，也不等于耐久确认。必要完成输出登记继续按 `[A-FS-DURABLE-COMPLETED-OUTPUTS]` 覆盖全部 active/leased；归档仍先 flush 再 close/move。Open 失败须尝试释放此前打开的全部 owned 资源；不能因为文件数多于当前 Builder 上限而跳过文件、删除文件或改变最低编号选择。
资源成本按实际 active 数计算：曾用更高数量上限或旧构建峰值留下的 active 可以多于当前 M，降低 config 不主动归档、删除或忽略它们。在固定配置/阈值、新文件仅无候选时创建且按 `[S-FS-ARCHIVE-AFTER-FLUSH]` 维护的健康运行中，仍可继续追加的 active（含已租出文件）受历史同时租借峰值约束；从空 store 开始的这一峰值最多为 M + 1。停止文件另按该条的维护窗口计数；这个推导不覆盖旧配置遗留，不构成整个 active/OS 句柄集合的硬上限。
（Informative）此基线让租借、历史读取和 dirty 屏障直接复用 active 句柄，避免首版引入闲置淘汰后的再次恢复/确认与关闭异常路径。若实际 active/句柄压力要求淘汰，另与 S2-Q4 的维护边界一起审定；缓存失效不能清除必要输出登记，Dispose 本身也不替代 DurableFlush。

#### 实际资源成本（Informative / Derived）

当前 RBF 已知/未知尺寸 Begin 均不预分配声明长度，而是从 HeadLen reservation 开始；最早 reservation 阻止提交前的帧 bytes 输出。多个 Builder 的实际租用 chunks 相加，GetSpan/GetMemory 的 sizeHint 与池数组容量也影响占用。已知声明只限制实际 Advance/Reserve 消费，不限制借用容量；即使很小的声明也可能先申请很大的空 chunk，不能用 `M × 声明长度` 或 `M × 最大帧长度` 当作内存硬上界。
成功 End 的 Commit 与健康取消 Reset 会把 chunks 归还池；这不保证进程 RSS 立即下降，writer 的增长目标和容器容量也可以跨租借保留。完整 Append 保持输入 borrowed：当前 RBF 小帧使用至多 8 KiB stack buffer，非零转义键的大帧可为每个打开文件保留一个申请尺寸为 1 MiB 的 scratch，直至 File Dispose；实际池数组可更大。显式 Off 消除默认 Slots16 的每文件 64 KiB pages + 8 KiB scratch，不能据此承诺吞吐更优。
因此资源预算应分别报告实际 active/停止分配文件数、活跃 Builder 的实际 chunk 占用、Append scratch、调用方预编码输入及尚未 Dispose 的自有读结果。及时完成/取消、合理 sizeHint 和释放读结果由消费者控制；数量配置不是完整资源账本。以上机制依据当前 [RBF Writer](../../src/Rbf/RbfPayloadWriter.cs)、[chunk 归还](../../src/Data/SinkReservableWriter.cs)、[完整 Append](../../src/Rbf/Internal/RbfAppendImpl.cs) 与[读缓存](../../src/Rbf/ReadCache/ReverseReadCache.cs)源码推导。FrameStore 的实际 public 资源轨迹另见 [R2 测量](02-framestore-consumer-resource-acceptance.md)；其中进程分配、GC 后存活量和 RSS 不是库独占占用，也不能直接分解为池、scratch 与 chunks 的精确账本。

### spec [S-FS-LOWEST-FILE-FIRST] 从可分配 active 文件中选择最低编号

普通 FrameStore 的三种追加入口 MUST 共用这一选择规则：通过参数/owner/资源准入后，从已发布且初始化合法、未租出、未停止分配、TailOffset <= RotationThreshold 的 active 文件中，选择数值 FileId 最小者；没有候选时才创建新文件。
低编号文件仍被租出时 MUST 跳过，不等待它归还、不隐式结束其 Builder。健康归还且仍可分配后，该文件重新按编号参与选择；达到归档条件但尚未完成物理移动的文件不再参与选择。
文件编号按数值比较，不依赖 FS 枚举顺序。是否缓存了打开句柄不改变选择优先级，未缓存的候选可按需打开；不能仅因句柄未打开而改选高编号或新建文件。
选择不依据预计帧长度、预计完成时间或文件剩余软阈值额度，不做轮询/负载均衡，也不声称公平调度或按帧顺序分配。owner 已 fault 或选择后的文件打开/校验失败时按既有错误规则处理，不能静默跳过坏文件继续选择另一份。
（Informative）这个规则倾向先填充较早文件；嵌套租借仍使用其他文件。小型 active 集合可以在内存按编号维护可分配项，索引可从目录重建，不构成全历史分段表或新的持久元数据。

### spec [S-FS-LEASE-SERIAL] 租借与归还串行且可嵌套

文件选择、创建、租借、归还、归档及耐久屏障 MUST 串行执行；首版 FrameStore 及派生对象由调用方串行使用。多个尚未结束的 Builder 不等于同一时刻并行调用。
各 Builder 的文件、一次性共享 Lease 和构建资源 MUST 独立绑定，内层 epoch 由相应 RBF 文件负责；未来不同 Builder 各由一个线程使用是单独扩展，不在首版声明并发安全。EndAppend 自动归还时，设计应区分文件内提交与 owner 登记，避免把整个编码/CRC/EscapeKey/写出过程绑定到一个全局当前 Builder。

### spec [S-FS-RANDOM-READ-COMPLETED-PREFIX] 活跃构建期间可随机读取已完成前缀

FrameStore 的指定地址随机读取 MUST 消费 RBF `[S-RBF-RANDOM-READ-COMPLETED-PREFIX]`，允许在目标文件 Builder 活跃时串行读取此前已完成帧，不另设文件或整个 owner 的 Building 读取禁令。同文件通过现有 owned reader 读取，不开第二个同文件句柄绕过 FileShare.None。
Building 期间，请求必须连同帧后 Fence 完整落在当前已完成前缀内；未完成 Builder 的提前地址和跨越边界的范围先于实际读取拒绝。该范围检查不替代真实帧解析、完整内容校验或地址来源责任，裸数值范围不证明主链成员或原始 store。精确范围与错误分类由 RBF 公共合同定义，本层不复制布局常量；本条不扩大 Idle 随机读取的既有合同。
owner 生命周期、共享 fault、串行调用和读结果所有权 MUST 保持；缓存命中也不能绕过这些检查。底层新扫描、扫描边界和物理后继查询仍遵循 RBF Building guard；其此前已取得的 sequence/枚举器继续按捕获的完成上界及既有串行、生命周期/fault 合同使用，不修改 `[S-RBF-SCAN-IDLE-ONLY]`。FrameStore 不外泄这些 sequence，自有 Inventory/Audit 的同步回调资格由 `[A-FS-INVENTORY-AUDIT]` 定义。

## 候选合同

### spec [F-FS-OWN-FORMAT] 新库有独立格式身份

FrameStore MUST 有自己的格式版本、持久 StoreId 和 create-only 格式门，不复用旧目录身份。目录布局按本层版本解释，active 集合不再另存 locator/manifest/status；格式门不随普通租借和轮转反复更新。
数据文件及其私有数据初始化文件 MUST 是 RBF3；active 和 archive 的实际格式通过 S1 的已打开格式信息检查，不能仅信自家 marker 或文件长度。格式门与 JSON config 是独立控制文件，按各自合同解释，不纳入 FileId、FrameAddress、用户 Inventory/Audit 或普通 data dirty 集合。
Create 为 create-only；Open 不创建缺失 store。格式未知、metadata CRC/字段坏、必要文件缺失明确分类；I/O 保留原异常资格。
完整合法格式门成立前的初始创建残留是 incomplete creation，不宣称已有可打开实例；不得盲目覆盖该目录。只读不修尾、不改定位元数据或派生缓存。

**版本 1 格式门。** 正式相对名称 MUST 为精确小写 `framestore.format`。这是 store 级普通文件中的一条固定记录，不是 RBF 文件；MUST 恰为 24 bytes，显式字段编码如下：

| byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | FrameStoreFormatVersion | uint32 LittleEndian，首版为 1，与数据 header/目录使用同一版本 |
| `[4,20)` | StoreId | 16 个 canonical opaque bytes，非全零 |
| `[20,24)` | CRC32C | 前 20B 的 CRC32C，uint32 LittleEndian；init/finalXor 均为 0xffffffff |

CRC 使用现有 `RollingCrc.SealCodewordForward` / `CheckCodewordForward`，传入完整 24B codeword，消费其标准 CRC32C 和上述参数，不自行实现算法。该 CRC 是普通门记录的唯一完整性校验；数据文件的 24B header payload 仍仅由 RBF 完整 CRC 保护，两者不是同一种记录，门末 4B 不是 FileId。MUST NOT 增加独立 Magic、GateSchemaVersion、flags/预留、FileId、config、编号/目录集合或追加状态。统一版本选择整份布局/schema；未知版本拒绝，不猜旧 marker 或补默认身份。

**共同只读校验。** Open 与 OpenReadOnly MUST 共用一个内部检查流程；在 S2-Q3 的实际名称/普通文件/no-follow 准入资格下，以 `FileMode.Open`、`FileAccess.Read`、`FileShare.Read` 取得临时 FileStream。先在同一句柄上要求 Length 恰为 24，再 `ReadExactly` 到固定 24B 栈 buffer，校验整个 codeword，最后解码版本并检查非零 StoreId。截短、尾随、CRC 坏或字段不合法均拒绝；CRC 不合格时不能认领其中身份。固定长度已提供完整消费，无需额外 EOF 探测、第二个长度查询句柄或 RBF scanner。
版本/身份只暂存在局部值中；取得独立拥有的身份值且临时句柄关闭成功后，才交给后续 owner 初始化。不得外泄 span、stream 或借用 buffer；关闭前先取出/清空资源槽，所有失败按 `[S-FS-OWNED-FAULT]` 的主错误优先/单次清理规则处理。任何检查或必要清理失败 MUST 在 active 恢复、私有清理及新输出前结束，不签发 owner。仅真实缺失作缺门分类，权限/I/O 不折算为不存在；不通过 File.Exists 猜缺失。

正式门 MUST NOT 通过可写 RBF Open、修尾、追加、替换或数据 header 认领来“修复”；缺坏门时不从其他文件取得身份或自动创建。正确记录是打开的必要条件，不独自证明 root 身份/目录资格、独占或旧 Create 已成功返回；FileShare.Read 不替代整个 store 锁。初次建立消费 `[R-FS-STORE-CREATE]`；owner 锁消费 `[S-FS-OWNER-LOCK]`；私有数据残留消费 `[R-FS-CREATION-PRIVATE]`；实际组件大小写/类型入口和根准入消费 `[S-FS-ROOT-ADMISSION]`，平台锁/data rename 按实施记录验证，本条不授权清理未知内容。

（Informative）固定门每次 owner 打开只读一次，没有历史/追加需求；普通定长读取和现成 Data codeword 是本轮更直接的工程默认。单帧 RBF3 承载同一 20B 内容也可正确，但需额外编排格式、首帧/形状、完整帧和唯一后界；当前推导为 56B 文件，未测得 raw 的性能优势。首版只选一个 codec，不提供双模式/fallback 或公开门 API；有实际统一物理工具消费者时再评估格式演化。依据为 [公开 CRC codeword](../../src/Data/Hashing/RollingCrc.cs)及[独立 CRC/损坏测试](../../tests/Data.Tests/Hashing/RollingCrcCodewordTests.cs)，不构成新 FrameStore 实施资格。

### spec [R-FS-STORE-CREATE] 初次创建空 store，最后写完整格式门

**输入与前置。** Create MUST 为 create-only。初始输入只接受尚不存在的根，或经准入确认的空根/仅含合法可选 `framestore.config.json` 的根；不存在根按缺失 config 预检，不为读配置先建 store。已有门（包括短/坏门）、初始化目录、数据、私有残留或其他未知内容 MUST 拒绝，不能仅据缺门认领或自动续作。此处规定初次输入，不建立已创建根的永久白名单；唯一控制设施例外与门前 bootstrap 消费 `[S-FS-OWNER-LOCK]`，合格控制文件可单独存在或伴随合法 config，不凭名称豁免未知项。参数/config 预检先于 bootstrap 输出，获锁后重新确认初始输入；在准备布局及门输出前，MUST 已取得实际根准入与整个 store 独占。实际根、类型/no-follow 消费 `[S-FS-ROOT-ADMISSION]`；平台及完整阶段资格按实施记录判断。

**最小建立顺序。** 在上述资格下，先准备空 active、archive 与本协议所需的私有初始化区域；不预建归档桶、数据文件或持久编号表。为本次创建固定版本 1 和一份随机非零 StoreId，按 `[F-FS-OWN-FORMAT]` 在局部 buffer 中准备完整 24B codeword。然后以 `FileMode.CreateNew`、`FileAccess.Write`、`FileShare.None` 直接创建正式 `framestore.format`，从长度 0 顺序写入完整记录，不先扩长或分次原地补字段；`Flush(flushToDisk: true)`、单次 close 均成功后，才交付持有原独占的空 owner。MUST NOT 在创建中签发数据地址。无需私有门候选、门 rename、门读回阶段、公开 Creating 状态或初始化日志；普通数据文件继续遵循 `[R-FS-CREATION-PRIVATE]`。

**成立事实与成功返回。** 初次创建的可打开事实是必要布局已就绪、正式门具有完整合法内容；后续 Open/OpenReadOnly 仍须通过共同 checked 门流程及其他准入，不能用 File.Exists 或 Write 返回代替资格。正常 Create 返回还要求完整写入、上述 flush/close 和 owner 交付全部成功。完整门可能在 flush/close 返回前已存在；失败或丢失返回值的 Create 因而可能已成立，重开可认领实际门身份，但不能由门推断旧调用成功或曾获得确认。本条只消费 ProcessCrashOnly：进程终止后 OS/FS 仍运行；不以 24B 单次写入原子性或断电后的目录/门耐久作证明。

| 中断后实际事实 | 后续解释 |
| --- | --- |
| 必要布局尚未齐备，或布局已就绪但门未写入 | 没有可打开 store；不自动补布局、续作或盲重试 Create |
| 门为 0–23B 前缀，或完整长度但 CRC/版本/身份不合法 | 按既有门检查拒绝；重复打开仍不修复、不覆盖、不从数据补身份 |
| 布局与门均完整合法，原调用未完成 flush/close/交付或未返回 | 按实际资格可 Open；旧调用没有成功交付证据，不能据异常撤销门 |
| 创建步骤全部成功并正常返回 | 取得空 owner；没有数据文件、用户帧或已签发地址 |

任何门写入、flush、close 或后续交付失败，MUST 不交付可用 owner，按 `[S-FS-OWNED-FAULT]` 释放所有 owned 资源、保留主错误且单次关闭；不得删除正式门或递归删除根/初始化目录作回滚，不自动重新 Create。门不合格或其余准入失败时只拒绝；门合格也不能补造缺失的必要布局。初次残留的显式维护不在普通 Create/Open 内增加 reset/resume 接口。

**空集合与首次追加。** 成功 Create 的 `MaxPublishedFileId=0`，active 台账及 data dirty 集合为空，不持有 RBF 数据句柄。空 store 可关闭后按同一 StoreId 重开；ConfirmDurable 无数据输出，Inventory/Audit 成功计数为 0，不为这些操作预建文件。首个通过既定准入、确实需要文件的 Append/Begin 才按私有数据初始化 → active 发布 → max 登记创建 FileId=1；参数/配额拒绝不建文件，随后取消也不撤销已经正式发布的编号。

（Informative）私有普通门 flush/close 后 rename，或整根 staging 后 rename，也可设计正确；当前没有“正式门出现必已完成旧调用 flush/close”、根原子出现或失败零残留的消费者要求。直接写唯一 create-only 门复用既定完整检查，省去私有门与它的发布/残留协议。初次写入/清理的两平台中断与资源证据随 S2 实施取得；数据创建、归档所需的同文件系统 no-overwrite rename 资格不由本条代替。

### spec [A-FS-CONTEXT-ADDRESS] 地址与上下文资格明确

地址 MUST 明确非法值、segment/ticket 范围、编码、相等和默认规则；owned Builder 绑定 owner 与一次性共享租借，底层 epoch 留在 RBF 内部。
来自成功追加、真实主链扫描或已接受上层事实的地址具有明确来源；任意合法数值及一次随机 CRC 读取不自动证明其 intended identity。
只有中性定位信息进入 FrameAddress；Parent、graph schema、提交关系等在上层记录中。错误 owner 或旧租借的 Builder/Writer/read handle 在访问文件前拒绝。

### spec [R-FS-OPEN-RECOVERS] 可写打开接受 RBF 恢复

可写打开 MUST 枚举 active 的规范文件，按 `[F-FS-META-FIRST]` 先检查完整初始化的长度下界，再让 RBF 分别处理每个文件的单尾恢复并校验首帧；报告按 FileId 关联供诊断，Action 不驱动业务回滚或发布。所有需恢复的 active 文件成功打开并完成本层初始化检查后，才能向外签发新 Builder。
Open 不重复读取整个尾 payload 取得结构资格；业务 metadata 作事实前仍 checked-read。archive 历史只读严格访问，残尾不被自动改写。恢复后的 TailOffset 大于当前阈值时，文件不可再租借，按归档协议处理。
所有 active 恢复/header 检查及目录准入通过后，可写 Open MUST 执行 `[S-FS-ARCHIVE-AFTER-FLUSH]` 的统一归档维护，再签发 owner；任何维护或清理失败都不签发可用实例。正式路径与编号按下文已定合同，owner 独占消费 `[S-FS-OWNER-LOCK]`；私有残留消费 `[R-FS-CREATION-PRIVATE]`，实际根准入消费 `[S-FS-ROOT-ADMISSION]`。
正常 Open 核对格式门、私有创建槽位、active 集合及 `[S-FS-DIRECTORY-STATES]` 的完整正式名称发现/编号恢复；按需读取报告已知地址的缺失/坏内容，Inventory/Audit 覆盖实际发现的正式集合及其中已观察的错误，不声称发现不可观测的整文件丢失。枚举目录名不等于审计历史帧，不宣称已验证全部历史文件内容。只读复用正式名称发现与冲突检查，但不恢复、不移档、不清理创建残留。

### spec [S-FS-SOFT-THRESHOLD] 完成后大于阈值才停止追加

三种追加 MUST 共用事后检查：成功完成帧并取得新的 TailOffset 后，只有 TailOffset > RotationThreshold 才停止该文件的新分配并进入归档；等于阈值仍可追加。未知尺寸与已知尺寸不使用不同轮转算法。
正常取消和可纠正拒绝不推进 TailOffset，不据此触发尺寸轮转。成功完成的合法 RBF 帧不得因使文件超过目标阈值被拒绝、改地址或挪到另一文件。
阈值不保证单文件尺寸不超过该值。合法范围 MUST 为初始化边界 I <= RotationThreshold <= SizedPtr.MaxOffset；I 由 `[F-FS-META-FIRST]` 的公共尺寸/边界定义，不复制当前初始化长度。阈值是 long 字节比较值，不是 ticket 起点，MUST NOT 增加 4B 对齐要求或向上/向下取整。既有大文件不为新阈值拆分或重写。

**唯一运行输入。** 可写 Create/Open MUST 接受同一个可选 `long rotationThresholdBytes` 参数，首版默认值为 `64L * 1024 * 1024 * 1024`（64GiB）。在任何文件系统访问、恢复、私有清理或初始化输出前检查上述范围；非法参数抛 ArgumentOutOfRangeException，不创建或修改 store。有效值捕获为实例的 RotationThreshold，整个 owner 生命周期固定，不在普通追加/确认时重读或热修改。OpenReadOnly 不提供此参数，也不消费写入阈值策略。
首版只选这个输入：不新增 Options 类、公开 setter、config 字段或参数/config 优先级；`framestore.config.json` 仍仅接受 `[A-FS-BUILDER-LIMIT]` 的 MaxOutstandingBuilders。阈值不写入格式门、文件 header 或每文件状态，不改变格式版本或地址编码。

**重开与变更。** 每次可写 Open MUST 用本次有效阈值和各 active 的恢复后完成 TailOffset 重新派生停止资格，不能使用恢复前的原文件长度、预约量或丢失的旧阈值/停止位。所有正式目录、私有残留及 active 恢复/header 资格通过后，按 `[S-FS-ARCHIVE-AFTER-FLUSH]` 排空停止文件，再签发 owner；失败不签发实例，不跳过坏文件。

| 重开后的实际位置与完成 tail | 本次阈值的作用 |
| --- | --- |
| active，TailOffset > RotationThreshold | 停止分配；在 Open 返回前 fresh flush/close/归档 |
| active，TailOffset <= RotationThreshold | 仍可按最低 FileId 规则分配，包括合法 header-only 文件 |
| 上轮已超过旧阈值，但尚未移档的 active | 提高阈值后可以重新成为可分配项；旧内存停止位不是持久事实 |
| archive | 始终只读；提高阈值不移回 active 或重新追加 |

阈值变化不撤销已完成/确认事实，不改变 FileId/ticket，不拆分、重写或删除文件。OpenReadOnly 不恢复或归档，不因 active 超过某个写入阈值而拒绝其只读资格。
（Informative）固定阈值下，新文件只在未超过阈值时开始下一帧，因此正常尺寸超出量最多是一次最大合法追加占用；用 RBF 公共 MeasureWriteSize 计算，不复制 wire 常量。物理长度查询可用 TailOffset，不要求每帧额外 FileInfo/stat。
（Informative）单参数让尚不存在的根也可直接选取策略，并以小阈值执行同一 public 合同的轮转/重开验收；它是工程可验证性选择，当前没有新栈业务消费者证明可调必须公有。固定常量或唯一 config 字段也能正确，首版不同时提供。64GiB 是可调工程起点，不是测量出的最优值或资源/磁盘上限；若工作集与目录成本要求不同目标，调用方传入其他合法值。起点/末端资格依据 [RBF 公共合同](../Rbf/rbf-interface.md)、[最大起点 Builder 向量](../../tests/Rbf.Tests/Internal/RbfFrameBuilderTests.cs)及[最大起点 Append 向量](../../tests/Rbf.Tests/Internal/RbfFacadeTests.cs)，不是 FrameStore 实施或性能资格。

### spec [F-FS-BUCKETED-PATHS] 归档路径由编号固定计算

首版归档布局 MUST 固定 BucketShift=10：bucket = FileId >> 10，slot = FileId & 1023。每桶覆盖 1024 个编号，与帧大小、完成顺序和内存分段表无关。FileId=0 非法不意味着其他桶的 slot=0 非法。

**版本 1 的正式相对路径。** 令 F 为非零完整 uint FileId 的恰好 8 个小写 ASCII hex 字符，B 为 bucket 的恰好 6 个小写 ASCII hex 字符；以下组件相对于调用方选定的 store 根目录：

| 位置 | canonical 组件序列 |
| --- | --- |
| active | `active/<F>.rbf` |
| archive | `archive/<B>/<F>.rbf` |

固定组件为精确小写 `active`、`archive`、`.rbf`；hex 字符只允许 `[0-9a-f]`，数值高位在前，与 FrameAddress/header 的二进制 LittleEndian 无关。B 的数值 MUST 在 `[0, 0x3fffff]`，空桶同样校验；6 个 hex 字符本身不证明落在 FileId 高 22 bits 的范围。归档文件的 B MUST 等于从其完整 F 解码的 FileId >> 10；slot 仅由该编号派生，不另写 slot 文件名或字段。桶大小、组件与编码属于统一格式版本，不随实例配置变化，不新增路径专用版本或 layout 选项。
实现 MUST 共用一个内部文件名生成/checked 解析 codec，用于两种位置的生成、正式集合发现、随机定位和移档；不增加公开 path API。检查 MUST 以实际枚举项的组件名为输入，按字符/宽度/范围校验并与按值回编的组件 Ordinal 精确比较，不先 trim、case-fold 或规范化再认领。大小写变体、少/多前导零、符号/`0x`、非 ASCII 数字、尾部空格/点、错后缀、FileId=0、错桶和额外层级均拒绝。示例中的 `/` 表示组件分隔，实际用平台路径组合入口连接；不持久化 OS 分隔符或调用方根路径。

**正式子树语法。** 目录发现 MUST 检查这些受管层级的全部直接目录项，包括隐藏项；不能用 `*.rbf`、仅文件/仅目录枚举或忽略不可访问项的选项预先过滤。active 的直接项只允许上述正式文件；archive 的直接项只允许合法 B 桶目录；每个桶的直接项只允许 B/F 对应的正式文件。未知名、错误类型或多余子目录立即拒绝，不递归寻找其中的合法后代，不忽略备份/临时后缀，也不修名、删除或迁移。
这些受管目录及桶必须取得普通目录资格，叶项必须取得普通文件资格；不跟随其中的符号链接/junction/reparse，不把设备、FIFO 等特殊项计作正式文件。词法成功不提供类型或同文件系统证明。两平台实际类型/no-follow 检查入口、成本及拒绝向量仍须 S2-Q3 实施验证，不由本条指定系统调用或宣称无额外 metadata I/O。
本条以目录准入已取得实际固定组件 `active`/`archive` 的精确拼写及目录资格为前提；大小写宽容的路径查找成功不能替代实际名称资格。取得该资格消费 `[S-FS-ROOT-ADMISSION]`；本条不规定 store 根其他项的白名单、私有槽/锁文件命名，或调用方根路径/祖先/硬链接的物理别名识别，不新增全树安全扫描或每次操作重查根目录。

| FileId（hex 数值） | active 相对路径 | archive 相对路径 |
| --- | --- | --- |
| `00000001` | `active/00000001.rbf` | `archive/000000/00000001.rbf` |
| `000003ff` | `active/000003ff.rbf` | `archive/000000/000003ff.rbf` |
| `00000400` | `active/00000400.rbf` | `archive/000001/00000400.rbf` |
| `89abcdef` | `active/89abcdef.rbf` | `archive/226af3/89abcdef.rbf` |
| `ffffffff` | `active/ffffffff.rbf` | `archive/3fffff/ffffffff.rbf` |

（Informative）archive 用完整 F 是复用一个 leaf codec、移档保持 basename 的工程选择。改用 3 位 slot 也可正确定位，但需另一套 leaf 解析/编号重组，且当前没有名称成本预算支持每文件省 5 个字符；不作为首版布局选项。旧 SegmentStore 的 6/8 位 hex 只提供实现参考，不产生旧布局兼容、连续编号或已验证新栈的义务。
随机读取 MUST 按编号定位 archive 或 active，不依赖全历史分段表；只有路径确实不存在才尝试另一个允许位置，不把权限、I/O、格式或 CRC 错误当作缺失回退。

### spec [S-FS-DIRECTORY-STATES] 目录是文件生命周期事实

creating 是未发布的私有初始化槽位；active 是可能继续追加且重开时需检查/恢复的集合；archive 是已按本协议 flush 后移入的只读文件。当前“已租出”只保存在内存，不写状态帧或 status 文件。
规范文件不得同编号同时出现在 active 与 archive；发现时 MUST 报协议矛盾，不能任选、覆盖或删一份掩盖问题。所需文件不存在时明确缺失，不寻找其他编号替代。正常 Open 不承担发现所有未知历史文件的全库 audit。
**编号恢复。** FileId 首版采用递增非零 uint，已完成帧及其文件不删除。首版正式目录发现 MUST 共用完整流式名称枚举/校验器，供 Open/OpenReadOnly 和 Inventory/Audit 的集合发现消费；只共用名称发现，不在物理检查中再次执行可写 Open 的恢复/归档。不新增持久 next counter、全历史 FileId 表或 Fast/Full 打开模式。编号状态只保留一个内存 `uint MaxPublishedFileId`；以下打开过程取得名称/编号资格，初次 Create 的空集合与门建立步骤消费 `[R-FS-STORE-CREATE]`：

1. 格式门、必要目录与目录准入资格成立；可写打开还须先取得整个 store 的独占。完整枚举 active 的规范项，建立本来就需要的 active 台账，并按数值取最大 FileId。
2. 完整枚举 archive 的全部桶与其中正式项。消费本阶段路径 codec 校验非零 FileId、唯一 canonical 路径/层级及 bucket/slot 对应；不按文件系统枚举次序推断大小。archive 的 ID 若已在 active 台账中，立即拒绝双位置，不挑副本。未知/非规范正式项和枚举/权限/I/O 错误不得跳过或折算为不存在。
3. 取两个正式集合实际文件的最大编号；空桶不贡献编号，不以桶边界推算最大值。只有全部名称枚举及必要枚举资源清理成功后才取得这份资格；可写 Open 中的 active 恢复及归档 MUST 在正式名称检查通过之后进行。
4. 私有槽位还须通过 `[R-FS-CREATION-PRIVATE]` 的残留裁决，取得资格前不恢复 active。可写 Open 按既定合同恢复全部 active 并检查 header，之后才 DrainStopped 并签发 owner；只读按既定只读 header 合同检查，不恢复、不归档、不清理残留。creating 不计入正式 max，也不从其中 header 认领已发布编号；未裁决残留不得直接忽略、删除或复用其槽位。

canonical 路径 codec 按 `[F-FS-BUCKETED-PATHS]` 为每个 FileId 在 active、archive 分别给出唯一位置并拒绝等值别名/错桶，桶名本身也校验规范编码与可表示范围，包括空桶。由此 archive 内不需要额外历史 ID 集合，跨集合重复用已有 active 台账检测。仅按正式名称发现文件便占用其编号，包括 header-only 文件；archive 不为恢复 max 额外打开或校验 header/payload，坏内容不能被当作空桶或腾出的编号，其内容/身份资格仍由按需 checked-read 与 Inventory/Audit 取得。active 的必需 header 错误仍阻止 Open。
owner 初始化取得的目录资格在正常运行中由已定发布/归档协议维护；Append、Begin、Confirm 不重新全库枚举。Inventory/Audit 仍按自身合同发现完整实际集合，不把正常写入的内存台账代替全库检查。

**发号与耗尽。** 通过格式门、必要目录与残留资格后，实际正式集合为空时 `MaxPublishedFileId=0`，下次真正新建取 1；Create 按 `[R-FS-STORE-CREATE]` 不预建首文件；缺门/缺目录/坏文件或枚举失败不能被视为空 store。允许实际编号缺口，但不证明缺口从未使用，也不填补缺口；下一候选只能是实际最大值加一。
合法写请求仍优先复用当前最低可分配 active。owner/模式/参数及 Begin 数量 guard 通过后，若 max 为 uint.MaxValue 且用既有可分配谓词已能证明没有候选，MUST 在 DrainStopped、新文件创建和地址签发前确定拒绝，不 fault；维护只移走停止项，不会产生候选。拒绝不声明所有未维护文件健康。有可分配 active 时仍正常维护并复用；Open、读取、已有 Builder 完成/取消与 ConfirmDurable 不因编号耗尽整体失效。不在 Open 无条件计算 next；仅需新文件且 max 小于 uint.MaxValue 时计算 checked(max + 1)，不回绕、不缩短编号、不因目标冲突循环试更高号。
新文件完成私有初始化并正常 rename 到 active 后，MUST 在签发任何用户 Builder/地址前把 max 上调到这个已发布 FileId；不等首个用户帧 End。后续 End、取消、归档或从 active 台账移除均不降低/再次递增 max。发布、登记或打开失败按 shared fault 停止旧实例，rename 结果不明也不得继续发号；重开只按实际正式位置重算。同编号只在 active 或 archive 任一处时计一次，两处都有拒绝；空桶或未发布私有 header 不补造发布事实。归档只改变位置，正式 ID union 与 max 不变。

**成本与资格（Informative / Derived）。** 设 A 为 active 文件数、B 为 archive 桶数、H 为 archive 文件数，正常名称发现工作为 O(A+B+H) 目录项，保留 O(A) 的既有 active 台账、一个 max 及当前目录枚举状态，不排序/积累全部桶或历史文件。active 恢复/header、异常尾及实际路径入口成本另计；完整名称检查不证明 archive header/帧 CRC 健康，也无法从剩余集合证明一个已整体消失、没有已知引用的历史文件曾存在。最大编号文件若被外部删除，目录 max 无法见证旧 highwater；外部删除不属于本轮正常协议/ProcessCrashOnly，不增加计数器来承诺该能力。
完整名称扫描是减少桶排序、空桶回退和逐 active 目标探测的首版工程选择，不是编号安全唯一可行的算法，也不是测量得出的性能优选。若实际冷打开目录成本超出预算，再研究最高实际非空桶定位等窄机制，并明确其名称检查资格变化；不预设历史规模无关 Open、配置双模式或新的持久索引。

### spec [R-FS-CREATION-PRIVATE] 初始化完整后才发布文件

新文件 MUST create-only 写入私有 creating 槽位，完成本层初始化并 flush/close，再同文件系统、不覆盖地 rename 到 active；成功登记后才能打开供追加、签发 Builder/提前地址。目录管理串行，因此每次至多一个未发布创建槽位。creating 不接受用户帧、不签发地址，也不贡献正式 max；取消未发布初始化不消耗编号，发布到 active 后的编号则不得撤销。

**私有语法与独立候选。** 格式版本 1 使用根内精确小写普通目录 `creating`，其全部直接项只允许空集合或一个 `<F>.rbf` 普通文件；F 共用正式文件的 8 位小写非零 FileId codec。必须完整枚举，包括隐藏项；未知/多项、目录/特殊项/链接、错误拼写或枚举失败均拒绝，不递归寻找合法内容、不筛掉未知项。creating 是 Create 准备的必要布局，Open 缺失时不补建；实际普通类型/no-follow 和根准入仍按 S2-Q3 实施。

只有完整正式名称发现、双位置冲突与枚举资源清理通过后，才由 MaxPublishedFileId 推出私有期望：空槽不需要 next；存在文件时 max MUST 小于 uint.MaxValue，候选号为 checked(max+1)，文件名 MUST 恰为该号。错号或已耗尽时有私有文件属于协议矛盾，保留拒绝；不从残留 header 认领身份/水位，也不另开持久编号账本。预期 header 由已校验门的版本/StoreId 和这个独立候选号编码。

这消费现有单槽/串行/单点 rename：creating 期间正式 union/max 不变；发布成功后源消失、正式目标占号；rename 结果不明即停用旧 owner，不能再发号。因而正常未发布残留的号必为当前 max+1。该论证不覆盖外部复制、删除、改写或断电，不把目录 max 称为不可伪造历史证明。

**字节资格。** 在 owner 锁、完整格式门、根/目录/普通类型资格及上述名称条件下，以同一只读普通句柄取长度并读取全部实存 bytes。令 I 为 `[F-FS-META-FIRST]` 的初始化后界；长度 MUST 在 0..I，超过 I 先拒绝，不读取任意大文件或仅检查其 header。对实际长度的 bytes 调用 [RBF 有界初始帧前缀核验](../Rbf/rbf-initial-frame-prefix.md)，传入 Tag=0 和上述预期 24B payload。只有全部 bytes 兼容某个合法 Key 下的完整预期 RBF3 初始化 codeword 的前缀，且必要只读句柄单次关闭成功，才取得残留资格。

0B、0–3B HeaderFence 前缀、裸完整 HeaderFence、部分 header 帧以及完整 header-only 初始化均可取得该范围资格；空/短前缀没有尚未出现的 StoreId/FileId/CRC 证明。谓词只证明前缀兼容，不认证历史作者；清理资格来自本条全部前提，不能仅凭文件名、长度、合法 header、CRC 或谓词 true 删除任意文件。任何不兼容 bytes、完整坏身份/形状/CRC、用户或未知 suffix 都保留并拒绝，不修复、覆盖或补发布。

**打开与取消。** 正式名称及全部私有项/bytes/必要关闭检查 MUST 在任何 active 可写 RBF 恢复、私有删除或新输出之前完成。私有文件不得交给可写 RbfFile.OpenExisting 修尾后再认领，也不能用严格 RO 工厂拒绝残尾代替前缀核验。

| 入口 / 事实 | 行为 |
| --- | --- |
| creating 合格且为空 | 两种打开正常接续正式集合，不因空槽计算耗尽 next |
| 唯一文件取得全部残留资格，可写 Open | 在持续独占下仅删除这个文件，保留 creating 目录；不恢复或补发布，再检查/恢复 active |
| 同样合格的文件，OpenReadOnly | 原样保留，只接续正式集合；不计 max、签发地址或公开该文件 |
| 任何私有项不合格或必要读取/关闭/删除失败 | 不交付 owner；内容不合格时不删除，I/O/权限/清理错误传播，不折算为空或成功 |

删除中断后的事实只有原文件仍在或槽已空；下次从相同门、正式集合和实存 bytes 重新裁决，不记取消 receipt，也不循环重试删除。所有失败/关闭仍消费 `[S-FS-OWNED-FAULT]` 的主错误优先/单次清理，最后释放 store 锁；不递归删除目录、盲删目标或在失败旧 owner 内重建/续跑。普通创建中断也保留残留交下次 Open 裁决，不能因为初始化尚未签地址就跳过内容资格回滚。没有完整格式门的初次 store 残留仍按 `[R-FS-STORE-CREATE]` 拒绝，不能用本条续作初始化树。

**底座与证据。** FrameStore 使用现已实施的 RBF 公共纯前缀入口取得上述未修改前缀资格；底座复用 RBF layout/footer/CRC 与 Data XOR，不在 FrameStore 复制 wire parser/encoder，也不另写 scratch RBF/marker/日志。[探针](../../experiments/PrivateInitializationPrefixProbe/README.md)已用内部 core 和真实部分输出验证可行性，但探针自身不是公面或完整 FrameStore 验收。当前 public 入口与目录闭环证据见实施记录；进程中断、资源/规模及完整 S2 资格分别取得；S1 的既有 Accepted 资格不变。

### spec [S-FS-ARCHIVE-AFTER-FLUSH] 先确认内容再归档

归档 MUST 在目标文件没有活跃租借时按停止分配 → DurableFlush 返回 → 关闭 writer → 同文件系统、不覆盖的 rename 到计算出的 archive 路径执行。其他文件仍有活跃 Builder 不阻断该文件归档。桶目录可以提前创建，空桶本身不表示某文件已完成归档。
写句柄采用现有 RBF FileShare.None，先关闭再移动，不为轮转擅自改变 RBF 共享规则。active/archive/creating 必须处于同一文件系统；不接受跨卷复制删除作为本协议的 rename。

**唯一维护入口。** 首版以内部 DrainStopped 复用上述协议，按 FileId 升序处理全部 idle 且停止分配的 active；停止资格由已完成 TailOffset 与实例阈值派生，不持久化、不增加待归档日志或队列。每次归档均 fresh DurableFlush，成功后清除该文件的 dirty 登记，再 close/rename；不因此前曾确认而省略该归档调用的 flush。

| 入口 | 首版维护时机 |
| --- | --- |
| Append / 两种 BeginAppend | owner/模式/参数、Begin 数量及上述确定编号耗尽前检通过之后，选择或创建本次用户帧文件之前，先排空停止文件 |
| ConfirmDurable | owner/模式准入后，先排空停止文件，再确认其余 dirty active（包括 leased）；归档已 flush 的文件不在本次重复 flush |
| 可写 Open | 所有 active 的恢复/header 与目录准入通过后，签发 owner 之前排空；余下 active 仍登记为首次待确认 |
| EndAppend / 个体取消 / 普通 Read / OpenReadOnly / owner Dispose | 不运行归档维护；End 成功先正常返回完成事实，Dispose 只清理资源 |

确定参数/满额度拒绝 MUST 在维护之前结束；不能为一个本应立即拒绝的 Begin 先 flush/rename 或使 owner fault。任何 DrainStopped 失败 MUST 停用整个 owner、传播原异常并停止后续分配/确认；不跳过坏文件继续新建，不在同一实例内恢复、回滚 rename 或盲重试。已关闭但移动结果未确认的文件仍由实际目录位置在重开时裁决，不能从丢失的进程内状态推断其位置。
归档关闭与 owner 清理均在调用 public Dispose 前从 owned handle 槽位取出该句柄并标记已尝试；无论 close 是否抛错都不再次调用它的 Dispose。FileEntry 的目录事实保持可诊断，其他 owned 资源仍须尝试释放。

（Informative / Derived）下一次合法分配必先排空，持续写入不会使 stopped 文件随帧数无限累积。若不再分配，只能已有至多 M 个 Builder 的 End 和最后一次同步 Append 留下新停止项，因此一次维护窗口可暂存至多 M + 1 个；Open 负责先处理旧高峰和中断残留。这个界不限制未超阈值的旧 active，也不是总句柄/内存硬保证。仅在 Confirm 才维护会让不调用屏障的独立消费者持续增长；End 内维护则需要额外表达帧已完成后的维护失败。本方案不增加 public Maintain、后台 worker、维护 receipt 或调用方调度责任；实际需求出现后再评估这些扩展。

进程终止、OS/FS 仍运行是本轮故障模型；具体 Windows/Linux 原子 rename 入口仍需 S2-Q3 的实现与实证，不以方法名 File.Move 当作平台资格，也不宣称断电目录项耐久。

| 中断后实际位置 | 本层解释 |
| --- | --- |
| 私有 creating 残留，没有已发布文件 | 按已证明的初始化残留范围处理；无用户地址资格 |
| active 中合法已初始化文件 | 逐文件 RBF 恢复；重新检查阈值，必要时重新 flush/归档 |
| 文件已关闭但仍在 active | 按 active 处理，不依赖丢失的进程内 sealed 标志 |
| 文件只在计算出的 archive 位置 | 归档已发生，按只读访问；前置 flush 由正常协议保证 |
| 同编号两处都有，或所需文件两处都无 | 协议矛盾或文件缺失，拒绝；不补造、不挑副本 |

### spec [F-FS-META-FIRST] 首帧必须是文件 meta/header

每个新文件 MUST 把第一个 RBF frame 用作 FrameStore 自有 meta/header，至少提供版本解释能力。MUST 在 creating 中完成该帧、取得完整 CRC 资格并完成初始化 flush/close，再发布到 active；裸 RBF Header Fence 不足以成为已初始化 FrameStore 文件。
header 保持标准 RBF3 帧，RBF 不感知其业务类型。初始化完整的 active/archive 中缺少、损坏或无法解释必需 header 是错误，不自动补造，也不把它当成可丢弃的普通用户残尾接纳。

**首版记录。** header MUST 使用 Tag=0、TailMetaLength=0、IsTombstone=false；RBF 解码后的 payload MUST 恰为 24 bytes：

| payload byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | FrameStoreFormatVersion | uint32 LittleEndian，首版为 1，须与格式门的版本相等 |
| `[4,20)` | StoreId | 16 个 canonical opaque bytes，非全零，须与格式门逐字节相等 |
| `[20,24)` | FileId | 完整 uint32 LittleEndian，非零，须与本文件规范路径所表达的编号相等 |

检查以 owner 已确定的期望版本、StoreId、FileId 为输入：Open 从已校验格式门和正式规范路径取得；新数据文件创建从已成立 owner 的格式门固定值及预定目标 FileId 取得，不要求 creating 已有正式数据路径。随后发布的 active 目标路径 MUST 使用同一 FileId；MUST NOT 从被检查的 header 反向认领期望身份。表中的格式门/路径比较在创建时对应这些拟发布值。

版本统一解释 store 布局及文件 header，不另立 HeaderSchemaVersion。按字段显式编码，精确消费 24B，不使用 struct dump、varint 或文本身份。此固定记录可用 BCL 的字段读写实现，不要求通用 schema/Tagged Value 框架。RBF 完整 CRC 已覆盖 header 内容和 tag，不增加 payload magic、额外 CRC、预留字段、业务 schema、发布序号、时间戳、索引或可变状态。未知版本、错身份、错形状及内容损坏均拒绝；不猜旧格式或默认身份。header 不替代格式门，也不检测裸 FrameAddress 的原始来源错误。

**初始化边界。** 令 `H = RbfFile.MeasureWriteSize(24, 0)`，`I = RbfScanBoundary.Empty.EndExclusive + H.AppendLength`。合法 header 的 Ticket.Offset MUST 等于 Empty.EndExclusive，Ticket.Length MUST 等于 H.FrameLength；其完整后界由 `GetPhysicalOffsetImmediatelyAfter(ticket)` 或 checked `GetScanBoundaryAfter(ticket).EndExclusive` 取得，并须等于 I。实现 MUST 消费这些公共 API，不复制 RBF wire 开销。当前 RBF3 推导为 52B frame ticket、60B 初始化文件；该数字是格式推导，不是测量结果。仅含完整 header、没有用户帧的文件合法，首个用户帧从 I 开始；只含底层 HeaderFence 的文件不合法。

**共同首帧检查。** 在 Idle 的 owned RBF 句柄上先要求 Format=Rbf3；调用 `ScanForward(showTombstone: true)`，只取第一个物理帧。MoveNext 返回 false 时先传播 TerminationError，没有错误才报缺少 header；不得跳过墓碑、寻找后续同 tag 帧或从 EOF 猜 header。先核对首位置、H.FrameLength、payload 24B、meta 0、tag 0 和非墓碑，拒绝错形状而不按其长度申请大 buffer；再以该真实 ticket 执行完整 `ReadFrame` 或 `info.ReadFrame`，取得完整 CRC 资格后解码并核对版本/StoreId/FileId。正常 Open 的这一步不遍历用户历史；扫描结构资格或随机 CRC 读取单独不能替代首位置与身份检查。

**创建顺序。** `CreateNew(creating/<F>.rbf, Off) → Append(0, encodedHeader, noTailMeta) → 共同首帧检查 → GetScanBoundaryAfter(headerTicket) → DurableFlush → close → 不覆盖 rename 到 active`。Append 返回 ticket 必须等于首帧检查所得 ticket，TailOffset 与 checked boundary 必须均为 I；creating 中不允许后续用户帧。只有完成这条初始化协议并发布、登记后的文件才能供普通追加使用。失败不签发用户地址，残留资格消费 `[R-FS-CREATION-PRIVATE]`，不因 CRC 合格自动认领或补发布。

**可写打开顺序。** 在 store 独占及已知格式门资格成立后、任何可写 `RbfFile.OpenExisting` 前，MUST 用公共文件长度入口检查该 active 的实际长度 >= I；过短直接拒绝且不交给 RBF 修复。只读长度查询须在进入 RBF 的 FileShare.None 工厂前关闭；该预检不解析 RBF wire，也不替代独占资格，I/O/权限/缺失错误保留。随后调用 `OpenExisting(path, out recovery, Off)`，再做共同首帧检查。非 None 恢复若 AffectedFrameOffset 缺失或小于 I，MUST 拒绝；合法用户残尾从 I 或更后位置开始。所有 active 通过后才签发 owner/Builder；失败按 owned 资源合同清理已打开句柄，不补造 header 或身份。

（Informative / Derived）长度前检有必要：正式文件中完整 header body 缺 Key/Fence 时，RBF 可以 CompletedTail。只在恢复后拒绝会留下已修好的文件，下一次 Action=None 就可能接纳；正常 v1 header 的任意未完成前缀都短于 I，前检直接阻断这一窗口。RBF 补尾不改变 HeadLen/body/descriptor，因此错长度、meta 或 payload 不能被补成合法固定 header。该论证消费当前正常 marker-free 顺序前缀模型；不新增任意镜像修复能力。

公共 RBF 工厂先结构恢复，再返回可用于内容读取的对象；带合法残尾的 active 不能先用 OpenReadOnlyExisting 检验 header。因此上述顺序不承诺任何 header 拒绝前都没有物理修尾：长度足够但版本/身份/CRC 错的 active，可能先发生其用户尾结构恢复，再被拒绝。不会据此签发 owner、补造首帧或回退旧身份，不增加 raw parser、prefix-open API 或回滚恢复动作。只读/归档用 `OpenReadOnlyExisting(path, Off)` 后执行同一首帧检查；残尾由只读 RBF 拒绝，不恢复、不清理。

**用户范围与展示。** header 仅在上述首物理位置具有系统意义；普通 Append/EndAppend MUST 继续接受整个 uint tag 值域，包括 0。普通追加和用户 Inventory 不签发 header 的 FrameAddress；Inventory 只省略已校验首 ticket，不能按 tag 过滤后续用户帧。Audit MUST 校验并以 FrameFileAudit 的文件元信息报告 header，不能把坏 header 当空用户集合。原生 RBF 扫描照常包含这份标准帧，不改变底座过滤规则，不增加 showHeader 开关。ReadFrame 保持中性随机读取合同，不增加针对自行编码 header 地址的特殊拒绝；数值 codec 与 CRC 成功仍不赋予用户来源或发布资格。

本条的公共调用顺序依据当前 [RBF 工厂](../../src/Rbf/RbfFile.cs)、[首位置扫描](../../src/Rbf/RbfForwardEnumerator.cs)及[结构恢复](../../src/Rbf/Internal/RbfTailRecovery.cs)；不是 FrameStore 已实施的证明。

### spec [S-FS-OWNED-FAULT] Owned 操作共享 fault

owned append、最终 Builder 提交、flush、归档和 metadata 输出 MUST 共享 owner fault。已识别的参数/state guard、满额度拒绝、End 的普通 borrow 前检及可纠正 Result 不 fault。
在上述确定 guards 之外，实际委派 RBF EndAppend/Append 时抛出的异常 MUST 保守停用整个 FrameStore；End 的当前 Lease 终结且不再签发完成资格，原异常传播，所有其他 Builder/Writer 与缓存访问也在接触 owned 状态前拒绝。取消资源异常、flush/close/rename 失败同样停用实例。不得依据异常类型/消息或尚未推进的 TailOffset 推断没有输出、继续使用或自动重复追加；文件资源仍须受控清理。
这明确收窄 FrameStore 的继续使用资格：底层 RBF 的纯准备异常可能保持健康或已经取消 Builder，公共异常表面却不提供相位/fault 查询；本层不猜相位、不改 RBF/S1，也不声称底层必然已经 fault。Begin 初始化失败没有成功签发的租借/额度；其包装预分配与内部清理继续遵循 `[A-FS-BUILDER-LIMIT]`，不把确定 Begin 拒绝算作未知最终输出。

正常完成后归档在另一调用发生，维护异常不撤销此前 End/Append 成功、此前成功屏障或已经存在的完整 bytes。ConfirmDurable 失败只表示本次没有整体成功确认，不能推出所有输出均未耐久；不新增 completed-but-maintenance-failed Result、尝试身份或业务 PublicationOutcome。后者属于发布层，耐久范围由下条唯一定义。
当前 [RBF 提交源码](../../src/Rbf/Internal/RbfFileImpl.cs)及[故障测试](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs)已有决定性反例：完整 frame/Fence 写出后的池归还异常使 End 抛错而 TailOffset 仍旧，重开 Action=None 后帧仍存在；另有未 Advance borrow 在修改前抛错且可纠正。以上为底座证据，不是 FrameStore 已实现的声明。

**Dispose 与失败清理。** owner Dispose MUST 先进入 Disposed 并失效全部 Lease，再逐一尝试所有尚未尝试释放的 owned file/其他资源，最后按 `[S-FS-OWNER-LOCK]` 释放 store 控制句柄；不先遍历裸 Builder 再重复对文件取消，RBF File.Dispose 已负责其构建资源。不 flush、不归档、不输出 metadata；正常清理不重新抛出此前已交给调用方的 owner fault。以后再次 Dispose 为 no-op，原 fault 不能被清理解除。
仅汇总本次 public 清理调用实际传播的异常：无异常正常返回；一个异常保留原对象/类型并保留原栈；多个正常构造 AggregateException，保持主操作异常（若有）在首位及各清理异常。汇总分配失败不得中断后续资源释放；完成全部尝试后至少传播主异常/首个已观察异常，不能让汇总异常遮蔽它。不能声称恢复 RBF 内部 finally 已遮蔽的异常。Create/Open 失败清理也用这一规则，不能用 finally 的关闭异常覆盖初始失败。

### spec [A-FS-DURABLE-COMPLETED-OUTPUTS] 同步确认调用时的全部必要完成输出

ConfirmDurable MUST 同步确认调用时本 owner 所有已完成、尚未确认的必要输出；成功返回是本调用栈对这些输出的耐久证据。MUST NOT 仅因仍有未归还 Builder 而拒绝；未完成 Builder 保持构建内容、声明、epoch、租借与配额，不被隐式提交、取消或赋予完成/耐久资格。首版所有调用串行，屏障执行期间不允许 EndAppend 或其他调用交错，因而本次完成边界不变化。
本入口先按 `[S-FS-ARCHIVE-AFTER-FLUSH]` 完成归档维护，再按 FileId 升序对余下 NeedsConfirmation=true 的 active 执行 DurableFlush；这些标量存于已存在的 FileEntry，不使用逐帧集合或 receipt。维护也须全部成功，才能正常返回本次整体确认。
archive 文件在移动前已 flush；所有必要 active 文件的完成输出都必须确认，包括仍 leased 的文件中此前已完成的输出，不能跳过 leased 文件或只确认一个当前文件。每个重开的 active 即使 Action=None 也纳入首次确认。这个证明利用目录归档协议，不要求调用方认识文件或比较地址。
leased 文件成功 flush 可清除其此次已完成输出的未确认登记；该 Builder 后续成功 EndAppend MUST 重新登记新完成输出为未确认。后续输出必须由新的屏障确认，不能把文件或租借“以前 flush 过”解释为未来帧的耐久证据。健康取消也不让该次提前地址获得资格；位置可能复用，地址来源责任不因屏障改变。
任一 flush 失败 MUST 按 `[S-FS-OWNED-FAULT]` 停止整个 FrameStore owner；其他文件上的 Builder、旧 Writer 及缓存命中也必须在访问 owned 状态前拒绝，受控 Dispose 仍负责资源清理。不能只 fault 单个 RBF 后让其余 Builder 继续。失败不撤销已有完整 bytes，也不产生成功耐久返回。
不允许裸 RBF writer 或外部文件导入绕过登记。范围可包含不相关完整 orphan，无须选择每帧集合；它不解析业务依赖闭包，也不覆盖未来多个 data owner。
首版不提供 DurabilityReceipt、集合合并/复用/过期协议。多个 active 已是当前设计范围，必要输出必须全部覆盖；只 flush 根所在文件不满足本条。未来并发屏障须另建合同。

## 单帧资格与实施片

已知尺寸为 `Opened → Building(address known) → Completed`；未知尺寸只在正常完成时取得地址。Builder 完成/取消与文件归还的边界由 owned wrapper 明确，值拷贝、旧租借、重复 End/Dispose 不得二次归还或改变别人的租借。
ConfirmDurable 消费本阶段 `[A-FS-DURABLE-COMPLETED-OUTPUTS]` 的核心合同，不解析业务依赖闭包；交错构建与循环引用的消费资格在后续阶段独立验证。
业务发布不在本阶段建立。

1. S2-A：创建项目/solution；实施已定不透明 FrameAddress 值/两方向 12B bool codec、数值下界/失败产物和普通内部表示，记录目标运行时成本；实施已定格式门 24B codec/只读校验及空 store 直接门建立，实施 `[S-FS-OWNER-LOCK]` 的控制设施、门前 bootstrap、模式互斥与清理顺序，定稿实际根准入；实施本阶段已定的 StoreId/header codec、数量上限 config 及软阈值单参数/范围/实例固定。
2. S2-B：实施已定 FrameBuilder/FramePayloadWriter、一次性共享 Lease、borrow 前检、无分配的完成/归还登记与保守共享 fault，以及 FrameRead、同步 Inventory/Audit；三种追加、交错完成、随机读、物理检查、基础确认与 Dispose 清理可独立验证。
3. S2-C：实施统一 DrainStopped 及确认/写准入/Open 入口，结合编号恢复、creating/active/archive 中断状态与多个 active 的独立恢复取得资格。
4. S2-D：进程中断/资源失败、只读、错误格式、规模和 public 指南资格。

至少验证两种无 EventHeader 记录、三种追加、嵌套租借与乱序完成、取消后文件复用、旧地址跨归档稳定、FrameAddress 的 context 限制、archive RBF1 拒绝、创建/flush/close/rename 各窗口、多个 active 独立恢复及完整内容损坏。
地址向量覆盖固定 12B/端序的独立 bytes、FileId 高位与 uint.MaxValue、Packed 高低 32 bits 的往返、SizedPtr 最大起点/长度且末端可越过起点上界、Begin/End 编码相等、归档和冷重开不改编码、编号耗尽不回绕。独立 bytes 取 FileId=0x89ABCDEF、Packed=0x123456789ABCDEF0，预期 `EF CD AB 89 | F0 DE BC 9A 78 56 34 12`；最大 FileId/Packed 的 12B 全 FF 仍数值合法。覆盖 FileId=0、ticket 起点 0、零长度（包括 Packed 非零）和低于公共 RBF3 minimum 的长度；header 坐标仍可编码。TryRead 的 0/11/13B 均 false 且覆盖先前有效 out 为 default；TryWrite 的 default/短目标 false 不修改任何 bytes，长目标仅前 12B 改变。复合 reader 先切 12B、成功才推进，失败不吞下字段；default 可比较/hash 但不能 ReadFrame，值比较包含 Packed 全部 bits且无排序合同。codec 验证与内部 struct/数组成本验证分开，不宣称格式解码能检测错 store、旧 Serialize wire 或取消预约复用。
覆盖阈值以下/等于/超过、最大帧超阈值仍成功、可纠正失败不归还、成功后不等 Dispose 即可重新申请/确认、重复 Dispose 不二次释放、不覆盖归档目标、1024 边界与 slot=0、编号耗尽/空桶、仅根文件 flush 不充分；首帧 header 覆盖初始化中断、缺失/损坏、未知版本、字段绑定和用户扫描规则。
阈值参数覆盖省略时 64GiB、I/I+1/MaxOffset 合法且不要求对齐、I-1/MaxOffset+1/负数/long.MaxValue 在任何文件系统访问前拒绝且不留输出。以小 T 验证 header-only 可分配、三种追加的等于/越过、声明大尺寸后取消仍可复用；T=MaxOffset 的最后合法帧成功后停止而不后验拒绝。降低 T 后按恢复后的 tail 分类，分别覆盖 Truncated 变短与 CompletedTail 变长、全部 active/header 资格后才维护，以及维护失败不签发 owner。提高 T 时，尚未移档的超旧阈值 active 可重新分配，同内容已归档文件仍只读；归档/冷重开前后地址与 payload 不变。不增加生产测试开关或以内部比较测试代替 public 生命周期验收。
header 独立 bytes 向量固定 version=1、StoreId 为 hex 01 至 10、FileId=0x89ABCDEF：decoded payload 为 `01 00 00 00 | 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 | EF CD AB 89`，不是原文件 escaped bytes。覆盖精确 24B、截短/多字节/meta/墓碑/错 tag、全零/错 StoreId、0/错 FileId、未知/门不一致版本、HeadLen/TrailerCRC/PayloadCRC/Fence 损坏；坏首帧后存在合法同 tag 帧仍拒绝，合法 header 后同 tag 用户帧仍读出。验证无用户帧的初始化文件合法、首用户 ticket 从 I 开始、原生 RBF 可见和用户 inventory/audit 分类。正式 active 的每个短于 I 的 header 字节前缀在可写 Open 前拒绝，重复打开仍不修改/接纳；尤其覆盖仅缺 Key/Fence 的完整 body。header 后用户帧残尾按 RBF 恢复，report 不得触及初始化区；长度足够但坏身份/header CRC 的文件仍拒绝，即使底层已先修用户尾。私有 creating 中断留给目录协议裁决，不把初始化校验当删除授权。
格式门独立文件 bytes 取 version=1、StoreId 为 hex 01 至 10：`01 00 00 00 | 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 | 7D 5C 28 69`，CRC32C=0x69285c7d；这是完整 raw 24B 文件，不是 header 的 decoded payload 或 escaped RBF frame。覆盖 0–23B 每个前缀、25B/尾随、版本与 ID/CRC 各区损坏、重新封 CRC 的未知版本/全零 ID、旧 marker/RBF 文件及缺门；重复可写/只读打开都不修门、不从合法 data header 补身份。失败不得发生 active 恢复/私有清理/输出；校验成功但必要 close 失败也不签发 owner，读取/关闭双失败保留主错误且不二次关闭。实际规范名称/普通类型及初次发布中断仍另取 Q1/Q3 资格。
初次创建向量覆盖缺根/空根/合法 config-only 输入与非法 config 先于初始化输出拒绝；已有好/坏门、初始化树、私有残留及未知内容不覆盖或收养。覆盖必要目录准备各窗口、门缺失/0–23B前缀/完整坏字段的重复拒绝，以及完整合法门写出后、flush/close返回前或owner交付失败时可按实际事实重开；不要求写24B原子，不把失败等同未创建。任何故障清理不得删除正式门/根，门前独占不得依赖已有门；合法门旁缺必要目录仍拒绝且不补建。成功Create无data文件/桶/RBF句柄，重开StoreId不变，空确认/Inventory/Audit不建文件且扫描计0；首次实际追加才建FileId=1，非法请求不建、已发布后取消仍占号。具体根/锁/type/no-follow及进程中断、资源失败实证仍归Q3和S2-D。
私有初始化验收覆盖 0..I 全部实际顺序前缀、合法非默认 Key、232/233B 底座有界域与同一句柄完整读取/单次关闭；不得将 internal 原型当 public 消费证据。正式 max 完整发现后才核对唯一候选号，覆盖空槽/Max 空槽合法、错号/正式重复/Max 有私有文件、缺 creating/未知/隐藏/多项/特殊项及完整枚举失败。另一份合法短 RBF、合法 header 后用户帧、错预期身份/形状/CRC 或坏 closure 必须拒绝并保留，重复打开不修改；合格短前缀不宣称身份/作者认证。只读合格留原样且不纳入 Inventory/Audit/地址/max；可写全部资格及必要 close 成功后才删唯一文件，之后 active 才可恢复。取消中断、读取/close/delete 错误与主/清理双失败不签 owner、不过早释放锁、不递归删目录或补发布；当前写入正常中断不自动盲删。无格式门的初次残留不套用本协议续作。
owner 锁向量覆盖无data/全部archive时仍排斥第二writer，writer/RO互斥、RO/RO共享；两类句柄都在完整门/正式目录检查前取得锁，所有factory失败与Dispose最后单次关锁。覆盖Create锁前空根→其他调用先建门/布局→本调用获锁后重检拒绝，以及纯control/config+control bootstrap中断可复用而坏门/初始化树不能续作；非空/特殊control、缺control的Open/RO拒绝且不补造/修复。覆盖fault期间第二owner仍被排斥、取消/数据close/汇总异常不跳过最后关锁、kill后保留同一空control可重开、不删除或DeleteOnClose。Linux验证禁用BCL自动锁或native错误不能绕过显式锁准入，unsupported即拒绝；两平台实际名称/ordinary/no-follow与资源证据另取得。Windows原语28项探针不替代完整factory或Linux验收。
屏障覆盖 A 完成/B 仍 Building 时成功确认 A、B 租借文件内更早 dirty 帧也被确认、B 内容及租借不变、B 后续完成须重新确认、B 健康取消不获得资格、reopened active Action=None 首次确认，以及任一文件 flush 失败后所有 owned Builder/Writer 停用。
维护向量覆盖 End 成功/配额释放后没有 flush/close/rename，下一合法写准入先维护、满额或非法 Begin 不维护，以及 Confirm/Open 排空、读/取消/Dispose 不移档。让 A 成功越过阈值，下一 Begin 或 Confirm 的 flush/close/rename 各自失败：A 仍是完整事实、本次无新 Builder/成功确认，其他活跃 Builder/Writer 全部拒绝；关闭已尝试对象不重复 Dispose，重开按真实位置裁决。覆盖 M 个 Builder 加一次 Append 的 M + 1 停止项窗口、持续 Append 不调用 Confirm 时仍及时归档、同一次 Confirm 不重复 flush 已归档项。
租借向量覆盖 Builder/Writer 副本、成功/取消后同文件复用、旧 End/Dispose/Writer 不触及新租借和内层 epoch 回绕不恢复 outer 资格。借用镜像覆盖 Span/Memory、成功 Advance(0)、失败 Advance 保持、borrow 期间 reservation Commit/TryGetReservedSpan、End 可纠正前检及 Confirm 保持该位。分别验证 Result 拒绝保留、委派 finalize/Commit 未知异常终结并 shared fault，尤其完整输出后回收失败而 TailOffset 尚未推进的窗口；不以一次异常等于零输出。
Dispose 向量覆盖取消资源异常不重租、全部 owned 文件逐一尝试、单原异常/多 Aggregate、汇总分配失败仍继续清理且保留首异常、Open 主异常不被清理遮蔽、二次 Dispose no-op、不重抛旧 fault，以及清理不 flush/归档。
读/检查向量覆盖 FrameRead 在 reader 关闭、归档、owner fault/Dispose 后仍可用、别名只释放一次、结果 Dispose 后 span 不再使用；包装 OOM/读失败/关闭失败均释放未移交的 pooled 帧。Inventory 的 payload 损坏可保留结构资格而 Audit 必须失败，TrailerCRC/Framing 错误不能变成 EOF，坏/墓碑 header 拒绝且后续 tag=0/墓碑用户帧保留；Audit 的 24B header 副本与元信息跨回调/owner Dispose 仍可保留。
同步 visitor 覆盖 live Builder/递归扫描准入先于 I/O 拒绝、回调 Append/Begin/Confirm 先于 DrainStopped 拒绝且不 fault、随机 Read 合法、callback/OCE 后 guard 释放，以及最后一项 Dispose owner 后不得正常成功。操作临时句柄由 owner/栈清理一次，callback 主异常不被关闭失败遮蔽；后文件坏/目录枚举失败/取消保留前缀回调但不产生全库成功。正常文件含完整 orphan，零用户帧成功计数为 0；真实集合全 CRC 资格不变成业务依赖闭包或未知历史文件存在性证明。
随机读取覆盖目标文件 Building 时成功读取旧完整帧、提前地址及包含帧后 Fence 的跨边界范围在 I/O 前拒绝、CRC/解析/Dispose/fault 仍传播、同一历史读取不受租借分配结果影响。扫描/扫描边界/物理后继保留原 guard，不以随机读取放宽宣称并发或扫描已开放。
数量配置覆盖 M=1/32、满额 Begin 先于租借/创建/输出拒绝、初始化失败不占额度、短写拒绝仍占额度、成功 EndAppend/健康取消释放一次，以及有效/缺失/空 object/非法/重复或未知属性/读取错误、实例固定和下次打开生效；只读忽略写入配置。覆盖满额时合法 Append 仍成功、无关 Builder/ConfirmDurable 不受影响，以及 M=1 时先取得 A 地址、Append B 指向 A、再回填完成 A 的 public 轨迹；不以数量上限测试冒充总内存预算证据。
资源基线覆盖所有工厂显式 Off、active idle 句柄保留与 archive 随机读结束后的关闭/自有结果、扫描提前结束和错误时释放、Open 中途失败清理、旧高上限留下的 active 数大于当前 M 仍完整检查并首次再确认。记录大 sizeHint、重复大 Append 后 scratch/池回收及实际句柄成本；不把配置降低等同于总资源立即缩减，也不宣称没有归档维护的 active 集合受 M 限制。
文件选择覆盖乱序目录枚举、最低编号忙时选下一空闲文件、健康取消/归还后重新优先低编号、尚未移档但已停止分配的低编号排除，以及仅当全部候选不可分配时新建。编号恢复覆盖低号仍 active / 高号先 archive、较高桶连续为空、正式集合为空/全部已归档、合法缺口不补洞、uint 高位及1024边界、低桶错位/非规范别名/跨集合重复、枚举中途失败在 active 恢复前拒绝。覆盖 header-only active 已占号、私有残留通过裁决后不计 max、active 发布后首个 Builder 签发前失败与 rename 结果不明，重开按正式位置取得同一 max；归档/End/取消不改变 max。耗尽时 Open/读/Confirm/已有 Builder 仍合法、空闲低号仍可写、全忙或全停止时新建请求在维护/创建前不 fault 拒绝；不回绕或跳号绕过目标冲突。失去缓存句柄不改变选择结果，坏文件不被静默跳过。
路径向量覆盖上述独立文本、0/uint 高位/最大编号、1023→1024 与合法 slot=0、首/末桶及空 `400000`/`ffffff` 超范围拒绝。覆盖 uppercase hex/`.RBF`、少/多零、空白/尾点/符号/Unicode/错层级、合法 stem 的目录或合法桶名的文件、隐藏未知项及枚举/类型检查失败；都不得经筛选或规范化成为成功完整名称资格。验证发现与按地址生成的位置一致、移档 basename/header FileId/12B 地址不变；Windows 实际固定组件大小写与两平台 link/特殊项拒绝另取得目录准入资格，不由纯字符串向量替代。
最小消费者只按地址取回并解释引用；不同申请/完成次序构建相同逻辑图时，业务结果不依赖文件选择或枚举次序。
健康 Open 成本包含 config 读取、全部正式名称的 O(A+B+H) 枚举及 active 的小型 header 校验；记录实际目录规模/冷打开成本，不宣称与历史规模无关或已符合性能预算。冷历史 reader、异常尾扫描、完整 payload 和 audit 的成本另外报告。
项目遵循仓库 SDK/test pin，不复制生产包清单。源码可用不等于包已交付。

## Ready 阻断项与出口

| ID | 需定稿或实施验证 |
| --- | --- |
| S2-Q1 | FrameAddress 值/两方向 12B bool codec、数值/默认/相等/失败、普通表示，以及 framestore.format 的 24B 记录/唯一 CRC/只读完整校验已定；实施相应 bytes/拒绝/关闭/无后验失败向量并记录地址运行时成本。初次空 store/直接 create-only 门建立与成立/返回/失败边界已定，实施对应中断、空集合和首次追加向量；owner 模式互斥消费 `[S-FS-OWNER-LOCK]`，实际上下文/根准入消费 `[S-FS-ROOT-ADMISSION]`，实际资格见持久化闭环记录，直接消费统一版本 1 / 16B StoreId |
| S2-Q2 | 软阈值唯一 long 参数/64GiB 默认/范围/实例固定/恢复后重算与 archive 不解封已定，实施小阈值 public、非法参数、升降/修尾及维护失败向量；正式路径/单一 codec/全部直接项语法和编号恢复已定，实施文本/拒绝/定位向量并测量 O(A+B+H) 目录成本，不重新选择布局、计数器或最高桶快速路径 |
| S2-Q3 | 初始输入消费 `[R-FS-STORE-CREATE]`，owner 锁/控制角色、门前 bootstrap 与获锁后重检、模式互斥及退出顺序消费 `[S-FS-OWNER-LOCK]`；实施同/跨进程冲突、锁前后竞争、kill/fault/关闭错误向量。私有 creating 语法/独立候选/全部前缀资格、可写取消及只读保留消费 `[R-FS-CREATION-PRIVATE]`；所需 RBF 公共纯前缀入口及取消/清理已实施。实际根与固定组件/普通类型/no-follow 消费 `[S-FS-ROOT-ADMISSION]`；两平台严格锁/data 同文件系统不覆盖 rename 的源码测试见实施记录，真实跨进程中断等系统实证仍留后续 |
| S2-Q4 | Builder/Writer/Lease、borrow、完成/dirty、维护/fault/Dispose，以及 FrameRead、纯值 FrameInfo、FrameFileAudit、同步 Inventory/Audit 准入/重入/取消/终止合同已定；实施对应拒绝/资源清理/信任级别与 completed-prefix 随机读取向量，不再作为待定 API 设计 |
| S2-Q5 | 保留可写 active 句柄、显式 Off、只读按操作开关且无 reader pool 的基线已定；验证身份/读结果/扫描资源归属、Open 失败清理、历史峰值与实际资源成本，不做精确总资源配额 |
| S2-Q6 | 24B header/单一格式版本/身份绑定、首位置识别、长度前检及 RBF 恢复后 checked-read、初始化与用户 tag/扫描合同已定；实施上述 bytes、拒绝、重复打开和用户残尾验收，不再作为待定设计 |
| S2-Q7 | 可选 framestore.config.json/默认 32/严格校验/实例固定、Builder-only 计数与 Append 的额外短租借已定；验证先拒绝、失败不占额、释放一次、满额 Append 和配置/生效向量 |

完整出口包含多个 active 的交错构建、软轮转、独立使用/恢复，不依赖发布库或其他扩展存在。单文件子合同可以先 Accepted，但不等于文件租借、多文件恢复或整个 S2 已完成。未来多线程 Builder 资格不由首版串行结果推定。
