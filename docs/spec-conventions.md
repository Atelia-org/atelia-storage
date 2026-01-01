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

**[S-DOC-RELATIONS-AS-TABLE]** 二维关系/矩阵信息（字段定义、枚举值、对照表、对比矩阵、投票记录）**当需要属性列或对照维度时**，SHOULD 使用 Markdown 表格作为 SSOT。
- 若表格过宽或需要多段落解释，SHOULD 拆为"每项一小节（标题 + key/value 列表）"。

**[S-DOC-RELATIONS-AS-TEXT]** 当需要表达"少量实体之间的少量关系"，且关系的关键在于**边语义（动词）**而非复杂拓扑时，SSOT SHOULD 使用"关系列表（Relation Triples List）"的自然语言描述。
- **适用门槛**（可判定）：满足以下全部条件时，作者 SHOULD 首选关系列表作为 SSOT：
  1. **节点数**（distinct subject/object）≤ 6；且
  2. **关系数**（条目数）≤ 10；且
  3. 关系条目不需要额外属性列（例如"版本/条件/注释列"）才能保持无歧义。
- **格式约束**（可判定）：作为 SSOT 的每条关系 MUST 满足：
  - 单条关系 = 单条 bullet（或单行）；
  - 以 **SVO**（Subject–Verb–Object）顺序表达；
  - **动词/谓词 MUST 加粗**（例如 **使用** / **实现** / **依赖**），用于让边语义显式可识别；
  - Subject/Object SHOULD 使用 code span 或链接（保持实体名稳定）。
- **升级规则**（可判定）：若关系数 > 10 或节点数 > 6，SSOT SHOULD 升级为表格（当需要属性列）或 Mermaid（当需要表达拓扑/分支/环/多参与者时序）。

**[S-DOC-GRAPHS-AS-MERMAID]** 图类/序列类信息（状态机、流程/依赖、时序）一旦出现分支/环/多参与者/非局部箭头关系，SSOT SHOULD 使用 Mermaid 代码块。
- 状态机优先 `stateDiagram-v2`；流程/依赖优先 `flowchart`/`graph`；时序优先 `sequenceDiagram`。
- 若 Mermaid 被选为 SSOT，MUST NOT 再维护等价的 ASCII 框图（避免双写漂移）。

**[S-DOC-SIMPLE-FLOW-INLINE]** "简单线性流程"MAY 使用行内箭头表示（如 `A → B → C`），前提是：无分支、无环、步骤数不超过 5，且不会被后续章节引用为规范性依据。
- **注意**：该条仅用于线性步骤流程，SHOULD NOT 用于表达依赖/实现/使用等语义关系（此类场景参见 `[S-DOC-RELATIONS-AS-TEXT]`）。

**[S-DOC-BITLAYOUT-AS-TABLE]** 位布局/字节布局（bit layout / wire layout）SSOT SHOULD 使用"范围明确"的表格表示，并声明端序与位编号约定。
- **推荐结构**：**行 = 位段/字段，列 = 属性**（如 位范围、字段名、类型、语义）。这种结构支持任意数量的字段和属性扩展。
- **视觉表格**（Visual Table，列模拟位段）MAY 作为辅助 Illustration，但 SHOULD NOT 作为 SSOT——因为：(1) 行语义隐式（无行标题）；(2) 字段多时列爆炸；(3) 难以添加属性列。
- ASCII 位图若保留 MUST 为 Illustration，且以表格为准。

**位布局表格示例**：
```markdown
| 位范围 | 字段名 | 类型 | 语义 |
|--------|--------|------|------|
| 31..16 | SubType | `u16` | ObjectKind（当适用时） |
| 15..0 | RecordType | `u16` | Record 顶层类型 |

> **端序**：Little-Endian (LE)
```

### 3.3 快速参考

| 信息类型 | 推荐 SSOT | 避免 |
|----------|-----------|------|
| 树/层级 | 嵌套列表 | box-drawing 目录树 |
| 二维关系（需属性列） | Markdown 表格 | 空格对齐伪表格 |
| 少量关系（边语义重要） | 关系列表（SVO 文本） | 箭头图（语义不自证） |
| 状态机/流程图 | Mermaid | ASCII 框图 |
| 时序图 | Mermaid `sequenceDiagram` | ASCII 箭头图 |
| 简单线性流程 | 行内 `A → B → C` | — |
| 位布局 | 范围表格（行=字段，列=属性） | 视觉表格（仅作 Illustration）、ASCII 位图 |

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.4 | 2025-12-25 | 细化 `[S-DOC-BITLAYOUT-AS-TABLE]`：明确推荐"行=字段，列=属性"结构；视觉表格降级为 Illustration |
| 0.3 | 2025-12-25 | 新增 `[S-DOC-RELATIONS-AS-TEXT]` 条款；澄清 `[S-DOC-RELATIONS-AS-TABLE]` 和 `[S-DOC-SIMPLE-FLOW-INLINE]` 的适用边界（[畅谈会决议](../../agent-team/meeting/2025-12-25-llm-friendly-notation-field-test.md)）|
| 0.2 | 2025-12-24 | 新增第 3 章"信息表示与图表"（LLM-Friendly Notation）|
| 0.1 | 2025-12-22 | 从 StateJournal mvp-design-v2.md 提取 |

