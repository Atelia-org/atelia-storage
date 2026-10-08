# Bare Primitive Value 编解码规范

日期：2026-10-08；同日纳入独立可空性与显式受控压缩。状态：**Accepted，BPV1 实现、源码及本地候选包消费验收通过；性能与下游接入仍独立**。
本文遵循[规范约定](../spec-conventions.md)。下文 spec 是本次实施采用的可测试合同；源码与包资格见[实施验收](bare-primitive-value-acceptance.md)。另见 [Tagged Value 意向](tagged-value-intent.md)，它不构成本方案的前置要求。

## 目标与需求来源

提供一份公共的、按已知 schema 使用的二进制值编码：数值、字符串和 bytes 的编码/解码及精确尺寸计算。调用方选择字段类型、编码方式与顺序，字节中不携带通用类型 tag、字段名或 schema。

| 来源 | 要求或证据 | 本草案处理 |
| --- | --- | --- |
| 用户本轮明确要求 | Bare 草案面向施工；Tagged 只记录意向，需求与自有协议理由不足 | 两份文档独立；Bare 不等待 Tagged |
| 用户此前动议 | 跨库统一基础 codec；VarInt、自适应 UTF-8/UTF-16；内部草稿可重做，不承担旧草稿兼容 | 定义公共基元，不加旧格式 adapter |
| 用户本轮确认 | 可空性独立；控制字节仅描述解压方法、不描述编码参数；压缩态后依次编码压缩后长度与解压后长度；完整编码收益不足保存原文 | 新增显式受控 string/bytes；普通 Bare API 保持；按完整码字严格较小选择压缩 |
| 用户授权的工程选择 | 算法优先参考旧 EventJournal，其余问题择优 | 首片 Brotli；Zlib 待严格完成/消费资格后再加入；不因空码位多而预分配未实现方法 |
| 上轮讨论的设计建议，非已发布合同 | writer 输出默认紧凑表示；reader 可接受明确、无损、有界的其他表示 | 本文提出单一宽容 reader，不加严格/宽松模式矩阵 |
| 已确认的 FrameStore 合同 | FrameAddress 固定 12B：uint32 LE FileId + uint64 LE Packed；提前地址稳定 | 消费层组合固定 LE 基元；不引入 varint 地址 |
| 当前代码证据 | DurableGraph 有纯 BCL reader/writer、VarInt、字符串；StateJournal 另有实现且错误/字符串规则不同 | 复用算法经验与独立 golden，不整份照搬领域 writer |
| 当前存储边界 | RBF 解释物理帧；FrameStore 管分配/随机读；VersionStore 解释 RootMap | 本层不解释 frame、根、身份、发布、CRC、耐久与恢复 |

## 程序集与责任边界

落点：`src/Binary/Binary.csproj` / `Atelia.Binary`，测试为 `tests/Binary.Tests/Binary.Tests.csproj` / `Atelia.Binary.Tests`，目标 .NET 10。只依赖 BCL，以 `IBufferWriter<byte>` 和 `ReadOnlySpan<byte>` 接入，不引用 Data/Rbf/Primitives 或任何业务库。
本次采用独立程序集，允许 codec 接纳与存储底座升级分别推进。当前 DurableGraph.Serialization 无项目/包依赖，其 Storage 仍引用旧 Data/RBF；StateJournal 也消费旧 RBF。该静态事实支持独立演化，不证明已经出现 NuGet 冲突，也不要求永久保留旧底座。
本次采用独立 Binary，不再拆 Bare/Tagged 两个程序集。生产包清单仍仅由 `eng/Pack.ps1` 定义，新阶段 All 为 Primitives → Data → Rbf → Binary。Binary 只依赖 BCL；Rbf 的原三包闭包保持，Binary 的单包消费另行隔离验证。旧三包阶段的验收记录保留历史身份。

### spec [S-BPV-SCHEMA-OWNED] schema 与记录资格归消费方

Bare 基元 MUST 按调用方指定的编码读写，不猜测整数使用 Fixed 或 Var，不写类型标签、padding、CRC、magic 或逐值版本号。本文将这组规则命名为 `BPV1`，仅用于规范引用；消费方的记录/header 版本选择该规则，不要求每个值额外写 `BPV1`。
记录版本、字段顺序、可空性、枚举值、StoreId/FrameAddress、字典顺序及重复 key、总记录上限、业务闭包与整条记录的完全消费由消费方定义。Binary 不注册业务 codec，也不提供反射 serializer、通用对象树、schema registry 或全局配置。

首片包括 byte/sbyte、bool、固定 16/32/64-bit 整数、变长 16/32/64-bit 整数、Half/Single/Double、UTF-16 code unit、string、raw bytes 与 length-prefixed bytes。enum 和容器由 schema 组合。
首片另含下面的显式受控 string/bytes：可空、可选择尝试压缩，schema 仍决定值类型。普通 WriteString/WriteBytes、ReadString/ReadBytes 不自动增加 control 或压缩。
Guid/decimal/时间及全套 nullable 标量 helper 首片 defer，现有消费层继续明确组合；这些中立类型已有真实消费者，不是永久禁止进入公共库。首次 DG/SJ 实际接入工作包需要这些类型之前，另行确定公共表达、范围与 golden；不把 BCL 全家桶迁移作为首片门槛，也不宣称首片已统一其全部编码。

## 固定值

### spec [F-BPV-FIXED-LE] 固定值显式使用 LittleEndian

多字节固定整数 MUST 使用 LittleEndian；signed 使用对应宽度的二进制补码。方法名明确 `LE`，不得通过宿主 CLR 内存转储定义格式。

| 值 | 字节数 | 表示与 reader 接受范围 |
| --- | --- | --- |
| Byte / SByte | 1 | 8 bits；SByte 按补码解释 |
| Boolean | 1 | writer false=`00`、true=`01`；reader 仅接受这两个值 |
| UInt16LE / Int16LE | 2 | 完整 16 bits |
| UInt32LE / Int32LE | 4 | 完整 32 bits |
| UInt64LE / Int64LE | 8 | 完整 64 bits |
| CharLE | 2 | 单个 UTF-16 code unit，包括 surrogate；不是 Unicode scalar |
| HalfLE / SingleLE / DoubleLE | 2 / 4 / 8 | 对应 IEEE 754 位表示，按同宽整数 LE 存储 |

