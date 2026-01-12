---
docId: "rbf-test-vectors"
title: "RBF 测试向量"
produce_by:
  - "wish/W-0009-rbf/wish.md"
---

# RBF 测试向量

> **文档定位**：测试向量，覆盖 Layer 0 的 Frame 编码、扫描、CRC 校验。
> 文档层级与规范遵循见 [README.md](README.md)。
>
> **版本**：0.13 | **状态**：Draft | **对齐规范**：rbf-format.md v0.32, rbf-interface.md v0.28

## 概述

本文档定义 RBF（Layer 0）的测试向量，覆盖 Frame 编码、逆向扫描、Resync、CRC 校验与 `SizedPtr` 定位。

**覆盖范围**（对齐 rbf-format.md v0.28）：
- FrameBytes 结构（HeadLen/FrameTag/Payload/FrameStatus/TailLen/CRC32C）
- FrameStatus 位域格式（Tombstone bit + StatusLen encoding）
- Fence-as-Separator 语义
- Framing/CRC 损坏判定与 Resync
- `SizedPtr`：`OffsetBytes` 指向 Frame 的 `HeadLen` 起始位置，`LengthBytes` 等于 `HeadLen`（见 `rbf-format.md` 的 `[S-RBF-SIZEDPTR-WIRE-MAPPING]`）

**不在覆盖范围**：
- 上层语义（如 FrameTag 取值/MetaCommitRecord）
- varint（未在 rbf-format.md Layer 0 中定义）

---

## 0. 约定

- **Fence-as-Separator**：Fence 是 Frame 分隔符，不属于任何 Frame
- **文件结构**：`[Fence][Frame1][Fence][Frame2]...[Fence]`
- **FrameBytes 格式**：`[HeadLen][FrameTag][Payload][FrameStatus][TailLen][CRC32C]`（不含 Fence）
- **SizedPtr**：`OffsetBytes` 指向 Frame 的 `HeadLen` 字段起始位置；`LengthBytes` 等于 `HeadLen`
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
  - Frame 起始位置 = 4（`SizedPtr.OffsetBytes` 指向 HeadLen 字段）

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

### 1.6 FrameStatus 位域格式

> 对应规范条款 `[F-FRAMESTATUS-RESERVED-BITS-ZERO]`

**位域布局**（引用自 rbf-format.md）：
- Bit 7：Tombstone（0=Valid，1=Tombstone）
- Bit 6-2：Reserved（MVP MUST 为 0）
- Bit 1-0：StatusLen - 1（`00`=1, `01`=2, `10`=3, `11`=4）

**用例 RBF-STATUS-001（MVP 有效值枚举）**

| 值 | 二进制 | IsTombstone | StatusLen | IsMvpValid |
|----|--------|-------------|-----------|------------|
| `0x00` | `0b0000_0000` | false | 1 | ✅ |
| `0x01` | `0b0000_0001` | false | 2 | ✅ |
| `0x02` | `0b0000_0010` | false | 3 | ✅ |
| `0x03` | `0b0000_0011` | false | 4 | ✅ |
| `0x80` | `0b1000_0000` | true | 1 | ✅ |
| `0x81` | `0b1000_0001` | true | 2 | ✅ |
| `0x82` | `0b1000_0010` | true | 3 | ✅ |
| `0x83` | `0b1000_0011` | true | 4 | ✅ |

**用例 RBF-STATUS-002（无效值：Reserved bits 非零）**

| 值 | 二进制 | 失败原因 |
|----|--------|----------|
| `0x04` | `0b0000_0100` | Reserved bit 2 set |
| `0x7F` | `0b0111_1111` | Reserved bits 2-6 all set |
| `0xFE` | `0b1111_1110` | Reserved bits 2-6 set |
| `0xFF` | `0b1111_1111` | Reserved bits 2-6 set（旧版 Tombstone，v0.14 起无效）|

---

## 2. 损坏与恢复

### 2.1 有效 Frame（正例）

