# S5-B 专项：不可变 tag

命名更新：2026-10-16 活跃合同采用 HeadStore/Head/Entry；既有 Accepted 仍对应原 VersionStore revision，本轮重命名验证见[13记录](13-headstore-renaming-plan.md)。格式版本、路径与 Clause-ID 不变；[14 HeadSetTag](14-named-head-snapshots-plan.md)尚未实施。

日期：2026-10-11。状态：**Accepted（既有独立 source-only 源码资格）**。1024个惰性桶、独立元信息CRC、匿名/关联 CreateTag、ResolveTag、ReadTagInfo/ScanTags及完整检查的证据见[最终源码验收](12-versionstore-final-acceptance.md)。V4-F补充具名内部进程窗口及六个独立名称/路由golden的冷进程回读，平台结果见[实施计划](12-versionstore-implementation-plan.md)；隔离包消费仍是独立交付出口。该设计取代未实施的tag草案，无旧tag数据迁移，不改Head/CU布局；CU 已正式接纳，其新增资格见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。Related 仍只是坐标注记，不重新验证 CU 来源或共同判据。

本文属于 [S5](05-versionstore-names-and-indexes.md) 的 S5-B，不新增项目或阶段。统一版本、持久身份、借入 data、根/锁、唯一 pending、single-driver、ProcessCrashOnly、PublicationOutcome 与停用清理由 [S4](04-versionstore-publication.md)定义；bytes 输入与自有结果由 [11 专项](11-versionstore-root-payload-bytes-api.md)定义。本篇独立拥有 tag 名称、桶、TailMeta、关联注记与元信息公面，不复制下层尺寸算法，不增加并发或断电保证。

## 需求、边界与公开结果

名字标识创建时的一份完整 root-payload bytes，create-only，不分配 HeadId，不随 Head 更新，不提供删除、改名、覆盖或同值幂等创建。可选关联只是创建时记录的 Head 坐标注记；应用需要精确历史选点与来源复制时使用 S5 的 HeadEntryId/ForkHead，不能从 tag 内容同值或关联坐标推出来源资格。

### spec [S-VS-TAG-PAYLOAD-IMMUTABLE] tag 一次冻结完整 root bytes

每个 tag MUST 保存一条 TagRecord：Payload 仅为原样 root-payload bytes，长度 1..1MiB；TagName、记录时间与可选关联放在 TailMeta。应用 codec 与 data 依赖闭包由应用负责，tag 不解码 NamedFrameMap/JSON。同名拒绝，即使 bytes 同值；关联注记不改变内容不可变性。

CreateTag 的两个 bytes 入口与 ResolveTag 唯一消费11：CreateTag 返回 `AteliaResult<bool>`，成功恒为 true，不回显输入；ResolveTag 返回自有 GC array 支撑的 `AteliaResult<RootPayload>`，只交 bytes，不携带假 EntryId、时间或关联坐标，不需要 Dispose。健康 missing 为 TagNotFound，不返回空/default 成功。应用取得 bytes 后自行解码与读取 data，失败不触发回退、改绑或撤销 tag。

### spec [A-VS-TAG-RELATED] 可选关联不授予来源资格

原 CreateTag(name, payload, out outcome) 创建匿名 tag；新 CreateTag(name, payload, relatedRevision, out outcome) 在 I/O 前要求 relatedRevision 为 nondefault、同一 HeadStoreId、库签发的 HeadEntryId。两个入口都捕获实际传入的 bytes；新入口不从源取 bytes、不比较 bytes、不重读或 flush 源，也不承诺源以后存在、健康或持久可读。注记不是精确 clone、ForkOrigin、恢复依赖或发布证明。

落盘只保存 RelatedLocalRefId 与 RelatedTicket；本桶 checked header 绑定 HeadStoreId，不重复保存 HSID。读取仅恢复 HeadId 和 SizedPtr 值，不签发 HeadEntryId，不探源文件或核验主链成员。声明字段检查非零 local id、可解码的非default ticket 与非零长度等公共值域，不附加 SingleEntry 尺寸或来源生效资格。匿名值不带 ticket。

## 名称接受合同与 codec

