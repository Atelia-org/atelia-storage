# VersionStore：ConditionalUpdate 单帧跨 ref 事务候选

日期：2026-10-08。状态：**Draft / 用户暂定的优先候选；独立质询与 public RBF3 研究支持协议已达到可正式选型的程度，推荐选用。尚未记为主线选型决策，S4/S5 尚未 Ready，VersionStore 尚未实施；生产事务、进程终止、跨平台及包消费资格另行取得**。

比较基线：[Prepare + Commit 历史方案](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)。现行合同：[S0](00-architecture-decisions.md)、[S1 已知尺寸追加](01-rbf-sized-append.md)、[S4 发布](04-versionstore-publication.md)、[S5 历史](05-versionstore-names-and-indexes.md)、[RBF 接口](../Rbf/rbf-interface.md)。可重复研究探针见 [VersionStoreConditionalUpdateProbe](../../experiments/VersionStoreConditionalUpdateProbe/README.md)。

## 本轮结论与需求来源

将每 ref 的 Prepare/Commit 合并为一条 ConditionalUpdate 可以保持原子性，同时将一批 k 个 ref 的记录数从 2k 降为 k，去掉两帧邻接判定。最小条件是：**每份成员表包括自身并绑定其实际 ticket；全部指定成员均为完整合法 CU 且完整成员表相等；每次内部调用为所有成员写 fresh CU，不补写旧组**。读取统一采用“精确完整读；歧义失败时定位实际槽位”的窄函数，无需新 RBF API。

事务资格只需“完整 / 不完整”布尔判据，读取失败沿现有错误通道传播，不增加第三种持久事务状态。未完成 peer 的位置可被新记录复用：**实际 ticket 不同且必要结构检查通过，就足以否定旧成员，不审计替换 payload**；实际 ticket 相同时，仍须完整校验必要记录，再判断普通 RootMap 或异表 CU。按旧 SizedPtr 读取失败不能直接当作损坏，也不能一律吞成不完整。

| 约束 | 来源及处理 |
| --- | --- |
| RootMap + RefId → SizedPtr 合成一帧，提前确定所有 ticket，读时验证事务资格 | 用户提议；本轮暂定 CU 为优先发展方向，本文收敛最小执行算法 |
| 同一 VersionStore、同一绑定 data、多个 distinct 既有 ref | 本轮最小范围；不包含创建、名称/tag 混合、删除或跨库事务 |
| 普通单 ref RootMap 直接发布，批次只写参与 ref | 用户目标；不引入全量 Heads 或全局日志 |
| 单 owner/driver 串行，无 callback/操作交错，排斥另一 writer | 现行 S0/S4；不提供并发事务或跨多次读取的快照隔离 |
| ProcessCrashOnly；OS/FS 继续，完整前缀不丢失、不重排 | 当前 RBF 恢复模型；不扩大为断电、设备乱序或任意外部修改 |
| data-first、完整坏记录报错、Unknown 停用重开、Confirmed 不降级 | 现行 S0/S4；按本候选共同判据解释 |
| 完整历史不截断、已完整位置不复用；返回 owned 字典 | 现行 S4/S5；GC、删 ref、搬迁/截历史需另审 |
| 完整 / 不完整判据与读取失败分离，异长替换只作必要结构消歧 | 用户进一步简化要求；错误传播与按需读取仍保留，不扩大为替换内容或全库审计 |

FrameStore/VersionStore 尚未创建，没有新栈已发布格式或当前实现消费者。候选与 S4/S5 都是设计输入，不作为彼此的独立实现证据。本文给出选型建议及其证据，不把后续实施资格作为协议选型的前置条件。

2026-10-09 S4/S5 增加首帧 ForkOrigin 与跨文件发布历史。本候选仍不覆盖 ref 创建；未来正式并入时，fork 来源与继承历史只能接受普通 Snapshot 或通过本候选共同判据的 CU，不能采用物理完整但事务不完整的 CU。来源定位 actual ref/SizedPtr，既不追踪参与 ref 当前 head，也不要求新增事务身份；读取资格与 peer 成本随并入同步验收。

## 记录与尺寸规划

```text
ConditionalUpdate:
    kind / version
    RootMap                        // 本 ref 的完整根字典
    Members: RefId => SizedPtr      // 本批完整成员表，包含本 ref
```

