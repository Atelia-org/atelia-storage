# VersionStore 分片实施与续接

更新：2026-10-11。本文是实施导航，不增加设计权威。行为以 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[08](08-versionstore-record-metadata.md) 至 [11](11-versionstore-root-payload-bytes-api.md) 为准；[S6](06-integration-and-delivery.md) 管组合与交付。[07 CU](07-versionstore-cross-ref-conditional-update.md) 仍是未并入主线的候选。

## 调度原则与整体结构

每轮先看 git 状态、当前源码及本文完成项，保留其他工作；选择能独立验证的一条路径。实施者可以调整内部表示和助手，不能把缺少实现重新扩展成已关闭的设计问题。若出现合同反例，记录具体失败轨迹与受阻分片，再调整计划。不要用 `NotImplementedException`、伪成功或 public 裸句柄导入填满候选 API。

核心只发布不透明 bytes。应用可显式调用 `RootMapCodec.Encode/Decode`，也可使用 JSON 或自己的格式；普通 ref、fork、历史、tag 和未来 CU 都不能调用 RootMap decoder 来判发布健康。值和结果不依赖 owner：RefId 包含持久 VSID，RefRevision 包含完整 RefId/ticket，RefBytesSnapshot 拥有 GC 数组。字段 codec 只证明格式，不能自行签发来源或主链资格。

建议维持当前窄分层：

| 位置 | 责任及依赖 |
| --- | --- |
| `RefId` / `RefRevision` / `RefBytesSnapshot` / `PublicationOutcome` | 公共值与自有结果；可信内部构造只保存已资格化字段 |
| `Internal/Format` | VSID、44B 门、24/36B ref header、4B local 字段、meta8、规范路径；无文件 I/O |
| `Internal/Tags` | tag 名称/1024桶路由、BucketHeader/变长 meta codec 与借入句柄的单桶扫描；独立元信息资格与完整 root 资格分离 |
| `Internal/Records` | 从已取得的 public IRbfFile 校验本次需要的 header/Snapshot，先元信息尺寸再完整读取、复制结果；不拥有文件，历史 helper 内真实步骤由窄回调计费 |
| `VersionStore` / `Internal/Runtime` | 身份/模式 guard、输入捕获、data-first、PublicationOutcome 与 owned 资源清理；`VersionStore.History.cs` 管固定前缀、预算、回调及一个当前临时句柄；声明图查询另持查询局部临时句柄 |
| `Internal/Storage` / `Internal/Platform` | 根准入/锁/门 I/O/发现/唯一 pending/原生不覆盖发布/按模式打开；不解释应用 bytes |
| `RootMapCodec` | 独立可选应用 codec，使用 Binary 和 FrameAddress 公共 codec；不进入 owner 的调用链 |

VersionStore 与 FrameStore 均保持 source-only，新栈只引用 main 项目。现有 RBF1 冻结参考及 pack 清单不变。FrameStore 的 S2/S3 Accepted 不自动覆盖新的借入 guard 或 VS 实现。

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

## 后续顺序与验收出口

