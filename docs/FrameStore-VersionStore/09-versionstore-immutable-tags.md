# S5-B 专项：不可变 tag

日期：2026-10-10。状态：**Draft；名称相等/输入域已由用户采用，名称 codec、完整记录容量与固定路由参数经三方独立审查及交叉质询定稿；既有桶初始化/扫描、data-first 与 PublicationOutcome 合同迁入本专项。VersionStore 尚未创建，无本专项的实现或验收证据**。

本文属于 [S5](05-versionstore-names-and-indexes.md) 的 S5-B，不是新增项目或阶段。建议先完成 S4 ref 闭环与 S5 历史/fork，再施工本文 tag，最后补齐 S6 tag 组合消费；tag 的机制依赖 S4，历史回调互操作另消费 S5，不将这份施工建议变成新增依赖。执行保持单 owner/driver 串行与 ProcessCrashOnly，不增加并发或断电保证。统一版本、持久身份、RootMap、借入 data、根/锁、唯一 pending 及停用清理由 [S4](04-versionstore-publication.md) 唯一定义；本篇是 tag 名称、桶及其操作的唯一权威，不复制下层格式或尺寸算法。

## 需求、边界与公开结果

用户移除 branch 名称层后仍保留命名不可变 tag：名字标识创建时的一份完整 RootMap。这里的“只读 tag”指已创建内容不可变，仍提供 CreateTag 与 ResolveTag。tag 不分配 RefId，不绑定 ref，不随 ref 更新，不保存精确 RefRevision/来源，也不提供删除、改名、覆盖或同值幂等创建。应用需要历史选点与来源时使用 S5 的 RefRevision/ForkRef，不能从 tag 字典同值推出来源资格。

### spec [S-VS-TAG-ROOTS-IMMUTABLE] tag 一次冻结完整根字典

每个 tag MUST 保存 `TagName + 完整 RootMap` 的一条 RBF3 TagRecord。从当前或历史快照创建时，仅复制所选 Roots；后来 ref 更新不改变它。同名再次创建 MUST 在本次 data barrier/私有初始化/tag 输出前拒绝，即使字典同值。空 RootMap 合法，与缺失 tag 不同。

RootMap 的公面、自有冻结、输入捕获、默认地址/重复 key 拒绝与 codec 唯一消费 S4 `[A-VS-ROOTS-OWNED]`、`[F-VS-ROOTMAP-BPV1]`；地址消费其中引用的 S2 变长 codec。tag 不另定地址、字典比较或旧 fixed12 fallback。应用仍负责地址来自绑定 data、必要新增依赖已完成且闭包不含 provisional/canceled 地址；库不遍历该图。

首版候选公面如下；这是待实施合同，不是已存在的 API：

```csharp
AteliaResult<IReadOnlyDictionary<string, FrameAddress>> CreateTag(
    string name, IReadOnlyDictionary<string, FrameAddress> roots,
    out PublicationOutcome outcome);

AteliaResult<IReadOnlyDictionary<string, FrameAddress>> ResolveTag(string name);
```

成功均交付非 null、自有且无公开修改入口的 S4 RootMap；CreateTag 可复用已捕获的冻结值，ResolveTag 返回本次扫描中已校验的值。不新增 TagSnapshot、tag revision、结果 Lease 或 Dispose；结果在查询结束、owner fault/Dispose 后仍有效。ResolveTag 健康缺失返回 TagNotFound，不返回 default 或空字典。查询只证明目标桶的发布记录健康；应用须按地址调用 data.ReadFrame 检查所需内容，失败时不得回退、改绑或修改 tag。

## 名称接受合同与 codec

### spec [S-VS-TAG-NAME] 非空名称按 Ordinal 原样比较

本轮用户已采用：TagName MUST 为 **non-null、non-empty .NET string**，相等使用 `StringComparer.Ordinal`，区分大小写；`Release` 与 `release` 可以分别创建并解析。MUST 原样保留 code-unit 序列，不 trim、不做 Unicode normalize、大小写转换或 replacement。空串被拒绝，非空空白名合法；NUL、控制符、路径分隔符、U+FEFF 与未配对 surrogate 均无额外字符拒绝，长度受下面的记录容量合同约束。

名称是 record 字段，不成为文件路径组件，不继承 branch 或冻结旧栈的 ASCII、128B、文件名字符限制。当前 VersionStore 无实现、已发布数据或新栈消费者，不增加旧 tag 格式兼容义务。

### spec [F-VS-TAG-NAME-BPV1] 名称复用普通无损字符串

