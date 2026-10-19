# VersionStore 独立源码验收与条款映射（V4-E / V4-F）

2026-10-17 引用初始化 fork 的新增资格见本文末节；下述 V4-E/V4-F 保持原 revision、名称与范围，不作为格式3证据。

引用初始化 fork 之后的健壮性维护（CU peer 尾 Fence、tag 主链成员核对及底层 buffer 清理）与独立源码/包验证见[本轮维护状态](../../maintenance/2026-10-16.md)。公开 API 与格式3保持，旧资格不重标为该增量的证据。

日期：2026-10-11。V4-E审查基线：`9e751d4`；V4-F组合增量基线：`3274558`。本记录消费 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[08](08-versionstore-record-metadata.md)–[11](11-versionstore-root-payload-bytes-api.md) 的已实施合同及 [S6](../FrameStore-HeadStore/06-integration-and-delivery.md) 的分层取证要求；不增加协议或重标历史运行结果。

状态：**Accepted（独立 source-only 源码资格）。** S4/S5及08–11的已实施 ref/历史/fork/tag/Audit/可选 RootMap 合同取得本记录限定的资格；V4-E发现的P2已修复并通过两平台回归。V4-F的具名内部窗口和组合增量单列在末节；后续 S6-C/D 本地候选资格另见[包交付记录](../FrameStore-HeadStore/13-local-package-delivery.md)，不重标本次源码证据；应用迁移保留独立出口；未来批量/CU不在本次Accepted范围。

## 证据层次与来源

| 标记 | 本记录中的含义 |
| --- | --- |
| Public | 从公开工厂及操作取得行为；测试可以借内部工具构造损坏输入，不能因此称作独立消费者 |
| Internal | 生产内核/真实 RBF 包装的受控异常、读取观察或资源计数；不等同 OS 故障或实际中断 |
| Residue | 关闭 owner 后制作的目录、截尾或损坏镜像；证明给定输入的裁决，不证明正常 writer 曾在该字节停止 |
| Process | 父进程核对 READY/PID，实际终止其持有 owner 的子进程，再由新进程冷开；只证明具名停点 |
| Source | 当前源码与底座已验证合同的组合论证，未直接执行的窗口必须单列 |

既有运行资格按固定来源、源码差异及原始工件 hash 复核后消费，不能把上轮测试称为本轮新运行。本轮发现并修正 ListRefs 的只读集合逃逸，另补坏 data 的 public 应用反例；最终增量与运行结果单列于下文。

| 来源 | 实际资格与边界 |
| --- | --- |
| [V0–V4-A 分片记录](12-versionstore-implementation-plan.md) | 格式/值、目录与平台、ref/历史/fork/tag/Audit 的 public、受控故障与实际文件镜像；具名测试映射见下文 |
| [V4-B](evidence/2026-10-11-versionstore-v4b.json) | public 图消费者、两层进程锁、六个公开停点强杀与真实冷重开；不是内部写出中途 |
| [V4-C](12-versionstore-linux-resource-acceptance.md) | Windows 回归，WSL2/ext4 新栈及其他完成组、冻结 EventJournal 单独 tmpfs 回归；16/64/256 有限资源观察，不是性能 SLO 或单次完整 ext4 solution 通过 |
| [V4-D](evidence/2026-10-11-versionstore-v4d.json) | Windows Release build 0 error、41 项既有 warning，完整 solution 3438 passed / 9 skipped；WSL2 新增应用用例 7/7。tool-loop/generation 只证明示例的应用策略 |

Windows 实际测试临时文件位于 W: NTFS，仓库位于 E: ReFS。Linux 资格来自本机 WSL2 Ubuntu 24.04.4、ext4 与 SDK 10.0.201；不外推其他架构、网络文件系统、断电或设备乱序。Linux 的历史分组与增量分别保留：V4-C 八组 ext4 3262 passed / 2 Windows-only skipped，冻结 EventJournal 的174项在 tmpfs通过，最终增量 SystemIntegration 21/21；V4-D 再补应用7/7。各轮重叠测试不相加为一个新测试总数。

历史核验重读594个唯一工件，发现 V4-C 的两个派生描述符漂移：`linux-delta/evidence-files.json` 多一项 `baseline-preservation-check.json`，`linux-delta/finalize-evidence.py` 多一条复制该文件的语句。仅移除对应新增项即可精确重建旧 hash/长度；直接 TRX、源码、process raw、summary 及两个保留的 WSL workspace 实际源文件/资产仍匹配独立记录。保留旧摘要，不回写成假定未变；不能宣称“V4-C 全部旧 hash 通过”。详细实测和旧/新 hash 在[本轮机器证据](evidence/2026-10-11-versionstore-v4e.json)的 `baselineEvidence` 中登记。

## V4-E 反例、修正与验证