浮点编解码 MUST 保留输入位表示，不做小数舍入、自动缩窄、NaN 统一、负零归零或跨类型转换。对 Half/Single/Double 分别选择 API，所有 bit pattern 都可以读；位保真使用 BitConverter 位转换及独立 golden 验证。typed bool 不因宽容整数 reader 而接受任意非零字节。

## 变长整数

### spec [F-BPV-VARINT-BOUNDED] Base128 接受有界冗余表示

VarUInt16/32/64 MUST 使用低位组在前的 unsigned Base128。每字节 bit 6..0 是下一个 7-bit 数值组，bit 7 表示后续还有组；首个 bit 7=0 的字节终止本值。值为 `sum((byte[i] & 0x7F) << (7*i))`，先检查范围再进行移位/累加，不以 CLR 截断掩盖溢出。

| API 类型 | 最大字节数 | 到达最大宽度时允许的末组 payload |
| --- | --- | --- |
| UInt16 | 3 | `0..3` |
| UInt32 | 5 | `0..15` |
| UInt64 | 10 | `0..1` |

writer MUST 输出最短表示，0 编码为 `00`。reader MUST 接受未超过上述宽度、没有数值溢出且已经终止的非最短表示，如 `80 00` 为 0；终组为 0 不自行成为错误。最大宽度字节仍设置 continuation 时明确拒绝，不消费下一字段来寻找终止符。尚未到最大宽度而输入结束是截短。
同一值在不同 API 宽度下的 accepted set 可不同：6-byte 的冗余零可由 VarUInt64 接受，但 VarUInt32 拒绝。长度/header 使用 VarUInt32，不隐式提升为任意宽度。读入非最短表示后写回只保持值相同，不保证字节相同；raw bytes 转发另由调用方负责。

### spec [F-BPV-SIGNED-ZIGZAG] signed VarInt 使用同宽 ZigZag

VarInt16/32/64 MUST 先按对应宽度进行 ZigZag，再使用同宽 VarUInt。对宽度 `w`：`u = unsigned((n << 1) XOR (n >> (w-1)))`，右移 n 为算术右移，运算按 w bits 解释；逆变换是 `(u >> 1) XOR -(u & 1)`。0→0、-1→1、1→2，最小负值和最大正值均完整可逆，不使用绝对值绕开最小负值。

（Informative）C# 施工时显式使用同宽转换，避免 checked 构建选项改变值域：short 为 `unchecked((ushort)((n << 1) ^ (n >> 15)))`，int 为 `unchecked((uint)((n << 1) ^ (n >> 31)))`，long 为 `unchecked((ulong)((n << 1) ^ (n >> 63)))`。short 运算先提升为 int；逆变换的 mask 用 signed 类型，对 ulong 的例子为 `unchecked((long)(u >> 1)) ^ -unchecked((long)(u & 1))`，不对 unsigned 直接取负。

## 字符串

### spec [F-BPV-STRING-DUAL] 字符串保留 UTF-16 code units

非 null string 的语义 MUST 是 .NET UTF-16 code-unit 序列，按 Ordinal 原样往返，不做 Unicode 归一化、大小写变换或 replacement。格式为 `VarUInt32(header)`，后跟指定编码的 payload；不自动插入或消费 encoding preamble，无 terminator。字符串内容中的 U+FEFF 无论位于开头或中间都原样保留，不当作可剔除的 BOM。

| header | 编码 | payload 长度 |
| --- | --- | --- |
| 偶数 `2*C` | UTF-16LE，C 个 code units | `2*C` bytes，即 header 本身 |
| 奇数 `2*B+1` | strict UTF-8 | B bytes，即 `header >> 1` |

UTF-16LE payload MUST 原样保存每个 16-bit code unit，允许未配对 surrogate。UTF-8 MUST 是符合 Unicode UTF-8 的有效序列，不接受 overlong sequence、surrogate 编码或越界码点；无效文本明确拒绝，不替换为 U+FFFD。
单个 payload 的可表示字节数上界是 `int.MaxValue`；UTF-16LE 字节数天然为偶数。reader 在分配字符串前核对该范围、剩余输入和调用方提供的 byte 上限。基础格式不继承 RBF 的约 256MiB 帧上限；记录层另行执行它的容量要求。

### spec [S-BPV-STRING-DEFAULT] writer 选择可表示的最短 payload

默认 writer MUST 在可表示、无损的候选中选择 payload 字节数较短者，payload 字节数相同选择 UTF-16LE；空字符串默认 `00`。不配对 surrogate 只能使用 UTF-16LE。这里比较的是 payload，不声称“完整 codeword 持平时必选 UTF-16”；header 长度也计入精确 Measure。
UTF-16 长度按 `long` 计算；UTF-8 字节计数超过 int 上界时，若 UTF-16 可表示就选择 UTF-16，而非无条件拒绝。两个候选都不能表示时确定拒绝，准备阶段不输出。实现可提前停止已无法胜出的 UTF-8 计数，但结果须符合这条选择规则。

### spec [A-BPV-STRING-TOLERANT] reader 不重新选编码

reader MUST 接受 header 明确指定且满足长度/文本规则的两种表示，不因另一种编码更短而拒绝，不在解码后再运行最短选择。`01` 是合法的空 UTF-8 字符串，即使 writer 默认写空 UTF-16；字符串 header 的冗余 Base128 表示同样适用 `[F-BPV-VARINT-BOUNDED]`。
宽容范围仍由格式闭合；它不包含猜测损坏的 header、替换非法 UTF-8 或忽略剩余长度。首版不提供 `ReadCanonicalString`、全对象 canonical validator 或严格模式开关；真正需要同值同 bytes 的消费方另定记录级规范化协议。

