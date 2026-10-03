---
title: "RBF 尾 EscapeKey 编码的 writer / reader 实现专项"
status: "Measured recommendation / production integration pending"
normative: false
---

# RBF 尾 EscapeKey 编码的 writer / reader 实现专项

日期：2026-10-02，2026-10-03 补 ZeroThenRandom 专项。初始生产源码基线：`5711c4706509eb3b144299a75f83c09aa75da75f`；随机补测仓库基线：`0e0df096ab7c0afcca20f29ab86c6b996dfde7d0`。本文补充[普通打开重构方案](rbf-open-fast-path-refactoring.md)的 G1，只研究整帧预处理、编码输出与读入解码。实验代码见 [RbfCodecCost](../../experiments/RbfCodecCost/README.md)。生产仍为 RBF1；本文没有分配格式版本、修改规范或实现生产 RBF2。

术语采用用户建议的 **EscapeKey（二进制转义键）**：每帧选择一个 uint32 值，使 encoded body 的每个对齐32-bit word均不等于 Fence。本文后续 Key/K、TailKey 及原实验 `PreparedFrame.Key` 均指 EscapeKey；TailKey 只是其尾部存放位置。**EscapePayload** 指前一条 Fence 之后、尾 EscapeKey 之前的整个区间，即 `raw HeadLen + encoded body`，长度为 `L-4`；这是与业务内容无关的分帧层术语。raw HeadLen 仍不参与 XOR，实际编码 body 为 `[4,L-4)`、长度 `L-8`。该定义不绑定 XOR 或模加法，禁止的是对齐 Fence，不要求消除滑动 byte 窗口里的同一字节序列。

推荐最小实现是：**CRC/footer 完成后，先检测 EscapeKey=0；失败且 EscapePayload≤256B 时使用一个 ulong bitmap，否则以系统随机源抽取候选并完整检测，不建立数组 bitmap；Append 使用借用输入和有界输出缓冲，Builder 在提交前原地编码现有 owned chunks；reader 保留 encoded cache，在 caller/pooled buffer 向量解码，再用现有 CRC。** 2026-10-03 的 ZeroThenRandom 与小帧标量 bitmap 补测见§3.1–3.2；原有测量保留为历史基线。模 uint32 加减法同样可行，当前测量未显示吞吐优势，先保留已验证的 XOR 实现；任意非对齐切片解码不作为格式选择的硬条件。融合解码/CRC 留作实验候选。

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

候选 FrameBytes 仍为 `HeadLen4 + encoded body + TailKey4`，后接 Fence4；body 包括 payload、meta、padding、PayloadCRC 和 Trailer16。每个 plaintext word w 排除 `Key=w XOR F`，m 个 words 在 `[0,m]` 至少留下一个 Key；这是 bitmap 的充分候选范围，不是 wire-format 必需的 Key 上限。随机搜索允许完整 uint32 值域，但 raw TailKey 必须不等于 Fence。

因此第一次发布之前必须知道完整 coverage、CRC 和 footer。只看 payload、不看最后 footer 就选 Key 不成立，例如合法业务 tag 本身也能排除候选。逐个递增 Key 试扫会在低 Key 禁词位于帧末尾时变成二次工作量；2026-10-02 的方案以固定候选加 bitmap 限制它，2026-10-03 的新方案改用与固定输入独立的随机候选。

但**依赖完整内容不等于拥有第二份完整内容**：

- [Append](../../src/Rbf/Internal/RbfAppendImpl.cs) 已收到完整 payload/meta spans。预处理直接读它们，不能原地改变用户数据；非零 Key 输出到有界 scratch。
- [Builder](../../src/Rbf/Internal/RbfFileImpl.cs) 立刻建立 HeadLen reservation。[SinkReservableWriter](../../src/Data/SinkReservableWriter.cs) 在最早 pending reservation 处停止 Push，当前 Builder 原本就保留全部 pooled chunks。
- Builder 的 `GetCrcSinceReservationEnd` 在 EndAppend 真正遍历这些 chunks，并非写入过程中自动积累 CRC。新格式的额外成本应与这条真实 baseline 比较。

实验的 `byte[][]` 是已冻结 chunk 视图。`Corpus.Split` 的复制、连续 Serialize destination 属于计时外 fixture，不是生产 Builder 接点，也不是格式必需的内存。

## 3. Key 选择：确定性基线与 ZeroThenRandom

