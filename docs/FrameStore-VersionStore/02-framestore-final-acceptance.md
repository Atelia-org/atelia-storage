# FrameStore 最终源码验收与条款映射（R3）

日期：2026-10-10。审查基线：`3fa45e3369edce4e0f7819fc9b75ff7b54200adf`。依据为 [S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md) 和[收尾计划](02-framestore-completion-plan.md)。本记录接续 [R1 过程取证](02-framestore-process-acceptance.md) 与 [R2 消费/资源取证](02-framestore-consumer-resource-acceptance.md)，不改写前轮的来源、运行结果或证据身份。

状态：**Accepted。S2/S3 独立源码出口、具名失败窗口、两平台受影响回归与成本记录已闭合；本次收尾队列完成。** 主线程已复核源码与实际运行结果，源码/测试输入及 TRX 身份见[机器记录](evidence/2026-10-10-framestore-r3.json)。

本轮终点是 source-only FrameStore 的独立多文件分配、读取、恢复、确认、物理检查和 S3 public 交错构建消费资格。当轮 FrameStore 仅依赖 main Rbf/Data/Primitives；冻结旧栈继续消费精确 RBF1 package。用户随后批准的变长地址 codec 新增 Binary 依赖，其独立增量验证见[实施记录](02-framestore-varint-address-implementation.md)，不将本记录的旧源码 hash/平台结果重标为新增能力的证据。包交付、消费仓切换、VersionStore、并行调用和断电资格分别处理。

## 证据的身份与复用

下文用以下标记区分证据，组合使用不改变各项身份。

| 标记 | 所证明的范围 |
| --- | --- |
| Public | 从真实公开 FrameStore 工厂与操作取得的行为；测试工程可能使用内部夹具构造输入，独立 public probe 则仅直接引用 FrameStore |
| Internal | 真正生产路径的逐调用接缝、内部运行核心或真实 RBF 薄包装的受控故障；不称作真实 OS 故障或外部强杀 |
| Residue | owner 关闭后构造的实际目录/字节镜像；可以证明该输入下的拒绝/恢复，不能声称正常写入实际被打断于该 byte |
| Process | 父子进程握手到公开停点，强杀本探针持有身份的进程，再用新进程冷打开；不证明停在内部 syscall |
| Source | 当前实际代码及底座测试的组合论证，明确列出没有直接注入或目标层实跑的部分 |
| Platform | 记录所列 Windows/ReFS、WSL2 Linux/ext4 的实际运行；不推广到任意卷、网络文件系统、独立 Linux 主机或其他架构 |

R1 三组最终原始 JSON、R2 两组消费者和两组资源最终 JSON 均在本工作区保留；本轮逐一核对其 SHA-256 与已提交摘要一致。三个探针的所有摘要内源码 SHA-256 也与当前文件一致。`59ebd43..3fa45e3` 未改变生产 C#，因此可以复用这些结果；新 R3 工厂接缝须另由本轮回归验证。没有重跑长资源探针或把重复断言相加为新向量。

| 既有实际证据 | 平台与范围 | 可追溯位置 |
| --- | --- | --- |
| R1 | Windows 11 build 22000/x64/E: ReFS、WSL2 Ubuntu/x64/ext4、同 Linux 禁用 BCL 自动文件锁；各 317 检查、80 个已退出子进程、13 次握手后强杀 | [过程记录](02-framestore-process-acceptance.md)、[机器摘要](../../experiments/FrameStoreProcessProbe/evidence/2026-10-10-summary.json)、[public 探针源码](../../experiments/FrameStoreProcessProbe/Child.cs) |
| R2 public 消费 | Windows 与 WSL2 Linux 各 304 断言、6 个隔离场景；独立 public codec 与图解码 | [消费记录](02-framestore-consumer-resource-acceptance.md)、[机器摘要](../../experiments/FrameStoreConsumerProbe/evidence/2026-10-10-summary.json)、[消费者源码](../../experiments/FrameStoreConsumerProbe/Program.cs) |
| R2 资源/规模 | 两平台各 200 断言、226 样本；M=1/4/16，archive=16/128/1024，各点三次；显式 600s/96MiB 预算 | [资源记录](02-framestore-consumer-resource-acceptance.md)、[机器摘要](../../experiments/FrameStoreResourceProbe/evidence/2026-10-10-summary.json)、[测量源码](../../experiments/FrameStoreResourceProbe/Program.cs) |
| 格式/持久化/检查单测 | 已有 Windows 与 WSL2 Linux Release 测试，平台专属用例分别实际运行；历史完整次数见各记录，不作为本轮运行计数 | [首片](02-framestore-core-implementation.md)、[持久化](02-framestore-persistence-implementation.md)、[物理检查](02-framestore-inspection-implementation.md) |

