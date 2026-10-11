# VersionStore ConditionalUpdate 独立源码验收

日期：2026-10-11。状态：**Accepted（CU 增量独立源码资格；Windows / WSL2 Linux）**。本记录消费 [S0](../FrameStore-VersionStore/00-architecture-decisions.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[07 CU](07-versionstore-cross-ref-conditional-update.md)、[08 元信息](08-versionstore-record-metadata.md)、[11 bytes 公面](11-versionstore-root-payload-bytes-api.md) 与 [S6](../FrameStore-VersionStore/06-integration-and-delivery.md)，不增加协议。主线程完成源码合同复核、反例裁决、Windows 回归与 WSL2/Linux 分组回归；[机器证据](evidence/2026-10-11-versionstore-cu07.json)记录受测源码、逐组TRX、具名进程窗口及原始工件 hash。

既有 [V4-E/V4-F 最终源码验收](12-versionstore-final-acceptance.md)仍为其原范围的 Accepted，不覆盖 CU。既有 [S6-C/D 六包候选](../FrameStore-VersionStore/13-local-package-delivery.md)不包含本轮 PublishRefs/CU，版本、manifest、hash 与来源不可变；本轮没有重新打包、发布或迁移消费者。历史 ConditionalUpdate 研究探针仍为各自格式/算法的研究证据，不改标为当前生产实现全矩阵通过。

## 本轮范围与源码身份

前置只读审查判定现有 public RBF/FrameStore 能力足够；本轮不新增底层 API。修改为 VersionStore 的 CU canonical codec、PublishRefs、exact-first 成员定位与共同判据，以及当前值/历史/fork/Audit/owned 清理的必要接线。普通 Snapshot/header 编码与 VersionStoreFormatVersion=1 保持，新增 CU kind=2；旧 reader 必须按未知 kind 拒绝。

| 身份项 | 本轮实际值 |
| --- | --- |
| 实施前 HEAD | `45b12f5b53492f6a03c3185b74b5078e5caa597b`；仅为起始来源，不是新增实现/构建身份 |
| 最终受测源码 | 779 个文件已冻结；原始清单 `artifacts/versionstore-cu07/linux/source-manifest.json`，SHA256=`3b344591faab733c9a82b94bac1cce32387c7314c7a64b530db94c3dfe3c5fe6`。生产/测试与冻结清单一致，此后仅文档续写；起始 HEAD 不替代该工作树增量身份 |
| Windows 受测构建/日志 | `artifacts/versionstore-cu07/solution-build-observation.log`：最终 Release build 0 warning / 0 error；完整 solution 与新增3例后的 VersionStore 回归分开记录如下 |
| Linux 受测副本/文件系统/构建 | WSL2 Ubuntu 24.04.4 / kernel 6.18.33.2 / SDK 10.0.201；ext4 `/var/tmp/atelia-storage-cu07-45b12f5.yr64wr3n/repo`，消费上述冻结清单。独立完整 Release build 0 error / 41项既有 XML warning；9个测试项目串行通过，新栈夹具均在 ext4，冻结 EventJournal.Tests 单独使用 tmpfs TMPDIR。构建/测试/进程与源码清单均保留 |
| 独立源码复核 | 最终 Go，无剩余 must-fix。ArrayPool.Return 失败的清理顺序已修正为 pooled lease → local Audit 句柄 → owner 其余资源 → 根锁最后；运行与证据边界见下文，Go 不独立替代平台验收 |

## 条款与验证映射

下表列出本轮已独立复核的增量，裁决依据源码、合同反例与实际运行证据。测试入口相对 [tests/VersionStore.Tests](../../tests/VersionStore.Tests)，生产入口相对 [src/VersionStore](../../src/VersionStore)。实际覆盖和结果已由主线程按最终源码复核。

| 合同组 | 生产/测试入口与裁决内容 |
| --- | --- |
| 11 `A-VS-BYTES-BATCH` | `VersionStore.Batch.cs`；`ConditionalUpdatePublic`。实际枚举私有捕获、不信 Count/comparer、错库/default/重复/empty 拒绝、single 普通 Snapshot、全部目标既有；所有 slice 捕获/枚举器清理先于 barrier，自有只读完整 revision 字典与异常证据 |
| 07 canonical 格式与容量、S4 `F-VS-REF-FRAMES` | `ConditionalUpdateCodec`、`RefRecordCodec`、`RefHeaderCodec`；`ConditionalUpdateCodec`。tag2/count4、递增非零 LocalRefId4+Packed8、self/全表、root1..1MiB/meta8、268435416B stored payload/268435452B frame、actual-count ticket Measure 区间与 SourceSnapshotTicket 完整宿主上界，无任意成员数硬限 |
| S4 data-first / Outcome / 停用 | `VersionStore.Batch.cs` 与 runtime operations；`ConditionalUpdateFaults`。全体确定准备/预测/编码和整批一次取时在 barrier 前，逐文件 Begin/End 后逐文件 flush；首输出前 NotAttempted、尝试后 Unknown、全部确认先写 Confirmed，后续局部拒绝不降档，清理锁最后且不 Dispose data |
| 07 exact-first 与共同判据 | `VersionStore.ConditionalRead.cs` owner partial；`RefRecordReader` 保留局部格式读取职责，不拥有 peer/组判据。`ConditionalUpdateFaults`、`ConditionalUpdatePublic` 验证指定历史位置/self/完整表，EOF 缺席、同长度合法异表、异长真实前驱后继，不递归替代组；只有有限长度歧义错误进入消歧，CRC/I/O/fault/非法结构传播，异长 replacement payload 不审计 |
| S5 历史与 fork | `VersionStore.History.cs`、`VersionStore.Fork.cs`；`ConditionalUpdateIntegration`。未提交 CU 不交付，peer 访问/扫描/完整读/结构后继动作前扣 work，peer 不加入继承 visited；exact CU source 未提交拒绝，普通子初始只 clone root bytes，data 后仅确认 exact source 文件，ProcessCrashOnly |
| S4 `A-VS-AUDIT`、S5 `A-VS-FORKS-CHECKED`、09 Related | `VersionStore.Audit.cs` 与原声明查询/tag 内核；`ConditionalUpdateIntegration`。Audit 完整检查物理 CU，SnapshotCount 为普通 Snapshot+成立 CU，未提交 CU 不计且不能充 fork source；ListForks 仍只 header 声明图，Related 仍只注记 |
| owned 资源与读取范围 | `VersionStore` 清理槽与共同 reader；`ConditionalUpdateCleanup`、`ConditionalUpdateFaults`。短期 peer 槽进入 owner 清理，已有匹配句柄借用、其他 peer 逐个关闭，O(1) 额外 handles，pooled 帧/句柄单次归还、关闭失败停用，必要错误不回退 |
| 07 观察稳定性与恢复 | `ConditionalUpdateFaults`、`ConditionalUpdatePublic`。fresh 组不复活旧未提交组，成立旧组经普通覆盖仍成立；本地物理 head 覆盖不强制验证旧 peer，lazy CompletedTail 不构成已成功缺席后复活；RW 恢复/RO 拒绝残尾分别记录 |

## 实际验证结果

| 验证层 | 命令、实际结果与证据 |
| --- | --- |
| 文档静态核对 | 最终15份文档静态检查通过：现行 Clause-ID 保留且各篇定义唯一、35个新增本地链接有效、UTF-8/LF、CU容量算术；git diff --check 通过。`artifacts/versionstore-cu07/final-static-validation.json`另记录两平台60条进程事实（12 READY、48 RESULT）与514份不变构建输入。不代表未执行的产品矩阵 |
| Windows Release build | `dotnet build Atelia.Storage.slnx -c Release --no-restore`；最终 `artifacts/versionstore-cu07/solution-build-observation.log` 为0 warning / 0 error。首次编译错误及3个新增测试告警均已修复，过程见下节 |
| Windows 定向 CU 回归 | 初版 Release `--no-build` 的 CU filter 为141 passed / 0 skipped / 0 failed，TRX=`artifacts/versionstore-cu07/windows-initial/conditional-update-initial.trx`。新增3例后完整 VS TRX 内全部 CU 用例为144/144通过；两轮分别保留，不相加 |
| Windows 完整 solution | `dotnet test Atelia.Storage.slnx -c Release --no-build`：`artifacts/versionstore-cu07/solution-tests-final.log` 与 `windows-final/` 的9组TRX共3595 passed / 9 skipped / 0 failed，其中 VS1332 passed / 6 skipped。该单次完整 solution 在新增3例之前；随后匹配新构建的完整 VS 回归为1335 passed / 6 skipped / 0 failed，TRX=`artifacts/versionstore-cu07/windows-observation/versionstore-observation.trx`。保留其他8组与最新VS的覆盖并集为9组3598 passed / 9 skipped / 0 failed，不标为单次新版完整 solution。汇总见 `artifacts/versionstore-cu07/windows-summary.json` |
| 实际依赖 assets | `artifacts/versionstore-cu07/rbf1-assets.log`：7个冻结参考项目仍精确消费 RBF1 `[0.2.0-rbf1-preview.1]` 与固定来源；main VS tests assets 包含6个 main 项目，外部 runtime codec 为 K4os.Compression.LZ4 1.3.8，无旧栈或业务依赖混入 |
| WSL2/Linux | 完整 Release build 后串行执行9个测试项目：**3605 passed / 2 Windows-only skipped / 0 failed**；VersionStore1340 passed / 1 skipped，其中 CU144/144；FrameStore424 passed / 1 skipped；Rbf920、Primitives75、Data288、Binary214、SegmentStore48、Toolkit122、EventJournal174均通过。前8组夹具位于ext4，冻结 EventJournal.Tests 的完整用例另用 `/dev/shm` tmpfs TMPDIR；不是单次完整ext4 solution。命令、各组TRX和环境见 `artifacts/versionstore-cu07/linux/` 及机器证据 |
| 真实 CU 进程终止/冷重开 | Windows 具名6窗口 `first-end` / `all-ends` / `flush-1` / `flush-2` / `flush-3` / `confirmed` 已实际强杀并新进程冷开。最新套件包含30次 CU 子进程调用、6次 kill，READY/PID及原始事实保留在 `artifacts/versionstore-cu07/windows-process-observation/`，摘要见 `windows-summary.json`。Linux完整VS也执行相同6窗口，共30次CU child/6次kill（exit137），READY/PID、冷RO全树不变、历史/fork/Audit验证见 `artifacts/versionstore-cu07/linux/processes/`。两平台分别计数，不相加为12个不同窗口。只证明具名状态，不证明任意字节停止或断电；镜像观察另见下节 |
| 独立源码裁决 | 独立复核最终 Go，无剩余 must-fix；清理反例已修正并纳入受控测试。主线程独立核对最终TRX、具名CU进程原始事实和冻结源码输入后，裁决新增源码范围 Accepted；平台、进程与包资格仍分别记录 |
| 新 CU 包消费/公开发布 | 未执行；原六包候选不含 CU。本轮源码资格不替代新版本的四个隔离 PackageReference smoke |

## 首次拒绝、修正与新增观察

首次编译失败来自缺少 SizedPtr namespace using 及通过接口类型作 Result 隐式转换的代码，均已修正。初版 solution build 的3项新增测试告警也已修正；最终 observation build 为0 warning / 0 error。初版 CU141项与最终完整 solution 没有运行期失败；编译拒绝、告警修正与 runtime 验证分别保留，不能把编译错误称为事务行为反例。

独立源码复核收束为 Go，重点清理修正为：当 ArrayPool.Return 抛出时，先完成 pooled lease 的单次归还尝试，再关闭 local Audit 句柄，随后 owner 其余资源，根锁最后。资源槽先清空再清理，不重复关闭、不 Dispose 借入 data；受控 pool/close 异常验证顺序，不宣称实际内存耗尽或原生设备故障。

新增 [PublicConditionalUpdateObservationTests](../../tests/VersionStore.Tests/ConditionalUpdatePublic/PublicConditionalUpdateObservationTests.cs) 的3个 public case：前两例在关闭 owner 后，以已经 Confirmed 的组删除 peer 完整8B TailKey+Fence 后缀形成镜像；第三例使用活跃无关 data Builder，分别验证：

- 只读必要 peer 的完整 body 缺后缀必须被底座拒绝，不能充当 Uncommitted 见证交付旧值。
- 普通覆盖只检查本地物理 head，不提前打开或恢复 peer；后续必要可写访问恢复原后缀，旧 CU 组仍可进入历史并用于 exact fork，子只 clone root bytes，冷只读和 Audit 保持结果。
- 无关已知尺寸 data Builder 仍 Building 时，CU 可以确认其已完成依赖；provisional 地址不获读取资格，随后两层冷重开保持已发布值。

前两例的镜像来源是已确认完整组，删除范围恰为 TailKey+Fence，不模拟一次 Unknown 原生写入中断，也不代表所有尾前缀或断电轨迹。第三例直接验证活跃无关 Builder 与已完成依赖的 public 组合。上述3例的加入只使最新完整 VS 回归增加3项；旧研究 probe、Windows具名kill及这些 Residue/Public观察保持各自证据身份。

## 资格与续接

本轮 CU 源码范围 **Accepted**；144项CU增量、既有回归与6个具名真实进程窗口在Windows和WSL2/Linux均通过。保持ProcessCrashOnly；不外推断电/设备故障、所有字节截点、最大容量成功、k增长性能或历史探针全矩阵。受测779文件的生产/测试/构建输入在验收文档续写后再次核对，未变更；当前提交包含后续验收文档与机器摘要。

没有必须先调整的底层设计或新增源码工作片。下一步若交付CU给下游，按S6用新版本从干净提交生成新六包候选，重新取得四个隔离PackageReference消费证据；旧feed版本不含CU，不能覆盖或沿用它的资格。

CU 原子性为全组共同发布资格，不承诺多个 ReadRef 调用看到同一时刻、heads 永远同步、跨 ref 全序、创建/tag/跨库事务、数据业务闭包或断电耐久。时钟不判组，不引入 TxId、Prepared 日志、持久 heads、恢复执行或额外公开句柄。后续 CU package 交付使用新版本/新 manifest，从对应干净已提交源码打包并取得四个隔离公面闭包证据；不覆盖旧候选。
