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

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.1 | 2025-12-22 | 从 StateJournal mvp-design-v2.md 提取 |