各成员可以有不同 RootMap 和帧尺寸；相同的是完整 Members 表。RootMap 继续复用完整字典 codec 和 S2 固定 12B FrameAddress。Members 的 ticket 是对应 ref 文件的 SizedPtr，不能替换为 data FrameStore 的 FrameAddress。

本候选消费 S4 `[F-VS-REF-ID-8B]`：RefId 内部字段为固定 8B LocalRefId LittleEndian，公开值另携同一 VS 的持久上下文；Members 不为每项重复写 VSID，入口在 I/O/barrier 前拒绝错上下文。ticket 采用固定 8B `SizedPtr.Packed` LittleEndian；RootMap 原样消费 `[F-VS-ROOTMAP-BPV1]` 的 codeword/1MiB 上限。Members 计数与字段顺序、kind/version、完整 CU 组合上限仍待工程定稿。两定位字段解码都不证明 frame 存在或完成。此方向不改变 S2 地址格式，不从 CLR struct 内存布局推导 wire。

设各 RootMap 已编码字节数为 rᵢ，RefId 字段宽度 w=8，成员数为 k，固定 schema 与计数字节数为 h。CU payload 长度可直接度量为 `h + rᵢ + k × (w + 8) = h + rᵢ + 16k`，不依赖 tickets 的数值。由公共 `MeasureWriteSize`、各文件可信 TailOffset 与 `SizedPtr.Create` 可先预测全部 tickets，再编码完整 payload，不存在自身尺寸的循环求解。

准确说，消除循环只要求 ticket 的编码宽度不随值变化；已知 RefId 的编码长度也能事先度量。固定 RefId 是本候选按用户提议采用的方向，不是正确性额外要求。不得对包含待定 tickets 的整个 payload 做未经解决的变长压缩，再拿未确定的 stored 长度预约。

Members 唯一、有界，跨 ref CU 至少两个成员。解码须检查 RefId 与每个 ticket 的坐标/合法帧尺寸约束，拒绝 default、零长度、截短和非法项；自定位检查不能代替整表 codec 校验。所有文件、身份、尺寸与追加起点均先检查。它是本次内部调用生成的私有值，不接受调用方提供旧表或任意 prepared tickets。

最小公开入口只增加一个 `PublishRefs(changes)`：接收 distinct 既有 RefId 的完整新 RootMap，拒绝空批次；一个目标可复用普通 PublishRef，两项以上才写 CU。成功返回各 ref 的 owned RootMap 与已发布 revision，共用一次批次 Outcome；不交付部分成功结果、事务 Builder 或恢复句柄。签名与结果载体随 S4 工程定稿。

## 写入协议

```text
PublishRefs(changes):
    完成模式 / owner / lifecycle / history mutation guard
    验证全部目标及身份，拒绝重复、缺失及非法输入
    私有复制并编码全部 RootMap
    度量全部 CU payload / frame，检查每个追加起点
    由可信 TailOffset 预测全部 tickets
    构造完整 Members，完成所有 CU 字段的私有编码

    data.ConfirmDurable()
    对全部参与 ref:
        builder, actualTicket = BeginAppend(payloadLength[ref], 0)
        核对 actualTicket == predictedTicket[ref]
        写入该 ref 的已编码 CU 字节
        核对 EndAppend(ConditionalUpdate) 成功 ticket == actualTicket
        结束该 Builder 生命周期

    对全部参与 ref:
        DurableFlush()

    先记录 Confirmed
    一起安装本批内存结果并返回已发布 revisions
```

伪代码中的 Begin 消费现有 `BeginAppend(payloadLength, tailMetaLength, out ticket)`，不是新增预约/事务 API。默认逐文件 Begin/End，任一时刻只持一个事务 Builder。已知尺寸 Begin 并不预分配整帧，也不保证后续内存分配、编码输出和 End 成功。尚未完成的 Builder 在失败释放时取消，已完整 CU 不删除或回滚。

预编码使用预测值，所有确定的业务 codec、尺寸、容量和起点拒绝均在 data barrier 前完成；Begin 再核对实际值，但不预先保证后续资源操作成功。持有单 driver 串行准入，预测至 Begin 间没有其他追加。提前 ticket 不向应用签发 RefRevision，不证明已完成、已发布或耐久。

