# EventJournal v2：实施合同冻结附件

日期：2026-09-29。状态：T00设计冻结稿，由本轮gpt-6-astra有界子任务提出并经主线程源码核对；T00 已在同一生产基线上核验 Passed；生产实现及编译/故障测试仍由后续任务提供。实施开始时按[工单](bounded-online-io-work-order.md) T00做一次基线确认，不能让后续sol工作包各自定义格式。

本附件补充[综合方案](bounded-online-io-design.md)的字段/API/错误边界，不改变事实源、格式拒旧、严格坏尾停维或首版无repair的范围。实验证明本合同不能实现时，上报T00；不得边编码边静默改版。

## 1. 公共编码规则

所有整数little-endian。CRC32C使用现有`Atelia.Data.Hashing.RollingCrc`：reflected polynomial `0x82F63B78`、init=`0xffffffff`、final xor=`0xffffffff`；校验值以LE u32保存。保留字段必须零，文件长度必须精确，无隐式padding或尾随bytes。

名字为1..128 ASCII bytes，沿用当前canonical name校验；snapshot排序使用StringComparer.Ordinal，不按文化排序。RefId沿用u64 Packed；EventAddress沿用16-byte codec。不得对既有事件或ref/tag帧重编号/改地址。

### 1.1 `journal.format`（16 bytes）

| offset | width | 值 |
| --- | --- | --- |
| 0 | 4 | ASCII `EJFM` |
| 4 | 2 | LayoutVersion=2 |
| 6 | 2 | RecordLength=16 |
| 8 | 4 | reserved=0 |
| 12 | 4 | CRC32C([0,12)) |

根初始化的最后一步发布此文件。不存在的根目录允许Create；已有非空根缺marker时只能报告LegacyOrIncompleteLayout，不能声称已区分“旧数据”与“中断初始化”。不自动接管半成品目录。文件magic与其他上下文的帧tag相同不代表它是RBF帧；本文件是独立定长记录。

### 1.2 `<segmented-store>/active.segment`（20 bytes）

| offset | width | 值 |
| --- | --- | --- |
| 0 | 4 | ASCII `RBSA` |
| 4 | 2 | LocatorVersion=1 |
| 6 | 2 | RecordLength=20 |
| 8 | 1 | Layout：0=Bucketed、1=Flat |
| 9 | 3 | reserved=0 |
| 12 | 4 | ActiveSegmentNumber≥1 |
| 16 | 4 | CRC32C([0,16)) |

通过locator选规范路径；仅定点检查相反layout根是否存在、active是否存在以及active+1路径是否存在。不枚举整个store。相反layout冲突/next存在都停维；active=uint.MaxValue时不执行加一。locator不保存业务head、sequence或tail offset。

### 1.3 `refs/catalog.snapshot`

64-byte header + branch entries + tag entries + 4-byte整体CRC：

| offset | width | 值 |
| --- | --- | --- |
| 0 | 4 | ASCII `EJCS` |
| 4 | 2 | SnapshotVersion=1 |
| 6 | 2 | HeaderLength=64 |
| 8 | 8 | FileLength，含末CRC |
| 16 | 8 | BoundaryEndExclusive |
| 24 | 8 | AnchorTicketPacked |
| 32 | 4 | AnchorContentCrc32C，见§2 |
| 36 | 4 | BranchCount |
| 40 | 4 | TagCount |
| 44 | 1 | AnchorKind：0=Empty、1=Frame |
| 45 | 19 | reserved=0 |

branch entry：`u16 nameLength + nameBytes + u64 RefId`，无padding；tag entry：`u16 nameLength + nameBytes + 16-byte EventAddress`。先全部branch，后全部tag；每组名称严格递增。跨组同名合法；同一RefId多个active名字非法。末CRC覆盖从offset0到该CRC之前的所有bytes。

空快照为68 bytes：boundary=4、AnchorKind=0、ticket/contentCRC/counts均0。Empty表示“被快照覆盖的前缀为空”，不要求当前op-log仍为空。日志非空但live entries为0时使用Frame anchor，不能用空snapshot替代历史边界。