各轮使用 SDK 10.0.201/.NET 10.0.5；WSL2 kernel 为 `6.18.33.2-microsoft-standard-WSL2`。R1/R2 的 Windows solution 结果均为 2163 passed/3 Linux-only skipped/0 failed，FrameStore 为 377 passed/3 skipped；Linux R1/R2 实跑的是独立探针，未重跑完整 solution。历史物理检查切片的 Linux FrameStore 379 passed/1 Windows-only skipped，与当时 380 个展开用例对应。跳过项不算该平台通过。

## 当前实现和测试入口

本轮核对下面的实际源码与测试，表中简称供后文定位；方法名是可直接检索的证据锚点。

| 简称 | 实际文件 | 负责的边界 |
| --- | --- | --- |
| Factory | [FrameStoreFactory](../../src/FrameStore/Internal/Storage/FrameStoreFactory.cs) | 参数/config、锁前/锁后输入检查、门→名称/max→private→active→维护、所有权移交与失败清理 |
| Directory | [DirectoryFrameStoreFiles](../../src/FrameStore/Internal/Storage/DirectoryFrameStoreFiles.cs) | raw gate、完整正式名称发现、private 全字节核验、显式 Off、初始化发布、按需读与清理 |
| Core | [FrameStoreCore](../../src/FrameStore/Internal/Runtime/FrameStoreCore.cs) | 最低编号、配额、完成登记、一次性归还、维护、确认、fault 和 Dispose |
| Scan | [FrameStoreCore.Scan](../../src/FrameStore/Internal/Runtime/FrameStoreCore.Scan.cs) | 同步 visitor、临时 reader 槽、CRC 信任级别、最终 guard 和错误清理 |
| Platform | [FrameStorePlatform](../../src/FrameStore/Internal/Platform/FrameStorePlatform.cs) | ordinary/no-follow、精确名称、同文件系统、native lock/rename |
| Header | [FrameHeaderReader](../../src/FrameStore/Internal/Admission/FrameHeaderReader.cs) | 首物理位置、RBF3、固定形状、完整 CRC、预期身份/编号与边界 |
| Address/Formats | [FrameAddress](../../src/FrameStore/FrameAddress.cs)、[格式目录](../../src/FrameStore/Internal/Format/) | 唯一 12B codec、完整 Packed、24B gate/header、路径、config、阈值 |
| T-Life / T-Admit | [PublicLifecycleTests](../../tests/FrameStore.Tests/Public/PublicLifecycleTests.cs)、[PublicAdmissionTests](../../tests/FrameStore.Tests/Public/PublicAdmissionTests.cs) | public 冷生命周期与构造残留的重复拒绝/恢复 |
| T-Inspect / T-Handoff | [PublicInspectionTests](../../tests/FrameStore.Tests/Public/PublicInspectionTests.cs)、[InspectionOwnerHandoffTests](../../tests/FrameStore.Tests/Public/InspectionOwnerHandoffTests.cs) | 实际正式集合、损坏、visitor/final guard、Dispose 后立即重开 |
| T-Core / T-Scan | [FrameStoreCoreTests](../../tests/FrameStore.Tests/Runtime/FrameStoreCoreTests.cs)、[FrameStoreScanTests](../../tests/FrameStore.Tests/Runtime/FrameStoreScanTests.cs) | 内部行为、真实 RBF 包装故障、调用次序与单次清理 |
| T-Directory / T-DirScan | [DirectoryFrameStoreFilesTests](../../tests/FrameStore.Tests/Storage/DirectoryFrameStoreFilesTests.cs)、[DirectoryScanFilesTests](../../tests/FrameStore.Tests/Storage/DirectoryScanFilesTests.cs) | 完整名称/max、private、backend、checkpoint 取消 |
| T-Format / T-Policy / T-Paths | [FrameAddressTests](../../tests/FrameStore.Tests/Format/FrameAddressTests.cs)、[FormatRecordTests](../../tests/FrameStore.Tests/Format/FormatRecordTests.cs)、[FrameStorePolicyTests](../../tests/FrameStore.Tests/Format/FrameStorePolicyTests.cs)、[FrameStorePathsTests](../../tests/FrameStore.Tests/Format/FrameStorePathsTests.cs) | 纯 bytes/数值/格式/策略向量 |
| T-Platform / T-Header | [FrameStorePlatformTests](../../tests/FrameStore.Tests/Platform/FrameStorePlatformTests.cs)、[FrameHeaderReaderTests](../../tests/FrameStore.Tests/Admission/FrameHeaderReaderTests.cs) | 实际组件与 native 平台准入、首帧检查 |
| T-RecoveryThreshold | [RecoveryThresholdTests](../../tests/FrameStore.Tests/Public/RecoveryThresholdTests.cs) | R3 新增 `CompletedTail/Truncated × T=finalTail/finalTail-1` public 配对，两平台通过 |
| Layout | [FrameAddressLayoutProbe](../../experiments/FrameAddressLayoutProbe/Program.cs) | R3 新增 public 地址尺寸/步长/嵌套/数组分配与编解码调用成本，两平台已记录 |
| T-FactoryWindow / T-Legacy | [FactoryFailureWindowTests](../../tests/FrameStore.Tests/Storage/FactoryFailureWindowTests.cs)、[LegacyArchiveTests](../../tests/FrameStore.Tests/Public/LegacyArchiveTests.cs) | R3 新增10项真实工厂受控窗口及1项公开RBF1 archive拒绝；前者使用[逐调用internal接缝](../../src/FrameStore/Internal/Storage/FrameStoreFactoryOperations.cs)，未加入public开关 |

