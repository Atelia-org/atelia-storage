# 引用初始化 fork 重构方案（临时）

状态：2026-10-17，方案已完成三个独立 subagent 的源码可行性复核及主线程裁决，未发现设计阻断；尚未实施或取得运行资格。无旧数据/API 兼容要求。

源码核对基线：`731eef7cd3158572532fbeea7ba95a79213a7328`。主要落点：[创建](../../src/HeadStore/HeadStore.cs)、[fork](../../src/HeadStore/HeadStore.Fork.cs)、[发布读取/CU](../../src/HeadStore/HeadStore.ConditionalRead.cs)、[历史](../../src/HeadStore/HeadStore.History.cs)、[tag](../../src/HeadStore/HeadStore.Tags.cs)、[Audit](../../src/HeadStore/HeadStore.Audit.cs)。

本文是施工与验收清单，不成为长期规范。实施完成后，将合同修订进第 10 节列出的现有文档，记录实际验收范围，然后删除本文及导航链接；不再新增一套长期设计/验收文档。历史 evidence JSON 和旧轮次资格保持原样。

## 1. 目的与边界

下游 [DurableGraph R2](../../../durable-graph/docs/design-branches/0096-history-and-head-model/headstore-requests.md) 要求 fork 不增加一次领域存档。当前 ForkHead 复制准确来源 payload，产生新的本地初始 Entry，迫使消费者区分克隆与真实发布。

选择引用初始化：新 fork 仅写带准确 ForkOrigin 的 HeadHeader，不写克隆 Entry。HeadStore 仍只解释不透明 payload、物理发布及来源关系，不解释 DG Parent、业务存档、标签或对象图。R1 的公开 HeadEntryId 持久化接口另行设计，不作为本轮前置。

不保留 clone/reference 双模式，不增加迁移 reader、引用 Entry 类型、持久依赖图、引用计数、GC、删除、Head 重定向或通用图框架。CreateHead(payload) 仍创建无来源 root 和真实初始 SingleEntry；应用显式复制 payload 创建独立 root 的能力保留。

## 2. 唯一语义

- ForkOrigin 永久指向同库实际发布的准确 Entry（HeadId + 完整 SizedPtr），不指向另一 Head 的当前值。header 坐标本身不签发已检查的 HeadEntryId。
- 当前值：从本地尾向前选择最新成立的 SingleEntry/CU；只有正常抵达已检查 header 且没有本地已接受 Entry 时，才精确读取 ForkOrigin。完整坏记录、CRC/envelope 错误、扫描错误不能触发来源回退。
- 无来源 root 必须有直接初始 SingleEntry；有来源 fork 可以没有本地记录，也可以以 SingleEntry 或 CU 开始。无来源 header-only 文件仍非法，不能统一视为有效空 Head。
- 首次本地发布后，读取该已接受本地 Entry 不额外检查祖先；历史与 Audit 按其范围检查来源。未成立 CU 可以存在于首个已接受 Entry 之前。
- ReadHead(C) 返回真实 Entry：若 C 尚未续写，来源为 a1，则 ID、RecordedAtUnixMs、Payload 均属于 a1，不伪造 C 的 Entry。
- 不按 payload 去重；相同 bytes 的两次真实发布仍是两个 Entry。只消除 fork 自动产生的克隆。

| 操作 | 固定来源 | 当前 / 逆序历史 |
| --- | --- | --- |
| A 有 a0、a1，从 a1 fork C、D | C→a1，D→a1 | 两者当前 a1；历史 a1,a0 |
| 从未续写 C 的 ReadHead(C).Id fork E | E→a1 | 直接读 a1，不经过 C |
| C 发布 c1，再从 c1 fork F | C→a1，F→c1 | F 当前 c1；历史 c1,a1,a0 |
| A 随后发布 a2 | 上述来源不变 | C/D/E 不随 A 漂移 |

未续写 fork 的反复分叉不会累积当前值解析链。真实发布形成的历史仍可跨多个 Head；沿现有有界历史遍历处理。声明图按实际 Entry 归属建边，不保存用户从哪个尚未续写的界面分支发起操作这一额外事实。

