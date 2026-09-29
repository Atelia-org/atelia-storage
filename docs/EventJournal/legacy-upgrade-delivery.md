# upgrade-v1tov2 实施与验证记录

日期：2026-09-29。Linux 实施与验证通过；[Windows 后补门](windows-platform-delivery.md)通过（原生 suite、NTFS/ReFS create-only/rename 与进程中断）。设计见[升级方案](legacy-upgrade-design.md)，命令见[toolkit 指南](../../tools/EventJournal.Toolkit/README.md)。本轮只制作独立候选，未切换 Galatea、修改源 repo 或升级应用依赖。

## 实施范围

- 新增 `LegacyUpgrade.Check/Upgrade`、`UpgradeReport/UpgradeManifest`、严格 legacy 库存及 CLI `upgrade-v1tov2`。唯一 profile 为 `legacy-bb7c4fb`，没有自动版本猜测。
- 全部事实保留原路径和字节，只新建 locator、EOF catalog 与 format；完整源/目标文件和目录清单、hash、full audit 与 daily check 通过后，最后发布 manifest。
- 普通 v2 audit/rebuild 继续拒绝无格式门及多义 locator；共享事实规则仅取消 Archive/Close 时间强等。sequence、ReasonKind、身份与归属检查保持。
- 拒绝混合 metadata、未知库存、坏事实、特殊文件和 reparse；检测来源/输出/manifest 临时文件变化。取消及 I/O 不伪造成功；发布后的 stdout 失败不会撤销已有 manifest。
- 没有新增生产包或修改生产 runtime 源码；未额外打包五包候选。新工具仍通过源码项目运行。

## 跨版本及故障证据

旧源码固定为 `bb7c4fb3eb6477783c70ee61bc62b832be195d07`，完整导出后独立构建旧公开 API generator；旧生产源码 hash 前后不变。四组小型固定样本为 empty、events-only、complex、empty-active，共约22 KiB，见[fixture说明](../../tests/EventJournal.Toolkit.Tests/LegacyFixtures/README.md)及[来源清单](../../tests/EventJournal.Toolkit.Tests/LegacyFixtures/provenance.json)。Git 不保存空目录，测试按来源清单恢复，不在journal内添加占位文件。

真实旧归档样本的 Close 与 Archive 时间相差52ms，未重写字节。复杂样本含三种payload codec、Hint、fork、归档后同名新RefId、unborn、同名tag、CAS orphan及旧磁盘cache；另有旧SegmentStore公开API生成的合法空最高段。

最终 Release solution build：**0 warnings / 0 errors**；全套 **1034/1034**，无失败/跳过：Data212、EventJournal173、Toolkit122、Primitives75、RBF405、SegmentStore47。Toolkit比基线增加66个测试，包括六个跨版本测试及在可丢弃tmpfs副本上以1025次公开CreateTag触发checkpoint。功能测试临时目录为明确tmpfs，不作为磁盘耐久证据。

```bash
TMPDIR=/dev/shm/atelia-upgrade-final-tests dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false
TMPDIR=/dev/shm/atelia-upgrade-final-tests dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false
dotnet build eng/LegacyUpgradeValidation/LegacyUpgradeValidation.csproj -c Release -m:1 -nr:false
python3 eng/Test-LegacyUpgrade-Kill.py --output /tmp/atelia-legacy-upgrade-kill-u04
```

七个真实SIGKILL阶段全部通过：CopyChunkWritten、LocatorWritten、CatalogWritten、FormatWritten、TargetAudited、BeforeManifestPublish、ManifestPublished。前六个没有完成manifest；最后一个虽然终端没收到Created，仍有完整manifest、精确目录/文件hash及成功的v2 full audit。所有源文件与目录不变。实际文件系统为ext4，不能把进程退出证据称为断电证明。

本地日志：`/tmp/u01-u03-{build,test}.log`、`/tmp/u04-{console-build,kill,continued}.log`、`/tmp/upgrade-final-{build,test}.log`；中断逐项记录为 `/tmp/atelia-legacy-upgrade-kill-u04/results.jsonl`。独立强审发现的源路径漂移与target audit取消分类问题已修复并有精确回归；没有遗留阻塞项。

## 两个真实数据集

原路径为 `/repos/Atelia-org/atelia/prototypes/Galatea/.atelia/galatea/sessions/` 下两个repo。各自根含 `events/refs/control/derived`：直接提交整个应用根给命令会按设计拒绝 `UnknownInventoryEntry`。

本次先记录完整清单/hash并复制完整快照，再从稳定快照独立复制 `events/refs` 形成storage输入。原始目录、完整快照、纯storage输入、升级bundle、续写副本分别保留。`control/derived` 是应用数据，完整保留在快照中，没有迁移、删弃或解释其内容。

| 数据集 | 事实文件 / bytes | events / ref moves | 结果 |
| --- | ---: | ---: | --- |
| cyber-session-journal-recap-grid | 3 / 7,131,352 | 870 / 879 | Eligible、Created、Healthy/Consistent；无findings |
| gpt | 3 / 10,564,956 | 1264 / 1271 | Eligible、Created、Healthy/Consistent；无findings |

两者事实文件路径/长度/SHA256完全一致，exact地址与refs由共享audit及daily检查验证。各自第二个可丢弃副本成功执行public create/append/advance/move/tag/archive、低阈值rotation、strict reopen与完整audit；交付候选本身的hash仍匹配manifest。

gpt的storage输入在私有mount namespace内bind remount为真正只读，root touch得到`Read-only file system`，升级仍成功。namespace退出后未留下全局挂载。原始两个repo各14个文件（包括所有应用sidecar）及目录清单，在整个验证前后保持不变。`/proc`检查未发现源文件句柄，仅作为当前环境的停写佐证，不声称证明在线一致快照或SQLite应用语义。

本地证据根（被Git忽略，不提交真实数据）：

```text
artifacts/upgrade-v1tov2-validation-20260929T011345Z-76eaf78c/
  inputs.json
  verification-summary.json
  run-real-validation.py
  <repo-name>/
    provenance.json
    source-snapshot/        # 完整原应用快照，含control/derived
    storage-input/          # 只含events/refs的稳定独立输入
    upgrade-bundle/
      manifest.json
      journal/             # 验证完成的v2 storage候选
    continued-copy/        # 已追加测试数据，不用于部署
    check.json
    upgrade.json
    target-audit.json
    continued-copy.json
```

候选的实际使用目录是`upgrade-bundle/journal/`，不是包含manifest的外层bundle，也不是continued-copy。**这些是EventJournal候选，尚不是完整可运行的Galatea升级实例。** 消费者版本适配、领域selected-chain/sidecar验证及应用切换仍是后续工作。Windows create-only/rename及中断测试已在NTFS/ReFS通过，见[平台记录](windows-platform-delivery.md)；真实数据集仍仅有本文列出的Linux验证范围。
