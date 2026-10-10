# S5-B 专项：不可变 tag

日期：2026-10-10。状态：**Draft；用户已批准固定名称长度上限与请求范围的坏记录隔离，本次修订保留名称 codec、256 桶路由、初始化保护、data-first 与 PublicationOutcome。VersionStore 尚未创建，本次契约修订未实施，无运行或验收证据**。

本文属于 [S5](05-versionstore-names-and-indexes.md) 的 S5-B，不是新增项目或阶段。建议先完成 S4 ref 闭环与 S5 历史/fork，再施工本文 tag，最后补齐 S6 tag 组合消费；tag 的机制依赖 S4，历史回调互操作另消费 S5，不将这份施工建议变成新增依赖。执行保持单 owner/driver 串行与 ProcessCrashOnly，不增加并发或断电保证。统一版本、持久身份、root-payload envelope、借入 data、根/锁、唯一 pending 及停用清理由 [S4](04-versionstore-publication.md) 唯一定义；本篇是 tag 名称、桶及其操作的唯一权威，不复制下层格式或尺寸算法。

## 需求、边界与公开结果

用户移除 branch 名称层后仍保留命名不可变 tag：名字标识创建时的一份完整 root-payload bytes。这里的“只读 tag”指已创建内容不可变，仍提供 CreateTag 与 ResolveTag。tag 不分配 RefId，不绑定 ref，不随 ref 更新，不保存精确 RefRevision/来源，也不提供删除、改名、覆盖或同值幂等创建。应用需要历史选点与来源时使用 S5 的 RefRevision/ForkRef，不能从 tag 内容同值推出来源资格。

### spec [S-VS-TAG-PAYLOAD-IMMUTABLE] tag 一次冻结完整 root bytes

每个 tag MUST 保存 TagName + 完整 root-payload bytes 的一条 TagRecord，name prefix 后全部余 bytes 为 root payload，长度1..1MiB。tag 不分配 RefId/revision，不随 ref 更新，不保存精确来源，create-only 同名即拒绝，即使 payload 同值。应用 codec 与 data 依赖闭包由应用负责，tag 不解码 RootMap/JSON。

CreateTag(name, ReadOnlySpan<byte> payload, out outcome) 返回 AteliaResult<bool>，成功值恒为 true；ResolveTag(name) 返回 AteliaResult<RootPayload>，唯一消费 [11 bytes 公面专项](11-versionstore-root-payload-bytes-api.md)的捕获、自有 GC array 和只读 Span 合同；没有假 revision、TagSnapshot、结果租借或 Dispose。CreateTag 不回显输入，ResolveTag 仅保留命中自有 bytes；健康 missing 仍为 TagNotFound，不返回空/default 成功。应用取得 bytes 后自行解码与读取 data，失败不得要求回退、改绑或撤销 tag。

## 名称接受合同与 codec

### spec [S-VS-TAG-NAME] 非空名称按 Ordinal 原样比较

TagName MUST 为 non-null .NET string，按 `string.Length` 接受 **1..1024 个 UTF-16 code units**。首版 VersionStore 提供 `public const int MaxTagNameCodeUnits = 1024`，不提供实例配置；两个入口在文件 I/O 前共用该准入，超长查询不能探桶后报 TagNotFound。相等使用 `StringComparer.Ordinal`，区分大小写；`Release` 与 `release` 可以分别创建并解析。MUST 原样保留 code-unit 序列，不 trim、不做 Unicode normalize、大小写转换或 replacement。空串被拒绝，非空空白名合法；NUL、控制符、路径分隔符、U+FEFF 与未配对 surrogate 均无额外字符拒绝。长度单位不是 Unicode scalar、grapheme 或编码字节数。

名称是 record 字段，不成为文件路径组件，不继承 branch 或冻结旧栈的 ASCII、128B、文件名字符限制。当前 VersionStore 无实现、已发布数据或新栈消费者，不增加旧 tag 格式兼容义务。

### spec [F-VS-TAG-NAME-BPV1] 名称复用普通无损字符串

