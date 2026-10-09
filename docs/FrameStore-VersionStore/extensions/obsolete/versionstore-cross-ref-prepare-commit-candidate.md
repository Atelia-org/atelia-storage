# VersionStore：Prepare + Commit 跨 ref 事务候选

日期：2026-10-08。状态：**Draft / 独立候选；用户赞同本轮分析与协议补充，要求先记录以便上下文压缩后续谈；尚未并入 S4/S5，未实施、未取得真实 I/O 或包消费资格**。

> 历史比较方案：当前优先发展方向为 [ConditionalUpdate](../../07-versionstore-cross-ref-conditional-update.md)。下文保留提出 Prepare + Commit 时的结论和续谈计划，不作为当前首选方案。

依赖与现行合同：[S0 边界](../../00-architecture-decisions.md)、[S1 RBF 尺寸能力](../../01-rbf-sized-append.md)、[S4 ref 发布](../../04-versionstore-publication.md)、[S5 历史与名称](../../05-versionstore-names-and-indexes.md)、[RBF 接口](../../../Rbf/rbf-interface.md)。本文记录选用该候选后拟建立的行为，不把现行 S4 的“首版不提供跨 ref 事务”悄悄改为已经接纳或交付。

## 续谈入口

本轮结论：保留普通单 ref RootMap 直接发布，通过每个参与 ref 内的一对 `Prepare → Commit` 和全部 Commit 副本共同建立跨 ref 发布资格。共同判据不必放在独立共享文件里。

在单 owner/driver、通常只有少量参与 ref、希望避免全量 Heads 的目标下，本方案成为当前首选候选。需要保留的代价是：Commit 字典复制产生的 O(k²) 元数据、事务记录对参与 ref 的读取依赖，以及连续失败后缀的冷扫描成本。暂不增加 published predecessor、公开 Prepared handle 或独立事务身份。

下一轮按用户提议独立研究将两帧合并的 `conditional-update` 变体，见本文末尾。此时不继续扩展该变体，也不提前宣布它更优。

## 需求来源与范围

| 约束 | 来源及本轮处理 |
| --- | --- |
| 一次原子更新多个既有 ref 的完整 RootMap | 用户本轮要求；初片关注至少两个 distinct RefId |
| 每 ref 一个 RBF3 追加文件、稳定 RefId、完整历史不截掉 | 现行 S0/S4；保持物理布局和身份 |
| 普通 PublishRef 仍是一条 RootMap 记录 | 用户方案；保留原单 ref 路径，不强制经过全库 Heads |
| 单 owner/driver 串行、排斥另一 writer、操作不交错 | 现行 S0/S4；包括借入 data 的相关操作和历史 mutation guard |
| ProcessCrashOnly，进程终止而 OS/FS 继续运行 | 当前 RBF 合同；不扩大为断电、设备乱序或任意外部改写 |
| data-first；应用保证本次新增依赖已完成且闭包合法 | 现行 S0/S4；无关 data Builder 活跃不构成拒绝 |
| Unknown 后停用重开、完整损坏不回退、Confirmed 不降级 | 现行 S4；按新的事务发布判据解释 |

FrameStore/VersionStore 项目仍未创建，无已实现的新栈消费者或已发布格式兼容负担。S4/S5 和下游评估对同一方案的转述不作为独立实现证据。

初片仅更新同一 VersionStore 内、绑定同一 data FrameStore 的既有 ref。CreateRef、命名 fork、alias 与 tag 保持现有独立协议；创建、删除、名称/tag 混合事务、多 writer、跨库事务及跨多次查询的快照隔离不纳入本候选。

## 持久记录与地址

| frame 类型 | 内容 | 发布资格 |
| --- | --- | --- |
| 普通 RootMap | 现有完整 `string => FrameAddress` 快照 | 本地完整合法记录直接发布 |
| Prepare | 与普通 RootMap 相同的根字典内容/codec，以独立 kind 标记 | 单独存在不发布，不进入 ref 发布历史 |
| Commit | 本批完整 `RefId => prepareTicket` 字典 | 全部参与 ref 的固定 Prepare/Commit 对成立后发布 |

