# S5：ref 历史、不可变 tag 与 branch 名称

状态：**Draft；2026-10-07 采用完整 RootMap、真实 ref 历史、单文件 tag 桶、统一 branch 绑定与命名 fork 的目录共同发布；2026-10-08 同步 S2 固定 12B 地址；2026-10-09 增加来源感知 fork、跨文件完整发布历史与全分叉查询，定稿同步历史 visitor、预算与清理；其余 API、路径编码及 codec 尚未实施/冻结**。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个 ref 的当前/历史值以及 tag 的内容都为应用解释的完整 `{key => FrameAddress}`。ref 有独立稳定 RefId；对外可见 branch 绑定已正式发布的 ref，可以给既有 ref 加 alias，也可以与新 ref 共同发布；tag 冻结创建时的字典，而不是跟踪 ref。两类名称空间独立。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadRefHistory(refId, budget, visitor, cancellationToken) | 从固定上界逐项交付本地及 ForkOrigin 接续的自有快照，可正常选点停止 | 同步 visitor；文件内真实链 + 首帧来源链接，无另写索引 |
| ForkRef(sourceRevision) | 从精确源快照创建匿名 fork | 子 ref 的 header/ForkOrigin 与完整初始 Snapshot，一次目录发布 |
| ListForks(budget) | 查询全部正式 ref 声明的创建来源边 | checked header 与初始边界，派生完整分叉图 |
| CreateTag(name, roots) / ResolveTag(name) | 创建/读取不可变完整字典 | 目标桶的一条完整 tag record |
| CreateBranch(name, refId) / ResolveBranch(name) | 为既有 ref 建立/解析不可变名称绑定 | 该 ref 的 names 中一个 create-only 绑定文件 |
| CreateBranchFromRoots(name, roots) | 原子创建无来源的命名 ref | 私有 RefId 容器内的 ref 与初始绑定，一次目录发布 |
| CreateBranchFromRevision(name, sourceRevision) | 原子创建保留前序历史的命名 fork | 同一容器另有精确 ForkOrigin，仍只一次目录发布 |
| ListBranches | 枚举正式 branch 名称与 RefId | 全部正式 ref 容器的 names 文件集合 |
| 无来源复制 / rewind 便利流程 | 旧字典新建独立 ref / 追加回原 ref | 调用 S4 CreateRef / PublishRef |

历史读取的入口与终止值由 `[A-VS-REF-HISTORY-CHECKED]` 定义，其余操作的成功值类型在 Ready 定稿；同步 mutation 统一消费 S4 `[A-VS-PUBLICATION-EVIDENCE]` 的 AteliaResult/必选 out PublicationOutcome，不另立 tag/branch/fork 证据异常或恢复 token。有来源 fork 必须按 revision 创建，只传 roots 的入口表示无来源起点，不凭字典相等推断或补造链接。两个基础操作手工组合仍是两次发布。首版不提供 branch rename/unbind/delete/archive/name reuse、tag 修改/删除、差分或 state-checkpoint。命名创建的共同初始化是窄创建协议，不扩展为多个既有 ref 的事务；本层定义 ref 发布历史与创建分叉，应用数据的因果谱系仍由应用解释。

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

ForkRef / CreateBranchFromRevision MUST 接收本 VersionStore 的已发布 RefRevision，由库定位正式源 ref、checked header，并从真实 RBF 主链确认 exact 源 SizedPtr；完整读取该 Snapshot 的 CRC、kind/version、身份与 RootMap codec，私有复制其字典。MUST NOT 接受调用方自由组合 roots 与 origin，不接受 header、残尾或仅随机 CRC 读成功的伪成员。源可以是当前或历史快照，也可以位于另一 ref 的继承历史；链接指向返回该快照的实际 RefId，不指向发起枚举的子 ref。

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

RefSnapshot 复用 S4 ReadRef 的自有结果，只读 Revision 与 Roots；不另立 history 专用快照、结果 Lease 或 Dispose。每项 MUST 完整校验 CRC、kind/version、身份与 RootMap codec，确认真实主链成员后才能交付。解码到自有值并释放临时 RbfPooledFrame 后再回调，不能外泄 Reader、RbfFrameInfo、pool Span 或依赖 reader 的字典。结果的 RootMap 不提供公开修改入口；方法结束、owner fault/Dispose 不撤销已交付值，内部初始字典比较不受 visitor 改写。RefRevision 仍由 S4 绑定实际位置，RootMap/身份的具体表示和 codec 留在 S4-Q1/Q2，不开放任意裸 revision 的随机 ReadRevision。

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

