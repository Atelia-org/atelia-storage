# 下游公面完善：分片实施与源码验收

日期：2026-10-11。状态：**Accepted，限本轮第一片的独立源码复核与 Windows Release 运行资格。** 实施起点为 `89d4acd`；原方案设计基线为 `e989ba6`。本轮不改变持久格式、RBF/Data 计数、恢复算法、包清单或冻结旧栈，旧源码验收与旧六包候选保持各自来源。

以下至“续接与资格边界”保存第一片记录；恢复报告第二片的独立源码与两平台增量单列于本文末。两片证据不相互代替，也不重标旧候选包。

## 交付与合同

| 增量 | 实际行为及权威 |
| --- | --- |
| VS `ReadRevision(RefRevision)` | 精确重读同库库签发值，返回自有 root bytes、输入 revision 与原时间；成功检查尾 Fence/CU，合法未成立 CU 失败且不回退。资格见 [S4](../VersionStore/04-versionstore-publication.md) `[A-VS-READ-REVISION]`，结果见 [11](../VersionStore/11-versionstore-root-payload-bytes-api.md) |
| exact fork 共用内核 | `ReadExactPublication` 保留现有必要错误/null/snapshot 三条路径，仅 null 由公开读取与 fork 各自解释。fork 同步消费已捕获的私有 snapshot.Payload，删除第二次 ToArray；普通 Create/Publish 的用户输入捕获、data→源确认→子输出与 Outcome 保持。见 [S5](../VersionStore/05-versionstore-names-and-indexes.md) |
| FS `Length` / `Payload` / `TailMeta` | 用户 Reserved + Advanced 含 meta，从0开始，所有副本共享首次 guarded Builder getter 捕获的原点；切片零复制，空切片也继承 Dispose guard。见 [S2](../FrameStore/02-framestore-core.md) `[A-FS-OWNED-BUILDER]`、`[A-FS-READ-OWNED]` |
| VS 公共 `EnsureUsable()` | VS disposed→VS fault→实际 borrowed data；纯 guard 无 I/O/确认，不额外 fault。内部继续 owner-only，Dispose 独立于 data。见 S4 `[S-VS-DATA-BORROW]` |

`Length` 是公开语义变更：先前从绝对值中扣除内部 HeadLen 的消费者需移除扣减，前后差值用法继续成立；物理尺寸使用 RBF MeasureWriteSize。仓内实验源码未发现需要迁移的绝对 HeadLen 假设，历史证据初值保持原样。用法已同步两个 src README 和 [public consumer](../../tools/VersionStore.Smoke/README.md)。

## 独立复核与反例

FS/VS 两个不重叠写包交付后，第三位 agent 从规范、实际 diff 和测试独立复核，结论 Go、无 mustfix；主线程再核对共享契约、核心源码及运行结果，不以实施者报告代替验收。

- [普通精确读取](../../tests/VersionStore.Tests/Public/PublicRevisionReadTests.cs)：两层冷重开、继承源实际 revision、无本地后缀定位扫描/确认、default/foreign 在 I/O 前拒绝、无关坏 payload 不扩大访问、必要目标 CRC/Fence 与完成范围失败；owned bytes 在后续读取和 owner fault/Dispose 后不变。
- [CU 精确读取](../../tests/VersionStore.Tests/ConditionalUpdateIntegration/ConditionalRevisionReadTests.cs)：成立 old CU 只返 root/原时间，受控缺 peer 的已签发位置不回退；public InvalidRefRecord 与 fork InvalidForkOrigin 各自保留，必要 peer CRC 原错误传播。未成立分支仍先失败，不让坏目标 Fence 替换该错误。
- [公共 guard](../../tests/VersionStore.Tests/Runtime/VersionStoreUsabilityTests.cs)：RO/RW、dirty data/未 Advance Builder、实际 data Confirm 失败驱动 fault、data 原 guard 错误传播、VS 优先级；内部纯查询范围、实际发布 barrier 停用/NotAttempted 与 Dispose 单次/锁最后保持。测试中的失效 data 探针不撤销调用方正常使用时维持 data 寿命的前提。
- [FS façade](../../tests/FrameStore.Tests/Public/PayloadFacadeTests.cs)：known/unknown、首个 Length 在 Advance 之后、副本与重复 getter、Reservation/Commit/Advance、确定拒绝/可纠正 End、空/纯 meta/混合帧、Overlaps 证明零复制、buffer Dispose 后三个 getter 拒绝。[运行时测试](../../tests/FrameStore.Tests/Runtime/FrameStoreCoreTests.cs)补已移交 bytes 跨 owner fault 与旧 writer 生命周期。
- [exact 清理](../../tests/VersionStore.Tests/ConditionalUpdateCleanup/ConditionalUpdateFrameCleanupTests.cs)：已有真实 pooled-return 故障测试增加3个 exact 分支；验证主/清理异常顺序、buffer 失效、peer→owned→锁关闭、重复 Dispose 不重关及冷重开组仍成立。
- history/tag visitor 可串行精确读，复用匹配句柄；调用方独立读取不消耗外层 HistoryBudget，外层 CU 工作额度9成功/8拒绝。既有 history 内核仍走计费 ReadPublication，7个既有 VS partial 仅改 owner guard 名称；保留嵌套伪帧不能由裸位置签发的 Audit 反例。

