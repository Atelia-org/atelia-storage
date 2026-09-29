# EventJournal 旧库升级设计：保留事实字节，生成 v2 元数据

日期：2026-09-29。状态：**已实施；Linux源码、跨版本、中断及两份真实storage输入验证通过；Windows平台门通过，未切换真实实例**。实际证据见[交付记录](legacy-upgrade-delivery.md)。

来源基线：`bb7c4fb3eb6477783c70ee61bc62b832be195d07`；目标调查基线：`402054c`（实现源码与候选包来源 `18c256e`）。本文给出本次实施合同；实际验收证据在文末追加，当前CLI使用方式见toolkit指南。

用户先授权设计，随后明确授权实现及使用两个Galatea旧repo做final验证。命令对象是单个 EventJournal 根目录，不是 Git 仓库、SessionJournal 外层配置目录或整个应用实例。实现、消费者适配和真实实例切换分别取证。当前 toolkit 入口见[使用指南](../../tools/EventJournal.Toolkit/README.md)，v2 格式见[合同](bounded-online-io-contracts.md)。

## 1. 推荐方案与不变量

首版在 `tools/EventJournal.Toolkit` 提供显式 `upgrade-v1tov2`：**只读旧库，逐字节复制全部事实到新目录，生成 locator/catalog/format，完整验证后发布升级 manifest**。不原地升级，不经业务 Append/CreateBranch API 重建，不新增逻辑中间格式。

这次重构没有改变 EventFrame header/address、payload codec、TagBinding 的 wire；RefOp/RefMove 的有效编码也没变，只收紧了部分解码检查。因此可以保留：

- 每个 RBF 事实文件的相对路径、长度、全部字节与 SHA256；包括空 active 文件。
- `EventAddress` 的 SegmentNumber/Ticket/Hint、Parent、物理 sequence、时间、压缩后的 stored payload。
- `RefId`（allocation 的物理 ticket）、完整 reflog、fork 来源、归档与同名重新创建的不同 lifetime。
- active names、unborn head、全部不可变 tags，以及 orphan events 和合法未 Bind 的 allocations/objects。
- 应用 payload 中可能内嵌的物理地址：工具不解释它们，也不因重排事实使它们失效。

不复制旧 `cache/forward-plans/v1/`，也不修改或删除源 cache。输出存储不携带旧缓存；报告明确列出排除项。

| 方案 | 本次取舍 |
| --- | --- |
| 原事实复制 + v2 元数据 | 推荐。原地址和身份保持，验证可直接比较文件字节；需要一份完整输出空间 |
| 逻辑 JSON/JSONL 导出后用 public API 导入 | 暂不做。地址与 RefId 没有保留入口；重写 Parent/ref/tag 仍不能修复不透明 payload 或外部数据库中的地址，还可能丢失 orphan/归档历史 |
| 带原始 RBF 的中间包 | 可以作为未来传输封装；不解决额外语义问题，首版不增加打包/解包协议 |
| 原地补三个元数据类别 | 不推荐首版。失败状态与业务目录混在一起，源不变与回退证明更复杂 |

## 2. 对照源码发现的兼容差异

以下差异最初来自 `git show bb7c4fb:<path>` 和当前代码；实施阶段已补真实旧 binary fixture及跨版本证据，见[交付记录](legacy-upgrade-delivery.md)。

### 2.1 必须先修正 Archive/Close 时间戳审计

旧 `EventJournal.Refs.cs` 的 `ArchiveRef` 先调用 `AppendRefMove(Close)`；后者自行取时间并 durable flush；随后 Archive 再次取 `UtcNow`。一次正常成功操作就可能产生 `tClose != tArchive`，时钟回拨还可使 `tArchive < tClose`。

当前 writer 共用时间戳；当前 `JournalToolkit.cs / AuditEngine.ValidateRefs` 又把时间相等作为健康条件。因此直接复制健康旧事实，会被当前 audit 误判 `RefMoveInvalid`。

