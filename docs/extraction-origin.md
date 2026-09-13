# atelia-storage 提取来源

2026-09-13 从 [Atelia-org/atelia](https://github.com/Atelia-org/atelia) 提取。
本记录只描述迁移来源；长期使用和开发入口见仓库根 README 与 AGENTS.md。

- 原仓基准 commit：`6242f3bb6b631079d2513288ca54d823f630802f`。
- 提取后的初始 HEAD：`8c43265b4d52a3f61d30dc3e7d6ac3c736ac8726`，保留 259 个相关提交。
- 方法：在独立的 `git clone --no-local --single-branch --no-tags` 中运行 `git-filter-repo --paths-from-file`，没有修改原仓历史。
- 工具入口来自官方 [git-filter-repo](https://github.com/newren/git-filter-repo)，工具版本标识 `31ebad4c8fb3`；脚本 SHA256 为 `39d35fb2c35637a9d555353b6a8b53a223a227d3beb61a0ac7e1180bfa5572f1`。
- 本次历史提取成功，没有采用快照降级。只提取当时检出的分支，不复制原仓其他分支或 tags。
- 原仓基准提交仅含提取范围外的变化，因此在 commit-map 中对应全零；提取后 HEAD 对应原仓 `021c374d466870a3f1833e2df9771f2588fe9f54`。这不表示遗漏基准源码：以下逐文件比对使用完整原仓基准。

## 文件与历史清单

[提取路径](extraction-paths.txt) 是实际传给工具的完整筛选清单；
[commit-map](extraction-commit-map.txt) 保存工具产生的原提交到新提交映射，全零表示提交被过滤或裁剪。

当前文件包括五个生产项目、对应五个测试项目、两个 benchmark 项目和四组文档目录，共 172 个文件。
另保留 `LICENSE`、`.editorconfig`、`.gitattributes` 和现行规范直接引用的 `docs/spec-conventions.md`。
[源文件清单](extraction-source-manifest.json) 记录这 176 个文件在任何迁移适配前的 SHA256；提取后逐项一致，其中 80 个生产 `.cs` 文件没有修改。
后续构建、包配置和文档改动是提取后的正常提交，不反写这份历史基准。

额外纳入以下已经消失的旧路径，以连接现有文件的演进：

| 旧路径 | 原仓迁移证据 |
| --- | --- |
| `src/Atelia.Memory/` | `e231031a7d23023375f49d0ea01cf56d73d17569` 将其改名为 Atelia.Data |
| `src/Atelia.Data/`、`tests/Atelia.Data.Tests/` | `c3ad86a5245f6c3ea2407470cd9c2629c38d8ca2` 移入当前 Data 路径 |
| `docs/AteliaResult-Specification.md` | `6fcb67c4689ecb11a9bc92a97b4ea7fe1e004512` 移入 Primitives 文档 |
| `docs/StateJournal/rbf-format.md`、`docs/StateJournal/rbf-interface.md` | `ae0762505532b6ab41a3fed99624c75548aced17` 移入 Rbf 文档 |
| `docs/StateJournal/rbf-test-vectors.md` | `886769735d93622379f06170014da222447fb79a` 移入 Rbf 文档 |

没有迁入 StateJournal 业务目录、`example/RenderPrototype` 的项目模板来源，或 2025-12-29 后废弃 RBF 实现所在的 archive。
被废弃 RBF 实现在迁入 archive 前位于已选路径中的历史仍可查询；不声称复制了 archive 后续演进。
原仓历史仍是未选路径与旧业务资料的来源。

## 构建边界

原有程序集、namespace、PackageId、IVT 和项目间运行时引用保持。
新仓构建不引用 `Analyzers.Style` 或 `Analyzers.Style.CodeFixes`；原仓仅用于 Analyzer 自分析的 `Directory.Build.targets` 未迁入。
没有发现需要另行提取的链接源码、项目 Import、嵌入资源或外部测试样本：当前测试向量写在代码中，文件测试在临时目录生成数据。

新 solution、SDK 选择和根构建/包配置属于提取后的适配产物。
构建、测试和包验证的结果由迁移实施证据记录；本来源记录本身不构成运行验证成功证明。

历史聊天与完成记录中的旧机器路径保持其当时含义，不保证在新仓可访问。
现行指南和格式/API 入口的链接单独维护，不为历史聊天链接迁入无关业务资料。