## 3. API 与格式硬切

- 两个 ForkHead 重载改为 AteliaResult<HeadId>，out PublicationOutcome 保留。返回新 Head 身份；调用者已持有准确来源 ID，无需新的复合返回类型。
- ReadHead / ReadEntry / PublishHead / PublishHeads 的类型保留。写入目标必须来自选定 HeadId，不能取 ReadHead 返回值的 Id.HeadId 代替。
- ListHeads 包含已正式发布的 header-only fork。ListHeadOrigins 继续是声明图查询，不因此变成源 Entry 内容验证；准确来源在读当前/历史/Audit 时验证。
- HeadSetTag.Entries 保持 IReadOnlyDictionary<HeadId, HeadEntryId>，移除 key == value.HeadId 的约束。
- HeadStore 统一格式门禁从 2 升至 3，拒绝格式 1/2；更新 HeadStore 的控制/Head/tag header 版本检查及 fixtures。RBF3 和 FrameStore 格式不随之升级。无猜测旧布局或兼容分支。
- HeadHeader 的来源字段与布局可保持。fork 不再产生 Entry 时间；本轮不新增 Head 创建时间字段。
- Tag 成员固定 16B：selected LocalHeadId:uint32 + entry-owner LocalHeadId:uint32 + PackedSizedPtr:uint64，沿用 little-endian。按 selected ID 严格递增；两 ID 非零、同库上下文、ticket 完整非零；value 可重复。元信息 EntryCount 继续表示映射成员数。
- 所有容量推导、尺寸提前检查与 metadata-only 路径统一消费新成员宽度；不增加特殊压缩或重复值表。

## 4. 创建及持久化

保留占用目标先拒绝、自动编号、creating 私有文件、一次正式文件发布和 Outcome 边界。

1. 准备目标和 header 尺寸，精确重验来源 Entry（包括来源 CU 的成立资格及 anchor Fence）。来源不可接受即失败，不能选择更旧记录。
2. 从同一次已接受来源读取收集确认集合 D：普通 Entry 为来源文件；CU 为来源及其成员文件。由 ReadExactPublication 透传可选集合给 ReadPublication，在原 CU lease 内收集，不再次读取 CU；只有完整 exact 读取（含 Fence）成功后才使用集合。复用现有 tag 的逐文件确认原则，不递归检查 peer 的旧当前值或祖先。
3. ConfirmDataDurable → 按 LocalId 去重排序 flush D → 创建私有文件 → append header → flush → close → 正式发布。
4. Outcome 的 Unknown 从正式发布尝试开始；Confirmed 在正式发布成功后设置。私有 header 的存在不等于 Head 已发布。source/peer 确认失败不得输出正式子文件。

不再写初始 clone，不复制来源 payload 作为子输出。来源的完整校验仍需要读取 payload；是否消除该读取的临时复制由局部实现决定，不作为新协议或优化工程。

CU 来源必须扩大确认集合：现有 clone 路径仅 flush exact source file，不能原样沿用。确认的是所引用 CU 的直接成立依赖；不会递归 flush 每个 peer 的历史。

引用初始化的实际代价：首次本地发布前，当前值依赖来源和必要 CU peers 可读。来源失效必须可见，不改读父 Head 当前值；已有本地健康发布的当前读取仍不预检来源。GC/删除策略不在本轮。

## 5. 当前读取、首次发布与恢复

将内部 InitialEntryOffset 的含义改为 header 后的 RecordsOffset（或等价清晰名称），避免继续把第一物理记录与必有初始 Entry 混为一谈。集中表达 root/fork 的首记录约束，清理 ReadPublication、ReadExactPublication、CheckPublicationFrame、CheckLocalCurrent、CheckConditionalObservation 的重复旧假设。

ReadPublishedCurrent 只增加“抵达自身 checked header”分支：无来源报错；有来源调用准确发布读取并要求结果非 null。严禁调用 ReadHead(sourceHead) 取可变末端。头部不计入 Entry，不构造空 payload 哨兵。

