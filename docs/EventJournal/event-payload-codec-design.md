# EventJournal Payload Codec 设计方案

> **状态**：Design Proposal（建议进入 EventFrame v2 wire format）
> **日期**：2026-07-24
> **动机**：为 SessionJournal 等长寿命文本事件提供写入时可选压缩，同时保持上层 payload 语义稳定。
> **相关文档**：[EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)、[EventJournal 功能需求与粗粒度设计基线](event-journal-requirements-and-design.md)、[SessionJournal 主干设计基线](../SessionJournal/session-journal-trunk-design.md)

## 1. 结论

EventJournal 应在 EventFrame wire format 层预留并实现 payload codec metadata。压缩不应下沉到 RBF frame 层，也不应要求 SessionJournal 在领域 envelope 内自行编码压缩。

推荐分层：

```text
SessionJournal logical body
  -> canonical JSON bytes                  // 上层事实 bytes
  -> EventJournal optional payload codec   // 存储策略
  -> RBF stored payload bytes              // 二进制 frame payload
```

对外语义：

- `AppendEventFrame(..., payload)` 接受的是 **logical payload bytes**。
- `ReadEvent(address).Payload` 返回的是 **logical payload bytes**。
- RBF frame 内保存的是 **stored payload bytes**，可能等于 logical payload，也可能是压缩结果。
- EventFrame TailMeta header 记录 codec id 与 logical length。stored length 从 RBF frame 自身派生，Parent walk 和 header preview 不需要读取或解压 payload。

这能满足两条核心约束：

1. SessionJournal 的 canonical JSON 仍是事实源，压缩不是领域 schema 的一部分。
2. EventJournal 的存储格式现在就能容纳压缩，后续无需大改 EventFrame header。

## 2. 为什么放在 EventJournal

### 2.1 不放 RBF

RBF 是通用二进制信封，当前契约强依赖 “写入什么 payload，就读取什么 `PayloadAndMeta`”：

- `SizedPtr.Length` 指向物理 frame 区间。
- `RbfFrameInfo.PayloadLength` 从 trailer 解码，表达 RBF stored payload length，用于只读元信息扫描。
- `ReadFrame(ptr, buffer)` 的 buffer size 以物理 frame ticket 为边界。
- `ReadTailMeta` 只读 TailMeta，不读 payload，提供 L2 preview。
- `RbfFrameBuilder.PayloadAndMeta` 支持 reservation / backfill，适合上层 codec 自己构造 payload。

如果 RBF 透明解压，就必须重新定义 physical length、logical length、zero-copy、TailMeta offset、builder streaming 和 CRC 覆盖对象。这会把 RBF 从“贫瘠信封”变成“内容存储策略层”，破坏现有边界。

### 2.2 不放 SessionJournal

SessionJournal 的 payload 是 canonical JSON envelope。把压缩塞进 JSON body 会导致：

- 领域事件 schema 混入存储策略。
- 每个上层应用重复实现 codec negotiation、阈值、失败策略。
- header preview 仍不知道 stored/logical length 等通用信息。
- 后续 request manifest 的 hash 语义更容易混乱。

压缩应属于 EventJournal：它知道 EventFrame 的 stored payload 与 header，也能对所有上层应用统一提供透明读写。

### 2.3 FS 透明压缩仍可叠加

ZFS/Btrfs 等文件系统压缩仍然有价值，但它是部署优化，不是 Atelia wire format 能力。EventJournal payload codec 与 FS 压缩可以同时存在；默认应避免高压缩等级，减少双重压缩造成的 CPU 浪费。

## 3. 术语

| 名称 | 含义 |
|:-----|:-----|
| logical payload | 调用方传给 `AppendEventFrame`、`ReadEvent` 返回给调用方的 bytes。对 SessionJournal 来说就是 canonical JSON bytes。 |
| stored payload | 实际写入 RBF frame payload 区域的 bytes。未压缩时等于 logical payload。 |
| payload codec | logical payload 与 stored payload 之间的可逆转换。初始只考虑 lossless codec。 |
| identity codec | codec id = 0，不转换，stored payload = logical payload。 |

## 4. EventFrame Header v2

因为 EventJournal 尚未实际投入使用，建议直接把 EventFrame TailMeta header 升级为 v2，不保留 v1 兼容 reader，测试与文档同步迁移。v2 仍保持固定 64 bytes，只把 v1 的 `Flags` 字段收缩为 codec metadata 和 reserved bytes，并把 `PayloadLength` 收缩为 `u32` logical length。

v2 使用 little-endian。`HeaderCrc32C` 覆盖 `[0, 60)`。

