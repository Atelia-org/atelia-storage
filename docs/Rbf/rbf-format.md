---
docId: "rbf-format"
title: "RBF 二进制格式规范（Layer 0）"
produce_by:
      - "wish/W-0006-rbf-sizedptr/wish.md"
---

# RBF 二进制格式规范（Layer 0）

> **状态**：Draft
> **版本**：0.28
> **创建日期**：2025-12-22
> **接口契约（Layer 1）**：[rbf-interface.md](rbf-interface.md)
> **测试向量（Layer 0）**：[rbf-test-vectors.md](rbf-test-vectors.md)

> **Decision-Layer（AI 不可修改）**：本规范受 `rbf-decisions.md` 约束。

---

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 范围与分层

本文档（Layer 0）只定义：
- RBF 文件的 **线格式（wire format）**：Fence/Magic、Frame 字节布局、对齐与 CRC32C
- 损坏判定（Framing/CRC）与恢复相关的扫描行为（Reverse Scan / Resync）

本文档不定义：
- Frame payload 的业务语义（由上层定义）
- `FrameTag` 等接口层业务封装（见 [rbf-interface.md](rbf-interface.md)）

本规范的 **SSOT（Single Source of Truth）** 是：
- §3 的字段布局表（`[F-FRAME-LAYOUT]`）
- §4 的 CRC 覆盖与算法约定
- §6 的扫描契约（`[R-REVERSE-SCAN-ALGORITHM]`/`[R-RESYNC-BEHAVIOR]`）

任何示意、推导、示例都只能解释 SSOT，不得引入新增约束。

---

## 2. 常量与 Fence

### 2.1 Fence / Genesis（Decision-Layer）

Fence 的定义、Genesis Fence 规则、以及 Fence 的 Writer/Reader 语义已上移至 Decision-Layer：
- `rbf-decisions.md`：`[F-FENCE-DEFINITION]`、`[F-GENESIS]`、`[F-FENCE-SEMANTICS]`

本规范保留其在 wire format 中的引用点与推导使用位置。

**`[F-FILE-MINIMUM-LENGTH]`**
- 有效 RBF 文件长度 MUST >= 4（至少包含 Genesis Fence）。
- `fileLength < 4` 表示文件不完整或损坏，Reader MUST fail-soft（返回空序列），MUST NOT 抛出异常。

> **设计理由**：fail-soft 策略与接口层 `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` 保持一致，使上层可以统一处理"无有效帧"的情况，而无需区分"文件不完整"与"文件为空但合法"。
---

## 3. Wire Layout

### 3.1 Fence 语义

Fence 语义已上移至 Decision-Layer：
- `rbf-decisions.md`：`[F-FENCE-SEMANTICS]`

### 3.2 FrameBytes（二进制帧体）布局

**`[F-FRAME-LAYOUT]`**
> 下表描述 FrameBytes 的布局（从 Frame 起点的 `HeadLen` 字段开始计偏移）。
> FrameBytes **不包含** 前后 Fence。

| 偏移 | 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|------|
| 0 | HeadLen | u32 LE | 4 | FrameBytes 总长度（不含 Fence） |
| 4 | FrameTag | u32 LE | 4 | 帧类型标识符（见 `[F-FRAMETAG-WIRE-ENCODING]`） |
| 8 | Payload | bytes | N | `N >= 0`；业务数据 |
| 8+N | FrameStatus | bytes | 1-4 | 帧状态标记（见 `[F-FRAMESTATUS-VALUES]`）；其长度由 `[F-STATUSLEN-FORMULA]` 定义 |
| 8+N+StatusLen | TailLen | u32 LE | 4 | MUST 等于 HeadLen |
| 12+N+StatusLen | CRC32C | u32 LE | 4 | 见 `[F-CRC32C-COVERAGE]` |

**`[F-FRAMETAG-WIRE-ENCODING]`**
- FrameTag 是 4 字节 u32 LE 帧类型标识符，位于 HeadLen 之后、Payload 之前。
- RBF 层不保留任何 FrameTag 值，全部值域由上层定义。
- `FrameTag` 的接口层定义见 [rbf-interface.md](rbf-interface.md) §2.1。