VersionStoreFormatVersion=1 MUST 选择 [BPV1 ordinary BareString](../Binary/bare-primitive-value.md)；writer 使用 `BareValueEncoding.PrepareString` 与 `BareValueWriter.WriteString(in plan)`，reader 使用现成 `BareValueReader.ReadString`。不使用 nullable/control、压缩、独立名称版本或手写 UTF 编码。

BPV 普通字符串保留完整 UTF-16 code units，未配对 surrogate 可走 UTF-16LE；默认 writer 选择较短的无损 payload，持平选 UTF-16LE。reader 接受 BPV 规定的另一种合法字符串表示与有界冗余前缀，非法 UTF-8 明确拒绝。TagName 的相等、重复检测和路由 MUST 基于解码后的 code units；不能以 wire bytes、默认编码重算或 hash 相同代替 fullname 等值。

## 名称与完整记录容量

### spec [F-VS-TAG-CAPACITY] 名称与完整记录的固定接受界

记 `N` 为完整 TagName codeword 的实际字节数，`M` 为 S4 root-payload bytes 字节数。合法 UTF-8 表示至多使用每 code unit 3B，UTF-16LE 使用每 code unit 2B；BPV VarUInt32 header 至多5B。因此接受域的 name payload 上界为 `3 * MaxTagNameCodeUnits = 3072B`，完整 name codeword 上界为 `3077B`，整条 TagRecord payload 上界为 `1MiB + 3077 = 1,051,653B`。这些界由固定名称接受域推导，不增加第二套名称配置；TailMeta 仍为0，完整记录还 MUST 满足公共 RBF3 硬界。CreateTag 在名称准入后用 BPV plan 取得默认输出的精确 N，再按请求的实际 M 检查：

```text
名称符合 [S-VS-TAG-NAME]
1 <= M <= S4 的 root-payload 上限
N <= 3077
P = checked(N + M) <= 1051653
P <= RbfFile.MaxPayloadAndMetaLength
RbfFile.MeasureWriteSize((int)P, 0) 成功
本次实际追加起点符合 SizedPtr 的公开范围
```

N 包含实际字符串 header 与 payload；M 的限额及计量原样消费 S4。reader 接受另一种合法编码及有界冗余 header，不能用 writer 默认编码长度作磁盘 N 的上界。全部算术先用 checked long，在已验范围后转 int；物理 FrameLength/AppendLength 只由公共 MeasureWriteSize 取得，不复制 RBF padding/footer 公式。这里只限制单记录，文件的下一次起点另检查；合法末帧可越过 SizedPtr.MaxOffset 的末端，下一次追加再拒绝，不隐式轮转或缩小末帧。

**输出前。** 先完成名称准入和 BPV plan，捕获/度量实际 root bytes，组合 P 并检查完整帧与实际起点；通过后才物化最终组合输出，全部在本次 data barrier 与输出前。StringEncodingPlan 只保留不可变 string 与编码选择，不物化完整名称 payload；`EncodedLength` 是默认输出的精确 N。不能先分配超限完整记录再拒绝、截断名字、截断 payload 或拆记录。输入字符串已由调用方提供；这些检查不撤销调用方先前分配，也不保证合法请求必然分配成功。

**完整帧租用前。** 从真实主链取得 RbfFrameInfo 后先检查 Rbf3、kind/meta/tombstone、PayloadLength 上界与公共 `MeasureWriteSize(PayloadLength, 0)` 对应的 FrameLength。合法 TagRecord 的 payload 范围为3..1,051,653B：非空名 codeword 最少2B，root payload 最少1B；扫描为识别零 root 的目标坏记录，可读取形状合格且 payload 恰为2B的记录，解码后仍拒绝零 root，不放宽接受域。结构可遍历但 info 形状不合格、payload 小于2B或超过上界时，按下文记为未知名称坏记录并跳过，不租用超界完整帧；不能用已读名称的默认 Measure 代替接受界。首 header 的必要资格仍完整执行，不按局部坏 TagRecord 跳过。

