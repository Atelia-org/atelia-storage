# FrameStore / VersionStore 分阶段设计入口

FrameStore 的 R1–R3 收尾已完成，S2/S3 的[独立源码验收](02-framestore-final-acceptance.md)已 Accepted。[收尾计划与自动队列入口](02-framestore-completion-plan.md)现处完成状态，不自动扩展到包交付或 VersionStore。

日期：2026-10-03–10 逐步确认核心/历史合同；2026-10-10 采用无 branch 名称、持久预留低号区、自动/预留区指定创建与单文件发布，并采用固定 4B LE LocalRefId。状态：**S1–S3 Accepted（各自源码资格）；S4–S6 Draft；FrameStore source-only，VersionStore 尚未创建；参考依赖拆分 Accepted**。
初始设计源码观察基线：`main @ 70d1009e78a73342a0c0fdc8ffed7731dec58173`；S1 验收记录的核对基线为 `f6f1eb38557863ba5ea1634844a90f0cbe5774cf`；前轮设计阅读基线为 `4175a46`，本轮租借/目录修订核对 `50e8e28`。本文档集供逐阶段细化、审阅和实施，不把文档修订视为新实现或验收。

目标：**以 RBF3 的帧原子性为基础，让中层构建新状态，再发布完整根地址字典使状态生效。**
MVP 设计组合由 RBF3、FrameStore 核心与 VersionStore 的 ref 发布、历史/fork 和不可变 tag 能力组成；格式、API、实施顺序与验收以 S0–S6 及其阶段内专项为准。
恢复依据是完整事实帧及其有效发布记录。RBF 处理物理尾部；FrameStore 分配不可变帧并按地址读取；中层负责状态构建和业务依赖闭包；VersionStore 管理字典快照、ref 历史/fork 与 tag。

基础编码见已 Accepted 的 [BPV1 规范](../Binary/bare-primitive-value.md)及[实施验收](../Binary/bare-primitive-value-acceptance.md)：独立 `Atelia.Binary` 提供 FixedLE/VarInt、无损自适应 string、宽容 reader 与精确 Measure，当前依赖 BCL 与精确 K4os.Compression.LZ4 `[1.3.8]`。S4 RootMap 已选择复用普通基元；底座资格不替代各阶段的记录 schema、版本、限额与新库验收，FrameAddress 由 S2 同时定义固定 12B 与变长 codec；RootMap 采用变长地址，预留回填继续采用固定地址。[Tagged Value](../Binary/tagged-value-intent.md) 仅记录意向，不是本栈前置。

main 当前演进主线为 RBF3 / FrameStore / VersionStore。旧 EventJournal/RbfSegmentStore、toolkit 及测试保留为冻结参考，底层精确 PackageReference `[0.2.0-rbf1-preview.1]`；维护和公开交付归 RBF1 分支。依赖隔离、solution 共存与本轮仅三包交付的实施及证据见[过渡方案](../rbf1-reference-transition.md)。旧目录继续保留；FrameStore 源码切片不改变 main 的 RBF1 只读兼容或旧栈依赖。

## 当前最小模型

