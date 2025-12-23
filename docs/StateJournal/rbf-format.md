# RBF 二进制格式规范

> **状态**：Draft
> **版本**：0.6
> **创建日期**：2025-12-22
> **接口契约**：[rbf-interface.md](rbf-interface.md)

---

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 概述

本文档定义 RBF（Reversible Binary Framing）的二进制格式规范。RBF 是一种 append-only 的日志帧格式，提供：
- CRC32C 完整性校验
- 4 字节对齐
- Magic 分隔符支持逆向扫描（Reversible = backward scan / resync）
- 崩溃恢复与 Resync 机制

**与 rbf-interface.md 的关系**：
- 本文档（Layer 0）定义 RBF 的二进制格式细节
- [rbf-interface.md](rbf-interface.md) 定义上层（Layer 1）的接口契约
- 上层无需了解本文档的实现细节，仅需依赖接口文档

**文档层次**：

```
┌─────────────────────────────────────┐
│  mvp-design-v2.md (StateJournal)    │
│  - 使用 RBF 接口                     │
│  - 定义 RecordKind, ObjectKind 等   │
└─────────────────┬───────────────────┘
                  │ 依赖
┌─────────────────▼───────────────────┐
│  rbf-interface.md                    │
│  - Layer 0/1 的对接契约              │
│  - 定义 FrameTag, Address64 等       │
└─────────────────┬───────────────────┘
                  │ 实现
┌─────────────────▼───────────────────┐
│  rbf-format.md (本文档)              │
│  - RBF 完整二进制格式规范            │
│  - Genesis Header, Frame 结构       │
└─────────────────────────────────────┘
```

---

## 2. 文件结构

### 2.1 Magic 定义

**`[F-MAGIC-DEFINITION]`**

| 字段 | ASCII | Hex 字节序列 | 编码约定 |
|------|-------|--------------|----------|
| Magic | `RBF1` | `52 42 46 31` | ASCII 字节序列写入（非 u32 端序），读取时按字节匹配 |

> **命名说明**：`RBF` = Reversible Binary Framing，`1` = 版本号。

### 2.2 Genesis Header（文件头）

**`[F-GENESIS-HEADER]`**

每个 RBF 文件以 Magic 开头作为 Genesis Header（创世头）：

- 偏移 0，长度 4 字节
- 值：见 `[F-MAGIC-DEFINITION]`

**`[F-GENESIS-EMPTY-FILE]`** 空文件约束：

- 新建的 RBF 文件长度为 4 字节（仅含 Genesis Header）
- `FileLength == 4` 表示"无任何 Frame"

### 2.3 文件整体布局

```
[Genesis][Frame 1][Frame 2][...]
   4B      var      var
```

每个 Frame 后紧跟一个 Magic 作为分隔符（见 §5）。

---

## 3. Frame 结构

### 3.1 Frame 二进制布局

**`[F-FRAME-LAYOUT]`**

| 偏移 | 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|------|
| 0 | HeadLen | u32 LE | 4 | Frame 总长度（不含尾部 Magic）|
| 4 | Payload | bytes | N | Tag(1B) + PayloadBody(N-1B) |
| 4+N | Pad | bytes | 0-3 | 全 0，使 (N + Pad) 为 4 的倍数 |
| 4+N+Pad | TailLen | u32 LE | 4 | == HeadLen |
| 8+N+Pad | CRC32C | u32 LE | 4 | 见 `[F-CRC32C-COVERAGE]` |
| 12+N+Pad | Magic | bytes | 4 | 见 `[F-MAGIC-DEFINITION]` |

**`[F-FRAMETAG-WIRE-ENCODING]`** Payload 结构：

- Payload 的第 1 字节为 **Tag**（FrameTag）
- `PayloadLen = 1 (Tag) + PayloadBodyLen`
- RBF 层读写 Tag 但不解释语义（`0x00` = Padding 除外）
- FrameTag 定义见 [rbf-interface.md](rbf-interface.md) 的 `[F-FRAMETAG-DEFINITION]`

### 3.2 长度字段

**`[F-HEADLEN-TAILLEN-SYMMETRY]`** HeadLen/TailLen 对称性：

- `HeadLen == TailLen` MUST 成立
- 若不等，视为帧损坏

