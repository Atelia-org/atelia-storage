---
docId: "rbf-interface"
title: "RBF Shape-Tier"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF Layer Interface Contract
**文档定位**：Layer 0/1 边界，定义 `IRbfFile` 门面与对外可见类型/行为契约。
文档层级与规范遵循见 [README.md](README.md)。

本文档只描述"对外外观 + 可观察行为"，不暴露内部实现与 wire-format 细节。

2026-10-03 合同同步：新建及写入采用 `RBF3`；`RBF1` 保留只读、旧 ticket 与旧容量；未知 Header（包括历史实验 `RBF2`）拒绝。源码0999851对应内容的Release RBF测试670/670与正式生产探针已通过并独立复核，证据身份及范围见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)。

## 1. 概述

RBF 是"二进制信封"：只关心如何安全封装 payload，不解释 payload 语义。

**设计原则**：
- 上层只需依赖本文档，无需了解 RBF 内部实现细节
- 对外接口以门面 `IRbfFile` 为中心，管理资源生命周期并提供读写能力
- 接口设计支持 zero-copy 热路径（`RbfFrame`/`ReadOnlySpan<byte>`），但不强制

---

## 2. 术语表（Layer 0）
本节定义 RBF 层的独立术语。上层业务语义（如 FrameTag 取值）由上层文档定义。
## term `FrameTag` 帧类型标识符

**FrameTag** 是 4 字节（`uint`）的帧类型标识符。RBF 层不解释其语义，仅作为 payload 的 discriminator 透传。

**三层视角**：
- **存储层**：`uint`（线格式中的 4 字节 LE 字段）
- **接口层**：`uint`（本文档定义的 API 参数/返回类型）
- **应用层**：上层可自由选择 enum 或其他类型进行打包/解包

**保留值**：无。RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

## term `Tombstone` 墓碑帧
**Tombstone**（墓碑帧）是帧的有效性标记（Layer 0 元信息），表示该帧已被逻辑删除或是 Auto-Abort 的产物。
RbfFrame 通过 `bool IsTombstone` 属性暴露此状态。

### spec [S-RBF-SCANREVERSE-TOMBSTONE-FILTER] ScanReverse过滤Tombstone
`IRbfFile.ScanReverse(bool showTombstone = false)` 的行为：
- 当 `showTombstone == false`（默认）时：MUST 自动跳过 Tombstone 帧（`IsTombstone == true`），只产出有效业务帧。
- 当 `showTombstone == true` 时：MUST 产出所有通过 framing/CRC 校验的帧（含 Tombstone）。

### derived [H-TOMBSTONE-VISIBILITY-RATIONALE] 墓碑帧可见性设计理由
- **默认隐藏**：提升易用性，绝大多数日常场景不需要关心已被标记删除的数据。
- **可选暴露**：为了诊断、调试或特定审计需求，允许通过参数查看所有物理帧。
- **职责下沉**：Layer 0 负责过滤系统级标记（Tombstone），简化上层逻辑。

## derived `SizedPtr` 帧句柄
**引用**：[Atelia.Data.SizedPtr](../../src/Data/SizedPtr.cs)
@[S-RBF-DECISION-SIZEDPTR-CREDENTIAL](rbf-decisions.md)

**属性概要**（详见源文件）：
| 属性 | 类型 | 说明 |
|:-----|:-----|:-----|
| `Offset` | `long` | 以字节表示的起始偏移（4B 对齐） |
| `Length` | `int` | 以字节表示的区间长度（4B 对齐） |
| `EndOffsetExclusive` | `long` | 区间结束位置（不含） |

## term `Frame` 帧
**Frame** 是 RBF 的基本 I/O 单元。Frame 的内部结构（wire format）接口层无需关心。

上层只需知道：
- 每个 Frame 有一个 @`FrameTag`、`PayloadAndMeta` 和 `IsTombstone` 状态
- Frame 写入后返回其 @`SizedPtr`（包含 offset+length）
- Frame 读取通过 @`SizedPtr` 定位

---

## 3. 对外门面（Facade）(Layer 1 Interface)

### spec [A-RBF-IRBFFILE-SHAPE] IRbfFile接口定义

