---
title: "RBF 尾 Key 编码的 writer / reader 实现专项"
status: "Measured recommendation / production integration pending"
normative: false
---

# RBF 尾 Key 编码的 writer / reader 实现专项

日期：2026-10-02。生产源码基线：`5711c4706509eb3b144299a75f83c09aa75da75f`。本文补充[普通打开重构方案](rbf-open-fast-path-refactoring.md)的 G1，只研究整帧预处理、编码输出与读入解码。实验代码见 [RbfCodecCost](../../experiments/RbfCodecCost/README.md)。生产仍为 RBF1；本文没有分配格式版本、修改规范或实现生产 RBF2。

推荐最小实现是：**CRC/footer 完成后，定向检查 Key0、Key1，两者都禁用才建立完整 bitmap；Append 使用借用输入和有界输出缓冲，Builder 在提交前原地编码现有 owned chunks；reader 保留 encoded cache，在 caller/pooled buffer 向量解码，再用现有 CRC。** 模 uint32 加减法同样可行，当前测量未显示吞吐优势，XOR 的非对齐切片更直接，建议继续采用 XOR。融合解码/CRC 留作实验候选。

## 1. 需求与证据边界

| 要求 | 来源及本专项处理 |
| --- | --- |
| 优化 writer 整帧预处理、reader 解码，形成后续实施文档 | 本轮用户明确要求；允许专项实验、git 提交 |
| 真实读写使用 W: 数据 SSD | 用户指定；所有专项数据、日志与实际大帧在 W: |
| uint32 模加法作为候选 | 用户线索；验证存在性、wire、核函数与切片限制，不预先排除 |
| 任意 binary、约 256MiB 上限、完整读 CRC、逆序元信息不读头/payload | 已接受的 RBF 设计及现行代码；保留 |
| Open 结构 / ReadFrame 内容职责；单尾截断或补原 Key/Fence；进程终止 | 先前用户决定；本专项不改变恢复模型 |
| reservation/epoch、取消、pooled lifetime、write/flush fault | 现行接口、源码与测试；优化不能绕过 |
| 下游、离线救援、冷热 Open、异常扫描性能、断电及包发布 | 本专项之外；不以本轮结果声称完成 |

“最佳”指在当前约束下有测量支持、状态和内存较小的实施起点。只有一台机器和一种数据 SSD；内容分布不同，局部最优会改变，本文不定义跨平台性能保证。

## 2. 整帧依赖存在，但不需要再复制整帧

候选 FrameBytes 仍为 `HeadLen4 + encoded body + TailKey4`，后接 Fence4；body 包括 payload、meta、padding、PayloadCRC 和 Trailer16。每个 plaintext word w 排除 `Key=w XOR F`，m 个 words 在 `[0,m]` 至少留下一个 Key。

因此第一次发布之前必须知道完整 coverage、CRC 和 footer。只看 payload、不看最后 footer 就选 Key 不成立，例如合法业务 tag 本身也能排除候选。逐 Key 无界试扫会在低 Key 禁词位于帧末尾时变成二次工作量；本方案只定向检查两个固定候选，之后一次完整 bitmap。

但**依赖完整内容不等于拥有第二份完整内容**：

- [Append](../../src/Rbf/Internal/RbfAppendImpl.cs) 已收到完整 payload/meta spans。预处理直接读它们，不能原地改变用户数据；非零 Key 输出到有界 scratch。
- [Builder](../../src/Rbf/Internal/RbfFileImpl.cs) 立刻建立 HeadLen reservation。[SinkReservableWriter](../../src/Data/SinkReservableWriter.cs) 在最早 pending reservation 处停止 Push，当前 Builder 原本就保留全部 pooled chunks。
- Builder 的 `GetCrcSinceReservationEnd` 在 EndAppend 真正遍历这些 chunks，并非写入过程中自动积累 CRC。新格式的额外成本应与这条真实 baseline 比较。

实验的 `byte[][]` 是已冻结 chunk 视图。`Corpus.Split` 的复制、连续 Serialize destination 属于计时外 fixture，不是生产 Builder 接点，也不是格式必需的内存。

