# FrameStore / HeadStore 跨层决策与交付

2026-10-16 活跃入口已同步 HeadStore 名称；历史验收与候选仍保持 VersionStore 的来源身份。本轮重命名验证见[重命名记录](../HeadStore/15-headstore-renaming-plan.md)，HeadSetTag格式2行为替换已实施，实际新增源码/进程/包资格见[16验收记录](../HeadStore/16-headset-tags-acceptance.md)，Windows源码、进程与本地包消费均已通过。文档目录已改为 `docs/FrameStore-HeadStore/` 与 `docs/HeadStore/`，重命名记录及 HeadSetTag 专项调整为 15、16 号；其余 basename 与既有 Clause-ID 保持。

2026-10-11 文档按模块归类。查某个库的设计或继续实施，从对应模块入口开始；本目录保留跨层决策、组合验收和包交付。

| 任务 | 阅读入口 | 核心合同与当前续接 |
| --- | --- | --- |
| FrameStore：帧分配、地址、构建、耐久与目录生命周期 | [FrameStore 文档](../FrameStore/README.md) | [S2 核心](../FrameStore/02-framestore-core.md)、[S3 交错构建](../FrameStore/03-framestore-interleaved-builders-and-durability.md)、[收尾计划](../FrameStore/02-framestore-completion-plan.md) |
| HeadStore：Head 发布、历史/fork、tag 与 CU | [HeadStore 文档](../HeadStore/README.md) | [S4 发布](../HeadStore/04-versionstore-publication.md)、[S5 历史/fork](../HeadStore/05-versionstore-names-and-indexes.md)、[实施计划](../HeadStore/12-versionstore-implementation-plan.md) |
| RBF：物理帧、指定 ticket、恢复与提前地址 | [RBF 文档](../Rbf/README.md) | [接口](../Rbf/rbf-interface.md)、[格式](../Rbf/rbf-format.md)、[S1 已知尺寸追加](../Rbf/01-rbf-sized-append.md) |

## 共同文档的职责

| 文档 | 唯一负责的内容 |
| --- | --- |
| [00 总体边界与决策](00-architecture-decisions.md) | 各层职责、已确认方向、依赖与恢复模型；具体 API/wire 由所属模块规范定义 |
| [06 集成与交付](06-integration-and-delivery.md) | public 跨层组合、进程中断、包消费及真实消费者的分层资格 |
| [13 本地包交付](13-local-package-delivery.md) | 既有六包候选的不可变版本、来源、manifest 与隔离消费证据；该候选不含后来的 CU 增量 |
| [14 下游公面完善计划](14-downstream-api-refinement-plan.md) | 获准施工队列已收束；已完成公面归入模块规范，保留延后事项的重访条件 |
| [14 下游公面增量验收](14-downstream-api-refinement-acceptance.md) | 精确读取、用户长度/切片、组合 guard 与恢复报告的分片源码及实际运行证据；不改变旧候选资格 |
| [设计问题导航](design-questions.md) | 设计研究边界与已关闭事项；当前没有待定设计项，不作为额外规范输入 |
| [reviews/](reviews/) | 跨层讨论与历史审阅；当时结论、源码与检查数字保持原身份 |

FrameStore S2/S3 的[源码验收](../FrameStore/02-framestore-final-acceptance.md)、HeadStore 既有范围的[源码验收](../HeadStore/12-versionstore-final-acceptance.md)及新增 CU 的[独立验收](../HeadStore/07-versionstore-cross-ref-conditional-update-acceptance.md)均为 Accepted，范围各自限定。源码、平台、具名进程窗口、包及消费者资格分别记录；当前文件不能替代对应验收。

## 阅读与维护规则

- 先读仓库根 [README](../../README.md)，按任务进入模块 README，再读该模块的核心合同与当前计划。修改哪个行为，就修改其唯一权威；其他篇只引用。
- 规范遵循[规范约定](../spec-conventions.md)。S0–S6 阶段身份与既有 Clause-ID 保持；重命名记录及 HeadSetTag 专项使用 15、16 号，其余文件 basename 保持；专项编号不新增施工阶段，规范依赖仍沿 RBF → FrameStore → HeadStore。
- 每个模块的 `evidence/` 保存其源码、测试与进程证据；`extensions/` 保存未接纳候选，`extensions/obsolete/` 保存历史比较方案。FrameBatch/FrameLog 不属于 MVP；CU 已正式接纳，其历史 Prepare+Commit 比较方案位于 HeadStore 扩展目录。
- 本次归类只迁移文件、维护引用与导航。设计条款、API、持久格式、实现和已取得的资格保持；现行正文继续只保留有效条款，被替代内容由历史审阅与 Git 保存。
- 历史证据 JSON 原字节和 hash 不变。其中源码清单、命令或工件记录使用的旧路径属于原 revision/快照事实；按文件 basename 找现目录，或在其记录的 Git revision 查阅原路径，勿为追随目录迁移改写证据。

## 路径迁移对照

| 原本目录中的文件 | 当前目录 |
| --- | --- |
| `01-rbf-*` | `docs/Rbf/` |
| `02-framestore-*`、`03-framestore-*` 及 FrameStore 证据/候选 | `docs/FrameStore/` |
| `04-versionstore-*`、`05-versionstore-*`、`07`–`12` 专项、`15-headstore-*`、`16-headset-*`、HeadStore 证据与历史 CU 比较方案 | `docs/HeadStore/` |
| `00`、`06`、`13`、`14`、`design-questions.md` 与跨层审阅 | 保留本目录 |

模块拥有的历史记录仍与模块规范同目录或位于其 `evidence/`；未来实施片按当前计划继续，不将已完成的收尾队列或历史 Probe 的“下一步”作为新任务。
