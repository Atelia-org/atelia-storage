# HeadSetTag 具名成员表替换旧 bytes tag

日期：2026-10-16。状态：已实施，Windows源码、进程与本地包消费验收通过；当前合同已归入 [09 tag 规范](09-versionstore-immutable-tags.md)，本篇记录决策和施工范围，不另立第二份合同。实际结果见[14验收记录](14-headset-tags-acceptance.md)，不沿用旧bytes tag资格。[重命名方案](13-headstore-renaming-plan.md)先行（行为保持重命名已通过 Windows Release 源码验收，范围以该篇第6节为准），本篇用目标名称描述新合同。设计审阅不是源码、进程或包消费验收。

## 1. 最小模型与需求账本

`CreateTag(name, heads)` 在一个同步操作中选择每个 Head 的当前已接受 Entry，把固定成员表作为一条具名记录发布。它不复制 Entry payload，不移动 Head。重开后 `ReadTag(name)` 取得成员表，再按需 `ReadEntry(id)`。

| 需求 | 来源 | 保留的可观察行为 / 边界 |
| --- | --- | --- |
| 替换旧 tag，输入 HeadId 集合并捕获当前位置 | 用户当前确认及下游移交提案 | 不保留独立任意 bytes、Related 或显式 EntryId 创建入口 |
| 名称 create-only，按 Ordinal 比较 | 提案；当前 tag 源码与测试 | 不覆盖、改绑、删除或自动生成名称 |
| 非空、同库、无重复、存在且可读的选择集合 | 移交提案的准入建议，本篇采用 | 不跳过坏成员，不默默合并重复 |
| 固定成员在冷重开后仍能精确读取 | 提案；既有精确读取和 CU 合同 | 成员是正式依赖；不降级为 Related 注记 |
| 串行 owner，独占可写、共享只读，ProcessCrashOnly | 当前 S4、CU、RBF 合同与实现 | 不引入线程安全、MVCC、掉电承诺或跨调用读隔离 |
| data-first、完整帧 append-only、Outcome 与 fault 分离 | 当前发布源码和故障测试 | 不能删除完整记录撤销失败；Unknown 后重开按名称裁决 |
| 元信息轻量读取、完整内容按需读取 | 提案及现有 metadata-first 行为 | 信息/扫描不声称验证成员内容 |
| DG 当前 tag 是历史图帧书签 | DG RepositoryResources.MetaBootstrap 源码 | 新 HeadSetTag 不直接覆盖这一用途，阻止无适配升级 |
| 不加旧 API alias、双写、旧 payload 猜测转换 | 提案兼容性边界，用户认可方向 | 开发数据需要显式重建；普通 Open 不自动迁移 |

证据基线与移交来源见重命名方案。由该提案派生的本篇和审阅结论不是相互独立的需求来源。本篇采用的格式、容量、依赖确认算法、重入 guard 和 Audit 范围经第10节裁决；后续实施如出现反证，应记录反证并修改本篇，不暗中扩大公面。

## 2. 公面与返回值

2026-10-16 本轮命名裁决：主线程与独立只读 subagent 比较 `HeadTag` 和 `HeadSetTag` 后，选择 **HeadSetTag**。对 coding agent，类型名直接说明多入口集合，比省略 `Set` 更能避免把它当作单个 Head 的附加标签。集合允许只有一个成员；`HeadSet` 仅是名称中的集合限定，不新增独立对象、身份或 API。其内容始终为 `HeadId → HeadEntryId` 固定表，不能退化成只保存 HeadId 的集合，也不随之后的 Head 发布移动。

方法采用 `CreateTag / ReadTag / ReadTagInfo / ScanTags`；完整对象及辅助类型采用 `HeadSetTag / HeadSetTagInfo / HeadSetTagScanEnd`。已有 `TagCount / TagBucketCount / TagBucket / MaxTagNameCodeUnits` 和内部桶、记录的 `Tag` 词根保留；不为统一前缀额外重命名整个桶实现。本文文件名采用 `14-headset-tags-plan.md`，仓内引用同步更新。旧提案中的 `HeadSnapshot` 名称不再作为目标 API。

**同名不等于兼容：** 新 `CreateTag` 接收 HeadId 集合并返回 HeadSetTag，替换旧任意 bytes / Related overload；新成员表与格式版本2仍须完整实施。此前选择 HeadSetTag 名称的修订仅调整目标标识符；本轮实施范围包含下述创建、确认、校验、故障与交付行为变化。

