# RBF 测试向量

> **版本**：0.6
> **状态**：Draft
> **关联规范**：[rbf-format.md](rbf-format.md)

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 概述

本文档定义 RBF（Layer 0）的测试向量，覆盖 Frame 编码、逆向扫描、Resync、CRC 校验与 Address64/Ptr64 编码。

**覆盖范围**（对齐 rbf-format.md v0.12）：
- FrameBytes 结构（HeadLen/FrameTag/Payload/FrameStatus/TailLen/CRC32C）
- Fence-as-Separator 语义
- Framing/CRC 损坏判定与 Resync
- Address64/Ptr64（u64 LE 文件偏移，4B 对齐）

**不在覆盖范围**：
- 上层语义（如 FrameTag 取值/MetaCommitRecord）
- varint（未在 rbf-format.md Layer 0 中定义）

---

## 0. 约定

- **Fence-as-Separator**：Fence 是 Frame 分隔符，不属于任何 Frame
- **文件结构**：`[Fence][Frame1][Fence][Frame2]...[Fence]`
- **FrameBytes 格式**：`[HeadLen][FrameTag][Payload][FrameStatus][TailLen][CRC32C]`（不含 Fence）
- **Ptr64**：指向 Frame 的 `HeadLen` 字段起始位置（第一个 Fence 之后）
- 字节序：定长整数均为 little-endian
- CRC32C：覆盖 `FrameTag + Payload + FrameStatus + TailLen`

---

## 1. Frame 编码

### 1.1 空文件

**用例 RBF-EMPTY-001**
- Given：文件内容 = `[Fence]`（Genesis Fence，4 bytes）
  - `52 42 46 31`（"RBF1"）
- Then：
  - `reverse_scan()` 返回 0 条 frame
  - 文件中不应存在任何可通过 framing/CRC 校验的 Frame

### 1.2 单条 Frame

**用例 RBF-SINGLE-001**
- Given：文件结构 = `[Fence][Frame][Fence]`
- Then：
  - `reverse_scan()` 返回 1 条 frame
  - Frame 起始位置 = 4（Ptr64 指向 HeadLen 字段）

### 1.3 双条 Frame

**用例 RBF-DOUBLE-001**
- Given：文件结构 = `[Fence][Frame1][Fence][Frame2][Fence]`
- Then：`reverse_scan()` 按逆序返回 Frame2, Frame1

### 1.4 HeadLen/TailLen 计算

**用例 RBF-LEN-001**
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`
- Then：
  - StatusLen = `1 + ((4 - ((PayloadLen + 1) % 4)) % 4)`
  - `HeadLen = 4 (HeadLen) + 4 (FrameTag) + PayloadLen + StatusLen + 4 (TailLen) + 4 (CRC32C)`
  - `HeadLen == 16 + PayloadLen + StatusLen`
  - `HeadLen == TailLen`

**用例 RBF-LEN-002（StatusLen 取值覆盖）**
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`
- Then：StatusLen 分别为 `4, 3, 2, 1`；下一条 Fence 起点 4B 对齐

### 1.5 Payload 含 Fence 字节

**用例 RBF-FENCE-IN-PAYLOAD-001**
- Given：Payload（或 FrameTag）恰好包含 `52 42 46 31`（Fence 值 `RBF1`）
- Then：
  - CRC32C 校验通过时，正常解析
  - resync 时遇到 Payload 中的 Fence 值，CRC 校验失败应继续向前扫描

---

## 2. 损坏与恢复

### 2.1 有效 Frame（正例）

**用例 RBF-OK-001（Valid：PayloadLen=0 → StatusLen=4）**
- Given：`Fence` 值为 `RBF1`（`52 42 46 31`）；Payload 为空（0 bytes）。
- When：写入 `Fence + HeadLen(20) + FrameTag + (Empty Payload) + FrameStatus(0x00 x4) + TailLen(20) + CRC32C`。
- Then：
  - `HeadLen == TailLen == 20`
  - `FrameStatus == 0x00` 且 4 字节全相同
  - Frame 起点 4B 对齐
  - CRC32C 校验通过
  - 反向扫描能枚举到该条 frame

**用例 RBF-OK-002（Tombstone：PayloadLen=3 → StatusLen=1）**
- Given：PayloadLen=3（任意 3 字节 payload）。
- When：写入 `Fence + HeadLen(20) + FrameTag + Payload(3) + FrameStatus(0xFF x1) + TailLen(20) + CRC32C`。
- Then：
  - StatusLen=1（因为 `PayloadLen % 4 == 3`）
  - `FrameStatus == 0xFF` 且所有字节相同
  - CRC32C 校验通过
  - 反向扫描能枚举到该条 frame（Scanner MUST 可见 Tombstone 帧；上层是否忽略不属于 Layer 0）

**用例 RBF-OK-003（StatusLen 覆盖：1/2/3/4）**
- Given：分别构造 4 条通过 framing/CRC 的帧，使 `PayloadLen % 4` 为 `3,2,1,0`
- Then：其 StatusLen 分别为 `1,2,3,4`，且 `(PayloadLen + StatusLen) % 4 == 0`

### 2.2 损坏 Frame（负例）

**用例 RBF-BAD-001（HeadLen != TailLen）**
- Given：尾部长度与头部长度不一致
- Then：该 frame 视为损坏；反向扫描应停止在上一条有效 frame