**用例 RBF-OK-001（Valid：PayloadLen=0 → StatusLen=4）**
- Given：`Fence` 值为 `RBF1`（`52 42 46 31`）；Payload 为空（0 bytes）。
- When：写入 `Fence + HeadLen(20) + FrameTag + (Empty Payload) + FrameStatus(0x03 x4) + TailLen(20) + CRC32C`。
  - FrameStatus = `0x03`：Valid (bit 7=0) + StatusLen=4 (bits 0-1 = 0b11)
- Then：
  - `HeadLen == TailLen == 20`
  - `FrameStatus == 0x03` 且 4 字节全相同
  - Frame 起点 4B 对齐
  - CRC32C 校验通过
  - 反向扫描能枚举到该条 frame

**用例 RBF-OK-002（Tombstone：PayloadLen=3 → StatusLen=1）**
- Given：PayloadLen=3（任意 3 字节 payload）。
- When：写入 `Fence + HeadLen(20) + FrameTag + Payload(3) + FrameStatus(0x80 x1) + TailLen(20) + CRC32C`。
  - FrameStatus = `0x80`：Tombstone (bit 7=1) + StatusLen=1 (bits 0-1 = 0b00)
- Then：
  - StatusLen=1（因为 `PayloadLen % 4 == 3`）
  - `FrameStatus == 0x80` 且所有字节相同
  - CRC32C 校验通过
  - 反向扫描能枚举到该条 frame（当 `showTombstone=true` 时；默认 `showTombstone=false` 会过滤）

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
- Given：`TailLen > fileSize` 或 `TailLen < 20` 或 `TailLen != HeadLen`
- Then：视为损坏

**用例 RBF-BAD-005（FrameStatus 非法值：Reserved bits 非零）**
- Given：FrameStatus 取值满足 `(value & 0x7C) != 0`（即 bits 2-6 任一为 1）
- Examples：`0x04`, `0x7F`, `0xFE`, `0xFF`（见 §1.6 RBF-STATUS-002）
- Then：Reader MUST 视为 framing 失败（拒绝该帧）

**用例 RBF-BAD-006（FrameStatus 填充不一致）**
- Given：FrameStatus 长度为 2-4，且存在至少 1 字节与其他字节不同（例如 `03 01 03`、`80 81 80`）
- Then：Reader MUST 视为 framing 失败（拒绝该帧）

### 2.3 截断测试

**用例 RBF-TRUNCATE-001**
- Given：原文件 `[Fence][Frame][Fence]`，截断为 `[Fence][Frame]`
- Then：逆向扫描不得将不完整尾部视为有效 frame（具体恢复策略由上层决定）

**用例 RBF-TRUNCATE-002**
- Given：截断在 frame 中间
- Then：逆向扫描不得将其视为有效 frame（Resync 继续寻找更早的有效 fence/frame）

---

## 3. SizedPtr / ReadFrame 行为

> 对应 `rbf-interface.md` 的 `ReadFrame(SizedPtr)`（Result-Pattern：`AteliaResult<RbfFrame>`）。

### 3.1 成功读取（正例）

**用例 READFRAME-OK-001（有效 SizedPtr）**
- Given：`SizedPtr` 的 `OffsetBytes` 指向一条通过 framing/CRC 校验的帧起点，且 `LengthBytes == HeadLen`
- When：调用 `scanner.ReadFrame(ptr)`
- Then：返回 `AteliaResult<RbfFrame>.IsSuccess == true`

### 3.2 读取失败（负例）