| Offset | Size | 字段 | 类型 | 说明 |
|------:|-----:|:-----|:-----|:-----|
| 0 | 4 | `Magic` | `u32` | 继续使用 `EJH1` magic，版本字段区分格式 |
| 4 | 2 | `FormatVersion` | `u16` | v2 固定为 `2` |
| 6 | 2 | `HeaderLength` | `u16` | v2 固定为 `64` |
| 8 | 2 | `PayloadCodecId` | `u16` | `0 = identity`，其他值见 §5 |
| 10 | 2 | `Reserved0` | `u16` | 必须写 0；reader 必须拒绝非 0 |
| 12 | 8 | `SequenceNumber` | `u64` | store-local 单调序号 |
| 20 | 8 | `UtcUnixTimeMilliseconds` | `i64` | UTC Unix time milliseconds |
| 28 | 4 | `OpaqueEventKind` | `u32` | 应用定义的 opaque event kind |
| 32 | 4 | `Hint` | `u32` | 与 `EventAddress.Hint.Packed` 一致 |
| 36 | 4 | `PayloadLength` | `u32` | logical payload bytes 长度 |
| 40 | 4 | `Reserved1` | `u32` | 必须写 0；reader 必须拒绝非 0 |
| 44 | 16 | `Parent` | `EventAddress` | null parent 使用全零地址 |
| 60 | 4 | `HeaderCrc32C` | `u32` | 覆盖 `[0, 60)` |

v2 不设置 `Flags.HasParent`。`Parent` 全零即 null；非零 parent 必须通过 `EventAddressCodec.TryDecodeNullable` 校验，半空地址非法。header CRC 已能发现普通损坏，额外的 parent flag 只会增加不一致状态而缺少足够收益。

关于 `Reserved0` / `Reserved1`：严格「非 0 即拒绝」是本阶段最简策略，但代价是这两个字段无法在不 bump `FormatVersion` 的前提下承载新语义（如压缩等级、dictionary id）。鉴于项目仍处早期、可自由升级 wire version，此选择可接受；但要自觉：未来任何对 reserved 字节的使用都等价于一次版本升级，而非平滑扩展。

### 4.1 长度不变量

- `PayloadLength` 是 logical length，等于 `ReadEvent(address).Payload.Length`，值域为 `0..uint.MaxValue`。
- stored length 等于完整 RBF frame payload 区域长度，由 checked read 时的 `frame.PayloadAndMeta.Length - frame.TailMetaLength` 派生，不写入 EventFrame header，避免双真源。
- identity codec 下 logical length 必须等于 stored length。
- 非 identity codec 下 stored length MAY 小于、等于或大于 logical length，但 writer 的自动压缩策略只应在有收益时采用压缩。
- v2 初始实现不允许压缩绕过单 Event logical 上限：`PayloadLength <= EventJournalOptions.MaxLogicalPayloadLength`，默认不超过 `RbfFile.MaxPayloadAndMetaLength`。当前同步 `ReadEvent.Payload` 暴露为 `ReadOnlySpan<byte>`，实现还必须拒绝 `PayloadLength > int.MaxValue`；默认上限事实上会被 RBF 单帧上限压在更小范围内。
- stored length + TailMetaLength 仍必须满足 RBF 单 frame 上限。
- 如果未来支持 multi-frame payload，logical length 超过 `uint.MaxValue` 的 payload 应进入 streaming/chunk manifest 设计，而不是复用当前同步 `ReadEvent.Payload` 模型。

### 4.2 CRC 与信任边界

- RBF `PayloadCrc32C` 覆盖 stored payload + TailMeta + padding。
- EventFrame header CRC 覆盖 header 自身，支持 L2 preview 更早发现 header 损坏。
- `ReadEventHeaderPreview` 只校验 TailMeta，不校验 stored payload，也不解压。
- `ReadEventHeaderChecked` 完整读取 RBF frame 并校验 stored payload CRC，但不必解压 logical payload。
- `ReadEvent` 在 checked read 之后执行 payload codec decode，并校验 decoded length 必须等于 `PayloadLength`。

- decode 必须以 header 声明的 `PayloadLength` 为**硬上界**（见 §7），而不是信任压缩流自描述的输出长度，以防解压炸弹。
- 压缩帧的逻辑正确性只能在 `ReadEvent` 解压时验证；checked read 不解压，因此压缩帧的 checked 校验强度天然弱于 identity（identity 可校验 stored == logical，压缩帧只能靠 RBF `PayloadCrc32C` 保证 stored 完整性）。这是固有且可接受的：parent walk / ref advance 只需 header + stored 完整性。

