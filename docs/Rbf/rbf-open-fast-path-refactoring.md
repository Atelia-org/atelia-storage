---
title: "RBF 普通打开快路径重构方案"
status: "Accepted refactoring plan / implementation pending"
normative: false
---

# RBF 普通打开快路径重构方案

日期：2026-10-01。源码基线：`5711c4706509eb3b144299a75f83c09aa75da75f`。这是实施前方案；只修改方案与实验，未修改生产实现或现行 RBF1 规范。`RBF2` 是候选名称，尚未分配生产格式版本。实施按 §9 同步规范、源码与验收。

2026-10-02 补充：[writer / reader 实现专项](rbf-codec-implementation-study.md)完成 C# codec 成本原型和 W: 真实读写。推荐 Append 有界输出、Builder owned chunks 原地编码、wire cache/caller 解码；生产接入仍待实施。搜索策略随后由下述新实验更新。

2026-10-03 更新：专项新增 **ZeroThenRandom**。先检查 EscapeKey=0，失败后用系统随机源抽取全 uint32 候选（排除0/Fence），完整检测通过后采用；无 bitmap 循环版成为实施主候选，固定1与两次随机后 bitmap 版保留为实验对照。185条CPU、20条W: I/O的7样本证据见[专项结果](../../experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json)。随机搜索没有确定的尝试次数上限；31B反例、概率尾及尚未验证的元信息/恢复入口见专项。该更新不改变恢复动作。