**设计裁决：统一 audit 删除 Archive 与最终 Close 的时间戳相等检查，不增加 legacy 特权。** 继续严格校验 RefId、完整 move chain、最终 Close、Close sequence 等于 Archive.SourceMoveSequenceNumber、ReasonKind、合法绑定及归档归属。时间是各记录的观察值，不承担跨日志身份。也不改成时间单调检查。

当前 writer 仍可共用时间戳，生产 wire 不变；保留 Create/Fork allocation、Init、Bind 原本同源字段的精确匹配。禁止重写 Archive 时间或重算其 CRC 来迎合旧审计，也不在 manifest 中授权目标绕过普通 audit。目标必须被统一的 v2 audit 接受。

这是本方案对上轮验证合同的一处明确修订；实施时同步相关文档与回归测试，不声称 `402054c` 已经具备此兼容能力。

### 2.2 旧库最高空段不是 v2 的缺 locator 歧义

旧 `RbfSegmentStore.DiscoverActiveSegment` 以连续规范库存的最高编号为 active。`RotateActiveSegment` 创建 next 后立即安装 active；`OpenActiveWriter` 的调用者可以归还 lease 而不 append，所以最高段恰为 4B HeaderFence 是合法状态。

当前 v2 rebuild 在缺 locator 且最高段为空时拒绝，是因为它无法区分 locator 发布前后。旧格式根本没有此发布权威，必须使用明确的 legacy 发现规则：连续编号，所有 historical 段非空且健康，最高段健康并允许为空；选择最高段，不删空段、不退回前段。

该规则只属于显式 legacy 入口。普通 v2 audit/rebuild 的 locator 歧义政策保持不变。

### 2.3 旧 reader 容忍不等于可健康升级

| 旧库存状态 | 首版处理 |
| --- | --- |
| 完整健康事实，包括不同 Archive/Close 时间 | 接受，保留原字节 |
| allocation 未 Bind，对象缺失、合法空 segment1 或有效 Init/move chain | 按共享审计规则接受，`UnpublishedRef` warning；不补 Bind/Init |
| CAS 失败留下完整 orphan event | 接受，原样复制 |
| 同名 branch 多个 lifetime、已归档 refs、tag 指向 orphan | 完整验证后接受，不只复制当前可达部分 |
| Close 已完成而 Archive 未完成 | 拒绝，`IncompleteArchive`；不推断解绑意图 |
| 坏尾、CRC/codec 错误、缺段、空 historical、错桶、未知 frame/tag/version | 拒绝，不 recovery/truncate |
| Bind 不邻接 allocation、重复绑定旧身份、reserved 非零或 presence flag 矛盾 | 拒绝；正常旧 encoder/串行 writer 不产生这些形态 |
| 旧 Append durable 异常后继续用 driver 造成重复 sequence、极限回绕 | 拒绝，不重编号 |
| 旧可写 reader 能回退 head 的坏 move/target | 拒绝；旧版成功打开不是健康证明 |

尤其禁止调用旧 `OpenExisting/OpenOrCreate` 做“兼容探测”：它们可能自动 truncate。旧 readonly API 可用于隔离 fixture 的语义对照，不代替全事实扫描。

## 3. 显式 legacy profile 与库存边界

命令名中的 v1/v2 指整个 journal 的存储布局代际：v1 是 `bb7c4fb…` 对应的无 marker/locator/snapshot 旧布局，v2 是本次重构后的布局。旧布局没有声明版本的根 marker，因此 v1 是本工具的明确命名，不是从源文件读出的版本号；它也不是 EventFrame header 的版本号（旧库已使用 header v2）。该命令仅处理此固定方向，不提供通用 legacy 升级或反向转换。

唯一首版 profile：`legacy-bb7c4fb`，固定含义是上述完整 SHA 的 writer 兼容布局，不是从 Git 动态加载任意版本。

