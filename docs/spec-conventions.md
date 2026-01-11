# Atelia 规范约定

> 本文档定义 Atelia 项目所有设计规范文档的公共约定。

> （Informative）本文档定义"如何写规范"的写作与表示约定：**在满足 Design-DSL 的机读语法约束前提下**，规定正文中 `@Term-ID`、`@Clause-ID` 等标记的书写/编码方式，以及章节组织、图表/表格/Mermaid 等 规范（Spec） 表示与 LLM 友好写法等内容。
>
> Design-DSL 的机读语法与解析规则（例如条款类型 `decision/spec/derived`、定义/引用区分、依赖图建模）见 [AI-Design-DSL.md](../../agent-team/wiki/SoftwareDesignModeling/AI-Design-DSL.md)。

> **术语迁移锁定**：本文档已完成术语统一（2026-01-12），使用 **Clause-ID（条款ID）** 作为标准术语。
> 禁止重新引入：章节编号、语义锚点、REQID、Semantic Anchor、Requirement ID。

## 1. 规范语言（Normative Language）

### decision [S-DOC-NORMATIVE-LANGUAGE-RFC2119] 规范语言基线

本文使用 RFC 2119 / RFC 8174 定义的关键字表达规范性要求：

- `MUST` / `MUST NOT`：绝对要求/绝对禁止，实现必须遵守
- `SHOULD` / `SHOULD NOT`：推荐/不推荐，可在有充分理由时偏离（需在实现文档中说明）
- `MAY`：可选

### spec [S-DOC-NORMATIVE-INFORMATIVE-SEPARATION] 规范性与解释性内容的区分标注

文档中的规范性条款（Normative）与解释性内容（Informative）MUST 通过以下方式区分：
- 规范性条款：使用上述关键字，或明确标注为"（MVP 固定）"、"（MUST）"
- 解释性内容：使用"建议"、"提示"、"说明"等措辞，或标注为"（Informative）"

### term `MVP-Fixed` （MVP 固定）

> **（MVP 固定）的定义**：等价于"MUST for v2.x"，表示当前版本锁死该选择。后续主版本可能演进为 MAY 或改变语义。

### spec [S-DOC-MVP-FIXED-NEEDS-CID] （MVP 固定）规范约束的编号要求

**规范性** @`MVP-Fixed` 约束（即定义 MUST/MUST NOT 行为的）SHOULD 有对应的条款编号。

### derived [S-DOC-MVP-FIXED-SCOPE-NO-CID] （MVP 固定）范围说明可不编号

**范围说明性** @`MVP-Fixed` 标注（如"MVP 不支持 X"、"MVP 仅实现 Y"）MAY 仅作标注，不强制编号。

### term `Canonical-Source` （权威源）