ListForks MUST 覆盖全部正式 RefId 容器，checked-read 每个 header 与本地初始 Snapshot/边界；包括未命名 ref，不依赖 names、branch aliases 或当前 head 值。返回自有 `ForkInfo = (ChildRefId, ForkOrigin)` 集合：无来源的 ref 不产出边；有来源的每个 ref 恰产出一条边，精确保留 SourceRefId 与 SourceSnapshotTicket。creating 槽位不参与，header-only 正式容器报错，不当作完整 fork。

扫描、构图及缓存安装 MUST 属于同一次串行 driver 操作，不允许 mutation 交错。完整扫描正常结束后，MUST 核对每条边的源 RefId 属于同一正式集合、源 header 身份正确，拒绝自链接与整个来源图中的循环。目录枚举、必要 CRC/codec、身份、预算或 I/O 错误不能当成无分叉或完整的部分结果；成功返回全部边之前不安装证明“没有孩子”的缓存。结果只声明本次已校验首帧记录的创建关系，不重新定位每条源 ticket、不校验所有源快照或子初始字典与源字典同值，不宣称全部历史健康。实际沿边遍历时才建立这些资格；结果中的 ForkOrigin 坐标不是新签发的 checked 源 RefRevision。

该边集合是唯一来源的派生视图：按 SourceRefId 可找直接分叉，按 exact source ticket 可区分同一 ref 的不同分叉点，对 child/source 关系求传递闭包可找所有后代；全图包含多层与兄弟分叉，不隐含跨 ref 的时间全序。alias 只增加名称，不增加分叉边；名称解析可另行附加展示，不成为图查询前置。

实现 MAY 从完整 checked 扫描建立可重建内存反向表，空间 O(ref 数 + fork 数)；同 owner 成功创建后按 Confirmed 规则更新。缓存不替代其依赖的必要读取或提供永久 CRC 健康保证。首版不写持久孩子列表、全局 catalog 或反向索引，也不在每次 PublishRef 时维护分叉图。

## 不可变 tag 与固定分桶

### spec [S-VS-TAG-ROOTS-IMMUTABLE] tag 一次冻结完整根字典