## Bytes

### spec [F-BPV-BYTES-EXPLICIT] raw 与带长度 bytes 分开

`WriteRawBytes/ReadRawBytes(length)` MUST 直接消费指定数量的字节，无前缀；用于已知 schema 的固定字段或已有编码。`WriteBytes/ReadBytes` MUST 使用 `VarUInt32(byteLength)` 后跟原始 bytes，0 为 empty；长度解码适用宽容 VarUInt32，检查 int 上界、剩余输入及调用方上限。
ReadBytes 返回借入输入的 ReadOnlySpan，不分配 owned 数组，不延长上游 pooled frame 的生命周期；需要跨生命周期持有时由消费方复制。

## 可空性

### spec [F-BPV-NULL-CONTROL] null 使用独立控制字节

采用本公共可空 profile 的字段 MUST 使用 `00=null`，无后续值；`01=非 null 原文`，后跟该字段的完整普通 Bare value。存在性不通过 string/blob 长度 +1 表示，不缩小原始数值或长度 header 的可表示范围。不可空字段仍直接使用普通 Bare value，无 control 成本；是否可空与字段类型由 schema 指定。
nullable 标量只允许 00/01；可用现有 ReadBoolean/WriteBoolean 与 Bare 标量组合，不为每个 Fixed/Var/浮点宽度复制一套 helper。两次独立 Read 不自动组成原子事务，调用方按 `[A-BPV-CURSOR-ATOMIC]` 复制 reader。压缩态不是 Boolean true，下面的受控 reader 使用专门 switch；普通 Boolean 仍拒绝 02 等值。
SJ/DG 旧 `0=null，present=rawHeader+1` 是不同 schema 的既有经验，不是本 profile 的另一种表示；新 reader 不猜测旧格式。本轮不改这些库、冻结 EJ 或增加 adapter，后续实际接入明确选择记录版本和可空 profile。

## 显式受控 string/bytes 与压缩

### spec [F-BPV-CONTROLLED-ENVELOPE] 控制字节包装完整 Bare 值

受控字段 MUST 按下表编码。字段究竟是 string 还是 bytes 仍由 schema 决定；控制字节描述 null/存储方式，不是通用类型 tag。

| control | 意义 | 后续 bytes |
| --- | --- | --- |
| `00` | null | 无 |
| `01` | Raw | 完整普通 BareString 或 BareBytes；不再增加外层长度 |
| `02` | Brotli | `VarUInt32(C)`、`VarUInt32(U)`、恰好 C bytes 的压缩流 |

C MUST 是压缩后 body 字节数；U MUST 是解压后的完整内层 Bare codeword 字节数，包括 string header 或 bytes 长度前缀，而非仅用户 payload。压缩输入是该完整 codeword，不另定义 UTF-8-only 或压缩专用字符串编码。C/U 复用 `[F-BPV-VARINT-BOUNDED]`，writer 最短、reader 接受有界冗余表示。
受控 null 是 `00`，原文 empty string/bytes 是 `01 00`；普通不可空 empty 仍为 `00`。两个完整内层类型至少需要 1B header，故压缩态 U=0 MUST 在解压/分配前拒绝；C=0 也不能形成完整 Brotli 流，明确拒绝。`03..FF` 尚未分配，reader MUST 拒绝，不当 Raw、不跳过或尝试其他 codec。
首片不提供独立公共 ByteBlock、泛型 value dispatcher、codec registry 或新的 AST；内部共享 envelope 解码不要求新增这些概念。

### spec [S-BPV-COMPRESSION-SELF-CONTAINED] 方法只定义自足解压格式

control=02 MUST 表示 RFC 7932 的标准、自足 Brotli 流。合法标准 window 与格式自带静态 dictionary 属流格式，不限制为 writer 初始 window；不使用外部 dictionary、跨字段压缩上下文或 large-window 扩展。quality、level、writer window 选择不写入 control 或新增元数据，不以此区分解码方法。
首片 writer 的 ValueCompression 只有 None/Brotli，分别不尝试压缩或只尝试 Brotli；其 enum 数值不是 wire control。None 是默认，以免普通可空工作流隐含压缩 CPU。Brotli 内部初值可参考 EJ 的 quality=3、window=22，属于 Informative 调优起点；未来改变编码参数不改变方法 ID，不承诺跨实现版本产生相同压缩 bytes。
EJ 实际支持 Brotli/Zlib；ZstdFrame 只是未实现保留。Zlib、Zstd、Gzip 等本轮 defer，不先分配有效 control。加入方法前须明确自足格式、完成与实际消费检查、独立 malformed/golden，以及接入收益；不能只凭旧 enum 或空位多宣称新格式支持。

### spec [A-BPV-COMPRESSION-PREPARED] 按完整尺寸选择并冻结输出

受控 Prepare MUST 先按普通 Bare 默认规则选择编码并以 long/checked 计算完整尺寸 U；受控 owned 准备要求 U<=int.MaxValue，超限在分配前确定拒绝，检查通过后才物化完整内层表示。这是新增入口的资源边界，不收缩普通 WriteString/WriteBytes 的 payload 值域和 long Measure 合同。string 不搜索编码×算法组合，先复用 `[S-BPV-STRING-DEFAULT]`，再尝试所选的一种方法。
令 V(x) 为最短 VarUInt32 字节数，非 null 候选总长 MUST 按下式比较：

```text
rawSize        = 1 + U
compressedSize = 1 + V(C) + V(U) + C
```

