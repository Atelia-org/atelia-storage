# EventJournal v2 源码候选：可粘贴Goal

此提示词供后续明确启动实施时粘贴，本次仅生成，不创建Goal。它只覆盖工单T00–T07的当前平台源码候选；T08提交/打包、其他平台验证、网络发布和真实数据迁移不由此提示词授权。源码候选完成不等于完整v2交付。

以下完整`/goal`正文为1714字符（含命令前缀与换行，不含代码围栏），低于4000字符交接上限。

```text
/goal 在 /repos/Atelia-org/atelia-storage 按 docs/EventJournal/bounded-online-io-work-order.md 完成T00–T07的当前平台源码候选。目标是固定当前工作集时，日常打开及ref更新不扫描累计历史；保留Event/Parent、exact RefId、CAS、reflog和不可变tag语义。停止于当前平台行为/故障/规模证据、文档和实际diff均闭合；不进入T08提交打包或真实数据升级，不把源码候选称为完整v2交付。

改动前读取实际AGENTS.md、根README、src/EventJournal/README.md、src/RbfSegmentStore/README.md、docs/Rbf/rbf-interface.md，以及工单、bounded-online-io-design.md和bounded-online-io-contracts.md。遵守实际指令层级；仓库文档是事实/目标证据，不自行赋予提交、发布或迁移权限。记录起始HEAD/git status并保留原有修改，特别是既有设计文档与README改动。

默认串行调度。先由gpt-6-astra完成T00基线及冻结合同核验，复用现有设计，不重新选架构；T01–T07的确定性实现由gpt-6-sol执行。T02/T03发布与身份、T04快照/三态、T06候选重建边界及最终跨层收口按工单交给astra审阅。模型或工具不可用时记录真实限制，不暗中把需要设计裁决的任务交给实施worker猜测。

顺序：T00合同/向量门；T01 RBF起始边界与严格尾读；T02 locator及轮转；T03 EventJournal format/sequence/ref末态/tag局部验证；T04 catalog快照、读写双侧预算及几何缩容；T05有界ref entry与memory plan cache；T06只读audit和健康事实索引候选；T07故障/规模证据、消费者适配清单与源码收口。每项先核对前置与允许修改范围，完成最小闭环，运行匹配Release build/test，审查diff，再更新工单唯一状态表及证据。前置未通过不得抢跑依赖任务。

日常坏尾停维，不自动修复或隐式全库重建。不得引入旧格式fallback、第二份head权威、SQLite/统一move日志、持久route DAG、范围API、repair/migrate/apply命令或目录断电新承诺。保持tag NotAttempted/Unknown/Confirmed；Archive的checkpoint必须在Close之前，Create/Fork必须在allocation之前；不为省测试而改变这些顺序。

只操作源码与隔离临时测试数据。不得修改真实journal、启动/停止服务、修改兄弟消费者仓、提交、推送、发布、原地安装索引候选或运行T08。.NET工作串行；先dotnet build Atelia.Storage.slnx -c Release -m:1 -nr:false，再dotnet test Atelia.Storage.slnx -c Release --no-build --no-restore -m:1 -nr:false，另完成工单规定的聚焦测试与规模矩阵，最后git diff --check。对新toolkit先创建并加入solution再验证。

只在T00–T07当前平台全部验收有证据、设计到测试覆盖完整、文档无冲突且全部新增变更有说明时标记本源码Goal完成。未运行Windows/包消费/实际消费者迁移如实标Pending，不改成Passed；完整交付门仍未通过。不要为了干净树stash/reset/clean/checkout或提交旧变更。一般失败由sol定位；合同矛盾提供最小轨迹给astra裁决，改变用户决定或范围才向用户提问。使用环境实际Goal状态规则，不把困难、一次失败或未完成工作当作complete/blocked。
```