预编码不强制保留 k 份拼接后的连续大 buffer：可保存各 ref 的私有 RootMap 字节与其他已编码字段，共享一份不可变 Members 编码，再向各 Builder 输出这些字节。barrier 后不调用业务 codec；仍允许底层写入、资源或 End 失败，不新增分片 API 或编码框架。

全部 tickets 在最终输出前已经确定，不需要全部 Builder 同时活跃。原“先全部 Begin 再逐个 End”路径同样正确，但不是最低合同；保留其既有探针证据，不并列维护第二套生产协议。逐文件路径可能在较早 CU 已完成后遇到后续 Begin/资源失败，仍按整批 Unknown 处理。

当前 RBF 的健康取消无文件输出，成功 End 返回前同步输出完整 bytes。最后一份 CU 完整后共同判据可以成立；正常 Confirmed 另需 k 个文件各自的 flush 成功。不存在一次原子多文件 fsync；末尾只需一轮逐文件 flush，没有单独 Prepare 屏障。

输出/flush 异常后停用 VersionStore，按各 owner 的 fault 合同释放、重开裁决。新事务为**全部成员**新写 CU；不重新采用旧表，不续写旧事务缺失成员，不恢复执行旧调用的业务步骤。RBF 物理 CompletedTail 仅补原 body 的 Key/Fence，仍按当前底层恢复合同执行。

## 共同发布判据

令本地真实主链 CU 位于 ticket q，属于 ref a，完整成员表为 M。本地 CU 已通过完整 CRC、codec 与 self 检查；非法本地记录直接报错，不进入下面的布尔判据：

```text
Committed(a, q, M) 当且仅当:
    M[a] == q
    对 M 中每个 (ref, wantedTicket):
        正式 ref/header 与 VersionStore/data 身份正确
        指定 offset 的实际主链记录完整合法
        actualTicket == wantedTicket
        kind == ConditionalUpdate
        RootMap 与 Members 的 codec / CRC / version 合法
        Members[ref] == actualTicket
        Members 的完整内容 == M
```

比较完整解码成员表或规范编码的成员表 bytes；不能只比较 RefId 集合、某个相互指针、成员数量或 frame 存在性。RootMap 允许不同，不能比较整个 CU payload 相等；RBF EscapeKey 也可能不同，不能比较原始帧字节。

检查 peer 的固定历史位置，**不调用 peer 的 ReadRef 当前值，不要求 peer CU 仍为 EOF，不递归验证另一组**。相同成员表使每个成员对同一组的判据完全一致。因此全组共同成立，或本组不能发布；后续普通更新可以合法覆盖其中一个 ref 的当前值，旧组仍成立并保留在历史中。

原子性是“一组的所有成员共享同一发布资格”，不是“参与 ref 的当前 head 永远同步”。查询开始前已 checked-read 的本地 CU 可直接复用，组验证只需再读其他 k−1 个指定成员；拒绝组时允许合法见证短路。不能把一个成员的单独完整/flush 作为该成员已经发布的证明。

这里“完整合法”指实际读到的发布记录，不替应用遍历 RootMap 所引用的数据图。应用仍保证所有本次新增依赖完成、属于绑定 data 且闭包合法；无关活跃 data Builder 不阻断发布。

## 未完成位置复用与最小关联身份

```text
旧组 G = {A:a, B:b}:
    A 的 CU(a,G) 完整
    B 只有提前 ticket b，未完成；取消或真正残尾截断

新组 H = {A:a′, B:b}:
    A 必须追加到新位置 a′
    B 可在旧 b 位置写同长度的新 CU(b,H)

验证旧 A：B 的 CU 合法，但 H != G，因此旧组 Uncommitted
验证新组：全部 H 副本齐全时，H 整体成立
```

未完成位置可复用，但任一已完整旧 CU 是不可复用的锚点。未来 fresh 组包含该 ref 时必使用新 ticket，排除该 ref 时成员集合不同；都不能复制旧完整成员表。因此不用 TxId/nonce/generation 也能防止跨批拼组。若旧组完全没有完整成员，则没有可读旧 CU 需要辨别其尝试身份。

