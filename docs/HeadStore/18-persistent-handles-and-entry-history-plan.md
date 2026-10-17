# 持久 Handle 与准确 Entry 起点历史：实施方案

> 状态：已完成三个并行视角的静态源码审查及主线程整合，未发现设计阻断；尚未实施或取得新增运行资格。基线为 `8b67739`（生产源码 `34cd0e8`，Data 数学基础 `1149ab3`）。
> 需求：[下游请求](../../../durable-graph/docs/design-branches/0096-history-and-head-model/headstore-save-handles-request.md)、[五个调用故事](../../../durable-graph/docs/design-branches/0096-history-and-head-model/save-handle-stories.md)。当前项目无旧 API/数据兼容负担。
> 本文为临时施工方案。实施并验收后，将合同和证据分别归入现有文档，再删除本文及入口链接；不长期保留第二份规范。

## 1. 决策与交付范围

接纳 P1（两类值的公开持久编码）、P2（准确 Entry inclusive 起点历史）及 P3（导入后操作资格）。采用现有 HeadId / HeadEntryId，不另设 locator/save 类型、验证态或导入登记表。

- `HeadId` 仍为 4B 的编码 Handle，`HeadEntryId` 仍保存它与完整 SizedPtr。托管尺寸保持 4B / 16B；持久编码选择 4B / 12B。
- codec 是无 owner、无 I/O 的纯值操作，恢复的是定位值；读取、发布、fork、历史在使用时验证。
- 新增准确 Entry 起点的历史入口，共用现有历史资源、预算、回调与来源接续内核。
- 保留引用初始化 fork、所选 Head 与实际 Entry owner 分离、24bit local 域及概率跨库检查。
- 不修改 FrameStore 地址、不引入业务 Parent/State/Event/Schema、cursor、全局 Entry 索引、跨页 lease、任意历史集合 tag 或 exclusive 起点。

本轮增加公共 API，既有 HeadStore 格式4、内部 raw-local 字段与 RBF3 格式无需改变。新增公开 codec 有独立表示合同，不能把托管内存直接写盘。

## 2. 对下游身份要求的明确修订

下游请求中“完整持久库身份”“不同库同编号不混同”的要求，以用户随后接受并已实施的紧凑 Handle 合同替换：

`HandleKey = UInt32LE(HeadStoreId canonical bytes[0..4])`；`HeadId = ZeroPreservingXor32.Apply(localId, HandleKey)`。解码 local 需要当前 owner；合法范围为 `1..0x00FFFFFF`。

HeadId 等值比较全部编码32bit，EntryId 等值额外比较全部 ticket Packed bits。不同库可以相等或误命中；key 高8bit不同则合法 Handle 跨库必越界，高8bit相同是随机库对的1/256相关风险，不是每次调用独立抽样。codec 不恢复被省掉的库身份，也不能检查24bit local 范围。应用选择正确库是主要上下文。完整128bit身份仍用于门/header及 data 绑定；健康目标 header 不证明 Handle 的原始库。

无值继续用上层 nullable/字段状态表示。default 允许比较/hash，但不能编码或定位。解码成功不证明存在、发布成立、历史归属、业务角色或永久健康。

## 3. P1：唯一的首版公开编码

### 3.1 API 与字节布局

在两个现有 readonly struct 上分别增加：

```csharp
public const int EncodedSize = 4; // HeadEntryId 为 12
public bool TryWrite(Span<byte> destination);
public static bool TryRead(ReadOnlySpan<byte> source, out HeadId value);
// HeadEntryId.TryRead 的 out 类型为 HeadEntryId
```

| 类型 | 固定字节序列 | 长度 |
|---|---|---|
| HeadId | UInt32LE(encoded Handle) | 4B |
| HeadEntryId | HeadId 的4B + UInt64LE(ticket.Packed) | 12B |

使用 Packed，不使用 SizedPtr.Serialize() 的交错表示，不引入 VarInt 第二编码。HeadId 经 key 编码后通常没有小数值优势；完整 ticket 的变长压缩只有真实负载证明值得时再独立考虑。

v1 固定 `ticketBits = ((offset / 4) << 26) | (length / 4)`，即高38bit为offset/4、低26bit为length/4。SizedPtr 未来改变内部表示不能自动改变本编码合同。公开codec读写 encoded Handle，不能误复用需要owner且读写raw-local的内部 HeadIdCodec。

