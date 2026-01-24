---
docId: "rbf-format"
title: "RBF 二进制格式规范（Layer 0）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 二进制格式规范（Layer 0）
**文档定位**：Layer 0，定义 RBF 文件的线格式（wire format）。
文档层级与规范遵循见 [README.md](README.md)。

**状态**：Draft | **版本**：0.40 | **创建日期**：2025-12-22

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

PayloadCodeword:
| 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|
| HeadLen | u32 LE | 4 | FrameBytes 总长度（不含 Fence） |
| Payload | bytes | N | `N >= 0`；业务数据 |
| UserMeta | bytes | M | `M >= 0`；用户元数据（原 PayloadTrailer） |
| Padding | bytes | 0-3 | 为对齐进行的填充 |
| PayloadCrc32C | u32 LE | 4 | Crc32C(Payload+UserMeta+Padding) |

TrailerCodeword (Fixed 16 Bytes):
| 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|
| TrailerCrc32C | u32 **BE** | 4 | Crc32C(FrameDescriptor+FrameTag+TailLen) |
| FrameDescriptor| u32 LE | 4 | 描述符：Tombstone, PaddingLen, UserMetaLen |
| FrameTag | u32 LE | 4 | 帧类型标识符（见 @[F-FRAMETAG-WIRE-ENCODING]） |
| TailLen | u32 LE | 4 | MUST 等于 HeadLen |

### spec [F-FRAMETAG-WIRE-ENCODING] FrameTag线格式编码
FrameTag 是 4 字节 u32 LE 帧类型标识符，位于 Frame 尾部。
RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

### spec [F-FRAMEDESCRIPTOR-LAYOUT] FrameDescriptor布局
FrameDescriptor 是 Frame 尾部 TrailerCodeword 中的 4 字节控制字（u32 LE），统一编码元属性。

| Bit (MSB-LSB) | 字段 | 说明 |
|:--------------|:-----|:-----|
| 31 | IsTombstone | 1 = 墓碑帧，0 = 正常帧 |
| 30-29 | PaddingLen | Payload 填充字节数 (0-3) |
| 28-16 | Reserved | 保留位，MUST 为 0 |
| 15-0 | UserMetaLen | 用户元数据长度 (0-65535) |

### spec [F-TRAILER-CRC-BIG-ENDIAN] TrailerCrc32C按大端序存储
为了逆序用CRC扫描Codeword时兼容检查固定CRC residual的算法，TrailerCrc32C MUST 按BigEndian存储。*逐字节逆序CRC时，等效于顺序LE。*

**反误用护栏（Normative）**：仅 `TrailerCrc32C` 为 BE 存储；TrailerCodeword 中其余字段（`FrameDescriptor`、`FrameTag`、`TailLen`）仍按各自 u32 **LE** 解码。

*示例（Informative）*：给定 TrailerCodeword 字节序列 `[AA BB CC DD] [11 22 33 44] [55 66 77 88] [99 AA BB CC]`：
- `TrailerCrc32C` = `0xAABBCCDD`（按 BE 读取前 4 字节）
- `FrameDescriptor` = `0x44332211`（按 LE 读取第 5-8 字节）
- `FrameTag` = `0x88776655`（按 LE 读取第 9-12 字节）
- `TailLen` = `0xCCBBAA99`（按 LE 读取第 13-16 字节）

### spec [F-TRAILERCRC-COVERAGE] TrailerCrc32C覆盖范围
`TrailerCrc32C` MUST 覆盖 TrailerCodeword 中除自身以外的所有字段。
> TrailerCrc32C = crc32c(FrameDescriptor + FrameTag + TailLen)

- MUST 覆盖：FrameDescriptor (4B) + FrameTag (4B) + TailLen (4B)。
- MUST NOT 覆盖：HeadLen、Payload、UserMeta、Padding、CRC32C、TrailerCrc32C 本身、任何 Fence。

### spec [F-PADDING-CALCULATION] Padding长度计算
**depends:[S-RBF-DECISION-4B-ALIGNMENT-ROOT]**
**depends:[F-FRAMEBYTES-FIELD-OFFSETS]**

为满足 4 字节对齐约束，`Padding` 长度由 `Payload` 和 `UserMeta` 的总长度决定：
> PaddingLen = (4 - ((PayloadLen + UserMetaLen) % 4)) % 4

Writer MUST 将计算出的 `PaddingLen` 填入 `FrameDescriptor` 的 Bit 30-29，并在 `UserMeta` 之后写入相应数量的填充字节（值为 0）。Reader MUST 使用 `FrameDescriptor` 中的 `PaddingLen` 来正确定位 `UserMeta` 和 `Payload` 的边界。

---

## 4. CRC32C

### 4.1 覆盖范围

### spec [F-CRC32C-COVERAGE]  PayloadCrc32C覆盖范围
> PayloadCrc32C = crc32c(Payload + UserMeta + Padding)

`PayloadCrc32C`（即 PayloadCodeword 中的 Checksum）MUST 覆盖：Payload、UserMeta 和 Padding。
`PayloadCrc32C` MUST NOT 覆盖：HeadLen、TrailerCodeword、PayloadCrc32C 本身、任何 Fence。

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
任何违反上述 SSOT 约束的情况（包括但不限于保留位非零、对齐错误或 Fence 不匹配），Reader MUST 视为 Framing 校验失败（损坏）。
具体失败策略（例如：逆向扫描的硬停止 vs 修复工具的 Resync）由相应操作条款定义。