本轮已实施的公开签名（验收范围见14验收记录）：

```csharp
AteliaResult<HeadSetTag> CreateTag(
    string name, ReadOnlySpan<HeadId> heads, out PublicationOutcome outcome);
AteliaResult<HeadSetTag> ReadTag(string name);
AteliaResult<HeadSetTagInfo> ReadTagInfo(string name);
AteliaResult<HeadSetTagScanEnd> ScanTags(
    Func<HeadSetTagInfo, bool> visitor, CancellationToken cancellationToken = default);
```

使用 `ReadOnlySpan<HeadId>` 接受数组/collection expression，入口复制有效 slice；不支持任意 IEnumerable 的便利重载。这样捕获输入不执行用户枚举器、Count、comparer 或 Dispose 回调；调用方仍须保持输入稳定。它实现 HeadId 集合语义，不要求调用方预读 Entry。

`HeadSetTag`：sealed、自有、不可变、内部构造；`Name: string`、`RecordedAtUnixMs: long`、`Entries: IReadOnlyDictionary<HeadId, HeadEntryId>`。实际 backing 不通过正常接口或集合 cast 暴露写能力；不持 owner/lease，不需要 Dispose。每个 key 等于 value.HeadId。输入顺序不构成语义，不承诺公开字典的枚举顺序。时间是非单调 UTC Unix 毫秒，不作排序或事务身份。

`HeadSetTagInfo`：sealed、自有、不可变、内部构造；`Name: string`、`RecordedAtUnixMs: long`、`EntryCount: int`，不含 Entries、payload 或来源坐标。EntryCount 是经独立元信息校验的声明值，不表示已遍历成员表。`HeadSetTagScanEnd` 保留 Complete/VisitorStopped。RootPayload 与 Related 公面退出；HeadEntry.Payload 继续为只读 bytes，不新增 HeadEntryPayload 类型。

| 旧 tag 公面 | 本片替换结果 |
| --- | --- |
| CreateTag 的两个 overload | 方法名保留，签名替换为 CreateTag(name, heads, out outcome)，返回 HeadSetTag；删除 bytes 与 Related overload |
| ResolveTag / RootPayload | ReadTag / HeadSetTag；不是原返回类型换名 |
| TagInfo / TagScanEnd | 行为替换片使用 HeadSetTagInfo / HeadSetTagScanEnd；前者删除 Related、增加 EntryCount，不列为首批纯 rename |
| ReadTagInfo / ScanTags | 方法名保留，返回值和 visitor 改用新类型；不沿用旧 Related 合同 |
| MaxTagNameCodeUnits | 保留现名与1024上限；行为保持重命名片已完成名称纠正 |
| Audit.TagCount / TagBucketCount | 名称保留；格式2下统计新成员表及其桶，Audit 增加成员精确读取资格验证 |
| HeadStoreRecoveryFileKind.TagBucket | 名称及枚举值保留；物理路径仍见第5节 |

## 3. 稳定捕获与写入流程

选定的最小流程：

```text
outcome = NotAttempted
检查 owner / 可写 / history与scan guard / 名称
进入本次 tag-create guard（finally 退出）
用真实 Span.Length 与名称编码计划先检查非空及自然容量
复制 HeadId slice，检查同库、重复，按 LocalId 排序
查重名称，检查真实输出位置的完整帧容量
对所选 Head：用既有 current-read 判据选取已接受 Entry
  收集该 Entry 的必要发布文件；CU 包含完整同组成员
准备成员表、元信息及自有返回对象
data.ConfirmDurable()
按 LocalId 顺序逐个确认必要发布文件，关闭本次临时句柄
按既有桶初始化与追加方式写一条完整 TagRecord
  Append 前 outcome = Unknown
  桶 DurableFlush 正常返回后 outcome = Confirmed
交付预构造结果
```

增加一个窄的 tag-create 活动标志；全部 mutation 的共同准入在其活跃时返回 `HeadStore.TagCreateActive`，包括递归 CreateTag。标志持续到整个操作的 finally；它覆盖取时和故障测试 hook 的重入窗口，不充当互斥锁，也不建立通用重入框架。每次可执行回调/hook及 owned I/O 后复检 owner，任何下一次 I/O 前发现 Dispose/fault 即停止；Dispose 本身不被 mutation guard 拒绝。桶 flush 正常返回后先无分配地设 Confirmed，再进行 hook、owner 复检或其他可能失败的工作。

