---
docId: "rbf-test-vectors"
title: "RBF 测试向量"
produce_by:
  - "wish/W-0009-rbf/wish.md"
---

# RBF 测试向量

2026-10-03范围：§0–§7保留RBF1 v0.40的byte-wire向量及其历史操作案例，不重写成units；旧工厂/修复案例不代表本轮普通打开合同。RBF1兼容读取仍须核对这些原始字节。RBF3独立参考与本轮生产资格在§8，源码/测试/正式证据已独立复核；证据身份、结果及限制归[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)。历史实验RBF2快照不作RBF3向量。

> **文档定位**：测试向量，覆盖 Layer 0 的 Frame 编码、扫描、CRC 校验。
> 文档层级与规范遵循见 [README.md](README.md)。
>
> **版本**：0.40 | **状态**：Draft | **对齐规范**：rbf-format.md v0.40, rbf-interface.md v0.30

## 概述

本文档定义 RBF（Layer 0）的测试向量，覆盖 Frame 编码、逆向扫描、Resync、CRC 校验与 `SizedPtr` 定位。

**覆盖范围**（对齐 rbf-format.md v0.40）：
- FrameBytes 结构（v0.40 布局）：
  - `[HeadLen][Payload][TailMeta][Padding][PayloadCrc32C][TrailerCodeword(16B)]`
- TrailerCodeword（固定 16 字节）：
  - `[TrailerCrc32C(4B BE)][FrameDescriptor(4B LE)][FrameTag(4B LE)][TailLen(4B LE)]`
- FrameDescriptor 位域格式（bit31=Tombstone, bit30-29=PaddingLen, bit28-16=Reserved, bit15-0=TailMetaLen）
- 双 CRC 机制（PayloadCrc32C + TrailerCrc32C）
- Fence-as-Separator 语义
- Framing/CRC 损坏判定与 Resync
- `SizedPtr`：`Offset` 指向 Frame 的 `HeadLen` 起始位置，`Length` 等于 `HeadLen`（见 @[S-RBF-SIZEDPTR-WIRE-MAPPING]）

**不在覆盖范围**：
- 上层语义（如 FrameTag 取值/MetaCommitRecord）

---

## 0. 约定

- **Fence-as-Separator**：Fence 是 Frame 分隔符，不属于任何 Frame
- **文件结构**：`[Fence][Frame1][Fence][Frame2]...[Fence]`
- **FrameBytes 格式（v0.40）**：`[HeadLen][Payload][TailMeta][Padding][PayloadCrc32C][TrailerCodeword]`（不含 Fence）
- **TrailerCodeword 格式**：`[TrailerCrc32C(BE)][FrameDescriptor(LE)][FrameTag(LE)][TailLen(LE)]`（固定 16 字节）
- **SizedPtr**：`Offset` 指向 Frame 的 `HeadLen` 字段起始位置；`Length` 等于 `HeadLen`
- **字节序**：
  - 定长整数默认为 little-endian
  - **唯一例外**：`TrailerCrc32C` 按 **big-endian** 存储（见 @[F-TRAILER-CRC-BIG-ENDIAN]）
- **双 CRC**：
  - `PayloadCrc32C`（LE）覆盖 `Payload + TailMeta + Padding`
  - `TrailerCrc32C`（BE）覆盖 `FrameDescriptor + FrameTag + TailLen`
- **最小帧长度**：24 字节 = HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16)

---

## 1. Frame 编码

### 1.1 空文件

**用例 RBF-EMPTY-001**
- Given：文件内容 = `[Fence]`（HeaderFence，4 bytes）
  - `52 42 46 31`（"RBF1"）
- Then：
  - `reverse_scan()` 返回 0 条 frame
  - 文件中不应存在任何可通过 framing/CRC 校验的 Frame

### 1.2 单条 Frame

**用例 RBF-SINGLE-001**
- Given：文件结构 = `[Fence][Frame][Fence]`
- Then：
  - `reverse_scan()` 返回 1 条 frame
  - Frame 起始位置 = 4（`SizedPtr.Offset` 指向 HeadLen 字段）

### 1.3 双条 Frame

**用例 RBF-DOUBLE-001**
- Given：文件结构 = `[Fence][Frame1][Fence][Frame2][Fence]`
- Then：`reverse_scan()` 按逆序返回 Frame2, Frame1

### 1.4 帧长度计算（v0.40）

