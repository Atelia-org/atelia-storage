# EventJournal v2 候选交付记录

日期：2026-09-29。本文记录 main 的未发布 breaking 候选；task 调度与验收状态以[实施工单](bounded-online-io-work-order.md)为准，[冻结合同](bounded-online-io-contracts.md)定义格式与失败协议。nuget.org 已发布版本与固定源码链接见[根 README](../../README.md)，不能把当前 main 当成已发布包。

## 已有源码证据

T00–T07 已通过当前 Linux 源码验证：格式/发布合同核验、RBF boundary scan、locator 与严格打开、EventJournal 局部末态、catalog snapshot/有限 suffix、ref entry/ForwardPlan 缓存收口、离线 audit/健康事实索引候选重建。

T07-C 接手基线为 `d3ba093`；astra 对该基线跨层源码终审完成，无新增阻塞 finding。主线程交接的该基线匹配配置 Release solution 验证为 **968/968**：Data 212、EventJournal 173、Toolkit 56、Primitives 75、RBF 405、RbfSegmentStore 47。最终T07匹配配置Release solution build通过（增量0 warnings/errors），968/968测试全通过且无Skipped；测试临时目录明确为tmpfs，仅功能证据。日志`/tmp/t07a-solution-build.log`、`/tmp/t07a-solution-test.log`。T04 实文件验证包括 1100 次固定 live churn、2500→1 先扩后缩、Q/r 边界、checkpoint 故障三态和实际读取范围；这些聚焦 case 不代替下列长测或进程中断门禁。

| 门禁 | 当前记录 | 待追加材料 |
| --- | --- | --- |
| T07-A 性能矩阵与进程 kill | Passed（Linux） | 下文28组成本、22个SIGKILL及机器可读结果 |
| T07 最终源码收口 | Passed（Linux） | 968/968、跨层审阅及harness独立复核通过；来源见下文 |
| Windows 平台 | PendingPlatform，无实际证据 | Windows build/test、原子替换/坏尾/进程中断结果 |
| T08 隔离五包消费 | 用户已授权 Linux 本地包验证，尚未执行 | 唯一候选版本、已提交干净 source revision、manifest/nupkg/snupkg hashes、独立 assets/Source Link 与两种 public smoke 日志 |
| 消费者升级与旧数据兼容 | 仅只读适配检查 | 另行授权后的消费者编译/测试及应用旧数据证明 |
| 网络发布与真实迁移 | 未执行 | 单独授权、来源与停止服务/备份/验证/切换证据 |

用户已明确当前交付先完成 Linux 源码与本地包验证，Windows 门保持待验证。该范围不授权网络发布或真实实例迁移；Linux/本地包通过也不能声明 Windows 已验证。

## 当前成本与恢复边界

日常打开复用 snapshot，直接扫描最多 `Q=max(1024,L0)` 条 suffix，必要控制 frame 在租 buffer 前限制为 248B（含 Fence 252B）。Create/Fork/Archive/tag 在业务首写前按 suffix/缩容阈值 checkpoint。snapshot 只包含 active names 与所有 tags；历史 op-log 不删除。

streaming snapshot 不保存整文件副本，但 codec 排序/数组仍为 O(L log L) CPU、O(L) 辅助空间，checkpoint 当次有延迟尖峰。catalog 必要 live entries 为 O(L)。ref entry 默认保留 32 个 state/store，0 禁用保留；唯一 ForwardPlan memory LRU 为 4096 项 / 16 MiB 估算预算。进程重启后的 cold Parent walk 及全量 N 事件请求仍可能需要 O(N) 时间和临时空间；缓存估算不能冒充进程总内存上限。

所有日常打开都严格、不修尾、不扫描全历史。坏必要尾帧/metadata 进入可识别停维路径，不 fallback 到较早 head。只读成功不等于全库健康。首版[离线 toolkit](../../tools/EventJournal.Toolkit/README.md)仅支持停写或稳定副本的 `audit` / `rebuild-indexes`；它不修改 source、不补 Bind/Archive/Close 等业务事实，不猜多义边界，没有 apply/repair-tail/旧格式迁移。

