# RBF 测试向量

> **版本**：0.5
> **状态**：Draft
> **关联规范**：[rbf-format.md](rbf-format.md)

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 概述

本文档定义 RBF（Layer 0）的测试向量，覆盖 Frame 编码、逆向扫描、Resync、CRC 校验、Ptr64 和 varint 编码等。

**覆盖范围**：
- Frame 结构（HeadLen/FrameTag/TailLen/Pad/CRC32C）
- Fence-as-Separator 语义
- 逆向扫描与 Resync
- Meta 恢复与撕裂提交
- Ptr64（4B unit pointer）
- varint（canonical 最短编码）

---

## 0. 约定

- **Fence-as-Separator**：Fence 是 Frame 分隔符，不属于任何 Frame
- **文件结构**：`[Fence][Frame1][Fence][Frame2]...[Fence]`
- **Frame 格式**：`[HeadLen][FrameTag][Payload][Pad][TailLen][CRC32C]`（不含 Fence）
- **Ptr64**：指向 Frame 的 `HeadLen` 字段起始位置（第一个 Fence 之后）
- 字节序：除 varint 外，定长整数均为 little-endian
- varint：protobuf 风格 base-128，要求 canonical 最短编码
- CRC32C：覆盖 `FrameTag + Payload + Pad + TailLen`

---

## 1. Frame 编码

### 1.1 空文件

**用例 RBF-EMPTY-001**
- Given：文件内容 = `[Fence]`（Genesis Fence，4 bytes）
  - `52 42 46 31`（"RBF1"）
- Then：
  - `reverse_scan()` 返回 0 条 frame
  - `Open()` 成功，`Epoch = 0`

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
  - PadLen = `(4 - PayloadLen % 4) % 4`
  - `HeadLen = 4 (HeadLen) + 4 (FrameTag) + PayloadLen + PadLen + 4 (TailLen) + 4 (CRC32C)`
  - `HeadLen == 16 + PayloadLen + PadLen`
  - `HeadLen == TailLen`