已有 history/scan visitor 的查询能力保持；CreateTag 在这些 visitor 活跃时拒绝。CreateTag 不提供公开 callback，也不因此新增“在创建中嵌套任意查询”的支持。应用仍遵守单 driver；对内部 hook 的窄防护不构成线程安全承诺。

内部使用 current-read 内核而非循环拼接公共方法。在同一次接受记录的判定中取得 EntryId 及其普通/CU 依赖，不通过“只返回 HeadEntry 后另建一套 CU 解码/判据”来补信息。合法但未成立 CU 按现有规则跳过；必要损坏/I/O 错误终止，不回退掩盖损坏。可先复用完整 Entry 读取并在每个选择完成后释放不再需要的 payload，未来有实际测量再优化为不交付 bytes 的内部路径。

惰性恢复不要求全库 preflight：按07的规则，必要 peer 必须先经正常打开/恢复，再裁决缺席。成立组已核验所有指定记录；未成立组的合法 EOF/异记录见证在无新追加、无外部篡改时保持成立。后续选择触发别处 CompletedTail 不能消除已取得的否定见证，也不能改写此前接受的完整记录。若某 peer 的原 body 足以完成尾部，第一次需要它时就应恢复并参与裁决，不能先视为缺席再继续选取。

两次独立单 Head 发布之间捕获的组合是真实当前状态。`PublishHeads` 和 `CreateTag` 仍是两个操作；前者成功而后者失败是合法结果。不提供“发布并命名”事务。

## 4. 成员资格与依赖确认

选择集合 S 与验证依赖 D 不相同：若只选择 A，但 A 当前 Entry 来自 A/B CU，HeadSetTag.Entries 只有 A；D 包含 A/B 的指定 CU 记录。后续 ReadEntry(A) 仍验证 B 的指定历史记录，不要求 B 当前值仍属于该组，也不递归验证 B 后来的另一组。

首版采用直接确认：从成功 current-read 的普通 Entry/CU 取得必要文件集合，去重后在 data barrier 之后、TagRecord Append 之前 DurableFlush。只为最终选中的已接受发布收集依赖；不能把为跳过未成立 CU 所触及的全部文件都无条件当成正式成员。集合仅为本次操作私有数据，不持久化依赖图、不缓存跨调用“已确认”证明、不扫描全部 Head。

普通 Entry 依赖本文件；成立 CU 依赖该组全部文件。fork 的当前初始 Entry 是子文件自己的普通记录，payload 已复制，不为读取它追确认来源历史；这不意味着 fork 的继承历史从此无来源依赖。应用 data 图的合法闭包仍由应用负责；库不解析 opaque payload。

确认依据是 S0 `[S-ROOT-PUBLISHED-LAST]` 的顺序合同。现有测试已经证明：CU 全部 End 完成而 flush 失败后，旧调用是 Unknown，冷重开仍可接受该组。因此 checked-read 不能替代历史确认凭证；没有这类缓存时，直接确认 D 是最小方案。**这不是“少 flush 一个 peer 会在普通进程终止后丢失”的证明，也不增加掉电保证。** 现有 fork 只确认 exact source 文件；本片为具名坐标表选择更完整的已知依赖确认策略，不借此修改 fork。

确认时复用已 owned 的句柄；其余文件逐个短期开启、确认、关闭，不因一次快照把全部 CU peer 填入永久缓存。k 个选择、d 个去重依赖，额外标识集合为 O(k+d)，确认调用为 O(d)，确认阶段最多增加一个临时文件句柄；已有 owner 缓存另计。current-read 验证成本仍可能重复访问同组，不承诺总读取 O(d)，首版不加组缓存。

必要文件的打开、恢复、flush、清理复用现有 owner 生命周期规则；成功物理恢复继续入 RecoveryReports。依赖确认失败保持本次 HeadSetTag 的 NotAttempted，并按实际 I/O/确认/清理异常处理 fault。持久确认不是共同发布资格的替代：两者都需要成立。

## 5. 物理格式

采用统一 store 格式版本 2，保留 `versionstore.format`、`versionstore.lock`、`refs`、`tags`、`creating/pending.rbf` 路径字面量；所有新建 header 写统一版本，旧版本在门校验时拒绝。不引入双 reader 或原地转换，即使旧库没有 tag 也不例外。版本2门下混入版本1 ref/bucket header 时仍在访问该文件时拒绝；不为检查混入而扩大 Open 为全库审计。纯重命名片仍写版本1，只有本片改变格式。保留路径是缩小改动，不表示 tags 目录仍接受旧 tag payload；不要求 FrameStore/RBF 格式升级。

