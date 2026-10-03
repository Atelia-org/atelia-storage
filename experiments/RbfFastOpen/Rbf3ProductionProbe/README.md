# RBF3 production probe

本目录只检验生产 RBF3：Header/Fence 为 `RBF3`（LE `0x33464252`），Head/Tail 长度为 4B units，ticket 仍是 byte offset/length。它不扩写此前 RBF2 byte-wire 原型或其不可变快照。

使用现有 `Atelia.UnifiedRootProbe` friend assembly 读取逻辑计数、安装既有 write instrumentation，没有新增生产 hook。正常行为全部走 `RbfFile`/`IRbfFile` 公共入口。真实文件、临时目录和日志均在新的 W: 目录。

串行执行，先 build，再 quick，确认源码提交后使用新的 formal 目录。`run.py` 不负责 build；运行期间源码和 commit 不得变化。

```powershell
dotnet build experiments/RbfFastOpen/Rbf3ProductionProbe/Rbf3ProductionProbe.csproj -c Release
python experiments/RbfFastOpen/Rbf3ProductionProbe/run.py --output W:/RbfFastOpen/rbf3-quick-20261003 --quick
python experiments/RbfFastOpen/Rbf3ProductionProbe/run.py --output W:/RbfFastOpen/rbf3-formal-20261003 --expect-commit <full-source-commit>
python experiments/RbfFastOpen/Rbf3ProductionProbe/export_snapshot.py W:/RbfFastOpen/rbf3-formal-20261003 experiments/RbfFastOpen/Rbf3ProductionProbe/results/rbf3-<source-short>.json
```

输入和证据分为以下有限集合：

- Python 独立 bitwise CRC32C/LE/XOR encoder 生成 Key 0/301/高 uint、meta 0/1/3/65535 的 units wire。生产公开 caller/pooled checked read、info/meta、扫描、边界和 Cache Off/Slots16 验证；RBF1 旧 byte ticket 只读且拒绝可写打开。
- 实际 Append 与 Builder 的同一小帧，逐字节保留 terminal prefix，涵盖完整 body 后 raw Key/Fence 的全部 0..8B。只读打开不修改、首次恢复保留全部现有 retained prefix、第二次恢复无动作；payload 损坏仍可结构打开，checked read 拒绝 CRC。1MiB 性能路径通过 32761B 切片提交而跨过 owned writer chunks。
- 实际 managed child 在 `AfterWrite` 已写出的正常/repair prefix、`BeforeSetLength` 和 `AfterSetLength` 发出 READY 后被父进程终止。正式 35 个 checkpoint；这不是所有 syscall 内任意终止或断电模拟。
- 健康 Open：N=1/1000/100000，最后一帧 payload 为 0/1MiB/近最大（此前历史均为空帧），共 9 组。7 个 warm OS 时间样本，计量另行读取，公共只读/可写工厂请求均应 ≤39B、≤4 次。最大帧为 268435452B，payload+meta 268435424B，meta65535；W: 实际 caller/pooled roundtrip 和 phase1 meta 读取。
- 1MiB/最大未完成 Trailer 尾：生产 64KiB 非重叠 aligned Fence 扫描。扫描 offset 范围 ≤M+7，与包含 Header/结构资格检查的 aggregate requested bytes ≤M+135 分别报告。
- 4KiB/1MiB、zero/Fence 两种输入，Append/Builder 每种 7 轮交替，16MiB 目标批量；实际 CRC/key/transform/copy/chunks/write 包含在计时内，pool/scratch 预热，DurableFlush 分开。checked read/info/meta 在 Off/Slots16 下按操作轮转 7 轮；同 ticket、OS 与 RBF cache 预热。计量另采，不混入计时。

独立 Python 对小生产 writer wire 全量复算 CRC 和编码。最大文件只独立验证 Header/units/raw key/Fence/decoded Trailer 字段及 12B bitwise TrailerCRC，完整 payload CRC 由生产 checked-read 检验，不能将两者写成同等独立审计。最大文件不会多组全量读，避免扩大内存与运行量。

`production-results.json`、Python 输出、每个 child 日志和真实 `.rbf` 文件保留在 W:；`provenance.json` 固定 SDK、commit、源码与全部产物 SHA256。计时外采集 `environment.json` 的 CPU/OS/Win32 W逻辑盘事实，Get-Volume/物理 disk provider 不可用则明确记 `Unavailable`，不阻断文件实验；用户对 W: 的 SSD 描述另行标注。运行固定 `DOTNET_TieredCompilation=0`、`DOTNET_TC_QuickJitForLoops=0`，child 继承。导出器拒绝覆盖 existing snapshot，并复核全部 hash。JSON 输出 UTF-8 LF。正式预计数分钟；quick 缩为 4 组 Open、23 个终止 checkpoint、3 样本、不写最大帧。

本证据不代表冷盘、机器断电、下游适配、公开 NuGet 消费或旧离线 Recovery scanner 的 RBF3 支持。

## 4KiB / 8KiB 输出阈值比较入口

