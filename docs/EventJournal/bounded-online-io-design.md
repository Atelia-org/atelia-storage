# EventJournal 长期运行性能重构方案

日期：2026-09-29。状态：经 dialectical-simplification 三方独立审查、交叉质询及一项第三轮裁决后的设计建议，尚未实施。
代码基线：`bb7c4fb3eb6477783c70ee61bc62b832be195d07`。
本次仅设计；不授权实现、改写现有数据、发布包或切换消费者。
审查过程与删减依据见 [审查记录](bounded-online-io-review.md)。本文件描述目标合同，不覆盖当前已发布版本的使用指南。

## 1. 最小模型与需求账本

保留不可变 Event + Parent、branch name → 稳定 RefId、每 ref 的 append-only move log、独立不可变 tag。日常打开只读取定位信息、catalog 快照及有限后缀；ref 当前状态直接读取末条 move。完整历史校验、损坏修复和索引重建交给离线 toolkit。

| 要求 | 来源 | 处理 |
| --- | --- | --- |
| 日常读取不校验全部历史；完整校验属于离线 toolkit | 用户本轮明确决定 | 新合同，优先于旧设计及旧测试 |
| 可升级 wire-format、拒绝旧数据；唯一自有历史另行迁移 | 用户本轮明确决定 | 不建设双读、双写或在线迁移 |
| EventAddress、Parent、RefId 的精确身份；同名重建不是同一 ref | 当前代码、测试及 SessionJournal 消费 | 保留身份语义；保留现有宽度是本方案的最小改动选择，并非用户禁止改变 |
| tag 不可变、独立名称空间、每次 Resolve 校验所指事件 | Tags.cs、ImmutableTagTests、public smoke | 保留；删除打开时全体目标校验 |
| 单 driver、串行操作、无跨实例 CAS、无 live 外部改写 | 两层 README 与实际缓存模型 | 不引入多 writer 协议 |
| Event durable 后再发布 ref/tag；失败结果不明不能盲重试 | 现有写入路径、tag 故障测试 | 保留并覆盖新元数据发布 |
| reflog、reason、fork 来源仍可查询/审计 | 现有 ReadReflog、BranchRewind 测试 | 保留历史，不自动清除 |
| Dispose 释放资源；只读不得创建、修尾或写 cache | 当前测试 | 保留 |
| 不承诺目录项在硬件断电后存活 | 当前 ConfirmDurable 合同 | 本轮不扩大；可观察的元数据缺失/矛盾失败关闭，不能保证检测全部回退 |

现有消费者证据：本仓 [EventJournalSmoke](../../examples/EventJournalSmoke/Program.cs) 使用 tag 与 branch；兄弟仓 `/repos/Atelia-org/atelia/prototypes/SessionJournal` 固定 RefId 并读 GetHead、遍历 Parent；`SessionJournal.Cli/BranchRewindCommand.cs` 与测试依赖 rewind/reflog。其 `eng/StorageDependency.props` 此次读取仍固定 `0.1.1-preview.2` / `976aa345`，不是新 tag 包的已部署证据。tag 设计指出 DurableGraph 是发起方，本次在该兄弟仓未找到其项目，不能声称已完成此消费者核查。

故障模型：单 driver 正常串行写；需要处理进程中断、部分写入和 I/O 异常；不处理并发外部文件改写。继承现有单文件 flush 合同，未增加目录项在硬件断电后的存活保证。2026-09-29 编写task级工单时，用户明确确认沿用严格坏尾停维、首版toolkit不提供修复的取舍；这项确认不是实施或操作现有数据的授权。

本方案接受 live branch/tag 数量的必要成本，不承诺在无限活跃实体下常量内存。目标是固定当前工作集时，在线成本不随已积累历史增长。

## 2. 代码证据与问题范围