## 3. Key 选择：固定两条快路径与一个确定性 fallback

| 实验策略 | 工作 | 临时 bitmap 数据空间 |
| --- | --- | --- |
| FullBitmap | 扫完整 body，标记所有 `[0,m]` 禁 Key，取最小缺失 | `4*ceil((m+1)/32)`，最多 8MiB |
| SmallBitmap | 32B 标记 0..255；耗尽再 FullBitmap | 常见32B；fallback 加完整 bitmap |
| ZeroFirst | 向量检测 F；禁0后直接 FullBitmap | Key0 无 bitmap，其余完整 bitmap |
| **ZeroThenOne** | 检测 F；禁0后检测 `F XOR 1`；两者都禁再 FullBitmap | **Key0/1 无 bitmap**，其余完整 bitmap |

定向检测遍历连续 body words，包括完整 footer。跨 chunk 保留至多3B组 word；对齐中段用现有 span `Contains`。检测遇到对应 word 可以提前停止；后续候选重新从 body 起点检查。四策略选择相同最小 Key，wire 相同。ZeroThenOne 最多启动三次禁 Key 扫描，最坏仍为 O(L)，不使用试 Key 循环。

CRC 与筛 Key 使用不同 loop，未宣称一次 memory load 同时完成两者。保留生产 `RollingCrc`，不用自写 CRC SIMD。完整 bitmap 在生产可以租用并清理**实际 m 所需范围**，释放或复用；无需常驻最大 8MiB。实验 FullBitmap 每次 new，因而表中同时包含清零与分配成本，不能直接等同 pooled 实现。

本机 Prepare 中位时间；每项5样本，单位 µs，最后两行单位 ms：

| 输入，单 chunk | FullBitmap | SmallBitmap | ZeroFirst | ZeroThenOne |
| --- | ---: | ---: | ---: | ---: |
| 1MiB random，Key0 | 546.8 | 537.0 | 396.6 | **401.7** |
| 1MiB Fence 重复，Key1 | 936.6 | 926.9 | 970.4 | **411.8** |
| 1MiB 前端禁0..300，Key301 | 548.6 | 708.4 | 534.0 | **533.5** |
| 近256MiB random，Key0，ms | 229.6 | 144.2 | 112.2 | **112.5** |
| 近256MiB 末端禁0..300，Key301，ms | **143.8** | — | — | 174.6 |

最后一行是保留的反例：定向检测均扫到帧末，ZeroThenOne 比 FullBitmap 慢约21%。默认选择不是所有分布都最快；其价值是常见 Key0/1 不租 bitmap、避免反复更新同一 bitmap bit，且退化有界。没有证据支持内容分类器、根据大小自动调策略或外部配置。

原型 Key0/1 的 Prepare 分配约120B，来自实验 `PreparedFrame` 与 Footer 对象，不是格式要求。生产 Append 的小 footer 可使用已有局部/stack buffer，Builder 写进已有 chunks；不要把实验包装对象搬成每帧必需 heap 状态。

## 4. Append 输出与 Builder 的更小实现

### 4.1 Append：借用输入，按需租有界 scratch

先完成全部参数/offset 校验、CRC/footer 与 Key；随后按顺序输出 raw HeadLen、encoded body、raw Key/Fence。Key0 大帧可直接写原 spans，不需要编码 scratch。小帧合并头尾减少调用；非零 Key 大帧使用每 writer 按需取得、重复利用的有界输出空间。

本机建议先采用 **1MiB 非零 Key 输出 workspace**，不按帧 new，不让 Key0 大帧支付这个空间。依据是同一1MiB marker、64帧/64MiB batch、5次轮转样本：

| workspace | Append 中位 ms | 5次 Append 范围 ms | 每 batch Write calls | Append+durable 中位 ms |
| --- | ---: | ---: | ---: | ---: |
| 16KiB | 102.00 | 见原始 JSON | 4160 | 143.49 |
| 64KiB | 82.70 | 见原始 JSON | 1088 | 120.95 |
| 256KiB | 62.71 | 62.54–63.39 | 320 | 99.92 |
| **1MiB** | **58.07** | **56.32–61.54** | **128** | **99.68** |

