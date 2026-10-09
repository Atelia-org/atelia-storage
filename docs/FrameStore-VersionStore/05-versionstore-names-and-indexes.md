# S5：ref 历史、不可变 tag 与 branch 名称

状态：**Draft；2026-10-07 采用完整 RootMap、真实 ref 历史、单文件 tag 桶、统一 branch 绑定与命名 fork 的目录共同发布；2026-10-08 同步 S2 固定 12B 地址；2026-10-09 增加来源感知 fork、跨文件完整发布历史与全分叉查询，定稿同步历史 visitor、预算与清理，tag 桶初始化/组合 schema/每次完整扫描，以及 ListForks 完整自有结果/单项数量预算/迭代图校验与清理；其余 API、路径编码及基础 codec 尚未实施/冻结**。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个 ref 的当前/历史值以及 tag 的内容都为应用解释的完整 `{key => FrameAddress}`。ref 有独立稳定 RefId；对外可见 branch 绑定已正式发布的 ref，可以给既有 ref 加 alias，也可以与新 ref 共同发布；tag 冻结创建时的字典，而不是跟踪 ref。两类名称空间独立。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadRefHistory(refId, budget, visitor, cancellationToken) | 从固定上界逐项交付本地及 ForkOrigin 接续的自有快照，可正常选点停止 | 同步 visitor；文件内真实链 + 首帧来源链接，无另写索引 |
| ForkRef(sourceRevision) | 从精确源快照创建匿名 fork | 子 ref 的 header/ForkOrigin 与完整初始 Snapshot，一次目录发布 |
| ListForks(maxRefs, cancellationToken) | 查询全部正式 ref 声明的创建来源边 | checked 首两帧与完整图；单次自有只读结果，无跨调用缓存 |
| CreateTag(name, roots) / ResolveTag(name) | 创建/读取不可变完整字典 | 目标桶的一条完整 tag record |
| CreateBranch(name, refId) / ResolveBranch(name) | 为既有 ref 建立/解析不可变名称绑定 | 该 ref 的 names 中一个 create-only 绑定文件 |
| CreateBranchFromRoots(name, roots) | 原子创建无来源的命名 ref | 私有 RefId 容器内的 ref 与初始绑定，一次目录发布 |
| CreateBranchFromRevision(name, sourceRevision) | 原子创建保留前序历史的命名 fork | 同一容器另有精确 ForkOrigin，仍只一次目录发布 |
| ListBranches | 枚举正式 branch 名称与 RefId | 全部正式 ref 容器的 names 文件集合 |
| 无来源复制 / rewind 便利流程 | 旧字典新建独立 ref / 追加回原 ref | 调用 S4 CreateRef / PublishRef |

历史读取的入口与终止值由 `[A-VS-REF-HISTORY-CHECKED]` 定义，ListForks 的完整结果与限额由 `[A-VS-FORKS-CHECKED]` 定义；其余操作的成功值类型在 Ready 定稿；同步 mutation 统一消费 S4 `[A-VS-PUBLICATION-EVIDENCE]` 的 AteliaResult/必选 out PublicationOutcome，不另立 tag/branch/fork 证据异常或恢复 token。有来源 fork 必须按 revision 创建，只传 roots 的入口表示无来源起点，不凭字典相等推断或补造链接。两个基础操作手工组合仍是两次发布。首版不提供 branch rename/unbind/delete/archive/name reuse、tag 修改/删除、差分或 state-checkpoint。命名创建的共同初始化是窄创建协议，不扩展为多个既有 ref 的事务；本层定义 ref 发布历史与创建分叉，应用数据的因果谱系仍由应用解释。
ref header/普通 Snapshot 的字段、来源判别与完整记录容量唯一消费 S4 `[F-VS-REF-FRAMES]`；历史、fork 和 ListForks 不另选编码。可写恢复前的可变初始帧保护仍属 S4-Q2，不由本层的事后首两帧检查自动成立。
匿名/命名新ref入口统一消费S4 `[S-VS-REF-ID-ALLOCATION]` 的候选与耗尽前检，在data/源flush和私有准备前拒绝发号耗尽；alias/tag不分配新RefId。正式目录发布正常返回后，先Confirmed和编号登记，再安装本层投影/结果；编号不成为分叉来源资格或跨ref全序。
正式ref容器/文件及names根定位消费S4 `[F-VS-REF-PATHS]`；完整容器发现复用其直接项检查，历史按exact来源RefId定位。名称/tag内部文件路径与codec仍由本层定稿，不借路径复用新增全部历史审计或缩减匿名发现范围。

## 表达能力与历史边界

本模型的节点是实际发布快照：同一 ref 内的前一节点由文件顺序隐式表达，只有初始节点的跨文件前序由 ForkOrigin 表达。每个节点最多一个前序，多个新 ref 可以从同一快照分出，因此足以表达线性 head 推进、任意历史选点 fork、fork 的再次 fork，以及全部创建分叉构成的森林；不需要每条 Snapshot 重复保存 Parent。

| 动作 | 本层历史表达 |
| --- | --- |
| 普通发布或重复发布同值字典 | 追加新 revision，前序为本 ref 的前一实际快照 |
| 从源 A2 fork 为 B | B0 保存 A2 的字典，header 指向 A2；B0 是新的创建发布 |
| 源 A 后来继续更新 | 不改变 B 的来源，A2 之后的源更新不进入 B 的继承历史 |
| rewind 到旧字典 | 追加一次新的 head 变更，保留被撤回的更新记录 |
| 业务 merge、编辑命令、撤回目标、状态因果 | RootMap 本身不说明这些意图；应用另行保存相关信息 |

例如 A 的实际序列为 `A0 → A1 → A2 → A3(字典同 A0)`，从 A3 fork 后的发布历史仍包含 A2/A1；不能把它当成只沿业务父状态回到 A0 的因果链。本层也不将一个合并后的 RootMap 自动解释为多父 merge DAG。这里的“完整历史”是当前 ref 的本地发布序列，以及各次 fork 固定选中的前序发布前缀；兄弟分叉的更新与源 fork 点之后的更新另由分叉图发现，不混入这条历史。

### spec [S-VS-FORK-FROM-REVISION] fork 在创建时建立精确来源资格

ForkRef / CreateBranchFromRevision MUST 接收本 VersionStore 的已发布 RefRevision，先消费 S4 `[A-VS-REF-REVISION-VALUE]` 的 default/持久上下文前检，再由库定位正式源 ref、checked header，并从真实 RBF 主链确认 exact 源 SizedPtr；完整读取该 Snapshot 的 CRC、kind/version、身份与 RootMap codec，私有复制其字典。MUST NOT 接受调用方自由组合 roots 与 origin，不接受 header、残尾或仅随机 CRC 读成功的伪成员。源可以是当前或历史快照，也可以位于另一 ref 的继承历史；链接指向返回该快照的实际 RefId，不指向发起枚举的子 ref。