### spec [S-VS-TAG-NAME] 非空名称按 Ordinal 原样比较

TagName MUST 为 non-null .NET string，按 `string.Length` 接受 **1..1024 个 UTF-16 code units**。HeadStore 提供 `public const int MaxTagNameCodeUnits = 1024`，不提供实例配置；两个 CreateTag 入口、ResolveTag 与 ReadTagInfo 在文件 I/O 前共用准入，超长查询不能探桶后报 TagNotFound。相等使用 StringComparer.Ordinal；Release 与 release 可以分别创建。MUST 原样保留 code units，不 trim、normalize、大小写转换或 replacement。空串被拒绝；非空空白、NUL、控制符、路径分隔符、U+FEFF 与未配对 surrogate 均合法。长度单位不是 scalar、grapheme 或编码字节数。

名称是字段，不成为路径组件，不继承 branch 或冻结旧栈的 ASCII、128B、文件名字符限制。匿名 tag 已有公开源码持久化闭环，尚无包消费验收，不增加旧 tag 兼容义务。

### spec [F-VS-TAG-NAME-BPV1] 名称复用普通无损字符串

HeadStoreFormatVersion=1 MUST 选择 [BPV1 ordinary BareString](../Binary/bare-primitive-value.md)。writer 使用 BareValueEncoding.PrepareString 与 BareValueWriter.WriteString(in plan)；reader 使用现成 BareValueReader.ReadString(3072)。不使用 nullable/control、压缩、独立名称版本或手写 UTF 编码。

BPV 保留完整 UTF-16 code units，未配对 surrogate 可走 UTF-16LE；默认 writer 选择较短无损 payload，持平选 UTF-16LE。reader 接受另一种合法字符串表示及有界冗余前缀，非法 UTF-8 拒绝。相等、重复与路由基于解码 code units，不能以 wire bytes、默认编码重算或 hash 相同代替 fullname 等值。

## TailMeta schema 与容量

### spec [F-VS-TAG-METADATA] 独立 CRC 的可变长元信息

checked 格式门、桶 header 的 HeadStoreFormatVersion 与 FrameTag 共同选择 schema。TagRecord 的 TailMeta 依次编码以下字段；不能以可变 TailMetaLength 猜 schema 版本，不给 Head 的固定 meta8/CU 规则增加例外：

| 字段 | 编码与资格 |
| --- | --- |
| RecordedAtUnixMs | 完整 int64 LE；本次创建私有准备时一次取 UTC Unix 毫秒，允许负值、重复与回退 |
| RelatedLocalRefId | BPV VarUInt32；0 表示匿名；非0表示同库关联 |
| RelatedTicket | 仅非0 id 时出现；BPV VarUInt64(ticket.Serialize())，读回 SizedPtr.Deserialize |
| TagName | 完整 ordinary BareString codeword |
| MetaCRC | CRC32C，4B LE，覆盖前面全部 decoded meta bytes，不含自身 |

时间的单位、完整 long 值域与非单调语义复用 [08](08-versionstore-record-metadata.md) [S-VS-RECORD-TIME]；tag 在通过输入/关联准入后、最终 meta 编码及 data barrier/输出前取时，不复制相关 Head 时间，不表示源事件或 flush 完成时间。它不参与路由、名称等值或来源生效判据。08 的固定 meta8、按唯一长度演进与仅靠完整帧确认时间的规则只用于 Head/CU；本篇独立 CRC 资格用于 tag。

writer MUST 直接复用 RollingCrc.SealCodewordForward(meta)，reader MUST 复用 RollingCrc.CheckCodewordForward(meta)，均使用默认参数；不手写另一份 CRC 或使用 rolling scanner。writer 在 RBF escape 前对逻辑 meta 封口；reader 对 ReadTailMeta/ReadPooledTailMeta 已解 escape 的 meta 校验。RBF TrailerCRC 仅保护结构描述符；这份 MetaCRC 允许独立确认元信息，不能替代完整 RBF PayloadCRC 对 root、meta 与 padding 的检查。

