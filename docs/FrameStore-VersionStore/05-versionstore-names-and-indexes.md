# S5：ref 历史、fork 与 tag 施工导航

状态：**既有历史/fork/tag 等范围的独立 source-only 源码资格 Accepted；CU 已正式接纳并取得本轮独立源码验收 Accepted**。历史 visitor/预算/清理、精确 ForkRef、ListForks 及09的 tag 公开闭环的既有资格见[最终源码验收](12-versionstore-final-acceptance.md)。CU 来源、历史判据与预算增量见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。公开进程、受控故障、文件镜像与包消费分别记录，旧六包候选不含 CU；后续见[实施计划](12-versionstore-implementation-plan.md)。
本文负责历史、fork 与声明图查询；ref 格式/身份/编号及发布协议消费 [S4](04-versionstore-publication.md)，记录时间见 [08 专项](08-versionstore-record-metadata.md)，bytes 结果见 [11 专项](11-versionstore-root-payload-bytes-api.md)。不可变 tag 由 [09 专项](09-versionstore-immutable-tags.md)定义，安排在 ref、历史与 fork 之后施工。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个 ref 的当前/历史值及 tag 内容都为应用解释的完整 root-payload bytes。ref 以 RefId 定位，应用管理名称/元数据/固定角色；tag 冻结创建时 bytes，不跟踪 ref，不提供 branch 名称绑定。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadRefHistory(refId, budget, visitor, cancellationToken) | 从固定上界逐项交付本地及 ForkOrigin 接续的自有快照，可正常选点停止 | 同步 visitor；文件内真实链 + 首帧来源链接，无另写索引 |
| ForkRef(sourceRevision) | 自动编号，从精确源快照创建 fork | header/ForkOrigin 与完整初始 Snapshot，一次文件发布 |
| ForkRef(destLocalRefId, sourceRevision) | 在空闲预留号创建 fork | 同上，指定目标 create-only |
| ListForks(maxRefs, cancellationToken) | 查询全部正式 ref 声明的创建来源边 | checked header 与完整图；单次自有只读结果，无跨调用缓存 |
| CreateTag / ResolveTag | 创建/读取不可变完整 bytes；可选 Related 仅作关联注记 | 09 的目标桶元信息裁决与必要完整 root 读取 |
| ReadTagInfo / ScanTags | 按名读取或同步逐项交付 tag 元信息 | 09 的独立 checked 元信息；不证明 root 健康或签发 revision |
| 无来源复制 / rewind 便利流程 | 旧 payload 新建独立 ref / 追加回原 ref | 调用 S4 CreateRef / PublishRef |

历史入口/终止消费 `[A-VS-REF-HISTORY-CHECKED]`，ListForks 结果/限额消费 `[A-VS-FORKS-CHECKED]`；mutation 统一用 S4 AteliaResult/必选 out PublicationOutcome。fork 只接收 revision，不接受自由组合 roots/origin 或凭同值补链接；无来源复制仍用 CreateRef。首版不删除/复用 ref，不修改/删除 tag，不提供差分/checkpoint；多个同库既有 ref 的共同发布由07定义，不包括创建或 tag。发布历史与应用因果谱系分别解释。
ref header/普通 Snapshot 的字段、来源判别与完整记录容量唯一消费 S4 `[F-VS-REF-FRAMES]`；历史、fork 和 ListForks 不另选编码。正式 ref 的恢复与读取资格消费 S4 `[R-VS-LOCAL-COMPLETE]`：可写访问先恢复，再校验 header 与本次实际需要的记录；不预读初始 payload、不追溯创建过程，也不增加本层前缀保护。
自动/指定 fork 共用 S4 `[S-VS-REF-ID-DOMAINS]` 的编号/目标前检，在 data/源 flush 及私有准备前拒绝本次确定失败；tag 不分配 RefId。file rename 正常返回后先 Confirmed，自动创建再登记水位，随后安装结果；编号不是跨 ref 时间全序或来源资格。
正式 ref 文件/来源定位与全集发现消费 S4 `[F-VS-REF-PATHS]`；private 排除。tag 名称、内部路径与桶合同唯一见 [09 专项](09-versionstore-immutable-tags.md)，不缩减 ref 发现范围或新增全部历史审计。

## 表达能力与历史边界

本模型的节点是实际发布快照：同一 ref 内的前一节点由文件顺序隐式表达，只有初始节点的跨文件前序由 ForkOrigin 表达。每个节点最多一个前序，多个新 ref 可以从同一快照分出，因此足以表达线性 head 推进、任意历史选点 fork、fork 的再次 fork，以及全部创建分叉构成的森林；不需要每条 Snapshot 重复保存 Parent。