```csharp
/// <summary>RBF 文件对象门面。</summary>
/// <remarks>
/// 职责：资源管理（Dispose）、状态维护（TailOffset）、调用转发。
/// 串行约束：同一 File 及其派生对象访问共享 reader/cache、构建状态或执行 I/O 的操作，由调用方串行；包括 Dispose 与枚举器 MoveNext。不要求固定 OS 线程；独立只读实例可并行。
/// 门面在 Builder 活跃期间拒绝读取与扫描；此前取得的 RbfFrameInfo 可串行读取历史帧。已物化数据及纯元信息值属性按原生命周期使用。
/// </remarks>
public interface IRbfFile : IDisposable {
    /// <summary>获取当前文件逻辑长度（也是下一个写入 Offset）。</summary>
    long TailOffset { get; }

    /// <summary>追加完整帧（payload 已就绪）。</summary>
    /// <remarks>
    /// 失败场景（返回 AteliaResult.IsFailure）：
    /// - TailMeta 超长（> 64KB）
    /// - Payload + TailMeta 超长（> MaxPayloadAndMetaLength）
    /// - TailOffset 非 4B 对齐或超出 SizedPtr 可表示范围
    /// I/O 错误（磁盘满、权限等）仍抛出异常。
    /// </remarks>
    AteliaResult<SizedPtr> Append(uint tag, scoped ReadOnlySpan<byte> payload, scoped ReadOnlySpan<byte> tailMeta = default);

    /// <summary>复杂帧构建（流式写入 payload / payload 内回填）。</summary>
    /// <remarks>
    /// 注意：在 Builder Dispose/EndAppend 前，TailOffset 不会更新。
    /// 注意：存在 open Builder 时，不应允许并发 Append/BeginAppend。
    /// </remarks>
    RbfFrameBuilder BeginAppend();

    /// <summary>读取指定位置的帧到提供的 buffer 中（zero-copy）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">目标缓冲区，长度必须 &gt;= ticket.Length。</param>
    /// <returns>成功时返回帧视图（指向 buffer 内部），失败返回错误。</returns>
    AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer);

    /// <summary>随机读（从 ArrayPool 借缓存）。</summary>
    /// <remarks>
    /// 调用方 MUST 调用返回值的 Dispose() 归还 buffer。
    /// 失败时 buffer 已自动归还。
    /// </remarks>
    AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket);

    /// <summary>逆向扫描，返回帧元信息序列。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    /// <remarks>
    /// CRC 职责分离：ScanReverse 只做 Framing 校验，不校验 PayloadCRC32C。
    /// 如需完整校验，请对返回的 Ticket 调用 ReadFrame/ReadPooledFrame。
    /// </remarks>
    RbfReverseSequence ScanReverse(bool showTombstone = false);

    /// <summary>正向扫描，返回帧元信息序列。</summary>
    /// <param name="showTombstone">是否包含墓碑帧。默认 false（不包含）。</param>
    /// <remarks>
    /// CRC 职责分离：ScanForward 只做 Framing 校验，不校验 PayloadCRC32C。
    /// 如需完整校验，请对返回的 Ticket 调用 ReadFrame/ReadPooledFrame。
    /// </remarks>
    RbfForwardSequence ScanForward(bool showTombstone = false);

    /// <summary>从 SizedPtr 获取帧元信息（只读 TrailerCodeword，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfFrameInfo，失败返回错误。</returns>
    /// <remarks>
    /// I/O：只读取 TrailerCodeword（16B），不读 Payload。
    /// 信任级别：L2（TrailerCrc 校验通过）。
    /// 此方法允许从持久化的 SizedPtr 恢复完整的帧元信息。
    /// </remarks>
    AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= info.TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 内部），失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// 若需完整数据完整性保证，请使用 <see cref="ReadFrame(SizedPtr ptr, Span{byte})"/>。
    /// </remarks>
    AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer);

    /// <summary>读取帧的 TailMeta（预览模式，L2 信任，自动租用 buffer，从 SizedPtr）。</summary>
    /// <param name="ticket">帧位置凭据。</param>
    /// <returns>成功时返回 RbfPooledTailMeta，失败返回错误。</returns>
    /// <remarks>
    /// 信任级别：L2（仅保证 TrailerCrc），不校验 PayloadCrc。
    /// I/O：读取尾部元信息（RBF1 16B；RBF3 Trailer+Key 20B）+ TailMeta 区域。
    /// 此方法是 ReadFrameInfo(ticket) + ReadPooledTailMeta(info) 的便捷组合。
    /// </remarks>
    AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket);

    /// <summary>durable flush（落盘）。</summary>
    /// <remarks>
    /// 用于上层 commit 顺序（例如 data→meta）的 durable 边界。
    /// </remarks>
    void DurableFlush();

}

public static class RbfFile {
    public static IRbfFile CreateNew(string path);       // FailIfExists，只创建 RBF3
    public static IRbfFile OpenExisting(string path, out RbfTailRecoveryReport recovery,
        RbfCacheMode cacheMode = RbfCacheMode.Slots16);    // RBF3 结构打开与单尾恢复；RBF1 可写拒绝
    public static IRbfFile OpenReadOnlyExisting(string path,
        RbfCacheMode cacheMode = RbfCacheMode.Slots16);    // 验证，不修改
}
```

*@[S-RBF-DECISION-READFRAME-RESULTPATTERN]，随机读取 API Result-Pattern（返回 `AteliaResult<RbfFrame>`）。*

### spec [S-RBF-APPEND-RESULTPATTERN] Append使用Result模式
`IRbfFile.Append` MUST 返回 `AteliaResult<SizedPtr>`，使用 Result-Pattern 表达可预见的操作拒绝。

**返回失败的场景**（前置校验，不产生 I/O）：
- `tailMeta.Length > MaxTailMetaLength`（64KB）
- `payload.Length + tailMeta.Length > MaxPayloadAndMetaLength`
- `TailOffset` 非 4B 对齐
- 帧起点 `TailOffset > SizedPtr.MaxOffset`

`SizedPtr.MaxOffset` 只约束帧起点。合法末帧的 FrameBytes 末端及后续 Fence MAY 超过此偏移上界；MUST NOT 因此额外收窄 `SizedPtr` 已允许的帧长度或起点。后续追加仍检查其新的帧起点。

**抛出异常的场景**（系统级故障）：
- 磁盘满、权限不足、设备 I/O 错误等底层异常
- 句柄已释放（`ObjectDisposedException`）

### spec [S-RBF-APPEND-PRECONDITION-CHECK] Append前置校验
`IRbfFile.Append` 的参数校验 MUST 在任何 I/O 操作之前完成：
- 校验失败时 MUST NOT 产生任何文件写入
- 校验失败时 MUST NOT 更新 `TailOffset`
- 校验失败时返回包含 `RecoveryHint` 的 `AteliaError`

### spec [S-RBF-TAILOFFSET-IS-NEXT-WRITE-OFFSET] TailOffset语义
`IRbfFile.TailOffset` MUST 表示“当前逻辑文件长度（byte length）”，并且等于下一次 `Append/BeginAppend` 的写入起点（byte offset）。

### spec [S-RBF-TAILOFFSET-UPDATE] TailOffset更新规则
`TailOffset` MUST 只在以下时刻推进：
- `Append()` 成功返回后；
- `BeginAppend()` 返回的 `RbfFrameBuilder.EndAppend()` 成功返回后。

在 open Builder 的生命周期内（`EndAppend/Dispose` 之前），`TailOffset` MUST NOT 提前更新。

### spec [S-RBF-DURABLEFLUSH-DURABILIZE-COMMITTED-ONLY] DurableFlush语义
`IRbfFile.DurableFlush()` MUST 尝试将“已提交写入”的数据持久化到物理介质。

- 若发生不可恢复的 I/O 错误，允许抛出异常（具体异常类型由实现选择）。
- 本方法不对“未提交的 Builder 写入”做任何可观察承诺。

### spec [S-RBF-OPEN-RECOVERS-SINGLE-INCOMPLETE-TAIL] 普通打开与单尾帧恢复

Header MUST 在 owned handle 上一次分派，MUST NOT 从 EOF、试解码或 payload magic 猜格式；不提供公共 codec/profile 配置。`CreateNew` MUST 只创建 RBF3。`OpenExisting` MUST 在任何修改前拒绝 RBF1；`OpenReadOnlyExisting` MUST 支持 RBF1 与 RBF3。未知 Header、历史实验 RBF2 及 0–3B Header 残留 MUST 拒绝且不修改。

普通 Open MUST 只检查分帧结构，MUST NOT 校验 PayloadCRC 或读取 payload/TailMeta 作内容资格证明。内容及 PayloadCRC 损坏而结构合法时，Open MAY 成功；后续 `ReadFrame` / `ReadPooledFrame` MUST 拒绝坏内容，Open MUST NOT 为内容损坏丢弃帧或回退。