| 位置 | 当前行为 | 累积维度 |
| --- | --- | --- |
| [LoadRefState](../../src/EventJournal/EventJournal.Refs.cs)、[ReadAllMoves](../../src/EventJournal/RefMoveStore.cs) | 读全 reflog 为 List，逐条 checked-read NewTarget | move 数及重复目标的 stored bytes |
| [AppendRefMove](../../src/EventJournal/EventJournal.Refs.cs)、[DiscoverFlatActiveSegment](../../src/RbfSegmentStore/RbfSegmentStore.cs) | 每次写重新打开、枚举全部 segment、执行 recovery | segment 数、句柄及系统调用 |
| [ReplayRefOpLog](../../src/EventJournal/EventJournal.Refs.cs) | 从零扫描；knownRefs 保留历史创建身份 | 累计 create/fork/archive/tag 数 |
| [ValidateTagTargets](../../src/EventJournal/EventJournal.Tags.cs) | constructor 校验所有 tag 的目标 | tag 总数与目标 bytes |
| [ComputeNextSequenceNumber](../../src/EventJournal/EventJournal.cs) | 所有打开入口扫描所有 event segment/header | 全部物理 events，包括 orphan |
| [OpenReadOnlyExisting](../../src/EventJournal/EventJournal.cs)、[ValidateActiveTail](../../src/RbfSegmentStore/RbfSegmentStore.cs) | 急切加载全部 live refs、扫描 active framing | 所有 live reflog、active segment 长度 |
| [ForwardPlan](../../src/EventJournal/EventJournal.ForwardPlan.cs) | 每个 head 落盘完整 redirects；binding 额外持有 plan | 历史 head 数；交错写时可出现二次累计量 |

补充调查纠正：只优化 refs 仍不能消除冷启动衰退，因为 `ComputeNextSequenceNumber` 无条件全量扫描 event store。健康 writable recovery 从尾找首个有效 frame，不是每次全量扫描 active 文件；不要把它误报为每次写 O(move 数)。

## 3. 在线与离线边界

新格式的 `OpenExisting`、`OpenOrCreate`、`OpenReadOnlyExisting` 都是日常打开，不自动 recovery；只读仅表达访问权限。未知版本、定位损坏、不可解释尾部、catalog 缺失返回明确错误，不静默退回更早 head，也不隐式进行全库重建。

| 操作 | 在线验证 | 离线验证 |
| --- | --- | --- |
| Open | format、locator、catalog 快照 CRC、有限 suffix、必要尾部 | 全部 framing/CRC、目录连续性、历史语义 |
| OpenBranch | 名称和 catalog 当前绑定 | 全历史绑定过程 |
| GetHead | 指定 ref 的 Init 与末条 move、末条非空 target checked read；缓存后不重复 | 完整 move 序号、Old/Expected 链、历史目标 |
| ResolveTag | 本次名字对应的 target checked read | 所有 tag 历史绑定和目标 |
| Advance/Move/Create/Fork | CAS、当前指定目标、Advance 的直接 Parent；durable 顺序 | 无关历史 |
| ReadEvent | 本次完整帧 CRC、codec、header/hint | 未被读取的 events |

不存在“daily open 成功即全库健康”的断言。坏的历史 target 不再阻止无关读取；它被直接访问或 audit 时仍报错。当前 head 自身坏了，GetHead 失败，不能悄悄回退旧 move。

查询未知 tag publication 的结果仍只能严格打开；严格是“不修复”，不是“扫描全历史”。tag binding 不可解读时不能报告 TagNotFound。

**明确的可用性变化**：旧 writable open 默认自动修尾，新版本正常入口不修尾。一次普通进程中断也可能留下坏尾并使 journal 暂时不可用；首版 toolkit 不提供任意坏尾的修复闭环，需恢复经过核对的完整备份，或另行实施针对该故障的修复。若后续要求与旧版相同的自动恢复可用性，应单独设计有限修复协议，不能隐含加回 daily open。

## 4. 新格式范围

引入 journal layout v2，沿用 EventFrame v2、RefMoveFrame v1、RefOpFrame v1 和 TagBindingFrame v1 的有效字段。不为性能重写 payload codec 或地址。

新增三类小型持久记录：

1. 根 `journal.format`：magic、layout version；初始化最后发布。无 marker 的已有非空目录拒绝打开/自动创建，提示离线迁移。拒绝未知版本与非零保留位。
2. 每个 segmented store 的 `active.segment`：magic/version、layout、active SegmentNumber、CRC。只在初建/轮转更新，不保存每帧 head，不承担业务提交权威。
3. `refs/catalog.snapshot`：format、ref-op-log 精确 end-exclusive boundary、boundary frame ticket/CRC 见证、active names→RefId、tags→EventAddress、记录数与整体 CRC。不是第二份 ref head 表。

