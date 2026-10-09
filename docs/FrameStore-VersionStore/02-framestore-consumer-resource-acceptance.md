# FrameStore 独立消费与资源规模取证（R2）

日期：2026-10-10。生产源码基线：`59ebd43`。本轮消费 [S2](02-framestore-core.md)、[S3](03-framestore-interleaved-builders-and-durability.md) 和[收尾计划 R2](02-framestore-completion-plan.md)，增加两个独立 source-only public API 探针，不改变生产行为、solution 或包清单。两个实施工作包和一个独立复核工作包均为 `gpt-6.1-sol / xhigh`；主线程复核源码、串行构建/运行并读取实际结果。

状态：**R2 完成；未发现需本轮修复的确定生产缺陷。完整 S2/S3 验收仍待 R3。**

## 独立消费资格

[FrameStoreConsumerProbe](../../experiments/FrameStoreConsumerProbe/README.md) 只直接 ProjectReference FrameStore，不使用 IVT 或内部地址表示。六个隔离 store 合计完成 11 个用户帧；固定 node payload 包含业务 identity、目标 identity 和 public codec 编码的 12B 引用。

| 向量 | 实际证据 |
| --- | --- |
| self-reference | 提前地址写入自身，归档前后及新 RO owner 中仍解码为同一个自环 |
| A↔B | 分段交错写 header、预留引用、回填/Commit；分别改变申请次序和完成次序，重开均得到 `1→2,2→1` |
| M=1 + Append | A 占据唯一 Builder 槽，第二 Builder 拒绝而完整 Append B 成功；再回填 A 的引用并完成 |
| 取消与复用 | A 完成而 B 未完成时 Confirm 不使 B 可读；取消 B 后 C 实际复用 b，旧 Builder/Writer 拒绝，旧 Dispose 不取消 C |
| 来源责任 | `ReadFrame(b)` 可以合法读到 C；消费者发现 B/C identity 不符而拒绝旧图，FrameStore 没有验证业务来源 |

每个场景关闭 writer 后重新载入保存的 StoreId/12B 地址，打开新 RO owner，沿实际 payload 引用解码图；不按文件名、分配顺序或 Inventory 顺序推断业务关系。地址 JSON 只是实验输入证据，不是模拟的业务发布协议。首次运行纠正探针预期：Building 越界读取在产生 Result 前抛 `InvalidOperationException`，当前生产 guard 正确。

## 资源与成本口径

[FrameStoreResourceProbe](../../experiments/FrameStoreResourceProbe/README.md) 同样只直接引用 public FrameStore。默认 M=1/4/16、archive=16/128/1024、每点三次；sizeHint=1/4/8MiB、每批四次取消；大 Append 两种载荷各三批、每批八个 256KiB 帧；归档固定 256B payload，每批随机读 64 次。1024 点实际跨两个归档桶。

最终运行显式给出 600 秒协作时限、96MiB 普通文件数据预算；时间检查不能打断在途同步 I/O，磁盘预算不含目录元数据、输出 JSON 和外部日志。Windows 首轮 180 秒到限退出，已执行 176 条断言无失败、199 个样本；覆盖未完成，不能算完整通过。其数据保留，最终资格只取随后新目录运行。该首轮还早于一处测量解释字符串的修改，不重标为最终源码身份。

计时/累计分配区间包含 public 操作和小型行为核对，GC、句柄观察与全目录磁盘采样在区间外。记录实际 regular-file Length 与分配字节；Windows 用 `FileStandardInfo.AllocationSize`，Linux 用 `statx.stx_blocks * 512`，不以未压缩文件的逻辑长度冒充分配量。