**用例 RBF-LEN-001**
- Given：PayloadLen 分别为 `4k, 4k+1, 4k+2, 4k+3`，TailMetaLen = 0
- Then：
  - PaddingLen = `(4 - ((PayloadLen + TailMetaLen) % 4)) % 4`
  - FrameLength = `4 (HeadLen) + PayloadLen + TailMetaLen + PaddingLen + 4 (PayloadCrc) + 16 (TrailerCodeword)`
  - FrameLength = `24 + PayloadLen + TailMetaLen + PaddingLen`
  - `HeadLen == TailLen == FrameLength`

**用例 RBF-LEN-002（PaddingLen 取值覆盖）**
- Given：PayloadLen + TailMetaLen 分别为 `4k, 4k+1, 4k+2, 4k+3`
- Then：PaddingLen 分别为 `0, 3, 2, 1`；下一条 Fence 起点 4B 对齐

**用例 RBF-LEN-003（TailMeta 长度影响）**
- Given：PayloadLen = 10, TailMetaLen = 5（总计 15，需要 1 字节 padding）
- Then：
  - PaddingLen = 1
  - FrameLength = 24 + 10 + 5 + 1 = 40

### 1.5 Payload 含 Fence 字节

**用例 RBF-FENCE-IN-PAYLOAD-001**
- Given：Payload（或 TailMeta）恰好包含 `52 42 46 31`（Fence 值 `RBF1`）
- Then：
  - CRC32C 校验通过时，正常解析
  - resync 时遇到 Payload 中的 Fence 值，CRC 校验失败应继续向前扫描

### 1.6 FrameDescriptor 位域格式

> 对应规范条款 @[F-FRAME-DESCRIPTOR-LAYOUT]

**位域布局**（引用自 rbf-format.md v0.40）：
- Bit 31：IsTombstone（0=Valid，1=Tombstone）
- Bit 30-29：PaddingLen（0-3）
- Bit 28-16：Reserved（MVP MUST 为 0）
- Bit 15-0：TailMetaLen（0-65535）

**用例 RBF-DESCRIPTOR-001（MVP 有效值枚举）**

| 描述符值 | 二进制 | IsTombstone | PaddingLen | TailMetaLen | 有效性 |
|----------|--------|-------------|------------|-------------|--------|
| `0x00000000` | `0b0...0` | false | 0 | 0 | ✅ |
| `0x20000000` | bit30 set | false | 1 | 0 | ✅ |
| `0x40000000` | bit30-29=10 | false | 2 | 0 | ✅ |
| `0x60000000` | bit30-29=11 | false | 3 | 0 | ✅ |
| `0x80000000` | bit31 set | true | 0 | 0 | ✅ |
| `0x80000001` | bit31+bit0 | true | 0 | 1 | ✅ |
| `0x0000FFFF` | bit15-0 all set | false | 0 | 65535 | ✅ |
| `0xE000FFFF` | bit31+30-29+15-0 | true | 3 | 65535 | ✅ |

**用例 RBF-DESCRIPTOR-002（无效值：Reserved bits 非零）**

| 描述符值 | 二进制 | 失败原因 |
|----------|--------|----------|
| `0x00010000` | bit16 set | Reserved bit 16 非零 |
| `0x10000000` | bit28 set | Reserved bit 28 非零 |
| `0x1FFF0000` | bit28-16 all set | Reserved bits 28-16 全部非零 |

### 1.7 TrailerCodeword 编解码

> 对应规范条款 @[F-TRAILER-CRC-BIG-ENDIAN]

**用例 RBF-TRAILER-001（端序验证）**
- Given：TrailerCodeword 字节序列 = `[AA BB CC DD] [11 22 33 44] [55 66 77 88] [99 AA BB CC]`
- Then：
  - `TrailerCrc32C` = `0xAABBCCDD`（按 **BE** 读取前 4 字节）
  - `FrameDescriptor` = `0x44332211`（按 LE 读取第 5-8 字节）
  - `FrameTag` = `0x88776655`（按 LE 读取第 9-12 字节）
  - `TailLen` = `0xCCBBAA99`（按 LE 读取第 13-16 字节）

---

## 2. 损坏与恢复

### 2.1 有效 Frame（正例）

**用例 RBF-OK-001（Valid：PayloadLen=0, TailMetaLen=0）**
- Given：`Fence` 值为 `RBF1`（`52 42 46 31`）；Payload 和 TailMeta 均为空。
- When：写入最小帧（24 字节）：
  - `HeadLen = 24`（u32 LE）
  - Payload = 空
  - TailMeta = 空
  - PaddingLen = 0
  - PayloadCrc32C = crc32c(空) = `0x00000000`（u32 LE，空输入的 CRC32C 标准结果）
  - TrailerCodeword：
    - FrameDescriptor = `0x00000000`（Valid + PaddingLen=0 + TailMetaLen=0）
    - FrameTag = 任意值
    - TailLen = 24
    - TrailerCrc32C = 计算值（u32 **BE**）
