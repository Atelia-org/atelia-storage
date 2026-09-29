# Legacy upgrade 验证进程

此 console 非 pack，不进入五库。独立新进程仅加载新 toolkit；旧 binary fixture 通过 `../Generate-LegacyFixtures.py` 生成。所有写入均使用新临时输出。

```bash
dotnet build eng/LegacyUpgradeValidation/LegacyUpgradeValidation.csproj -c Release -m:1 -nr:false
python3 -B eng/Test-LegacyUpgrade-Kill.py --output /tmp/legacy-upgrade-kill-UNIQUE
# 任意已通过 audit 的 v2 候选，复制到新位置后执行少量公开写入与全 audit。
dotnet eng/LegacyUpgradeValidation/bin/Release/net10.0/Atelia.EventJournal.LegacyUpgradeValidation.dll \
  continue-copy /candidate/upgrade-bundle/journal /tmp/continued-copy-UNIQUE
```

`continue-copy` 不向交付候选写入；独立复制后验证事实 hash，使用低 threshold，执行 create、append/advance、move、tag、Archive 和 rotation，严格重开并完整 audit，最后核对来源文件 hash 不变。不在此模式强制checkpoint；`LegacyCrossVersionTests` 的一次可丢弃 tmpfs 副本用 1025 个 public tag 操作触发真实checkpoint。

SIGKILL脚本覆盖 copy chunk、locator 后、catalog 后、format 后、target audit 后、manifest rename 前后。测试进程通过反射设置现有 toolkit internal Probe，在精确阶段报告 ReadyToKill 后暂停；这不改生产写序，也不抛入异常。最终 rename 前任何 kill 均无 manifest；rename 后即使调用者未收到 Created，完整 manifest 与文件 hash、普通 v2 audit 仍须通过。每个case重建独立的旧fixture副本，按provenance目录清单恢复Git不能保存的空目录，核对来源文件/目录不变。此Python入口只证明Linux进程中断；Windows原生入口见[独立脚本](../Test-WindowsInterruption.md)，NTFS/ReFS实证见[平台交付](../../docs/EventJournal/windows-platform-delivery.md)。两者都没有目录fsync或断电保证。
