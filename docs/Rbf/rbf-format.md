---
docId: "rbf-format"
title: "RBF 二进制格式规范（Layer 0）"
produce_by:
      - "wish/W-0006-rbf-sizedptr/wish.md"
---

# RBF 二进制格式规范（Layer 0）

> **状态**：Draft
> **版本**：0.27
> **创建日期**：2025-12-22
> **接口契约（Layer 1）**：[rbf-interface.md](rbf-interface.md)
> **测试向量（Layer 0）**：[rbf-test-vectors.md](rbf-test-vectors.md)

---

> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 范围与分层

本文档（Layer 0）只定义：
- RBF 文件的 **线格式（wire format）**：Fence/Magic、Frame 字节布局、对齐与 CRC32C
- 损坏判定（Framing/CRC）与恢复相关的扫描行为（Reverse Scan / Resync）

本文档不定义：
- Frame payload 的业务语义（由上层定义）
- `FrameTag`/`<deleted-place-holder>` 等接口类型（见 [rbf-interface.md](rbf-interface.md)）

本规范的 **SSOT（Single Source of Truth）** 是：
- §3 的字段布局表（`[F-FRAME-LAYOUT]`）
- §4 的 CRC 覆盖与算法约定
- §6 的扫描算法（`[R-REVERSE-SCAN-ALGORITHM]`/`[R-RESYNC-SCAN-MAGIC]`）

任何示意、推导、示例都只能解释 SSOT，不得引入新增约束。

---

## 2. 常量与 Fence

### 2.1 Fence 定义

**`[F-FENCE-DEFINITION]`**

Fence 是 RBF 文件的 **帧分隔符**，不属于任何 Frame。

| 属性 | 值 |
|------|-----|
| 值（Value） | `RBF1`（ASCII: `52 42 46 31`） |
| 长度 | 4 字节 |
| 编码 | ASCII 字节序列写入（非 u32 端序），读取时按字节匹配 |

### 2.2 Genesis Fence

**`[F-GENESIS]`**

- 每个 RBF 文件 MUST 以 Fence 开头（偏移 0，长度 4 字节）——称为 **Genesis Fence**。
- 新建的 RBF 文件 MUST 仅含 Genesis Fence（长度 = 4 字节，表示"无任何 Frame"）。
- 首帧（如果存在）的起始地址 MUST 为 `offset=4`（紧跟 Genesis Fence 之后）。

**`[F-FILE-MINIMUM-LENGTH]`**

- 有效 RBF 文件长度 MUST >= 4（至少包含 Genesis Fence）。
- `fileLength < 4` 表示文件不完整或损坏，Reader MUST fail-soft（返回空序列），MUST NOT 抛出异常。

> **设计理由**：fail-soft 策略与接口层 `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` 保持一致，使上层可以统一处理"无有效帧"的情况，而无需区分"文件不完整"与"文件为空但合法"。
---

## 3. Wire Layout

### 3.1 Fence 语义

**`[F-FENCE-SEMANTICS]`**

- Fence 是 **帧分隔符**（fencepost），不属于任何 Frame。
- 文件中第一个 Fence（偏移 0）称为 **Genesis Fence**。
- **Writer** 写完每个 Frame 后 MUST 紧跟一个 Fence。
- **Reader** 在崩溃恢复场景 MAY 遇到不以 Fence 结束的文件（撕裂写入），通过 Resync 处理（见 §6）。

文件布局因此为：

```
[Fence][FrameBytes][Fence][FrameBytes][Fence]...
```

### 3.2 FrameBytes（二进制帧体）布局

**`[F-FRAME-LAYOUT]`**

> 下表描述 FrameBytes 的布局（从 Frame 起点的 `HeadLen` 字段开始计偏移）。
> FrameBytes **不包含** 前后 Fence。

| 偏移 | 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|------|
| 0 | HeadLen | u32 LE | 4 | FrameBytes 总长度（不含 Fence） |
| 4 | FrameTag | u32 LE | 4 | 帧类型标识符（见 `[F-FRAMETAG-WIRE-ENCODING]`） |
| 8 | Payload | bytes | N | `N >= 0`；业务数据 |
| 8+N | FrameStatus | bytes | 1-4 | 帧状态标记（见 `[F-FRAMESTATUS-VALUES]`），使 `N + StatusLen` 为 4 的倍数 |
| 8+N+StatusLen | TailLen | u32 LE | 4 | MUST 等于 HeadLen |
| 12+N+StatusLen | CRC32C | u32 LE | 4 | 见 `[F-CRC32C-COVERAGE]` |