压缩结果仅在 compressedSize 严格小于 rawSize 时使用；持平或变大存 Raw，null 不压缩。reader 不重新比较收益，合法但不划算的压缩表示仍可读取。无需复制 EJ 的 2048B/256B/5% 三阈值、失败回退 policy 或多算法竞争；如跳过必不能获益的极小输入，须以完整尺寸下界证明。确实无法节省的候选是正常 Raw 回退；调用错误、allocation/编码器异常仍保持异常，不伪装成收益不足。
ControlledValueEncodingPlan MUST 是无 public constructor 的 sealed 自有快照：私有保存选中的 storedBody、control 与压缩态 U；EncodedLength 精确派生为 long。Raw storedBody 保存完整 Bare bytes，Brotli storedBody 保存已选压缩 bytes；外层前缀在 Write 时输出，无需再复制为整份 wire 数组。Null 是有效的共享计划，仅写 00。
PrepareControlledBytes 在准备期间消费借入 span；成功返回后不得借入它，Raw 回退也必须快照。计划不公开数组、Span 或可取回私有数组的 ReadOnlyMemory，不提供 Dispose/池租借合同。WritePreparedValue MUST 输出同一快照，不重新选择编码/压缩；成功预检仍不保证 sink 输出成功，失败仍无 rollback。该计划不证明 schema 类型、RBF 完成或耐久资格。
受控 EncodedLength 来自实际准备结果，不新增可仅由原长度推算压缩精确尺寸的 MeasureCompressed API。普通 StringEncodingPlan 继续轻量，只存 string/header。准备会物化内层并可能同时持有候选缓冲；chunked Write 仅约束 sink 请求，不将整个 Prepare 宣称为流式、恒定内存或零分配。公共 pooled lease/caller-buffer 在实际性能证据要求时另议。

### spec [A-BPV-CONTROLLED-READ-BOUNDED] 完整验证后提交 cursor

ReadControlledString/ReadControlledBytes MUST 在 reader 副本上完成外层解析、解压、内层 Read 和所有必要分配，仅最终成功才提交外层 cursor。ReadControlledBytes 在 Raw/Brotli 两条路径都返回自有 byte[]（null 为 null），不混合 borrowed 与 owned；普通 ReadBytes 仍借入源。ReadControlledString 返回普通自有 string。
maxStoredByteCount 约束 storedBody（压缩态 C，Raw 态实际内层 codeword）；maxDecodedByteCount 约束完整内层 codeword U；均不包含外 control/C/U 前缀。先校验非负调用参数，再校验声明 int 范围、limit、剩余输入，最后分配/解码。Raw 也须先窥读内层 header，按实际 header 消费数+payload 算出 U 并检查两个 limit，不得先 ReadString 分配再拒绝；冗余 header 字节计入实际 U，不以默认 Measure 替代。limit=0 可读 null，但不容纳需要 1B 内层 header 的 empty。
Brotli MUST 使用 instance decoder 验证 Done、总 bytesConsumed=C、总 bytesWritten=U，随后内层 Bare reader 解析指定值并 EnsureFullyConsumed。U 已满而 decoder 尚未 Done 时用 1B scratch 驱动剩余完成步骤，产生任何额外 byte 即拒绝；不分配 U+1 大数组、不把 destination 满或输出长度相符当成功。decoder 资源必须在成功/异常时释放。拒绝 C 中的尾随 garbage、串联流、内部截断以及声明 U 过小/过大。
外层缺 control/header/C body 是 EndOfStreamException；未知 control、范围/limit 错误、非法压缩流、U 不匹配及解压后内层截短/非法文本/多余字节是 InvalidDataException。内层错误不能被 Raw 回退或读旧值掩盖；OOM 与其他非格式异常保持原异常，外 cursor 仍不变。Raw 身体截短继续按普通 Bare 的 EndOfStreamException 分类。

## API 与资源合同

### spec [A-BPV-CURSOR-ATOMIC] 单个 Read 失败不推进 cursor

reader MUST 是只借入 ReadOnlySpan 的值型 cursor，成功 Read 推进实际消费字节数；任何单个公开 Read 失败时，ConsumedCount/RemainingCount MUST 保持调用前状态，包括 string 的文本错误和 allocation 失败。可先在 reader 副本上解析，成功后安装副本。
复合记录的多次 Read 不自动回滚；消费方需要原子解析时自行复制 cursor，成功后再安装。`EnsureFullyConsumed` 只检查当前受限输入，没有剩余才成功；不能自动替每个字段要求 EOF。record slice 和真实 EOF 的边界由消费方提供。

### spec [A-BPV-ERROR-CLASSIFIED] 输入错误与调用错误分开

| 情况 | 拟定 public 异常 |
| --- | --- |
| null downstream / null 普通 WriteString/PrepareString 参数 / null WritePreparedValue plan | ArgumentNullException |
| 负 length/limit，不能表示的待写 payload | ArgumentOutOfRangeException |
| 固定值或未到最大宽度的 VarInt 截短；声明 payload 多于剩余输入 | EndOfStreamException |
| 数值溢出、最大宽度仍 continuation、非法 bool/UTF-8、超声明上限、尾部未消费 | InvalidDataException |
| default/uninitialized writer 或 string plan 使用 | InvalidOperationException |
| sink/pool/I/O/allocation 异常 | 保留原异常；不包装成输入格式错误 |

显式 limit 非负；先校验公开 length/limit 的调用参数，再处理输入的 header/数值范围、声明 limit、剩余输入，最后解码/分配。limit=0 允许 empty；limit 不改变 wire。default reader 等价于 empty 输入。Binary 不拥有底层 store，也不自行把 writer 标记为 Faulted；调用方按其 sink/Builder 的合同处理输出异常。不规定多个同时发生的调用错误之间的穷尽排序。
上句 limit=0 的 empty 规则指普通 payload/bytes limit；受控 limit 计入内层 header，按 `[A-BPV-CONTROLLED-READ-BOUNDED]` 区分。受控 string 的 null 是合法值；未知 ValueCompression（包括 null string 时传入的未知值）是 ArgumentOutOfRangeException。class plan 的 default 就是 null，WritePreparedValue 确定拒绝；它与 struct StringEncodingPlan 的未初始化状态不是同一种异常。

### spec [A-BPV-WRITE-PARTIAL] writer 借入 sink 且不承诺回滚