CLI 必须显式指定 profile。缺少 `journal.format` 不能自动证明旧版本；文件本身也无法证明由哪个 commit 生成。profile 是操作者的来源声明，加严格内容验证，报告字段应命名 `sourceProfile`，不得冒充已验证的 `sourceRevision`。

来源已有任一 `journal.format`、`active.segment`、`refs/catalog.snapshot`，或这些新 metadata 的临时发布痕迹，一律拒绝 legacy profile。未知/损坏 marker 也不能退回 legacy。完全未留下 v2 metadata 的中断新库与旧库可能字节不可区分：工具不声称能鉴别来源，仍要求显式 profile 和停写来源责任。

允许事实路径：

```text
events/buckets/<6位小写hex>/<8位小写hex>.rbf
refs/ref-op-log.rbf
refs/objects/<16位小写RefId>/segments/<8位小写hex>.rbf
```

事件 store 必须 bucketed；ref store 必须 flat；编号从1连续、RefId非零。要求旧 CreateNew 建立的 roots 和 ref-op-log 存在；完全空 journal 仍须有 event segment1 的4B Fence。缺整个对象仅在有合法未Bind allocation时允许，不凭名字创造对象。

只把旧生成的 `cache/forward-plans/v1/` 子树识别为可丢弃派生数据，包括其 `.efplan` 与临时文件；禁止泛化为“任何 `.tmp` 都忽略”或递归复制所有未知文件。未知库存（含其他 cache namespace）给出路径后拒绝，让操作者对照来源另行处理。不在升级中删除未知文件。

源、输出和父路径拒绝 symlink/reparse；只接受目录与普通文件，不读取 FIFO/device。输出是尚不存在、源目录之外的独占新目录；复制使用独立普通文件，禁止 hardlink 到来源。沿用现有 trusted offline 运行模型，不声称能抵御敌意进程在路径检查后替换挂载/目录。

## 4. 命令与结果合同

命令：

```bash
# 只扫描与判定，不创建输出
dotnet run --project tools/EventJournal.Toolkit -c Release -- \
  upgrade-v1tov2 /old/journal --profile legacy-bb7c4fb --check-only

# 完整转换到新bundle，不能和--check-only同时使用
dotnet run --project tools/EventJournal.Toolkit -c Release -- \
  upgrade-v1tov2 /old/journal --profile legacy-bb7c4fb --output /new/upgrade-bundle
```

保留已有 `audit` / `rebuild-indexes` 参数和 JSON/退出码。不能把旧缺 marker 当作普通 audit 成功，也不能先往源目录写假 marker 再调用 rebuild。

新增独立 `UpgradeReport`，camelCase，最小字段：`schemaVersion=1`、`operation`（CheckOnly/Upgrade）、`sourceProfile`、`targetLayoutVersion=2`、`status`、`sourceScanCompleted`、`counts`、`findings`、`outputRoot`（可空）。沿用 AuditFinding 的 metadata-only 表达，不输出 payload 或 exception.Message，不引入自由文本诊断作为脚本协议。

| status / exit | 含义 |
| --- | --- |
| Eligible / 0 | check-only全覆盖通过；不代表已转换，不能保证稍后来源未变化 |
| Created / 0 | 完整目标通过验证，最终manifest已发布 |
| Rejected / 2 | profile/事实/库存/目标验证存在确定不符合；只返回已完成的覆盖情况 |
| Incomplete / 3 | 发布前I/O、取消、来源/输出漂移，未能完成；无成功manifest。发布后stdout传输失败见§5例外 |
| CLI参数错误 / 64 | 缺profile、参数组合错误、目标已存在/在源内等；不覆盖目标 |

升级前置缺少v2索引是预期状态，不套用现有 rebuild 的 `Healthy/Missing => exit2`。内部共用事实扫描，外部不伪造 `AuditReport.IndexesStatus=Consistent` 来表示 legacy 合格。