这一点非常重要：checked header 不能依赖 payload codec 支持情况。否则缺少某个 codec 的 reader 将无法做 parent walk、ref advance 校验或恢复工具扫描。

## 5. Payload Codec Registry

建议预留小型 registry，codec id 是 wire format，不直接等同于 .NET 类型名。

| CodecId | 名称 | 状态 | 说明 |
|-------:|:-----|:-----|:-----|
| 0 | identity | 必须支持 | 不转换 |
| 1 | zstd-frame | 预留/长期默认目标 | 标准 zstd frame，适合作为长期默认压缩格式；但 .NET 10 BCL 无内置 zstd，实现需先确认运行时依赖策略。本阶段仅固定 id，不实现。 |
| 2 | brotli | 推荐首个落地 | .NET BCL `System.IO.Compression.BrotliEncoder.TryCompress` / `BrotliDecoder.TryDecompress` 可用，零新增依赖、span-based 契合连续 payload；热写入等级不宜过高。 |
| 3 | zlib | 可选 | .NET BCL 可用，兼容性强；压缩率通常不如 zstd/brotli。 |
| 4..32767 | reserved | 保留 | Atelia 内置 codec |
| 32768..65535 | private/experimental | 实验 | 不应写入长期 journal，除非 manifest 固定说明 |

实现策略可以分两步：

1. wire format 先支持 metadata 与 identity codec，API 先稳定。
2. 首个真实压缩 codec 落地 `brotli`（id = 2）：它在 .NET 10 BCL 中即有 span-based 的 `BrotliEncoder.TryCompress` / `BrotliDecoder.TryDecompress`，无需 native 依赖即可打通完整压缩链路与验收测试。`zstd-frame`（id = 1）保留为长期默认目标，待依赖策略确认后再实现。无论选择哪一个，codec id 必须固定，不能因实现库更换而变。

热压缩路径应使用一次性 span API（`BrotliEncoder.TryCompress`），不要用 `BrotliStream`，以避免流对象与中间缓冲的额外分配。

## 6. 写入策略 API

推荐提供 journal-wide default policy，再允许单次 append override。

候选 API：

```csharp
public sealed class EventJournalOptions {
    public EventPayloadCodecPolicy PayloadCodecPolicy { get; init; } = EventPayloadCodecPolicy.Identity;
}

public readonly record struct EventPayloadCodecPolicy(
    EventPayloadCodecId PreferredCodec,
    int MinimumPayloadLength = 2048,
    int MinimumSavingsBytes = 256,
    double MinimumSavingsRatio = 0.05,
    EventPayloadCodecFallback Fallback = EventPayloadCodecFallback.StoreIdentity
);

public enum EventPayloadCodecFallback {
    // 压缩失败或无收益时回落为 identity 存储（默认，推荐）。
    StoreIdentity = 0,
    // 压缩失败时让 AppendEventFrame 直接返回写入错误，不静默降级。
    FailWrite = 1,
}

public readonly record struct EventPayloadWriteOptions(
    EventPayloadCodecPolicy? PayloadCodecPolicy = null
);

public AteliaResult<EventAddress> AppendEventFrame(
    EventAddress? parent,
    ReadOnlySpan<byte> payload,
    uint opaqueEventKind = 0,
    AddressHint hint = default,
    long? utcUnixTimeMilliseconds = null,
    EventPayloadWriteOptions? writeOptions = null
);
```

`CommitToRef` 应透传同样的 `EventPayloadWriteOptions?`，或使用 journal-wide default。

### 6.1 自动压缩规则

writer 流程：

1. 先校验 logical payload 长度。
2. 若 policy 为 identity 或 payload 小于阈值，直接 identity。
3. 使用 preferred codec 压缩到临时 buffer。
4. 若压缩失败，按 fallback 决定返回错误或 identity。
5. 若 compressed length 没达到 `MinimumSavingsBytes` / `MinimumSavingsRatio`，写 identity。
6. 构造 v2 header，`PayloadLength = logical length`，`PayloadCodecId = actual codec id`。
7. RBF `Append(EventFrameTag, storedPayload, tailMeta)`。

自动策略必须记录“实际采用的 codec”，而不是记录“曾经尝试的 codec”。

> **开销说明**：identity 路径不变，仍直接 `Append(tag, logicalPayload, tailMeta)`，零额外拷贝。压缩路径会先把 logical payload 压缩到一块临时 pooled buffer（`ArrayPool<byte>`），再作为 stored payload 交给 RBF，因此每次压缩写入多一次租用 + 拷贝。这是压缩的固有成本，不是零拷贝。

## 7. 读取 API 与内存模型