不公开 raw uint 构造器、encoded 属性、任意 HeadId+ticket 的便捷构造器或新的 serializer 框架。codec 字节可由调用方构造，这不是防伪边界；无需用私有构造器继续宣称只有“库签发值”。文本由应用按需 Base64 包装。

### 3.2 失败与数值资格

- TryRead 要求 source 恰好为 EncodedSize，拒绝短输入和尾随 bytes；失败 out=default。
- HeadId 编码值非零即可结构解析。无 owner 时无法判定它在某库解码后是否越界。
- EntryId 要求非零 HeadId 及 ticket.Length > 0。SizedPtr Packed 本身确定非负、4B对齐、可表示的 offset/length；codec 不判断 header 后界、完成范围或记录最小长度，这些由使用时资格负责。
- TryWrite 先完整验证值及目标容量，失败不改任何字节；成功仅写前 EncodedSize，剩余 destination 保持不变。不分配数组、不访问 owner、不抛出普通格式错误。
- TryRead 不存在消费游标；组合 reader 由应用切出精确字段，仅在成功后推进。TryWrite 不转储 CLR padding。
- 同一受支持编码下 `Decode(Encode(id)) == id`；长度不同的两种 codeword 不能整段交给另一种 decoder。上层主动切错字段不属于可检测的类型认证。

### 3.3 版本与种类由容器承载

把上述布局明确命名为 **Handle encoding v1**，适用于当前格式4的 Handle 数学与 ticket 表示。为保持4B/12B，不在每个值增加 version/kind/CRC。类型由字段 schema 和精确长度决定，版本由设置文件、数据库 schema、DG codec 注册或包裹它的应用记录显式指定。

独立交换 blob 若需要自描述，应由应用保存 `(encodingVersion, kind, bytes)`。仅4B/12B不能自动识别未来同长度但不同语义的编码；不得宣称 decoder 可拒绝所有未知版本。未来改变表示必须切换外层版本/codec，不能继续把旧布局叫 v1。无需提前实现第二版或通用 envelope。

本次无版本参数的 TryRead/TryWrite 固定实现 v1。外层先确认支持的版本与种类，再调用对应decoder；将来不同表示须提供明确区分的codec入口，或明确宣布不兼容，不能静默更改这些方法对已有v1字节的解释。

这是对下游“版本/种类边界必须可解释”的具体回答，且维持用户选定的密度。完整 HSID 不重新塞回 codec。

## 4. P3：从签发值转为可导入定位值

### 4.1 正常存储模型与成员资格

保证范围为正常 RBF3 writer 及其已支持的进程终止/尾恢复模型、完整记录不删除/复用。RBF3 的全局4B对齐 Fence 不出现在正常 frame 的非 Fence 部分；其格式约束见 [RBF3](../Rbf/rbf-format.md)。因此准确 frame/ticket 与边界校验可利用真正的边界，不因开放 codec 而要求每次从 EOF 或根扫描到目标。

源码审查确认成功定位的成员证明依赖联合条件：请求末端的完整aligned Fence是真实边界；紧邻该边界的真实TailKey/Trailer解出的TailLen等于ticket.Length；完整读取验证HeadLen、CRC、padding及HeadStore envelope/meta。因此 `offset = FenceOffset - TailLen` 唯一确定真实frame起点，内部子区间、跨帧区间或伪造长度不能成为成功目标。正常writer即使收到包含伪造头尾的payload，也会转义其中aligned Fence。

证据路径为 [ReadFrameInfo](../../src/Rbf/Internal/RbfReadImpl.ReadFrameInfo.cs) 的TailLen相等检查、[ReadFrame](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs) 的首尾长度及完整校验、[ScanBoundary](../../src/Rbf/Internal/RbfReadImpl.ScanBoundary.cs) 的实际Fence检查，以及 [ReadExactPublication](../../src/HeadStore/HeadStore.Entry.cs) 的组合。普通owner的ReadFrameInfo不能单独代替尾Fence检查，必须保留GetScanBoundaryAfter。现阶段未发现需要增加前置Fence检查或全链扫描的缺口；实施仍须用新增导入输入矩阵验证推理，不把静态审查当作运行验收。

