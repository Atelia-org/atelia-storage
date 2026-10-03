---
title: "RBF 普通打开快路径实施记录"
status: "RBF3 implemented / source, tests and production evidence independently reviewed"
normative: false
---

# RBF 普通打开快路径实施记录

日期：2026-10-01。历史调查源码基线：`5711c4706509eb3b144299a75f83c09aa75da75f`。2026-10-03用户完整授权RBF实施：生产Header/Fence确定为 **RBF3（LE 0x33464252）**，以区别历史byte RBF2实验。源码已提交 `0999851207978fe947ecabccb1093e5774d007df`；对应源码树最终Release RBF资格670/670通过，提交后的clean源码重新构建并完成正式生产探针，证据见§11。源码、测试身份与正式证据已独立复核；旧实验不重标为RBF3结果。

2026-10-02 补充：[writer / reader 实现专项](rbf-codec-implementation-study.md)完成 C# codec 成本原型和 W: 真实读写。推荐 Append 有界输出、Builder owned chunks 原地编码、wire cache/caller 解码；当时生产接入仍待实施。搜索策略随后由下述新实验更新。

2026-10-03 更新：专项新增 **ZeroThenRandom**。先检查 EscapeKey=0，失败后用系统随机源抽取全 uint32 候选（排除0/Fence），完整检测通过后采用；无 bitmap 循环版成为实施主候选，固定1与两次随机后 bitmap 版保留为实验对照。185条CPU、20条W: I/O的7样本证据见[专项结果](../../experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json)。随机搜索没有确定的尝试次数上限；31B反例、概率尾及尚未验证的元信息/恢复入口见专项。该更新不改变恢复动作。