**`[F-FRAMETAG-WIRE-ENCODING]`**

- FrameTag 是 4 字节 u32 LE 帧类型标识符，位于 HeadLen 之后、Payload 之前。
- RBF 层不保留任何 FrameTag 值，全部值域由上层定义。
- `FrameTag` 的接口层定义见 [rbf-interface.md](rbf-interface.md) §2.1。

**`[F-FRAMESTATUS-VALUES]`**

> **FrameStatus** 是 1-4 字节的帧状态标记，所有字节 MUST 填相同值。
> FrameStatus 采用**位域格式**，同时编码帧状态和 StatusLen。

**位域布局（SSOT）**：

| Bit | 名称 | 说明 |
|-----|------|------|
| 7 | Tombstone | 0 = Valid（正常帧），1 = Tombstone（墓碑帧） |
| 6-2 | Reserved | 保留位，MVP MUST 为 0；Reader 遇到非零值 MUST 视为 Framing 失败 |
| 1-0 | StatusLen | 状态字节数减 1：`00`=1, `01`=2, `10`=3, `11`=4 |

**判断规则（SSOT）**：

```
IsTombstone = (status & 0x80) != 0
IsValid     = (status & 0x80) == 0
StatusLen   = (status & 0x03) + 1
IsMvpValid  = (status & 0x7C) == 0   // Reserved bits must be zero
```

> **设计理由**：
> - 位域格式解决了 HeadLen 无法唯一确定 PayloadLen/StatusLen 边界的问题。
> - Bit 7 作为 Tombstone 标记，语义清晰，判断高效（符号位检测）。
> - Bit 0-1 编码 StatusLen，支持 1-4 字节。
> - Bit 2-6 保留给未来扩展，当前 MUST 为 0。
> - 全字节同值设计提供隐式冗余校验：若字节不一致，可直接判定损坏。
>
> MVP 有效值的完整枚举见 [rbf-test-vectors.md](rbf-test-vectors.md) 的 §1.6。

### 3.3 长度与对齐

**`[F-HEADLEN-FORMULA]`**

```
HeadLen = 4 (HeadLen) + 4 (FrameTag) + PayloadLen + StatusLen + 4 (TailLen) + 4 (CRC32C)
        = 16 + PayloadLen + StatusLen
```

> 当 PayloadLen = 0 时，StatusLen = 4（见 `[F-STATUSLEN-FORMULA]`），故最小 HeadLen = 20。

**`[F-STATUSLEN-FORMULA]`**

```
StatusLen = 1 + ((4 - ((PayloadLen + 1) % 4)) % 4)
```

> StatusLen ∈ {1, 2, 3, 4}，保证 `(PayloadLen + StatusLen) % 4 == 0`。

| PayloadLen % 4 | StatusLen |
|----------------|----------|
| 0 | 4 |
| 1 | 3 |
| 2 | 2 |
| 3 | 1 |

**`[F-STATUSLEN-REVERSE-FORMULA]`**

> Reader 从 HeadLen 反推 PayloadLen/StatusLen 的算法。

```
读取路径：
1. statusByteOffset = frameStart + HeadLen - 9   // TailLen(4) + CRC(4) + 1 = 9
2. statusByte = bytes[statusByteOffset]          // FrameStatus 最后一字节
3. StatusLen = (statusByte & 0x03) + 1           // 从位域提取
4. PayloadLen = HeadLen - 16 - StatusLen         // 反推
```

> **设计理由**：FrameStatus 位域在 Bit 0-1 编码 StatusLen-1，使得 Reader 只需读取 FrameStatus 的任意字节（全字节同值）即可确定边界，无需额外字段。

**`[F-FRAME-4B-ALIGNMENT]`**

- Frame 起点（HeadLen 字段位置）MUST 4B 对齐。

**`[F-FRAMESTATUS-FILL]`**