同日追加小帧快速路径：Zero失败且 **EscapePayload≤256B** 时用一个 `ulong` bitmap选择最小可用键，超过阈值仍走上述随机循环。EscapePayload是前一条Fence与尾EscapeKey之间的区间，包含raw HeadLen，长度`L-4`；实际编码body最多252B/63words，64候选保证有空位。CPU实测、跨chunk与边界资格见[专项§3.2](rbf-codec-implementation-study.md#32-小帧用一个-ulong-作为完整-bitmap)。这是writer内部算法选择，不改变wire、reader或恢复规则。

基础实现另见 [ZeroThenTinyBitmapRandom重构方案](../Data/xor-escape-key-refactoring.md)：两轮辩证审查选择Data窄入口、内部共享选键/XOR核；Append至多三个借用spans，Builder对reservation后全部已写bytes一次选键并原地变换，不公开source/view/visitor。Data基础已在 `00329fd` 同时落地两种真实输入并完成行为/成本资格；新RBF Builder接入仍待后续切片，不改变本文件的格式/恢复规则。

2026-10-03 最终长度裁决：用户接受两轮独立审查收敛的 **HeadLen/TailLen以4B为单位、Fence≥2^26**。令物理FrameBytes长度为L bytes，wire字段存 `U=L>>2`，Fence下界是 `0x04000000`（67108864）。Data选键入口同步限定到该Fence范围，EscapeKey仍保留完整uint32值域。RBF候选Fence=0x32464252满足约束；不再附加 `Fence & 3 != 0` 规则。本文§4是新长度编码的方案依据，Data不拥有长度字段。

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
| Payload / TailMeta / PayloadCRC | 不校验 PayloadCRC，不读取内容作资格证明 | 两项 CRC 必验，失败不能返回有效数据 | 元信息扫描不替代全 payload Audit |
| 未访问历史结构 | 不 eager 检查 | 只检查所选帧 | 全链扫描 / Audit 才覆盖全部 |
| 历史 Fence 损坏 | 可延迟发现 | ReadFrame 不检查 ticket 外 Fence | boundary / 跨界扫描检查 |
| 主序列成员身份 | 正常 writer 的 marker-free 前缀约束建立尾边界 | 任意 ticket 局部合法不等于主链成员证明 | 从 Header 连续正扫可建立全链证据 |

可观察变化：末帧或直接前驱的 payload / PayloadCRC 损坏而结构完好，Open 可以成功；若尾闭合后缀缺失，也可只补 Key/Fence，原损坏内容保留，随后 ReadFrame 拒绝。不能声称 Open 证明“末帧内容完整”，也不能因内容损坏回退或截掉结构完整的帧。

完整 A/B/C 中只损坏 A 后的 Fence，尾 C 及其左边界仍健康，新 profile 的局部结构 Open 和普通 ReadFrame(A) 均可成功，GetScanBoundaryAfter(A) 才拒绝；RBF1 的全结构主链 Open 仍拒绝该 Fence 损坏。§8 的 RBF1 生产探针隔离验证了普通读与 boundary 的职责，不是已实现 RBF2 快开。

本方案只定义单文件物理分帧、读写与尾部恢复。RBF 双读、单新 writer；mixed、下游适配、可插拔 codec、sidecar/frontier 与业务引用图转码另有明确需求再设计。

## 2. 当前实现与旧格式的证据

[RbfTailRecovery.Open](../../src/Rbf/Internal/RbfTailRecovery.cs) 无条件从 Header 循环按 HeadLen 前进，检查长度、TrailerCodeword、descriptor、padding、HeadLen/TailLen 与 Fence，然后完整校验最后相关帧。可写、只读以及 Action=None 均执行。历史 payload 不全部读取，但每个已闭合帧约有 24–27B 小范围读取与对应调用成本。

当前实现直接进行每帧 3 次小读，有 padding 时再多一次；工厂资格读取发生在正式 cache 建立之前。[生产探针](../../experiments/RbfFastOpen/ProductionProbe/Program.cs) 实测零 payload、无 padding、`CacheMode.Off` 的只读打开：

| 历史帧数 N | Read / RawRead calls | RawReturnedBytes |
| ---: | ---: | ---: |
| 1 | 8 | 56 |
| 100 | 305 | 2,432 |
| 10,000 | 30,005 | 240,032 |

该 fixture 为 `3*N+5` calls、`24*N+32` bytes，包含 Header 和末帧资格读取，不含创建与后续用户读取；它是同步读取请求/返回字节证据，不是设备物理 I/O、冷盘延迟或新格式性能排名。

当前 solution 基线因仓内其他项目的旧工厂调用存在编译缺口。独立生产探针只构建 `src/Rbf` 依赖闭包，不能称为 solution 通过；其他项目的修复不作为本 RBF 方案的任务或退出门禁。

父提交 `bf7d68a` 的 RBF 工厂仅检查最小长度、4B 对齐与 Header。现循环是 `5711c47` 新增，并同步写入 [接口规范](rbf-interface.md) 的 MUST 条款。若采用候选方案，需要正式修改该条及相关验收，不能暗中跳过它。

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

候选 Header / Fence 为 `RBF2`，其 LE u32 值记为 `F=0x32464252`：

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
| Fence F | uint范围 `2^26..uint.MaxValue`；候选 `0x32464252`，最终生产profile标识按G0确定 |
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

Builder目前已整帧缓冲：BeginAppend的HeadLen reservation持续pending，SinkReservableWriter.FlushCommittedData在首pending处停止，EndAppend提交后逐chunk Push。Data基础方案改用具体 `XorEscapeSinceReservationEnd(headToken,fence)` 成员：在完整plaintext CRC/Trailer之后、raw Key/Fence追加之前，对reservation后全部已写body一次选键并原地XOR，再由原sink Commit/Push。没有外部visitor、可逃逸view或byteCount；内部cursor保持carry/相位，不复制整帧。全部Result/借用/参数拒绝置于首次padding/footer修改前；从该首次准备修改到Commit前的异常取消/Reset，禁止同一Builder重试；实际输出异常永久fault。具体Data方法及owned chunks资格已完成；RBF取消路径和真实新Builder端到端成本仍待接入。

| 接点 | 实施边界 |
| --- | --- |
| 工厂 / Open | Header 一次分派；结构资格、修改、最终 reader/cache 同 owned handle |
| Append / Builder | 完整 CRC/footer 后选 Key；尾块含 Trailer/Key/Fence；前置 Result 失败不写，输出/flush 异常永久 fault |
| Builder pending bytes | 唯一有效HeadLen、无未Advance借用、PushedLength=0；完整plaintext footer后fused选键/XOR，rawKey/Fence后置；无callback/Push的Data操作不新增visitor guard；首次padding/footer修改起的预发布异常取消当前Builder |
| wire cache | 保留 encoded bytes，只在调用方 buffer 解码，不修改共享 cache |
| FrameInfo / Reverse | 保存内部 TailKey、offset/相位；尾块解析，无头 Key，也不增加公共 ticket 版本 |
| ReadFrame / pooled / info 快路径 | 原 user bytes；两项 CRC 必验，禁止因 Open 结构成功跳过内容校验 |
| TailMeta / preview | 非对齐起点、跨 chunk、零 meta；维持只校验 TrailerCRC 的原职责，不物化整帧 |
| Forward / boundary | 从 HeadLen 跳尾解析；boundary 内容见证及指定 FrameBytes/Fence 验证保留 |
| 离线工具 | 各入口单独 Header 分派；工厂分派不自动覆盖工具 |

普通 Append 只有总长≤4096B 时合为一次 Write，大帧走首/mid/尾多次 Write；Builder 逐 chunk Push。合写 Trailer/Key/Fence 可减少 syscall，不作为不可中断的事务边界。[.NET 10 Unix WriteAtOffset](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.Unix.cs#L89-L130) 明确处理单次正数短写并继续剩余 bytes。因此一次 API 调用本身不能证明不存在部分输出；这里没有声称已实测 Windows syscall 内 kill。

补尾 Key/Fence 仍有确定且便宜的恢复路径（§6），保留它不需要 payload 读取；不以“似乎不容易撕裂”删除此前提。XOR 是 framing 编码，不是加密；更强 metadata/key 绑定另案。

离线支持矩阵：普通读/forward/reverse/boundary/meta 两 profile 必须覆盖；RbfRecovery.OpenReadOnly/Fence search、RollingCrc Trailer search、TruncateToSuggestedTail 对 RBF1 保留原语义，首切片对 RBF2 显式拒绝。未来 rescue 按具体需求独立设计，不引入 scanner 插件框架。

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

这是唯一确定值，不是尝试Key，也不是未知completion求解；**不能XOR物理byte长度L**。encoded TailLenUnits本身可占全uint32，禁止在解码前限制高bits。检查已读encoded Trailer words不等于F，解码完整Trailer并按LE(U)验证原TrailerCRC、descriptor/reserved、长度/meta/padding约束、Key不等于F，以及最多3B已有padding。已有0–4B尾Key必须等于 `LE(K*)` 前缀；已有Fence必须等于原Fence前缀。通过后只追加 `LE(K*) || Fence` 的缺失后缀，最多8B，不重写footer/CRC/已有字节。完整Key时同样核对其值，不容许更换Key。随机选择过程无需保留，重建公式适用于全uint32；旧byte-length恢复模型仍使用 `[0,m]` 限制，生产接入须新增units、高位Key的资格/前缀反例，不能冒称本轮完成恢复验收。

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

| 入口 | RBF1 | RBF2 |
| --- | --- | --- |
| CreateNew | 不创建 | 只创建新 profile |
| OpenReadOnlyExisting | 原布局完整结构主链 O(N)，不 eager 校验 PayloadCRC；旧 ticket/user bytes/容量保留，残尾拒绝 | 固定结构快开；需截断/补 Key/Fence 时拒绝 |
| OpenExisting | 修改前明确拒绝旧可写打开 | 结构快开、单尾截断/补闭合后缀，后续只写新帧 |

Header一次分派、两组具体wire布局即可：RBF1的HeadLen/TailLen仍是bytes，新profile是4B units；不要把旧文件的字段原位重解释为U。历史实验曾用RBF2候选标识加byte字段，这些实验文件不能作为新units生产文件或兼容承诺，G0须明确区分。是否另拆assembly是独立交付选择，本次长度裁决不要求新项目、通用codec插件或公开长度模式。

转码每帧+4B会改变ticket及后继offset，因此不提供常规离线转码；保留旧字节直接保留旧ticket。mixed与下游适配另案。

仍有成本：写前线性预扫/bitmap、必要 XOR、读解码、每帧 +4B；健康 Open 固定结构读取，异常定位仍可扫描约 256MiB。[专项](rbf-codec-implementation-study.md)已有 C# 预处理/解码、W: Append/完整读/FrameInfo/meta 和真实最大帧证据；新 Builder 接点、Pool 生命周期、生产新 profile 及冷热 Open 仍待实施。模型字节上界不替代这些成本证据。

## 8. 当前实证与历史证据

本节所有已有RBF2候选模型、成本原型与wire快照都使用 **byte HeadLen/TailLen**，包括高位Key和缺Key代数验证。它们支持算法、成本与结构/内容分工的论证；units格式的TrailerCRC、所有切断点及再次恢复仍须用新codec/独立向量验收。不得直接重标旧hash、覆盖快照或把候选名称相同当作wire相同。

从仓库根运行：

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
| 双向生产 RBF1 互证 | 四 fixtures 与旧 Open requests/bytes、普通读/Fence/boundary 职责 | 当前 RBF1 生产源码，不是新 RBF2 快开 |

两份历史快照原样保留：[baseline-5711c47.json](../../experiments/RbfFastOpen/results/baseline-5711c47.json) 是墓碑补写政策，[process-termination-truncate-5711c47.json](../../experiments/RbfFastOpen/results/process-termination-truncate-5711c47.json) 是双 Key/末帧完整 CRC/不补 Key 政策。旧布局、hash、计数不作为当前门禁，也不与新源码比对。

PayloadCRC forward/LE、TrailerCRC backward/BE 由生产黄金向量和完整 wire 互证；原 forward Trailer 模型已撤回。compat strict_read 是全镜像内容裁判，比普通结构 Open 更强。

专项成本证据独立保留在 [codec-cost-5711c47-20261002.json](../../experiments/RbfCodecCost/results/codec-cost-5711c47-20261002.json)，不改上述历史快照。模 uint32 加减法同样具存在性与唯一尾 Key，但非对齐 meta 要扩 word、padding 读完整4B word令理想健康上界为40B；实测吞吐接近，暂继续 XOR。reader 默认 Vector 解码后使用现有 CRC，融合候选不进默认路径。

随机搜索新证据独立保留在 [zero-then-random-0e0df09-20261003.json](../../experiments/RbfCodecCost/results/zero-then-random-0e0df09-20261003.json)。完整实验读允许全域 Key（拒绝F），独立bitwise CRC/wire、高位 Key与缺 Key代数重建通过；既有 MetadataProbe/cache和旧恢复资格模型尚未按全域 Key扩展。185 CPU/20 I/O均为7轮转样本，入口 Environment.Samples=5是未参与专项的旧默认，快照已明确。真实最大帧验证不等于生产恢复；没有用少量未重试观察验证理论概率尾。

## 9. RBF 实施切片与退出门禁

| 切片 | 交付 | 退出证据 |
| --- | --- | --- |
| G0：RBF 合同 | 确定与历史实验区分的生产profile标识；锁定Head/Tail units、F≥2^26、byte API；结构/内容职责、旧可写拒绝、三动作和报告enum、进程终止模型 | §4长度换算/CRC覆盖与§6重建公式进入规范差异清单；两profile的内容CRC均留ReadFrame，RBF1保完整结构主链；结构坏拒绝；未完成body截尾，完整body补Key/Fence |
| G1：Data基础与codec资格 | [Data基础方案§9](../Data/xor-escape-key-refactoring.md#9-基础切片实施结果)的Zero/tiny/random、F≥2^26、三spans与具体writer fused能力已完成；新units codec与RBF Builder/高位Key读入口资格仍待做 | Data基础已通过真实三spans与pending chunks、独立scalar/成本/ownership资格；保持int body域、全uint Key，不承担长度wire，不需额外整帧复制；不能把基础完成写成整个G1/G2完成 |
| G2：双读与纯新 writer | 同步interface/format/新units向量/容量；两组具体序列化/解析、Append/Builder、FrameInfo/cache/scan/boundary/meta/profile | 独立LE(U)/CRC黄金向量、range先于shift、全uint Key与所有读取入口通过；旧ticket/最大容量/墓碑保留，新user bytes round-trip，入口支持或显式拒绝；G3前不交付新可写Open |
| G3：结构快开与单尾恢复 | 同 handle、定位/资格、截尾/补闭合后缀、flush/报告；实际生产 I/O 异常与进程 kill | 无 oracle/内容 CRC 资格/墓碑补写；健康读取对 N/L 独立，异常扫描≤M+7；再次恢复完整 |
| G4：RBF 源码验收 | 主线程独立 review，Release 构建 RBF 依赖和匹配 RBF.Tests | RBF 测试通过；其他项目/下游设计/包交付独立，包交付按既有 smoke 与会话授权 |

G0/G2 遵循 [Decision-Layer 约束](README.md#decision-layer-约束)。尾 Key 保留逆扫不读头的固定决策；本轮未修改规范。用户已授权提交文档与专项实验；方案不自动成为规范，也不扩展为打包或发布授权。

Data基础切片已完成，下一实施回合进入RBF的G0/G2：wire单位已经确定，落实生产profile标识、规范与units codec，再按既定取消/输出边界接入Append/Builder。RBF在新units向量与全部读入口完成前不能启用新writer；结构Open/恢复仍由G3单独验收。旧实验只作对照，不先批量改写来伪造新格式验收。

成本 workload：历史 N=1/1000/100000，末帧 coverage=0/4KiB/1MiB/近 M；全零/随机/密集 F1/F2/多禁 Key、meta=0/1/3/65535、跨 span/chunk；测 Append/Builder/完整随机读/FrameInfo/meta、健康 Open 与异常截尾/补尾，记录配置/cache、重复次数、请求/返回 bytes、分配/bitmap/延迟/吞吐。未控制 OS cache 不称冷盘。

正确性门禁：

- 旧容量、ticket、业务 tag/墓碑及两 CRC 方向；新 +4B、单 TailKey、marker-free 正常 writer、MaxLength/MaxOffset。
- U=7及U=2^26-1对应L=28及M；0..6、2^26及uint.MaxValue拒绝；`U=0x40000007`不能因unchecked左移别名为28而通过。raw U低2bits不作对齐限制。
- LE(U)作为TrailerCRC输入，encoded TailLen先XOR后验范围；高位Key及缺Key的 `encodedTailLenUnits XOR U` 独立向量。完整非法U拒绝，真实B后的任意1–3B残头截断；新profile不复用旧byte partial-head guard。
- Data的Fence边界2^26-1/2^26在空输入和真实来源上验证；随机Key==F仍拒绝，Copy/InPlace允许全uint Key。8MiB FullBitmap界仅在RBF域引用，默认策略不增加数组bitmap。
- 所有正常 writer 前缀与旧嵌套/平行假尾；不能从 EOF 猜缺 Key 帧起点；独立裁判不只由同一 writer/reader 互验。
- 未完成 body 截到唯一 B；完整 body 的 Key 0–3B / Fence 0–3B 仅补原后缀；结构矛盾先拒绝，首坏候选不 fallback。
- 正常/修尾/前驱内容坏：Open 不验证 PayloadCRC，不吞帧、不回退；完整 ReadFrame/ReadPooledFrame（含 info 快路径）始终拒绝内容坏。
- 结构 Open 读字节对 N/L 独立，padding≤3B；异常 marker 定位可读 body，但不重算 PayloadCRC；近 M 声明截尾不分配/填充。
- SetLength/write/flush 异常关闭；资格失败/read-only 输入逐 byte 不变；cache 仅最终结构验证后建立。
- 生产进程 kill 覆盖 append、SetLength 前/后、Key/Fence 每个 prefix、再次恢复/幂等；有限 READY 点结合 writer 前缀证明，不宣称全部 syscall 时机已实测。
- Builder reservation/epoch/取消、pooled 生命周期、wire cache、跨 chunk 相位及 preview 保留；历史 framing 延迟检测与完整 Audit 分开。

完成相应生产切片后才改为实施记录。模型、RBF1 生产互证、Python kill 各有证据边界，不互相替代。

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

Key 预扫/bitmap、XOR/解码与 W: 实验成本已有专项量化；剩余为生产接入后的 Append/Builder/pooled 成本、冷热 Open 与最坏异常扫描。旧可写拒绝、历史损坏延迟发现与离线 rescue 范围仍是显式取舍。
