# HeadStore 术语与符号重命名方案

日期：2026-10-16。状态：行为保持重命名已完成，独立源码复核与 Windows Release 验收通过；未提交、打包或发布。本文记录迁移对照和本轮交付；[HeadSetTag 方案](14-named-head-snapshots-plan.md)负责下一片用具名固定成员表替换旧 bytes tag，尚未实施。第3节是原名到目标名的核验对照，不再作为要求用户逐项 IDE 操作的待办。既有 VersionStore 验收与包证据保留原 revision 身份。

## 1. 来源、边界与顺序

- 用户确认：VersionStore 改为 HeadStore；旧 tag 替换为所选 Head 当前位置的具名固定成员表。用户已先行修改一批 symbol，随后授权 coding agents 完成剩余重命名；本轮覆盖源码、测试、工具、工程及活跃文档，不实施新的 tag 行为。
- 本轮由主线程与独立只读 subagent 讨论后定名 **HeadSetTag**，替代前稿 HeadSnapshot。Set 在局部代码中直接提示集合语义，降低 coding agent 误作单 Head 标签的风险；允许单成员，不新增 HeadSet 实体。方法与桶保留简短 Tag 词根，完整命名家族见第4节。
- 下游移交：[HeadStore 重命名与具名快照提案](../../../durable-graph/artifacts/HeadStore-renaming-and-named-snapshots-proposal-2026-10-16.md)。该文与本方案属于同一需求来源，不互作独立证据。
- 核对基线：Storage `3de8fb050d81d35ee1c36d1b978024cbc42cb455`；DG `998ce968d0c674ef7686629af4dab7c5202c2692`，DG 当前 pin 对应此 Storage revision。本轮先核对并保留用户已有工作，再补齐剩余标识符。
- 本次核对对象为上述 Storage HEAD 上的未提交工作树。用户已完成大部分公共符号、重载和参数；agents 继续统一内部符号、字符串合同、工程/目录和文档。EventJournal 的 RefId 误改已纠正，冻结旧栈保持原名。本轮验证见第6节，不沿用此前验证结论。
- 先完成本篇的行为保持重命名，再实施 HeadSetTag 替换。重命名片暂保留旧 tag API 和行为；这是短暂施工状态，不添加 alias、适配层或双写。

## 2. 最小概念

| 概念 | 含义与不可改变的边界 |
| --- | --- |
| Head | 持久发布入口；身份不随发布变化。current entry 是其当前值，不是 Head 本身 |
| HeadEntryId | 实际 HeadId + 完整 SizedPtr ticket；不是新的逻辑序号或全库 CommitId |
| HeadEntry | 一次 checked-read 的自有结果：Id、RecordedAtUnixMs、Payload |
| HeadEntryPayload | Entry 中由应用解释的完整 bytes；只是概念名，不新增包装类型 |
| HeadSetTag | 不可变的具名 HeadId → HeadEntryId 表，固定创建时所选非空 Head 集合的当前位置；可只有一个成员，不随后续发布移动 |
| HeadOriginInfo | checked header 的来源声明；来源 ticket 不自动变成已签发 EntryId |

保留：同库持久身份、精确读取无回退、fork 初始克隆的新身份、沿来源接续的历史、CU 共同判据、所有权与生命周期、PublicationOutcome、惰性恢复和只读 Audit。HeadHistory 跨入来源时仍交付来源 Head 的 EntryId，不把它伪装成子 Head 的记录。

## 3. 行为保持重命名对照

第3.1–3.3节保留完整基线映射，供核验公共 API 与下游迁移；第3.4–3.5节记录本轮补齐范围。各重载和 named arguments 分别核验；只处理 main 新栈及其活跃测试/工具，不触碰冻结的 EventJournal/RbfSegmentStore。旧列保留原名是迁移依据，不代表当前声明。

### 3.1 类型

| 旧符号（Atelia.VersionStore 下） | 新符号 |
| --- | --- |
| VersionStore | HeadStore |
| RefId | HeadId |
| RefRevision | HeadEntryId |
| RefBytesSnapshot | HeadEntry |
| HistoryBudget | HeadHistoryBudget |
| HistoryEnd | HeadHistoryEnd |
| ForkInfo | HeadOriginInfo |
| RootMapCodec | NamedFrameMapCodec |
| VersionStoreAudit | HeadStoreAudit |
| VersionStoreRecoveryReport | HeadStoreRecoveryReport |
| VersionStoreRecoveryFileKind | HeadStoreRecoveryFileKind |

### 3.2 方法、属性、常量和枚举成员