| 分片 | 可审查交付物 | 必须保留的边界与验收 |
| --- | --- | --- |
| V1：已接通普通 ref 公开闭环 | Create/Open/OpenReadOnly、自动/指定 CreateRef、ReadRef/PublishRef/ListRefs；正式目录 backend 接入现有内核 | public `Create → CreateRef → PublishRef → ReadRef → Dispose → Open/RO → ReadRef`；借入/模式在 VS I/O 前检查；门 close 后才发现/清 pending；可写 H 先完整发现，RO 不为发号全扫；唯一 pending 只取消不采用；原生同 FS no-overwrite rename；坏门/别名/特殊文件/错 data/失败清理与锁最后 |
| V2-A：已接通有界历史读取 | RBF 指定边界逆扫公共重载；ReadRefHistory、HistoryBudget、HistoryEnd | 固定上界、实际工作计费、早停/回调 Dispose、临时句柄复用/单次清理；受控 ForkOrigin 继承读取与 exact bytes 检查，不代表 fork 创建协议已实施 |
| V2-B：已接通精确 fork 创建 | 自动/预留号两种 ForkRef；复用现有新 ref 单文件发布路径 | 已签发旧 revision exact 定位/尾 Fence、捕获源 bytes；所有确定拒绝先于 barrier，data 后源 flush，子重新取时并 exact clone；各失败窗口的 Outcome 与停用；public fork-of-fork/历史冷重开闭环 |
| V2-C：已接通声明分叉图 | ListForks、ForkInfo 与全正式 ref header 扫描 | 仅读 header、单数量预算、缺源/自链接/迭代环检查、自有完整边集合；历史 callback 内同文件复用且不覆盖历史临时槽；不逐边完整审计源 ticket |
| V3-A：已接通 tag 记录与扫描内核 | 09 的名称/容量/1024桶路由、BucketHeader/TagRecord codec、必要 header、独立 meta CRC 与请求范围扫描裁决；11 的自有 RootPayload 值 | 原 UTF-16/Ordinal 与宽容 BPV，尺寸前检；元信息资格与 root 完整CRC分离，可信非目标不读 root；unknownName、目标重复/损坏与结构错误分离；真实 RBF 文件验收，内部能力不冒称公开 tag 闭环 |
| V3-B：tag 公开持久化闭环（下一片） | 可选 tags 目录及目标桶 backend、空桶发布、匿名 CreateTag/ResolveTag、公开冷重开 | 正式桶长度 I 前检保护初始化；空桶 rename 不推进 tag outcome；同柄元信息查重到 Append；同一 schema 写匿名 id0；借入/history/清理/Outcome 与 bytes 结果互操作 |
| V3-C：tag 关联注记与元信息公面 | Related CreateTag overload；09 的自有 TagInfo、ReadTagInfo、ScanTags/TagScanEnd 与同步 callback | Related 不触发源重读/flush或签发revision；元信息成功不证明 root 健康；逐全已建桶、无 fullname 集合或整体重复证明；Complete/VisitorStopped、坏meta失败、取消/异常/Dispose/关闭及 TagScanActive 互操作；在 V3-B 后独立实施，不阻断 V3-A/B |
| V4：独立完整校验与系统验收 | 只读完整检查入口、public 消费例子、进程终止/资源与两平台矩阵、S4/S5/S6 条款映射 | 日常必要读取与完整校验分别取证；新旧/循环图、坏初始健康 head、应用解码失败、所有发布与清理窗口；不得将内部模拟、字节镜像或源码测试标成 public factory、真实 kill、断电或包资格 |

V1 至 V2-C 及 V3-A 已完成相应源码接线，后续不重做 ref 工厂、历史、fork、声明图或 tag 字段格式。下一轮优先 V3-B：消费 [09 tag 专项](09-versionstore-immutable-tags.md) 与现有单桶内核，接通真实目录和匿名 CreateTag/ResolveTag 公开发布闭环。拆分依据是 tag 有独立的局部损坏隔离及初始化保护，不能简单套用 ref 当前值检查；这沿用既定 tag 施工顺序，不新增设计问题。

V3-A 已使用 Binary 字符串 plan/reader 与 RollingCrc 默认 Seal/Check，实现09的实际 codeword 接受界。后续直接消费 `Internal/Tags` 的准备、尺寸预检、checked header、named 扫描与唯一目标完整读取，不在公开操作中再实现第二份 codec 或名称裁决。内部单记录元信息读取要求 FrameInfo 来自 checked header 绑定句柄的真实主链；它严格返回坏元信息错误，named 路径才允许精确隔离，未来 ScanTags 直接传播。`RootPayload` 与 `MaxTagNameCodeUnits` 已加入既定公面，尚无不能工作的 tag 操作占位。

