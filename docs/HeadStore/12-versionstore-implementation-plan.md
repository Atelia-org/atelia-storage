# HeadStore 当前续接与 VersionStore 历史实施记录

2026-10-17 持久定位值与准确起点历史增量：HeadId/HeadEntryId 的4B/12B公开codec，以及 `ReadEntryHistory` 已接通；使用时资格由正常RBF3局部边界检查保证，跨库误用仍按紧凑Handle合同处理。规范归04/05/09/11，实际新增资格单列于[12验收](12-versionstore-final-acceptance.md)。本轮不修改格式4、FrameStore地址或DG依赖。

2026-10-17 当前续接：紧凑Handle按现行[04](04-versionstore-publication.md)/[05](05-versionstore-names-and-indexes.md)/[07](07-versionstore-cross-ref-conditional-update.md)/[09](09-versionstore-immutable-tags.md)/[11](11-versionstore-root-payload-bytes-api.md)合同实施。统一格式4拒绝1/2/3；HeadId为4B上下文编码、本地号限制24bit，GetLocalHeadId替代HeadId.LocalId。引用初始化fork与16B独立tag key/value保持。本轮实际资格待[12新增验收](12-versionstore-final-acceptance.md)填写，不沿用格式3或更早Accepted；此前引用初始化fork的Windows源码、具名进程窗口及四个隔离包消费者通过记录，仍在12、[07 CU验收](07-versionstore-cross-ref-conditional-update-acceptance.md)、[16 tag验收](16-headset-tags-acceptance.md)保留当轮命令、来源与范围。以下V0–V4与2026-10-16格式2均保留历史来源；其克隆、单文件来源确认与12B同owner成员表描述不再是当前合同。

下文保留 2026-10-10/11 各片实施及验证的 VersionStore 原名、命令、统计和 revision，作为历史记录，不是待重做的队列或 HeadStore 新包资格。当前合同请从[模块入口](README.md)读取；生产指南为 [Atelia.HeadStore](../../src/HeadStore/README.md)。

更新：2026-10-11。本文是实施导航，不增加设计权威。行为以 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[07 CU](07-versionstore-cross-ref-conditional-update.md)、[08](08-versionstore-record-metadata.md) 至 [11](11-versionstore-root-payload-bytes-api.md) 为准；[S6](../FrameStore-HeadStore/06-integration-and-delivery.md) 管组合与交付。CU 已正式接纳、实施并取得独立源码验收 Accepted，新增证据另列；[既有最终源码验收](12-versionstore-final-acceptance.md)和[六包候选](../FrameStore-HeadStore/13-local-package-delivery.md)保持原来源，不含 CU。

**下游新增公面队列已收束**，续接与延后条件统一见[下游反馈重构备忘](../FrameStore-HeadStore/14-downstream-api-refinement-plan.md)。精确 revision 读取、公共组合 guard、FrameStore façade 与惰性 RecoveryReports 已实施，独立源码复核及各片实际运行范围见[增量验收](../FrameStore-HeadStore/14-downstream-api-refinement-acceptance.md)。本文下方 V0–V4/CU 保留各自完成与验收身份，不作为这些新增 API 的证据，也不把已完成项重复列为待办。

## 调度原则与整体结构

每轮先看 git 状态、当前源码及本文完成项，保留其他工作；选择能独立验证的一条路径。实施者可以调整内部表示和助手，不能把缺少实现重新扩展成已关闭的设计问题。若出现合同反例，记录具体失败轨迹与受阻分片，再调整计划。不要用 `NotImplementedException`、伪成功或 public 裸句柄导入填满候选 API。

核心只发布不透明 bytes。应用可显式调用 `RootMapCodec.Encode/Decode`，也可使用 JSON 或自己的格式；普通 ref、fork、历史、tag 和 CU 都不能调用 RootMap decoder 来判发布健康。值和结果不依赖 owner：RefId 包含持久 VSID，RefRevision 包含完整 RefId/ticket，RefBytesSnapshot 拥有 GC 数组。字段 codec 只证明格式，不能自行签发来源或主链资格。

建议维持当前窄分层：

| 位置 | 责任及依赖 |
| --- | --- |
| `RefId` / `RefRevision` / `RefBytesSnapshot` / `PublicationOutcome` | 公共值与自有结果；可信内部构造只保存已资格化字段 |
| `Internal/Format` | VSID、44B 门、24/36B ref header、4B local 字段、meta8、规范路径；无文件 I/O |
| `Internal/Tags` | tag 名称/1024桶路由、BucketHeader/变长 meta codec 与借入句柄的单桶扫描；独立元信息资格与完整 root 资格分离 |
| `Internal/Records` | CU canonical codec、ref kind/尺寸/字段资格，以及 RefRecordReader 的窄 header/普通记录读取；从借入 public IRbfFile 建立本次局部格式资格，不解释应用 bytes、不拥有文件，真实 helper 读取动作由窄回调先计费 |
| `VersionStore` / `Internal/Runtime` | 身份/模式 guard、输入捕获、data-first、PublicationOutcome 与 owned 资源清理；`VersionStore.ConditionalRead.cs` owner partial 管 exact-first 成员定位、有限 allowlist、异长真实前驱/后继与共同判据，供当前值/历史/fork/Audit 共用；PublishRefs 只更新既有 ref，历史管固定前缀/预算/回调，当前历史与短期 CU peer 各持一个可清理槽；声明图查询另持查询局部临时句柄 |
| `Internal/Storage` / `Internal/Platform` | 根准入/锁/门 I/O/发现/唯一 pending/原生不覆盖发布/按模式打开；不解释应用 bytes |
| `RootMapCodec` | 独立可选应用 codec，使用 Binary 和 FrameAddress 公共 codec；不进入 owner 的调用链 |

源码构建中新栈只引用 main 项目，现有 RBF1 冻结参考不变。2026-10-11 用户明确转入 S6-C/D 本地包交付，FrameStore/VersionStore 纳入六包 pack 清单；当前交付状态及消费证据见[本地包记录](../FrameStore-HeadStore/13-local-package-delivery.md)。历史 source-only 验收保持其原有身份。FrameStore 的 S2/S3 Accepted 不自动覆盖新的借入 guard 或 VS 实现。

## V0：已完成骨架和关键局部（2026-10-10）

该轮从 `9bc5527` 的干净树开始，提交为 `7cca95c`。项目加入 solution，完成以下局部，历史验证记录见下方：

- FrameStore 公共 `EnsureUsable()` 直接复用现有生命周期/fault 检查，允许只读及活跃 Builder，无 I/O 或确认副作用。
- VS 身份/值、门/header/meta/path codec 和独立 golden；保持 VSID、DataStoreId、RefId、SizedPtr 与 FrameAddress 各自职责。
- 普通 ref header/head 的必要读取：不预读初始或源 payload，不因内容不可解码拒绝，不回退坏 head；pooled 帧在交付自有结果前归还。
- 已有 ref 的 ReadRef/PublishRef 运行内核：先输入/容量检查，再 data barrier，正式 Append 前 Unknown，flush 后立即 Confirmed；停用时完整清理且锁最后，不 Dispose 借入 data。
- 独立 RootMapCodec：有效 Unicode、Ordinal、变长地址、1MiB 实际 codeword 界、exact-consume 与自有冻结结果。

**V0 当时没有生产目录 backend 或公开 Create/Open/OpenReadOnly；下述缺口已由 V1 部分补齐。** owner 仅能经内部资格移交构造，测试 backend 可接入真实 RBF 文件并注入失败；这证明局部读写/状态机，不证明 VS 根/锁/路径/pending/恢复的公开闭环。没有 CreateRef、ForkRef、ListRefs、历史、tag、CU 或独立完整校验入口；也不为它们提供占位方法。V0 的 internal constructor 借入复检不能替代 factory 在任何 VS I/O 前的准入检查。

内部文件接缝是可替换实施边界，不冻结为 public 扩展点。当前已有 ref 句柄随 owner 保留；后续大量 ref/历史临时句柄的策略须在对应路径明确 owned 与 borrowed 责任，不把现有测试 backend 或缓存自动当作已验收的资源方案。

## V1：普通 ref 公开持久化闭环（2026-10-11）

本轮从 `7cca95c` 的干净树开始，按平台、目录工厂、创建内核、公开验收与独立审查分包实施，未修改 FrameStore/RBF 底座或 pack 清单。现已接通：

- `Create(rootPath, data, reservedRefIdUpperBound = 0)`、`Open`、`OpenReadOnly`：借入可用性/模式检查先于 VS I/O；fresh 根锁前及锁后重检；门关闭后再校验布局、发现 H、裁决 pending。正式 ref 惰性打开，不在工厂统一恢复。
- Windows/Linux 窄平台实现：精确受管名称、ordinary/no-follow、同 volume/mount、永久 owner 锁和原生不覆盖移动。Linux 分支已写入并审查，运行资格单列，不由 Windows 结果推定。
- 两种 `CreateRef`、`ListRefs` 及原有 `ReadRef/PublishRef` 生产 backend。默认 L=0；指定创建限预留区空闲槽，自动使用 H+1，不补洞；正式坏内容仍占号并列出。
- 新 ref 先捕获 bytes/准备元信息和尺寸，data barrier 后私有初始化，flush/close 后移动。私有阶段保持 NotAttempted，正式移动调用前 Unknown，原生移动正常返回后立即 Confirmed，再无分配置 H/交付结果。移动入口允许原生调用前的资格前检，成功后不再读取、分配或清理。
- 工厂失败与运行期停用均单次清理 owned 资源、锁最后，保留原始错误顺序，不 Dispose 借入 data。pending 冷开只按实际名称/类型/同文件系统取消，不读内容或认领旧尝试。

