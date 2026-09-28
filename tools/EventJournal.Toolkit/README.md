# EventJournal 离线 toolkit

非打包工具，仅接受停写 journal 或稳定副本。扫描事实文件、全部 event/Parent/physical sequence/payload codec、ref-op-log、所有 ref move/CAS/targets/tag，以及目录库存和索引；不会调用日常 EventJournal/RefMoveStore 打开入口。

```bash
dotnet run --project tools/EventJournal.Toolkit -c Release -- audit /path/to/journal
dotnet run --project tools/EventJournal.Toolkit -c Release -- audit /path/to/journal --report /outside/new-report.json
dotnet run --project tools/EventJournal.Toolkit -c Release -- rebuild-indexes /path/to/journal --output /outside/new-candidate
```

`--report` 和 `--output` 均为 source 外尚不存在的路径，父目录必须存在，不支持 symlink/reparse 路径。源只读；重建仅生成 `candidate/` 下 locator/catalog，不安装、不修改业务事实。拒绝坏事实和多义发布边界；根格式未知/缺失不能重建。

报告/manifest JSON 合同见 [冻结附件](../../docs/EventJournal/bounded-online-io-contracts.md#5-toolkit接口与候选输出)。退出码：0=完整且源事实/索引一致；2=明确源事实/索引问题或格式不支持；3=I/O、取消或判定不完整；64=CLI参数错误。健康事实但源缺索引时，成功生成候选仍返回2；候选可用性由最后发布的 `manifest.json` 的 `completed=true` 及完整文件清单/hash证明，不能只看退出码。未完成产物永远不可安装。

候选记录来源全部事实（含orphan与format）及对应索引存在/缺失状态；扫描前后、生成前后重新枚举并核对路径/长度/SHA256，防止忽略新增或删除。它不提供在线一致快照、apply、repair-tail或旧格式迁移，也不证明消费者领域事件语义。