加载先验证物理长度≥68、header、声明长度相等、计数可容纳于各自Dictionary的int范围；用checked u64/long计算entry理论长度上下界。branch最短11、最长138 bytes；tag最短19、最长146 bytes。L0为两个count的checked宽整数和。不得对未验证count直接EnsureCapacity；按记录流式构造临时索引，校验CRC和精确EOF后才安装到driver。

控制日志anchor ticket长度最多248 bytes；每个suffix frame含尾Fence最多252 bytes（RefOp固定头96+名字128+RBF开销24+Fence4）。这些限制在读payload/租buffer之前检查。限制来自现有codec/layout，不增加新消息体类型。

## 2. RBF API与确定性尾读

在`Atelia.Rbf`冻结如下新增表面，保留现有`ScanForward(bool)`：

```csharp
public readonly record struct RbfScanBoundary(
    long EndExclusive,
    SizedPtr AnchorTicket,
    uint AnchorContentCrc32C) {
    public static RbfScanBoundary Empty => new(4, default, 0);
}

// IRbfFile:
AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket);
AteliaResult<RbfForwardSequence> ScanForward(
    RbfScanBoundary boundary, bool showTombstone = false);
```

当前AteliaResult<T>支持`allows ref struct`，可容纳RbfForwardSequence；无需新增异步结果框架。实现T01仍必须实际编译验证ref escape/lifetime合法性。

GetScanBoundaryAfter只checked-read指定frame及尾Fence，使用现有`GetPhysicalOffsetImmediatelyAfter(ticket)`得到end，不读prefix。AnchorContentCrc32C的输入严格为：`LE u32 Tag || LE u32 TailMetaLength || LE u32 IsTombstone(0/1) || PayloadAndMeta`。排除RBF存储CRC字段与padding；不得对完整、已含CRC的codeword再做同种CRC充当内容见证。

ScanForward(boundary)先验证4B对齐、4≤end≤当前EOF；Empty必须精确为(4,0,0)，default全零非法。Frame anchor验证ticket、完整frame CRC、内容见证、尾Fence及end相等，再直接从end构造现有dataStart扫描器。非空文件可以从Empty开始，供初始空snapshot读取新suffix。扫描本身仍只做framing/trailer检查；EJ对每个实际控制记录再做完整checked-read。

malformed参数返回已有RbfArgumentError；坏framing/CRC返回已有RBF对应error；I/O保留异常；枚举中失败必须通过TerminationError被调用方检查。snapshot字段有效却与现文件anchor不匹配时，EJ映射BoundaryMismatch，而不是把它当调用方参数错误或忽略。

尾读先留调用方内部helper，不新增另一个public读取体系：HeaderFence正确且physical length恰为4才是empty；其他短尾失败；`ScanReverse(showTombstone:true)`只取一帧，验证帧+Fence的结束位置等于physical EOF并完整checked-read。不循环向前寻找可用帧。EJ在active空且编号>1时最多访问前一段，前一段必须非空；通用RBF不认识Event/ref操作类型。

## 3. 可识别的打开错误

保持factory抛异常、业务API返回AteliaResult的总体形态。在`Atelia.RbfSegmentStore`定义（EJ复用其依赖中的类型）：

```csharp
public enum StorageOpenErrorKind { MaintenanceRequired, FormatUnsupported }

public sealed class StorageOpenException : IOException {
    public StorageOpenErrorKind Kind { get; }
    public string ReasonCode { get; }
    public string StoragePath { get; }
    public long? Offset { get; }
    public ushort? ObservedVersion { get; }
    // public constructor接收上述字段；message由类型生成，innerException可选。
}
```

构造签名固定为`StorageOpenException(StorageOpenErrorKind kind, string reasonCode, string storagePath, long? offset = null, ushort? observedVersion = null, Exception? innerException = null)`。错误码来自下表，禁止动态拼payload内容作为code。

| Kind | ReasonCode |
| --- | --- |
| FormatUnsupported | LegacyOrIncompleteLayout、UnsupportedVersion、UnsupportedFlags |
| MaintenanceRequired | MetadataMissing、MetadataCorrupt、LayoutConflict、NextSegmentPresent、ActiveSegmentMissing、InvalidTail、BoundaryMismatch、SuffixBudgetExceeded、ControlFrameTooLarge、CatalogInvalid |