## S2-Q1..Q7 的逐条裁决

| 项 | 当前实际实现与证据 | 本轮裁决 |
| --- | --- | --- |
| S2-Q1 | Address/Formats 的独立 LE/Packed bytes、全部数值下界、default、完整相等、精确输入/容量输出；T-Format 验证无公开数值投影。Directory 的唯一 24B raw gate/CRC/同句柄只读完整校验，T-Admit 的全部短门/字段错误均在 private 删除和 active 恢复前重复拒绝。T-Life/T-Inspect 与 R1 证明空 store、同 StoreId 冷开、首次 FileId=1、空确认/扫描不建数据；Core 成功登记后无可失败 getter/维护。 | T-FactoryWindow 的门实存完整后 flush/close、reader必要close及双失败已两平台通过；Layout 两平台成本已记录。闭合，不从 wire=12 推导 CLR 尺寸。 |
| S2-Q2 | 统一阈值比较与 DrainStopped；T-Policy 的默认/闭区间/不对齐，T-Admit 的任何 bootstrap 前参数拒绝，T-Life 的等于/超过、升降、archive 不解封及地址稳定。单一路径 codec 与完整正式发现只保 active/max，T-Paths/T-Directory 覆盖 uint 高位、1024 边界、错桶/别名/空桶/耗尽。R2 记录实际目录成本。 | T-RecoveryThreshold 四个直接 public 配对已两平台通过，补足既有恢复与阈值分别测试之间的组合缺口。闭合。 |
| S2-Q3 | Factory 的资格顺序、同锁持有和最后清理；Platform 的 native 普通类型/no-follow/同文件系统/不覆盖 rename；T-Platform、T-Admit 和 R1 覆盖模式矩阵、empty/all archive 仍锁住、kill 后同空 control 重开。Directory 用 public 前缀入口完整核验唯一 max+1 私有项，Residue 覆盖 `0..I` writer 取消/RO 保留及所有非法候选先于恢复拒绝。Linux 禁用 BCL 自动锁的 R1 实跑仍通过。 | T-FactoryWindow 的private必要close/lock-close双失败、Create锁前预检至获锁的确定竞争已两平台通过。闭合；不将锁原语探针替代公开工厂。 |
| S2-Q4 | Core/共享 FrameLease/owned façades 落实 borrow 前检、可纠正 Result 保留、成功自动归还、未知委派异常与共享 fault；T-Core/T-Life 覆盖旧副本、leased 旧前缀、re-dirty、全部 dirty 及维护失败。FrameRead 独立拥有结果，Scan/T-Inspect/T-Scan/T-Handoff 覆盖结构/完整 CRC、回调准入、取消/Dispose 后最终拒绝、临时资源单次清理及主错误优先。 | 已有证据及本轮回归闭合。pool-return 后完整输出异常与真实 OOM 采用下文明确的底座/Source 证据，不重标为 FrameStore 实跑。 |
| S2-Q5 | 所有 RBF factory 调用显式 Off；writer active retained、unowned/archive 按操作开关并在移交前关 reader。T-Life/T-Directory/T-Scan 覆盖自有结果、失败清理和锁最后；R2 多规模点覆盖 historical active>M、降 M 后完整接管/首次再确认、大 sizeHint、大 Append、反复读/扫描异常与 Open 中途失败的成本。 | 无新增资源阻碍；R2 规模基线不设吞吐门槛，池保留量 unknown，重复 CRC 读取失败的资源走势未过程测量。 |
| S2-Q6 | Header/Formats 只识别 checked 第一物理帧；T-Format/T-Header 的独立 24B bytes、形状/身份与完整 CRC；T-Admit `0..I-1` 正式前缀预检保留、用户残尾各文件恢复；T-Inspect 用户 tag=0/墓碑及坏 header 不当空文件。恢复报告不得触及初始化区。 | T-Legacy 直接确认底座可只读的RBF1 archive被FrameStore明确以格式拒绝；两平台回归通过。闭合。 |
| S2-Q7 | config 唯一严格 UTF-8 object/int 属性、缺失/空 object 默认32、只读忽略；Factory 在恢复/输出前一次读取，Core readonly 捕获有效 M。T-Policy/T-Core/T-Life 覆盖先拒绝、初始化失败无额度、可纠正拒绝保额、End/取消释放一次、满额 Append。R2 独立 M=1 回填与历史峰值取得实际资源证据。 | 无新增计数阻碍；实例固定来自一次读取/readonly 字段的 Source 论证，降 M 后重开生效另有 R2 Public。数量准入不升级为总内存/句柄硬限额。 |