| 动作 | 本层历史表达 |
| --- | --- |
| 普通发布或重复发布同值字典 | 追加新 revision，前序为本 ref 的前一实际快照 |
| 从源 A2 fork 为 B | B0 原样复制 A2 的 root-payload bytes，header 指向 A2；B0 是新的创建发布 |
| 源 A 后来继续更新 | 不改变 B 的来源，A2 之后的源更新不进入 B 的继承历史 |
| rewind 到旧字典 | 追加一次新的 head 变更，保留被撤回的更新记录 |
| 业务 merge、编辑命令、撤回目标、状态因果 | RootMap 本身不说明这些意图；应用另行保存相关信息 |

例如 A 的实际序列为 `A0 → A1 → A2 → A3(字典同 A0)`，从 A3 fork 后的发布历史仍包含 A2/A1；不能把它当成只沿业务父状态回到 A0 的因果链。本层也不将一个合并后的 RootMap 自动解释为多父 merge DAG。这里的“完整历史”是当前 ref 的本地发布序列，以及各次 fork 固定选中的前序发布前缀；兄弟分叉的更新与源 fork 点之后的更新另由分叉图发现，不混入这条历史。

### spec [S-VS-FORK-FROM-REVISION] fork 在创建时建立精确来源资格

ForkRef 两种入口 MUST 接收本 VersionStore 的已发布 RefRevision，先消费 S4 default/持久上下文前检，定位正式源 ref、checked header，确认 ticket 在 header 后的完成范围内并直接精确读取。成员来源消费库签发 revision、完整位置不删除/复用与无外部改写的协议，不从源 EOF 重新定位，也不预读无关初始内容；本次仍按 S4 `[A-VS-ROOT-PAYLOAD]` 完整校验目标 CRC、kind、meta 和尺寸，私有捕获 root-payload bytes，并复用现有公共 GetScanBoundaryAfter 重验完整 anchor 及其尾 Fence；随机完整帧 CRC 本身不覆盖尾 Fence。这个 helper 的额外完整读计入实际成本，不扫描源本地后缀，也不依赖边界逆扫重载。不得用 ReadRef 替代 exact 来源、不接受裸 SizedPtr 或自由组合 payload/origin。CU 来源还 MUST 通过07共同判据并只截取 root bytes，判为未提交时返回来源错误，不改选较旧节点；peer 的异长消歧可按07访问其必要后缀。两种入口均返回 AteliaResult<RefRevision>，成功值的 RefId 为新 ref，保持批外单文件发布与必选 out PublicationOutcome，不为结果物化 Payload。

所有生命周期/历史 mutation guard、自动或指定目标及源 bytes 捕获/容量检查先完成；子初始 Snapshot 按 `[S-VS-RECORD-TIME]` 重新取时，允许与源同毫秒，不复制源时间。随后 data ConfirmDurable，再对 exact 源 ref DurableFlush，最后在单个 private 子文件写完整 header/ForkOrigin 与初始 Snapshot，flush/close 并按 S4 file rename 发布。源 flush 明确确认本次链接的发布记录耐久，checked-read 不能替代；无 receipt、不重刷全部祖先，仍限 ProcessCrashOnly。

源 flush 失败时，本次 fork 尚未尝试公开事实，为 NotAttempted；源 RBF 按自身 fault，VersionStore 停用，不进入子输出。子私有文件/rename/内存安装的结果继续使用 S4 PublicationOutcome。只在子 header 写来源，不向源文件追加孩子列表、不改变源 head，避免源登记与子发布之间引入第二个一致性协议。

子初始记录 MUST 为普通 Snapshot，原样复制 exact 源的完整 root-payload bytes，不重新编码、不复制源 RBF 包络、TailMeta 或 CU Members；保留完整初始 Snapshot 而非 header-only 引用。B0 与源 A2 即使内容相同也都是实际发布节点，历史保留二者及其不同 revision，不按内容去重。需要 bytes 时另行 ReadRef；无来源复制仍用 CreateRef，tag 同值不产生精确来源。CU 源的确认仍只在 data barrier 后确认 exact source 文件，沿 ProcessCrashOnly，不增加全部 peer flush 或断电保证。

## ref 历史与反事实分支

### spec [A-VS-REF-HISTORY-CHECKED] 同步历史读取交付真实且自有的快照

首版历史入口已实施如下；实际源码资格见实施计划，两种 ForkRef 与 ListForks 亦已接通：

```csharp
public readonly record struct HistoryBudget(long MaxSnapshots, long MaxWorkSteps);
public enum HistoryEnd { Complete, VisitorStopped }

AteliaResult<HistoryEnd> ReadRefHistory(
    RefId refId, HistoryBudget budget, Func<RefBytesSnapshot, bool> visitor,
    CancellationToken cancellationToken = default);
```

