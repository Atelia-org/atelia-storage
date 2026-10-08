# Atelia.Binary

按已知 schema 使用的 .NET 10 二进制基元编解码库，依赖 BCL 和纯托管 K4os.Compression.LZ4 `[1.3.8]`。格式与 API 合同见 [Bare Primitive Value](../../docs/Binary/bare-primitive-value.md)。本库不定义记录版本、字典顺序、FrameAddress 身份、CRC 或耐久语义。

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

显式受控 string/bytes 使用 `00=null`、`01=完整原文 Bare 值`、`02=C、U、Brotli 流`、`03=C、U、LZ4 block`；C/U 是 VarUInt32，U 包括内层 header。压缩是显式选择，默认 `ValueCompression.None`。

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

Prepare 冻结自有快照，仅当完整压缩编码严格更小时采用指定方法；重复写计划不重新压缩。受控 bytes 的 null 使用 `ControlledValueEncodingPlan.Null`，empty span 表示非 null 空值。`ReadControlledBytes` 始终返回自有数组。reader 在分配前检查 C/U 限额，并要求一个完整流/块、精确消费与输出以及内层完全消费。

快速块压缩可选 `PrepareControlledString(text, ValueCompression.Lz4Block)` 或对应 bytes 入口；Write/Read API 相同，reader 自动按 control 分派。Lz4Block 是独立标准块，复用外层 C/U，不是 `.lz4` Frame 或 K4os Pickler 格式，不支持外部 dictionary。结构预检拒绝无效 offset 和末尾约束后由库解码；块本身没有内容校验和。LZ4 的完整内层准备上限为 0x7E000000 bytes，仍需为 owned 内存预算负责。

Prepare 会物化完整值并可能同时保留原文和压缩候选缓冲；分块输出不代表准备过程是流式或恒定内存。

压缩包装与算法实现已在内部拆开：所有已支持的压缩方法共享 C/U、精确尺寸和 typed 内层校验，算法自己的实现由 ControlledValueCodecs 显式分派。公开 Prepare/plan/Write/Read 签名与默认 None 保持稳定，当前支持 None/Brotli/Lz4Block。

TODO（.NET 11 GA）：升级主线目标框架后用 BCL 接入 Zstandard 与 RFC 1951 裸 Deflate；RFC 1950 的 ZLib 包装如需支持，使用独立方法编号。新增成员/control 须与严格单流、限额/窗口和独立 fixture 验收一起落地，详见[后续接入步骤](../../docs/Binary/bare-primitive-value.md#增量压缩方法接入todo-net-11-ga)。源码 XML doc 和 ControlledValueCodecs 注释也保留这些 TODO。Tagged、全套 nullable 标量和业务库迁移仍为独立后续工作。