所有生命周期、历史 mutation guard、名称查重及输入/编码/容量检查先完成。随后调用 data ConfirmDurable，再对本次 exact 源 ref 文件调用 DurableFlush，最后在私有子容器写入完整 header/ForkOrigin 与初始 Snapshot，并按 S4 flush/close、目录 rename 发布；命名入口同时准备初始名称。源 flush 为本次 fork 显式确认所引用发布记录的耐久依赖，checked-read 不替代该确认；不新增 receipt，也不重刷所有祖先。该额外屏障明确本次 fork 的确认范围，仍限现有 ProcessCrashOnly 故障模型。

源 flush 失败时，本次 fork 尚未尝试公开事实，为 NotAttempted；源 RBF 按自身 fault，VersionStore 停用，不进入子输出。子私有文件/rename/内存安装的结果继续使用 S4 PublicationOutcome。只在子 header 写来源，不向源文件追加孩子列表、不改变源 head，避免源登记与子发布之间引入第二个一致性协议。

新 ref 保留完整初始 Snapshot，而非 header-only 引用。B0 与源 A2 即使字典相同也都是实际发布节点；历史 MUST 保留二者及其不同 revision，不按 RootMap 同值去重。无来源的 CreateRef / CreateBranchFromRoots 仍可复制字典，但不承诺继承来源历史；tag 也不因字典同值而变成可追溯的 fork 点。

## ref 历史与反事实分支

### spec [A-VS-REF-HISTORY-CHECKED] 同步历史读取交付真实且自有的快照

首版入口如下；这是待实施合同，不是已存在的 API：

```csharp
public readonly record struct HistoryBudget(long MaxSnapshots, long MaxWorkSteps);
public enum HistoryEnd { Complete, VisitorStopped }

AteliaResult<HistoryEnd> ReadRefHistory(
    RefId refId, HistoryBudget budget, Func<RefSnapshot, bool> visitor,
    CancellationToken cancellationToken = default);
```

RefSnapshot 复用 S4 `[A-VS-ROOTS-OWNED]` 的普通自有结果类与集合公面，只读 Revision 与 Roots；不另立 history 专用快照、结果 Lease 或 Dispose。每项 MUST 完整校验 CRC、kind/version、身份与 RootMap codec，确认真实主链成员后才能交付。解码到自有值并释放临时 RbfPooledFrame 后再回调，不能外泄 Reader、RbfFrameInfo、pool Span 或依赖 reader 的字典。结果的 RootMap 不提供公开修改入口；方法结束、owner fault/Dispose 不撤销已交付值，内部初始字典比较不受 visitor 改写。RefId 消费 S4 `[F-VS-REF-ID-8B]` 的公开上下文值、前检与内部字段 codec；RefRevision 消费 `[A-VS-REF-REVISION-VALUE]` 的自有值、完整等值和只读实际位置，RootMap wire/限额消费 `[F-VS-ROOTMAP-BPV1]`，不开放任意裸 revision 的随机 ReadRevision。

visitor 返回 true 表示继续，false 表示已接收当前项并正常选点停止。false 后 MUST 不再扫描旧帧或访问下一源；先完成健康/取消复检和必要清理，再返回 VisitorStopped，即使该项恰为起点也保持此结果。调用方可保存所选值，在方法返回后 fork/tag/rewind；需要列表时由调用方收集，不要求库保留整批历史。

**准入与固定上界。** MUST 先检查 owner disposed/fault、RefId 参数、非 null visitor、两个预算均为正数及无其他活动历史读取，再做文件 I/O。无隐含预算默认值；long.MaxValue 可表示调用方不设置实际限额，default 的零预算无效。通过准入后设置一个 HistoryActive 位，整个同步调用拒绝递归 ReadRefHistory 及本 VersionStore 的所有 mutation，包括发布、匿名/命名 fork、alias 和 tag 创建；mutation 按 S4 先初始化 out，guard 拒绝先于 barrier/输出且不新增 fault。visitor 可串行读取当前值及 data，保留自有结果；同文件查询复用当前可用的 RBF 句柄，不重复独占打开。owner Dispose 仍是受控清理，不作为发布 mutation 拒绝。首版无并发调用或外部改写，不建跨实例 snapshot 协议。
进入本 ref 时 MUST 用公共正向扫描完整检查首 header 与物理第二帧初始 Snapshot，保存它们的 exact ticket、checked ForkOrigin 与自有初始字典。随后从实际完成尾部取得逆向扫描器，完整校验紧贴 EOF 的末 Snapshot，取得固定上界 revision，先交付它，再依次向前；初始即末帧时可复用本次已校验的字典。后续不得重读 head 替换这个上界，不能调用未计费的 ReadRef 助手完成隐含资格工作。

**继承前缀。** 正反向枚举均使用 `showTombstone: true`。本地历史包含重复同值快照与 rewind，只省略已检查的首 header；必要节点的未知 kind/version、tombstone、损坏均报错。首版逐文件循环，直接消费 public RBF API：

1. 进入每个 ref 前加入路径 visited，重复 RefId 为循环错误。逆链到达该文件的 exact 初始 Snapshot 后，无 ForkOrigin 才是合法历史起点；有来源则保留当前初始字典与来源，放下局部扫描器并归还当前临时句柄，再进入源 ref。既有 owner 句柄不因跳转而关闭。
2. 只定位正式 SourceRefId，按 S4 访问模式打开/复用文件并检查身份、首 header 与第二帧；不采用 creating。调用 `ScanReverse(showTombstone: true)` 从源实际完成尾部定位 exact SourceSnapshotTicket，同时匹配 offset/length；随机完整读取不是主链成员证明。
3. 对源 fork 点之后的无关后缀只消费 framing/TrailerCRC，不读其 payload/RootMap codec。已观察到的结构错误、TerminationError、I/O 或 fault 仍传播；到 checked 初始边界仍未找到 exact 来源是非法来源，不是无来源起点。
4. 找到后完整读取源 Snapshot，按 Ordinal key/FrameAddress 值比较它与子初始字典；不一致报错，不能只比较编码顺序或 CRC。交付源快照自己的 RefRevision，继续用该逆向扫描器读更早前缀；源后续 head 不进入历史，子初始与源同值也都保留。

每个实际 MoveNext=false MUST 检查 TerminationError；非 null 传播底层失败，尚未到已 checked 初始边界的正常 false 也是非法序列/来源，不能伪装 Complete。无来源的 exact 初始 Snapshot 已完整校验，且其 visitor 返回 true 时，首两帧资格已证明没有更早快照，可直接 Complete，不必额外推进到 header/EOF。Complete 只证明本次继承发布前缀，既不审计被排除的源后缀 payload，也不证明应用图健康。

**预算与停止。** 两个上限只约束本次遍历，不持久化，不增加配置、cursor 或分页协议：

| 预算 | 计费与拒绝边界 |
| --- | --- |
| MaxSnapshots | 每次交付 checked 自有快照计一项；交付上限用完且起点尚未证成，继续遍历前即失败，不为探测下一项执行额外 I/O |
| MaxWorkSteps | 每次进入一个 ref、每次 forward/reverse MoveNext 尝试、每次完整帧读取各计一步；包括首两帧资格、无输出的源后缀定位、重复实际读取及返回 false 的尝试。调用前检查并扣费，内部 helper 同样计费，不复制 RBF 帧长度/CRC 算法 |