- **RBF1 只读**：MUST 从 Header 连续按 byte HeadLen 检查完整结构主链，包括长度、descriptor、TrailerCRC、padding 与 Fence；残尾拒绝。MUST 保留原字节、旧 ticket 及 24B 开销对应的旧完整容量，不自动升级或转码。
- **RBF3 健康尾**：MUST 检查 Header、尾 Trailer/Key/Fence、长度与直接左 Fence/HeadLenUnits，以及最多3B padding。健康资格只读固定结构，与历史帧数及末帧 payload 大小无关；逻辑请求字节上界为39B。这不是 syscall、cache页、设备I/O或延迟保证，也不证明未访问历史结构或内容完整。
- **RBF3 异常尾**：MUST 逆扫最近完整的全局4B aligned Fence，定位扫描的覆盖跨度最多 `M+7` bytes（`M=2^28-4`），并检查直接前驱结构及单个 suffix；Header/结构资格额外读取不计入该扫描跨度。MUST 在首个 marker 或已呈现结构矛盾处裁决，MUST NOT 越过坏候选寻找更早好帧。真实边界的依据是正常 writer 的 marker-free 顺序前缀，不是局部CRC成功。

`OpenExisting` MUST 在同一个独占可写 handle 上完成 RBF3 结构资格、恢复、最终验证，再建立正式 reader/cache。令真实边界后的帧起点为B，suffix长度为R bytes；完整raw HeadLenUnits须先验证 `7≤U<2^26`，再得到 `L=U<<2` bytes：

| 已有 suffix | 动作 |
| --- | --- |
| 无 suffix / 健康闭合尾 | `None`，不修改 |
| 真实且合法B后的任意1–3B HeadLenUnits前缀 | `Truncated`，截到B；不猜U，不复用RBF1低2bits guard |
| 完整合法U，`4≤R<L-4`，body未完成 | `Truncated`，截到B；不猜Key或解码partial Trailer |
| `L-4≤R≤L+3`，body完整、已有结构通过 | `CompletedTail`，仅追加缺失的原Key/Fence后缀 |
| 完整非法字段、TrailerCRC/Key/padding/Fence矛盾或超出单尾 | 拒绝且不修改 |

完整body的末word为encoded TailLenUnits；MUST 以 `K*=LE_u32(encoded TailLenUnits) XOR U` 唯一重建Key，MUST NOT XOR物理长度L。encoded字段可占全uint32，MUST NOT 在XOR前mask高位或套用U上界。MUST 验 `K*!=F`、完整解码Trailer的原wire CRC及结构/padding，已有Key必须等于 `LE(K*)` 前缀、已有Fence必须等于RBF3 Fence前缀。通过后最多追加8B，不改已有body、Key、CRC、Trailer或其他帧；MUST NOT 补零、合成墓碑或更换Key。

修改 MUST durable flush 并重新验证最终结构尾后才返回。任何 SetLength/write/flush 异常 MUST 关闭 handle 并使打开失败，不能在该 handle 上改走其他恢复动作。成功返回的文件 MUST 是闭合的 `[Fence] ([Frame] [Fence])*` 主序列。

```csharp
public enum RbfTailRecoveryAction {
    None = 0, CompletedFence = 1, CompletedTombstone = 2, Truncated = 3,
    CompletedTail = 4
}
public readonly record struct RbfTailRecoveryReport(
    RbfTailRecoveryAction Action, long OriginalLength, long FinalLength,
    long? AffectedFrameOffset, SizedPtr? FrameTicket);
```

`recovery` 只对成功打开有意义。本轮RBF3只产生 `None / Truncated / CompletedTail`；旧 `CompletedFence / CompletedTombstone` 名称及数值保留，不增加旧格式恢复分支。None的offset/ticket为空；Truncated只有受影响起点，没有可读ticket；CompletedTail返回保留帧的原byte ticket（不含Fence）。报告不是业务 publication 证明。

`OpenReadOnlyExisting` MUST 只验证，不恢复；需恢复时抛 `InvalidDataException`。格式/资格错误使用 `InvalidDataException` 并提供位置/detail；权限和设备 I/O 异常原样传播。离线 scanner/candidate 不受普通打开的恢复策略支配。普通 `IRbfFile` 不提供任意 Truncate，离线显式截断独立保留。

恢复模型限单writer正常顺序追加后因进程终止留下的最后一次append字节前缀，包含闭合后缀续写再次终止；OS/文件系统仍运行。此模型不承诺断电、设备乱序、空洞、sector损坏或多文件事务。RBF1只读资格为O(历史帧数)结构读取；RBF3健康资格固定，异常扫描有界且不分配整帧或计算PayloadCRC。详细动作见[实施方案§4/§6](rbf-open-fast-path-refactoring.md)；[旧RBF1墓碑恢复记录](rbf-tail-recovery-refactoring.md)不支配本轮普通打开。

### spec [S-RBF-WRITER-FAULT-STOPS-INSTANCE] writer 故障后停止实例

原始 Append/Builder 输出或 DurableFlush 抛异常后，实例 MUST 永久拒绝新读写与枚举器 MoveNext，只允许释放与 Dispose，重开时依据实际文件镜像判断。前置 Result/参数/state 拒绝不触发 fault。

### spec [S-RBF-SERIALIZED-INSTANCE-ACCESS] 同一实例串行访问

同一 `IRbfFile` 及其派生对象凡是访问共享 reader/cache、构建状态或执行 I/O 的操作，MUST 由调用方串行，包括 `Dispose()` 与扫描枚举器的 `MoveNext()`。本合同不要求固定 OS 线程；独立只读实例 MAY 并行使用。已物化的帧数据与 `RbfFrameInfo` 等纯元信息值属性不增加访问限制，仍按各自生命周期使用。

该串行合同不改变门面在 open Builder 期间拒绝读取/扫描的规则；此前取得的 `RbfFrameInfo` 可串行读取历史帧，仍遵守 reader 生命周期与共享 fault。枚举器遵守其既有入口及推进规则，不因本合同增加 Building 拒绝条件。

### spec [S-RBF-PREPARATION-FAILURE-BOUNDARY] 预发布准备失败边界