本轮不提供历史、fork、tag、完整校验入口或 CU。没有增加公开裸句柄导入、故障注入 API 或通用文件系统框架。测试中的受控 flush/close/rename 异常与手工截尾分别记录，不是 OS 故障、真实进程终止或断电证据。

已解决的集成接缝：内部助手以 `scoped ref PublicationOutcome` 直接更新调用方证据；意外私有 Append 拒绝视为已预计算计划的不变量失效，停用并留下冷开可取消的 pending；Linux canonical 名称与大小写别名共存也拒绝；平台缺项探测不以宽泛异常捕获吞掉 owned 清理失败。这些修正未改变持久格式或产品范围。

## V2-A：有界历史读取与 RBF 边界逆扫（2026-10-11）

本轮从 `acf3560` 的干净树开始，按 RBF 增量、历史生产路径、公开读取验收、资源故障验收与独立复核分包。新增 `HistoryBudget`、`HistoryEnd` 和 `ReadRefHistory`，不增加 ForkRef/ListForks 占位 API，不改变持久格式或 pack 清单。

- `IRbfFile.ScanReverse(RbfScanBoundary, bool)` 与原有正向边界扫描共用完整 anchor/内容见证/尾 Fence 准入；序列从 anchor（含自身）向前，Empty 在非空文件上仍为空。准入不遍历后缀，也不替代 factory 当前 EOF 资格。接口文档明确区分边界准入的完整 CRC 与后续结构枚举。
- 历史先检查必要首 header，从固定实际尾部逐项完整读取；到达初始位置才检查初始内容。ForkOrigin 跳转使用 exact ticket、公共 boundary helper 和逐 byte clone 比较，源后来追加的后缀不进入继承遍历。所有交付项为自有 bytes/revision/原时间。
- 进入 ref、MoveNext 尝试及每次完整读取分别计 work；两个 boundary helper 各有一次真实完整 anchor 重读，均先扣费。当前算法普通文件 n 项用 `3+2n` 步，每个继承源 n 项用 `5+2n` 步；这是该实现的实际步骤，不是新增固定预算 API 保证。helper 取得元信息前也检查尚有工作额度，避免耗尽后额外探测。
- HistoryActive 拒绝递归历史和现有全部 mutation，必选 out 仍先 NotAttempted；visitor 可以读取当前值和 data。false 不访问更旧节点，true 到达无来源初始才 Complete；取消、callback Dispose/异常及必要关闭失败优先于正常返回。
- 只保存一个当前临时 ref 槽并供同文件查询复用；跳转和退出先清空再单次关闭，不将每个历史文件加入长期缓存。已缓存 owner 文件只借用。纯读、预算、取消与应用 callback 失败不新增 writer fault；实际 owned 清理/可写打开恢复失败停用、尝试其余资源并最后释放锁。

独立复核发现并修复了一个工作预算边界：源 header 恰好耗尽额度后，未计费的 `ReadFrameInfo` 仍可能探测 anchor。现以不扣费的剩余额度检查阻止该 I/O；测试同时检查调用计数和 public RBF 读取日志，确认两个 helper 的内部完整读取没有漏计。初次集成的测试 `params` 单项构造推断错误已修正，未改变生产 API。

继承路径使用受控、真实 RBF writer 生成的来源文件；普通 ref 的历史使用公开 Create/Publish 与冷重开。它们证明读取和资源合同，不证明 V2-A 当时尚未实现的 ForkRef 创建时源 flush/子发布协议；该资格链已由下述 V2-B 补齐。Linux、真实进程终止、负载与包消费仍分开验收。

## V2-B：精确 fork 创建（2026-10-11）

本轮从 `da922ae` 的干净树开始，按生产接线、公开生命周期/内容反例、发布故障验收与独立复核分包。接通 `ForkRef(RefRevision, out PublicationOutcome)` 和 `ForkRef(uint destLocalRefId, RefRevision, out PublicationOutcome)`；不增加 ListForks/tag/CU，不改变持久格式、平台 backend 或 pack 清单。

- 两入口首句初始化 out，复用生命周期/HistoryActive/mode guard；先拒绝 default/异库 revision、非法目标或 H 耗尽，再检查目标空槽，之后才打开源，避免已知占用仍触发源恢复。
- 源只检查首 header 与所选 exact ticket 的完成范围、kind/meta/尺寸及完整 CRC，私有捕获不透明 bytes；额外调用公共 `GetScanBoundaryAfter` 完整重验 anchor 与尾 Fence。无需 ReadRef、逆扫、源后来追加内容或无关初始 payload；当前 RBF factory 的 EOF 资格仍独立适用。
- 普通 CreateRef 与 fork 共用 `PublishNewRef`，分别准备 24B/36B header。子 Snapshot 重新取时，原样复制源 root bytes，来源只写入子 header，不向源登记孩子。
- 所有准备先于 barrier；顺序为 data ConfirmDurable → exact 源 DurableFlush → 子 pending 初始化/flush/close → no-overwrite rename。源确认及子私有阶段失败保持 NotAttempted；rename 入口 Unknown，成功立即 Confirmed，再无分配推进自动号水位。异常沿既有 fault/单次清理/锁最后规则处理。

公开创建与历史现在组成完整的 fork-of-fork/冷重开路径。受控损坏镜像与内部故障代理分别验证访问边界和失败窗口，不能作为真实进程终止或原生设备故障资格。实际命令与结果见本文 V2-B 验证记录。

## V2-C：声明分叉图查询（2026-10-11）

本轮从 `7364166` 的干净树开始，按生产接线、公开图查询、枚举/资源故障与独立复核分包。接通 `ListForks(int maxRefs, CancellationToken)` 与只含三个自有属性的 `ForkInfo`；保留扁平正式路径和不可变 header 来源，不引入目录谱系、持久孩子表或跨调用缓存。

- 内部 backend 增加可提前停止的逐项目标访问接缝，先检查每个直接项的规范名称、普通类型及同文件系统资格，再由查询检查额度。全部正式 ref 都计数，含无来源根；额满后仍须检查实际下一项或 EOF，超额前不打开文件、读 header 或扩节点表。ListRefs 复用同一发现规则，保持完整发现语义。
- 只复用首 header 检查，不读取初始/head/source Snapshot；底层 factory 的当前 EOF 资格与可写恢复仍按原合同。收集全部节点后检查缺源和环，用迭代已完成状态避免递归及重复追祖先。
- 已有 owner 句柄与当前 history 句柄仅借用；其他文件逐个登记在本查询局部槽，清空后单次关闭，不覆盖历史槽、不增长长期缓存。取消在访问/关闭及图步之间检查；实际恢复/owned 清理失败先关闭查询资源与目录枚举器，再执行 owner teardown，锁最后。
- 结果只在完整发现、图检查、必要清理及最终状态检查成功后交付，使用私有数组薄封装，正常接口不暴露 IList/可变 backing。它声明来源关系，不签发源 revision 或证明源 ticket/clone 内容健康。

实际测试、独立复核与范围限制见本文 V2-C 验证记录。本轮未实施 tag、完整检查、CU 或包交付。

## V3-A：tag 记录与单桶扫描内核（2026-10-11）

本轮从 `33b6cee` 的干净树开始，先完整重读更新后的09，再按 codec、借入句柄读取、内容验收、I/O 边界验收与独立复核分包。采用1024桶、纯 root Payload 与独立 CRC 的可变 TailMeta；关联声明可编解码，但公开 tag 操作仍留后续分片。

- 名称保留1..1024个原 UTF-16 code units，使用普通 BPV 字符串及固定 FNV 路由；接受另一合法表示与有界冗余前缀，按实际消费长度解码。
- BucketHeader 完整 CRC/身份/桶号检查；TailMeta 用 RollingCrc 默认封口和校验，精确解码时间、可选关联和值域合法的名称。关联仅恢复坐标，不签发 RefRevision 或读取来源。
- 单桶 named 扫描只检查小 meta，目标计数饱和于2，未知名称只影响零命中的缺失判定。可信元信息即占名，root 长度非法或 CRC 坏不会使名字消失；只有唯一命中的 root 读取才执行完整尺寸、CRC 和自有 RootPayload 复制。
- 内核借入已准入的 Idle RBF 句柄，不打开/恢复/关闭文件，不接管 owner 的资源或 callback guard。真实读取失败与结构终止失败原样传播；仅本层元信息内容错误可局部隔离。

本片不接通 CreateTag/ResolveTag/ReadTagInfo/ScanTags 公共操作，不证明正式桶 I 长度前检、空桶发布、Outcome 或全桶回调生命周期；这些资格依次由 V3-B/V3-C取得。验证结果见下方本轮记录。

## V3-B：匿名 tag 公开持久化闭环（2026-10-11）