额度不足返回错误码 `VersionStore.HistoryBudgetExceeded` 的失败 Result，不返回正常 BudgetExhausted/partial-success 或公开 usage 摘要。先复检 owner/fault/token，再判继续工作的额度；visitor false 优先正常停止，visitor true 且真实无来源起点已经证成则 Complete，返回数恰好用完不阻碍该结果。尚需资格检查才能证明起点时，所需步骤也必须有额度；禁止越额探测或根据数量猜祖先不存在。已交付前缀仍可保存，但预算失败、正常早停都不证明未访问历史完整。
工作步是算法入口次数，不是物理 I/O bytes、耗时、单帧大小或 RSS 上界；工厂 Open 的内部结构检查/恢复、缓存预读和一次同步读/CRC/解码不能被这些计数拆分或抢占。visitor 自己的查询/data 读取及结果积累另计。进入 ref 的一步约束访问 ref 数；文件打开前后、每个扫描/完整读和回调之间检查 cancellationToken。完整读取前先以已取得的 RbfFrameInfo 检查 S4 该版本的记录尺寸上限，不先租用超限全帧再拒绝；解码仍消费 S4 字段/容量检查，不能为满足工作预算取消它们。每查询全帧请求字节额度、硬内存/时限等独立预算留待真实大 RootMap 成本或调用方限额需求出现后设计。

**清理与失败。** raw sequence/枚举器仅在方法栈内，跨文件不预收集 ticket 列表；RBF 枚举器本身无 Dispose，不引入外泄 sequence、owner epoch 或扫描 Lease。除既有 owner 文件外，遍历只保留一个当前临时 RBF 句柄、当前完整帧/必要自有字典和 O(已访问 ref 数) visited；visitor 自行保留的结果另计。临时文件按 S4 的只读/可写恢复规则打开，本调用不另发布、flush 或清理私有创建残留。
当前临时句柄登记在 owner 可清理槽位，连同 RefId 可供同文件查询复用；正常跳转、所有退出及 owner Dispose 都 MUST 在调用 public Dispose 前先取出并清空槽位，关闭抛错也不重试。owner Dispose 先失效实例，尝试当前临时及其他 owned 资源，不 Dispose 借入 data；方法 finally 只清理仍在槽位的句柄，所有退出都清除 HistoryActive。既有 owned 文件继续遵守自身生命周期，不把借用句柄当临时句柄归还。
每次 visitor 返回后、继续 I/O 前、必要关闭后及最终正常返回前 MUST 复检 owner disposed/fault 与 token；最后回调 Dispose owner、取消或抛异常，最后关闭失败，都不能返回 Complete/VisitorStopped。取消抛 OperationCanceledException，visitor/I/O 等异常原样传播；必要 CRC/codec/身份/成员错误返回所属层失败，不跳过坏记录、不回退。预算失败、普通读错误、纯读异常、取消或 visitor 异常本身不 fault 健康 writer；实际 owned 清理/恢复失败按 S4 停用规则，主异常优先并继续尝试清理，清理失败不能被正常停止遮蔽。

现有 RBF **没有从旧 ticket 开始的 ScanReverse 重载**；不使用 internal 文件视图或手写 Fence 算法。源后缀定位随源后续更新增长，必须支付上述工作步；同步 visitor 保留按需选点，不承诺恢复 cursor。依据为当前 [RBF 栈内逆扫](../../src/Rbf/RbfReverseEnumerator.cs)、[payload 损坏仍可结构扫描的测试](../../tests/Rbf.Tests/Internal/RbfScanReverseTests.cs)，以及旧消费者的[按需选择测试](../../../durable-graph/tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs)；旧消费者只提供工作流证据，不引入旧栈 API/运行时依赖，这些证据也不是 VersionStore 实施验收。

（Informative）A 已有 A0/A1/A2/A3，B 从 A2 创建并更新 B1，C 从 B1 创建后更新 C1：`ReadRefHistory(C)` 返回 `C1, C0, B1, B0, A2, A1, A0`；A3 不出现，C0/B1 与 B0/A2 的字典分别相同但 revision 不同。

（Informative）Gym 在 visitor 中选中旧快照 `R` 并返回 false，待 ReadRefHistory 返回 VisitorStopped 后，再调用 `ForkRef(R.Revision)` 或 `CreateBranchFromRevision(name, R.Revision)` 保留来源；只要独立起点时用 roots 入口。rewind 调用 `PublishRef(oldRefId, R.Roots)` 追加新 revision，保留原发布历史。data 根帧共享，不复制其图；tick、RNG、业务父状态与推演规则继续由应用保存。

## 全部分叉查询

### spec [A-VS-FORKS-CHECKED] 正式 ref 的首帧声明完整分叉图

首版入口如下；这是待实施合同，不是已存在的 API：

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

**完整集合与必要资格。** ListForks MUST 覆盖全部正式 RefId 容器，包括未命名与无来源 ref；不依赖 names、branch aliases 或当前 head 值。直接消费 S4 `[F-VS-REF-PATHS]` 的全部直接项发现/期望目标定位，以及 `[F-VS-REF-FRAMES]` 的共同首两帧形状、物理成员、完整 CRC/身份/codec 检查；不是用首帧 framing 或随机 CRC 成功代替。creating 不参与，正式缺文件/header/初始 Snapshot、未知项、枚举或必要读取错误皆报错；只有已资格化且正常结束的空正式集合才可当空，不能用 File.Exists=false 或过滤枚举猜缺失。逐 ref 及时释放 pooled 帧和初始字典，仅保留身份与可选来源元数据；不调用会额外检查末 head 的 ReadRef，不收集全部 RootMap、扫描全部本地历史或重读所有 source ticket。

**单一数量预算。** 在任何发现 I/O 前 MUST 检查 owner disposed/fault、`maxRefs > 0` 与取消；没有隐含默认限额，不按 maxRefs 预分配。每个正式 ref 在本次打开/首两帧检查及加入节点表前计一项，含无来源根，不能只计输出边。达到上限后仍可推进直接项枚举并按 S4 检查该项名称/类型，以证明实际 EOF；若还有正式容器，在其 ref 文件打开/内容读取或节点表增长前返回错误码 `VersionStore.ForkQueryBudgetExceeded` 的失败 Result。不以额度用完猜 EOF，不探测超额 ref 的内容；未知项/枚举或必要清理错误仍走原错误通道。恰有 maxRefs 个 ref 且正常 EOF 可以成功；int.MaxValue 合法，只表示不设置更小逻辑限额，不保证实际集合分配成功。
数量上限约束节点、结果项及下述图算法的访问规模，不是目录枚举内部工作、RBF factory 尾部资格/恢复、同步单帧读/CRC/解码、物理 I/O bytes、耗时或 RSS 硬上限。记录尺寸/prerent 与 RootMap 容量继续消费 S4；取消在每个直接项、文件访问/关闭前后及图行走步骤之间检查，不承诺抢占底层同步调用。无独立 MaxForks/MaxWorkSteps/bytes 计数、预算载体、usage、配置或分页协议。

