# Atelia.VersionStore

设计、当前计划与验收索引见 [VersionStore 文档](../../docs/VersionStore/README.md)。

本项目参加 solution 构建与测试，已纳入主线本地候选包清单。普通 ref/历史/fork/tag/Audit 等既有范围取得[独立源码验收](../../docs/VersionStore/12-versionstore-final-acceptance.md)。本轮 CU 已正式接纳并实施，新增范围取得独立源码验收 Accepted，范围与实际验证见[07 CU 验收](../../docs/VersionStore/07-versionstore-cross-ref-conditional-update-acceptance.md)。[原六包候选](../../docs/FrameStore-VersionStore/13-local-package-delivery.md)不含 CU，版本/manifest/来源不变；本轮不自动重新打包或发布。设计入口为[S4](../../docs/VersionStore/04-versionstore-publication.md)与[07](../../docs/VersionStore/07-versionstore-cross-ref-conditional-update.md)。

VersionStore 借入一个 FrameStore，发布不透明 root-payload bytes。每个 ref 是独立 RBF3 文件中的完整快照序列；应用选择内容 codec 并保证 data 依赖闭包。PublishRefs 以 CU 的完整成员表共同裁决多个同库既有 ref 的发布资格，普通单 ref 更新仍为 Snapshot；不提供跨调用快照隔离或创建/tag/跨库事务。

当前源码包含：

- 持久上下文绑定的 `RefId`、完整位置 `RefRevision`、自有 `RefBytesSnapshot` 与 `PublicationOutcome`。
- 内部格式门、ref header/ForkOrigin、meta8 和规范路径 codec。
- 真实目录与公开 `Create/Open/OpenReadOnly`，独立 owner 锁、绑定 data 的格式门和唯一 pending 工作区。
- 自动/预留区指定 `CreateRef`、`ListRefs`、必要 header/head 校验、opaque bytes 自有读取与 data-first 的 `PublishRef`。
- `ReadRevision`：精确重读同持久 VSID 的库签发 revision，返回原位置/时间和自有 root bytes；共同 CU 资格与目标尾 Fence 仍须成立。
- 公共 `EnsureUsable`：先检查 VS 自身 Dispose/fault，再检查实际借入 data 的生命周期；纯检查无 I/O 或确认，失败不额外停用 VS。
- `PublishRefs`：私有捕获实际输入，空批拒绝、单成员普通 Snapshot、至少两个成员 fresh CU；canonical 自绑定成员表、整批一次取时、data-first 与全部文件确认，返回自有只读 revision 字典。
- `ReadRefHistory`：显式 `HistoryBudget`、固定完成尾上界、逐项 visitor、自有快照与 exact ForkOrigin 前缀接续；预算、早停和访问范围分别裁决。
- 自动/预留区指定 `ForkRef`：精确读取已签发 revision，确认 data 后单独确认源 ref，原样复制 bytes 并发布带 ForkOrigin 的子文件；不扫描源后续历史。
- `ListForks(maxRefs, cancellationToken)`：检查所有正式首帧及声明图，交付自有只读 `ForkInfo` 边列表，不读取 Snapshot 或重读来源 ticket。
- 私有初始化后一次不覆盖文件移动发布；异常停用、资源单次清理和锁最后释放。
- 匿名 `CreateTag` / `ResolveTag`：1024个惰性桶、独立 CRC 元信息查重、data-first 发布与自有 `RootPayload`；正式桶初始化保护、空桶发布及冷重开。
- 独立可选 `RootMapCodec.Encode/Decode`：有效 Unicode key、Ordinal、变长 FrameAddress、1MiB 上限及完整 codeword 解码。它不在发布内核中自动运行。

`Create(rootPath, data, reservedRefIdUpperBound: 0)` 建立空库，默认没有预留低号；自动创建从 1 开始。指定创建只允许 1..预留上界的空闲槽，自动创建使用正式集合水位加一。`Open(rootPath, data)` 独占打开并取消合格 pending；`OpenReadOnly(rootPath, data)` 共享只读打开，保留 pending，不恢复 ref。各工厂先检查实际借入 data 的可用性与模式，随后核对持久绑定。调用方保持 data 存活并串行使用两层；VS Dispose 不关闭 data。

缓存命中前可调用 `store.EnsureUsable()`，组合检查 VS 和 backing data 的寿命。VS 自身 Disposed 优先，再检查 VS fault，最后传播 data 的原 guard 异常；允许 RO/RW、活跃 data Builder 和现有 visitor。检查只证明当前时刻的生命周期资格，无 I/O、恢复、内容检查或输出确认。纯发布目录查询仍只检查 VS 自身；借入 data 的 guard 失败本身不 fault/清理 VS，真正发布中的 data barrier 异常仍按既有停用与 `PublicationOutcome` 合同处理。纯属性、已交付 revision/snapshot 是保存值，Dispose 仍可独立清理 VS 的资源。