> **Canonical Source** 是某一类事实的 canonical definition 所在的**权威载体**（文件/章节/条款）。与 SSOT（属性——"是什么"）区分：Canonical Source 描述"在哪"。
>
> 详见 [Decision-Spec-Derived-Model.md §3.3](../../agent-team/wiki/SoftwareDesignModeling/Decision-Spec-Derived-Model.md#33-canonical-source-权威源)。

## 2. 条款编号（Clause-IDs）

### decision [S-DOC-STABLE-CLAUSE-IDS] 使用稳定条款ID标识规范性条款

本项目使用**稳定条款ID（Stable Clause-IDs）**标识规范性条款，便于引用和测试映射。

### spec [S-DOC-CID-PREFIXES] Clause-ID 前缀与覆盖范围

条款编号 MUST 使用以下前缀之一：

| 前缀 | 含义 | 覆盖范围 |
|------|------|----------|
| `[F-NAME]` | **Framing/Format** | 线格式、对齐、CRC 覆盖范围、字段含义 |
| `[A-NAME]` | **API** | 签名、返回值/异常、参数校验、可观测行为 |
| `[S-NAME]` | **Semantics** | 跨 API/格式的语义不变式（含 commit 语义） |
| `[R-NAME]` | **Recovery** | 崩溃一致性、resync/scan、损坏判定 |

### spec [S-DOC-CID-NAMING-SCREAMING-KEBAB] Clause-ID 命名格式

条款编号 MUST 使用 `SCREAMING-KEBAB-CASE` 格式（如 `[F-OBJECTKIND-STANDARD-RANGE]`）。

### decision [S-DOC-CID-STANCE-NAMING] Clause-ID 表达立场（锚点承诺边界）

条款锚点对引用者的承诺边界是：**该条款要求的立场/约束**，而非“讨论了什么话题”。

因此条款编号 MUST 直接表达条款要求的立场（可读作“答案”），避免只写抽象话题名。

示例：用 `CASE-INSENSITIVE` 而非 `CASE-SENSITIVITY`；用 `IMPORT-NON-TRANSITIVE` 而非 `IMPORT-TRANSITIVITY`。

若条款立场发生实质性变更，MUST NOT 复用原锚点名；应按 @[S-DOC-CID-SEMANTIC-STABILITY-AND-CHANGE] 走新建与废弃流程。

### spec [S-DOC-CID-SEMANTIC-STABILITY-AND-CHANGE] 锚点语义稳定性与变更规则

锚点名 SHOULD 概括条款核心语义，使其在内容精炼/补充解释时仍成立；锚点名 SHOULD 避免绑定易变细节（具体实现、数值、版本等）。

条款允许措辞精炼与解释补充，且在核心语义未变时 SHOULD 保持锚点名不变。

当条款的核心语义发生实质性变更时，SHOULD 新建条款，并将旧条款标记为 `DEPRECATED` 且指向替代条款。

“实质性变更”至少包括：
- 规范方向改变（MUST ↔ MUST NOT，或禁止/允许互换）
- 约束范围显著改变（显著收窄或放宽）
- 决策立场改变（原先要求的选择被替换为另一选择）

纯编辑性调整（措辞精炼、示例/解释补充、格式/错别字修正）不构成实质性变更。

### spec [S-DOC-CID-NAME-LENGTH] 锚点名长度约束

锚点名 SHOULD 控制在 3-5 个词。

### spec [S-DOC-CID-DEPRECATION] 废弃条款的锚点保留规则

废弃条款 MUST 用 `DEPRECATED` 标记，保留原锚点名便于历史追溯，并 SHOULD 指向替代条款。

### spec [S-DOC-NORMATIVE-MAP-TO-TEST] 规范性条款的测试映射

每条规范性条款（MUST/MUST NOT）SHOULD 能映射到至少一个测试向量或 failpoint 测试。

## 3. 文档分层与引用方向（Decision → Spec (SSOT) → Derived）

> 本节是为“LLM/AI 参与迭代修订”而新增的工程护栏：通过**文件边界**将不同因果层级的信息分离，降低混杂导致的自洽性维护成本。

### 3.1 三层信息模型

#### spec [S-DOC-LAYERING-DECISION-SPEC-DERIVED] 三层信息模型

规范文档在信息形态上 MUST 显式区分三层：

1. **Decision-Layer（决策层）**：
  - 定义"不可随意推翻的关键设计决策"（例如类型选型、错误协议、凭据语义）。
  - SHOULD 以单独文件承载，并在文件头部写明"AI 不可主动修改"（当该决策被锁定时）。

2. **Spec-Layer（规范层，充当 SSOT）**：
  - 定义可测试的 wire layout、API 签名、算法步骤、失败语义（MUST/MUST NOT）。
  - 该层作为 Single Source of Truth (SSOT)，通常以布局表/公式/伪代码/条款锚点承载。

3. **Derived-Layer（Informative/推导层）**：
  - 只承载从 Decision/SSOT **推断得到**的结论、FAQ、例子、算例、直觉解释。
  - 该层的内容允许滞后、允许被删改、允许不完整。

> （Informative）在采用 Design-DSL 的文档中，映射关系为：
> *   **Normative-Clauses** (规范性集合) = `decision` (Decision-Layer) + `spec` (Spec-Layer)
> *   **Derived-Clause** (推导型条款) ≈ `derived` (Derived-Layer)

### 3.2 单向引用规则

#### spec [S-DOC-REF-DIRECTION-DECISION-SPEC-DERIVED] 单向引用规则

文档之间 MUST 遵守单向引用（禁止逆流）：

- Decision-Layer MAY **声明性引用** Spec-Layer 条款（仅用于"宣告本决策锁定了哪些规范选择"，而非依赖 Spec 的内容作为决策输入）。
- Spec-Layer MUST 引用其所遵循的 Decision-Layer（用于声明"本规范受哪些决策约束"）。
- Derived-Layer MAY 引用 Decision-Layer 与 Spec-Layer（用于解释与答疑）。
- Spec-Layer MUST NOT 反向依赖 Derived-Layer 作为规范依据（Derived 只能"可选参阅"，不得成为约束来源）。

> **解释**：Derived-Layer 的存在是为了解释和降低沟通成本，但它不应反向塑造规范，否则会形成“双写漂移”。

### 3.3 Derived 层的“允许滞后”约定

#### spec [S-DOC-DERIVED-LAYER-ALLOW-DRIFT] Derived 层允许滞后

Derived-Layer MUST 明确标注为（Informative / Derived），并满足：

- 当 Derived 与 Normative Clauses (Decision/Spec) 冲突时，MUST 以 Normative Clauses 为准。
- Derived-Layer 的内容 MAY 在演进中被删除、重写或暂时缺失，不构成规范缺陷。
- Derived-Layer SHOULD 标注其推导输入范围：仅基于 Normative Clauses（Decision/Spec），或 Normative Clauses + 已显式声明的上下文源（以链接列出）。
- Derived-Layer 若引入假设（assumption），MUST 显式标注；assumption MUST NOT 反向作为 Spec-Layer 的规范依据。

**（建议）Derived 条款锚点**：Derived-Layer MAY 使用 `**[NAV-NAME]**` 作为导航锚点（仅用于引用与检索），但该类锚点不属于规范性条款，不应被解释为 MUST/MUST NOT 约束。

### 3.4 文件命名建议（规范化提示）

#### spec [S-DOC-LAYER-FILENAME-CONVENTION] 层级文件命名建议

当采用"分文件分层"时，文件名 SHOULD 显式体现层级：

- Decision-Layer：`<topic>-decisions.md`
- Derived-Layer：`<topic>-derived-notes.md`

其中 `<topic>` MUST 使用 `lower-kebab-case`（见 §5.1"文件名域"）。

## 4. 信息表示与图表（LLM-Friendly Notation）

> 本节规范适用于所有设计规范、RFC、提案、会议结论、接口规格等 Markdown 文档。
> 
> **背景**：文档既要给人类阅读，也要给 LLM 阅读。不同表示形式对 LLM 的"认知负担"差异显著——ASCII art 框图容易导致方向误解，而 Mermaid/表格/列表是 LLM 的"黄金标准"。
> 
> **参考**：[2025-12-24 畅谈会记录](../../agent-team/meeting/2025-12-24-llm-friendly-notation.md)

### 4.1 核心原则

#### spec [S-DOC-ASCIIART-SHOULDNOT] 避免 ASCII art 承载结构化信息

规范文档 SHOULD NOT 使用 ASCII art / box-drawing 图承载结构化信息（例如框图、手工画的状态机/流程图/位图）。
- 若出于教学/情绪/历史原因保留 ASCII art，MUST 标注为（Informative / Illustration），并在相邻位置提供等价的 规范性（Spec）描述（列表/表格/Mermaid 代码块）。

#### spec [S-DOC-SSOT-NO-DOUBLEWRITE] 同一事实禁止双写

同一事实/约束 MUST 只保留一个 权威定义（Canonical Definition / SSOT）。
- 任何非 规范性（Normative） 的"辅助表示"（图示、例子、口语复述）MUST NOT 引入新增约束，且 SHOULD 指回 权威定义（章节链接或条款 ID）。

### 4.2 形式选择指导

#### spec [S-DOC-FORMAT-MINIMALISM] 表示形式最小复杂度原则

表示形式选择 SHOULD 遵循"最小复杂度（降级）"原则：
1. 能用**行内文本**讲清的，不用**列表**。
2. 能用**列表**讲清的，不用**表格**。
3. 能用**表格**讲清的，不用**Mermaid**。
4. 只有涉及**复杂拓扑/时序/依赖**时，才使用 Mermaid。

> **维度测试法**（快速决策启发式）：
> - **1D**（清单、层级）→ 列表
> - **2D**（属性、对比、网格）→ 表格
> - **ND**（连接、时序、依赖）→ Mermaid

#### spec [S-DOC-HIERARCHY-AS-LIST] 树结构使用列表

树/层级结构（目录树、概念分解、任务分解、API 路径树）SHOULD 使用嵌套列表作为 规范形式（Spec）。
- 若目标是"可复制粘贴的目录结构"，MAY 使用"缩进 + 纯路径名"的代码块；SHOULD NOT 使用 `└──` 等 box-drawing 字符作为 规范形式（Spec）。

#### spec [S-DOC-RELATIONS-AS-TABLE] 二维关系使用表格

二维关系/矩阵信息（字段定义、枚举值、对照表、对比矩阵、投票记录）**当需要属性列或对照维度时**，SHOULD 使用 Markdown 表格作为 规范形式（Spec）。
- 若表格过宽或需要多段落解释，SHOULD 拆为"每项一小节（标题 + key/value 列表）"。

#### spec [S-DOC-RELATIONS-AS-TEXT] 少量关系使用 SVO 文本

当需要表达"少量实体之间的少量关系"，且关系的关键在于**边语义（动词）**而非复杂拓扑时，规范形式（Spec） SHOULD 使用"关系列表（Relation Triples List）"的自然语言描述。
- **适用门槛**（可判定）：满足以下全部条件时，作者 SHOULD 首选关系列表作为 规范形式（Spec）：
  1. **节点数**（distinct subject/object）≤ 6；且
  2. **关系数**（条目数）≤ 10；且
  3. 关系条目不需要额外属性列（例如"版本/条件/注释列"）才能保持无歧义。
- **格式约束**（可判定）：作为 规范形式（Spec） 的每条关系 MUST 满足：
  - 单条关系 = 单条 bullet（或单行）；
  - 以 **SVO**（Subject–Verb–Object）顺序表达；
  - **动词/谓词 MUST 加粗**（例如 **使用** / **实现** / **依赖**），用于让边语义显式可识别；
  - Subject/Object SHOULD 使用 code span 或链接（保持实体名稳定）。
- **升级规则**（可判定）：若关系数 > 10 或节点数 > 6，规范形式（Spec） SHOULD 升级为表格（当需要属性列）或 Mermaid（当需要表达拓扑/分支/环/多参与者时序）。

#### spec [S-DOC-GRAPHS-AS-MERMAID] 图使用 Mermaid

图类/序列类信息（状态机、流程/依赖、时序）一旦出现分支/环/多参与者/非局部箭头关系，规范形式（Spec） SHOULD 使用 Mermaid 代码块。
- 状态机优先 `stateDiagram-v2`；流程/依赖优先 `flowchart`/`graph`；时序优先 `sequenceDiagram`。
- 若 Mermaid 被选为 规范形式（Spec），MUST NOT 再维护等价的 ASCII 框图（避免双写漂移）。

#### spec [S-DOC-SIMPLE-FLOW-INLINE] 简单线性流程可行内表示

"简单线性流程"MAY 使用行内箭头表示（如 `A → B → C`），前提是：无分支、无环、步骤数不超过 5，且不会被后续章节引用为规范性依据。
- **注意**：该条仅用于线性步骤流程，SHOULD NOT 用于表达依赖/实现/使用等语义关系（此类场景参见 @[S-DOC-RELATIONS-AS-TEXT]）。

#### spec [S-DOC-BITLAYOUT-AS-TABLE] 位布局使用范围表格

位布局/字节布局（bit layout / wire layout）规范形式（Spec） SHOULD 使用"范围明确"的表格表示，并声明端序与位编号约定。
- **推荐结构**：**行 = 位段/字段，列 = 属性**（如 位范围、字段名、类型、语义）。这种结构支持任意数量的字段和属性扩展。
- **视觉表格**（Visual Table，列模拟位段）MAY 作为辅助 Illustration，但 SHOULD NOT 作为 规范形式（Spec）——因为：(1) 行语义隐式（无行标题）；(2) 字段多时列爆炸；(3) 难以添加属性列。
- ASCII 位图若保留 MUST 为 Illustration，且以表格为准。

**位布局表格示例**：
```markdown
| 位范围 | 字段名 | 类型 | 语义 |
|--------|--------|------|------|
| 31..16 | SubType | `u16` | ObjectKind（当适用时） |
| 15..0 | RecordType | `u16` | Record 顶层类型 |

> **端序**：Little-Endian (LE)
```

### 4.3 快速参考

| 信息类型 | 推荐 规范形式（Spec） | 避免 |
|----------|-----------|------|
| 树/层级 | 嵌套列表 | box-drawing 目录树 |
| 二维关系（需属性列） | Markdown 表格 | 空格对齐伪表格 |
| 少量关系（边语义重要） | 关系列表（SVO 文本） | 箭头图（语义不自证） |
| 状态机/流程图 | Mermaid | ASCII 框图 |
| 时序图 | Mermaid `sequenceDiagram` | ASCII 箭头图 |
| 简单线性流程 | 行内 `A → B → C` | — |
| 位布局 | 范围表格（行=字段，列=属性） | 视觉表格（仅作 Illustration）、ASCII 位图 |

## 5. 术语命名规范（Terminology Naming Conventions）

> 本节规范适用于所有设计规范、RFC、提案、会议结论、接口规格等 Markdown 文档中的术语使用。
> 
> **背景**：统一的术语命名规范有助于提高文档的可读性、搜索友好性和机器可处理性，为 DocGraph 等工具提供清晰的语义边界。
> 
> **参考**：[2025-12-31 畅谈会记录](../../agent-team/meeting/2025-12-31-terminology-naming-convention.md)

### 5.1 分域命名体系

#### spec [S-TERM-DOMAIN-SEPARATION] 分域命名体系

术语命名 SHOULD 遵循分域命名体系，避免跨域污染：

| 域 | 格式 | 示例 | 用途 |
|:---|:-----|:-----|:-----|
| **条款锚点域** | `SCREAMING-KEBAB-CASE` | `[F-OBJECTKIND-STANDARD-RANGE]` | 规范性条款标识 |
| **术语域** | `Title-Kebab`（首字母大写 + 连字符） | `Resolve-Tier`、`App-For-LLM` | 文档中的概念术语 |
| **文件名域** | `lower-kebab-case`（全小写 + 连字符） | `why-Tier.md`、`app-for-llm.md` | 文件系统路径 |
| **代码标识符域** | 遵循语言惯例 | `WhyLayer`、`AppForLLM` | 源代码中的标识符 |

### 5.2 多单词术语格式

#### spec [S-TERM-FORMAT-TITLE-KEBAB] 多单词术语使用 Title-Kebab

多单词概念术语 MUST 使用连字符连接的首字母大写形式（Title-Kebab）。
- 示例：`Resolve-Tier`、`Shape-Tier`、`App-For-LLM`、`Context-Projection`
- 例外：文件名使用 `lower-kebab-case`（如 `why-Tier.md`）

#### decision [S-TERM-TIER-CLOSED-SET] 层级术语闭集

层级术语（Resolve-Tier / Shape-Tier / Rule-Tier / Plan-Tier / Craft-Tier）MUST 作为闭集枚举使用，需畅谈会一致才能创建新的层级术语。

### 5.3 缩写大小写规则

#### spec [S-TERM-ABBREV-REGISTRY] 缩写大小写由注册表决定

缩写的大小写 SHOULD 由术语注册表决定（以 `agent-team/wiki/terminology-registry.yaml` 为 SSOT）：
1. 注册表中 `acronyms_uppercase` 列表内的缩写使用全大写形式
2. 不在注册表中的缩写使用首字母大写形式
3. 复合术语中的缩写保持其独立形式

#### spec [S-TERM-ABBREV-COMPOUND-PRESERVE] 复合术语保持缩写形式

复合术语中的缩写 SHOULD 保持其独立形式，不因复合而改变大小写。
- 示例：`App-For-LLM`（不是 `App-For-Llm`）

### 5.4 术语注册表管理

#### spec [S-TERM-REGISTRY-MANAGEMENT] 术语注册表管理

术语注册表 MUST 作为机器可读的 SSOT，并遵循以下管理原则。

**注册表位置**：`agent-team/wiki/terminology-registry.yaml`（参考链接：[terminology-registry.yaml](../../agent-team/wiki/terminology-registry.yaml)）

**使用方式**：
1. 工具（lint、DocGraph、IDE）必须从此文件读取规则
2. 文档中只引用此文件，不重复列出清单
3. 变更必须通过 PR 评审并更新版本号

**当前版本**：v1.0.0（可通过检查脚本查看最新版本）

### 5.5 迁移与兼容性

#### spec [S-TERM-MIGRATION-GRADUAL] 渐进式迁移策略

术语命名规范的迁移 SHOULD 采用渐进式策略：
1. **SSOT 层**：立即更新（术语表、模板、规范）
2. **规范层**：1 周内更新（活跃规范文档）
3. **活跃文档层**：1 个月内逐步更新
4. **历史层**：保持原样（历史事实）

#### spec [S-TERM-COMPATIBILITY-ALIAS] 兼容别名映射

术语表 SHOULD 维护向后兼容的别名映射，便于历史文档引用。

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.11 | 2026-01-12 | 术语统一：语义锚点/REQID → Clause-ID/CID（基于白板室讨论决策） |
| 0.10 | 2026-01-11 | 明确条款ID命名风格：具体倾向而非抽象话题（基于畅谈会决策） |
| 0.9 | 2026-01-11 | 明确条款ID策略：语义锚点+可选内容指纹，分层应用（基于 Seeker 与 Craftsman 的联合研究） |
| 0.8 | 2026-01-09 | 与 Design-DSL 和 D-S-D 模型对齐 |
| 0.7 | 2026-01-02 | Resolve-Tier 术语迁移：更新层级术语闭集枚举 |
| 0.6 | 2025-12-31 | 明确引用 terminology-registry.yaml，移除重复清单 |
| 0.5 | 2025-12-31 | 新增第 4 章"术语命名规范"（基于 2025-12-31 畅谈会决策） |
| 0.4 | 2025-12-25 | 细化 @[S-DOC-BITLAYOUT-AS-TABLE]：明确推荐"行=字段，列=属性"结构；视觉表格降级为 Illustration |
| 0.3 | 2025-12-25 | 新增 @[S-DOC-RELATIONS-AS-TEXT] 条款；澄清 @[S-DOC-RELATIONS-AS-TABLE] 和 @[S-DOC-SIMPLE-FLOW-INLINE] 的适用边界（[畅谈会决议](../../agent-team/meeting/2025-12-25-llm-friendly-notation-field-test.md)）|
| 0.2 | 2025-12-24 | 新增第 3 章"信息表示与图表"（LLM-Friendly Notation）|
| 0.1 | 2025-12-22 | 从 StateJournal mvp-design-v2.md 提取 |

