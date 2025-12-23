# RBF 二进制格式规范（Layer 0）

> **状态**：Draft
> **版本**：0.8
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
- `FrameTag`/`Address64` 等接口类型（见 [rbf-interface.md](rbf-interface.md)）

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
| 4 | FrameData | bytes | N | `N >= 1`；第 1 字节为 Tag（FrameTag） |
| 4+N | Pad | bytes | 0-3 | MUST 全 0，使 `N + PadLen` 为 4 的倍数 |
| 4+N+PadLen | TailLen | u32 LE | 4 | MUST 等于 HeadLen |
| 8+N+PadLen | CRC32C | u32 LE | 4 | 见 `[F-CRC32C-COVERAGE]` |

**`[F-FRAMETAG-WIRE-ENCODING]`**

- FrameData 的第 1 字节 MUST 为 Tag（FrameTag）。
- `FrameDataLen = 1 (Tag) + PayloadLen`。
- `FrameTag` 的语义定义见 [rbf-interface.md](rbf-interface.md) 的 `[F-FRAMETAG-DEFINITION]`。

### 3.3 长度与对齐

**`[F-HEADLEN-FORMULA]`**

```
HeadLen = 4 (HeadLen) + FrameDataLen + PadLen + 4 (TailLen) + 4 (CRC32C)
            = 12 + FrameDataLen + PadLen
```

**`[F-PADLEN-FORMULA]`**

```
PadLen = (4 - (FrameDataLen % 4)) % 4
```

**`[F-FRAME-4B-ALIGNMENT]`**

- Frame 起点（HeadLen 字段位置）MUST 4B 对齐。

**`[F-PAD-ZERO-FILL]`**

- Pad 字节 MUST 全为 0。

---

## 4. CRC32C

### 4.1 覆盖范围

**`[F-CRC32C-COVERAGE]`**

```
CRC32C = crc32c(FrameData + Pad + TailLen)
```

- CRC32C MUST 覆盖：FrameData（含 Tag）+ Pad + TailLen。
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
- `[F-FRAME-LAYOUT]`：HeadLen/TailLen 一致性、Pad 全 0
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
            若 recordEnd < GenesisLen + 16:  // 不足以容纳最小 FrameBytes（HeadLen/Tag/Pad/TailLen/CRC）
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

            // CRC 覆盖范围是 [frameStart+4, recordEnd-4)
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

## 7. Address64 / Ptr64（编码层）

### 7.1 Wire Format

**`[F-PTR64-WIRE-FORMAT]`**

- **编码**：本规范所称“地址（Address64/Ptr64）”在 wire format 上为 8 字节 u64 LE 文件偏移量，指向 Frame 的 `HeadLen` 字段起始位置。
- **空值**：`0` 表示 null（无效地址）。
- **对齐**：非零地址 MUST 4B 对齐（`Value % 4 == 0`）。

> 接口层的类型化封装见 [rbf-interface.md](rbf-interface.md) 的 `Address64`（`[F-ADDRESS64-DEFINITION]`）。

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
| `[F-FENCE-SEMANTICS]` | Fence 语义 |
| `[F-FRAME-LAYOUT]` | FrameBytes 布局 |
| `[F-FRAMETAG-WIRE-ENCODING]` | FrameTag 编码 |
| `[F-HEADLEN-FORMULA]` | HeadLen 公式 |
| `[F-PADLEN-FORMULA]` | PadLen 公式 |
| `[F-FRAME-4B-ALIGNMENT]` | 4B 对齐 |
| `[F-PAD-ZERO-FILL]` | Pad 全 0 |
| `[F-CRC32C-COVERAGE]` | CRC 覆盖范围 |
| `[F-CRC32C-ALGORITHM]` | CRC 算法 |
| `[F-FRAMING-FAIL-REJECT]` | Framing 失败策略 |
| `[F-CRC-FAIL-REJECT]` | CRC 失败策略 |
| `[F-PTR64-WIRE-FORMAT]` | Address/Ptr Wire Format |
| `[R-REVERSE-SCAN-ALGORITHM]` | 逆向扫描 |
| `[R-RESYNC-BEHAVIOR]` | Resync 行为 |
| `[R-DATATAIL-DEFINITION]` | DataTail 定义 |
| `[R-DATATAIL-TRUNCATE]` | DataTail 截断 |

---

## 10. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.10 | 2025-12-23 | 术语重构：合并 Magic 与 Fence 概念，统一使用 Fence；Magic 降级为 Fence 的值描述 |
| 0.9 | 2025-12-23 | 条款重构：合并 Genesis/Fence/Ptr64/Resync 相关条款；精简冗余描述 |
| 0.8 | 2025-12-23 | 消除冗余：删除 `[F-HEADLEN-TAILLEN-SYMMETRY]`（布局表已含）；精简 `[F-FRAME-4B-ALIGNMENT]` 推导；`[F-FRAMING-FAIL-REJECT]` 改为引用式 |
| 0.7 | 2025-12-23 | 彻底重写：以布局表/算法条款为 SSOT，删除同义复述；修复“Payload 允许为空”与“Tag 必须存在”的矛盾；修复逆向扫描终止条件与 Resync 入口表述 |
| 0.6 | 2025-12-23 | 结构重构：Magic 单一定义；删除 EBNF/ASCII 图；布局表增加偏移列 |

