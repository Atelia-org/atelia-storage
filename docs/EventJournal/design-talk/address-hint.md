这里有一个很重要的分界已经浮现：`FrameAddress` 在内存中确实会自然占 16 字节，但“padding 免费”不等于 wire format 也免费。若地址表原本可手工编码成 `4-byte segment + 8-byte ticket = 12 bytes`，持久化 32-bit hint 会把每项放大三分之一；所以应分别设计内存 ABI、持久格式和身份语义，不能仅凭 CLR padding 决定位布局。接下来我会核对三类文件系统的单文件/分配特性，并据此给出 segment 阈值的量化建议。

靠谱，而且我认为这比继续压缩 `SizedPtr` 的 bit 分配更有价值。但要把它定义成**目标绑定的 immutable hint**，不能变成任意可变的“用户备注”。

**推荐地址布局**
在常见 64-bit CLR ABI 下，可以直接填掉原本的 padding：

```csharp
public readonly record struct AddressHint(uint Packed);

public readonly record struct FrameAddress(
    uint SegmentNumber,
    AddressHint Hint,
    SizedPtr Ticket
);
```

即：

| 字段 | 位数 | 含义 |
|---|---:|---|
| `SizedPtr` | 64 | segment-local frame location |
| `SegmentNumber` | 32 | segment 路由 |
| `AddressHint` | 32 | smart-pointer hint |

无需缩减 `SegmentNumber`。即使每 64 GiB 轮转一次，32-bit 编号也能表达理论上的 256 EiB；每日轮转 30 年也只有约 10,958 个 segment。

我建议优先让 `EventAddress` 使用应用 hint，而普通 payload `FrameAddress` 初期保持 `Hint = 0` 或只使用存储层标记。event kind、时间桶等信息对 Event 更稳定，对 payload chunk 未必有意义。

**Hint 必须遵守的规则**
1. `Hint` 在 Event 创建时确定，此后不可改变。
2. `MetaFrame` 保存一份 `SelfHint`，解引用后校验地址和目标一致。
3. 未读取目标前，hint 只能用于 dispatch、过滤、预取和缓存选择。
4. 外部传入的地址必须视为未验证数据，不能仅凭 hint 决定安全敏感行为。
5. 同一物理位置只能有一个 canonical hint；否则 equality、hash、去重和 cycle detection 都会混乱。
6. 粗时间戳只能表示 time bucket，不能替代事件顺序或权威时间。系统时钟会跳变，Parent 链才是顺序依据。
7. EventJournal 只保存 opaque bits 和验证一致性，bit schema 由上层定义。可以在 store manifest 中记录 `HintSchemaId`，不必浪费每个 hint 的版本位。

一个初步可实验的 32-bit schema 可以是：

- 8 bits：event kind
- 8 bits：flags/color
- 16 bits：粗粒度时间桶或上层 subtype

但现在不宜锁死。先收集 ChatSession 真正需要免 I/O 判断的字段，再反推布局。

另一个关键点是：**内存 padding 免费，wire format 不免费**。原来的地址可紧凑编码成 12 bytes；加入完整 hint 后固定编码是 16 bytes。对于每个 Event 只有一个 Parent，这 4 bytes 基本可以忽略；对于包含大量 payload part address 的 MetaFrame，则会增长约三分之一。因此可以让：

- `EventAddress` wire format 固定 16 bytes。
- 无 hint 的 payload `FrameAddress` wire format保持 12 bytes。
- 两种类型使用独立 codec，不从 CLR layout 推导 wire format。