这个论证依赖 self 绑定、fresh 内部流程、完整前缀不丢失/不复用和不补旧业务成员。删除任一项都需重审：只检查帧存在会拼接 G/H；复制旧表到新位置而不验 self 会把副本当作新发布；补写旧 B 后可能让旧组迟到成立。不给 SizedPtr 增加额外身份字段，不代表可以公开接受任意旧 tickets。

## 读取：布尔资格与失败传播

先消费 S4 的正式 ref 打开、store/data 身份门与当前访问模式准入，取得必要文件的 RBF 结构资格，再作事务判定。可写 Open 会截真正残尾或补齐原 CU 的合法后缀；只读遇需修尾文件直接拒绝。不能把尚可 CompletedTail 的 CU 当作缺席后成功返回旧值。

| 查询行为 | 条件与行为 |
| --- | --- |
| `true`：事务完整（Committed） | 全部指定 CU 的实际 ticket、self 和完整表匹配；接受本地 RootMap |
| `false`：事务不完整（Uncommitted） | 正常结构打开后，必要 slot 正好在 EOF 尚无帧；或实际 ticket 不同且必要结构资格通过；或同 ticket 的完整合法记录是普通 RootMap / 异表 CU；跳过本地 CU |
| 读取失败 | 必要正式 ref/header 缺失、身份错；本地或同 ticket 必要记录坏 CRC/codec/version/self；必要结构、已观察元信息中的未知或该位置不允许的 kind/tombstone、I/O、资源或 lifecycle/fault 错误；停止，不回退 |

这是查询返回 bool 或失败的行为说明，不定义持久事务状态枚举，也不替代调用证据 `NotAttempted / Unknown / Confirmed`。成员定位不必向上区分“缺席”和“被异长替换”；二者都表示没有指定成员。

同 ticket 的 CU 表不同可以是合法 fresh 事务留下的替代记录，与两帧基线的“异表 Commit 为 Error”不同。此时已经完整读到该记录，须检查其 RootMap、成员表约束和 self；**不验证替代组是否已经提交**。异长分支则只验证必要结构，不解析替换 RootMap、版本或 Members/self；若应用之后选中或直接读取该替换记录，再按自身读取合同校验其内容。

缺席只包括本协议下的合法尾位置。指定 offset 越过已恢复 EOF，或真实链表明它不是 frame 起点，都属于非法/损坏证据，不泛化成缺席。已观察到的必要错误必须传播；一旦缺席或不匹配足以证明不完整，可以短路，未访问的其他内容不声称健康，也不做全库审计。预算耗尽、取消或读取失败没有提供不完整见证。

### 按旧长度读取失败的消歧

旧 b 是 SizedPtr，含 offset **和 length**。B 可在同 offset 写更短/更长的新记录，旧长度不再描述实际帧。这里只需回答“指定成员是否存在”，不需要给各种替换记录另设事务状态。

选定的最低读取路径集中为一个内部函数，供当前值和历史共同使用：

```text
ReadDesignatedMember(peer, wanted):
    // 正式 ref / 身份 / 模式已准入；wanted 已经过整表 codec 检查
    scan = peer.ScanReverse(showTombstone: true)
    // 创建入口执行 Idle / disposed / fault guard；此时尚未扫描 bytes
    tail = peer.TailOffset
    wanted.offset == tail: 返回无指定成员
    wanted.offset > tail: 报 Error

    exact = peer.ReadPooledFrame(wanted)
    exact 成功: 返回完整帧，继续统一检查 kind / codec / self / Members
    exact 的异常 / CRC / 其他非歧义错误: 直接传播

    // 当前完整读取仅 Rbf.ArgumentError / Rbf.FramingError 允许长度消歧
    推进 scan，逐条检查已观察元信息的 kind / tombstone，寻找 wanted.offset
    TerminationError / 扫过该 offset / 未找到: 报 Error
    actual.ticket == wanted: 传播原 exact 错误
    actual.ticket != wanted:
        继续同一逆扫一步，取得真实前驱 p；失败或无前驱: 报 Error
        按 ref 布局检查 p 的已观察 kind / tombstone；非法则报 Error
        GetPhysicalOffsetImmediatelyAfter(p.ticket) != wanted.offset: 报 Error
        after = peer.ReadFrameInfoImmediatelyAfter(p.ticket)
        after 失败 / None / ticket 与公开元信息不等于 actual: 报 Error
        返回无指定成员；不读取或解析替换 payload
```