**`[F-FRAMESTATUS-VALUES]`**
> **FrameStatus** 是 1-4 字节的帧状态标记。
> FrameStatus 采用**位域格式**，同时编码帧状态和 StatusLen。

**位域布局（SSOT）**：
| Bit | 名称 | 说明 |
|-----|------|------|
| 7 | Tombstone | 0 = Valid（正常帧），1 = Tombstone（墓碑帧） |
| 6-2 | Reserved | 保留位，MVP MUST 为 0；Reader 遇到非零值 MUST 视为 Framing 失败 |
| 1-0 | StatusLen | 状态字节数减 1：`00`=1, `01`=2, `10`=3, `11`=4 |

> **设计理由**：
> - 位域格式解决了 HeadLen 无法唯一确定 PayloadLen/StatusLen 边界的问题。
> - Bit 7 作为 Tombstone 标记，语义清晰，判断高效（符号位检测）。
> - Bit 0-1 编码 StatusLen，支持 1-4 字节。
> - Bit 2-6 保留给未来扩展，当前 MUST 为 0。
> - 全字节同值设计提供隐式冗余校验：若字节不一致，可直接判定损坏。
>
> MVP 有效值的完整枚举见 [rbf-test-vectors.md](rbf-test-vectors.md) 的 §1.6。

### 3.3 长度关系（HeadLen / StatusLen）

**`[F-HEADLEN-FORMULA]`**
HeadLen = 4 (HeadLen) + 4 (FrameTag) + PayloadLen + StatusLen + 4 (TailLen) + 4 (CRC32C)

**`[F-STATUSLEN-FORMULA]`**
StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)

**`[F-FRAME-4B-ALIGNMENT]`**
- Frame 起点（HeadLen 字段位置）MUST 4B 对齐。
> 注：该 4B 对齐不变量属于根设计决策，见 `rbf-decisions.md` 的 **[S-RBF-DECISION-4B-ALIGNMENT-ROOT]**。

**`[F-FRAMESTATUS-FILL]`**
- FrameStatus 的所有字节 MUST 填相同值。
- 若任意字节与其他字节不一致，视为 Framing 失败。
> 注：合法值由 `[F-FRAMESTATUS-VALUES]` 位域 SSOT 定义。

---

## 4. CRC32C

### 4.1 覆盖范围

**`[F-CRC32C-COVERAGE]`**
CRC32C = crc32c(FrameTag + Payload + FrameStatus + TailLen)
- CRC32C MUST 覆盖：FrameTag + Payload + FrameStatus + TailLen。
- CRC32C MUST NOT 覆盖：HeadLen、CRC32C 本身、任何 Fence。

### 4.2 算法约定

**`[F-CRC32C-ALGORITHM]`**
CRC 算法为 CRC32C（Castagnoli），采用 Reflected I/O 约定：
- 初始值：`0xFFFFFFFF`
- 最终异或：`0xFFFFFFFF`
- Reflected 多项式：`0x82F63B78`（Normal 形式：`0x1EDC6F41`）
> 等价实现：`.NET System.IO.Hashing.Crc32C`。

---

## 5. 损坏判定与失败策略

**`[F-FRAMING-FAIL-REJECT]`**
Reader MUST 验证以下条款所定义的约束，任一不满足时将候选 Frame 视为损坏：
- `[F-FRAME-LAYOUT]`：HeadLen/TailLen 一致性
- `[F-FRAMESTATUS-VALUES]`：FrameStatus 位域合法（IsMvpValid）
- `[F-FRAMESTATUS-FILL]`：FrameStatus 所有字节一致
- `[F-HEADLEN-FORMULA]`：长度公式一致性
- `[F-FRAME-4B-ALIGNMENT]`：Frame 起点 4B 对齐
- `[F-FENCE-DEFINITION]`：Fence 匹配
- `[F-GENESIS]`：Frame 位于 Genesis 之后