根路径不存在、权限不足与设备I/O错误继续标准异常，不一概当损坏。格式已识别后的长度/CRC坏是MetadataCorrupt；语义格式不支持是FormatUnsupported。Standalone SegmentStore缺locator无法区分legacy/incomplete，使用LegacyOrIncompleteLayout；已验证EJ v2根后，其必需locator/snapshot缺失由EJ报告MetadataMissing。不能静默选择旧格式。

首次访问ref才发现维护错误时，返回Result的API映射为`EventJournal.MaintenanceRequired`（未知格式映射`EventJournal.FormatUnsupported`），Details保留ReasonCode/StoragePath及可用Offset/ObservedVersion；GetHead维持既有Unwrap便利抛错行为，不另外承诺所有业务错误都变成StorageOpenException。当前Event/TagTarget业务错误继续原合同。TagPublicationException及三个Outcome不被新Open异常替换。

## 4. 生命周期与操作前置协议

EventJournal统一fault latch代替仅tag latch。业务写、目标durable确认、ref object创建、catalog/根metadata发布阶段发生异常，先latch再构造诊断；之后正常数据API拒绝，Dispose仍可运行。参数/CAS/纯预分配等尚未进入持久写入的确定失败不fault；tag预编码/预备容量延续当前行为。

SegmentStore只latch自己执行的locator初建/rotation/ConfirmDurable发布异常；参数/只读/live lease guard在I/O前拒绝，不fault。已借lease释放与Dispose不受fault guard阻断。通过lease.File自行Append/flush的独立调用方负责I/O异常后Dispose；本轮不增加IRbfFile代理或更改任意RBF direct caller状态机。EJ作为调用者必须捕获其业务写异常，不能借此遗漏latch。

ref entry持有store，不持有跨调用writer lease。LRU eviction/Dispose尝试释放全部资源；目标reader lease必须在ConfirmDurable之前释放。metadata发布helper分别属于SegmentStore与EJ内部；允许复用同一已验证的几行原子替换流程，不新增通用生产包或改变Data职责。

控制操作顺序：

1. 检查名称/目标/CAS/sequence溢出，预分配可知长度的frame缓冲、预备正反索引容量。Create/Fork的RefId必须由allocation Append返回；之后回填预分配的Init/Bind缓冲，不预测ticket、不增加dry-run API。路径字符串/对象创建等后续分配仍可能失败，按fault/orphan协议处理，不声称所有后半段异常已被前置消除。
2. checked计算Lnext/r与完整操作空间；每个计划frame的起始offset≤SizedPtr.MaxOffset。不能只查第一次append，亦不能错误要求最终含Fence tail≤MaxOffset。完整控制操作容量不足返回`EventJournal.RefOpCapacityExhausted`的业务Result，不写、不触发checkpoint、不fault，可由调用方处理；不为了沿用旧单次Append的内部RBF错误类型新增公共接口。
3. 在本次第一处业务写入之前，按`d+r>max(1024,L0)`或`L0>2*max(1024,Lnext)`执行checkpoint。Archive必须先checkpoint再Close；Create/Fork必须先checkpoint再allocation。
4. checkpoint：确认既有op-log durable→取得精确boundary见证→流式写temp→Flush(true)/close→同目录原子replace→安装L0/Q/d。失败fault；本次tag为NotAttempted。不使用delete目标再move。
5. 非空业务目标确认durable后，Create/Fork：allocation append+flush→object/locator→Init append+flush→Bind append+flush→内存安装；单driver串行操作使allocation与Bind在op-log中物理相邻，Bind的起点必须等于allocation含Fence的结束位置，字段与allocation身份信息一致。这是T04按现有发布顺序收口的局部验证规则，拒绝在Archive之后重放旧Bind，不引入历史knownRefs；Archive：Close append+flush→Archive append+flush→内存解绑；Move/Advance：move append+flush→内存安装。
6. Tag：Append调用前NotAttempted，开始Append为Unknown，日志flush返回即Confirmed，随后内存安装失败不降级。成功之后无额外强制checkpoint。确定的RBF前置Result拒绝没有I/O，不等同异常的Unknown。
7. 每个成功durable的控制frame更新实际d；一旦异常不继续运行补计数。snapshot记录的是checkpoint当时的已发布状态，不能预写尚未完成的业务效果。