本轮从 `a0d7211` 的干净树开始，按目录 backend、owner 发布接线、公开生命周期、故障窗口与独立复核分包。接通 `CreateTag(string, ReadOnlySpan<byte>, out PublicationOutcome)` 与 `ResolveTag(string)`；沿用 V3-A 同一 schema 写匿名 id0，不新增关联或扫描占位 API，不改变持久格式、RBF/FrameStore 或 pack 清单。

- 独立内部 `IVersionStoreTagFiles` 消费可选 tags 目录，named 请求完整资格化所有直接项名称、普通类型/no-follow及同文件系统，但只打开目标桶。缺目录/目标用 nullable 目标表示，不缓存名称或向 ref backend 的旧测试扩展空实现；公开工厂仍仅检查既定布局，不提前读取桶内容。
- 正式 writer 桶先用公共 FileStream 长度探测拒绝 `<I`，必要关闭成功后才进入独占 RBF 工厂。非 None 恢复只允许影响 `>=I`；共同 header 检查随后执行。路径/长度纯前检及内容 Result 拒绝不新增 fault，实际可写工厂/违规恢复报告或 owned 清理失败才停用。
- 创建在 barrier 前捕获实际 slice、确定名称缺失、度量实际追加起点并一次取时/封口。既有桶从查重到 Append 保留同一 Idle 句柄；缺桶在 data-first 后建目录，初始化/检查/flush/关闭唯一 pending，再不覆盖发布合法空桶、正式重开及检查后追加 TagRecord。
- 空桶 rename 不推进 tag outcome；正式 Append 前 Unknown，成功 flush 后立即 Confirmed，再关闭与交付。RBF pre-I/O 失败 Result 恢复 NotAttempted；空桶仍保留。停用先使实例失效，清空并单次关闭桶/其余 owned 资源，最后释放锁；不 Dispose 借入 data。
- named 操作使用独立短期桶槽，不增长 ref 缓存或覆盖 history 临时槽。历史 callback 可 ResolveTag，CreateTag 仍在 I/O 前被 HistoryActive 拒绝。关联注记、TagInfo/ReadTagInfo/ScanTags 及它们的扫描槽/回调资格留 V3-C。

本轮源码验收与受控故障证据见下方记录；公开冷重开不等于新 OS 进程启动，受控截尾不等于真实进程终止或断电。未发现需要重开产品设计的问题。

## V3-C：tag 关联注记与元信息公面（2026-10-11）

本轮从 `31f7edd` 的干净树开始，按生产接线、公开行为验收、扫描生命周期与独立复核分包。接通 `CreateTag(name, payload, relatedRevision, out outcome)`、自有 `TagInfo`、`ReadTagInfo` 与 `ScanTags/TagScanEnd`；沿用09的持久 schema，不修改 RBF/FrameStore 底座或 pack 清单。

- 关联入口在 I/O 前拒绝 default、异库与非法 ticket，随后复用匿名 tag 的私有捕获、准备、data-first 与三态发布。记录实际传入 bytes 和关联坐标，不读取、比较或确认来源，也不签发来源 revision。
- `ReadTagInfo` 复用 named 唯一性与 unknownName 裁决，只读取独立 checked meta。`ScanTags` 流式遍历实际已建桶，严格检查每条 meta，重复记录逐条交付，不读 root 或建立全量名称表；所有交付值自有。完整 CRC、名称全局唯一及来源一致性仍属独立完整校验。
- `TagScanActive` 拒绝全部 mutation、递归 tag 扫描及进入历史；允许日常读取、named tag 查询和声明图查询。历史 callback 可以调用 tag 扫描，两个临时文件槽各自独立；同桶 named 查询完成通常的全部路径资格后借用扫描 checked 句柄，其他桶使用独立短期句柄。
- 扫描每次 callback 前已归还 pooled meta，callback 后先复检 owner/取消，再解释 false；false 不推进后续帧/桶。正常、早停、取消与异常路径统一关闭必要资源，清空槽后单次关闭，保持主异常优先。纯读/visitor/取消错误不新增 writer fault，实际 owned 恢复或清理失败按既有合同停用。
- 本轮明确让 owner 持有目录 cursor，避免 callback Dispose 或嵌套 named 查询停用时，根锁已经释放而目录枚举器仍活跃。cursor、当前桶、history、named 与缓存句柄均先于根锁释放。

独立复核修正了同桶借句柄路径的一处遗漏：借柄不能豁免 named 查询对全部目录直接项的名称/类型/no-follow资格。实际验证及证据见下方 V3-C 记录。该轮没有需要用户裁决的产品设计问题；下一片 V4-A 已在下文接续。

## V4-A：独立只读完整校验（2026-10-11）

本轮从 `752ab15` 的干净树开始，按生产实现、公开反例、资源失败验收与独立审查分包。新增 `Audit(CancellationToken)` 和只含五个 long 计数的自有 `VersionStoreAudit`；最小公面与范围已落入 S4 `[A-VS-AUDIT]`。不修改持久格式、RBF/FrameStore 底座或 pack 清单。

- 仅允许只读 owner，writer 在 I/O 前返回 ReadOnlyRequired，history/tag 扫描 callback 内拒绝。所有临时文件均只读打开，不走可写惰性恢复或把全库句柄加入缓存。
- 收集正式 ref header 声明并检查缺源/环，再从真实主链正扫全部 Snapshot，校验初始化、完整 CRC/envelope 与 EOF。ForkOrigin 的完整 ticket 必须实际命中主链，然后逐 byte 比较子初始；不会把随机读合格的内嵌伪帧当成历史。
- 全部 tag 桶检查完整 root 和独立 meta、名称路由及 Ordinal 重复。只保留当前桶名称集合；关联注记不触发源存在性/bytes 比较。opaque payload 与 data 业务闭包仍交应用。
- 首次失败停止；只在所有必要 EOF 和清理成功后交付计数。临时句柄和完整帧数量为常数，图/请求位置 O(ref 数)，名称空间 O(最大单桶名称量)。纯读/取消不新增 fault，owned 清理失败停用并锁最后；这不是系统规模或原生故障证据。

实际构建、测试与独立审查记录见下方 V4-A 验证记录。下一片转入 V4-B 系统组合和真实进程验收，不再扩展完整检查为修复框架或报告系统。

## V4-B：公开组合消费者与真实进程边界（2026-10-11）

