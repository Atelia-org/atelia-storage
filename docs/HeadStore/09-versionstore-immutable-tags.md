# S5-B 专项：HeadSetTag 具名固定成员表

状态：2026-10-17 当前合同采用格式3与引用初始化 fork；16B成员表分别保存所选 Head 与实际 Entry owner。实际本轮源码、进程与包资格由[现有验收记录的新增轮次](16-headset-tags-acceptance.md)维护；未完成验证不沿用旧 Accepted。格式2/HeadSetTag 首轮及更早 bytes/Related 的证据保留原 revision。

本篇负责成员表、名称/桶、独立元信息、稳定捕获、依赖确认、查询与扫描。统一版本3、身份、借入 data、根/锁、pending、单 driver、ProcessCrashOnly、PublicationOutcome 与清理消费 [S4](04-versionstore-publication.md)。HeadEntry bytes 的长度和所有权由 [11](11-versionstore-root-payload-bytes-api.md)负责；tag 不再保存任意 bytes。

## 需求、边界与公开结果

### spec [S-VS-TAG-PAYLOAD-IMMUTABLE] tag 固定所选 Head 的当前位置

HeadSetTag MUST 为不可变的具名 `HeadId → HeadEntryId` 表。CreateTag 在一次同步操作中捕获所选非空 Head 集合的当前已接受 Entry；成员可以只有一个；key 是所选 Head，value 是实际已接受 Entry，允许 key != value.HeadId 以及多个 keys 共用一个 value。后续发布不移动成员位置；不复制 Entry payload，不分配或推进 Head，不建立 HeadSet 身份，不提供覆盖、删除、改名、同值幂等创建或历史 EntryId 任意组合入口。

```csharp
AteliaResult<HeadSetTag> CreateTag(
    string name, ReadOnlySpan<HeadId> heads, out PublicationOutcome outcome);
AteliaResult<HeadSetTag> ReadTag(string name);
AteliaResult<HeadSetTagInfo> ReadTagInfo(string name);
AteliaResult<HeadSetTagScanEnd> ScanTags(
    Func<HeadSetTagInfo, bool> visitor, CancellationToken cancellationToken = default);
```

HeadSetTag 为 sealed、自有不可变对象，内部构造，具有 Name、RecordedAtUnixMs 和 `IReadOnlyDictionary<HeadId, HeadEntryId> Entries`。实际 backing 不经正常接口或集合 cast 暴露修改能力；不保留 owner、lease 或输入数组，不需要 Dispose。输入顺序不是语义，也不承诺 Entries 枚举顺序。正常 missing 返回 TagNotFound，不返回 null/default 成功。

旧 `CreateTag(name, payload[, relatedRevision], out outcome)`、ResolveTag、RootPayload、TagInfo/TagScanEnd 及 Related 公面删除，不保留兼容 alias。原 `[A-VS-TAG-RELATED]` 条款随版本1退役；新成员是正式引用，不能按旧坐标注记解释。读取完整成员表可恢复 HeadEntryId，但 ID 仍是定位值，成员当前健康由 ReadEntry/Audit 另行检查。

## 名称接受合同与 codec

### spec [S-VS-TAG-NAME] 非空名称按 Ordinal 原样比较

TagName MUST 为 non-null .NET string，按 `string.Length` 接受 **1..1024 个 UTF-16 code units**。HeadStore 提供 `public const int MaxTagNameCodeUnits = 1024`，不提供实例配置；CreateTag、ReadTag 与 ReadTagInfo 在文件 I/O 前共用准入，超长查询不能探桶后报 TagNotFound。相等使用 StringComparer.Ordinal；Release 与 release 可以分别创建。MUST 原样保留 code units，不 trim、normalize、大小写转换或 replacement。空串被拒绝；非空空白、NUL、控制符、路径分隔符、U+FEFF 与未配对 surrogate 均合法。长度单位不是 scalar、grapheme 或编码字节数。

名称是字段，不成为路径组件，不继承 branch 或历史旧栈的 ASCII、128B、文件名字符限制。当前版本3统一拒绝版本1/2格式门；即使旧库没有 tag，也不猜测旧布局或迁移成员表。

### spec [F-VS-TAG-NAME-BPV1] 名称复用普通无损字符串