核心公开闭环为 `Create → CreateRef → PublishRef → ReadRef/ReadRefHistory → Dispose → Open/RO → ReadRef/ReadRefHistory`。两种 `ForkRef` 已接通，并用公开创建和两层冷重开验证继承历史。`ListForks` 已可查询全部正式 ref 的声明分叉图。tag 公开闭环、关联 overload、ReadTagInfo 与 ScanTags 已接通；只读完整校验 `Audit` 已接通。Windows 源码测试与 Linux、进程终止、包消费资格分别记录，见实施计划。

保存轻量 revision 后按需精确读取：

```csharp
RefRevision saved = store.ReadRef(refId).Unwrap().Revision;
// saved 可在同库重开 owner 后继续使用。
RefBytesSnapshot exact = store.ReadRevision(saved).Unwrap();
// exact.Revision == saved；原 RecordedAtUnixMs；Payload 仅含 root bytes。
```

`ReadRevision` 支持 RO/RW，只接受 nondefault、同持久 VSID 的库签发值，并在发现 I/O 前拒绝 default/foreign 输入。它按既有模式惰性打开目标，重验必要 header、完成范围、完整 CRC/envelope/meta、CU 共同资格与成功目标的尾 Fence；合法未成立 CU 返回 `VersionStore.InvalidRefRecord`，不改选当前值或更旧点。该入口不确认 data/源文件、不发布或产生 Outcome。成员来源仍依赖库签发值、完整位置不删除/复用与无外部改写；无裸 ticket/位置构造或跨进程匿名 revision 导入。重读旧点不为重新定位目标扫描本地后缀；CU 必要 peer 与异长消歧仍可能访问多个文件。自有结果可在后续读、owner fault/Dispose 后继续使用。

可独立运行的 [VersionStore.Smoke](../../tools/VersionStore.Smoke/README.md) 仅通过 public API 演示多根/循环 data、历史选点、两种 fork、rewind/tag 及新进程只读重开。solution 中的 SystemIntegration 测试另负责真实子进程锁矩阵和公开停点强杀；内部写入窗口、断电和包消费不由这些停点证明。 同一consumer的 resource-write/resource-read 提供16/64/256个ref的有限资源观察，平台实测和测量边界见 [V4-C记录](../../docs/VersionStore/12-versionstore-linux-resource-acceptance.md)。