| 实验策略 | 工作 | 临时 bitmap 数据空间 |
| --- | --- | --- |
| FullBitmap | 扫完整 body，标记所有 `[0,m]` 禁 Key，取最小缺失 | `4*ceil((m+1)/32)`，最多 8MiB |
| SmallBitmap | 32B 标记 0..255；耗尽再 FullBitmap | 常见32B；fallback 加完整 bitmap |
| ZeroFirst | 向量检测 F；禁0后直接 FullBitmap | Key0 无 bitmap，其余完整 bitmap |
| **ZeroThenOne** | 检测 F；禁0后检测 `F XOR 1`；两者都禁再 FullBitmap | **Key0/1 无 bitmap**，其余完整 bitmap |
| **ZeroThenRandom** | 检测 F；禁0后抽取随机 Key 并完整检测，撞禁则继续 | **无 bitmap** |
| ZeroThenRandom2Bitmap | 禁0后最多检测两个有效随机候选，两者都禁才 FullBitmap | 通常无 bitmap；fallback 最多8MiB |
| **ZeroThenTinyBitmapRandom** | 检测 F；禁0且 EscapePayload≤256B 时完整标记0..63，较大帧继续随机检测 | **一个 ulong，无数组 bitmap** |

定向检测遍历连续 body words，包括完整 footer。跨 chunk 保留至多3B组 word；对齐中段用现有 span `Contains`。检测遇到对应 word 可以提前停止；后续候选重新从 body 起点检查。原有四种确定性策略选择相同最小 Key，wire 相同；随机策略不要求 wire 相同，decoded 原文、两项 CRC 与 marker-free 保证必须相同。ZeroThenOne 最多启动三次禁 Key 扫描，最坏仍为 O(L)，不使用试 Key 循环。

当前 ZeroThenOne **不是一次扫描同时检测0/1**：先 Contains(F)，禁0再从头 Contains(F XOR 1)，两者都禁才 FullBitmap；这里的扫描次数不含独立 PayloadCRC 遍历。用户提出的两阶段候选可在首遍同时记录两个禁用位，只在两者都禁时启动 bitmap；只出现 F 时仍能选1，只出现 F XOR 1 时仍应选0。两位都置位即可提前停止首遍。此合并检测尚未实现或计时，不能套用本表的 ZeroThenOne 结果；收益需比较 SIMD 每段的额外检测工作与减少整帧遍历的成本。

CRC 与筛 Key 使用不同 loop，未宣称一次 memory load 同时完成两者。保留生产 `RollingCrc`，不用自写 CRC SIMD。完整 bitmap 在生产可以租用并清理**实际 m 所需范围**，释放或复用；无需常驻最大 8MiB。实验 FullBitmap 每次 new，因而表中同时包含清零与分配成本，不能直接等同 pooled 实现。

2026-10-02 历史 Prepare 中位时间；每项5样本，单位 µs，最后两行单位 ms：

| 输入，单 chunk | FullBitmap | SmallBitmap | ZeroFirst | ZeroThenOne |
| --- | ---: | ---: | ---: | ---: |
| 1MiB random，Key0 | 546.8 | 537.0 | 396.6 | **401.7** |
| 1MiB Fence 重复，Key1 | 936.6 | 926.9 | 970.4 | **411.8** |
| 1MiB 前端禁0..300，Key301 | 548.6 | 708.4 | 534.0 | **533.5** |
| 近256MiB random，Key0，ms | 229.6 | 144.2 | 112.2 | **112.5** |
| 近256MiB 末端禁0..300，Key301，ms | **143.8** | — | — | 174.6 |

最后一行是保留的反例：定向检测均扫到帧末，ZeroThenOne 比 FullBitmap 慢约21%。原 ZeroThenOne 建议不是所有分布都最快；其价值是常见 Key0/1 不租 bitmap、避免反复更新同一 bitmap bit，且退化有界。§3.1的新比较改用随机候选替代固定1；§3.2另测固定64位标量 bitmap 的小帧分支。不引入内容分类器或外部配置。

2026-10-02 原型 Key0/1 的 Prepare 分配约120B，来自实验 `PreparedFrame` 与 Footer 对象，不是格式要求；新增计数后的分配见§3.1。生产 Append 的小 footer 可使用已有局部/stack buffer，Builder 写进已有 chunks；不要把实验包装对象搬成每帧必需 heap 状态。