VersionStoreFormatVersion=1 MUST 选择 [BPV1 ordinary BareString](../Binary/bare-primitive-value.md)；writer 使用 `BareValueEncoding.PrepareString` 与 `BareValueWriter.WriteString(in plan)`，reader 使用现成 `BareValueReader.ReadString`。不使用 nullable/control、压缩、独立名称版本或手写 UTF 编码。

BPV 普通字符串保留完整 UTF-16 code units，未配对 surrogate 可走 UTF-16LE；默认 writer 选择较短的无损 payload，持平选 UTF-16LE。reader 接受 BPV 规定的另一种合法字符串表示与有界冗余前缀，非法 UTF-8 明确拒绝。TagName 的相等、重复检测和路由 MUST 基于解码后的 code units；不能以 wire bytes、默认编码重算或 hash 相同代替 fullname 等值。

## 名称与完整记录容量

### spec [F-VS-TAG-CAPACITY] 组合容量只增加完整记录硬界

整条 TagRecord payload MUST 直接消费 RBF3 硬界，不新增独立名称配额。记 `Pmax = RbfFile.MaxPayloadAndMetaLength`（tag 的 TailMeta 为 0）；`N` 为完整 TagName codeword 字节数，`M` 为 S4 RootMap codeword 字节数。两入口在文件 I/O 前 MUST 共用名称准入：先检查 non-null/non-empty，再使用 BPV plan 取得默认输出的精确 N，要求 `N <= Pmax - 1`；这里的 1B 是最小 RootMap，不是新的配额。无法由 BPV 表示或超过此界的名称属于确定输入拒绝，ResolveTag 不探桶后报 TagNotFound。CreateTag 复用该 plan，再按请求的实际 M 校验完整记录：

```text
名称符合 [S-VS-TAG-NAME]
1 <= M <= S4 的 RootMap 上限
P = checked(N + M) <= Pmax
RbfFile.MeasureWriteSize((int)P, 0) 成功
本次实际追加起点符合 SizedPtr 的公开范围
```

N 包含实际字符串 header 与 payload；M 的限额及计量原样消费 S4。名称的独立最大 codeword 可由 RootMap 至少 1B 得到 `N <= Pmax - 1`，但实际请求仍须按自己的 M 检查 N+M，不能保证最大名称与最大 RootMap 同时装入。全部算术先用 checked long，在已验范围后转 int；物理 FrameLength/AppendLength 只由公共 MeasureWriteSize 取得，不复制 RBF padding/footer 公式。这里只限制单记录，文件的下一次起点另检查；合法末帧可越过 SizedPtr.MaxOffset 的末端，下一次追加再拒绝，不隐式轮转或缩小末帧。

**输出前。** 先完成名称准入和 BPV plan，捕获/度量实际 RootMap，组合 P 并检查完整帧与实际起点；通过后才冻结/物化最终输出，全部在本次 data barrier 与输出前。StringEncodingPlan 只保留不可变 string 与编码选择，不物化完整名称 payload；`EncodedLength` 是默认输出的精确 N。不能先分配超限完整记录再拒绝、截断名字、丢根或拆记录。输入字符串已由调用方提供；这些检查不撤销调用方先前分配，也不保证合法请求必然分配成功。

**完整帧租用前。** 从真实主链取得 RbfFrameInfo 后先检查 Rbf3、kind/meta/tombstone、PayloadLength 的本版本范围与公共 `MeasureWriteSize(PayloadLength, 0)` 对应的 FrameLength；TagRecord 的 payload 范围为 3..Pmax B：非空名 codeword 最少 2B，空 RootMap 最少 1B。不得先租用超限全帧再拒绝。本版本的完整 tag 上限就是公共 RBF3 单帧硬界，不再以 `名称默认 Measure + 1MiB` 作为已读记录的真实长度。

**字符串分配前。** 宿主 MUST 先从完整 payload 借入扣除末尾 1B 的受限窗口，以局部 BareValueReader 直接调用现成 ReadString。reader 自身在分配前检查有界 header、声明 int 范围及完整字符串 payload 是否落在窗口内，因此为 RootMap 保留至少 1B；不另写 header 窥读器、不复制该窗口。随后按该 reader 的实际 ConsumedCount 在原完整 payload 中继续读取 RootMap，名称非空/路由、S4 的实际消费与 1MiB 检查均通过，最后要求整个 payload 完全消费。名称 reader 的窗口可能还包含部分 RootMap，不要求该局部窗口被完全消费。不得先分配名称再发现声明越界或它吃掉必需 suffix。

