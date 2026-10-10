# VersionStore：Binary RootMap payload 专项

日期：2026-10-10。状态：**Draft / key 的 Unicode 与 JSON 映射合同已由用户采用；RootMap 为现行首版 payload。VersionStore 尚未实施；本文独立 codec 职责，不宣称通用 opaque-payload API 或 CU 已并入主线**。

本文内聚 RootMap 的 key、wire 与容量合同，供 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[tag 专项](09-versionstore-immutable-tags.md)及 [CU 候选](07-versionstore-cross-ref-conditional-update.md)复用。RootMap 是完整 string→FrameAddress 字典；自有集合、RefSnapshot 和内容比较仍由 S4 定义，FrameAddress codec 由 S2 定义。

## JSON 名称映射

key 原样映射为 JSON 对象属性名：空串合法，Ordinal 区分大小写，不做 Unicode normalize。JSON 导入须先解码转义再拒绝重复 key；例如 "a" 与 "\u0061" 是同一个名称。引号、反斜杠和控制字符由 JSON 工具转义，不能因此拒绝、改名或截断 key。JSON 对象的成员顺序不成为 RootMap 内容语义。

该合同只规定名称的无损映射，不提供 JSON codec、JSON 文本规范化、地址的 JSON 表示或整个快照的导入导出 API。有效 Unicode 检查属于 RootMap；底层 BPV1 的任意 CLR code-unit 字符串合同不变，TagName 的独立接受域也不变。依据见 [RFC 8259 §4、§7、§8](https://www.rfc-editor.org/rfc/rfc8259.html#section-8.2)。

## Binary codec 与容量

### spec [F-VS-ROOTMAP-BPV1] 字典复用无损基元，一个 codeword 容量上限

VersionStoreFormatVersion=1 MUST 选择 [BPV1](../Binary/bare-primitive-value.md) 的普通 VarUInt32/string 规则，内部 RootMap codec 使用现成 Atelia.Binary，不新增公开 codec、类型 tag、nullable/control、压缩或另一套字符串算法。RootMap codeword 仅包含下列字段；宿主版本/kind 与完整 RBF CRC 不在每份字典中重复：

| 顺序 | 字段 | 编码 |
| --- | --- | --- |
| 1 | 实际 entry count | BPV1 VarUInt32 |
| 2，重复 count 次 | 非 null key | BPV1 ordinary BareString，原样保留 UTF-16 code units |
| 3，每个 key 后 | FrameAddress | S2 `[F-FS-FRAME-ADDRESS-VARINT]`：VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())，无符号，合法地址 3..15B |

Key MUST 为非 null 的有效 Unicode 字符串：UTF-16 中每个 high surrogate 必须紧邻一个 low surrogate，每个 low surrogate 必须属于这样的配对；输入及持久解码均拒绝未配对 surrogate，不 replacement。empty、中文、emoji、空白、NUL、控制符、路径分隔符及 U+FEFF 仍合法，长度受下述 codeword 预算限制；不剔除 BOM、不 trim、不归一化或大小写折叠，按 Ordinal 比较。这使 key 能无损映射成 JSON 对象属性名；JSON 转义只是文本表示，不进入 key 值或持久编码。这是应用 key，不继承 tag/path 政策。writer 使用 PrepareString 的最短无损选择及 FrameAddress.WriteVarInt 的最短地址表示；reader 接受 BPV1 指定的另一种合法字符串编码、字符串/count 有界冗余前缀及 S2 允许的冗余地址表示，非法 UTF8 明确拒绝而非 replacement。条目无排序要求，reader 按解码后的 Ordinal key 拒重；同 key 不同字符串表示也重复。wire bytes/枚举次序不替代 `[A-VS-ROOTS-OWNED]` 的内容比较，也不承诺同字典产生同 bytes。

首版完整 RootMap codeword MUST 不超过 **1,048,576B（1MiB）**，包括 count、全部实际 string header/payload 与地址；读写使用同一固定上限，不增加独立 key/count 配额或配置。默认输出精确尺寸为 `M = MeasureVarUInt32(n) + Σ(StringEncodingPlan.EncodedLength + address.MeasureVarInt())`，逐项度量地址并用 checked long 累积，不能用 FrameAddress.EncodedSize 或固定变长宽度代替；捕获过程中按实际条目检查增长，不信任源 Count，超限不得先冻结/物化超限完整输出。过长 key 可用 code-unit 数的必然尺寸下界先拒绝，再用现成 plan 确认精确尺寸。全部确定拒绝在 data barrier/发布输出前、NotAttempted 且无新增 fault；不得截断 key、拆 map 或丢弃条目来适配。

内部 decoder MUST 在局部 cursor 上组合读取。count 前缀读完后，在集合容量分配前要求 `4L × count <= min(实际剩余输入, RootMap 剩余预算)`；每项至少 1B empty string codeword 加 3B 地址。字符串分配前还须检查其**实际** header 长度与声明 payload，同时给本项地址保留至少 3B、给后续每项保留至少 4B；若尚余 n 项，必需 suffix 为 `3L + 4L × n`，不是地址实际长度的预测。可窥读至多 5B 的 BPV string header，或先截出扣除必需 suffix 的借入窗口交给现成 reader，不新增 public API。检查 int 范围、实际剩余和预算后才物化 key；地址在本次剩余输入/预算内调用 S2 ReadVarInt，消费长度可为 3..15B，包括合法冗余表示，失败时不提交消费，且不得改为 default 继续。全部条目/拒重/冻结成功才提交本次 RootMap 消费；损坏、超限及资源异常沿宿主现有错误通道传播，不交付部分字典。