### 3.1 ZeroThenRandom：先赌0，之后随机检测

用户提出固定1没有0的免变换收益，且最大帧约64M words只占uint32空间的约1/64。新原型采用最小循环：

```csharp
uint key = 0;
while (ContainsAlignedBodyWord(Fence ^ key)) {
    do {
        key = NextSystemRandomUInt32();
    } while (key is 0 or Fence);
}
```

实际 `PrototypeCodec.Prepare` 复用上述跨chunk完整body检测；随机值来自 `RandomNumberGenerator.Fill(stackalloc byte[4])`，按LE读取，不mask成小范围。0成功不调用随机源。0失败后排除0是去掉已知失败候选；排除Fence是防止raw尾Key自身制造边界。候选完整检测通过才允许发布，随机只影响搜索耗时，marker-free保证仍是确定的。测试注入 `Func<uint>` 只用于强制失败/高Key反例，不建议作为生产公开配置。

固定body有m个words，最大 `m=2^26-3`。0已被禁后，在均匀、独立的有效候选域 `2^32-2` 中最多剩m−1个禁Key，因此单次有效候选失败概率<1/64，期望尝试次数<64/63。这个结论不要求payload随机，但要求候选与冻结输入独立；系统CSPRNG用来避免可预测序列被输入针对。它是概率上界，不是确定完成时间或实验观察得到的失败率保证。

比较版 `ZeroThenRandom2Bitmap` 允许最多两次**有效随机候选扫描**后fallback，计入0和bitmap共最多四次body扫描；0/Fence的rejection draws仍可继续，所以整个RNG调用数/完成时间没有硬上限。理论两次有效候选均失败的概率<1/4096；不能据此声称实验测到了罕见事件。默认无bitmap版本更小，保留比较版作为有扫描次数需求时的候选，不在格式层引入随机预算配置。

固定新证据：[zero-then-random-0e0df09-20261003.json](../../experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json)。185条CPU记录覆盖37 workloads×5策略，每项7样本；五策略同CRC/footer管线，RNG及每次搜索都计时，按样本轮转顺序。64次或近最大帧16次Key/扫描/bitmap观察在独立未计时阶段收集，不能当作计时样本的Key记录。旧入口环境字段Samples=5只是旧阶段默认值，本专项实际CPU和I/O数组均为7，快照 `MeasuredProtocol` 已标明。

本轮同组 Prepare 中位，单位µs；最后两行单位ms：

| 输入，单chunk | FullBitmap | ZeroFirst | ZeroThenOne | ZeroThenRandom | Random2Bitmap |
| --- | ---: | ---: | ---: | ---: | ---: |
| 4KiB Fence重复 | 3.774 | 3.882 | **1.708** | 1.824 | 1.817 |
| 4KiB 末端禁0..300 | 3.122 | 3.156 | 3.220 | **1.862** | 1.868 |
| 1MiB Fence重复 | 962.8 | 962.6 | 409.6 | **406.5** | 413.1 |
| 1MiB 末端禁0..300 | 707.6 | 735.4 | 763.6 | **430.2** | 434.6 |
| 16MiB 末端禁0..300 | 14551.5 | 16396.9 | 17664.7 | **9381.4** | 9488.3 |
| 近256MiB Fence重复，ms | 254.9 | 257.0 | 117.9 | **117.7** | 117.7 |
| 近256MiB 末端禁0..300，ms | 204.8 | 213.2 | 237.2 | 140.9 | **140.5** |

无bitmap随机版在末端禁低Key分布省去逐word标记与最大8MiB bitmap，优势明确；纯Fence重复则与固定1接近。它不是普遍更快：31B Fence输入，固定1约121ns、随机约212ns；单次系统uint抽取中位92.37ns、当前线程heap分配为0。近最大distinct-rich且头部显式Fence时，固定1约109.8ms、随机112.7ms，差约2.6%。§3.2据小帧专项补充固定 scalar bitmap 分支，大帧搜索保持不变。

新Prepare包装对象加两个观测计数后常见heap分配为128B；这是实验对象/footer，不是生产必要分配。观察的所有无bitmap随机帧均未分配bitmap；自然随机观察没有二次有效尝试或fallback，不能证明尾部概率或最坏延迟。用注入候选确定性覆盖4次撞禁后成功，以及两次撞禁走bitmap；计时输入另有 `random-fence-first/last` distinct-rich模式，避免只在重复word输入上检验随机路径。