**`[F-HEADLEN-FORMULA]`** HeadLen 计算公式：

```
HeadLen = 4 (HeadLen) + PayloadLen + PadLen + 4 (TailLen) + 4 (CRC32C)
        = 12 + PayloadLen + PadLen
```

**`[F-PADLEN-FORMULA]`** PadLen 计算公式：

```
PadLen = (4 - (PayloadLen % 4)) % 4
```

> **推导说明**：Pad 的目的是使 `PayloadLen + PadLen` 为 4 的倍数。
> 当 `PayloadLen % 4 == 0` 时，`PadLen = 0`；
> 当 `PayloadLen % 4 == k (1≤k≤3)` 时，`PadLen = 4 - k`。

**`[F-FRAME-4B-ALIGNMENT]`** 对齐约束：

- `HeadLen % 4 == 0` MUST 成立
- Frame 起点（HeadLen 字段位置）MUST 4B 对齐
- **最小 HeadLen**：`>= 12`（空 payload + 0 pad = `4+0+0+4+4 = 12`）

### 3.3 Pad 填充

**`[F-PAD-ZERO-FILL]`**

- Pad 字节 MUST 全为 0
- Reader 可忽略 Pad 内容（仅校验长度）
- Writer MUST 写入 0 值

**Pad 长度示例**（Informative，由 `[F-PADLEN-FORMULA]` 推导）：

| PayloadLen | PayloadLen % 4 | PadLen |
|------------|----------------|--------|
| 0 | 0 | 0 |
| 1 | 1 | 3 |
| 2 | 2 | 2 |
| 3 | 3 | 1 |
| 4 | 0 | 0 |
| 5 | 1 | 3 |

---

## 4. CRC32C 校验

### 4.1 覆盖范围

**`[F-CRC32C-COVERAGE]`** CRC32C 覆盖范围：

```
CRC32C = crc32c(Payload + Pad + TailLen)
```

覆盖：
- ✅ Payload（全部 N 字节）
- ✅ Pad（0-3 字节填充）
- ✅ TailLen（4 字节）

不覆盖：
- ❌ HeadLen（因为逆向扫描时先读 TailLen）
- ❌ CRC32C 本身
- ❌ Magic 分隔符

### 4.2 CRC32C 算法

**`[F-CRC32C-ALGORITHM]`**

使用 CRC32C（Castagnoli 多项式），与 iSCSI、btrfs、ext4 等标准一致。

**多项式表示**：

| 形式 | 值 | 说明 |
|------|-----|------|
| Normal | `0x1EDC6F41` | 数学多项式直接编码 |
| Reflected | `0x82F63B78` | 比特反射后的形式 |

**本规范采用 Reflected I/O 约定**：

- 输入数据：reflected（每字节比特反射后处理）
- 输出 CRC：reflected（结果比特反射后输出）
- 初始值：`0xFFFFFFFF`
- 最终异或：`0xFFFFFFFF`

> **等价实现**：与 `.NET System.IO.Hashing.Crc32C`、Intel CRC32 硬件指令、iSCSI CRC 语义一致。
> 实现时直接使用 `System.IO.Hashing.Crc32C` 即可，无需手动处理反射。

### 4.3 校验失败处理

**`[F-CRC-FAIL-REJECT]`** CRC 校验失败：

- CRC32C 校验不匹配 MUST 视为帧损坏（Frame Corruption）
- Reader MUST NOT 将损坏帧作为有效数据返回
- 恢复流程中遇到 CRC 失败 SHOULD 触发 Resync

**`[F-FRAMING-FAIL-REJECT]`** Framing 校验失败：

以下任一情况 MUST 视为帧损坏：
- `HeadLen != TailLen`（长度不对称）
- `HeadLen % 4 != 0`（非 4 字节对齐）
- `RecordStart < GenesisLen`（越界：Frame 起点位于 Genesis Header 之前）
- `RecordEnd > FileLength`（越界：Frame 超出文件边界）
- Magic 不匹配（与 Genesis Header 的 Magic 不一致）

---

## 5. Magic 分隔符

### 5.1 Magic 语义

**`[F-MAGIC-IS-FENCE]`** Magic 是 Fence：

- Magic 不属于 Frame，是 Frame 之间的"栅栏"（fencepost）
- Genesis Header 是第一个 Magic
- 每个 Frame 结束后追加一个 Magic

