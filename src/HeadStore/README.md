# Atelia.HeadStore

当前采用 `Atelia.HeadStore` 名称与 `HeadId / HeadEntryId / HeadEntry` 公面；2026-10-16 行为保持重命名已通过独立源码复核与 Windows Release 验收，状态见[重命名记录](../../docs/VersionStore/13-headstore-renaming-plan.md)。既有验收仍对应原 VersionStore revision。本轮已完成格式2与HeadSetTag成员表替换，并通过Windows源码、进程和本地包消费验收，实际资格见[14验收记录](../../docs/VersionStore/14-headset-tags-acceptance.md)；旧bytes/Related API退出，不沿用旧tag验收。

设计、当前计划与验收索引见 [HeadStore 文档](../../docs/VersionStore/README.md)。

本项目参加 solution 构建与测试，已纳入主线本地候选包清单。普通 Head/历史/fork/tag/Audit 等既有范围取得[独立源码验收](../../docs/VersionStore/12-versionstore-final-acceptance.md)。本轮 CU 已正式接纳并实施，新增范围取得独立源码验收 Accepted，范围与实际验证见[07 CU 验收](../../docs/VersionStore/07-versionstore-cross-ref-conditional-update-acceptance.md)。[原六包候选](../../docs/FrameStore-VersionStore/13-local-package-delivery.md)不含 CU，版本/manifest/来源不变；HeadSetTag新候选及四个隔离消费者见[14验收记录](../../docs/VersionStore/14-headset-tags-acceptance.md)，公开发布另行进行。设计入口为[S4](../../docs/VersionStore/04-versionstore-publication.md)与[07](../../docs/VersionStore/07-versionstore-cross-ref-conditional-update.md)。

HeadStore 借入一个 FrameStore，发布不透明 root-payload bytes。每个 Head 是独立 RBF3 文件中的完整快照序列；应用选择内容 codec 并保证 data 依赖闭包。PublishHeads 以 CU 的完整成员表共同裁决多个同库既有 Head 的发布资格，普通单 Head 更新仍为 SingleEntry；不提供跨调用快照隔离或创建/tag/跨库事务。

当前源码包含：

- 持久上下文绑定的 `HeadId`、完整位置 `HeadEntryId`、自有 `HeadEntry` 与 `PublicationOutcome`。
- 内部格式门、Head header/ForkOrigin、meta8 和规范路径 codec。
- 真实目录与公开 `Create/Open/OpenReadOnly`，独立 owner 锁、绑定 data 的格式门和唯一 pending 工作区。
- 自动/预留区指定 `CreateHead`、`ListHeads`、必要 header/head 校验、opaque bytes 自有读取与 data-first 的 `PublishHead`。
- `ReadEntry`：精确重读同持久 HSID 的库签发 EntryId，返回原位置/时间和自有 root bytes；共同 CU 资格与目标尾 Fence 仍须成立。
- 公共 `EnsureUsable`：先检查 HS 自身 Dispose/fault，再检查实际借入 data 的生命周期；纯检查无 I/O 或确认，失败不额外停用 HS。
- `RecoveryReports`：本 owner 惰性打开 Head/tag 桶期间的成功物理修尾动作，只读累计视图；仅追加非 None 报告，fault/Dispose 后仍可读取。
- `PublishHeads`：私有捕获实际输入，空批拒绝、单成员普通 SingleEntry、至少两个成员 fresh CU；canonical 自绑定成员表、整批一次取时、data-first 与全部文件确认，返回自有只读 EntryId 字典。
- `ReadHeadHistory`：显式 `HeadHistoryBudget`、固定完成尾上界、逐项 visitor、自有快照与 exact ForkOrigin 前缀接续；预算、早停和访问范围分别裁决。
- 自动/预留区指定 `ForkHead`：精确读取已签发 EntryId，确认 data 后单独确认源 Head，原样复制 bytes 并发布带 ForkOrigin 的子文件；不扫描源后续历史。
- `ListHeadOrigins(maxHeads, cancellationToken)`：检查所有正式首帧及声明图，交付自有只读 `HeadOriginInfo` 边列表，不读取 SingleEntry 或重读来源 ticket。
- 私有初始化后一次不覆盖文件移动发布；异常停用、资源单次清理和锁最后释放。
- `CreateTag / ReadTag`：捕获所选Heads当前位置的自有不可变`HeadSetTag`；1024个惰性桶、独立CRC元信息查重、data后确认必要发布文件，再写成员表。`ReadTagInfo / ScanTags`只交付名称、时间和成员数。
- 独立可选 `NamedFrameMapCodec.Encode/Decode`：有效 Unicode key、Ordinal、变长 FrameAddress、1MiB 上限及完整 codeword 解码。它不在发布内核中自动运行。