W:端到端采用1MiB workspace、32MiB batch、7次轮转；Prepare/RNG/扫描/编码/write均在Append时段，FlushToDisk单独计时，每个写入帧的完整read/CRC/原文核对在时段外：

| workload | ZeroThenOne Append ms | ZeroThenRandom Append ms | One / Random batch durable ms |
| --- | ---: | ---: | ---: |
| 1MiB Fence重复×32 | 31.59 | 31.39 | 50.48 / 50.20 |
| 1MiB 末端禁0..300×32 | 42.76 | **31.42** | 62.81 / 50.14 |
| 1MiB random+末端Fence×32 | 32.68 | 32.47 | 51.40 / 51.06 |
| 16MiB 末端禁0..300×2 | 43.92 | **33.14** | 62.50 / 51.45 |

I/O共20条记录，全部writer变体同工作量；workspace取得与文件创建仍在计时外，没有物理cold SSD保证。Random2Bitmap和无bitmap版通常接近，未观测自然fallback，不能从这些时间排名fallback本身。

正确性新增848帧、2512次损坏拒绝、153个独立Python **bitwise** CRC/wire裁判；153个fixture本次均Key>旧m上限。跨chunk/非滑动Fence、footer独自禁0、拒绝rawKey=Fence、跳过0、全uint高Key、两CRC、原文与唯一缺Key公式均核对。真实W:最大帧 `L=268435452`、Key=3819218019、meta65535，0 bitmap，两种完整读以及 `encodedTailLen XOR HeadLen` 重建通过；主线程另核对实际Header/HeadLen/rawKey/Fence、独立bitwise TrailerCRC，文件SHA256为 `df15abfc41679344174aab913864818a32f50e195b3e6d5d99a5bf69d42ebf62`。单次大帧不排名延迟。

本轮把 `StreamCodec.DecodeAndCheck` 的Key上限改成只拒绝Fence。旧 `MetadataProbe` 仍使用确定策略fixture和m上限，**未验收全uint随机Key的独立meta/cache API**；生产接入需统一相关guard。旧FastOpen历史模型/快照仍保留其有界Key条件，不能直接声称已验证全uint随机writer的进程恢复。缺Key代数重建与随机搜索正交；是否取消补Key仍是后续需求决定。本轮实验不改变恢复政策。

所有C#核与runner在正式测量期间通过源码hash前后守护；结束后仅证据派生exporter补充Samples解释，差异列在快照中。实验Release增量构建0警告/错误；首次quick重编译依赖有43条既有XML文档警告、0错误，保留日志。匹配Release RBF.Tests本轮504/504通过，TEMP/TMP在W:，TRX保存在 `W:/RbfCodecCost/random-formal-20261003/rbf-tests`；未更改生产src或宣称solution、包消费、生产RBF2验收。

### 3.2 小帧用一个 ulong 作为完整 bitmap

用户提出：保留Zero优先，失败后按内容无关的EscapePayload长度选择小帧标量bitmap，较大帧继续随机循环。原型新增 `ZeroThenTinyBitmapRandom`，采用包含端点的 `L-4≤256` 阈值；当前布局对应payload+TailMeta经padding补齐后≤232B，不能把它误写为user payload≤256B。

存在性证明直接来自实际编码范围：EscapePayload包含4B raw HeadLen，因此阈值内的encoded body最多252B/63words。每word最多禁止一个Key，用64bits表示候选0..63必有空位。raw Key不等于Fence也自动成立，因为所选键≤63。若把阈值改成encoded body≤256B，则会有64words；仅标记0..63不再有无条件保证，需收紧阈值或利用Zero已失败改用候选1..64。本轮按用户的Fence到EscapeKey区间定义，不增加这项变体。

```csharp
// Only after the complete Zero check fails and EscapePayloadBytes <= 256.
ulong forbidden = 0;
foreach (uint word in AlignedPlaintextBodyWords) {
    uint candidate = word ^ Fence;
    if (candidate < 64) forbidden |= 1UL << (int)candidate;
}
uint key = (uint)BitOperations.TrailingZeroCount(~forbidden);
```

这是完整bitmap，不是投机小表；第二遍完成后确定选键，无随机源、fallback或数组清零/索引。范围检查不可省，C#的ulong shift count会取低6bits，否则禁Key65会错误标记Key1。循环仅解释连续二进制words，不按payload/meta/CRC/tag语义分类；CRC/footer必须先完成，所有将编码的words均参与标记。raw HeadLen计入长度阈值，但不扫描、不编码。

