# EventJournal Windows 平台验证

日期：2026-09-29。本文补充[有界日常 I/O 交付](bounded-online-io-delivery.md)与[旧布局升级交付](legacy-upgrade-delivery.md)的 Windows 证据。生产源码与包来源为 `5288bd55b4a5181942dadd76516f799002508144`；本次只新增验证脚本、两条测试和交付记录，没有修改生产实现。

## 环境与范围

从 WSL 调用 Windows 原生 `pwsh.exe` / `dotnet.exe`，实际工作目录为 `E:\repos\Atelia-org\atelia-storage`。Windows 11 build 22000，PowerShell 7.6.3，精确 SDK 10.0.201。构建与 .NET 验证全部串行。

- 原有全套测试运行于独立 SSD 的 `C:` NTFS 临时目录；没有禁用 Flush 或替换文件实现。
- 原生强杀矩阵与新增共享冲突测试分别运行于 `C:` NTFS、`E:` ReFS。
- 初次使用默认 `F:\TEMP`（Storage Spaces / NTFS）的 suite 因同步落盘耗时较高，由主线程验证 PID/父进程/命令后停止自己的测试进程树；保留在 `interrupted-f-temp/`，不计为通过。改到 C: 后从头执行完整 suite。
- 所有写入均为新建测试目录、固定旧 binary fixture 的副本或新本地包验证目录；没有修改 Galatea 数据、切换应用或升级其依赖。

## 源码与文件发布验证

原始 Release build 为 **45 条既有 XML 文档警告、0 error**。`5288bd5` 全套 **1034/1034** 通过：Data212、EventJournal173、Toolkit122、Primitives75、RBF405、SegmentStore47；runner 报告无失败或 skipped。Linux-only FIFO 测试在 Windows 内部直接返回，Linux 只读权限 helper 也不在 Windows 设置权限，不能据总数宣称这两项有 Windows 等价权限证据。

新增两条 `WindowsPublicationTests` 分别在 NTFS/ReFS 运行，共四次通过：

1. 持有 `active.segment` 且不允许 delete sharing，实际 rotation 替换失败，旧 locator 字节保留、空 next 留存、实例 fault；释放后严格重开报告 `NextSegmentPresent`。独立相邻 fixture 的公开轮转正常成功。
2. 持有 `catalog.snapshot` 且不允许 delete sharing，实际前置 checkpoint 替换失败，tag=`NotAttempted`、实例 fault、op-log/snapshot 字节不变。释放占用后严格重开仍看不到 tag，再以新 writer 在同目录执行同一 tag 操作成功，排除持久 ACL/路径错误。

这台 Windows 的 overwrite `File.Move` 在上述占用下返回 `UnauthorizedAccessException / 0x80070005`；独立原生最小复现也在两种文件系统得到该结果，释放同一句柄后相同替换成功。测试精确接受该组合或 `IOException / 0x80070020`，不接受任意 I/O 异常；观测 probe 只记阶段，不注入错误。原有 Windows-only tag target flush 共享冲突测试、坏尾/CRC/严格 EOF 与极限稀疏文件用例也已随完整 suite 执行。

## 真实进程中断

复现入口为 [Windows 中断脚本](../../eng/Test-WindowsInterruption.md)。每种文件系统执行 **22 个运行时阶段 + 7 个升级阶段 = 29** 个独立强杀，共 **58** 个。子进程到达精确 phase 后由父进程实际 `Process.Kill()`，等待退出，再启动独立 verifier；不是异常注入。

- append 校验 tail、sequence、Parent/CRC；tag 校验发布前后的可见性。
- rotation 四个 old locator + next 中间窗口必须按 `NextSegmentPresent` 拒绝；其余窗口的 active 段号精确匹配已发布状态。
- checkpoint 额外校验 snapshot boundary4/260、branchCount0/1，区分替换前后的快照；完整 codec 与 anchor 校验仍由普通 strict reopen 执行。
- upgrade 前六个窗口无完成 manifest；ManifestPublished 窗口即使调用者未收到 Created，仍必须满足 manifest 全量文件/目录/hash、事实 byte-exact、统一 v2 full audit，以及验证前后源/目标不变。

首次 NTFS 运行前28项通过，最后一项因验证脚本只取 pretty JSON 首行而失败；已改为严格解析完整 stdout 中的单个 JSON 对象，并补紧凑/多行及8类非法输入的纯 helper 回归。保留初次失败材料，最终两种文件系统各用新目录完整重跑。

这些证据关闭本次 Windows 文件替换、坏尾与进程中断门。它们不是硬件断电或目录项断电耐久证明，也不是 Windows ETW/syscall 性能测量；既有 Linux 成本矩阵仍单独保留。ReFS 本轮范围是中断与共享冲突矩阵，不冒称完整 suite 也在 ReFS 重跑。

## 五包隔离消费

在 Windows 干净 `5288bd5` 上用 `eng/Pack.ps1` 生成新候选 **`0.2.0-dev.20260929031204`**，没有覆盖既有 Linux 候选。随后 `eng/Test-Package.ps1 -AdditionalSegmentSmoke` 通过：

- EventJournal public PackageReference smoke 与直接 SegmentStore smoke，分别验证五包、四包资产闭包。
- 十个 feed 产物 SHA256、五包 private cache 字节 hash 与 manifest 一致。
- metadata、PDB、Source Link 映射及90个本地源码 checksum 通过。未下载远端 Source Link，未网络发布。

精确 manifest、逐项强杀结果、源码测试计数及工件 hash 见[机器可读验证记录](windows-platform-verification.json)。该 Windows 包与先前 Linux 包使用不同版本、不同来源提交；没有进行跨平台同版本包哈希一致性声明。

## 保留证据与复现

Windows 证据根：`E:\repos\Atelia-org\atelia-storage\artifacts\windows-validation-20260929`。

- `build.log`、`test.log`、`test-results/*.trx`：原始完整 suite。
- `extra-test-results/{ntfs,refs}` 及对应 `*-sharing.log`：新增两条测试的四次执行。
- `refs-extra/interruption-final/`：ReFS 完整中断矩阵；NTFS 对应 `C:\Users\gdtut\AppData\Local\Temp\atelia-windows-extra-20260929\interruption-final`。
- `feed/manifest.0.2.0-dev.20260929031204.json` 与 nupkg/snupkg：本地包候选。
- 独立消费目录：`E:\repos\Atelia-org\storage-windows-package-smoke-20260929031204`，保留逐步 logs、assets、private cache。`pack.log` 仅保存最终摘要，native pack 的详细输出在执行 transcript；不宣称 pack 文件日志完整。
- `superseded-sharing-initial/` 与初次 `interruption/`：测试假设/脚本解析修正前的失败记录，不计入最终通过。

```powershell
# 在 Windows 本地 checkout 中串行执行；选择新 SSD 临时目录。
$env:TEMP = Join-Path $env:LOCALAPPDATA 'Temp/atelia-validation-UNIQUE'
$env:TMP = $env:TEMP
New-Item -ItemType Directory $env:TEMP | Out-Null
dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false
dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false
./eng/Test-WindowsInterruption.Tests.ps1
# 构建两个 console 并分别选择 NTFS/ReFS 新输出，见中断脚本说明。
# 从干净已提交树另选新 Version，再运行 Pack / Test-Package。
```

本次平台验证没有改变真实消费者接入与应用数据切换的验收边界；两个真实数据集的 Linux 独立升级候选仍按原交付记录保留。