## S2-A..D 与验收向量

| 实施出口 | 实际证据组合 | 当前裁决 |
| --- | --- | --- |
| S2-A 项目、地址、格式/身份、锁与输入策略 | solution/两个项目及仅 main 项目引用；T-Format/T-Policy/T-Paths/T-Platform、Factory/Directory、T-Life/T-Admit、R1；Layout 与 R3 工厂补窗 | 源码、行为、具名窗口及成本已闭合 |
| S2-B owned 构建/读/检查、租借与共享 fault | T-Core/T-Scan/T-Life/T-Inspect/T-Handoff、Core/Scan；R2 independent consumer | 直接行为、故障及本轮回归已闭合 |
| S2-C 统一维护、目录状态、编号、多 active 恢复 | T-Directory/T-DirScan/T-Admit/T-Life/T-Core、Directory/Factory；R1；T-RecoveryThreshold | public/Residue/Process及恢复后阈值配对已闭合 |
| S2-D 进程、失败、只读、错误格式、资源规模及指南 | R1/R2 两平台、T-Platform、T-FactoryWindow/T-Legacy、故障注入与下表 Source 论证、[public 指南](../../src/FrameStore/README.md) | 仅本记录所列两个本地平台与 ProcessCrashOnly；具名窗口和最终回归已闭合 |

以下按 S2“单帧资格与实施片”的验收段逐组登记。一个组中如果有仅组合论证的部分，明确列出，不暗示全部都由 public 测试逐一命中。