继续使用 1024 个惰性名称桶、现有名称接受域/BPV 编码、稳定路由和 header-only 初始化保护。名称 namespace 与 Head 本地数字身份独立。旧 TagRecord 的任意 bytes 不解释为新成员表。

新 TagRecord 保存 HeadSetTag 成员表，与格式1的旧 bytes TagRecord 不是同一 payload 合同。其 schema 由统一版本2与 FrameTag 选择：BucketHeader 仍用 tag0，TagRecord 用 tag1；普通 Entry/CU 的 tag 数值不变，不增加每条记录或成员的版本字段。

| 位置 | 内容 |
| --- | --- |
| BucketHeader | 统一版本、Store 身份、桶号；沿用既有 header 结构 |
| Payload | 按非零 LocalHeadId 严格递增的成员项；每项 uint32 LE LocalHeadId + uint64 LE 完整 Packed SizedPtr |
| TailMeta | 依次 int64 LE RecordedAtUnixMs、uint32 LE EntryCount、BPV ordinary BareString 名称、uint32 独立 MetaCRC codeword |

EntryCount 只在元信息保存一次；完整读取要求 PayloadLength == 12 × EntryCount。ticket 使用 Packed/FromPacked 的完整8B表示，不改用 offset 或时间戳。先按实际长度和上限检查再分配，拒绝重复/乱序/零 ID、default或零长度 ticket、截短和多余 bytes；记录存在性、具体kind/尺寸、完整内容与必要CU资格由ReadEntry/Audit按既有协议检查。普通ReadEntry/Fork的成员来源依赖库签发/完整表恢复、完整位置不删除复用与无外部改写，不新增前缀或后缀全链扫描。RBF3正常writer保持marker-free；离线构造内嵌frame不构成正常过程反例，不据此扩大普通精确读取。Audit仍保持既有全库物理、真实主链与引用健康检查强度。Store 身份由 checked 桶 header 绑定，不在每个成员重复保存。

不增加任意成员数硬限或 MaxTagEntries 公共常量。令 n 为真实成员数、t 为实际合法 TailMeta 长度，要求 n > 0、checked long 的 `12*n+t <= RbfFile.MaxPayloadAndMetaLength`，并通过 `MeasureWriteSize(12*n,t)` 和真实追加位置的 SizedPtr 检查。该容量独立于 MaxHeadEntryPayloadLength。名称编码接受域沿用09，名称 codeword 为2..3077B，因此新 TailMeta 为18..3093B；codec 在解码分配前按该界和真实帧尺寸拒绝不可能值。元信息声明 n 也先按其真实 t 校验可编码容量，不能直接按未检查的 count 分配集合。正常可编码输入仍可能 OOM，不承诺固定 RSS，不新增可配置预算、流式公面或压缩。

完整表的 RBF CRC 保护成员内容；独立 MetaCRC 只支持名称查重与轻量信息。沿旧Related元信息的整体准入模式，CRC、精确字段结构、名称/路由和 count 语义全部合格后，才取得可信名称；count为0或超自然容量使整份meta不合格，named查询按unknownName分流，Scan失败，不从半合法meta抢救名字，不增加第二个CRC或可信等级。

**元信息整体合格**而成员表损坏、实际payload长度不等于12*n时，名字仍占用，Info可交付元信息声明；ReadTag/Audit拒绝坏表，不能覆盖或以健康同名记录替代。可信名称唯一性、未知名、局部元信息错误和结构/I/O错误的分流沿用09，不把真正I/O错误吞为局部坏项。这里要求唯一可信目标名，允许同桶unknownName在前或在后；unknownName只使零可信命中时缺失不确定，不能阻断唯一可信目标的Info/ReadTag。重复可信目标必须失败，不能用健康副本替代。

## 6. 读取、扫描、Audit 的资格

