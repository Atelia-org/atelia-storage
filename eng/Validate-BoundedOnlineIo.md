# 有界在线 I/O 显式验证

此入口是非 pack console harness，不进入五个生产包。fixture metadata 存在同级 `<fixture>.fixture.json`，不能放入 journal 内破坏完整目录库存。只接受新 fixture 目录；所有故障操作仅作用于生成的临时数据，不执行 repair。先串行构建：

```bash
dotnet build tests/EventJournal.Validation/EventJournal.Validation.csproj -c Release -m:1 -nr:false
python3 eng/Validate-BoundedOnlineIo.Tests.py
python3 eng/Validate-BoundedOnlineIo.py --output /tmp/atelia-v2-small-UNIQUE --small --scales 10 --repetitions 3 --strace /absolute/path/strace
python3 eng/Validate-BoundedOnlineIo.py --output /tmp/atelia-v2-long-UNIQUE --public-root /dev/shm/atelia-v2-public-UNIQUE --scales 1000,100000,1000000 --repetitions 25 --strace /absolute/path/strace
```

`--resume` 只允许复用完整的已有 harness fixture（同级 metadata 身份必须匹配），重新测量所有操作，并把先前 results/trace/environment 移到独立 superseded 目录；它不重造事实，不修复不完整 fixture。先前 kill 目录存在则停止，须为新 kill 运行选择新输出位置。

`--output` 和可选的 `--public-root` 必须不存在。`--public-root` 只将 public churn/缩容 fixture 置于另一明确文件系统；在 tmpfs 上仍执行生产 Flush，但仅提供 RAM-backed 功能及读取成本，不能视作磁盘写延迟。每条成本结果记录 statfs 文件系统类型（Linux stat 将 ext4 报为 ext2/ext3；实际挂载类型需结合 /proc/mounts）。完整运行会生成百万级帧、10000 个 ref segment 文件以及 public API churn/缩容目录；磁盘 fsync 成本可能使运行较慢。脚本产生 `results.jsonl` 与按 fixture/operation 分开的 `.strace`。strace 必须支持 `-f -yy` 与 `-e raw=read,pread64,getdents64`；raw 模式只打印 buffer 指针，不输出内容。未知/未配对 trace 记录导致验证失败。

每种 fixture 的生成与测量是独立进程。大 moves/orphans/Parent-chain/tag fixture 使用真实生产 codec + RBF Append，再 completion flush；这些生成耗时不代表生产 public 写延迟。control-allocations 大历史使用未 BindName 的合法 Create allocations，写最后 allocation 的空 catalog snapshot，再 public CreateBranch("main", target)，留下两个 suffix records。这不是百万次 completed create/archive churn。小规模 public churn、先2500 live后缩到1另行保留；生产 DurableFlush 未禁用。snapshot 编码只使用已有内部生产 codec。

计时/当前线程分配来自不带 strace 的独立进程；系统调用量来自执行相同模式与次数的另一个进程。cold 表示新 journal instance，OS page cache 未清除。p50/p95 包含打开操作且首次 JIT 未单独剔除；25 个样本时 p95 通常不包括最大样本。warm 模式保留一个 journal instance；native 计时不含其一次性 open/final Dispose，但 warm trace aggregate 含这些一次性 I/O，不能把 aggregate 除次数称为每次 warm hit 的精确读量。输出明确记录模式与次数，禁止把 cold 当冷物理磁盘。

这些是 kernel read/pread 返回字节，不是块设备物理读量；未清 OS cache，不能据此推断磁盘吞吐。真实 I/O 统计按 `openat` 获得的绝对路径追踪共享 fd table，重组多线程 unfinished/resumed 行，统计 dataset 的 read/pread64 请求量及返回字节、getdents64、open/close 和峰值 fd。trace 保留外置 metadata 读取，但 dataset 路径过滤明确排除该固定开销；aggregate 仅包括全部 iterations 的 journal 文件读取与释放，不包含 fixture 生成及脚本前后 hash 扫描。已知 dataset fd 的任何失败/中断都显式分类，不可解析的行导致失败。`/proc/self/fd` 观测是测量操作完成时的持有量；trace 峰值可包含瞬时验证句柄。分配量不是总进程 RSS。fixture 前后哈希含相对路径及全部文件字节，保证读取没有改源；这些哈希不证明文件系统崩溃恢复能力。

进程中断验证：子进程 public 操作在既有内部 phase probe 输出 metadata ready，父进程 SIGKILL，再用全新进程 strict read-only reopen。old locator+已经创建 next 的中间窗口必须拒绝；完成 locator 发布后接受新 active。append/tag/checkpoint阶段保存实际 reopen 结果。它是进程中断证据，不是断电/目录项硬件持久性保证；该Linux入口本身不验证Windows；原生Windows入口及NTFS/ReFS实证见[中断脚本](Test-WindowsInterruption.md)与[平台交付](../docs/EventJournal/windows-platform-delivery.md)。异常 probe 与真实关闭句柄失败继续由聚焦测试分别覆盖。

额外窄验证（均用新临时目录；`describe` 只读已生成 fixture）：

```bash
dotnet tests/EventJournal.Validation/bin/Release/net10.0/Atelia.EventJournal.Validation.dll write-sample /tmp/atelia-v2-public-write-UNIQUE 5
dotnet tests/EventJournal.Validation/bin/Release/net10.0/Atelia.EventJournal.Validation.dll closed-handle /tmp/atelia-v2-closed-handle-UNIQUE
dotnet tests/EventJournal.Validation/bin/Release/net10.0/Atelia.EventJournal.Validation.dll describe /tmp/atelia-v2-long-UNIQUE/control-allocations-1000000
```

`closed-handle` 实际关闭所属 RBF 的 `SafeFileHandle`，不使用抛异常 probe；检查 ConfirmDurable 失败、fault 后拒绝和新实例 strict reopen。`write-sample` 保持公共 MoveRef 的持久屏障，每次为相同有效 target 写入新 move；仅五个样本不代表稳定生产尾延迟。完整源验证可显式选择 TMPDIR 的文件系统，报告必须注明；tmpfs 只给功能证据，不替代 ext4 发布/中断取证。