Append的CRC/footer、选Key/RNG等预处理异常若发生于实际文件输出之前，MUST NOT 将健康实例标为write fault。Builder的全部可预见Result/借用/state拒绝 MUST 在首次padding/footer修改前结束，并保留可纠正拒绝后的同Builder重试；从首次padding/footer修改到最终 `Commit` 调用前的异常 MUST 尝试取消/Reset当前Builder，禁止同一Builder重试。Reset 正常完成且无 fault 时，File MUST 回到 Idle 并允许新 Builder；若资源 Reset 自身失败，不承诺同一 File 可继续写，也不承诺保留 Reset 与原异常中的哪一个。最终 `Commit` 开始尝试发布后，任何异常均 MUST 保守地永久 fault，即使不能证明已写出字节；此边界也涵盖 Push 后回收失败而 TailOffset 尚未推进的情况。清 buffer不能解除fault。

File、reader、已有 FrameInfo 和枚举器 MUST 共享一份 fault 事实；缓存命中、零 TailMeta、无 I/O 的结束早退亦 MUST 检查。已物化 buffer/span 与元信息值属性不追溯撤销。Builder Dispose MUST 不重试输出或解除 fault，File Dispose MUST 尝试释放所有 owned resources。

### spec [A-RBF-FRAME-BUILDER] RbfFrameBuilder定义

*see:`atelia/src/Data/IReservableBufferWriter.cs`，此类型扩展了标准的`System.Buffers.IBufferWriter<byte>`*

```csharp
/// <summary>帧构建器。支持流式写入 payload，并支持在 payload 内进行预留与回填。</summary>
/// <remarks>
/// 生命周期：调用方 MUST 调用 <see cref="EndAppend"/> 或 <see cref="Dispose"/> 之一来结束构建器生命周期。
/// Auto-Abort（Optimistic Clean Abort）：若未 EndAppend 就 Dispose，
/// 逻辑上该帧视为不存在；物理实现规则见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
/// 类型选择：采用 readonly struct 作为一次性值对象，避免 Builder 复用带来的语义混淆。
/// 注意：Builder 为值类型，请避免复制并跨生命周期使用；旧 Builder 会因 epoch 不匹配而失败。
/// </remarks>
public readonly struct RbfFrameBuilder : IDisposable {
    /// <summary>Payload 写入器。</summary>
    /// <remarks>
    /// 该写入器实现 <see cref="IBufferWriter{T}"/>，因此可用于绝大多数序列化场景。
    /// 此外它支持 reservation（预留/回填），供需要在 payload 内延后写入长度/计数等字段的 codec 使用。
    /// 接口定义（SSOT）：<c>atelia/src/Data/IReservableBufferWriter.cs</c>（类型：<see cref="IReservableBufferWriter"/>）。
    /// 注意：Payload 类型本身不承诺 Auto-Abort 一定为 Zero I/O；
    /// 健康取消不输出；系统资源释放异常可能传播，见 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]。
    /// </remarks>
        public RbfPayloadWriter PayloadAndMeta { get; }

    /// <summary>提交帧。回填 header/CRC，返回帧位置和长度。</summary>
    /// <returns>成功返回帧位置与长度；失败返回错误。</returns>
    /// <remarks>
    /// Result-Pattern：可预见的参数/状态错误返回 <see cref="AteliaResult{T}"/> 失败；I/O 异常仍抛出。
    /// </remarks>
    public AteliaResult<SizedPtr> EndAppend(uint tag, int tailMetaLength = 0);

    /// <summary>释放构建器。若未 EndAppend，自动执行 Auto-Abort。</summary>
    /// <remarks>
    /// 健康取消不主动抛状态异常、不输出帧并回到可继续写状态；已有 writer fault 不解除。
    /// 资源归还、内存不足等系统异常可能传播；发生后不保证同一 File 可继续写。
    /// </remarks>
    public void Dispose();
}
```

#### RbfPayloadWriter
`RbfPayloadWriter` 是一个 readonly struct，内部携带 epoch 信息并在每次调用时做鉴权。
这可避免旧 writer 被长期持有后在错误时机写入。

**注意**：若上层将其上转为 `IReservableBufferWriter`，会产生装箱；如无必要，建议使用 `var` 保持值类型形态。

**关键语义**：

### spec [S-RBF-BUILDER-ENDAPPEND-RESULTPATTERN] EndAppend使用Result模式
`RbfFrameBuilder.EndAppend` MUST 返回 `AteliaResult<SizedPtr>`，使用 Result-Pattern 表达可预见的操作拒绝。

**返回失败的场景**（前置校验，不产生 I/O）：
- `tailMetaLength < 0` 或 `tailMetaLength > payloadAndMetaLength`
- `payloadAndMetaLength > MaxPayloadAndMetaLength`
- 存在未提交 reservation（除 HeadLen 外）
- builder 状态不允许（重复提交、已 Dispose）
- 帧起点 `TailOffset > SizedPtr.MaxOffset`；末帧末端可超过该偏移上界

**抛出异常的场景**（系统级故障）：
- 磁盘满、权限不足、设备 I/O 错误等底层异常


### spec [S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME] Auto-Abort逻辑语义
若 `RbfFrameBuilder` 未调用 `EndAppend()` 就执行 `Dispose()`：

**逻辑语义**：
- 该帧视为**逻辑不存在**（logical non-existence）
- 上层 Record Reader 遍历时 MUST NOT 看到此帧作为业务记录

**后置条件**：
- 资源归还正常完成且此前无 fault 时，`Dispose()` MUST 将实例恢复到 Idle；此后健康实例的 `Append()` / `BeginAppend()` MUST 可继续成功
- 若已有 writer fault，Builder `Dispose()` MUST 不再输出、不解除 fault；File 须释放后重开
- Auto-Abort 不主动抛出状态异常；资源归还、内存不足等系统异常 MAY 传播，发生后不保证同一 File 可继续写

此机制防止上层异常导致 Writer 死锁，同时在可能时优化为零 I/O。取消不发布帧，也不解除既有 fault。


### spec [S-RBF-BUILDER-SINGLE-OPEN] 单Builder约束
同一 `IRbfFile` 实例同时最多允许 1 个 open `RbfFrameBuilder`。
在前一个 Builder 完成（EndAppend 或 Dispose）前调用 `BeginAppend()` MUST 抛出 `InvalidOperationException`。

### spec [S-RBF-READ-DISALLOW-WHILE-BUILDER-ACTIVE] Builder活跃时禁止读取与扫描
当存在 open Builder（`BeginAppend()` 与 `EndAppend/Dispose` 之间）时，以下 `IRbfFile` 门面入口拒绝读取：
- `ReadFrame` / `ReadPooledFrame` / `ReadFrameInfo` / `ReadTailMeta` / `ReadPooledTailMeta` MUST 抛出 `InvalidOperationException`。
- `ScanReverse` MUST 抛出 `InvalidOperationException`。

