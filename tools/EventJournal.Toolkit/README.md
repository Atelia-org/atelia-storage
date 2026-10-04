# EventJournal 离线 toolkit

main 保留本工具及其测试作为 **RBF1 旧栈的冻结参考**，维护与旧栈公开交付归 `RBF1` 分支。经 EventJournal/RbfSegmentStore 取得的 Rbf/Data/Primitives 固定为 `[0.2.0-rbf1-preview.1]`，不使用 main RBF3 底座；它不是 FrameStore/VersionStore 的维护工具。

本轮引用拆分为 **Implementing，实施中，待验收**，实际构建、测试与 assets 见[过渡方案](../../docs/rbf1-reference-transition.md)。先按[根入口](../../README.md#构建与主线包验证)串行 Release build/test，再运行工具；main 包验证只覆盖三个底层包，不把本工具打包或发布。

非打包工具，仅接受停写 journal 或稳定副本。扫描事实文件、全部 event/Parent/physical sequence/payload codec、ref-op-log、所有 ref move/CAS/targets/tag，以及目录库存和索引；不会调用日常 EventJournal/RefMoveStore 打开入口。

```bash
dotnet run --project tools/EventJournal.Toolkit -c Release --no-build -- audit /path/to/journal
dotnet run --project tools/EventJournal.Toolkit -c Release --no-build -- audit /path/to/journal --report /outside/new-report.json
dotnet run --project tools/EventJournal.Toolkit -c Release --no-build -- rebuild-indexes /path/to/journal --output /outside/new-candidate
```

`--report` 和 `--output` 均为 source 外尚不存在的路径，父目录必须存在，不支持 symlink/reparse 路径。源只读；重建仅生成 `candidate/` 下 locator/catalog，不安装、不修改业务事实。拒绝坏事实和多义发布边界；根格式未知/缺失不能重建。

报告/manifest JSON 合同见 [冻结附件](../../docs/EventJournal/bounded-online-io-contracts.md#5-toolkit接口与候选输出)。退出码：0=完整且源事实/索引一致；2=明确源事实/索引问题或格式不支持；3=I/O、取消或判定不完整；64=CLI参数错误。健康事实但源缺索引时，成功生成候选仍返回2；候选可用性由最后发布的 `manifest.json` 的 `completed=true` 及完整文件清单/hash证明，不能只看退出码。未完成产物永远不可安装。

候选记录来源全部事实（含orphan与format）及对应索引存在/缺失状态；扫描前后、生成前后重新枚举并核对路径/长度/SHA256，防止忽略新增或删除。它不提供在线一致快照、apply或repair-tail，也不证明消费者领域事件语义。普通`audit/rebuild-indexes`仍要求v2布局，不自动识别旧库。

## v1 → v2 独立副本升级

```bash
dotnet run --project tools/EventJournal.Toolkit -c Release --no-build -- upgrade-v1tov2 /old/journal --profile legacy-bb7c4fb --check-only
dotnet run --project tools/EventJournal.Toolkit -c Release --no-build -- upgrade-v1tov2 /old/journal --profile legacy-bb7c4fb --output /outside/new-upgrade-bundle
```

`v1/v2`指journal整体布局，不是EventFrame header版本，也不是 RBF1 → RBF3 转写。唯一profile固定为`bb7c4fb3eb6477783c70ee61bc62b832be195d07`对应writer的旧布局；它是调用方的来源声明，不是从磁盘鉴定出的commit。原升级实现的 Linux 验证与[Windows平台门](../../docs/EventJournal/windows-platform-delivery.md)均已通过，详见[设计](../../docs/EventJournal/legacy-upgrade-design.md)与[交付记录](../../docs/EventJournal/legacy-upgrade-delivery.md)；历史结果保留原身份，不作为本轮引用拆分结果。

输入必须停写或为稳定副本。命令逐字节复制全部RBF事实至`<output>/journal/`，生成v2 locator、EOF catalog和format，再执行完整audit与日常只读核对，最后发布`<output>/manifest.json`。原EventAddress、RefId、Parent、reflog、tags及orphan保留；源不修改，旧`cache/forward-plans/v1/`记录后不复制。目标必须尚不存在，拒绝源内输出、symlink/reparse、特殊文件、未知库存及任何混合v2 metadata。

报告状态/退出码：`Eligible/0`仅表示check-only通过；`Created/0`表示候选完成；`Rejected/2`表示确定不兼容或坏事实；`Incomplete/3`表示发布前I/O、取消或来源/目标变化；参数错误为64。manifest最终rename是完成点，之后的取消或终端输出失败不会撤销候选。若stdout传输失败，CLI返回3但不伪造Incomplete报告；应核对已经发布的manifest与文件hash。

坏尾、缺段、坏CRC/Parent/ref、重复sequence、Close无Archive都拒绝升级；不会truncate、补业务事实或选择更早head。旧最高合法空段按旧发现规则保留，不放松v2缺locator时的歧义拒绝。Archive/Close的跨帧时间戳可以不同，身份、sequence、reason和归属仍严格校验。

没有成功manifest的输出不可用于切换；失败产物保留，重试选择新目录。工具不提供resume、原地替换或消费者切换。manifest是候选验证时的记录，候选正常投入使用后append会改变hash。

Galatea等应用若把`control/derived`与`events/refs`放在同一根，不能直接当作本工具的纯EventJournal输入。应保留完整应用快照，再从稳定快照独立复制`events/refs`用于storage验证；这些应用sidecar并非可丢缓存，本命令不迁移它们。