### 5.2 Magic 位置

```
File: [Magic][Frame1][Magic][Frame2][Magic]...
       ↑      ↑       ↑       ↑       ↑
     Genesis Frame1  Fence  Frame2  Fence
               End           End
```

**`[F-MAGIC-FRAME-SEPARATOR]`** Magic 是 Frame Separator：

- 空文件先写 Genesis Magic
- 每写完一条 Frame 后追加 Magic 作为分隔符

---

## 6. 写入流程

**`[F-FRAME-WRITE-SEQUENCE]`** Frame 写入步骤（MUST 按顺序执行）：

| 步骤 | 操作 | 说明 |
|------|------|------|
| 0 | 确保文件以 Magic 结束 | 新文件先写 Genesis Header |
| 1 | 写入 HeadLen 占位（先写 0） | 预留位置 |
| 2 | 顺序写入 Payload | 实际数据 |
| 3 | 写入 Pad（0-3 字节全 0） | 保证对齐 |
| 4 | 写入 TailLen | 此时已知总长度 |
| 5 | 计算并写入 CRC32C | 按 `[F-CRC32C-COVERAGE]` |
| 6 | 回填 HeadLen = TailLen | 首尾长度一致 |
| 7 | 追加写入 Magic | 作为分隔符 |

**示意图**：

```
写入前: [...][Magic]
             ↑ append position

写入后: [...][Magic][HeadLen][Payload][Pad][TailLen][CRC32C][Magic]
                                                            ↑ append position
```

---

## 7. 逆向扫描算法

### 7.1 算法概述

逆向扫描（Reverse Scan）从文件尾部向前遍历所有 Frame，用于：
- 查找最后一个有效 Commit Record（恢复 HEAD）
- 跳过尾部损坏数据

### 7.2 扫描步骤

**`[R-REVERSE-SCAN-ALGORITHM]`** 逆向扫描算法：

```
输入: FileLength
输出: 有效 Frame 列表（从尾到头）
常量: GenesisLen = 4  // Genesis Header 长度

1. MagicPos = FileLength - 4  // 尾部 Magic 位置
2. 若 MagicPos < GenesisLen: 返回 "无 Frame"
3. 循环:
   a. RecordEnd = MagicPos  // Frame 结束位置
   b. 若 RecordEnd == GenesisLen: 返回 "扫描完成"（仅 Genesis）
   c. 读取 TailLen @ (RecordEnd - 8)
   d. 读取 CRC32C @ (RecordEnd - 4)
   e. RecordStart = RecordEnd - TailLen
   f. 验证 RecordStart >= GenesisLen 且 RecordStart % 4 == 0
   g. PrevMagicPos = RecordStart - 4
   h. 验证 @ PrevMagicPos 的 4 字节 == Magic
   i. 读取 HeadLen @ RecordStart
   j. 验证 HeadLen == TailLen
   k. 计算 CRC32C(Payload + Pad + TailLen)
   l. 验证 CRC 匹配
   m. 若验证通过: 输出 Frame，MagicPos = PrevMagicPos
   n. 若验证失败: 进入 Resync 模式
```

> **边界说明**：单帧文件的第一帧 `RecordStart = GenesisLen = 4`，因此步骤 f 使用 `>= GenesisLen` 而非 `>= 8`。

### 7.3 地址计算

```
RecordEnd = MagicPos (当前 Magic 起始位置)
TailLen @ (RecordEnd - 8) .. (RecordEnd - 5)
CRC32C @ (RecordEnd - 4) .. (RecordEnd - 1)
RecordStart = RecordEnd - TailLen
HeadLen @ RecordStart .. (RecordStart + 3)
PrevMagicPos = RecordStart - 4
```

**图示**：

```
[PrevMagic][HeadLen][Payload][Pad][TailLen][CRC32C][CurrMagic]
 ↑          ↑                      ↑        ↑       ↑
 PrevMagicPos RecordStart    RecordEnd-8  RecordEnd-4 RecordEnd=MagicPos
```

---

## 8. Resync 机制

### 8.1 问题背景

由于崩溃、撕裂写入或外部追加，文件尾部可能包含：
- 随机垃圾数据
- 半写入的 Frame
- 损坏的 TailLen 值