| 操作 | 验证范围 | 不证明的内容 |
| --- | --- | --- |
| ReadTagInfo | 唯一可信目标名称命中与独立元信息 | 未知名记录中的隐藏重名、成员表及成员内容健康 |
| ScanTags | 实际桶的元信息遍历、取消/visitor/资源规则 | 全局唯一性、完整成员表、成员可读 |
| ReadTag | 名称、完整帧、元信息与 canonical 成员表 | 本次未遍历成员文件，不证明各 Entry 当前健康 |
| ReadEntry | 精确定位、完整内容、Fence 与 CU 资格 | 不解释业务 payload，不回退 |
| Audit | 只读全库完整检查，并验证每份 HeadSetTag 的成员均为正式可读 Entry | 不验证应用 data 图或业务一致性 |

HeadEntryId 有三种内部交付来源：完成发布、checked Entry read、checked HeadSetTag 成员表恢复。第三种是本片对旧 RefRevision 签发合同的有意扩展；其来源受完整记录格式与 CRC 约束，不开放裸 HeadId+ticket 公开构造。ID 是定位值，不是健康证书。ReadTag 成功与随后 ReadEntry 失败可以同时成立，不能误报整个库仍健康。ForkHead 接受这种 ID 时仍重新验证 exact 来源，不因已经内部签发而跳检。

Audit 复用内部精确读取，不复制一套 CU 判据，也不循环公开 ReadEntry 填充 owner 的永久文件缓存。复用既有 Audit 的短期成员槽，桶扫描句柄与成员句柄分开，CU peer沿既有短期槽逐个检查/关闭。内部 exact helper 按现有 ReadPublication 模式传递 cancellation/state检查和 deferOwnedCleanup，保证所有局部 pooled lease/文件先清理、根锁最后清理。不得新增常驻已接受Entry索引或审计证明缓存。取消/状态检查必须进入成员exact与CU peer工作，覆盖成员间、打开前后、完整读/peer工作前及最终交付；取消后无完整计数，Dispose/fault后无后续I/O。主异常优先，lease/member/peer/bucket/cursor资源一次清理且根锁最后；纯取消不fault，owned cleanup失败仍停用。

Audit 的 EntryCount 保留旧已发布记录计数，TagCount/TagBucketCount 表示具名记录与桶数；验证HeadSetTag成员时不重复增加EntryCount。所有统计只在完整遍历、引用检查和必要清理成功后交付。成员表正确而指向不存在ticket/未成立CU仍是Audit失败；全库每个物理文件各自健康不证明引用完整性。

## 7. 故障与交付

确定参数、名称或容量拒绝无本次正式记录输出；允许既有惰性恢复，不承诺零 I/O。data/依赖确认及空桶发布不提前推进 HeadSetTag outcome。完整 TagRecord 输出开始后未知异常保持 Unknown；公开 Append 的失败 Result 若证明无输出可恢复 NotAttempted。桶确认后立即设 Confirmed，后续异常不得降级；预构造返回值减少确认后分配，但不保证交付绝不失败。

保持完整帧 append-only；半写尾按既有 RBF 恢复处理，不能删除已完整的命名记录“回滚”。Unknown/fault 后关闭、重开并按名称检查，不自动重试或重新取当前 Head 覆盖旧请求。RO 不执行物理修复。验证范围仍是当前 ProcessCrashOnly，未新增掉电或平台保证。

## 8. DG 兼容性与范围外事项

DG 当前 ref 1 是整个分支目录，ref 2 是 shared Schema；图 checkpoint 是 FrameAddress。旧 CreateStoredTag 把选中历史 FrameAddress 写成底层 tag bytes。新 HeadSetTag 即使同时捕获两入口，也不记录调用方所选的历史图帧。

DG 必须独立安排历史书签模型后再升级 pin；可研究把书签放进受发布保护的应用目录，但本篇不替 DG 选路线。不伪造临时 Head、不移动当前 Head、不强制每个业务 branch 映射一个 Head。

延后：历史 EntryId 任意组合、HeadSetTag 覆盖/删除/GC、发布并命名事务、恢复/rewind、业务 metadata/BLOB、自动名称、持久确认缓存、通用依赖规划器、并发读快照。只有真实消费者和具体需求出现后重访。

## 9. 实施切片与验收

先完成重命名，再按一个可运行闭环实施本篇：新格式与类型 → Create/ReadTag 冷重开 → Info/Scan/Audit 与旧 bytes/Related 合同删除 → 故障/进程/包验证。前两步只是施工组织，不单独交付“新名称、旧行为”的产品版本。