| 验收向量组 | 实际锚点与证据层次 |
| --- | --- |
| 独立分配器/三种追加/两种无 EventHeader 记录/交错/最小消费者 | T-Life `InterleavedBuildersBuildingBarrierAndColdAddressesRemainIndependent`、T-Core `MixedBuilders_*`；R2 self-reference/A↔B/M=1 的真实 node schema 与冷图解码。Public；没有 EventJournal/VersionStore 依赖。 |
| 12B、端序、高位/全 FF、全部数值下界、精确/失败、default/相等 | T-Format `SpecificationPackedVector_*`、`FullRepresentableTicketRange_*`、`RejectedDecode_*`、`RejectedWrite_*`、`PublicSurface_*`。纯 Public codec 测试；Begin/End 与归档/冷重开的编码稳定另有 T-Life/R2。运行时尺寸和数组成本由 Layout 单列。 |
| 原始来源/错 store/取消坐标复用 | 地址源码只包含 FileId/Packed；R2 `CancelAndReuse` 实际读到 C，并由消费者 identity 检查拒绝旧 A↔B。Public + Source；裸值不提供来源认证。 |
| 阈值以下/等于/超过、无尺寸预轮转、默认/合法范围/非法参数 | T-Policy、T-Life `EqualThreshold*`/`ArchiveMaintenance*`、T-Core `ThresholdEquality*`/`SuccessAfterInnerRbf_*`；参数在 Factory 第一条验证。Public + Internal + Source。 |
| 最大合法帧跨软阈值、T=MaxOffset 最后一帧末端越界 | [RBF 最大起点 Append 测试](../../tests/Rbf.Tests/Internal/RbfFacadeTests.cs) `Append_AtMaxOffset_WithMaximumPayloadAndMeta_Succeeds_ThenFurtherAppendFails`、[Builder 测试](../../tests/Rbf.Tests/Internal/RbfFrameBuilderTests.cs) `BeginAppend_TailOffsetAtMaxOffset_EndAppendEmpty_Succeeds`；Address 全范围测试及 Core `RegisterCompletedOutput` 仅比较完成 tail、成功后不重新限制/挪帧。**底座实跑 + Source 组合证明；没有 FrameStore 最大帧/最大起点 public 实跑。** |
| 重开升降 T、修尾变短/变长后分类、全部 active 合格后维护、archive 永远只读 | T-Life `EqualThreshold*`；T-Admit `WritableOpenRecoversMultipleActiveTails*` 的 actual Residue；Factory 先全部 adopt、后 handoff Drain；T-RecoveryThreshold 四例直接配对已两平台通过。 |
| 固定24B header、首位置/版本/StoreId/FileId/形状/CRC、用户 tag=0/墓碑 | T-Format 独立 header bytes；T-Header `InvalidFirstShapeCannotBeReplacedByLaterValidHeader`/`FullPayloadCrcIsRequired*`；T-Admit/T-Inspect 相应拒绝和用户保留。Public + Residue + 内部 checked-read。 |
| 正式初始化每个短前缀/重复打开不修、用户残尾与报告边界 | T-Admit `EveryShortFormalHeader*`、`WritableOpenRecoversMultipleActiveTails*`；Directory `CheckInitializedLength` 在可写 RBF 前，`OpenQualifiedActive` 检查 report affected offset。Residue + Source。长度足够但坏 header 时可能先恢复用户尾的既定边界保留。 |
| raw gate 独立bytes/唯一CRC、全部0–23前缀/尾随/坏字段、缺门、不能借 data 补身份 | T-Format `Gate_*`；T-Admit `EveryIncompleteGatePrefix*`/`FullLengthGateErrors*`；T-Directory `BadGatePrevents*`；T-FactoryWindow `GateReadOrRequiredCloseFailureRejectsBothModesBeforePrivateCleanupAndActiveRecovery` 在两模式验证必要close及read/close双失败。 |
| 初次Create、布局不齐/短坏门、完整门失败可重开、空集合/首次追加 | T-Life `EmptyCreate*`、T-Inspect `EmptyStore*`、T-Admit `InvalidThresholdAndConfig*`/`ValidGateCannotReplaceMissingRequiredLayout`；R1 Create returned 强杀；R3 门完整后的 flush/close 注入。目录逐步建立后残留按 CheckFreshInput/CheckLayout 拒绝为 Source + Residue，未在每个 mkdir syscall 强杀。 |
| private全部0..I前缀、非默认Key/232–233有界域、唯一max+1、错号/多项/特殊/用户suffix | T-Admit/T-Directory 全部实际 public RBF writer 前缀、T-DirScan 私有检查及 checkpoint；[public RBF 前缀测试](../../tests/Rbf.Tests/RbfInitialFramePrefixTests.cs) 覆盖独立 encoder、合法非默认Key、域界及零分配。Public + Residue；T-FactoryWindow private必要close失败及lock-close双失败证明同句柄全读后不删、不恢复、单次关闭和锁最后。 |
| owner模式矩阵、门前bootstrap、获锁重检、fault/Dispose锁最后、kill释放 | T-Life `ReadOnlyRejectsMutation*`、T-Admit `EmptyControlBootstrap*`、T-Platform；R1 empty/active/all archive 真正跨进程模式矩阵和kill；T-Core/T-Scan 单次清理及fault仍持锁，R3 Create确定竞争。Linux `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` 实跑与 native flock 源码。 |
| Building中的已完成前缀、包含帧后Fence的范围拒绝 | T-Life `LeasedCompletedPrefix*`、T-Core `ConfirmDuringBuilding*`；[RBF completed-prefix测试](../../tests/Rbf.Tests/Internal/RbfCompletedPrefixReadTests.cs) 直接验证跨界在读/租池前拒绝及各缓存模式。本层 retained read 直接委派，无第二句柄/旁路，Public + 底座测试 + Source。 |
| 全owner barrier、leased旧dirty、重开Action=None、新End再次dirty、任一flush失败共享fault | T-Core `AllDirtyFilesIncludingLeased_*`、`ConfirmDuringBuilding_*`、`FlushFailure_AfterEarlierFlush*`；T-Life Building barrier；R1 leased三个active完成前缀确认后强杀。Internal + Public + Process；跨文件失败不是根所在文件单独flush。 |
| End返回仅登记/归还、后续合法入口才维护、拒绝先于维护、flush/close/rename失败不撤销旧成功 | T-Core `SuccessAfterInnerRbf_*`、`QuotaAndInvalidParameters_*`、`ThresholdEquality*`、`ArchiveCloseOrMoveFailure*`；T-Life archive顺序；T-Platform/T-Directory不覆盖目标；Core三个入口统一Drain。M+1停止窗口来自计数/串行/维护入口的Source推导，R2实际M+1 active并非所有停止项窗口的实跑。 |
| 一次性Lease、旧Builder/Writer、borrow/Advance(0)/reservation、可纠正拒绝 | T-Core `MixedBuilders_*`/`CorrectableEndResults_*`/`BorrowGuard_*`、T-Life、R2旧副本不干扰C。逐次新分配且不池化的引用身份证明内层epoch回绕不能恢复outer资格；没有另跑epoch回绕。 |
| 未知委派异常、完整输出后pool归还失败而tail尚未推进、取消资源异常 | T-Core `UnknownEndException_*` 通过raw-inner borrow制造真实委派异常，验证catch后终结当前租借/共享fault；**该例不是合法消费或post-output注入**。[RBF故障测试](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs) `FinalCommitReturnFailure_AfterCompleteOutputPermanentlyFaultsSharedReader`、`BuilderDisposeReturnFailure_*` 实测决定性底座反例；Core End/Cancel catch-all、先EndLease再取消由Source组合证明。 |
| Dispose全部单次尝试、单原异常/多Aggregate、锁最后、主异常优先、汇总OOM退路 | T-Core `Dispose_InvalidatesAllAliases*`/`SingleDisposeFailure*`；T-Scan `VisitorExceptionAndTemporaryCloseFailure*`、R3工厂双失败；Core/Factory/Directory先取空槽、CleanupErrors.Add吞汇总分配失败并在全部清理后传播首异常。**真实OOM及BCL目录枚举器Dispose失败为Source审查，无注入实跑。** |
| FrameRead移交/别名/owner关后存活、包装/读/临时关闭失败未移交结果释放 | T-Life `ReadResultOutlivesDisposedOwner*`、T-Core `OwnedRead_*`/`UnownedRead_*`、T-Directory `ArchiveRandomReadClosesTemporaryFile*`；Directory/Core的失败分支与[RBF pooled read](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs)。包装OOM与重复CRC失败资源走势是Source，R2读/Dispose与扫描失败仅证明实际测量轨迹。 |
| Inventory结构资格/Audit完整CRC、坏中间帧不当EOF、header副本与元信息自有 | T-Inspect `PayloadDamageRemainsStructuralInventory*`/`InteriorStructuralDamage*`/`AColdBadArchiveHeader*`/`AuditHeaderCopies*`；T-Scan `MoveNextTerminationError*`，Scan每次false读TerminationError。Public + Residue + Internal。 |
| visitor准入/重入、随机读、异常/OCE/最终Dispose、不把前缀当整体成功 | T-Inspect相关public用例、T-Handoff回调Dispose后立即重开归档；T-Scan在空backend最终guard、主/close双错误；T-DirScan逐checkpoint取消。所有finally清ScanActive；目录栈不在旧ownerDispose后继续推进。 |
| config全部形式/整数界/严格读取、实例固定、满额Append与额度一次归还 | T-Policy严格JSON/UTF-8、T-Core计数/初始化失败/可纠正拒绝、T-Life M=1与RO忽略坏配置，R2 M=1回填及M=1重开historical active。实例固定通过Factory单次读取/Core readonly捕获Source。 |
| 最低FileId/乱序发现/停止项排除、max来自active+archive/空桶/缺口/耗尽/发布中断 | T-Core `MixedBuilders_*`/`FileIdExhaustion_*`/`BeginInitializationFailure_*`、T-Directory `FormalDiscoverySortsActive*`/`ExhaustedMax*`/冲突；Factory先完整发现，Directory流式archive无历史ID表。枚举中途异常及rename结果未知按实际路径重开由Source与拒绝/不覆盖实测组合，不称内部rename强杀。 |
| 规范路径全部文本、实际组件拼写/普通类型、同FS/no-follow/no-overwrite | T-Paths独立文本与全部别名/桶界；T-Platform真实Windows root大小写/受管精确名/junction，Linux symlink/FIFO/跨mount，native不覆盖移动。privileged bind-mounted叶文件和unsupported native错误未实测，源码strict拒绝无fallback。 |
| RBF1 archive拒绝 | T-Legacy `ValidRbf1ArchiveIsRejectedByFrameStoreReadersWithoutRepair` 先确认raw RBF1被main Rbf只读打开并报告Rbf1，再从public RO FrameStore执行ReadFrame/Inventory/Audit，各自返回明确格式拒绝FrameStore.InvalidHeader，全部bytes原样保留；两平台通过。 |
| 资源归属/historical active、池/scratch/读/扫描失败、O(A+B+H)目录与冷Open成本 | R2两平台多规模点+T-Directory/T-Scan/Factory清理；发现源码只保存active/max且已枚举叶项不重扫父目录。active排序O(A log A)、大量同时Drain的List.RemoveAt搬移、native metadata成本另计；观察到Windows128→1024耗时增长快于数量，不宣称已测线性延迟或性能达标。 |

