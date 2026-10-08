# Atelia.Binary

按已知 schema 使用的 .NET 10 二进制基元编解码库，仅依赖 BCL。格式与 API 合同见 [Bare Primitive Value](../../docs/Binary/bare-primitive-value.md)。本库不定义记录版本、字典顺序、FrameAddress 身份、CRC 或耐久语义。

`BareValueWriter` 借入 `IBufferWriter<byte>`；`BareValueReader` 借入 `ReadOnlySpan<byte>`。固定整数和浮点数显式使用 LE，变长整数使用有界 Base128/ZigZag。writer 写最短整数；reader 接受有界冗余表示。每个 Read 失败时 cursor 不推进，多个字段的组合回滚由调用方复制 reader 完成。

```csharp
var output = new ArrayBufferWriter<byte>();
var writer = new BareValueWriter(output);
var key = BareValueEncoding.PrepareString("世界");
long length = key.EncodedLength + 4 + 8;
writer.WriteString(key);
writer.WriteUInt32LE(fileId);
writer.WriteUInt64LE(packedSizedPtr);

var reader = new BareValueReader(output.WrittenSpan);
string restoredKey = reader.ReadString(maxPayloadByteCount: 1024);
uint restoredFileId = reader.ReadUInt32LE();
ulong restoredPtr = reader.ReadUInt64LE();
reader.EnsureFullyConsumed();
```

string 原样保留 UTF-16 code units，包括孤立 surrogate；默认选择较短的 UTF-8/UTF-16LE payload，持平选择 UTF-16LE。普通 bytes 带 VarUInt32 长度，raw bytes 不带头。普通 `ReadBytes` 返回借入 span，需要跨输入生命周期持有时自行复制。大输出分块索取 sink 缓冲，writer 不 Dispose/flush sink，也不保证写失败可撤销。

显式受控 string/bytes 使用 `00=null`、`01=完整原文 Bare 值`、`02=C、U、Brotli 流`；C/U 是 VarUInt32，U 包括内层 header。压缩是显式选择，默认 `ValueCompression.None`。

```csharp
var plan = BareValueEncoding.PrepareControlledString(text, ValueCompression.Brotli);
long budget = plan.EncodedLength;
var controlledOutput = new ArrayBufferWriter<byte>();
new BareValueWriter(controlledOutput).WritePreparedValue(plan);
var controlledReader = new BareValueReader(controlledOutput.WrittenSpan);
string? restored = controlledReader.ReadControlledString(
    maxStoredByteCount: 1_048_576, maxDecodedByteCount: 4_194_304);
controlledReader.EnsureFullyConsumed();
```

Prepare 冻结自有快照，仅当完整压缩编码严格更小时采用 Brotli；重复写计划不重新压缩。受控 bytes 的 null 使用 `ControlledValueEncodingPlan.Null`，empty span 表示非 null 空值。`ReadControlledBytes` 在 Raw/Brotli 两条路径都返回自有数组。reader 在分配前检查 C/U 限额，并要求一个完整 Brotli 流、精确消费与输出以及内层完全消费。

Prepare 会物化完整值并可能同时保留原文和压缩候选缓冲；分块输出不代表准备过程是流式或恒定内存。Tagged、Zlib、全套 nullable 标量和业务库迁移属于后续工作。