独立 ref 评审发现一个 P2：`Array.AsReadOnly(refs)` 的非泛型 `ICollection.SyncRoot` 返回实际 `RefId[]`，调用方可经普通 BCL API 改变 `ListRefs` 已交付的结果。这没有修改磁盘，但违反 `A-VS-REFS-DISCOVERY` 的自有只读结果合同。改为与 ListForks 相同的私有薄封装，只提供 `IReadOnlyList<RefId>` 及枚举；不新增通用集合框架或公开类型。

`PublicRefDiscoveryTests.ListRefsCannotExposeBackingArrayThroughCollectionSyncRoot` 能击中旧实现，并核对关闭 owner 后的两种枚举。另两项旧测试原先强制转换为 `IList<RefId>`，现改为验证任何存在的可变集合接口都拒绝写入且不外泄 backing，不将具体 BCL 包装类当公共合同。独立 tag/Audit 评审及主线程均复核了该修正。

新增 `PublicDataClosureTests.ColdHealthyRootRetainsLatestRevisionWhileItsDamagedDataFailsApplicationRead`：公开创建两代 RootMap，关闭两层 owner 后只翻转最新版所指 data 帧的 payload 位；冷开 VS 仍返回最新 revision/bytes，应用沿地址读取时明确得到 `Rbf.CrcMismatch`，旧 data 仍可读，再次 ReadRef 不回退，全部目录和文件 bytes 保持不变。这补 S6 的坏 data 应用边界，证据类型为 **Residue + Public**，不称磁盘故障或进程中断。

Windows 本轮先 Release build（0 error、41项既有 XML warning），定向6/6通过。首次完整回归为3439 passed / 1 failed / 9 skipped，唯一失败是 Storage 测试遗漏的一处旧 `IList` 强制转换；保留失败日志，修正断言后再次 Release build（0 error、0 warning）及完整 solution，最终 **3440 passed / 0 failed / 9 skipped**，其中 VersionStore1177 passed / 6 Linux-only skipped。实际七个冻结参考项目 assets 仍为精确 RBF1 包；独立 source consumer 的 Atelia 闭包仍全为 main project，仅外部 codec K4os 1.3.8。

Linux 在新的 WSL2/ext4 workspace 使用固定 `9e751d4` archive 加五份明确生产/测试增量，共740个受测源文件；全部 hash 逐一核对。完整 Release build 为0 error、41项同源既有warning；随后串行运行三个项目的完整 `--no-build` 回归：FrameStore **424 passed / 1 Windows-only skipped**，RBF **920 passed**，VersionStore **1182 passed / 1 Windows-only skipped**，合计 **2526 passed / 0 failed / 2 skipped**。这不是本轮 Linux 完整 solution 重跑；未变化的其他组保留上述历史资格，冻结旧栈的本轮完整回归在Windows取得。五份输入的精确 hash、实际平台/资产、命令及TRX路径见[机器证据](evidence/2026-10-11-versionstore-v4e.json)。底座、持久格式、公开签名、pack 清单和 public consumer 未改。

## S4：身份、目录、发布与恢复

以下测试路径均相对 [tests/VersionStore.Tests](../../tests/VersionStore.Tests)，源码路径相对 [src/VersionStore](../../src/VersionStore)。表中的测试类/方法是行为证据索引，测试数量不代替条款核对。