- Then：
  - `HeadLen == TailLen == 24`
  - Frame 起点 4B 对齐
  - 双 CRC 均校验通过
  - 反向扫描能枚举到该条 frame

**用例 RBF-OK-002（Tombstone：PayloadLen=3, TailMetaLen=0）**
- Given：PayloadLen=3（任意 3 字节 payload），TailMetaLen=0。
- When：写入帧：
  - HeadLen = 24 + 3 + 0 + 1 = 28（PaddingLen=1）
  - Payload = 3 字节
  - TailMeta = 空
  - Padding = 1 字节
  - PayloadCrc32C = crc32c(Payload + Padding)
  - TrailerCodeword：
    - FrameDescriptor = `0xA0000000`（Tombstone + PaddingLen=1）
    - TailLen = 28
- Then：
  - Frame 为 Tombstone
  - 双 CRC 均校验通过
  - 反向扫描（`showTombstone=true`）能枚举到该条 frame
  - 反向扫描（`showTombstone=false`）会过滤该帧

**用例 RBF-OK-003（TailMeta 非空）**
- Given：PayloadLen=10, TailMetaLen=6
- When：写入帧：
  - HeadLen = 24 + 10 + 6 + 0 = 40（PaddingLen=0，因为 16 % 4 == 0）
  - PayloadCrc32C = crc32c(Payload + TailMeta)
  - FrameDescriptor 包含 TailMetaLen=6
- Then：
  - FrameDescriptor 的 bit15-0 = 6
  - 可以从 RbfFrameInfo 获取 TailMetaLen

**用例 RBF-OK-004（PaddingLen 覆盖：0/1/2/3）**
- Given：分别构造 4 条帧，使 `(PayloadLen + TailMetaLen) % 4` 为 `0,3,2,1`
- Then：其 PaddingLen 分别为 `0,1,2,3`，FrameDescriptor 的 bit30-29 对应编码

### 2.2 损坏 Frame（负例）

**用例 RBF-BAD-001（TrailerCrc32C 不匹配）**
- Given：篡改 TrailerCodeword 中的 FrameDescriptor / FrameTag / TailLen 任意 1 byte
- Then：TrailerCrc32C 校验失败；反向扫描停止

**用例 RBF-BAD-002（PayloadCrc32C 不匹配）**
- Given：篡改 Payload / TailMeta / Padding 任意 1 byte
- Then：PayloadCrc32C 校验失败；`ReadFrame` 返回错误

**用例 RBF-BAD-003（Frame 起点非 4B 对齐）**
- Given：构造 TailLen 导致 Frame 起点 `% 4 != 0`
- Then：视为损坏

**用例 RBF-BAD-004（TailLen 超界）**
- Given：`TailLen > fileSize` 或 `TailLen < 24`（最小帧长度）
- Then：视为损坏

**用例 RBF-BAD-005（FrameDescriptor 非法值：Reserved bits 非零）**
- Given：FrameDescriptor 取值满足 `(value & 0x1FFF0000) != 0`（即 bits 28-16 任一为 1）
- Examples：`0x00010000`, `0x10000000`, `0x1FFF0000`（见 §1.6 RBF-DESCRIPTOR-002）
- Then：Reader MUST 视为 framing 失败（拒绝该帧）

**用例 RBF-BAD-006（TailLen != HeadLen）**
- Given：TailLen 与实际帧长度不一致
- Then：`ReadFrame` 返回 framing 错误

**用例 RBF-BAD-007（PaddingLen 与实际不符）**
- Given：FrameDescriptor 中的 PaddingLen 与 `(4 - ((PayloadLen + TailMetaLen) % 4)) % 4` 计算值不符
- Then：Reader SHOULD 视为 framing 失败（PayloadLength 计算为负数或溢出）

### 2.3 截断测试

**用例 RBF-TRUNCATE-001**
- Given：原文件 `[Fence][Frame][Fence]`，截断为 `[Fence][Frame]`
- Then：逆向扫描不得将不完整尾部视为有效 frame

**用例 RBF-TRUNCATE-002**
- Given：截断在 frame 中间
- Then：逆向扫描不得将其视为有效 frame（Resync 继续寻找更早的有效 fence/frame）

---

## 3. SizedPtr / ReadFrame 行为

> 对应 `rbf-interface.md` 的 `ReadFrame(SizedPtr)`（Result-Pattern：`AteliaResult<RbfFrame>`）。

### 3.1 成功读取（正例）

