---
docId: "rbf-format"
title: "RBF 二进制格式规范（Layer 0）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 二进制格式规范（Layer 0）
**文档定位**：Layer 0，定义 RBF 文件的线格式（wire format）。
文档层级与规范遵循见 [README.md](README.md)。

**状态**：Draft | **版本**：0.32 | **创建日期**：2025-12-22

## 1. 范围与分层
本文档（Layer 0）只定义：
- RBF 文件的 **线格式（wire format）**：Fence/Magic、Frame 字节布局、对齐与 CRC32C
- 损坏判定（Framing/CRC）与恢复相关的扫描行为（Reverse Scan / Resync）

本文档不定义：
- Frame payload 的业务语义（由上层定义）

---

## 2. 常量与 Fence

### 2.1 Fence 常量定义

本节定义 @`Fence` 的线格式常量值。
关于 @`Fence` 的布局模式与 @`HeaderFence` 定义，参见 @[F-FENCE-IS-SEPARATOR-NOT-FRAME](rbf-decisions.md)。

## spec [F-FENCE-VALUE-IS-RBF1-ASCII-4B] Fence值定义

| 属性 | 值 |
|------|-----|
| 值（Value） | `RBF1`（ASCII: `52 42 46 31`） |
| 长度 | 4 字节 |
| 编码 | ASCII 字节序列写入（非 u32 端序），读取时按字节匹配 |

---

## 3. Wire Layout

### spec [F-FRAMEBYTES-FIELD-OFFSETS] FrameBytes布局
下表描述 FrameBytes 的布局（从 Frame 起点的 `HeadLen` 字段开始计偏移）。
*FrameBytes **不包含** 前后 Fence。*

| 偏移 | 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|------|
| 0 | HeadLen | u32 LE | 4 | FrameBytes 总长度（不含 Fence） |
| 4 | FrameTag | u32 LE | 4 | 帧类型标识符（见 @[F-FRAMETAG-WIRE-ENCODING]） |
| 8 | Payload | bytes | N | `N >= 0`；业务数据 |
| 8+N | FrameStatus | bytes | 1-4 | 帧状态标记（见 @[F-FRAMESTATUS-RESERVED-BITS-ZERO]）；其长度由 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT] 定义 |
| 8+N+StatusLen | TailLen | u32 LE | 4 | MUST 等于 HeadLen |
| 12+N+StatusLen | CRC32C | u32 LE | 4 | 见 @[F-CRC32C-COVERAGE] |

### spec [F-FRAMETAG-WIRE-ENCODING] FrameTag线格式编码
FrameTag 是 4 字节 u32 LE 帧类型标识符，位于 HeadLen 之后、Payload 之前。
RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

### spec [F-FRAMESTATUS-RESERVED-BITS-ZERO] FrameStatus位域定义
**FrameStatus** 是 1-4 字节的帧状态标记。
FrameStatus 采用**位域格式**，同时编码帧状态和 StatusLen。

**位域布局（SSOT）**：
| Bit | 名称 | 说明 |
|-----|------|------|
| 7 | Tombstone | 0 = Valid（正常帧），1 = Tombstone（墓碑帧） |
| 6-2 | Reserved | 保留位，MVP MUST 为 0；Reader 遇到非零值 MUST 视为 Framing 失败 |
| 1-0 | StatusLen | 状态字节数减 1：`00`=1, `01`=2, `10`=3, `11`=4 |

*位域格式解决了 HeadLen 无法唯一确定 PayloadLen/StatusLen 边界的问题。*

### spec [F-FRAMESTATUS-FILL] FrameStatus全字节同值
FrameStatus 的所有字节 MUST 填相同值。
若任意字节与其他字节不一致，视为 Framing 失败。

### spec [F-STATUSLEN-ENSURES-4B-ALIGNMENT] StatusLen计算公式
**depends:[S-RBF-DECISION-4B-ALIGNMENT-ROOT]**
**depends:[F-FRAMEBYTES-FIELD-OFFSETS]**
> StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)

---

## 4. CRC32C

### 4.1 覆盖范围

### spec [F-CRC32C-COVERAGE] CRC32C覆盖范围
> CRC32C = crc32c(FrameTag + Payload + FrameStatus + TailLen)
CRC32C MUST 覆盖：FrameTag + Payload + FrameStatus + TailLen。
CRC32C MUST NOT 覆盖：HeadLen、CRC32C 本身、任何 Fence。