任意离线改写镜像、故意嵌入 Fence 并重封 CRC 不在日常定位的主链真实性保证内。现有 Audit 的全链检查保持；不把其能力搬到每次定位，也不弱化 Audit。随机损坏仍按实际访问范围执行既有检查，不承诺所有未访问损坏均被发现。

### 4.2 各入口的使用时检查

| 入口 | 必要资格与结果 |
|---|---|
| ReadHead / PublishHead(s) / HeadId 起点历史 | owner guard、按当前key解码与24bit范围、实际目标/header、既有当前值/发布协议；导入 HeadId 可作为选中的写入目标 |
| GetLocalHeadId | 只做 owner guard、上下文解码和范围；无 I/O、不证明目标存在或原始库 |
| ReadEntry | 目标/header、header后完成范围、准确 ticket/形状、完整内容/CRC/meta、成功目标 Fence、CU资格；不回退 |
| ForkHead | 共用准确源资格，成功后沿既有 data→依赖确认→私有header→正式发布；坏源不产生新 Head |
| EntryId 起点历史 | 同样检查准确 anchor；首项必须是该 Entry，合法未成立CU也必须拒绝；随后按普通历史规则跳过未成立CU |
| ReadTag / Audit | 表读取仍只恢复位置；Audit 保持全链/引用验证，不用 codec 代替内部成员编码 |

已接受 ID 与导入 ID 使用同一条操作路径，不建立 ID 来源登记、验证缓存或免检分支。保留现有 mutation 重入、只读及 Outcome 检查顺序。缺失、default、解码越界沿用 HeadNotFound；HeadStore 自己裁决的完成范围/记录种类错误沿用 InvalidHeadRecord；RBF 对过短 ticket、长度不匹配等给出的 ArgumentError/FramingError，以及必要 CRC、I/O、清理错误保持原类别，不统一改报 InvalidHeadRecord。未成立CU：ReadEntry/新历史 anchor 返回 InvalidHeadRecord；ForkHead 保留当前 InvalidForkOrigin。错误码不承担跨库诊断。

ReadEntry/Fork可在已证明CU未成立时先于成功目标Fence检查返回拒绝；准确历史先构造边界再判断首项CU。保持各入口现有顺序，多重故障时不承诺它们得到相同首个错误。

## 5. P2：准确 Entry 起点的有界历史

### 5.1 公开入口

```csharp
public AteliaResult<HeadHistoryEnd> ReadEntryHistory(
    HeadEntryId startInclusive,
    HeadHistoryBudget budget,
    Func<HeadEntry, bool> visitor,
    CancellationToken cancellationToken = default);
```

采用独立方法名，避免 `ReadHeadHistory(default, ...)` 在新增重载后产生歧义；表示从 Entry 位置开始遍历其继承历史。现有 ReadHeadHistory 保留。

- 首项严格等于 startInclusive，保持原时间、实际 owner 与 payload；源/子 Head 后来追加不改变此前的历史前缀。
- 从 anchor 向旧读取本地已成立 Entry，到 header 后沿准确 ForkOrigin 接续。沿真实记录顺序交付，不产生克隆项、不按 payload 去重；同值真实发布仍分别交付。无需新增 Entry 去重集合，现有 Head 来源环检查保留。
- 不另传选中世界线，不判断 anchor 是否是某个视图的祖先。空 fork C 的 current=a1，拿 a1 起查即沿 A 历史；界面和后续写入仍保存 C 的 HeadId。
- callback=false 表示已接收当前项并返回 VisitorStopped，不多读一项探测 HasMore；到根按已有规则 Complete。
- 预算耗尽返回现有 HistoryBudgetExceeded，取消、异常和错误沿原通道；之前交付的自有 Entry 保留，但不能据此宣称遍历完成。
- 当前历史与新历史互相禁止嵌套，回调内 mutation/fork 仍拒绝；退出后保留 ID 可用于 fork。取消与生命周期检查点、临时句柄与 CU peer 清理、锁最后规则共用现有实现。

### 5.2 内部化简与预算

外层 guard/活动标志/try-finally 清理提取为一条入口路径；内核接收起始 HeadId 与可选准确 ticket，分别表达“当前尾起点”或“准确起点”。现有 ForkOrigin 跨文件接续也用相同准确起点路径。内部另区分当前准确起点来自调用参数还是 checked ForkOrigin：用户 anchor 的范围、首项不符或未成立CU返回 InvalidHeadRecord；跨文件声明来源沿用既有 InvalidForkOrigin 映射。RBF、I/O、预算与清理错误原样传播。起点成功交付后，后续 ForkOrigin 跳转均采用声明来源语义；这只是扫描上下文，不是 ID 来源登记或新的公面类型。

