# 下游公面完善：第一片实施与源码验收

日期：2026-10-11。状态：**Accepted，限本轮第一片的独立源码复核与 Windows Release 运行资格。** 实施起点为 `89d4acd`；原方案设计基线为 `e989ba6`。本轮不改变持久格式、RBF/Data 计数、恢复算法、包清单或冻结旧栈，旧源码验收与旧六包候选保持各自来源。

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

下一片按[剩余施工计划](14-downstream-api-refinement-plan.md)实施 VS 惰性物理 `RecoveryReports`。本轮没有 backend 打开/恢复接线变更，未新增 WSL2/ext4 恢复资格或重复全部内部窗口/资源规模矩阵；恢复报告片须取得相应两平台增量。

无新增历史确认、有界 ref cache、revision 导入/导出和 writable 禁止恢复仍有明确重访条件，不扩张本轮。没有打包、发布或迁移 DG；旧候选不包含本轮 API，后续包交付须新版本、干净来源与四个隔离 PackageReference smoke。
