# HeadStore：ConditionalUpdate 单帧跨 Head 条件发布

紧凑 Handle 更新：2026-10-17 当前合同采用格式4，拒绝格式1/2/3；fork 只发布来源 header，HeadSetTag 成员为16B独立 key/value 映射。本轮实际源码、进程与包资格见[既有验收入口中的新增轮次](12-versionstore-final-acceptance.md)，未完成验证不沿用旧 Accepted。既有 Clause-ID 与历史证据保持。

既有轮次状态：**2026-10-11 CU 及附属重构已实施，独立源码验收 Accepted（Windows / WSL2 Linux）**。本文负责 CU 的记录、共同判据、位置复用与读取算法，已接入 S0/S4/S5；身份字段消费 S4，时间消费[08专项](08-versionstore-record-metadata.md)，批量 bytes 公面消费[11专项](11-versionstore-root-payload-bytes-api.md)。实际验证与资格见[CU首轮验收记录](07-versionstore-cross-ref-conditional-update-acceptance.md)。既有[最终源码验收](12-versionstore-final-acceptance.md)和[六包候选](../FrameStore-HeadStore/13-local-package-delivery.md)不包含 CU；新源码不得沿用其包资格。

比较基线：[Prepare + Commit 历史方案](extensions/obsolete/versionstore-cross-ref-prepare-commit-candidate.md)。现行合同：[S0](../FrameStore-HeadStore/00-architecture-decisions.md)、[S1 已知尺寸追加](../Rbf/01-rbf-sized-append.md)、[S4 发布](04-versionstore-publication.md)、[S5 历史](05-versionstore-names-and-indexes.md)、[RBF 接口](../Rbf/rbf-interface.md)。可重复研究探针见 [VersionStoreConditionalUpdateProbe](../../experiments/VersionStoreConditionalUpdateProbe/README.md)。

## 本轮结论与需求来源

将每 Head 的 Prepare/Commit 合并为一条 ConditionalUpdate 可以保持原子性，同时将一批 k 个 Head 的记录数从 2k 降为 k，去掉两帧邻接判定。最小条件是：**每份成员表包括自身并绑定其实际 ticket；全部指定成员均为完整合法 CU 且完整成员表相等；每次内部调用为所有成员写 fresh CU，不补写旧组**。CU 成员裁决采用“精确完整读；歧义失败时定位实际槽位”的窄函数，消费现有 RBF public API；S5 继承历史所需的指定边界逆扫已在 V2-A 实施，见[RBF 增量](../Rbf/rbf-reverse-boundary-plan.md)及[实施计划](12-versionstore-implementation-plan.md)。本轮不新增 RBF API。

事务资格只需“完整 / 不完整”布尔判据，读取失败沿现有错误通道传播，不增加第三种持久事务状态。未完成 peer 的位置可被新记录复用：**实际 ticket 不同且必要结构检查通过，就足以否定旧成员，不审计替换 payload**；实际 ticket 相同时，仍须完整校验必要记录，再判断普通 SingleEntry 或异表 CU。按旧 SizedPtr 读取失败不能直接当作损坏，也不能一律吞成不完整。

CU 仅更新同一 HeadStore/绑定 data 中的多个 distinct 既有 Head，不覆盖创建、tag、删除或跨库事务。单 owner/driver、data-first、ProcessCrashOnly、完整历史不复用及停用/清理沿用 S0/S4；不提供跨调用快照隔离。NamedFrameMap 只是可选内容 codec，事务资格不解释 payload 语义。

fork 和继承历史只接受普通 SingleEntry 或满足共同判据的 CU；不完整 CU 不作为来源节点，exact fork anchor 指向不完整 CU 时必须拒绝，不能改选更旧来源。ForkOrigin 仍定位实际 Head/ticket，不追踪参与 Head 的当前 head；子文件只写带来源 header，不复制源 CU 或 root bytes；其首次本地发布可为 SingleEntry 或 CU。