已取得的 `RbfFrameInfo` 绑定 Reader，其历史帧读取不受此门面 Building 检查约束，仍须遵守 Reader 的 Dispose 与共享 fault 拒绝。

### spec [S-RBF-FRAMEINFO-DEFAULT-READS] default FrameInfo拒绝读取

default `RbfFrameInfo` 的四个读取入口 MUST 在 ticket 检查、零长度早退、buffer 租用或 I/O 前抛出 `InvalidOperationException`。其值属性与相等性仍保持普通 struct 默认值语义。

---

## 4. 读取与扫描（通过 IRbfFile 暴露）

`ReadFrame()` 的 framing/CRC 校验由 [rbf-format.md](rbf-format.md) 定义。
`ScanReverse()` 仅做 Framing 校验（含 `TrailerCrc32C`）并输出元信息，不做 `PayloadCrc32C` 校验。
本节只约束上层可观察到的结果形态与序列语义。

### spec [A-RBF-FRAME-INFO] RbfFrameInfo定义

```csharp
/// <summary>已验证的帧元信息句柄（不含 Payload）。</summary>
/// <remarks>
/// 用于 ScanReverse 产出，支持不读取 payload 的元信息迭代。
/// PayloadLength 与 TailMetaLength 从 TrailerCodeword 解码得出。
/// 句柄语义：构造时已完成 TrailerCrc、reserved bits、TailLen 一致性等验证，
/// 后续读取方法只做 I/O 级校验（buffer length、short read），不重复结构性验证。
/// 生命周期：File 为非拥有引用，调用方 MUST 确保 File 在使用期间有效。
/// </remarks>
public readonly struct RbfFrameInfo : IEquatable<RbfFrameInfo> {
    /// <summary>帧位置凭据。</summary>
    public SizedPtr Ticket { get; }

    /// <summary>帧标签。</summary>
    public uint Tag { get; }

    /// <summary>Payload 长度（字节）。</summary>
    public int PayloadLength { get; }

    /// <summary>TailMeta 长度（字节）。</summary>
    public int TailMetaLength { get; }

    /// <summary>是否为墓碑帧。</summary>
    public bool IsTombstone { get; }

    #region Read Methods（成员方法）

    /// <summary>读取 TailMeta 到调用方提供的 buffer（L2 信任，不校验 PayloadCrc）。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= TailMetaLength。</param>
    /// <returns>成功时返回 RbfTailMeta（TailMeta 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 最小化 I/O：只读取 TailMeta 区域，不读 Payload 或 TrailerCodeword。
    /// L2 信任：依赖构造时已完成的 TrailerCrc 校验，不做 PayloadCrc。
    /// 生命周期：返回的 TailMeta 直接引用 buffer，调用方 MUST 确保 buffer 有效。
    /// </remarks>
    public AteliaResult<RbfTailMeta> ReadTailMeta(Span<byte> buffer);

    /// <summary>读取 TailMeta（自动租用 buffer，L2 信任）。</summary>
    /// <returns>成功时返回 RbfPooledTailMeta，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
    /// TailMetaLength = 0：不租 buffer，返回无 buffer 的 RbfPooledTailMeta。
    /// 生命周期：成功时调用方拥有 buffer 所有权，MUST 调用 Dispose。
    /// </remarks>
    public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta();

    /// <summary>读取完整帧到调用方提供的 buffer 中。</summary>
    /// <param name="buffer">调用方提供的 buffer，长度 MUST &gt;= Ticket.Length。</param>
    /// <returns>成功时返回 RbfFrame（Payload 指向 buffer 子区间），失败时返回错误。</returns>
    /// <remarks>
    /// 复用创建 FrameInfo 时取得的 TrailerCRC 资格；本次仍验证 PayloadCRC，覆盖 Payload、TailMeta 与 Padding（L3 信任级别）。
    /// </remarks>
    public AteliaResult<RbfFrame> ReadFrame(Span<byte> buffer);

    /// <summary>读取完整帧（自动租用 buffer）。</summary>
    /// <returns>成功时返回 RbfPooledFrame，失败时返回错误（buffer 已自动归还）。</returns>
    /// <remarks>
    /// 复用创建 FrameInfo 时取得的 TrailerCRC 资格；本次仍验证 PayloadCRC，覆盖 Payload、TailMeta 与 Padding（L3 信任级别）。
    /// </remarks>
    public AteliaResult<RbfPooledFrame> ReadPooledFrame();

    #endregion

    #region Equality

    public bool Equals(RbfFrameInfo other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public static bool operator ==(RbfFrameInfo left, RbfFrameInfo right);
    public static bool operator !=(RbfFrameInfo left, RbfFrameInfo right);

    #endregion
}
```

### spec [S-RBF-FRAMEINFO-USERMETALEN-RANGE] TailMetaLength值域
`RbfFrameInfo.TailMetaLength` MUST 满足：`0 <= TailMetaLength <= 65535`。

**上限来源**：`FrameDescriptor.TailMetaLen` 字段为 16-bit（SSOT：[rbf-format.md](rbf-format.md) @[F-FRAME-DESCRIPTOR-LAYOUT]）。

### spec [S-RBF-FRAMEINFO-PAYLOADLEN-RANGE] PayloadLength值域
对已验证的 `ticket` / `RbfFrameInfo`，`PayloadLength` MUST 由字节长度计算：

```text
PayloadLength = ticket.Length - overhead - TailMetaLength - PaddingLength
PayloadLength >= 0
PayloadLength + TailMetaLength + PaddingLength <= SizedPtr.MaxLength - overhead
RBF1 overhead = 24 bytes; maximum PayloadLength = 268435428 bytes
RBF3 overhead = 28 bytes; maximum PayloadLength = 268435424 bytes
```

`MaxPayloadAndMetaLength` 只约束新 writer 的 `Append` / `EndAppend` 输入，不是通用读取上限；TailMeta 已在公式中扣除一次。长度先在 profile 边界归一化为 bytes，再按 [rbf-format.md](rbf-format.md) @[S-RBF-PAYLOADLENGTH-FORMULA] 计算，RBF1 旧完整容量保留。

### derived [H-RBF-FRAMEINFO-USERMETA-READING] 读取TailMeta
调用方可通过 `RbfFrameInfo` 的成员方法直接读取数据：
- `info.ReadTailMeta(buffer)` / `info.ReadPooledTailMeta()` — L2 信任级别
- `info.ReadFrame(buffer)` / `info.ReadPooledFrame()` — L3 完整校验