容量及消费 MUST 按 reader 的真实 ConsumedCount 计，包括合法冗余 count/string header 与地址字段，不能用 Measure(decoded) 重新推算已消费长度。RootMap 是可组合的 prefix，不增加 map byte-length：宿主在自己的局部 cursor 上继续解析其余字段，整条 payload 完全消费及所需 CRC/kind/身份资格通过后才交付公开结果。Snapshot、tag、后续 CU 候选复用这份 codeword；当前 Snapshot body 若只含 RootMap 就必须 exact-consume，其他宿主由自己的 schema 确定前后字段，不忽略 trailing。

1MiB 不是完整 tag/CU payload、物理 FrameLength 或文件容量上限。宿主 MUST 将自己的前后字段、RootMap 与 TailMeta 按 checked 尺寸组合，转换到 int 前验范围，再用公共 MeasureWriteSize 校验完整帧，追加起点另验；完整读取前按 RbfFrameInfo 检查该宿主上限，不先租超限全帧再拒绝。ref header/普通 Snapshot 消费 `[F-VS-REF-FRAMES]`；tag 名称与完整记录容量见 [S5-B 专项](09-versionstore-immutable-tags.md)，CU Members 的组合上限仍由其候选定稿，不把本条解释为所有宿主已经成立。

（Informative）1MiB 是限制单次完整快照工作规模的 MVP 工程选择，会拒绝仍可由 RBF 表示的更大 map；仅继承 RBF 硬界也可正确。没有现成大 map 消费者或峰值/吞吐测量证明这个数字最优、足够所有业务或不会 OOM；捕获、冻结、编码及读取仍有额外内存。实际需求超过此界或资源成本不合目标时再评估，不能在已发布版本中默改 accepted set。复用 Binary 减少自写规则，实际增加 Binary 与其精确 K4os `[1.3.8]` 依赖；固定逐 code-unit UTF16LE 也可正确，不声称此方案最快。BPV1 [已实施验收](../Binary/bare-primitive-value-acceptance.md)及[基元独立 golden](../../tests/Binary.Tests/SchemaCompositionTests.cs)是底座证据；[RootMapCodecProbe](../../experiments/RootMapCodecProbe/README.md)原 fixed12 synthetic 地址版本通过 47 项组合检查，含当时的 1MiB 边界、宽容/重复 key、截短与 12/13B suffix guard。该结果保留历史身份，不构成本次变长 RootMap、尚未实施的新库、发布/恢复、平台或包资格；本次 FrameAddress 新增能力的独立源码验证另见[变长地址实施记录](02-framestore-varint-address-implementation.md)。

## 不透明发布层的可行性与边界（Informative）

发布机制可以在内部只处理已捕获、自有且长度确定的 payload bytes：ref 发现、帧成员资格、历史位置、CRC、时间、data-first、Outcome 与 CU Members/self/完整组判据不需要解释 key 的业务含义。RootMap 编解码与字典自有值可作为内部流程的首个适配层，无需先建立公开 codec registry、泛型框架或多个包。

当前合同仍要求 RootMap 结构校验；“不解释业务”不等于“完全不解码”。若后续正式采用可替换 payload，须同步明确以下接缝：

- CU 与 tag 的边界必须由发布 envelope 定位，不能依赖 RootMap prefix decoder 才找到 Members 或 payload 末端。CU 可以先编码有界 Members，再将余下 bytes 作为 payload；这是方案建议，尚未替换候选格式。
- core 的事务完整只证明 envelope/CRC/全部成员匹配。RootMap 适配层须保留当前合同要求完整读取的全部 peer payload 校验；codec 错误必须报错，不能变为“不完整”而回退旧值。只解码本地成员会改变现行错误接受政策。
- opaque fork 可逐 byte 复制 exact 源 payload；现行 RootMap 的来源一致性仍按字典值比较，合法的不同条目顺序或 BPV 表示不能因 bytes 不同被判为业务不一致。字节相等足以证明相同内容，但不是 RootMap 内容相等的唯一判据。
- payload/schema 的选择应先绑定固定的库级格式合同；不要求首版支持同库每帧混合 codec。未来允许混合时才另定识别信息，不能靠试解码猜格式。
- payload 捕获/编码和确定容量拒绝先于 data barrier，读取交付自有 bytes/值，底层 pooled 资源不外泄。保留绑定 data 与应用依赖闭包责任；不透明化不增加库自动验证 data 图的能力。

因此，文档拆分和内部 byte-oriented 实现边界可立即采用；任意 payload 的公开入口与泛化后的持久 envelope 是另一步可观察合同调整。当前 ref/tag/CU 的主线或候选资格保持原身份，本文不以分层建议替换它们。

## 实施验证补充

输入和磁盘解码均覆盖孤立 high/low surrogate、连续 high surrogate、字符串末尾截断 pair 的拒绝，以及合法 surrogate pair/emoji 的保留；空串、NUL、控制符、分隔符、U+FEFF、组合/预组合字符原样往返，后两者仍是不同 Ordinal key。合法 BPV UTF-16LE 但非有效 Unicode 的 key 在 RootMap 层拒绝，Binary reader 自身不收紧。JSON 转义解码后的重复 key 与有效 Unicode 映射由对应工具验收。

既有 RootMap 探针曾接受未配对 surrogate，其结果保留历史身份；本次只调整设计接受域，没有重写探针或声称现行探针已验证新合同。