| 场景 | 必须观察 |
| --- | --- |
| 选 A/B，不选 C | 成员恰好 A/B，键与 EntryId.HeadId 相等 |
| 发布后再更新并双层冷重开 | 表不变，按成员读取仍得原 payload |
| A/B 联合发布后捕获；只选其中 A | 不撕裂；必要 peer 被验证/确认但不进入公开成员；不检查peer后续组 |
| CU 全End完成但flush失败→Unknown→冷重开→创建快照 | current可接受旧组；本次先确认所选组必要依赖，再输出名称；不伪称此测试覆盖掉电 |
| 必要peer可CompletedTail，或存在合法不成立见证 | 先正常恢复再裁决，不全库preflight；逐成员选择不产生同组撕裂 |
| 单独更新 A 后捕获 A/B | 保留真实当前组合，不判断业务事务 |
| 空/default/外库/重复/缺失/坏成员 | 明确拒绝，无部分命名记录 |
| 输入切片与调用后修改；hook 重入 mutation/Dispose | 私有捕获；重入写拒绝；Dispose后不继续I/O；finally恢复guard |
| 同名、坏表但名称健康、未知名记录 | 保持 create-only 和既有缺失不确定边界 |
| 未成立 CU、peer 位置复用、peer 损坏 | 不把候选当已发布；必要错误不能降级 |
| data/依赖确认、空桶初始化、Append、flush、确认后交付失败 | outcome 与重开事实相符，无正式半表，完整帧保留 |
| Info/Scan/ReadTag；非法count与合法meta坏表 | 不读取成员payload；非法meta沿unknownName/扫描失败，合法meta坏表保持占名 |
| 完整 Audit、缺失/损坏/未成立成员 | Audit失败，普通轻量查询不被扩大为全库审计；两轮Audit不填永久cache、不重复计EntryCount |
| Audit成员/peer读取取消，与member/peer/bucket/cursor/cached/根锁清理失败组合 | 取消/状态传入内部exact/CU；取消优先、不返回统计、锁最后且一次清理，普通取消后owner可复用 |
| 实际kill：dependencies-confirmed / record-appended / confirmed | 独立进程核对必要CU依赖确认顺序、outcome窗口与冷开成员；确认后捕获表固定，测试事实单独记载 |
| 实际短写kill：tag-body-partial / tag-body-complete | 新12B成员表记录截尾/完成尾后按格式2重验；RO拒绝不改字节，RW恢复后fresh RO/ReadTag/ReadEntry/Audit一致 |
| fork 子入口 | 捕获子 Entry 身份，不折叠为源 Entry |
| 格式1与格式2；版本2门下混入旧header | 拒绝旧门或访问时拒绝混入文件，不猜转换，不静默重建 |
| 所有返回对象跨 owner 生命周期保留 | 无 lease，正常接口与 cast 不可改写 |

实施时更新 S4/S5/07–11 中受影响合同与导航，保留未变条款和历史证据身份。Release build/test 串行；正式交付运行四个隔离 public PackageReference smoke，HeadStore 仍为六个 Atelia + K4os 闭包的顶层直接引用。新的进程/平台/包结果各自记录，不沿用旧 Accepted 作为本功能资格。

## 10. 辩证审阅记录

2026-10-16 使用 dialectical-simplification：三位只读 reviewer 分别担任需求质疑、最小架构和语义防守；第一轮独立读同一目标、需求账本与生产证据，第二轮交换最强反对意见；主线程独立核对源码并修改文档。未运行产品实验，结论不标 Accepted。两轮已收敛，无需第三轮；没有新增阻塞本仓设计的产品问题，DG书签路线仍为其升级前的独立决策。

| 对象 | 裁决 | 最小理由及删除失败 |
| --- | --- | --- |
| 旧任意bytes/Related API | delete | 用户选择替换；保留它们会继续维护第二种具名对象职责 |
| 65,536固定硬限 | delete | 无当前消费者或测量依据；保留自然容量、checked算术与分配前校验即可 |
| 任意集合枚举输入 | simplify | Span私有复制实现选择集合，去掉枚举器/comparer/Dispose回调生命周期 |
| count/version/StoreId的重复持久字段 | merge | count仅meta，统一版本与Store绑定由门/header负责；完整表检查不能删除 |
| tag-create窄guard | keep | hook在选择A后改B会破坏稳定捕获；一个活动位即可，无通用操作栈 |
| 本次必要文件直接确认 | keep | accepted CU不提供旧调用确认证明；按已有顺序合同直接确认，不建立持久证明系统 |
| 统一版本2、拒绝旧格式 | keep | 旧任意bytes不能猜测转换；既有门/各header共用版本，无双reader |
| Audit成员精确检查 | keep | 健康文件集合可以包含不存在的引用或指向未成立CU，必须检查引用本身 |
| 历史组合、确认缓存、组缓存、通用依赖规划器、DG书签路线 | defer | 无本片消费者要求；真实历史组合需求或测得重复读取/flush成本后再单独重访；DG路线由其升级任务选择 |