locator 与 snapshot 都有唯一固定路径，以同目录 temp → flush/close → replace 发布。使用同卷原子替换原语，支持的 Windows/Linux 文件系统必须通过进程中断测试；不得实现成 delete destination → move。原子可见性不等于目录断电耐久。没有正常读取时目录搜索“最新 generation”的路径。发布异常使实例 fault；重新打开只认可固定路径，临时文件由离线工具报告。不以旧快照兜底掩盖缺失/损坏。

日志仍是可离线重建 snapshot 的事实源；snapshot/locator 是日常可用性必需的派生入口，可观察的缺失/损坏显式 MaintenanceRequired，而非在线 O(history) fallback。CRC 不防止人为替换一个旧的、同样 CRC 有效的备份；备份/恢复必须以完整 journal 为单位，不承诺混搭文件可被自动鉴别。目录项在断电后整体回退/丢失也可能超出可检测范围，不宣称这种情况下全部已确认发布仍可恢复或必然被拒读。

旧数据拒绝发生在打开底层可写文件之前。旧 reader 不能安全消费新布局；不把新 marker 能约束旧 binary 视为保证。迁移写入新目录，原目录保留供旧版本工具只读核对。

## 5. 分段定位与 event 序号

新的 RbfSegmentStore 发行版本统一使用 locator 定位和严格打开；不在新 runtime 保留 legacy 自动发现/recovery fallback。旧实现留在旧 commit 或独立迁移进程。旧 `RecoverActiveTailOnOpen` 选项在新 API 中移除，不能保留一个被静默忽略的开关。EventJournal 的三个 recovery options 一并移除/调整，消费者编译错误要求显式迁移。该变化同时影响直接使用 RbfSegmentStore 的消费者，必须明确 breaking release、独立包版本与依赖下限。通用层不解释 Event/ref/tag。

RbfSegmentStore 自身也在任何可写打开前验证 locator 的 magic/version/layout；旧的非空 segment 目录不得被 OpenOrCreate 当作新 store。初始化的新 store 完整发布 locator 后才成功；有半成品的重试不能覆盖旧文件。

初建：创建 segment 1、flush，再发布 locator。轮转：flush 旧 active → create next empty segment 并 flush → 发布 locator(next) → 才允许向 next 追加。不先 append 再发布 locator。

崩溃窗口：

| 位置 | 固定 locator | 日常处理 |
| --- | --- | --- |
| next 创建前 | old | 继续 old |
| next 已创建，locator 未发布 | old | 定点发现 next，立即 MaintenanceRequired，不删除/接管/继续向 old 写 |
| locator 已发布，next 还为空 | next | 合法空 active；需要末条业务记录时最多读取上一段末帧 |
| next append 撕裂 | next | 日常报错；首版toolkit只报告，后续定向修复或恢复备份 |

每次从 locator 打开时，精确探测 `active+1` 的规范路径；存在则 MaintenanceRequired（`uint.MaxValue` 时不加一）。这防止可观察的旧 locator 指向旧 active，而 next 已含发布数据；它不是完整断电证明：next 目录项也丢失，或更远段存在而 next 消失，仍可能无法发现。无目录枚举，不搜索“真正最新段”。同一实例轮转成功后已有内存 active 可继续使用，不反复重新打开 store。

不允许连续多个空 historical segment；每次只有到达阈值的非空 active 才轮转。读取历史地址按 SegmentNumber 直接定位，池容量有界，不枚举目录。缺段在访问时失败，全连续性检查离线执行。

末帧读取必须区分：物理长度恰为 4 bytes 且 HeaderFence 正确才是合法空文件；其余短尾不是 empty。现有 reverse enumerator 在短于最小帧时可直接结束，故不能仅以 `MoveNext()==false` 判空。使用 `showTombstone:true` 并且只取紧贴 EOF 的一帧，验证 framing/CRC 和完整 EOF 位置；遇到坏尾不跳过。active 空且编号>1时，只检查其前一段的末帧，该段不允许空。