根初始化依次完成events segment+locator、空op-log durable、空snapshot durable，最后发布journal.format。rotation固定old flush→next empty flush→locator(next) replace→允许next append；存在next不覆盖。目录断电边界仍以综合方案为准，原子可见性不升级成目录持久性保证。

新增`EventJournalOptions.RefStoreCacheCapacity`，默认32，必须≥0；0表示操作结束不保留entry，仍正确Dispose，冷读依旧有限帧。沿用现memory plan cache的4096项/16MiB估算预算，不加另一组public cache配置。预算不宣称等于整个进程的真实heap上限。

## 5. Toolkit接口与候选输出

首版命令固定：`audit <journal> [--report <outside-file>]`；`rebuild-indexes <journal> --output <new-dir>`。没有apply/repair/migrate子命令，不支持在线一致快照；输入必须停写或为稳定副本。

audit直接读取事实，不依赖daily open。报告schemaVersion=1，含FactsStatus（Healthy/Invalid/Incomplete）、IndexesStatus（Consistent/Missing/Invalid/Ambiguous）、扫描计数、带相对路径/offset/code的findings、Completed。独立区分事实与索引：缺locator可使daily不可用，但不因此跳过事实扫描。orphan event/allocation可以是warning；坏frame、错误Parent/sequence、重复tag、ref转移链非法属于事实错误。取消/I/O中断是Incomplete，不是Healthy。

T06报告分类收口：合法allocation从未Bind，且没有object，或其object仅有规范segment1的合法4B HeaderFence，均为`UnpublishedRef` Warning，FactsStatus仍可Healthy；必须核对归属与全历史“从未Bind”，不能只看当前无名字。空object缺locator独立报告IndexesStatus=Missing，并允许生成locator候选；不补Init/Bind。未知归属、坏尾、多余段及locator冲突不享此例外。已Close但缺Archive且catalog事实仍绑定该ref时，报告`IncompleteArchive` Error、FactsStatus=Invalid并拒绝rebuild；若索引准确反映事实，IndexesStatus可独立为Consistent。

退出码：0=Completed且事实/索引均一致；2=已明确发现事实/索引问题或不支持的格式（不代表扫描过全部历史，查看Completed）；3=I/O/取消/无法完成判定，优先于已有局部finding；64=CLI输入错误。只输出metadata、计数与错误码，不输出payload、codec解压内容或任意exception.Message。--report必须在source外create-only；未指定则JSON到stdout。

rebuild只接受FactsStatus=Healthy且目录/边界唯一可解释。缺locator但规范连续段的最高段非空，或唯一segment1合法空，可生成定位候选；locator合法却指向old且next存在、缺段、highest为空且无法证明是否发布等多义情况拒绝。不得通过重建选择较旧head、补Bind/Archive或改事实bytes。

输出目录必须不存在且在source外。目录内`candidate/`存对应相对路径的locator/catalog，`manifest.json`在全部候选flush完毕后最后发布，含schemaVersion=1、Completed=true、source事实文件清单/长度/SHA256及candidate相对路径/长度/SHA256。没有完整manifest的目录永远不是可用候选。source前后摘要必须一致；实现必须防止输出路径逃逸。失败保留可诊断但不完整的输出，不原地安装，不复制/改写业务事实。

EJ与SegmentStore可向`Atelia.EventJournal.Toolkit`授予内部可见性，共享各自codec/路径和审计helper；工具ProjectReference上游，生产库不能反向引用工具。不公开任意raw mutate接口。toolkit自身IsPackable=false，不进入五包列表。候选安装只在tests的隔离副本中验证，真实安装另行授权。

### 5.1 JSON字段与清单边界

