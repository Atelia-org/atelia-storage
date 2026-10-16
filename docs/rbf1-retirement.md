# RBF1 从主线退出

日期：2026-10-16。源码清理、独立复核与 Windows Release 验证已通过；本地包验证见下文。

## 决策与恢复入口

主线仅支持 RBF3 / FrameStore / HeadStore。用户明确选择同时移除底层 RBF1 只读兼容；这是有意的 API 与读取兼容性变更，不是格式迁移。现存 RBF3 文件的 wire-format 不变，RBF1 文件必须由 RBF1 维护线或对应已发布包读取。

旧栈由 Git 保存，不在主线复制另一份 archive 源码：

| 用途 | 固定身份 |
| --- | --- |
| 清理前完整主线快照 | [`043f77aa68700568499ceb965c049f8362e4253f`](https://github.com/Atelia-org/atelia-storage/tree/043f77aa68700568499ceb965c049f8362e4253f)；本地 annotated tag `archive/rbf1-main-2026-10-16` |
| RBF1 维护分支 | [`RBF1`](https://github.com/Atelia-org/atelia-storage/tree/RBF1)；本轮 `git ls-remote` 核实为 `3ccb66ab46c317e9a189e19193cd72cf2547a5c2` |
| 已发布 RBF1 五包 | `0.2.0-rbf1-preview.1`，来源 [`3e9554e2ea70f769607e80b3fc11a85506050533`](https://github.com/Atelia-org/atelia-storage/tree/3e9554e2ea70f769607e80b3fc11a85506050533) |

RBF1 分支与清理前快照并非完全相同：主线后来增加了 Windows publication 验收，以及冻结旧包依赖的构建和指南调整。完整恢复这些材料应使用上表清理前快照；维护旧栈则使用 RBF1 分支。归档标签仅在本地创建，本轮不推送或公开发布。

可在独立工作树查阅完整旧快照：

```powershell
git worktree add --detach ../atelia-storage-rbf1-archive 043f77aa68700568499ceb965c049f8362e4253f
```

## 清理范围

- 删除 `src/EventJournal`、`src/RbfSegmentStore`、对应测试、validation、toolkit 与两个旧包 smoke 示例。
- 删除旧包依赖 props、assets guard、旧目录升级/fixture 生成、旧 Windows interruption 与 bounded-online-I/O 验证工具；同步 solution、MSBuild 和 CI/publish 入口。
- 删除旧 RBF1 append benchmark、RBF1 专属恢复/读取实现与测试；将通用低层 reader 测试迁至 RBF3，保留旧 Header 拒绝且不改写文件的负例。
- 删除纯旧栈设计、交付文档与过期实验；共享基础库、当前新栈和 RBF3 实验保留。混合历史验收与提取来源保持原 revision 身份，其旧路径按固定 Git 链接查阅。
- `RbfFormat.Rbf1`、仅供 RBF1 的 `RbfRecovery` 救援 API 与 `CompletedFence` / `CompletedTombstone` 恢复动作退出公面。当前 RBF3 enum 数值与格式保持。

主线六包清单仍由 `eng/Pack.ps1` 唯一维护；旧包名的拒绝测试仍有防止交付污染的意义。历史验收数字不是此次验收结果，参见[原过渡记录](rbf1-reference-transition.md)。

## 本轮验证

- Windows / .NET SDK `10.0.201`：`dotnet build Atelia.Storage.slnx -c Release`，0 warnings、0 errors。
- 匹配 Release 的完整 `dotnet test Atelia.Storage.slnx -c Release --no-build`：3338 passed、10 skipped、0 failed。分项：Primitives 75、Data 339、Binary 214、Rbf 856、FrameStore 438 + 4 skipped、HeadStore 1416 + 6 skipped。跳过项受 Linux 或文件系统平台条件限制，不计作通过。
- 通用低层测试迁移期间发现一条旧 padding 断言沿用 RBF1 的宽松结果；现按 RBF3 非零 padding 拒绝合同精确断言 `RbfFramingError`，没有放宽生产实现。
- 14 个 solution 项目的实际 `project.assets.json` 无 RBF1 包或旧栈项目；所有 Atelia 存储依赖均为 main project。
- 主线程检查跨包边界并抽查实现与测试；源码、测试、文档工作包相互复核，另由独立 agent 审阅最终运行时和文档删除范围，无剩余阻塞问题。最后清理了一条旧 writer 独占的无引用常量和一处旧布局注释。
- Markdown 相对链接扫描未发现本轮删除造成的新断链；12 个不同固定归档目标已用 Git 对象验证存在。`git diff --check` 通过。

源码测试日志和 TRX 保留在忽略目录 `artifacts/rbf1-retirement-*`。当前结果只覆盖此 Windows 运行；不表示 Linux、设备断电、真实 RBF1 数据迁移或公开发布已经验收。

本地六包候选、四个隔离消费者与 RBF3 生产探针的验证待在干净源码提交上执行并追加实际身份。