建议稳定的升级专属 code：`LegacyProfileMismatch`、`MixedLayout`、`UnknownInventoryEntry`、`SourceChanged`、`OutputChanged`、`TargetValidationFailed`；CRC/Parent/ref语义等继续复用现有 finding code。取消/I/O沿用已有 code。

## 5. 转换状态机与最终发布

前提：调用方停止源 writer，或提供稳定的完整副本。前后hash检查用于发现变化，不是在线一致快照，也不排除先改后还原的并发写。首版不增加跨进程锁服务。

1. **检查路径和profile。** 不创建输出。枚举源目录和文件，拒绝未知/混合库存；捕获完整相对目录清单、每文件分类/长度/SHA256。cache列为ExcludedDerived，仍记录清单和hash用于源稳定性核对。
2. **全事实扫描。** 使用当前RBF checked forward scan与codec，检查全部event、Parent、sequence、payload解码、op-log、tag、ref move/fork/Archive。legacy仅替换metadata/active发现规则；事实健康规则共用。得到所有store的最高编号、op-log精确EOF boundary、最终active names/tags及ref末态。失败不创建输出。
3. **源复查。** 核对完整目录与文件清单/hash，包含新增/删除、空目录变化；check-only至此返回Eligible。升级继续时独占创建bundle及`journal/`。
4. **复制事实。** 从清单逐文件以CreateNew复制，边复制边hash，与预扫描值相等；每个文件Flush(true)/close。保持相对路径、长度和bytes；不复制cache/元数据，不重编码、不压缩重排。建立必要的空目录。来源改变则停止，保留不完整输出供诊断，不自动重试。
5. **生成目标metadata。** 每个实际存在的store写locator，active为已审计的最高段；catalog使用完整op-log EOF boundary，写最终active names和全部tags，`d=0`。无op时用Empty boundary。最后写目标`journal.format`。均CreateNew并Flush；只复用codec，不调用会默认重建空snapshot的`PublishInitial`。
6. **验证完整目标。** 用统一v2 `JournalToolkit.Audit` 得到Completed/Healthy/Consistent；再独立比较全部目标事实路径/长度/hash与源清单。普通`OpenReadOnlyExisting`检查active names的exact RefId和head、所有tags、物理append frontier与预扫描一致；归档refs的Close/完整reflog由audit覆盖，不对closed ref调用GetHead。不向交付目标append测试事件。
7. **最终复查和manifest。** 再检查源完整库存/hash及目标完整文件/目录清单、生成索引hash；核对输出只包含预定产物。关闭所有读句柄，再用临时文件Flush/close及同目录create-only rename最后发布`manifest.json`，之后输出Created。最终复查和发布之间要求外部仍不写入；不宣称这是原子跨目录快照。

输出结构：

```text
upgrade-bundle/
  journal/                 # 独立完整v2目录；切换时使用此目录
    journal.format
    events/...
    refs/...
  manifest.json            # 仅成功完成后出现；位于journal外，避免非法库存
```

根format是runtime布局门，manifest是升级完成证据，二者职责不同。步骤5后即使部分日常API能打开，缺成功manifest的bundle仍不可用于切换。Manifest只表示验证时的候选；上线后正常append会改变hash，不要求其永远匹配运行中的库。

最终manifest rename成功是完成的线性化点。在它之前发生kill/磁盘满/取消，不得出现成功manifest；rename成功后候选已经Created，后续取消、stdout失败或kill不能把存储结果翻回Incomplete并声称无manifest。最后一次取消检查位于rename之前；rename之后不再安排可降级状态的验证步骤。CLI输出失败可能让调用方收不到结果，应读取并核对现有manifest和文件清单，不盲目重跑覆盖。

实现收口：stdout传输失败返回exit3，但不伪造Incomplete报告，不改变已Created的manifest。这是CLI传输失败，不是存储操作回滚；消费者在结果未收到时核对manifest。