发布准入继续检查本地结构及本地 CU self，不为 PublishHeads 增加所有旧值解析。header-only fork 可作为既有发布目标；root header-only 不可。首个 CU 的判据仍检查真实成员记录，不能借判断成员资格递归求其当前值。

首次 append 的不完整物理尾由既有 RBF 恢复处理；正常恢复到 header 后，fork 当前值为准确来源。保留 CompletedTail 与 TruncatedTail 的区别：若恢复补成完整记录，按该记录及 CU 判据选择当前值，不能把所有中断均断言为回到来源。完整合法但未成立 CU 正常跳过；本地和同 ticket 的必要完整记录、必要结构检查中已观察到的错误阻断回退。首个 CU 的异长位置复用消歧直接复用已有 checked-header 前驱识别及结构后继验证，只放开 fork 首条 CU，不增加第二套消歧算法。异长替换仍只以结构见证否定旧 ticket，不额外审计替换 payload/meta 或替代组；未访问替换 payload 的损坏可以不阻断此否定，完整 Audit 则仍应报错。

当前值来源读取默认使用 GetOrOpen(source owner) → CheckHeader → ReadExactPublication(source ticket)。GetOrOpen 已复用 history/owned 句柄，CU peer 查找也会复用它们；正常抵达 header 时此前 CU peer 已释放，不需要新增来源临时句柄槽。准确读取增添可选依赖收集参数，供 fork/tag 共用；不调用递归 ReadHead，也不用 conditional-peer 槽承载来源本身。保留 owner fault/Dispose 与 RecoveryReports 的现行边界。HeadRecordReader 的旧初始化辅助函数随测试/夹具更新，不扩为第二套 CU/fork 读取实现。

## 6. 历史、来源图与 Audit

历史对 root 保留现有快路径：交付位于 RecordsOffset 的已检查初始 SingleEntry、visitor 返回 true 后直接 Complete，不为再次观察 header 或 EOF 增加读取；返回数恰好用完预算仍能完成。fork 则在正常抵达 checked header 后转向准确来源 anchor，按现有 bounded reverse scan 继续。首条本地 CU 未成立时可以没有任何本地交付项。删除 childInitial、clone payload 比较和克隆交付，不给 header 分配 EntryId。

保留来源 cycle 检查、固定扫描上界、准确 anchor 的已发布资格、visitor 停止、取消及 work/entry 预算。没有克隆可交付后，抵达来源才交付首个 Entry；不得在预算耗尽后免费验证来源。停止不证明未访问祖先健康。

ListHeadOrigins 的来源仍指向实际 Entry 所属 Head，原有声明图算法可以保留。不得因正常 writer 不会产生环而移除针对损坏镜像的自链接/缺源/循环检查。

Audit 允许有来源的零本地 Entry，允许其第一记录为 CU；仍验证源 ticket 是源物理链中的实际已接受 Entry。删除 AuditForkClone 及比较子 payload 的读取。每个源 Head 可只保留待验证来源 ticket 的 HashSet，扫到已接受记录时移除，正常 EOF 时仍有待验项则失败；共享来源只需验证一次，不再保存逐 ticket 的子节点列表或逐 child 重开。OriginCount 仍按来源边计数。EntryCount 仅计真实已接受 Entry，不能给空 fork 增加计数。

## 7. HeadSetTag

CreateTag 逐个捕获 selected Head → 当前准确 Entry，允许多个 selected Heads 指向同一 Entry；持久化时同时保存 key 和 value 的实际归属。禁止只从 entries[] 推导 key。

确认集合应覆盖所选 Head 文件（包括无本地 Entry 的 header）、实际 Entry 文件和接受该 Entry 所需 CU peers。沿用 data-first、去重排序确认及 tag 最后发布；选择集合与依赖集合继续分离。