- FrameStatus 的所有字节 MUST 填相同值。
- 若任意字节与其他字节不一致，视为 Framing 失败。

> 注：合法值由 `[F-FRAMESTATUS-VALUES]` 位域 SSOT 定义。

---

## 4. CRC32C

### 4.1 覆盖范围

**`[F-CRC32C-COVERAGE]`**

```
CRC32C = crc32c(FrameTag + Payload + FrameStatus + TailLen)
```

- CRC32C MUST 覆盖：FrameTag + Payload + FrameStatus + TailLen。
- CRC32C MUST NOT 覆盖：HeadLen、CRC32C 本身、任何 Fence。

**字节偏移（SSOT）**：

设 `frameStart` 为 FrameBytes 起始地址（即 HeadLen 字段位置），`frameEnd` 为 FrameBytes 末尾（即 CRC32C 字段末尾）：

```
CRC 输入区间 = [frameStart + 4, frameEnd - 4)   // 半开区间
             = [frameStart + 4, frameStart + HeadLen - 4)
```

| 边界 | 偏移 | 说明 |
|------|------|------|
| 起始（含） | `frameStart + 4` | 跳过 HeadLen(4B)，从 FrameTag 开始 |
| 结束（不含） | `frameStart + HeadLen - 4` | 不含 CRC32C(4B) 本身 |
| 长度 | `HeadLen - 8` | FrameTag(4) + Payload(N) + FrameStatus(S) + TailLen(4) |

> 注：FrameStatus 在 CRC 覆盖范围内，Tombstone 标记受 CRC 保护。

**Tombstone 帧示例**（PayloadLen=0，最小帧）：

```
HeadLen = 20（最小值），StatusLen = 4（见 [F-STATUSLEN-FORMULA]）
CRC 覆盖区间 = [frameStart+4, frameStart+16)
CRC 覆盖内容 = FrameTag(4B) + FrameStatus(4B, 全填 0x83) + TailLen(4B) = 12 字节
```

> Tombstone 帧虽无 Payload，但其 FrameTag、FrameStatus（含 Tombstone 标记位）、TailLen 均受 CRC 保护。

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

**`[F-CRC-FAIL-REJECT]`**

- CRC32C 校验不匹配 MUST 视为帧损坏。
- Reader MUST NOT 将损坏帧作为有效数据返回。

---

## 6. 逆向扫描与 Resync

### 6.1 逆向扫描（Reverse Scan）

**`[R-REVERSE-SCAN-ALGORITHM]`**

> 该算法从文件尾部向前扫描 Fence，并尝试验证其前方的 Frame。
> 当验证失败时，进入 Resync：继续按 4B 对齐向前寻找下一个 Fence。

```
输入: fileLength
输出: 通过校验的 Frame 起始地址列表（从尾到头）
常量:
   GenesisLen = 4
   FenceLen   = 4
   MinFrameLen = 20  // PayloadLen=0, StatusLen=4 时的最小 FrameBytes

辅助:
   alignDown4(x) = x - (x % 4)   // 前置条件: x >= 0（RBF 地址均为非负）

1) 若 fileLength < GenesisLen: 返回空   // 不完整文件，fail-soft
2) 若 fileLength == GenesisLen: 返回空  // 仅 Genesis Fence，无 Frame
3) fencePos = alignDown4(fileLength - FenceLen)
4) while fencePos >= 0:
       a) 若 fencePos == 0: 停止（到达 Genesis Fence）
       b) 若 bytes[fencePos..fencePos+4] != FenceValue:
               fencePos -= 4
               continue   // Resync: 寻找 Fence

       c) // 现在 fencePos 指向一个 Fence
            recordEnd = fencePos
            若 recordEnd < GenesisLen + 20:  // 最小 FrameBytes = 20（PayloadLen=0, StatusLen=4）
                  fencePos -= 4
                  continue

            读取 tailLen @ (recordEnd - 8)
            读取 storedCrc @ (recordEnd - 4)
            frameStart = recordEnd - tailLen

            若 frameStart < GenesisLen 或 frameStart % 4 != 0:
                  fencePos -= 4
                  continue

            prevFencePos = frameStart - FenceLen
            若 prevFencePos < 0 或 bytes[prevFencePos..prevFencePos+4] != FenceValue:
                  fencePos -= 4
                  continue

            读取 headLen @ frameStart
            若 headLen != tailLen 或 headLen % 4 != 0 或 headLen < 20:
                  fencePos -= 4
                  continue

            // CRC 覆盖范围是 [frameStart+4, recordEnd-4)（含 FrameTag）
            computedCrc = crc32c(bytes[frameStart+4 .. recordEnd-4])
            若 computedCrc != storedCrc:
                  fencePos -= 4
                  continue

            // FrameStatus 校验（见 [F-FRAMESTATUS-VALUES] 和 [F-FRAMESTATUS-FILL]）
            statusByteOffset = frameStart + headLen - 9
            statusByte = bytes[statusByteOffset]
            statusLen = (statusByte & 0x03) + 1
            若 (statusByte & 0x7C) != 0:   // Reserved bits MUST be zero
                  fencePos -= 4
                  continue
            // 验证 FrameStatus 所有字节一致
            for i in 1 .. statusLen-1:
                  若 bytes[statusByteOffset - i] != statusByte:
                        fencePos -= 4
                        continue outer   // 跳到外层 while

            输出 frameStart
            fencePos = prevFencePos
```