每项完成 S4 发布资格后，按 [11 bytes 公面专项](11-versionstore-root-payload-bytes-api.md)构造自有 RefBytesSnapshot、归还 pooled 帧，再调用 visitor。结果所有权与内容解码边界不另定义；来源一致性检查不受 visitor 影响。应用可在 visitor 内解码，异常沿 callback 路径传播。RefId/RefRevision 值与位置资格消费 S4。

visitor 返回 true 表示继续，false 表示已接收当前项并正常选点停止。false 后 MUST 不再扫描旧帧或访问下一源；先完成健康/取消复检和必要清理，再返回 VisitorStopped，即使该项恰为起点也保持此结果。调用方可保存所选值，在方法返回后 fork/tag/rewind；需要列表时由调用方收集，不要求库保留整批历史。

**准入与固定上界。** MUST 先检查 owner disposed/fault、RefId 参数、非 null visitor、两个预算均为正数及无其他活动历史读取，再做文件 I/O；09 的 TagScanActive 期间亦不得进入历史。无隐含预算默认值；long.MaxValue 可表示调用方不设置实际限额，default 的零预算无效。通过准入后设置一个 HistoryActive 位，整个同步调用拒绝递归 ReadRefHistory 及本 VersionStore 的所有 mutation，包括发布、自动/指定 ref 或 fork 创建和 tag 创建；mutation 按 S4 先初始化 out，guard 拒绝先于 barrier/输出且不新增 fault。visitor 可串行读取当前值及 data，亦可按09调用 ResolveTag、ReadTagInfo 或 ScanTags，保留自有结果；历史与 tag 扫描使用独立临时槽。同文件 ref 查询复用当前可用的 RBF 句柄，不重复独占打开。owner Dispose 仍是受控清理，不作为发布 mutation 拒绝。首版无并发调用或外部改写，不建跨实例 snapshot 协议。
进入本 ref 时 MUST 用公共正向扫描只完整检查首 header，保存 exact header ticket、checked ForkOrigin 与 header 物理后界给出的预期初始起点，不预读第二帧 payload。随后从实际完成尾部取得逆向扫描器，以该物理完成上界固定本次遍历；依次完整校验普通 Snapshot 或 CU，CU 复用07共同判据，未提交 CU 不交付且继续向前，成立 CU 与普通 Snapshot 各交付一次自有快照。所有无输出的组验证与回退仍计工作步；必要错误不得转成未提交。只有逆扫实际到达预期初始起点时才完整读取该普通 Snapshot。初始即末时只复制一次自有结果。后续不得重读 head 替换这个上界，不能调用未计费的 ReadRef 助手完成隐含资格工作；初始坏而 head 健康不阻断前面的健康交付或 visitor false 停止。

**继承前缀。** 正反向枚举均使用 `showTombstone: true`。本地历史包含重复同值快照与 rewind，省略已检查的首 header 与07共同判据确认未提交的 CU；必要节点的未知 kind/格式版/meta 形状、tombstone、损坏均报错。历史按真实链交付记录原时间，不按时间排序或去重。首版逐文件循环，直接消费 public RBF API：

1. 进入每个 ref 前加入路径 visited，重复 RefId 为循环错误。逆扫到达该文件预期初始起点并完整校验 Snapshot 后，才证明直接第二帧及合法本地起点；越过该起点、提前到 header/正常 EOF 均为非法序列。先交付该初始节点；visitor false 立即停止，不读取来源。visitor true 且无 ForkOrigin 才可 Complete；有来源则保留子初始 bytes 与来源，放下局部扫描器并归还当前临时句柄，再进入源 ref。既有 owner 句柄不因跳转而关闭。
2. 只定位正式 SourceRefId，按 S4 访问模式打开/复用文件并完整检查源 header；不采用 creating、不预读源初始 payload。以协议生成、checked 不可变 ForkOrigin 的 exact SourceSnapshotTicket 直接定位；先检查 header 后范围及对应 Snapshot/CU 的尺寸，再完整验证 anchor 与其尾 Fence，并按下述边界逆扫合同从该点开始。不扫描源本地后来追加的后缀，不以裸 ticket 的随机 CRC 成功替代来源协议；CU peer 的必要异长消歧仍按07执行。
3. 完整校验 exact 源记录的发布资格，CU 来源还须07共同判据；未提交 source anchor 直接返回 InvalidForkOrigin，不跳过或改选更旧来源。取 root bytes，与子初始 bytes 检查长度及逐 byte 相等，时间/envelope/Members 不参与。不同即来源错误，不接受同字典不同 bytes，不用 CRC/hash 替代。来源一致性通过后才向 visitor 交付自有源 bytes 与源自己的 revision，继续更早前缀；子与源均为不同发布节点。
4. 源本地 fork 点之后的更新不进入继承历史，也不进行本层结构、meta 或内容遍历；CU peer 的必要结构消歧不受此本地前缀范围限制。边界/目标自己坏、实际遍历的旧节点坏、已观察到的 TerminationError、I/O 或 fault 仍传播；source factory 对当前 EOF 的结构资格/恢复仍按 S4 执行，边界起扫不绕过它。

