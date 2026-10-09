# FrameStore public 跨进程与返回边界强杀探针

消费实际 `Atelia.FrameStore` 的 public API，为 [收尾计划 R1](../../docs/FrameStore-VersionStore/02-framestore-completion-plan.md) 取得有界过程证据。项目只直接 `ProjectReference` 当前 `src/FrameStore`，source-only、非 packable，不加入 solution 或生产包清单。

从仓库根串行执行；需要仓库指定的 .NET SDK，运行在 FrameStore 可准入的 Windows 或 Linux 本地文件系统上：

```powershell
dotnet build experiments/FrameStoreProcessProbe/Probe.csproj -c Release
dotnet run --project experiments/FrameStoreProcessProbe/Probe.csproj -c Release --no-build
```

默认新建 `artifacts/framestore-process-probe/<UTC时间>-<随机值>`，保留 fixtures、`result.json` 和全部子进程 stdin/stdout/stderr 原始日志。成功退出码为 0，运行阶段失败为 1；失败结果包含已完成检查、当前异常和已启动进程身份。已有输出目录拒绝复用，不递归删除。

显式指定输出目录、阶段超时和调用方记录的源码身份：

```powershell
$revision = git rev-parse HEAD
dotnet run --project experiments/FrameStoreProcessProbe/Probe.csproj -c Release --no-build -- --output-directory artifacts/framestore-process-windows-1 --timeout-seconds 30 --revision $revision
```

Linux 可以在已确认 runtime 开关有效的环境下额外运行，记录禁用 BCL 自动锁后实际 public 行为；下面是 Bash 命令，仍须使用全新输出目录：

```bash
dotnet build experiments/FrameStoreProcessProbe/Probe.csproj -c Release
DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1 dotnet run --project experiments/FrameStoreProcessProbe/Probe.csproj -c Release --no-build -- --output-directory artifacts/framestore-process-linux-no-bcl-lock-1 --revision "$(git rev-parse HEAD)"
```

结果记录 runtime、OS、架构、运行目录、assembly SHA-256、传入 revision、`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` 的实际值及 `System.IO.DisableFileLocking` 的 AppContext 值。`callerSuppliedRevision` 是调用方声明，探针不把 dirty 工作区或未提交源码自动视作该提交。WSL 应使用 Linux 文件系统上的独立 checkout/输出目录；两平台资格分别读取实际结果，本文不预先宣称通过。