### 6.2 Resync 规则

**`[R-RESYNC-BEHAVIOR]`**

当候选 Frame 校验失败时（Framing/CRC）：
1. Reader MUST NOT 信任该候选的 TailLen 做跳跃。
2. Reader MUST 进入 Resync 模式：以 4 字节为步长向前搜索 Fence。
3. Resync 扫描 MUST 在抵达 Genesis Fence（偏移 0）时停止。

---

## 7. <deleted-place-holder>（编码层）

### 7.1 Wire Format

**`[F-ADDRESS64-WIRE-FORMAT]`**

- **编码**：`<deleted-place-holder>` 在 wire format 上为 8 字节 u64 LE 文件偏移量，指向 Frame 的 `HeadLen` 字段起始位置。
- **空值**：`0` 表示 null（无效地址）。
- **对齐**：非零地址 MUST 4B 对齐（`Value % 4 == 0`）。

> 接口层的类型化封装见 [rbf-interface.md](rbf-interface.md) 的 `<deleted-place-holder>`（`[F-ADDRESS64-DEFINITION]`）。

---

## 8. DataTail 与截断（恢复语义）

**`[R-DATATAIL-DEFINITION]`**

- `DataTail` 是一个地址（见 §7），表示 data 文件的逻辑尾部。
- `DataTail` MUST 指向“有效数据末尾”，并包含尾部 Fence（即 `DataTail == 有效 EOF`）。

**`[R-DATATAIL-TRUNCATE]`**

恢复时（上层依据其 HEAD/commit record 的语义决定使用哪条 DataTail）：
1. 若 data 文件实际长度 > DataTail：MUST 截断至 DataTail。
2. 截断后文件 SHOULD 以 Fence 结尾（若 `DataTail` 来自通过校验的 commit record）。

---

## 9. 条款索引（导航）

| 条款 ID | 名称 |
|---------|------|
| `[F-FENCE-DEFINITION]` | Fence 定义 |
| `[F-GENESIS]` | Genesis Fence |
| `[F-FILE-MINIMUM-LENGTH]` | 文件最小长度 |
| `[F-FENCE-SEMANTICS]` | Fence 语义 |
| `[F-FRAME-LAYOUT]` | FrameBytes 布局 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码（4B） |
| `[F-FRAMESTATUS-VALUES]` | FrameStatus 值定义 |
| `[F-HEADLEN-FORMULA]` | HeadLen 公式 |
| `[F-STATUSLEN-FORMULA]` | StatusLen 公式 |
| `[F-STATUSLEN-REVERSE-FORMULA]` | StatusLen 反推公式（Reader 路径） |
| `[F-FRAME-4B-ALIGNMENT]` | 4B 对齐 |
| `[F-FRAMESTATUS-FILL]` | FrameStatus 填充规则 |
| `[F-CRC32C-COVERAGE]` | CRC 覆盖范围 |
| `[F-CRC32C-ALGORITHM]` | CRC 算法 |
| `[F-FRAMING-FAIL-REJECT]` | Framing 失败策略 |
| `[F-CRC-FAIL-REJECT]` | CRC 失败策略 |
| `[F-ADDRESS64-WIRE-FORMAT]` | <deleted-place-holder> Wire Format |
| `[R-REVERSE-SCAN-ALGORITHM]` | 逆向扫描 |
| `[R-RESYNC-BEHAVIOR]` | Resync 行为 |
| `[R-DATATAIL-DEFINITION]` | DataTail 定义 |
| `[R-DATATAIL-TRUNCATE]` | DataTail 截断 |