以下JSON键固定为camelCase，枚举值使用表中大小写；没有省略的可选键，未知值不得被reader默认为Healthy。所有计数/长度为非负64-bit整数，offset为非负64-bit整数或null。

| 对象 | 必需字段与类型 |
| --- | --- |
| audit report | schemaVersion=1；completed:bool；factsStatus:string；indexesStatus:string；counts:object；findings:array |
| counts | eventSegments、events、refObjects、refMoves、refOpFrames、tagBindings、storedFrameBytesChecked，均非负整数；最后一项累计完整checked-read的RBF ticket bytes，重复校验重复计数，不冒充物理磁盘I/O |
| finding | severity=Error/Warning；code:string；relativePath:string；offset:integer/null；refId:16位小写hex/null；eventAddress:object/null |
| eventAddress | ticketPacked:16位小写hex；segmentNumber:u32≥1；hint:8位小写hex |
| candidate manifest | schemaVersion=1；completed=true；sourceRoot:string；sourceFacts:array；sourceIndexes:array；outputs:array |
| sourceFacts entry | kind=Format/EventSegment/RefOpLog/RefMoveSegment；relativePath:string；length:integer；sha256:64位小写hex |
| sourceIndexes entry | kind=Locator/CatalogSnapshot；relativePath:string；present:bool；length:integer/null；sha256:string/null。absent时两个值都null |
| outputs entry | kind=Locator/CatalogSnapshot；relativePath:string（相对目标journal，不含candidate前缀）；length:integer；sha256:64位小写hex |

finding.code沿用§3的ReasonCode，并允许以下audit专用值：EventFrameInvalid、PayloadCodecInvalid、ParentInvalid、EventSequenceInvalid、RefAllocationInvalid、RefInitInvalid、RefMoveInvalid、RefTargetInvalid、TagBindingInvalid、TagTargetInvalid、DirectoryInventoryInvalid、UnpublishedRef、IncompleteArchive、OrphanEvent、IgnoredTemporaryMetadata、OperationCancelled、IoFailure。具体坏帧保留精确路径/offset，不需要输出原payload或底层异常文本。

sourceFacts必须包含有效`journal.format`、全部events RBF、ref-op-log以及全部ref object RBF，包括orphan/unpublished对象；不含locator/snapshot/temp/cache。根format不支持或缺失时拒绝rebuild，本工具不替代旧格式迁移。sourceIndexes记录所有对应locator及snapshot的存在/摘要；缺失项也列出。sourceFacts/sourceIndexes的路径清单与bytes在扫描前后均须一致，不能只hash最初列出的文件而漏掉新出现的文件。任意cache、temp残留只报告，不进入候选安装集合。

relativePath统一`/`分隔，禁止绝对路径、空组件、`..`或reparse逃逸；根级目录finding可用`.`。manifest数组按relativePath的ordinal顺序输出，无重复；outputs中的实体写在`<output>/candidate/<relativePath>`。读取manifest不能据其路径任意访问source外文件。sourceRoot仅记录来源位置，不声称是跨仓身份。

下面是“空v2 journal的facts健康、两个索引缺失”的最小audit示例；扫描数不代表实现已运行：

```json
{
  "schemaVersion": 1,
  "completed": true,
  "factsStatus": "Healthy",
  "indexesStatus": "Missing",
  "counts": { "eventSegments": 1, "events": 0, "refObjects": 0, "refMoves": 0, "refOpFrames": 0, "tagBindings": 0, "storedFrameBytesChecked": 0 },
  "findings": [
    { "severity": "Error", "code": "MetadataMissing", "relativePath": "events/active.segment", "offset": null, "refId": null, "eventAddress": null },
    { "severity": "Error", "code": "MetadataMissing", "relativePath": "refs/catalog.snapshot", "offset": null, "refId": null, "eventAddress": null }
  ]
}
```

对应manifest示例使用下节内存构造的精确bytes与SHA256，不声称`/example/source`是实际存在的仓库：

