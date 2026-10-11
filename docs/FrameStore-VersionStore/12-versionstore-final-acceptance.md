# VersionStore 独立源码验收与条款映射（V4-E / V4-F）

日期：2026-10-11。V4-E审查基线：`9e751d4`；V4-F组合增量基线：`3274558`。本记录消费 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[08](08-versionstore-record-metadata.md)–[11](11-versionstore-root-payload-bytes-api.md) 的已实施合同及 [S6](06-integration-and-delivery.md) 的分层取证要求；不增加协议或重标历史运行结果。

状态：**Accepted（独立 source-only 源码资格）。** S4/S5及08–11的已实施 ref/历史/fork/tag/Audit/可选 RootMap 合同取得本记录限定的资格；V4-E发现的P2已修复并通过两平台回归。V4-F的具名内部窗口和组合增量单列在末节；S6仍为Draft，包交付与应用迁移各自保留出口；未来批量/CU不在本次Accepted范围。

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