不设计resume、覆盖、自动清理或自动原地安装；未完成后的重试使用另一新输出路径。此方案不添加目录fsync承诺，SIGKILL测试不能冒充断电测试。

## 6. 升级 manifest

新建 `UpgradeManifest`，不复用仅含locator/catalog的 `CandidateManifest`：

- `schemaVersion=1`、`kind="EventJournalLegacyUpgrade"`、`completed=true`。
- `sourceProfile="legacy-bb7c4fb"`、`profileBaselineRevision`完整SHA、`targetLayoutVersion=2`。后者是工具实现内固定profile定义，不是对源生成commit的鉴定。
- `sourceRoot`、`sourceDirectories`、`sourceFiles[]`（kind/relativePath/length/sha256，含ExcludedDerived）。
- `outputs[]`：相对于`journal/`的全部事实与生成元数据（kind/relativePath/length/sha256）；排序ordinal，无绝对输出路径依赖。
- `targetDirectories`、`factsCopiedByteExactly=true`、`validation`（sourceFullScan、targetFullAudit、dailyReadCheck、sourceUnchanged各Passed）和warning counts。

集合和hash构成验收证据，不复制每个event的第二套逻辑事实到manifest。不加入用户payload、head内容摘要、凭证或应用配置。文件mtime/owner/ACL不是本格式身份，首版不承诺保留；交付目录采用工具创建权限，切换前由操作者按部署用户验证可读写。

## 7. 实现切口

限制在现有非pack toolkit与其tests；不新增生产包、跨仓业务依赖或public原始写入API。日常runtime继续只接受v2。

当前 `AuditEngine` 将发现、metadata及事实验证放在同一文件。实施时只提取必要切口：

- 库存层产生 `StoreInventory`、分类文件清单及明确active选择。固定两个内部入口：V2Audit、LegacyBb7c4fb；不做可插拔版本注册框架。
- 把event/op-log/move/ref跨文件验证保留为共享一套；metadata规则在扫描外围选择。legacy不读取snapshot、不要求marker，不触发v2 missing-locator重建判定。
- 内部扫描结果提供EOF boundary、catalog和已验证ref末态，供升级writer使用；不要把整个可变AuditEngine暴露成公共API。
- `LegacyUpgrade.cs`负责check-only/copy/metadata/验证/manifest状态机；`UpgradeReports.cs`负责新DTO；`ToolkitCli.cs`增加显式解析分支。
- 现有`Audit`/`RebuildIndexes`测试必须原样维持；除Archive时间戳语义修正外，不放松v2边界。

不同时大规模整理toolkit，不新增“复制一次后跳过全audit”的快路径。首版时间O(B+N+F log F+L log L)、输出磁盘约B+metadata；B为事实及需hash的来源数据字节，F为文件目录数，N为历史记录数，L为最终live条目。现audit会保留事件/ref历史索引，离线内存仍为O(N+F+L)，不是constant-memory迁移。输入过大应报告资源限制，不能改为抽样校验。

## 8. 跨版本验证与实施任务

测试不能只用v2 generator删marker伪装旧库。必须在隔离目录检出/导出完整`bb7c4fb…`源码，用独立进程编译旧公开API fixture generator；旧/新同名assembly不装入同一进程。记录旧SHA、SDK、generator版本及fixture文件hash，生产旧源码不打补丁。