每个实际 MoveNext=false MUST 检查 TerminationError；非 null 传播底层失败，尚未到预期初始起点的正常 false 也是非法序列/来源，不能伪装 Complete。无来源的 exact 初始 Snapshot 已完整校验，且其 visitor 返回 true 时，header 后界与该节点起点已证明没有更早快照，可直接 Complete，不必额外推进到 header/EOF。Complete 只证明本次继承发布前缀，不审计被排除的源后缀或证明应用图健康；必要历史节点坏不能静默跳过。

**预算与停止。** 两个上限只约束本次遍历，不持久化，不增加配置、cursor 或分页协议：

| 预算 | 计费与拒绝边界 |
| --- | --- |
| MaxSnapshots | 每次交付 checked 自有快照计一项；交付上限用完且起点尚未证成，继续遍历前即失败，不为探测下一项执行额外 I/O |
| MaxWorkSteps | 每次进入一个 ref（含 CU peer 访问）、每次 forward/reverse MoveNext 尝试、每次完整帧读取各计一步；包括 header 资格、source anchor 核验、CU 无输出组验证/消歧、重复实际读取及返回 false 的尝试。CU 异长定位的 ReadFrameInfoImmediatelyAfter 结构后继查询另计一步。GetScanBoundaryAfter 与边界逆扫重验 anchor 的每次完整读也在动作前分别扣费，不能以 helper 封装漏计；复用已读本地 CU 不重复计 peer 读取，不复制 RBF 帧长度/CRC 算法 |

额度不足返回错误码 `VersionStore.HistoryBudgetExceeded` 的失败 Result，不返回正常 BudgetExhausted/partial-success 或公开 usage 摘要。先复检 owner/fault/token，再判继续工作的额度；visitor false 优先正常停止，visitor true 且真实无来源起点已经证成则 Complete，返回数恰好用完不阻碍该结果。尚需资格检查才能证明起点时，所需步骤也必须有额度；禁止越额探测或根据数量猜祖先不存在。已交付前缀仍可保存，但预算失败、正常早停都不证明未访问历史完整。
工作步是算法入口次数，不是物理 I/O bytes、耗时、单帧大小或 RSS 上界；工厂 Open 的内部结构检查/恢复、缓存预读和一次同步读/CRC/envelope解码不能被这些计数拆分或抢占。visitor 自己的查询/data 读取及结果积累另计。进入 ref 的一步约束访问 ref 数；文件打开前后、每个扫描/完整读和回调之间检查 cancellationToken。完整读取前先以已取得的 RbfFrameInfo 检查 S4 该版本的记录尺寸上限，不先租用超限全帧再拒绝；解码仍消费 S4 字段/容量检查，不能为满足工作预算取消它们。每查询全帧请求字节额度、硬内存/时限等独立预算留待真实大 payload 成本或调用方限额需求出现后设计。

**清理与失败。** raw sequence/枚举器仅在方法栈内，跨文件不预收集 ticket 列表；RBF 枚举器本身无 Dispose，不引入外泄 sequence、owner epoch 或扫描 Lease。除既有 owner 文件外，遍历只保留一个当前临时 RBF 句柄、至多一个短期 CU peer 句柄、当前完整帧/必要自有 root bytes 和 O(继承路径已访问 ref 数) visited；额外 handles 为 O(1)，visitor 自行保留的结果另计。CU peer 访问不加入继承路径 visited，也不递归验证替代 CU 组。临时文件按 S4 的只读/可写恢复规则打开，本调用不另发布、flush 或清理私有创建残留。
当前历史和短期 CU peer 句柄均登记在 owner 可清理槽位，连同 RefId 可供同文件查询复用；peer 逐个访问/关闭，不装入全组长期缓存。正常跳转、所有退出及 owner Dispose 都 MUST 在调用 public Dispose 前先取出并清空相应槽位，关闭抛错也不重试。owner Dispose 先失效实例，尝试当前临时及其他 owned 资源，不 Dispose 借入 data；方法 finally 只清理仍在槽位的句柄，所有退出都清除 HistoryActive。既有 owned 文件继续遵守自身生命周期，不把借用句柄当临时句柄归还。
每次 visitor 返回后、继续 I/O 前、必要关闭后及最终正常返回前 MUST 复检 owner disposed/fault 与 token；最后回调 Dispose owner、取消或抛异常，最后关闭失败，都不能返回 Complete/VisitorStopped。取消抛 OperationCanceledException，visitor/I/O 等异常原样传播；必要 CRC/envelope/长度/身份/成员错误返回所属层失败，不跳过坏记录、不回退。预算失败、普通读错误、纯读异常、取消或 visitor 异常本身不 fault 健康 writer；实际 owned 清理/恢复失败按 S4 停用规则，主异常优先并继续尝试清理，清理失败不能被正常停止遮蔽。