FrameStore 普通合同是不透明地址分配与随机读取，不提供业务全局顺序。保留 RBF 三种追加方式；每个 Builder 独占一个文件，owner 可以嵌套租借多个文件、交错构建并乱序完成，首版调用仍串行。三种追加统一在成功完成后按 TailOffset 大于软阈值触发轮转。
S2 已定软阈值由可写 Create/Open 的一个 long 参数提供，默认 64GiB、实例内固定、不持久化；合法范围消费公共初始化边界与 SizedPtr.MaxOffset，不要求阈值对齐。重开按恢复后 active 的完成 tail 重算：提高阈值可重新使用尚未移档的文件，archive 永远只读。默认仅为工程起点；config 仍只有 Builder 数量属性，实际轮转/恢复及成本见[源码验收](02-framestore-final-acceptance.md)。
文件分配优先选择 active 中当前可分配的数值最低 FileId；忙文件和已停止分配文件跳过，没有候选才新建。不因已知帧尺寸提前试配，也不引入轮询或负载均衡。
FrameAddress 保留固定 12B 编码，并提供变长地址编码；两者均保持完整 uint FileId 与 SizedPtr。额外见证按明确需求引入，不把未来预留或内容 CRC 纳入基础定位合同。值/格式权威为 S2 `[F-FS-FRAME-ADDRESS-12B]`：一个 readonly struct、EncodedSize、精确 12B TryRead 与至少 12B TryWrite，失败 default/无写入、完整等值，数值下界消费公开 RBF 合同；无公开数值字段/构造、额外 codec/error 或规范文本。提前地址预留和互引继续复用固定入口；RootMap 采用新增的 MeasureVarInt/WriteVarInt/ReadVarInt，组合 Binary 的 VarUInt32(FileId) 与 VarUInt64(SizedPtr.Serialize())。两种格式由宿主 schema 明确选择，变长 codec 的实现与资格见[实施记录](02-framestore-varint-address-implementation.md)。地址仍是局部定位值，StoreId 由上下文绑定；普通内部表示及实际运行时成本与持久编码分开，不从 sizeof 推导 wire 容量，该公开地址入口已进入[首个源码切片](02-framestore-core-implementation.md)，公开生命周期与证据见[后续持久化闭环](02-framestore-persistence-implementation.md)。
私有 creating 槽位完成必需首帧 meta/header 后发布到 active。残留消费 S2 `[R-FS-CREATION-PRIVATE]`：完整正式 max 推得唯一下一号，全字节前缀合格才可写取消；只读合格留原样、未知内容保留拒绝。所需 RBF 公共纯前缀入口已实施，实际源码/平台测试见持久化闭环记录，内部探针保留自身历史身份。S2 已定稿固定 24B header，保存统一格式版本与 StoreId/FileId；按首位置识别，完整保留用户 uint tag 范围。可写打开先检查初始化长度下界，再消费公共 RBF 恢复并完整校验首帧。active 目录表达可写集合，flush/close 后移入固定 1024 编号分桶的 archive 并只读，不维护 active manifest 或全历史分段表。屏障确认全部必要 active 输出。
FrameStore 格式门记录/读取已定于 S2：`framestore.format` 是版本 + 16B StoreId + CRC32C 的普通固定 24B 文件，所有打开模式只读完整校验，必要关闭成功后才交付身份；缺坏门不修复或从数据 header 补造。门自身仅此一份 CRC，数据 header 仍由 RBF 保护。初次建立已定于 S2 `[R-FS-STORE-CREATE]`：先准备空必要布局，再直接 create-only 写正式门，flush/close/交付成功才正常返回；完整合格门即使来自失败调用也可按实际资格重开，不倒推旧调用确认。Create 不预建数据文件，首个实际追加才创建 FileId=1；owner 协调已定于 `[S-FS-OWNER-LOCK]`：永久0B控制文件、writer独占/readers共享、门前bootstrap后重检及最后关锁；不以文件存在判busy。私有data残留与实际根准入已定，RBF 公面与目录协议已实施；实际测试和剩余系统资格见持久化闭环记录。
2026-10-07 用户确认：**ConfirmDurable 确认调用时已经完成的全部必要输出；未完成 Builder 保持原状，不因此次确认而获得完成或耐久资格。** leased 文件中的旧完成输出也必须覆盖，Builder 后续完成重新登记为未确认；flush 失败停用整个 owner 及其他 Builder/Writer。
**Builder 活跃期间，允许随机读取已经完成的文件前缀；正在构建的新帧仍不可读。** RBF 指定 ticket 随机入口的 Building 范围检查包含帧后 Fence；普通读取仍校验内容、生命周期和共享 fault，不扩大扫描相关入口或并发合同。正式核心要求见 S2。
成功 EndAppend 自动归还文件和数量配额，不等后续 Dispose。S2 定稿可选 framestore.config.json、默认 MaxOutstandingBuilders=32、严格校验、可写打开一次读取且实例固定；超限 Begin 立即拒绝。完整 Append 不占 Builder 配额，单 driver 最多另占一个短期租借，不引入精确总内存账本。首版保留可写 active 句柄并显式使用 RbfCacheMode.Off，archive 随机读按操作开关；成本按实际 active 和构建/读结果占用计算，降低配置不抹掉旧高峰。
同步物理检查的源码与验证边界见[实施记录](02-framestore-inspection-implementation.md)。S2 已定 ReadFrame 返回独立拥有 buffer 的 FrameRead，关闭 reader/owner 后仍可使用至结果 Dispose。Inventory/Audit 使用同步 visitor：分别提供真实主链结构发现和全部实际帧完整 CRC 检查，回调期间拒绝同 owner mutation、允许随机读取；不公开扫描器或延迟 Reader-bound 结果，不把前缀回调、结构扫描或物理 CRC 健康当作业务闭包健康。
S2 已定稿一次性共享 Lease 与 owned Builder/Writer：成功完成只登记并归还；下一合法 Append/Begin、ConfirmDurable 或可写 Open 统一归档停止文件。已完成帧不因维护失败撤销，可纠正拒绝保留租借，无法辨相位的委派异常保守停用 owner；Dispose 只尝试清理全部资源。
编号恢复已定为完整流式正式名称检查，取 active/archive 实际最大 FileId，只保留内存 max 和既有 active 台账。空桶/私有槽不提供已发布编号，缺口不补填，uint 耗尽仅拒绝需新文件的请求；archive 内容仍按需校验。正式路径已定为 `active/<8位完整FileId>.rbf` 和 `archive/<6位bucket>/<同一FileId>.rbf`，严格小写 ASCII hex、非零编号、桶范围/对应及全部直接项语法；唯一细节见 S2。冷打开名称成本为 O(active 文件 + archive 桶 + archive 文件)，不保留计数器或全历史 ID 表。owner 锁、实际根准入/组件类型及私有残留合同已进入公开目录生命周期实现；两平台测试、中断/资源/规模资格见[最终源码验收](02-framestore-final-acceptance.md)。
循环引用通过多个已知尺寸 Builder 的提前地址形成。FrameStore core 与交错构建/耐久确认可以独立定稿、实施和验收。
VersionStore 采用完整 `RootMap = string => FrameAddress` 快照：每 ref 一个 RBF3 文件，每次追加完整字典；tag 保留命名不可变字典与固定哈希分桶。应用管理名称/元数据到 RefId 映射，库不提供 branch 绑定或 alias。首版不分段/轮转、差分/checkpoint 或独立 Commit/Parent。
VersionStore 借入一个 data FrameStore 并拥有独立发布目录；CreateRef/PublishRef/ForkRef/CreateTag 先完成必要依赖与 data ConfirmDurable，再确认输出/发现边界并安装。无关 Builder 不阻断独立闭包；应用成功后按字典加载，Key 语义与业务因果由应用解释。
S4 格式门 `versionstore.format` 为普通 44B 记录：统一版本、两份 16B 身份、4B ReservedRefIdUpperBound 与唯一 CRC32C。两模式只读校验并核对实际借入 data，必要 close 成功后才恢复/private 清理/输出；失败不补门或停用健康 data。预留上界创建后不变，Open 不能覆盖；初次创建、根/锁/模式等剩余机制仍待定。
RefId 完整身份为(VSID,非零 local32)，内部字段 4B LE；完整等值/default/跨 VSID 前检不变。新增 LocalId 只读导出与 ReadRef(local)的显式库上下文 checked 定位，应用可保存动态号或用常量选择固定槽；纯 local 查询由应用保证选对库，不提供整体身份 codec。RefRevision 仍为完整 RefId/ticket，不新增精确 revision 跨进程 codec。S4 `[S-VS-REF-ID-DOMAINS]` 固定 L：1..L 指定 create-only，高于 L 仅自动；冷开 H=max(L,正式最大号)，自动 H+1，低号不推进 H，不复用、无持久 next；Max 仅拒绝自动创建。创建/L/private/平台资格仍随实施取得。
RootMap 公开输入/输出复用 IReadOnlyDictionary，不另建公开集合类型；库逐项 Ordinal 校验后冻结，普通 sealed RefSnapshot 提供 Revision/Roots 与专项规定的本帧时间，CreateRef/ReadRef/PublishRef 统一成功值。内容比较仅内部完成；[BCL 集合探针](../../experiments/OwnedRootMapProbe/README.md)揭示普通 ReadOnlyDictionary 的 SyncRoot 改写路径并支持冻结选择。S4 `[F-VS-ROOTMAP-BPV1]` 已定 count + 无损 string/变长地址组合、任意条目顺序与唯一 1MiB codeword 上限；[codec 组合探针](../../experiments/RootMapCodecProbe/README.md)包含实际边界及复合消费检查。其他 API 仍待定；探针不构成新库或峰值内存/性能资格。
S4 `[F-VS-REF-FRAMES]` 已定 RefHeader=0 / Snapshot=1、header meta0/非墓碑；header 精确 24/36B 判别无来源/有来源，统一版本+VSID16+RefId4，来源再加 4+8；Snapshot payload仅RootMap、上限1MiB，TailMeta消费 [S4元信息专项](08-versionstore-record-metadata.md) 的时间/长度判形。[帧格式探针](../../experiments/RefFrameSchemaProbe/README.md)在旧 8B LocalRefId/fixed12 synthetic RootMap 草案验证 75 项并复现长初始 body 缺尾仍超过最小初始化长度、被 RBF 补齐后再次 None 的缺口。因此可写恢复前初始化保护仍在 S4-Q2，schema 定稿不等于 Ready 或平台/恢复已实施；该坏正式输入也不是正常文件发布中断会产生的半初始化。
2026-10-10 用户采用不支持 branch 名称与任意指定 ID 的化简：S0 `[S-VS-REF-ID-ONLY]`，S4 `[S-VS-REF-ID-DOMAINS]`、`[A-VS-LOCAL-REF-LOOKUP]` 与 `[S-VS-REF-FILE-PUBLISH]` 为权威。CreateRef/ForkRef 都有自动/预留区指定入口，预留不预创建对象；L=256 时首个自动号 257，不规定默认 L 或必建 main。
所有 ref 位于 `refs/<8 位小写 hex>.rbf`，整数可直接拼出路径，无 name binding、ref 容器或 ID→path 索引；private 完整 flush/close 后一次 no-overwrite file rename 公开。Unknown/private 及可变初始化保护仍须处理，文件移动的底座资格不自动成为 VersionStore 验收。
当前值校验本地 header/初始快照并完整读取最后快照，不递归祖先；S5 历史从固定完成上界沿首帧 `ForkOrigin = 源 RefId + 源 SizedPtr` 接续发布前缀，通过同步 bool visitor 逐项交付自有 RefSnapshot。选中后可正常停止，调用结束后再 fork/tag/rewind；不外泄枚举器/epoch。返回数与工作步分别限额，Complete/VisitorStopped 与专用预算失败区分，步数不称实际 I/O 或内存上限。ForkRef 两种入口 内部确认源成员、复制源字典，仍保存子初始完整快照；只传 roots 的创建是无来源起点。rewind 追加旧字典成为新 revision，tag 冻结所选字典。RefRevision 来自完整发布/实际 checked 历史，不是输出前尝试 token；跨重开的 RootMap 字典书签可用 tag，不保存精确 ref 历史来源。
ListForks 消费 S5 `[A-VS-FORKS-CHECKED]`：每次扫描全部正式 ref 的 checked 首两帧，包括预留区/自动区与无来源根，完整源存在/迭代环检查后一次性交付自有只读 ForkInfo 边列表。只设正 int maxRefs 数量参数，计全部正式节点，预算失败不当无分叉或部分成功；首版不保留跨调用反向缓存或持久孩子表。来源字段仅为声明位置，不签发 checked 源 revision 或宣称全部源历史健康；实际历史跳转再校验源快照与子初始字典一致。数量不是硬内存/时限，实际成本与 S4 初始化/平台缺口仍另验；现有 RBF 逆扫只从 EOF 开始，历史定位旧 fork 点可能扫描源后续帧。
输出异常停止实例并重开读取实际状态；完整快照损坏报错，不回退旧值。同步 mutation 的 Result/必选 out PublicationOutcome、公开尝试与确认边界已在 S4 定稿，异常路径不依赖新建证据包装；NotAttempted/Confirmed 均不代替实例健康。自动创建丢失返回值后不按同值精确认领，首版不提供通用 CAS 或精确 Unknown 尝试查询。数据闭包、工具 operationId、外部 uncertain 策略、模拟器 RNG 和应用谱系由应用负责；[两类下游评估](reviews/2026-10-07-downstream-fit.md)未发现必须扩大核心的需求。VersionStore 发布与续跑协议仍未实施。