`Create(rootPath, data, reservedHeadIdUpperBound: 0)` 建立空库，默认没有预留低号；自动创建从 1 开始。指定创建只允许 1..预留上界的空闲槽，自动创建使用正式集合水位加一。`Open(rootPath, data)` 独占打开并取消合格 pending；`OpenReadOnly(rootPath, data)` 共享只读打开，保留 pending，不恢复 Head。各工厂先检查实际借入 data 的可用性与模式，随后核对持久绑定。调用方保持 data 存活并串行使用两层；HS Dispose 不关闭 data。

缓存命中前可调用 `store.EnsureUsable()`，组合检查 HS 和 backing data 的寿命。HS 自身 Disposed 优先，再检查 HS fault，最后传播 data 的原 guard 异常；允许 RO/RW、活跃 data Builder 和现有 visitor。检查只证明当前时刻的生命周期资格，无 I/O、恢复、内容检查或输出确认。纯发布目录查询仍只检查 HS 自身；借入 data 的 guard 失败本身不 fault/清理 HS，真正发布中的 data barrier 异常仍按既有停用与 `PublicationOutcome` 合同处理。纯属性、已交付 EntryId/entry 是保存值，Dispose 仍可独立清理 HS 的资源。

核心公开闭环为 `Create → CreateHead → PublishHead → ReadHead/ReadHeadHistory → Dispose → Open/RO → ReadHead/ReadHeadHistory`。两种 `ForkHead` 已接通，并用公开创建和两层冷重开验证继承历史。`ListHeadOrigins` 已可查询全部正式 Head 的声明分叉图。本轮tag闭环改为CreateTag/ReadTag及HeadSetTagInfo/ScanTags；只读`Audit`增加成员引用检查，当前验收状态见14记录。Windows 源码测试与 Linux、进程终止、包消费资格分别记录，见实施计划。

保存轻量 EntryId 后按需精确读取：

```csharp
HeadEntryId saved = store.ReadHead(headId).Unwrap().Id;
// saved 可在同库重开 owner 后继续使用。
HeadEntry exact = store.ReadEntry(saved).Unwrap();
// exact.Id == saved；原 RecordedAtUnixMs；Payload 仅含 root bytes。
```

`ReadEntry` 支持 RO/RW，只接受 nondefault、同持久 HSID 的库签发值，并在发现 I/O 前拒绝 default/foreign 输入。它按既有模式惰性打开目标，重验必要 header、完成范围、完整 CRC/envelope/meta、CU 共同资格与成功目标的尾 Fence；合法未成立 CU 返回 `HeadStore.InvalidHeadRecord`，不改选当前值或更旧点。该入口不确认 data/源文件、不发布或产生 Outcome。成员来源仍依赖库签发值、完整位置不删除/复用与无外部改写；无裸 ticket/位置构造或跨进程匿名 EntryId 导入。重读旧点不为重新定位目标扫描本地后缀；CU 必要 peer 与异长消歧仍可能访问多个文件。自有结果可在后续读、owner fault/Dispose 后继续使用。

观察一次访问期间的物理恢复：