**字符串分配前。** 仅对完整 CRC 已通过的记录，从完整 payload 借入长度为 `min(PayloadLength, 3077)` 的名称窗口，调用现成 `BareValueReader.ReadString(3072)`；它在分配前验证声明字节数、int 与实际剩余，不复制窗口或手写另一套 decoder。解码成功后再检查 `1 <= name.Length <= MaxTagNameCodeUnits`。3072B 的字节界可能先分配最多3072个 ASCII chars 的临时字符串，不能称为分配前已保证逻辑1024 code units；随后拒绝超长名字。按 reader 实际 ConsumedCount 在原完整 payload 定位后续所有 bytes，即 root payload；要求其实际长度1..1MiB，不调用内容 decoder。名称窗口可含部分 root bytes，不要求局部窗口完全消费，也不预留 suffix 字节以免把零 root 的可信名称截短。

容量计量 MUST 按实际 name prefix 消费和后续 root bytes 长度计算；合法冗余 name header/另一种字符串表示也计入 N，不能用 Measure(decodedName) 代替磁盘 N。root bytes 原样保存，内容末尾任何 bytes 都属于 payload，不另检查 RootMap trailing。命中记录的完整 CRC、name合法/路由、root 长度及下文目标唯一性通过才交自有结果。

（Informative）固定1024 code units 是用户批准的首版工程上限，不是实测工作集最优值。它将单次完整记录请求限制在约1MiB、名称解码字节限制在3KiB；完整帧、当前临时字符串、命中自有 root bytes、输入与输出缓冲仍可同时存在，ArrayPool 的实际租借数组也可能更大，不构成精确 RSS 或总内存保证。合法输入仍可能 OOM，运行期 OOM 不能作为局部坏记录吞掉。实施需报告峰值与连续同桶创建成本，不建立总内存账本、通用预算、租借 API 或流式 tag 格式。

## 稳定路由与正式路径

### spec [F-VS-TAG-ROUTING] 按原 code units 固定分桶

名称相等必同桶、跨进程稳定路由与 fullname 碰撞裁决是已有合同。MUST 使用固定 hash 输入/算法与桶规则，不能使用进程随机化的 `string.GetHashCode()`；hash 相同而完整名字不同可以共存。具体参数是 VersionStoreFormatVersion 选择的持久寻址/schema，不能在同一格式版本下随运行配置或进程改变。

VersionStoreFormatVersion=1 MUST 固定以下工程参数：BPV codeword 不参与 hash；逐个 UTF-16 code unit 按 low byte、high byte 的顺序输入 FNV-1a 64-bit，不额外插入 BOM、terminator 或长度前缀；名字中的 U+FEFF/NUL 原样参与。初值 `14695981039346656037`，每 byte 先 XOR 再乘 `1099511628211`，算术取模 2^64。固定 `B = 256`，`BucketId = (hash XOR (hash >> 8)) & 255`，合法桶号为 0..255。正式路径为 `tags/<BucketId 的 8 位小写 ASCII hex>.rbf`，如 `tags/00000025.rbf`；不直接插入名字，不保存 hash、B、算法名或新版本字段。hash 输入必须保留每个原 code unit，不能使用会 replacement 未配对 surrogate 的文本编码入口。