**边界逆扫依赖。** [RBF 边界逆扫增量](../Rbf/rbf-reverse-boundary-plan.md)已提供 `ScanReverse(RbfScanBoundary boundary, bool showTombstone)`，复用现有 anchor/完整 CRC/尾 Fence 检查，从 anchor（含自身）向前。V2-A 直接使用该公共重载，独立底座测试与 VS 接线验证分别记录，不以 internal 文件视图、手写 Fence 或仍扫描无关后缀替代。RbfScanBoundary 本身是可构造数据而非成员凭证；本层来源资格来自已签发 revision 或自身创建协议写入的不可变 ForkOrigin、完整位置不复用及无外部改写模型。RBF3 正常 writer 的 marker-free 边界约束不认证任意损坏/手工镜像；失败仍按必要记录传播。同步 visitor 不承诺持久 cursor，底座工厂成本仍另计。

（Informative）A 已有 A0/A1/A2/A3，B 从 A2 创建并更新 B1，C 从 B1 创建后更新 C1：`ReadRefHistory(C)` 返回 `C1, C0, B1, B0, A2, A1, A0`；A3 不出现，C0/B1 与 B0/A2 的字典分别相同但 revision 不同。

（Informative）Gym 在 visitor 中选中旧快照 `R` 并返回 false，待 ReadRefHistory 返回 VisitorStopped 后，再调用 `ForkRef(R.Revision)` 或 `ForkRef(destLocalRefId, R.Revision)` 保留来源；只要独立起点时用 CreateRef(R.Payload) 入口。rewind 调用 `PublishRef(oldRefId, R.Payload)` 追加新 revision，保留原发布历史。data 根帧共享，不复制其图；tick、RNG、业务父状态与推演规则继续由应用保存。

## 全部分叉查询

### spec [A-VS-FORKS-CHECKED] 正式 ref 的首帧声明完整分叉图

首版入口已在 V2-C 实施；源码与后续系统资格见[实施计划](12-versionstore-implementation-plan.md)：

```csharp
public readonly struct ForkInfo {
    public RefId ChildRefId { get; }
    public RefId SourceRefId { get; }
    public SizedPtr SourceSnapshotTicket { get; }
    // 仅库内部构造；三个属性保存自有值。
}

AteliaResult<IReadOnlyList<ForkInfo>> ListForks(
    int maxRefs, CancellationToken cancellationToken = default);
```

**完整集合与必要资格。** ListForks MUST 覆盖全部正式 ref 文件，包括预留区/自动区与无来源 ref；不依赖应用名称映射或当前 head 值。直接消费 S4 `[F-VS-REF-PATHS]` 的全部直接项发现/期望目标定位，以及 `[F-VS-REF-FRAMES]` 的共同 header 检查：实际首帧、完整 CRC/身份及来源字段；不读取初始 Snapshot、末 head 或 root-payload bytes。creating 不参与，正式文件缺/坏 header、未知项、枚举或必要读取错误皆报错；只有已资格化且正常结束的空正式集合才可当空，不能用 File.Exists=false 或过滤枚举猜缺失。逐 ref 及时释放 pooled header，仅保留身份与可选来源元数据；不调用 ReadRef 或重读 source ticket。初始 Snapshot 的内容损坏不阻断健康 header 声明的查询；底座工厂仍须通过当前 EOF 的结构准入，结构损坏不能借 header-only 查询绕过；header-only 文件也不据此取得当前值或初始化完整资格。

**单一数量预算。** 在任何发现 I/O 前 MUST 检查 owner disposed/fault、`maxRefs > 0` 与取消；没有隐含默认限额，不按 maxRefs 预分配。每个正式 ref 在本次打开/header 检查及加入节点表前计一项，含无来源根，不能只计输出边。达到上限后仍可推进直接项枚举并按 S4 检查该项名称/类型，以证明实际 EOF；若还有正式文件，在其打开/内容读取或节点表增长前返回错误码 `VersionStore.ForkQueryBudgetExceeded` 的失败 Result。不以额度用完猜 EOF，不探测超额 ref 的内容；未知项/枚举或必要清理错误仍走原错误通道。恰有 maxRefs 个 ref 且正常 EOF 可以成功；int.MaxValue 合法，只表示不设置更小逻辑限额，不保证实际集合分配成功。
数量上限约束节点、结果项及下述图算法的访问规模，不是目录枚举内部工作、RBF factory 尾部资格/恢复、同步单帧读/CRC/解码、物理 I/O bytes、耗时或 RSS 硬上限。header 尺寸与完整帧租用前检查继续消费 S4；取消在每个直接项、文件访问/关闭前后及图行走步骤之间检查，不承诺抢占底层同步调用。无独立 MaxForks/MaxWorkSteps/bytes 计数、预算载体、usage、配置或分页协议。

