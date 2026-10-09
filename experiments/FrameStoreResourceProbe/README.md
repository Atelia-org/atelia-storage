# FrameStore R2 有界资源与规模探针

独立 `net10.0` console，`IsPackable=false`，不加入 solution。唯一直接 ProjectReference 是 `src/FrameStore/FrameStore.csproj`，只调用 FrameStore public API；BCL 用于观测进程、普通文件元数据及在无 owner 时构造可还原的坏磁盘向量。

权威：[S2 核心与资源基线](../../docs/FrameStore-VersionStore/02-framestore-core.md)、[收尾计划 R2](../../docs/FrameStore-VersionStore/02-framestore-completion-plan.md)、[当前 public 指南](../../src/FrameStore/README.md)。探针测量既定基线，不新增资源预算 API、缓存、reader pool、manifest 或运行时开关，也不设置吞吐或“泄漏字节”门槛。

## 事先声明的边界

默认 Builder 数量点 `M=1/4/16`；archive 文件点 `16/128/1024`，最大点跨固定 1024 编号桶边界；每点 **3 次**重复。默认 regular-file 磁盘预算 **96MiB**，协作时长预算 **180 秒**，默认输入保守预估数据约 17MiB（实际数值由启动参数的 `estimatedDataBytes` 给出）。数据和结果保留，不自动删除。

预算在同步操作之间检查，不能打断已经开始的 I/O、CRC 或 GC。因此 `maxSeconds` 是协作退出界限，不是硬实时时限。`budget-exceeded` 表示探针按事先边界退出、覆盖尚未完成，不等于 FrameStore 行为失败；高耐久发布/归档成本的文件系统可用新 fresh 路径显式提高 `--max-seconds`，保留首次预算退出的证据。磁盘在 sample 边界核对；优先使用 OS 报告的普通文件分配字节，无法取得时明确降为文件 `Length` 合计。目录、文件系统 metadata、输出 JSON 与外部日志未计入该数据预算。输入预估不是未知文件系统分配单元的保证。

输入还有有限硬界：最多 8 个 M/规模/hint 点，`M<=128`，每规模 `<=16384`，hint `<=32MiB`，repeats `2..10`，hint iterations `<=32`，Append iterations `<=128`，大 payload `16KiB..4MiB`，archive payload `256..4096B`，随机读每批 `<=4096`，时长 `<=600s`，磁盘 `<=256MiB`。所有数字须为正，archive 至少两个规模点。扩大输入必须仍通过预估磁盘准入。

## 执行

仓库约定 .NET 操作串行。先 build Release，再运行匹配的 binary；不要与 solution build/test 并行。

```powershell
dotnet build experiments/FrameStoreResourceProbe/Probe.csproj -c Release
dotnet experiments/FrameStoreResourceProbe/bin/Release/net10.0/Atelia.FrameStoreResourceProbe.dll `
  --work-root E:/storage-resource-r2/run-001 `
  --output E:/storage-resource-r2/result-001.json `
  --source-revision <source-commit>