EventJournal v2 writer 保证 event sequence 随物理 append 严格递增，checked 溢出；失败写后 fault，不继续追加。日常打开从 active 或上一段末 event 取得 next sequence；只需检查末 frame 的完整 CRC/header，不取全局 Max。不接受末尾 tombstone/未知业务帧或任意向后搜索。完整历史序号校验交给 toolkit。

## 6. Ref 当前状态与 writer 生命周期

`LoadRefState` 改为：读取该 RefId 创建记录（ticket 直达，确认 Create/Fork）→ 通过 locator 打开 ref object → 校验第一段 Init → 读取 active 或上一段末 move → 解码 RefId/operation/sequence/NewTarget → 若 non-null 则 checked-read 本次 head。

末条 move 已包含完整结果状态，因此不新增 head checkpoint、PrevMove 指针或 ref-local state 文件。序号连续性与过去 CAS 链属于 audit；在线 writer 从已读末 sequence + 1 写入。Close 返回 RefClosed；Init 的 null head 是合法 unborn。

局部语义检查不能只调用 codec.Decode：allocation 必须确为该 ticket 的 Create/Fork；Init.RefId 必须匹配、sequence=1、Old/Expected=null、NewTarget 与 allocation.StartHead 一致（不重读历史 StartHead payload）；末 move 的 RefId 必须匹配，Init 仅允许物理首末为同一帧，其他 operation 的 sequence>1、Old==Expected、Advance.NewTarget 非空、Close.NewTarget 为空，递增 checked 防溢出。完整连续性、过去 Close 后是否又写入等关系交给 audit。真实末帧损坏必须报错，不能跳过它读取较旧 move。

使用一个有容量上限的 ref entry LRU，entry 统一持有状态和可选打开的 RefMoveStore；无独立无限 `_refStates`。evict 释放 store，冷读仍是有限帧读取。Archive 清除该 entry；plan 仅由统一预算缓存持有，没有待清理的 per-ref binding。为 O(1) Archive 维护 active RefId→name 的内存反向映射。它与 names 同时更新，不增加持久权威。

单 writer 持有句柄，不等于一直持有 active writer lease。每次 append 现借现还；Dispose 释放全部 entry，即使一个释放失败也尝试其余资源。任何 append/flush/metadata 发布异常后 fault，避免缓存状态与磁盘状态分叉后继续写。

保留 Create/Fork → Init → BindName 的发布顺序，不引入多文件事务。分配成功但未 Bind 的 ref 仍是 orphan；Close 成功但 Archive 未完成时，名字可暂时指向 closed ref，GetHead/写入失败，audit 报告未完成归档，后续定向修复处理。首版rebuild不补写Archive。日常打开不为检查这类状态遍历所有 refs，不自动复活或重绑定名称。

## 7. Catalog checkpoint

ref-op-log 仍单文件、保留全部历史，RefId 仍为原 Create/Fork ticket。快照消除重放成本，不做物理压缩/重排；`SizedPtr.MaxOffset = 2^40-4` bytes 是 frame 起始偏移上限，不能把此方案描述成无限容量。容量预检覆盖整个 Create/Fork+BindName 最坏字节，越界在开始发布前拒绝，不 wrap。若未来确需跨 segment 的 RefOpLog，另行升级身份格式，本轮不预做。

在正常串行 API 操作边界写 snapshot；初建发布空 snapshot。snapshot 只含 active branches 和所有不可变 tags，不含 archived refs、历史 knownRefs 或各 ref 的 heads。从 snapshot boundary 到 EOF replay suffix，BindName 的 allocation 验证用 RefId 直接 checked-read 创建帧，不重建全部 knownRefs。

加载snapshot检查名字规范、地址/RefId编码、重复名字及同一RefId的多重active绑定；suffix延续相同局部语义检查，不能以CRC正确替代schema验证。branch与tag同名仍合法。snapshot不要求checked-read全部所指目标或打开全部ref对象。

令 `L0` 为上次 snapshot 的 live entry 数，`Lnext` 为本次操作完成后的预计 live 数，`d` 为已持久 suffix 条数，`r` 为本次最坏追加条数，`Q=max(1024,L0)`。每个控制操作在完成输入校验与内存准备后、任何本次发布之前，满足任一条件则先 checkpoint：

```text
d + r > Q                         // 限制后缀
L0 > 2 * max(1024, Lnext)          // 大量 archive 后几何缩容
```