**一个临时图。** 扫描与构图属于同一次 single-driver 操作，不允许 mutation 交错。用一个本次调用的 RefId 节点表保存所有 checked 正式身份、可选来源及检查状态；完整目录正常结束并必要关闭成功后，逐来源核对 SourceRefId 属于同一已 checked 集合，拒绝自链接。无来源根仍在表中，不作为缺源；源 header 已在本次扫描校验，不再逐边打开。全图环检查 MUST 使用迭代三色或等价的已完成节点记忆，每节点仅作常数次图访问；禁止递归深链或从每个节点重新追完整祖先链，也不以 RefId 大小替代来源图检查。不排序，不引入持久 generation、拓扑编号或通用图框架。

**仅交付完整自有边集合。** 全部资格、图校验、必要清理、结果构造及最终 owner/fault/token 复检通过后，才返回非 null 自有列表；无来源 ref 不输出，有来源的每个 child 恰输出一项，零边成功为空列表。任一失败或取消不交付部分成功、visitor 前缀、正常 BudgetExhausted 或“没有孩子”的结论。输出顺序没有跨调用稳定、编号排序或时间顺序承诺。
ForkInfo 是普通非 record readonly struct，仅有三个只读属性及内部构造，无 setter/init、带参 public 构造、Deconstruct、专用整体等值/hash/codec、owner/reader 或 Dispose。它平铺逻辑 ForkOrigin，避免再公开嵌套来源类型；SourceSnapshotTicket 保留完整 SizedPtr（含 length），MUST NOT 包装成 checked 源 RefRevision。default 属性可读取，但不是库成功列表中的有效边，不新增 IsValid/default 错误族。结果仅声明本次已 checked 创建关系，不定位每条源 ticket、不校验源 root-payload bytes 与子初始 bytes 一致，不证明全部历史/data 图健康或旧调用曾 Confirmed；内部构造也不是防伪认证。
列表内部使用唯一自有数组和只实现 IReadOnlyList/IEnumerable 的私有薄封装；不外露可变 array/List、IList 或能取得 backing 的 ICollection.SyncRoot，不复用下一调用 scratch 或长期缓存。ForkInfo 内的 RefId/SizedPtr 都是自有值，无需复制业务对象。查询结束、后续查询/创建及 owner fault/Dispose 不改变已交付列表；反射、Unsafe 等越过正常接口的改写不在此保证内。

**句柄与失败。** 本查询无应用 callback，按既有串行前提执行，不新增 ListForksActive/busy 或改写 HistoryActive。历史 visitor 可调用它；匹配 RefId 时复用可用的既有 owner/历史当前句柄，不重复独占打开、不关闭借用句柄。其他 ref 逐个使用本方法局部临时句柄，绝不覆盖历史当前临时槽；关闭前取出并清空局部槽，finally 只清理仍登记的资源，每个句柄仅尝试一次。目录枚举器、当前 pooled 帧和全部临时句柄的正常/异常退出，消费 S4 的 owned 清理/主错误优先规则；最后关闭失败不能被空列表或预算错误掩盖。
取消抛 OperationCanceledException；必要格式/CRC/codec/身份/图/预算失败返回所属失败，I/O/运行期异常原样传播，不跳过坏 ref。纯读取、预算、取消或结果分配失败本身不新增健康 writer fault；实际 owned 恢复/清理失败按 S4 停用并完整受控清理，不 Dispose 借入 data。访问模式及可写打开的恢复直接消费 S4，本查询不另行 flush、发布或清理 pending；ref恢复后完整性、实际组件/根锁准入已定，VS平台与运行资格仍随实施取得，本条不以 header 校验替代它们。

该边集合是唯一来源的派生视图：按 SourceRefId 可找直接分叉，按 exact source ticket 可区分同一 ref 的不同分叉点，对 child/source 关系求传递闭包可找所有后代；不隐含跨 ref 时间全序。已有 RefBytesSnapshot 可用 Revision.RefId/Ticket 与声明位置比较，关联 exact 点的分叉；匹配不签发源 revision 或证明源内容健康。应用映射不增加边，名称展示由应用管理；应用可从结果派生反向表。
首版每次 ListForks 都执行上述完整 checked 扫描，不保留跨调用反向表、负缓存或图安装/更新相位，不写持久孩子列表、全局 catalog 或索引。实际重复查询成本超出目标后，再研究内存表及其完整建立/更新资格。