HeadSetTag创建复用本篇同一次current-read判据，取得已接受Entry及其必要CU发布文件；只选组内A时公开表只含A，但确认集合包含整个成立组。未成立组触及文件不自动成为确认依赖，必要peer仍先正常打开/恢复再裁决；ReadTag不读取成员，ReadEntry/Audit精确成员读取仍共用本篇判据，不递归peer后来的组。该引用/确认增量由[09](09-versionstore-immutable-tags.md)定义，不把CreateTag合并进PublishHeads事务，也不改变CU payload/FrameTag/meta8。

## 记录与尺寸规划

```text
ConditionalUpdate:
    FrameTag = 2                   // ConditionalUpdate
    Payload:
      MemberCount: uint32 LE
      Members: LocalHeadId => SizedPtr // 本批完整成员表，包含本 Head；LocalHeadId 严格递增
      RootPayloadBytes              // envelope 之后的全部剩余 bytes
    TailMeta:                      // 专项规定的8B记录时间
```

各成员可以有不同 root-payload bytes 和帧尺寸，相同的是完整 Members 表。Count 为固定4B uint32 LE，每项为4B LocalHeadId + 8B Packed ticket；表后全部剩余 payload 是本 Head 的 root payload，不增加另一层 byte-length。完整 CRC 覆盖整个记录。发布/事务判据不调用 NamedFrameMap decoder；调用方在进入 PublishHeads 前自行编码，入口按11公面专项逐项复制 bytes。Members ticket 是对应 Head 文件的 SizedPtr，不能替换为 data FrameAddress。

CU 消费 S4 `[F-VS-REF-ID-4B]`：LocalHeadId 固定 4B LE，公开值另携同一 HS 上下文，入口拒绝错上下文；ticket 固定 8B Packed LE，不使用 Serialize/VarUInt。Members 按非零 LocalHeadId 严格递增形成 canonical 表，重复、乱序及零 ID 均拒绝。root payload 直接消费 S4 `[A-VS-ROOT-PAYLOAD]` 的 bytes 范围，保持 1..1MiB；应用使用 NamedFrameMap 时才消费 [NamedFrameMap 专项](10-versionstore-rootmap-payload.md)。CU 的 FrameTag 固定为 2，完整 stored payload 上限为 `RbfFile.MaxPayloadAndMetaLength - 8 = 268435416B`，完整 frame 上限为 `SizedPtr.MaxLength = 268435452B`；不另增加任意成员数硬限。CU自身不新增payload kind/version；当前统一门与header版本为3，拒绝版本1/2，SingleEntry/CU记录布局保持。meta 消费专项，两定位字段的解码不证明发布。

在预测 CU tickets 前先捕获全部私有 root-payload bytes，长度 rᵢ 已确定。CU payload 长度精确为 4 + 12k + rᵢ，用 checked long 校验，再调用 MeasureWriteSize(payloadLength,8) 并以可信 TailOffset/SizedPtr 预测各 ticket。固定宽度的成员项不依赖 ticket 数值，root payload 的编码也不依赖 CU 自身 tickets，无尺寸循环；不引入成员 padding。相对原8B HeadId草案，每表少4k bytes，全组少4k² bytes。

准确说，消除循环只要求 ticket 的编码宽度不随值变化；已知 HeadId 的编码长度也能事先度量。固定 HeadId 是本协议按用户提议采用的方向，不是正确性额外要求。不得对包含待定 tickets 的整个 payload 做未经解决的变长压缩，再拿未确定的 stored 长度预约。

Members 唯一、有界，跨 Head CU 至少两个成员。Count 解码后在集合分配前要求 `4 + 12L × count + 1 <= 实际 payload 长度 <= 268435416`，checked 地为 root payload 保留至少 1B；表解码后剩余 root payload 必须在 1..1MiB。不能用 count 直接分配未验规模的集合，不把 root bytes 当 Members 的尾部扩展。以实际 count=k 计算 `pMin=4+12k+1`、`pMax=min(4+12k+1MiB,268435416)`，再用公共 `MeasureWriteSize(pMin,8)` / `MeasureWriteSize(pMax,8)` 得出每个成员 ticket 可接受的 frame 长度区间；不得只凭普通 SingleEntry 或 k=2 的全局下界接受与本表不可能相容的长度。解码须检查每个1..MaxLocalHeadId范围内的 LocalHeadId 与 ticket 的坐标/上述帧尺寸约束，拒绝 default、零长度、截短和非法项；自定位检查不能代替整表 codec 校验。所有文件、身份、尺寸与追加起点均先检查。它是本次内部调用生成的私有值，不接受调用方提供旧表或任意 prepared tickets。