| 合同组 | 实现及实际证据 |
| --- | --- |
| `S-VS-DATA-BORROW`、`F-VS-OWN-FORMAT` | `VersionStoreFactory.CheckBorrowedData` 在 VS I/O 前调用 FrameStore `EnsureUsable`，门绑定实际 data；`PublicAdmissionTests`、`FormatGateTests` 与 `FactoryFailureWindowTests.GateReadAndCloseFailurePrecedeFormalDiscoveryAndPendingCancellation`。公开 FrameStore guard 的新增资格见 `tests/FrameStore.Tests/Public/EnsureUsableTests.cs`，不借旧 S2 Accepted 自动覆盖 |
| `S-VS-ROOT-ADMISSION`、`S-VS-OWNER-LOCK`、`R-VS-STORE-CREATE/OPEN` | `VersionStorePlatform`、`VersionStoreFactory` 与 `DirectoryVersionStoreFiles`；`PublicPathAdmissionTests`、`VersionStorePlatformTests`、`FactoryFailureWindowTests`。真实 VS/data 两层锁矩阵与 kill 后释放由 `PublicProcessTests` 两平台验证；获锁 fresh 重检及门 read/flush/close/交付失败由 Internal/Residue 证明 |
| `S-VS-CREATING-WORKSPACE`、`F-VS-REF-PATHS`、`S-VS-REF-ID-DOMAINS`、`A-VS-REFS-DISCOVERY` | `QualifyPending` 不读取内容，writer 完整发现 H 后取消，RO 保留；`PrepareNewRef/VisitRefTargets` 严格资格化实际名称/类型/同文件系统。`DirectoryVersionStoreFilesTests`、`PublicRefDiscoveryTests`、`PublicPayloadAndRecoveryTests.InvalidOrOccupiedSpecifiedTargetsRejectWithoutOverwriteOrBurningAutomaticIds`；不认领 private 或同值旧尝试 |
| `F-VS-REF-ID-4B`、`A-VS-LOCAL-REF-LOOKUP`、`A-VS-REF-REVISION-VALUE` | `RefId/RefRevision` 与 `RefIdCodec`；`RefValueTests`、`RefIdCodecTests`、`PublicLifecycleTests`。LocalId 只在显式库上下文定位，Revision 保留完整 ticket，无公开裸来源导入 |
| `S-VS-ROOTS-OPAQUE`、`A-VS-ROOT-PAYLOAD`、`S-VS-REF-FILE-SINGLE`、`F-VS-REF-FRAMES` | `RefHeaderCodec/RefRecordCodec/RefRecordReader`，独立格式向量 `RefHeaderTests/RecordMetadataTests` 与 `RefRecordReaderTests`；`PublicPayloadAndRecoveryTests.BothPayloadLengthEndpointsRemainOpaqueAcrossCreatePublishAndColdRead` 直接证明随机 bytes 不经 RootMap 解码。首帧身份、最新完整 head 与本次必要内容分别检查 |
| `S-VS-REF-FILE-PUBLISH`、`S-VS-REF-FORK-ORIGIN` | `PublishNewRef` 共用自动/指定创建与 fork，data→源确认→pending Append/flush/close→no-overwrite rename；`VersionStoreCreationTests`、`ForkPublicationFaultTests`、`PublicForkTests`。原生操作及前后异常分别由 Platform/Internal 验证；公开成功后的强杀另由 Process 验证 |
| `S-VS-SINGLE-RECORD`、`S-VS-ROOTS-AFTER-DATA-CONFIRM`、`A-VS-PUBLICATION-EVIDENCE`、`R-VS-PUBLICATION-UNKNOWN` | 每个 mutation 首句初始化 out；`PublishRef/PublishNewRef/CreateTagCore` 直接写回调用方证据，确认后才交付结果。`VersionStoreRuntimeTests` 的 data/Append/flush/交付失败、`VersionStoreCreationTests`、`ForkPublicationFaultTests`、`TagPublicationWindowTests` 验证 NotAttempted/Unknown/Confirmed、助手异常及锁最后。模拟 `OutOfMemoryException` 不称真实内存耗尽 |
| `R-VS-LOCAL-COMPLETE`、`A-VS-VALIDATION-SCOPE` | `OpenRef` 先消费公共 RBF 恢复，再按访问范围检查；`PublicPayloadAndRecoveryTests` 与 `RefRecordReaderTests` 验证 CompletedTail/Truncated、只读不修、坏 head 不回退、坏初始/健康 head 可读。是 Residue/Public，不能冒称真实 Append 半途 kill |

`CleanupErrors` 首错误先保存、汇总失败仍继续资源清理；`CleanupOwned` 先清空资源槽、逐项单次尝试、锁最后且不 Dispose 借入 data。多异常顺序与重复 Dispose 有 Internal 断言；异常汇总自身真实 OOM 退路是 Source 论证，不宣称在真实耗尽环境完成全部实验。

## S5：历史、精确 fork 与声明查询

| 合同组 | 实现及实际证据 |
| --- | --- |
| `A-VS-REF-HISTORY-CHECKED` | `VersionStore.History.cs` 固定完成上界、逐步预算、exact anchor 与 byte clone；`PublicHistoryTests/InheritedHistoryTests/HistoryBudgetTests/HistoryFaultTests` 验证早停、坏旧记录、循环、两个 helper 的完整重读计费、callback Dispose/取消/异常、临时句柄及自有结果。`InheritedAnchorRepeatedCompleteReadsAreChargedAndExactTwelveStepsComplete` 直接核对预算，不将 work 称 I/O 或 RSS 上限 |
| `S-VS-FORK-FROM-REVISION` | `VersionStore.Fork.cs` 直接读取已签发旧点，再进入统一新 ref 发布；`PublicForkTests.PublicOldPointForkOfForkKeepsDistinctNodesAndExactPrefixesAcrossBothOwnersColdReopen`、`ForkSourceValidationTests`、`ForkPreflightFaultTests/ForkPublicationFaultTests` 覆盖 exact bytes、源后来后缀、目标先拒绝与源确认失败 |
| `A-VS-FORKS-CHECKED` | `VersionStore.ForkQuery.cs` 全正式 header、数量额度与迭代图检查；`PublicForkQueryTests/ForkQueryValidationTests/ForkQueryScaleTests` 及 `ForkQuery*FaultTests` 覆盖坏 payload 仍可声明查询、缺源/环、深链、超额前不开文件、完整自有结果和临时清理。声明不签发源 revision |
| `A-VS-ROOTS-QUERY-THEN-READ`、`A-VS-QUERY-COSTS-EXPLICIT` | 核心不调用 RootMap decoder；`PublicPayloadAndRecoveryTests`、`PublicForkTests`、public 图消费者及 V4-C resource 消费者。惰性打开不审全库；长期 ReadRef cache 与临时遍历资源分开，不引入跨调用孩子索引或性能承诺 |

