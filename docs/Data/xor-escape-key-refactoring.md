---
title: "ZeroThenTinyBitmapRandom 实现重构方案"
status: "Proposed refactoring plan / dialectical review complete / implementation pending"
normative: false
---

# ZeroThenTinyBitmapRandom 实现重构方案

日期：2026-10-03；方案源码基线 `dc11774`。本轮只撰写、审查并修订方案，生产实现未开始。本方案为 [RBF 普通打开重构](../Rbf/rbf-open-fast-path-refactoring.md)的 G1 提供基础算法；原始成本证据见 [writer/reader 专项§3.2](../Rbf/rbf-codec-implementation-study.md#32-小帧用一个-ulong-作为完整-bitmap)。相邻方案来自同一调查，不能彼此充当独立需求证据。

采用 **Data窄入口、内部共享选择核和XOR核**：Append传入至多三个借用spans；Builder在具体writer内对reservation后的全部已写bytes选键并原地XOR。先尝试Key0，失败且待编码区间≤63words时，以一个ulong完整标记候选；较大区间循环抽取系统随机Key并检查。RBF负责body组成、CRC、长度字段、文件输出和恢复。没有公共可重放协议、可逃逸chunk视图或新增lease。

## 1. 需求账本与证据边界

| 要求 | 来源 | 当前消费者 / 边界 |
| --- | --- | --- |
| 采用ZeroThenTinyBitmapRandom，先形成实施方案并做辩证简化 | 本轮用户决定 | 本文；不授权本轮生产实现 |
| 优先评估src/Data，但分层代价高则不强行抽取 | 本轮用户方向 | 只有RBF是已知拟接入消费者，第二种协议尚未找到 |
| Zero优先，小帧完整ulong bitmap，大帧原随机循环 | 用户及已提交实验 | 现实验PrototypeCodec；生产仍为RBF1 |
| 任意二进制bytes，长度有限，禁止对齐Fence，raw EscapeKey也不能等于Fence | 已接受wire方案、实验和独立word存在性证明 | RBF新profile；不是加密或滑动byte序列过滤 |
| EscapePayload≤256B包含raw HeadLen，实际encoded body≤252B/63words | 用户区间定义与候选布局 | Data只看实际待编码区间，不接收FrameLength或假定HeadLen |
| 完整body包含payload/meta/padding/CRC/footer；输入冻结到发布完成 | 现实验、Append/Builder实际输入 | Append的少量spans；Builder的任意pending chunks |
| 无第二份整帧、无数组bitmap、保持跨chunk相位 | 用户性能目标、现原型与writer拓扑 | Span可以在任意byte切分，整体待编码区间按4B对齐 |
| reservation/Advance/Reset/Commit、单线程、取消/故障语义保持 | 当前Data/RBF源码及测试 | 不改变IReservableBufferWriter或IByteSink协议 |
| 最大RBF FrameBytes约256MiB、Open结构与ReadFrame内容CRC、单尾修复 | 先前用户决定 | 保留在RBF主方案，本基础切片不实施 |
| .NET10、Windows build/test串行、真实磁盘实验W: | AGENTS及用户 | 不打包、不发布、不改下游 |

未决的是实现分层和借用数据的接口成本，不是产品恢复策略或wire格式选择。本轮可用源码和接口小探针裁决，先不向用户增加技术确认问题。

## 2. 当前源码支持的接点

- [Data.csproj](../../src/Data/Data.csproj)没有RBF依赖；[Rbf.csproj](../../src/Rbf/Rbf.csproj)已经引用Data，无需新项目/包或反向引用。
- [RbfAppendImpl.Append](../../src/Rbf/Internal/RbfAppendImpl.cs)持有payload和tailMeta spans；新profile的完整footer可以在局部buffer完成。不能为调用公共API把它们复制成连续body。
- [RbfFileImpl.CommitFromBuilder](../../src/Rbf/Internal/RbfFileImpl.cs)在唯一HeadLen reservation阻塞下保存整帧，随后Commit发布。
- [SinkReservableWriter.GetCrcSinceReservationEnd](../../src/Data/SinkReservableWriter.cs)已有受token、唯一pending reservation、无未Advance借用约束的只读遍历；GetActiveChunks为private。
- [SinkReservableWriterCrcTests](../../tests/Data.Tests/SinkReservableWriterCrcTests.cs)覆盖跨chunks、旧token、Reset、多个reservation及未Advance。它们支持借用边界，不能代替新Key/XOR资格。
- [PrototypeCodec](../../experiments/RbfCodecCost/PrototypeCodec.cs)与[XorTransform](../../experiments/RbfCodecCost/XorTransform.cs)已有算法原型，但byte[][]不是生产输入协议；实验包装对象、策略enum、统计和重叠兼容分支不应直接搬入生产。

## 3. 选定的归属与最小公开面

在 `Atelia.Data.Binary` 增加一个 `XorEscape` 静态类型；同一assembly的 `SinkReservableWriter` 增加一个具体成员。依赖方向沿用Rbf→Data，BCL提供SIMD/span/CSPRNG，无新项目、第三方依赖或friend assembly。计划签名如下，最终源码公开前按§6完成两种输入资格：

```csharp
public static class XorEscape {
    public static uint SelectKey(uint fence, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second = default, ReadOnlySpan<byte> third = default);
    public static void Copy(ReadOnlySpan<byte> source, Span<byte> destination,
        uint key, int bytePhase = 0);
    public static void InPlace(Span<byte> bytes, uint key, int bytePhase = 0);
}

// Added to the existing concrete writer, not IReservableBufferWriter.
public uint XorEscapeSinceReservationEnd(int reservationToken, uint fence);
```

`SelectKey`将first/second/third按顺序视为一个逻辑区间，长度在Data内用long相加后校验，不复制输入、不要求各span分别4B对齐。三个spans正好容纳当前Append的payload、TailMeta和完整plaintext footer；单span/双span用default即可，不新增重载矩阵。它只读，返回键后调用方保持输入冻结，再借用有界输出逐段 `Copy`。

writer成员内部一次校验，选择键后变换owned bytes，返回键。它不回填、不Commit、不Push，不暴露spans/数组/池。选择所需的可重放来源只在Data内部：三个spans的ref struct来源、writer的具体值cursor可以共用internal generic selector；内部类型不是调用方协议，不增加公开 `ByteLength/Rewind/TryGetNext` 或委托callback。`InPlace`只是 `Copy(bytes,bytes,key,phase)` 的可读薄入口，不是第二变换实现。

不把原型byte[][]、PreparedFrame、策略enum、扫描统计、RNG配置或CRC融合搬入Data。此时复用来自一个与内容无关的操作，而非第二协议假设。若真实两种输入实测抵消既有收益，先修内部来源/窄入口；只有出现无法小改解决的反例才重新评估RBF内编排，不预先建设通用source框架。

Data不负责FrameLength、HeadLen、TrailerCRC、rawKey落点、文件Fence、Header/profile、SizedPtr、recover/flush。Data的fence参数是一个uint值；其返回键承诺也排除raw Key==fence，以便调用方直接存储键。任意fence与RBF固定大值的差异必须单独证明。

## 4. 算法与完整性契约

输入是连续逻辑byte序列，总长属于非负int值域且4B对齐，实际最大为 `int.MaxValue & ~3`。三spans入口和writer成员都先用long计算总长，再检查范围及对齐，之后才进入搜索/变换；不在Data限定RBF的256MiB容量。每次内部遍历必须返回相同bytes、长度和次序，允许空块或1–3B边缘；禁止在选Key与编码之间回填reservation、改CRC/footer或复用pooled buffer。

令F为fence，plaintext word按LE解释；word w仅禁止K=w XOR F，另外raw Key要求K!=F。Data不接受RBF布局长度，tiny阈值为实际body word数≤63；RBF映射 `EscapePayload=L-4≤256` 到 `body=L-8≤252`。

```text
若 F!=0 且完整body中不含F：返回0
若body words≤63：一遍完整ulong标记，确定返回可用键
否则：系统随机uint32候选，拒绝0/F；body不含F XOR K才返回
```

一般Fence的小帧规则：先把raw F禁用位纳入（F<64时置bit F），再标记0..63。最多m+1≤64个禁值，0..64总有可用键。若64bits全满则返回64；此时F必在0..63，而且64不可能再被body禁止，否则会需要至少64个body words。可直接利用 `TrailingZeroCount(0UL)==64`，无第二bitmap或随机fallback。RBF的F=0x32464252不在0..63，因此原策略仍只返回0..63。F=0时Key0本身不可用，跳过Zero检测而直接进入后续分支。

标记前保留 `candidate<64` 检查，避免C# shift对64取模。各pass共享word装配/carry和SIMD Contains核；tiny热循环保持局部ulong，每chunk写回一次，不以逐word回调处理标记。

大帧均匀独立抽样来自 `RandomNumberGenerator.Fill(stackalloc byte[4])` 并按LE解释，成功前完整检查冻结输入；随机性只影响耗时，正确性取决于检测。总words最多536870911，body禁键加raw F禁键远少于uint32值域，存在键；“长度有限”本身不是充分证明，若放宽为无界long，约16GiB包含所有uint32words就没有可用键。无确定重试/时间上限，无确定性wire、无最小非零Key保证；不加入预算/fallback配置。约256MiB RBF输入的单次失败概率<1/64来自既有专项，任意Data输入长度不能沿用这个数值。

XOR是LE key四bytes的周期流，bytePhase只取0..3；跨chunk由调用方累计相位。Copy只支持分离buffer或精确原地alias，拒绝shifted overlap并在修改前完成长度/phase校验；仅写source.Length前缀。Key0的InPlace为空操作，Copy仍复制。编码与解码共用同一操作；不引入Encode/Decode双重authority、CRC融合、模加法或向量模式公开enum。

无效长度/phase/overlap用参数异常，writer已Dispose用ObjectDisposedException，token/借用状态不合法用InvalidOperationException；全部可预见拒绝在修改前完成。RNG故障正常传播，发生于XOR之前；writer内转换若意外异常，不保证回滚部分已变换bytes，也不自行Reset/Commit/标记RBF fault。调用方按其owner语义丢弃或重建该buffer，RBF规则见§5。Foundation不以返回Failure来假装合法输入“找不到键”。撞禁候选可以提前停止；只有完整消费内部确定的范围且长度/carry闭合后才能成功。

## 5. Builder完整后缀与RBF接入边界

writer成员在入口确定 `[reservation.LogicalOffset + reservation.Length, writer.Length)` 的完整已写后缀，先验证disposed、有效且唯一pending token、无未Advance借用、派生长度int范围及4B对齐。以当前written-end为本次内部扫描终点，不纳入池buffer未写容量；前缀和reservation本身不变。Data可以处理已有Push前缀的writer，不把RBF要求的 `PushedLength==0` 变成Data通用限制。

同一个内部范围完成搜索和变换；每pass cursor独立，不把接口枚举器引用当作值cursor副本。当前 `GetActiveChunks()` 返回IEnumerable，`SlidingQueue`的显式接口入口存在struct→IEnumerator转换，不能据此保证无分配。新遍历用具体struct enumerator或直接索引；必要时CRC复用同一内部范围，但不改其签名/结果，不顺带重构整个writer的Flush/回收。

不提供byteCount或可写view。合法新RBF顺序固定为：

1. 完成epoch、长度/meta/offset、有效且唯一HeadLen token、无未Advance借用及 `PushedLength==0` 等全部可预见Result/参数/state检查；用户payload reservation已回填并Commit。可纠正拒绝保留同一Builder重试。
2. 补明文padding，按原覆盖算CRC，追加并Advance完整plaintext CRC/Trailer，**暂不追加raw Key/Fence**。
3. 调用 `XorEscapeSinceReservationEnd(headToken,fence)`，完整后缀恰是body。内部选键并原地变换，不Commit/Push。
4. 追加raw Key/Fence，再回填HeadLen并Commit；正常返回后推进TailOffset。

第3步没有需要截取子范围的当前消费者；闭合字段误编码的反例依赖提前写入raw Key/Fence，已不属于合法新顺序。因此删除byteCount，避免调用方误传body前缀而漏编码余部。未来真实需求要求已写后缀的子区间时再设计，不留占位配置。

fused操作同步、单线程，没有用户callback、Trace、Push/Commit或可逃逸view，扫描与转换之间没有合法重入点；不用visitor专用访问guard、新lease/generation或锁。存量span/Memory的调用方仍须遵守原有借用终止规则，不能因readonly参数便宣称bytes物理不可变。

**RBF的取消边界从进入首次padding/footer追加开始，到调用Commit前。** 该区间任何异常均取消并Reset当前Builder后抛出，禁止旧Builder继续重试；不要求逆XOR回滚。具体trace：大于tiny阈值且禁Zero的payload→已追加padding/CRC/Trailer→RNG或扫描抛异常→若允许retry，`writer.Length-HeadLenSize` 会把旧footer算作payload，再追加第二份footer。这个污染在第一次XOR之前已经成立。进入Commit/Push及后续flush后的输出异常沿现有共享fault停止实例，清buffer不能解除fault。

后续Append也必须按真实输出边界处理异常：[当前Append](../../src/Rbf/Internal/RbfFileImpl.cs)用包住整个RbfAppendImpl的catch-all标fault；不能直接把未来RNG预处理塞进同一catch，误把尚未输出的故障当write fault。wire cache仍只在caller buffer解码，元信息phase由RBF计算。这里是后续接入要求，本轮不改变当前RBF1行为。

## 6. 一个基础实施切片与验收

基础切片同时交付两种实际输入：Data选择/XOR核、固定三个借用spans入口、具体writer fused成员、Data.Tests及实验对照。可以先做局部接口资格再完成实现，但不先交一个不能用于Builder的公开平台，不拆成独立“Data核心”和“未来来源”两次交付。

预期修改：`src/Data/Binary/XorEscape.cs`（必要时按搜索/变换拆内部文件）、`src/Data/SinkReservableWriter.cs`的具体能力与内部cursor、`tests/Data.Tests`正确性/borrow/failure资格、`experiments/RbfCodecCost`对照入口与新证据。沿用Data已存在的Tests friend；测试专用候选/故障注入保持internal，不公开可配置RNG或新增实验friend。真实Append三个span与真实writer多chunks必须在公开面冻结前均可调用，无第二份整帧。

| 退出门禁 | 验证内容 |
| --- | --- |
| 单一选择/变换authority | 多pass共享内部word/carry/Contains与tiny核；Copy/InPlace共用一份XOR核，生产只有默认策略 |
| 当前真实输入 | stack/borrowed三spans与实际SinkReservableWriter多chunks，不以实验byte[][]替代接点 |
| ownership与修改范围 | invalid输入不修改；writer操作不Push、不Commit、不推进Length/PushedLength；reservation/前缀与池归还次数保持 |
| 独立正确性 | LE scalar/key oracle、wire/CRC裁判和下面的反例；不只由同一writer/reader互验 |
| 成本 | 在相同工作量下比较当前策略与Data入口，记录真实cursor/跨assembly/Pool成本，不能只测内核 |
| 旧行为 | Data现有reservation/CRC测试与匹配RBF.Tests通过，当前RBF1格式/容量/票据不变 |

完成基础切片后，RBF主方案G1/G2接入§5顺序，并验证首次footer后异常取消、实际输出fault等反例。基础准备本身不实现新profile、结构快开、恢复或下游，不新增移交系统/状态文件。

正确性：Zero无RNG；tiny边界63/64words、raw F=0/1/63/64/大值、空输入F=0返回非零、bitmap全满返回64、shift别名；强制多次随机撞禁、高位Key、全body marker-free与独立LE scalar oracle；同body不同chunk分割键一致性仅要求确定tiny，随机只要求正确；XOR往返、phase0..3、空块/1B/3B边缘、精确alias、Key0也拒绝shifted overlap与错误参数且不修改；writer token/唯一reservation/未Advance/Reset/无Push、真实written-end外bytes不变。long派生长度的int上界拒绝用小fixture验证，不能为它实际分配16GiB。

性能：复用已有104条小帧和185条随机CPU工作量，不混旧快照；重点比较跨assembly来源/范围开销、分配、Zero与≤256B非零路径、1MiB和近最大body，CSPRNG计入搜索。不规定任意百分比通过线；若公开来源/adapter抵消现有小帧收益，应改用更小入口或将编排留RBF，保留测量反例。真实文件数据均使用W:，不把warm/CPU结果叫cold SSD性能。

Windows串行Release build Data.Tests，匹配 `--no-build` tests，再按接入范围验证RBF依赖和Rbf.Tests；最终检查diff、旧公开API/格式fixture和独立wire向量。包交付另按AGENTS与当时授权执行public PackageReference smoke，不在本切片自动发包。

## 7. 两轮辩证审查裁决

三位独立reviewer分别承担demand skeptic、minimal architect、semantic defender，完成独立源码审查及最强反例的交叉质询；主线程按源码和独立构造裁决。第二轮均撤回byteCount必须保留的原反例：它依赖不再合法的closure提前写入顺序。没有剩余产品语义争议，不继续第三轮。性能/生产接入证据仍是实施门禁。

| verdict | 最小机制与证据 |
| --- | --- |
| simplify | Data接收实际二进制输入，不接收RBF FrameLength/HeadLen/CRC/profile；只新增一个公共静态类型 |
| delete/defer | 删除public IXorEscapeSource/public generic selector，内部重放保留；第二真实输入形态超出窄入口或测量证明必要时再审公开协议 |
| merge | Builder选键+原地变换合为一个具体成员；基础算法与真实Builder资格同一个交付切片 |
| delete | byteCount、公共read/write views、visitor、专用重入guard、lease/generation、策略/向量模式/RNG配置公开面 |
| keep | 完整成功pass、carry/phase、冻结、rawKey!=Fence、一般Fence的满bitmap→64与int输入上限 |
| keep | 可预见拒绝先发生；RBF从首次padding/footer修改起取消；真正输出共享永久fault |

相比初稿，公开新增类型从两个（static类型+source接口）减为一个；新增公开成员从六个（source三成员+选择/Copy/InPlace）减为四个（选择/Copy/InPlace+现有writer一成员），内部来源仍复用。删除一个显式范围参数和外部view/visitor候选，并将两个基础交付阶段合成一个；没有减少正确性所需的连续byte信息。

已处理的文档/源码矛盾：实验tiny全满抛错仅适用于固定大F，通用Data应返回64；span被readonly借用不等于冻结；值类型若保存接口cursor仍会共享/装箱；RBF旧catch-all不适合新预发布RNG；旧专项visitor/guard与Key0/1措辞需要随本裁决同步更新。本文为基础实现方案，已测策略结果继续留专项，不复制一份测量authority。

## 8. 本轮接口小探针的限度

主线程在 `W:/RbfCodecCost/layering-shape-20261003` 建立独立Library/Consumer两个.NET10项目，沿用仓库SDK pin，串行Release build为0警告/错误。跨assembly借用ref-struct generic source与固定三span调用均可编译；1+127+124B切分下F=0/1/63/64/大值的五个scalar构造通过；100000次generic source调用当前线程managed allocation为0。主线程另用独立Python重现相位重置把合法word编码成Fence的反例。

这证明初稿公开泛型来源并非“语言做不到”，删除它依据的是公开契约较宽；不证明最终Data API、实际writer、JIT全部内联、SIMD吞吐或完整策略已实现。源码/日志及测后hash保留在上述W:目录，并内嵌于 [接口小探针证据](../../experiments/RbfCodecCost/results/escape-source-shape-dc11774-20261003.json)。它没有自动测前/测后源码守护，只作标量接口资格，不混入旧性能快照。生产源码及旧实验结果未改，本轮没有运行生产测试或声称包消费验收。