MetaCRC 通过后从其前缀顺序解码时间、id、条件 ticket 与名字，要求 **exact-consume**；多余字段/尾随 bytes 拒绝，不按名字的默认编码长度定位。ReadVarUInt32 至多5B、ReadVarUInt64 至多10B，接受 BPV 有界冗余编码；匿名0也可能使用冗余 id 前缀。字符串按实际 ConsumedCount 消费，声明 payload 在分配前用 ReadString(3072) 限界，解码后再检查 1..1024 code units。字节界仍可能先产生3072个 ASCII chars 的临时字符串，不能声称分配前已保证逻辑1024上界；超长逻辑名随后拒绝。

逻辑 meta golden（RBF escape 前，V3-A 纳入 codec 验收）：time=0/id=0/Name=A → `00 00 00 00 00 00 00 00 00 03 41 DE 80 26 2D`，15B；time=0/id=1/ticket=(60,40)，Serialize=574、VarUInt64=`BE 04`/Name=A → `00 00 00 00 00 00 00 00 01 BE 04 03 41 69 45 83 C3`，17B。checksum 分别为 0x2D2680DE 与 0xC3834569，按 LE 保存。

### spec [F-VS-TAG-CAPACITY] 名称、元信息与完整记录的接受界

记 N 为实际 name codeword 字节数、M 为 root 字节数、T 为完整 TailMeta 字节数。合法 UTF-8 最多每 code unit 3B，UTF-16LE 最多2B；name string data 至多3072B，含最多5B header 的 codeword 至多3077B。meta 最少 `8+1+2+4=15B`，最多 `8+5+10+3077+4=3104B`；匿名与关联字段的长度都不是固定值。CreateTag 必须在本次 barrier/输出前完成如下确定检查：

```text
名称符合 [S-VS-TAG-NAME]；关联符合 [A-VS-TAG-RELATED]
1 <= M <= HeadStore.MaxHeadEntryPayloadLength = 1048576
N <= 3077；15 <= T <= 3104
checked(M + T) <= 1051680 <= RbfFile.MaxPayloadAndMetaLength
RbfFile.MeasureWriteSize(M, T) 成功
本次实际追加起点符合 SizedPtr 的公开范围
```

输出准备先做名称准入与轻量 StringEncodingPlan、关联准入、私有 root 捕获、实际 meta 度量和完整帧/起点检查，再物化最终 meta；不得先分配超界完整帧再拒绝、截断或拆记录。时间固定8B，id/ticket 宽度按实际输出取值计量。reader 的另一合法编码及冗余前缀计入实际 N/T，不用 writer 默认 Measure(decodedName) 代替。算术先 checked long，确认范围后转 int；FrameLength/AppendLength 只由公共 MeasureWriteSize 取得，不复制 padding/footer 公式。合法末帧可越过 SizedPtr.MaxOffset 的末端，下一次追加再拒绝，不隐式轮转。

**小 meta 租用前。** 真实主链 RbfFrameInfo 已具备 RBF 结构资格；先检查 Rbf3、TagRecord kind、非 tombstone 与15..3104的T，再只读小 TailMeta。不得因 M=0 或 M>1MiB 跳过可读元信息，也不为这些记录租完整帧。MetaCRC/精确解码/名称/路由/关联声明字段通过后身份可信，root 长度是否合法不改变占名资格。结构可靠但 kind/meta/tombstone 不合格的记录，按 named 查询的 unknownName 或 ScanTags 的必要错误处理；首 header 不按局部记录跳过。

**完整 root 租用前。** 仅 ResolveTag 在元信息扫描正常结束且确认唯一目标后读取该帧：先检查M的1..1MiB界、公共 MeasureWriteSize(M,T) 与实际 FrameLength 对应，再租完整帧、验证完整 RBF CRC、kind/meta 形状和本篇元信息及 root envelope，复制实际 Payload 为自有结果。root 内容末尾所有 bytes 都属于 opaque payload，不另检查 NamedFrameMap trailing。超界或零 root 明确为目标损坏，不租超界完整帧；不能回退另一同名项。

（Informative）固定1024 code units 为批准的首版工程上限，未声称工作集最优。元信息检查最多请求约3KiB，ResolveTag 仍需约1MiB完整帧与命中自有 bytes；pool 实际数组、输入副本、临时字符串与应用持有值另计，不保证精确 RSS 或合法请求不 OOM。运行期 OOM 不能作为局部坏记录吞掉，不建立总内存账本、预算或租借公面。