## 实际验证

Windows / SDK10.0.201，按以下顺序串行执行命令：

```powershell
dotnet build Atelia.Storage.slnx -c Release --nologo
./eng/Test-Rbf1ReferenceAssets.ps1
dotnet test Atelia.Storage.slnx -c Release --no-build --nologo --logger trx --results-directory artifacts/api-refinement-20261011132703/solution
```

| 检查 | 本轮结果 |
| --- | --- |
| Release solution build | 0 error，41项位于未修改 Data/Rbf 源码的既有 XML 注释 warning |
| 9个测试程序集的完整 solution 回归 | **3642 passed，0 failed，9 skipped**；从9份 TRX 的逐项 outcome 汇总，跳过为 Windows 上的 Linux 专属项 |
| FrameStore.Tests | 438 passed，0 failed，3 skipped |
| VersionStore.Tests | 1363 passed，0 failed，6 skipped |
| 新增向量 | 41个新命名 case与3个既有清理测试的 exact 分支全部通过；包含在上述完整回归，未重复计作第二组覆盖 |
| public source consumer | `PublicGraphWritesAndReadsInSeparateColdProcesses` 通过；graph 使用新 ReadRevision/guard/Length/Payload，write/read 在不同实际进程 |
| 实际依赖资产 | 7个冻结参考项目保持精确 RBF1 `[0.2.0-rbf1-preview.1]` / `3e9554e`；FS/VS 的 Atelia 依赖全为 main project，K4os 精确1.3.8，无旧库运行时依赖 |

原始 TRX 在本地 ignored `artifacts/api-refinement-20261011132703/solution/`，不是随 clone 分发的包证据。主线程同步规范/指南/导航，13份本轮 Markdown 的282处本地链接/锚点检查及 git diff --check 均通过；未重写历史 evidence JSON。

## 续接与资格边界

第一片完成时，下一片为 VS 惰性物理 `RecoveryReports`。第一片没有 backend 打开/恢复接线变更，未新增 WSL2/ext4 恢复资格或重复全部内部窗口/资源规模矩阵；恢复报告的后续实际资格见下方第二片记录，当前续接统一见[计划](14-downstream-api-refinement-plan.md)。

无新增历史确认、有界 ref cache、revision 导入/导出和 writable 禁止恢复仍有明确重访条件，不扩张本轮。没有打包、发布或迁移 DG；旧候选不包含本轮 API，后续包交付须新版本、干净来源与四个隔离 PackageReference smoke。

## 第二片：VS 惰性恢复报告（2026-10-11）

状态：**Accepted，限独立源码复核、Windows Release 完整回归与 WSL2/ext4 恢复增量。** 实施起点为干净 `ac97ad3`。本片完成计划中最后一个获准施工项，合同归入 S4 `[A-VS-RECOVERY-REPORTS]`，09 引用同一 tag 通道；后续仅保留计划中的产品需求重访条件。

新增 public `VersionStoreRecoveryFileKind`、`VersionStoreRecoveryReport` 与 `VersionStore.RecoveryReports`。Create/Open 把同一个小 [ledger](../../src/VersionStore/Internal/Runtime/VersionStoreRecoveryLog.cs)交给 runtime backend 与 owner；[ref](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.cs)/[tag](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.Tags.cs) 两处正式打开均先准备容量，再调用 RBF 工厂，成功返回立即无分配记录非 None 动作，早于 tag 初始化保护和 VS 校验。没有修改 files 接口、恢复算法、持久格式或 RBF/FS 公面。

私有薄 view 仅持有报告值集合，不实现可写集合/SyncRoot、不引用 owner 资源。Count 可增长，旧项及顺序不变；getter 和保存的 view 在 fault/Dispose 后仍可读。报告不证明发布或耐久；工厂失败可能已改 bytes 而没有成功报告。第一片 API、data 探测/确认范围、CU 共同资格、历史预算、Outcome 与清理实现保持。

生产实现、公开测试和故障测试由三个无冲突写包完成，第四位 agent 从当前规范及稳定源码/全部新测试独立复核，结论 Go、无 mustfix；主线程复核接线、真实失败轨迹与实际测试证据。