1MiB 比256KiB的 Append 中位快约7.4%，durable batch 的分布重叠，不能说耐久吞吐更快。Pool 获取/归还在此实验未计时；这是后续生产接入的核验项。256KiB是4倍省空间的已测备选；目前不引入公开 buffer 配置或自适应控制器。

真实生产 RBF1 Append 同 workload 中位53.16ms、加durable为91.85ms；最优原型仍有写前预处理和变换成本。4KiB 原型较快包含“≤8KiB合为一次 Write”的输出拓扑变化，不能归因于新格式。更完整的吞吐、分布和调用数保留在原始证据中。

### 4.2 Builder：原地编码 owned pending chunks

**删除原方案的 Builder 专用 encoding sink。** 源码审查支持在现有 chunks 原地变换，再由原 sink 输出；不需要 Append 的1MiB scratch，也不复制整帧。

推荐顺序：

1. 检查 epoch、长度/meta、唯一且有效的 HeadLen reservation、无未 Advance 借用、`PushedLength==0`；所有可预见 Result 拒绝都在这里结束。
2. 补明文 padding，计算 coverage CRC，将完整 plaintext CRC/Trailer 追加并 Advance 到 pending chunks，暂不写 raw Key/Fence；选择 Key。
3. 同步遍历精确 body `[4,L-4)`，累计相位原地 XOR；排除 raw HeadLen、TailKey、Fence。
4. HeadLen 继续 pending 时追加 raw Key/Fence 并 Advance，再回填 HeadLen、调用原 `Commit(HeadLen)` 同步 Push；正常返回后推进 TailOffset，原 chunk 在 Push 返回后才回收。追加闭合字段的准备异常仍按未发布异常取消当前 Builder。

最窄 Data 接点是在具体 `SinkReservableWriter` 上对 reservation 末尾之后的**指定 byteCount**同步访问 pending spans。可使用标准 `SpanAction<byte,TArg>` 与每 writer context；只读扫描回调不修改字节，最终转换回调原地修改。接口不公开 ArrayPool 数组、chunk 列表或通用 codec。byteCount 前置校验，访问不 Push、不推进游标；累计状态不能依赖按值传递 struct 的修改回传。

[reservation 契约](../../src/Data/IReservableBufferWriter.cs) 只保持未 Commit reservation 的借用。此时唯一 pending 是 HeadLen，用户 payload reservation 已结束；旧普通 buffer 已 Advance。这使原地编码合法，但需要守住两条失败边界：

- callback 不能重入 GetSpan/GetMemory/Advance/Reserve/Commit/TryGetReservedSpan/Reset/Dispose 或再进 visitor。Commit 可提前发布未编码后缀，Reset/Dispose 可归还正在访问的数组。需要一次同步访问 guard，finally 解除。
- 从首次追加 padding/footer 起，任何准备或转换异常、尚未发布时，取消并 Reset 当前 Builder 后抛出，允许新 Builder；**不能保留当前 Builder 重试，也无需逆变换回滚**。已追加的 footer 会被重试误计入 payload，一部分已 XOR 的 chunks 再次 XOR 会变回 plaintext。输出开始后的异常仍永久 fault，取消不能解除。

[现行故障测试](../../tests/Rbf.Tests/Internal/RbfWriterFaultTests.cs) 明确允许 `EndAppend(-1)` 返回失败后同一个 Builder 修正再提交；优化必须保留这个行为。进程在原地编码中终止时还没有 Push，只损失未提交内存，无需新的恢复状态。

这是源码与契约审查结论；**生产 pending visitor、重入 guard、自动取消路径与新 Builder 端到端性能尚未实现或实测**。旧 Builder 的真实64KiB feeds 是参照，不能冒充新 Builder 验收。

## 5. reader：wire cache、owned buffer、批量解码

保持 [ReverseReadCache](../../src/Rbf/ReadCache/ReverseReadCache.cs) 的 encoded wire pages。只有复制/读取到 caller 或 pooled buffer 后才解码；否则同页不同 Key 和重复读取会破坏 cache。