这是当前 public API 的执行路径，不给原子判据增加持久字段或新接口。逆扫途中各条已观察元信息、actual 与前驱 p 均按 ref 布局检查 kind / tombstone；未知或位置不允许的 kind、tombstone 直接报错，合法初始化 header 不当作发布记录。所返回完整帧只在必要范围内使用并归还 pool；接受结果须物化为 owned 值。所有扫描、后继查询和内容读取都计入预算；失败、取消与预算不足不能返回“无指定成员”。

当前 `ReadPooledFrame(wanted)` 先检查短读及 HeadLen 与 wanted length 一致，再验证 TrailerCRC 和 PayloadCRC。因此正常异长替换只会产生范围或 framing 拒绝，**CRC 错误直接传播，不进入消歧**。不能把 `ReadFrameInfo(oldTicket)` 的错误 footer/CRC 行为套到这条完整读取路径。allowlist 不包括 state、buffer 或未知错误码，也不捕获异常；后续底层改变检查顺序时须重新核对。非法 Members 坐标不由消歧修复。

逆扫只验证尾部元信息，**不读取 HeadLen**。若异长替换的历史 HeadLen 损坏而后面还有健康尾，Open 可以成功、exact 已报结构错误，逆扫仍能给出不同 actual ticket；直接返回不完整会吞掉该错误。继续逆扫得到真实前驱，再从前驱读取直接后继，才补上实际 HeadLen、长度、右 Fence 和 Trailer 资格。CU 仅追加到既有正式 ref，目标 slot 前至少已有初始 Snapshot/header；找不到前驱就是异常，不能伪造前驱或解释为缺席。

RBF `TailOffset` 属性本身不检查 Dispose/fault，必须保留扫描入口准入；即使 wanted 正好在 EOF，也不能绕过这一 guard。结构检查通过的异长替换即使有未读取的坏 payload，也已足以否定旧 ticket；此查询不宣称替换内容健康。

精确读成功仍校验 HeadLen 与 wanted length、TrailerCRC 与 PayloadCRC。同起点的异长替代不能在该完整读中误成功；起点主链资格来自内部协议生成的可信位置、正常结构打开与不变完整前缀。随机读取 API 自身不证明任意外部 ticket 的主链成员身份。

按可信 start 定位实际 FrameInfo 的新 RBF 入口延期，仅在真实失败查询成本要求触发时评估。内部 `ReadFrameInfoAt` 不是现成公共合同；当前消歧现场逆扫取得真实前驱即可消费公开 `ReadFrameInfoImmediatelyAfter`。不给 Members 增加前驱字段，不把暖缓存作为冷读证明。

### 当前值、历史与观察稳定性

ReadRef 从本地真实 RBF 逆向链读：合法普通 RootMap 直接返回；CU 全组成立才返回，不成立则继续向前。任何实际发现的错误不回退。ReadRefHistory 复用同一判据；每条成立 CU 产生一次发布 revision，未成立 CU 不进入发布历史；同值发布与 rewind 仍是不同位置的 revision。CU 的 RootMap 和发布证据就在同一帧，RefRevision 自然锚定其实际 ticket，只在接受后签发；owned、固定完成上界、预算与 owner mutation guard 保持。

已完整组的资格对正常后续追加稳定。一个**已成功判为 Uncommitted**的旧组，也不能被未来 fresh 操作复活：缺席或合法替代的见证已经过底层结构裁决。延后打开的 peer 若还能 CompletedTail，说明旧组尚未被成功判作缺席；恢复原 body 可以作为崩溃前 Unknown 操作的接受结果，不能先成功返回“缺席”再采用同一原 body。无需为覆盖式 PublishRef 强制读取所有前一组成员；若增加新观察/并发模型，须另审这种恢复解释。

具体顺序：G(A,B) 的 A 完整，B 仅缺原 Key/Fence；重开只访问 A，追加普通 A2，之后才打开 B 并 CompletedTail。此时 A2/B_G 合法：未知 G 可以解释为先被接受、A 再被 A2 覆盖。任何成功的 G 缺席判断都必须先正常打开 B，届时会恢复 G 或只读拒绝，因而不存在“已成功观察 G 缺席、随后同一原 body 又使 G 成立”的轨迹。真实缺席或异表替代一旦被判 U，fresh 流程与完整锚点使这个见证保持稳定。