主要立场修订：最小架构方最初建议只flush所选宿主，依据是ProcessCrashOnly与既有fork先例；防守方指出新正式引用的确认合同。复核S0与CU故障测试后，撤回“仅宿主已满足全部合同”的结论，保留本次D确认。各方同时撤回“未flush peer必然在普通进程终止后丢失”的论证；这是顺序要求，不是新增故障模型。

元信息损坏裁决：曾比较“先救出name，再独立判count”与现有整份meta准入。主线程选择后者；两者可安全，但没有当前消费者要求新增更细的损坏可用性。明确非法count使整份meta不可信，只有整份meta合格而表坏才保持可信占名，避免两套说法并存。

可观察的简化：不再有任意成员数公共硬限；创建仅一个Span签名；成员数只存一份；不新增持久依赖文件、事务ID、确认缓存、payload包装类或兼容层。实施仍只有“行为保持重命名”和“完整 HeadSetTag 替换”两片，codec/查询/故障验证只是后一片的施工步骤。

## 11. 源码与验收设计依据

以下为重命名后的活跃源码入口；证据论证仍来自记录的基线与既有测试，不改写历史验收字节。

| 证据 | 支持的判断 |
| --- | --- |
| [S0](../FrameStore-VersionStore/00-architecture-decisions.md) `[S-ROOT-PUBLISHED-LAST]`、[S4](04-versionstore-publication.md) | data-first、依赖确认顺序、owner/Outcome/故障模型 |
| [07 CU](07-versionstore-cross-ref-conditional-update.md)、[ConditionalRead](../../src/HeadStore/HeadStore.ConditionalRead.cs) | 指定peer共同判据、合法否定见证、惰性恢复与不递归后来组 |
| [CU发布故障测试](../../tests/HeadStore.Tests/ConditionalUpdateFaults/ConditionalUpdatePublicationFaultTests.cs) `AllCompleteEndsRemainUnknownAfterAnyHeadFlushFailureAndColdReadAcceptsTheWholeGroup` | 已成立可与旧调用Unknown并存，不等于此前全部确认 |
| [主类](../../src/HeadStore/HeadStore.cs)、[Fork](../../src/HeadStore/HeadStore.Fork.cs) | mutation guard、data/source确认、精确克隆和资源清理 |
| [Tags](../../src/HeadStore/HeadStore.Tags.cs)、[09](09-versionstore-immutable-tags.md)、[HeadSetTag公开测试](../../tests/HeadStore.Tests/HeadSetTagPublic/HeadSetTagBehaviorTests.cs) | 当前成员表行为、meta查重与桶复用；旧bytes/Related证据保留原revision |
| [Entry](../../src/HeadStore/HeadStore.Entry.cs)、[HeadEntryId](../../src/HeadStore/HeadEntryId.cs)、[Audit](../../src/HeadStore/HeadStore.Audit.cs) | ID签发变化、精确读取与局部审计资源，不用公共读取填永久缓存 |
| [FormatGate](../../src/HeadStore/Internal/Format/FormatGateCodec.cs)、[HeadHeader](../../src/HeadStore/Internal/Format/HeadHeaderCodec.cs)、[BucketHeader](../../src/HeadStore/Internal/Tags/TagBucketHeaderCodec.cs) | 统一版本已经是共享权威，v2无需增加版本机制 |
| [TagMetadata](../../src/HeadStore/Internal/Tags/TagMetadataCodec.cs)、[RbfFile](../../src/Rbf/RbfFile.cs)、[11 bytes合同](11-versionstore-root-payload-bytes-api.md) | 名称编码尺寸、自然容量、捕获/自有结果与OOM边界 |
| [DG接线](../../../durable-graph/src/DurableGraph.Persistence/RepositoryResources.MetaBootstrap.cs) `CreateStoredTag/ResolveStoredTag` | 真实历史FrameAddress书签，不能直接改名成当前Head捕获 |