当前 `EventFrame` 直接持有 `RbfPooledFrame`，`Payload` span 指向 RBF pooled buffer。v2 后应允许两种 payload lease：

```text
identity:
  EventFrame owns RbfPooledFrame
  Payload span = stored payload slice

compressed:
  EventFrame owns RbfPooledFrame + decoded byte[]
  Payload span = decoded byte[]
```

`EventFrame.Dispose()` 必须同时释放 RBF pooled frame 与 decoded buffer。如果 decoded buffer 来自 `ArrayPool<byte>`，必须归还；如果是普通 `byte[]`，只需丢弃引用。

decode 的缓冲分配与校验必须以 header 声明的 `PayloadLength` 为**硬上界**，而不是信任压缩流自描述的输出长度：

- decoded buffer 按 `header.PayloadLength` 精确租用/分配；若 `PayloadLength > int.MaxValue`，当前同步 `ReadEvent` 直接拒绝；decoder 必须拒绝产出超过该长度的字节（zstd/brotli 的解压 API 都支持限定输出上界），以防解压炸弹（zip bomb）。
- 解压完成后仍校验 decoded length 必须**恰好等于** `PayloadLength`，否则返回 `PayloadLengthMismatch`。
- `EventFrame` 需额外持有 logical length。`Payload` getter 在压缩路径下返回 `decoded[..(int)Header.PayloadLength]`；identity 路径仍沿用 `PayloadAndMeta[..^TailMetaLength]`。

为了诊断与工具，建议额外提供 raw/stored 读取入口，但不作为主业务 API：

```csharp
public AteliaResult<EventStoredFrame> ReadStoredEvent(EventAddress address);
```

`ReadStoredEvent` 返回 header + stored payload，用于 debug dump、压缩率统计、离线修复和 codec 迁移工具。普通上层只用 `ReadEvent`。

## 8. Hash 与 Canonical 语义

SessionJournal 和未来 request manifest 应把 logical payload bytes 视为 canonical event payload：

- 领域 hash / request manifest hash：hash logical payload bytes。
- EventAddress：仍是物理写入位置，可能因压缩策略、segment 状态不同而不同。
- RBF CRC：校验 stored bytes 与 TailMeta。
- 可选未来 payload digest：若加入，应默认 digest logical payload，除非字段名明确写 `StoredPayloadDigest`。

压缩等级改变不应改变事件事实语义；它只改变 stored payload bytes 和 EventAddress 物理长度。

## 9. 错误语义

建议新增错误：

| ErrorCode | 场景 |
|:----------|:-----|
| `PayloadCodecUnsupported` | `ReadEvent` 遇到当前实现不支持的 codec。`ReadEventHeaderPreview/Checked` 不应因此失败。 |
| `PayloadCodecDecodeFailed` | codec 解码失败、压缩流损坏或实现报告错误。 |
| `PayloadLengthMismatch` | identity codec 下 stored length 与 header logical length 不一致，或非 identity codec decoded logical length 与 header `PayloadLength` 不一致。 |
| `PayloadCodecPolicyInvalid` | 写入策略非法，如阈值为负数或 unknown preferred codec。 |
| `PayloadLogicalLengthExceeded` | logical payload 超过 EventJournal 配置上限。 |

## 10. 与现有模块的影响

### EventJournal

- `EventFrameHeader` 增加 `PayloadCodecId`，`PayloadLength` 改为 logical `uint`。
- `EventFrameHeaderCodec.FixedLength` 保持 64，`FormatVersion` 从 1 改为 2。
- `ReadEventHeaderChecked` 从 `ReadEvent` 解耦：完整校验 stored frame，但不解压 payload。
- `ReadEvent` 在 checked read 后透明 decode logical payload。
- `AppendEventFrame` / `CommitToRef` 接入 policy。
- open 时的 tail 扫描（`ComputeNextSequenceNumber` 等）与 recovery 路径必须保持 codec-agnostic：它们只 `ScanForward` + decode header TailMeta 读取 `SequenceNumber` 等字段，绝不读取或解压 stored payload，因此天然不依赖任何 codec 支持。实现时不要误把这些路径接入 payload decode。

### ForwardPlan

ForwardPlan 只依赖 `ReadEventHeaderPreview` / `ReadEventHeaderChecked` 和 Parent，不需要解压 payload。v2 后 checked replay 仍可保持“校验 stored frame 而不解压”的成本边界。

### SessionJournal

SessionJournal 无需感知 codec。它仍然把 canonical JSON bytes 传给 EventJournal，并从 `ReadEvent` 得到同一 logical bytes。

需要更新文档措辞：SessionJournal 的 `payload = { "v", "body" }` 指的是 EventJournal logical payload，不承诺 RBF stored payload 一定就是这段 JSON。