容量计量 MUST 包括合法冗余 string header、另一种合法字符串表示与 RootMap 内的实际冗余字节。名称分配前限额来自该记录窗口，RootMap 限额来自 S4；`Measure(decodedName)` 不能代替 N。RootMap 是可组合 prefix，按实际消费累计其 1MiB 限额，不把宿主交给它的全部 RemainingCount 当成 RootMap 长度；宿主最后另拒绝 trailing。完整解码/拒重/冻结成功才提交宿主结果，失败不交部分值。

（Informative）这项选择不承诺小内存：当前公开 RBF3 硬界为 268,435,424B，名称 codeword 可接近 256MiB；ASCII 对应的 decoded string 字符存储可接近 512MiB，单次完整帧与字符串合计可接近 768MiB，尚未计临时 fullname 集合、当前/命中 RootMap、输入与编码缓冲。桶累计名称字符量没有固定总上限，合法输入仍可能 OOM。当前没有新栈工作集或明确小内存目标要求额外配额，实施需报告峰值及连续同桶创建成本；不建立总内存账本、通用预算、租借 API 或流式 tag 格式。若出现名称/桶资源目标，再评估更小接受域或其它方案，不能在已写入的同一格式版本下默改容量规则。

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
| 后续 TagRecord | 完整 TagName codeword；原样 S4 RootMap codeword | 名称合法且重新路由到本桶；CRC、RootMap 与整条 payload 完全消费 |

统一格式版本选择整个桶的名称/RootMap 组合 schema，不增加 HeaderSchemaVersion、TagRecordVersion、额外 magic、内容 CRC、预留字段或每记录身份/桶号；RBF kind 与完整 CRC 已承担对应职责。未知版本/FrameTag、后续 header、tombstone、未知 meta 形状或 trailing bytes 皆报错；首版不在同桶混合 schema。

以 checked 门版本、本次期望 VSID/BucketId 编码 canonical header，得确定 payload 长度 L；令 `H = RbfFile.MeasureWriteSize(L, 0)`、`I = RbfScanBoundary.Empty.EndExclusive + H.AppendLength`。共同首帧检查使用 `ScanForward(showTombstone: true)`：要求 Rbf3，首物理帧 offset、H.FrameLength、payload L、kind/meta/tombstone 匹配，再完整读取 CRC、精确解码并比对期望身份；checked 后界必须为 I。缺首帧先检查 TerminationError，再报错，不找后续替代 header。

可写访问正式桶 MUST 在 public `RbfFile.OpenExisting` 前用公共文件长度入口拒绝实际长度 < I，并先关闭探测句柄再进入独占 RBF 工厂。非 None 恢复须有 `AffectedFrameOffset >= I`；随后执行共同首帧资格，只允许 header 后的 tag 残尾恢复，不补造/认领 header。header body 完整但缺 Key/Fence 时也应在 I 前检拒绝，不能先 CompletedTail，再于下次 Action=None 收养。长度足够但 header 内容坏仍拒绝；不承诺底层 tag 尾恢复前完全不修改坏文件。只读使用 public `OpenReadOnlyExisting`，残尾拒绝且不修复。

## 每次目标桶完整检查

ResolveTag 与 CreateTag 查重复用一个内部同步扫描流程；不保留跨调用 name→ticket/RootMap 表、负缓存或持久名称索引，不外泄 sequence/info/reader，不增加 visitor、扫描预算或缓存 API。

1. CreateTag 第一条语句初始化 outcome=NotAttempted；两个入口均在文件 I/O 前检查 owner disposed/fault 与上述共用名称准入，包括默认 N 可容纳最小 RootMap；CreateTag 再检查可写模式及 S5 HistoryActive mutation guard。借入 data 与 single-driver 的持续责任消费 S4，不为纯 tag 查询另建 data 健康探测。
2. 只取得该名字的正式规范桶，按访问模式打开并完成上述 header/恢复资格。沿 `ScanForward(showTombstone: true)` 检查真实链；完整帧读取前先按本版本检查 info，每条 TagRecord 均完整 CRC、名称/路由、RootMap codec 检查。非目标记录的坏 RootMap 也报错，不能只读名称或 framing。
3. 本次临时 fullname 集合按 Ordinal 检测所有同名记录，字典同值也冲突；hash 碰撞而 fullname 不同合法。逐帧释放 RbfPooledFrame，仅保留命中的自有 RootMap，不收集所有 RootMap/ticket。命中后继续扫描，不采用 first-match；每次 MoveNext=false 都检查 TerminationError。
4. 正常 EOF 与必要关闭成功后才交付 ResolveTag 命中/健康 TagNotFound。CreateTag 用同一完整扫描判同名拒绝或继续创建，已有桶从查重至 Append 复用同一 Idle 句柄；不关闭后重新查重，也不建立并发快照协议。