**一个临时图。** 扫描与构图属于同一次 single-driver 操作，不允许 mutation 交错。用一个本次调用的 RefId 节点表保存所有 checked 正式身份、可选来源及检查状态；完整目录正常结束并必要关闭成功后，逐来源核对 SourceRefId 属于同一已 checked 集合，拒绝自链接。无来源根仍在表中，不作为缺源；源 header 已在本次扫描校验，不再逐边打开。全图环检查 MUST 使用迭代三色或等价的已完成节点记忆，每节点仅作常数次图访问；禁止递归深链或从每个节点重新追完整祖先链，也不以 RefId 大小替代来源图检查。不排序，不引入持久 generation、拓扑编号或通用图框架。

**仅交付完整自有边集合。** 全部资格、图校验、必要清理、结果构造及最终 owner/fault/token 复检通过后，才返回非 null 自有列表；无来源 ref 不输出，有来源的每个 child 恰输出一项，零边成功为空列表。任一失败或取消不交付部分成功、visitor 前缀、正常 BudgetExhausted 或“没有孩子”的结论。输出顺序没有跨调用稳定、编号排序或时间顺序承诺。
ForkInfo 是普通非 record readonly struct，仅有三个只读属性及内部构造，无 setter/init、带参 public 构造、Deconstruct、专用整体等值/hash/codec、owner/reader 或 Dispose。它平铺逻辑 ForkOrigin，避免再公开嵌套来源类型；SourceSnapshotTicket 保留完整 SizedPtr（含 length），MUST NOT 包装成 checked 源 RefRevision。default 属性可读取，但不是库成功列表中的有效边，不新增 IsValid/default 错误族。结果仅声明本次已 checked 创建关系，不定位每条源 ticket、不校验源快照与子初始字典同值，不证明全部历史/data 图健康或旧调用曾 Confirmed；内部构造也不是防伪认证。
列表内部使用唯一自有数组和只实现 IReadOnlyList/IEnumerable 的私有薄封装；不外露可变 array/List、IList 或能取得 backing 的 ICollection.SyncRoot，不复用下一调用 scratch 或长期缓存。ForkInfo 内的 RefId/SizedPtr 都是自有值，无需复制业务对象。查询结束、后续查询/创建及 owner fault/Dispose 不改变已交付列表；反射、Unsafe 等越过正常接口的改写不在此保证内。

**句柄与失败。** 本查询无应用 callback，按既有串行前提执行，不新增 ListForksActive/busy 或改写 HistoryActive。历史 visitor 可调用它；匹配 RefId 时复用可用的既有 owner/历史当前句柄，不重复独占打开、不关闭借用句柄。其他 ref 逐个使用本方法局部临时句柄，绝不覆盖历史当前临时槽；关闭前取出并清空局部槽，finally 只清理仍登记的资源，每个句柄仅尝试一次。目录枚举器、当前 pooled 帧和全部临时句柄的正常/异常退出，消费 S4 的 owned 清理/主错误优先规则；最后关闭失败不能被空列表或预算错误掩盖。
取消抛 OperationCanceledException；必要格式/CRC/codec/身份/图/预算失败返回所属失败，I/O/运行期异常原样传播，不跳过坏 ref。纯读取、预算、取消或结果分配失败本身不新增健康 writer fault；实际 owned 恢复/清理失败按 S4 停用并受控清理，不 Dispose 借入 data。访问模式及可写打开的恢复直接消费 S4，本查询不另行 flush、发布或清理 private；S4 恢复前初始化保护、实际组件/根准入及平台资格仍待定，本条不把事后首两帧校验称为这些缺口已关闭。

该边集合是唯一来源的派生视图：按 SourceRefId 可找直接分叉，按 exact source ticket 可区分同一 ref 的不同分叉点，对 child/source 关系求传递闭包可找所有后代；不隐含跨 ref 时间全序。已有 RefSnapshot 可用 Revision.RefId/Ticket 与声明位置比较，关联 exact 点的分叉；匹配不签发源 revision 或证明源内容健康。alias 不增加边，名称展示另行查询；应用可从结果派生反向表。
首版每次 ListForks 都执行上述完整 checked 扫描，不保留跨调用反向表、负缓存或图安装/更新相位，不写持久孩子列表、全局 catalog 或索引。实际重复查询成本超出目标后，再研究内存表及其完整建立/更新资格；名称层已有的可选投影不受本条重选。

（Informative）int 参数与完整物化列表/节点索引的 Count 域相符，是没有超出该域消费者时的工程选择，long 也可正确。readonly 结果与私有薄封装提供稳定保留视图；独立可变副本或标准 immutable 集合也可正确，不因此建立公共集合框架或安全凭证。每 ref 的本层读取固定为首两帧，图行走是线性节点访问；哈希表定位按通常均摊成本估算，不承诺碰撞下的硬 CPU 界。公开 [RBF 正扫](../../src/Rbf/RbfForwardEnumerator.cs)与[payload 坏仍可结构扫描的测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)证明 framing 不替代必要完整读取；兄弟仓的[私有只读列表封装](../../../durable-graph/src/DurableGraph.Storage/StateRevision.cs)只提供窄所有权实现参考，不引入依赖或兼容。没有新栈实际消费者或性能测量证明额外 work/byte 限额、排序、缓存及通用图 API 必要；本条不是实现、平台或包资格。

## 不可变 tag 与固定分桶

### spec [S-VS-TAG-ROOTS-IMMUTABLE] tag 一次冻结完整根字典