`ThresholdProbe.cs` 与 `threshold_compare.py` 只比较冻结的4KiB基线和一个8KiB候选，不运行历史crash/Open矩阵，不修改生产阈值。主线程在同工作树依次构建、freeze，再施加经审阅的RBF3专用阈值补丁、构建、freeze；运行期间仅使用W:中复制的两套完整Release输出。下面的路径均须使用新的名字；build失败时不要freeze。

```powershell
$probe = 'experiments/RbfFastOpen/Rbf3ProductionProbe/Rbf3ProductionProbe.csproj'
$runner = 'experiments/RbfFastOpen/Rbf3ProductionProbe/threshold_compare.py'
dotnet build $probe -c Release *> W:/RbfFastOpen/threshold-baseline-build.log
if ($LASTEXITCODE -ne 0) { throw 'Baseline Release build failed.' }
python -B $runner freeze --output W:/RbfFastOpen/threshold-baseline --label baseline --threshold 4096 --build-log W:/RbfFastOpen/threshold-baseline-build.log --build-command 'dotnet build experiments/RbfFastOpen/Rbf3ProductionProbe/Rbf3ProductionProbe.csproj -c Release'

# 主线程此处施加受控8KiB候选补丁；只改AppendRbf3的专用阈值与stackalloc上限。
dotnet build $probe -c Release *> W:/RbfFastOpen/threshold-candidate-build.log
if ($LASTEXITCODE -ne 0) { throw 'Candidate Release build failed.' }
python -B $runner freeze --output W:/RbfFastOpen/threshold-candidate --label candidate --threshold 8192 --build-log W:/RbfFastOpen/threshold-candidate-build.log --build-command 'dotnet build experiments/RbfFastOpen/Rbf3ProductionProbe/Rbf3ProductionProbe.csproj -c Release'

python -B $runner compare --baseline W:/RbfFastOpen/threshold-baseline --candidate W:/RbfFastOpen/threshold-candidate --output W:/RbfFastOpen/threshold-comparison --rounds 7 --batch-mib 8 --operation-cap 16384
python -B $runner jit --bundle W:/RbfFastOpen/threshold-baseline --output W:/RbfFastOpen/threshold-jit-baseline
python -B $runner jit --bundle W:/RbfFastOpen/threshold-candidate --output W:/RbfFastOpen/threshold-jit-candidate
```

freeze保存精确源码/配置副本及哈希、整套二进制哈希、实际build日志、SDK/runtime清单和commit/status。两份源码闭包必须只有`RbfAppendImpl.cs`不同，Data/Primitives DLL须相同；probe入口、Python验证器及输入不能随候选变化。脏树候选的ProductVersion只表示编译元数据，不替代源码快照。每个独立子进程记录实际加载的DLL路径、SHA256、ProductVersion；4100B Key0实际write count与所有count/scratch路径共同检查声明阈值，不能只靠baseline/candidate标签。

固定`DOTNET_TieredCompilation=0`、`DOTNET_TC_QuickJitForLoops=0`，每轮交替A/B、B/A。15个布局包括Key0的total512/4092/4096/4100/6144/8188/8192/8196/65536B及其中6个真实marker非零Key布局；meta0和meta3覆盖payload phase0/1。`total=Align4(payload+meta)+32`另由生产layout/ticket核对。暖态每case目标8MiB、最多16384次；原始batch毫秒数随结果保存，过短或噪声大的样本须另取新目录追加，不由七轮或中位数自动判胜。

先预热JIT及进程Shared pool，再分别记录**新File首次Append**与同File暖态批量：首次不是进程/OS/pool cold，工厂创建与Dispose不在Append时间内。两段Append分配和DurableFlush时间分别保存。只读反射在计时外观察已有`_appendScratch`长度及Dispose后清空；allocated为0不等于File未保留scratch。全部计时结束后，单独count pass在CreateNew完成之后安装既有write hooks，记录首次/暖态请求长度及顺序偏移；它不是设备I/O计数。

每个pass保存实际first/last独立wire文件并记录两个实际Key。生产非零Key可能随Append变化，所以不要求同一文件或A/B编码字节相等。所有计时结束后复用`oracle.py`的独立bitwise CRC32C/units/XOR模型，完整验证这两个端点的payload/meta/Tag、CRC与wire重编码，并核对其对应的真实文件首尾及EOF；中间批量帧不称作独立全审。`comparison.json`保留逐轮原始比值、首次/暖态成本，`provenance.json`固定全部原始工件哈希；脚本不自动决定合入。

JIT另pass使用[官方JIT诊断变量](https://github.com/dotnet/runtime/blob/v10.0.5/docs/design/coreclr/jit/viewing-jit-dumps.md)捕获实际目标runtime的`AppendRbf3`反汇编，保持与计时相同优化JIT设置。主线程审阅saved registers、静态prolog和分支localloc/stack probe；这是该方法的目标JIT栈证据，不能仅按源码stackalloc大小推导峰值，也不代表完整调用链栈峰值。诊断不混入性能计时。本入口目前没有测量结论，候选可能保持延期。