跨两次 ReadRef 的值可能来自不同操作时刻，不承诺跨调用快照隔离。数据损坏在应用读取图时仍报错，不据此撤销已接受 RootMap。

## Outcome、成本与比较

| 阶段 | 调用证据与行为 |
| --- | --- |
| 确定拒绝、data barrier 或 Begin/私有填充准备失败，整批尚无 CU 最终输出尝试 | NotAttempted；按所涉资源/fault 合同停止或释放 |
| 首次 CU 最终输出尝试起发生异常 | 整批保守 Unknown；只有证明整批未尝试公开输出时才可用 pre-I/O 例外，停用、重开裁决 |
| 全部 CU 完成且全部 ref flush 返回 | 先记录 Confirmed，再安装内存结果 |
| 确认后的内存安装/应用加载失败 | 保留 Confirmed，从实际发布状态恢复 |

最小实现可在首次 EndAppend/Append 调用前保守记录输出尝试，不为了精确分类内部准备失败增加底层 failpoint 协议。整批 Outcome 按已有输出证据单调推进：A.End 已完成后，后续 B.Begin/B.End 的 pre-I/O 拒绝仍为整批 Unknown；全部 CU 已完成后，任何 flush 的前置拒绝也不能降为 NotAttempted。不增加“只有最后成员才算 Unknown”的优化或精确尝试查询。完整组重开后被接受，不倒推旧调用曾 flush 成功或返回。

| 项目 | Prepare + Commit | ConditionalUpdate |
| --- | --- | --- |
| 普通单 ref 更新 | 一帧 RootMap | 相同 |
| k-ref 批次帧数 | 2k | k |
| 发布侧 flush | k 个文件各一次 | 相同 |
| 定位关联 | Prepare ticket + 直接后继 Commit | CU 自身 ticket + 全表匹配 |
| 复制元数据 | k 份 k 项 Commit 表，O(k²) | k 份 k 项 Members 表，O(k²) |
| 失败 peer 的异长复用 | 有完整 Prepare 锚点可定位其后继 | 需区分旧 SizedPtr 与实际替代帧；可能扫描消歧 |
| 当前值与历史 | 跳过 Prepare/不成立 Commit | 跳过不成立 CU；revision 无两帧锚定选择 |

纯 framing 比较，设保留的逻辑 bytes 分为 r/c：S1 当前 RBF3 追加占用 `B(x)=32+Align4(x)`，两帧合并节省 `B(r)+B(c)-B(r+c)`，为 **32 或 36B/ref**；c 为 4B 倍数时恰好 32B。schema 字段的新增/合并另计，实际代码一律使用公共 Measure，不复制公式作为执行逻辑。不据此宣称吞吐翻倍或总成本减半。

写入只触及本批 k 个 ref。每个组的精确 ticket 校验只访问它的成员与必要 bytes，不需要所有 N 个 Heads；全部 k 张完整表比较仍是 O(k²) 元数据工作，并需读取所选 peer RootMap 内容。已成立组的精确读取不扫描 peer 后续历史。连续失败后缀的本地逆扫会累积；异长消歧可能扫描 peer 后缀及真实前驱，再作一次结构后继查询，但不读替换 payload。一次 ReadRef 回退多个旧 CU 时，读取范围是这些被检查组成员集合的并集，不能宣称始终只读最终成立组的成员。

预算计扫描帧、bytes 和 peer I/O，不能只计返回 revisions。预算耗尽或取消不是 Uncommitted 见证，不能继续回退并把旧值作为成功结果；历史按既有预算/结束合同报告未完成范围，不声称全历史已验证。首次必要文件打开/恢复另计。失败时固定读取范围或时延目前没有保证。

后续成功发布的本地 RootMap 或成立 CU 会终止当前值的逆向回退；历史依赖仍保留。不增加 PreviousPublishedTicket、持久索引、全量 Heads 或 TxId 来掩盖这些代价。未来若实际要求失败情况下稳定冷读成本，优先评估上述窄定位入口或独立有证据的优化。

## 验证证据与后续验收

候选初录、探针完善及本次文档简化分别经过三位独立 reviewer 的两轮质询，主线程核对当前源码与证据；没有剩余协议争议需要第三轮裁决。源码基线：`f33c7bdc5a366b113db85613606e07c1beca8550`；设计文档含用户既有未提交修改。这个 HEAD 不是本候选或探针的提交/交付身份。