HeadStoreFormatVersion=3 MUST 选择 [BPV1 ordinary BareString](../Binary/bare-primitive-value.md)。writer 使用 BareValueEncoding.PrepareString 与 BareValueWriter.WriteString(in plan)；reader 使用现成 BareValueReader.ReadString(3072)。不使用 nullable/control、压缩、独立名称版本或手写 UTF 编码。

BPV 保留完整 UTF-16 code units，未配对 surrogate 可走 UTF-16LE；默认 writer 选择较短无损 payload，持平选 UTF-16LE。reader 接受另一种合法字符串表示及有界冗余前缀，非法 UTF-8 拒绝。相等、重复与路由基于解码 code units，不能以 wire bytes、默认编码重算或 hash 相同代替 fullname 等值。

## 成员表、TailMeta 与容量

### spec [F-VS-TAG-MEMBERS] 固定宽度 canonical 成员表

TagRecord Payload 按 selected LocalHeadId 严格递增保存成员，每项为 `uint32 LE selected LocalHeadId + uint32 LE entry-owner LocalHeadId + uint64 LE SizedPtr.Packed`，恰为16B。两个 ID 均非零，Store 身份由 checked 桶 header 绑定，不重复到每项。EntryCount 只在 TailMeta 保存一次；完整解码要求 `PayloadLength == 16L * EntryCount`，拒绝零 ID、重复/乱序 selected ID、default/零长度 ticket、截短或尾随 bytes；不同 selected keys 可以引用同一 owner/ticket。ticket 保留完整 Packed bits，使用 Packed/FromPacked，不替换为 offset、时间或应用 FrameAddress。

成员表解码只证明本条完整记录中的字段资格，不遍历成员文件。其内部 HeadEntryId 恢复依赖 checked 格式、身份、完整 RBF CRC 与正常写入协议；普通 ReadEntry/ForkHead 保持 S4 的精确读取和来源协议，不因本片增加全链定位扫描。Audit 保持全库物理与引用健康检查。

### spec [F-VS-TAG-METADATA] 独立 CRC 的可变长元信息

统一版本3与 FrameTag 选择 schema，不由 TailMetaLength 猜版本。TailMeta 依次保存：

| 字段 | 编码与资格 |
| --- | --- |
| RecordedAtUnixMs | int64 LE；私有准备阶段一次取 UTC Unix 毫秒，允许负值、重复和回退 |
| EntryCount | uint32 LE；正数，且须通过真实 TailMeta 长度下的自然容量检查 |
| TagName | BPV ordinary BareString 完整 codeword |
| MetaCRC | 4B LE CRC32C，直接复用 RollingCrc 默认 SealCodewordForward / CheckCodewordForward |

时间意义消费 [08](08-versionstore-record-metadata.md) `[S-VS-RECORD-TIME]`，不复制成员时间，不表示 flush 完成时间或事务顺序。writer 在 RBF escape 前对逻辑 meta 封口，reader 对已解 escape 的 meta 校验；独立 MetaCRC 不能替代完整 RBF PayloadCRC。

CRC 后必须精确解码时间、count 与名称，exact-consume，拒绝多余字段。BareString 的另一合法表示及有界冗余前缀保持可读，实际 ConsumedCount 决定边界，不按 writer 默认编码重算。ReadString(3072) 限制分配前字节域，随后仍检查1..1024 code units；可能暂时产生3072个ASCII chars，不声称分配前已限制为1024 chars。

独立逻辑meta golden（RBF escape前）：time=0、EntryCount=1、Name=A，完整codeword为 `00 00 00 00 00 00 00 00 01 00 00 00 03 41 CE 6A AC 1E`，18B，CRC32C=`0x1eac6ace`。不是用同一生产encoder round-trip代替独立向量。

### spec [F-VS-TAG-CAPACITY] 自然容量先于集合分配

记 n 为成员数，N 为实际名称 codeword 长度，T 为真实完整 TailMeta 长度。名称 codeword 为2..3077B，因此 `18 <= T <= 3093`。所有算术先用 checked long：

```text
n > 0
PayloadLength = 16 * n
16 * n + T <= RbfFile.MaxPayloadAndMetaLength
RbfFile.MeasureWriteSize(16 * n, T) 成功
本次真实追加位置与完整 FrameLength 满足 SizedPtr 范围
```

不增加 MaxTagEntries 或任意成员数硬限，也不使用 MaxHeadEntryPayloadLength 的1MiB界限制成员表。入口依据真实 Span.Length、名称计划和公共尺寸先检查，再复制实际 slice；元信息解码在按声明count分配任何集合前先验证自然容量。reader 使用真实 T/N，不能用默认编码 Measure(decodedName) 代替；合法输入仍可能OOM，不承诺固定RSS或流式公面。