每调用只有一个本次桶句柄、当前完整帧/必要 RootMap、命中自有值和 O(该桶 fullname 数及字符总量) 临时集合。局部 finally 在 public Dispose 前先取出并清空句柄槽，所有退出只归还一次，主错误优先。ResolveTag 无应用 callback；从历史 visitor 调用时不覆盖历史当前临时 ref 槽。普通内容/codec 失败不自动 fault 健康 writer，实际恢复/owned 清理失败依 S4 停用；必要 close 失败不能返回成功或健康缺失。最终交付前复检 owner disposed/fault。

## 创建、空桶发布与三态证据

CreateTag 直接消费 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]`、`[A-VS-PUBLICATION-EVIDENCE]` 与 `[R-VS-PUBLICATION-UNKNOWN]`，不调用其他公开 mutation 拼成本次操作。入口第一条语句初始化必选 out 为 NotAttempted；完成名称、全桶查重、RootMap 私有捕获/编码、完整记录尺寸与实际起点检查。缺桶时另预检 header 与首 tag 尺寸、从 I 起的追加资格。确定拒绝及纯输入捕获/分配失败先于本次 data barrier、私有初始化/tag 输出，不新增 fault。

随后同步调用 `data.ConfirmDurable()`。无关 data Builder 仍 Building 不阻断创建，本根必要依赖仍由应用保证完成；屏障确认所有必要完成输出，不签发未完成依赖资格。屏障异常本次 tag 保持 NotAttempted、VersionStore 停用；data 按自身 fault，不 Dispose 借入 data。

缺桶时在 S4 唯一 `creating/pending.rbf` 写 header、执行共同首帧检查并要求 TailOffset=I，再 DurableFlush/close，以同文件系统 no-overwrite file rename 公开合法空桶。该初始化包括可选 tags 目录建立均不推进本次 tag outcome；打开并验证正式空桶后才追加首条 tag。首 tag 与后续 tag 使用同一普通正式 Append/flush 协议：

| 边界 | 本次 tag outcome | 失败/后续行为 |
| --- | --- | --- |
| 确定准入/查重/输入/容量拒绝 | NotAttempted | 无本次额外 data barrier/tag 输出，健康实例可继续 |
| data barrier、私有 header/flush/close、空桶 rename，尚未尝试 tag Append | NotAttempted | 异常停用并完整受控 teardown；空桶 rename 不确认首 tag |
| 正式 Append 调用之前 | 立即写回 Unknown | Append/flush 异常保留 Unknown，不按异常或旧 tail 猜零输出 |
| Append 正常返回 pre-I/O 失败 Result，证明无本次公开 tag 输出 | 恢复 NotAttempted | S4 唯一降档例外；返回确定失败，不新增 fault |
| Append 成功且 DurableFlush 正常返回 | 立即写回 Confirmed | 此后才关闭/交付；后置异常保持 Confirmed，停用 |

正常成功 Result 对应 Confirmed，正常失败 Result 对应 NotAttempted；Unknown/Confirmed 后异常不伪装普通失败。内部助手直接更新同一 out，finally/清理不重置；Confirmed 之后无缓存安装相位，但关闭/交付仍可失败。停用先使 owner 失效，按 S4 完整清理全部 owned 资源、根锁最后，不 Dispose 健康借入 data。

合法正式空桶保留，不删除或自动补首 tag；pending 残留按 S4 `[S-VS-CREATING-WORKSPACE]` 可写取消/只读保留，不读私有内容、不认领角色/桶号或续旧请求。该策略不放宽正式桶的 I 前检。header 后未完成 tag body 可由 RBF 截回真实边界；完整 body 缺原 Key/Fence 可 CompletedTail 成为完整记录，随后仍须全桶重验。完整坏记录不跳过、不回退、不改绑。

重开以实际 checked ResolveTag 接受当前内容，不重建旧 outcome；完整 tag 不证明旧同步调用曾 Confirmed，同名/同值也不成为旧尝试身份。未知结果后由应用检查实际名称与业务内容并决定续行，不机械重复 create-only，不新增 receipt、尝试 token 或 exactly-once 账本。

## 成本、施工与实施验证

每次查询/重名检查包含输入名称度量/hash、规范路径的目录名称/类型发现与 O(目标桶记录数 + checked bytes) 的扫描；固定桶数也不意味着只需一次 metadata 调用。临时 fullname 集合成本还含该桶名称总字符量；只保留当前/命中完整字典。连续创建同桶 n 个 tag 可累计 O(n²) 记录检查。分桶影响文件/header 开销、单桶终身容量压力及坏记录连带范围：坏 A 与健康 B 同桶时查询 B 也失败，其他桶不由此判坏。惰性 Open 不审计全部桶；分桶不承诺 O(1) 查询、长期无限容量或性能目标已经满足。

实际成本超出目标后再设计跨调用位置表、其更新/资格与持久索引；首版不预建 catalog、checkpoint、轮转 manifest 或通用预算。tag 查询与创建实现需与 S4/S5 原有生命周期、历史回调和根锁共同接受评审，源码、真实进程中断、平台、性能与包资格分别取证。

| 验证组 | 必须覆盖 |
| --- | --- |
| 名称/codec/路由 | null/empty 拒绝；非空空白、NUL/控制符/分隔符/U+FEFF/未配对 surrogate 原样；Release/release；同名不同合法 BPV codeword、冗余 header；跨冷进程同桶；合法 hash 碰撞按 fullname 区分 |
| 容量/分配前检 | 两入口同一名称准入、超界查询先于 I/O 拒绝；名称+实际 RootMap/完整帧边界；RootMap 独立 1MiB；声明巨大/overflow/截短/缺 suffix 在名称分配前拒绝；冗余实际字节超界；超限帧在 pooled 租用前拒绝；确定输出前拒绝与合法末帧越过起点上界后的下一次拒绝 |
| 桶/路径 | missing 与合法空桶；可选 tags 缺失/首次建立/RO 不创建；未知名/别名/特殊类型/同文件系统/枚举失败；bare Fence/短 header/缺原 Key/Fence 重复可写打开均拒绝；错版/身份/桶号及后续 header |
| 全桶资格 | 命中后方重复 fullname（同值/不同值）、非目标 CRC/RootMap 坏、错路由、tombstone/unknown kind/meta、TerminationError 与 close 失败均传播；不交部分或健康缺失 |
| 自有值/资源 | 输入后续变化不改变 tag；结果在查询/owner 关闭后有效；pooled 帧及时归还、句柄一次清理；历史 visitor 中 ResolveTag 不损害历史槽；历史期间 CreateTag pre-I/O 拒绝 |
| 发布/恢复/Outcome | 同名/容量拒绝先于 barrier；无关 Builder 不阻断；data 失败无 tag 输出；pending 中断/取消/RO 保留；空桶 rename 仍 NotAttempted；首 body 截断保留空桶、CompletedTail 全桶重验；Append/flush Unknown、flush 后 close/交付失败 Confirmed；冷开不认领旧尝试 |
| 组合消费 | 历史选点后创建 tag，ref 后续更新不影响它；应用按地址读取坏 data 时明确失败、不回退；冷重开取 tag Roots 可作无来源 CreateRef，不能由 tag 直接取得精确 fork 来源 |

**施工与成熟度：** 先在独立 tag 切片实现名称/容量/路由向量，再完成正式空桶初始化、CreateTag→ResolveTag→冷重开，随后补全部扫描/损坏/Outcome/资源与历史互操作验收。设计合同与现成底座接线已经足以开工，不需新的 RBF 公共能力或额外产品决定；D8 移出待定队列。尚缺 VersionStore 实现、真实冷进程/中断、平台、资源和包证据，不宣称 Ready/Accepted。`tags` 根准入资格应在 S4 工厂实现时认识，桶内容能力可以靠后加入；目录布局认识不等于 tag 已支持。

依据：本篇既有 tag 合同从 `35d6c2d` 的 S5 迁入；[S5](05-versionstore-names-and-indexes.md)现只保留导航与历史互操作。[S4 下层契约](04-versionstore-publication.md)、[RBF 公共扫描/CRC/恢复](../Rbf/rbf-interface.md)、[RbfFile 公共容量与尺寸入口](../../src/Rbf/RbfFile.cs)、[BPV string 选择](../../src/Binary/BareValueEncoding.Strings.cs)、[分配前限制与 cursor 提交](../../src/Binary/BareValueReader.Strings.cs)、[轻量计划](../../src/Binary/StringEncodingPlan.cs)是本次机制依据。[RBF 正扫测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)是底座证据；冻结旧栈[不可变 tag 测试](../../tests/EventJournal.Tests/ImmutableTagTests.cs)与[书签消费者](../../../durable-graph/tests/DurableGraph.Persistence.Tests/RepositoryTagTests.cs)仅提供工作流背景，不引入旧 API/格式，也不构成本专项实施验收。