| 旧声明符号 | 新名称 | 说明 |
| --- | --- | --- |
| VersionStore.VersionStoreId | HeadStoreId | 当前是 ReadOnlySpan<byte> 属性，不是独立类型 |
| VersionStore.ReservedRefIdUpperBound | ReservedHeadIdUpperBound | 预留低号范围不变 |
| VersionStore.CreateRef（两个重载） | CreateHead | 自动/指定编号语义不变 |
| VersionStore.ForkRef（两个重载） | ForkHead | 精确复制并产生新初始 Entry |
| VersionStore.PublishRef | PublishHead | 单入口发布 |
| VersionStore.PublishRefs | PublishHeads | 既有入口的联合发布 |
| VersionStore.ReadRef（两个重载） | ReadHead | HeadId 或本地编号读取 |
| VersionStore.ReadRevision | ReadEntry | 精确位置读取 |
| VersionStore.ListRefs | ListHeads | 入口发现 |
| VersionStore.ReadRefHistory | ReadHeadHistory | 保留继承历史 |
| VersionStore.ListForks | ListHeadOrigins | 保留声明图查询语义 |
| VersionStore.MaxRootPayloadLength | MaxHeadEntryPayloadLength | 此片数值不变；暂存 tag 使用处待下一片解除耦合 |
| RefRevision.RefId | HeadId | EntryId 内的实际入口身份 |
| RefBytesSnapshot.Revision | Id | HeadEntry.Id |
| HistoryBudget.MaxSnapshots | MaxEntries | positional record 参数及生成属性一起处理 |
| ForkInfo.ChildRefId | ChildHeadId | 子入口 |
| ForkInfo.SourceRefId | SourceHeadId | 来源入口 |
| ForkInfo.SourceSnapshotTicket | SourceEntryTicket | 仍是原始 SizedPtr，勿改成 SourceEntryId |
| VersionStoreAudit.RefCount | HeadCount | 正式入口数 |
| VersionStoreAudit.SnapshotCount | EntryCount | 普通记录 + 成立 CU，排除未成立 CU |
| VersionStoreAudit.ForkCount | OriginCount | 保留原精确来源/克隆检查统计 |
| VersionStoreRecoveryFileKind.Ref | Head | 保留枚举数值 |

### 3.3 公共参数

参数名可能被 named arguments 使用，应随声明做符号重命名并检查调用点：

| 声明范围 | 旧参数 | 新参数 |
| --- | --- | --- |
| VersionStore.Create | reservedRefIdUpperBound | reservedHeadIdUpperBound |
| CreateRef / ForkRef 的指定编号重载 | destLocalRefId | destLocalHeadId |
| ReadRef(uint) | localRefId | localHeadId |
| ReadRef / PublishRef / ReadRefHistory | refId | headId |
| ReadRevision | revision | entryId |
| ForkRef | sourceRevision | sourceEntryId |
| ListForks | maxRefs | maxHeads |
| RootMapCodec.Encode | roots | frames |

保留 `Payload`、`RecordedAtUnixMs`、`Ticket`、`LocalId`、`MaxWorkSteps`、`PublicationOutcome` 及其成员、`Create/Open/OpenReadOnly`、`EnsureUsable`、`RecoveryReports`、`Audit`、`Dispose`。没有收益的名字不为整齐而改。


### 3.4 本轮公共面补齐与核验

用户 IDE 操作已覆盖多个原待办；本轮按实际声明补齐并核对：

- `CreateHead`、`ReadHead`、`ForkHead` 的两个重载均使用新名称。
- 常量统一为 `MaxTagNameCodeUnits`，数值仍为 1024。
- 第3.3节的 12 个参数符号及 named arguments 统一到目标名，类型、顺序和默认值不变。
- `HeadEntry.Id`、`HeadEntryId.HeadId`、`HeadHistoryBudget.MaxEntries`、来源属性及 Audit 计数使用第3.2节新名。

公共声明入口：[HeadStore](../../src/HeadStore/HeadStore.cs)、[Fork](../../src/HeadStore/HeadStore.Fork.cs)、[Entry](../../src/HeadStore/HeadStore.Entry.cs)、[History](../../src/HeadStore/HeadStore.History.cs)、[NamedFrameMapCodec](../../src/HeadStore/NamedFrameMapCodec.cs)。本节记录实施目标与核验范围，最终通过资格见第6节。

### 3.5 内部符号与物理源码名称