关键源码证据：

- [IRbfFile](../../src/Rbf/IRbfFile.cs)、[RbfFileImpl](../../src/Rbf/Internal/RbfFileImpl.cs)：已知尺寸 Begin、相同提前/完成 ticket、健康取消、串行与 fault；已有公共扫描和 ticket 读取。
- [ReadFrameInfo](../../src/Rbf/Internal/RbfReadImpl.ReadFrameInfo.cs)、[ReadFrame](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs)：旧 length 定位 footer、实际 HeadLen 匹配及完整 CRC；内部按 start 定位与公共接口的区别。
- [ReadTrailerBefore](../../src/Rbf/Internal/RbfReadImpl.ReadTrailerBefore.cs)、[Rbf3ReaderTests](../../tests/Rbf.Tests/Internal/Rbf3ReaderTests.cs)：逆扫不读 HeadLen；实际结构不能仅凭逆扫元信息证明。
- [SizedAppendAcceptanceTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendAcceptanceTests.cs)：既有双文件互引及取消位置复用证据；本轮查阅而未重跑这些历史测试。
- [SinkReservableWriter](../../src/Data/SinkReservableWriter.cs)、[RbfWriteInstrumentation](../../src/Rbf/Internal/RbfWriteInstrumentation.cs)：最终完成输出和 RandomAccess.Write/FlushToDisk，完成与 flush 仍是不同事件。

Windows / W: 真实 public RBF3 探针，初录 [结果](../../experiments/VersionStoreConditionalUpdateProbe/evidence/2026-10-08-result.json)保持历史身份：

| 覆盖 | 结果 |
| --- | --- |
| k=2/3/4 的所有 CU 完成排列及全部帧追加前缀 | 150 例；全旧或全新，完整前缀选择符合预期 |
| k=2 两种排列，在每个输出阶段对下一帧逐 byte 截取镜像 | 260 例；结构恢复后全旧或全新 |
| 上述逐 byte 镜像的恢复/只读 | 220 Truncated、32 CompletedTail、252 次只读拒绝残尾 |
| 同/异长普通 RootMap、同/异长新 CU 复用未完成 peer 起点 | 四例旧组均不复活；同长度可经旧 ptr 读成功，异长度均报 Rbf.FramingError；真实链消歧正确 |
| 完整组后单 ref 普通更新、复制旧表到新 self、peer 坏内容 CRC | 旧资格保持、错误 self 被拒、Rbf.CrcMismatch 显式传播 |

此前补充探针先在仓外研究，再保存到同一实验项目；以仓内相对 ProjectReference 的 Release build / `run --no-build` 再验，0 warning / 0 error，[完善结果](../../experiments/VersionStoreConditionalUpdateProbe/evidence/2026-10-08-refinement-result.json)单独留存，不覆盖初录证据。**该版保守与精确路径均完整审计替换 payload，结果反映此前较强策略，不代表本次收窄后的读取路径已经通过同一全矩阵**：

| 补充覆盖 | 结果 |
| --- | --- |
| 原 150 个帧前缀、260 个 byte 镜像，加 k=3 三种不同帧长的全部排列/前缀 24 例 | 保守真实链与精确读取共 1,156 次当前值比较一致 |
| 旧成立组的其他两个成员随后合计追加 100 条普通记录 | 精确读当前值成功，0 次 fallback；不是性能基准 |
| 更短/同长/更长 peer 复用，fresh A/B 或改组 B/C | 六例旧 G 均被拒绝；新组以预测全部 tickets、预编码、逐文件 Begin/End 完成 |
| 原 A 完整/B 可 CompletedTail，先仅打开 A 并普通覆盖，后打开 B | 只读 B 拒绝；可写补原后缀；A2/B_G 与旧组资格符合观察解释 |
| 同 ticket 的坏 CRC、异长实际替代坏 CRC、替代 CU 错 self/重复 Members | 旧版强审计路径均报错；本次只保留同 ticket 必要错误传播，异长分支不再审计替换内容 |
| disposed peer 的 wanted 正好是原 EOF | 入口直接拒绝，0 次 fallback；不误判 Missing |