（Informative）FNV 步骤与常量、缩小 hash 的折叠形式见 [RFC 9923 §2–§3](https://datatracker.ietf.org/doc/html/rfc9923#section-3)。本篇固定上述一次 XOR folding，桶值取 hash 的低两个 byte，不宣称全 64-bit 折叠、分布最优或无碰撞；没有新栈工作集时不寻找最优 hash/B。固定 256 桶限制潜在桶文件数，每个已创建桶增加一个 header，单桶仍不轮转。这组参数已经绑定统一版本，落盘后不能通过运行配置或实现替换使同名改路由。

独立向量（脚本复算，尚非实现验收）：`A` → hash `0908a207b5a15704` / bucket `00000053`；`Release` → `c5566333f1504164` / `00000025`；`release` → `bc7f822919cbbb44` / `000000ff`；单个 code unit U+D800 → `0831b007b4ea00e5` / `000000e5`。`tag-17` 与 `tag-50` 的桶号均为 `00000041`，仍须分别创建/解析。BPV 的 `03 41`、`02 41 00`、`83 00 41` 均表示 `A`，必须得到同一 hash/桶；不将这种宽容读取改为同值同 bytes 的规范化要求。

`tags` 是 S4 根准入认识的**可选普通目录**，初次建库不预建。缺失可表示尚无桶；只读 Open/ResolveTag 不创建。首次合法 CreateTag 在已完成确定准入与 data barrier 后、私有初始化前 create-only 建立它，并取得 ordinary/no-follow 与同文件系统资格。已存在的组件必须精确小写且合格；目录存在本身不表示任何 tag 已发布。

`tags` 的直接项只允许上述 8hex、桶号 0..255 的普通 `.rbf` 文件，不允许子目录；零号桶合法，不继承 ref 的非零编号限制。取得目标路径时沿用 S4 的精确组件、全部直接项名称/类型及 no-follow 机制，拒绝大小写别名、未知层级/名称/后缀、特殊类型和枚举错误；不能由 File.Exists=false 吞掉权限/I/O/资格失败。此处只做目录/path 资格，不要求 Open 或 ResolveTag 扫描全部桶内容。只打开目标规范桶，其他桶的内容健康不由本次查询证明。缺失目录或确定缺失目标桶才可作为健康缺失；合法 header-only 桶为空，bare RBF Fence/坏 header 不是空桶。

## 桶组合 schema 与初始化保护

### spec [F-VS-TAG-BUCKET] 首帧 header 与后续完整记录

首版每桶一个 RBF3 文件、不轮转；统一版本与 16B canonical VersionStoreId 直接消费 S4 `[F-VS-OWN-FORMAT]`。所有帧非 tombstone、TailMetaLength=0；[元信息专项](08-versionstore-record-metadata.md) 不给 tag 增加时间。两种 FrameTag 固定为 0（BucketHeader）与 1（TagRecord）：

| 帧 | payload，依次编码 | 必要资格 |
| --- | --- | --- |
| 首物理帧 BucketHeader | uint32 LE VersionStoreFormatVersion；S4 canonical VersionStoreId；uint32 LE BucketId | 版本等于 checked 门，身份逐字节匹配门，桶号合法且等于本次路径期望值；exact-consume |
| 后续 TagRecord | 完整 TagName codeword；原样 S4 root-payload bytes | 名称合法且重新路由到本桶；完整 CRC；名称实际消费后的全部余 bytes 为 root payload，检查1..1MiB长度 |

统一格式版本选择桶 header、名称 codec、TagRecord envelope 与 meta 形状，不选择 root 内容 codec。首 header 的版本/身份/形状或尾随错误拒绝桶；后续帧未知 kind、重复 header、tombstone、未知 meta、非法 name/root 长度属于局部坏记录，按下文请求范围裁决，完整校验仍判坏。name prefix 后全部余 bytes 都是应用 payload，不按 RootMap trailing 判坏。首版不增加独立桶/记录版本、名称 CRC 或内容 CRC。

以 checked 门版本、本次期望 VSID/BucketId 编码 canonical header，得确定 payload 长度 L；令 `H = RbfFile.MeasureWriteSize(L, 0)`、`I = RbfScanBoundary.Empty.EndExclusive + H.AppendLength`。共同首帧检查使用 `ScanForward(showTombstone: true)`：要求 Rbf3，首物理帧 offset、H.FrameLength、payload L、kind/meta/tombstone 匹配，再完整读取 CRC、精确解码并比对期望身份；checked 后界必须为 I。缺首帧先检查 TerminationError，再报错，不找后续替代 header。

可写访问正式桶 MUST 在 public `RbfFile.OpenExisting` 前用公共文件长度入口拒绝实际长度 < I，并先关闭探测句柄再进入独占 RBF 工厂。非 None 恢复须有 `AffectedFrameOffset >= I`；随后执行共同首帧资格，只允许 header 后的 tag 残尾恢复，不补造/认领 header。header body 完整但缺 Key/Fence 时也应在 I 前检拒绝，不能先 CompletedTail，再于下次 Action=None 收养。长度足够但 header 内容坏仍拒绝；不承诺底层 tag 尾恢复前完全不修改坏文件。只读使用 public `OpenReadOnlyExisting`，残尾拒绝且不修复。

## 每次目标桶扫描与请求范围的完整性裁决

ResolveTag 与 CreateTag 查重复用一个内部同步扫描流程，扫描至正常 EOF，仅对请求名称裁决命中、损坏及重复；成功不证明整桶内容健康。不保留跨调用 name→ticket/payload 表、负缓存或持久名称索引，不外泄 sequence/info/reader，不增加 visitor、扫描预算、partial-result 或缓存 API。

1. CreateTag 第一条语句初始化 outcome=NotAttempted；两个入口均在文件 I/O 前检查 owner disposed/fault 与共用名称准入。CreateTag 再检查可写模式及 S5 HistoryActive mutation guard。借入 data 与 single-driver 的持续责任消费 S4，不为纯 tag 查询另建 data 健康探测。
2. 只取得该名字的正式规范桶，按访问模式打开并完成 header/恢复资格，沿 `ScanForward(showTombstone: true)` 推进真实链。结构可靠时，逐条执行上述 info 与完整帧租用前检查及完整读取；局部 PayloadCRC、名称 codec/接受域/路由或记录 envelope 失败允许跳过。完整 CRC 失败时不得从未认证的 name prefix 猜非目标；info 形状/尺寸不合格、无法取得可信合法名称等情形置 `unknownName`。完整 CRC、记录形状及名称/路由通过后，名称身份才可信；若其恰为目标则计一次目标匹配，root 长度坏另置 `knownTargetDamaged`。可信其他名的 root 长度坏不使目标缺失变成未知。
3. 仅按 Ordinal 计数请求名称的可信匹配，计数饱和于2（表示重复），payload 同值也冲突；不建立整桶 fullname 集合，其他名称的重复不阻断目标查询，由独立完整校验负责。逐帧释放 RbfPooledFrame，仅保留唯一健康目标的自有 root bytes，不收集所有 payload/ticket。命中后仍继续扫描，未知名称坏项不计为目标或非目标。每次 MoveNext=false 都检查 TerminationError。
4. 以下裁决要求结构扫描正常 EOF、必要清理成功及最终 owner 健康；已有桶允许创建时从查重至 Append 复用同一 Idle 句柄，Append 后仍按 S4 flush/close 才交付，不关闭后重新查重，不建立并发快照协议。

| 扫描结论 | ResolveTag | CreateTag |
| --- | --- | --- |
| 目标重复或 knownTargetDamaged | 明确目标重复/损坏失败，不采用健康副本替代 | 同名已占/损坏拒绝，NotAttempted |
| 唯一完整健康目标，无已知目标损坏或重复 | 交付其自有 bytes，即使 unknownName=true | 同名已占拒绝，NotAttempted |
| 零目标且 unknownName=true | 缺失不确定的失败 Result，不能 TagNotFound | 查重不确定拒绝，NotAttempted，不进入 barrier/输出 |
| 零目标且 unknownName=false | 健康 TagNotFound | 允许进入后续创建协议 |

只有记录层的局部内容错误可隔离；扫描 framing/TrailerCRC/Fence/TerminationError、真实 I/O、OOM、生命周期/模式 guard 及必要 close 失败 MUST 传播，不能统一 catch 后继续。名称解码的截短/非法编码只在已经完整读取的 buffer 解码阶段映射为局部错误，不能按异常基类吞掉文件读取失败。结构损坏时不能猜下一个帧边界或搜索 resync，可能仍影响同桶健康目标；首 header 坏也不能跳过。正向扫描自身已完成的边界资格是继续推进的前提。完整校验另检查全桶名称重复与记录内容；本专项不新增泛用 audit API。

每调用只有一个本次桶句柄、当前完整帧与临时名称、命中自有值、目标计数和上述两个布尔标志，临时状态不随桶名称总量增长。局部 finally 在 public Dispose 前先取出并清空句柄槽，所有退出只归还一次，主错误优先。ResolveTag 无应用 callback；从历史 visitor 调用时不覆盖历史当前临时 ref 槽。普通局部 CRC/envelope/名称/长度或缺失不确定失败不自动 fault 健康 writer，实际恢复/owned 清理失败依 S4 停用；必要 close 失败不能返回成功或健康缺失。最终交付前复检 owner disposed/fault。

## 创建、空桶发布与三态证据

CreateTag 直接消费 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]`、`[A-VS-PUBLICATION-EVIDENCE]` 与 `[R-VS-PUBLICATION-UNKNOWN]`，不调用其他公开 mutation 拼成本次操作。入口第一条语句初始化必选 out 为 NotAttempted；完成名称、请求名查重与确定缺失裁决、root bytes 私有捕获、完整记录尺寸与实际起点检查。缺桶时另预检 header 与首 tag 尺寸、从 I 起的追加资格。同名/损坏/查重不确定及确定容量拒绝均先于 data barrier、私有初始化/tag 输出。纯输入捕获/分配失败不新增 fault；同一流程的 owned 清理失败仍按 S4 停用，out 不变。

随后同步调用 `data.ConfirmDurable()`。无关 data Builder 仍 Building 不阻断创建，本根必要依赖仍由应用保证完成；屏障确认所有必要完成输出，不签发未完成依赖资格。屏障异常本次 tag 保持 NotAttempted、VersionStore 停用；data 按自身 fault，不 Dispose 借入 data。

缺桶时在 S4 唯一 `creating/pending.rbf` 写 header、执行共同首帧检查并要求 TailOffset=I，再 DurableFlush/close，以同文件系统 no-overwrite file rename 公开合法空桶。该初始化包括可选 tags 目录建立均不推进本次 tag outcome；打开并验证正式空桶后才追加首条 tag。首 tag 与后续 tag 使用S4同一正式Append/flush证据协议，Confirmed后才关闭/交付，无缓存安装相位；关闭/交付异常仍保留Confirmed并停用。内部助手/清理、pre-I/O Result例外、完整teardown及borrowed data处理直接消费S4，不另定义。

合法正式空桶保留，不删除或自动补首 tag；pending 残留按 S4 `[S-VS-CREATING-WORKSPACE]` 可写取消/只读保留，不读私有内容、不认领角色/桶号或续旧请求。该策略不放宽正式桶的 I 前检。header 后未完成 tag body 可由 RBF 截回真实边界；完整 body 缺原 Key/Fence 可 CompletedTail 成为完整记录，随后仍须扫描至正常 EOF 并执行请求范围裁决。局部坏记录可在查询中隔离，但不删除、修写、回退或改绑；不能把它当健康不存在后重建同名项。

重开以实际 checked ResolveTag 接受当前内容，不重建旧 outcome；完整 tag 不证明旧同步调用曾 Confirmed，同名/同值也不成为旧尝试身份。未知结果后由应用检查实际名称与业务内容并决定续行，不机械重复 create-only，不新增 receipt、尝试 token 或 exactly-once 账本。

## 成本、施工与实施验证

每次查询/重名检查包含有界输入名称度量/hash、规范路径的目录名称/类型发现与 O(目标桶记录数 + checked bytes) 的扫描；固定桶数不意味着只需一次 metadata 调用。日常查询不保存整桶 fullname 集合，只保留当前帧/名称、命中 root bytes 与常数个判定状态；单帧请求有上述界，实际 pool/RSS 与应用持有值另计。连续创建同桶 n 个 tag 仍可累计 O(n²) 记录检查。局部内容坏 A 可与同桶完整健康 B 隔离；未知名称坏项会阻止健康缺失/创建，结构坏或必要 I/O/关闭失败仍可能阻断同桶查询。其他桶内容不由本次证明。惰性 Open 不审计全部桶，不承诺 O(1) 查询、长期无限容量或性能目标已经满足。

实际成本超出目标后再设计跨调用位置表、其更新/资格与持久索引；首版不预建 catalog、checkpoint、轮转 manifest 或通用预算。tag 查询与创建实现需与 S4/S5 原有生命周期、历史回调和根锁共同接受评审，源码、真实进程中断、平台、性能与包资格分别取证。

| 验证组 | 必须覆盖 |
| --- | --- |
| 名称/codec/路由 | public 固定 MaxTagNameCodeUnits=1024；null/0/1025拒绝，1/1024接受且先于 I/O；原 UTF-16 计量，surrogate pair计2、未配对surrogate计1；非空空白、NUL/控制符/分隔符/U+FEFF原样；Release/release；同名不同合法BPV codeword与冗余header；跨冷进程同桶；hash碰撞按fullname区分 |
| 容量/分配前检 | ReadString(3072)、name窗口/codeword 3077、整payload 1051653及公共Measure配套；另一合法UTF-8表示按actual consumed，不能用默认Measure定位suffix；巨大声明/overflow/截短在名称分配前拒绝；3072 ASCII chars可能临时分配但随后按1024逻辑界拒绝；info已知超界不租完整帧、置unknown；root独立1..1MiB、输出前确定拒绝；合法末帧越过起点上界后的下一次拒绝 |
| 桶/路径 | missing 与合法空桶；可选 tags 缺失/首次建立/RO 不创建；未知名/别名/特殊类型/同文件系统/枚举失败；bare Fence/短 header/缺原 Key/Fence 重复可写打开均拒绝；首header错版/身份/桶号 |
| 请求范围隔离 | 坏A与健康B同桶、坏项前后顺序及unknownName仍可返回唯一健康B；零hit且unknown拒绝健康缺失与Create，out=NotAttempted且无barrier/输出；有界完整读取后CRC与目标name可信但root为0或超1MiB时判目标损坏、拒Create，不用另一健康同名记录替代，含2B name+零root边界；可信其他名root坏不阻断目标；同名冗余编码及命中后方目标重复（同值/不同值）拒绝，其他名重复不堵目标且完整校验判坏；后续header/未知kind/meta/tombstone及局部codec/CRC/envelope隔离，与结构/framing/TrailerCRC/TerminationError、普通I/O/OOM/guard/close传播分别取证 |
| 自有值/资源 | 输入后续变化不改变 tag；结果在查询/owner 关闭后有效；pooled 帧及时归还、句柄一次清理；历史 visitor 中 ResolveTag 不损害历史槽；历史期间 CreateTag pre-I/O 拒绝 |
| 发布/恢复/Outcome | 同名/目标损坏/缺失不确定/容量拒绝先于barrier；无关Builder不阻断；data失败无tag输出；pending中断/取消/RO保留；空桶rename仍NotAttempted；首body截断保留空桶、CompletedTail后请求范围重验；Append/flush Unknown、flush后close/交付失败Confirmed；冷开不认领旧尝试 |
| 组合消费 | 历史选点后创建 tag，ref 后续更新不影响它；应用按地址读取坏 data 时明确失败、不回退；冷重开取 tag bytes 可作无来源 CreateRef，不能由 tag 直接取得精确 fork 来源 |

**施工与成熟度：** 本次名称上限与请求范围隔离已获用户批准，修订取代未实施的旧容量/全桶资格草案，无旧数据迁移。先在独立 tag 切片实现名称/容量/路由及损坏裁决，再完成正式空桶初始化、CreateTag→ResolveTag→冷重开，补扫描EOF/Outcome/资源与历史互操作验收。所需tag底座能力已公开，不新增路径、索引或RBF prefix接口；D8设计关闭。独立完整校验仍承担全桶健康与重复检查，不由日常成功替代，本专项不另立泛用audit API。尚缺VersionStore实现、真实冷进程/中断、平台、资源和包证据，不宣称Ready/Accepted。`tags`根准入应在S4工厂实现时认识，桶内容能力可后续施工；目录布局认识不等于tag已支持。

依据：本篇既有 tag 合同从 `35d6c2d` 的 S5 迁入；[S5](05-versionstore-names-and-indexes.md)现只保留导航与历史互操作。[S4 下层契约](04-versionstore-publication.md)、[RBF 公共扫描/CRC/恢复](../Rbf/rbf-interface.md)、[RbfFile 公共容量与尺寸入口](../../src/Rbf/RbfFile.cs)、[BPV string 选择](../../src/Binary/BareValueEncoding.Strings.cs)、[分配前限制与 cursor 提交](../../src/Binary/BareValueReader.Strings.cs)、[轻量计划](../../src/Binary/StringEncodingPlan.cs)是本次机制依据。[RBF 正扫测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)是底座证据；冻结旧栈[不可变 tag 测试](../../tests/EventJournal.Tests/ImmutableTagTests.cs)与[书签消费者](../../../durable-graph/tests/DurableGraph.Persistence.Tests/RepositoryTagTests.cs)仅提供工作流背景，不引入旧 API/格式，也不构成本专项实施验收。