完整读的最小顺序：检查 ticket/目标长度、读完并先拒绝 short read；取 raw TailKey，局部解码并验证 Trailer；按合法布局解码 coverage 与存储 CRC，调用现有 forward PayloadCRC；两项 CRC 成功后才返回 frame。HeadLen/TailKey保持 raw，普通读仍不检查 ticket 外 Fence。pooled Result失败/异常各归还一次，成功转移原租用 buffer，不加新 lease。

XOR 相位始终是相对 body 起点 frame+4 的累计 byte offset。完整 coverage 是0，meta 是 PayloadLength，padding 是 payload+meta，Footer 是 inputLength。Key0 原地解码为空操作。使用 `Vector` 批量变换，尾段 ulong/4/2/1；不重置每个 span/chunk 的相位。

FrameInfo 固定20B、reverse尾块24B。非对齐 TailMeta直接读原区间并解码，不物化 payload、不算 PayloadCRC；零 meta 不读不租。实验复用生产 cache，Key301、meta0/1/3/65535、phase1 的反复读取均保留 wire bytes。Slots16 已预热的小 meta 100次读 raw bytes为0；关闭 cache 时请求量准确等于 meta长度×100。65535B meta 多页 passthrough仍会进行真实 raw reads，不能把所有 preview 都称零I/O。

目前 `FrameInfo` full-read 快路径复用已验证 Trailer，不重新计算 TrailerCRC。候选方案若要求本次 full-read 两 CRC，则新增固定16B decoded Trailer检查并核对 info；PayloadCRC始终不可跳过。这里是明确的生产接入差异。

### 5.1 融合解码与 CRC 的裁决

融合原型每次load encoded ulong、XOR/store plaintext、把同一 plaintext ulong送入 `BitOperations.Crc32C`，减少重复 load；加法融合必须拆两个独立 uint lane。数学正确不保证更快。

最初固定测序的相同 reader 核在不同 chunk 标签下有明显上下文差异；不能归因于 chunk。补充试验固定同一 plaintext，分别使用各变换对应的 encoded 输入，共用目标空间；5个候选按样本轮转，7次样本、等量 copy与CRC、计时外校验最终 plaintext：

| 输入 | copy+CRC Key0 | XOR Vector+CRC | XOR fused | uint32减法 Vector+CRC | 减法 fused |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4KiB，µs | 1.61 | 1.79 | 1.64 | 1.82 | 1.80 |
| 1MiB，ms | 0.416 | **0.487** | 0.655 | 0.484 | 0.669 |
| 16MiB，ms | 7.61 | **9.43** | 11.43 | 9.49 | 11.48 |
| 近256MiB，ms | 120.77 | **156.08** | 187.32 | 155.55 | 189.58 |

本机大 coverage 的普通向量路径更稳妥；短输入融合有微小收益。W: 完整 checked-read 的普通/融合路径还受 Key、文件与内存上下文影响：64MiB batch，1MiB marker时42.02/37.96ms，16MiB marker时50.62/58.00ms。不是统一胜者。建议默认 Vector+现有CRC；不为几种小范围收益增加大小 heuristic、循环展开或新 CRC 实现。

## 6. uint32 模加法：可行，暂不替换 XOR

用户的数值环解释成立：`E(w)=w+K mod 2^32`，每个 word排除 `K=F-w mod 2^32`；m+1存在性、Key范围和marker-free前缀证明保持。完整 body 缺 Key 时唯一值改为 `encodedTailLen-L mod 2^32`。CRC仍计算 plaintext。两种方案相同固定开销和整帧依赖。

必须逐32-bit lane运算，不能用一次ulong加重复Key；低word的carry会串到高word。向量加减吞吐与XOR接近，前表没有显示稳定优势；数学解释更自然不等于读写管线更少。