**用例 RBF-LEN-002（PadLen 取值覆盖）**
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`
- Then：PadLen 分别为 `0, 3, 2, 1`；下一条 Fence 起点 4B 对齐

### 1.5 Payload 含 Fence 字节

**用例 RBF-FENCE-IN-PAYLOAD-001**
- Given：Payload（或 FrameTag）恰好包含 `52 42 46 31`（Fence 值 `RBF1`）
- Then：
  - CRC32C 校验通过时，正常解析
  - resync 时遇到 Payload 中的 Fence 值，CRC 校验失败应继续向前扫描

---

## 2. 损坏与恢复

### 2.1 有效 Frame（正例）

**用例 RBF-OK-001（最小可解析 frame）**
- Given：`Fence` 值为 `RBF1`（`52 42 46 31`）；Payload 为空（0 bytes）。
- When：写入 `Fence + HeadLen(16) + FrameTag + (Empty Payload) + Pad(0) + TailLen(16) + CRC32C`。
- Then：
  - `HeadLen == TailLen == 16`
  - `HeadLen % 4 == 0`
  - Frame 起点 4B 对齐
  - CRC32C 校验通过
  - 反向扫描能枚举到该条 frame

### 2.2 损坏 Frame（负例）

**用例 RBF-BAD-001（HeadLen != TailLen）**
- Given：尾部长度与头部长度不一致
- Then：该 frame 视为损坏；反向扫描应停止在上一条有效 frame

**用例 RBF-BAD-002（CRC32C 不匹配）**
- Given：只篡改 FrameTag 或 Payload 任意 1 byte
- Then：CRC32C 校验失败；反向扫描不得将其作为有效 head

**用例 RBF-BAD-003（Frame 起点非 4B 对齐）**
- Given：构造 TailLen 导致 Frame 起点 `% 4 != 0`
- Then：视为损坏

**用例 RBF-BAD-004（TailLen 超界）**
- Given：`TailLen > fileSize` 或 `TailLen < 16`
- Then：视为损坏

### 2.3 截断测试

**用例 RBF-TRUNCATE-001**
- Given：原文件 `[Fence][Frame][Fence]`，截断为 `[Fence][Frame]`
- Then：`Open()` 应回退到上一个有效 commit（或报告空仓库）

**用例 RBF-TRUNCATE-002**
- Given：截断在 frame 中间
- Then：`Open()` 识别出不完整 frame，回退到上一个有效 commit

### 2.4 Meta 恢复与撕裂提交

**用例 META-RECOVER-001（正常 head）**
- Given：meta 尾部有多条 commit record，最后一条有效
- Then：Open 选择最后一条

**用例 META-RECOVER-002（meta 领先 data：DataTail 越界）**
- Given：最后一条 meta frame CRC 通过，但 `DataTail` > data 文件长度
- Then：Open 必须回退到上一条 meta frame

**用例 META-RECOVER-003（meta 指针不可解引用）**
- Given：最后一条 meta frame CRC 通过，但 `VersionIndexPtr` 指向不存在/损坏 frame
- Then：Open 必须回退到上一条 meta frame

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

## 4. varint（canonical）

**用例 VARINT-OK-001（canonical 最短编码）**
- Given：对一组 `uint64` 值（0、1、127、128、16384、2^63、2^64-1）编码
- Then：writer 输出 canonical；reader 可解

**用例 VARINT-BAD-001（非 canonical：多余 continuation 0）**
- Given：`0` 被编码为 `0x80 0x00`
- Then：reader 必须拒绝（格式错误）

**用例 VARINT-BAD-002（溢出/过长）**
- Given：`uint64` varuint 用 11 bytes
- Then：reader 必须拒绝

**用例 VARINT-BAD-003（EOF）**
- Given：截断在 varint 中间
- Then：reader 必须拒绝

---

## 5. 条款映射

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-GENESIS]` | Genesis Fence | RBF-EMPTY-001 |
| `[F-FENCE-SEMANTICS]` | Fence 语义 | RBF-SINGLE-001, RBF-DOUBLE-001 |
| `[F-FRAME-LAYOUT]` | FrameBytes 布局 (含 HeadLen/Tag/TailLen) | RBF-LEN-001, RBF-BAD-001 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码 (4B) | RBF-OK-001, RBF-BAD-002 |
| `[F-FRAME-4B-ALIGNMENT]` | Frame 起点 4B 对齐 | RBF-BAD-003 |
| `[F-PTR64-WIRE-FORMAT]` | Address/Ptr Wire Format | PTR-OK-001, PTR-BAD-001/002 |
| `[F-CRC32C-COVERAGE]` | CRC32C 覆盖范围 (含 Tag) | RBF-OK-001, RBF-BAD-002 |
| `[F-VARINT-CANONICAL-ENCODING]` | VarInt canonical 最短编码 | VARINT-OK-001, VARINT-BAD-001/002/003 |
| `[F-DECODE-ERROR-FAILFAST]` | VarInt 解码错误策略 | VARINT-BAD-001/002/003 |
| `[R-RESYNC-BEHAVIOR]` | Resync 行为 (不信任 TailLen) | RBF-TRUNCATE-001/002, RBF-BAD-003/004 |
| `[R-META-AHEAD-BACKTRACK]` | Meta 领先 Data 按撕裂提交处理 | META-RECOVER-002, META-RECOVER-003 |
| `[R-DATATAIL-TRUNCATE]` | 崩溃恢复截断 | RBF-TRUNCATE-001/002 |

---

## 6. 推荐的黄金文件组织方式

- `test-data/format/rbf/`：手工构造的 data/meta 二进制片段（包含 OK 与 BAD）
- `test-data/format/varint/`：canonical 与非 canonical 的字节序列

每个黄金文件建议配一个小的 `README.md`，写明：
- 编码输入（keys/values）
- 期望输出（state）
- 期望错误类型（FormatError/EOF/Overflow 等）

---

## 变更日志

| 日期 | 版本 | 变更 |
|------|------|------|
| 2025-12-24 | 0.5 | 适配 rbf-format.md v0.11：FrameTag 扩充为 4B，Payload 偏移调整，CRC 覆盖 Tag |
| 2025-12-23 | 0.4 | 适配 rbf-format.md v0.10+：术语更新（Payload -> FrameData, Magic -> Fence） |
| 2025-12-23 | 0.3 | 适配 rbf-format.md v0.10：统一使用 Fence 术语，替换 Magic |
| 2025-12-23 | 0.2 | 适配 rbf-format.md v0.9：更新条款 ID 映射（Genesis/Fence/Ptr64/Resync） |
| 2025-12-22 | 0.1 | 从 mvp-test-vectors.md 提取 Layer 0 测试向量创建独立文档 |