writer MUST 借入 IBufferWriter，不 Dispose/flush 它。null、非法 size 或无效 plan 的确定拒绝须在首次 GetSpan/Advance 前发生；default writer 在包括 empty raw bytes 在内的所有 Write 入口都先拒绝。成功预检不保证后续输出成功。已经 Advance 的内容不可自动回滚，写入异常也不承诺 sink 尚未变化。上层 RBF Builder 决定取消/停用资格，不由基础 writer 模拟事务。
writer 仅保存 readonly sink 引用，不缓存 Span，采用普通 readonly struct；复制或保存只得到同一 sink 的门面，不拥有或延长 sink/Builder 的生命，也不取得并发资格。每次实际 GetSpan/Advance 仍执行 sink 本身的 guard，不缓存或模拟 store/epoch 状态。健康 writer 的 empty raw bytes 可以不调用 sink，本层不承诺通过空操作探测 sink 健康。
输入 ReadOnlySpan 须在整个调用期间保持可读，不能因 sink 的操作被覆写、释放或失效；不提供 alias 快照/检测。调用仍须满足 sink 的状态与串行使用规则；writer 不提供 reservation、长度回填或所有权转移。

### spec [A-BPV-SIZE-EXACT] Measure 是默认 writer 的精确尺寸

标量尺寸由固定表或最短 VarInt 精确计算，不执行一轮编码。bytes 尺寸为 `MeasureVarUInt32(byteLength) + byteLength`；string 使用同一编码选择的 header 长度加 payload 长度。总尺寸用 long/checked 求和，不因 payload 是 int 而让加前缀后的尺寸溢出 int。
reader 不用 Measure 默认输出尺寸代替实际消费数；非最短表示可比 Measure 结果长。传给 RBF 已知尺寸入口前，消费方核对整个记录预算并安全转换为 int；基础层不知帧 overhead。

为避免 `MeasureString + WriteString` 重复选择，提供可选 StringEncodingPlan：只保存不可变 string 的强引用和已选择的 uint header；编码与 payload 长度从 header 推导，不再分别缓存 enum/count。EncodedLength 是默认输出的精确总长；default 计划的 EncodedLength getter 和 WriteString(plan) MUST 抛 InvalidOperationException，有效 empty 计划有非 null string 引用。
计划不保存整份编码副本，不提供通用 Prepared AST 或持久 handle。普通 WriteString(string) 与 MeasureString(string) 无需调用方管理计划，分别执行选择仍然正确；计划只让已知尺寸工作流复用一次选择，不是申请 ticket 的资格。保留该小型可选能力基于扫描/分配路径分析，尚未测出 CPU 优势。

### spec [A-BPV-OUTPUT-CHUNKED] 大 payload 有界索取 sink 缓冲

raw bytes、带长度 bytes 和 string writer MUST 采用与 payload 总长度无关的有限 sizeHint 上限，大 payload 不一次 GetSpan(全部长度)。上限只约束请求，sink 可以返回更大的 Span，不宣称限制 sink/RBF 自身总内存。
WritePreparedValue 的 storedBody 也遵守同一请求上限；计划内部自有数组不等于要求 sink 提供同样大的连续 Span。
UTF-16LE 分块以完整 code unit 为单位；UTF-8 分块保留 surrogate pair/encoder 状态，完整结果与单次 strict 编码一致。每次只 Advance 实际写入数，不保留 Advance 后的 Span，不要求 sink 的下一块与上一块连续。固定标量/VarInt 可一次 GetSpan 取得最大 10B。
（Informative）首版内部上限可从 16KiB 起步，无 public tuning 参数；后续测量调常量不改变 wire/public 合同。小串沿直接写路径，大串使用 `Encoder.Convert` 或等价的 code-point 边界算法；不引入 encoder pool/策略插件为前置要求。测试 sink 的每次返回块须不小于 sizeHint，可以恰好满足请求、跨调用互不连续；不把违约返回短 Span 当支持能力。

### public surface

下列为本次实施签名；对称方法表是 surface 清单，避免复制全部 signed/unsigned 声明。类型/方法不采用语义不明的 `ReadUInt32/WriteUInt32`。

```csharp
namespace Atelia.Binary;

public ref struct BareValueReader {
    public BareValueReader(ReadOnlySpan<byte> source);
    public int ConsumedCount { get; }
    public int RemainingCount { get; }
    public bool End { get; }
    public void EnsureFullyConsumed();
    public string ReadString(int maxPayloadByteCount = int.MaxValue);
    public ReadOnlySpan<byte> ReadBytes(int maxByteCount = int.MaxValue);
    public ReadOnlySpan<byte> ReadRawBytes(int length);
    public string? ReadControlledString(
        int maxStoredByteCount = int.MaxValue,
        int maxDecodedByteCount = int.MaxValue);
    public byte[]? ReadControlledBytes(
        int maxStoredByteCount = int.MaxValue,
        int maxDecodedByteCount = int.MaxValue);
    // scalar Read 方法见下面的对称表。
}

public readonly struct BareValueWriter {
    public BareValueWriter(IBufferWriter<byte> downstream);
    public void WriteString(string value);
    public void WriteString(in StringEncodingPlan plan);
    public void WriteBytes(ReadOnlySpan<byte> value);
    public void WriteRawBytes(ReadOnlySpan<byte> value);
    public void WritePreparedValue(ControlledValueEncodingPlan plan);
    // scalar Write 方法见下面的对称表。
}

public readonly struct StringEncodingPlan {
    public long EncodedLength { get; }
    // private string? value + uint header；由 Prepare 创建。
}

public enum ValueCompression { None = 0, Brotli = 1 }

public sealed class ControlledValueEncodingPlan {
    public static ControlledValueEncodingPlan Null { get; }
    public long EncodedLength { get; }
    // private constructor、storedBody/control/压缩态U；不公开 buffer。
}

public static class BareValueEncoding {
    public static StringEncodingPlan PrepareString(string value);
    public static long MeasureString(string value);
    public static long MeasureBytes(int byteLength);
    public static ControlledValueEncodingPlan PrepareControlledString(
        string? value, ValueCompression compression = ValueCompression.None);
    public static ControlledValueEncodingPlan PrepareControlledBytes(
        ReadOnlySpan<byte> value, ValueCompression compression = ValueCompression.None);
    // MeasureVarUInt16/32/64、MeasureVarInt16/32/64：参数为对应整数，返回 int。
}
```