每个 tag MUST 保存 `TagName + 完整 RootMap` 的一条 RBF3 record，使用 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` 的私有拷贝、输入/容量 guard、data ConfirmDurable、Append 与 DurableFlush 协议；无关 data Builder 尚未归还不阻断 tag 创建，所需依赖仍必须由应用保证完成。tag 从某 ref/历史快照创建时复制当时字典，不持有可变 ref 间接绑定。同名再创建 MUST 在输出前拒绝，即使字典同值。
tag 内容直接复用 S4 RootMap codec，包括其消费的 S2 固定 12B FrameAddress；名称 codec 属于本层，RefId 字段消费 S4，RefRevision 不另立整体 codec，均不从地址固定 12B 推导宽度。tag 不保存 RefRevision 或精确 ref 历史来源。
TagName 的比较采用明确稳定的名称政策；默认候选为 Ordinal。路由 MUST 使用固定、跨进程复现的字符串 hash 与固定桶规则，不使用进程随机化的 string.GetHashCode；名称政策下比较相等的两个名字 MUST 路由同一桶，不能因原始大小写或同值 codeword 表示不同而漏掉同名。记录保留完整原名；hash 相同但名称不同不是同名，MUST 按完整名字比较。
首版每桶一个 RBF3 文件、不轮转。以下是已选的 tag 局部合同；统一版本与 16B canonical VersionStoreId 直接消费 S4 `[F-VS-OWN-FORMAT]`，RootMap wire/1MiB codeword 上限消费 `[F-VS-ROOTMAP-BPV1]`；名称编码/限额、固定 hash/桶数量/规范路径仍消费 S5-Q1，不能据此宣称其余依赖已经冻结。桶 header 使用同一身份编码，不另选宽度、不从 CLR struct 大小或被检查文件反推。

**单一格式与组合 schema。** 两种 RBF FrameTag 在 tag 桶文件中固定为 0（BucketHeader）、1（TagRecord）；所有帧 MUST 非 tombstone 且 TailMetaLength=0。RBF tag 已表达 kind，不在 payload 重复保存 kind：

| 帧 | decoded payload，依次编码 | 资格 |
| --- | --- | --- |
| 首物理帧 BucketHeader | uint32 LE VersionStoreFormatVersion；S4 canonical VersionStoreId；uint32 LE BucketId | 版本与格式门一致，身份与已 checked 格式门逐字节一致，桶号合法且等于名称路由/规范路径要求的期望值 |
| 后续 TagRecord | S5-Q1 的完整 TagName codeword；S4 的完整 RootMap codeword | 名称有效且重新路由到本桶；RootMap 使用同一 key/地址/容量检查，完整消费 payload，不接受截短或 trailing bytes |

header 的统一格式版本选择整个桶的名称与 RootMap 组合 schema，不另立 HeaderSchemaVersion/TagRecordVersion；原样消费 S4 codeword，不增 tag 专用 RootMap 版本或容量规则。未知格式版本、未知 FrameTag、后续 header 或非法组合均报错；首版不在同一桶混合 schema，未来升级需另定合同。无额外 magic、内容 CRC、预留字段、每记录 StoreId/BucketId 或 tag revision；RBF 完整 CRC 覆盖内容和 kind。

**初始化边界与访问。** 以格式门版本、期望 VersionStoreId/BucketId 编码该版本的 canonical header，得到本次期望 header 的确定 payload 长度 L；令 `H = RbfFile.MeasureWriteSize(L, 0)`、`I = RbfScanBoundary.Empty.EndExclusive + H.AppendLength`。共同首帧检查 MUST 要求 Rbf3，使用 `ScanForward(showTombstone: true)` 取得首物理帧，先核对 offset、H.FrameLength、payload L、kind/meta/tombstone，再完整读取 CRC、精确解码及比对期望身份；header 的 checked 后界 MUST 为 I。没有首帧时先检查 TerminationError，再报缺 header，不寻找后续替代帧。
可写访问正式桶 MUST 在 public RbfFile.OpenExisting 前用公共文件长度入口拒绝实际长度 < I，先关闭长度探测句柄再进入独占 RBF 工厂；非 None 恢复必须具有 `AffectedFrameOffset >= I`。随后执行共同首帧检查，只允许 header 后的 tag 残尾恢复，不补造/认领 header。此顺序消费 S2 `[F-FS-META-FIRST]` 的初始化保护模式：header body 完整但缺 Key/Fence 时，不能让 CompletedTail 修好后在下次 Action=None 被接纳。长度足够但身份/CRC 错的 header 仍拒绝，不承诺底层 tag 尾恢复前完全不修改坏文件。只读访问使用 public OpenReadOnlyExisting，残尾拒绝、不修复。访问按需进行，不要求 owner Open 扫描全部桶。

**每次完整扫描。** ResolveTag 与 CreateTag 查重复用一个内部同步扫描流程，不保留跨调用 name→ticket/RootMap 表或负缓存，不外泄 raw sequence/info/reader，也不增加 visitor/预算/缓存 API：

1. 在文件 I/O 前检查 owner disposed/fault、名字及操作模式；CreateTag 另检查 S4 历史 mutation guard。只探测名字的正式规范桶路径，不采用私有初始化文件。确认正式桶确实不存在可判缺失；权限、I/O、目录/格式错误不得经 File.Exists=false 吞成 missing。合法 header-only 桶为空，裸 RBF Fence、坏 header 不是空桶。
2. 在同一次串行操作内沿 `ScanForward(showTombstone: true)` 检查真实链，首帧执行上述资格，之后每条 TagRecord 都完整读取 CRC、名称/路由与 S4 RootMap codec。全帧读取前用 RbfFrameInfo 检查该版本的完整 tag 记录尺寸上限；上限由 S5-Q1 名称和 S4 RootMap 编码上限 checked 组合并受 RBF 公共容量约束，不先租超限全帧再拒绝。非目标记录的坏 RootMap 也报错，不能仅检查名字或 framing。
3. 临时 fullname 集合按名称比较政策检测全部同名记录，字典相同也冲突；hash 碰撞而完整名字不同可以共存。逐帧释放 RbfPooledFrame，只保留命中的自有 RootMap，不收集全部 RootMap 或 ticket。命中后继续扫描，不以 first-match 胜出。实际 MoveNext=false 必须检查 TerminationError，错误不当正常 EOF。
4. 完整正常结束及必要关闭成功后才能交付命中或健康缺失；ResolveTag 返回不依赖 owner/reader、无公开修改入口的 S4 RootMap，不新增 TagSnapshot/Lease/Dispose。命中值已经在本次扫描完整校验，无需同调用再读一次。缺失返回 TagNotFound，不借同名 branch/ref 兜底、不初始化桶。CreateTag 用同一完整扫描判同名拒绝或允许追加；这省去特殊早拒分支，并非已有 checked 同名记录仍不足以安全拒绝。

每调用只管理一个本次桶句柄、当前临时完整帧/RootMap、所选自有值和 O(桶名称数) 临时 fullname 集合；集合随调用结束释放。临时句柄由方法局部 finally 按 S4 清理规则在 Dispose 前取出并置 null，所有退出只尝试归还一次，保留主异常。ResolveTag 不执行应用 callback；从历史 visitor 调用时不能覆盖/清空历史的当前临时 ref 槽位。普通读/codec 错误不自动 fault 健康 writer，实际恢复/owned 清理失败依 S4 停用；必要关闭失败不能返回查询成功或健康 TagNotFound。最终交付前复检 owner disposed/fault。该资格只覆盖本次目标桶，不证明其他桶或引用 data 图健康。

**创建与三态。** 在同 owner/driver 串行准入下，先最早初始化 out NotAttempted，完成名字、重名扫描、RootMap 私有复制/编码与容量检查；确定拒绝先于 data barrier、私有初始化及 tag 输出。缺桶时预检完整 header 与首 tag 的尺寸、从 I 开始的追加起点；已有桶使用 checked 实际完成尾部，从查重至 Append 复用本次同一个 Idle 句柄。下一条起点超过 SizedPtr.MaxOffset 明确拒绝，不隐式分段；合法末记录可以越过起点上界。
随后按 S4 调用 data ConfirmDurable。缺桶时仅在私有文件写 header、执行共同首帧检查并确认 TailOffset=I，再 DurableFlush/close、同文件系统 no-overwrite rename 公开合法空桶；本次 tag 仍 NotAttempted。打开并验证该正式空桶后，首条 tag 与后续 tag 均走普通正式 Append/flush：Append 调用前 Unknown，Append 成功且 DurableFlush 正常返回后立即 Confirmed，再必要关闭/交付成功值。公开 Append 异常不猜零输出，pre-I/O 失败 Result 的唯一降档例外消费 S4。空桶 rename 异常且尚未尝试 tag Append 时保持 NotAttempted，但停用 owner；flush 成功后的关闭/交付异常保留 Confirmed。无 tag 名称缓存安装相位。
合法正式空桶保留，不删除、不自动补首 tag；私有残留不成为 tag、不自动续作，只读不清理。header 后未完成 tag body 由可写 RBF 截回其真实边界，完整 body 缺原 Key/Fence 可按 CompletedTail 成为完整 tag，仍不证明旧调用曾 Confirmed。恢复后必须重新取得全桶资格；完整坏记录不跳过、不回退、不改绑。

（Informative）本合同直接消费 [RBF 公共正扫与 CRC 分层](../Rbf/rbf-interface.md)、[payload 损坏仍能结构正扫的测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)；旧 [重复/坏 tag 测试](../../tests/EventJournal.Tests/ImmutableTagTests.cs)和[书签冷重开后继续分叉的消费者](../../../durable-graph/tests/DurableGraph.Persistence.Tests/RepositoryTagTests.cs)只提供独立工作流证据，不引入旧栈 API/格式兼容，也不构成 VersionStore 实施验收。

## branch 绑定与命名 fork

### spec [S-VS-REF-ID-STABLE] 名称与稳定 ref 身份分离

对外可见 branch 名称 MUST 绑定已正式发布的 stable RefId，不把名称本身当作快照 revision。CreateBranch 只命名既有 ref；命名 fork 使新 ref 与初始名称共同达到公开资格。解析后的更新使用 exact RefId；多个 branch 可命名同一 ref，不为此复制 ref/data。RefId 不因 RootMap 同值而合并，也不随 fork 复用源 ref 身份。
首版创建后名称绑定不再改变；不存在同名删除再建的身份重用。空 RootMap ref 有效，missing ref 不被隐式创建；损坏 ref 不被当 missing。绑定目标 ref 缺失/身份不符时报错，不按名称选择其他对象。

### spec [A-VS-BRANCH-NAMES-GLOBAL] branch 名称在全部正式 ref 中唯一

所有 branch 绑定 MUST 采用同一种 checked 文件表示，放在 S4 正式 `refs/<RefId>/names/` 中；初始绑定与后加 alias 不使用不同的格式或发现来源。记录至少包含 version/kind、VersionStoreId、完整 BranchName 和 exact RefId，且 RefId 必须与所属容器及目标 ref 身份一致。
名称比较、路径安全编码/hash 在 Ready 定稿；默认候选为 Ordinal，tag 与 branch 名称空间仍独立。hash 碰撞必须完整名字比较，不能覆盖另一名称；非法路径字符、保留名、大小写差异不得被 OS 路径语义悄悄转换成错误名称绑定。
ResolveBranch / ListBranches 与所有名称创建的查重 MUST 覆盖全部正式 ref 的 names，或使用由该完整集合建立的可重建内存名称表；creating 内容不参与。查重不能只探测目标 ref 的局部 names。同名再次创建必须在 data barrier、私有输出之前确定拒绝，即使目标 RefId 或 RootMap 同值。
扫描 MUST checked-read 绑定、核对完整名字/所属身份并正常结束；目录枚举、I/O、CRC、未知版本/codec 错误或跨容器重复 fullname 必须报错，不能当未找到、先找到者获胜或成功的部分列表。完整扫描及正常结束之前不得安装可用于证明名称缺失的内存表；命中查询也不能跳过其所依赖的全局唯一性资格。目标 ref 缺失/身份不符仍按 `[S-VS-REF-ID-STABLE]` 报错，不回退到另一个对象；名称发现不要求扫描 ref 的全部 Snapshot 历史。
名称创建全程 MUST 持有 S4 单 writer/driver 准入且不允许操作交错，包括全局查重、私有准备及最终发布；成功发布后更新相同 owner 的派生表，安装失败仍遵循 Confirmed/停用规则。局部 no-overwrite 只保护具体目标路径，不能单独保证不同 RefId 目录间的名称唯一性。只读查询不修复名称，不采用私有准备；首版不承诺跨实例实时可见或多 writer 竞争。

### spec [S-VS-BRANCH-BIND-CREATE-ONLY] 给既有 ref 添加不可变名称绑定

CreateBranch MUST 先验证名字、目标已发布 ref 与 `[A-VS-BRANCH-NAMES-GLOBAL]` 的全局重名准入，将完整记录写入正式 names 之外的私有文件，flush/close 后同文件系统 no-overwrite rename 到目标 ref 的 names 路径；调用 rename 前写回 Unknown，rename 正常返回后立即写回 Confirmed，然后才安装内存投影或交付成功值。名称层不得在正式 names 中留下半写文件。失败/不确定结果使用 S4 PublicationOutcome 与停用/重开规则，不在私有 flush 后宣称名称已建立。
该 create-only 操作遵守 S4 owner 模式/生命周期及历史 mutation guard，但无需再次遍历/flush data 图，也不因无关 data Builder 尚未完成而拒绝：它只命名已经存在的 ref，不发布新的 RootMap。新 alias 失败不得删除、移动或重建既有 ref，也不得撤销其已有名称；只清理可证明私有的准备文件。
`CreateRef(roots)` 与 `CreateBranch(name, refId)` 手工调用仍是两个独立步骤，MUST NOT 宣称跨操作原子；第二步失败可留下第一步已正式成立且 ListRefs 可发现的 ref。需要共同公开时 MUST 使用下面的组合协议，不追加 allocation 日志或回滚已发布 ref。

### spec [S-VS-NAMED-FORK-PUBLISH] 命名 fork 由一个目录共同发布

本条消费 S0 `[S-VS-NAMED-FORK-ATOMIC]` 与 S4 `[S-VS-REF-DIRECTORY-PUBLISH]`。组合入口 MUST 保证：**命名 fork 成功发布时，新 ref 与 branch 一起可见；发布前，普通查询两者都不可见。失败可以留下私有准备文件，但不会留下公开的未绑定 ref。**
实现 MUST 按以下步骤执行，不以两个公开 API 的串接代替：

1. 在同一串行 owner 准入下，完成生命周期/模式/历史 mutation guard、全局名称唯一性、RootMap 私有拷贝与编码，以及 ref 和绑定的容量检查；来源感知入口先按 `[S-VS-FORK-FROM-REVISION]` 确认源成员并重读字典。确定拒绝不输出、不调用 data barrier。
2. 按 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` 调用 data ConfirmDurable；来源感知入口随后确认源 ref 文件耐久。应用保证本根新增依赖已完成，无关 Builder 可继续构建，失败不进入本次子输出。
3. 在同一个 S4 私有容器内准备完整 header、初始 Snapshot 和 names 中的一份完整初始绑定；来源感知入口写入 ForkOrigin，roots 入口的来源为空。采用与 alias 完全相同的绑定 codec。所有本次写入文件 flush/close，仍不向调用方交付已发布 RefId/revision。
4. 消费 S4 的一次同文件系统 no-overwrite 目录 rename，将整个容器发布到正式 refs。此步骤同时建立 ref 和初始名称的发现边界；不得先公开 ref、先正式占名、追加最终 Bind，或发布后再 promote 文件。
5. 按 S4 PublicationOutcome 先记录 Confirmed，再安装内存名称/ref 投影并正常返回新 RefId 与初始 revision；后续内存失败不撤销或降级发布证据。