PublishHeads的输入捕获、空/单成员分流、自有EntryId字典与共用Outcome唯一消费[11公面专项](11-versionstore-root-payload-bytes-api.md) `[A-VS-BYTES-BATCH]`；本篇定义两个及以上既有Head的CU执行路径。

## 写入协议

```text
PublishHeads(changes):
    完成模式 / owner / lifecycle / history mutation guard
    验证全部目标及身份，拒绝重复、缺失及非法输入
    按11专项私有捕获全部 root-payload bytes，不调用应用 codec
    度量全部 CU payload / frame，检查每个追加起点
    由可信 TailOffset 预测全部 tickets
    构造完整 Members；按专项为整批取时一次
    完成所有 CU payload 字段与共享8B TailMeta的私有编码

    data.ConfirmDurable()
    对全部参与 Head:
        builder, actualTicket = BeginAppend(payloadLength[Head], 8)
        核对 actualTicket == predictedTicket[Head]
        顺序写入该 Head 已编码payload及共享8B TailMeta
        核对 EndAppend(ConditionalUpdate) 成功 ticket == actualTicket
        结束该 Builder 生命周期

    对全部参与 Head:
        DurableFlush()

    先记录 Confirmed
    一起安装本批内存结果并返回已发布 EntryIds
```

伪代码中的 Begin 消费现有 `BeginAppend(payloadLength, tailMetaLength, out ticket)`，不是新增预约/事务 API。默认逐文件 Begin/End，任一时刻只持一个事务 Builder。已知尺寸 Begin 并不预分配整帧，也不保证后续内存分配、编码输出和 End 成功。尚未完成的 Builder 在失败释放时取消，已完整 CU 不删除或回滚。

预编码使用预测值，应用编码在调用 API 前完成；库的确定尺寸、容量和起点拒绝均在 data barrier 前完成；Begin 再核对实际值，但不预先保证后续资源操作成功。持有单 driver 串行准入，预测至 Begin 间没有其他追加。提前 ticket 不向应用签发 HeadEntryId，不证明已完成、已发布或耐久。

预编码不强制保留 k 份拼接后的连续大 buffer：可保存各 Head 的私有 root-payload bytes与其他已编码字段，共享一份不可变 Members 编码，再向各 Builder 输出这些字节。barrier 后不调用业务 codec；仍允许底层写入、资源或 End 失败，不新增分片 API 或编码框架。

全部 tickets 在最终输出前已经确定，不需要全部 Builder 同时活跃。原“先全部 Begin 再逐个 End”路径同样正确，但不是最低合同；保留其既有探针证据，不并列维护第二套生产协议。逐文件路径可能在较早 CU 已完成后遇到后续 Begin/资源失败，仍按整批 Unknown 处理。

当前 RBF 的健康取消无文件输出，成功 End 返回前同步输出完整 bytes。最后一份 CU 完整后共同判据可以成立；正常 Confirmed 另需 k 个文件各自的 flush 成功。不存在一次原子多文件 fsync；末尾只需一轮逐文件 flush，没有单独 Prepare 屏障。

输出/flush 异常后停用 HeadStore，按各 owner 的 fault 合同释放、重开裁决。新事务为**全部成员**新写 CU；不重新采用旧表，不续写旧事务缺失成员，不恢复执行旧调用的业务步骤。RBF 物理 CompletedTail 仅补原 body 的 Key/Fence，仍按当前底层恢复合同执行。

## 共同发布判据

令本地真实主链 CU 位于 ticket q，属于 Head a，完整成员表为 M。本地 CU 已通过完整 CRC、envelope codec、专项meta形状与 self 检查；非法本地记录直接报错，不进入下面的布尔判据：

```text
Committed(a, q, M) 当且仅当:
    M[a] == q
    对 M 中每个 (Head, wantedTicket):
        正式 Head/header 与 HeadStore/data 身份正确
        指定 offset 的实际主链记录完整合法
        actualTicket == wantedTicket
        kind == ConditionalUpdate
        Members codec、root payload 边界、完整CRC及专项meta形状合法
        Members[Head] == actualTicket
        Members 的完整内容 == M
```