```

Linux 对应调用同一个 DLL，使用本机的尚不存在目录与输出路径即可。未提供 `--work-root` 时，在当前目录的 `artifacts/FrameStoreResourceProbe/<UTC>-<GUID>` 创建 fresh 数据目录。未提供 `--output` 时，结果写入其 `result.json`。已存在的 work root/output 直接拒绝。stdout 是完整 JSON，stderr 是少量阶段/边界记录。退出码 `0=全部行为断言通过`，`1=行为/运行失败`，`2=输入拒绝`，`3=预算退出、覆盖未完成`。

可配置参数（逗号列表按升序去重）：

| 参数 | 默认 |
| --- | --- |
| `--builder-limits` | `1,4,16` |
| `--archive-scales` | `16,128,1024` |
| `--size-hints` | `1048576,4194304,8388608` |
| `--repeats` / `--hint-iterations` / `--append-iterations` | `3` / `4` / `8` |
| `--large-payload-bytes` / `--archive-payload-bytes` | `262144` / `256` |
| `--random-reads` | `64` |
| `--max-seconds` / `--max-disk-mib` | `180` / `96` |
| `--source-revision` | `not-supplied`；运行者应填写被测 revision |

每次运行均保留所有 store，复跑使用新路径。probe/source 的 SHA256 与运行日志由验收主线程统一归档；单独的 JSON `sourceRevision` 是调用方提供的来源声明。

## 实际轨迹

- **active 历史峰值**：每个 M/repeat 创建独立 store，持有 M 个 Builder，再在满额时成功 Append，形成 `M+1` active。健康取消后保留 idle active；Dispose 后才改 config 为 M=1。冷 owner Open 须完整恢复历史 active 并首次 Confirm，不能按新 M 跳过或删去。新 owner 的 M=1 配额另行验证。
- **大 sizeHint**：声明 1B 的 Builder 分别 `GetSpan(1/4/8MiB)`，记录实际 `Span.Length` capacity 和同一 writer 的 `Length` 差值，`Advance(0)` 后健康取消。不能用声明长度或逻辑 Length 推算池/总内存。
- **大 Append / owned read**：每批 8 个 256KiB 帧；每批保留 8 个完整 `FrameRead`，再显式 Dispose。两个 payload 对照：重复 `0x35`，以及首个 aligned word 为公开 RBF3 Fence ASCII `RBF3`。后者强制非零转义 Key，覆盖当前大 Append 的 retained scratch 分支。owner 关闭后，按[公开 wire 格式](../../docs/Rbf/rbf-format.md)从末帧 raw TailKey 核对实际 `0/非0`，写入断言及 sample。scratch 容量的 1MiB 归属来自当前源码，不把低分配数解读为没有 scratch，也不把池回收解读为 RSS 必须下降。
- **Open 失败清理**：关闭 owner 后，备份三个 header-only active 的最高编号文件，只改其首 header payload 一个 byte，使完整 CRC 不合格。重复 Open 应以 `InvalidDataException` 拒绝；记录完整异常，finally 逐字节还原，再 Open/Confirm。这样失败位置位于此前 active 已纳入工厂台账之后。另在无 owner 时加坏正式名称，重复拒绝并删除这一已知新建测试项，随后重开。坏向量由探针自己建立，不修改活 owner 的受管文件。
- **archive 规模**：固定 payload 与公开 `rotationThresholdBytes=256`，每次 Append 实际形成一个 archive 文件，并断言 requested 与实际文件数一致。分别测构造、Open、Inventory、Audit、随机完整读/释放和 Dispose；每个规模重复 3 次。Open 是新的 owner，OS/storage cache 未控制，建库后通常温热，不能称为冷盘。
- **扫描退出**：每次重复中分别让 Inventory/Audit 的首 visitor 抛出指定异常或取消 token。异常须传播；同一个 owner 随后 Confirm、Read 和再次完整扫描均成功，记录操作返回后资源样本。所有 owner 关闭后逐文件取得 `FileShare.None` 普通文件句柄，不改 bytes；与严格 public owner 重开组合验证资源清理。

## 如何解释 JSON

`environment` 包含 OS/runtime/process/GC 和 `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`；`input` 包含所有有限参数、预估和来源声明；`samples` 保留原始逐阶段耗时、managed 分配差值、资源前后快照、实际磁盘规模和 case details。`assertions` 是可检查的行为断言，`expectedFailures` 保留完整异常；`status=budget-exceeded` 不取得完整覆盖资格。

测量区间只含被测操作及其行为核对，资源观测、显式强制 GC 与目录容量采样在计时区间外。`allocatedBytesDelta` 用 `GC.GetTotalAllocatedBytes(true)`，包含区间内所有线程的 managed 分配和消费者的核对/小型结果对象，不能当成库独占分配。`managedBytes` 在两次完整 GC 和 finalizer drain 后读取，配合 heap/fragmentation 指标；`managedPoolRetainedBytes=unknown`，池保留量不能由 live 总数倒算。探针自身的 `Samples`、`Assertions`、`ExpectedFailures`、地址表持续保留，GC 后 live 增长也包含这些报告数据；主动 GC 还可能影响 pool trimming。RSS 在 GC 后观察，包含 runtime/native/code/OS paging。

native handle 每次 snapshot 有 **`nativeHandleCountBeforeForcedGc` 与 `nativeHandleCountAfterForcedGc` 两个独立数值**及各自错误字段。`after.nativeHandleCountBeforeForcedGc` 在被测操作返回后、探针主动 GC 前读取，避免显式 GC 掩盖未及时 Dispose；操作内仍可能自然 GC/finalize，因此单个数值不是绝对的泄漏证明。Windows 使用 `Process.HandleCount`；Linux 使用 `/proc/self/fd`，包含该枚举自己的观察 fd。两者还包含 JIT/runtime、日志、观察代码资源；warmup 用于预热，不宣称消除全部观测扰动。

磁盘 `logicalFileBytes` 是实际普通文件 Length 合计；Windows `allocatedFileBytes` 用 access=0 metadata handle 的 `FileStandardInfo.AllocationSize`，Linux 用 `statx.stx_blocks * 512`，目录 metadata 均排除。观测失败时该字段为 null 并保留错误。测量 metadata handles 在查询后同步关闭，且不进入操作计时。

验收应结合重复轨迹看与实际 active 数吻合的保留、临时 archive/scan handles 是否返回、failed Open 修复后能否重开、GC 前后差值及持续净增长。逐文件 `FileShare.None` 成功仅是补充观察，Linux 下受 BCL flock、环境开关及进程锁机制影响，不能单独作为绝对 close 证明；即时 native fd 轨迹结合源码/故障测试更可靠。没有预设吞吐 SLO，也不因单次 GC、RSS 高水位或 pool warmup 的上升判定产品泄漏。通过行为断言只证明本次有界轨迹；不代替 R3 的完整 S2 条款验收、包消费或任意网络文件系统资格。