### derived [H-RBF-TRUST-LEVELS] 信任级别模型
RBF 读取 API 按 CRC 校验程度分为三个信任级别：

| 级别 | 校验内容 | API | 信任断言 |
|:-----|:---------|:----|:---------|
| **L1: Framing** | TrailerCodeword 可解码 | `ScanReverse` | "这是一个结构合法的帧" |
| **L2: Meta** | TrailerCrc 通过 | `info.ReadTailMeta`, `info.ReadPooledTailMeta` | "尾部元信息结构通过校验；TailMeta bytes未做内容CRC" |
| **L3: Full** | PayloadCrc + TrailerCrc | `info.ReadFrame`, `info.ReadPooledFrame` | "整个帧内容完整" |

调用方根据场景选择适当的信任级别：预览/筛选场景用 L2，业务处理用 L3。

### spec [A-RBF-REVERSE-SEQUENCE] RbfReverseSequence定义

```csharp
/// <summary>逆向扫描序列（duck-typed 枚举器，支持 foreach）。</summary>
/// <remarks>
/// 设计说明：返回 ref struct 以避免堆分配并满足栈上生命周期约束。
/// 上层通过 foreach 消费，不依赖 LINQ。
/// </remarks>
public ref struct RbfReverseSequence {
    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfReverseEnumerator GetEnumerator();
}

/// <summary>逆向扫描枚举器。</summary>
public ref struct RbfReverseEnumerator {
    /// <summary>当前帧元信息。</summary>
    public RbfFrameInfo Current { get; }

    /// <summary>移动到下一帧。</summary>
    public bool MoveNext();

    /// <summary>迭代终止原因。null 表示正常结束。</summary>
    public AteliaError? TerminationError { get; }
}
```

### spec [A-RBF-SCAN-BOUNDARY] 有见证的边界起扫

```csharp
public readonly record struct RbfScanBoundary(
    long EndExclusive, SizedPtr AnchorTicket, uint AnchorContentCrc32C) {
    public static RbfScanBoundary Empty => new(4, default, 0);
}
// IRbfFile
AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket);
AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false);
```

`GetScanBoundaryAfter` MUST 只读取指定帧及其尾 Fence，执行完整 framing、PayloadCrc 和 TrailerCrc 校验。
`EndExclusive` 等于 `GetPhysicalOffsetImmediatelyAfter(ticket)`。
内容见证 CRC32C 的输入严格为 `LE u32 Tag || LE u32 TailMetaLength || LE u32 IsTombstone(0/1) || PayloadAndMeta`，不含 padding 或存储 CRC。

边界起扫 MUST 先检查 end 的 4B 对齐及 `4 <= end <= EOF`。
空 anchor 只允许精确的 `(4, default, 0)`；该边界可用于已有 suffix 的非空文件。
Frame anchor MUST 验证 ticket 与 end 一致、完整帧 CRC、内容见证及尾 Fence，随后直接从 end 起扫，MUST NOT 为寻找边界扫描 prefix。
Malformed 参数返回 `Rbf.ArgumentError`；损坏 framing 和 CRC 返回对应 RBF 错误；I/O 异常保留。
起扫后的枚举仍遵循 framing/trailer 校验及 `TerminationError`、tombstone 过滤合同。
缓存可能预读 anchor 邻近页的 prefix 字节，但不得解析或验证无关 prefix 帧。

### spec [S-RBF-DETERMINISTIC-TAIL] 确定性尾读基础

只有 HeaderFence 正确且物理文件长度恰为 4B 才是空文件。
`ScanReverse(showTombstone: true)` MUST 对非空短尾返回非 null `TerminationError`，MUST NOT 将其吞为空序列或向前寻找有效帧。
需要完整尾帧的调用方只取紧贴 EOF 的第一帧，核对 `GetPhysicalOffsetImmediatelyAfter(ticket) == EOF`，并使用 `ReadFrame` / `ReadPooledFrame` 执行完整 CRC 校验。
只读打开要求已闭合的4B对齐文件；RBF3可写打开 MAY 恢复非对齐的最后byte前缀，成功返回时EOF重新闭合且4B对齐。读取失败不修改文件bytes。

### spec [A-RBF-FORWARD-SEQUENCE] RbfForwardSequence定义

```csharp
/// <summary>正向扫描序列（duck-typed 枚举器，支持 foreach）。</summary>
/// <remarks>
/// 设计说明：返回 ref struct 以避免堆分配并满足栈上生命周期约束。
/// 上层通过 foreach 消费，不依赖 LINQ。
/// </remarks>
public ref struct RbfForwardSequence {
    /// <summary>获取枚举器（支持 foreach 语法）。</summary>
    public RbfForwardEnumerator GetEnumerator();
}

/// <summary>正向扫描枚举器。</summary>
public ref struct RbfForwardEnumerator {
    /// <summary>当前帧元信息。</summary>
    public RbfFrameInfo Current { get; }

    /// <summary>移动到下一帧。</summary>
    public bool MoveNext();

    /// <summary>迭代终止原因。null 表示正常结束。</summary>
    public AteliaError? TerminationError { get; }
}
```

### derived [S-RBF-SCANREVERSE-NO-IENUMERABLE] ScanReverse不实现IEnumerable
`RbfReverseSequence` MUST NOT 实现 `IEnumerable<T>`。
**原因**：该序列与枚举器为 `ref struct`，以避免堆分配并满足栈上生命周期约束。

### derived [S-RBF-SCANFORWARD-NO-IENUMERABLE] ScanForward不实现IEnumerable
`RbfForwardSequence` MUST NOT 实现 `IEnumerable<T>`。
**原因**：该序列与枚举器为 `ref struct`，以避免堆分配并满足栈上生命周期约束。

### spec [S-RBF-READFRAME-ALWAYS-CRC] ReadFrame始终执行CRC校验
完整读取 MUST 具备 `PayloadCrc32C` 与 `TrailerCrc32C` 两项有效校验资格；普通ticket入口与FrameInfo入口的取得方式如下。
普通ticket入口在每次完整读中 MUST 验证两项CRC；Open结构成功不能替代内容校验。`RbfFrameInfo` 管线由创建时的TrailerCRC资格与每次 `info.ReadFrame` / `ReadPooledFrame` 的PayloadCRC共同覆盖两项CRC，MAY 复用已取得且不变的尾部元信息资格（RBF3包括Key），无需重复验TrailerCRC。这同时适用于RBF1/RBF3。普通ticket读取不检查ticket外Fence，也不证明主链成员身份。