**用例 READFRAME-BAD-001（OffsetBytes 越界）**
- Given：`ptr.OffsetBytes >= fileSize`
- When：调用 `scanner.ReadFrame(ptr)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-002（OffsetBytes 非 4B 对齐）**
- Given：`ptr.OffsetBytes % 4 != 0`
- When：调用 `scanner.ReadFrame(ptr)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-003（LengthBytes 与 HeadLen 不匹配）**
- Given：`ptr.OffsetBytes` 指向一条合法帧起点，但 `ptr.LengthBytes != HeadLen`
- When：调用 `scanner.ReadFrame(ptr)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-004（指向位置无合法帧）**
- Given：`ptr.OffsetBytes` 落在文件内且 4B 对齐，但该位置 framing/CRC 校验失败
- When：调用 `scanner.ReadFrame(ptr)`
- Then：返回 `IsFailure == true`

---

## 4. ScanReverse 接口行为

> 对应 rbf-interface.md v0.15 的 ScanReverse 语义条款。

### 4.1 空序列

**用例 SCAN-EMPTY-001（空文件返回零元素）**
- Given：文件内容 = `[Fence]`（仅 Genesis Fence）
- When：调用 `scanner.ScanReverse()`
- Then：
  - `foreach` 循环执行 0 次
  - `GetEnumerator().MoveNext()` 首次返回 `false`
  - 不抛出任何异常

**用例 SCAN-EMPTY-002（仅 Tombstone 帧 — 测试过滤行为）**
- Given：文件包含 1 条 Tombstone 帧（`IsTombstone == true`）
- When & Then：
  - **默认行为**（`showTombstone=false`）：
    - `scanner.ScanReverse()` 返回空序列（0 条帧）
    - Tombstone 帧被过滤
    - 符合 @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER](rbf-interface.md)
  - **显式包含 Tombstone**（`showTombstone=true`）：
    - `scanner.ScanReverse(showTombstone: true)` 返回 1 条帧
    - `frame.IsTombstone == true`
    - 不抛出异常

### 4.2 Tombstone 过滤行为

**用例 SCAN-TOMBSTONE-FILTER-001（混合序列过滤）**
- Given：文件包含 3 条帧：`[Valid1][Tombstone][Valid2]`（按写入顺序）
- When & Then：
  - **默认过滤**（`showTombstone=false`）：
    - 逆序返回：Valid2, Valid1（2 条）
    - Tombstone 帧被跳过
  - **包含 Tombstone**（`showTombstone=true`）：
    - 逆序返回：Valid2, Tombstone, Valid1（3 条）
    - 中间的 Tombstone 帧可见
- 目的：验证"过滤中间位置 Tombstone"的核心行为

### 4.3 Current 生命周期

**用例 SCAN-LIFETIME-001（Current 在 MoveNext 后失效）**
- Given：文件包含 2 条帧 `[Frame1][Frame2]`
- When：
  ```csharp
  var enumerator = scanner.ScanReverse().GetEnumerator();
  enumerator.MoveNext();
  var frame1 = enumerator.Current;  // 捕获 Frame2（逆序）
  enumerator.MoveNext();
  // 此时 frame1 的 Payload 已失效（生命周期已结束）
  ```
- Then：
  - 规范不保证 `frame1.Payload` 在第二次 `MoveNext()` 后仍有效
  - 若需保留数据，必须调用 `frame1.PayloadToArray()`

### 4.4 多次 GetEnumerator

**用例 SCAN-MULTI-ENUM-001（独立枚举器）**
- Given：文件包含 3 条帧 `[Frame1][Frame2][Frame3]`
- When：
  ```csharp
  var seq = scanner.ScanReverse();
  var enum1 = seq.GetEnumerator();
  var enum2 = seq.GetEnumerator();
  enum1.MoveNext(); enum1.MoveNext();  // enum1 前进 2 步
  enum2.MoveNext();                     // enum2 前进 1 步
  ```
- Then：
  - `enum1.Current` 指向 Frame2（从尾部数第 2 帧）
  - `enum2.Current` 指向 Frame3（从尾部数第 1 帧）
  - 两个枚举器互不干扰

### 4.5 foreach 兼容性

**用例 SCAN-FOREACH-001（duck-typed foreach）**
- Given：文件包含 N 条帧
- When：
  ```csharp
  int count = 0;
  foreach (var frame in scanner.ScanReverse())
  {
      count++;
      // frame 可正常访问
  }
  ```
- Then：
  - `count == N`
  - 每次迭代 `frame` 都是有效的 `RbfFrame`

**用例 SCAN-LINQ-FAIL-001（LINQ 不可用）**
- Given：调用方尝试使用 LINQ
- When：
  ```csharp
  // 编译错误：RbfReverseSequence 不包含 'Where' 定义
  var filtered = scanner.ScanReverse().Where(f => !f.IsTombstone);
  ```
- Then：编译失败（这是预期行为，非缺陷）

### 4.6 ref struct 约束

**用例 SCAN-REFSTRUCT-001（不能存储到字段）**
- Given：调用方尝试存储序列到字段
- When：
  ```csharp
  class Holder {
      // 编译错误：ref struct 不能作为类的字段
      private RbfReverseSequence _seq;
  }
  ```
- Then：编译失败（ref struct 护栏生效）

**用例 SCAN-REFSTRUCT-002（不能跨 await）**
- Given：调用方尝试在 async 方法中跨 await 使用
- When：
  ```csharp
  async Task ProcessAsync(IRbfScanner scanner) {
      var seq = scanner.ScanReverse();
      await Task.Delay(1);
      // 编译错误：ref struct 不能跨 await 边界
      foreach (var frame in seq) { }
  }
  ```
- Then：编译失败（ref struct 不能跨 await 边界）

### 4.7 并发修改行为

**用例 SCAN-MUTATION-001（并发修改行为未定义）**
- Given：文件包含 N 条帧
- When：在 `foreach` 枚举期间，另一线程追加新帧到文件
- Then：
  - 行为为**未定义**（规范不保证任何特定行为）
  - 实现 MAY fail-fast（抛出异常）
  - 调用方 MUST 在稳定快照上使用 `ScanReverse()`

**用例 SCAN-MUTATION-002（稳定快照正例）**
- Given：先完成所有写入，关闭 Framer，然后打开 Scanner
- When：调用 `scanner.ScanReverse()` 并完整遍历
- Then：
  - 所有帧正常枚举
  - 无竞争条件风险

---

## 5. 条款映射

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-FILE-STARTS-WITH-GENESIS-FENCE]` | Genesis Fence | RBF-EMPTY-001, SCAN-EMPTY-001 |
| `[F-FENCE-IS-SEPARATOR-NOT-FRAME]` | Fence 语义 | RBF-SINGLE-001, RBF-DOUBLE-001 |
| `[F-FRAMEBYTES-FIELD-OFFSETS]` | FrameBytes 布局 (含 HeadLen/Tag/Payload/FrameStatus/TailLen/CRC) | RBF-LEN-001, RBF-OK-001/002/003 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码 (4B) | RBF-OK-001, RBF-BAD-002 |
| `[F-FRAMESTATUS-RESERVED-BITS-ZERO]` | FrameStatus 位域格式 | RBF-STATUS-001/002, RBF-OK-001/002, RBF-BAD-005 |
| `[F-FRAMESTATUS-FILL]` | FrameStatus 填充规则 | RBF-OK-001/002, RBF-BAD-006 |
| `[F-STATUSLEN-ENSURES-4B-ALIGNMENT]` | StatusLen 公式 | RBF-LEN-001/002, RBF-OK-003 |
| `[F-FRAME-4B-ALIGNMENT]` | Frame 起点 4B 对齐 | RBF-BAD-003 |
| `[S-RBF-SIZEDPTR-WIRE-MAPPING]` | SizedPtr 与 Wire Format 的对应关系 | READFRAME-OK-001, READFRAME-BAD-001/002/003/004 |
| `[F-CRC32C-COVERAGE]` | CRC32C 覆盖范围 (含 Tag/Status/TailLen) | RBF-OK-001/002, RBF-BAD-002 |
| `[R-RESYNC-SCAN-BACKWARD-4B-TO-GENESIS]` | Resync 行为 (不信任 TailLen) | RBF-TRUNCATE-001/002, RBF-BAD-003/004 |
| `[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD]` | 逆向扫描 | RBF-SINGLE-001, RBF-DOUBLE-001, RBF-OK-001/002 |
| `[A-RBF-REVERSE-SEQUENCE]` | RbfReverseSequence (duck-typed) | SCAN-FOREACH-001, SCAN-MULTI-ENUM-001 |
| `[S-RBF-SCANREVERSE-NO-IENUMERABLE]` | 不实现 IEnumerable | SCAN-LINQ-FAIL-001, SCAN-REFSTRUCT-001/002 |
| `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` | 空序列合法 | SCAN-EMPTY-001, SCAN-EMPTY-002 |
| `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]` | Tombstone 默认过滤行为 | SCAN-EMPTY-002, SCAN-TOMBSTONE-FILTER-001, RBF-OK-002 |
| `[S-RBF-SCANREVERSE-CURRENT-LIFETIME]` | Current 生命周期 | SCAN-LIFETIME-001 |
| `[S-RBF-SCANREVERSE-CONCURRENT-MUTATION]` | 并发修改行为未定义 | SCAN-MUTATION-001/002 |
| `[S-RBF-SCANREVERSE-MULTI-GETENUM]` | 多次 GetEnumerator 独立 | SCAN-MULTI-ENUM-001 |

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
| 2026-01-12 | 0.13 | **Tombstone 过滤测试**：更新 SCAN-EMPTY-002 拆分为两个子用例（测试 `showTombstone` 参数）；新增 SCAN-TOMBSTONE-FILTER-001（混合序列过滤）；更新 RBF-OK-002 注释；更新规范版本对齐（rbf-format.md v0.28→v0.32, rbf-interface.md v0.20→v0.28）；条款映射表补充 `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]` |
| 2026-01-07 | 0.12 | **SizedPtr 迁移**：将旧版地址指针相关测试向量迁移为 `SizedPtr` + `ReadFrame` 行为向量；对齐 `rbf-format.md` 的 `[S-RBF-SIZEDPTR-WIRE-MAPPING]` |
| 2025-12-28 | 0.11 | 更新关联规范版本：rbf-format.md v0.15 → v0.16, rbf-interface.md v0.16 → v0.17（FrameTag wrapper type 移除）；核查结果：本文档无需变更——测试向量始终将 FrameTag 作为线格式字段（4B uint）描述，不涉及 C# wrapper type |
| 2025-12-28 | 0.10 | 更新关联规范版本：rbf-interface.md v0.15 → v0.16（Payload 接口简化、新增 `[S-RBF-BUILDER-FLUSH-NO-LEAK]`）；无测试向量变更——接口简化不影响 Layer 0 线格式或读取行为 |
| 2025-12-28 | 0.9 | **新增 §4 ScanReverse 接口行为**（[畅谈会决议](../../../agent-team/meeting/2025-12-28-scan-reverse-return-type.md)）：空序列、Current 生命周期、多次枚举、foreach 兼容性、ref struct 约束测试向量；更新条款映射表 |
| 2025-12-28 | 0.8 | 适配 rbf-format.md v0.15：修正 RBF-BAD-004 最小帧长度边界（16 → 20） |
| 2025-12-25 | 0.7 | 适配 rbf-format.md v0.14：FrameStatus 改为位域格式（Bit 7=Tombstone, Bit 0-1=StatusLen-1）；新增 §1.6 位域测试向量；更新 RBF-OK/BAD 用例 |
| 2025-12-24 | 0.6 | 适配 rbf-format.md v0.12：Pad→FrameStatus（1-4B）；新增 Valid/Tombstone 与 StatusLen(1-4) 覆盖；CRC 覆盖 FrameStatus；移除 Layer 0 未定义的 varint 与上层 meta 恢复用例 |
| 2025-12-24 | 0.5 | 适配 rbf-format.md v0.11：FrameTag 扩充为 4B，Payload 偏移调整，CRC 覆盖 Tag |
| 2025-12-23 | 0.4 | 适配 rbf-format.md v0.10+：术语更新（Payload -> FrameData, Magic -> Fence） |
| 2025-12-23 | 0.3 | 适配 rbf-format.md v0.10：统一使用 Fence 术语，替换 Magic |
| 2025-12-23 | 0.2 | 适配 rbf-format.md v0.9：更新条款 ID 映射（Genesis/Fence/Address/Resync） |
| 2025-12-22 | 0.1 | 从 mvp-test-vectors.md 提取 Layer 0 测试向量创建独立文档 |