准确起点直接 CheckHeader → 范围/ReadFrameInfo → GetScanBoundaryAfter → 有界 ScanReverse。完整资格由首项 ReadPublication/CU检查完成，不先调用公开 ReadEntry 再重复装载 payload。首项完整 ticket 必须等于请求；失败/未成立不得转为普通“跳过CU”。头部之后更早的未成立CU则正常跳过。ReadEntry/Fork 与历史的完整读取顺序可以不同，共用局部资格规则即可，不为机械消重引入策略对象或大范围重排。

TakeWorkStep 在实际操作前执行；继续使用当前计费单位：文件获取尝试、header完整读取、GetScanBoundaryAfter完整读取、有界ScanReverse构造时完整anchor重读、MoveNext、publication完整读取以及CU必要peer与消歧工作。ReadFrameInfo 等已有只检查剩余额度的动作维持现有口径。新起点不得绕过这些计费；具体成功/差一步预算用测试从实际调用路径推导，更新05的唯一计费表，不臆定固定磁盘读取次数。

MaxEntries 统计每次交付，包括每页重读 anchor；MaxWorkSteps 包括不产生 Entry 的必要工作。沿用根首 SingleEntry 的直接完成优化，不为判断根完成再多读header。普通工厂准入、物理尾恢复等未完全纳入工作计数的成本单列，不能承诺严格 O(K) 物理 I/O。

### 5.3 最小分页

第一页固定 e100，读到 e81 主动停止。下一页从 e81 inclusive 开始，仅跳过首个与 e81 相等的 ID，再收集更早项；预算须包含此项及其资格工作。到根可能得到只有重复anchor的一次 Complete 调用，应用过滤后为空页。

DG筛选State/Event时保存最后处理的Entry，不能只保存最后显示项。准确anchor自身资格就可能耗完工作预算而零回调；anchor已交付后，连续未成立CU也可能耗完预算而没有新的可用继续位置，翻页重复anchor不算新增进度。相同预算重试不保证前进，调用方允许提高预算或中断。首版不持久保存这种物理工作进度。

## 6. 实施切片与验收

1. **P3前置核验**：追踪RBF3准确边界路径，用正常writer生成镜像、由测试构造候选定位值，证明非记录起点/伪造长度不能成为接受的目标；确定需要补的最小局部检查。
2. **P1及使用入口**：实现4B/12B codec、统一数值资格，删除“只接受库签发”约束及公面反射测试旧断言；保留不提供直接位置构造器。把导入值接入ReadHead/Publish/ReadEntry/Fork现有路径。
3. **P2**：抽出已有准确anchor分支，加ReadEntryHistory与共有入口；不把查询实现成临时fork，不新增依赖追踪系统。
4. **组合与文档**：用下列矩阵验证，修订现有规范和public smoke，再按新内容独立取源码/包资格。

| 验收组 | 必须观察的结果 |
|---|---|
| 编码固定向量 | 独立人工字节向量、4B/12B、LE、完整Packed而非Serialize；全短长度/尾随、零Handle/零长度、失败default与目标不变、长目标后缀不变 |
| 无owner往返 | 两种值无owner编解码、跨进程实际保存再读，不只在内存变量中关闭owner；保持同值与不同ticket长度可区分 |
| 紧凑语义 | 4B/16B托管尺寸、24bit范围、零固定特殊key；高8bit不同拒绝，同key不同完整库身份允许误命中，同高8bit不同key可解到另一合法local；不写“所有错库必拒绝”的测试 |
| 空fork C | 保存C的HeadId与a1的EntryId；冷恢复后写入C而非A；同一payload两次真实发布保持不同EntryId |
| 导入坏位置 | header、frame内部、跨frame拼接、错长度、零/过短长度、完成范围外、缺失、坏CRC/Fence、未成立CU；ReadEntry/Fork/历史anchor均不回退，fork不输出新Head |
| 旧点及后缀 | A推进后书签仍准确；新增后缀不作为重新定位扫描步骤；正常打开/恢复成本另计，不宣称零后缀I/O |
| 准确历史 | 与从该点fork后的继承序列一致；跨C→A不包含源a1之后的a2/a3；跨多层fork、同值发布、成立CU |
| 页边与预算 | 根初始Entry恰好预算时visitor=true得到Complete、false得到VisitorStopped；首本地Entry停止不访问来源，继续且Entry预算耗尽时失败不越额探测；重复anchor及后续header跳转收费、差一步拒绝；区分零回调与重复anchor之后零新增进度 |
| anchor错误类别 | 同一未成立CU作为用户anchor报InvalidHeadRecord、作为声明来源报InvalidForkOrigin、作为普通更早节点跳过；预算/必要peer错误不能改当未成立 |
| 生命周期与故障 | 两类历史四种嵌套及活动ScanTags准入拒绝，default/null visitor/零预算/预取消在首次文件访问前拒绝；visitor内fork拒绝/退出后成功；现有只读回调查询和同文件句柄借用保持；最后回调true/false时取消、Dispose、异常及最终close失败均不能正常结束；临时/peer单次归还、主异常优先、锁最后、借入data不Dispose |
| 既有功能 | HeadId导入后ReadHead/PublishHeads等保持选择身份；tag仍固定成员、CU raw-local排序、ListHeadOrigins声明语义、Audit全链不弱化 |