| ReadX / WriteX 的 X | CLR 值类型 | measure |
| --- | --- | --- |
| Byte / SByte / Boolean | byte / sbyte / bool | 固定表；不为每个固定值增加 Measure 方法 |
| UInt16LE / Int16LE / UInt32LE / Int32LE / UInt64LE / Int64LE | 对应整数 | 固定表 |
| CharLE / HalfLE / SingleLE / DoubleLE | char / Half / float / double | 固定表 |
| VarUInt16 / VarUInt32 / VarUInt64 | ushort / uint / ulong | 对应 MeasureVarUIntX |
| VarInt16 / VarInt32 / VarInt64 | short / int / long | 对应 MeasureVarIntX |

示意消费（Informative）：FrameStore 的 address codec 调用 WriteUInt32LE 与 WriteUInt64LE，保持已确认 12B。VersionStore 可用 VarUInt32(count)、逐项 WriteString(key) 与该 address codec 组合 Snapshot；完整记录的字段/版本/限额仍在各自规范定稿，不能把该示意当已冻结 RootMap wire。
受控使用（Informative）：PrepareControlledString(value, ValueCompression.Brotli) → 用 EncodedLength 预算 → WritePreparedValue(plan) → ReadControlledString。bytes 的 null 显式使用 ControlledValueEncodingPlan.Null；PrepareControlledBytes(span) 表示非 null bytes，empty span 不表示 null。调用方按 schema 选择 string/bytes reader；同一个 plan 类型不承诺自动识别内层类型。

## 独立 golden 与验收向量

每条 spec 映射下表与相应 public 行为测试；golden 手写预期 bytes，不使用待测生产 Measure/Write 来生成预期。

| 组 | 必须覆盖的独立预期或失败边界 |
| --- | --- |
| 固定值 / `[F-BPV-FIXED-LE]` | uint `0x12345678`→`78 56 34 12`；int -1 全 FF；bool 00/01 与 02 拒绝；float 的 ±0、subnormal、Inf、多个 NaN payload 的 raw bits；CharLE surrogate |
| VarUInt / `[F-BPV-VARINT-BOUNDED]` | 0=`00`，127=`7F`，128=`80 01`；ushort max=`FF FF 03`，uint max=`FF FF FF FF 0F`，ulong max=九个 FF 后 01；各宽度末组溢出与最大宽度 continuation |
| 宽容整数 / 同条 | `80 00`、`80 80 00` 接受 0；后接其他值验证只消费终止前缀；UInt32 拒绝 6-byte 冗余零；真实消耗不等于最短 Measure |
| ZigZag / `[F-BPV-SIGNED-ZIGZAG]` | 0=`00`、-1=`01`、1=`02`，每个 signed 宽度 min/max；避免取 abs 溢出 |
| 默认字符串 / `[F-BPV-STRING-DUAL]`、`[S-BPV-STRING-DEFAULT]` | empty=`00`；A=`03 41`；é=`02 E9 00`；中=`02 2D 4E`；😀=`04 3D D8 00 DE`；单 D800=`02 00 D8`；ASCII + D800 不取有损 UTF-8；U+FEFF=`02 FF FE` 不剔除 |
| 宽容字符串 / `[A-BPV-STRING-TOLERANT]` | UTF-16 A=`02 41 00`、UTF-8 中=`07 E4 B8 AD`、UTF-8 empty=`01`，冗余 header `83 00 41` 都接受；非法 UTF-8 拒绝；UTF-8 U+FEFF=`07 EF BB BF` 保留；header=FFFFFFFF 的 UTF-8 byte count 为 int.MaxValue，80000000 是超 int 的 UTF-16 声明，在分配前分类 |
| bytes / `[F-BPV-BYTES-EXPLICIT]` | raw 不带头；empty bytes=`00`；非最短长度；blob payload 含零及任意 bytes；借入结果受源生命周期约束 |
| nullable / `[F-BPV-NULL-CONTROL]` | nullable scalar null=`00`，present=`01 + scalar`；±0/NaN 原 bits；02 不能作为 Boolean present；复合解析用 cursor 副本；不可空入口无控制前缀 |
| controlled wire / `[F-BPV-CONTROLLED-ENVELOPE]` | null=`00`，empty string/bytes=`01 00`；原文 A=`01 03 41`；原文字节串 AA BB=`01 02 AA BB`；压缩 C/U 有界冗余表示；U 包含内层 header；U=0/C=0/未知 control 拒绝 |
| 方法 / `[S-BPV-COMPRESSION-SELF-CONTAINED]` | 独立 RFC 7932 fixture，不用待测 encoder 生成全部 decoder 预期；不同合法标准 window；外部 dictionary/large-window 不作支持；未知 writer enum 在 null 输入时也拒绝 |
| 准备 / `[A-BPV-COMPRESSION-PREPARED]` | 独立算术 U=128 时 C=127→raw129/压缩131，C=125→129/129，C=124→129/128；仅最后选压缩；U=int.MaxValue 的原文完整尺寸=2147483648L；U 超 owned 上限在物化前拒绝；原文和压缩 Prepare 后源数组变更不影响写出；重复 Write 不重新压缩；实际 EncodedLength 精确；Null plan=1B，null class plan 拒绝 |
| 受控读取 / `[A-BPV-CONTROLLED-READ-BOUNDED]` | Brotli 截断/tail garbage/concat、U 过小/过大、合法但无收益表示；U 已满后 scratch 确认结束；C/U limit 在分配前；Raw `01 83 00 41` 内层实际 U=3，limit=2 分配前拒绝，而 `01 03 41` 的 U=2 可接受；内层非法 UTF-8/余字段/截短；失败 cursor 不变、资源释放、raw bytes 仍自有 |
| cursor / errors / `[A-BPV-CURSOR-ATOMIC]`、`[A-BPV-ERROR-CLASSIFIED]` | 所有截断点；恶意超大长度在分配前拒绝；limit=0/边界；失败 cursor 不变；复合记录失败不自动回滚；尾部检查 |
| writer / `[A-BPV-WRITE-PARTIAL]` | 确定拒绝不碰 sink；GetSpan/Advance 故障保留异常；已写部分不承诺撤销；default writer 的空操作也拒绝；保存/副本不延长 sink 生命周期或制造事务 |
| Measure / `[A-BPV-SIZE-EXACT]` | 与实际默认输出一致；MeasureBytes(int.MaxValue)=2147483652L，不分配；plan 复用同一不可变 string；default.EncodedLength/Write(default plan) 拒绝；不能以 Measure 推进宽容 reader |
| chunk / `[A-BPV-OUTPUT-CHUNKED]` | 记录 sizeHint，按实现选定 bound 检查；合规、不小于 hint 的非连续块与更大返回块；边界前后 surrogate pair；大 string/blob、empty；逐字节等于独立完整编码；失败无 rollback 虚假资格 |
| 消费边界 / `[S-BPV-SCHEMA-OWNED]` | 独立 FrameAddress12B fixture + 截短，后接下一字段；RootMap 示例不替代完整 FS/VS codec 验收；无业务依赖 |

