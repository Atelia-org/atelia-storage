---
docId: "rbf-decisions"
title: "RBF 关键设计决策（Decision-Layer）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 关键设计决策（Decision-Layer）

> 本文件条款由 AI-Design-DSL `decision` modifier 保护。

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md) §3（Decision → SSOT → Derived）。
>
> **说明**：为降低"同一事实双写漂移"的风险，本文件只锁定"不可随意推翻的关键决策"。
> 规范细节以规范层（SSOT）文档为准。

## decision [S-RBF-DECISION-AI-IMMUTABLE] AI不可修改

本文件中的 Decision 条款为 **AI 不可修改（MVP 固定）**：
- **AI MUST NOT 修改**任何 Decision 条款的语义。
- 如需演进，必须创建新的 Wish + 评审记录，并在相关规范文档的变更日志中显式登记。

## decision [S-RBF-DECISION-CORE-TYPES-SSOT] 核心类型SSOT

RBF 规范（Interface/Format）MUST 以如下通用底层类型及其源码文件作为 SSOT：
- `Atelia.Data.SizedPtr` → `atelia/src/Data/SizedPtr.cs`
- `Atelia.Data.IReservableBufferWriter` → `atelia/src/Data/IReservableBufferWriter.cs`
- `Atelia.AteliaResult<T>` → `atelia/src/Primitives/AteliaResult.cs`（用法指南：`atelia/docs/Primitives/AteliaResult/guide.md`）

## decision [S-RBF-DECISION-SIZEDPTR-CREDENTIAL] SizedPtr凭据语义

写入路径返回的 `SizedPtr` MUST 作为"再次读取同一帧"的凭据（ticket），并且上层 MUST 将其视为不透明值：
- 上层 MUST 以原样保存/传递/回放该值。
- 上层 MUST 将其作为定位读取的输入参数，而不是业务主键。

## decision [S-RBF-DECISION-READFRAME-RESULTPATTERN] 读帧Result模式

随机读取 API MUST 使用 Result-Pattern：`IRbfScanner.ReadFrame` MUST 返回 `AteliaResult<RbfFrame>`；不得使用 `TryReadAt` 的 bool 模式。

---

> 下面条款从 wire format 规范上移为 Decision-Layer（根设计决策）。
> 目的：降低 wire format 的"根常量/根语义"在多个文档中双写漂移的风险。

## design [F-FENCE-DEFINITION] Fence定义

Fence 是 RBF 文件的 **帧分隔符**，不属于任何 Frame。

| 属性 | 值 |
|------|-----|
| 值（Value） | `RBF1`（ASCII: `52 42 46 31`） |
| 长度 | 4 字节 |
| 编码 | ASCII 字节序列写入（非 u32 端序），读取时按字节匹配 |

## design [F-GENESIS] Genesis Fence

- 每个 RBF 文件 MUST 以 Fence 开头（偏移 0，长度 4 字节）——称为 **Genesis Fence**。
- 新建的 RBF 文件 MUST 仅含 Genesis Fence（长度 = 4 字节，表示"无任何 Frame"）。
- 首帧（如果存在）的起始地址 MUST 为 `offset=4`（紧跟 Genesis Fence 之后）。

## design [F-FENCE-SEMANTICS] Fence语义

- Fence 是 **帧分隔符**（fencepost），不属于任何 Frame。
- 文件中第一个 Fence（偏移 0）称为 **Genesis Fence**。
- **Writer** 写完每个 Frame 后 MUST 紧跟一个 Fence。
- **Reader** 在崩溃恢复场景 MAY 遇到不以 Fence 结束的文件（撕裂写入），通过 Resync 处理（见 wire format 的扫描章节）。

文件布局因此为：

```
[Fence][FrameBytes][Fence][FrameBytes][Fence]...
```

## decision [S-RBF-DECISION-4B-ALIGNMENT-ROOT] 4字节对齐根决策

RBF wire format 的以下三个信息 MUST 以 **4 字节对齐**为基础不变量（根设计决策）：
- `[Fence]` 的起始地址（byte offset）
- `[FrameBytes]` 的起始地址（即 FrameStart / HeadLen 字段位置）
- `lengthOf([FrameBytes])`（即 HeadLen）

该不变量用于支撑：
- 逆向扫描/Resync 以 4B 步进寻找 Fence；
- `SizedPtr` 的 4B 对齐约束（offset/length 可用更紧凑的表示并保持热路径简化）。

## decision [S-RBF-DECISION-WRITEPATH-CHUNKEDRESERVABLEWRITER] 写入路径绑定ChunkedReservableWriter

RBF（近期 / MVP）**不追求成为与实现无关的抽象格式**；为效率与一致性，写入路径（尤其是 `BeginFrame()` / `RbfFrameBuilder.Payload` 的 streaming 写入）MUST 绑定采用如下实现作为 SSOT：
- `Atelia.Data.ChunkedReservableWriter` → `atelia/src/Data/ChunkedReservableWriter.cs`

该绑定意味着：
- RBF 写入侧的关键语义（reservation 回填、contiguous prefix flush、以及未提交 reservation 的"不可外泄"属性）允许直接依赖 `ChunkedReservableWriter` 的实现语义。
- 若未来需要支持替代写入路径（例如纯 `PipeWriter` / 纯 `Stream`），必须以新的 Wish 明确提出，并通过新的 Decision 条款锁定其等价语义与验收标准。

---

> 规范层 MUST 引用本文件以声明"受哪些决策约束"。