```csharp
IReadOnlyList<HeadStoreRecoveryReport> reports = store.RecoveryReports;
int beforeCount = reports.Count;
var result = store.ReadHead(headId);
int afterCount = reports.Count;
for (int index = beforeCount; index < afterCount; index++) {
    HeadStoreRecoveryReport report = reports[index];
    // report.FileKind / LocalId / Recovery
}
```

`RecoveryReports` 初始为空，属于本 owner 的 HeadStoreId。`FileKind.Head` 的 LocalId 是非零本地 Head 编号，`FileKind.TagBucket` 是 0..1023 桶号。普通 Head、历史/ListHeadOrigins 临时 Head、CU peer、named tag 与 ScanTags 的可写惰性打开共享记录通道；借用已开句柄或以后同文件健康打开不会增加报告。RO 不修尾、需恢复时仍拒绝。

保存的视图 Count 可以增长，已有项和顺序不变；读取 `[beforeCount, afterCount)` 获得本次新增动作，需要固定集合或异步交付日志时由调用方复制。枚举不提供跨调用快照保证。视图只保存动作值集合，不持有 owner、backend、文件或锁，也不暴露可写集合通道；getter、Count/indexer 和既有视图在 fault/Dispose 后仍可用。调用方仍按单 driver 串行操作。

仅记录成功 RBF open 返回的非 None 动作；记录早于必要 header/CU/tag 初始化保护和资源移交检查，因此后续读取、visitor、取消或清理失败仍保留已执行动作，异常后也可取得 afterCount。报告不证明发布成立、root/data 图健康或旧调用 Confirmed，`NotAttempted` 可以伴随 lazy 修尾。RBF 工厂失败可能已改 bytes 却没有成功报告，台账不合成 None 或提供完整失败取证；借入 FS 的恢复报告仍归 FS。独立导出日志时一并保存 HeadStoreId，报告不签发 HeadEntryId。

可独立运行的 [HeadStore.Smoke](../../tools/HeadStore.Smoke/README.md) 仅通过 public API 演示多根/循环 data、历史选点、两种 fork、rewind/tag 及新进程只读重开。solution 中的 SystemIntegration 测试另负责真实子进程锁矩阵和公开停点强杀；内部写入窗口、断电和包消费不由这些停点证明。 同一consumer的 resource-write/resource-read 提供16/64/256个Head的有限资源观察，平台实测和测量边界见 [V4-C记录](../../docs/VersionStore/12-versionstore-linux-resource-acceptance.md)。