同日追加小帧快速路径：Zero失败且 **EscapePayload≤256B** 时用一个 `ulong` bitmap选择最小可用键，超过阈值仍走上述随机循环。EscapePayload是前一条Fence与尾EscapeKey之间的区间，包含raw HeadLen，长度`L-4`；实际编码body最多252B/63words，64候选保证有空位。CPU实测、跨chunk与边界资格见[专项§3.2](rbf-codec-implementation-study.md#32-小帧用一个-ulong-作为完整-bitmap)。这是writer内部算法选择，不改变wire、reader或恢复规则。

基础实现另见 [ZeroThenTinyBitmapRandom重构方案](../Data/xor-escape-key-refactoring.md)：两轮辩证审查选择Data窄入口、内部共享选键/XOR核；Append至多三个借用spans，Builder对reservation后全部已写bytes一次选键并原地变换，不公开source/view/visitor。Data基础已在 `00329fd` 同时落地两种真实输入并完成行为/成本资格；RBF3接入和生产资格见§11，不改变本文件的格式/恢复规则。

2026-10-03最终长度裁决：**HeadLen/TailLen以4B为单位、Fence≥2^26**。物理FrameBytes长度为L bytes，wire存 `U=L>>2`；Fence下界是 `0x04000000`（67108864）。Data选键入口保持该范围，EscapeKey仍为完整uint32。生产RBF3 Fence=0x33464252满足约束，不附加 `Fence & 3 != 0`。长度合同已进入[格式规范](rbf-format.md)，Data不拥有长度字段。

此前实验profile的HeadLen/TailLen均存byte长度。已有快照、黄金向量和性能结果原样保留，**不作为新units wire的CRC、恢复或生产格式验收**；实施阶段须新增明确标识的向量和证据。

用户当前决定：**单个 RBF 文件优先；恢复目标仅进程终止，断电依赖平台；确认真实边界后可截掉未完成帧；Open 负责分帧结构，PayloadCRC 留给 ReadFrame；只考虑补原尾 Key/Fence；保留约 256MiB 单帧上限；逆序读取优先，研究省头 Key。** 独立反审与字节推演支持采用这些简化。

最小方案是 **单份尾 Key、结构快开、未完成 body 截断、完整 body 只补尾 Key/Fence**。恢复只有 `None / Truncated / CompletedTail` 三种动作；`CompletedTail` 仅追加确定的 1–8B 闭合后缀。无需补 coverage、CRC、Trailer、墓碑、nonce、未知 completion 或 writer oracle。

## 1. 实施目标、范围与检查职责

新 profile 普通 Open 负责 Header 分派、真实尾边界、尾部结构与单个残尾。**健康打开只读取固定大小的结构字节，与历史帧数和末帧 payload 大小都无关；内容正确性由 ReadFrame / ReadPooledFrame 检查。** 同一职责分离也适用于 RBF1：旧格式仍从 Header 遍历完整结构主链，不再 eager 校验末帧 PayloadCRC；不会因此获得局部 EOF 快开。

结构与内容必须分开：

| 检查对象 | 新 profile 普通 Open | 普通 checked-read | 扫描 / Audit |
| --- | --- | --- | --- |
| Header / 未知 profile | 必须检查，未知拒绝 | 使用文件已确定的 profile | 各入口必须 Header 分派 |
| 末帧、残尾直接前驱的结构 | TrailerCRC、Key/长度/descriptor/padding、相关 Fence | 检查指定 FrameBytes 结构 | 扫描检查跨越的 framing/TrailerCRC |
| Payload / TailMeta / PayloadCRC | 不校验PayloadCRC，不读取内容作资格证明 | ticket每次两CRC；info管线复用创建时Trailer资格、每次验PayloadCRC；失败不能返回有效数据 | 元信息扫描不替代全payload Audit |
| 未访问历史结构 | 不 eager 检查 | 只检查所选帧 | 全链扫描 / Audit 才覆盖全部 |
| 历史 Fence 损坏 | 可延迟发现 | ReadFrame 不检查 ticket 外 Fence | boundary / 跨界扫描检查 |
| 主序列成员身份 | 正常 writer 的 marker-free 前缀约束建立尾边界 | 任意 ticket 局部合法不等于主链成员证明 | 从 Header 连续正扫可建立全链证据 |

可观察变化：末帧或直接前驱的 payload / PayloadCRC 损坏而结构完好，Open 可以成功；若尾闭合后缀缺失，也可只补 Key/Fence，原损坏内容保留，随后 ReadFrame 拒绝。不能声称 Open 证明“末帧内容完整”，也不能因内容损坏回退或截掉结构完整的帧。

完整A/B/C中只损坏A后的Fence，尾C及左边界仍健康，RBF3局部结构Open和普通ReadFrame(A)均可成功，GetScanBoundaryAfter(A)才拒绝；RBF1全结构主链Open仍拒绝。§8的历史RBF1探针验证普通读/boundary职责，不是RBF3快开验收。

本方案只定义单文件物理分帧、读写与尾部恢复。RBF 双读、单新 writer；mixed、下游适配、可插拔 codec、sidecar/frontier 与业务引用图转码另有明确需求再设计。

## 2. 旧实现与byte格式的证据

本节记录5711c47调查基线的RBF1实现和历史实测，不描述本轮正在接入的RBF3源码；旧数据/数值不重标为新实现结果。

[RbfTailRecovery.Open](../../src/Rbf/Internal/RbfTailRecovery.cs) 无条件从 Header 循环按 HeadLen 前进，检查长度、TrailerCodeword、descriptor、padding、HeadLen/TailLen 与 Fence，然后完整校验最后相关帧。可写、只读以及 Action=None 均执行。历史 payload 不全部读取，但每个已闭合帧约有 24–27B 小范围读取与对应调用成本。

当前实现直接进行每帧 3 次小读，有 padding 时再多一次；工厂资格读取发生在正式 cache 建立之前。[生产探针](../../experiments/RbfFastOpen/ProductionProbe/Program.cs) 实测零 payload、无 padding、`CacheMode.Off` 的只读打开：

| 历史帧数 N | Read / RawRead calls | RawReturnedBytes |
| ---: | ---: | ---: |
| 1 | 8 | 56 |
| 100 | 305 | 2,432 |
| 10,000 | 30,005 | 240,032 |

该 fixture 为 `3*N+5` calls、`24*N+32` bytes，包含 Header 和末帧资格读取，不含创建与后续用户读取；它是同步读取请求/返回字节证据，不是设备物理 I/O、冷盘延迟或新格式性能排名。

当前 solution 基线因仓内其他项目的旧工厂调用存在编译缺口。独立生产探针只构建 `src/Rbf` 依赖闭包，不能称为 solution 通过；其他项目的修复不作为本 RBF 方案的任务或退出门禁。

父提交 `bf7d68a` 的RBF工厂仅检查最小长度、4B对齐与Header。调查基线循环由 `5711c47` 新增，并写入当时接口规范的MUST条款。本轮G0已正式同步[接口规范](rbf-interface.md)及相关验收，不能把当前职责分离描述成暗中跳过旧合同。

[内嵌 EOF 验收](../../tests/Rbf.Tests/Internal/RbfTailRecoveryAcceptanceTests.cs) 用正常 Append 构造外层 payload 中的完整 RBF 文件，断点停在内嵌 Fence 后。内嵌候选的全部 CRC 正确，却不能成为外层主序列成员。这不是随机 CRC 碰撞，而是把合法字节原样放入合法 payload。

所以“先局部校验，只有失败才全扫”会将这个残帧误归 happy path，根本不会进入失败后的扫描。扩大为固定 K 个前驱也不够。

### 最大帧长度也不能代替可靠起点

还可以构造跨多个真实帧的平行伪链，不限于单个外层 payload：

- 真实帧起点 `H_i = 4 + 68*i`，FrameBytes 长度 64，payload 长度 40。
- 伪帧起点 `P_i = H_i + 32`，长度同为 64；其尾 CRC、Trailer 与 Fence 位于下一真实帧的 payload 内。
- 两条链的结构字段不重叠；按 `真实 CRC_i → 伪 CRC_i → 真实 CRC_(i+1)` 计算，无循环依赖。
- 写完 n 个真实帧后，在下一个真实帧起点加 32B 处终止写进程，伪链恰好闭合在 EOF。

两条链都能有正确的长度、前后 Fence 和完整 CRC。伪链可任意延长，因此“逆向验证超过 MaxFrameLength 即相信当前尾”仍不能证明成员身份。必须连回可靠起点，或改变写出约束。有限模型已验证 64 个真实完整帧和 64 个伪完整帧。

## 3. 方案选择与排除理由

| 方案 | 能否排除正常残帧的假完成尾 | 代价 / 限制 |
| --- | --- | --- |
| 旧格式局部 EOF 成功、失败才全扫 | 不能 | 假 EOF 可全部局部校验成功 |
| payload 后补 padding / salt / 固定 trailer 字段 | 单独不能 | 进程可能停在此前内嵌假 EOF |
| 8-bit 全局 XOR | 不能保证有 Key | 任意 payload 可排除全部 256 个 mask |
| 可信 frontier / 双槽 Header / sidecar | 可以设计 | 新可变权威及其更新、flush、恢复协议 |
| byte/word escaping | 可以设计 | 变长编码与 ticket/reservation 映射 |
| 32-bit body XOR，禁止内部对齐 Fence | 有 Key 存在性与前缀证明 | 新 profile、线性预扫/变换、单份尾 Key 增加 4B |

只禁止嵌套假尾必需的内部边界，无需逐个检测内嵌合法帧；键搜索采用上述Zero/小帧标量bitmap/随机检测，不逐个递增键试扫。

## 4. 单份尾 Key 布局与快开算法

术语采用 **EscapeKey（二进制转义键）**，指每帧用于消除 encoded body 内对齐 Fence 的 uint32 参数。后文 Key/K 与 TailKey 均指此值；TailKey 仅表示尾部存放位置。其语义不绑定 XOR 或模加法，也不要求消除非对齐滑动窗口内的 Fence 字节序列。

**EscapePayload** 是前一条Fence之后、尾EscapeKey之前的内容无关分帧区间：`raw HeadLenUnits + encoded body`，长度`L-4` bytes。其中raw HeadLenUnits不参与XOR；键选择遍历的编码body长度为`L-8` bytes。小帧阈值按EscapePayload长度判定，不按user payload或TailMeta长度判定。

生产Header/Fence为 `RBF3`，bytes `52 42 46 33`，LE u32 `F=0x33464252`：

```text
FrameBytes =
    raw HeadLenUnits  4B LE，值为 U
    XOR_Key(
        Payload + TailMeta + zero Padding
        + PayloadCRC  4B
        + TrailerCodeword 16B（末word是TailLenUnits，明文值为U）
    )
    raw TailKey       4B
后接 Fence            4B
```

CRC先按plaintext计算，再对连续body编码。PayloadCRC正向计算、LE存储；TrailerCRC对 `descriptor LE || tag LE || TailLenUnits LE` 的原始wire字节反向计算、BE存储；word XOR按LE解释。TrailerCRC覆盖的是LE(U)，不是LE(L)。读端先解码并验证这份Trailer，再向内部布局转换为byte长度，禁止先把TailLen字段改写成L再验CRC。遍历方向与存储端序独立验证。

HeadLen/TailLen仍描述同一FrameBytes、不含Fence，但新profile的两个uint32 LE字段都存 **U个4B units**，以下称HeadLenUnits/TailLenUnits。字段各占4B，数值本身无需是4的倍数。固定开销旧24B→新28B，PayloadOffset仍为4，body words数 `m=U-2=(L-8)/4`。

| 量 | 新profile契约 |
| --- | --- |
| 物理FrameBytes长度L | `28≤L≤M` bytes，4B对齐；`M=SizedPtr.MaxLength=2^28-4=268435452` bytes |
| wire长度U | `U=L>>2`；`7≤U<2^26`，最大67108863；HeadLenUnits明文，TailLenUnits参与body XOR |
| wire→内部长度 | **先验证U的完整uint32值在上述范围内，再计算L=U<<2**；不能mask高bits或先shift后验证 |
| Fence F | uint范围 `2^26..uint.MaxValue`；本生产profile固定 `0x33464252`，不提供公共Fence/codec配置 |
| API与位置 | ticket/SizedPtr.Length、offset、span长度、TailMetaLength、PayloadLength及所有读取范围继续以bytes表示 |

仅在新profile的序列化/解析边界换算，不新增公开units类型、长度模式enum或第二份持久长度。相同M下payload+TailMeta容量减少4B，旧decoder保留旧完整容量。TailMeta 65535B与业务tag值域保持；不修改SizedPtr公共byte语义或额外收窄合法frame起点。

此选择来自word分帧本身：所有合法wire长度形成连续值域，读端range检查后的转换自然得到4B对齐长度；`U<F`统一排除了raw长度成为Fence的可能。byte单位并不会自动提供非对齐分帧能力，后者还需改变Fence扫描网格、word编码与票据约束。两方案合法编码数量相同，不声称units增强CRC保护、节省空间或已有实测速度优势。

### 4.1 Key 存在性与前缀证明

每个 plaintext word w 只排除一个 Key：`w XOR Key == F ⇔ Key == w XOR F`。m 个 words 至多排除 m 个 Key，因此 `[0,m]` 必有可用值；这是 bitmap 对照/fallback 的存在性证明，不是所有有效 EscapeKey 的值域限制。当前实施主候选先完整检查 Key0；禁0且`L-4≤256`时，body至多63words，用一个ulong标记0..63并取最小未标记值，无随机调用或数组bitmap。较大帧以系统随机源抽取全 uint32 Key，排除0/Fence，再检测完整 body 是否含 `F XOR Key`，遇禁值继续抽取，不建立 bitmap。内容固定、抽样独立均匀时，约256MiB帧每次有效候选失败概率小于1/64，随机尝试次数的期望小于64/63；这些是理论界，无确定次数或耗时上限。实验保留两次有效随机候选扫描失败后 bitmap 的比较版本。Key0 省 XOR；非零随机 Key 不要求最小，wire 可随抽样改变，原文、物理长度和 ticket 不变。

coverage、PayloadCRC与完整Trailer均纳入连续word划分，跨span/chunk不能重置相位。raw HeadLenUnits满足 `U<2^26≤F`，不等于F；raw TailKey必须不等于F；所有encoded body words不等于F。物理长度L=4U保证边界始终位于全局4B网格。因此正常writer任意进程终止前缀中，完整的全局4B对齐Fence只在真实边界出现。尾Key的F排除不能只靠body检测代替；F的下界也不能删除全uint32随机路径的Key!=F检查。

在RBF容量域，`m≤2^26-3<F`，故完整bitmap对照的 `[0,m]` 候选天然避开raw F，最大8MiB。这为未来FullBitmap实现保留简化空间，不把它加入默认策略。Data仍支持更大的int长度body，不能把这个候选分离或8MiB界推广到全部Data输入。

这是正常 writer 与顺序写出前缀的保证。进程终止后 OS/文件系统仍运行；不另建断电、sector 撕裂或设备乱序协议。任意位损坏或手工镜像可能破坏 marker-free invariant；局部 Open 不是任意镜像认证。Open 不遍历 body 重证这个 writer invariant，也不以 PayloadCRC 代替成员证明。

### 4.2 健康 Open：固定结构读取

Header-only 是正常空文件。非空文件若 EOF 对齐且完整尾 Fence 存在：

1. 固定读尾部 `encoded Trailer16 + TailKey4 + Fence4`，检查已读 Trailer words 不等于 F，解码并验 TrailerCRC。
2. 对解码后的TailLenUnits验 `7≤U<2^26`，再换算L；验Key不等于F、descriptor/reserved/meta/padding长度及offset范围，定位帧起点。encoded TailLen word是全uint32，不能在XOR前套用U上界。
3. 读该起点的HeadLenUnits和紧邻左Fence；验证raw U合法且与尾U一致，验证真实边界。
4. 仅检查最多 3B padding 为零，不读 payload、TailMeta 或 PayloadCRC 作内容校验。

一个合并左 Fence/HeadLen 的直接读取实现，包含 Header4，总请求最多 `4+24+8+3=39B`，非空无 padding 时 3 次读，有 padding 时最多 4 次。**这是模型逻辑请求/返回字节上界，不是生产 syscall、cache 页、设备 I/O 或延迟结论。** 元信息合法并不代表内容合法。

逆向 `ReadFrameInfo` 固定读取Trailer16+TailKey4；普通ScanReverse仍需验证沿途Fence，可合读24B，均无需头字段或payload。前向扫描先验HeadLenUnits，再换算L跳到尾块读Key/Trailer。FrameInfo等内部布局继续存byte长度；省头Key只删除了一份副本。

### 4.3 异常尾定位

EOF 不满足闭合尾形态时，先逆扫最近完整 aligned Fence。设其结束位置为 B、EOF 为 E，单残尾 `E-B<=M+3`，故最多检查 `M+7` bytes 可抵达 marker 起点 B-4。命中第一个 marker 后，仅检查直接前驱结构与该 suffix；坏候选或可见结构矛盾即拒绝，不能越过它寻找更早好帧。

异常定位仍可能读约 256MiB；这是保留最大帧上限的成本。找到 B 后不再为资格读取全部 payload 或计算 PayloadCRC，直接前驱也只做固定结构检查。

## 5. 实现接点与写出边界

新writer由byte布局先得到合法L，再计算U。完整plaintext Trailer必须先写TailLenUnits=U并封TrailerCRC，随后才能选Key/XOR。raw HeadLenUnits也写U，保持在编码范围之外。旧RBF1的TrailerCodewordHelper仍解释byte TailLen；新profile不能把单位不同的原始值直接送入旧布局计算。实现用两组具体wire序列化/解析接点，在profile边界向byte布局归一化，不让units流入通用buffer或Data核。

普通 Append 已持有完整 spans，先计算coverage CRC和完整plaintext footer，再将payload、TailMeta、footer作为三个借用spans交给Data中的 `XorEscape.SelectKey`，采用ZeroThenTinyBitmapRandom。CRC与筛Key仍是不同loop，不宣称一次memory load完成两者。Key0大帧直接写原spans；非零Key使用有界buffer编码输出，不改用户数据、不复制整帧。专项建议按需取得、每writer重用1MiB输出空间；durable与Pool成本边界见专项。预处理/RNG异常处于实际输出之前，不应被现行包住整个Append调用的catch-all误标为write fault；真正输出/flush异常仍永久fault。

Builder已整帧缓冲：BeginAppend的HeadLen reservation持续pending，SinkReservableWriter.FlushCommittedData在首pending处停止，EndAppend提交后逐chunk Push。具体 `XorEscapeSinceReservationEnd(headToken,fence)` 成员在完整plaintext CRC/Trailer之后、raw Key/Fence追加之前，对reservation后全部已写body一次选键并原地XOR，再由原sink Commit/Push。没有外部visitor、可逃逸view或byteCount；内部cursor保持carry/相位，不复制整帧。全部Result/借用/参数拒绝置于首次padding/footer修改前；从该首次准备修改到Commit前的异常取消/Reset，禁止同一Builder重试；实际输出异常永久fault。RBF3取消路径已接入，真实Builder端到端成本及其分配保留见§11。

| 接点 | 实施边界 |
| --- | --- |
| 工厂 / Open | Header 一次分派；结构资格、修改、最终 reader/cache 同 owned handle |
| Append / Builder | 完整 CRC/footer 后选 Key；尾块含 Trailer/Key/Fence；前置 Result 失败不写，输出/flush 异常永久 fault |
| Builder pending bytes | 唯一有效HeadLen、无未Advance借用、PushedLength=0；完整plaintext footer后fused选键/XOR，rawKey/Fence后置；无callback/Push的Data操作不新增visitor guard；首次padding/footer修改起的预发布异常取消当前Builder |
| wire cache | 保留 encoded bytes，只在调用方 buffer 解码，不修改共享 cache |
| FrameInfo / Reverse | 保存内部 TailKey、offset/相位；尾块解析，无头 Key，也不增加公共 ticket 版本 |
| ReadFrame / pooled / info快路径 | 原user bytes；ticket每次两CRC，info复用创建时Trailer/Key资格并验本次PayloadCRC，禁止因Open成功跳过内容校验 |
| TailMeta / preview | 非对齐起点、跨 chunk、零 meta；维持只校验 TrailerCRC 的原职责，不物化整帧 |
| Forward / boundary | 从 HeadLen 跳尾解析；boundary 内容见证及指定 FrameBytes/Fence 验证保留 |
| 离线工具 | 各入口单独 Header 分派；工厂分派不自动覆盖工具 |

普通 Append 只有总长≤4096B 时合为一次 Write，大帧走首/mid/尾多次 Write；Builder 逐 chunk Push。合写 Trailer/Key/Fence 可减少 syscall，不作为不可中断的事务边界。[.NET 10 Unix WriteAtOffset](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Unix.cs#L89-L130) 明确处理单次正数短写并继续剩余 bytes。因此一次 API 调用本身不能证明不存在部分输出；这里没有声称已实测 Windows syscall 内 kill。

补尾 Key/Fence 仍有确定且便宜的恢复路径（§6），保留它不需要 payload 读取；不以“似乎不容易撕裂”删除此前提。XOR 是 framing 编码，不是加密；更强 metadata/key 绑定另案。

离线支持矩阵：普通读/forward/reverse/boundary/meta须覆盖RBF1/RBF3；RbfRecovery.OpenReadOnly/Fence search、RollingCrc Trailer search、TruncateToSuggestedTail保留RBF1语义，首切片显式拒绝RBF3及未知/实验RBF2。共享Write的离线输入由caller冻结至后续info读取完成，不能套用普通工厂资格不变保证。未来rescue按具体需求设计，不引入scanner插件框架。

## 6. 单尾恢复：截掉未完成 body，只补确定闭合后缀

### 6.1 分类与唯一 Key 重建

必须先按§4.3建立真实B，必要时验证直接前驱结构。不能从EOF猜footer并以局部CRC成功倒推成员身份。B/R及下面的范围均以bytes计；完整HeadLenUnits先验 `7≤U<2^26`，再得到物理长度L=U<<2。body末端是 `L-4`，尾Key占 `[L-4,L)`。

```text
无 suffix / 健康闭合尾                        → None
任意 partial HeadLenUnits 1–3B               → 截到 B
HeadLenUnits 合法且 4 <= R < L-4             → 截到 B
L-4 <= R <= L+3，已有结构检查通过             → 仅补缺失 TailKey/Fence
非法长度 / 已呈现结构矛盾 / 超出单尾           → 拒绝，不改
```

在真实B及合法frame起点已确认后，任意1–3B HeadLenUnits前缀都存在合法U补全，直接截断，无需旧byte长度的低2bits检查或completion搜索。证明：n个LE前缀bytes的low值小于s=2^(8n)≤2^24；low≥7时取low，否则取low+s，两者均落在 `[7,2^26-1]`。这里只证明可丢弃残头，不猜原长度，也不接受非法的完整4B字段。这个简化依赖当前固定最大容量，后续若再加长度限制必须重审。

body未完成时无需猜Key、解码partial Trailer、求未知字段或重算payload。真正未知的结构随该帧丢弃。本方案将用户“其余问题截断”理解为其他未完成结构截断，并保留现行“已呈现结构损坏拒绝”；不将其扩大为吞掉已完整坏结构。

body已到 `L-4` 时，encoded TailLenUnits的4B已在 `[L-8,L-4)`。由于plaintext TailLenUnits必须等于HeadLenUnits=U：

```text
K* = LE_u32(encoded TailLenUnits) XOR U
   = LE_u32(encoded TailLenUnits) XOR (L >> 2)
```

这是唯一确定值，不是尝试Key，也不是未知completion求解；**不能XOR物理byte长度L**。encoded TailLenUnits本身可占全uint32，禁止在解码前限制高bits。检查已读encoded Trailer words不等于F，解码完整Trailer并按LE(U)验证原TrailerCRC、descriptor/reserved、长度/meta/padding约束、Key不等于F，以及最多3B已有padding。已有0–4B尾Key必须等于 `LE(K*)` 前缀；已有Fence必须等于原Fence前缀。通过后只追加 `LE(K*) || Fence` 的缺失后缀，最多8B，不重写footer/CRC/已有字节。完整Key时同样核对其值，不容许更换Key。随机选择过程无需保留，重建公式适用于全uint32；旧byte-length恢复模型使用 `[0,m]` 限制，不能替代本轮units/高位Key恢复资格，实际生产结果见§11。

| 现有阶段 | 动作前检查 | 结果 |
| --- | --- | --- |
| Header 0–3B / 未知 Header | 不属于末帧 | 拒绝，不初始化 |
| HeadLenUnits 1–3B | B是真实起点且地址合法；固定容量下任意前缀都有合法U补全 | 直接截到B；无需低2bits检查/completion搜索 |
| HeadLenUnits完整，body未完整 | 先验合法U再换算L，物理字节未到L-4；B是真实起点 | 截到B，不认证未知Trailer或内容 |
| body 完整，尾 Key 0–3B | K* 唯一重建，完整 TrailerCRC/结构/padding、已有 Key 前缀 | 保原 payload/meta/tag/ticket，补剩余 Key 和 Fence |
| 完整 FrameBytes，Fence 0–3B | 尾 Key/TrailerCRC/结构/padding、已有 Fence 前缀 | 保原记录，只补 Fence |
| 已完整结构字段矛盾 / TrailerCRC、Key、padding、Fence 错 | 属于已呈现结构损坏 | 拒绝，不能回退到更早好帧 |
| 任意阶段内容或 PayloadCRC 损坏 | Open 不计算 PayloadCRC | 按结构决定动作；结构保留的坏内容在 ReadFrame 拒绝 |

**PayloadCRC 对此结构分类与 Key 重建不是必要开销。** 正常 writer 前缀的成员证据来自 marker-free 编码；完整 body 的结构证据来自位置/长度、TrailerCRC 与闭合字段。CRC 不是数据真实性或未知 completion 的证明。

### 6.2 修改、再次终止与报告

- Truncated：一次 SetLength(B)，保留闭合 prefix，不追加字节。
- CompletedTail：仅追加确定的 1–8B Key/Fence 后缀，保留全部已有字节。
- None：不修改。

成功修改沿用现有一次 durable flush，再做最终结构尾验证，随后建立 reader/cache。同 handle；SetLength/write/flush 异常关闭并使打开失败，不换动作，不复用旧 cache；只读输入需要动作时拒绝且 bytes 不变。不追加断电恢复协议。

再次终止状态只有SetLength前/后，或闭合后缀每个byte prefix。Key重建始终基于同一HeadLenUnits/encoded TailLenUnits，重复恢复保留同一帧；无需撤回/改写CRC或中间flush协议。

建议新 profile 统一报告 CompletedTail；实施时将新枚举值追加到公共 enum，保留既有 CompletedFence/CompletedTombstone 的名称与数值，不将“补 Key”虚称为只补 Fence。OriginalLength/FinalLength/AffectedFrameOffset/FrameTicket 继续表达原物理结果。现有闭合墓碑读取、IsTombstone/showTombstone 和 Builder 取消语义保留；仅不再由恢复创建墓碑。

## 7. 文件级兼容与成本

Header 是唯一 profile 分派依据，不能用 EOF、试解码或 payload magic 选版本。

| 入口 | RBF1 | RBF3 |
| --- | --- | --- |
| CreateNew | 不创建 | 只创建新 profile |
| OpenReadOnlyExisting | 原布局完整结构主链 O(N)，不 eager 校验 PayloadCRC；旧 ticket/user bytes/容量保留，残尾拒绝 | 固定结构快开；需截断/补 Key/Fence 时拒绝 |
| OpenExisting | 修改前明确拒绝旧可写打开 | 结构快开、单尾截断/补闭合后缀，后续只写新帧 |

Header一次分派、两组具体wire布局：RBF1保留byte字段，RBF3是4B units；不要把旧字段原位重解释为U。生产Header=RBF3已与历史byte RBF2区分；RBF2及未知Header拒绝。本轮内部双读，不新增assembly、通用codec插件或公开长度模式。

转码每帧+4B会改变ticket及后继offset，因此不提供常规离线转码；保留旧字节直接保留旧ticket。mixed与下游适配另案。

仍有成本：写前线性预扫/bitmap、必要 XOR、读解码、每帧 +4B；健康 Open 固定结构读取，异常定位仍可扫描约 256MiB。[专项](rbf-codec-implementation-study.md)保留历史成本证据；本轮生产RBF3接入、Pool生命周期及Open的实际验证见§11，旧证据不替代新结果。模型字节上界不替代设备或时延测量。

## 8. 当前实证与历史证据

本节所有已有RBF2候选模型、成本原型与wire快照都使用 **byte HeadLen/TailLen**，包括高位Key和缺Key代数验证。它们支持算法、成本与结构/内容分工的论证；units格式的TrailerCRC、所有切断点及再次恢复仍须用新codec/独立向量验收。不得直接重标旧hash、覆盖快照或把候选名称相同当作wire相同。

历史基线复现命令（针对5711c47源码，不在本轮新生产源码上重测或用于RBF3资格）：

```text
python -B experiments/RbfFastOpen/run_probes.py --assert-baseline-5711c47
```

runner 串行执行三个字节模型、真实子进程终止探针、RBF1 黄金 fixture 互证、生产探针 Release build/run。详细产物与限制见 [实验说明](../../experiments/RbfFastOpen/README.md)。

2026-10-02 尾恢复模型结果记入 [tail-key-structural-open-5711c47-v2.json](../../experiments/RbfFastOpen/results/tail-key-structural-open-5711c47-v2.json)，含 revision、SDK、七个实验源码 hash、wire hash；该资格模型仍限制Key在 `[0,m]`，下列数字不验收全uint随机Key的恢复。通过 5226 cuts、4926 截断、240 次补尾（含 120 个 Key 0–3B 阶段）、11232 再次恢复状态；20 类结构坏输入拒绝（含 CRC 自洽的非法 descriptor）、22 个内容 CRC 正交场景通过；补充 Key=301 / 非零 encoded padding 的 1241 个前缀，以及高位 Key 的 padding 相位结构向量。真实 child kill 为 181 次，四个生产 RBF1 黄金 fixture、原 Open 指标和 Fence 轨迹保持，独立生产探针 Release build 零警告/错误。验收边界如下：

| 证据 | 要证明的事实 | 限制 |
| --- | --- | --- |
| 正常 writer 前缀模型 | 单份尾 Key、全 body marker-free、CRC 方向、已闭合墓碑；旧 64 真/64 伪平行链 | 实验 RBF2，不是生产 codec |
| 结构读取计量 | 健康打开逻辑读取≤39B，对 N/L 独立；近最大长度用稀疏逻辑 fixture，不分配整帧 | 合成大帧只验证结构读取，不冒充完整 payload 黄金向量或设备 I/O |
| 单尾资格 / 再次恢复 | 所有正常 cuts、尾 Key 重建/补 Fence、不读内容作资格、只读无修改拒绝 | 无 oracle，不认证手工任意镜像 |
| CRC 职责分离负例 | 内容/PayloadCRC 损坏不阻止结构 Open/恢复，完整参考 ReadFrame 拒绝；结构损坏仍拒绝 | 纯模型；生产实施另验 |
| 真实 child kill | 实际 append/truncate/Key+Fence 后缀每阶段重读、再次恢复和幂等复开 | Python 模型+文件操作，READY 检查点；不是 Windows syscall 内终止或断电验收 |
| 双向生产 RBF1 互证 | 四 fixtures 与旧 Open requests/bytes、普通读/Fence/boundary 职责 | 历史 RBF1 生产基线，不是 RBF3 快开验收 |

两份历史快照原样保留：[baseline-5711c47.json](../../experiments/RbfFastOpen/results/baseline-5711c47.json) 是墓碑补写政策，[process-termination-truncate-5711c47.json](../../experiments/RbfFastOpen/results/process-termination-truncate-5711c47.json) 是双 Key/末帧完整 CRC/不补 Key 政策。旧布局、hash、计数不作为当前门禁，也不与新源码比对。

PayloadCRC forward/LE、TrailerCRC backward/BE 由生产黄金向量和完整 wire 互证；原 forward Trailer 模型已撤回。compat strict_read 是全镜像内容裁判，比普通结构 Open 更强。

专项成本证据独立保留在 [codec-cost-5711c47-20261002.json](../../experiments/RbfCodecCost/results/codec-cost-5711c47-20261002.json)，不改上述历史快照。模 uint32 加减法同样具存在性与唯一尾 Key，但非对齐 meta 要扩 word、padding 读完整4B word令理想健康上界为40B；实测吞吐接近，暂继续 XOR。reader 默认 Vector 解码后使用现有 CRC，融合候选不进默认路径。

随机搜索新证据独立保留在 [zero-then-random-0e0df09-20261003.json](../../experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json)。完整实验读允许全域 Key（拒绝F），独立bitwise CRC/wire、高位 Key与缺 Key代数重建通过；既有 MetadataProbe/cache和旧恢复资格模型尚未按全域 Key扩展。185 CPU/20 I/O均为7轮转样本，入口 Environment.Samples=5是未参与专项的旧默认，快照已明确。真实最大帧验证不等于生产恢复；没有用少量未重试观察验证理论概率尾。

## 9. RBF 实施切片与验收状态

| 切片 | 已交付 | 主线程已运行的退出证据 |
| --- | --- | --- |
| G0：RBF合同 | 已完成：Header/Fence=RBF3、Head/Tail units、F≥2^26、byte API、结构/内容职责、旧可写拒绝、三动作及保留enum数值同步权威文档 | 新旧独立wire、结构/内容正交、残尾策略在670项测试和正式探针中覆盖 |
| G1：Data基础与codec资格 | 已完成：[Data基础方案§9](../Data/xor-escape-key-refactoring.md#9-基础切片实施结果)及RBF3 units codec、Append三spans、Builder fused与高位Key读取 | Data保留int byte域与全uint Key；新codec range先于shift、原LE(U) CRC、真实输入/取消/输出fault资格已运行，成本见§11 |
| G2：双读与纯新 writer | 已完成：RBF1只读与RBF3纯新writer；FrameInfo/cache/双向scan/boundary/meta按具体profile分派 | 6个独立fixtures×两cache共12views；真实Append/Builder完整wire由Python独立裁判验证；旧ticket/容量及所有读取入口由RBF测试覆盖 |
| G3：结构快开与单尾恢复 | 已完成：同handle定位/资格、截尾/补原后缀、flush/报告；I/O异常与实际进程kill | 658个真实writer cuts、4个Header拒绝、35次managed kill；9个健康Open请求36/37B，异常扫描覆盖跨度≤M+7；详见§11的范围限制 |
| G4：RBF源码资格 | 已通过：0999851对应源码内容的Release RBF闭包build及匹配 `--no-build` Rbf.Tests；提交后clean源码重建正式probe | 670/670通过，109份源码PDB checksum及正式snapshot/source/binary/artifact独立复核通过。其他项目/下游、pack/publish未纳入 |

G0/G2遵循[Decision-Layer约束](README.md#decision-layer-约束)。用户已明确授权本轮规范与实现同步；4B根决策只澄清物理L与wire U的表示关系，逆扫不读头等根语义不变。pack/publish与下游不属于本轮目标。

G0–G4交付与源码/测试/正式证据资格已记录于§11并独立复核。旧实验只作对照，不批量改写为新格式验收。

成本 workload：历史 N=1/1000/100000，末帧 coverage=0/4KiB/1MiB/近 M；全零/随机/密集 F1/F2/多禁 Key、meta=0/1/3/65535、跨 span/chunk；测 Append/Builder/完整随机读/FrameInfo/meta、健康 Open 与异常截尾/补尾，记录配置/cache、重复次数、请求/返回 bytes、分配/bitmap/延迟/吞吐。未控制 OS cache 不称冷盘。

正确性门禁：

- 旧容量、ticket、业务 tag/墓碑及两 CRC 方向；新 +4B、单 TailKey、marker-free 正常 writer、MaxLength/MaxOffset。
- U=7及U=2^26-1对应L=28及M；0..6、2^26及uint.MaxValue拒绝；`U=0x40000007`不能因unchecked左移别名为28而通过。raw U低2bits不作对齐限制。
- LE(U)作为TrailerCRC输入，encoded TailLen先XOR后验范围；高位Key及缺Key的 `encodedTailLenUnits XOR U` 独立向量。完整非法U拒绝，真实B后的任意1–3B残头截断；新profile不复用旧byte partial-head guard。
- Data的Fence边界2^26-1/2^26在空输入和真实来源上验证；随机Key==F仍拒绝，Copy/InPlace允许全uint Key。8MiB FullBitmap界仅在RBF域引用，默认策略不增加数组bitmap。
- 所有正常 writer 前缀与旧嵌套/平行假尾；不能从 EOF 猜缺 Key 帧起点；独立裁判不只由同一 writer/reader 互验。
- 未完成 body 截到唯一 B；完整 body 的 Key 0–3B / Fence 0–3B 仅补原后缀；结构矛盾先拒绝，首坏候选不 fallback。
- 正常/修尾/前驱内容坏：Open不验证PayloadCRC，不吞帧、不回退；普通ticket完整读每次两CRC，info复用创建时TrailerCRC/Key资格、每次完整读PayloadCRC，拒绝内容坏。资格不变依赖普通工厂只追加/不可Truncate与handle共享/生命周期/fault边界；离线共享Write另须冻结输入。
- 结构 Open 读字节对 N/L 独立，padding≤3B；异常 marker 定位可读 body，但不重算 PayloadCRC；近 M 声明截尾不分配/填充。
- SetLength/write/flush 异常关闭；资格失败/read-only 输入逐 byte 不变；cache 仅最终结构验证后建立。
- 生产进程 kill 覆盖 append、SetLength 前/后、Key/Fence 每个 prefix、再次恢复/幂等；有限 READY 点结合 writer 前缀证明，不宣称全部 syscall 时机已实测。
- Builder reservation/epoch/取消、pooled 生命周期、wire cache、跨 chunk 相位及 preview 保留；历史 framing 延迟检测与完整 Audit 分开。

本轮已形成实施记录；模型、RBF1生产互证、Python模型kill与RBF3生产managed kill各有证据边界，不互相替代。

## 10. 需求来源与简化裁决

| 要求 | 来源 / 效力 |
| --- | --- |
| Open 结构与 ReadFrame 内容正交 | 本轮用户目标；技术推演可行，替代末帧/前驱/残尾 PayloadCRC 资格 |
| 仅考虑补原 Key/Fence，其他未完成结构截断 | 本轮用户方向；真实 B + 完整 Trailer 可确定原 Key，无需 payload CRC |
| 保持约 256MiB 上限；逆读优先，研究移除头 Key | 用户明确；尾 Key 单份是本方案据存在性/前缀证明选择的机制，保留尾部导向决策 |
| HeadLen/TailLen以4B units存储，Fence≥2^26 | 用户接受两轮独立比较的收敛结论；统一raw长度与Fence分离，API/物理范围仍以bytes计 |
| 进程终止、单 RBF 文件、下游另案 | 先前用户决定持续有效 |
| 任意 binary、frame 原子性、内容坏不能返回有效数据、已呈现结构坏拒绝、现有墓碑读取 | 现行源码/规范/测试；内容拒绝移到 ReadFrame，不自动删除其他语义 |
| 旧读/ticket 保留 | 既有方案与 SizedPtr 持久凭据；文件级兼容保留 |

| 裁决 | 最小机制 / 触发 |
| --- | --- |
| simplify | 单 TailKey，固定开销 28B；Open 固定结构读取；三动作及最多 8B 闭合补写 |
| simplify | units连续长度域与F≥2^26合并raw HeadLen排Fence规则；删除新profile partial-head低2bits检查/completion搜索；只在wire边界转换 |
| delete | 头 Key、打开时 PayloadCRC/coverage 遍历、partial Trailer 解码/CRC 方程与可见内容矛盾分类 |
| delete | 恢复墓碑/补零/nonce/tag 合成、CRC/footer 改写、外部 oracle、未知 completion |
| keep | marker-free writer、两项 CRC 各归其责、完整 TrailerCRC 与 Key 前缀检查、真实 B/直接前驱结构、失败关闭与 DurableFlush |
| keep | 旧容量/ticket/墓碑 decoder，最大帧上限及对应最坏异常扫描 |
| defer | 下游/mixed、metadata/key 加强绑定、新格式离线 rescue；具体需求独立设计 |

Key预扫/bitmap、XOR/解码与历史W:成本保留专项；生产Append/Builder/pooled、warm Open与近最大异常扫描已在§11量化。冷盘、断电与任意syscall中断未验收；旧可写拒绝、历史损坏延迟发现与离线rescue范围仍是显式取舍。

## 11. 本轮最终实施与验收记录

2026-10-03源码提交 `0999851207978fe947ecabccb1093e5774d007df` 已同步RBF1/RBF3[接口](rbf-interface.md)、[格式](rbf-format.md)、[独立RBF3参考](rbf-test-vectors.md#8-rbf3独立向量与生产资格)、guide与导航。Header/Fence固定RBF3，Data基础00329fd的历史资格保留；以下记录主线程实际运行的新生产结果，源码/测试身份/正式证据独立复核已通过。

实施后的[代码设计审阅](rbf3-code-design-review-0999851.md)另行记录继承的入口状态缺口、公开串行合同、新诊断回归及具体枚举器改进；反例证据独立保留，建议修复尚未实施，不改写下述验收快照。

后续修复范围及验收由[审阅 finding 处理方案](rbf3-review-findings-disposition.md)指导：该方案已辩证收窄入口承诺并补入归还资格的小修复，尚未应用到生产源码。

### 11.1 证据身份

- 0999851对应源码树最终Release RBF闭包build成功（该日志0warnings/0errors），匹配 `--no-build` Rbf.Tests **670/670**通过、无跳过。运行发生在源码提交前的对应工作树，提交未改代码；日志/TRX：`W:/RbfFastOpen/rbf3-validation-1791001216923/{build-final.log,test-final.log,final.trx}`，TRX SHA256=`1f35acc487a9fcca6b14e89fd86bc5bc881c477378dcc3b7de1f0592a52f406c`。
- 最终测试PDB SourceLink仍标记提交前的 `ddb9c53`，并非0999851测试二进制元数据；独立复核将Rbf.Tests35/Rbf46/Data21/Primitives7共109份当前.cs SHA256与该测试闭包PDB源码checksum逐一对照，零缺项且全部吻合，确认测试执行的是0999851对应源码内容。
- 提交后clean源码重新Release构建正式probe成功，0errors、43个既有XML文档warnings；DLL ProductVersion由主线程核对为 `1.0.0+0999851207978fe947ecabccb1093e5774d007df`。不把局部增量build零warnings扩展为所有构建无warnings。
- 正式目录 `W:/RbfFastOpen/rbf3-formal-20261003`，不可变[生产快照](../../experiments/RbfFastOpen/Rbf3ProductionProbe/results/rbf3-0999851-20261003.json) SHA256=`be77425d2b0af11fe0a500d7a007dbbf1ca098e156fdfbf6419b6732531f2422`；`Quick=false`、`Accepted=true`。SDK10.0.201、runtime .NET10.0.5/X64，TieredCompilation与QuickJitForLoops关闭。
- runner前后同一commit、clean树及88份source hashes一致。独立复核确认probe11个binary、88份source、828个artifacts（共1796948366B）、manifest完整集合及snapshot身份全部吻合；主线程重新export全审。快照及历史RBF1/byte RBF2文件未重标。

### 11.2 正确性与终止边界

| 对象 | 实际结果与范围 |
| --- | --- |
| 独立wire与读取入口 | Python bitwise CRC32C/units oracle生成6fixtures，Off/Slots16共12views通过；覆盖caller/pooled、info/meta、双向scan、boundary及RBF1只读旧ticket。两份真实Append/Builder完整wire由独立Python裁判通过 |
| units/Key/CRC/旧容量/故障 | 670项RBF测试覆盖range先于shift、原LE(U) CRC、高位Key、ticket两CRC、info创建Trailer+每次Payload资格复用、旧容量与票据、可纠正Result/准备取消/真实输出fault及I/O异常 |
| 真实writer全byte前缀 | Append和Builder小帧各329cuts，共658；各None2/Truncated319/CompletedTail8。4个Header残留拒绝且不改，read-only不变、再次恢复None、内容CRC正交检查通过。该小帧helper只用一片/一个chunk；快照Scope中的chunked措辞不代表这些cuts已跨chunk，真实1MiB成本路径另覆盖owned chunks |
| 真实进程终止 | 35次managed child kill：Append12、Builder12、补尾9、SetLength前/后各1。READY检查点在实际生产输出阶段控制；结合marker-free顺序前缀证明，不称任意Windows syscall时机或断电验收 |
| 真实最大帧 | FrameBytes=268435452、payload+meta=268435424，其中payload=268369889、meta=65535；caller/pooled与phase1 meta通过。Python独立最大帧裁判只验Header/U/Key/Fence/decoded Trailer的12B字段及TrailerCRC，不冒充独立全payload CRC |

### 11.3 健康与异常Open

9个健康case覆盖N=1/1000/100000及末帧payload=0/1MiB/近M，readonly/writable均为36或37逻辑requested/returned bytes、3或4read calls；Header一次读取，健康合同上界39B成立，与N/L独立。7样本warm Open中位数范围93.8–158.1µs；未控制冷OS cache，不称SSD物理请求或冷盘延迟。

| 异常case | 动作 | 总RequestedBytes / calls | 单次观测时延 |
| --- | --- | --- | --- |
| 1MiB残尾 | Truncated | 1048632B / 20 | 15.1016ms |
| 近最大残尾 | Truncated | 268435476B / 4099 | 210.6466ms |

`M+7=268435459`是非重叠aligned Fence定位扫描的**覆盖跨度**上界，不是包括Header/资格在内的聚合请求上界。快照单独记录aggregate上界268435587B（M+135）及64KiB扫描块；上表聚合计数含Header/结构资格读取。异常两行是单次观测，不声称7样本异常时延分布。

### 11.4 实际接入成本与已知改进点

正式快照有48个性能rows，每行7样本；写入为真实buffered W: I/O，计入公开CRC/选Key/XOR/copy或chunks，DurableFlush另计。读为同ticket反复读取、warm OS/RBF cache，计入CRC/解码/Pool生命周期。以下是sample中位数，分配为sample分配中位数除以每sample帧数。

| payload / 内容 | Append µs/帧 | Builder µs/帧 | Append / Builder分配B/帧 |
| --- | ---: | ---: | ---: |
| 4KiB / Zero成功 | 47.0666 | 25.0298 | 0 / 280 |
| 4KiB / marker迫使非零Key | 22.4724 | 24.9116 | 0 / 280 |
| 1MiB / Zero成功 | 1376.6063 | 2513.6312 | 0 / 2200 |
| 1MiB / marker迫使非零Key | 1483.5438 | 2731.4688 | 0 / 2200 |

4KiB+meta3的FrameBytes L=4128B，加后续Fence的physical write span为4132B，`L+4`超过small 4096B门槛：源码Key0 Append分head/payload/meta/tail四次write，非零Key在该尺寸走scratch一次合写。47.07µs对22.47µs的观测差异与此分支差异一致，但探针未单独计每帧write-call或拆分因果，不能把全部差值归因于syscall，更不能推导“XOR更快”或内容无关排名。Builder零内容首sample有池扩张：4KiB sample分配1179672B（后续1146880B/4096frames），1MiB首sample166320B（后续35200B/16frames）；保留原始样本，不以warm中位数宣称Builder零分配。

| 4KiB Zero内容读取 | Off / Slots16 µs/帧 | 分配B/帧 |
| --- | ---: | ---: |
| ReadFrame caller | 8.4994 / 3.0445 | 0 |
| ReadPooledFrame | 8.5445 / 3.3980 | 56 |
| ReadFrameInfo | 6.5139 / 0.4047 | 0 |
| ReadTailMeta | 11.7326 / 0.8098 | 0 |
| ReadPooledTailMeta | 11.7937 / 0.8809 | 48 |

其余marker/1MiB/cache行及7个raw样本保留快照，不另复制测量authority。多span Key0输出次数与Builder当前分配是已知后续改进点，本轮不新增优化工作，不设全局最佳或速度通过声明。下游编译/test、整仓solution、pack/publish、冷盘、设备断电与任意syscall终止均未纳入；源码/测试/正式证据通过不扩大这些范围。