本轮从 `4fb6008` 的干净树开始，新增独立、非打包的 [VersionStore.Smoke](https://github.com/Atelia-org/atelia-storage/blob/3de8fb050d81d35ee1c36d1b978024cbc42cb455/tools/VersionStore.Smoke/README.md) 与 SystemIntegration 父进程测试。消费者仅直接 ProjectReference VersionStore，使用 public API；不依赖测试程序集的 IVT、反射或生产故障接缝。没有修改生产协议、存储格式或 pack 清单。

- 多根/循环图：两类应用记录、四个 RootMap 根、三种追加、提前地址/交错完成及软轮转；真实新进程遍历 A↔B/self、历史选点、两种 fork/fork-of-fork、rewind、匿名/关联 tag 与元信息查询，分别执行应用引用闭包和两层 Audit。
- 跨进程锁：分别验证全栈和 VersionStore 独立 reader/writer 矩阵；后者仅在所有 seed owner 关闭后复制 data 夹具，保持 StoreId、隔离 data 锁，断言已成功打开 data 后才被 versionstore.lock 拒绝。这是测试隔离，不新增生产克隆能力。
- 六个公开停点：未发布的完整 data、已确认 A 且无关 B 仍活跃、普通 ref / 指定 CreateRef / 精确 fork / 关联 tag 的 Confirmed 返回后。父进程核对 READY/PID 后强杀自己启动且仍持有 owner 的进程，再以新进程真实 RW→RO 工厂检查已发布状态和完整 data 闭包；还验证正常 Dispose 与强杀后释放锁。
- 公开停点只证明 ProcessCrashOnly；没有截获内部 pending、rename、Append 或 flush 中途。不把恢复报告数量当成实际修尾，也不要求未发布 data 必须消失。S6 的应用 tool-loop/generation 场景、WSL2、有限资源测量和最终条款复核仍列入后续。

实际命令、平台、运行结果与审查记录见下方 V4-B 验证记录。

## V4-C：Linux 与有限资源取证（2026-10-11）

本轮沿用 `1176bd2` 的生产源码，只增加独立 public consumer 的 resource-write/resource-read 和两档跨进程只读不变测试。支持16/64/256个 ref、长来源后缀下的固定继承历史、同桶 tag 聚集、发现/历史/元信息/完整校验重复调用，以及 fresh owner 的 ReadRef 缓存对照。RW重开、连续tag创建和RO查询分别记录实际计时/分配，资源样本与性能承诺分开。

WSL2 使用固定源码归档及最终三个源码增量，在独立 ext4 的 repo/bin/obj/TMPDIR 中执行；新栈 Linux 专属路径与真实进程组合已取得实测。冻结旧栈 EventJournal 的大规模 fsync 回归单独放在 tmpfs 完成，原整套 ext4 命令明确保留 Interrupted，不能宣称单次完整 ext4 通过。Windows 完整回归与最终 Linux 增量各自记录。

具体平台、命令、结果、测量限制与成本观察集中在 [V4-C验收记录](12-versionstore-linux-resource-acceptance.md)，原始工件摘要见其证据链接。本轮不改变生产格式/API，不开启包注册或业务仓迁移；S4/S5/S6仍待最终条款复核。

## 后续顺序与验收出口

| 分片 | 可审查交付物 | 必须保留的边界与验收 |
| --- | --- | --- |
| V1：已接通普通 ref 公开闭环 | Create/Open/OpenReadOnly、自动/指定 CreateRef、ReadRef/PublishRef/ListRefs；正式目录 backend 接入现有内核 | public `Create → CreateRef → PublishRef → ReadRef → Dispose → Open/RO → ReadRef`；借入/模式在 VS I/O 前检查；门 close 后才发现/清 pending；可写 H 先完整发现，RO 不为发号全扫；唯一 pending 只取消不采用；原生同 FS no-overwrite rename；坏门/别名/特殊文件/错 data/失败清理与锁最后 |
| V2-A：已接通有界历史读取 | RBF 指定边界逆扫公共重载；ReadRefHistory、HistoryBudget、HistoryEnd | 固定上界、实际工作计费、早停/回调 Dispose、临时句柄复用/单次清理；受控 ForkOrigin 继承读取与 exact bytes 检查，不代表 fork 创建协议已实施 |
| V2-B：已接通精确 fork 创建 | 自动/预留号两种 ForkRef；复用现有新 ref 单文件发布路径 | 已签发旧 revision exact 定位/尾 Fence、捕获源 bytes；所有确定拒绝先于 barrier，data 后源 flush，子重新取时并 exact clone；各失败窗口的 Outcome 与停用；public fork-of-fork/历史冷重开闭环 |
| V2-C：已接通声明分叉图 | ListForks、ForkInfo 与全正式 ref header 扫描 | 仅读 header、单数量预算、缺源/自链接/迭代环检查、自有完整边集合；历史 callback 内同文件复用且不覆盖历史临时槽；不逐边完整审计源 ticket |
| V3-A：已接通 tag 记录与扫描内核 | 09 的名称/容量/1024桶路由、BucketHeader/TagRecord codec、必要 header、独立 meta CRC 与请求范围扫描裁决；11 的自有 RootPayload 值 | 原 UTF-16/Ordinal 与宽容 BPV，尺寸前检；元信息资格与 root 完整CRC分离，可信非目标不读 root；unknownName、目标重复/损坏与结构错误分离；真实 RBF 文件验收，内部能力不冒称公开 tag 闭环 |
| V3-B：已接通匿名 tag 公开持久化闭环 | 可选 tags 目录及目标桶 backend、空桶发布、匿名 CreateTag/ResolveTag、公开冷重开 | 正式桶长度 I 前检保护初始化；空桶 rename 不推进 tag outcome；同柄元信息查重到 Append；同一 schema 写匿名 id0；借入/history/清理/Outcome 与 bytes 结果互操作 |
| V3-C：已接通 tag 关联注记与元信息公面 | Related CreateTag overload；09 的自有 TagInfo、ReadTagInfo、ScanTags/TagScanEnd 与同步 callback | Related 不触发源重读/flush或签发revision；元信息成功不证明 root 健康；逐全已建桶、无 fullname 集合或整体重复证明；Complete/VisitorStopped、坏meta失败、取消/异常/Dispose/关闭及 TagScanActive 互操作；复用 V3-B 发布与 named 读取内核 |
| V4-A：已接通独立完整校验 | Audit/VersionStoreAudit；只读全 ref/tag 与 fork 一致性 | 完整帧、真实主链 ticket、名称重复、失败/取消/清理及无恢复；不审 data 或 pending 内容 |
| V4-B：公开组合与公开停点进程验收 | 独立 public 消费者、跨进程两层锁、六个明确停点的强杀/冷开 | 只证明已执行的 Windows public 停点；内部写入窗口、应用策略和两平台资格分别取证 |
| V4-C：Linux 与有限资源取证 | 独立 ext4 快照的新栈回归/进程验证、16/64/256有限资源消费者、Windows完整回归 | ext4与旧栈tmpfs分组回归分别记录；临时遍历与owner缓存分开，资源/成本不推广为任意负载承诺 |
| V4-D：已接通应用策略最小示例 | S6 tool-loop Started/结果/uncertain 与 generation 串行合并 | public consumer 在三个应用边界真实kill后冷恢复，外部receipt严格匹配、Result不依赖backend；单driver拒绝错operation/旧generation/重复结果，保留同代A/B合并 |
| V4-E：已完成独立源码验收与反例修正 | S4/S5及08–11的已实施源码条款映射，ListRefs只读结果修复及坏data应用反例 | [独立验收记录](12-versionstore-final-acceptance.md)统一记录Accepted裁决、当前回归与历史证据差异；不把源码Accepted扩为S6全部完成 |
| V4-F：已完成内部进程窗口与剩余组合补证 | 8个发布停点、4个真实物理残尾停点、VS入口冲突拒绝、独立目录登记窗口、tag golden冷进程 | Windows完整回归、WSL2/ext4的RBF/VS完整回归及独立复核通过；source-only队列收束，包交付另立范围 |
| S6-C/D：已完成本地候选交付 | 六包 All、四个精确 PackageReference consumer、脚本/来源/闭包验证 | Windows完整源码回归和Windows/WSL2同候选隔离消费通过；保留 Atelia.Rbf 名称；实际版本/来源与反馈续接见[交付记录](../FrameStore-HeadStore/13-local-package-delivery.md) |

V1 至 V4-D 已完成相应源码、公开组合、两平台有限资源与应用策略。V4-E 按实际源码/测试和证据独立复核，发现并修正 ListRefs 的 `ICollection.SyncRoot` 可变 backing 逃逸，补充健康发布根指向损坏 data 时的 public 冷读反例。V4-F 接续具名内部窗口及剩余组合，实际范围和运行见下方记录；不重做已经闭合的源码实施片。

V4-F 的精确范围和冷开判据集中在[验收记录的组合矩阵](12-versionstore-final-acceptance.md#s6-组合资格与后续最小分片)。内部宿主复用测试程序集的 IVT，public consumer 仍无 internal 访问。真实物理残尾由 RBF 实际短写后停住再 kill 形成，没有事后截尾；恢复报告由 VS 正常 lazy open 的内部观察入口取得。同步异常/out 的局部证据继续消费已有测试，不要求进程死后恢复旧 outcome。包注册/隔离包消费仍另立交付片。

V3 续接只需保持现有窄分层：`Internal/Tags` 独立管理 codec/checked header/named 名称裁决；匿名与关联写入共用发布路径。元信息读取不需要完整 root 资格，ScanTags 严格传播记录错误；named 路径的 unknownName 隔离不能搬到完整校验。`_tagOperationFile` 为 named 查询短期 owned 槽；扫描桶、checked header 与目录 cursor 另持独立状态，以便同桶 callback 借用、跨桶查询独立关闭及 callback Dispose 时锁最后。不要以日常 ScanTags 成功代替完整校验。

V4-C 的有限观察未提出新的性能SLO或缓存机制。后续不重复这批向量来凑轮次；若实际应用暴露容量/延迟要求，再据具体负载立项。后续只按具体反例或新的交付目标开启工作，不无限扩展中断位置的排列组合。

V2 的底座增量已由 [RBF 边界逆扫记录](../Rbf/rbf-reverse-boundary-plan.md)接入公共接口合同。普通当前值与普通 PublishRef 不依赖它，V1 证据保持原身份。ForkOrigin 元数据可在 V0 编解码，V2-A 读取既有声明并检验 exact 源/bytes；V2-B 已用正式源读取/确认与子发布建立公开 fork 创建闭环。

2026-10-11 用户已正式授权 CU 与附属读取重构，S0/S4/S5/07/11 已同步；本轮 CU 是独立源码范围，见下节。普通 Snapshot 不增加 Members，CU 不覆盖 ref 创建/tag/跨库事务。包注册、隔离 PackageReference smoke、发布及真实业务仓切换按 S6 另立明确交付片，旧六包候选保持不变，本轮不自动重新打包或发布。

## CU：本轮跨 ref 条件发布与读取整合（2026-10-11）

状态：**Accepted（CU 增量独立源码资格；Windows / WSL2 Linux）**。前置只读审查认定当前 public RBF/FrameStore API 足够，不需要偏离07或新增底层入口。当前完成项、源码/构建身份及真实运行结果集中在[07 CU 独立验收](07-versionstore-cross-ref-conditional-update-acceptance.md)；已按源码反例复核与两平台运行结果裁决为 Accepted。

本轮交付 `PublishRefs(IReadOnlyDictionary<RefId, ReadOnlyMemory<byte>>, out PublicationOutcome)`，返回自有只读 revision 字典；捕获实际枚举，不信 Count/comparer，empty 拒绝、single 普通、至少两个同库既有 ref 使用 CU。CU tag=2、count4、递增非零 LocalRefId4+Packed8、root1..1MiB、meta8且整批一次取时；容量消费公共 RBF/SizedPtr，不另设成员数上限，成员 ticket 按 actual count 的 Measure 区间校验。格式门仍为1，旧 Snapshot/header 编码保持。

当前值、历史、exact fork 来源与 Audit 使用同一共同判据。只有明确未提交见证允许跳过本地 CU，必要错误/取消/预算不足不能伪装旧值；exact source 未提交拒绝。异长消歧只核对实际主链前驱/后继结构，不审计 replacement payload，不递归替代组。历史 peer 访问/扫描/完整读/结构后继动作前先扣 work，peer 不加入继承 visited；短期 peer 槽进入 owner 清理，额外 handles 为 O(1)。CU 来源子文件仅 clone root bytes 为普通初始 Snapshot，data 后只确认 exact source 文件；Audit 完整物理检查 CU，SnapshotCount 排除未提交 CU。普通 PublishRef 不强制验证被覆盖旧组的 peer，lazy CompletedTail 解释保留。

本轮已分别验收公开闭环/冷重开、canonical/容量与身份反例、共同判据/复用/错误传播、history/fork/Audit 范围、输入及结果所有权、单调 Outcome/停用/单次清理、真实进程终止与 Windows/WSL2；历史实验和前轮测试数字不作为本轮通过结果。包资格未开启：后续若交付 CU，使用新版本、新来源 manifest 与四个隔离 PackageReference smoke，不覆盖已有候选。

## 后续自动队列与 Linux 验证入口

2026-10-11 用户明确授权本机 WSL2 验证。V4-C 已实际使用 Ubuntu 24.04.4 / kernel 6.18.33.2-microsoft-standard-WSL2 / SDK **10.0.201**，源码、构建与新栈测试位于 `/var/tmp` 的 ext4，执行用户为 root。此前仅环境探测的状态已被本轮实际证据接续，历史Windows跳过项仍保留其原记录。

后续在 WSL 的 Linux 文件系统上建立独立、对应明确源码 revision 的检出或源码副本，并在其中还原、Release 构建和测试；bin/obj、日志及测试临时目录与 Windows 隔离。Linux 文件语义验收的测试文件放在 ext4 等 Linux 文件系统，不以 `/mnt/e` 上的 Windows 挂载目录代替；如专测挂载边界，分别记录两侧实际文件系统。保留发行版、内核、SDK、源码身份、实际测试路径/文件系统、TRX与进程实验结果，区分 WSL2/Linux 执行与 Windows 原生执行；不将 WSL2 结果外推为全部 Linux 部署环境、断电或物理设备故障资格。

后续Linux功能回归沿用分组执行：新栈及本轮其他完成组保留ext4；冻结EventJournal使用单独tmpfs TMPDIR运行原始完整用例，保存各组TRX及实际文件系统。若要取得旧栈ext4大量fsync的资格，再单独安排有明确目标的运行。Windows仍使用完整solution回归入口。

**V4-F 已完成，本 source-only 队列到此收束。** 具名窗口和剩余组合的两平台验收见下方；不再自动安排V4-G或重新展开设计问题。2026-10-11 用户已明确转入 S6-C/D 包注册及隔离 PackageReference 消费，并选择保留 Atelia.Rbf 名称，先供下游本地试用。当前分片和实际结果见[本地交付记录](../FrameStore-HeadStore/13-local-package-delivery.md)。后续先收集下游反馈，按具体问题调整 API 并生成新候选；公开发布与业务仓迁移保持独立；本轮已授权 CU 源码范围另见上节，不自动改变旧包候选。

## 每轮结束时的交接要求

更新本轮完成项、实际验证命令/结果、尚未取得的资格，以及下一轮最小闭环。后续实现如果改变 internal 接缝，同步修改调用方和有意义的测试；公开行为或持久格式的改变回写所属权威，不只改本文。

当前未发现需要改变产品设计的阻碍。RBF边界逆扫、历史跨文件资源/预算和独立完整校验均已接通；V4-F收束具名系统组合。历史自身不会把每个访问文件加入长期缓存；既有`ReadRef/PublishRef/ForkRef`的owner缓存仍保留，不能把局部资源验证视为大规模负载资格。不把已定机制的接线重新扩展为设计待定项。

## V0 历史验证记录（2026-10-10）

V0 已完成源码集成、主线程验证和独立反例复核，无未解决的 P1/P2。命令、源码输入摘要、TRX 与日志摘要见 [V0 证据](evidence/2026-10-10-versionstore-v0.json)。

- `dotnet build Atelia.Storage.slnx -c Release --nologo -v minimal`：最终增量构建 0 error、0 warning；此前底座构建出现的 41 项既有 XML 文档 warning 未作范围外修订。
- `dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/versionstore-v0/tests`：2410 通过、0 失败、3 项 Linux 专属测试在 Windows 跳过。其中 VersionStore 202 项（Format 79、Values 10、Records 40、RootMap 43、Runtime 30），FrameStore 422 项通过（包含新 guard 的 5 项）。
- `eng/Test-Rbf1ReferenceAssets.ps1`：7 个冻结参考项目保持精确 RBF1 包。VersionStore assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部 codec 依赖为 K4os.Compression.LZ4 1.3.8。

复核中已补齐路径资格检查的 owned 临时资源清理失败标记，并让 owner teardown 直接枚举句柄表、在 finally 中最后释放锁，避免清理路径上的额外集合分配。真实 RBF 内容损坏由记录层测试验证；运行层故障注入使用实际经过的扫描入口，不把未被调用的装饰器读方法计作覆盖。

当前 PublishRef 会读取并复制旧 head 的 payload，以保守确认其完整性。后续可在保持坏 head 裁决一致的前提下避免旧 payload 的结果复制；这是局部优化，不阻塞 V1。当前没有发现需调整持久格式或发布协议的反例。

以上是 V0 当时的源码及内部接线资格，未涵盖真实 VS 根/公开工厂；该部分的后续证据见 V1。进程终止、Linux 平台与包消费仍未取得资格，FrameStore 原有验收不能代替它们。


## V1 验证记录（2026-10-11）

主线程最终 Release 构建 0 error、0 warning；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **2618 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore 为 **410 通过、6 跳过**，包含 V0 的 202 项及本轮新增 214 项（208 通过、6 跳过）。独立源码及关键断言复核无未解决 P1/P2。精确命令、最终 TRX、日志摘要、源码摘要和依赖闭包见 [V1 证据](evidence/2026-10-11-versionstore-v1.json)。

公开生命周期与目录工厂测试在 W: 使用真实 FrameStore/RBF/VersionStore；覆盖两层冷重开、模式/持续借入、active Builder、锁矩阵、全部短门前缀与字段错误、创建中断镜像、门 close 先于发现/取消、惰性恢复、pending 只取消不采用、H 耗尽/低号仍可建、必要读不预审初始、坏 head 不回退及自有结果。Runtime/工厂窄注入另覆盖 barrier 计数与创建各相位异常、确认后交付失败、主错优先和锁最后单次清理。

`eng/Test-Rbf1ReferenceAssets.ps1` 验证 7 个冻结参考项目仍使用精确 RBF1 包；VS 的 FrameStore/Binary/Rbf/Data/Primitives 都是 main project，外部依赖为 K4os.Compression.LZ4 1.3.8。没有打包、发布、push 或消费者迁移。

Windows 测试和独立复核支持 V1 普通 ref 公开闭环；真实进程终止、跨进程锁、跨 volume/mount、Linux 执行与系统完整验收仍留 V4。当时的下一片 V2-A 已在本轮接续，当前顺序以上表为准。

## V2-A 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **2753 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **490 通过、6 跳过**，本轮新增 History 50 项及 HistoryFaults 30 项；RBF **920 通过**，本轮新增边界逆扫 55 项。独立源码/反例复核无未解决 P1/P2。精确命令、最终 9 份 TRX、日志摘要、基线及本轮源码变更摘要见 [V2-A 证据](evidence/2026-10-11-versionstore-v2a.json)。

公开历史测试在 W: 使用真实 FrameStore/RBF/VersionStore，覆盖两层冷重开、重复同值/rewind、原时间顺序、必要坏节点、初始延后检查、固定 head、来源后缀排除、缺源/环/byte clone 不符、准确预算、回调早停/取消/Dispose 与资源复用。故障测试通过真实 RBF 读取日志确认 source helper 内部完整读次数，并分别验证普通错误与 owned 清理失败的停用差异、主异常优先和锁最后。

首轮完整测试中的一项 Fence 构造提前触发 factory 拒绝；现已拆为两个实际路径：旧 anchor 紧邻末 head 时，坏共享 Fence 在冷开抛异常；存在健康间隔帧时，先证明 factory/head 合格，再由历史 anchor 校验返回失败。最终完整测试使用修正后的 50 项 History 用例。生产合同未为通过测试而放宽。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认 7 个冻结参考项目仍消费精确 RBF1 包；VS assets 中五个 Atelia 底座依赖均为 main project，外部 codec 仍为 K4os.Compression.LZ4 1.3.8。新增 IRbfFile 成员只对本仓新栈测试代理补转发，旧栈包及 pack 清单未改动。早期构建中的既有 Data/Rbf XML warning 未扩大修订，最终增量构建为零 warning。

V2-A 当轮未实施 ForkRef/ListForks/tag/CU、未打包/发布/push。继承来源为受控文件、关闭失败为窄注入，不据此宣称公开 fork 创建、原生故障、真实进程终止、Linux、规模或包消费资格。当时没有产品决策阻塞；两种精确 ForkRef 后续已在 V2-B 接通。


## V2-B 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **2843 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **580 通过、6 跳过**，本轮新增 Forks 37 项与 ForkFaults 53 项。独立源码及反例复核无未解决 P1/P2。精确命令、最终 9 份 TRX、日志摘要、基线与本轮 9 个源码/测试文件摘要见 [V2-B 证据](evidence/2026-10-11-versionstore-v2b.json)。

公开测试在 W: 使用真实 FrameStore/VersionStore/RBF，覆盖两种编号、公开旧点/历史选点、fork-of-fork、源后续追加排除、两层冷重开、自有 bytes、1..1MiB opaque payload、重新取时、源文件逐 byte 不变、HistoryActive/只读/default/异库/Dispose 与活跃 data Builder。受控内容反例另验证坏初始/坏后来 head 不阻断健康 exact 来源，必要 CRC/envelope/尺寸/尾 Fence 必须拒绝；完整 EOF 的 factory 拒绝与旧 anchor 的 boundary 拒绝分别证明。复核后补充了已签发 revision 超出截断后完成范围的拒绝与健康前点仍能 fork；该截断仅为受控损坏镜像，正常协议仍禁止删除已发布位置。

故障测试验证先 data 后 exact 源确认、只刷直接源不刷祖先、私有阶段/rename/确认后异常的 Outcome、主错误优先、单次清理及锁最后。真实 RBF 读取日志证明 exact 源两次完整读取，源后缀仅由测试显式 ReadRef 访问；不以代理调用计数替代底层读证据。

首轮完整测试中两个同源用例在辅助转储步骤触发 Windows 共享冲突：writer 使用 FileShare.None，不能在其存活期间另开读取句柄。已删除该用例重复的转储断言，保留真实读取日志与发布顺序；源文件不变由关闭 owner 前后的公开逐 byte 比较验证。生产共享模式及协议未改动，修正后重新构建并运行完整测试。最初底座构建中的既有 XML warning 未扩大修订。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认 7 个冻结参考项目仍消费精确 RBF1 包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部 codec 仍为 K4os.Compression.LZ4 1.3.8。未打包、发布、push 或迁移消费者；Windows 源码测试、内部故障注入不能代替 Linux、真实进程终止、原生设备故障、规模或包消费资格。当时没有产品决策阻塞；V2-C 后续已接通，当前顺序见上表。


## V2-C 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **2920 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **657 通过、6 跳过**，本轮新增 ForkQueries 35 项与 ForkQueryFaults 42 项。独立源码及反例复核无未解决 P1/P2。精确命令、最终 9 份 TRX、日志摘要、基线与本轮 15 个源码/测试文件摘要见 [V2-C 证据](evidence/2026-10-11-versionstore-v2c.json)。

公开测试在 W: 使用真实 FrameStore/VersionStore/RBF，覆盖双编号域、多根、多 fork 点兄弟与多层来源、逆向编号、冷只读重开、历史 callback 内查询、结果自有且无可变集合接口、空集合/恰额/超额（含无来源根）及 pending 排除。受控镜像另验证 header-only 声明、坏初始/head 内容仍可查、必要 header 拒绝、缺源/自链接/独立分量环，以及声明图不预审源 ticket/clone 内容。1024 节点深链与 384 祖先加 192 分支是有限真实文件正确性向量；线性迭代结论结合源码复核，不作为 CPU/RSS/负载资格。

故障测试使用生产 DirectoryVersionStoreFiles 的真实逐项枚举接缝及真实 RBF，注入枚举/打开/读取/关闭窗口；验证超额节点不打开、未知名称/类型不能被预算掩盖、恰额仍证明 EOF、普通错误不新增 fault、owned 恢复/清理失败停用且锁最后。预算失败 Result 叠加枚举器关闭失败明确抛关闭异常。真实读取日志证明本层完整读取 header、不读取 Snapshot；历史当前槽只借用，查询最多另有一个临时 ref，嵌套查询取消后历史可继续，清理失败则两个槽均只关闭一次。

测试设计阶段发现并补上取消检查间隙：取得首帧扫描器后、实际推进和完整 header 读取前，复用 CheckHeader 的 beforeWork 接缝检查状态。对应测试通过真实 RBF 日志证明该点取消后没有读取请求。最终测试前修正两项新 xUnit 断言风格 warning；早期底座 XML warning 未扩大修订。最终完整运行无失败。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认 7 个冻结参考项目仍消费精确 RBF1 包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部 codec 仍为 K4os.Compression.LZ4 1.3.8。未打包、发布、push 或迁移消费者。内部故障、受控文件镜像与 Windows 源码测试不替代 Linux、真实进程终止、原生设备故障、规模或包消费资格；声明图成功不证明初始化、全部 Snapshot 或 data 图健康。当时下一片为 V3-A；现已接续，当前顺序见上表。


## V3-A 验证记录（2026-10-11）

最终 Release build **0 error、0 warning**；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3030 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **767 通过、6 跳过**，本轮新增 Tags 73 项与 TagFaults 37 项，共110项。独立源码及测试复核无未解决 P1/P2。命令、9份最终TRX、日志摘要、基线及19个源码/测试文件摘要见 [V3-A 证据](evidence/2026-10-11-versionstore-v3a.json)。

W: 真实 RBF 文件验证首 header、冷重开后的单桶内核、自有 opaque bytes、同桶碰撞/同名不同编码/其他名重复、unknownName 在唯一目标前后及零目标不确定。独立 golden 与手工 BPV 向量覆盖全 UTF-16 code units、冗余前缀、实际3104B meta、关联声明值域、CRC 与 exact-consume；真实帧另强制非零 escape key 和5B root，验证非对齐 meta 解 escape 后的独立CRC。容量与实际起点检查只消费公共 RBF MeasureWriteSize/SizedPtr，合法最后帧末端可越过最大起点。

真实 RBF 读日志和现有内部 read/rent 诊断接缝证明：非目标不完整读 root；M=0/超界仍读小 meta 并占名，Resolve 在租完整帧前拒绝；唯一目标完整读取在扫描正常 EOF 后进行。同柄受控截断验证 meta/framing/full-frame short read，诊断注入验证真实读/租用位置的 I/O、EOF异常、OOM及guard失败传播。buffer 内 codec 截短与真实文件短读分别取证。上述测试不等于原生设备故障、实际系统 OOM、owner停用/关闭或公开 tag 生命周期资格。

首轮定向测试有两项夹具失败：未配对 surrogate 经 InlineData 属性传值被替换，并产生重复测试ID；现改为运行时从原 code unit 构造。坏 Fence 镜像仅有一个健康后继时由 factory EOF 的左 Fence 校验先拒；现增加第二个健康后继，确认进入 scanner 的 TerminationError 路径。未放宽生产合同。首次构建另有既有 Data/Rbf XML warning 和两个新增可空性 warning；新增 warning 已按明确成功分支修正，未扩大旧代码清理。修正后最终完整运行无失败或重复测试ID。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认7个冻结参考项目仍消费精确 RBF1包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部codec仍为 K4os.Compression.LZ4 1.3.8。未改RBF/FrameStore/pack清单，未打包、发布、push或迁移消费者。该轮无产品决策阻塞；当时的下一片 V3-B 已由本轮接续，当前顺序见上表。


## V3-B 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；V3-B 定向 **174/174 通过**；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3204 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **941 通过、6 跳过**，本轮新增 TagPublic 99 项与 TagPublicationFaults 75 项。独立源码及反例复核无未解决 P1/P2。命令、9份最终TRX、日志摘要、基线及16个源码/测试文件摘要见 [V3-B 证据](evidence/2026-10-11-versionstore-v3b.json)。

W: 真实公开工厂验证匿名创建/解析、两层冷重开、原 UTF-16 code units 与名称边界、同桶碰撞、1B/1MiB opaque bytes、自有结果、ref/history/活跃 Builder 互操作。全部0..59字节正式桶前缀经历重复可写打开而保持原 bytes，覆盖完整header body缺原Key/Fence；header-only桶合法，header后残尾可恢复，CompletedTail之后仍重验meta。未知路径/错误类型拒绝，非目标坏桶内容不读；目标坏root仍占名、unknownName只影响零命中、不同合法编码的同名重复均有公开路径证据。

生产 backend 的真实 FileStream probe 和公共 RBF 工厂配合窄注入，验证必要probe关闭先于独占打开、长度前检失败不fault、真实可写工厂失败停用、违规恢复报告关闭返回文件后再释放锁。正式已有桶的真实读日志验证同柄小meta查重到Append，且不完整读其他root。发布窗口覆盖barrier、目录建立、private header/flush/close、空桶rename、正式reopen、Append/flush/关闭及确认后交付；NotAttempted/Unknown/Confirmed不混淆。查询普通错误与owned清理失败分别验证，主异常优先、history独立槽和锁最后均有单次关闭断言。模拟报告、读/租用异常与记录型锁不冒称原生故障或跨进程锁资格。

集成编译修正了 nullable target 包装违反 AteliaResult 的 notnull 约束，改为直接 nullable target 表示健康缺失；测试修正正扫sequence/enumerator使用、params单项类型推断与局部变量重名。独立复核另把 PrepareNewTagBucket 的普通前检异常从初始化停用矩阵移入前检用例，保持原生产fault语义。首次构建出现既有XML文档warning，未作范围外修订；最终编译及首次运行的174项定向、完整测试均无失败。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认7个冻结参考项目仍消费精确RBF1包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部codec仍为 K4os.Compression.LZ4 1.3.8。未修改底座/pack清单，未打包、发布、push或迁移消费者。Windows源码验收及同进程冷重开不替代Linux、真实进程终止、规模/RSS或包消费资格；S4/S5/S6仍为Draft。该轮无产品决策阻塞；当时的下一片 V3-C 已由下述记录接续。


## V3-C 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；新增定向测试 **114/114 通过**（TagMetadataPublic 66、TagScanLifecycle 48）；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3318 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore **1055 通过、6 跳过**。独立源码及反例复核无未解决 P1/P2。命令、最终9份TRX、日志摘要、基线及18个源码/测试文件摘要见 [V3-C 证据](evidence/2026-10-11-versionstore-v3c.json)。

W: 真实公开工厂覆盖关联实际 slice、输入归池后的冻结值、双 owner 冷重开、匿名/关联同一 create-only 命名空间、自有 TagInfo 与公开形状。已签发坐标对应的源后来缺失或 root 损坏不阻断关联 tag 创建/查询。独立 RBF reader observer 与已建立非空基线的读取日志验证关联操作没有新增源读取或 flush；非法关联与名称在路径/clock/barrier 前拒绝。

元信息查询/扫描测试覆盖坏 root CRC、零/超界 root、未知名称与目标重复、另一合法名称编码、原 UTF-16 code units、零号桶与全部实际桶。真实 RBF 日志及租用诊断证明只读小 meta，不完整读取 root；BCL Shared pool 事件确认同一 buffer 在 callback 前已单次归还。ScanTags 必要坏 meta、未知 kind/tombstone、真实 short read 与正扫 TerminationError 均失败，不将已交付项伪装 Complete。

生产目录 backend 与真实 RBF 配合窄注入，覆盖所有 mutation/递归scan/进入history的拒绝、同桶借柄及全路径资格、跨桶独立关闭、history→scan双槽、false不推进后缀、取消/visitor异常/Dispose/必要关闭优先。callback直接Dispose和捕获跨桶named关闭错误后返回false均验证owned桶/cursor/history/cache单次清理、主异常优先和锁最后。普通读取/取消/visitor异常保持健康writer；实际owned清理失败停用。这些日志与窄注入不等于原生设备故障、真实系统OOM或进程终止。

首次集成构建修正测试四处单参数params的目标类型推断；首次定向110通过、3失败，失败原因分别为零读取日志尚未物化和逻辑/raw观察重复计数，修正测试证据后重跑全部新增项及solution通过。未配对surrogate用ASCII case标签并在测试体构造，避免runner将高/低surrogate替换为相同测试ID；最终114项独立执行，无重复ID。初次构建的既有Data/Rbf XML warning未作范围外清理，新增xUnit断言warning已消除。

`eng/Test-Rbf1ReferenceAssets.ps1` 确认7个冻结参考项目仍消费精确RBF1包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，K4os.Compression.LZ4 为1.3.8。未修改底座或pack清单，未打包、发布、push或迁移消费者。S4/S5/S6仍为Draft；下一片V4-A补独立只读完整校验，随后继续系统组合、真实进程终止、WSL2 Linux与最终独立复核。


## V4-A 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；新增定向 **92/92通过**（AuditPublic 40、AuditFaults 52）。完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3410通过、0失败、9项Linux专属测试跳过**；VersionStore **1147通过、6跳过**。独立源码、反例及异常树审查无未解决P1/P2。命令、最终9份TRX、日志、依赖闭包与12个源码/测试文件摘要见 [V4-A证据](evidence/2026-10-11-versionstore-v4a.json)。

公开工厂与两层冷只读重开验证准确计数、自有结果、不解码opaque/data闭包、Related只作注记。坏初始/中间但健康head、非法envelope、缺初始化、缺源/环、同offset异length、clone内容/长度不符、tag完整root/meta与另一合法wire编码的重名均有反例。内嵌伪帧先证明其随机完整读取和boundary witness成功、容器Snapshot CRC成功、真实正扫仅有外帧，随后Audit拒绝其来源资格。短尾/短header拒绝及任意3B合格pending保留均比较VS/data目录与bytes不变。

真实目录/RBF配合窄observer验证取消、实际完整读/租用异常、普通错误不fault、owned关闭失败停用及锁最后。fork clone窗口同时持两柄时注入child读错误并叠加child/source关闭失败，证明主错优先和单次清理。80ref有限向量验证至多两个新增同时reader、没有全库缓存增长；既有缓存句柄保持独立所有权。这些结果不等于RSS/大规模负载、原生设备故障、实际OOM或进程终止资格。

首次生产编译发现一处把OptionalRbfFrameInfo当nullable进行pattern matching的错误，已改为HasValue/Value。首次定向90通过、2失败，原因是测试BCL AggregateException.Flatten按广度展开而改变嵌套主错观察顺序；已改为沿原异常树深度遍历，与既有测试一致，生产聚合规则未改变。修正后重新build、全部新增项和完整solution均通过。初次底座编译41项既有XML warning未扩大清理，最终增量构建零warning。

`eng/Test-Rbf1ReferenceAssets.ps1`确认7个冻结参考项目的精确RBF1包与来源；VersionStore的五个Atelia依赖均为main project，K4os.Compression.LZ4保持1.3.8。未修改底座或pack清单，未打包/发布/push或迁移消费者。S4/S5/S6仍为Draft；没有产品决策阻塞，下一片V4-B按上文进入公开组合和真实进程边界，随后纳入WSL2 Linux、有限资源证据与最终独立复核。


## V4-B 验证记录（2026-10-11）

主线程最终 Release build **0 error、0 warning**；新增 SystemIntegration **19/19通过**。完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3429通过、0失败、9项Linux专属测试跳过**；VersionStore **1166通过、6跳过**。定向与完整回归各自启动71个子进程、10个真实强杀，其余61个正常退出；两次运行是同一19项向量，不合并宣称38项独立覆盖。Windows 10.0.22000 / .NET SDK 10.0.201，过程夹具位于 `W:/TEMP`（NTFS）。命令、9份完整TRX、日志、逐进程证据摘要及源码身份见 [V4-B证据](evidence/2026-10-11-versionstore-v4b.json)。

公开图得到4 refs、11个本地 Snapshot、3 forks、3 tags、24个data帧；写与两次冷只读验证各在独立进程，RO前后文件集合和SHA-256完全不变。所有数据读取沿真实地址，包含A↔B/self、三种追加、交错完成与多代逆序申请。进程另用非RootMap的opaque应用格式验证确认后的bytes和完整引用闭包。

锁矩阵分别证明FrameStore和VersionStore的reader共享/writer排斥，拒绝具体落在对应lock文件的Windows原生code32；VS隔离先成功打开具有同StoreId的冷镜像data。正常release与实际kill后均可重新取得writer。六个公开停点无正常退场路径，父端核对READY中的PID/Outcome后强杀并等待退出，再启动验证进程；A确认时B的真实writer仍可访问，完成依赖与B的file关系由公开地址wire检查。

本轮所有data恢复报告实际为 `None`，原始/最终长度相同；活跃Builder中的未完成bytes尚在buffer，没有实证物理修尾。未发布的完整data在这两次运行中均留下，ref仍为旧值；这是观察，不成为未确认data必存合同。内部pending/rename/flush中途、OS关闭/设备失败、断电、WSL2和应用tool-loop/generation仍不在本轮实际取证内。

独立源码复核无未解决P1/P2。运行前修正父进程构造失败后的child回收责任、锁异常必须排除Aggregate并按OS精确识别、活跃Builder不能只靠场景标记。构建/定向/完整运行均无失败；首次底座重新构建出现41项既有Data/Rbf XML warning，最终增量构建零warning，未作范围外清理。生产库源码未变。

`eng/Test-Rbf1ReferenceAssets.ps1`确认7个冻结参考项目仍消费精确RBF1包与来源；独立consumer仅直接ProjectReference VersionStore，实际六个Atelia依赖均为main project，唯一外部runtime codec依赖K4os.Compression.LZ4保持1.3.8。没有打包、发布、push或业务仓迁移；S4/S5/S6保持Draft/source-only。没有产品决策阻碍；下一轮V4-C按上文完成WSL2/有限资源，并保持剩余应用场景和最终条款复核可见。


## V4-C 验证记录（2026-10-11）

Windows Release build 0 error/41项既有XML warning；完整solution **3431通过、9项Linux专属跳过、0失败**。固定HEAD的WSL2基线有8组在ext4完成：**3262通过、2项Windows专属跳过**；冻结EventJournal因大量实际fsync改在独立tmpfs完成174项，原整套ext4命令保留Interrupted，不改标为单次完整通过。9项Linux专属均已实际通过。最终三个源码增量经Linux Release build零warning/error与SystemIntegration **21/21** 验证，未重复未改生产库的完整测试。

Windows与最终Linux进程集成各77个child、10次实际强杀；16/64/256资源向量两平台全部成功，每档两个新进程只读验证前后文件清单/hash不变。Linux按路径fd证明临时遍历不累积、ReadRef缓存为N且重复稳定、Dispose归零；Windows总handle变化未归因。单次/有限样本成本及重复目录资格/同桶查重机制单列，不新增SLO或优化任务。完整口径、执行分组、原始工件摘要与局限见 [V4-C记录](12-versionstore-linux-resource-acceptance.md)。

本轮只新增消费者、2项跨进程资源检查及文档；没有生产源码修正或未解决的确定功能反例。保持source-only，下一片V4-D，随后V4-E按条款裁决最终源码资格与具体缺口。


## V4-D 验证记录（2026-10-11）

本轮从 `fb3d126` 的干净树开始，只新增 public consumer 应用示例、7项父进程测试与文档；没有修改生产库、公开API或持久格式。工具说明见 [应用策略示例](../../tools/VersionStore.Smoke/README.md#应用策略示例)，命令、源码身份、两平台TRX及逐进程工件摘要见 [V4-D证据](evidence/2026-10-11-versionstore-v4d.json)。

`ToolLoopScenario` 的四个RootMap根保存runtime/history/tasks/context；Prepared之后正常确认同一冻结请求与身份的Started，才调用模拟backend。父端在Started、external-completed、result-published三个明确应用边界实际kill，再用独立进程恢复。Started无receipt返回uncertain且保持2 snapshots；匹配receipt补Result至3 snapshots；已Result不查询backend，删除receipt后仍可恢复。二次恢复没有重复Started/Result或结算；wrong attempt和wrong request两种receipt均在任何恢复输出前拒绝。父端比较完整文件清单/SHA-256，确认只读及不发布分支没有修改文件。

`GenerationScenario` 在一个driver中消费受控Task完成顺序，每次回包重新读取当前head。九次回包接受三次，错operationId、旧generation、同代重复各拒绝两次；operationId跨generation保持相同，使两个身份判断分别受到验证。拒绝不追加data/ref，下次合法发布才携带已观察的拒绝日志。冷读逐份核对五个完整Snapshot、二十个data帧、冻结请求与结果；第二代先B后A仍保留A=101/B=202，累计应用结算值为314。完整应用图由consumer解码，核心仍只处理opaque bytes。

Windows Release build **0 error、41项既有Data/Rbf XML warning**；新增定向 **7/7通过**。完整solution为 **3438通过、0失败、9项Linux专属跳过**，VersionStore为 **1175通过、6跳过**。完整运行共102次child调用（100个唯一PID，OS复用了两个PID），其中16次真实强杀、84次正常退出、2次预期receipt拒绝；新增应用向量本身为25次child调用、6次强杀、17次正常与2次预期拒绝。定向与完整运行重复同七项，不能合并成十四项独立覆盖。

WSL2 Ubuntu24.04.4 / SDK10.0.201在新的ext4工作区以固定HEAD archive加五份明确源码增量构建，738文件逐一核对，生产库与基线一致。Release build **0 error、41项同源既有warning**；新增应用测试 **7/7通过**，25个child、6次实际kill（exit137）、2次预期拒绝及17次正常退出。未重复V4-C的Linux完整回归或资源向量，前轮证据保留。Windows实际夹具仍在W: NTFS；source-only消费仅直接ProjectReference VersionStore，两个平台实际assets保持main项目闭包，7个冻结参考项目保持精确RBF1包来源。

独立源码复核在执行前修正了tool phase与generation输出大小写两处示例/父断言接缝；最终build、定向及完整测试均无失败。证据汇总修正了Windows PID复用及TRX跳过项的统计方式；这些是观察工具修订，未改变测试或生产合同。没有发现需要修改生产库的功能反例。

外部receipt仅模拟可查询服务；这里的settlement是应用发布状态内的结果计入，不是独立外部支付。应用策略不提供任意backend的exactly-once，不验证多线程writer或通用CAS。实际kill全部发生于公开调用/应用边界，未覆盖内部pending、rename、Append/flush中途、原生设备失败或断电。S4/S5/S6仍保持Draft/source-only；下一片V4-E按上文逐条裁决最终源码资格与必要补证，不自动进入打包、发布、业务迁移或CU。

## V4-E 验证记录（2026-10-11）

本轮基于干净 `9e751d4`，按 ref/历史/fork、tag/Audit、S6过程矩阵和历史工件独立复核，再由主线程集成。发现一个 P2：ListRefs 的 `Array.AsReadOnly` 可经普通 `ICollection.SyncRoot` 取出可变 backing；改为私有只读薄封装，新增公开回归并同步两处旧实现类型断言。另补公开冷读反例：最新健康root所引用data损坏时，data完整读取报CRC错误，VS仍返回最新root且不回退、不改文件。生产改动仅该集合封装；格式/公面签名/底座/pack未变。

Windows 初次build0error/41既有warning，定向6/6；首完整回归唯一失败是遗漏同步的Storage旧IList断言，修正后重新build0error/0warning，最终完整solution **3440 passed / 9 skipped / 0 failed**，VS为1177 passed/6 skipped。WSL2新ext4 workspace采用固定archive加最终五份代码增量（740源文件）；完整Release build0error/41同源既有warning，随后FrameStore/Rbf/VersionStore三个项目全项串行回归 **2526 passed / 2 Windows-only skipped / 0 failed**，分项为424+1、920、1182+1。本轮不是Linux整套solution重跑。

历史核验594个唯一工件，保留V4-C两个派生描述符的hash漂移；直接TRX、源码、process raw与实际assets仍有匹配证据，不修改旧记录或宣称全部旧摘要通过。依赖资产保持main项目/精确RBF1包隔离。详细条款映射、剩余窗口及机器工件见[最终源码验收](12-versionstore-final-acceptance.md)和[本轮证据](evidence/2026-10-11-versionstore-v4e.json)。

**S4/S5及已实施专项的独立source-only源码资格现为Accepted；S6仍Draft。** 下一片V4-F按上表补内部进程窗口和少量尚缺组合；不扩大运行时功能、不重新展开设计问题、不自动开启包交付、业务迁移或CU。

## V4-F 验证记录（2026-10-11）

本轮基于干净`3274558`，分成内部发布窗口、真实物理残尾、public组合、独立复核和WSL验证工作包。生产增量仅为可空内部测试hooks、factory显式测试接线与保持默认语义的OpenRef观察接缝，另给VersionStore.Tests增加RBF测试友元；公开API、持久格式和pack清单不变。测试程序集增加独立可执行入口，现有`tools/VersionStore.Smoke`仍纯public。

- `InternalPublicationProcessTests`的8个停点：自动create/指定旧点fork的private已flush/close与rename后未交付，各2个；private tag header与已公开合法空桶，各1个；正式ref/已有桶完整Append返回但未flush，各1个。真实Kill后分别由fresh RO→RW→RO检查事实，private只取消不续作，正式完整记录不混搭；另1项实际VS发布路径冲突检查Unknown/fault、目标不变、RO保留/RW取消pending和正式高水位。冲突发生在平台目标前检，不称原生syscall竞争实测。
- `InternalTailProcessTests`的4个停点：ref后续Snapshot和tag首TagRecord各覆盖部分body及完整body缺TailKey+Fence。停点在真实短写后的AfterWrite，parent不改镜像；RO拒绝且全树不变，VS实际lazy RW报告分别为Truncated/CompletedTail，再两次fresh RO/Audit稳定。实际ref长度159→148 / 244→252，tag长度71→60 / 180→188；CompletedTail tag实际重读32B必要meta，完整root随后另验。本片尾向量的data RecoveryReports为空，未触发data恢复；不能把这四个VS文件窗口称为新data修尾资格。
- `PublicCombinationProcessTests`的2项：独立目录ref尚未登记已确认动态ref时强杀，冷开仍按ListRefs和应用身份发现；A、Release/release、孤立U+D800、同桶tag-48/tag-65六个固定向量在新进程验证五个路径、Ordinal、完整payload/meta/Related、ScanTags及公开RBF格式。顺序子进程允许OS复用PID，每条消息只核对当前Process.Id。

Windows首轮Release构建0error/42warning（41既有XML warning和1条新增xUnit断言风格warning），定向14/15：唯一失败为测试在RW owner独占ref时使用File.ReadAllBytes。把字节对比移至关闭双方owner后，并修正xUnit断言；重新Release构建0error/0warning，完整solution最终**3455 passed / 0 failed / 9 skipped**，VersionStore为1192 passed / 6 Linux-only skipped，新增15项全通过。完整运行181个子进程：73个Internal+Process含12次强杀，108个public含17次强杀及2次预期失败；本片新增79个子进程、13次强杀。首次失败工件保留，不改标通过。

Linux使用新的WSL2/ext4工作区`/var/tmp/atelia-storage-v4f-3274558.fex9ktjr`，固定`3274558`archive加17份明确源码增量（8个新增文件），750个受测源文件；Windows raw hash与Linux受测输入逐项核对。完整Release构建0error/41既有warning，随后串行RBF完整**920/920**、VersionStore完整**1197 passed / 1 Windows-only skipped**，共**2117 passed / 0 failed / 1 skipped**；不是本轮Linux完整solution test或FrameStore重跑。两平台均复核七个冻结旧栈项目的精确RBF1资产及新栈project闭包；public consumer唯一外部codec保持K4os 1.3.8。Linux的bin/obj、NuGet和TMPDIR全部与Windows隔离并位于ext4，既有工件保持原身份。

两平台精确来源、命令、TRX、进程raw与文件hash统一见[本轮机器证据](evidence/2026-10-11-versionstore-v4f.json)。当前已完成的源码资格与S6组合判据见[最终验收](12-versionstore-final-acceptance.md#s6-组合资格与后续最小分片)。所有.NET重命令串行，未打包、发布、push或切换业务消费者；残尾为Internal+Process有限代表，不外推任意切点、恢复再中断或断电/设备故障。