## 最小实施顺序与出口

1. B0：创建 Binary 与测试项目，固定值、VarInt、reader cursor、分类错误和基础尺寸；保留独立 golden。
2. B1：string 编码选择/轻量 plan/宽容 reader、bytes，以及所有大输出的 chunk 断点资格；普通原文格式先形成独立资格。
3. B2：独立可空规则、受控 Raw、owned plan，再加入 Brotli Prepare/严格解压与 typed 内层验证；覆盖完整收益、源快照、C/U 和资源/cursor 断点。Zlib 不作为出口前置；不增加 Tagged、BCL 全家桶、公共 ByteBlock 或旧 adapter。
4. B3：以纯 public API 做 schema 组合 smoke：固定 12B address fixture、带 string keys 的小字典、受控 string/blob、已知尺寸预算与读回；不因此宣称 FrameStore/VersionStore 已创建。
5. B4：注册主线 pack 清单，隔离 Binary PackageReference smoke 检查闭包，原三包 smoke 保持；无需强制 Data/RBF 反向依赖 Binary。后续 FS/VS、SJ、DG 分别接入，不做横向一次迁移。

源码资格按仓库 Release build 后匹配 --no-build tests 串行取得；package 资格单列。真实 I/O 若需要使用 W: SSD，但此 codec 首先是内存测试。下文草案阶段的隔离 BCL 机制探针只证明其机制；本次实现与验收证据以[实施验收](bare-primitive-value-acceptance.md)为准，不能将探针标为新 codec 实现通过。
本次已确认上述 public 名称与签名、独立 Binary 程序集、默认/异常合同和 golden，实施不再临时改变首片整型布局、控制格式或字符串选择。性能预算先记录编码总 bytes、Measure/Prepare/Write/Read 各自 CPU 与 allocation，分别记录 Raw 与 Brotli、准备期间峰值和计划保留量；没有测量时不宣称自有格式比 CBOR 更快。

## 证据与外部依据（Informative）

调查基线：storage `62ba7d3` 加当前未提交设计修订；DurableGraph checkout `c900edd`；StateJournal checkout `76a6afa`。跨仓源码通过其工作副本读取，本文件不是那些库的格式权威或迁移验收。