```json
{
  "schemaVersion": 1,
  "completed": true,
  "sourceRoot": "/example/source",
  "sourceFacts": [
    { "kind": "EventSegment", "relativePath": "events/buckets/000000/00000001.rbf", "length": 4, "sha256": "9c015e3e48da804c41adc4cf8834dc8055689d3ed68fce65c10b2e85153c71e6" },
    { "kind": "Format", "relativePath": "journal.format", "length": 16, "sha256": "a96b9af01f19f0e075922f9400c012f3ef33050afb2ed9a73891eedd9748dffe" },
    { "kind": "RefOpLog", "relativePath": "refs/ref-op-log.rbf", "length": 4, "sha256": "9c015e3e48da804c41adc4cf8834dc8055689d3ed68fce65c10b2e85153c71e6" }
  ],
  "sourceIndexes": [
    { "kind": "Locator", "relativePath": "events/active.segment", "present": false, "length": null, "sha256": null },
    { "kind": "CatalogSnapshot", "relativePath": "refs/catalog.snapshot", "present": false, "length": null, "sha256": null }
  ],
  "outputs": [
    { "kind": "Locator", "relativePath": "events/active.segment", "length": 20, "sha256": "fc81d42a5627ac65b7a6db219333221e094577bf9a930cd3acdc242742ae7dce" },
    { "kind": "CatalogSnapshot", "relativePath": "refs/catalog.snapshot", "length": 68, "sha256": "bc6ec8d79c78adb1f7d88392f27f16ab664ced4c13d68f1fd62e9da91796977b" }
  ]
}
```

## 6. 独立格式向量

本轮以独立Python位算法按§1 CRC规则生成，且断言CRC32C(`123456789`)=`e3069283`。这只验证冻结规格的编码算例，不是C#实现测试。十六进制不含空格：

| 记录 | bytes | hex |
| --- | --- | --- |
| journal.format | 16 | `454a464d0200100000000000b47af8d3` |
| Bucketed active=1 | 20 | `52425341010014000000000001000000cf6cdc28` |
| Flat active=1 | 20 | `52425341010014000100000001000000e811e061` |
| empty snapshot | 68 | `454a4353010040004400000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000962ba6f3` |
| live snapshot | 105 | `454a4353010040006900000000000000480100000000000010000004010000006be942590100000001000000010000000000000000000000000000000000000004006d61696e1f0000040000000005007361766564160000040000000001000000000000007917f201` |

live向量对应：Append root（logical payload空、hint=0）→CreateBranch("main",root)→CreateTag("saved",root)之后的snapshot；root ticket=`0000000004000016`、segment=1；RefId=`000000000400001f`；op-log末tag ticket=`0000000104000010`、boundary=328；anchor内容CRC=`5942e96b`。时间戳不进入末tag payload，故不影响此向量。T03/T04必须用真实codec及public写路径重建并核对，不能只测试硬编码返回同一hex。

## 7. T00交接门

本附件冻结命名与字节布局，独立CRC向量已给出；下一实施会话核对基线、复算向量并列出逐字段拒绝case后才派发T01。T01编译和T03/T04的C#编码/集成验证仍须实际执行，不能把本附件的算例写成生产实现已通过。

若T01编译暴露ref-struct lifetime问题、原子替换probe不满足同卷合同、或以上错误映射不能保持tag三态，提交最小反例给astra修订此附件后再继续。不允许通过隐藏扫描、旧格式fallback、额外head权威或扩展repair范围绕过。

### T00 启动补充向量

时间戳和 reasonKind 固定为0，CreateBranch("main",…) 后 checkpoint。BindName ticket=`000000008400001f`，boundary=260，RefId=`000000000400001f`。以下由独立逐位CRC脚本按现有BindName字段构造，真实codec验证归T03/T04。非空head使用上面的root地址。

| 状态 | bytes | anchor CRC | hex |
| --- | --- | --- | --- |
| unborn | 82 | `2dce7609` | `454a435301004000520000000000000004010000000000001f000084000000000976ce2d0100000000000000010000000000000000000000000000000000000004006d61696e1f00000400000000ebaf6cf4` |
| 非空head | 82 | `5d5a95d9` | `454a435301004000520000000000000004010000000000001f00008400000000d9955a5d0100000000000000010000000000000000000000000000000000000004006d61696e1f00000400000000e924cd43` |
