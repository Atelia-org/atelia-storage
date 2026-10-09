# Store owner lock：Windows 有界探针

目的：验证 S2 `[S-FS-OWNER-LOCK]` 所需的 Windows 控制文件锁原语，回答它能否在格式门出现前建立、协调同/跨进程 owner，并在进程终止后释放。没有 FrameStore 实现，也不访问正式 store。

项目仅引用 BCL，net10.0、非 packable，不加入 solution 或生产包清单。只接受 Windows；Linux 的显式 checked flock 入口另行实施验证，不能直接复用本探针的 BCL 成功判据。

从仓库根串行执行：

```powershell
dotnet build experiments/StoreOwnerLockProbe/Probe.csproj -c Release
dotnet run --project experiments/StoreOwnerLockProbe/Probe.csproj -c Release --no-build
```

每次在忽略的 `artifacts/store-owner-lock-probe/<随机值>` 新建独立 fixture，保留结果，不递归删除。子进程用隐藏、非 shell 启动；持锁子进程在强杀实验后退出。可传一个结果 JSON 路径；默认输出到 evidence，重跑前若需保存旧结果应传另一路径。

2026-10-09：SDK 10.0.201 / .NET 10.0.5 / Windows build 22000 / E:，Release 构建零警告，实际运行通过 [28 项检查](evidence/2026-10-09-result.json)。`designBaseline=3d776b1` 是研究开始时的设计提交；新探针源码与这份证据同次提交，不把基线标为已包含探针。

| 被检验的机制 | 观察 |
| --- | --- |
| 无门 bootstrap | OpenOrCreate、ReadWrite、FileShare.None 建立 0B 文件，不需要格式门；第二次 bootstrap 不能绕过持有者 |
| 同/跨进程模式矩阵 | writer–writer、writer–reader、reader–writer 均为原生 sharing conflict；reader–reader 成功 |
| 强制终止 | writer 或 reader 子进程先排斥新 writer；kill 并确认退出后，writer 用原有 0B 文件正常重新取得资格 |
| 控制文件保持 | Windows 持锁时删除失败；缺锁只读 Open 不创建，1B 坏锁两个模式都拒绝且字节不变 |

这只取证当前 Windows/runtime/文件系统上的原语，没有复现完整 Create 锁前后竞争、fault/清理异常注入或恢复。实际 root identity、精确组件 spelling、ordinary/no-follow、Linux、data rename、断电或隔离包资格均未由此证明。

机制依据：[FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0)。[.NET v10.0.0 Unix handle](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs) 的 312–327 / 411–417 行显示自动锁会忽略部分失败且可禁用，因此 strict Linux 准入必须明确检查同一 fd 的原生锁成功；[flock](https://man7.org/linux/man-pages/man2/flock.2.html)提供共享/独占、nonblocking 与 fd 生命周期机制依据。这是源级论证，未声称 Linux 实测。