2026-10-03 的初始裁决及失败轨迹见[历史设计审阅](reviews/2026-10-03-dialectical-review.md)；旧单流、单 active/locator、纯 batch planner，以及后来 Commit/control 草案均已被后续会话修订，不作为当前模型的实现证据。S1–S3 已取得各自源码 Accepted；已确认方向见 S0，S4–S6 的剩余工程选择仍是 Draft。

## 顺序与职责

| 顺序 | 权威合同 | 对外用途 |
| --- | --- | --- |
| 单个 RBF 文件的物理追加顺序 | RBF；S2 后端调度 | 原子帧恢复，不解释数据谱系 |
| 同一 ref 的快照追加顺序 | S4 单文件；S5 checked 历史枚举 | 选择当前值或旧快照；不建立跨 ref 全序 |
| ref 创建来源与继承前缀 | S4 ForkOrigin；S5 跨文件历史及 ListForks | 固定分叉点、遍历发布历史、查询全部创建分叉；不按同值去重 |
| 应用因果/实验谱系 | 应用数据 codec | 显式引用轨迹/来源，不由 ref 历史或 data 地址推算 |
| tag 的名称唯一性 | S5 tag 名称政策与桶记录 | 冻结不可变字典，应用 ref 名称不进入此层 |

下一轮重点讨论见[设计难题汇总](design-questions.md)。该文是问题索引与推导说明，不是额外阶段或前序层的规范输入；正式合同仍在相应 S0–S6 及其引用的阶段内专项。