> 注：`[F-FENCE-DEFINITION]` / `[F-GENESIS]` / `[F-FENCE-SEMANTICS]` 已上移至 Decision-Layer：`rbf-decisions.md`。

**`[F-CRC-FAIL-REJECT]`**
- CRC32C 校验不匹配 MUST 视为帧损坏。
- Reader MUST NOT 将损坏帧作为有效数据返回。

---

## 6. 逆向扫描与 Resync

### 6.1 逆向扫描（Reverse Scan）

**`[R-REVERSE-SCAN-ALGORITHM]`**
> 本条款定义 Reverse Scan 的**规范性契约（可观察行为）**。

Reverse Scan MUST 满足：
1. **输出定义**：输出为“通过 framing/CRC 校验的 Frame 起始地址序列”，顺序 MUST 为 **从尾到头**（最新在前）。
2. **合法性判定（SSOT）**：候选 Frame 是否有效 MUST 以 §5 的 `[F-FRAMING-FAIL-REJECT]` 与 `[F-CRC-FAIL-REJECT]` 为准。
3. **Resync 行为（SSOT）**：当候选 Frame 校验失败时，Reader MUST 进入 Resync，且 Resync 行为 MUST 遵循 `[R-RESYNC-BEHAVIOR]`。

### 6.2 Resync 规则

**`[R-RESYNC-BEHAVIOR]`**
当候选 Frame 校验失败时（Framing/CRC）：
1. Reader MUST NOT 信任该候选的 TailLen 做跳跃。
2. Reader MUST 进入 Resync 模式：以 4 字节为步长向前搜索 Fence。
3. Resync 扫描 MUST 在抵达 Genesis Fence（偏移 0）时停止。

---

## 7. SizedPtr 与 Wire Format 的对应关系（Interface Mapping）

> 本节用于定义“接口层凭据（`SizedPtr`）”与“线格式（FrameBytes）”之间的对应关系。
> 这是跨层映射规则，不是对 wire layout 的重复定义。

**`[S-RBF-SIZEDPTR-WIRE-MAPPING]`**
当上层以 `SizedPtr` 表示一个 Frame 的位置与长度时：
- `OffsetBytes` MUST 指向该 Frame 的 `HeadLen` 字段起始位置（即 FrameBytes 起点）。
- `LengthBytes` MUST 等于该 Frame 的 `HeadLen` 字段值（即 FrameBytes 总长度，不含 Fence）。

---

## 8. DataTail 与截断（恢复语义）

**`[R-DATATAIL-DEFINITION]`**
- `DataTail` 是一个字节偏移量（byte offset），表示 data 文件的逻辑尾部。
- `DataTail` MUST 指向“有效数据末尾”，并包含尾部 Fence（即 `DataTail == 有效 EOF`）。

**`[R-DATATAIL-TRUNCATE]`**
恢复时（上层依据其 HEAD/commit record 的语义决定使用哪条 DataTail）：
1. 若 data 文件实际长度 > DataTail：MUST 截断至 DataTail。
2. 截断后文件 SHOULD 以 Fence 结尾（若 `DataTail` 来自通过校验的 commit record）。

---

## 10. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.28 | 2026-01-07 | §7 移除遗留的旧版地址指针编码层描述，改为定义 `SizedPtr` 与 FrameBytes 的跨层映射规则 `[S-RBF-SIZEDPTR-WIRE-MAPPING]`；`DataTail` 不再引用 §7 |
| 0.29 | 2026-01-07 | §6.1 将 Reverse Scan 从“参考实现伪代码”收敛为“可观察行为契约（SSOT）” `[R-REVERSE-SCAN-ALGORITHM]`（并去除与 `[R-RESYNC-BEHAVIOR]` 重复的边界条款）；参考伪代码迁移至 Derived-Layer |
| 0.30 | 2026-01-07 | §3.2 `[F-FRAME-LAYOUT]` 的 FrameStatus 描述去除对齐语义双写，改为引用 `[F-STATUSLEN-FORMULA]`（由公式定义 StatusLen 并保证 payload+status 对齐） |