| 基线符号 | 本轮名称 |
| --- | --- |
| IVersionStoreFiles / IVersionStoreTagFiles | IHeadStoreFiles / IHeadStoreTagFiles |
| DirectoryVersionStoreFiles / QualifiedRefTarget | DirectoryHeadStoreFiles / QualifiedHeadTarget |
| RefIdCodec / RefHeader / RefHeaderCodec | HeadIdCodec / HeadHeader / HeadHeaderCodec |
| CheckedRefHeader / RefRecordCodec / RefRecordReader | CheckedHeadHeader / HeadRecordCodec / HeadRecordReader |
| RefHeaderError / RefRecordError | HeadHeaderError / HeadRecordError |
| ExactRootMapBuffer | ExactNamedFrameMapBuffer |
| VersionStorePlatform / Paths / RecoveryLog / Factory / FactoryOperations / RuntimeError / TestHooks | 对应 HeadStore 前缀 |
| FormatGate.ReservedRefIdUpperBound | ReservedHeadIdUpperBound |
| RefHeader.RefId / CheckedRefHeader.RefId | HeadId |
| ForkOrigin.SourceRefId / SourceSnapshotTicket | SourceHeadId / SourceEntryTicket |
| ConditionalUpdateMember.LocalRefId | LocalHeadId |
| CheckedRefHeader.InitialSnapshotOffset | InitialEntryOffset |
| SnapshotTag / IsSnapshotShape | SingleEntryTag / IsSingleEntryShape |
| ReadSnapshot / ReadCheckedSnapshotFrame / CaptureExactSnapshot | ReadSingleEntry / ReadCheckedSingleEntryFrame / CaptureExactSingleEntry |
| 泛指已接受发布结果的 Snapshot / SnapshotCount | Entry / EntryCount |

普通物理记录称 `SingleEntry`，区别于 CU；公开 `HeadEntry` 可来自普通记录或成立 CU。字段、局部变量、错误码与相关测试按职责同步。结构改名不改变位置值、编码、FrameTag 数值或格式路径；C# `ref` 关键字、RBF frame tag 和旧 bytes tag 不机械替换。

## 4. 保持到下一片的旧 tag 合同与历史名称

| 当前内容 | 处理方式 |
| --- | --- |
| CreateTag / ResolveTag / ReadTagInfo / ScanTags | 下一片分别成为 CreateTag / ReadTag / ReadTagInfo / ScanTags；签名、返回类型和行为一起替换，不提前把 ResolveTag 改成 ReadTag 冒充新成员表读取 |
| TagInfo / TagScanEnd | 下一片使用 HeadSetTagInfo / HeadSetTagScanEnd；TagInfo删除Related并增加EntryCount，不列为当前纯rename |
| MaxTagNameCodeUnits | 本轮统一名称；值仍为1024 |
| TagInfo.RelatedRefId / RelatedTicket | 下一片删除，不为过渡状态单独重命名 |
| RootPayload | 下一片删除旧 tag 返回角色，不改名为 HeadSetTag 或 HeadEntryPayload |
| Audit.TagCount / TagBucketCount、HeadStoreRecoveryFileKind.TagBucket | 保留名称；下一片改为新HeadSetTag记录/桶的语义，Audit须校验成员引用 |
| 内部 SnapshotTag、SnapshotCount、InitialSnapshotOffset 等 | 本轮按第3.5节改为 Entry 或 SingleEntry 语义；不得替换为 HeadSetTag |
| 内部 CU Members | 保持“联合发布成员”的职责，不复用为 HeadSetTag 的公开集合 |
| 路径、格式版本、FrameTag 数值和 wire magic | 纯重命名片保持实际字节与路径不变 |
| 历史证据、已发布 manifest、旧包标识、固定来源链接 | 保留原身份，不能改写为新命名 |

最终新名称家族：HeadSetTag、HeadSetTagInfo、HeadSetTagScanEnd；CreateTag、ReadTag、ReadTagInfo、ScanTags；MaxTagNameCodeUnits；TagCount、TagBucketCount、TagBucket。创建guard错误码为HeadStore.TagCreateActive，内部记录仍可称TagRecord。HeadSetTag本身尚无源码类型，不需要用户先造类型或把RootPayload改名充当它；其Entries固定保存HeadId到HeadEntryId的映射，详见14方案。

## 5. 本轮工程迁移与独立后续事项

以下超出单个 C# symbol rename，本轮一并实施并核对；第7项仍为独立后续任务：

1. namespace `Atelia.VersionStore` → `Atelia.HeadStore` 及其子 namespace；生产、测试、Smoke、PackageSmoke 工程和程序集名称；InternalsVisibleTo。
2. `src/VersionStore` → `src/HeadStore`、`tests/VersionStore.Tests` → `tests/HeadStore.Tests`、`tools/VersionStore.Smoke` → `tools/HeadStore.Smoke`、`examples/VersionStorePackageSmoke` → `examples/HeadStorePackageSmoke`；solution 与 ProjectReference 同步。
3. NuGet ID `Atelia.VersionStore` → `Atelia.HeadStore`；`eng/Pack.ps1` 是唯一生产包顺序定义。更新 `eng/PackageMetadataCheck`、隔离消费者、脚本测试、发布工作流及公开回读验证。
4. 运行时错误码的模块前缀和 Ref/Revision 术语逐项统一；这是字符串合同，不依赖 Roslyn 自动修改。旧 tag 的特定错误留待下一片。更新活跃断言，不追改历史日志。
5. 内部 VersionStore/Ref 前缀类型、字段、文件名和测试名称按职责整理；不把 C# `ref` 关键字、RBF frame tag 或旧栈 tag 混入替换。
6. 当前有效规范与 README 同步术语；保持既有 Clause-ID、历史验收和 evidence 原身份。设计文档目录及 basename 本轮不迁移。
7. DG 源码适配、PackageReference 与 package pin 是独立接入任务；原 tag 功能未适配前不把新包当作可直接替换的依赖。