`prepareTicket` 是对应 ref 文件内的 **SizedPtr**，由 RefId 定位文件；它不是 data FrameStore 的 12B FrameAddress。RootMap 中的数据地址继续消费 S2 的固定 12B codec。VersionStoreId、RefId 和 DataStoreId 的上下文绑定继承格式门与文件 header，不因 ticket 合法就证明业务闭包或原始来源。

Commit 字典非空、成员唯一、有界，包含本地 RefId。各成员均为本次调用内新追加且成功完成的 Prepare；不接受调用方提供任意 prepared ticket。成员顺序/编码、RefId 与 SizedPtr 的持久 codec、kind/version 和容量限额尚未冻结。

所有副本复用同一份私有 Commit payload。比较完整解码字典或其规范编码，不能比较 RBF 原始文件字节：EscapeKey 等底层编码可能不同。

## 写入协议

Prepare/Commit 是一个 `PublishRefs` 候选入口内部的两种记录，不提供跨调用或跨重开的 Prepare 票据复用接口。

```text
PublishRefs(changes):
    验证模式、生命周期、历史 mutation guard、全部目标与身份
    拒绝缺失目标、重复 RefId、非法/超限输入
    私有复制并编码所有 RootMap
    度量各 Prepare，并由可信追加起点预测本批 prepareTickets
    用预测 tickets 预先构造并编码完整 Commit 字典
    预检每个 ref 的 Prepare、Commit 尺寸和两个追加起点
    data.ConfirmDurable()

    对全部参与 ref:
        prepareTicket[ref] = Append(Prepare(该 ref 的完整 RootMap))

    核对实际 prepareTickets 与预测 tickets 完全一致
    核对本批 Prepare 仍为各自实际物理末帧
    对全部参与 ref:
        Append(Commit(同一份字典 payload))

    对全部参与 ref:
        DurableFlush()

    先记录 Confirmed
    安装全部内存结果并返回实际已发布 revisions
```

整个调用禁止同 driver 的其他操作或应用 callback 交错。确定拒绝在 data barrier 和任何追加前完成；编码/容量试算通过可信 TailOffset 与 RBF 公共 Measure 预测内部位置，并在输出前编码完整 Commit payload。全部实际 Append 返回的 tickets 与预测一致后才复用该 payload；不在写完 Prepare 后才做可能产生确定拒绝的 Commit 编码。预测不增加预约、公开 Prepared handle 或输出前的已发布 revision。

容量预检同时覆盖每个 Prepare 和 Commit 的单帧尺寸，以及两条记录各自的起点硬界；不能只检查第一帧可追加。所有 Prepare 正常完成后，才允许尝试第一份 Commit。当前 RBF 支持同步输出完整帧后返回，因此在本故障模型下可以仅在末尾做一轮逐 ref DurableFlush；不额外要求 Prepare flush。这一轮仍是 k 个文件的独立 flush，不是多文件原子 fsync。

任何实际输出/flush 异常后停用 VersionStore；相应 RBF/data owner 按自身 fault 合同处理。重开后新操作为每个成员重新写 fresh Prepare，**不续写、不补齐旧事务的业务 Commit**。已经完整的 Prepare/Commit 位置不删除、不覆盖、不复用。

## 共同发布判据

```text
Committed(C) 当且仅当，对 C 中每个 (refId, prepareTicket):
    对应正式 ref/header 身份正确
    prepareTicket 指向合法完整的 Prepare
    该 Prepare 的直接物理后继是合法完整的 Commit
    该后继 Commit 的完整字典内容等于 C
```

正在解释的本地 Commit 自身也必须就是其本地 Prepare 的直接后继，不能用另一处同内容副本证明这个位置也是一次发布。验证其他成员直接检查指定 Prepare 和其后继，不递归调用 peer 的当前 ReadRef。