checkpoint 记录操作前的已发布状态，不能提前把尚未 durable 的 archive 写入 snapshot。完成后重算 L0/Q/d 再预留；Create/Fork 预留2条、tag/Archive 预留1条。退出或重启不能绕过计数。几何缩容避免百万 branch 归档到1后，始终重读旧百万项快照；低于基数的常量余量明确允许。

**reader 同样有硬上限**：最多 replay Q 条 suffix；到限额仍未到物理 EOF 即 MaintenanceRequired，不再读下一条 payload。控制 frame 在读取/分配 payload 前检查长度上限，现有 codec 对应每条至多252 bytes（含尾 Fence，按 codec/layout 常量推导）；未知格式或超限不能忽略。snapshot 的长度/条目数/算术溢出也先检验，按记录流式解码至必要 live 字典，不额外保存整文件 byte array。

**必须真正在 boundary seek**：当前公开 `IRbfFile.ScanForward` 只能从文件头扫描。增加一个窄的 boundary-start 扫描入口，复用已有 `RbfForwardSequence.dataStart`；检查对齐、范围、边界前已验证 frame 的 ticket/CRC 见证及 end-exclusive 位置。空日志 boundary=4，用明确 empty anchor。不能用从头 ScanForward 再 Skip 模拟后缀。文件短于 boundary、anchor 不匹配或 EOF 不完整均报错。

这样累计 H 次历史 churn 不进入正常打开成本；checkpoint 的 O(live entries) 写入按 Q 次元操作摊销。代价是 checkpoint 那次操作仍会有 O(live entries) 的延迟尖峰。本轮接受这一显式权衡，不建设后台任务、分页 B-tree 或数据库引擎。大量 active tags 是必要 live 数据增长，不能声称 O(1)。

发布顺序：确保 boundary 前 ref-op-log 已 durable → 序列化快照 → temp durable → replace 固定 snapshot。旧 snapshot + 未超过Q的 suffix 或新 snapshot + 更短 suffix 均合法；超限旧快照要求离线重建。历史日志从不在 checkpoint 后删除，中断不得截断 op-log 到 snapshot boundary。

checkpoint 故障与业务 publication 结果是两回事。前置 checkpoint 失败，本次 tag 尚未 Append，结果为 NotAttempted，但 driver fault；tag Append 开始为 Unknown；ref-op-log flush 成功为 Confirmed，此后内存安装失败也不能降级成 Unknown/NotAttempted。不在一次已确认 tag 操作尾部追加强制 checkpoint。

创建流程必须整体预留 frame 数，checkpoint 不切在 Create/Fork 与 BindName 中间。崩溃留下的 orphan allocation 无未来正常 Bind；后续救援属于 toolkit，必须同步重建 snapshot。不确定发布结果严格按当前日志后缀判定，不自动 retry。

## 8. ForwardPlan 与读取节奏

删除自动 compiled disk cache 写入/加载，旧目录不再读取，迁移时不复制它。保留一个有 entry/bytes 双预算的内存 exact-head cache 及已有 cached-prefix reuse。删除 `_forwardPlanBindings` 与依赖它的 ref-local tail-merge 路径；不另造 weak handle 或跨 ref plan 图。每次生成新 plan 不强制落盘，不引入持久 route DAG。

对于要求全历史的 `ReadChronologicalChain`，保留 O(N) 时间/返回空间的真实合同。Redirect 复制 O(k) 是这次完整读取 O(N) 的子项；不能把全量输出伪装为增量。

删除 compiled cache 后，writable reopen 的首次全量读取可能增加一次 O(N) Parent walk；rewind 不再保证 tail-merge 复用。这是用较少常驻状态和零自动磁盘积累换取的冷读取代价，必须测量并在交付说明中给出。内存预算只限制保留的 cache，不覆盖本次 plan 构建的 redirects、detectCycles 集合或输出 List；这些仍可能 O(N)。

范围读取 API 整体延期，本次不加接口，也不把 delta 读取验收算入完成条件。触发条件：找到真实增量调用方，并在同一任务中定义 exact ancestor/end-point 合同、上限与接入。现有全历史调用若每一步重读 N 条，累计 O(N²) 是请求量本身，本方案不宣称消除这一项。

## 9. 离线 toolkit

