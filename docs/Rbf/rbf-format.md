---
docId: "rbf-format"
title: "RBF Rule-Tier"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 二进制格式规范（Layer 0）
> 本文档遵循 [Atelia 规范约定](../spec-conventions.md)。

## 1. 范围与分层
本文档（Layer 0）只定义：
- RBF 文件的 **线格式（wire format）**：Fence/Magic、Frame 字节布局、对齐与 CRC32C
- 损坏判定（Framing/CRC）与恢复相关的扫描行为（Reverse Scan / Resync）

本文档不定义：
- Frame payload 的业务语义（由上层定义）
- `FrameTag`/`SizedPtr` 等接口类型（见 [rbf-interface.md](rbf-interface.md)）

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
- 新建的 RBF 文件 MUST 仅含 Genesis Fence（长度 = 4 字节，表示“无任何 Frame”）。

---

## 3. Wire Layout

### 3.1 Fence 语义

**`[F-FENCE-SEMANTICS]`**

- Fence 是 **帧分隔符**（fencepost），不属于任何 Frame。
- 文件中第一个 Fence（偏移 0）称为 **Genesis Fence**。
- 每个 Frame 之后 MUST 紧跟一个 Fence。

文件布局因此为：

```
[Fence][FrameBytes][Fence][FrameBytes][Fence]...
```

> 注：在崩溃/撕裂写入场景，文件尾部 MAY 不以 Fence 结束；Reader 通过 Resync 处理（见 §6）。

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

> 注：FrameStatus 在 CRC 覆盖范围内，Tombstone 标记受 CRC 保护。

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

辅助:
   alignDown4(x) = x - (x % 4)

1) 若 fileLength < GenesisLen: 返回空
2) fencePos = alignDown4(fileLength - FenceLen)
3) while fencePos >= 0:
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
            若 headLen != tailLen 或 headLen % 4 != 0 或 headLen < 16:
                  fencePos -= 4
                  continue

            // CRC 覆盖范围是 [frameStart+4, recordEnd-4)（含 FrameTag）
            computedCrc = crc32c(bytes[frameStart+4 .. recordEnd-4])
            若 computedCrc != storedCrc:
                  fencePos -= 4
                  continue

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

## 7. SizedPtr（Wire Format）

### 7.1 Wire Format

**`[F-SIZEDPTR-WIRE-FORMAT]`**

- **编码**：SizedPtr 在 wire format 上为 8 字节 u64 LE 紧凑编码，包含 offset 和 length。
- **Offset**：指向 Frame 的 `HeadLen` 字段起始位置（38-bit，4B 粒度）。
- **Length**：Frame 的字节长度（26-bit，4B 粒度）。
- **空值**：`Packed == 0`（即 `OffsetBytes == 0 && LengthBytes == 0`）表示 null（无效引用）。
- **对齐**：非零 SizedPtr MUST 4B 对齐。

> 接口层的类型定义见 [rbf-interface.md](rbf-interface.md) 的 `SizedPtr`（`[F-SIZEDPTR-DEFINITION]`）。

### 7.2 DataTail 表达

`DataTail` 使用 `SizedPtr.OffsetBytes` 表达文件截断点（length 部分无意义，可为 0）。

---

## 8. DataTail 与截断（恢复语义）

**`[R-DATATAIL-DEFINITION]`**

- `DataTail` 是一个 `SizedPtr.OffsetBytes`（见 §7），表示 data 文件的逻辑尾部。
- `DataTail` MUST 指向“有效数据末尾”，并包含尾部 Fence（即 `DataTail == 有效 EOF`）。

**`[R-DATATAIL-TRUNCATE]`**

恢复时（上层依据其 HEAD/commit record 的语义决定使用哪条 DataTail）：
1. 若 data 文件实际长度 > DataTail：MUST 截断至 DataTail。
2. 截断后文件 SHOULD 以 Fence 结尾（若 `DataTail` 来自通过校验的 commit record）。

---

## 10. 近期变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.17 | 2026-01-06 | **SizedPtr Wire Format**（[W-0006](../../../wish/W-0006-rbf-sizedptr/artifacts/)）：§7 重写为 SizedPtr 编码；`[F-PTR64-WIRE-FORMAT]` 改为 `[F-SIZEDPTR-WIRE-FORMAT]`；新增 §7.2 DataTail 表达 |
| 0.16 | 2025-12-28 | 更新 FrameTag 接口引用：移除过时的 `[F-FRAMETAG-DEFINITION]` 条款引用，改为引用 rbf-interface.md §2.1 |
| 0.15 | 2025-12-28 | 消除内部矛盾：删除过时的 `0x00/0xFF` 值域枚举，修正最小 HeadLen 为 20 |