固定位置邻接不要求 Commit 仍为当前 EOF。一个已成立组后续被某个 ref 的普通更新覆盖，该组仍保留在历史中；检查 peer 的当前 EOF 会错误撤销旧事务。

邻接消费 RBF 公共 `ReadFrameInfoImmediatelyAfter`，不复制 Fence 常量。其元信息资格不能代替最终 Prepare/Commit 的完整 CRC/codec 检查。

最后一份 Commit 成为完整事实帧时，全组共同判据成立；正常返回的 Confirmed 另需全部参与文件的 flush 成功。记录完整、结构恢复接受、耐久确认返回和调用返回仍是不同事件。

## 当前值、历史与错误分类

当前值从本 ref 的真实 RBF 逆向主链读取，使用 `showTombstone: true`：遇到合法普通 RootMap 返回；遇到 Commit 判定全组资格，Committed 时返回其本地 Prepare 的 RootMap，Uncommitted 时继续向前；Prepare 不单独作为结果。未知 kind、tombstone、TerminationError 和已发现的损坏显式传播。

| 判定 | 条件与行为 |
| --- | --- |
| Committed | 全部固定相邻副本匹配，必要 Prepare/Commit 均经完整内容校验；接受全组 |
| Uncommitted | 必要相邻 Commit 不存在，或该位置已被新的 Prepare/普通 RootMap 占据；继续寻找此前发布 |
| Error | 必要正式 ref/header/Prepare 缺失或身份错；实际读取发现坏 CRC/codec/版本；结构错误、I/O 错误或完整异表 Commit；停止，不回退 |

“异表 Commit 为 Error”依赖 fresh Prepare、内部单次 PublishRefs、无旧票据复用的流程。放开公共 Prepared handle 或旧事务续写后，应重新研究该分类。

已有本组 Commit 意味着此前全部 Prepare 已正常完成。因此 Prepare 缺失不能像“最后一份 Commit 尚未写”那样解释为正常未提交。所有 Commit 尚未齐全时，不必先审计全部未发布 RootMap payload 才能判 Uncommitted；任何实际读取发现的错误仍必须传播，未检查内容不声称健康。组成立前后所需的内容校验范围应在正式合同写明。

参与文件先取得相应 RBF 模式的结构资格，再裁决事务：可写恢复可能截掉真正残尾，也可能 CompletedTail 后接受原 Commit；只读需要修尾时直接拒绝。不得先把可恢复 Commit 当缺失定案。RBF 补齐原帧 Key/Fence 属于物理结构恢复，与禁止重开后新追加旧事务 Commit 不矛盾。

ReadRefHistory 复用相同资格判定。一个成立的本地 Commit 返回其 Prepare RootMap 一次；Prepare 本身和未成立组不进入发布历史。同值新发布、rewind 仍有独立 revision；返回自有字典，固定完成上界、预算/错误区分和历史枚举期间 owner mutation 排斥保持。

RefRevision 的候选建议是绑定被接受的 Prepare ticket，以保持“完整 RootMap 快照位置”的含义；只在全组资格成立后签发，Commit 是内部资格证据。最终公开类型/wire 与是否改用本地 Commit 锚定，仍属工程定稿项；两帧不能被返回为两个 revisions，也不签发未发布 Prepare 的 revision。

## PublicationOutcome 与中断轨迹

| 阶段 | 本次调用证据 | 必要行为 |
| --- | --- | --- |
| 参数/编码/容量等确定拒绝 | NotAttempted | 无 barrier、无追加，健康实例可继续 |
| data barrier 异常或 Prepare 阶段失败 | NotAttempted | 本次根尚未进入共同发布尝试；按 owner/fault 停用 |
| Commit 输出尝试开始后发生异常 | 保守 Unknown；可证明的 pre-I/O 拒绝例外 | 停用，重开按实际全组资格续行，不盲重试/回滚 |
| 全部参与 ref DurableFlush 成功 | Confirmed | 先保存该证据，再做可能失败的内存安装 |
| 发布确认后库/应用安装失败 | 保留 Confirmed | 停用或放弃旧应用状态，从实际发布值恢复 |