Windows .NET操作串行：先Release build，再匹配配置的完整solution `--no-build` test。针对新增编码使用真正的两个进程往返，以及现有公开包consumer中固定旧点的读/fork/历史组合；源码成功不能替代包消费。

包资格使用干净已提交来源、新唯一版本、六包与四个隔离PackageReference consumer，复核manifest/assets/Source Link本地checksum。公开发布、DG pin/接线验收、Linux和设备断电不由本方案的Windows本地证据自动覆盖。本轮不要求新增一套无关崩溃窗口。

## 7. 合同归档与完成条件

| 现有文档 | 实施后需要修订的唯一内容 |
|---|---|
| [04](04-versionstore-publication.md) | 值的公开codec与弱身份、导入后准确使用、取消只签发前提；明确正常存储模型与错误映射 |
| [05](05-versionstore-names-and-indexes.md) | 两种历史起点、准确anchor失败、共用预算表和分页语义；fork接受恢复的定位值 |
| [11](11-versionstore-root-payload-bytes-api.md) | 新API签名与自有结果/visitor边界 |
| [09](09-versionstore-immutable-tags.md) | 成员表是恢复来源之一；不再暗示唯一持久导入途径，内部16B成员格式保持 |
| [src README](../../src/HeadStore/README.md)、[模块入口](README.md)及根README/AGENTS | 当前公面、无owner codec示例、所选Head与Entry实际owner区分 |
| [既有公面计划](../FrameStore-HeadStore/14-downstream-api-refinement-plan.md) | 收束EntryId导入/导出延后项，链接新增合同；历史资格不改写 |
| [12验收](12-versionstore-final-acceptance.md) | 新增来源revision、源码/实际进程/本地包证据、未覆盖范围；不重标历史Accepted |

实施结束须搜索活跃文档/XML注释中“无公开codec”“只接受已签发”“不能匿名导入”等断言，按当前合同修订；历史evidence JSON保留原样。完成条件是P1/P2/P3矩阵有实际证据、现有规范接管、本临时文档删除。本文的审查通过仅说明方案可实施，不等于新增能力已通过验收。

## 8. 初稿后的独立审查

三个工作包均只读核对源码、现行合同和下游请求，没有执行新增代码或测试。主线程采纳以下修订：

| 视角 | 结论与纳入的修订 |
|---|---|
| 编码与下游调用故事 | 4B/12B及外层schema承担版本/种类可覆盖需求；固定无版本方法为v1、冻结ticket的38:26位语义，补同高8bit不同key的误定位向量 |
| RBF3位置资格 | 正常writer/尾恢复模型内局部证明成立，现有成功路径无需新全链检查；明确实际Fence不可省、TailLen确定起点、错误原类与入口检查顺序不统一 |
| 历史与资源 | 复用准确anchor路径可行；区分用户anchor与声明来源的错误映射，删除全程去重歧义，补根完成优先级/首本地页边/四种重入/允许查询借用/清理失败，以及零回调与零新增进度的区别 |

这些结论支持按第6节实施。新增输入、跨进程codec和包消费尚待实际验收；不预先继承紧凑Handle那一轮的Accepted。