文件系统上的 `versionstore.format`、`versionstore.lock`、`refs`、`tags` 等属于格式，不由类/namespace 改名决定。HeadSetTag 片的格式策略见另一方案。

## 6. 验收与交接

本轮 agents 已在用户改名基础上收尾生产、测试/工具、工程与活跃文档；主线程已依据实际源码、声明、依赖及运行结果完成验收，并由独立 reviewer 复核生产和消费侧改动。

纯重命名验收：公共符号覆盖、重载/named arguments、程序集友元和工具接线一致；持久编码及格式路径不变；旧 tag 在本片仍维持原行为。Windows 下串行执行 Release build，再执行匹配的 `dotnet test Atelia.Storage.slnx -c Release --no-build`。实际新包交付还需新版本、干净已提交树、四个隔离 PackageReference smoke；源码通过不替代包证据。

2026-10-16 本轮验证记录（Windows / SDK 10.0.201，未提交工作树）：

- 源码/API/格式路径：53个生产C#文件经独立逐token复核，排除注释、标识符改名及106处诊断文本变更后，数值、运算符和执行结构保持；123个测试/工具/示例文件的golden bytes保留。大小写负例的`VersionStore.lock`与`Refs`曾被误改，本轮已恢复，未放松断言。
- 格式与范围：格式版本仍为1，Header/SingleEntry/CU的FrameTag仍为0/1/2；`versionstore.format`、`versionstore.lock`、`refs`、`tags`、`creating/pending.rbf`保留。冻结旧栈源码/测试无diff；39份历史验收、evidence/review等文件与初始工作树快照逐文件hash一致。未实施HeadSetTag。
- `dotnet build Atelia.Storage.slnx -c Release`：通过，0 warning / 0 error。
- `dotnet test Atelia.Storage.slnx -c Release --no-build`（附TRX输出参数）：3751 passed / 0 failed / 10平台条件skips；其中HeadStore为1416 passed / 6 skips。此为本次Windows运行，不宣称新的Linux或掉电资格。最终日志与9份TRX在本地`artifacts/rename/build-release-final.log`、`artifacts/rename/test-release-final.log`、`artifacts/rename/test-results-final/`，该目录为忽略的本地证据。
- `eng/Test-Rbf1ReferenceAssets.ps1`：7个旧栈项目实际assets均解析精确`0.2.0-rbf1-preview.1`及固定来源；HeadStore的5个Atelia依赖均为main项目。75条solution/ProjectReference路径检查通过。
- 临时`Atelia.Storage.Rename.slnx`：Release构建通过，0 warning / 0 error；`.rename/HeadStorePackageSmoke.Source.csproj`链接原示例源码的public smoke实际运行通过，包括普通/CU发布、精确读取、两层冷重开及Audit。这是源码消费证据，不是PackageReference资格。
- `eng/PackageMetadataCheck/PackageMetadataCheck.csproj`：Release构建通过，0 warning / 0 error；`eng/Test-Package.EntryPoint.Tests.ps1`及其调用的Verify-Published资源测试通过，均为mock，不访问发布端点、不证明真实包消费。mock证据目录为`W:/TEMP/headstore-rename-package-entry-80e46b74c1084090b9de661b15e85df6`。
- 工程与交付接线：源码/测试/Smoke/PackageSmoke目录、程序集/namespace/友元、两份solution、六包顺序与CI/包检查统一到HeadStore；`git diff --check`通过。文档目录/basename和历史证据身份保留。
- 包构建/隔离PackageReference/发布：未进行。本轮改变公共API符号及NuGet ID（`Atelia.VersionStore`→`Atelia.HeadStore`），消费方需独立适配并更新引用；新包须使用新的不可变版本，从干净已提交来源完成四个隔离包消费者验证。此处源码验收不代替该资格。

下一片：[HeadSetTag 具名成员表替换旧 bytes tag](14-named-head-snapshots-plan.md)。两片分别报告 API 变化、格式变化和验收结果；本设计不授权提交、打包或发布。