ReadTag 仅检查完整成员表，不访问成员文件；ReadTagInfo/ScanTags 继续只检查元信息。Audit 对 key 和 value 分别检查：selected Head 存在；同 Head value 是其实际已发布 Entry；跨 Head value 与该 selected Head 的固定 ForkOrigin 精确相符，且准确来源 Entry 已发布。不要求 value 等于 selected Head 的今日当前值，不回放 tag 创建时刻，不增加时间戳排序证明。复用 Audit 已建立的 checked Head 节点表检查 key/Origin，不为每个 tag 成员额外重开 selected 文件；value 的准确读取仍保留。

## 8. 最小验收矩阵

| 边界 | 必须证明的结果 |
| --- | --- |
| 创建与冷开 | 两种 ForkHead 目标均只写 header，返回新 HeadId；关闭全部 owner 后仍可列出、读取、继续发布；无来源 header-only 仍拒绝 |
| 多孩子 / 连续空 fork | C/D/E 均直接指向 a1；不生成 c0；父推进不改变来源；声明图按实际归属建边 |
| 首次本地发布 | SingleEntry 与成立 CU 均可成为第一条本地记录；随后当前与历史正确；同值真实发布不合并 |
| 首次 CU 回退 | 成员指定位置恰为 EOF/合法替换导致未成立时回到来源；缺少整个必要成员文件仍报错；必要完整记录或结构错误阻断回退；header 前驱下同长/异长 ticket 复用符合原 CU 判据 |
| CU 替换访问范围 | 异长替换的结构见证可否定旧 ticket，不读取替换 payload；该未访问 payload 坏 CRC 时当前值仍可回到来源，而 Audit 失败 |
| 父子 CU | 引用 source Entry 所属 Head 与 header-only 子 Head 同批更新，无递归求值；成功/部分写入/冷重开均准确 |
| 多成员 CU | 多个 header-only fork（含共享来源）参与同一组，未成立时各自回到固定来源，组表仍只含实际发布目标 |
| 来源 CU | fork 准确引用成立 CU；未成立/坏来源拒绝；data、source、peer flush 故障在正式子输出前阻断 |
| 创建进程窗口 | 私有 header 写入/flush/关闭及正式发布前后终止：不暴露半初始化正式 fork；已正式发布者不因无本地 Entry 被丢弃 |
| 首次写进程窗口 | 不完整尾按 RBF 恢复后可回到来源；完整坏记录不伪装成无本地值；RecoveryReports 保持物理事实 |
| 历史 / Audit | 无重复克隆；精确旧 anchor 排除后续；源坏/环/缺源失败；预算、停止、清理与统计符合新语义 |
| tag | A/C/D 可共用 value，保留全部 keys；冷开与后续推进不改变 tag；尺寸/非 canonical 表由 ReadTag 拒绝；结构合法但跨 Head 错来源/缺 key 文件由 Audit 拒绝，ReadTag 不访问文件、仍可成功 |
| tag 确认集合 | 仅选择 header-only C，来源为 A 的 CU、peer 为 B，确认集合必须为 {C,A,B}；共享来源去重，data 或任一必要文件确认失败均不追加 TagRecord |
| 格式与资源 | 统一拒绝旧格式；header-only/CU 组合资源单次释放；只读不修复；已有 owner guard 和错误传播不弱化 |
| 历史回调句柄 | visitor 正持有来源 history 句柄时读取 header-only child，来源为 CU：复用原句柄、peer 读取不冲突、不改变固定历史上界 |
| 实际消费者 | DG 风格消费者用选定 HeadId 写入，用 ReadHead 返回的真实 EntryId 作前序；首次保存无需克隆过滤；不要求实施 DG 重写 |

## 9. 实施顺序及验收边界

1. 先修改格式门禁与内部首记录约束、准确读取/当前读取/首次 CU 路径；用 root/header-only fork/CU 镜像覆盖最窄机制。不引入长期过渡模式。
2. 接通 ForkHead 返回值、依赖确认与正式创建；同步历史/来源图/Audit。此时可以用公开 API 覆盖连续 fork 与父子 CU。
3. 同步 tag 16B 映射、确认集合与 Audit，更新 examples、tools、tests 和源码联调调用方的编译使用；兄弟仓应用实现改动另按授权。
4. 修订现有规范、测试断言、smoke 输出及交付导航，执行独立复核。Windows build/test/pack 串行：先 Release build，再匹配配置 --no-build test；进程窗口单列结果。
5. 包交付时用新版本执行六包打包及四个隔离 public PackageReference smoke。源码通过不沿用旧包 Accepted；公开发布、Linux、设备断电等只记录实际执行范围。