`appContextDisableFileLocking` 只记录显式 AppContext switch 查询，不是合并环境变量后的有效配置；环境变量为 `1` 时该字段仍可为 false。实际 Unix BCL 选择逻辑见 [runtime v10.0.5 SafeFileHandle.Unix.cs](https://github.com/dotnet/runtime/blob/v10.0.5/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)。本轮执行结果见[过程取证记录](../../docs/FrameStore-VersionStore/02-framestore-process-acceptance.md)。

| 场景 | 公开行为与断言 |
| --- | --- |
| empty / active / 全部 archive | 每种健康 fixture 都执行 writer/writer、writer/reader、reader/writer 拒绝与 reader/reader 共享；空 store 和无 active 的 store 仍须持有控制锁 |
| 两个 reader | 同时保持两个 public owner；第一个显式 Dispose 并退出后 writer 仍拒绝；最后一个 reader 强杀并确认退出后，新进程 writer/reader 均成功 |
| writer 强杀 | 在 READY 后确实拒绝竞争 owner，终止同一个 `Process` 对象，确认退出，再由新进程打开；控制文件仍为 0B |
| Create 返回 | 成功公开返回后强杀；新进程 RO / writer / RO 保留同一 StoreId，且没有 data 文件 |
| Append 返回、尚未确认该输出 | 保留先前确认的 prefix；新完成但未确认的地址允许在冷重开后消失或精确读回，不要求消失 |
| ConfirmDurable 返回 | 新完成输出被确认后强杀，地址、tag、长度、载荷 SHA-256 与首 64B 均须保留 |
| known / unknown Builder | 写入并 Advance 2MiB 逻辑数据，未调用 EndAppend；known 声明 4MiB 并保存提前 12B 地址。活跃期间确认其文件上的已完成 prefix，然后强杀 |
| 三个交错 Builder | 三个不同 active 文件均有已完成 prefix，再同时持有 known / unknown / known Builder；活跃确认后强杀，各文件有独立 RecoveryReports，全部 prefix 保留 |
| 归档返回 | 小阈值使完整帧在确认时归档；归档前及归档后用同一 12B 地址公开读取，再强杀、冷重开验证地址和 payload 稳定，正式集合全部在 archive |

父进程每次启动自己的新子进程，stdin/stdout 使用 JSON line 请求、boot 与 ready/result/disposed 响应，并核对创建时记录的 PID 和随机 token。每次协议读取、输入写入、退出和日志排空有独立超时（默认 30 秒，可选 5–120 秒）。没有固定 sleep；只有收到指定 public 阶段的 READY 后才执行强杀。失败清理也只操作保留的那个 `Process` 对象，不按 PID、名称或进程树选择其他进程。完整成功轨迹为 10 个 fixture、80 个子进程，输入规模固定，不启动服务或持续负载。

锁拒绝必须是具体原生冲突：Windows `IOException` 的 native code（或 BCL HResult 低位）32/33，Linux `IOException` 内含 `Win32Exception.NativeErrorCode=11`。其他异常为探针失败，不计为锁冲突。每种 fixture 在冲突实验前取得 public RO/Inventory/Audit 健康基线，kill/退出后用 fresh process 成功打开作为对照。

每次 crash 之后依次启动全新的 RO 观察进程、可写恢复进程、RO 读回进程。验证使用 `Inventory`、`Audit`、`ReadFrame` 和公开地址 codec，记录逐文件完整恢复报告、header、用户帧数、地址和载荷摘要。RO 前后在无 owner 期间计算目录集合、每文件长度和 SHA-256，成功或拒绝均不得修改受管内容。父进程不在 owner 持有期间改写、移动、删除或替换受管项；只在 child 自己持有文件时记录实际长度，完整指纹在 owner 退出后取得。

Builder 的 `BeforeBuilderWrite` / `AfterBuilderWrite` 是实际文件长度。当前 RBF 的 pending `HeadLen` reservation 会把未完成 Building 数据留在内存；2MiB Advance 证明逻辑写入阶段，不证明物理残尾。当长度未变化时探针要求 pre-recovery RO 成功、每 active 报告为 `None` 且 original/final 长度相等、Inventory 只有已完成 prefix；提前地址不得成为普通完整用户帧。若实际出现物理增长，结果按观测分支记录 RO 拒绝/恢复事实，仍禁止未完成 Builder 变成普通用户帧。这一分支允许合法物理墓碑；墓碑在 Inventory/Audit 中保留并完整校验，不被当作 Builder 完成。

`result.json` 的 `checks` 是实际断言，`observations` 保留阶段、expected/optional 地址及每次打开前后的文件指纹，`processes` 指向 raw logs 并记录 PID、token、kill 和退出码。expected 的 payload 摘要来自确定性输入，read 的摘要来自 fresh process 的公开完整 CRC 读取；内存握手只用于确定停点，不是业务发布记录。

本探针只取证 `ProcessCrashOnly`。Create/Append/Confirm/归档返回后才 READY，不能证明命中 private 初始化/发布、门 write/flush/close、EndAppend 输出、data flush/close/rename 内部 syscall 的窄窗口。Building 未发布的观测也不能替代 `Truncated` / `CompletedTail` 的真实输出中断证据。受控内部故障、构造残留镜像和源码论证继续保持各自证据身份；断电、任意设备乱序、业务发布一致性、资源/规模资格及 NuGet 包消费不在本探针范围。