此时直接信任 TailLen 跳跃可能跳过最后一个有效 Frame。

### 8.2 Resync 策略

**`[R-RESYNC-DISTRUST-TAILLEN]`** 不信任 TailLen：

当从某个 MagicPos 推导的候选 Frame 验证失败时：
- Reader MUST NOT 信任该候选的 TailLen 并做跳跃
- MUST 进入 Resync 模式

**`[R-RESYNC-SCAN-MAGIC]`** Resync 扫描规范：

```
Resync 模式:
1. 从当前 MagicPos 开始，按 4B 对齐向前扫描
2. MagicPos = MagicPos - 4
3. 若 @ MagicPos 的 4 字节 == Magic:
   a. 按正常逆向扫描验证该位置的 Frame
   b. 若验证通过: 退出 Resync，继续正常扫描
   c. 若验证失败: 继续 Resync（MagicPos -= 4）
4. 若 MagicPos < 4: 扫描结束，无更多有效 Frame
```

### 8.3 Resync 图示

```
正常扫描失败场景:
[Magic][Frame][Magic][Garbage.....][Corrupted]
                 ↑                      ↑
               真正的最后有效 Magic    文件尾部（损坏）

Resync 过程:
1. 从尾部尝试，TailLen 指向错误位置，验证失败
2. 向前 4B 扫描，找到 Magic
3. 验证该 Magic 对应的 Frame
4. 通过 → 找到最后有效 Frame
```

---

## 9. Ptr64 / Address64

### 9.1 编码

**`[F-PTR64-ENCODING]`**

- Ptr64 为 8 字节 LE 编码的文件偏移量
- 指向 Frame 的 HeadLen 字段起始位置

### 9.2 对齐与空值

**`[F-PTR64-NULL-AND-ALIGNMENT]`**

- `Ptr64 == 0` 表示 null（无效指针）
- 有效 Ptr64 MUST 满足 `Ptr64 % 4 == 0`（4B 对齐）
- Reader 遇到非对齐 Ptr64 MUST 视为格式错误

### 9.3 与 Address64 的关系

- `Address64`（在 rbf-interface.md 中定义）是 `Ptr64` 的类型化封装
- 两者在 wire format 上相同（8 字节 LE）
- `Address64` 强调"指向 Frame 起点"的语义

---

## 10. DataTail 与截断

### 10.1 DataTail 定义

**`[R-DATATAIL-DEFINITION]`**

- `DataTail` 是 Ptr64 值，表示 data 文件的逻辑尾部
- 存储在 MetaCommitRecord 中
- `DataTail` 包含尾部 Magic 分隔符（即 `DataTail = 文件有效末尾`）

### 10.2 截断恢复

**`[R-DATATAIL-TRUNCATE]`**

恢复时：
1. 读取 HEAD 的 `DataTail`
2. 若 data 文件实际长度 > DataTail：截断至 DataTail
3. 截断后文件仍以 Magic 结尾

**示意**：

```
恢复前: [Magic][Frame1][Magic][Frame2][Magic][Garbage...]
                                       ↑
                                     DataTail (有效末尾)

恢复后: [Magic][Frame1][Magic][Frame2][Magic]
                                       ↑
                                     文件末尾 = DataTail
```

---

## 11. 条款索引

### 11.1 格式条款 [F-xxx]

> 本索引为导航用途；规范性定义见正文各条款。

| 条款 ID | 名称 |
|---------|------|
| `[F-MAGIC-DEFINITION]` | Magic 定义 |
| `[F-GENESIS-HEADER]` | 创世头 |
| `[F-GENESIS-EMPTY-FILE]` | 空文件 |
| `[F-FRAME-LAYOUT]` | 帧布局 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码 |
| `[F-HEADLEN-TAILLEN-SYMMETRY]` | 长度对称 |
| `[F-HEADLEN-FORMULA]` | 长度公式 |
| `[F-PADLEN-FORMULA]` | Pad 公式 |
| `[F-FRAME-4B-ALIGNMENT]` | 4B 对齐 |
| `[F-PAD-ZERO-FILL]` | 填充为零 |
| `[F-CRC32C-COVERAGE]` | CRC 覆盖 |
| `[F-CRC32C-ALGORITHM]` | CRC 算法 |
| `[F-CRC-FAIL-REJECT]` | CRC 失败 |
| `[F-FRAMING-FAIL-REJECT]` | Framing 失败 |
| `[F-MAGIC-IS-FENCE]` | Magic 是栅栏 |
| `[F-MAGIC-FRAME-SEPARATOR]` | Magic 分隔符 |
| `[F-FRAME-WRITE-SEQUENCE]` | 写入顺序 |
| `[F-PTR64-ENCODING]` | Ptr64 编码 |
| `[F-PTR64-NULL-AND-ALIGNMENT]` | Ptr64 空值与对齐 |