比较完整解码成员表或规范编码的成员表 bytes；不能只比较 HeadId 集合、某个相互指针、成员数量或 frame 存在性。root-payload bytes 允许不同，不能比较整个 CU payload 相等；时间是诊断值，不比较它来判组，也不替代完整Members。RBF EscapeKey 也可能不同，不能比较原始帧字节。

检查 peer 的固定历史位置，**不调用 peer 的 ReadHead 当前值，不要求 peer CU 仍为 EOF，不递归验证另一组**。相同成员表使每个成员对同一组的判据完全一致。因此全组共同成立，或本组不能发布；后续普通更新可以合法覆盖其中一个 Head 的当前值，旧组仍成立并保留在历史中。

原子性是“一组的所有成员共享同一发布资格”，不是“参与 Head 的当前 head 永远同步”。查询开始前已 checked-read 的本地 CU 可直接复用，组验证只需再读其他 k−1 个指定成员；拒绝组时允许合法见证短路。不能把一个成员的单独完整/flush 作为该成员已经发布的证明。

这里“完整合法”指实际读到的发布记录，不替应用遍历 NamedFrameMap 所引用的数据图。应用仍保证所有本次新增依赖完成、属于绑定 data 且闭包合法；无关活跃 data Builder 不阻断发布。

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

未完成位置可复用，但任一已完整旧 CU 是不可复用的锚点。未来 fresh 组包含该 Head 时必使用新 ticket，排除该 Head 时成员集合不同；都不能复制旧完整成员表。因此不用 TxId/nonce/generation 也能防止跨批拼组。若旧组完全没有完整成员，则没有可读旧 CU 需要辨别其尝试身份。

这个论证依赖 self 绑定、fresh 内部流程、完整前缀不丢失/不复用和不补旧业务成员。删除任一项都需重审：只检查帧存在会拼接 G/H；复制旧表到新位置而不验 self 会把副本当作新发布；补写旧 B 后可能让旧组迟到成立。不给 SizedPtr 增加额外身份字段，不代表可以公开接受任意旧 tickets。

## 读取：布尔资格与失败传播

先消费 S4 的正式 Head 打开、store/data 身份门与当前访问模式准入，取得必要文件的 RBF 结构资格，再作事务判定。可写 Open 会截真正残尾或补齐原 CU 的合法后缀；只读遇需修尾文件直接拒绝。不能把尚可 CompletedTail 的 CU 当作缺席后成功返回旧值。

| 查询行为 | 条件与行为 |
| --- | --- |
| `true`：事务完整（Committed） | 全部指定 CU 的实际 ticket、self 和完整表匹配；接受本地 root payload |
| `false`：事务不完整（Uncommitted） | 正常结构打开后，必要 slot 正好在 EOF 尚无帧；或实际 ticket 不同且必要结构资格通过；或同 ticket 的完整合法发布记录是普通 SingleEntry / 异表 CU；跳过本地 CU |
| 读取失败 | 必要正式 Head/header 缺失、身份错；本地或同 ticket 必要记录坏 CRC/codec/格式版/meta形状/self；必要结构、已观察元信息中的未知或该位置不允许的 kind/tombstone、I/O、资源或 lifecycle/fault 错误；停止，不回退 |

这是查询返回 bool 或失败的行为说明，不定义持久事务状态枚举，也不替代调用证据 `NotAttempted / Unknown / Confirmed`。成员定位不必向上区分“缺席”和“被异长替换”；二者都表示没有指定成员。

同 ticket 的 CU 表不同可以是合法 fresh 事务留下的替代记录，与两帧基线的“异表 Commit 为 Error”不同。此时已经完整读到该记录，须检查其 root-payload 边界、完整 CRC、成员表约束和 self；**不验证替代组是否已经提交**。异长分支则只验证必要结构，不解析替换 NamedFrameMap、版本或 Members/self；若应用之后选中或直接读取该替换记录，再按自身读取合同校验其内容。