| 边界 | XOR | 模 uint32 加减 |
| --- | --- | --- |
| 非对齐 byte slice | 只需已知相位 | 必须读完整相关 word；非空meta额外读取≤6B |
| Open检查padding | 精确读1–3B，健康逻辑上界39B | 读末coverage word4B，上界40B |
| 跨chunk边缘 | 重复Key bytes，累计相位 | 同word的边缘bytes需carry/borrow；word结束清零 |
| 完整word批量解码 | XOR，同一变换正反用 | 编码加、解码减，独立uint lanes |

不可省略低byte的反例：Key1，plaintext `FF 00 00 00` / `00 01 00 00`分别变成 `00 01 00 00` / `01 01 00 00`。只读encoded后3B相同，原后3B却不同，因此不能直接对meta slice逐byte减Key。读扩展完整words可解决；这不是正确性否决，只是额外接入。

加法也可在Builder原地处理跨chunk word的边缘carry，不必引入encoding sink。本轮 AddTransform 只实现完整word核；未实测跨chunk carry、完整加法writer或非对齐preview的真实I/O。不声称格式已冻结；推荐XOR依据是已验证的切片实现更小且没有观测到加法吞吐补偿。

## 7. 实验、独立复核与后续退出门禁

本机 AMD Ryzen 7 1700，8核/16线程、约64GiB RAM，Windows 11、.NET SDK10.0.201 / runtime10.0.5、32B Vector；禁用 tiered compilation。W: 为用户指定数据SSD，核实为NTFS `Write` 卷。未设置CPU affinity或隔离后台负载，不控制OS缓存淘汰；重开handle、CacheOff和durable flush都不构成cold SSD证明。

固定主证据见 [codec-cost-5711c47-20261002.json](../../experiments/RbfCodecCost/results/codec-cost-5711c47-20261002.json)：

- CPU：34 workloads ×15 operations =510 rows，每项5样本；0/31/4096/1MiB/16MiB/近最大输入，zero/random/Fence/前端与末端禁0..300，single/65533B chunks。
- 修正后的W: I/O：69 rows，每项5样本、64MiB顺序append batch；Append/flush分记，writer和reader样本均轮转。生产read指标在额外未计时batch收集；内容比较/JSON/fixture生成均不在计时内。
- 元信息：16 rows，使用生产CacheOff/Slots16；既核对尾20B也核对实际meta范围的encoded cache未改。
- 读核补测：20 rows，每项7轮转样本，同原文、相同copy/CRC工作量。
- 正确性：4740个scalar/vector组合、448帧、1760个坏CRC拒绝、9个参数拒绝；四Key策略wire相同，phase/guard/overlap/uint carry/borrow覆盖。独立Python table-based CRC/wire裁判核对76 XOR和76加法fixture；另有reviewer的bitwise CRC/meta互证。
- W: 真实最大帧 `L=268435452`，Key301、meta65535、meta phase1，1MiB输出workspace：完整write/flush及两种完整读均保原字节并通过CRC。单次大帧探针只证明可执行，不排名延迟、不证明生产Builder或恢复。
- 当前RBF依赖及实验Release构建零警告/错误；匹配RBF.Tests通过504/504。solution已知下游编译缺口不在本专项中修复，不能称solution验收。

保留CPU正式阶段、修正I/O阶段、补测阶段各自产物与源码hash。后两阶段新增入口参数使Program/runner不同；只比较相应工作核源码，不能写所有历史hash匹配当前全部源码。最初短测的byte-XOR与uint-add不公平、首次I/O的生产metrics在计时内，均不参与上述最终裁决；原始目录保留用于追查。

后续最小实施顺序：先落生产pending visitor/guard和Builder原地转换，完成取消/重试/fault反例；再接入Append尾布局、按需workspace、caller/pooled解码和所有info/meta/cache入口。复用现有CRC和故障事实，不加codec框架或配置层。

实施退出需要：旧reservation/epoch/Result可纠正拒绝保留；转换中异常零发布且Builder取消，后续新Builder正常；visitor重入动作前拒绝；Push/flush异常永久fault及池只归还一次；Footer禁Key、任意chunk相位与两CRC；真实生产Append/Builder/读路径W:端到端成本。现有process-prefix/单尾恢复实验另接生产实现后验收。