本次另外执行独立的 public API 槽位检查：实际尺寸短于/等于/长于 wanted，各覆盖健康、坏 payload CRC 和非 EOF 历史 HeadLen 损坏，共 9 例；另验正常 EOF 与 disposed EOF 两项。异长槽位结构合格即否定旧 ticket，其坏 payload 留待直接读取时报 CRC；同 ticket 坏 CRC、坏 HeadLen 与 disposed 仍报错。Release build 0 warning / 0 error，`run --no-build` 通过；源码、镜像与结果保留在 `W:/storage-cu-doc-review-20261008/`。这只验证真实前驱 + 公共后继查询的槽位路径，不替代事务矩阵；仓内旧探针与历史 JSON 未修改。

上述探针只使用生产 public Rbf API，未复制首部、Fence 或 CRC 算法。它们使用合成 payload，**不是最终 RefId 或 RootMap codec，也没有 data FrameStore barrier、生产 VersionStore/header/身份门、真实进程终止、输出/flush 异常或 Linux/包资格**。字节镜像是顺序输出前缀模拟，不是断电模拟；没有新增 RBF 入口。

## 简化裁决与成熟度

| 裁决 | 本轮收敛 |
| --- | --- |
| merge | 每 ref 的数据与组证据合为一条 CU；k 帧而非 2k，revision 直接锚定本帧，去掉两帧邻接 |
| delete | 异长替换 payload/RootMap/Members/self 审计；完整读的 CRC 消歧分支 |
| keep | self、完整表相等、fresh/no 旧组补写、完整前缀、data-first、身份/模式、本地与同 ticket 必要内容及错误传播 |
| simplify | bool 资格 + 失败传播；真实前驱 + 现有后继 API 作结构消歧；逐文件 Begin/End、共享预编码成员表、整批 Outcome 单调 |
| defer | TxId/nonce、Prepared handle、builder group、新 RBF start 入口、Previous、持久索引、GC/搬迁；实际需求触发后另审 |
| user-decision | 将推荐协议正式记入主线；若要求失败查询也有固定范围/时延，须重新选择成本承诺 |

**成熟度判断：可以正式选择 CU 作为跨 ref 事务协议。** 共同判据、位置复用与稳定性有明确论证，现有 public API 足以实现读取；本次删除替换内容审计不改变原子判据，并有源码和定向检查支持必要结构消歧。没有发现需要新增持久机制的失败轨迹；新读取路径的完整组合验收仍属于实施出口。对用户当前少量根、几个地址的目标，减少帧与阶段的收益明确；P+C 的诊断、分帧容量及拒绝未提交组时可少读 RootMap 的优势仍保留，不据此宣称 CU 在所有工作负载更快。

该结论不等于整个 S4/S5 Ready。正式选型并入时，须同步替换 S0/S4/S5 及入口的“无跨 ref 事务/直接接受末 Snapshot/当前值与历史成本”旧合同，保留普通单 ref 路径与名称创建的独立范围；不能只在导航加一个链接便称主线支持。RefId 字段及 RootMap codec/上限直接消费 S4 已定合同；具体 Ready 项为 header/CU Members 组合 codec 与完整容量、PublishRefs/批次结果类型、文件/枚举生命周期与真实错误载体。它们是已有阶段工程定稿及本协议的实施出口，不是新的事务身份或恢复日志。

最小安全纵向片是**两个已正式创建的 ref**：正式 codec/header + 绑定 data barrier + PublishRefs + 当前/历史共用判据 + owned revisions；随后在每个 Begin/End、输出、flush、确认后安装位置验证失败与冷重开。验收包含布尔资格与读取失败、坏身份/self/重复表/同 ticket 坏内容、异长坏 HeadLen、异长未读坏 payload 不影响否定旧成员及直接读取仍报错、缺前驱/后继不符、I/O/fault；失败旧 A/B 后新 B/C 且不递归；lazy CompletedTail；同值/rewind/预算；普通单 ref 不增加成员表；新 helper 全矩阵与 k 增长成本。Windows/Linux 与 public 包消费分别出证据，不为此片先实现名称事务、全局 Heads 或日志。

本次只修订独立 CU 候选；P+C、仓内研究探针、主线合同与生产源码保留原状。没有新的协议产品问题必须先向用户澄清。