## S3 的独立出口

S3 只验收预算内交错构建、跨文件互引与全 owner 同步耐久。字典发布、真实应用采用旧 root 或“独立 A 已发布”不在 FrameStore 内建立；对应物理前提用已完成/已确认 A 与无关 B 的轨迹验证，不能凭这些试验宣称实现了上层发布协议。

| S3项/实施片 | 实际证据及裁决 |
| --- | --- |
| S3-Q1 / S3-A | R2 independent public consumer形成单帧self-reference、多个提前地址的A↔B、改变申请/完成顺序仍同图、M=1+完整Append回填；只用public 12B codec保存/冷解码。T-Core/T-Life数量拒绝，R2真实资源基线。已取得独立消费证据。 |
| S3-Q2 / S3-B | T-Core可纠正Result/borrow、自动归还、旧副本/一次性Lease、未知End异常与Dispose；R2取消B/C实际复用b及消费者拒旧图。Source补内层epoch与底座取消/pool异常的资格边界；不推定任何取消地址拥有原尝试来源。 |
| S3-Q3 / S3-C | T-Core leased/多active/重开首次dirty/后续End再dirty、归档fresh flush和失败shared fault；T-Life旧帧混合与Building确认，R1三active旧完成前缀确认后强杀。全owner屏障是物理输出资格，未完成B不因此可读/完成。 |
| S3-Q4 / S3-D | R2 `CancelAndReuse` 在A完成/B未完成时确认不使B可读，取消/reuse后合法物理读取C但拒绝旧业务identity；R1已确认独立前缀在无关未完成Builder强杀后保留；R2记录实际句柄/分配/RSS/磁盘/时限。没有恢复未完成图、构建状态日志或发布库。 |