缺席只包括本协议下的合法尾位置。指定 offset 越过已恢复 EOF，或真实链表明它不是 frame 起点，都属于非法/损坏证据，不泛化成缺席。已观察到的必要错误必须传播；一旦缺席或不匹配足以证明不完整，可以短路，未访问的其他内容不声称健康，也不做全库审计。预算耗尽、取消或读取失败没有提供不完整见证。

### 按旧长度读取失败的消歧

旧 b 是 SizedPtr，含 offset **和 length**。B 可在同 offset 写更短/更长的新记录，旧长度不再描述实际帧。这里只需回答“指定成员是否存在”，不需要给各种替换记录另设事务状态。

选定的最低读取路径集中为一个内部函数，供当前值和历史共同使用：

```text
ReadDesignatedMember(peer, wanted):
    // 正式 Head / 身份 / 模式已准入；wanted 已经过整表 codec 检查
    scan = peer.ScanReverse(showTombstone: true)
    // 创建入口执行 Idle / disposed / fault guard；此时尚未扫描 bytes
    tail = peer.TailOffset
    wanted.offset == tail: 返回无指定成员
    wanted.offset > tail: 报 Error

    exact = peer.ReadPooledFrame(wanted)
    exact 成功:
        完整检查 kind / codec / meta形状 / self；非法则报 Error
        boundary = peer.GetScanBoundaryAfter(wanted)
        boundary 失败: 直接传播；不进入长度消歧
        尾 Fence 合格后才按 SingleEntry / Members 返回不匹配或成立
    exact 的异常 / CRC / 其他非歧义错误: 直接传播

    // 当前完整读取仅 Rbf.ArgumentError / Rbf.FramingError 允许长度消歧
    推进 scan，逐条检查已观察元信息的 kind / tombstone，寻找 wanted.offset
    TerminationError / 扫过该 offset / 未找到: 报 Error
    actual.ticket == wanted: 传播原 exact 错误
    actual.ticket != wanted:
        继续同一逆扫一步，取得真实前驱 p；失败或无前驱: 报 Error
        按 Head 布局检查 p 的已观察 kind / tombstone；非法则报 Error
        GetPhysicalOffsetImmediatelyAfter(p.ticket) != wanted.offset: 报 Error
        after = peer.ReadFrameInfoImmediatelyAfter(p.ticket)
        after 失败 / None / ticket 与公开元信息不等于 actual: 报 Error
        返回无指定成员；不读取或解析替换 payload
```

这是当前 public API 的执行路径，不给原子判据增加持久字段或新接口。逆扫途中各条已观察元信息、actual 与前驱 p 均按 Head 布局检查 kind / tombstone；未知或位置不允许的 kind、tombstone 直接报错，合法初始化 header 不当作发布记录。所返回完整帧只在必要范围内使用并归还 pool；接受结果须物化为 owned 值。用于 ReadHeadHistory 时，peer 访问、扫描、后继查询和完整读取都按下文消费本次历史工作步；失败、取消与预算不足不能返回“无指定成员”。

当前 `ReadPooledFrame(wanted)` 先检查短读及 HeadLen 与 wanted length 一致，再验证 TrailerCRC 和 PayloadCRC。因此正常异长替换只会产生范围或 framing 拒绝，**CRC 错误直接传播，不进入消歧**。不能把 `ReadFrameInfo(oldTicket)` 的错误 footer/CRC 行为套到这条完整读取路径。allowlist 不包括 state、buffer 或未知错误码，也不捕获异常；后续底层改变检查顺序时须重新核对。非法 Members 坐标不由消歧修复。

逆扫只验证尾部元信息，**不读取 HeadLen**。若异长替换的历史 HeadLen 损坏而后面还有健康尾，Open 可以成功、exact 已报结构错误，逆扫仍能给出不同 actual ticket；直接返回不完整会吞掉该错误。继续逆扫得到真实前驱，再从前驱读取直接后继，才补上实际 HeadLen、长度、右 Fence 和 Trailer 资格。元信息专项只约束需要完整资格化的本地/同ticket记录；本分支的异长实际替代保持RBF必要结构检查，不增加HeadStore meta形状或bytes审计。CU 仅追加到既有正式 Head，目标 slot 前至少已有 checked header（无来源 root 还须初始 SingleEntry）；找不到前驱就是异常，不能伪造前驱或解释为缺席。