[RBF 指定边界逆扫增量](../Rbf/rbf-reverse-boundary-plan.md) 的 V2-A 源码测试和当前两平台 RBF 回归、public 继承历史组合共同支撑本次源码出口；该 API 的新增包消费仍未取得。

## 08–11、tag 与完整检查

| 合同组 | 实现及实际证据 |
| --- | --- |
| 08 `S-VS-RECORD-TIME`、`F-VS-RECORD-TAILMETA`、`R-VS-RECORD-META-READ` | `RecordMetadataCodec` 与创建/发布/历史接线；`RecordMetadataTests`、`VersionStoreRuntimeTests.Publish_SameBytesCreateDistinctRevision_ClockMayRepeatAndRegress` 及 `MetadataWrittenByPublish_IsExactlyEightLittleEndianBytes`。子创建/rewind 重新取时，历史保留实际源时间，时间不参与链序或 clone 判断；CU 接线不在本资格内 |
| 09 名称、路由、格式与容量 | `TagNameCodec/TagMetadataCodec/TagBucketHeaderCodec/TagRecordCodec`；`TagNameTests/TagHeaderAndMetadataTests/TagRecordCapacityTests` 验证原 UTF-16/Ordinal、独立 meta CRC、完整消费、1024桶与 SizedPtr 最大合法起点。独立格式向量不等同所有最大位置均有公开文件实跑 |
| 09 不可变发布、Related 与初始化保护 | `VersionStore.Tags.cs/DirectoryVersionStoreFiles.Tags.cs`；`PublicTagLifecycleTests/PublicRelatedTagTests/PublicTagBucketAdmissionTests`、`TagPublicationWindowTests/TagPreflightFaultTests`。data-first、合法空桶 rename 不确认首tag、正式 `<I` 前检、同柄查重/Append、Related 不重读或确认源、不签发来源资格 |
| 09 请求名称裁决、`A-VS-TAG-INFO`、`A-VS-TAG-SCAN` | `TagRecordReader/VersionStore.TagScan.cs`；`PublicTagReadScopeTests/TagReadScopeTests/TagErrorPropagationTests`、`PublicTagInfoTests/PublicTagScanTests/TagMetadataReadEvidenceTests`。可信名称即占名，未知名称阻止 missing；meta 成功不证明 root 健康。`TagScanCallbackTests/TagScanCancellationAndPoolTests/TagScanResourceTests` 验证回调互操作、早停与必要清理，不将吞掉实际 I/O 当局部损坏隔离 |
| 10 `A-VS-ROOTS-OWNED`、`F-VS-ROOTMAP-BPV1` | 独立 `RootMapCodec`，`RootMapCodecTests` 覆盖实际枚举捕获、Ordinal重复、Unicode、宽容 BPV/变长地址、完整 codeword 1MiB 界与冻结结果；不依赖输入 Count/comparer，不在发布内核执行 |
| 11 `A-VS-BYTES-INPUT`、`A-VS-BYTES-OWNED` | mutation 在 barrier 前捕获实际 slice；`RefBytesSnapshotTests/RootPayloadTests`、`VersionStoreRuntimeTests.Publish_CapturesOnlyEffectiveInputSliceBeforeBarrier`、tag/fork 公共测试验证 GC 自有 bytes、有效长度与跨 Dispose 保留。`A-VS-BYTES-BATCH` 只约束未来批量/CU，当前无该 API，不纳入 Accepted 接线 |
| S4 `A-VS-AUDIT` | `VersionStore.Audit.cs` 仅 RO，全 ref/tag 及正常 EOF、真实主链 exact ticket、fork clone 与桶内 Ordinal 去重；`PublicRefAuditTests/PublicTagAuditTests/PublicAuditBehaviorTests`、`AuditActiveGuardTests/AuditCancellationTests/AuditResourceTests`。最多两个新增 RBF 内容句柄/pooled 帧，资源台账不含平台临时句柄；不解析 data、Related 来源或 pending，不修复/回退 |

## S6 组合资格与后续最小分片

独立 public 消费者已覆盖多根循环图、三种追加与交错 Builder、非末选点、fork-of-fork、rewind/tag、冷重开、tool-loop 与 generation 串行合并。`PublicProcessTests` 与 `ApplicationProcessTests` 使用真实子进程，不借 reflection 或 IVT 补 public 能力。内部单元夹具不改变消费者的资格。