其中[应用策略示例](../../tools/VersionStore.Smoke/README.md#应用策略示例)用四个RootMap根演示tool-loop冻结请求/Started确认、uncertain或receipt查询恢复，以及单driver的operationId/generation合并。应用边界强杀、冷恢复与过期/重复回包拒绝由跨进程测试验证；外部receipt和应用结算规则属于示例，不成为存储的自动重试或exactly-once合同。

历史调用示例：

```csharp
var result = store.ReadRefHistory(refId, new HistoryBudget(100, 1000), snapshot => {
    // snapshot 与 Payload 在调用结束或 owner Dispose 后仍可保存使用。
    return true; // false：接收当前项后立即正常停止。
});
```

`Complete` 只证明所访问的继承发布前缀，`VisitorStopped` 不检查更早节点；额度不足返回 `VersionStore.HistoryBudgetExceeded`，不伪装完整结果。工作步在动作前计进入 ref（含 CU peer）、每次 MoveNext、完整帧读取和 CU 异长消歧结构后继查询，包含边界 helper 的重复完整读；它不是物理 I/O、内存或时间上限。未提交 CU 不交付，peer 访问不加入继承 visited，也不递归替代组。callback 可以读取当前值或调用 `ReadRevision`，匹配 ref 借用现有句柄；visitor 自行精确读是独立操作，不自动计入外层 HistoryBudget。history 内核继续对自己的全部必要工作计费。同 owner mutation 和递归历史被拒绝；需要 fork/rewind 时在历史返回后执行。callback Dispose/取消/异常或必要关闭失败均不能作为正常结束。

`ForkRef(selectedRevision, out outcome)` 自动分配新编号；`ForkRef(reservedLocalId, selectedRevision, out outcome)` 创建空闲预留槽。revision 可以来自当前值、历史 visitor 或同库先前 owner 的已交付结果；必须等历史调用结束再创建。fork 完整读取 exact 源点并检查尾 Fence，CU 来源还验证共同判据，未提交来源直接拒绝。随后确认 data 和 exact source 文件，沿 ProcessCrashOnly 发布子文件；不额外确认全部 peer 或扩充断电保证。成功返回子 revision；子初始始终为普通 Snapshot，仅 clone root bytes（排除 CU Members），与源逐 byte 相同，记录时间重新取得。创建不推进源 head；无来源复制用 `CreateRef(snapshot.Payload, out outcome)`。

`ListForks` 的正整数 `maxRefs` 包含全部正式 ref，连没有来源的根也计数；恰好达到额度且枚举正常结束可成功，超额返回 `VersionStore.ForkQueryBudgetExceeded`，不交付部分图。每次查询重新检查 header、缺源与环，不保留跨调用索引。结果只声明创建关系；`SourceSnapshotTicket` 不是新签发的源 revision，也不证明初始/源 payload 一致或全库内容健康。查询可在历史 visitor 内执行，借用同文件句柄；其他临时句柄逐个关闭，不加入长期缓存。

`CreateTag(name, payload, out outcome)` 按原 UTF-16 code units 的 Ordinal 名称创建，不覆盖已有 tag；成功值恒为 true。`ResolveTag(name)` 返回自有 `RootPayload`，不带 ref revision。名称查重只读取目标桶的小元信息，可信同名即占用，即使其 root 损坏；解析唯一目标时再检查完整 CRC。零命中但存在未知名称记录时返回缺失不确定。每次查询先检查全部已建桶路径名称/类型，只打开目标桶内容，成功不证明其他桶或 data 图健康。

`CreateTag(name, payload, relatedRevision, out outcome)` 额外记录同库已签发 revision 的坐标注记。它仍冻结传入 bytes，不读取、确认或比较关联源；坐标不提供可直接传给 ForkRef 的来源资格。`ReadTagInfo(name)` 返回自有 `TagInfo`，仅检查独立元信息；即使 root 内容损坏仍可能成功。

```csharp
var result = store.ScanTags(info => {
    // 可按 info.RecordedAtUnixMs / RelatedRefId 筛选；仍遍历全部实际桶。
    return true; // false：收到当前项后停止，不推进下一项。
});
```

`ScanTags` 按物理记录交付元信息，包含重复名称；所到坏元信息直接失败。`Complete` 只证明遍历范围内的元信息，不证明 root 健康、全库名称唯一或关联源有效。回调允许 ReadRef、ReadRevision、ReadTagInfo、ResolveTag 和 ListForks，同桶查询借用当前扫描句柄；回调内全部 mutation、递归 tag 扫描及进入历史被拒绝。历史回调可以进入 tag 扫描。取消、回调异常、Dispose 或必要关闭失败优先于正常早停；所有 `TagInfo` 在 owner 关闭后仍有效。

完整检查使用单独的只读 owner：

```csharp
using var check = VersionStore.OpenReadOnly(rootPath, data);
var audit = check.Audit(cancellationToken).Unwrap();
// audit.RefCount / SnapshotCount / ForkCount / TagBucketCount / TagCount
```

先关闭同根 writer，再取得只读 owner。Audit 检查所有正式 ref 的初始化和全部物理 Snapshot/CU、fork 来源真实主链 exact ticket/共同发布资格与初始 root bytes 一致性，以及全部 tag 的完整 root/meta 与名称重复。完整 CU 即使未提交仍检查；未提交 CU 可通过但不能作为 fork 来源。成功计数只在全部 EOF 和必要清理完成后返回；SnapshotCount 计普通 Snapshot 加成立 CU，排除未提交 CU，不重复展开继承历史。可写 owner 返回 `VersionStore.ReadOnlyRequired`，history/tag callback 内也拒绝；取消和普通读异常不因检查本身停用 owner，owned 清理失败仍停用。临时打开全部只读，不恢复、修复或缓存全库句柄。

Audit 不解码应用 bytes、不遍历 data、不把 tag Related 注记变成来源依赖，也不审 private pending 内容。首次错误即失败，没有部分健康报告；当前实现保留 O(ref 数) 的图/被引用位置以及当前桶名称集合，至多两个当前完整帧与两个新增临时 RBF 句柄（不含调用前已有缓存或平台路径检查句柄）。取消不是资源额度；系统规模资格另行测量。精确合同见 S4 `[A-VS-AUDIT]`。

`RootMapCodec` 已可独立消费。应用从 FrameStore 正常追加取得地址，用 `Encode(roots)` 生成自有 byte[]；取得 root bytes 后，显式 `Decode(payload)` 得到自有 Ordinal 冻结字典。空 RootMap 编成单字节 `00`，而 VersionStore 的空 payload 不合法。JSON 或其他编码的应用无需使用 RootMapCodec。

写入内核按实际 slice 捕获私有 bytes，在 data barrier/发布输出前完成确定检查；先 `data.ConfirmDurable()`，再正式追加和 flush。`PublishRefs(changes, out outcome)` 不信任输入 Count/comparer，全部副本与枚举器清理完成后按实际数量分流；CU 全部完成且全部文件 flush 返回才先写 Confirmed，再交付整个自有只读 revision 字典，没有部分成功。`out PublicationOutcome` 区分未尝试、未知和已确认，异常不得抹掉证据。VS 停用会受控清理文件与锁（含短期 CU peer 槽），不 Dispose 借入 data；自有结果可跨 owner Dispose 保留。当前值对未提交 CU 继续向前，必要坏记录/peer 错误传播，不解码应用内容或预读无关历史。普通 PublishRef 覆盖仅检查本地物理 head，不强制验证旧组 peer；lazy CompletedTail 解释见07。

项目仅引用 main 的 FrameStore/Binary/Rbf/Data/Primitives，不引用冻结 RBF1 库或业务包。独立完整校验的源码验收与进程终止、两平台、包资格按实施计划分别记录。