**小 meta 读取前**只检查可靠结构、RBF3、TagRecord kind、非tombstone及18..3093的T；即使实际PayloadLength不等于16*n，也应继续小meta资格，不提前租完整帧。CRC、精确字段结构、名称/路由与count语义全部合格才有可信名称。count=0或超自然容量使整份meta不合格：named查询为unknownName，ScanTags失败，不能从半合法meta抢救名字。

**完整表读取前**，ReadTag 在元信息扫描正常EOF且得到唯一可信目标后，检查16*n与实际payload长度、公共Measure与真实FrameLength，再租完整帧，验证完整RBF CRC、元信息及canonical成员表。元信息整体合法但表坏仍占名：ReadTagInfo可返回声明，CreateTag拒绝覆盖，ReadTag/Audit失败。不得以健康同名副本替代。

## 稳定路由与正式路径

### spec [F-VS-TAG-ROUTING] 按原 code units 固定分桶

HeadStoreFormatVersion=3 MUST 固定 FNV-1a 64-bit：逐个原 UTF-16 code unit 按 low byte、high byte 输入，不加 BOM、terminator 或长度前缀；U+FEFF/NUL 原样参与。初值14695981039346656037，每 byte 先 XOR 再乘1099511628211，算术取模2^64。固定 `B=1024`，`BucketId=(hash ^ (hash >> 10)) & 1023`，范围0..1023。路径为 `tags/<BucketId 的8位小写 ASCII hex>.rbf`，如 `tags/00000174.rbf`；BPV codeword 不参与 hash，不保存 hash/B/算法名，不使用 string.GetHashCode 或 replacement surrogate 的文本编码入口。