| 新向量 | 实际覆盖 |
| --- | --- |
| [公开恢复报告](../../tests/VersionStore.Tests/RecoveryReportsPublic/)：37 cases | 初始/后续 CompletedTail、Truncated 的原/最终长度、offset/ticket；current/exact、继承 history、ListForks、CU peer、named tag/ScanTags 及桶0/1023；RO 拒绝后整目录 bytes/SHA256 不变；cache/扫描借用及后来 None 不重记或抹旧 |
| live view 与寿命 | ref→tag→ref 的追加顺序和固定区间；普通 IList/ICollection cast 没有修改通道；保存 view 跨 VS/data Dispose 后可读，实际独占文件/锁与 owner 冷重开成功；FS/VS 台账分离 |
| [恢复后失败](../../tests/VersionStore.Tests/RecoveryReportsFailures/)：15 cases | header/缺 Snapshot/CU 必要资格/tag header 拒绝仍保留；tag 初始化保护先修尾后拒绝，visitor/取消/Dispose/关闭失败后仍保留；primary→cleanup 顺序、单次关闭、锁最后；NotAttempted 可有修尾且不增加 data 探测或确认 |
| 记录时机与工厂边界 | 工厂入口观察容量已准备，首次 header 扫描观察报告已追加且容量未变化；真实 RBF 修尾后代理在返回前 throw，没有成功台账但 bytes 已变，沿原 fault/清理路径结束，不伪造 None |

Windows 先完整 Release build，再定向回归、RBF1 assets 检查和完整 solution 回归；随后在 Linux 新目录独立 build/test。命令串行执行，没有同时运行两个构建或测试命令。

```powershell
dotnet build Atelia.Storage.slnx -c Release --nologo
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --nologo --filter 'FullyQualifiedName~RecoveryReports|FullyQualifiedName~PublicPayloadAndRecoveryTests|FullyQualifiedName~PublicTagBucketAdmissionTests' --logger trx --results-directory artifacts/recovery-reports-20261011134255/windows-increment
./eng/Test-Rbf1ReferenceAssets.ps1
dotnet test Atelia.Storage.slnx -c Release --no-build --nologo --logger trx --results-directory artifacts/recovery-reports-20261011134255/windows-solution
```

Linux 消费冻结源码清单的799个文件，清单 SHA256 为 `cf150d54d2aa8cf9f9f5d909242d8e0de4983539ac56b18e69562ff88f75f37b`；其中548个构建输入另与最终工作树逐个 hash 比对一致。环境为 WSL2 Ubuntu24.04.4 / kernel6.18.33.2-microsoft-standard-WSL2 / SDK10.0.201，用户root。源码、bin/obj 与 TMPDIR 位于 `/var/tmp/atelia-storage-recovery-reports.K4A6hcAK` 的 `/dev/sde` ext4；不以 `/mnt/e` 充当测试文件系统。NuGet 复用 `/root/.nuget/packages`，不是隔离 PackageReference 消费。首次缓存复制准备被协调停止、没有进入构建/测试，不计作资格。

Linux 在受测 repo 执行以下命令，TMPDIR 为受测目录下的 `tmp`：

```bash
dotnet build tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --nologo
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --nologo --filter 'FullyQualifiedName~RecoveryReports|FullyQualifiedName~PublicPayloadAndRecoveryTests|FullyQualifiedName~PublicTagBucketAdmissionTests' --logger trx --results-directory ../results
```

| 检查 | 第二片实际结果 |
| --- | --- |
| Windows solution / Linux VS closure Release build | 两次均0 error / 41项未修改 Data/Rbf 中的既有 XML warning |
| Windows 定向增量 | **142 passed / 0 failed / 0 skipped**；52项新增与90项既有恢复/桶准入对照 |
| Windows 完整 solution | **3694 passed / 0 failed / 9 skipped**，9份 TRX 逐项 outcome；FS438/3 skipped，VS1415/6 skipped，跳过为 Linux 专属项 |
| WSL2/ext4 定向增量 | **142 passed / 0 failed / 0 skipped**；同一52项新增全部实际运行通过，没有宣称完整 Linux solution 或新增进程/断电矩阵 |
| 依赖隔离 | 7个冻结参考项目保持精确 RBF1 `[0.2.0-rbf1-preview.1]` / `3e9554e`；两平台实际 FS/VS assets 的 Atelia 依赖全为 main project、K4os 精确1.3.8，无旧库运行时依赖 |

原始日志、TRX、环境、受测源码清单及派生汇总保留在本地 ignored `artifacts/recovery-reports-20261011134255/`，不作为随 clone 分发的包证据；相关新测试及合同随源码提交。离线损坏镜像和窄代理观察不冒充进程崩溃、设备或断电证据。未注入 OOM、独立资源移交分配失败或 RBF 内部设备故障；容量/时序及既有移交清理路径分别有运行观察和独立源码复核。

本计划施工队列已收束。无新增历史确认、缓存预算等按明确需求再重访；本轮没有打包、发布、迁移 DG 或改写历史 evidence JSON，旧六包候选仍不含 CU 与这两片 API。新包交付需另用新版本、干净提交及四个隔离 PackageReference smoke。