最终目录 rename 前失败，本次业务结果为 NotAttempted；开始 rename 后无法证明结果为 Unknown，停用并重开按实际正式容器裁决，不盲重试。正常发布或重开接受后，ResolveBranch 所得 RefId 与 ListRefs/ReadRef 定位的是同一完整对象。私有残留不占公开名称，不自动续建；只读不采用/清理它，清理不删除正式目录。完整坏记录必须报错，不能借此把已发布对象回退为不存在。
匿名 fork 用 ForkRef，命名 fork 用 CreateBranchFromRevision；CreateRef / CreateBranchFromRoots 则创建无来源起点。它们共享 data 帧，不改变源 head、不复制业务图，也不添加每快照 Parent。给既有 ref 的多个 alias 继续使用 `[S-VS-BRANCH-BIND-CREATE-ONLY]`，不会另开 ref 历史或新增来源边。原子性只覆盖一个新容器及其初始名称，不提供跨既有 ref/tag 的通用事务或多个命名 fork 的整批原子性。

## 查询、启动与成本

### spec [A-VS-ROOTS-QUERY-THEN-READ] 查询根字典后由应用接受内容

ReadRef/ResolveTag MUST 返回 checked 发布记录里的 RootMap，ReadRef 同时返回实际 revision；名称解析返回 stable RefId。发布记录健康不证明引用帧或业务图健康。应用按这些地址调用 data FrameStore.ReadFrame 完整读取和解码；缺/坏必要数据报错，不改 RootMap、回退 ref 或重绑定 tag。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