候选命令归一到一个独立 console project，不成为五个库的新生产包，也不引用 atelia 业务层：

- `audit <journal>`：默认只读，扫描 events、ref-op-log、所有 ref moves、tag 目标、Parent/sequence/CAS 链和目录结构；报告精确地址、计数、错误码，不输出 payload。
- `rebuild-indexes <journal> --output <new-dir>`：只对可完整验证的健康事实日志生成 catalog/locators 候选及来源摘要，不默认原地覆盖，不截断事实日志。无法唯一判定布局/发布边界、坏尾、缺段或互相矛盾的记录都报告并停止，不猜测哪个 segment 应当获胜。

需要停写/稳定副本；不承诺在线审计的一致快照。audit 不通过 daily open，以免缺 snapshot 就无法修复。保持完整 reflog 可流式输出；旧 ReadReflog 全量便利 API 可保留，但普通打开不调用它。

toolkit 不是启动前强制跑的服务。通用 `repair-tail` 延期，不单独建设旧 cache-prune 命令。首版有审计与健康事实的索引重建，不宣称完整崩溃恢复闭环；即使只剩一个未发布的空 next 文件，也先报告而不自动删除。不能把扫描最后可解码帧等价于证明所有已确认发布都保留。

消费者迁移必须区分存储与领域审计：SessionJournal 的 `SessionJournalOfflineValidator` 在 OpenReadOnly 后另调用 `ScanCheckedAuditEvents`，其所选 Parent 链业务验证继续保留；本次没有证据说它原先仅凭 open 就宣称全库通过。branch-rewind 的 exact ref/head/suffix 检查也继续保留，不能把全库 toolkit audit 加成每次 rewind 的前置。需要审计全部物理 events/reflog/catalog 时才显式调用 storage toolkit，并在报告标明各自覆盖范围。

## 10. 替代方案与当前取舍

| 方案 | 当前选择理由 |
| --- | --- |
| 只删除历史 target 校验 | 不足：event sequence、move replay、catalog replay、目录枚举仍增长 |
| 每 ref 独立 head snapshot | 删除：末条 move 已是完整结果状态，多一份同步权威无必要 |
| 全部 ref/tag/move 迁入 SQLite | 暂不选：引入第二套持久引擎/依赖；本次未发现比尾读与单快照更小的迁移模型 |
| 合并所有 move 到统一控制日志 | 暂不选：reflog 定位需要额外 per-ref index/prev pointer，破坏范围更大 |
| 自建分页 catalog、共享 route DAG | 延后：当前没有 live catalog 大到快照不可接受的实测 |
| 旧格式 fallback、双写渐进迁移 | 删除：用户明确允许 hard cut；虽可共享兼容实现，但未发现必须保留在新 runtime 的需求 |

## 11. 最小安全实施切片

以下是内部实现/测试顺序，只有一个完整 v2 对外交付门；不发多个中间格式，不把第一片描述成性能已收口。

1. **最小语义闭环**：format gate、locator（含next probe）、严格 EOF读取、尾读 ref/event sequence、局部语义与fault；Create→Append→Advance→close→read-only reopen→GetHead/ResolveTag。同步只读 audit 骨架，旧源码留给后续迁移解释。
2. **catalog 边界**：RBF boundary-start 扫描、snapshot/suffix 双侧预算、几何缩容、RefId 直达验证与发布故障窗口；再加入有界 ref entry cache，测冷热切换。
3. **派生缓存与离线入口**：停止 compiled cache、删除 ref-local tail-merge/binding、统一内存预算；完成 audit 与健康事实索引重建候选，不包含范围API或事实修复。
4. **唯一交付门**：以上全部完成；更新当前 README、旧设计与受影响测试的合同；消费者源码联调、直接 SegmentStore smoke、明确新包版本/依赖下限；依仓规提交后 pack/public PackageReference smoke。是否发布/迁移/切换另受会话授权控制。本次不执行这些动作。

后续迁移是独立任务：在新目录生成v2 locator/catalog，尽可能逐字节保留 events/ref-op-log/ref moves及其路径，从而保留 EventAddress/RefId；先对照旧代码核实全局 sequence 物理单调、无 tombstone/非法尾部、全部身份/绑定/历史语义符合新合同。不满足前提时停止并另定迁移，不默默重排事件。验收包括旧新事件数量、文件hash、selected heads、tag绑定、完整reflog与只读往返。不得复制旧 compiled cache，原数据不原地升级。