热核使用 `MemoryMarshal.Cast<byte,uint>` 遍历完整对齐中段，在局部ulong累积后每chunk写回一次；单word循环没有 `ObserveWord` 调用或bitmap数组访问。跨chunk保留至多3B carry；仍有每chunk helper调用，不声称JIT必把全部状态留在寄存器。首版逐word span切片/通过ref更新bitmap在232B输入反而慢于随机版约8–9%；简化热核后的两次独立run均观察到阈值内非零键路径获益。首版数据留在 `W:/RbfCodecCost/tiny-ulong-20261003`，优化后复测在 `W:/RbfCodecCost/tiny-ulong-local-20261003`；它们不混入正式快照。

正式证据：[tiny-ulong-7667b9c-20261003.json](../../experiments/RbfCodecCost/results/tiny-ulong-7667b9c-20261003.json)，SHA256 `802ea5098f913aeaab4ca42cd3b2f00a651501c51b2e568ab364780de7024cc9`。W:目录 `W:/RbfCodecCost/tiny-ulong-formal-20261003`。26workloads×4策略=104条CPU记录，每条7样本轮转，计时包括完整Prepare、CRC/footer、相同实验对象分配及随机调用；没有本轮磁盘吞吐测量。

正式同组Prepare中位，单位ns；输入指payload+TailMeta合计长度：

| 输入与分布 | ZeroThenRandom | 小帧ulong混合策略 |
| --- | ---: | ---: |
| 0B，footer tag独自禁0 | 201.4 | **103.3** |
| 31B，Fence重复 | 218.1 | **125.0** |
| 31B，禁低Key | 203.9 | **121.8** |
| 128B，禁低Key | 234.5 | **171.2** |
| 232B，Fence重复，EscapePayload=256B | 267.8 | **224.7** |
| 232B，禁低Key，EscapePayload=256B | 271.0 | **234.7** |
| 233B，Fence重复，EscapePayload=260B | 288.5 | 288.9 |
| 4KiB，禁低Key，仍随机 | 1787.7 | 1770.3 |

保留Zero成功时的免XOR收益，不为它建立bitmap。其计时中位有机器波动：232B random组为167.3/183.6ns，七样本范围159.3–198.7/160.9–222.6ns重叠；不能从控制流相同推导零额外周期，也不能声称所有小帧更快。小帧非零键可见获益，额外算法只需要固定ulong和一次长度分支；256B是存在性允许且已测的实施起点，不是跨机器最优阈值或公开配置。大帧沿用原随机循环，不从本轮小范围CPU结果推断其真实I/O变化。

新资格覆盖448帧（352个阈值内、96个较大帧）、112个独立Python bitwise CRC/wire向量；单字节/3B/7B chunks、footer独自禁0、最小键对照、完整marker-free、两种checked-read CRC/原文、shift别名与232/233B边界均通过。Zero及整个小帧路径用会抛错的随机源证明不抽样；边界外强制高Key证明仍走原循环。runner保留既有4740变换、448个XOR帧、448个加法帧等资格并做测量源码hash前后守护；正式Release build为0警告/错误。生产源码/规范和旧不可变快照未改，本轮无新生产恢复或高Key元信息资格。

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

用户的数值环解释成立：`E(w)=w+K mod 2^32`，每个 word排除 `K=F-w mod 2^32`；m+1存在性、bitmap充分候选范围和marker-free前缀证明保持。完整 body 缺 Key 时唯一值改为 `encodedTailLen-L mod 2^32`。CRC仍计算 plaintext。两种方案相同固定开销和整帧依赖。

必须逐32-bit lane运算，不能用一次ulong加重复Key；低word的carry会串到高word。向量加减吞吐与XOR接近，前表没有显示稳定优势；数学解释更自然不等于读写管线更少。

Fence、尾 EscapeKey、CRC 和 Trailer 均为4B对齐；完整 body 也由padding补为完整words。现行 TailMeta 的起点/长度不要求对齐，chunk/Advance 边界也不等于格式 word 边界。这些局部情况可通过扩展读取或跨chunk暂存1–3B处理，不要求给 RBF 增加任意切片转义的公开特性；其处理成本是实现比较项，不是模加法的正确性否决条件。

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
