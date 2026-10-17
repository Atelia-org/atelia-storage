# S5：Head 历史、fork 与 tag 施工导航

紧凑 Handle 更新：2026-10-17 当前合同采用格式4，拒绝格式1/2/3；fork 只发布来源 header，HeadSetTag 成员为16B独立 key/value 映射。本轮实际源码、进程与包资格见[既有验收入口中的新增轮次](12-versionstore-final-acceptance.md)，未完成验证不沿用旧 Accepted。既有 Clause-ID 与历史证据保持。

既有轮次状态：**既有历史/fork/tag 等范围的独立 source-only 源码资格 Accepted；CU 已正式接纳并取得CU首轮独立源码验收 Accepted**。历史 visitor/预算/清理、精确 ForkHead、ListHeadOrigins 及09的 tag 公开闭环的既有资格见[最终源码验收](12-versionstore-final-acceptance.md)。CU 来源、历史判据与预算增量见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。公开进程、受控故障、文件镜像与包消费分别记录，旧六包候选不含 CU；后续见[实施计划](12-versionstore-implementation-plan.md)。
本文负责历史、fork 与声明图查询；Head 格式/身份/编号及发布协议消费 [S4](04-versionstore-publication.md)，记录时间见 [08 专项](08-versionstore-record-metadata.md)，bytes 结果见 [11 专项](11-versionstore-root-payload-bytes-api.md)。不可变 tag 由 [09 专项](09-versionstore-immutable-tags.md)定义，安排在 Head、历史与 fork 之后施工。
前置：[S0](../FrameStore-HeadStore/00-architecture-decisions.md)、[S2](../FrameStore/02-framestore-core.md)、[S3](../FrameStore/03-framestore-interleaved-builders-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个Head的当前/历史值为应用解释的完整root-payload bytes。Head以HeadId定位，应用管理名称/元数据/固定角色；HeadSetTag固定创建时所选Heads的EntryId，不保存payload、不随Head移动，也不提供branch名称绑定。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadHeadHistory(headId, budget, visitor, cancellationToken) | 从固定上界逐项交付本地及 ForkOrigin 接续的自有快照，可正常选点停止 | 同步 visitor；文件内真实链 + 首帧来源链接，无另写索引 |
| ReadEntryHistory(startInclusive, budget, visitor, cancellationToken) | 从准确 Entry（含自身）继续继承历史 | 与 Head 起点共用内核/预算/资源，无持久 cursor |
| ForkHead(sourceEntryId) | 自动编号，从精确源快照创建 fork | 仅 header/ForkOrigin，一次文件发布；返回新 HeadId |
| ForkHead(destLocalHeadId, sourceEntryId) | 在空闲预留号创建 fork | 同上，指定目标 create-only |
| ListHeadOrigins(maxHeads, cancellationToken) | 查询全部正式 Head 声明的创建来源边 | checked header 与完整图；单次自有只读结果，无跨调用缓存 |
| CreateTag / ReadTag | 捕获所选Head当前位置或读取不可变成员表 | 09 的稳定捕获/依赖确认、唯一可信名裁决与完整canonical表读取 |
| ReadTagInfo / ScanTags | 按名读取或同步逐项交付 tag 元信息 | 09 的独立 checked 元信息；不证明成员表/Entry健康或签发成员EntryId |
| 无来源复制 / rewind 便利流程 | 旧 payload 新建独立 Head / 追加回原 Head | 调用 S4 CreateHead / PublishHead |

历史入口/终止消费 `[A-VS-REF-HISTORY-CHECKED]`，ListHeadOrigins 结果/限额消费 `[A-VS-FORKS-CHECKED]`；mutation 统一用 S4 AteliaResult/必选 out PublicationOutcome。fork 只接收 EntryId，不接受自由组合 roots/origin 或凭同值补链接；无来源复制仍用 CreateHead。首版不删除/复用 Head，不修改/删除 tag，不提供差分/checkpoint；多个同库既有 Head 的共同发布由07定义，不包括创建或 tag。发布历史与应用因果谱系分别解释。
Head header/普通 SingleEntry 的字段、来源判别与完整记录容量唯一消费 S4 `[F-VS-REF-FRAMES]`；历史、fork 和 ListHeadOrigins 不另选编码。正式 Head 的恢复与读取资格消费 S4 `[R-VS-LOCAL-COMPLETE]`：可写访问先恢复，再校验 header 与本次实际需要的记录；不预读初始 payload、不追溯创建过程，也不增加本层前缀保护。
自动/指定 fork 共用 S4 `[S-VS-REF-ID-DOMAINS]` 的编号/目标前检，在 data/源 flush 及私有准备前拒绝本次确定失败；tag 不分配 HeadId。file rename 正常返回后先 Confirmed，自动创建再登记水位，随后安装结果；编号不是跨 Head 时间全序或来源资格。
正式 Head 文件/来源定位与全集发现消费 S4 `[F-VS-REF-PATHS]`；private 排除。tag 名称、内部路径与桶合同唯一见 [09 专项](09-versionstore-immutable-tags.md)，不缩减 Head 发现范围或新增全部历史审计。

## 表达能力与历史边界

本模型的节点是实际发布快照：同一 Head 内的前一节点由文件顺序隐式表达，没有更早本地 Entry 时的跨文件前序由 ForkOrigin 表达。每个节点最多一个前序，多个新 Head 可以从同一快照分出，因此足以表达线性 head 推进、任意历史选点 fork、fork 的再次 fork，以及全部创建分叉构成的森林；不需要每条 SingleEntry 重复保存 Parent。

| 动作 | 本层历史表达 |
| --- | --- |
| 普通发布或重复发布同值字典 | 追加新 EntryId，前序为本 Head 的前一实际快照 |
| 从源 A2 fork 为 B | 仅发布指向 A2 的 header，不产生 B0；未续写 B 的当前值就是 A2 |
| 源 A 后来继续更新 | 不改变 B 的来源，A2 之后的源更新不进入 B 的继承历史 |
| rewind 到旧字典 | 追加一次新的 head 变更，保留被撤回的更新记录 |
| 业务 merge、编辑命令、撤回目标、状态因果 | NamedFrameMap 本身不说明这些意图；应用另行保存相关信息 |

例如 A 的实际序列为 `A0 → A1 → A2 → A3(字典同 A0)`，从 A3 fork 后的发布历史仍包含 A2/A1；不能把它当成只沿业务父状态回到 A0 的因果链。本层也不将一个合并后的 NamedFrameMap 自动解释为多父 merge DAG。这里的“完整历史”是当前 Head 的本地发布序列，以及各次 fork 固定选中的前序发布前缀；兄弟分叉的更新与源 fork 点之后的更新另由分叉图发现，不混入这条历史。

### spec [S-VS-FORK-FROM-REVISION] fork 在创建时建立精确来源资格

ForkHead 两种入口 MUST 接收准确 HeadEntryId 定位值，包括公开 codec 恢复值；先消费 S4 default/持久上下文前检及自动/指定目标前检，再定位正式源 Head、checked header，直接精确读取 ticket。成员资格消费 S4 `[A-VS-READ-REVISION]` 的正常 RBF3 模型与局部证明；不从源 EOF 重新定位、不预读无关初始内容、不调用 ReadHead 选择可变末端。本次完整检查 CRC、kind、meta、尺寸及 GetScanBoundaryAfter anchor/尾 Fence；CU 来源须通过07共同判据，未成立直接返回 InvalidForkOrigin，不改选更旧节点。来源读取与 ReadEntry 共用内核；坏源不产生新 Head。

两个重载均返回 `AteliaResult<HeadId>`，成功值只标识新 Head，保持必选 out PublicationOutcome；不产生子 EntryId 或子 Entry 时间。header 的 ForkOrigin 永久保存实际持有 payload 的 Entry 身份。基于未续写 fork 的 ReadHead 返回值再次 fork，会直接指向同一实际来源，不形成空分支的当前值解析链；多个 Head 可以引用同一准确 Entry。不按 payload 内容合并真实发布。

所有生命周期/历史 mutation guard、目标/容量及准确来源资格先完成。从同次来源读取收集确认集合 D：普通 Entry 为来源文件，成立 CU 为来源及必要成员文件；只在完整 exact 读取（含 Fence）成功后使用该集合，不再次读取 CU、不递归解析 peer 的旧当前值。随后 data ConfirmDurable → 按 LocalId 去重排序 DurableFlush D → 在单个 private 子文件写带 ForkOrigin 的 header → flush/close → no-overwrite file rename。仍限 ProcessCrashOnly，不增加断电保证或持久依赖图。

来源或必要 CU peer 确认失败时，本次 fork 尚未尝试公开事实，保持 NotAttempted，HeadStore 按既有规则停用，不进入子输出。私有文件/rename/内存安装继续使用 S4 PublicationOutcome，Unknown 从正式 rename 尝试开始，成功后立即 Confirmed。来源只写子 header，不向源登记孩子，不推进源当前值。

新 fork 不写克隆 SingleEntry，不复制来源 payload 作为子输出；读取来源所需的完整 CRC 校验仍按原范围执行。正式 header-only fork 可读、可列出，并可追加首个 SingleEntry 或 CU。首次本地接受前，当前值依赖准确来源及必要 CU peers；这些文件失效时必须显式失败。已有本地健康发布的当前读取不额外预审祖先。需要独立内容副本时，应用显式调用 CreateHead(entry.Payload) 建立无来源 root；HeadSetTag 中恢复的 ID 仍须精确重验。

## Head 历史与反事实分支

### spec [A-VS-REF-HISTORY-CHECKED] 同步历史读取交付真实且自有的快照

两种历史入口如下；新增准确起点的实际源码/进程/包资格单列于[验收新增轮次](12-versionstore-final-acceptance.md)，不沿用旧历史 Accepted：

```csharp
public readonly record struct HeadHistoryBudget(long MaxEntries, long MaxWorkSteps);
public enum HeadHistoryEnd { Complete, VisitorStopped }

AteliaResult<HeadHistoryEnd> ReadHeadHistory(
    HeadId headId, HeadHistoryBudget budget, Func<HeadEntry, bool> visitor,
    CancellationToken cancellationToken = default);
AteliaResult<HeadHistoryEnd> ReadEntryHistory(
    HeadEntryId startInclusive, HeadHistoryBudget budget, Func<HeadEntry, bool> visitor,
    CancellationToken cancellationToken = default);
```

每项完成 S4 发布资格后，按 [11 bytes 公面专项](11-versionstore-root-payload-bytes-api.md)构造自有 HeadEntry、归还 pooled 帧，再调用 visitor。结果所有权与内容解码边界不另定义；准确来源资格仍按按需访问范围检查。应用可在 visitor 内解码，异常沿 callback 路径传播。HeadId/HeadEntryId 值与位置资格消费 S4。

visitor 返回 true 表示继续，false 表示已接收当前项并正常选点停止。false 后 MUST 不再扫描旧帧或访问下一源；先完成健康/取消复检和必要清理，再返回 VisitorStopped，即使该项恰为起点也保持此结果。调用方可保存所选值，在方法返回后fork/rewind；CreateTag只捕获调用时当前位置，不能把所选历史ID直接做成tag。需要列表时由调用方收集，不要求库保留整批历史。

**共用准入与固定上界。** 两种入口 MUST 共用 owner disposed/fault、HeadId/EntryId 参数、非 null visitor、两个预算均为正数、取消及无其他活动历史的准入检查，在首次文件访问前完成；09 的 TagScanActive 期间亦不得进入历史。无隐含预算默认值；long.MaxValue 可表示调用方不设置实际限额，default 的零预算无效。通过准入后使用同一个 HistoryActive 位和 try/finally 清理路径，拒绝两类历史的全部四种嵌套及本 HeadStore 的所有 mutation，包括发布、自动/指定 Head 或 fork 创建和 tag 创建；mutation 按 S4 先初始化 out，guard 拒绝先于 barrier/输出且不新增 fault。visitor 可串行读取当前值、ReadEntry及 data，亦可按09调用 ReadTag、ReadTagInfo 或 ScanTags，保留自有结果；历史与 tag 扫描使用独立临时槽。同文件 Head 查询复用当前可用的 RBF 句柄，不重复独占打开。visitor自己的查询不计入外层预算；历史内核必要工作必须计费。owner Dispose 仍是受控清理，不作为发布 mutation 拒绝。首版无并发调用或外部改写，不建跨实例 entry 协议。
进入本 Head 时 MUST 用公共正向扫描只完整检查首 header，保存 exact header ticket、checked ForkOrigin 与 header 物理后界给出的本地记录起点，不预读第二帧 payload。ReadHeadHistory的初始Head随后从实际完成尾部取得逆向扫描器，以该物理完成上界固定本次遍历；准确起点及ForkOrigin接续使用下述有界扫描路径；依次完整校验普通 SingleEntry 或 CU，CU 复用07共同判据，未提交 CU 不交付且继续向前，成立 CU 与普通 SingleEntry 各交付一次自有快照。所有无输出的组验证与回退仍计工作步；必要错误不得转成未提交。无来源 root 在逆扫实际到达本地记录起点时必须读取普通初始 SingleEntry；有来源 fork 的第一条本地记录可为 SingleEntry/CU，也可以没有本地记录。初始即末时只复制一次自有结果。后续不得重读 head 替换这个上界，不能调用未计费的 ReadHead 助手完成隐含资格工作；初始坏而 head 健康不阻断前面的健康交付或 visitor false 停止。

**准确用户 anchor。** ReadEntryHistory 使用独立方法名，避免 ReadHeadHistory(default, ...) 重载歧义。内核接收起始 HeadId 和可选准确 ticket：无 ticket 表示上述当前尾起点，有 ticket 表示准确起点；ForkOrigin接续共用准确起点路径。准确起点直接 CheckHeader → 范围/ReadFrameInfo → GetScanBoundaryAfter → 有界 ScanReverse；首项 ReadPublication/CU 检查完成内容资格，不先调用公开 ReadEntry 重复装载 payload。首项完整 ticket 必须等于 startInclusive，交付原ID/时间/实际owner/payload；源或子Head后来追加不改变该历史前缀。

用户 anchor 的范围、首项不符或合法未成立 CU 返回 InvalidHeadRecord；checked ForkOrigin 接续中的相同失败沿用 InvalidForkOrigin。RBF、必要peer、I/O、预算与清理错误保持原类；不把它们改当未成立CU。首项不得跳过或回退，成功交付后更早的未成立CU才按普通历史规则跳过；后续ForkOrigin均采用声明来源错误语义。这个区分只是扫描上下文，不是ID来源登记。无需另传所选世界线或判断某视图祖先；空fork C的current=a1，从a1起查沿A历史，后续写入仍须保存C的HeadId。

**继承前缀。** 正反向枚举均使用 `showTombstone: true`。本地历史包含重复同值快照与 rewind，省略已检查的首 header 与07共同判据确认未提交的 CU；必要节点的未知 kind/格式版/meta 形状、tombstone、损坏均报错。历史按真实链交付记录原时间，不按时间排序或去重，也不新增Entry去重集合。首版逐文件循环，直接消费 public RBF API：

1. 进入每个 Head 前加入路径 visited，重复 HeadId 为循环错误。root 到达 RecordsOffset 并完整校验普通初始 SingleEntry、visitor 返回 true 后即可 Complete；保持该快路径，不额外读取 header/EOF。有来源 fork 正常抵达已 checked header 时转向固定来源，header 不交付为 Entry；首个 CU 未成立时可没有任何本地交付。归还当前临时句柄再进入源，既有 owner 句柄不因跳转而关闭。
2. 只定位正式 SourceHeadId，按 S4 访问模式打开/复用并完整检查源 header；不采用 creating、不预读源初始 payload。以 checked 不可变 ForkOrigin 的 exact SourceEntryTicket 直接定位，先检查 header 后范围及 SingleEntry/CU 尺寸，再完整验证 anchor 与尾 Fence，按边界逆扫合同从该点开始。不扫描源本地后来追加的后缀；CU peer 必要异长消歧仍按07执行。
3. exact 源须为实际已接受 Entry；CU source anchor 未成立直接 InvalidForkOrigin，不改选旧节点。资格通过后向 visitor 交付源自己的 ID、原记录时间和自有 bytes，再继续更早前缀。不保存 childInitial，不比较克隆，不给 header 签发 EntryId。
4. 来源 fork 点之后的本地更新不进入继承历史；CU peer 必要结构消歧不受此范围限制。边界/目标或实际遍历旧节点坏、已观察 TerminationError、I/O/fault 继续传播；source factory 当前 EOF 的结构资格/恢复仍按 S4 执行。

每个实际 MoveNext=false MUST 检查 TerminationError；非 null 传播底层失败，尚未到 root 初始记录或 fork checked header 的正常 false 也是非法序列/来源，不能伪装 Complete。无来源的 exact 初始 SingleEntry 已完整校验，且其 visitor 返回 true 时，header 后界与该节点起点已证明没有更早快照，可直接 Complete，不必额外推进到 header/EOF。Complete 只证明本次继承发布前缀，不审计被排除的源后缀或证明应用图健康；必要历史节点坏不能静默跳过。

**共用预算与停止。** 两个上限只约束本次遍历，不持久化，不增加配置或持久 cursor。两种入口及 ForkOrigin 接续使用下面唯一计费表，TakeWorkStep 必须先于实际动作：

| 预算 | 计费与拒绝边界 |
| --- | --- |
| MaxEntries | 每次交付 checked 自有快照计一项，包括每页重复的 inclusive anchor；交付上限用完且根起点尚未证成，继续遍历前即失败，不为探测下一项执行额外 I/O |
| MaxWorkSteps | 每次进入 Head 的文件获取尝试（含 CU peer）、每次 forward/reverse MoveNext 尝试、每次完整帧读取各计一步，包含 header完整读取、用户/来源anchor、publication、CU无输出组工作及返回false尝试。同ticket CU peer的GetScanBoundaryAfter完整重读另计一步；准确起点的GetScanBoundaryAfter与有界ScanReverse构造时anchor完整重读分别计费。CU异长定位的ReadFrameInfoImmediatelyAfter结构后继查询另计一步。ReadFrameInfo等已有只检查剩余额度的动作维持口径，不另臆定固定磁盘读次数；复用已读本地CU不重复计peer读取，helper封装不得漏计 |

额度不足返回错误码 `HeadStore.HistoryBudgetExceeded` 的失败 Result，不返回正常 BudgetExhausted/partial-success 或公开 usage 摘要。先复检 owner/fault/token，再判继续工作的额度；visitor false 优先正常停止，visitor true 且真实无来源起点已经证成则 Complete，返回数恰好用完不阻碍该结果。尚需资格检查才能证明起点时，所需步骤也必须有额度；禁止越额探测或根据数量猜祖先不存在。已交付前缀仍可保存，但预算失败、正常早停都不证明未访问历史完整。
工作步是算法入口次数，不是物理 I/O bytes、耗时、单帧大小或 RSS 上界；工厂 Open 的内部结构检查/恢复、缓存预读和一次同步读/CRC/envelope解码不能被这些计数拆分或抢占。visitor 自己的查询/data 读取及结果积累另计。进入 Head 的一步约束访问 Head 数；文件打开前后、每个扫描/完整读和回调之间检查 cancellationToken。完整读取前先以已取得的 RbfFrameInfo 检查 S4 该版本的记录尺寸上限，不先租用超限全帧再拒绝；解码仍消费 S4 字段/容量检查，不能为满足工作预算取消它们。每查询全帧请求字节额度、硬内存/时限等独立预算留待真实大 payload 成本或调用方限额需求出现后设计。

**清理与失败。** raw sequence/枚举器仅在方法栈内，跨文件不预收集 ticket 列表；RBF 枚举器本身无 Dispose，不引入外泄 sequence、owner epoch 或扫描 Lease。除既有 owner 文件外，遍历只保留一个当前临时 RBF 句柄、至多一个短期 CU peer 句柄、当前完整帧/必要自有 root bytes 和 O(继承路径已访问 Head 数) visited；额外 handles 为 O(1)，visitor 自行保留的结果另计。CU peer 访问不加入继承路径 visited，也不递归验证替代 CU 组。临时文件按 S4 的只读/可写恢复规则打开，本调用不另发布、flush 或清理私有创建残留。
当前历史和短期 CU peer 句柄均登记在 owner 可清理槽位，连同 HeadId 可供同文件查询复用；peer 逐个访问/关闭，不装入全组长期缓存。正常跳转、所有退出及 owner Dispose 都 MUST 在调用 public Dispose 前先取出并清空相应槽位，关闭抛错也不重试。owner Dispose 先失效实例，尝试当前临时及其他 owned 资源，不 Dispose 借入 data；方法 finally 只清理仍在槽位的句柄，所有退出都清除 HistoryActive。既有 owned 文件继续遵守自身生命周期，不把借用句柄当临时句柄归还。
每次 visitor 返回后、继续 I/O 前、必要关闭后及最终正常返回前 MUST 复检 owner disposed/fault 与 token；最后回调 Dispose owner、取消或抛异常，最后关闭失败，都不能返回 Complete/VisitorStopped。取消抛 OperationCanceledException，visitor/I/O 等异常原样传播；必要 CRC/envelope/长度/身份/成员错误返回所属层失败，不跳过坏记录、不回退。预算失败、普通读错误、纯读异常、取消或 visitor 异常本身不 fault 健康 writer；实际 owned 清理/恢复失败按 S4 停用规则，主异常优先并继续尝试清理，清理失败不能被正常停止遮蔽。

**边界逆扫依赖。** [RBF 边界逆扫增量](../Rbf/rbf-reverse-boundary-plan.md)提供 `ScanReverse(RbfScanBoundary boundary, bool showTombstone)`，从 anchor（含自身）向前，复用完整 CRC/尾 Fence 检查。两类历史及来源接续直接使用该公共重载，不以 internal文件视图、手写Fence或扫描无关后缀替代。RbfScanBoundary和导入ID本身不是成员凭证；局部成员证明、正常writer/尾恢复模型及离线镜像边界唯一消费S4 `[A-VS-READ-REVISION]`。同步visitor不承诺持久cursor，工厂成本仍另计，Audit全链检查不弱化。

**inclusive 最小分页。** 第一页从 e100 固定起点读到 e81，visitor返回false；下一页从 e81 开始，只过滤首个与e81相等的ID，再收集更早项。重复anchor及其资格工作照常收费，到根可有仅重复anchor的一次Complete调用，应用过滤后是空页。callback=false已接收当前项，不多读一项探测HasMore。筛选State/Event的应用必须保存最后处理的Entry，不能只存最后显示项。

anchor资格可能耗尽工作预算而零回调；anchor已交付后，连续未成立CU也可能耗尽预算而没有新的可用继续位置。重复anchor不算新增进度，相同预算重试不保证前进；调用方可提高预算或中断。首版不保存这种物理扫描进度，不新增exclusive起点或跨页lease。已交付自有Entry仍可保存，预算/取消/错误均不能冒充遍历完成。

（Informative）A 已有 A0/A1/A2/A3，B 从 A2 创建并更新 B1，C 从 B1 创建后更新 C1：`ReadHeadHistory(C)` 返回 `C1, B1, A2, A1, A0`；A3 不出现，fork 不产生 C0/B0。

（Informative）Gym 在 visitor 中选中旧快照 `R` 并返回 false，待 ReadHeadHistory 返回 VisitorStopped 后，再调用 `ForkHead(R.Id)` 或 `ForkHead(destLocalHeadId, R.Id)` 保留来源；只要独立起点时用 CreateHead(R.Payload) 入口。rewind 调用 `PublishHead(oldHeadId, R.Payload)` 追加新 EntryId，保留原发布历史。data 根帧共享，不复制其图；tick、RNG、业务父状态与推演规则继续由应用保存。

## 全部分叉查询

### spec [A-VS-FORKS-CHECKED] 正式 Head 的首帧声明完整分叉图

首版入口已在 V2-C 实施；源码与后续系统资格见[实施计划](12-versionstore-implementation-plan.md)：

```csharp
public readonly struct HeadOriginInfo {
    public HeadId ChildHeadId { get; }
    public HeadId SourceHeadId { get; }
    public SizedPtr SourceEntryTicket { get; }
    // 仅库内部构造；三个属性保存自有值。
}

AteliaResult<IReadOnlyList<HeadOriginInfo>> ListHeadOrigins(
    int maxHeads, CancellationToken cancellationToken = default);
```

**完整集合与必要资格。** ListHeadOrigins MUST 覆盖全部正式 Head 文件，包括预留区/自动区与无来源 Head；不依赖应用名称映射或当前 head 值。直接消费 S4 `[F-VS-REF-PATHS]` 的全部直接项发现/期望目标定位，以及 `[F-VS-REF-FRAMES]` 的共同 header 检查：实际首帧、完整 CRC/身份及来源字段；不读取初始 SingleEntry、末 head 或 root-payload bytes。creating 不参与，正式文件缺/坏 header、未知项、枚举或必要读取错误皆报错；只有已资格化且正常结束的空正式集合才可当空，不能用 File.Exists=false 或过滤枚举猜缺失。逐 Head 及时释放 pooled header，仅保留身份与可选来源元数据；不调用 ReadHead 或重读 source ticket。初始 SingleEntry 的内容损坏不阻断健康 header 声明的查询；底座工厂仍须通过当前 EOF 的结构准入，结构损坏不能借 header-only 查询绕过；有来源 header-only 文件的当前值须另按准确来源读取；声明图成功仍不证明来源内容健康。

**单一数量预算。** 在任何发现 I/O 前 MUST 检查 owner disposed/fault、`maxHeads > 0` 与取消；没有隐含默认限额，不按 maxHeads 预分配。每个正式 Head 在本次打开/header 检查及加入节点表前计一项，含无来源根，不能只计输出边。达到上限后仍可推进直接项枚举并按 S4 检查该项名称/类型，以证明实际 EOF；若还有正式文件，在其打开/内容读取或节点表增长前返回错误码 `HeadStore.ForkQueryBudgetExceeded` 的失败 Result。不以额度用完猜 EOF，不探测超额 Head 的内容；未知项/枚举或必要清理错误仍走原错误通道。恰有 maxHeads 个 Head 且正常 EOF 可以成功；int.MaxValue 合法，只表示不设置更小逻辑限额，不保证实际集合分配成功。
数量上限约束节点、结果项及下述图算法的访问规模，不是目录枚举内部工作、RBF factory 尾部资格/恢复、同步单帧读/CRC/解码、物理 I/O bytes、耗时或 RSS 硬上限。header 尺寸与完整帧租用前检查继续消费 S4；取消在每个直接项、文件访问/关闭前后及图行走步骤之间检查，不承诺抢占底层同步调用。无独立 MaxForks/MaxWorkSteps/bytes 计数、预算载体、usage、配置或分页协议。

**一个临时图。** 扫描与构图属于同一次 single-driver 操作，不允许 mutation 交错。用一个本次调用的 HeadId 节点表保存所有 checked 正式身份、可选来源及检查状态；完整目录正常结束并必要关闭成功后，逐来源核对 SourceHeadId 属于同一已 checked 集合，拒绝自链接。无来源根仍在表中，不作为缺源；源 header 已在本次扫描校验，不再逐边打开。全图环检查 MUST 使用迭代三色或等价的已完成节点记忆，每节点仅作常数次图访问；禁止递归深链或从每个节点重新追完整祖先链，也不以 HeadId 大小替代来源图检查。不排序，不引入持久 generation、拓扑编号或通用图框架。

**仅交付完整自有边集合。** 全部资格、图校验、必要清理、结果构造及最终 owner/fault/token 复检通过后，才返回非 null 自有列表；无来源 Head 不输出，有来源的每个 child 恰输出一项，零边成功为空列表。任一失败或取消不交付部分成功、visitor 前缀、正常 BudgetExhausted 或“没有孩子”的结论。输出顺序没有跨调用稳定、编号排序或时间顺序承诺。
HeadOriginInfo 是普通非 record readonly struct，仅有三个只读属性及内部构造，无 setter/init、带参 public 构造、Deconstruct、专用整体等值/hash/codec、owner/reader 或 Dispose。它平铺逻辑 ForkOrigin，避免再公开嵌套来源类型；SourceEntryTicket 保留完整 SizedPtr（含 length），MUST NOT 包装成 checked 源 HeadEntryId。default 属性可读取，但不是库成功列表中的有效边，不新增 IsValid/default 错误族。结果仅声明本次已 checked 创建关系，不定位每条源 ticket、不校验源 Entry 内容与共同发布资格，不证明全部历史/data 图健康或旧调用曾 Confirmed；内部构造也不是防伪认证。
列表内部使用唯一自有数组和只实现 IReadOnlyList/IEnumerable 的私有薄封装；不外露可变 array/List、IList 或能取得 backing 的 ICollection.SyncRoot，不复用下一调用 scratch 或长期缓存。HeadOriginInfo 内的 HeadId/SizedPtr 都是自有值，无需复制业务对象。查询结束、后续查询/创建及 owner fault/Dispose 不改变已交付列表；反射、Unsafe 等越过正常接口的改写不在此保证内。

**句柄与失败。** 本查询无应用 callback，按既有串行前提执行，不新增 ListForksActive/busy 或改写 HistoryActive。历史 visitor 可调用它；匹配 HeadId 时复用可用的既有 owner/历史当前句柄，不重复独占打开、不关闭借用句柄。其他 Head 逐个使用本方法局部临时句柄，绝不覆盖历史当前临时槽；关闭前取出并清空局部槽，finally 只清理仍登记的资源，每个句柄仅尝试一次。目录枚举器、当前 pooled 帧和全部临时句柄的正常/异常退出，消费 S4 的 owned 清理/主错误优先规则；最后关闭失败不能被空列表或预算错误掩盖。
取消抛 OperationCanceledException；必要格式/CRC/codec/身份/图/预算失败返回所属失败，I/O/运行期异常原样传播，不跳过坏 Head。纯读取、预算、取消或结果分配失败本身不新增健康 writer fault；实际 owned 恢复/清理失败按 S4 停用并完整受控清理，不 Dispose 借入 data。访问模式及可写打开的恢复直接消费 S4，本查询不另行 flush、发布或清理 pending；Head恢复后完整性、实际组件/根锁准入已定，HS平台与运行资格仍随实施取得，本条不以 header 校验替代它们。

该边集合是唯一来源的派生视图：按 SourceHeadId 可找直接分叉，按 exact source ticket 可区分同一 Head 的不同分叉点，对 child/source 关系求传递闭包可找所有后代；不隐含跨 Head 时间全序。已有 HeadEntry 可用 Id.HeadId/Ticket 与声明位置比较，关联 exact 点的分叉；匹配不签发源 EntryId 或证明源内容健康。应用映射不增加边，名称展示由应用管理；应用可从结果派生反向表。
首版每次 ListHeadOrigins 都执行上述完整 checked 扫描，不保留跨调用反向表、负缓存或图安装/更新相位，不写持久孩子列表、全局 catalog 或索引。实际重复查询成本超出目标后，再研究内存表及其完整建立/更新资格。

（Informative）int 参数与完整物化列表/节点索引的 Count 域相符，是没有超出该域消费者时的工程选择，long 也可正确。readonly 结果与私有薄封装提供稳定保留视图；独立可变副本或标准 immutable 集合也可正确，不因此建立公共集合框架或安全凭证。每 Head 的本层读取固定为首 header，图行走是线性节点访问；哈希表定位按通常均摊成本估算，不承诺碰撞下的硬 CPU 界。公开 [RBF 正扫](../../src/Rbf/RbfForwardEnumerator.cs)与[payload 坏仍可结构扫描的测试](../../tests/Rbf.Tests/Internal/RbfScanForwardTests.cs)证明 framing 不替代必要完整读取；兄弟仓的[私有只读列表封装](../../../durable-graph/src/DurableGraph.Storage/StateRevision.cs)只提供窄所有权实现参考，不引入依赖或兼容。没有新栈实际消费者或性能测量证明额外 work/byte 限额、排序、缓存及通用图 API 必要；本条定义访问范围与复杂度；V2-C 源码验证见实施计划，不能替代平台或包资格。

## 不可变 tag 的独立施工片

[S5-B：不可变 tag 专项](09-versionstore-immutable-tags.md)是名称、桶路径/格式、初始化保护、独立元信息资格、请求范围损坏裁决、CreateTag/ReadTag/ReadTagInfo/ScanTags 与对应验收的唯一权威；原 `[S-VS-TAG-PAYLOAD-IMMUTABLE]` Clause-ID 随合同迁入，本文不保留第二份定义。

HeadSetTag固定所选Heads的当前EntryId，创建只接受HeadId slice；不接受历史EntryId任意组合。历史visitor可调用ReadTag、ReadTagInfo或ScanTags，创建必须等历史调用结束；得到的成员ID可在回调外供ForkHead，仍重验精确来源。tag扫描重入限制、TagCreateActive、临时桶/成员槽与必要依赖确认唯一见09。本轮完整替换旧bytes/Related合同，验收见16记录及本轮新增验收。

## 应用映射与固定槽位

固定角色由应用常量/枚举选择预留号，动态Head可保存公开编码的HeadId，冷开后TryRead恢复所选分支；也可只存LocalId，在正确库上下文ReadHead(local)读取当前Entry，更新/创建tag前用ListHeads按GetLocalHeadId匹配。不能用来源Entry.Id.HeadId代替所选Head；合同见 S4 `[A-VS-LOCAL-REF-LOOKUP]`。无独立 branch binding、全局应用名称查重或 name→Head 索引。

摘要/缩略图/人物区域等元数据可作为 data 帧，与业务根一起放入同一 Head 的 NamedFrameMap 整体发布。独立目录 Head 登记动态对象若顺序调用单 Head 发布，创建后登记前中断仍可能留下未登记 Head；ListHeads 可发现实际集合，应用自行核对业务身份。07 CU 只可共同更新多个既有 Head，不把新 Head 创建与目录登记并为一批。

## 查询、启动与成本

### spec [A-VS-ROOTS-QUERY-THEN-READ] 查询 root bytes 后由应用接受内容

查询与应用接受内容的边界直接消费 S4 `[S-VS-ROOTS-OPAQUE]` 与 [11 公面专项](11-versionstore-root-payload-bytes-api.md)；采用 NamedFrameMap 时，由应用 Decode 后按地址读取 data。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

Open MUST NOT 默认回放历史/data 图；惰性打开只延后资格检查，不把未访问对象说成健康。实际目录/metadata、factory 尾部资格/恢复、I/O 与资源成本随实施报告，预算不成为硬时间/RSS 保证。各操作的算法与预算由所属条款定义，成本概览如下：

| 操作 | 首版成本与边界 |
| --- | --- |
| ReadHead | 普通 head 为本地 header/末 SingleEntry 的 checked bytes；CU head 另检查指定 peer 完整帧/全表，未提交后缀可累积回退，异长 peer 消歧可扫描必要后缀/前驱。不预读无关初始 payload，底座 factory 成本另计，不能宣称 CU 查询成本恒定 |
| ReadHeadHistory / ReadEntryHistory / ForkHead | exact 旧点直接读；历史从所选 anchor 继续更早前缀，不定位源本地后续帧。CU 另付07 peer 校验/消歧成本，所有历史工作先计费；fork 另确认准确来源及成立 CU 的直接成员文件，底座 factory 成本另计 |
| ListHeadOrigins | N个正式 Head、F条边：通常哈希成本下 O(N+F+checked header bytes)，临时元数据 O(N)、结果 O(F)；不读初始 payload，maxHeads含无来源根 |
| ListHeads / 可写冷开 H | 完整正式路径枚举，不打开全部快照；只读 Open 不因 pending 全扫 Head，直接整数定位无需 ID→path 或应用名称表 |
| tag 查询/创建 | 目标桶结构扫描、请求名裁决、常数个名称状态与连续创建成本唯一见 [09专项](09-versionstore-immutable-tags.md)，分桶不构成 O(1)保证 |

不新增持久名称/孩子索引、control 全量投影或尝试账本；ListHeadOrigins与tag不保留跨调用索引，消费各自首版合同。Head 分段/轮转、差分/checkpoint、tag 分片、可重建索引和业务 merge 等仅在出现实际需求与成本证据后另审，不作为首版前置。

## 实施与验收

建议先完成并独立验收 [RBF 边界逆扫增量](../Rbf/rbf-reverse-boundary-plan.md)，再按 S5-A 历史 visitor → S5-C 来源感知 fork/ListHeadOrigins → S5-D 两类应用 public 消费及进程终止 → S5-B [独立 tag](09-versionstore-immutable-tags.md)施工。S5-B 保留原切片标识，字母不表示强制顺序；真实文件实验使用 W:，完整组合汇入 S6。

前文历史/fork/声明图合同须逐项验证；S4 创建/编号/Outcome、08 时间、11 bytes 与09 tag 的详细向量从原权威引用，只在本篇补互操作和以下特有组合。

| 验证组 | 特有向量 |
| --- | --- |
| 准确起点与分页 | 导入anchor首项相等，旧点后缀排除，多层来源、同值真实发布与成立CU；用户未成立anchor为InvalidHeadRecord，声明来源为InvalidForkOrigin，普通旧节点跳过。inclusive重复anchor收费；根页可过滤为空；零回调与零新增进度分别验证，相同预算重试不承诺前进 |
| 历史路径 | 当前/非末 fork 点、源后续追加排除、fork-of-fork、多孩子及继承快照再 fork；header-only fork 无本地交付，连续空 fork 直接引用实际源 Entry；同值重发/rewind 不去重。缺源/错身份/成员/length/CRC/envelope/meta、墓碑/未知 kind/TerminationError、循环失败，不作正常终止 |
| 历史预算/停止 | 第k项 false 后不扫描旧坏内容或下一源；初始 CRC 坏/head 好时首项 false 成功，继续到坏初始失败。本地记录 visitor false 不访问更旧来源，抵达 fork header 后才跳源。返回数恰在无来源起点耗尽为 Complete，有来源且需继续为预算失败；进入Head/header资格/anchor及内部重复完整读/false扫描全计work，先扣费再动作；前缀/VisitorStopped/失败不称完整历史 |
| 回调/资源 | 两类历史的四种嵌套与全部 mutation 在 barrier/I/O 前拒绝，调用后 guard 释放；visitor可当前值/data读取及 ReadTag/ReadTagInfo/ScanTags/ListHeadOrigins，复用匹配 Head 句柄且 tag 使用独立槽；活动 ScanTags 拒绝进入历史。最后回调 false/true 时 Dispose/取消/异常，末 close失败，均不正常终止；跳转及所有退出资源单次归还，已交付自有值仍有效 |
| 引用初始化 fork | 两种目标只发布带来源 header，返回新 HeadId；准确来源/CRC/envelope/尾 Fence 及必要 CU 资格重验；目标占用先拒绝；data 后按 LocalId 确认来源及必要 peers，任一确认失败无子输出。连续空 fork/共享来源/父子同批 CU、冷开与首次本地 SingleEntry/CU；来源不随父推进，fork 不产生 Entry 时间，不修改源文件 |
| ListHeadOrigins集合/图 | 两编号域、无来源根、多层/兄弟分叉，creating排除；无来源同值复制/应用映射不产生边。初始/head 的 payload CRC 或 envelope 坏而 header 好、factory EOF 结构合格时仍能查询；缺/坏header、未知项/枚举、缺源/自链接/独立分量环失败，有来源 header-only 可是完整 fork，声明图不证明来源健康。深链/共享祖先不递归或二次方重追；不逐边审计源ticket，实际历史访问坏来源另须失败 |
| ListHeadOrigins额度/结果 | 零/负限额先于I/O；全部正式节点含无来源根计费，恰额EOF成功，超额不打开/读内容/扩表；全无来源成功空边。末close/取消/分配/图错误不交部分成功；三个自有字段/full ticket而非源EntryId、无稳定顺序、正常接口不能改backing，关owner后仍有效；visitor内查询不覆盖历史临时槽 |
| 应用互操作 | 历史停止后fork/rewind或无来源CreateHead；CreateTag另捕获当时当前Heads，应用解码/数据失败不回退；无关data Builder活跃仍能基于完成闭包fork/tag。固定槽与动态LocalId/正确库冷重开，富元数据整体发布；独立目录登记的未登记窗口由应用核对，不声称跨Head原子 |

时间与bytes验收按专项接线，继承项保留实际源时间，真实 CreateHead/PublishHead/rewind 重新取时但允许同值；fork 不取 Entry 时间，时钟回退不改变链序。tag 复用08时间意义，独立元信息 schema/CRC、canonical成员表、名称/hash/请求范围损坏隔离及发布/元信息公面详细向量由09负责。

[07 CU](07-versionstore-cross-ref-conditional-update.md)已正式接纳。新增向量包括成立 CU 作为来源/历史节点、跳过未提交组但 exact source anchor 必须拒绝、CU→header-only 子只保存准确来源，不生成新 Entry、成员校验及异长结构后继步骤先扣预算、peer 不加入继承 visited 与短期句柄单次清理。ForkOrigin 始终指向实际 Head/frame，ListHeadOrigins 仍只验证 header 声明图，HeadSetTag成员是正式引用，其创建确认集合与Audit检查按09执行，fork 与 tag 均确认各自实际接受 Entry 所依赖的来源/CU 文件，不递归确认祖先。旧 S5 Accepted 不覆盖本轮 CU，新增结果见[07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。

以上支持实施交接；源码、进程中断、平台/规模与包资格仍须实际取证，不要求首版实现分段、checkpoint、业务谱系或精确尝试追踪。