S3 不新增独立功能阻碍。S2 必需窗口、运行成本和本轮集成回归已闭合，按以上实际范围取得源码阶段 Accepted；不等待 VersionStore 或 S6 包交付，也不以有限场景证明无限规模或多线程安全。

## R3 补证与最终执行结果

R1 留下的三个窗口没有由 Process 探针覆盖；本轮按真实生产工厂路径使用窄的 internal 逐调用接缝补证。它们是受控失败/调度，不能改称真实OS close失败或内部syscall kill。本轮另发现 Q1 地址成本、gate reader必要关闭与恢复后阈值配对缺口。

| 项 | 必须取得的可观察结果 | 当前状态 |
| --- | --- | --- |
| R3-W1 门已实存完整后的flush/close失败 | 注入前确认raw 24B完整合法门已实际写到文件；工厂不签owner、单次关资源/锁、不删门；之后Open/RO认领相同门身份，不能倒推失败Create成功 | T-FactoryWindow的Create理论组3项，两平台通过 |
| R3-W2 private必要close失败/lock-close双失败 | 不删除候选、不恢复active、不签owner；主错误在首位、数据资源关闭一次、锁最后释放；重开从实际bytes重新裁决 | T-FactoryWindow的Private理论组2项，两平台通过 |
| R3-W3 Create预检至获锁的确定竞争 | A完成锁前预检后，B实际Create并释放；A随后获锁，重新检查并拒绝；B门/布局/身份原样保留，A不初始化输出 | T-FactoryWindow的Create握手竞争1项，两平台通过 |
| R3-W4 gate reader必要close失败及读取+close双失败 | Open与RO均不签owner、不删private/不修active；全部资源单次清理、错误保序、锁释放 | T-FactoryWindow的ReadGate理论组4项，两平台通过 |
| R3-W5 FrameAddress目标运行时成本 | Layout两平台分别保存结构尺寸、数组步长、嵌套大小、三个数组规模分配，以及三次200k编解码/typedEquals时间与分配；wire仍独立12B | 两平台结果见下文，源码/结果已核对 |
| R3-W6 recovery后阈值重算 | T-RecoveryThreshold四例在Open返回前核对report original/final、真实active/archive及下一Append选择；原地址冷RO仍可读 | Public + Residue四项，两平台通过 |

