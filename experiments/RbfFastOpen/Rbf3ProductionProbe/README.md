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