本轮仅编写和复核方案，不运行以上实施验收，不将源码静态可行性标为运行资格。最终实现与测试通过前，现行规范仍描述 clone 行为；不提前将候选方案混入现行合同。

## 10. 沉淀与删除清单

| 现有文件 | 实施后吸收的内容 |
| --- | --- |
| [04](04-versionstore-publication.md) | 新 root/fork 初始化、格式门禁、创建与 Outcome、当前值与恢复 |
| [05](05-versionstore-names-and-indexes.md) | ForkHead 返回身份、无 clone 历史、准确来源与声明图、成本及新向量 |
| [07](07-versionstore-cross-ref-conditional-update.md) | fork 首条 CU、header 前驱复用、失败回到来源；保持成员判据局部性 |
| [08](08-versionstore-record-metadata.md) | 引用不新建 Entry 时间，真实 Entry 时间保持 |
| [09](09-versionstore-immutable-tags.md) | key/value 分离、16B 布局、确认集合、Audit 引用检查 |
| [11](11-versionstore-root-payload-bytes-api.md) | ForkHead 新返回值、ReadHead 实际 Entry 归属、payload 所有权 |
| [12 实施计划](12-versionstore-implementation-plan.md)与[现有验收记录](12-versionstore-final-acceptance.md) | 追加本轮具体 revision、运行范围及未覆盖项；不覆盖历史结果或重标旧证据 |
| [07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)、[16 验收](16-headset-tags-acceptance.md) | 如需详细专项结果，在原文件追加独立新轮次并交叉链接，不复制整套矩阵 |
| [15 重命名记录](15-headstore-renaming-plan.md)、[16 tag 历史方案](16-headset-tags-plan.md) | 保留当轮决定与格式2事实，只追加当前合同已转向 04/05/09 的提示，避免旧 clone/key==owner 描述被当作现行规范 |
| 模块/root README、AGENTS、公开示例、既有交付入口 | 当前能力、编译用法与实际资格；删除过时 key==owner/clone/必有本地 Entry 说明 |

实施完成的关闭条件：上述合同已各归其位、验收事实有现有落点、代码/测试/示例一致；随后删除本文和 README 中临时导航。无需永久保留候选比较、施工步骤或本轮静态评审日志；Git 历史可追溯它们。

## 11. 设计可行性复核

三个只读工作包均已完成，结论均为源码可行、无设计阻断；未修改生产代码或运行测试。主线程核对关键源码与现有测试合同后已将修订写入正文：

| 复核范围 | 采纳的修订与化简 |
| --- | --- |
| 当前读取 / CU / 恢复 | 来源复用 GetOrOpen 与准确读取；保留现有 header 前驱消歧；明确异长替换的必要访问范围和 CompletedTail/TruncatedTail 区别 |
| tag / 持久化 / 格式 | 显式确认 selected 文件；exact 读取透传同次 CU 依赖；ReadTag 与 Audit 分工；Audit 复用 checked 节点表；统一格式常量硬切 |
| 历史 / Audit / 创建发现 | 保留 root 精确预算完成快路径；Audit 删除 clone 子文件比较并用来源 ticket 集合；复用正式路径发现；补历史文档转向提示 |

关键回归依据包括[精确历史预算测试](../../tests/HeadStore.Tests/History/HistoryBudgetTests.cs)和[CU 替换访问范围测试](../../tests/HeadStore.Tests/ConditionalUpdateFaults/ConditionalUpdateReuseAndMemberTests.cs)。这些是现行行为证据，不是新方案已通过测试的声明。实际 header-only 冷重开、首次 CU、进程窗口、资源异常及包消费均按第 8/9 节在实施后验证。本节随本文删除，不另建永久评审记录。