每次资源快照分别记录主动 GC 前/后句柄；即时变化比较 `after.nativeHandleCountBeforeForcedGc - before.nativeHandleCountAfterForcedGc`。操作中仍可能发生自然 GC，因此不单靠进程计数证明所有 Dispose。GC 后存活量包含共享池、探针报告/地址表和运行时；池占用明确为 unknown，不能从 RSS 或存活总量倒算 scratch/chunks。逐文件独占打开和 owner 重开是补充观察，可能发生在显式 GC 后，Linux 的 FileShare 还受 BCL 锁机制影响。

实际轨迹确认：

- 满额 M 个 Builder 加 Append 形成 2/5/17 个 active。健康取消保持 idle 句柄；降低配置到 M=1 后全部 active 仍被接管、恢复报告完整、首次 Confirm 成功。句柄成本随历史 active 峰值保留，配置不是总句柄硬上限。
- 声明 payload=1B 的 Builder 实际借到 1/4/8MiB capacity，Advance(0) 的 Length 增量均为 0；取消归还 chunks 后，后续同档请求分配显著降低。不能将大 sizeHint 等同于实际输出，也不能要求 RSS 立即下降。
- 两组大 Append 分别用不含 fence 的载荷及 aligned `RBF3` fence 载荷；关闭 owner 后从公开 wire 的最后 TailKey 核实 0/非0，确实覆盖 retained scratch 分支。1MiB scratch 的具体归属来自源码，不以本轮进程总量宣称逐对象精确测量。
- 反复成功读取/Dispose、Inventory/Audit 的指定 visitor 异常和取消均保持 owner 可用；最高 active header 损坏导致工厂在先前 active 已接管后拒绝，修复后可完整重开。坏正式名称也重复拒绝，锁冲突异常不会冒充预期的 InvalidDataException。

本探针没有直接测量重复 `ReadFrame` CRC 失败的资源走势；该失败释放依据现有测试及 [RBF pooled read](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs)、[FrameStore read](../../src/FrameStore/Internal/Storage/DirectoryFrameStoreFiles.cs) 清理路径。不能把本轮扫描回调失败推广成所有 I/O 失败已过程取证。

## 执行结果与后续

| 环境 / 检查 | 最终结果 |
| --- | --- |
| Windows 11 build 22000 / x64 / E: ReFS / SDK 10.0.201 / .NET 10.0.5 | 消费者 304/304 断言、6 场景；资源 200/200 断言、226 样本，257.71 秒 |
| WSL2 Ubuntu / x64 / `/dev/sde` ext4 / 同 SDK/runtime | 消费者 304/304 断言、6 场景；资源 200/200 断言、226 样本，141.10 秒 |
| Windows solution Release build / matching `--no-build` | 2163 passed / 3 Linux-only skipped / 0 failed，其中 FrameStore 377 passed / 3 skipped |
| 新旧依赖边界 | 七个旧栈项目精确 RBF1 package/source 校验通过；FrameStore 的 Rbf/Data/Primitives 实际 assets 均为 main project |

两份资源结果的数据文件逻辑长度均为 12,999,364B、分配量 17,817,600B（不含结果 JSON/目录元数据）。所有磁盘分配和句柄观察均成功，没有静默降级为未知数据。Windows 两个探针最终构建各为 0 warnings / 0 errors；Windows solution 和 Linux 首次源码构建有 41 项既存 Data/Rbf XML warnings，0 errors。本轮未重跑 Linux 全 solution；Linux kernel 为 `6.18.33.2-microsoft-standard-WSL2`。

以下为每点三次的中位数，单位 ms；所有打开都是新 owner，OS/storage 缓存未清空、建库后通常温热。不同平台/文件系统结果单列，不当作受控平台速度竞赛。

| 平台 | archive 文件数 | Open | Inventory | Audit |
| --- | ---: | ---: | ---: | ---: |
| Windows / ReFS | 16 | 11.25 | 23.43 | 23.78 |
| Windows / ReFS | 128 | 58.04 | 186.55 | 183.19 |
| Windows / ReFS | 1024 | 887.41 | 3445.48 | 3428.43 |
| Linux / ext4 | 16 | 0.51 | 0.88 | 1.11 |
| Linux / ext4 | 128 | 0.93 | 5.35 | 5.66 |
| Linux / ext4 | 1024 | 7.60 | 44.95 | 50.55 |