不细分“最后一份 Commit 才 Unknown”的更精确优化，不增加尝试 token。完整组在重开后被接受，不倒推旧调用曾成功 flush/返回。

```text
部分副本：
    A: A0 → PA → C
    B: B0 → PB
    重开：A0 / B0

全部副本：
    A: A0 → PA → C
    B: B0 → PB → C
    重开：A1 / B1，即使旧调用还没有取得 Confirmed

禁止迟到补写：
    部分副本失败后，先普通发布 A2，再给 PB 补旧 C
    会使旧组迟到成立并得到 A2 / B1；内部流程必须排除这条轨迹

后续独立更新：
    完整组 A1 / B1 后正常普通发布 A2
    当前为 A2 / B1，旧组仍在历史中成立，这是合法后续操作
```

原子批量写不保证跨两次 ReadRef 的快照隔离；先读 A、执行更新、再读 B 是跨时刻查询。首片继续承诺单 driver 串行，必要时另研究一个串行 ReadRefs 调用。

## 成本、比较与暂缓机制

设 k 为本批参与 ref 数，N 为全库曾更新过的 ref 数：

- 普通 PublishRef 保留一条 RootMap 与一次发布侧 flush。
- 批量写约 2k 个 frame、k 次发布侧 flush；每份 Commit 有 k 项并复制 k 份，Commit 元数据约 O(k²)，另加各 RootMap bytes。
- 正常成功后的当前值资格只访问该组参与 ref；完整检查成本包含 k 个文件及各 Prepare/Commit bytes，不随 N 增长。
- 连续失败而尚未有后续成功发布时，当前值可能扫描越来越长的 Prepare/局部 Commit 后缀。成本与实际扫描帧、bytes 及各组验证相关；回退遇到更早事务还会检查该旧组的参与者。
- 一次后续成功普通 RootMap 或完整事务会重新缩短当前查询；不承诺反复失败下的常数冷读。历史预算须计入跳过的物理记录与 peer 验证，不能只按返回快照数计。
- 读取某个事务版本依赖 peer 当年的必要记录与文件打开资格。后续普通更新不取消旧资格；未来若接纳删 ref、截历史或 GC，必须重新审定证书保留规则。

| 方案 | 本轮裁决与实际代价 |
| --- | --- |
| Prepare + 复制 Commit | 当前目标下首选；小批次局部写入，接受 O(k²) 元数据和失败后缀扫描 |
| RefHeads + published 前驱 | 备选；稳定头定位，但每次 O(N) 全表写入及共享文件依赖 |
| 永久提交清单 | 备选；每批清单为 O(k)，增加永久文件/资格查询 |
| pending redo | 备选；增加主动物化恢复及事务级只读恢复准入 |
| 全局单帧 Batch | 写协议短；修改局部当前值/历史查询与共享容量模型 |

暂不增加 PreviousPublishedTicket。它能跳过失败后缀，但必须先确定此前实际已发布状态：新事务只改 A/C，而 A 上次属于 A/B 组时，冷发布可能因此读取 B。先保留局部写协议并如实记录成本，有真实冷读要求或测量后再考虑这一字段、派生内存索引或维护片。

也不增加公开 Prepare/Commit API、默认 CAS、TxId/nonce、精确尝试查询、永久全局目录或业务 Parent。成员表与本次 fresh Prepare 的不可复用完整位置，在当前模型内已经提供组关联。

## 证据与最小后续验收

本轮做了三位独立审阅者的两轮质询，主线程按源码与具体轨迹裁决。源码查阅基于 `f33c7bdc5a366b113db85613606e07c1beca8550`，设计文档当时存在用户未提交修改；不把 HEAD 当作本候选来源或已交付提交。

源码证据：

