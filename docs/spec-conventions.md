# Atelia 规范约定

> 本文档定义 Atelia 项目所有设计规范文档的公共约定。

## 1. 规范语言（Normative Language）

本文使用 RFC 2119 / RFC 8174 定义的关键字表达规范性要求：

- **MUST / MUST NOT**：绝对要求/绝对禁止，实现必须遵守
- **SHOULD / SHOULD NOT**：推荐/不推荐，可在有充分理由时偏离（需在实现文档中说明）
- **MAY**：可选

文档中的规范性条款（Normative）与解释性内容（Informative）通过以下方式区分：
- 规范性条款：使用上述关键字，或明确标注为"（MVP 固定）"、"（MUST）"
- 解释性内容：使用"建议"、"提示"、"说明"等措辞，或标注为"（Informative）"

> **（MVP 固定）的定义**：等价于"MUST for v2.x"，表示当前版本锁死该选择。后续主版本可能演进为 MAY 或改变语义。
> - **规范性**（MVP 固定）约束（即定义 MUST/MUST NOT 行为的）应有对应的条款编号。
> - **范围说明性**（MVP 固定）标注（如"MVP 不支持 X"、"MVP 仅实现 Y"）可仅作标注，不强制编号。

## 2. 条款编号（Requirement IDs）

本项目使用**稳定语义锚点（Stable Semantic Anchors）**标识规范性条款，便于引用和测试映射：

| 前缀 | 含义 | 覆盖范围 |
|------|------|----------|
| `[F-NAME]` | **Framing/Format** | 线格式、对齐、CRC 覆盖范围、字段含义 |
| `[A-NAME]` | **API** | 签名、返回值/异常、参数校验、可观测行为 |
| `[S-NAME]` | **Semantics** | 跨 API/格式的语义不变式（含 commit 语义） |
| `[R-NAME]` | **Recovery** | 崩溃一致性、resync/scan、损坏判定 |

**命名规则**：
- 使用 `SCREAMING-KEBAB-CASE` 格式（如 `[F-OBJECTKIND-STANDARD-RANGE]`）
- 锚点名应能概括条款核心语义，作为"内容哈希"
- 长度控制在 3-5 个词
- 废弃条款用 `DEPRECATED` 标记，保留原锚点名便于历史追溯
- 每条规范性条款（MUST/MUST NOT）应能映射到至少一个测试向量或 failpoint 测试

## 3. 信息表示与图表（LLM-Friendly Notation）

> 本节规范适用于所有设计规范、RFC、提案、会议结论、接口规格等 Markdown 文档。
> 
> **背景**：文档既要给人类阅读，也要给 LLM 阅读。不同表示形式对 LLM 的"认知负担"差异显著——ASCII art 框图容易导致方向误解，而 Mermaid/表格/列表是 LLM 的"黄金标准"。
> 
> **参考**：[2025-12-24 畅谈会记录](../../agent-team/meeting/2025-12-24-llm-friendly-notation.md)

### 3.1 核心原则

**[S-DOC-ASCIIART-SHOULDNOT]** 规范文档 SHOULD NOT 使用 ASCII art / box-drawing 图承载结构化信息（例如框图、手工画的状态机/流程图/位图）。
- 若出于教学/情绪/历史原因保留 ASCII art，MUST 标注为（Informative / Illustration），并在相邻位置提供等价的线性 SSOT（列表/表格/Mermaid 代码块）。

**[S-DOC-SSOT-NO-DOUBLEWRITE]** 同一事实/约束 MUST 只保留一个 SSOT（Single Source of Truth）表示。
- 任何非 SSOT 的"辅助表示"（图示、例子、口语复述）MUST NOT 引入新增约束，且 SHOULD 指回 SSOT（章节链接或条款 ID）。

### 3.2 形式选择指导

**[S-DOC-FORMAT-MINIMALISM]** 表示形式选择 SHOULD 遵循"最小复杂度（降级）"原则：
1. 能用**行内文本**讲清的，不用**列表**。
2. 能用**列表**讲清的，不用**表格**。
3. 能用**表格**讲清的，不用**Mermaid**。
4. 只有涉及**复杂拓扑/时序/依赖**时，才使用 Mermaid。

> **维度测试法**（快速决策启发式）：
> - **1D**（清单、层级）→ 列表
> - **2D**（属性、对比、网格）→ 表格
> - **ND**（连接、时序、依赖）→ Mermaid

**[S-DOC-HIERARCHY-AS-LIST]** 树/层级结构（目录树、概念分解、任务分解、API 路径树）SHOULD 使用嵌套列表作为 SSOT。
- 若目标是"可复制粘贴的目录结构"，MAY 使用"缩进 + 纯路径名"的代码块；SHOULD NOT 使用 `└──` 等 box-drawing 字符作为 SSOT。

**[S-DOC-RELATIONS-AS-TABLE]** 二维关系/矩阵信息（字段定义、枚举值、对照表、对比矩阵、投票记录）SHOULD 使用 Markdown 表格作为 SSOT。
- 若表格过宽或需要多段落解释，SHOULD 拆为"每项一小节（标题 + key/value 列表）"。

**[S-DOC-GRAPHS-AS-MERMAID]** 图类/序列类信息（状态机、流程/依赖、时序）一旦出现分支/环/多参与者/非局部箭头关系，SSOT SHOULD 使用 Mermaid 代码块。
- 状态机优先 `stateDiagram-v2`；流程/依赖优先 `flowchart`/`graph`；时序优先 `sequenceDiagram`。
- 若 Mermaid 被选为 SSOT，MUST NOT 再维护等价的 ASCII 框图（避免双写漂移）。

**[S-DOC-SIMPLE-FLOW-INLINE]** "简单线性流程"MAY 使用行内箭头表示（如 `A → B → C`），前提是：无分支、无环、步骤数不超过 5，且不会被后续章节引用为规范性依据。

**[S-DOC-BITLAYOUT-AS-TABLE]** 位布局/字节布局（bit layout / wire layout）SSOT SHOULD 使用"范围明确"的表格表示（如 `bit 31..24`、`byte 0..3`，并声明端序与位编号约定）。
- 为兼顾"视觉直观性"，MAY 使用"视觉表格（Visual Table）"（用列模拟位段）；ASCII 位图若保留 MUST 为 Illustration，且以表格为准。

### 3.3 快速参考

| 信息类型 | 推荐 SSOT | 避免 |
|----------|-----------|------|
| 树/层级 | 嵌套列表 | box-drawing 目录树 |
| 二维关系 | Markdown 表格 | 空格对齐伪表格 |
| 状态机/流程图 | Mermaid | ASCII 框图 |
| 时序图 | Mermaid `sequenceDiagram` | ASCII 箭头图 |
| 简单流程 | 行内 `A → B → C` | — |
| 位布局 | 范围表格 / 视觉表格 | ASCII 位图（仅作 Illustration） |

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.2 | 2025-12-24 | 新增第 3 章"信息表示与图表"（LLM-Friendly Notation）|
| 0.1 | 2025-12-22 | 从 StateJournal mvp-design-v2.md 提取 |