### 11.2 恢复条款 [R-xxx]

| 条款 ID | 名称 |
|---------|------|
| `[R-REVERSE-SCAN-ALGORITHM]` | 逆向扫描 |
| `[R-RESYNC-DISTRUST-TAILLEN]` | 不信任 TailLen |
| `[R-RESYNC-SCAN-MAGIC]` | Resync 扫描 |
| `[R-DATATAIL-DEFINITION]` | DataTail 定义 |
| `[R-DATATAIL-TRUNCATE]` | 截断恢复 |

---

## 12. 测试向量

> 完整测试向量将在独立文件中定义。以下为关键边界情况：

### 12.1 Frame 编码示例

> 本节示例为 **Informative**（验证用），规范性定义见各条款。

**最小 Frame（空 Payload）**：

```
Payload: []
PadLen: 0 (因为 PayloadLen=0 已对齐)
HeadLen: 4 + 0 + 0 + 4 + 4 = 12  (* 由 [F-HEADLEN-FORMULA] 推导 *)

Wire format (Magic = "RBF1" = 0x52424631):
[0C 00 00 00]  HeadLen = 12
               (无 Payload)
               (无 Pad)
[0C 00 00 00]  TailLen = 12
[XX XX XX XX]  CRC32C
[52 42 46 31]  Magic "RBF1"
```

**带 Payload 的 Frame（5 字节 Payload）**（Informative，由 `[F-HEADLEN-FORMULA]` 推导）：

```
Payload: [01 02 03 04 05]
PadLen: (4 - 5%4) % 4 = 3
HeadLen: 4 + 5 + 3 + 4 + 4 = 20

Wire format:
[14 00 00 00]  HeadLen = 20
[01 02 03 04 05]  Payload (5 bytes)
[00 00 00]     Pad (3 bytes)
[14 00 00 00]  TailLen = 20
[XX XX XX XX]  CRC32C
[52 42 46 31]  Magic "RBF1"
```

### 12.2 Resync 测试场景

| 场景 | 文件内容 | 预期行为 |
|------|----------|----------|
| 正常文件 | `[Magic][F1][Magic][F2][Magic]` | 正常扫描 F2, F1 |
| 尾部垃圾 | `[Magic][F1][Magic][Garbage]` | Resync 找到 F1 |
| 半写入 Frame | `[Magic][F1][Magic][HeadLen][PartialPayload]` | Resync 找到 F1 |
| 损坏 TailLen | `[Magic][F1][Magic][F2-corrupted-TailLen]` | Resync 找到 F1 |

---

## 13. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.1 | 2025-12-22 | 初稿，从 mvp-design-v2.md 提取 |
| 0.2 | 2025-12-22 | P0 修订：CRC32C 多项式表述（normal vs reflected）；逆向扫描边界条件（>= GenesisLen）；FrameTag wire encoding |
| 0.3 | 2025-12-22 | P1 修订：Magic 编码约定（字节序列）；PadLen 公式简化；CRC/Framing 失败策略条款 |
| 0.4 | 2025-12-22 | 命名重构：ELOG → RBF (Reversible Binary Framing)；Magic 统一为 `RBF1`；移除双 Magic 设计 |
| 0.5 | 2025-12-23 | 冗余消除：修复 Magic 条款残留错误；EBNF 语法修复并改为条款引用；测试向量闭合修复；CRC 覆盖改为引用；条款索引简化为纯导航；示例标注 Informative |
| 0.6 | 2025-12-23 | 结构重构：Magic 单一定义 `[F-MAGIC-DEFINITION]`；删除 EBNF 和 ASCII 图；Frame 布局表增加偏移列成为唯一规范 |