### spec [F-CRC-FAIL-REJECT] CRC校验失败策略
CRC32C 校验不匹配 MUST 视为帧损坏。
Reader MUST NOT 将损坏帧作为有效数据返回。

---

## 6. 逆向扫描与 Resync

### 6.1 逆向扫描（Reverse Scan）

### spec [R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD] 逆向扫描契约
本条款定义 Reverse Scan 的**规范性契约（可观察行为）**。

Reverse Scan MUST 满足：
1. **输出定义**：输出为"通过 framing 校验的 Frame 元信息序列"，顺序 MUST 为 **从尾到头**（最新在前）。
2. **合法性判定（SSOT）**：候选 Frame 是否可被产出 MUST 以 §3 的布局约束与 §5 的 framing 规则为准，并且 MUST 通过尾部元信息校验（见 @[R-REVERSE-SCAN-USES-TRAILERCRC]）。
3. **CRC 职责分离**：Reverse Scan MUST NOT 校验 `PayloadCrc32C`；完整帧的 Content 校验由随机读取路径负责。
4. **失败策略**：当候选 Frame 的 framing 或尾部元信息校验失败时，Reverse Scan MUST 硬停止（终止迭代），不得尝试 Resync 继续扫描。

### spec [R-REVERSE-SCAN-USES-TRAILERCRC] 逆向扫描使用TrailerCrc32C校验尾部元信息
为满足 @[S-RBF-DECISION-REVERSESCAN-TAIL-ORIENTED](rbf-decisions.md)，Reverse Scan MUST 在不读取 Frame 头部字段的前提下校验尾部关键元信息。

- Reverse Scan MUST 验证 `TrailerCrc32C`（覆盖范围见 @[F-TRAILERCRC-COVERAGE]）。
- Reverse Scan MUST NOT 读取 `HeadLen` 也 MUST NOT 执行 `HeadLen == TailLen` 的交叉校验。

### derived [H-REVERSE-SCAN-TAIL-ORIENTED-RATIONALE] 尾部导向逆向扫描设计理由
- 大帧场景下，逆向扫描只需读取尾部附近的定长 TrailerCodeword 即可迭代元信息，避免对 payload 的读取。
- 用 `TrailerCrc32C` 替代 `HeadLen == TailLen`，将可靠性焦点移到“尾部元信息”的可验证性。

### 6.2 Resync 规则

### spec [R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE] Resync行为规则
Resync 为“恢复/修复工具路径”的能力：用于在存在损坏数据时尽可能找回后续 Fence 边界。

- 当工具选择执行 Resync 时，MUST NOT 信任损坏候选的 TailLen 做跳跃。
- Resync 模式 MUST 以 4 字节为步长向前搜索 Fence。
- Resync 扫描 MUST 在抵达 HeaderFence（偏移 0）时停止。

---

## 7. SizedPtr 与 Wire Format 的对应关系（Interface Mapping）

本节用于定义“接口层凭据（`SizedPtr`）”与“线格式（FrameBytes）”之间的对应关系。
这是跨层映射规则，不是对 wire layout 的重复定义。

### spec [S-RBF-SIZEDPTR-WIRE-MAPPING] SizedPtr与FrameBytes映射
当上层以 @`SizedPtr` 表示一个 Frame 的位置与长度时：
- `OffsetBytes` MUST 指向该 Frame 的 `HeadLen` 字段起始位置（即 FrameBytes 起点）。
- `LengthBytes` MUST 等于该 Frame 的 `HeadLen` 字段值（即 FrameBytes 总长度，不含 Fence）。

### derived [H-RBF-SCANREVERSE-TAILLEN-IS-SSOT] 逆向扫描以TailLen为长度SSOT
为满足 @[S-RBF-DECISION-REVERSESCAN-TAIL-ORIENTED](rbf-decisions.md)，逆向扫描在定位上一帧时 MAY 以 `TailLen` 作为长度 SSOT。
这允许 Reverse Scan 在不读取 `HeadLen` 的前提下完成定位；随机读取路径仍以 `SizedPtr.Length == HeadLen` 作为权威映射。

---

## 9. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.40 | 2026-01-24 | **Wire Format Breaking Change**: 重构 Trailer 结构 为固定 16 字节的 `TrailerCodeword`；引入 `FrameDescriptor` (u32) 统一管理 Padding/UserMetaLen/Tombstone；重命名 PayloadTrailer 为 UserMeta；引入 `PayloadCrc32C` 与 `TrailerCrc32C` 双校验机制 |
| 0.32 | 2026-01-10 | 修正 @[F-CRC-IS-CRC32C-CASTAGNOLI-REFLECTED]：删除对不存在的 `.NET System.IO.Hashing.Crc32C` 的引用，改为引用 RFC 3720；添加 `BitOperations.Crc32C` 作为非规范性实现提示 |
| 0.31 | 2026-01-09 | **AI-Design-DSL 格式迁移**：将所有条款标识符转换为 DSL 格式（design/hint/term）；将设计理由拆分为独立 hint 条款；添加 @`DataTail` 术语定义 |
| 0.30 | 2026-01-07 | §3.2 @[F-FRAMEBYTES-FIELD-OFFSETS] 的 FrameStatus 描述去除对齐语义双写，改为引用 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]（由公式定义 StatusLen 并保证 payload+status 对齐） |