V4-B/D 的实际 kill 在 public 返回/应用边界，观测到的 data 恢复均为 None；活跃 Builder 中仅留内存缓冲，不能据此声称实测物理残尾恢复。**V4-F** 用独立测试程序集宿主补以下具名内部状态；实际平台运行、失败修正和源码身份见[实施计划的V4-F记录](12-versionstore-implementation-plan.md#v4-f-验证记录2026-10-11)及[机器证据](evidence/2026-10-11-versionstore-v4f.json)。

| 已实施向量 | 冷开判据及证据入口 |
| --- | --- |
| 自动 create/指定旧点 fork 的 private 已 flush/close、rename 前；以及 rename 返回、结果交付前（4个） | `InternalPublicationProcessTests`：实际 flush 观察和独占重开证明 private 已关闭；RO 保留且不出现在 ListRefs，RW取消不续作；正式对象、精确旧点来源/bytes正确，源不变；新请求不烧掉未发布编号 |
| private tag header rename 前；合法空桶 rename 后、首条 Append 前（2个） | 同上：RO树/bytes不变，RW取消private，完整空桶可Audit且`TagNotFound`；随后新CreateTag正常，不从空桶存在推断首tag确认 |
| 正式 ref / 已有 tag 桶的完整 Append 返回、DurableFlush 前（2个） | 同上：真实Append返回ticket后停住，目标flush计数为0；冷开读回完整实际bytes。进程终止没有旧outcome，不能倒推曾Confirmed |
| ref后续记录 / tag首条记录的部分body，以及完整body缺TailKey+Fence（4个） | `InternalTailProcessTests`：真实`RandomAccess.Write`短写后停于`AfterWrite`；parent kill，无事后截尾。先fresh RO拒绝且全树不变，实际VS lazy RW记录`Truncated/CompletedTail`的offset/length/ticket；tag完成后重读必要meta，再两轮fresh RO/Audit，除恢复目标尾外所有bytes不变 |

同片补三个有限组合：经实际VS创建入口的`BeforeNewRefRename`植入合格正式目标，真实发布路径的目标前检拒绝，out为Unknown、旧owner fault，目标bytes不变；冷RO保留pending、RW取消并由真实正式集合决定下个自动号。此证据**未到达原生rename syscall冲突**，不声称实测TOCTOU竞争；原生不覆盖flags仍由源码及既有正常移动/平台证据支撑。`PublicCombinationProcessTests`另覆盖动态ref确认后、独立目录发布前强杀（ListRefs发现未登记对象，应用按bytes识别业务身份），以及六个独立名称向量的冷进程回读（A、Release/release、孤立U+D800、同桶tag-48/tag-65）；固定五个路径、Ordinal、payload/meta/Related、ScanTags及公开RBF格式读回均核对，RO全树不变。

内部宿主使用原有发布hooks、两项Append后可空action和现有RBF写出观察；factory只有显式internal testHooks非null才转交runtime operations，观察恢复不能绕过根/锁/门/发现或提前裸RBF修尾。RBF新增的IVT仅给测试程序集；现有public consumer仍只用public API。同步 out/OOM/必要close异常继续由Internal/Source负责。上述有限代表不外推任意字节切点、多缓冲输出、恢复再中断、物理设备故障或断电资格。

包注册、隔离 PackageReference、发布、DurableGraph 接入及 CU 不属于本队列。S6 最低出口还要求隔离包消费，所以不能因源码 Accepted 宣布 S6 Accepted。


## 2026-10-17：引用初始化 fork / 格式3

本轮按现行04/05/07/08/09/11合同完成引用初始化。两个ForkHead重载返回新HeadId，只发布准确来源header；当前值可属于另一Head，首次本地SingleEntry/CU均可发布。tag改为16B独立selected/owner/ticket映射。统一格式3拒绝1/2，不提供旧格式或旧返回值兼容。

核心源码、历史/Audit、tag及测试分包实施，另有独立只读生产复核；主线程核对代码、预算和CU访问范围，并进行集成运行。删除克隆比较、逐child重开和旧克隆捕获helper；来源读取复用GetOrOpen/准确读取，Audit按来源ticket去重。临时17施工文档已由现行合同吸收并删除，方案可从Git历史追溯。

| 验证 | 实际结果与证据 |
| --- | --- |
| Windows Release build | `dotnet build Atelia.Storage.slnx -c Release`：0 warnings / 0 errors；`artifacts/reference-fork/build-final.log` |
| 完整solution测试 | `dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/reference-fork/tests-final`：3437 passed、0 failed、10 platform skips、3447 total；六份TRX及`test-summary.json` |
| 分项 | Primitives75、Binary214、Data339、Rbf856、FrameStore438+4 skips、HeadStore1515+6 skips |
| 新语义 | `ConditionalUpdateIntegration/ReferenceForkTests`：连续空fork、父子/共享来源首CU、合法未成立回到来源、必要peer错误、header前驱复用及未读替代payload、history回调句柄、来源CU确认失败；均在上述完整suite通过 |
| 历史/Audit/tag | 精确root预算保持、空fork过渡计费、80个共享空fork仅计真实Entry；tag多key共享value、错跨Head来源/缺selected、selected+source+CUpeer确认、格式1/2拒绝，均在完整suite通过 |
| 进程窗口 | 完整suite含新增`ReferenceForkProcessTests`六个首CU窗口及fork首次body partial/complete两种恢复窗口；实际PID/READY/kill与源码消费补充证据单列下段 |
| 范围 | Windows源码与具名进程测试通过；Linux新增资格、真实设备断电、远端Source Link、公开发布及DG迁移未执行；本地包资格单独记录 |

首轮集成发现的旧版本/Entry计数/来源读取次数及history预算断言已按新语义修订；最终完整重跑通过，未放松生产CU/CRC/错误传播规则。旧历史来源、不可变evidence JSON和既有包manifest保持原样。


进程补充取证：以`HEADSTORE_PROCESS_EVIDENCE_DIRECTORY=artifacts/reference-fork/process-evidence`运行`ReferenceForkProcessTests|InternalTailProcessTests`筛选，12/12通过；66份独立进程记录、12个实际强杀PID/READY/exitCode见该目录与`process-summary.json`。新增六个首CU窗口为first-end、all-ends、flush-1/2/3、confirmed，另覆盖fork首个body partial/complete尾恢复；RO验证不改文件，RW恢复后再RO复验。不外推设备掉电。

源码消费补充：`.rename/HeadStorePackageSmoke.Source.csproj` Release build及实际运行通过，日志为`artifacts/reference-fork/source-consumer-build.log`、`source-smoke.log`。包含DG风格连续空fork、共享来源tag、首次保存与两层冷开；源码消费不代替隔离包消费。


### 本轮本地六包与隔离消费

状态：**Accepted（限定Windows源码、具名进程窗口与本地包消费）**。实现及本地包来源：`474dbe4ddfc12898a894c157f72ac5a44a7bcc80`；后续验收文档提交不改变来源。统一候选版本：`0.6.0-referencefork-local.20261017062944`，与旧包使用不同版本，不覆盖既有feed产物。

```powershell
./eng/Pack.ps1 -Version 0.6.0-referencefork-local.20261017062944 -OutputDirectory artifacts/reference-fork/feed/0.6.0-referencefork-local.20261017062944
./eng/Test-Package.ps1 -Version 0.6.0-referencefork-local.20261017062944 -FeedDirectory artifacts/reference-fork/feed/0.6.0-referencefork-local.20261017062944 -WorkDirectory W:/AteliaStorage-referencefork-0.6.0-referencefork-local.20261017062944
```

六包及对应symbols/manifest生成成功，12个包产物hash通过验证。四个实际隔离public PackageReference消费者各仅直接引用一个Atelia包，实际闭包分别为Rbf三包、Binary+K4os两包、FrameStore五个Atelia+K4os、HeadStore六个Atelia+K4os，全部通过；168份本地源码checksum、PDB、静态Source Link、来源及冻结cache bytes检查通过。HeadStore消费者覆盖共享来源tag、连续空fork、首次保存和两层冷重开，实际运行不借ProjectReference。

manifest位于`artifacts/reference-fork/feed/0.6.0-referencefork-local.20261017062944/manifest.0.6.0-referencefork-local.20261017062944.json`；隔离assets和逐项日志保留在`W:/AteliaStorage-referencefork-0.6.0-referencefork-local.20261017062944/logs/`，包括`head-store-smoke.log`与`metadata-check.log`；运行参数另存`artifacts/reference-fork/package-run.json`。消费方可固定该版本或从对应source revision重新验收；日常PackageReference不会自动跟随兄弟仓源码。未推送Git、公开发布包、更新DG pin或取得Linux/断电/远端Source Link下载资格。


## 2026-10-17 紧凑 Handle / 格式4增量

当前合同见04：HeadId改为4B零固定XOR编码、24bit本地编号、GetLocalHeadId上下文解码；低频完整身份和内部raw-local字段布局保留，统一版本4拒绝1/2/3。HeadEntryId、fork、CU、tag消费该Handle语义；跨库值可能相等/误命中，准确记录验证保持。公开持久codec及准确起点历史API不在本轮。

Windows 本轮 Release build 为 **0 warning / 0 error**，完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **3506 passed / 0 failed / 10 platform skips**（HeadStore 1557 passed / 6 skipped）。包含现有系统及内部进程测试；不将其扩大为设备断电或新增 Linux 资格。实际 `Unsafe.SizeOf` 断言验证 HeadId 从原24B降为4B、HeadEntryId从原32B降为16B；尚未提供12B公开持久codec。

Data 的15项映射测试通过，另有独立Python缩域穷举及32bit边界见证，证明与复算入口见[Data规范](../Data/zero-preserving-xor32.md)。新增验证覆盖同key不同完整身份的允许误命中、同key外库header仍被完整身份校验拒绝、24bit上下界及耗尽、默认与越界的HeadNotFound、GetLocalHeadId无I/O/visitor/owner生命周期、CU/tag保持raw-local排序与越界拒绝。三个独立设计视角和后续生产/测试反向复核已收敛。

日志与六份TRX在`artifacts/compact-handles/`：`build-final.log`、`tests-final.log`、`tests-final/`及`test-summary.json`；数学见证在`math/probe.json`。首次编译的13项测试迁移错误、首次回归的77项失败保留在`build-first.log`/`tests-first.log`，根因为测试投影迁移、旧格式常量、超域固定编号及旧错误码/golden，修正后取得上述最终结果，未降低生产检查。

本轮源码提交为 `34cd0e8aec59b449673f3fa07ff3d2daca89cde9`；通用Data数学功能单独提交于 `1149ab3`。从该干净源码提交制作本地版本 `0.7.0-compacthandles-local.20261017092721`，六包及symbols成功生成，12份包hash复核通过。四个实际隔离public PackageReference消费者全部通过，分别仅直接引用Rbf、Binary、FrameStore、HeadStore；实际闭包为3/2/6/7包，无源码ProjectReference，169份本地源码checksum、PDB、静态Source Link及冻结cache检查通过。HeadStore包消费者还直接验证Data变换、4B/16B值尺寸、24bit上限及格式4，既有冷重开/fork/CU/tag/Audit组合继续通过。

```powershell
./eng/Pack.ps1 -Version 0.7.0-compacthandles-local.20261017092721 -OutputDirectory artifacts/compact-handles/feed/0.7.0-compacthandles-local.20261017092721
./eng/Test-Package.ps1 -Version 0.7.0-compacthandles-local.20261017092721 -FeedDirectory artifacts/compact-handles/feed/0.7.0-compacthandles-local.20261017092721 -WorkDirectory W:/AteliaStorage-compacthandles-0.7.0-compacthandles-local.20261017092721
```

manifest在上述feed目录；实际消费者assets和逐项日志保留在上述仓外workspace。参数与独立复核摘要为`artifacts/compact-handles/package-run.json`及`verification.json`。此前所有Accepted保留原revision；本轮未推送Git、公开发布包、更新DG依赖或验证远端Source Link下载。FrameStore接入紧凑Handle、公开Handle持久codec和准确起点历史均留给后续专项。

## 2026-10-17 持久 Handle / 准确 Entry 起点历史增量

本轮从 `8b67739` 开始，批准的临时方案保存于 `be210d5`；实施后删除临时18方案，将合同归入04/05/09/11和源码README。新增HeadId/HeadEntryId的4B/12B Handle encoding v1（无owner、外层version/kind、固定38:26 ticket位布局）及 `ReadEntryHistory`。格式仍为4，Head/tag内部raw-local字段、16B tag成员与FrameStore地址不变；公开API增加，包增量必须使用新版本。

ReadEntry/Fork/两种历史不再依赖值仅由库签发。准确读取仍用完整范围、帧首尾长度/CRC、实际Fence及必要CU资格，在正常RBF3 writer/尾恢复模型内以局部检查确定真实记录，不增加全链定位扫描。任意离线重写镜像不成为日常定位的真实性保证；既有Audit检查保持。跨库误用沿用紧凑Handle弱身份，不宣称所有错库值必拒绝。

### 源码与真实进程证据

Windows Release build最终为 **0 warning / 0 error**；定向测试 **82 passed / 0 failed**；完整solution为 **3579 passed / 0 failed / 10 platform skips**，其中HeadStore **1630 passed / 6 skipped**。新增六个测试类共 **73** 个执行用例均通过；三类独立源码/合同/消费复核未发现阻断。首轮构建的一个新测试ReadOnlySpan误用及两个xUnit分析器警告保留于日志，修正后重建通过。

| 覆盖 | 实际测试与关键结果 |
|---|---|
| codec与值语义 | `Values/HandleCodecTests`与更新的`HeadValueTests`：人工golden/逐位38:26/极值、4B/12B精确长度、失败输出及目标原子性、4B/16B托管尺寸、弱身份碰撞 |
| 导入位置 | `Public/ImportedEntryValidationTests`：真实Fence结束但错误起点/长度、内部与跨帧坐标、正常writer转义完整嵌入小帧、非法envelope、缺失/CRC/Fence/CU；三入口无回退，fork保持NotAttempted且无输出 |
| 历史/预算 | `History/EntryHistoryTests`、`ConditionalUpdateIntegration/EntryHistoryConditionalTests`：跨fork/同值/CU分页、排除新后缀；root准确首项7步、双成员CU首项12步及差一步失败；用户anchor/声明来源/更早CU三种语义 |
| 清理与回调 | `HistoryFaults/EntryHistoryFaultTests`：四种递归、ScanTags准入、允许查询借用、预算中断、末回调取消/Dispose/异常、peer读错与close失败、单次关闭/主异常优先/锁最后/借入data可用 |
| 跨进程保存 | `SystemIntegration/PersistentHandleProcessTests`：writer PID22912在owner关闭后编码4B/12B文件并正常退出；reader PID29300在任何Open前解码，逐字节复核相同codeword。空C写入不改源A，同值提交不同ID，准确旧点读/fork及五次分页（含一次重复根过滤后的空页），最后Audit为4Heads/7Entries/3Origins |

上述PID对应本轮定向证据，不是跨运行固定值。日志、六份完整TRX、汇总与两个子进程JSON保留在 `artifacts/persistent-handles/`：`build-first.log`、`build-second.log`、`tests-targeted.log`/`tests-targeted/`、`tests-final.log`/`tests-final/`、`test-summary.json`及`process-evidence/`。两个独立PID与持久字节往返是实际进程证据；本场景不声称强杀、断电或新增Linux资格。

### 本地包交付

本轮源码为 `c6fb64a97ebc066e10c480e4439f20a2067683a2`；从干净提交制作唯一版本 **`0.8.0-persistenthandles-local.20261017105233`**。六包及symbols生成成功，12份产物SHA256逐项与manifest复核一致。四个新鲜隔离public PackageReference消费者全部通过，各自仅直接引用一个Atelia包，实际闭包为Rbf三包、Binary两包、FrameStore六包、HeadStore七包；没有ProjectReference或旧栈包混入。

包内portable PDB、静态Source Link、来源revision、169份本地源码checksum及冻结cache字节均通过。HeadStore包消费者实际验证4B/12B磁盘往返、无owner解码、选中C的单/批发布、固定旧点ReadEntry/Fork/ReadEntryHistory、普通/CU历史前缀、inclusive分页和冷开Audit；不是仅调用源码fixture。

```powershell
./eng/Pack.ps1 -Version 0.8.0-persistenthandles-local.20261017105233 -OutputDirectory artifacts/persistent-handles/feed/0.8.0-persistenthandles-local.20261017105233
./eng/Test-Package.ps1 -Version 0.8.0-persistenthandles-local.20261017105233 -FeedDirectory artifacts/persistent-handles/feed/0.8.0-persistenthandles-local.20261017105233 -WorkDirectory W:/AteliaStorage-persistenthandles-0.8.0-persistenthandles-local.20261017105233
```

manifest位于上述feed目录；完整消费者日志、assets和保留的存储文件位于上述仓外workspace。主线程独立复核产物与实际assets的结果保留在 `artifacts/persistent-handles/verified-packages.json`、`verified-consumers.json`，参数及最终状态见 `package-run.json`，汇总见 `verification.json`。复验应使用该源码提交及新的仓外WorkDirectory，不能把仅补写本验收记录的后续提交冒充包来源。

旧候选、历史JSON和此前Accepted均保持原来源。本轮没有推送或公开发布；DG pin/接线、FrameStore紧凑Handle、Linux、远端Source Link下载和设备断电均未取得本增量资格。


## 2026-10-19 定向 Head 来源查询源码验收

由 DurableGraph DB-096 的指定成员目录查询需求驱动，在 `f8868b5` 上新增 `ReadHeadOrigin(HeadId) → AteliaResult<HeadOrigin>`。`HeadOrigin.SourceEntryId` 为可空的 header 声明定位值；不授来源发布资格。复用单目标解析、checked header、owned/history 借用和临时句柄清理，不调用全集枚举或读取来源 Entry，不改变格式4/Handle encoding v1/既有查询合同。规范归 [S5](05-versionstore-names-and-indexes.md) 的 `[A-VS-HEAD-ORIGIN-CHECKED]`。

实施、回归编写与独立源码质疑分为有界工作包；主线程独立检查 diff 并执行 Release solution build（0警告/0错误）及完整 solution tests：**3608 passed、10平台跳过、0 failed**。其中 HeadStore 为1659通过/6跳过，FrameStore438通过/4跳过；新增 PublicHeadOriginTests 与 HeadOriginResourceTests 的29条全部通过。跳过为当前 Windows 环境不适用的平台/链接用例，不能作为 Linux 资格。

新增用例证明 root/空fork/父推进子首存/冷重开，来源缺失或非法不扩大 header 查询资格；访问计数和真实读日志为0 List/Visit、不读 source/Entry；坏目标拒绝、无关坏文件隔离、owned/history/temp 借用、主错误与清理错误、RO/RW 工厂失败差异，以及仅所选目标的正常不完整尾恢复。没有本增量的进程中止、掉电或全平台新验收。

主线程运行工件位于下游本机 `.artifacts/db096-targeted-origin/{storage-build.log,storage-tests.log,storage-tests/,storage-test-summary.json}`。本段记录源码资格；普通包须从本增量的干净提交经 `eng/Pack.ps1` 与 `eng/Test-Package.ps1` 另取资格。HeadStorePackageSmoke 已补 root、空fork、首存及 RW/RO 冷来源调用，不能把其源码存在当作实际运行结果。后续固定版本、manifest 与消费日志由对应交付记录说明，不借用上一轮包的资格。