Open MUST NOT 默认回放全部 ref 历史或扫描 data 图，也不因此宣称全库 O(1)。目录/格式门、名称及文件枚举的实际成本须报告；惰性打开只把检查延后到目标访问，不把未检查对象说成健康。
当前 ReadRef 成本为本地 header、初始 Snapshot 与完整末 Snapshot bytes（初始即末帧时复用），不随历史帧数或祖先深度增长。ReadRefHistory 成本含返回快照的完整读取、跨文件 header/初始边界和定位源 fork 点所跳过的后缀 framing，不能只按返回条数估算；预算未覆盖的历史不计作已验证。按 revision fork 的来源定位也可能扫描源后缀，并增加一次源文件 flush。tag 每次查找/重名检查均为 O(目标桶记录数 + checked bytes)，临时名称集合为 O(该桶名称数)，完整字典只保留当前/所选值；连续创建同桶 n 个 tag 可能累计 O(n²) 次记录检查。不承诺分桶即可 O(1) 查询或长期无限容量。出现实际桶规模、重复解析/批量创建成本不可接受的证据后再设计跨调用内存位置表及其资格/更新规则，磁盘索引仍为独立后续片。
ListForks 每次执行完整 checked 扫描：令 N 为正式 ref 数、F 为有来源 ref 数，本层预期成本为 O(N + F + header/初始 Snapshot checked bytes)，含全部源存在和迭代环检查；哈希定位按通常成本估算。临时元数据 O(N)、自有结果 O(F)，另仅保留当前必要字典/pooled 帧，不保存全部初始 RootMap。数量参数包括无来源根；底座 factory 的尾部资格/恢复、实际 metadata/I/O 与内存成本另行报告，不能将 maxRefs 当硬时限/RSS 或历史规模无关的工厂保证。该查询与 names 数独立，不因只有少数命名 branch 而跳过匿名 ref；实际重复扫描超出目标后再研究内存反向表。
ListRefs 成本与正式 RefId 目录数相关；branch 首次解析/全局查重/ListBranches 需要遍历正式 ref 目录及 names 记录，成本为 O(ref 目录数 + binding 数 + 所需 checked bytes)，不扫描全部 Snapshot 历史。即使大量 ref 未命名、只有少数 branch，也可能付出全部目录发现成本；不能按名字编码直接访问一个全局正式路径而宣称 O(1)。
实现 MAY 惰性建立唯一的可重建内存 name→RefId/绑定位置表；其内存为 O(binding 数)，构建须消费 `[A-VS-BRANCH-NAMES-GLOBAL]` 的完整资格，后续定位仍需所选记录/目标的检查。名称解析后可保留 stable RefId，直接 ReadRef 不依赖该表或任何初生名称。目录/名称、句柄缓存及枚举生命周期的实际成本须报告，不增加持久名称索引、control 全量投影、全部尝试账本或磁盘 catalog/checkpoint。

## 工程 codec 与后续扩展

| 单元 | 最小字段 | Ready 选择 |
| --- | --- | --- |
| tag 桶 header | FrameTag=0；统一格式版本、S4 VersionStoreId、uint32 LE BucketId | 组合 schema、初始化 I 保护已定；版本/16B 身份消费 S4 `[F-VS-OWN-FORMAT]`，桶数量/hash/路径仍 S5-Q1 |
| tag record | FrameTag=1；完整 TagName、原样 S4 RootMap | 无额外版本/CRC/身份；每次全桶 checked 扫描及 S4 RootMap codec/上限已定；名称与完整组合容量消费 S5-Q1 |
| branch 绑定文件 | version/kind、VersionStoreId、完整 BranchName、RefId | RefId 字段与公开上下文值消费 S4 `[F-VS-REF-ID-8B]`；初始/alias 同一 checked codec、names 路径碰撞及全局发现验证仍待定 |
| history 结果 | 复用 S4 RefSnapshot；实际 RefRevision、自有 RootMap | 同步 visitor、两项预算、Complete/VisitorStopped 与预算失败已定，实施所有权/终止/清理验证 |
| fork 查询结果 | 自有 ForkInfo：ChildRefId、SourceRefId、完整 SourceSnapshotTicket | `[A-VS-FORKS-CHECKED]` 的 IReadOnlyList/私有只读封装、单项 maxRefs、完整集合/图及清理已定；不从元数据签发 checked revision |

未来 ref 分段/轮转、tag 分片、差分与可重建索引均为独立后续片，需实际容量/工作集证据及自己的定位/恢复合同；不预建 segment manifest 或 checkpoint 缓存。业务 Parent/RNG/operationId 可放在应用已引用对象中，不扩展中立字典 codec。外部工具 exactly-once、跨 ref 事务和自动 merge 均无框架保证。

## 实施片、Ready 工程定稿与验收

1. S5-A：同步 history visitor、栈内逆扫/固定上界、ForkOrigin 逐文件接续、返回/工作步预算与终止、mutation guard 和 owned 清理；形成冷重开选点正常停止后再次 fork 的 public 例子，不公开跨调用枚举器。
2. S5-B：实施 tag 桶组合 schema、header-only 初始化/长度保护、每次完整扫描与 data-first 单记录创建/查询；名称/hash 分桶消费 S5-Q1，不实施跨调用索引。
3. S5-C：来源感知匿名/命名 fork、ListForks、统一 names 绑定及全局唯一性、既有 ref alias；复用 S4 容器发布，保留无来源 CreateRef 与手工两步的独立结果。
4. S5-D：LLM tool-loop 多根状态与 Gym 旧快照反事实消费轨迹、进程终止 failpoint 及独立 public 验收；真实文件实验使用 W:。