每个 tag MUST 保存 `TagName + 完整 RootMap` 的一条 RBF3 record，使用 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` 的私有拷贝、输入/容量 guard、data ConfirmDurable、Append 与 DurableFlush 协议；无关 data Builder 尚未归还不阻断 tag 创建，所需依赖仍必须由应用保证完成。tag 从某 ref/历史快照创建时复制当时字典，不持有可变 ref 间接绑定。同名再创建 MUST 在输出前拒绝，即使字典同值。
tag 内容直接复用 S4 RootMap codec，包括其消费的 S2 固定 12B FrameAddress；名字、RefId 和 RefRevision 的 codec 属于各自合同，不因地址固定 12B 而获得同样宽度。
TagName 的比较采用明确稳定的名称政策；默认候选为 Ordinal。路由 MUST 使用固定、跨进程复现的字符串 hash 与固定桶规则，不使用进程随机化的 string.GetHashCode。记录保留完整原名；hash 相同但名称不同不是同名，MUST 按完整名字比较。
首版每桶一个 RBF3 文件、不轮转；库 meta/header 及桶身份必须 checked。首次只在私有文件写完整 header，flush/close 后 create-only 公开空桶，再按 S4 普通 CreateTag 的 Append/flush 协议发布首条 tag。空桶初始化只是 metadata 准备，不提前设置本次 tag Confirmed；首条 tag 不随桶 rename 一起生效。空桶 rename 异常但尚未尝试 tag Append 时，本次 tag 为 NotAttempted，VersionStore 停用并重开检查桶状态；已正式发布的合法空桶保留。不得发布半个有效桶，桶为空不同于格式缺失或损坏。下一条记录的起点超过 SizedPtr.MaxOffset 时明确拒绝/维护，不隐式新建分段；合法末记录可越过起点上界，与 S4 同一容量规则。
查找与重名检查需扫描目标桶，或按需建立可重建的内存 name→实际记录位置表；建表需真实链 checked-read 与正常结束，不能将错误当未找到。ResolveTag 返回选定记录的 checked、自有 RootMap；缓存不能提供永久 CRC 健康保证。发现目标桶的未知记录/损坏/冲突名须报错。磁盘索引不属于首版。

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
当前 ReadRef 成本为本地 header、初始 Snapshot 与完整末 Snapshot bytes（初始即末帧时复用），不随历史帧数或祖先深度增长。ReadRefHistory 成本含返回快照的完整读取、跨文件 header/初始边界和定位源 fork 点所跳过的后缀 framing，不能只按返回条数估算；预算未覆盖的历史不计作已验证。按 revision fork 的来源定位也可能扫描源后缀，并增加一次源文件 flush。tag 首次查找/重名检查的扫描为 O(目标桶记录数 + bytes)，建表的内存为 O(该桶名称数)，后续定位仍需选中记录读取。不承诺分桶即可 O(1) 查询或长期无限容量。
ListForks 成本为 O(ref 目录数 + header/初始 Snapshot bytes + fork 数)，含全图存在性/环检测，不含所有源历史；结果与可选反向表占 O(ref 数 + fork 数) 内存。该成本与 branch names 数独立，不因只有少数命名 branch 就跳过匿名 ref。
ListRefs 成本与正式 RefId 目录数相关；branch 首次解析/全局查重/ListBranches 需要遍历正式 ref 目录及 names 记录，成本为 O(ref 目录数 + binding 数 + 所需 checked bytes)，不扫描全部 Snapshot 历史。即使大量 ref 未命名、只有少数 branch，也可能付出全部目录发现成本；不能按名字编码直接访问一个全局正式路径而宣称 O(1)。
实现 MAY 惰性建立唯一的可重建内存 name→RefId/绑定位置表；其内存为 O(binding 数)，构建须消费 `[A-VS-BRANCH-NAMES-GLOBAL]` 的完整资格，后续定位仍需所选记录/目标的检查。名称解析后可保留 stable RefId，直接 ReadRef 不依赖该表或任何初生名称。目录/名称、句柄缓存及枚举生命周期的实际成本须报告，不增加持久名称索引、control 全量投影、全部尝试账本或磁盘 catalog/checkpoint。

## 工程 codec 与后续扩展

| 单元 | 最小字段 | Ready 选择 |
| --- | --- | --- |
| tag 桶 header | version/kind、VersionStoreId、桶身份 | tag、初始化边界、桶数量/hash 与文件路径 |
| tag record | version/kind、完整 TagName、S4 RootMap | 名称政策/限额及 codec 复用 |
| branch 绑定文件 | version/kind、VersionStoreId、完整 BranchName、RefId | 初始/alias 同一 checked codec、names 路径碰撞及全局发现验证 |
| history 结果 | 复用 S4 RefSnapshot；实际 RefRevision、自有 RootMap | 同步 visitor、两项预算、Complete/VisitorStopped 与预算失败已定，实施所有权/终止/清理验证 |
| fork 查询结果 | ChildRefId、S4 ForkOrigin 元数据 | 完整集合、预算/错误、内存视图与返回值所有权；不从元数据签发 checked revision |

未来 ref 分段/轮转、tag 分片、差分与可重建索引均为独立后续片，需实际容量/工作集证据及自己的定位/恢复合同；不预建 segment manifest 或 checkpoint 缓存。业务 Parent/RNG/operationId 可放在应用已引用对象中，不扩展中立字典 codec。外部工具 exactly-once、跨 ref 事务和自动 merge 均无框架保证。

## 实施片、Ready 工程定稿与验收

1. S5-A：同步 history visitor、栈内逆扫/固定上界、ForkOrigin 逐文件接续、返回/工作步预算与终止、mutation guard 和 owned 清理；形成冷重开选点正常停止后再次 fork 的 public 例子，不公开跨调用枚举器。
2. S5-B：tag 名称/hash 分桶、唯一性和 data-first 单记录创建/查询；无持久索引。
3. S5-C：来源感知匿名/命名 fork、ListForks、统一 names 绑定及全局唯一性、既有 ref alias；复用 S4 容器发布，保留无来源 CreateRef 与手工两步的独立结果。
4. S5-D：LLM tool-loop 多根状态与 Gym 旧快照反事实消费轨迹、进程终止 failpoint 及独立 public 验收；真实文件实验使用 W:。

| Ready 项 | 需定稿或实施验证 |
| --- | --- |
| S5-Q1 | 名称比较/编码/字符/限额、独立命名空间、bucket hash/数量/路径；同名拒绝 |
| S5-Q2 | 同步 visitor、自有 RefSnapshot、返回/工作步预算、两种正常结束与预算失败、visited/单临时句柄/HistoryActive 已定；实施跨文件资格、回调停止/重入/Dispose、取消及各退出清理，不再开放扫描器/epoch/预算载体设计 |
| S5-Q3 | header-only 空桶初始化/恢复与首 tag 正常追加、codec、扫描或惰性内存索引及 selected record checked-read |
| S5-Q4 | 统一绑定 codec/names 路径、全局扫描或惰性内存表、alias 文件发布、来源感知 fork API/重读/源 flush/容器发布/Outcome、ListForks 完整结果与规模成本 |

验收覆盖历史调用结束后字典仍可发布/创建 tag、R0→R1→R0 仍保留三个 revision、预算失败与损坏区分、tombstone/unknown/CRC/TerminationError 不跳过、调用期间 mutation 拒绝及结束后恢复、空字典与 missing、hash 碰撞/同名同值拒绝、tag 不随 ref 更新、多个 branch 同 ref、fork 不改源/不复制 data、应用数据读取坏时不回退。故障证据沿用 S4；首版 Accepted 不要求分段、差分、checkpoint、业务谱系或精确历史尝试追踪。
历史 visitor 向量另覆盖：选中第 k 项后 false 不读取更旧坏 payload 或源；首项即初始与重复同值 revision；返回额度恰好在无来源初始耗尽可 Complete，有来源则预算失败；forward 资格、每次 source 进入、无输出后缀/false 扫描及完整读都计 work，预算检查不隐含越额 I/O。覆盖递归历史及全部 mutation 的 pre-I/O 拒绝、回调当前值/data 读取、最后回调 false/true 时 owner Dispose/token 取消/异常、最后临时 close 失败、跳转/错误/取消每个句柄仅清理一次及 guard 释放；Result 失败后的已交付自有值有效，不把前缀或 VisitorStopped 称完整历史。
命名 fork 覆盖每个私有文件的初始化/flush/close、目录 rename 前后及内存安装失败：普通 ResolveBranch/ListBranches 与 ListRefs/ReadRef 只见共同未发布或共同已发布，绝不由私有残留公开单独 ref；只读不修改残留，重开不自动续作。覆盖正式 ref/绑定完整坏 CRC/身份/codec 明确报错，Unknown 后检查实际状态且不盲重试，重复名称先于 barrier/输出拒绝，跨不同 RefId 的同名记录冲突及全局扫描失败不当 absent。
兼容向量覆盖 standalone CreateRef 正常列出未命名 ref、手工两步失败仍保留第一步 ref、alias 失败不损害既有 ref/其他名称、empty RootMap、多个 aliases 与命名 fork 使用同一 codec/发现方式、source history 不变。目录 rename 的资格独立于 file rename；补大量未命名 ref/少量 branch 的冷查名测量，不用未来内存表掩盖首版发现成本。
补无关 data Builder 活跃时以已完成闭包创建 tag/fork ref，以及借助同文件历史随机读取构建新状态；CreateBranch 仍只确认自己的绑定，不新增 data 屏障。分别验证同步历史调用的 VersionStore mutation guard 与无关 data Builder 不阻断发布的资格。

链接验收覆盖当前/非末源快照、源后续追加不进入继承前缀、fork-of-fork、多孩子同一源位置、从继承快照再次 fork 时使用其实际 RefId、子初始与源同值但均返回、rewind 后完整发布历史仍含撤回记录。拒绝跨 store/header/伪成员/错 length 来源；实际跳转遇到缺源、坏身份/CRC/codec、初始字典不符或循环报错。覆盖后缀定位 framing 错误、工作预算先于下一项耗尽、取消与跨文件资源释放，不把定位失败当正常历史结束。
ListForks 覆盖匿名与命名孩子、多层/兄弟分叉、alias 不产生新边、无来源同值复制不产生边、creating 残留排除、header-only 正式容器错误、全图缺源/循环/枚举失败、完整缓存建立与 Confirmed 后安装失败。声明边查询不审计源 ticket payload，而实际历史访问该坏源快照必须报错，两种资格分别验证。来源 fork 的源 flush 失败不得输出子容器；正常确认与 rename 结果未知的冷重开仍只见完整子 header/初始快照/名称，不补写源孩子表。

07 的跨 ref CU 尚未并入本合同。未来接纳时，source revision 必须来自已接受的 Snapshot 或满足共同事务判据的 CU；物理完整但事务不完整的 CU 不得成为 fork 源或继承历史节点。ForkOrigin 继续指向该实际 ref/frame，不能改为跟踪其他参与 ref 的当前 head；相应读取资格与成本另随正式并入验收。