---

## 10. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.27 | 2026-01-06 | §2.2 `[F-FILE-MINIMUM-LENGTH]` 修复与接口层的不一致：`SHOULD fail-soft, MAY throw` → `MUST fail-soft, MUST NOT throw`（与 `[S-RBF-SCANREVERSE-EMPTY-IS-OK]` 对齐） |
| 0.26 | 2026-01-06 | §6.1 `alignDown4` 辅助函数增加前置条件注释：`x >= 0`（RBF 地址均为非负） |
| 0.25 | 2026-01-06 | §2.2 新增 `[F-FILE-MINIMUM-LENGTH]` 条款：明确有效 RBF 文件最小长度为 4 字节；§6.1 伪代码增加 `fileLength == GenesisLen` 的显式检查 |
| 0.24 | 2026-01-06 | §3.1 `[F-FENCE-SEMANTICS]` 明确 Writer/Reader 语境：Writer MUST 紧跟 Fence；Reader MAY 遇到撕裂文件 |
| 0.23 | 2026-01-06 | §2.2 `[F-GENESIS]` 明确首帧起始地址为 offset=4（Genesis Fence 之后） |
| 0.22 | 2026-01-06 | §4.1 `[F-CRC32C-COVERAGE]` 增加 Tombstone 帧（最小帧）CRC 覆盖范围示例 |
| 0.21 | 2026-01-06 | §7 术语统一：`Ptr64` 改为 `<deleted-place-holder>`，消除与 rbf-interface.md 的命名不一致 |
| 0.20 | 2026-01-06 | §4.1 `[F-CRC32C-COVERAGE]` 补充精确字节偏移：半开区间 `[frameStart+4, frameStart+HeadLen-4)` |
| 0.19 | 2026-01-06 | §6.1 伪代码补充 FrameStatus 校验：Reserved bits 合法性 + 全字节一致性（对齐 §5 `[F-FRAMING-FAIL-REJECT]`） |
| 0.18 | 2026-01-06 | 新增 `[F-STATUSLEN-REVERSE-FORMULA]`：Reader 从 HeadLen 反推 PayloadLen/StatusLen 的算法 |
| 0.17 | 2026-01-06 | 修复 §6.1 伪代码中 `headLen < 16` → `headLen < 20`，与 §3.3 最小 HeadLen 约束对齐 |
| 0.16 | 2025-12-28 | 更新 FrameTag 接口引用：移除过时的 `[F-FRAMETAG-DEFINITION]` 条款引用，改为引用 rbf-interface.md §2.1 |
| 0.15 | 2025-12-28 | 消除内部矛盾：删除过时的 `0x00/0xFF` 值域枚举，修正最小 HeadLen 为 20 |
| 0.14 | 2025-12-25 | **FrameStatus 位域格式**：Bit 7 = Tombstone，Bit 0-1 = StatusLen-1，Bit 2-6 保留 |
| 0.13 | 2025-12-25 | FrameStatus 编码 StatusLen（已被 v0.14 取代） |
| 0.12 | 2025-12-24 | Pad 重命名为 FrameStatus；长度从 0-3 改为 1-4 |
| 0.11 | 2025-12-24 | FrameTag 从 1B 扩展为 4B 独立字段 |
| 0.10 | 2025-12-23 | 术语重构：合并 Magic 与 Fence 概念 |
| 0.9 | 2025-12-23 | 条款重构：合并相关条款 |
| 0.8 | 2025-12-23 | 消除冗余条款 |
| 0.7 | 2025-12-23 | 彻底重写：以布局表为 SSOT |
| 0.6 | 2025-12-23 | 结构重构 |