## 12. 验收与性能证据

既测时间/p50/p95，也记录稳定工作量：目录枚举数、header/full-frame reads、bytes、打开句柄、分配量、checkpoint bytes；不用一次小样本耗时冒充复杂度证明。

| 固定项 | 增长项 | 预期 |
| --- | --- | --- |
| 1 live ref、固定 head payload | 10^3/10^5/10^6 moves | 冷 GetHead 读 Init+末 move+当前目标；不扫历史 |
| 当前 ref/head 数 | event 总数、orphan 总数 | Open 不调用全 event scan；序号由末帧得出 |
| 1 live branch、0 tag | create/archive 10^3→10^6 次 | Open 只读快照+有界 suffix；无历史 knownRefs |
| 最终1 live branch | 先积累大live快照再archive至1 | 几何缩容后不反复读取历史峰值snapshot |
| 固定访问集合、cache capacity | ref segments 1/100/10000 | 打开/更新不枚举历史 segment |
| 固定小 active 集合 | 反复创建/归档 ref | ref cache 与 plan 实际强引用受预算限制 |
| 两 branch 交错写 | 每次全量读取 | 磁盘 cache 不增长；明确报告全量请求的二次累计工作 |
| 固定 tag target 大小 | live tags 增长 | Open 允许 catalog O(T)，不得读 T 个目标 payload |

故障验收：逐点中断 append/flush/rotation/replace；4B空文件与4B以上短尾；完整 frame CRC 坏、locator 缺失/旧locator+next仍在、snapshot 落后/损坏/超过Q、suffix 撕裂/巨型声明长度；tag NotAttempted/Unknown/Confirmed（含前置checkpoint失败）；Close 后 Archive 未成；只读运行前后 bytes/hash 不变；本次无需访问的历史坏而定位/身份/当前数据好时 daily 可用且 audit 精确报错；当前 target 坏时读取失败、不回退。进程中断测试不是目录项硬件断电保证。

语义回归：CAS 失败不写 move、Commit CAS 失败保留 orphan、fork 捕获 source identity/head/move number、同名重建 RefId 不复用、tag 与 branch 同名而互不影响、旧版本拒绝在任何写入前发生（新 reader 对旧格式）、Dispose/lease 保护。

最终 build/test 按仓规串行 Release；包交付必须运行 `eng/Test-Package.ps1`。这份设计没有性能测量结果，也不代表已完成数据升级。

## 13. 交付后复杂度与明确的剩余边界

令 L 为当前 active branches + immutable tags 数，C=1024 为快照最低周期，P 为本次读取/校验的目标帧 bytes，N 为本次要求输出的历史长度。

| 操作 | 目标成本，不含文件系统自身的目录/缓存实现差异 |
| --- | --- |
| daily Open | O(L+C) 控制记录 + 常数数量locator/末帧；末event完整校验另加其P；不读所有ref/tag目标 |
| OpenBranch / cache-hit GetHead | 期望O(1)，名称长度有上限 |
| cache-miss GetHead | 常数个allocation/Init/末move/locator读取 + O(P)当前head校验 |
| Advance/Move | 常数帧和持久屏障 + O(P)指定目标；不枚举历史segment |
| Create/Fork/Archive/CreateTag | 常规常数控制记录，非空指定目标另加O(P)；checkpoint偶发O(L)，按元操作摊销，不承诺每次尾延迟恒定 |
| ResolveTag | 期望O(1)名字查询 + O(P)本次目标校验 |
| ListBranches | O(B log B)排序，B为当前active branches |
| 全量reflog/Parent traversal/audit | 与实际请求的历史量成正比，不属于日常隐含打开工作 |

不解决无限历史占盘、单ref-op-log地址容量耗尽、无限live tags、每步要求全量历史、文件系统在海量目录项下的性能以及硬件丢目录。refs/objects 保持直达RefId目录，不扫描全部对象；是否分桶要等文件系统实测，不凭空建设新目录索引。首版也不保证损坏事实日志自动可修。上述边界不以隐藏fallback补偿。