## 阅读与状态规则

- 先读仓库根 [README](../../README.md)，再读 [S0 总体决策](00-architecture-decisions.md)。
- 本目录遵循 [规范约定](../spec-conventions.md)：`decision` 记录会话已确认方向；S1–S3 的 `spec` 与签名已成为实施合同，S4–S6 仍是候选要求；未审定阶段的建议、算例、API 名称不自动冻结。[08元信息专项](08-versionstore-record-metadata.md)是S4子合同，编号不表示后序阶段或新增项目。
- 本组设计正文按用户要求只保留当前有效条款，被替代的草案条款直接移除，作为上述约定中保留废弃条款要求的局部例外。现行 Clause-ID 不重命名或复用；历史迁移保留在 `reviews/` 与 Git 历史，审阅记录的当时结论及检查数字不代表当前状态。
- 阶段状态使用 `Draft → Ready → Implementing → Accepted`。Ready 前定稿字段/API/算法及验收映射；可以对范围明确的必要子合同单独审定，未支持能力不得借整体标签宣称成立。
- S1 的实施合同提交为 `c940ed6`，实现提交为 `8ab98bf`；RBF 818/818、Data 288/288 和 W: public 源码消费已验收，实际工作树和二进制身份见[阶段验收记录](01-rbf-sized-append-acceptance.md)。本片不宣称新增包消费、性能或进程实杀通过，也不宣称下游已接入。
- 后续参考依赖拆分轮次已取得 RBF3 三包候选的 W: 隔离 PackageReference 消费资格；候选版本、实现提交和 76 个源码 checksum 见[过渡验收记录](../rbf1-reference-transition.md#6-验收记录)。它不重标 S1 的历史工件，也不代表公开发布或 FrameStore/VersionStore 已交付。
- 旧库维护归 RBF1 分支，main 仅保留冻结参考，不要求它们适配新栈。[原调查](../Rbf/rbf3-adaptation-baseline-investigation.md)基于 `e6ad4d2`，记录旧源码跟随新底座时的断点，不冒充本轮冻结依赖后的测试结果。

## 阶段顺序

下图的箭头表示合同依赖；下游使用的必要子合同必须先形成、实施并取得对应资格。它不要求整层横向完工后才能试验下一层；各阶段规范只依赖前序阶段。

```mermaid
flowchart LR
    S0["S0 总体边界"] --> S1["S1 RBF 已知尺寸追加"]
    S1 --> S2["S2 FrameStore 核心"]
    S2 --> S3["S3 交错构建 / 同步屏障"]
    S3 --> S4["S4 完整字典 / 单文件 ref 发布"]
    S4 --> S5["S5 历史选点 / fork / tag"]
    S5 --> S6["S6 集成与交付"]
```

| 阶段文档 | 本阶段形成的合同 | 主要实施范围 | 出口 |
| --- | --- | --- | --- |
| [S0 总体边界与决策](00-architecture-decisions.md) | 项目边界、事实归属、恢复模型、兼容政策 | 文档决策 | 后续阶段无需反向依赖消费者语义 |
| [S1 RBF 已知尺寸追加（Accepted）](01-rbf-sized-append.md) | 正向/预算尺寸试算、分立长度 Begin + out ticket、格式信息、Builder 生命周期 | `src/Rbf`、`tests/Rbf.Tests`、RBF public 源码 smoke | [单文件独立验收](01-rbf-sized-append-acceptance.md)已闭合 |
| [S2 FrameStore 核心](02-framestore-core.md) | 三种追加、不透明地址与固定/变长 codec、独占租借、软轮转、目录生命周期与基础耐久确认 | 新建 FrameStore 与其测试项目 | 分配器可独立使用，多个 active 恢复及重开 |
| [S3 交错构建与耐久确认](03-framestore-interleaved-builders-and-durability.md) | 提前地址、跨文件互引、交错完成，消费 S2 核心屏障的组合资格 | FrameStore 与其测试项目 | 消费者无需管理文件 flush，预算内互引与完成可独立验收 |
| [S4 VersionStore 字典与发布](04-versionstore-publication.md) | RootMap codec、单文件 ref、低号预留/两种创建、本地定位、屏障/恢复/证据 | 新建 VersionStore 及测试 | 当前值无 replay；文件发布/冷重开 |
| [S5 历史、fork 与 tag](05-versionstore-names-and-indexes.md) | owned 跨文件历史、全分叉发现、来源 fork 与分桶 tag | VersionStore 及测试 | fork/rewind/tag 与历史生命周期验收 |
| [S6 集成与交付](06-integration-and-delivery.md) | 第二种状态模型、公共包消费、交付边界 | examples、eng、CI；消费者接入另有明确范围 | 源码、包及消费者证据分别齐备 |

S4 当前值只检查本地必要首两帧/末快照，S5 历史才回溯。可写冷开发号仍完整枚举正式 ref 文件，直接 ID 访问无需映射表。tag 每次完整校验目标桶，以临时全名集合确认唯一性，无跨调用索引；名称政策/路由仍定稿，不宣称 O(1)查名。

[08：ref记录元信息](08-versionstore-record-metadata.md)是S4时间语义、固定8B TailMeta与长度判形的唯一权威。S4定义RefSnapshot属性和普通宿主尺寸，S5定义历史/fork语义，07复用元信息组合CU；不重复定义时间编码，不给header/tag/data加时间，也不要求全库时间顺序。

[07：ConditionalUpdate 跨 ref 事务](07-versionstore-cross-ref-conditional-update.md)是单独的专题设计稿，作为多 ref 联合提交的优先发展方向。它细化完整成员表、精确读与实际槽位消歧、fresh/self 和整批 Outcome；协议审阅与 public RBF3 研究支持正式选型建议，但尚未并入 S4/S5 的实施合同，VersionStore 尚未实施。

| 候选扩展 | 内容 | 接纳与实施 |
| --- | --- | --- |
| [FrameBatch 批量规划器](extensions/framestore-batch-planner-candidate.md) | 先规划大量地址、再以少量 Builder 逐帧填充的资源优化及 API 草图 | 设想阶段；需求未验证、API 未冻结，不属于 MVP 实施或 Ready 条件 |

[Prepare + Commit 历史比较方案](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)保留作 07 的比较基线，不列入当前候选扩展。

S2–S6 的 MVP 定稿、实施和验收不等待上述候选扩展的需求确认或 API 定稿；扩展如接纳，另行形成合同与资格。

2026-10-07 的整组一致性复核与修正见[复核记录](reviews/2026-10-07-consistency-review.md)。该复核轮次只修订合同边界、权威归属与验收向量，S2–S6 仍为 Draft，不构成实现或平台资格。
其后用户接受解除两项过宽 guard；[活跃构建期间已完成输出的改进记录](reviews/2026-10-07-completed-output-improvements.md)区分本轮 RBF 底座代码验收与 FrameStore/VersionStore 合同修订，不把底座通过视为新项目已实现。

## 最小纵向实施顺序

| 片 | 必要前序合同与证明 | 范围限制 |
| --- | --- | --- |
| V0 | S1 尺寸/格式/early ticket，双 RBF 文件互引读回 | 单文件能力，不声称新库已实现 |
| V1 | S2 owned 三种追加/嵌套租借与核心 barrier + S4 CreateRef/PublishRef/ReadRef；冷重开 | 单文件 ref，完整 RootMap；S3 验证交错构建组合，不引入分段或全局日志 |
| V2 | S3 双文件 A↔B、self-reference 与乱序完成后同时发布多个根，取消/reuse/Unknown | 本根所需依赖完成并确认，无关 Builder 可继续构建，旧/新字典不能混搭 |
| V3 | S2 creating/active/archive、软轮转/编号恢复 + S3 多 active barrier 组合验收与进程中断 | 完成后才声明完整多文件生命周期和恢复资格 |
| V4 | S5 历史/ForkOrigin、自动/指定 fork、ListForks 与 rewind/tag | 复用 S4 单文件发布；分段/差分/随机 revision 后置 |
| V5 | S6 平台、公共包、真实消费者边界 | 包 smoke/消费者接入仍各自出证据 |

FrameStore 及其测试项目已在 S2 创建；VersionStore 及其测试项目仍由 S4 创建。S6 汇总组合与交付，不垄断第一次 public API 纵向验证。

## 新项目落点

| 项目路径 | 身份 | 首次创建阶段 |
| --- | --- | --- |
| `src/FrameStore/FrameStore.csproj` | `Atelia.FrameStore`，生产库 | S2 |
| `tests/FrameStore.Tests/FrameStore.Tests.csproj` | `Atelia.FrameStore.Tests`，非 pack 测试 | S2 |
| `src/VersionStore/VersionStore.csproj` | `Atelia.VersionStore`，生产库 | S4 |
| `tests/VersionStore.Tests/VersionStore.Tests.csproj` | `Atelia.VersionStore.Tests`，非 pack 测试 | S4 |

项目创建时使用仓库的 .NET 10 SDK、命名与测试依赖约定。新增生产包注册及 pack 顺序仍由 `eng/Pack.ps1` 唯一维护；当前主线注册 Primitives/Data/Rbf/Binary，旧参考库 IsPackable=false，FrameStore 保持 source-only。后续新项目不得借旧库包名交付；S6 完成对应包入口后才能声明新库包消费可用。

本目录不固定后续实施的 Git 提交、包版本或公开发布；这些取决于实际实施会话的授权及验收结果。

## 每阶段的迭代方式

每阶段先补齐：外部入口、格式字段、状态转移、确定拒绝与未知结果、恢复步骤、资源归属、复杂度预算，以及可独立完成的实施片。
一次 Coding Agent 任务只处理其中一个有边界的工作包；一个阶段可以包含多轮设计、实现、评审和证据修复。

阶段需要新的下层能力时，在相应前序文档提出变更并重新验收，再继续下游；不在下游复制长度公式或增加隐含协议。导航索引可以链接全部阶段，规范输入保持单向。

S1-A/B/C 已实施并独立验收：公共尺寸计算、分立长度的提前 ticket 入口，以及双文件互引和生命周期证据已闭合。后续阶段继续沿依赖链关闭各自阻断项，不把本片源码资格扩展为新库、包或下游资格。