其中[应用策略示例](../../tools/HeadStore.Smoke/README.md#应用策略示例)用四个NamedFrameMap根演示tool-loop冻结请求/Started确认、uncertain或receipt查询恢复，以及单driver的operationId/generation合并。应用边界强杀、冷恢复与过期/重复回包拒绝由跨进程测试验证；外部receipt和应用结算规则属于示例，不成为存储的自动重试或exactly-once合同。

历史调用示例：

```csharp
var result = store.ReadHeadHistory(headId, new HeadHistoryBudget(100, 1000), entry => {
    // entry 与 Payload 在调用结束或 owner Dispose 后仍可保存使用。
    return true; // false：接收当前项后立即正常停止。
});
```

`Complete` 只证明所访问的继承发布前缀，`VisitorStopped` 不检查更早节点；额度不足返回 `HeadStore.HistoryBudgetExceeded`，不伪装完整结果。工作步在动作前计进入 Head（含 CU peer）、每次 MoveNext、完整帧读取和 CU 异长消歧结构后继查询，包含边界 helper 的重复完整读；它不是物理 I/O、内存或时间上限。未提交 CU 不交付，peer 访问不加入继承 visited，也不递归替代组。callback 可以读取当前值或调用 `ReadEntry`，匹配 Head 借用现有句柄；visitor 自行精确读是独立操作，不自动计入外层 HeadHistoryBudget。history 内核继续对自己的全部必要工作计费。同 owner mutation 和递归历史被拒绝；需要 fork/rewind 时在历史返回后执行。callback Dispose/取消/异常或必要关闭失败均不能作为正常结束。

`ForkHead(selectedEntryId, out outcome)` 自动分配新编号；`ForkHead(reservedLocalId, selectedEntryId, out outcome)` 创建空闲预留槽。EntryId 可以来自当前值、历史visitor、完整ReadTag成员表或同库先前owner的已交付结果；必须等历史调用结束再创建。fork 完整读取 exact 源点并检查尾 Fence，CU 来源还验证共同判据，未提交来源直接拒绝。随后确认 data 和 exact source 文件，沿 ProcessCrashOnly 发布子文件；不额外确认全部 peer 或扩充断电保证。成功返回子 EntryId；子初始始终为普通 SingleEntry，仅 clone root bytes（排除 CU Members），与源逐 byte 相同，记录时间重新取得。创建不推进源 head；无来源复制用 `CreateHead(entry.Payload, out outcome)`。

`ListHeadOrigins` 的正整数 `maxHeads` 包含全部正式 Head，连没有来源的根也计数；恰好达到额度且枚举正常结束可成功，超额返回 `HeadStore.ForkQueryBudgetExceeded`，不交付部分图。每次查询重新检查 header、缺源与环，不保留跨调用索引。结果只声明创建关系；`SourceEntryTicket` 不是新签发的源 EntryId，也不证明初始/源 payload 一致或全库内容健康。查询可在历史 visitor 内执行，借用同文件句柄；其他临时句柄逐个关闭，不加入长期缓存。

`CreateTag(name, heads, out outcome)` 接受非空HeadId的ReadOnlySpan，捕获调用时当前已接受EntryId，返回不可变具名表；不接受任意bytes、Related或历史EntryId组合。名称继续按1..1024个原UTF-16 code units/Ordinal比较，create-only。输入slice先私有复制，拒绝default、外库、重复、缺失或必要坏成员。

```csharp
HeadSetTag created = store.CreateTag(
    "checkpoint", [firstHeadId, secondHeadId], out PublicationOutcome outcome).Unwrap();
HeadSetTag savedTag = store.ReadTag("checkpoint").Unwrap();
foreach (var pair in savedTag.Entries) {
    HeadEntry exact = store.ReadEntry(pair.Value).Unwrap();
    // pair.Key == pair.Value.HeadId；exact.Payload仍由应用解释。
}
```

后续发布不改变表；fork子Head捕获的是子初始/当前Entry身份。只选A且A来自A/B CU时，表只含A，但创建在data确认后仍确认该成立组的必要A/B文件。PublishHeads与CreateTag是两个操作，不提供发布并命名事务。新格式门与所有header统一版本2，拒绝版本1门，不做旧bytes payload猜测转换；路径仍是versionstore.format/lock、refs/tags。

`ReadTag`只检查唯一可信名称、完整RBF帧和canonical成员表，不打开成员文件。其恢复的HeadEntryId是定位值；表成功而随后ReadEntry失败可以同时成立。ReadEntry/Fork保持精确内容/尾Fence/CU资格与既有来源协议，不新增全链定位扫描。元信息整体健康但表坏时，名称仍占用，ReadTagInfo可成功，ReadTag/Audit失败。

`ReadTagInfo(name)` 返回自有`HeadSetTagInfo`，只有Name、RecordedAtUnixMs、EntryCount；不证明表或成员健康。named扫描允许其他unknownName与唯一可信目标共存；零可信命中且有unknownName才报告缺失不确定，不能返回TagNotFound或创建。

```csharp
var result = store.ScanTags(info => {
    // 可按info.RecordedAtUnixMs / EntryCount筛选；仍遍历全部实际桶。
    return true; // false：收到当前项后停止，不推进后缀。
});
```

ScanTags逐条交付物理元信息，包含重复名称；必要坏meta直接失败。Complete不证明全局名称唯一、完整表或成员健康。回调允许ReadHead、ReadEntry、ReadTag、ReadTagInfo、ListHeadOrigins，同桶查询借用扫描句柄；全部mutation、递归scan和进入history被拒绝，history callback可以scan。取消、异常、Dispose与必要cleanup优先于正常停止。自有tag表和info在owner关闭后仍有效。

CreateTag的窄TagCreateActive guard拒绝内部hook/取时窗口中的所有mutation重入；每次hook和owned I/O后复检owner，Dispose/fault后不继续I/O。data/依赖确认及空桶维护仍为NotAttempted，正式Append起Unknown，桶flush成功立即Confirmed；后续失败不降级。Unknown后重开按名称和固定成员表判断，不自动重新捕获当前Heads或机械重试。

完整检查使用单独的只读 owner：

```csharp
using var check = HeadStore.OpenReadOnly(rootPath, data);
var audit = check.Audit(cancellationToken).Unwrap();
// audit.HeadCount / EntryCount / OriginCount / TagBucketCount / TagCount
```

先关闭同根 writer，再取得只读 owner。Audit 检查所有正式 Head 的初始化和全部物理 SingleEntry/CU、fork 来源真实主链 exact ticket/共同发布资格与初始 root bytes 一致性，以及全部tag的完整canonical成员表/meta、名称重复与成员引用可读性。完整 CU 即使未提交仍检查；未提交 CU 可通过但不能作为 fork 来源。成功计数只在全部 EOF 和必要清理完成后返回；EntryCount 计普通 SingleEntry 加成立 CU，排除未提交 CU，不重复展开继承历史。可写 owner 返回 `HeadStore.ReadOnlyRequired`，history/tag callback 内也拒绝；取消和普通读异常不因检查本身停用 owner，owned 清理失败仍停用。临时打开全部只读，不恢复、修复或缓存全库句柄。

Audit不解码应用bytes、不遍历data，也不审private pending内容。HeadSetTag成员是正式引用，成员不存在/损坏/未成立CU必须失败；成员验证不重复计EntryCount。取消与状态检查传入内部exact/CU工作，局部pooled帧和临时文件先清理、根锁最后，不循环公共ReadEntry填永久cache。首次错误即失败，没有部分健康报告；当前实现保留 O(Head 数) 的图/被引用位置以及当前桶名称集合，具体短期句柄/pooled帧边界随tag成员路径单独核验（不含调用前已有缓存或平台路径检查句柄）。取消不是资源额度；系统规模资格另行测量。精确合同见 S4 `[A-VS-AUDIT]`。

`NamedFrameMapCodec` 已可独立消费。应用从 FrameStore 正常追加取得地址，用 `Encode(frames)` 生成自有 byte[]；取得 root bytes 后，显式 `Decode(payload)` 得到自有 Ordinal 冻结字典。空 NamedFrameMap 编成单字节 `00`，而 HeadStore 的空 payload 不合法。JSON 或其他编码的应用无需使用 NamedFrameMapCodec。

写入内核按实际 slice 捕获私有 bytes，在 data barrier/发布输出前完成确定检查；先 `data.ConfirmDurable()`，再正式追加和 flush。`PublishHeads(changes, out outcome)` 不信任输入 Count/comparer，全部副本与枚举器清理完成后按实际数量分流；CU 全部完成且全部文件 flush 返回才先写 Confirmed，再交付整个自有只读 EntryId 字典，没有部分成功。`out PublicationOutcome` 区分未尝试、未知和已确认，异常不得抹掉证据。HS 停用会受控清理文件与锁（含短期 CU peer 槽），不 Dispose 借入 data；自有结果可跨 owner Dispose 保留。当前值对未提交 CU 继续向前，必要坏记录/peer 错误传播，不解码应用内容或预读无关历史。普通 PublishHead 覆盖仅检查本地物理 head，不强制验证旧组 peer；lazy CompletedTail 解释见07。

项目仅引用 main 的 FrameStore/Binary/Rbf/Data/Primitives。main 仅支持 RBF3，旧栈维护与归档见 [RBF1 退役记录](../../docs/rbf1-retirement.md)。独立完整校验的源码验收与进程终止、两平台、包资格按实施计划分别记录。