普通工厂的handle共享策略禁止其他writer，`IRbfFile`只追加且不提供Truncate；已提交帧不被后续Append修改。FrameInfo完整读仍须遵守Reader生命周期与共享fault拒绝，活跃Builder不使历史帧资格失效。离线共享Write扫描器不继承此保证：调用方 MUST 在资格创建至后续读取期间冻结输入，不能把可变镜像上的旧FrameInfo当成当前资格。
当 CRC 校验失败时，MUST 通过 `AteliaResult` 返回失败（错误类型由实现定义）。

### spec [S-RBF-SCANREVERSE-NO-PAYLOADCRC] ScanReverse不进行PayloadCRC校验
`ScanReverse(...)` MUST NOT 执行 `PayloadCrc32C` 校验。
`ScanReverse(...)` MUST 执行 framing 校验 + 尾部元信息校验（`TrailerCrc32C`）并输出 `RbfFrameInfo`。

### spec [S-RBF-SCANFORWARD-NO-PAYLOADCRC] ScanForward不进行PayloadCRC校验
`ScanForward(...)` MUST NOT 执行 `PayloadCrc32C` 校验。
`ScanForward(...)` MUST 执行 framing 校验 + 尾部元信息校验（`TrailerCrc32C`）并输出 `RbfFrameInfo`。

**CRC 职责分离（Normative）**：
- **ScanReverse / ScanForward**：只校验 `TrailerCrc32C`（覆盖FrameDescriptor、FrameTag及profile原wire长度：RBF1 LE(L)，RBF3 LE(U)）
- **ReadFrame / ReadPooledFrame**：具备 `PayloadCrc32C` + `TrailerCrc32C` 资格，ticket每次检查两项，info管线按 @[S-RBF-READFRAME-ALWAYS-CRC] 复用尾部资格

如需完整校验，调用方 MUST 使用返回的 `Ticket` 完整读取入口或 `info.ReadFrame` / `info.ReadPooledFrame`。

### spec [S-RBF-SCANREVERSE-EMPTY-IS-OK] 空序列合法
当文件为空（仅含 HeaderFence）或 **根据过滤条件无可见帧** 时，`ScanReverse()` MUST 返回空序列（0 元素），MUST NOT 抛出异常。

### spec [S-RBF-SCANFORWARD-EMPTY-IS-OK] ScanForward空序列合法
当文件为空（仅含 HeaderFence）或 **根据过滤条件无可见帧** 时，`ScanForward()` MUST 返回空序列（0 元素），MUST NOT 抛出异常。

### spec [S-RBF-SCANREVERSE-TERMINATION-ERROR] 终止错误语义
当 `MoveNext()` 返回 `false` 时：
- 若 `TerminationError` 为 `null`，表示正常结束；
- 若 `TerminationError` 非 `null`，表示因 framing 损坏或读取失败而提前终止。

### spec [S-RBF-SCANFORWARD-TERMINATION-ERROR] ScanForward终止错误语义
当 `MoveNext()` 返回 `false` 时：
- 若 `TerminationError` 为 `null`，表示正常结束；
- 若 `TerminationError` 非 `null`，表示因 framing 损坏或读取失败而提前终止。

### spec [S-RBF-SCANREVERSE-CURRENT-LIFETIME] DEPRECATED
该条款源于 `Current` 返回 `RbfFrame` 的旧契约。
现已改为 `RbfFrameInfo` 值语义，不再受 buffer 生命周期约束。

### spec [S-RBF-SCANREVERSE-CURRENT-VALUE] Current为值快照
`RbfReverseEnumerator.Current` MUST 返回 `RbfFrameInfo` 的值快照，
其生命周期不依赖底层 buffer，可安全跨越后续 `MoveNext()` 调用。

### spec [S-RBF-SCANFORWARD-CURRENT-VALUE] ScanForward Current为值快照
`RbfForwardEnumerator.Current` MUST 返回 `RbfFrameInfo` 的值快照，
其生命周期不依赖底层 buffer，可安全跨越后续 `MoveNext()` 调用。

### spec [A-RBF-IFRAME] IRbfFrame接口定义

```csharp
/// <summary>RBF 帧的公共属性契约。</summary>
public interface IRbfFrame {
    /// <summary>帧位置（凭据）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>帧负载数据。</summary>
    ReadOnlySpan<byte> Payload { get; }

    /// <summary>用户元数据。</summary>
    ReadOnlySpan<byte> TailMeta { get; }

    /// <summary>是否为墓碑帧。</summary>
    bool IsTombstone { get; }
}
```

### spec [A-RBF-FRAME-STRUCT] RbfFrame定义

```csharp
/// <summary>RBF 帧数据结构。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 Scope（如 ReadFrame 的 buffer）。
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// </remarks>
public readonly ref struct RbfFrame : IRbfFrame {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; init; }

    /// <inheritdoc/>
    public uint Tag { get; init; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload { get; init; }

    /// <inheritdoc/>
    public bool IsTombstone { get; init; }
}
```

### spec [A-RBF-POOLED-FRAME] RbfPooledFrame定义

```csharp
/// <summary>携带 ArrayPool buffer 的 RBF 帧。</summary>
/// <remarks>
/// 属性契约：遵循 <see cref="IRbfFrame"/> 定义的公共属性集合。
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 Payload 变为 dangling，不可再访问。
/// </remarks>
public sealed class RbfPooledFrame : IRbfFrame, IDisposable {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> Payload { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose();
}
```

### spec [A-RBF-IRBTAILMETA] IRbfTailMeta接口定义

```csharp
/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public interface IRbfTailMeta {
    /// <summary>帧位置凭据（支持"预览→完整读取"工作流）。</summary>
    SizedPtr Ticket { get; }

    /// <summary>帧类型标识符。</summary>
    uint Tag { get; }

    /// <summary>TailMeta 数据（可能为 <see cref="ReadOnlySpan{T}.Empty"/>）。</summary>
    ReadOnlySpan<byte> TailMeta { get; }

    /// <summary>是否为墓碑帧。</summary>
    bool IsTombstone { get; }

    // 注意：根据"诚实地贫瘠"原则，不暴露 PayloadLength
}
```

### spec [A-RBF-TAILMETA-FRAME] RbfTailMeta定义