**用例 READFRAME-OK-001（有效 SizedPtr）**
- Given：`SizedPtr` 的 `Offset` 指向一条通过 framing/CRC 校验的帧起点，且 `Length == HeadLen`
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `AteliaResult<RbfFrame>.IsSuccess == true`

### 3.2 读取失败（负例）

**用例 READFRAME-BAD-001（Offset 越界）**
- Given：`ptr.Offset >= fileSize`
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-002（Offset 非 4B 对齐）**
- Given：`ptr.Offset % 4 != 0`
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-003（Length 与 HeadLen 不匹配）**
- Given：`ptr.Offset` 指向一条合法帧起点，但 `ptr.Length != HeadLen`
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `IsFailure == true`

**用例 READFRAME-BAD-004（指向位置无合法帧）**
- Given：`ptr.Offset` 落在文件内且 4B 对齐，但该位置 framing/CRC 校验失败
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `IsFailure == true`

### 3.3 CRC 职责分离

**用例 READFRAME-CRC-001（ReadFrame 校验 PayloadCrc）**
- Given：一条帧的 TrailerCrc 正确，但 PayloadCrc 被篡改
- When：调用 `file.ReadFrame(ptr, buffer)`
- Then：返回 `IsFailure == true`，错误类型为 CRC 相关

**用例 READFRAME-CRC-002（ScanReverse 不校验 PayloadCrc）**
- Given：一条帧的 TrailerCrc 正确，但 PayloadCrc 被篡改
- When：调用 `file.ScanReverse()`
- Then：该帧仍然会被枚举出来（ScanReverse 只校验 TrailerCrc）

---

## 4. ScanReverse 接口行为

> 对应 rbf-interface.md v0.30 的 ScanReverse 语义条款。

### 4.1 空序列

**用例 SCAN-EMPTY-001（空文件返回零元素）**
- Given：文件内容 = `[Fence]`（仅 HeaderFence）
- When：调用 `file.ScanReverse()`
- Then：
  - `foreach` 循环执行 0 次
  - `GetEnumerator().MoveNext()` 首次返回 `false`
  - 不抛出任何异常

**用例 SCAN-EMPTY-002（仅 Tombstone 帧 — 测试过滤行为）**
- Given：文件包含 1 条 Tombstone 帧（`IsTombstone == true`）
- When & Then：
  - **默认行为**（`showTombstone=false`）：
    - `file.ScanReverse()` 返回空序列（0 条帧）
    - Tombstone 帧被过滤
    - 符合 @[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]
  - **显式包含 Tombstone**（`showTombstone=true`）：
    - `file.ScanReverse(showTombstone: true)` 返回 1 条帧
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

### 4.3 逆向扫描只用 TrailerCrc

**用例 SCAN-TRAILER-CRC-001（逆向扫描只校验 TrailerCrc）**
- Given：文件包含一条帧，其 TrailerCrc 正确但 PayloadCrc 错误
- When：调用 `file.ScanReverse()`
- Then：
  - 该帧被正常枚举（返回 RbfFrameInfo）
  - 符合 @[R-REVERSE-SCAN-USES-TRAILER-CRC]

**用例 SCAN-TRAILER-CRC-002（TrailerCrc 损坏则停止）**
- Given：文件包含两条帧，第二条帧的 TrailerCrc 损坏
- When：调用 `file.ScanReverse()`
- Then：
  - 逆向扫描在遇到损坏帧时停止
  - 只返回第二条之后的有效帧（如果有）
  - 符合 @[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD] 的硬停止策略

### 4.4 Current 生命周期

**用例 SCAN-LIFETIME-001（Current 在 MoveNext 后失效）**
- Given：文件包含 2 条帧 `[Frame1][Frame2]`
- When：
  ```csharp
  var enumerator = file.ScanReverse().GetEnumerator();
  enumerator.MoveNext();
  var frame1 = enumerator.Current;  // 捕获 Frame2 的 RbfFrameInfo（逆序）
  enumerator.MoveNext();
  // 此时 frame1 仍有效（RbfFrameInfo 是值类型，包含帧元信息）
  ```
- Then：
  - RbfFrameInfo 是值类型，不存在生命周期问题
  - 若需读取实际数据，调用 `frame1.ReadFrame(buffer)` 或 `frame1.ReadPooledFrame()`

### 4.5 多次 GetEnumerator

**用例 SCAN-MULTI-ENUM-001（独立枚举器）**
- Given：文件包含 3 条帧 `[Frame1][Frame2][Frame3]`
- When：
  ```csharp
  var seq = file.ScanReverse();
  var enum1 = seq.GetEnumerator();
  var enum2 = seq.GetEnumerator();
  enum1.MoveNext(); enum1.MoveNext();  // enum1 前进 2 步
  enum2.MoveNext();                     // enum2 前进 1 步
  ```
