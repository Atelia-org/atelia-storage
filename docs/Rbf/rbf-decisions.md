---
docId: "rbf-decisions"
title: "RBF 关键设计决策（Decision-Layer）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 关键设计决策（Decision-Layer）
**文档定位**：Decision-Layer，锁定不可随意推翻的关键决策（AI 不可修改）。
文档层级与规范遵循见 [README.md](README.md)。

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

## decision [F-FENCE-IS-SEPARATOR-NOT-FRAME] Fence语义
- Fence 是 **帧分隔符**（fencepost），不属于任何 Frame。
- 文件中第一个 Fence（偏移 0）称为 **Genesis Fence**。
- **Writer** 写完每个 Frame 后 MUST 紧跟一个 Fence。
- **Reader** 在崩溃恢复场景 MAY 遇到不以 Fence 结束的文件（撕裂写入），通过 Resync 处理（见 wire format 的扫描章节）。

文件布局因此为：
```
[Fence][FrameBytes][Fence][FrameBytes][Fence]...
```

## decision [F-FILE-STARTS-WITH-GENESIS-FENCE] Genesis Fence
- 每个 RBF 文件 MUST 以 Fence 开头（偏移 0，长度 4 字节）——称为 **Genesis Fence**。
- 新建的 RBF 文件 MUST 仅含 Genesis Fence（长度 = 4 字节，表示"无任何 Frame"）。
- 首帧（如果存在）的起始地址 MUST 为 `offset=4`（紧跟 Genesis Fence 之后）。

## decision [S-RBF-DECISION-4B-ALIGNMENT-ROOT] 4字节对齐根决策
RBF wire format 的以下三个信息 MUST 以 **4 字节对齐**为基础不变量（根设计决策）：
- `[Fence]` 的起始地址（byte offset）
- `[FrameBytes]` 的起始地址（即 FrameStart / HeadLen 字段位置）
- `lengthOf([FrameBytes])`（即 HeadLen）

该不变量用于支撑：
- 逆向扫描/Resync 以 4B 步进寻找 Fence；
- `SizedPtr` 的 4B 对齐约束（offset/length 可用更紧凑的表示并保持热路径简化）。

## decision [S-RBF-DECISION-WRITEPATH-SINKRESERVABLEWRITER] 写入路径绑定SinkReservableWriter
RBF（近期 / MVP）**不追求成为与实现无关的抽象格式**；为效率与一致性，写入路径（尤其是 `BeginFrame()` / `RbfFrameBuilder.Payload` 的 streaming 写入）MUST 绑定采用如下实现作为 SSOT：
- `Atelia.Data.SinkReservableWriter` → `atelia/src/Data/SinkReservableWriter.cs`

该绑定意味着：
- RBF 写入侧的关键语义（reservation 回填、contiguous prefix flush、以及未提交 reservation 的"不可外泄"属性）允许直接依赖 `SinkReservableWriter` 的实现语义。

---