## 稳定路由与正式路径

### spec [F-VS-TAG-ROUTING] 按原 code units 固定分桶

HeadStoreFormatVersion=1 MUST 固定 FNV-1a 64-bit：逐个原 UTF-16 code unit 按 low byte、high byte 输入，不加 BOM、terminator 或长度前缀；U+FEFF/NUL 原样参与。初值14695981039346656037，每 byte 先 XOR 再乘1099511628211，算术取模2^64。固定 `B=1024`，`BucketId=(hash ^ (hash >> 10)) & 1023`，范围0..1023。路径为 `tags/<BucketId 的8位小写 ASCII hex>.rbf`，如 `tags/00000174.rbf`；BPV codeword 不参与 hash，不保存 hash/B/算法名，不使用 string.GetHashCode 或 replacement surrogate 的文本编码入口。

相等名称必同桶、跨进程稳定，hash/桶碰撞仍按 Ordinal fullname 区分。这些参数绑定统一格式版本，不能随运行配置或实现更换。FNV 常量与 XOR folding 形式见 [RFC 9923 §2–§3](https://datatracker.ietf.org/doc/html/rfc9923#section-3)；这里固定一次折叠，不宣称全64-bit折叠或分布最优。1024桶限制潜在文件数，每个实际创建桶增加一个 header，仍不轮转。

独立向量（脚本复算，V3-A 纳入 codec 验收）：A → hash 0908a207b5a15704 / bucket 00000351；Release → c5566333f1504164 / 00000174；release → bc7f822919cbbb44 / 000001aa；单个 code unit U+D800 → 0831b007b4ea00e5 / 00000265。tag-48 与 tag-65 都为00000106，须分别创建/解析。BPV `03 41`、`02 41 00`、`83 00 41` 均为 A，必须得到同一 hash/桶，不把宽容读取改成 wire 规范化。

tags 是 S4 根准入认识的**可选普通目录**，初次建库不预建；只读 Open/ResolveTag/ReadTagInfo/ScanTags 不创建。首次合法 CreateTag 在确定准入与 data barrier 后、私有初始化前 create-only 建立它，取得 ordinary/no-follow 与同文件系统资格；已存在组件须精确小写且合格。目录存在不表示已发布 tag。

直接项只允许上述8hex、桶号0..1023的普通 .rbf 文件，不允许子目录；零号桶合法。沿用 S4 精确组件、全部直接项名称/类型与 no-follow 资格，拒绝别名、未知名称/后缀、特殊类型及枚举错误，不能以 File.Exists=false 吞权限/I/O错误。named 请求只打开目标桶；其他桶内容不由本次证明。确定缺失目录/目标桶才是健康缺失；header-only 桶为空，bare Fence/坏 header 不是空桶。ScanTags 另遍历实际已建桶，不打开1024个不存在的桶。

## 桶组合 schema 与初始化保护

### spec [F-VS-TAG-BUCKET] 首帧 header 与后续记录

每桶一个 RBF3 文件、不轮转；统一版本与16B canonical HeadStoreId 消费 S4 [F-VS-OWN-FORMAT]。FrameTag 固定0（BucketHeader）与1（TagRecord），均非 tombstone：

| 帧 | Payload | TailMeta 与必要资格 |
| --- | --- | --- |
| 首物理 BucketHeader | uint32 LE HeadStoreFormatVersion；canonical HeadStoreId；uint32 LE BucketId，共24B | meta0；完整 CRC；版本、身份、桶号匹配 checked 门与路径，exact-consume |
| 后续 TagRecord | 原样 root-payload bytes | 本篇可变 meta；独立 MetaCRC、exact-consume、合法 name/本桶路由/关联字段；ResolveTag 另需 root envelope 与完整 CRC |

统一版本选择桶 header、记录与 meta schema，不选择 root 内容 codec；可变T只是尺寸。首 header 版本/身份/形状或尾随错误拒绝桶。后续未知 kind、重复 header、tombstone 或坏 meta 在 named 查询中属于未知名记录，在 ScanTags 中属于必要错误；不增加独立桶/记录版本、名称 CRC、索引或泛用 audit API。

named 入口与 ScanTags 的可写桶打开共用 S4 `[A-VS-RECOVERY-REPORTS]` 的诊断通道。成功 RBF 修尾的非 None 动作在本篇初始化恢复保护和必要记录校验之前入账；后续拒绝不撤销动作。报告用 TagBucket/本地桶号标识，不按 tag 名产生第二份台账，缓存/当前扫描桶的借用不重复报告。

以 checked 门、本次 HSID/BucketId 编码 header，令 H=RbfFile.MeasureWriteSize(24,0)，I=RbfScanBoundary.Empty.EndExclusive+H.AppendLength。共同首帧检查使用 ScanForward(showTombstone:true)：要求 Rbf3、首物理 offset、H.FrameLength、payload24、kind/meta/tombstone 匹配，再完整 CRC、精确解码比对；checked 后界须为I。缺首帧先查 TerminationError，不找后续替代。

可写正式桶在 public RbfFile.OpenExisting 前用公共文件长度入口拒绝实际长度<I，先关闭探测句柄再进独占工厂。非 None 恢复须有 AffectedFrameOffset>=I，随后仍共同检查 header；只允许 header 后残尾恢复，不补造/认领 header。header body 完整但缺原 Key/Fence 也须在I前检拒绝，不能先 CompletedTail、下次 Action=None 收养。长度足够但 header 内容坏仍拒绝，不承诺底层尾恢复前完全不修改坏文件。只读用 OpenReadOnlyExisting，残尾拒绝且不修复。所有成功资格均消费公共工厂正常EOF/恢复边界，不另绕过工厂。

## named 查询：元信息优先与坏项隔离

ResolveTag、ReadTagInfo 与 CreateTag 查重共用内部同步元信息扫描，扫描到正常EOF，仅裁决请求名；不保留跨调用 name→ticket表、负缓存或索引，不外泄 sequence/info/reader，不加预算或 partial-result API。

1. CreateTag 第一条语句初始化 outcome=NotAttempted。named 入口均 pre-I/O 检查 owner disposed/fault 与名称；mutation 再检查可写、HistoryActive/TagScanActive 与可选关联。借入 data 与 single-driver 责任消费S4，纯查询不另建 data 健康探测。
2. 只取得规范目标桶，完成工厂/header/恢复资格，沿 ScanForward(showTombstone:true) 真实主链读取小 meta。MetaCRC、codec、接受域、路由或关联声明失败可局部隔离并置 unknownName；身份未可信时不能猜它是其他名。可信非目标不读 root，root CRC或长度坏不改变其非目标身份。
3. 对可信目标按 Ordinal 计数，饱和于2；只保存唯一目标 TagInfo 与内部定位，不收集全部 fullname/ticket/root。目标元信息即证明名字已占，CreateTag 不读 root；ReadTagInfo 也不读 root。逐条归还meta，命中后仍扫描以排除同名重复，每次 MoveNext=false 都查 TerminationError。
4. 正常EOF后依下表裁决；ResolveTag 的唯一目标再执行完整 root 资格。所有成功/健康缺失还需必要清理成功、最终 owner 健康。CreateTag 从查重到 Append 复用同一 Idle 句柄，不关闭重查，不建立并发快照协议。

| 元信息扫描结论 | ReadTagInfo | ResolveTag | CreateTag |
| --- | --- | --- | --- |
| 可信目标重复 | 明确重复失败 | 明确重复失败，不用健康副本替代 | 同名已占/重复拒绝，NotAttempted |
| 唯一可信目标，即使 unknownName=true | 返回自有 TagInfo，不证明 root 健康 | 完整 root 通过才交自有 bytes；零/超界 root、完整 CRC/envelope 坏明确目标损坏 | 同名已占拒绝，NotAttempted，不读取 root |
| 零目标且 unknownName=true | 缺失不确定失败 | 缺失不确定失败，不能 TagNotFound | 查重不确定拒绝，NotAttempted，无 barrier/输出 |
| 零目标且 unknownName=false | 健康 TagNotFound | 健康 TagNotFound | 允许后续创建 |

局部隔离仅限已取得可靠结构、完成读取后的记录内容错误。framing/TrailerCRC/Fence/TerminationError、真实I/O（含meta short read）、OOM、生命周期/模式guard与必要close失败 MUST 传播；不能按异常基类统一catch继续。buffer内codec截短/非法编码可隔离，文件读取截短不能冒充codec错误。结构坏不猜边界或resync，首header坏不跳过。目标完整root CRC坏不影响健康元信息，但ResolveTag仍失败；其他名称重复不堵目标，由独立完整审计负责。

每 named 调用只有本次桶句柄、当前小 meta/临时名字、唯一目标值/内部定位与常数状态；ResolveTag 另有一份当前完整帧与命中自有 bytes。局部 finally 在 Dispose 前取出并清空句柄槽，主错误优先、资源只归还一次。从历史或 tag 扫描 visitor 调用时不覆盖它们的临时槽；访问当前扫描桶 MUST 借用已有合格句柄，不另开同文件、不关闭 borrowed 句柄，其他桶读用独立局部句柄。局部坏记录/缺失不确定本身不新增 fault，实际恢复或 owned 清理失败仍按 S4 停用；必要 close 失败不能交成功/健康缺失，交付前复检 disposed/fault。

## 元信息公面与全桶扫描

### spec [A-VS-TAG-INFO] 自有元信息不授予 root/EntryId 能力

```csharp
public sealed class TagInfo {
    public string Name { get; }
    public long RecordedAtUnixMs { get; }
    public HeadId? RelatedRefId { get; }
    public SizedPtr? RelatedTicket { get; }
}
AteliaResult<TagInfo> ReadTagInfo(string name);
public enum TagScanEnd { Complete, VisitorStopped }
AteliaResult<TagScanEnd> ScanTags(
    Func<TagInfo, bool> visitor, CancellationToken cancellationToken = default);
```

代码块省略内部构造。TagInfo 为普通sealed class，库内部构造，无public setter/init、owner、lease、Dispose、root或EntryId能力；RelatedRefId/RelatedTicket同时有或同时无。Name是自有不可变string，时间与坐标为自有值，callback结束、owner关闭后仍可用。ReadTagInfo按上面的named唯一性/缺失表返回；元信息健康而root CRC坏或长度非法也可成功，不承诺关联源可用。

### spec [A-VS-TAG-SCAN] 物理元信息遍历与窄重入 guard

ScanTags 在文件 I/O 前检查 owner disposed/fault、非 null visitor、取消与无其他活动 tag 扫描，再流式遍历全部实际已建桶，校验每桶 header，并对每条后续记录执行本篇小 meta 完整资格；不读 root、不做完整 PayloadCRC、不排序、不建立全 names 集合或索引。Complete 只表示所有所需物理记录元信息已遍历、各桶正常 EOF 与必要清理成功，**不证明名字全局唯一、root 健康或关联源有效**；重复声明逐条交付，完整审计另判重复。桶打开仍消费公共工厂的 EOF 结构资格，callback 早停不消除这项既有成本。

与 named 隔离不同，ScanTags 所到坏 meta、未知 kind、tombstone、路由/声明错误等必要记录错误直接失败，不跳过后返回 Complete；结构/I/O/OOM/清理同样传播。每项构造自有 TagInfo、归还当前 pooled meta 后才调用 visitor。已有 callback 结果不回滚，最终失败仍是失败，不叫 partial success。visitor 当前收到 TagInfo 后返回 false 即正常 VisitorStopped，不推进后续帧/桶；取消、visitor 抛出、callback 中 Dispose、owner fault 或必要清理失败优先于正常 Complete/VisitorStopped。每个 callback 前后、继续文件工作前及最终交付检查所需 guard/cancellation；先复检 callback 后状态，再解释 false，不能吞关闭错误。取消抛 OperationCanceledException，visitor/I/O 运行期异常原样传播；纯读/取消/visitor 异常本身不 fault 健康 writer，实际 owned 恢复/清理失败仍按 S4 停用。

只增加 TagScanActive guard：拒绝递归 ScanTags、进入 ReadHeadHistory 与全部 mutation；允许日常 ReadHead、ReadTagInfo、ResolveTag、ListHeadOrigins。history callback 可以调用 ScanTags；扫描持有独立临时桶槽，不覆盖 history 的临时 Head 槽。callback 读当前桶借用扫描句柄，其他桶用独立局部句柄。owner Dispose 先失效实例、取出并清空槽再关闭；方法 finally 只关闭仍在槽中的 owned 句柄，不重复关闭 callback 已清理的资源。全库仍单 driver 串行，不增加并发、跨调用缓存、全量名称表或扫描预算。所有退出清除 TagScanActive；主异常优先且继续必要清理，不能以正常停止遮蔽清理失败。

## 创建、空桶发布与三态证据

CreateTag消费S4 [S-VS-ROOTS-AFTER-DATA-CONFIRM]、[A-VS-PUBLICATION-EVIDENCE]与[R-VS-PUBLICATION-UNKNOWN]，不调用其他公开mutation拼操作。第一条语句设必选out=NotAttempted；先完成名称/关联准入、元信息查重与确定缺失、私有root捕获、取时/封口、完整尺寸及实际起点检查。缺桶另预检header、从I追加首tag的资格。同名、查重不确定、容量与非法关联拒绝先于barrier/私有初始化/输出。纯捕获/分配失败不新增fault；同次owned清理失败仍按S4停用，out不变。

随后同步data.ConfirmDurable()；无关data Builder仍Building不阻断，root必要依赖由应用保证完成。屏障失败保持NotAttempted、HeadStore停用，data按自身fault且不被Dispose。关联注记不增加源ConfirmDurable/flush，也不证明关联位置与传入bytes有关系。

缺桶时在唯一creating/pending.rbf写header、共同检查并要求TailOffset=I，DurableFlush/close后以同文件系统no-overwrite rename公开合法空桶。目录建立/空桶初始化不推进本次tag outcome；验证正式桶后才Append(payload,tailMeta)。正式Append/flush原样采用S4：尝试后未确认Unknown，确认后Confirmed，再close/交付；确认后关闭/交付异常保留Confirmed并停用。无缓存安装相位，内部助手/teardown/borrowed data规则消费S4。

合法空桶保留，不删除或自动补首tag；pending按S4 [S-VS-CREATING-WORKSPACE]可写取消/只读保留，不读私有内容、不认领角色或续旧请求。I前检不放宽。header后未完成body可被RBF截回真实边界；完整body缺原Key/Fence可CompletedTail，随后仍执行正常EOF、独立元信息及请求范围裁决。坏记录不删除/修写/改绑，不能当健康不存在后重建。

重开用实际checked ResolveTag接受bytes，ReadTagInfo仅确认元信息，均不重建旧outcome。完整记录不证明旧调用曾Confirmed，同名/同值不成为旧尝试身份。未知结果后应用检查实际名称与业务内容决定续行，不机械重试create-only，不增加receipt、token或exactly-once账本。

## 成本、施工与实施验证

named请求仍做路径资格与O(目标桶记录数+checked meta bytes)扫描，CreateTag/ReadTagInfo不读旧root，ResolveTag仅对唯一目标追加完整root读取；固定桶数不等于O(1)。连续同桶创建n条仍可能O(n²)。1024桶相对256在均匀分布假设下减小平均桶负载，但增加潜在目录项、桶header与全库扫描的开关文件成本；不承诺文件系统甜点、实际分布或实测四倍性能。ScanTags遍历全部已建桶元信息，时间段/Head筛选仍为全库元信息扫描，callback持有值由应用另计；临时状态不随全部名称数增长。

坏meta的未知名称会阻止零命中的健康缺失/创建，但唯一健康目标隔离它；可信其他名的root CRC/长度坏不影响目标，可信目标的坏root只使ResolveTag失败；结构/I/O/关闭坏仍可能影响同桶目标。查询/ScanTags成功不替代独立完整审计。容量或性能证据超出目标后再评估位置表、更新资格与索引，不预建catalog/checkpoint/轮转/预算。

| 验证组 | 必须覆盖 |
| --- | --- |
| 名称/路由 | MaxTagNameCodeUnits=1024、1/1024接受，null/0/1025 pre-I/O拒绝；pair计2/unpaired计1；空白/NUL/控制符/分隔符/U+FEFF；Release/release；BPV另一合法表示/冗余前缀；上述冷进程hash/1024桶向量与碰撞fullname裁决 |
| meta/容量 | 逻辑meta golden、Seal/Check默认参数、escape前seal/解escape后check；0匿名/条件ticket/冗余VarUInt32与VarUInt64、exact-consume、ticket声明值域；N3077/T15..3104/M+T1051680与Measure(M,T)；巨大声明分配前拒绝，3072临时ASCII chars后按1024拒绝；非法M仍小读meta不租整帧、另一编码actual-consumed；末帧越界后下一起点拒绝 |
| 桶/路径/初始化 | missing与header-only；可选目录RO不创建/首次创建；别名/未知项/特殊类型/枚举错误；bareFence/短header/缺原KeyFence反复可写均拒；错版/身份/桶号；仅header后残尾恢复、工厂EOF与close失败 |
| named隔离 | 坏meta A与健康B、unknown在前/后且唯一B成功；zero+unknown拒TagNotFound/Create且无barrier；可信其他名root坏不阻断；可信目标M=0/超界或rootCRC坏：ReadTagInfo成功/Create已占/Resolve失败；meta坏不能借root猜name；同名不同合法编码/同值重复拒，其他名重复不堵目标；I/O/shortread/OOM/framing/close不吞 |
| 关联与自有值 | 默认/异库/非法关联pre-I/O拒，匿名无ticket；不源读取/flush/bytes比较，不因源随后不可用拒TagInfo或Resolve；读坐标不能签发HeadEntryId；输入变化/归池后tag不变，TagInfo/RootPayload在owner关闭后有效，pooled资源一次清理 |
| ScanTags/互操作 | missing返回Complete；元信息声明逐条交付含重复及坏root；后续坏meta失败不回滚已交值且不称partial success；false不读后续，取消/throw/Dispose/close优先；递归scan/进入history/全部mutation拒，read/info/resolve/ListHeadOrigins允许；history callback可scan，双临时槽与当前桶读复用 |
| 发布/恢复/组合 | data失败无tag输出/无关Builder不堵；pending取消/RO保留，空桶rename仍NotAttempted；Append/flush Unknown与确认后close失败Confirmed；CompletedTail后meta重验；冷开不认领旧尝试；Head后续更新不改tag，应用坏data明确失败，无精确来源能力 |

**施工与成熟度：** 本合同的V3-A/B/C已完成codec、惰性空桶、发布/解析、关联及元信息公面；匿名与关联writer共用发布路径和schema。现有公开RBF TailMeta读取及RollingCrc已经够用，不新增底层scanner/索引接口。V4-A已接通只读完整Audit，V4-B至V4-F的具名进程、两平台与有限规模证据分别见实施计划；V4-E取得独立源码Accepted。包消费资格仍缺，有限规模观察不代表任意负载SLO。

依据：既有tag合同从35d6c2d的S5迁入；[S5](05-versionstore-names-and-indexes.md)保留导航与历史互操作。[S4](04-versionstore-publication.md)、[RBF接口](../Rbf/rbf-interface.md)、[公共尺寸](../../src/Rbf/RbfFile.cs)、[小TailMeta读取与解escape](../../src/Rbf/RbfFrameInfo.cs)、[RollingCrc封口/检查](../../src/Data/Hashing/RollingCrc.cs)、[BPV字符串选择](../../src/Binary/BareValueEncoding.Strings.cs)、[分配前限制](../../src/Binary/BareValueReader.Strings.cs)、[VarInt](../../src/Binary/BareValueReader.cs)、[SizedPtr](../../src/Data/SizedPtr.cs)为机制依据。[RBF正扫测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)为底座证据；冻结旧栈[不可变tag测试](../../tests/EventJournal.Tests/ImmutableTagTests.cs)与[书签消费者](../../../durable-graph/tests/DurableGraph.Persistence.Tests/RepositoryTagTests.cs)仅作工作流背景，不引入旧API/格式，不构成本专项验收。