| 本轮命令/环境 | 实际结果与证据 |
| --- | --- |
| Windows `dotnet build Atelia.Storage.slnx -c Release` | 最终增量build 0 warnings/0 errors，日志`artifacts/framestore-r3-build.log` |
| Windows匹配Release `dotnet test Atelia.Storage.slnx -c Release --no-build` | 2178 passed/3 Linux-only skipped/0 failed；FrameStore为392 passed/3 skipped/0 failed，共395项；相对基线新增15项（10 factory + 4 recovery/threshold + 1 legacy archive）。日志`artifacts/framestore-r3-tests.log`，TRX目录`artifacts/framestore-r3-tests/`，本轮实际读取TRX计数核对。 |
| `eng/Test-Rbf1ReferenceAssets.ps1`及新栈actual assets | 主线程已运行隔离检查；本轮直接读取FrameStore/FrameStore.Tests的actual assets，Rbf/Data/Primitives均为main project。最终summary补充旧栈脚本结果身份。 |
| WSL2 Linux隔离源码Release build及受影响FrameStore测试 | `/tmp/atelia-framestore-r3-9eb559ac`，133项源码/项目/构建输入hash核验；Release build成功，41项既存Data/Rbf XML warnings/0 errors；FrameStore394 passed/1 Windows-only skipped/0 failed，共395项。日志/TRX在`artifacts/framestore-r3-linux/`；未重跑整solution。 |
| Layout Windows/Linux | [Windows JSON](../../experiments/FrameAddressLayoutProbe/evidence/2026-10-10-windows.json)、[Linux JSON](../../experiments/FrameAddressLayoutProbe/evidence/2026-10-10-linux.json)，包含runtime/OS/x64/assembly SHA-256；布局与成本见下文。 |
| 独立源码复核、Markdown链接及`git diff --check` | 独立反例审查与主线程复核无剩余阻断；主线程实际运行上述回归、解析 TRX 并核对 133 项冻结输入。修改文档的本地链接及 diff 空白检查通过。 |
| 最终来源提交 | 代码、15项补测、Layout探针及本记录由同次提交绑定；以首次加入本文件的 Git 提交定位。实际验证是 `3fa45e3` 加本轮工作树，不重标为提交前已存在的 clean HEAD 构建；精确源码/项目输入和日志 hash 见机器记录。 |

Layout 的两个 x64/.NET 10.0.5 结果相同：`FrameAddress` managed size 与数组步长均为 16B，`byte + FrameAddress + byte` 示例值容器为 32B；1/1024/65536 元素数组分别分配 40/16408/1048600B，包含数组头和对齐。每样本执行 200000 次 TryWrite + TryRead + typed Equals，三个样本线程分配均为 0；Windows 耗时 32.50–37.73ms，Linux 为 32.66–36.59ms。有限预热、tiered JIT、循环检查与宿主负载仍影响时间，这些是组合调用观测，不是单方法速度或固定 CLR ABI 承诺。持久编码继续固定 12B。

首次定向测试有两项失败，原因是测试包装器将内部 `Flush(false)` 也注入异常，早于实存门观察；已将故障限定到 `Flush(true)` 并重新构建、完整回归。最终 Close 异常是在实际句柄关闭后注入；测试证明错误返回的控制流、磁盘保留和所有权清理，不声称制造真实 OS close 失败。本轮没有发现需要改变生产行为的缺陷，生产代码仅增加默认行为等价的窄 internal 接缝。

## 状态建议与限制

最终裁决：**R3-W1..W6、RBF1 格式拒绝及两平台受影响回归已通过，S2/S3 源码出口按本记录范围 Accepted。** 无记录在案的未闭合源码缺陷或必需验收缺口，R4 无需启动。既有 Source 组合证明保持上述限定，不能写成目标层已逐窗口实测，也不要求为它们重复新增同义注入框架。

成立的资格限于遵守串行owner、可信父路径、持锁期间无外部受管项改写的Windows/ReFS与WSL2 Linux/ext4现场。ProcessCrashOnly不含断电；归档前已flush的协议资格不证明断电后的目录项耐久。未取得任意网络FS、privileged bind-mounted叶项、native unsupported错误、其他CPU/OS、真实OOM或每个syscall中断窗口的实证。

完整随机CRC读取、Inventory主链结构与Audit实际集合全CRC各有不同资格；它们不证明原始store/取消尝试来源、业务schema/引用闭包、上层发布或一个无任何已知引用的消失历史文件。未确认完整帧可能重开可读，失败调用没有成功确认，丢失返回值不能由完整bytes反向认领旧调用成功。

资源测量用于记录已选基线：historical active峰值可以超过当前M，sizeHint可远大于声明，池保留未知，RSS不等于库独占内存；有限点未观察到持续临时句柄累积不等于任意负载永不泄漏。没有业务SLO，未声明性能达标，也不增加缓存、manifest或资源账本。

FrameStore继续`StorageSourceOnly=true`/`IsPackable=false`，`eng/Pack.ps1`仍是四包清单。S2/S3源码Accepted不签发NuGet包资格、PackageReference smoke、消费应用迁移或S6整体完成，也不建立VersionStore实现资格。