### 4.2 算法约定

### spec [F-CRC-IS-CRC32C-CASTAGNOLI-REFLECTED] CRC32C算法约定
CRC 算法为 CRC32C（Castagnoli），采用 Reflected I/O 约定：
- 多项式（Normal）：`0x1EDC6F41`
- 多项式（Reflected）：`0x82F63B78`
- 初始值：`0xFFFFFFFF`
- 最终异或：`0xFFFFFFFF`
- 输入/输出：bit-reflected

规范参考：IETF RFC 3720 Appendix B (iSCSI CRC)。

*注（非规范性）：在 .NET（.NET 6+）可使用 `System.Numerics.BitOperations.Crc32C(uint crc, byte data)` 作为逐字节累加原语；需对每字节循环调用，并在开始前应用初始值、结束后应用最终异或。*

---

## 5. 损坏判定与失败策略

### spec [F-FRAMING-FAIL-REJECT] Framing校验失败策略
Reader MUST 验证{§2、§3、§4}中定义的所有结构、对齐与值域约束。*完整清单见推导条款 @[H-FRAMING-CHECKLIST](rbf-derived-notes.md)。*
任何违反上述 SSOT 约束的情况（包括但不限于 HeadLen/TailLen 不一致、保留位非零、对齐错误或 Fence 不匹配），Reader MUST 视为 Framing 校验失败（损坏），并按 @[R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE] 进入 Resync。

### spec [F-CRC-FAIL-REJECT] CRC校验失败策略
CRC32C 校验不匹配 MUST 视为帧损坏。
Reader MUST NOT 将损坏帧作为有效数据返回。

---

## 6. 逆向扫描与 Resync

### 6.1 逆向扫描（Reverse Scan）

### spec [R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD] 逆向扫描契约
本条款定义 Reverse Scan 的**规范性契约（可观察行为）**。

Reverse Scan MUST 满足：
1. **输出定义**：输出为"通过 framing/CRC 校验的 Frame 起始地址序列"，顺序 MUST 为 **从尾到头**（最新在前）。
2. **合法性判定（SSOT）**：候选 Frame 是否有效 MUST 以 §5 的 @[F-FRAMING-FAIL-REJECT] 与 @[F-CRC-FAIL-REJECT] 为准。
3. **Resync 行为（SSOT）**：当候选 Frame 校验失败时，Reader MUST 进入 Resync，且 Resync 行为 MUST 遵循 @[R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE]。

### 6.2 Resync 规则

### spec [R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE] Resync行为规则
当候选 Frame 校验失败时（Framing/CRC）：
1. Reader MUST NOT 信任该候选的 TailLen 做跳跃。
2. Reader MUST 进入 Resync 模式：以 4 字节为步长向前搜索 Fence。
3. Resync 扫描 MUST 在抵达 HeaderFence（偏移 0）时停止。

---

## 7. SizedPtr 与 Wire Format 的对应关系（Interface Mapping）

本节用于定义“接口层凭据（`SizedPtr`）”与“线格式（FrameBytes）”之间的对应关系。
这是跨层映射规则，不是对 wire layout 的重复定义。

### spec [S-RBF-SIZEDPTR-WIRE-MAPPING] SizedPtr与FrameBytes映射
当上层以 @`SizedPtr` 表示一个 Frame 的位置与长度时：
- `OffsetBytes` MUST 指向该 Frame 的 `HeadLen` 字段起始位置（即 FrameBytes 起点）。
- `LengthBytes` MUST 等于该 Frame 的 `HeadLen` 字段值（即 FrameBytes 总长度，不含 Fence）。

---

## 9. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.32 | 2026-01-10 | 修正 @[F-CRC-IS-CRC32C-CASTAGNOLI-REFLECTED]：删除对不存在的 `.NET System.IO.Hashing.Crc32C` 的引用，改为引用 RFC 3720；添加 `BitOperations.Crc32C` 作为非规范性实现提示 |
| 0.31 | 2026-01-09 | **AI-Design-DSL 格式迁移**：将所有条款标识符转换为 DSL 格式（design/hint/term）；将设计理由拆分为独立 hint 条款；添加 @`DataTail` 术语定义 |
| 0.30 | 2026-01-07 | §3.2 @[F-FRAMEBYTES-FIELD-OFFSETS] 的 FrameStatus 描述去除对齐语义双写，改为引用 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]（由公式定义 StatusLen 并保证 payload+status 对齐） |