（Informative）int 参数与完整物化列表/节点索引的 Count 域相符，是没有超出该域消费者时的工程选择，long 也可正确。readonly 结果与私有薄封装提供稳定保留视图；独立可变副本或标准 immutable 集合也可正确，不因此建立公共集合框架或安全凭证。每 ref 的本层读取固定为首 header，图行走是线性节点访问；哈希表定位按通常均摊成本估算，不承诺碰撞下的硬 CPU 界。公开 [RBF 正扫](../../src/Rbf/RbfForwardEnumerator.cs)与[payload 坏仍可结构扫描的测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)证明 framing 不替代必要完整读取；兄弟仓的[私有只读列表封装](../../../durable-graph/src/DurableGraph.Storage/StateRevision.cs)只提供窄所有权实现参考，不引入依赖或兼容。没有新栈实际消费者或性能测量证明额外 work/byte 限额、排序、缓存及通用图 API 必要；本条定义访问范围与复杂度；V2-C 源码验证见实施计划，不能替代平台或包资格。

## 不可变 tag 的独立施工片

[S5-B：不可变 tag 专项](09-versionstore-immutable-tags.md)是名称、桶路径/格式、初始化保护、独立元信息资格、请求范围损坏裁决、CreateTag/ResolveTag/ReadTagInfo/ScanTags 与对应验收的唯一权威；原 `[S-VS-TAG-PAYLOAD-IMMUTABLE]` Clause-ID 随合同迁入，本文不保留第二份定义。

tag 冻结所选 root payload，可选 Related 只是关联注记，不证明精确来源或内容相等。历史 visitor 可在调用期间 ResolveTag、ReadTagInfo 或 ScanTags，创建必须等历史调用结束；tag 扫描的反向重入限制与临时桶槽唯一见09。该施工片建议在 ref 闭环及历史/fork 后实施；元信息公面安排在 tag 发布闭环后，未完成 tag 不阻塞前面的路径。

## 应用映射与固定槽位

固定角色由应用常量/枚举选择预留号，动态 ref 由应用保存 LocalId，在正确库上下文 ReadRef(local)取得 checked 当前快照；合同见 S4 `[A-VS-LOCAL-REF-LOOKUP]`。无独立 branch binding、全局应用名称查重或 name→ref 索引。

摘要/缩略图/人物区域等元数据可作为 data 帧，与业务根一起放入同一 ref 的 RootMap 整体发布。独立目录 ref 登记动态对象若顺序调用单 ref 发布，创建后登记前中断仍可能留下未登记 ref；ListRefs 可发现实际集合，应用自行核对业务身份。07 CU 只可共同更新多个既有 ref，不把新 ref 创建与目录登记并为一批。

## 查询、启动与成本

### spec [A-VS-ROOTS-QUERY-THEN-READ] 查询 root bytes 后由应用接受内容

查询与应用接受内容的边界直接消费 S4 `[S-VS-ROOTS-OPAQUE]` 与 [11 公面专项](11-versionstore-root-payload-bytes-api.md)；采用 RootMap 时，由应用 Decode 后按地址读取 data。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

Open MUST NOT 默认回放历史/data 图；惰性打开只延后资格检查，不把未访问对象说成健康。实际目录/metadata、factory 尾部资格/恢复、I/O 与资源成本随实施报告，预算不成为硬时间/RSS 保证。各操作的算法与预算由所属条款定义，成本概览如下：

| 操作 | 首版成本与边界 |
| --- | --- |
| ReadRef | 普通 head 为本地 header/末 Snapshot 的 checked bytes；CU head 另检查指定 peer 完整帧/全表，未提交后缀可累积回退，异长 peer 消歧可扫描必要后缀/前驱。不预读无关初始 payload，底座 factory 成本另计，不能宣称 CU 查询成本恒定 |
| ReadRefHistory / ForkRef | exact 旧点直接读；历史从所选 anchor 继续更早前缀，不定位源本地后续帧。CU 另付07 peer 校验/消歧成本，所有历史工作先计费；fork 仍只另 flush exact source 文件，底座 factory 成本另计 |
| ListForks | N个正式 ref、F条边：通常哈希成本下 O(N+F+checked header bytes)，临时元数据 O(N)、结果 O(F)；不读初始 payload，maxRefs含无来源根 |
| ListRefs / 可写冷开 H | 完整正式路径枚举，不打开全部快照；只读 Open 不因 pending 全扫 ref，直接整数定位无需 ID→path 或应用名称表 |
| tag 查询/创建 | 目标桶结构扫描、请求名裁决、常数个名称状态与连续创建成本唯一见 [09专项](09-versionstore-immutable-tags.md)，分桶不构成 O(1)保证 |

不新增持久名称/孩子索引、control 全量投影或尝试账本；ListForks与tag不保留跨调用索引，消费各自首版合同。ref 分段/轮转、差分/checkpoint、tag 分片、可重建索引和业务 merge 等仅在出现实际需求与成本证据后另审，不作为首版前置。