- DurableGraph 的 [BinaryPayloadWriter](../../../durable-graph/src/DurableGraph.Serialization/BinaryPayloadWriter.cs)、[BinaryPayloadReader](../../../durable-graph/src/DurableGraph.Serialization/BinaryPayloadReader.cs)、[CanonicalVarInt](../../../durable-graph/src/DurableGraph.Serialization/CanonicalVarInt.cs)、[StringPayloadCodec](../../../durable-graph/src/DurableGraph.Serialization/StringPayloadCodec.cs) 是实现参考：VarInt 拒绝 nonminimal、string 读后重新验证最短，本草案明确放宽这两项，未声称行为相同。
- StateJournal 的 [VarInt](../../../atelia-statejournal/src/StateJournal/Serialization/VarInt.cs) 最宽终组零与其他路径处理不一；[StringPayloadCodec](../../../atelia-statejournal/src/StateJournal/StringPayloadCodec.cs) 默认 UTF-8 replacement 可损失未配对 surrogate。它们说明需要统一合同，不代表本轮已修复这些库。
- 既定 [FrameAddress codec](../FrameStore-VersionStore/02-framestore-core.md) 与 [RBF 尺寸/early ticket](../FrameStore-VersionStore/01-rbf-sized-append.md) 由消费层继续拥有，本层不重复其格式常量或身份保证。
- [CBOR RFC 8949 §4.1](https://www.rfc-editor.org/rfc/rfc8949.html#section-4.1) 区分 preferred writer 与接受其他表示的 decoder；这是设计参照，本方案不是 CBOR 子集/兼容格式。
- BCL 的 [IBufferWriter](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.ibufferwriter-1?view=net-10.0) 定义 sink 协议；[Encoder.Convert](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoder.convert?view=net-10.0) 可用于有状态分块编码。具体 fast path 仍需施工验收。
- 冻结参考 [EventPayloadCodec](../../src/EventJournal/EventPayloadCodec.cs) 的实际方法为 Brotli/Zlib，q3/w22、Zlib Optimal 与收益阈值是旧 writer 经验；本草案不依赖 EventJournal，也不照抄其解压资格或旧方法编号。
- [RFC 7932](https://www.rfc-editor.org/rfc/rfc7932.html) 定义 Brotli 流；[BrotliDecoder.Decompress](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.brotlidecoder.decompress?view=net-10.0) 提供状态及输入/输出计数。[.NET v10.0.0 DeflateStream](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/DeflateZLib/DeflateStream.cs#L266) 展示默认截断检查受全局 switch 控制及输入预读，不能用 BaseStream.Position 证明实际流消费。

## 首次基础草案辩证审阅（Informative）

初稿由 demand skeptic、minimal architect、semantic defender 分别完整阅读并检查源码，第二轮交换最强反例；主线程独立复核 RbfPayloadWriter 的逐次 epoch guard、相关源码和 wire 算术后修订。两轮已收敛，无需为未出现的新需求追加第三轮；本节是裁决记录，不是额外规范输入。

| 裁决 | 最终处理 | 依据或删减会造成的失败 |
| --- | --- | --- |
| simplify | writer 从 ref struct 改为 readonly struct，只持 sink 引用 | ref 限制不能证明 Builder 未归还；实际资格由 sink guard 执行，不增加缓存/事务状态 |
| merge / keep | 可选 string plan 只保留 string + header；default getter 也拒绝 | 其他字段可推导；default=0 会伪造预算，预编码完整 bytes 又增加分配；没有把 plan 说成正确性的前提 |
| simplify | 有界 sizeHint 保留，16KiB 降为内部初值 | 整块请求可强迫大连续租借；具体最佳块长没有测量证据，不冻结 public 调优值 |
| keep | 宽容有界 VarInt、两种合法字符串表示、严格 UTF-8、失败 cursor 不推进 | 无限扫 continuation 会越过字段；replacement 会改变 key；失败后提前推进会破坏重试/定位 |
| simplify | 补 checked ZigZag、uint/long header 算术、U+FEFF 内容与输入借期说明 | 边界转换可溢出，BOM 剔除会丢内容，sink 操作可让别名 source 失效；无需新 API |
| defer | Guid/decimal/时间、全套 nullable 标量 helper 在首个迁移片需要时定稿 | 有真实中立消费者，永久排除会继续重复编码；首片也无需先定义全套 BCL 类型 |
| defer | Tagged、自有 tagged wire、canonical 验证、通用 AST/registry/策略开关 | 当前 Bare schema consumer 无此前置需求；新增它们未解决本片已知故障 |

独立 Python 静态验算检查了 7 个默认 string golden（含 U+FEFF）、三种整数宽度、15 个 ZigZag 边界和 header/long 尺寸边界；它不是生产 codec 测试、吞吐实验或 package 资格。该次基础 surface 为四个类型，本次受控扩展新增一个 enum 与一个 sealed plan，共六个；没有以文档缩短或 reviewer 票数宣称性能更好。

## 受控压缩辩证审阅与机制实证（Informative）

本轮由 demand skeptic、minimal architect、semantic defender 三个 reviewer 独立读取完整草案和 EJ/BCL 源码，第二轮交换 A/B 包装、Zlib 资格、prepared 资源和 null/empty 的最强反例。semantic defender 撤回独立 ByteBlock 优先与 typed U=0 可读的意见；minimal architect 撤回首片公开 ZlibOptimal/BrotliFast preset 的意见。主线程以源码与下面的隔离实验裁决，两轮收敛，不追加第三轮。

| 裁决 | 最小保留或修订 | 具体理由 |
| --- | --- | --- |
| keep | 可空性独立；control 仅描述 null/解压方法；C 后 U | 用户确认，长度不再兼任存在性，不将编码参数变成 reader 分支 |
| simplify | typed control + 完整 Bare 值，Raw 无新长度 | 独立 ByteBlock 给原文 string 重复边界，未找到独立 consumer；内层保真/EOF 足以解决已知失败 |
| keep / simplify | 私有 owned storedBody、派生精确长、写同一快照 | 借入可变源使预算/值漂移；public pool lease 添加归还状态；整份 wire 再复制没有必要 |
| keep | C/U 双限额、Done/消费/输出、typed 内层与 cursor 原子 | 完整输出长度不能证明完整流；预读位置不能证明全部消费；内层解析失败不能提前提交 cursor |
| simplify / defer | 不新增控制读写族、全 nullable helper、公共 ByteBlock/AST/策略矩阵 | nullable 标量可组合既有基元；受控首片只新增两个类型，不展开所有假想消费者 |
| defer | Zlib 及其他方法，不分配待实现 control | 当前公开 BCL 路径不能直接满足精确完成与消费；后续需新公开机制/实证及独立收益，不永久排除 |

机制探针源码、default/strict 原始 JSONL 保留在 [W: 实验目录](W:/atelia-binary-compression-probe-20261008-9a0f19da/)。源码 SHA256=`482ec2428791068a9c842726dcbe5b5ea4d2919a4bb5fd04b06a41ce8bdaeaf4`，SDK=10.0.201，运行时=.NET 10.0.5，Release。输入是 50000B 文本，全部压缩/解压在内存；Brotli q3/w22 得 42B，Zlib Optimal 得 303B。这是此输入的机制记录，不是性能、实际 I/O 或新公共 codec 验收。

| 输入 | Zlib 默认验证：U 输出后再 ReadByte | 启用全局 strict switch | Brotli instance 的 Done + C + U 检查 |
| --- | --- | --- | --- |
| 完整流 | 50000B、EOF -1 | 相同 | Done / consumed=42 / written=50000，通过 |
| 截最后 1/4/5B | 仍 50000B、EOF -1 | InvalidDataException | NeedMoreData，拒绝 |
| 尾加 3B garbage | 50000B、EOF -1，source.Position=306 | 仍接受 | Done 但 consumed=42≠C=45，拒绝 |
| 串联两个完整流 | 50000B、EOF -1，source.Position=606 | 仍接受 | Done 但 consumed=42≠C=84，拒绝 |

因此不使用应用全局 switch、每次只喂 1B 的性能妥协或私有 runtime 访问来硬塞首片 Zlib。旧 EJ 仅作为经验来源，不在本轮修复。本次产品实现须覆盖 `[A-BPV-CONTROLLED-READ-BOUNDED]` 的所有断点、独立压缩 fixture、边界与资源测试，不能以这个探针代替它们。