相等名称必同桶、跨进程稳定，hash/桶碰撞仍按 Ordinal fullname 区分。这些参数绑定统一格式版本，不能随运行配置或实现更换。FNV 常量与 XOR folding 形式见 [RFC 9923 §2–§3](https://datatracker.ietf.org/doc/html/rfc9923#section-3)；这里固定一次折叠，不宣称全64-bit折叠或分布最优。1024桶限制潜在文件数，每个实际创建桶增加一个 header，仍不轮转。

独立向量（脚本复算，V3-A 纳入 codec 验收）：A → hash 0908a207b5a15704 / bucket 00000351；Release → c5566333f1504164 / 00000174；release → bc7f822919cbbb44 / 000001aa；单个 code unit U+D800 → 0831b007b4ea00e5 / 00000265。tag-48 与 tag-65 都为00000106，须分别创建/解析。BPV `03 41`、`02 41 00`、`83 00 41` 均为 A，必须得到同一 hash/桶，不把宽容读取改成 wire 规范化。

tags 是 S4 根准入认识的**可选普通目录**，初次建库不预建；只读 Open/ReadTag/ReadTagInfo/ScanTags 不创建。首次合法 CreateTag 在确定准入与 data barrier 与必要发布文件确认后、私有初始化前 create-only 建立它，取得 ordinary/no-follow 与同文件系统资格；已存在组件须精确小写且合格。目录存在不表示已发布 tag。

直接项只允许上述8hex、桶号0..1023的普通 .rbf 文件，不允许子目录；零号桶合法。沿用 S4 精确组件、全部直接项名称/类型与 no-follow 资格，拒绝别名、未知名称/后缀、特殊类型及枚举错误，不能以 File.Exists=false 吞权限/I/O错误。named 请求只打开目标桶；其他桶内容不由本次证明。确定缺失目录/目标桶才是健康缺失；header-only 桶为空，bare Fence/坏 header 不是空桶。ScanTags 另遍历实际已建桶，不打开1024个不存在的桶。

## 桶组合 schema 与初始化保护

### spec [F-VS-TAG-BUCKET] 首帧 header 与后续记录

每桶一个 RBF3 文件、不轮转；统一版本与16B canonical HeadStoreId 消费 S4 [F-VS-OWN-FORMAT]。FrameTag 固定0（BucketHeader）与1（TagRecord），均非 tombstone：

| 帧 | Payload | TailMeta 与必要资格 |
| --- | --- | --- |
| 首物理 BucketHeader | uint32 LE HeadStoreFormatVersion；canonical HeadStoreId；uint32 LE BucketId，共24B | meta0；完整 CRC；版本、身份、桶号匹配 checked 门与路径，exact-consume |
| 后续 TagRecord | canonical HeadId → HeadEntryId 成员表 | 本篇可变 meta；独立 MetaCRC、exact-consume、合法 name/本桶路由/EntryCount；ReadTag 另需完整 CRC 与成员表资格 |

统一版本选择桶 header、记录与 meta schema，不选择 HeadEntry 的应用内容 codec；可变T只是尺寸。首 header 版本/身份/形状或尾随错误拒绝桶。后续未知 kind、重复 header、tombstone 或坏 meta 在 named 查询中属于未知名记录，在 ScanTags 中属于必要错误；不增加独立桶/记录版本、名称 CRC、索引或泛用 audit API。

named 入口与 ScanTags 的可写桶打开共用 S4 `[A-VS-RECOVERY-REPORTS]` 的诊断通道。成功 RBF 修尾的非 None 动作在本篇初始化恢复保护和必要记录校验之前入账；后续拒绝不撤销动作。报告用 TagBucket/本地桶号标识，不按 tag 名产生第二份台账，缓存/当前扫描桶的借用不重复报告。

以 checked 门、本次 HSID/BucketId 编码 header，令 H=RbfFile.MeasureWriteSize(24,0)，I=RbfScanBoundary.Empty.EndExclusive+H.AppendLength。共同首帧检查使用 ScanForward(showTombstone:true)：要求 Rbf3、首物理 offset、H.FrameLength、payload24、kind/meta/tombstone 匹配，再完整 CRC、精确解码比对；checked 后界须为I。缺首帧先查 TerminationError，不找后续替代。

可写正式桶在 public RbfFile.OpenExisting 前用公共文件长度入口拒绝实际长度<I，先关闭探测句柄再进独占工厂。非 None 恢复须有 AffectedFrameOffset>=I，随后仍共同检查 header；只允许 header 后残尾恢复，不补造/认领 header。header body 完整但缺原 Key/Fence 也须在I前检拒绝，不能先 CompletedTail、下次 Action=None 收养。长度足够但 header 内容坏仍拒绝，不承诺底层尾恢复前完全不修改坏文件。只读用 OpenReadOnlyExisting，残尾拒绝且不修复。所有成功资格均消费公共工厂正常EOF/恢复边界，不另绕过工厂。

## named 查询：唯一可信目标与坏项隔离

ReadTag、ReadTagInfo 与 CreateTag 查重共用内部同步元信息扫描，只裁决请求名，扫描到正常EOF，不保留跨调用索引/负缓存。所有 named 入口先检查生命周期和名称；CreateTag 另消费写入准入。只打开目标桶，但先检查所有正式桶路径的名称/类型资格；成功不证明其他桶内容健康。

沿真实主链逐项读小meta；结构可靠且完整读取后的CRC/codec/count/name/路由内容错误可隔离为unknownName。可信非目标不读成员表，可信目标计数饱和于2，保存唯一目标info与定位；命中后仍继续扫描，MoveNext=false须检查TerminationError。逐项归还meta。

| 正常EOF后的结论 | ReadTagInfo | ReadTag | CreateTag |
| --- | --- | --- | --- |
| 可信目标重复 | 重复失败 | 重复失败，不选健康副本 | 已占/重复拒绝，NotAttempted |
| 唯一可信目标，允许unknownName=true | 返回自有info，不证明表或成员健康 | 该目标完整帧和canonical表合格才交付；不读取成员 | 已占拒绝，NotAttempted，不读表 |
| 零可信目标且unknownName=true | 缺失不确定 | 缺失不确定，不能TagNotFound | 查重不确定，无barrier/输出 |
| 零可信目标且unknownName=false | TagNotFound | TagNotFound | 允许继续创建 |

“唯一”指唯一**可信目标名**，不是要求桶内所有记录名字均可识别。unknownName在目标前后均不能阻断唯一可信目标；它只阻断零命中时的健康缺失/创建判断。可信其他名的坏表不影响目标，其他名称重复由Audit检查。

隔离只适用于有可靠结构且读取完成的内容错误；framing/TrailerCRC/Fence/TerminationError、真实I/O（含meta short read）、OOM、生命周期与必要close错误必须传播，不按异常基类统一catch。结构坏不resync，header坏不跳过。buffer内codec截短不是文件短读。

每调用仅保留桶句柄、当前小meta、唯一目标值/定位及常数状态；ReadTag另有当前完整表与自有字典。finally先取出/清空owned槽再关闭，主异常优先且继续清理。从history/scan visitor调用时使用独立局部槽，匹配当前扫描桶借用其已合格句柄，不重复打开或关闭borrowed句柄。

## 元信息公面与扫描

### spec [A-VS-TAG-INFO] 元信息不替代完整表或成员资格

HeadSetTagInfo 为sealed、自有不可变、内部构造对象，仅有 `Name: string`、`RecordedAtUnixMs: long`、`EntryCount: int`。没有Entries、payload、Related、owner、lease或Dispose；EntryCount为经元信息资格检查的声明，不表示已遍历成员。ReadTagInfo按上述唯一可信目标规则返回，坏表而健康meta仍可成功。

### spec [A-VS-TAG-SCAN] 物理元信息遍历与窄重入 guard

ScanTags pre-I/O检查生命周期、非null visitor、取消和无其他活动扫描，遍历实际已建桶，对每条记录取得完整meta资格，不读成员表或成员Entry，不建全names集合、不排序。重复声明逐条交付，遇必要坏meta直接失败。Complete只证明所访问全部meta、正常EOF与必要清理，不证明全局唯一、完整表或成员健康。

每项构造自有HeadSetTagInfo、归还pooled meta后调用visitor。false表示接收该项后VisitorStopped，不推进后缀；callback前后、继续I/O前和最终交付检查取消/状态，取消、异常、Dispose/fault与必要close失败优先于正常停止。已交付值不回滚，最终失败不称部分成功；纯读/取消本身不fault，owned恢复/清理失败仍停用。

TagScanActive拒绝递归scan、进入ReadHeadHistory及全部mutation；允许ReadHead、ReadEntry、ReadTag、ReadTagInfo、ListHeadOrigins。history callback可scan；两类遍历有独立临时槽，同桶查询借用当前句柄。Dispose先失效owner并取出/清空槽，finally只清理剩余owned资源；所有退出释放扫描guard，主异常优先、资源一次清理、根锁最后。

## 稳定捕获、确认与三态证据

### spec [S-VS-TAG-CAPTURE] 先选择当前位置，再确认必要发布依赖

CreateTag 第一条语句设置outcome=NotAttempted，检查owner/可写/history/scan准入及名称。进入窄TagCreateActive guard，finally退出；所有mutation在活跃时拒绝，包括递归CreateTag，返回HeadStore.TagCreateActive。guard覆盖取时和内部hook，不是线程锁或通用操作框架；不公开callback，不新增创建期间任意查询支持，Dispose不被mutation guard拒绝。

按自然容量预检并复制HeadId有效slice，拒绝空/default/外库/重复，按LocalId排序。名称查重和真实输出位置预检后，用既有current-read内核为每个选择取得已接受EntryId及普通/CU依赖；缺失/必要损坏/I/O失败立即终止。未成立CU只按07明确见证跳过，不回退掩盖损坏。必要peer先正常打开/恢复再裁决，不能把可CompletedTail的peer先作缺席；不扩大为全库preflight。

准备完整成员表、meta及自有返回对象后，先data.ConfirmDurable，再按LocalId顺序确认去重后的必要发布文件D，最后输出TagRecord。所选集合S与D不同：只选A且A来自A/B CU，Entries只有A，D仍含A/B。只收集最终被接受Entry的依赖，不把跳过未成立组时触及的全部文件当成员。D 始终包含所选 Head 的正式文件；普通Entry另依赖实际 owner，成立CU另依赖该组全部文件。仅选择 header-only C，C→A的CU且peer为B时，Entries只有 C→A-entry，D为{C,A,B}。不把所选身份从 value.HeadId 反推，也不追确认全部祖先。应用data闭包仍由应用保证。

确认复用已有owned句柄，其余逐个短期开启/确认/关闭，最多增加一个临时确认句柄，不缓存持久“已确认”证明或整组/全库句柄。k个选择、d个去重依赖需O(k+d)标识集合与O(d)确认；current-read可重复访问同组，不承诺总读取O(d)。每次hook/可执行回调及owned I/O后复检owner，发现Dispose/fault后不继续下一I/O；成功恢复继续入RecoveryReports。

已接受CU不证明其旧调用所有flush已返回；本次D确认满足data-first的顺序合同。它不证明少flush peer必在普通进程终止后丢失，不新增掉电保证。PublishHeads与CreateTag是两个操作；前者成功而后者失败合法，两个单独发布之间捕获的组合是实际当前状态，不提供发布并命名事务。

### spec [S-VS-TAG-PUBLICATION] 空桶维护与正式记录输出分开

data/依赖确认、目录建立和空桶初始化仍为NotAttempted。缺桶按既有pending header、flush/close、no-overwrite rename流程发布合法空桶，再Append完整表/meta。正式Append开始前置Unknown；公开Append失败Result若证明无输出可恢复NotAttempted。桶DurableFlush正常返回后立即无分配置Confirmed，随后hook/状态复检/close/交付异常不降级。确定拒绝和纯准备失败不新增fault，实际owned I/O/确认/清理错误按S4停用，borrowed data不被Dispose。

空桶保留，pending可写取消/只读保留，不续请求、不补首tag。完整TagRecord永不删除回滚；header后的部分body可截回，完整body缺Key/Fence可恢复，之后仍重新检查meta/表。RO残尾拒绝不修复。Unknown后关闭重开，用ReadTag检查实际名称与成员表；不自动重新选取当前Heads或机械重试create-only，也不反推旧调用Confirmed。

## Audit、成本与验收

### spec [A-VS-TAG-AUDIT-MEMBERS] 完整表及成员引用必须可读

Audit消费S4的只读全库物理/名称/来源检查，并逐个验证每份HeadSetTag的成员引用。canonical表合格不等于 key/value 的引用健康。selected Head 必须存在；同Head value须为其实际发布 Entry，跨Head value须与该selected Head的固定ForkOrigin精确相符，且准确来源Entry已发布。不要求等于今日当前值，不重放创建时刻。不存在key文件/ticket、错来源、坏成员、必要peer错误或未成立CU必须失败。检查 key/origin 复用本次 Audit 的 checked Head 节点表，不逐成员重开 selected 文件。用内部精确读取复用07判据，不循环公开ReadEntry填永久cache、不递归检查peer后来的组，不解码应用payload/data图。

全部Head主链检查成功后，跨Head成员复用已证明的ForkOrigin主链资格；同Head成员在既有短期owner句柄上，另从首帧正扫至完整ticket相等，证明它属于真实主链。扫描只补结构成员见证，不重读前缀payload或重复计EntryCount；遇到TerminationError必须传播。每tag的key唯一，同Head时key就是owner，因此每个同Head成员只需一段结构前缀与O(1)额外状态，不收集全库tag引用；不同tag引用同一旧点可重复扫描。完整CRC和尾Fence可读的离线内嵌frame镜像仍须拒绝；这种镜像违反正常RBF3 writer的marker-free输出，不作为真实写入/进程崩溃轨迹，也不扩大普通ReadEntry/Fork的全链前置要求。

桶扫描句柄与短期成员槽分开；成员及CU peer沿既有短期资源机制逐个访问。cancellation/state检查必须传入内部exact/CU路径，在成员间、文件打开前后、完整读取/peer工作前及最终交付前执行；取消中断不返回完整计数。局部pooled帧和临时文件先清理，根锁最后；已Dispose/fault后不得继续I/O，cleanup失败不被正常完成/取消路径吞掉。主异常优先、各资源只清理一次。纯只读/取消不新增fault，owned清理失败仍停用。

TagCount/TagBucketCount计新成员表及实际桶；验证成员时不重复增加EntryCount。所有统计只在全库遍历、成员引用检查与必要清理成功后交付。ForkOrigin须命中实际主链中的已接受Entry，不再比较子克隆，不把它复制成普通ReadEntry/Fork的全链前置扫描。

named查询为O(目标桶记录数+meta字节)，连续同桶创建可为O(n²)；ReadTag只为唯一目标完整表增加读取。扫描仍为全部实际桶meta，成员/时间筛选不变索引。没有持久索引、轮转、确认缓存、并发读快照或总RSS保证。

验收细目和具名过程证据由[16验收记录](16-headset-tags-acceptance.md)维护：格式3/拒1和2门与混入旧header、16B独立key/value映射、共享来源/连续空fork、成员canonical/自然容量、唯一可信名隔离、输入slice与自有值、CU依赖确认顺序、hook重入/Dispose、Audit取消/cleanup与新增进程窗口。旧bytes tag证据仅作历史依据，不重标本片Accepted；Release、实际进程终止、平台与隔离包消费分别记录。