- Then：
  - `enum1.Current` 指向 Frame2（从尾部数第 2 帧）
  - `enum2.Current` 指向 Frame3（从尾部数第 1 帧）
  - 两个枚举器互不干扰

### 4.6 foreach 兼容性

**用例 SCAN-FOREACH-001（duck-typed foreach）**
- Given：文件包含 N 条帧
- When：
  ```csharp
  int count = 0;
  foreach (var frameInfo in file.ScanReverse())
  {
      count++;
      // frameInfo 可正常访问 Ticket, Tag, PayloadLength, TailMetaLength
  }
  ```
- Then：
  - `count == N`
  - 每次迭代 `frameInfo` 都是有效的 `RbfFrameInfo`

**用例 SCAN-LINQ-FAIL-001（LINQ 不可用）**
- Given：调用方尝试使用 LINQ
- When：
  ```csharp
  // 编译错误：RbfReverseSequence 不包含 'Where' 定义
  var filtered = file.ScanReverse().Where(f => !f.IsTombstone);
  ```
- Then：编译失败（这是预期行为，非缺陷）

### 4.7 ref struct 约束

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
  async Task ProcessAsync(IRbfFile file) {
      var seq = file.ScanReverse();
      await Task.Delay(1);
      // 编译错误：ref struct 不能跨 await 边界
      foreach (var frame in seq) { }
  }
  ```
- Then：编译失败（ref struct 不能跨 await 边界）

### 4.8 并发修改行为

**用例 SCAN-MUTATION-001（并发修改行为未定义）**
- Given：文件包含 N 条帧
- When：在 `foreach` 枚举期间，另一线程追加新帧到文件
- Then：
  - 行为为**未定义**（规范不保证任何特定行为）
  - 实现 MAY fail-fast（抛出异常）
  - 调用方 MUST 在稳定快照上使用 `ScanReverse()`

**用例 SCAN-MUTATION-002（稳定快照正例）**
- Given：先完成所有写入，然后使用 `ScanReverse()`
- When：调用 `file.ScanReverse()` 并完整遍历
- Then：
  - 所有帧正常枚举
  - 无竞争条件风险

---

## 5. RbfFrameInfo 与 TailMeta

> 对应 rbf-interface.md 的 RbfFrameInfo 和 TailMeta API。

### 5.1 RbfFrameInfo 属性

**用例 FRAMEINFO-001（属性完整性）**
- Given：通过 ScanReverse 获取的 RbfFrameInfo
- Then：可访问以下属性：
  - `Ticket`：帧位置凭据（SizedPtr）
  - `Tag`：帧标签（uint）
  - `PayloadLength`：Payload 长度（int）
  - `TailMetaLength`：TailMeta 长度（int）
  - `IsTombstone`：是否为墓碑帧（bool）

**用例 FRAMEINFO-002（TailMetaLength 范围）**
- Given：TailMetaLen 在 FrameDescriptor 中为 16-bit
- Then：`TailMetaLength` 值域为 `0 <= TailMetaLength <= 65535`

### 5.2 ReadTailMeta

**用例 TAILMETA-001（读取 TailMeta）**
- Given：一条帧包含 TailMeta（例如 10 字节）
- When：通过 `frameInfo.ReadTailMeta(buffer)` 或 `frameInfo.ReadPooledTailMeta()` 读取
- Then：
  - 返回的 RbfTailMeta 包含正确的 TailMeta 数据
  - TailMeta.Length == 10

**用例 TAILMETA-002（空 TailMeta）**
- Given：一条帧的 TailMetaLength == 0
- When：调用 `frameInfo.ReadTailMeta(buffer)`
- Then：返回成功，TailMeta 为空 span

---

## 6. 条款映射

| 条款 ID | 规范条款 | 对应测试用例 |
|---------|----------|--------------|
| `[F-FILE-STARTS-WITH-HEADER-FENCE]` | HeaderFence | RBF-EMPTY-001, SCAN-EMPTY-001 |
| `[F-FENCE-IS-SEPARATOR-NOT-FRAME]` | Fence 语义 | RBF-SINGLE-001, RBF-DOUBLE-001 |
| `[F-FRAMEBYTES-LAYOUT]` | FrameBytes 布局 (v0.40) | RBF-LEN-001/002/003, RBF-OK-001/002/003/004 |
| `[F-FRAME-TAG-WIRE-ENCODING]` | FrameTag 编码 (4B, 在 TrailerCodeword) | RBF-TRAILER-001 |
| `[F-FRAME-DESCRIPTOR-LAYOUT]` | FrameDescriptor 位域格式 | RBF-DESCRIPTOR-001/002, RBF-OK-001/002/003/004, RBF-BAD-005 |
| `[F-TRAILER-CRC-BIG-ENDIAN]` | TrailerCrc32C 按 BE 存储 | RBF-TRAILER-001 |
| `[F-TRAILER-CRC-COVERAGE]` | TrailerCrc32C 覆盖范围 | RBF-BAD-001, SCAN-TRAILER-CRC-001/002 |
| `[F-PAYLOAD-CRC-COVERAGE]` | PayloadCrc32C 覆盖范围 | RBF-OK-001/002, RBF-BAD-002, READFRAME-CRC-001/002 |
| `[F-PADDING-CALCULATION]` | Padding 长度计算 | RBF-LEN-002, RBF-OK-004 |
| `[S-RBF-SIZEDPTR-WIRE-MAPPING]` | SizedPtr 与 Wire Format 映射 | READFRAME-OK-001, READFRAME-BAD-001/002/003/004 |
| `[R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE]` | Resync 行为 | RBF-TRUNCATE-001/002 |
| `[R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD]` | 逆向扫描 | RBF-SINGLE-001, RBF-DOUBLE-001, SCAN-TRAILER-CRC-002 |
| `[R-REVERSE-SCAN-USES-TRAILER-CRC]` | 逆向扫描只用 TrailerCrc | SCAN-TRAILER-CRC-001, READFRAME-CRC-002 |
| `[A-RBF-REVERSE-SEQUENCE]` | RbfReverseSequence | SCAN-FOREACH-001, SCAN-MULTI-ENUM-001 |
| `[S-RBF-SCANREVERSE-NO-IENUMERABLE]` | 不实现 IEnumerable | SCAN-LINQ-FAIL-001, SCAN-REFSTRUCT-001/002 |
| `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` | 空序列合法 | SCAN-EMPTY-001, SCAN-EMPTY-002 |
| `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]` | Tombstone 默认过滤 | SCAN-EMPTY-002, SCAN-TOMBSTONE-FILTER-001, RBF-OK-002 |
| `[S-RBF-SCANREVERSE-CURRENT-LIFETIME]` | Current 生命周期 | SCAN-LIFETIME-001 |
| `[S-RBF-SCANREVERSE-CONCURRENT-MUTATION]` | 并发修改行为未定义 | SCAN-MUTATION-001/002 |
| `[S-RBF-SCANREVERSE-MULTI-GETENUM]` | 多次 GetEnumerator | SCAN-MULTI-ENUM-001 |
| `[A-RBF-FRAME-INFO]` | RbfFrameInfo 定义 | FRAMEINFO-001/002 |
| `[S-RBF-FRAMEINFO-USERMETALEN-RANGE]` | TailMetaLength 值域 | FRAMEINFO-002 |

---

## 7. 推荐的黄金文件组织方式

- `test-data/format/rbf/`：手工构造的 RBF 二进制片段（包含 OK 与 BAD）

每个黄金文件建议配一个小的 `README.md`，写明：
- 编码输入（Payload/TailMeta/Tag）
- 期望的 FrameDescriptor 值
- 期望错误类型（FramingError/CrcMismatch 等）

---

## 8. RBF3独立向量与生产资格

本节对应[格式合同](rbf-format.md)及[接口合同](rbf-interface.md)，使用生产Header/Fence `52 42 46 33`（LE `0x33464252`），不复用历史byte RBF2文件。以下hex由独立bitwise CRC32C参考计算：多项式 `0x82F63B78`，init/final XOR均为 `0xFFFFFFFF`，标准检查串 `123456789` 的CRC为 `0xE3069283`；Trailer对 `descriptor LE || tag LE || U LE` 逐byte逆序计算后按BE存储。GOLD-001–003是tag0的字面参考；正式探针的6个Python fixtures使用tag11/17/23/29/31/37，属于另一组独立wire，不把其通过写成这三组字节已逐项公共writer比对。

### 8.1 独立wire参考

下列完整文件均为Header4 + FrameBytes L + Fence4，tag/meta/padding均为0；ticket.Offset=4、ticket.Length=L bytes。

**RBF3-GOLD-001：空payload，Zero成功。** L=28，U=7，K=0，PayloadCRC=0，TrailerCRC=`0xEC007B47`。

```text
52 42 46 33
07 00 00 00
00 00 00 00
EC 00 7B 47 00 00 00 00 00 00 00 00 07 00 00 00
00 00 00 00
52 42 46 33
```

**RBF3-GOLD-002：payload恰为Fence word，tiny选择Key1。** payload=`52 42 46 33`，L=32，U=8，K=1；PayloadCRC=`0x88A3793B`（LE存储），TrailerCRC=`0x931347CA`（BE存储）。编码body不存在aligned Fence。

```text
52 42 46 33
08 00 00 00
53 42 46 33 3A 79 A3 88
92 13 47 CA 01 00 00 00 01 00 00 00 09 00 00 00
01 00 00 00
52 42 46 33
```

**RBF3-GOLD-003：合法高Key与encoded TailLen高位。** 空payload，L=28，U=7，K=`0x80000001`，两plaintext CRC同GOLD-001。encoded TailLenUnits=`0x80000006`，先XOR得到U7再验范围。此向量验证合法wire/reader与Key重建，不要求默认Zero策略为该内容选择高Key。

```text
52 42 46 33
07 00 00 00
01 00 00 80
ED 00 7B C7 01 00 00 80 01 00 00 80 06 00 00 80
01 00 00 80
52 42 46 33
```

### 8.2 长度、CRC及兼容资格

| ID | 独立输入/触发 | 期望 |
| --- | --- | --- |
| RBF3-LEN-001 | U=7、U=2^26-1 | 对应L=28、M=268435452；ticket及读取范围仍为bytes |
| RBF3-LEN-002 | 完整U=0..6、2^26、uint.MaxValue、0x40000007 | range先于shift；拒绝且不得mask/别名成28B |
| RBF3-LEN-003 | U低2bits非零（包括最小U7） | 合法，不复用RBF1长度alignment guard |
| RBF3-CRC-001 | Trailer用LE(U)封CRC，再XOR | 按原wire字节通过；误改成LE(L)后验CRC应拒绝 |
| RBF3-KEY-001 | 高Key、rawKey==F、encoded Trailer word==F | 高Key合法；后两者拒绝；Key不能因Fence下界省检查 |
| RBF3-COMPAT-001 | RBF1原始黄金文件/旧ticket、旧最大容量 | 只读原字节与票据不变；全结构主链资格；内容CRC留checked-read |
| RBF3-COMPAT-002 | RBF1可写、RBF2或未知Header、0–3B Header | 在任何修改前拒绝；bytes不变 |
| RBF3-ADDR-001 | 合法frame起点=SizedPtr.MaxOffset | 末帧末端/Fence可超MaxOffset；不得因单位变更拒绝合法起点 |

### 8.3 全byte前缀、恢复与再次终止

在GOLD-001/002/003及多帧真实RBF3写出上，对最后帧的每个byte cut独立分类；先建立真实B并检查直接前驱结构。原像只作测试oracle，恢复输入不能获取writer计划、Key选择过程或完整目标文件。

| ID | 已有前缀 | 期望 |
| --- | --- | --- |
| RBF3-TAIL-001 | 无suffix/闭合尾 | None；不写不截 |
| RBF3-TAIL-002 | 真实B后的任意1–3B raw HeadLenUnits | Truncated到B，不猜U、无低2bits guard |
| RBF3-TAIL-003 | 完整合法U且4≤R<L-4 | 未完成body截到B，不求Key/partial Trailer或合成墓碑 |
| RBF3-TAIL-004 | 完整body、已有Key 0–3B | K*=encodedTailUnits XOR U；保留body/原ticket，补原Key/Fence缺失后缀 |
| RBF3-TAIL-005 | 完整Key、已有Fence 0–3B | 仅补原Fence缺失后缀 |
| RBF3-TAIL-006 | 完整TrailerCRC/descriptor/Key/padding/Fence矛盾 | 拒绝且不改，不fallback越过首坏候选 |
| RBF3-TAIL-007 | structure健康但payload/PayloadCRC坏，包含待补尾/直接前驱 | Open依结构裁决，原坏内容保留；checked-read拒绝，不能丢帧回退 |
| RBF3-TAIL-008 | 恢复补写Key/Fence每个byte前缀再次终止、SetLength前/后 | 重复恢复同一帧或同一截断边界，幂等；不改旧CRC/body |
| RBF3-TAIL-009 | read-only输入需动作；SetLength/write/flush抛错 | 只读bytes不变；修改异常关闭handle，禁止改动作/复用cache |
| RBF3-TAIL-010 | legacy嵌套假尾/平行伪链输入，以及正常RBF3 encoded body | RBF1连Header主链；RBF3 marker-free正常前缀仅真实Fence可定位，不以局部CRC倒推成员 |

### 8.4 读取入口与观测资格

- 两profile的普通ticket ReadFrame/pooled每次检查两CRC；info管线复用创建时TrailerCRC/Key资格，每次完整读检查PayloadCRC，不要求重复验TrailerCRC。普通ticket读不验外部Fence。forward/reverse/info/meta/boundary/cache及零meta、meta相位1–3、跨chunk均需覆盖，reverse仍不能读头；离线共享Write输入须冻结。
- Header-only逻辑读4B；RBF3非空健康结构Open请求总字节≤39且独立于N/L；异常逆扫≤M+7。记录请求/返回字节、read调用与配置，不能将逻辑上界写成物理SSD或延迟保证。
- Append和Builder真输入使用独立LE(U)/CRC裁判；全部Result拒绝在首次footer修改前。Builder准备异常取消而非同实例重试；真实Push/flush异常永久fault；Pool只归还一次，wire cache不被解码修改。
- 生产进程终止覆盖Append、截尾前/后、补原Key/Fence各byte前缀与再次恢复。有限READY检查点结合writer前缀证明，不声称Windows syscall内任意时机或断电已验证。

**本轮生产结果**：0999851对应源码内容的Release RBF测试670/670通过，其中RbfWireCodecTests有59项独立scalar codec资格；正式Python6fixtures×Off/Slots16共12views及两份真实writer全wire互证通过。658个Append/Builder小帧cuts、4个Header拒绝、35次managed READY kill、I/O故障及内容/结构正交测试、9个健康Open与2个异常计量、48个7样本成本rows已记录并独立复核。真实最大FrameBytes268435452的caller/pooled和phase1 meta通过。精确身份、PDB源码checksum、扫描跨度与聚合请求的区别、性能分配及独立裁判限度见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)；GOLD-001–003仍按上文保持字面参考身份。下游、整仓solution、冷盘/断电/任意syscall终止、pack/publish不属于本轮RBF资格。

## 变更日志

| 日期 | 版本 | 变更 |
|------|------|------|
| 2026-10-03 | 0.41 | 保留RBF1历史byte-wire身份；增加独立RBF3 units/Key/CRC参考与三动作/双读/异常资格；生产测试与另一组Python wire互证结果见实施记录§11 |
| 2026-02-01 | 0.40 | **Breaking Change 适配**：完整重写以对齐 rbf-format.md v0.40；FrameBytes 布局重构（旧 FrameStatus → 新 FrameDescriptor + TailMeta + 固定 TrailerCodeword）；双 CRC 机制（PayloadCrc + TrailerCrc）；最小帧长度 20→24；删除所有 FrameStatus/StatusLen 相关测试向量；新增 FrameDescriptor/TailMeta/TrailerCodeword 测试向量；新增 §3.3 CRC 职责分离测试；新增 §4.3 逆向扫描 TrailerCrc 测试；新增 §5 RbfFrameInfo 与 TailMeta 测试 |
| 2026-01-12 | 0.13 | **Tombstone 过滤测试**：更新 SCAN-EMPTY-002 拆分为两个子用例；新增 SCAN-TOMBSTONE-FILTER-001 |
| 2026-01-07 | 0.12 | **SizedPtr 迁移**：将旧版地址指针相关测试向量迁移为 `SizedPtr` + `ReadFrame` 行为向量 |
| 2025-12-28 | 0.11 | 更新关联规范版本：rbf-format.md v0.15 → v0.16, rbf-interface.md v0.16 → v0.17 |
| 2025-12-28 | 0.10 | 更新关联规范版本：rbf-interface.md v0.15 → v0.16 |
| 2025-12-28 | 0.9 | 新增 §4 ScanReverse 接口行为 |
| 2025-12-28 | 0.8 | 适配 rbf-format.md v0.15：修正 RBF-BAD-004 最小帧长度边界 |
| 2025-12-25 | 0.7 | 适配 rbf-format.md v0.14：FrameStatus 位域格式 |
| 2025-12-24 | 0.6 | 适配 rbf-format.md v0.12：Pad→FrameStatus |
| 2025-12-24 | 0.5 | 适配 rbf-format.md v0.11：FrameTag 扩充为 4B |
| 2025-12-23 | 0.4 | 适配 rbf-format.md v0.10+：术语更新 |
| 2025-12-23 | 0.3 | 适配 rbf-format.md v0.10：统一 Fence 术语 |
| 2025-12-23 | 0.2 | 适配 rbf-format.md v0.9：更新条款 ID 映射 |
| 2025-12-22 | 0.1 | 初始版本：从 mvp-test-vectors.md 提取 Layer 0 测试向量 |