Windows 的 128→1024 点耗时增长快于文件数，不能报告已测得线性延迟；小 payload 下 Inventory/Audit 相近，是打开/metadata 成本可能占主导的线索，不是具体根因证明。源码发现仍流式推进 archive、只保存 active IDs/max；每文件采用 `RequireEnumeratedFile`，没有逐历史文件重扫父目录。Windows 会反复建立 metadata handle 并取得 volume GUID，其具体系统调用成本尚未单独归因。累计托管分配接近按文件数增长，但也不是总耗时的复杂度证明；没有业务负载/SLO 时，保留基线而不据此增加缓存或判定性能达标。

M=16 的第二次重复中，两平台 Dispose 和重开分别减少/增加 18 个即时句柄（17 active + owner 锁），降低 M 后仍如此。对重复读取、扫描异常/取消及工厂失败/修复的 89 个操作样本，Linux 即时 fd 差量全部为 0；Windows 84 个为 0，其余五个为 -3/+3，过程中有回落。进程总计数包含运行时/观察者，未将波动归因于库，也未观察到随每次操作或文件数量持续累积的临时句柄。此结果结合源码与既有单次清理故障测试，支持当前资源基线；不宣称证明任意负载永不泄漏。

独立复核还记录了局部算法边界：`DrainStopped` 在 N 个 active 同时归档时重复 `List.RemoveAt(0)`，会搬移 N(N−1)/2 个引用；它属于 active 台账维护，不是历史 archive 名称发现的复杂度。本轮未见需修复的实际问题，不为该观察增加新的框架或待办。

已提交[消费者机器摘要](../../experiments/FrameStoreConsumerProbe/evidence/2026-10-10-summary.json)和[资源机器摘要](../../experiments/FrameStoreResourceProbe/evidence/2026-10-10-summary.json)，包含最终环境、输入、原结果 SHA-256、探针源码 SHA-256、图读回及全部样本分组的 min/median/max 与句柄变化范围；基线 `59ebd43` 是未改变的生产源码身份，不冒充包含探针的提交。探针、本文和摘要由同次 R2 提交绑定。

完整 JSON/日志保留在忽略的 `artifacts/framestore-consumer-windows-r2-accepted/`、`artifacts/framestore-resource-windows-r2-final/`、`artifacts/framestore-r2-linux/`；预算退出的原结果在 `artifacts/framestore-resource-windows-r2-accepted/`（该目录名不表示成功）。Linux 原始 store 位于 `/tmp/atelia-framestore-r2-Dojh1W/artifacts/`。source tar、116 项 C#/项目/构建输入 hash 和摘要提取脚本在 `artifacts/framestore-r2-source.*`、`framestore-r2-source-sha256.json`、`framestore-r2-summarize.py`；核对最终输入与 Linux 快照相同。Windows TRX 位于 `artifacts/framestore-r2-tests/`。

可重跑命令见两探针 README。实际先执行 `dotnet build Atelia.Storage.slnx -c Release`、`dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/framestore-r2-tests`、`eng/Test-Rbf1ReferenceAssets.ps1`，再串行 build/run 两个探针；资源 final run 增加 `--max-seconds 600`，均使用 fresh 目录和 `59ebd43-plus-R2-probes` 来源声明。Linux 从上述隔离快照独立 build/run；没有共享 Windows bin/obj、打包、发布或推送。

本轮不新增缓存、历史 ID 台账、资源配额或生产故障开关。S2/S3 仍保持 Draft；下一轮 R3 应补足 R1 记录的三个具名内部窗口，并完成 S2-Q1..Q7/S3 的条款到证据映射及最终状态裁决。包交付、VersionStore、多线程 writer 和断电资格不在本轮范围。
