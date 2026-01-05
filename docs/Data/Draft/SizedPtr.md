---
docId: "W-0004-Brief"
title: "SizedPtr 数据结构设计"
produce_by:
  - "wish/W-0004-sizedptr/wish.md"
---

# SizedPtr 数据结构设计

首个目标用户：`atelia/docs/Rbf/rbf-interface.md`

## 定位

**SizedPtr** 本质上是一个 **Packed Fat Pointer**（胖指针）数据结构。
它将 `Offset`（偏移量）和 `Length`（长度）压缩存储在一个 `ulong` (64-bit) 中。
该结构专注于数据的**紧凑存储与位操作算法**，与具体的上层业务语义（如 Null 值、空文件等）解耦。

## 语义定义（对外）

### Offset / Length 的含义

- `OffsetBytes`：以 **byte** 表示的起始偏移（必须 4B 对齐）。
- `LengthBytes`：以 **byte** 表示的区间长度（必须 4B 对齐）。
- `SizedPtr` 表达一个半开区间：

$$[OffsetBytes,\; OffsetBytes + LengthBytes)$$

> 说明：这是一种“纯几何语义”的 byte range 表达，不预设上层概念（不绑定 RBF Frame、payload、record 等）。上层可以把这个区间解释为“某个对象在文件中的 span”，但解释权不属于 SizedPtr。

### 特殊值（Null/Empty）

`SizedPtr` **不定义**任何特殊值语义（例如 Null/Empty）。

- `Packed == 0` 在数学上仅意味着 `(OffsetBytes=0, LengthBytes=0)`。
- 若某个使用者（例如某个协议/库）需要 Null 语义，应在其层通过 wrapper type / extension methods 定义约定。

## Bit 分配方案

**约束**：
- 总共 64 bit
- 偏移量需要 4B 对齐 → 可节省 2 bit
- 帧长度也要求 4B 对齐 → 再节省 2 bit

**范围估计**：

| 方案 | 偏移量 bit | 长度 bit | 寻址范围 (约数) | 帧长度范围 (约数) |
|:-----|:-----------|:---------|:---------|:-------|
| **大文件** | 40 | 24 | ~4 TB | ~64 MB |
| **均衡** | 38 | 26 | ~1 TB | ~256 MB |
| **大帧** | 36 | 28 | ~256 GB | ~1 GB |

> 注：表中的“寻址范围”和“长度范围”仅用于直观估算值域规模，精确的最大值由 Bit 数决定（见代码常量）。

**编码示意**（**均衡**方案，38:26 分配）：

（略；打包/解包的可执行定义以 `atelia/src/Data/SizedPtr.cs` 为准，避免文档↔代码漂移。）
## Decision Log

### DL-001：Length 语义（2026-01-04）

**决策**：`LengthBytes` 表示任意 byte range 的长度（纯几何语义），SizedPtr 表达区间 `[OffsetBytes, OffsetBytes + LengthBytes)`，不绑定 RBF/Frame 或其他上层概念。

**理由**：
1. `[offset, offset+length)` 是最大公约数的区间表达，可被多种上层复用。
2. 保持 SizedPtr 作为通用产品的定位，避免层泄漏（SizedPtr 不需要知道“Frame”是什么）。
3. 外部 API 以 bytes 暴露，内部按 4B 对齐打包仅是编码细节。

**排除**：Length 专指某种上层对象（例如 RBF payload/frame）长度——这会把 SizedPtr 绑死在单一用例上。

### DL-002：Bit 分配方案选择（2026-01-04）

**决策**：选择 **38:26**（OffsetBits=38, LengthBits=26）作为默认方案。

**理由**：
1. `LengthBytes` 的 256MB 上限提供更大的容错空间，降低“大块数据/大对象 span”场景被硬天花板卡死的风险。
2. `OffsetBytes` 的 1TB 上限覆盖绝大多数单文件场景；且“多文件分片”是更成熟的绕过手段。
3. 在不引入多类型/标记位复杂性的前提下，给两端都保留了合理余量。

**排除**：
- 36:28：Offset 上限约 256GB，在现代磁盘容量下风险偏高。
- 40:24：Length 上限约 64MB，硬天花板风险偏高。

**风险承认**：若未来出现 >1TB 单文件 span 需求，需要迁移策略（例如分片/多文件）。

### DL-003：特殊值分层策略（2026-01-04）

**决策**：SizedPtr 本体不定义 Null/Empty 等特殊值；由使用者在其层通过 wrapper type/约定定义。

**理由**：
1. 避免侵入使用者的业务语义空间。
2. `default(SizedPtr)` 对应 `(0,0)`，这在某些使用者语义下可能是合法值；因此不宜强行定义为 Null。
3. 不同目标用户可选择不同约定（例如某协议用 `packed=0` 表示 null，另一协议用别的值域约定），互不冲突。

**实现约定**：
- 纯粹的值类型，不包含任何业务状态（如 Empty/Null）的判断逻辑。
- `offset/length` 以 **字节** 暴露给使用者，但编码时按 4B 对齐打包（值域天然保证 4B 对齐）。
- 从 `Packed` 构造不需要 `Parse/TryParse` 校验（任何 `ulong` 都能解包成一个确定的区间）。
- 从 `(offset,length)` 构造提供 `Create/TryCreate`，仅用于帮助调用方在写入时做参数校验。
- `EndOffsetExclusive` 使用 `checked`；`Contains` 使用差值比较避免溢出。

## 测试计划（Plan-Tier 草案）

> 本节为实现/测试的工作入口，后续可迁移到按 Tier 分层的内部文档组中。

最小但高价值的单元测试覆盖建议（摘要）：

- **Pack/Unpack Roundtrip**：`Create(offset,length)` 后 `OffsetBytes/LengthBytes/Packed` 一致；覆盖 (0,0)、任意对齐值、MaxOffset/MaxLength。
- **对齐检查**：offset/length 非 4B 对齐时 `TryCreate` 返回 false，`Create` 抛 `ArgumentOutOfRangeException`。
- **边界**：`MaxOffset+4` / `MaxLength+4` 必须拒绝；常量推导值正确。
- **FromPacked**：任意 `ulong` 必须可解包且不抛异常（不做校验）。
- **区间语义**：`Contains` 为半开区间；`LengthBytes==0` 时永远 false；采用差值比较避免溢出。
- **溢出**：`EndOffsetExclusive` 使用 `checked`；构造阶段拒绝 `offset+length` 溢出。

> 注：本设计文档描述语义与决策；实现/可执行契约以 `atelia/src/Data/SizedPtr.cs` 与 `atelia/tests/Data.Tests/SizedPtrTests.cs` 为准（SSOT），本文不内嵌“伪实现代码”以避免漂移。