| Ready 项 | 需定稿或实施验证 |
| --- | --- |
| S5-Q1 | 名称比较/编码/字符/限额、独立命名空间、bucket hash/数量/路径；名称比较相等必路由同桶，同名拒绝 |
| S5-Q2 | 同步 visitor、自有 RefSnapshot、返回/工作步预算、两种正常结束与预算失败、visited/单临时句柄/HistoryActive 已定；实施跨文件资格、回调停止/重入/Dispose、取消及各退出清理，不再开放扫描器/epoch/预算载体设计 |
| S5-Q3 | header-only 空桶初始化/I 前检/恢复保护、单一版本组合 schema、每次全桶校验及自有结果/一次清理已定；消费 S5-Q1/S4-Q2 的基础 codec，实施首 tag/后续 tag、缺失/坏记录/重复与 Outcome 验证，不再选择索引或第二版本权威 |
| S5-Q4 | 统一绑定 codec/names 路径、全局名称扫描或惰性名称表、alias 文件发布、来源感知 fork API/重读/源 flush/容器发布/Outcome；ListForks 公开结果/单项数量预算/迭代图/清理已定，实施完整性、所有权、额度与规模向量 |

验收覆盖历史调用结束后字典仍可发布/创建 tag、R0→R1→R0 仍保留三个 revision、预算失败与损坏区分、tombstone/unknown/CRC/TerminationError 不跳过、调用期间 mutation 拒绝及结束后恢复、空字典与 missing、hash 碰撞/同名同值拒绝、tag 不随 ref 更新、多个 branch 同 ref、fork 不改源/不复制 data、应用数据读取坏时不回退。故障证据沿用 S4；首版 Accepted 不要求分段、差分、checkpoint、业务谱系或精确历史尝试追踪。
历史 visitor 向量另覆盖：选中第 k 项后 false 不读取更旧坏 payload 或源；首项即初始与重复同值 revision；返回额度恰好在无来源初始耗尽可 Complete，有来源则预算失败；forward 资格、每次 source 进入、无输出后缀/false 扫描及完整读都计 work，预算检查不隐含越额 I/O。覆盖递归历史及全部 mutation 的 pre-I/O 拒绝、回调当前值/data 读取、最后回调 false/true 时 owner Dispose/token 取消/异常、最后临时 close 失败、跳转/错误/取消每个句柄仅清理一次及 guard 释放；Result 失败后的已交付自有值有效，不把前缀或 VisitorStopped 称完整历史。
tag 向量另覆盖合法正式空桶与 missing、bare Fence/短 header/缺原 Key/Fence 的重复可写打开拒绝、错版本/store/bucket/形状及后续 header。目标命中后仍传播后方重复 fullname（同值/不同值）、非目标 CRC/RootMap codec 错、错路由、tombstone、unknown kind、TerminationError 与必要 close 失败；hash 碰撞不同 full name 均可解析。验证记录尺寸前检、pooled 帧及时归还、自有字典在查询/owner 关闭后仍可用、历史 callback 内 tag 查询不损害历史槽位。覆盖 first tag body 截断后保留空桶、CompletedTail 后全桶重验、空桶 rename 仍 NotAttempted、Append/flush Unknown及 flush 后关闭失败 Confirmed；同名拒绝和容量拒绝先于额外 data barrier/输出。记录每次扫描与连续同桶创建成本，不以无缓存选择声称已经满足性能指标。
命名 fork 覆盖每个私有文件的初始化/flush/close、目录 rename 前后及内存安装失败：普通 ResolveBranch/ListBranches 与 ListRefs/ReadRef 只见共同未发布或共同已发布，绝不由私有残留公开单独 ref；只读不修改残留，重开不自动续作。覆盖正式 ref/绑定完整坏 CRC/身份/codec 明确报错，Unknown 后检查实际状态且不盲重试，重复名称先于 barrier/输出拒绝，跨不同 RefId 的同名记录冲突及全局扫描失败不当 absent。
兼容向量覆盖 standalone CreateRef 正常列出未命名 ref、手工两步失败仍保留第一步 ref、alias 失败不损害既有 ref/其他名称、empty RootMap、多个 aliases 与命名 fork 使用同一 codec/发现方式、source history 不变。目录 rename 的资格独立于 file rename；补大量未命名 ref/少量 branch 的冷查名测量，不用未来内存表掩盖首版发现成本。
补无关 data Builder 活跃时以已完成闭包创建 tag/fork ref，以及借助同文件历史随机读取构建新状态；CreateBranch 仍只确认自己的绑定，不新增 data 屏障。分别验证同步历史调用的 VersionStore mutation guard 与无关 data Builder 不阻断发布的资格。

链接验收覆盖当前/非末源快照、源后续追加不进入继承前缀、fork-of-fork、多孩子同一源位置、从继承快照再次 fork 时使用其实际 RefId、子初始与源同值但均返回、rewind 后完整发布历史仍含撤回记录。拒绝跨 store/header/伪成员/错 length 来源；实际跳转遇到缺源、坏身份/CRC/codec、初始字典不符或循环报错。覆盖后缀定位 framing 错误、工作预算先于下一项耗尽、取消与跨文件资源释放，不把定位失败当正常历史结束。
ListForks 覆盖匿名与命名孩子、多层/兄弟分叉、alias 不产生新边、无来源同值复制不产生边、creating 排除、header-only/坏初始正式容器错误、未知项/枚举及必要关闭失败。预算向量含非法零/负数先于 I/O、无来源根也计费、恰好额度且 EOF 成功、超额 ref 不打开/读取/扩表而专用失败；全无来源仍可成功空边。覆盖缺源、自链接、独立分量中的环、深链迭代与大量兄弟共享祖先，不递归或逐节点重追；图错误或末关闭/取消/结果分配失败不交部分成功。验证结果三个自有字段/完整 length、无稳定顺序承诺、正常接口不能改写 backing、后续查询/创建/关 owner 后保存结果仍有效；历史 visitor 内调用借用匹配句柄、其他本地临时句柄归还一次，不覆盖历史槽。声明边不审计源 ticket payload，而实际历史访问该坏源快照必须报错；两种资格分开验证，factory 初始化保护及平台/规模成本仍另取证。来源 fork 的源 flush 失败不得输出子容器；正常确认与 rename 未知的冷重开仍只见完整子 header/初始快照/名称，不补写源孩子表。

07 的跨 ref CU 尚未并入本合同。未来接纳时，source revision 必须来自已接受的 Snapshot 或满足共同事务判据的 CU；物理完整但事务不完整的 CU 不得成为 fork 源或继承历史节点。ForkOrigin 继续指向该实际 ref/frame，不能改为跟踪其他参与 ref 的当前 head；相应读取资格与成本另随正式并入验收。