```csharp
/// <summary>TailMeta 预览结果（L2 信任级别：仅保证 TrailerCrc）。</summary>
/// <remarks>
/// 只读引用结构，生命周期受限于产生它的 buffer。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// TailMeta 字节本身不做 PayloadCrc 校验，可能已损坏（但 TrailerCrc 已通过）。
/// 若需完整数据完整性保证，请使用 <see cref="IRbfFile.ReadFrame(SizedPtr, Span{byte})"/>。
/// </remarks>
public readonly ref struct RbfTailMeta : IRbfTailMeta {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    public ReadOnlySpan<byte> TailMeta { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>内部构造函数（只能由验证路径调用）。</summary>
    internal RbfTailMeta(SizedPtr ticket, uint tag, ReadOnlySpan<byte> tailMeta, bool isTombstone);
}
```

### spec [A-RBF-POOLED-TAILMETA] RbfPooledTailMeta定义

```csharp
/// <summary>携带 ArrayPool buffer 的 TailMeta 预览结果（L2 信任）。</summary>
/// <remarks>
/// 调用方 MUST 调用 <see cref="Dispose"/> 归还 buffer。
/// 生命周期警告：Dispose 后 TailMeta 变为 dangling，不可再访问。
/// Buffer 租用：只租 TailMetaLength 大小，不租整帧大小。
/// 信任声明：本类型只保证 TrailerCrc 校验通过（L2），不保证 PayloadCrc（L3）。
/// </remarks>
public sealed class RbfPooledTailMeta : IDisposable, IRbfTailMeta {
    /// <inheritdoc/>
    public SizedPtr Ticket { get; }

    /// <inheritdoc/>
    public uint Tag { get; }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">对象已 Dispose（且原有 buffer）。</exception>
    public ReadOnlySpan<byte> TailMeta { get; }

    /// <inheritdoc/>
    public bool IsTombstone { get; }

    /// <summary>释放 ArrayPool buffer。幂等，可多次调用。</summary>
    public void Dispose();
}
```

### spec [S-RBF-READTAILMETA-L2-TRUST] ReadTailMeta信任语义
`ReadTailMeta` / `ReadPooledTailMeta` MUST 提供 L2 信任级别：
- 依赖 `ScanReverse` 已完成的 `TrailerCrc32C` 校验
- MUST NOT 执行 `PayloadCrc32C` 校验
- 只读取 TailMeta 区域（最小化 I/O）

### spec [S-RBF-READTAILMETA-EMPTY-OK] 空TailMeta合法
当 `info.TailMetaLength == 0` 时，`ReadTailMeta` MUST 返回成功 + 空 `ReadOnlySpan<byte>`。
`ReadPooledTailMeta` 在此场景 MUST NOT 租用 ArrayPool buffer。

---

## 5. 使用示例（Informative）

*已移动至 [rbf-guide.md](rbf-guide.md)*

---

## 6. 最近变更

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.35 | 2026-02-02 | **术语一致性**：修正两处遗留的 `Commit` 为 `EndAppend`（@[S-RBF-TAILOFFSET-UPDATE] 和 @[S-RBF-BUILDER-SINGLE-OPEN]） |
| 0.34 | 2026-01-28 | **ReadFrameInfo 接口**：新增 `ReadFrameInfo(SizedPtr)` 从 ticket 恢复帧元信息；新增 `ReadPooledTailMeta(SizedPtr)` 便捷重载；补齐"SizedPtr → TailMeta"的读取路径 |
| 0.33 | 2026-01-26 | **ReadTailMeta 接口**：新增 `ReadTailMeta` / `ReadPooledTailMeta` 方法用于大帧预览场景；新增 `RbfTailMeta` / `RbfPooledTailMeta` 类型；定义三层信任模型（L1/L2/L3） |
| 0.32 | 2026-01-24 | **CRC 术语澄清**：修复"ScanReverse 不做 CRC"的歧义表述，明确 ScanReverse 必须做 `TrailerCrc32C` 校验；增加 `TailMetaLength` 和 `PayloadLength` 的值域上限约束；条款 `[S-RBF-SCANREVERSE-NO-CRC]` 更名为 `[S-RBF-SCANREVERSE-NO-PAYLOADCRC]` |
| 0.31 | 2026-01-24 | **Format对齐**：`RbfFrameInfo` 字段重命名 `MetaTrailerLength` -> `TailMetaLength` 以适配 wire-format；更新 ScanReverse 校验描述（适配 `TrailerCrc32C`）；修正 `RbfFrameBuilder` 签名为 `EndAppend`；废弃 PayloadTrailer 相关描述 |
| 0.30 | 2026-01-17 | **ReadFrame 重构**：移除旧签名 `ReadFrame(SizedPtr)`，新增 `ReadFrame(ticket, buffer)` + `ReadPooledFrame(ticket)`；`RbfFrame.Ptr` → `Ticket`；新增 `IRbfFrame` 接口和 `RbfPooledFrame` 类型；更新 `SizedPtr` 属性引用（`Offset`/`Length`） |
| 0.29 | 2026-01-14 | **IDisposable 显式声明**：`RbfFrameBuilder` 添加 `: IDisposable` 声明，明确类型系统语义；来自团队设计讨论 |
| 0.28 | 2026-01-12 | **方法重命名**：`RbfFrameBuilder.Commit()` →  `EndAppend()`，与 `BeginAppend()` 形成对称配对，最大化 LLM 可预测性；详见[命名讨论会](../../../../agent-team/meeting/2026-01-12-rbf-builder-lifecycle-naming.md) |
| 0.27 | 2026-01-12 | **Tombstone 默认隐藏**：修改 `ScanReverse` 接口增加 `bool showTombstone = false` 参数；废弃 `[S-RBF-TOMBSTONE-VISIBLE]` 改为 `[S-RBF-SCANREVERSE-TOMBSTONE-FILTER]`，确立默认过滤 Tombstone 的行为 |
| 0.26 | 2026-01-11 | **文档职能分离**：拆分 Auto-Abort 条款为逻辑语义（本文档 @[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]）+ 实现路径（type-bone.md @[I-RBF-BUILDER-AUTO-ABORT-IMPL]）；明确本文档为规范性契约，type-bone.md 为非规范性实现指南 |
| 0.25 | 2026-01-11 | **接口细节对齐**：`Truncate` 参数类型改为 `long`（与 `TailOffset` 一致）；更新文档关系表中 `rbf-type-bone.md` 层级描述；§3 标题增加层级标注；统一 `RbfFrame` 生命周期注释；设计原则位置调整 |

## 7. 实现边界的当前状态

旧P2问题不再作为本轮待实现项：读取错误的参数/损坏分层及ScanReverse损坏时硬停止、TerminationError已由§4约束，RBF1/RBF3行为按当前源码与测试执行。源码、测试身份与正式探针结果见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)；离线救援、下游与包交付仍按其独立范围处理。

---