RBF `TailOffset` 属性本身不检查 Dispose/fault，必须保留扫描入口准入；即使 wanted 正好在 EOF，也不能绕过这一 guard。结构检查通过的异长替换即使有未读取的坏 payload，也已足以否定旧 ticket；此查询不宣称替换内容健康。

精确读成功仍校验 HeadLen 与 wanted length、TrailerCRC 与 PayloadCRC；这些不覆盖尾 Fence。同ticket的完整合法peer必须再由GetScanBoundaryAfter核验尾Fence，成功后才可成为成立成员或SingleEntry/异表CU的合法否定见证；Fence失败直接传播，不能进入长度消歧。所选本地CU已由合法peer见证否定时，仍按现行短路规则处理，不额外检查本地CU的尾Fence。同起点的异长替代不能在该完整读中误成功；异长结构反证保持前述后继查询，不增加替换payload读取。起点主链资格来自内部协议生成的可信位置、正常结构打开与不变完整前缀。随机读取 API 自身不证明任意外部 ticket 的主链成员身份。

按可信 start 定位实际 FrameInfo 的新 RBF 入口延期，仅在真实失败查询成本要求触发时评估。内部 `ReadFrameInfoAt` 不是现成公共合同；当前消歧现场逆扫取得真实前驱即可消费公开 `ReadFrameInfoImmediatelyAfter`。不给 Members 增加前驱字段，不把暖缓存作为冷读证明。

### 当前值、历史与观察稳定性

各入口仍先校验本地 header；无来源 root 必须有直接初始 SingleEntry，有来源 fork 可为 header-only 或以 CU 开始。成员成立判据仅检查实际物理成员，不递归求旧当前值；父子 Head 可同批更新。ReadHead 不预读初始 payload，历史按 S5 逐项校验，ListHeadOrigins 只检查 header 与声明图。CU 全组成员资格决定本次发布是否生效，仍须在读取时执行，不能移到独立完整校验中。ReadHead 从本地真实 RBF 逆向链读：合法普通 SingleEntry 直接交付自有 bytes；CU 全组成立才返回，不成立则继续向前；只有正常抵达自身 checked header 的 fork 才准确读取固定 ForkOrigin，无来源 root 缺初始 Entry 报错。首个本地 CU 未成立时可以回到来源；必要错误不伪装成空 fork。已有本地健康发布不预审来源。ReadHeadHistory 复用同一判据；每条成立 CU 产生一次发布 EntryId，未成立 CU 不进入发布历史；同值发布与 rewind 仍是不同位置的 EntryId。CU 的 root payload 和发布证据就在同一帧，HeadEntryId 自然锚定其实际 ticket，只在接受后签发；owned、固定完成上界、预算与 owner mutation guard 保持。

已完整组的资格对正常后续追加稳定。一个**已成功判为 Uncommitted**的旧组，也不能被未来 fresh 操作复活：缺席或合法替代的见证已经过底层结构裁决。延后打开的 peer 若还能 CompletedTail，说明旧组尚未被成功判作缺席；恢复原 body 可以作为崩溃前 Unknown 操作的接受结果，不能先成功返回“缺席”再采用同一原 body。无需为覆盖式 PublishHead 强制读取所有前一组成员；若增加新观察/并发模型，须另审这种恢复解释。

具体顺序：G(A,B) 的 A 完整，B 仅缺原 Key/Fence；重开只访问 A，追加普通 A2，之后才打开 B 并 CompletedTail。此时 A2/B_G 合法：未知 G 可以解释为先被接受、A 再被 A2 覆盖。任何成功的 G 缺席判断都必须先正常打开 B，届时会恢复 G 或只读拒绝，因而不存在“已成功观察 G 缺席、随后同一原 body 又使 G 成立”的轨迹。真实缺席或异表替代一旦被判 U，fresh 流程与完整锚点使这个见证保持稳定。

跨两次 ReadHead 的值可能来自不同操作时刻，不承诺跨调用快照隔离。数据损坏在应用读取图时仍报错，不据此撤销已接受 NamedFrameMap。

## Outcome、成本与比较