`rebuild-indexes` 可以在 `factsStatus="Healthy"` 且 `indexesStatus="Missing"` 时成功生成候选，但仍返回 exit 2，表示源索引问题。完成证明是最后发布的 `manifest.json`：`completed=true`、完整候选文件清单与 hash；不能把退出码或临时目录存在视为候选可安装证明。生成候选不等于实际实例已经升级。

## 消费者适配清单（只读）

2026-09-29 对 `/repos/Atelia-org/atelia` 实际源码只读核对；没有修改或构建兄弟仓，没有升级它的包引用或真实数据。

| 对象 | 当前证据 | 接入 v2 所需工作 |
| --- | --- | --- |
| `eng/StorageDependency.props` | 默认 `StoragePackageVersion=0.1.1-preview.2`、`StorageSourceRevision=976aa345f923da09e2a5cf1dc25ba592b3818b63`；`UseStorageSources=false` | 显式选择经 T08 验证的新包闭包与对应来源；源码联调仅显式 `UseStorageSources=true` |
| `prototypes/SessionJournal.Cli/BranchRewindCommand.cs` | 第 16/19/22 行三处 `RecoverActiveTailOnOpen=false`，涉及 event/ref store 与 ref-op options | 删除已移除属性后执行消费者编译/测试；v2 所有打开默认严格。仍核对 `--confirm-ref`、exact RefId 与 expected head，不能按名字替换 lifetime identity |
| `prototypes/SessionJournal.Offline/SessionJournalOfflineValidator.cs` | 第 41 行调用领域 `ScanCheckedAuditEvents`；报告保持 `BranchRefId` | 保留 selected-chain 领域审计；storage 全历史 toolkit audit 是另一显式检查，不成为每次 rewind 的隐式前置，也不能以 daily open 成功替代领域验证 |
| DurableGraph | 当前兄弟仓文件列表未定位该项目 | 未验证编译、API 接入或旧数据；不能宣布已升级 |

旧目录不能由 v2 自动打开/迁移；兄弟仓日常包消费不能自动跟随 storage main。真实迁移需另行授权，以新目录/副本验证事实字节、地址、exact refs、heads、tags、reflog 和领域 selected chain，之后才讨论切换。

## Linux 成本与中断实测

复现入口：[验证脚本说明](../../eng/Validate-BoundedOnlineIo.md)；[持久化精简原始结果](bounded-online-io-measurements.json)保留28组测量的调用数、请求/返回字节、分配、句柄、分位数、源hash及22个中断时点。完整本地证据为 `/tmp/t07-full-run/results.jsonl`、按文件类别重解析的 `results-audited.jsonl` 与同目录 strace；旧污染库存测量移入 `superseded-*`，不作为本次结果。

每组25次；下面 reads/requested bytes 是整组 kernel read/pread 总量。计时来自单独的无strace进程，单位ms；cold只表示新journal instance，未清OS page cache，不代表物理磁盘冷读。返回bytes因尾页位置改变可有有界波动，详见JSON；不能把请求量当块设备吞吐。