- [IRbfFile](../../../../src/Rbf/IRbfFile.cs)：已有直接后继查询；尾部 None 与结构错误区分，内容资格仍需完整读取。
- [RbfAppendImpl](../../../../src/Rbf/Internal/RbfAppendImpl.cs) 与 [RbfWriteInstrumentation](../../../../src/Rbf/Internal/RbfWriteInstrumentation.cs)：成功 Append 返回前同步输出全部帧 bytes，生产写入直接调用 RandomAccess.Write。
- [RbfFileImpl](../../../../src/Rbf/Internal/RbfFileImpl.cs)、[SinkReservableWriter](../../../../src/Data/SinkReservableWriter.cs) 与 [RandomAccessByteSink](../../../../src/Rbf/Internal/RandomAccessByteSink.cs)：Builder End 也在成功返回前推送完成数据，不存在等最终 DurableFlush 才输出 Prepare 的应用缓存。
- [RBF 接口](../../../Rbf/rbf-interface.md)：ProcessCrashOnly、CompletedTail/Truncated、只读不修尾及输出/flush fault 的权威边界。

会话内临时内存模型观察：枚举 2/3/4 个 ref 的全部 Prepare 写入排列、Commit 写入排列和帧追加前后状态，共 **5,456** 个情形，均全旧或全新；以帧前后状态抽象残尾截断/补尾，不是逐 byte 的 RBF 实验。连续 32 次 A 局部 Commit 后终止，A 冷逆扫 **65 帧**；旧事务迟到补写复现 A2/B1；完整坏 peer Commit 被分类为错误。模型未保存独立脚本或日志，这些是会话观察，需正式可重复验收重建。

没有实施 VersionStore，没有执行生产 RBF/I/O、真实进程终止、两平台或 package smoke；内存模型不构成耐久/产品资格。

选用后最小验收片：

1. 正常普通单 ref 与两个 ref 的 Prepare/Commit public 纵向流程、关闭重开和 owned 结果。
2. 各 Prepare/Commit 写入前缀、每次 flush、确认后安装各位置的真实 failpoint/进程终止；组只全旧/全新或显式错误。
3. RBF 截尾与 CompletedTail、只读拒绝残尾、必要 Prepare 缺失、坏 peer Commit/Prepare、异表副本和身份/版本/codec 错误。
4. 失败后 fresh Prepare，旧组不复活；成功后普通更新不撤销旧组；同值发布、rewind/history 不重计 Prepare。
5. 重复成员、整批容量与两帧起点边界在 barrier 前拒绝；无关 data Builder 活跃仍可发布完成闭包。
6. 反复失败冷扫描、k 增长的复制/验证开销与预算；真实文件实验使用 W:。包与平台资格仍单独出证据。

## 下一轮：conditional-update 单帧变体

用户提出：将 Prepare 的 RootMap 与 Commit 的成员表合并为一条 `conditional-update` frame；引入定长 RefId、定长 SizedPtr 编码，以已编码 RootMap bytes 和成员数提前计算每个参与 frame 的精确尺寸，再结合对应 ref 的可信追加起点计算所有 tickets，最后写出各 frame 并 fsync。

预期收益：每个 ref 从两帧降为一帧，减少小 payload 下的 framing 开销，并去掉 Prepare/Commit 邻接检查。总体仍以全部参与记录相互匹配作为共同发布条件。

这段只记录下一轮输入，**尚未审阅、未选择字段宽度、未冻结 wire 或认定优于两帧方案**。下一轮重点核验：

- 定长字段能否消除成员表与自身尺寸之间的计算循环；公共 Measure 与可信 TailOffset 的纯预测资格。
- 预计算 ticket 不等于预约、完成或耐久；未完成位置复用是否影响旧条件记录的组匹配和错误/未提交分类。
- 各条件帧的自身票据/完整成员表匹配、冷读取、历史/revision、Unknown 和 RBF 结构恢复。
- 减少一帧的实测/精确容量收益，是否仍有 O(k²) 成员表复制、参与者依赖和失败后缀扫描。

下一轮从这一变体继续讨论，本文件作为 Prepare + Commit 的比较基线保留；本次不修订 S4/S5 或启动实现。