| 阶段 | 调用证据与行为 |
| --- | --- |
| 确定拒绝、data barrier 或 Begin/私有填充准备失败，整批尚无 CU 最终输出尝试 | NotAttempted；按所涉资源/fault 合同停止或释放 |
| 首次 CU 最终输出尝试起发生异常 | 整批保守 Unknown；只有证明整批未尝试公开输出时才可用 pre-I/O 例外，停用、重开裁决 |
| 全部 CU 完成且全部 Head flush 返回 | 先记录 Confirmed，再安装内存结果 |
| 确认后的内存安装/应用加载失败 | 保留 Confirmed，从实际发布状态恢复 |

最小实现可在首次 EndAppend/Append 调用前保守记录输出尝试，不为了精确分类内部准备失败增加底层 failpoint 协议。整批 Outcome 按已有输出证据单调推进：A.End 已完成后，后续 B.Begin/B.End 的 pre-I/O 拒绝仍为整批 Unknown；全部 CU 已完成后，任何 flush 的前置拒绝也不能降为 NotAttempted。不增加“只有最后成员才算 Unknown”的优化或精确尝试查询。完整组重开后被接受，不倒推旧调用曾 flush 成功或返回。

| 项目 | Prepare + Commit | ConditionalUpdate |
| --- | --- | --- |
| 普通单 Head 更新 | 一帧 NamedFrameMap | 相同 |
| k-Head 批次帧数 | 2k | k |
| 发布侧 flush | k 个文件各一次 | 相同 |
| 定位关联 | Prepare ticket + 直接后继 Commit | CU 自身 ticket + 全表匹配 |
| 复制元数据 | k 份 k 项 Commit 表，O(k²) | k 份 k 项 Members 表，O(k²) |
| 失败 peer 的异长复用 | 有完整 Prepare 锚点可定位其后继 | 需区分旧 SizedPtr 与实际替代帧；可能扫描消歧 |
| 当前值与历史 | 跳过 Prepare/不成立 Commit | 跳过不成立 CU；EntryId 无两帧锚定选择 |

纯 framing 比较，设保留的逻辑 bytes 分为 r/c：x 表示 payload 与 TailMeta 的合计逻辑长度，S1 当前 RBF3 追加占用 `B(x)=32+Align4(x)`，两帧合并节省 `B(r)+B(c)-B(r+c)`，为 **32 或 36B/Head**；c 为 4B 倍数时恰好 32B。schema 字段的新增/合并另计，实际代码一律使用公共 Measure，不复制公式作为执行逻辑。不据此宣称吞吐翻倍或总成本减半。

写入只触及本批 k 个 Head。每个组的精确 ticket 校验只访问它的成员与必要 bytes，不需要所有 N 个 Heads；全部 k 张完整表比较仍是 O(k²) 元数据工作，并需读取所选 peer 的完整帧 bytes/CRC；不解析其 NamedFrameMap 内容。已成立组的精确读取不扫描 peer 后续历史。连续失败后缀的本地逆扫会累积；异长消歧可能扫描 peer 后缀及真实前驱，再作一次结构后继查询，但不读替换 payload。一次 ReadHead 回退多个旧 CU 时，读取范围是这些被检查组成员集合的并集，不能宣称始终只读最终成立组的成员。

ReadHeadHistory 共用 S5 的 MaxEntries / MaxWorkSteps：只有向 visitor 交付成立的发布记录才计一项快照；每次进入 peer、forward/reverse MoveNext 尝试和完整帧读取均按既有规则计一步，包括无输出的组验证和失败定位。同ticket peer的GetScanBoundaryAfter会再次完整读取指定帧，须在调用前另扣一步；额度不足不得探测其Fence。CU 异长消歧的 ReadFrameInfoImmediatelyAfter 结构后继查询另计一步，S5 计费表包含此项；每个动作前检查并扣费。继承历史的 source anchor 完整读、GetScanBoundaryAfter 与指定边界 ScanReverse 的完整读也按 S5 计费；CU 异长槽位消歧仍保留自身的后缀定位路径，不能借这份历史优化省略。复用已读本地 CU 不重复计一次 peer 读取；peer 访问不加入 ForkOrigin 继承路径的 visited，也不递归另一组。临时 peer 句柄登记在 owner 可清理槽，匹配已有文件时借用，其他 peer 逐个关闭；当前历史/本地句柄加至多一个 peer 句柄，额外 handles 保持 O(1)，不将全部成员文件安装成长驻缓存。