| Task | 工作与通过门 | 执行建议 |
| --- | --- | --- |
| U00 旧版实证 | 旧binary生成包含Archive时间差、tag、fork、同名重建、unborn、orphan、跨段和空最高段的fixture；固定小型字节fixture与来源清单。旧只读能打开只是辅助证据 | sol调查；astra只审出现的新语义差异 |
| U01 共享事实健康规则 | 删除Archive/Close时间强等；不同/回拨时间正例通过，错sequence/reason/Close/归属仍失败；同步文档 | sol实施，主线程/强审阅窄复核 |
| U02 legacy preflight | 显式profile、严格inventory、最高空段、全事实扫描、源hash稳定、check-only无输出；现v2门禁不退化 | sol |
| U03 copy与完成门 | create-only独立copy、metadata、目标全audit/daily读、完整manifest、取消与漂移拒绝 | sol；astra审失败窗口与事实保留 |
| U04 跨版本验收 | 每个原事实文件byte/hash一致；exact地址/refs/heads/tags/reflog/frontier一致；验证副本追加、tag、Archive、checkpoint和rotation成功 | sol，.NET串行 |
| U05 文档与工具交付 | CLI/JSON/退出码、复现命令、来源与平台结果；Release全suite。没有生产包内容变更则不额外pack五包 | sol；主线程收口 |

U00的Archive时间差fixture应来自实际旧writer并保存观察证据；不以手工篡改时间冒充旧binary输出。单元测试使用固定fixture避免每次依赖墙钟跨毫秒。原始大fixture、日志与小型固定回归按体积分别外置/入库，不写真实数据。

必须覆盖：

1. 空journal、只有events、unborn、tag与branch同名、tag→orphan、压缩payload、Hint、归档后同名新RefId、fork源后来被归档、unbound合法残留；保留所有历史而非只保留heads。
2. 旧最高空段→v2有效locator可读；同样形态的v2缺locator仍拒绝rebuild。混合metadata/未知profile不降级。
3. 坏尾（含5/8B）、中间CRC、坏Parent/target、重复sequence、缺段、空historical、未知库存、非邻接Bind、Close无Archive均无成功manifest，源bytes不变。
4. source新增/删除/改字节/改目录、copy后的target改字节/新增文件、同路径输出、目标已存在、symlink/reparse、实际只读源mount、真实I/O失败与取消。
5. 进程kill位于copy中途、locator/catalog写后、format写后、target audit后、manifest rename前后。最后一种区分“完成证据已在而终端未收到”与未完成。
6. 对最终目标的第二个可丢弃副本，用新writer继续append/move/tag/archive并重新audit；交付目标本身保持manifest对应bytes。

Linux与Windows分别验证create-only/rename和中断语义；没有Windows环境就保留PendingPlatform。真实应用升级另需改消费者已移除的recovery options、领域selected-chain audit、停写/备份/切换/回退流程，不因storage候选成功而自动宣告完成。

## 9. 若以后确实需要中间格式

首选“事实文件 + 同一清单/hash”的可搬运目录包，import时复用上述legacy检查与materialize流程。它仍是本地物理坐标保持型，不承诺跨任意未来wire格式兼容。只有需要编辑、跨实现交换或重分段时才设计逻辑格式，并显式处理地址映射、RefId映射、应用payload和外部引用升级；那是另一项任务。

本设计不需要新的runtime wire version：改变的是离线工具入口和一项过强audit约束。所有事实bytes保持，新增目标metadata使用现有v2 codec。

## 10. 本轮真实数据验证范围

实际两个Galatea session目录同时包含`events/refs/control/derived`。`control/derived`含应用状态及SQLite等文件，不作为storage可丢缓存，也不由本工具透传或升级。

final验证采用：真实目录完整清单/hash → 完整独立快照 → 从快照逐字节复制`events/refs`形成standalone输入 → 升级输出 → full audit/daily检查/在第二个可丢弃副本继续写入 → 复查原目录全部hash。三份工件与来源关系分别保留，禁止hardlink。工具仍拒绝未知库存；不为这两个目录放松通用接口。

`/proc`未发现持有source文件的句柄只作为当前环境的佐证，前后hash也不是在线一致快照机制。所有原数据及应用sidecar保持不变；通过结论限定为两个真实数据集的EventJournal事实升级验证，不表示Galatea应用已切换或领域数据库已验证。
