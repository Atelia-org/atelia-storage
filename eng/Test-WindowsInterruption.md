# Windows 原生进程中断验证

`Test-WindowsInterruption.ps1` 在原生 Windows PowerShell 7 中串行运行现有两个独立 console。脚本不构建，不接受真实 journal 输入，只创建新临时 fixture；legacy 来源是仓内有来源/hash证据的固定旧 binary fixture。既有 Linux Python 入口保留。

先由同一线程串行准备 Release binaries，再运行两种文件系统的独立输出。不要和 suite、pack 或其他 .NET 验证并行：

```powershell
# Windows PowerShell 7 / pwsh.exe；不是 Windows PowerShell 5.1。
dotnet build tests/EventJournal.Validation/EventJournal.Validation.csproj -c Release -m:1 -nr:false
dotnet build eng/LegacyUpgradeValidation/LegacyUpgradeValidation.csproj -c Release -m:1 -nr:false

# NTFS 与 ReFS 分别选尚不存在的目录；按机器实际驱动器修改。
pwsh -NoProfile -File ./eng/Test-WindowsInterruption.ps1 `
  -OutputDirectory 'C:\Users\gdtut\AppData\Local\Temp\atelia-interruption-ntfs-UNIQUE'
pwsh -NoProfile -File ./eng/Test-WindowsInterruption.ps1 `
  -OutputDirectory 'E:\repos\Atelia-org\atelia-storage\artifacts\interruption-refs-UNIQUE'
```

默认 `-Matrix All` 覆盖 **22 bounded + 7 legacy = 29** 个真实强杀 case；也可显式选 `-Matrix Bounded` 或 `-Matrix Legacy`。可选 `-DotnetPath` 指定原生 Windows dotnet 的绝对路径；参数通过 `ProcessStartInfo.ArgumentList` 传入，支持空格和 Unicode。`-LegacyFixture` 默认 complex，也接受 empty、events-only、empty-active。

输出目录必须尚不存在，位于仓外或仓内 ignored `artifacts/` 的新子目录。源码目录由 `$PSScriptRoot/..` 获取，没有 WSL `/mnt/` 或 Linux binary 路径依赖。`environment.json` 记录实际 Windows/PowerShell、输出绝对路径、drive、文件系统类型和 harness binary SHA256；NTFS 与 ReFS 的证据互不替代，存储介质也会影响耗时。

子进程 stdout 必须输出 `ReadyToKill` 且 phase 精确匹配。父进程确认它仍在运行后调用 **`System.Diagnostics.Process.Kill()`**，等候退出，再启动独立只读 verifier。它是 Windows 原生终止进程证据，不是模拟异常。Windows exit code 原值与 Kill 调用分别记录，不套用 Linux `SIGKILL=-9`。默认 readiness 60 秒、退出 15 秒、verification 120 秒；对应参数可增大，但超时或自然提前退出一律失败，不能算作通过。

Bounded matrix 沿用 Linux oracle：

- rotation 在 NextFlush / LocatorCreate / LocatorFlush / LocatorReplace 时严格拒绝，ReasonCode=`NextSegmentPresent`；OldFlush / NextCreate 返回 active1，LocatorPublished / OldDispose 返回 active2。
- append 在 BeforeAppend 时 tail96、sequence1、next2；AfterAppend / AfterDurableFlush 时 tail188、sequence2、next3。现有 verifier 使用完整 `ReadEvent` 校验 CRC 和 Parent。
- tag 在 BeforeTargetFlush / BeforeAppend 不可见，在 AfterAppend / AfterDurableFlush 可见。
- checkpoint 全部严格重开；AfterReplace / BeforeInstall 的 snapshot boundary260、branchCount1，其余阶段仍为 boundary4、branchCount0；全部 tagCount0、真实 op-log length260。脚本仅读取固定 header 见证，完整 snapshot CRC、anchor 与 branch 身份仍由既有生产 codec 的 strict reopen 校验。
- verification 前后全部文件长度/hash与完整目录清单不变，包含临时 metadata 残留。

Legacy matrix 在 CopyChunkWritten / LocatorWritten / CatalogWritten / FormatWritten / TargetAudited / BeforeManifestPublish / ManifestPublished 各强杀一次。每次从固定 fixture 独立物化，按 `provenance.fixtureDirectories` 恢复 Git 不保存的空 roots，并核对固定来源文件 hash。最终 manifest rename 前六个阶段均无成功 manifest；ManifestPublished 强杀后，即使没有收到 Created，manifest 必须完整且：

- 全部 validation=Passed，fixed profile/baseline/layout 正确。
- source/target 完整目录清单及全部文件长度/hash与 manifest 完全一致；原事实 byte-exact，cache 排除。
- 新进程统一 v2 full audit 为 Completed/Healthy/Consistent，audit 前后来源和目标 inventory 不变。

每个 case 保留 `child.stdout.log`、`child.stderr.log`、`termination.json`、独立 verifier/audit stdout/stderr，以及 source inventory 前后证据。`results.jsonl` 逐条 flush；只有全部选定 case 通过才写 `summary.json`。失败写 `failure.json` 并保留不完整目录，重跑必须使用另一个新路径，不执行 resume、repair 或自动清理。

此入口检验 Windows 句柄关闭、文件替换和完成点的进程中断可见性。它不添加目录 fsync 或断电保证，也不替代普通 suite、包消费验证或真实应用切换验收。
