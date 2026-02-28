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
随机读取 API MUST 使用 Result-Pattern：`IRbfFile.ReadFrame` MUST 返回 `AteliaResult<RbfFrame>`；不得使用 `TryReadAt` 的 bool 模式。

## decision [S-RBF-DECISION-APPEND-RESULTPATTERN] 写帧Result模式
写入 API MUST 使用 Result-Pattern：`IRbfFile.Append` MUST 返回 `AteliaResult<SizedPtr>`。

**设计理由**：
- 与读取侧的 `[S-RBF-DECISION-READFRAME-RESULTPATTERN]` 保持对称
- 参数约束失败（payload/tailMeta 超长、offset 越界）是"可预见的操作拒绝"，非 Bug
- Result-Pattern 强制调用方处理失败分支，避免"写了但拿不到票据"的情况
- I/O 异常（磁盘满等）仍直接抛出，与 BCL 惯例一致

---

## term `Fence` 栅栏
RBF 文件中用于界定 @`Frame` 边界的定长分隔符。

### decision [F-FENCE-IS-SEPARATOR-NOT-FRAME] Fence布局模式
RBF 数据流 MUST 符合如下交替布局模式：
`[Fence] ([Frame] [Fence])*`

这意味着：
1. **统一性**：流中所有的 @`Fence` 均具有完全相同的物理结构与长度。
2. **起始约束**：文件 MUST 以一个 @`Fence` 开头（Offset 0），此位置的实例特称为 @`HeaderFence`。
3. **闭合约束**：Writer 写完任意 @`Frame` 后，MUST 紧跟写入一个 @`Fence`。

### decision [F-FILE-STARTS-WITH-FENCE] 文件初始化状态
基于 @[F-FENCE-IS-SEPARATOR-NOT-FRAME] 规则，当数据流中不包含任何 @`Frame` 时（即新建空文件状态）：
- 文件 MUST 包含且仅包含 @`HeaderFence`。
- 文件大小恰好等于 1 个 @`Fence` 的长度。

## decision [S-RBF-DECISION-4B-ALIGNMENT-ROOT] 4字节对齐根决策
RBF wire format 的以下三个信息 MUST 以 **4 字节对齐**为基础不变量（根设计决策）：
- `[Fence]` 的起始地址（byte offset）
- `[Frame]` 的起始地址（即 @`Frame` 头部字段位置）
- `lengthOf([Frame])`（即 HeadLen）

该不变量用于支撑：
- 逆向扫描/Resync 以 4B 步进寻找 Fence；
- `SizedPtr` 的 4B 对齐约束（offset/length 可用更紧凑的表示并保持热路径简化）。

## decision [S-RBF-DECISION-WRITEPATH-SINKRESERVABLEWRITER] 写入路径绑定SinkReservableWriter
RBF（近期 / MVP）**不追求成为与实现无关的抽象格式**；为效率与一致性，写入路径（尤其是 `BeginFrame()` / `RbfFrameBuilder.PayloadAndMeta` 的 streaming 写入）MUST 绑定采用如下实现作为 SSOT：
- `Atelia.Data.SinkReservableWriter` → `atelia/src/Data/SinkReservableWriter.cs`

该绑定意味着：
- RBF 写入侧的关键语义（reservation 回填、contiguous prefix flush、以及未提交 reservation 的"不可外泄"属性）允许直接依赖 `SinkReservableWriter` 的实现语义。

---

## decision [S-RBF-DECISION-REVERSESCAN-TAIL-ORIENTED] 逆向扫描尾部导向
RBF 的逆向扫描（Reverse Scan / `ScanReverse`）MUST 以“尽量只触碰 Frame 尾部附近字节”为目标进行设计：
- 逆向扫描 MUST 能在不读取 Frame 头部字段的前提下，完成元信息迭代所需的定位、解析与校验。
- 逆向扫描 MAY 通过引入尾部校验字段（如 Trailer CRC）替代对头部字段的交叉校验。
- 本决策仅锁定“尾部导向”的目标与约束，不限定具体线格式布局与编码方案。

**设计直觉（Informative）**：在“从尾到头”的读取需求下，将传统 Header 的职责镜像到 Trailer，可显著降低大帧场景下的 I/O 与解析成本。

---
## decision [S-RBF-DECISION-TICKET-OVERHEAD-SUBTRACTION-TABLED] SizedPtr票据固定开销减法——搁置

**状态**：TABLED（2026-02-21）

**问题**：SizedPtr 作为帧 ticket 时 Length 包含 FixedOverhead(24B)，使得 VarInt 序列化时 Length 分量偏大。可在 Rbf 内部引入 `RbfFrameTicket` 包装类型，构造时减去 FixedOverhead，访问时加回来，使内部 SizedPtr 存储的 Length 更小从而改善 VarInt 编码紧凑性。

**评估过的候选方案**：

| 方案 | 思路 | 评估 |
|:-----|:-----|:-----|
| A. `RbfFrameTicket` 包装类型 | Rbf 内部包装，构造减/访问加，不改 SizedPtr | 收益真实但量级不足；封堵"未来更低 FixedOverhead 帧格式"路径（虽然该路径的真实性也存疑） |
| B. 序列化层减法 | 仅在 VarInt 编解码点做 delta | 改动最小，但 VarInt 编码器尚不存在 |

**搁置理由**：
1. **优化目标不存在**——VarInt 编码器尚未实现，无法验证实际收益
2. **收益量级不足**——每 ticket 节省 0-1 字节，每次 commit 约省 10-100 字节，相对总 I/O 可忽略
3. **FixedOverhead 是结构性承重墙**——HeadLen(4B) + TrailerCodeword(16B) 不可移除，仅 PayloadCrc(4B) 理论上可选，实际不太可能省掉

**保留的果实**：
- ticket 创建点与消费点的完整映射（3 创建 + 8 消费），见 `agent-team/handoffs/2026-02-21-sizedptr-fixedoverhead-optimization-INV.md`
- 确认了 DL-001（SizedPtr 纯几何语义）在优化压力下的健壮性
- 方案 B 可作为"设计弹药"在 VarInt 编码器实现时立取即用

**重审时机**：当 StateJournal VarInt 编码器进入实现阶段时。

---
