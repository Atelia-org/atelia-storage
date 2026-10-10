# VersionStore：Binary RootMap payload 专项

日期：2026-10-10。状态：**Draft / key 的 Unicode 与 JSON 映射合同已由用户采用；RootMap 为首个可选应用 payload codec。VersionStore 尚未实施；bytes 公面已采用 11 专项；CU 尚未并入主线**。

本文内聚 RootMap 的 key、wire 与容量合同，供选择该 codec 的应用显式使用；[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[tag 专项](09-versionstore-immutable-tags.md)及 [CU 候选](07-versionstore-cross-ref-conditional-update.md)只保存 opaque bytes。RootMap 是完整 string→FrameAddress 字典；RootMap 自有集合定义于本篇；bytes 自有结果由 11 公面专项定义；fork 来源一致性由 S5 `[A-VS-REF-HISTORY-CHECKED]` 定义，不消费字典内容比较，FrameAddress codec 由 S2 定义。

## JSON 名称映射

key 原样映射为 JSON 对象属性名：空串合法，Ordinal 区分大小写，不做 Unicode normalize。JSON 导入须先解码转义再拒绝重复 key；例如 "a" 与 "\u0061" 是同一个名称。引号、反斜杠和控制字符由 JSON 工具转义，不能因此拒绝、改名或截断 key。JSON 对象的成员顺序不成为 RootMap 内容语义。

该合同只规定名称的无损映射，不提供 JSON codec、JSON 文本规范化、地址的 JSON 表示或整个快照的导入导出 API。有效 Unicode 检查属于 RootMap；底层 BPV1 的任意 CLR code-unit 字符串合同不变，TagName 的独立接受域也不变。依据见 [RFC 8259 §4、§7、§8](https://www.rfc-editor.org/rfc/rfc8259.html#section-8.2)。

## 应用 codec 公面与自有字典

RootMapCodec 提供两个无 owner、无 I/O 的公开静态入口；这是待实施 API：

- Encode(IReadOnlyDictionary<string, FrameAddress> roots) → AteliaResult<byte[]>：生成调用方拥有的精确长度数组，可作为 bytes 发布输入。
- Decode(ReadOnlySpan<byte> payload) → AteliaResult<IReadOnlyDictionary<string, FrameAddress>>：要求整个输入 exact-consume，返回自有 Ordinal 冻结字典。

不提供 RootMap 专用 ref/tag 重载、泛型 owner 或 codec registry。应用先 Encode，再将 bytes 交 VersionStore；读取后自行 Decode。选择 JSON 的应用不调用此 codec。RootMap 的 Count、Keys/Values 与内容比较均由应用消费；它不签发 revision、验证 data 闭包或改写发布状态。

### spec [A-VS-ROOTS-OWNED] 编码捕获私有内容，解码结果冻结

RootMap codec 的输入与解码结果使用 `IReadOnlyDictionary<string, FrameAddress>`，不新增 public RootMap、集合 Builder 或比较器类型。非 null 空字典合法；null 字典、null key、实际重复的 Ordinal key 和 default FrameAddress 为确定输入拒绝；key/wire/容量消费 `[F-VS-ROOTMAP-BPV1]`。接口自身不证明所有权、不可变性或比较政策，不能直接保存调用方集合或只读包装。

外部输入 MUST 逐项捕获到私有 `Dictionary<string, FrameAddress>(StringComparer.Ordinal)`：检查实际枚举的 key/地址与既定容量，通过 TryAdd 拒重，不继承输入 comparer，不以输入 Count 或 TryGetValue 决定正确性或分配规模；捕获与枚举器必要清理成功后，直接从私有字典度量并编码精确数组，无需冻结编码中间集合。应用在调用 bytes 发布 API 前处理编码失败。不得直接用 ToFrozenDictionary 代替拒重步骤，其重复键行为是覆盖而非拒绝。Decode 同样先按 codec 拒绝重复/非法条目，最后交付自有冻结内容，不保存 pooled bytes 或复用下一帧的可变 scratch 字典。

调用方 MUST 在整个同步 Encode 调用期间保持输入集合稳定。自定义接口与枚举器可执行调用方代码，捕获与必要清理失败不交付结果；codec 没有 owner、HistoryActive、PublicationOutcome 或 I/O 职责，包含它的应用流程自行遵守所属 owner 的串行合同。Decode 同步期间的输入 bytes 也必须有效且稳定。

返回的 Roots MUST 持有上述自有冻结内容，通过正常集合接口不能改写；map、Keys、Values 不得暴露可变 backing 集合。string 与 FrameAddress 自身不可变，浅捕获足够；不复制或预读地址背后的业务对象。Decode 的结果与输入 span 独立，调用结束后仍有效，不需要 Dispose；合法空字典也可共享。Encode 返回的精确 byte[] 归调用方所有，可修改或复用。

## Binary codec 与容量

### spec [F-VS-ROOTMAP-BPV1] 字典复用无损基元，一个 codeword 容量上限

本篇固定 RootMapCodec 的 codeword 使用 [BPV1](../Binary/bare-primitive-value.md) 的普通 VarUInt32/string 规则，RootMapCodec 使用现成 Atelia.Binary，作为应用可显式调用的独立静态 codec，不挂入发布 owner；不新增类型 tag、nullable/control、压缩或另一套字符串算法。RootMap codeword 仅包含下列字段，不重复发布 envelope/kind/CRC；VersionStoreFormatVersion 不选择应用内容 codec，应用负责选择和记录内容 schema：

| 顺序 | 字段 | 编码 |
| --- | --- | --- |
| 1 | 实际 entry count | BPV1 VarUInt32 |
| 2，重复 count 次 | 非 null key | BPV1 ordinary BareString，原样保留 UTF-16 code units |
| 3，每个 key 后 | FrameAddress | S2 `[F-FS-FRAME-ADDRESS-VARINT]`：VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())，无符号，合法地址 3..15B |

Key MUST 为非 null 的有效 Unicode 字符串：UTF-16 中每个 high surrogate 必须紧邻一个 low surrogate，每个 low surrogate 必须属于这样的配对；输入及持久解码均拒绝未配对 surrogate，不 replacement。empty、中文、emoji、空白、NUL、控制符、路径分隔符及 U+FEFF 仍合法，长度受下述 codeword 预算限制；不剔除 BOM、不 trim、不归一化或大小写折叠，按 Ordinal 比较。这使 key 能无损映射成 JSON 对象属性名；JSON 转义只是文本表示，不进入 key 值或持久编码。这是应用 key，不继承 tag/path 政策。writer 使用 PrepareString 的最短无损选择及 FrameAddress.WriteVarInt 的最短地址表示；reader 接受 BPV1 指定的另一种合法字符串编码、字符串/count 有界冗余前缀及 S2 允许的冗余地址表示，非法 UTF8 明确拒绝而非 replacement。条目无排序要求，reader 按解码后的 Ordinal key 拒重；同 key 不同字符串表示也重复。wire bytes/枚举次序不定义字典语义等值；fork 原样 clone 与来源比较另按 S5 合同，也不承诺同字典产生同 bytes。

首版完整 RootMap codeword MUST 不超过 **1,048,576B（1MiB）**，包括 count、全部实际 string header/payload 与地址；读写使用同一固定上限，不增加独立 key/count 配额或配置。默认输出精确尺寸为 `M = MeasureVarUInt32(n) + Σ(StringEncodingPlan.EncodedLength + address.MeasureVarInt())`，逐项度量地址并用 checked long 累积，不能用 FrameAddress.EncodedSize 或固定变长宽度代替；捕获过程中按实际条目检查增长，不信任源 Count，超限不得先物化超限完整输出。过长 key 可用 code-unit 数的必然尺寸下界先拒绝，再用现成 plan 确认精确尺寸。Encode 确定拒绝不交付数组；不得截断 key、拆 map 或丢弃条目来适配。

内部 decoder MUST 在局部 cursor 上组合读取。count 前缀读完后，在集合容量分配前要求 `4L × count <= min(实际剩余输入, RootMap 剩余预算)`；每项至少 1B empty string codeword 加 3B 地址。字符串分配前还须检查其**实际** header 长度与声明 payload，同时给本项地址保留至少 3B、给后续每项保留至少 4B；若尚余 n 项，必需 suffix 为 `3L + 4L × n`，不是地址实际长度的预测。可窥读至多 5B 的 BPV string header，或先截出扣除必需 suffix 的借入窗口交给现成 reader，不新增 public API。检查 int 范围、实际剩余和预算后才物化 key；地址在本次剩余输入/预算内调用 S2 ReadVarInt，消费长度可为 3..15B，包括合法冗余表示，失败时不提交消费，且不得改为 default 继续。全部条目、拒重、exact-consume 与冻结成功才交付结果；格式/输入拒绝返回失败 Result，分配/自定义输入等异常传播，不交付部分数组或字典，也不修改发布状态。

Decode 入口先检查实际输入为1..1MiB，容量及消费 MUST 按 reader 的真实 ConsumedCount 计，包括合法冗余 count/string header 与地址字段，不能用 Measure(decoded) 重新推算。公开 Decode 只接收完整 RootMap codeword，并要求 exact-consume；尾随 bytes 返回失败，不公开 prefix/ConsumedCount API。内部局部 cursor 只是组合字段的实现，不承担 Snapshot/tag/CU 定位。

此1MiB codeword 界与 bytes API 的单项界一致；codec 不负责物理帧、追加起点或文件容量。应用将输出交发布层后，由 S4、tag 或 CU 按自己的 envelope 组合和公共 MeasureWriteSize 检查完整容量。

（Informative）1MiB 是限制单项 root payload 的 MVP 工程选择，不保证合法输入不会 OOM；codec 捕获、编码、冻结解码与发布层复制仍有额外内存。实际工作集与峰值/吞吐留实施测量，不声称数字最优或方案最快。复用 Binary 增加其精确 K4os `[1.3.8]` 依赖。BPV1 [已实施验收](../Binary/bare-primitive-value-acceptance.md)及[基元独立 golden](../../tests/Binary.Tests/SchemaCompositionTests.cs)是底座证据；[RootMapCodecProbe](../../experiments/RootMapCodecProbe/README.md)原 fixed12 synthetic 地址版本的47项检查保留历史身份，不构成本次变长地址/Unicode接受域、独立公共codec、新库或发布恢复资格。新增 FrameAddress 能力见[实施记录](02-framestore-varint-address-implementation.md)。

## 与发布层的边界

本codec只解释应用显式交付的完整codeword，不承担Snapshot/tag/CU定位、事务资格或fork来源比较。这些合同分别消费S4、S5及07候选；bytes公面消费[11专项](11-versionstore-root-payload-bytes-api.md)。调用链为Encode→发布bytes、读取自有bytes→Decode；fork原样复制bytes，内容schema由应用管理。

## 实施验证补充

独立 golden：empty=`00`；一项 A、FileId=1、ticket offset=4/length=28 的完整 codeword=`01 03 41 01 87 02`。覆盖真实变长地址3..15B、最短输出/有界冗余读取、截短/非法地址、count/string巨大声明的分配前界、实际消费及1MiB边界、不同表示的重复 key、`3 + 4 × 后续项数` suffix、exact-consume 拒绝尾随。Encode 捕获不信任 Count/comparer，源后改不影响结果；Decode 冻结 map/Keys/Values 不暴露可变 backing。

输入和磁盘解码均覆盖孤立 high/low surrogate、连续 high surrogate、字符串末尾截断 pair 的拒绝，以及合法 surrogate pair/emoji 的保留；空串、NUL、控制符、分隔符、U+FEFF、组合/预组合字符原样往返，后两者仍是不同 Ordinal key。合法 BPV UTF-16LE 但非有效 Unicode 的 key 在 RootMap 层拒绝，Binary reader 自身不收紧。JSON 转义解码后的重复 key 与有效 Unicode 映射由对应工具验收。

既有 RootMap 探针曾接受未配对 surrogate，其结果保留历史身份；本次只调整设计接受域，没有重写探针或声称现行探针已验证新合同。