预算耗尽或取消不是 Uncommitted 见证，不能继续回退并把旧值作为成功结果；历史按既有预算/结束合同报告未完成范围，不声称全历史已验证。bytes、底层物理 I/O 与文件工厂内部检查/恢复另作成本测量，不新增字节预算或 ReadHead/ForkHead 预算参数；工作步仍不是固定读取范围或时延保证。

后续成功发布的普通 SingleEntry 或成立 CU 会终止当前值的逆向回退；历史依赖仍保留。不增加 PreviousPublishedTicket、持久索引、全量 Heads 或 TxId 来掩盖这些代价。未来若实际要求失败情况下稳定冷读成本，优先评估上述窄定位入口或独立有证据的优化。

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
| 同/异长普通 NamedFrameMap、同/异长新 CU 复用未完成 peer 起点 | 四例旧组均不复活；同长度可经旧 ptr 读成功，异长度均报 Rbf.FramingError；真实链消歧正确 |
| 完整组后单 Head 普通更新、复制旧表到新 self、peer 坏内容 CRC | 旧资格保持、错误 self 被拒、Rbf.CrcMismatch 显式传播 |

上述历史探针也未使用本轮meta8时间合同，不作为当前尺寸/codec资格。

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

上述探针只使用生产 public Rbf API，未复制首部、Fence 或 CRC 算法。它们使用合成 payload，**不是本轮 Members-first 布局、最终 HeadId 或 NamedFrameMap codec，也没有 data FrameStore barrier、生产 HeadStore/header/身份门、真实进程终止、输出/flush 异常或 Linux/包资格**。字节镜像是顺序输出前缀模拟，不是断电模拟；没有新增 RBF 入口。

## 本轮实施与验收出口

**协议已正式接纳，本轮实现通过独立源码验收（Accepted）。** 共同判据、fresh 完整锚点与观察稳定性有明确论证，现有 public RBF API 足以执行结构消歧；少量根场景下减少帧和阶段的收益明确。P+C 仍有分帧容量、诊断以及拒绝未提交组时少读 root bytes 的优势，不宣称 CU 在所有工作负载更快。当前算法的完整组合结果以[本轮 CU 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)为准，历史探针不代替生产资格。

S0/S4/S5 及入口已同步既有 Head 批量范围、选尾/历史/fork 资格、工作步计费与成本，11 定义公开 PublishHeads。S4 的 SourceEntryTicket 保留字段名，将最大长度扩至完整 CU frame 范围；其数值检查仍不签发来源资格。沿用既定身份、bytes/时间、恢复后完整性、生命周期与 Outcome，不新增 TxId/nonce、Prepared handle、持久索引或恢复日志。只有真实失败查询成本需求才重审窄 start 定位入口等优化。

本轮范围为正式既有 Head 的 CU envelope + 绑定 data 屏障 + PublishHeads + 当前/历史/fork/Audit 共用判据 + 自有 EntryIds，并验证每个 Begin/End、最终输出、flush 及确认后安装的失败/冷开。Audit 完整检查物理 CU，即使其共同判据未成立；EntryCount 只计普通 SingleEntry 与成立 CU，不计未提交 CU，也不重复展开继承历史。

特有验收包括：Members-first边界/count越界/截短、self/完整表/坏身份/重复项、不同opaque payload的同组与禁用peer decoder；同ticket坏内容硬失败、异长坏HeadLen/缺前驱/后继不符失败、异长未读坏payload不妨碍否定旧ticket但直接读取仍报错；失败旧A/B后新B/C不递归另一组、lazy CompletedTail与观察稳定性、同值/rewind/预算/取消/I/O/fault。历史组验证不交付快照也计工作步，结构后继查询先扣费，peer 不误入继承 visited；CU 来源引用初始化 fork、首条本地 CU 成功/未成立回到准确来源、父子/多个共享来源 fork 同批 CU、header 前驱下同长/异长位置复用的向量；普通单Head不增加表。新helper全矩阵与k增长成本、Windows/Linux及public包资格分别取证。