### Ref store / ref-op-log

不建议对 ref-op-log 或 ref object move chain 启用 payload codec。它们是小型控制记录，压缩收益低，会增加恢复路径复杂度。Payload codec 只作用于 canonical EventFrame store。

## 11. 验收测试建议

1. identity event round-trip：header `PayloadLength == ReadEvent.Payload.Length`，codec id = 0。
2. compressed event round-trip：`ReadEvent.Payload` 等于原始 logical bytes；stored length 与 logical length 可不同。
3. small payload auto fallback：即使 preferred codec 开启，小 payload 仍 identity。
4. incompressible payload fallback：压缩无收益时 identity。
5. `ReadEventHeaderPreview` 读取 compressed event，不读 payload也成功。
6. `ReadEventHeaderChecked` 读取 compressed event，只校验 stored frame，不需要解压。
7. unsupported codec fixture：preview/checked header 成功，`ReadEvent` 返回 `PayloadCodecUnsupported`。
8. identity stored/logical length mismatch fixture：checked header/read event 返回 `PayloadLengthMismatch`。
9. decoded length mismatch fixture：`ReadEvent` 返回 `PayloadLengthMismatch`。
10. reserved 字段非 0 fixture：preview/checked header 均拒绝。
11. half-empty parent fixture：preview/checked header 均拒绝。
12. SessionJournal reopen：开启压缩策略后，reducer replay 与未压缩完全等价。

## 12. 推荐实施顺序

1. **wire v2 + identity codec**：先改 header shape、长度字段、checked header/read event 解耦，所有现有测试迁到 v2。
2. **policy API 骨架**：加入 `EventPayloadCodecPolicy`，但只支持 identity，验证 API 形状。
3. **一个真实 codec**：首选 `brotli`（id = 2）做垂直切片——它在 .NET 10 BCL 中零依赖可用，能立即打通完整压缩链路与验收测试；`zstd-frame`（id = 1）待依赖策略确认后再补。codec id 必须按 registry 固定。
4. **SessionJournal 压缩 smoke**：用大段中文 observation/action/tool result 验证 reopen/replay 等价。
5. **统计与调试工具**：增加 raw/stored event dump，输出 logical/stored length、codec id、compression ratio。

### 12.1 v1 → v2 header 迁移方式

因为 EventJournal / SessionJournal 均未实际投入使用，磁盘上不存在任何 v1 数据，也没有下游 reader。因此**直接把 v1 header 实现原地改写为 v2 规格，不保留任何 v1 reader / 双版本分支**，这与本项目「及时重构优于兼容层」的约定一致。

具体做法：

- `EventFrameHeaderCodec`：`FormatVersion` 常量 `1 → 2`；删除 `HasParentFlag` / `KnownFlags` 及其与 parent presence 的交叉校验分支；`Encode` / `Decode` 直接按 v2 offset 表读写 `PayloadCodecId` + `Reserved0` + `Reserved1`。不新增 `DecodeV1`，遇到 `version != 2` 一律返回 `HeaderVersionUnsupported`。
- `EventFrameHeader` record：`PayloadLength` 由 `ulong` 改为 `uint`，新增 `PayloadCodecId` 字段（`ushort` 或强类型 `EventPayloadCodecId`）。
- 单元测试：现有断言（如 `EventJournalTests` 的 header round-trip、`PayloadLength: 123`、`ReadEventHeaderChecked` 等）直接迁到 v2 shape，而不是新增一套 v2 测试与旧测试并存；再补「reserved 非 0 拒绝」「codec id round-trip」两类断言。

**不推荐**先并行实现 v2、再回头删 v1 的两步法：那会在没有任何兼容收益的前提下，短暂制造「双 header reader」这一双真源，增加协调成本，且与本项目约定冲突。原地改写是单一真源、blast radius 更小的路径。

## 13. 残余风险

- .NET 10 当前 BCL 压缩 API 有 Brotli/ZLib/GZip，但 zstd 支持需要确认依赖策略。wire format 可以先预留 zstd id，不必让 API 绑定具体实现库。
- `ReadEvent` 返回 `ReadOnlySpan<byte>` 的同步一次性模型适合当前实现；未来如果 payload 很大，需要 streaming decode API。
- 压缩会让 EventAddress 物理长度依赖策略。同一 logical payload 在不同策略下地址不同，这是 append-only store 的正常属性，不应用 EventAddress 表示内容身份。
- 如果同时使用 FS 压缩和 EventJournal 压缩，CPU 成本可能叠加；默认 policy 应保守，优先按真实 payload benchmark 调整。