| 场景 | 规模 | reads / requested bytes | p50 / p95 ms |
| --- | ---: | ---: | ---: |
| moves / head | 1000 | 275 / 819500 | 0.3765 / 1.4847 |
| moves / head | 100000 | 275 / 819500 | 0.3945 / 2.0089 |
| moves / head | 1000000 | 275 / 819500 | 0.4082 / 1.6734 |
| orphans / open | 1000 | 175 / 512200 | 0.2675 / 0.4382 |
| orphans / open | 100000 | 175 / 512200 | 0.3688 / 0.4877 |
| orphans / open | 1000000 | 175 / 512200 | 0.259 / 0.5174 |
| control-allocations / open | 1000 | 175 / 512200 | 0.276 / 0.5871 |
| control-allocations / open | 100000 | 175 / 512200 | 0.2569 / 1.4726 |
| control-allocations / open | 1000000 | 175 / 512200 | 0.2552 / 1.5854 |
| segments / head | 1 | 250 / 717100 | 0.3595 / 1.3292 |
| segments / head | 100 | 300 / 819600 | 0.408 / 1.5754 |
| segments / head | 10000 | 300 / 819600 | 0.3944 / 1.3014 |
| tags / open | 100 | 175 / 512200 | 0.4342 / 1.5692 |
| tags / open | 1000 | 475 / 1127725 | 2.0396 / 3.2207 |
| tags / open | 10000 | 3425 / 7181125 | 19.0382 / 22.2731 |
| chain / cold-chain | 10000 | 11100 / 45158700 | 22.3772 / 85.2044 |
| chain / warm-chain | 10000 | 5844 / 23924748 | 26.6037 / 64.9482 |
| cache / cache | 32 | 2625 / 7273800 | 3.3361 / 6.2379 |
| churn-public / open | 1100 | 375 / 1331400 | 1.6225 / 3.1077 |
| shrink-public / open | 2500 | 1375 / 4916375 | 7.3738 / 29.2535 |

tag规模增长时eventSegments始终为50 calls / 102500 requested bytes / 2500 returned bytes，增长来自snapshot，未反查历史tag target。所有28组读取前后文件清单与hash一致，getdents=0，失败/中断读取=0，Dispose后journal句柄=0。cache访问32个refs，配置容量8，实际保留8，操作后最多10个journal句柄，trace瞬时峰值11。cold链1000/10000规模分别额外ParentWalkReads=1001/10001；warm复用只进行一次编译，但完整checked replay仍为O(N)，不承诺热缓存全量回放常数时间。

百万control规模是合法未Bind的allocation历史与真实边界snapshot，不是百万次完成create/archive。公开API另验证1100次churn、2500→1缩容；这两组位于明确tmpfs路径 `/dev/shm/t07-public-run`，Flush未关闭，仅证明功能/读取成本。实际snapshot：allocation三规模L0=0/d=2/68B；public1100churn为L0=1/d=231/82B；2500→1为L0=1152/d=1151/21951B，符合几何缩容和Q预算，保留常量余量而非要求最终snapshot恰为1项。其余规模与22个SIGKILL fixture位于 `/tmp` 的ext4。大fixture用生产codec生成；生成耗时不作为公开写性能。

22个中断时点覆盖event append/flush、tag、checkpoint、rotation：全部SIGKILL实际exit=-9；4个old locator+next窗口按`NextSegmentPresent`拒绝，其余按对应旧/新已发布状态strict reopen。event检查CRC/Parent/sequence/物理tail，rotation检查active1/2；重开验证前后源hash一致。此结果是进程退出证据，不是硬件断电持久性或Windows证明。parser的fd复用ownership及sidecar/stat边界已独立审阅，5条合成回归通过；合成测试不冒充kernel实测。

真实只读挂载另在私有mount namespace中完成：bind remount为ro后root写入得到EROFS；7-file健康journal的toolkit audit为exit0、Healthy/Consistent，源SHA不变。证据：`/tmp/t07-readonly-audit.json`、`/tmp/t07-readonly-source-before.json`、`/tmp/t07-readonly-write-rejection.log`。

另有真实关闭SafeFileHandle后ConfirmDurable失败、driver拒绝后续调用并strict reopen成功（`/tmp/t07-closed-handle.log`）；10条allocation小fixture完整audit为Healthy/Consistent，10条UnpublishedRef warning（`/tmp/t07-control-small-audit.json`）。ext4公开warm MoveRef同目标5次，生产Flush保持，p50=65.5725ms、p95=86.6706ms、每次分配449B（`/tmp/t07-ext4-write.log`）；仅是本机小样本，不外推吞吐或百分位SLA。

## 最终源码与本地包

最终Release验证命令：`dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false`，然后 `TMPDIR=/dev/shm/t07-release-tests dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false`。T08将从本次干净源码提交制作候选；包来源与消费结果在完成后追加。