## 4. 术语命名规范（Terminology Naming Conventions）

> 本节规范适用于所有设计规范、RFC、提案、会议结论、接口规格等 Markdown 文档中的术语使用。
> 
> **背景**：统一的术语命名规范有助于提高文档的可读性、搜索友好性和机器可处理性，为 DocGraph 等工具提供清晰的语义边界。
> 
> **参考**：[2025-12-31 畅谈会记录](../../agent-team/meeting/2025-12-31-terminology-naming-convention.md)

### 4.1 分域命名体系

**[S-TERM-DOMAIN-SEPARATION]** 术语命名 SHOULD 遵循分域命名体系，避免跨域污染：

| 域 | 格式 | 示例 | 用途 |
|:---|:-----|:-----|:-----|
| **条款锚点域** | `SCREAMING-KEBAB-CASE` | `[F-OBJECTKIND-STANDARD-RANGE]` | 规范性条款标识 |
| **术语域** | `Title-Kebab`（首字母大写 + 连字符） | `Why-Tier`、`App-For-LLM` | 文档中的概念术语 |
| **文件名域** | `lower-kebab-case`（全小写 + 连字符） | `why-Tier.md`、`app-for-llm.md` | 文件系统路径 |
| **代码标识符域** | 遵循语言惯例 | `WhyLayer`、`AppForLLM` | 源代码中的标识符 |

### 4.2 多单词术语格式

**[S-TERM-FORMAT-TITLE-KEBAB]** 多单词概念术语 MUST 使用连字符连接的首字母大写形式（Title-Kebab）。
- 示例：`Why-Tier`、`Shape-Tier`、`App-For-LLM`、`Context-Projection`
- 例外：文件名使用 `lower-kebab-case`（如 `why-Tier.md`）

**[S-TERM-Tier-CLOSED-SET]** 层级术语（Why-Tier → Craft-Tier）MUST 作为闭集枚举使用，不允许创建新的层级术语。

### 4.3 缩写大小写规则

**[S-TERM-ABBREV-REGISTRY]** 缩写的大小写 SHOULD 由术语注册表决定（以 `agent-team/wiki/terminology-registry.yaml` 为 SSOT）：
1. 注册表中 `acronyms_uppercase` 列表内的缩写使用全大写形式
2. 不在注册表中的缩写使用首字母大写形式
3. 复合术语中的缩写保持其独立形式

**[S-TERM-ABBREV-COMPOUND-PRESERVE]** 复合术语中的缩写 SHOULD 保持其独立形式，不因复合而改变大小写。
- 示例：`App-For-LLM`（不是 `App-For-Llm`）

### 4.4 术语注册表管理

**[S-TERM-REGISTRY-MANAGEMENT]** 术语注册表 MUST 作为机器可读的 SSOT，并遵循以下管理原则。

**注册表位置**：`agent-team/wiki/terminology-registry.yaml`（参考链接：[terminology-registry.yaml](../../agent-team/wiki/terminology-registry.yaml)）

**使用方式**：
1. 工具（lint、DocGraph、IDE）必须从此文件读取规则
2. 文档中只引用此文件，不重复列出清单
3. 变更必须通过 PR 评审并更新版本号

**当前版本**：v1.0.0（可通过检查脚本查看最新版本）

### 4.5 迁移与兼容性

**[S-TERM-MIGRATION-GRADUAL]** 术语命名规范的迁移 SHOULD 采用渐进式策略：
1. **SSOT 层**：立即更新（术语表、模板、规范）
2. **规范层**：1 周内更新（活跃规范文档）
3. **活跃文档层**：1 个月内逐步更新
4. **历史层**：保持原样（历史事实）

**[S-TERM-COMPATIBILITY-ALIAS]** 术语表 SHOULD 维护向后兼容的别名映射，便于历史文档引用。

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.6 | 2025-12-31 | 明确引用 terminology-registry.yaml，移除重复清单 |
| 0.5 | 2025-12-31 | 新增第 4 章"术语命名规范"（基于 2025-12-31 畅谈会决策） |
| 0.4 | 2025-12-25 | 细化 `[S-DOC-BITLAYOUT-AS-TABLE]`：明确推荐"行=字段，列=属性"结构；视觉表格降级为 Illustration |
| 0.3 | 2025-12-25 | 新增 `[S-DOC-RELATIONS-AS-TEXT]` 条款；澄清 `[S-DOC-RELATIONS-AS-TABLE]` 和 `[S-DOC-SIMPLE-FLOW-INLINE]` 的适用边界（[畅谈会决议](../../agent-team/meeting/2025-12-25-llm-friendly-notation-field-test.md)）|
| 0.2 | 2025-12-24 | 新增第 3 章"信息表示与图表"（LLM-Friendly Notation）|
| 0.1 | 2025-12-22 | 从 StateJournal mvp-design-v2.md 提取 |