## 实施与验收

建议先完成并独立验收 [RBF 边界逆扫增量](../Rbf/rbf-reverse-boundary-plan.md)，再按 S5-A 历史 visitor → S5-C 来源感知 fork/ListForks → S5-D 两类应用 public 消费及进程终止 → S5-B [独立 tag](09-versionstore-immutable-tags.md)施工。S5-B 保留原切片标识，字母不表示强制顺序；真实文件实验使用 W:，完整组合汇入 S6。

前文历史/fork/声明图合同须逐项验证；S4 创建/编号/Outcome、08 时间、11 bytes 与09 tag 的详细向量从原权威引用，只在本篇补互操作和以下特有组合。

| 验证组 | 特有向量 |
| --- | --- |
| 历史路径 | 当前/非末 fork 点、源后续追加排除、fork-of-fork、多孩子及继承快照再 fork；子初始/源均交付不同 revision，同值重发/rewind 不去重。缺源/错身份/成员/length/CRC/envelope/meta、墓碑/未知 kind/TerminationError、循环及子/源 bytes 不符失败，不作正常终止 |
| 历史预算/停止 | 第k项 false 后不扫描旧坏内容或下一源；初始 CRC 坏/head 好时首项 false 成功，继续到坏初始失败。初始 visitor false 不验证其来源，true 后才跳源。返回数恰在无来源起点耗尽为 Complete，有来源且需继续为预算失败；进入ref/header资格/anchor及内部重复完整读/false扫描全计work，先扣费再动作；前缀/VisitorStopped/失败不称完整历史 |
| 回调/资源 | 递归历史与全部 mutation 在 barrier/I/O 前拒绝，调用后 guard 释放；visitor可当前值/data读取及 ResolveTag/ReadTagInfo/ScanTags/ListForks，复用匹配 ref 句柄且 tag 使用独立槽；活动 ScanTags 拒绝进入历史。最后回调 false/true 时 Dispose/取消/异常，末 close失败，均不正常终止；跳转及所有退出资源单次归还，已交付自有值仍有效 |
| fork byte clone | 两种目标均直接重读已签发 exact 源并在data后确认源，再发布单个完整子文件；源后来追加大量更新不增加本层定位扫描，源初始 payload 坏不妨碍健康旧点，但目标 CRC/envelope 或尾 Fence 坏必须失败，factory EOF 结构资格仍需通过。禁用/计数 decoder，用同值不同顺序/合法冗余BPV及JSON/随机bytes验证exact clone，不写源孩子表；源flush失败无子输出。子重新取时，源/子时间不参与比较 |
| ListForks集合/图 | 两编号域、无来源根、多层/兄弟分叉，creating排除；无来源同值复制/应用映射不产生边。初始/head 的 payload CRC 或 envelope 坏而 header 好、factory EOF 结构合格时仍能查询；缺/坏header、未知项/枚举、缺源/自链接/独立分量环失败，header-only不证明初始化。深链/共享祖先不递归或二次方重追；不逐边审计源ticket，实际历史访问坏来源另须失败 |
| ListForks额度/结果 | 零/负限额先于I/O；全部正式节点含无来源根计费，恰额EOF成功，超额不打开/读内容/扩表；全无来源成功空边。末close/取消/分配/图错误不交部分成功；三个自有字段/full ticket而非源revision、无稳定顺序、正常接口不能改backing，关owner后仍有效；visitor内查询不覆盖历史临时槽 |
| 应用互操作 | 历史停止后 fork/tag/rewind或无来源CreateRef，应用解码/数据失败不回退；无关data Builder活跃仍能基于完成闭包fork/tag。固定槽与动态LocalId/正确库冷重开，富元数据整体发布；独立目录登记的未登记窗口由应用核对，不声称跨ref原子 |

时间与bytes验收按专项接线，继承项保留实际源时间，子初始/rewind重新取时但允许同值，时钟回退不改变链序。tag 复用08时间意义，独立元信息 schema/CRC、Related注记、名称/hash/请求范围损坏隔离及发布/元信息公面详细向量由09负责。

[07 CU](07-versionstore-cross-ref-conditional-update.md)已正式接纳。新增向量包括成立 CU 作为来源/历史节点、跳过未提交组但 exact source anchor 必须拒绝、CU→普通子 Snapshot 只 clone root bytes、成员校验及异长结构后继步骤先扣预算、peer 不加入继承 visited 与短期句柄单次清理。ForkOrigin 始终指向实际 ref/frame，ListForks 仍只验证 header 声明图，tag Related 仍只是注记。旧 S5 Accepted 不覆盖本轮 CU，新增结果见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。

以上支持实施交接；源码、进程中断、平台/规模与包资格仍须实际取证，不要求首版实现分段、checkpoint、业务谱系或精确尝试追踪。