**用例 RBF-BAD-002（CRC32C 不匹配）**
- Given：只篡改 FrameTag / Payload / FrameStatus / TailLen 任意 1 byte
- Then：CRC32C 校验失败；反向扫描不得将其作为有效 head

**用例 RBF-BAD-003（Frame 起点非 4B 对齐）**
- Given：构造 TailLen 导致 Frame 起点 `% 4 != 0`
- Then：视为损坏

**用例 RBF-BAD-004（TailLen 超界）**
- Given：`TailLen > fileSize` 或 `TailLen < 16` 或 `TailLen != HeadLen`
- Then：视为损坏

**用例 RBF-BAD-005（FrameStatus 非法值）**
- Given：FrameStatus 取值为 `0x01`-`0xFE`（任意）
- Then：Reader MUST 视为 framing 失败（拒绝该帧）

**用例 RBF-BAD-006（FrameStatus 填充不一致）**
- Given：FrameStatus 长度为 2-4，且存在至少 1 字节与其他字节不同（例如 `00 FF 00`）
- Then：Reader MUST 视为 framing 失败（拒绝该帧）

### 2.3 截断测试

**用例 RBF-TRUNCATE-001**
- Given：原文件 `[Fence][Frame][Fence]`，截断为 `[Fence][Frame]`
- Then：逆向扫描不得将不完整尾部视为有效 frame（具体恢复策略由上层决定）

**用例 RBF-TRUNCATE-002**
- Given：截断在 frame 中间
- Then：逆向扫描不得将其视为有效 frame（Resync 继续寻找更早的有效 fence/frame）

---

## 3. Ptr64 校验

**用例 PTR-OK-001（指针可解析）**
- Given：`Ptr64` 指向的 `ByteOffset` 为 4B 对齐且落在文件内，且该处跟随有效 Frame
- Then：`ReadFrame(ptr)` 成功

**用例 PTR-BAD-001（ByteOffset 越界）**
- Given：`ByteOffset >= fileSize`
- Then：必须报错为"不可解引用"

**用例 PTR-BAD-002（ByteOffset 非 4B 对齐）**
- Given：构造一个 ptr，使 `ByteOffset % 4 != 0`
- Then：必须报错（格式错误）

---

## 5. 条款映射

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-GENESIS]` | Genesis Fence | RBF-EMPTY-001 |
| `[F-FENCE-SEMANTICS]` | Fence 语义 | RBF-SINGLE-001, RBF-DOUBLE-001 |
| `[F-FRAME-LAYOUT]` | FrameBytes 布局 (含 HeadLen/Tag/Payload/FrameStatus/TailLen/CRC) | RBF-LEN-001, RBF-OK-001/002/003 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码 (4B) | RBF-OK-001, RBF-BAD-002 |
| `[F-FRAMESTATUS-VALUES]` | FrameStatus 值定义 | RBF-OK-001/002, RBF-BAD-005 |
| `[F-FRAMESTATUS-FILL]` | FrameStatus 填充规则 | RBF-OK-001/002, RBF-BAD-006 |
| `[F-STATUSLEN-FORMULA]` | StatusLen 公式 | RBF-LEN-001/002, RBF-OK-003 |
| `[F-FRAME-4B-ALIGNMENT]` | Frame 起点 4B 对齐 | RBF-BAD-003 |
| `[F-PTR64-WIRE-FORMAT]` | Address/Ptr Wire Format | PTR-OK-001, PTR-BAD-001/002 |
| `[F-CRC32C-COVERAGE]` | CRC32C 覆盖范围 (含 Tag/Status/TailLen) | RBF-OK-001/002, RBF-BAD-002 |
| `[R-RESYNC-BEHAVIOR]` | Resync 行为 (不信任 TailLen) | RBF-TRUNCATE-001/002, RBF-BAD-003/004 |
| `[R-REVERSE-SCAN-ALGORITHM]` | 逆向扫描 | RBF-SINGLE-001, RBF-DOUBLE-001, RBF-OK-001/002 |

---

## 6. 推荐的黄金文件组织方式

- `test-data/format/rbf/`：手工构造的 RBF 二进制片段（包含 OK 与 BAD）

每个黄金文件建议配一个小的 `README.md`，写明：
- 编码输入（keys/values）
- 期望输出（state）
- 期望错误类型（FormatError/EOF/Overflow 等）

---

## 变更日志

| 日期 | 版本 | 变更 |
|------|------|------|
| 2025-12-24 | 0.6 | 适配 rbf-format.md v0.12：Pad→FrameStatus（1-4B）；新增 Valid/Tombstone 与 StatusLen(1-4) 覆盖；CRC 覆盖 FrameStatus；移除 Layer 0 未定义的 varint 与上层 meta 恢复用例 |
| 2025-12-24 | 0.5 | 适配 rbf-format.md v0.11：FrameTag 扩充为 4B，Payload 偏移调整，CRC 覆盖 Tag |
| 2025-12-23 | 0.4 | 适配 rbf-format.md v0.10+：术语更新（Payload -> FrameData, Magic -> Fence） |
| 2025-12-23 | 0.3 | 适配 rbf-format.md v0.10：统一使用 Fence 术语，替换 Magic |
| 2025-12-23 | 0.2 | 适配 rbf-format.md v0.9：更新条款 ID 映射（Genesis/Fence/Ptr64/Resync） |
| 2025-12-22 | 0.1 | 从 mvp-test-vectors.md 提取 Layer 0 测试向量创建独立文档 |