V3-B 消费该内核，另实现正式桶 I 长度前检、缺桶 data-first 初始化与空桶 rename、同一正式句柄元信息查重/Append/flush/close、HistoryActive guard 和完整失败证据。接通原匿名 Span 入口，仍按09的完整 schema 写 RelatedLocalRefId=0。验证独立一次取时、匿名不带ticket、同名root坏仍不能创建，以及冷重开元信息/opaque bytes与Outcome。继续区分已取得的局部扫描资格和待验证的公开生命周期；不能将局部扫描测试提前写成公开资格。后续真实文件实验按 S4/S5 使用 W:；Linux 验证另留身份与结果。

V3-C 在 V3-B 后接入 Related CreateTag overload、`TagInfo`、`ReadTagInfo(name)` 与 `ScanTags(visitor, CancellationToken)`；字段、签名及 `TagScanEnd.Complete/VisitorStopped` 唯一见09。Related overload 仅接收同 VS、非default、库已签发的 RefRevision，拒绝先于I/O/barrier，不解释输入bytes、不重读或flush相关ref、不签发新revision；验证Related同有同无、关联点与payload可以不相等、关联源随后不可用不影响tag自身资格。ReadTagInfo 检查请求名重复，只依赖独立checked meta，root损坏时仍可返回info；ScanTags逐全已建桶交付自有info，不读root、不建fullname集合或证明全库唯一，所到坏meta失败而不silent skip后Complete。验证false立即停且不访问后缀、callback已发生副作用不回滚、取消/异常/Dispose/必要关闭失败不能正常终止，已交付info在失败及owner关闭后仍有效。TagScanActive 拒绝全部mutation、递归ScanTags与进入历史，mutation必选out仍首句NotAttempted；允许日常读、ResolveTag、ReadTagInfo及ListForks。历史callback允许ScanTags，使用独立临时桶槽；同桶callback查询复用合格句柄，所有退出清guard并单次清理。时间段/ref反查由visitor按字段筛选，总成本包含全部已建桶名称资格、header及meta，不增加时间/来源索引或查询框架。

V2 的底座增量已由 [RBF 边界逆扫记录](../Rbf/rbf-reverse-boundary-plan.md)接入公共接口合同。普通当前值与普通 PublishRef 不依赖它，V1 证据保持原身份。ForkOrigin 元数据可在 V0 编解码，V2-A 读取既有声明并检验 exact 源/bytes；V2-B 已用正式源读取/确认与子发布建立公开 fork 创建闭环。

CU 在正式选型同步 S0/S4/S5 前不进入上述默认队列；尤其不能提前给普通 Snapshot 分配 Members 或让 API 暗中提供跨 ref 事务。包注册、隔离 PackageReference smoke、发布及真实业务仓切换按 S6 另立明确交付片，不由源码骨架自动开启。

## 每轮结束时的交接要求

更新本轮完成项、实际验证命令/结果、尚未取得的资格，以及下一轮最小闭环。后续实现如果改变 internal 接缝，同步修改调用方和有意义的测试；公开行为或持久格式的改变回写所属权威，不只改本文。

当前未发现需要改变产品设计的阻碍。RBF 边界逆扫和历史跨文件资源/预算已接通；后续重点是既定 tag 协议与系统验收。历史自身不会把每个访问文件加入长期缓存；既有 `ReadRef/PublishRef/ForkRef` 的 owner 缓存仍保留，不能把局部资源验证视为大规模负载资格。不把已定机制的接线重新扩展为设计待定项。

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

`eng/Test-Rbf1ReferenceAssets.ps1` 确认7个冻结参考项目仍消费精确 RBF1包；VS assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部codec仍为 K4os.Compression.LZ4 1.3.8。未改RBF/FrameStore/pack清单，未打包、发布、push或迁移消费者。当前无产品决策阻塞；下一片 V3-B 接正式桶初始化保护和匿名公开发布闭环，V3-C 再接元信息公面与回调生命周期。
