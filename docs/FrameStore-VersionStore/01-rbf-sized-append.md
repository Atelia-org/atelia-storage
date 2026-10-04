# S1：RBF 精确尺寸、已知尺寸追加与格式资格

日期：2026-10-04。状态：**Ready，实施合同已定稿；新增 API 尚未实施**。
前置规范：[S0](00-architecture-decisions.md)及现有 [RBF 接口](../Rbf/rbf-interface.md)、[格式](../Rbf/rbf-format.md)。本阶段不依赖 FrameStore 或 VersionStore。
源码核对基线：`f6f1eb38557863ba5ea1634844a90f0cbe5774cf`；新增签名是后续实施目标，不代表该基线已提供。

## 本阶段目标与已确认选择

增加两组单文件能力：下游无需了解 wire-format 即可试算新写尺寸；内容写入前取得绑定本次追加位置的 SizedPtr，支持不同 RBF 文件的帧互相引用。
当前 `IRbfFile.BeginAppend()` 不接受尺寸，ticket 由 EndAppend 返回；一个文件只允许一个活跃 Builder。新增入口继承已有初始化、最终 Commit、取消和资源异常边界。

用户已确认新 `out ticket` API 分别接收 `payloadLength`、`tailMetaLength`（TailMeta 的 meta 长度），与尺寸计算入口一致。
Begin 固定两部分的逻辑长度，正常完成只需 `EndAppend(tag)`，无需调用方先求和、再保留 meta 长度到 End。
当前两部分仍紧密存储，共用 PayloadAndMeta writer。分立参数保留语义边界和布局计算的输入；本阶段不改 wire-format，不承诺将来分别对齐或分别提供 writer。

## term `Frame-Measure` 精确物理尺寸

给定最终 stored payload/meta 字节长度得到的结果，区分 FrameLength（SizedPtr 长度，不含尾 Fence）与 AppendLength（TailOffset 推进量，含尾 Fence）。它不预留文件位置。

## term `Sized-Append` 已知尺寸追加

在当前文件尾部建立唯一活跃 Builder，声明两部分的长度并提前返回本次 frame 的 ticket。提前 ticket 只有位置和尺寸资格，没有读取、耐久或发布资格。

## 定稿 API 形态

以下是相关类型的签名摘录；已有无参数 Begin 和其他成员保留。

```csharp
// Atelia.Rbf；只读尺寸结果，构造入口由库内部持有。
public readonly struct RbfWriteSize {
    public int FrameLength { get; }
    public int AppendLength { get; }
}

public static class RbfFile {
    public static AteliaResult<RbfWriteSize> MeasureWriteSize(
        int payloadLength, int tailMetaLength = 0);

    public static bool TryGetMaxPayloadLengthForAppendBudget(
        long byteBudget, int tailMetaLength, out int payloadLength);
}

public enum RbfFormat { Rbf1 = 1, Rbf3 = 3 }

public interface IRbfFile {
    RbfFormat Format { get; }
    RbfFrameBuilder BeginAppend();
    RbfFrameBuilder BeginAppend(
        int payloadLength, int tailMetaLength, out SizedPtr ticket);
}

public readonly struct RbfFrameBuilder {
    public AteliaResult<SizedPtr> EndAppend(uint tag);
    public AteliaResult<SizedPtr> EndAppend(uint tag, int tailMetaLength);
}
```

共用现有 RbfFrameBuilder、owner 和 epoch；提前 ticket 只通过新重载的 out 参数提供，不新增 Builder 类型、条件可用的 Ticket 属性或合计长度重载。
两参数 EndAppend 去掉 optional 默认值，保留其 CLR 签名；新增一参数重载按 Builder 模式决定 TailMeta 长度。
RbfWriteSize 是计算结果，不是执行凭据；default 的零值没有已完成或预留资格。

## 实施合同

### spec [A-RBF-MEASURE-WRITE-SIZE] 提供精确新写尺寸计算

替代草案 `[A-RBF-EXACT-MEASURE]`（DEPRECATED）：这是后续层必须消费的基础能力。

MeasureWriteSize MUST 无 I/O、确定计算，输入是最终 stored payload 和 TailMeta 的字节长度。负数、meta 超过 RbfFile.MaxTailMetaLength、合计超过 RbfFile.MaxPayloadAndMetaLength 或容量算术不合法时返回 RbfArgumentError。
参数验证和 layout MUST 与实际 Append/Builder 共用内部实现，不能只包装依赖 Debug.Assert 的布局构造器；合计先以宽整数计算，避免输入相加溢出。
结果 MUST 区分 frame 长度和追加占用；下游无需知道 EscapeKey、长度单位或固定开销。新 writer 只采用 RBF3，无公共 profile 参数；RBF1 读取保留原容量。

（Informative）当前 RBF3 为 `L = 28 + Align4(payload + meta)`、追加推进 `L + 4`。此公式供格式验收核对；应用和普通测试 fixture 应调用公共计算入口。独立 wire golden 测试继续保有自己的格式预期，避免生产实现与测试同时算错。

### spec [A-RBF-APPEND-BUDGET] 提供追加预算的逆向试算

TryGetMaxPayloadLengthForAppendBudget MUST 在固定 tailMetaLength 下，返回不超过 byteBudget 的最大合法 payload 字节长度，受单帧容量上限约束。
预算包含本次追加的整个物理占用，包括尾 Fence；不含已有 Header/前缀，不是 ticket 长度。合法预算不足以容纳 payload 为 0 的帧时返回 false，out payloadLength 为 0；返回 true 时允许 payload 为 0。
负预算或不合法的 meta 长度抛 ArgumentOutOfRangeException，不能当作普通预算不足。逆向计算使用同一内部布局规则，先以宽整数运算再封顶；不提供从任意 ticket 推断真实 payload 长度的 API。

验收须证明成功结果重新 Measure 的 AppendLength 不超过预算；若尚未到容量上限，payload 再加 1 必定超预算。
当前格式向量包括 `(budget=35, meta=0) → true/0`、`(35, 1) → false/0`、`(65568, 65535) → true/1`、`(65567, 65535) → false/0`，另覆盖 0、long.MaxValue、meta 上限和负参数。

### spec [A-RBF-EXPOSE-OPENED-FORMAT] 提供已打开文件的格式信息

IRbfFile.Format MUST 是只读、不可变的 RbfFormat.Rbf1 或 RbfFormat.Rbf3；0/default 未定义，成功打开的文件不返回它。
复用 Header 分派结果，不另读文件，不选择 writer profile。内部 RbfProfile 与公开 enum 的数值不同，须显式映射，不能直接强转。
空文件两个格式的长度也相同；下游不能以只读状态、长度或可写探测替代格式资格。属性允许新库明确拒绝 RBF1，同时保留底层历史读取能力。

这是打开后检查；RBF1 可能先付出原有 O(历史) 只读资格成本。本阶段不增加 factory 的 expected-format guard。格式属性可独立实施，尺寸计算不依赖文件存在。

### spec [A-RBF-EARLY-TICKET] 已知尺寸 Builder 提前给出 ticket

BeginAppend(payloadLength, tailMetaLength, out ticket) MUST 在成功建立 Builder 后返回绑定当前追加起点的 ticket，长度由同一 Measure/layout 得到。
生命周期/可写/Idle guard、长度和起点校验、Begin 初始化资源准备 MUST 在发布新 epoch/Building 并正常返回之前完成。长度非法抛 ArgumentOutOfRangeException；其他 guard 延续已有 Begin 异常语义。
资源准备指现有 Reset、sink 初始化和 HeadLen reservation；不要求预分配整帧，也不保证后续分块租借、编码或最终提交不会失败。抛异常的 Begin 不签发有效 out ticket 或新 Builder。

起点 MUST 非负、4B 对齐且不超过 SizedPtr.MaxOffset；该上限约束起点，不能额外要求整帧及尾 Fence 也落在这个起点上限内。
Builder 活跃期间禁止同文件其他追加，TailOffset 保持不变；最终成功 ticket MUST 等于提前 ticket，成功后按 AppendLength 推进 tail。
提前 ticket 不创建可读 frame，不证明 bytes 已输出；健康 Dispose/取消不输出也不推进 tail。

Begin 之前的纯地址规划使用 Measure、可信起点与 SizedPtr.Create 组合：它是数值预测，未锁定文件。空新文件起点使用 RbfScanBoundary.Empty.EndExclusive；不增加 Begin 之前的预约状态。

### spec [S-RBF-DECLARED-LENGTH] 写入长度与声明一致

已知尺寸模式 MUST 在 Begin 固定 payload 和 meta 长度；一参数 EndAppend(tag) 自动消费该声明。显式 EndAppend(tag, tailMetaLength) 仅接受与声明相同的 meta 长度，不能重新划分。
未知尺寸模式继续支持流式写入：一参数 End 表示 meta 为 0，显式二参数 End 保留原含义。
两模式都先检查 owner/disposed/fault/epoch/Building，再读取当前模式及声明，避免 stale Builder 消费后来追加的声明。

同一 PayloadAndMeta writer 顺序写入 payload 后再写 meta。库能验证总字节数及声明的尾部划分，不能识别每次写操作的业务归属或验证 codec。
已知模式 End MUST 在 padding/footer 等 finalize 修改之前验证实际调用方字节数恰好等于声明之和，并验证 pending reservation 和未 Advance 的借用。
只比较最终 ticket 不够：不同逻辑长度可能被 padding 对齐到同一 frame 长度。
短写或显式 meta 冲突返回 RbfArgumentError，保留活跃 Builder，允许补足或改正后重试；未提交 reservation 返回 RbfStateError，未 Advance 的借用保留现有 InvalidOperationException 和可纠正边界。

已知模式的 Advance 和 ReserveSpan MUST 都在转发/修改底层 writer 前检查逻辑剩余容量；无效或超限 count 抛 ArgumentOutOfRangeException。这些方法先检查生命周期和 epoch，再检查计数。
容量计入调用方所有 Reserved + Advanced 字节；reservation 回填及 Commit 不重复计数。内部 HeadLen、padding 和 footer 不占调用方预算，finalize 使用内部 writer，不经过该预算 guard。
GetSpan/GetMemory 保留 IBufferWriter 的容量保证：返回容量至少为 sizeHint，不截短到剩余预算，也不因 sizeHint 大于剩余额而新增长度拒绝；实际消费在 Advance 时受限。余量为 0 时仍可借用并 Advance(0)。
超限 Advance 拒绝不消耗当前借用，允许改为合法 count 或 Advance(0)；其他 writer 协议 guard 保留。现有 RbfPayloadWriter.Length 投影语义不在本片改变，预算在内部排除 HeadLen。

### spec [R-RBF-SIZED-LIFECYCLE] 保留现有提交与取消边界

已知尺寸入口 MUST 使用现有 Builder 生命周期及串行规则，不创建第二个提交权威。
default Builder 的 End 仍抛 InvalidOperationException；stale、重复 End、已关闭 Builder 保留 RbfStateError，已有 fault 和系统异常保留现有传播方式。
确定的可纠正拒绝不输出；首次 finalize 修改之后的准备失败取消该 Builder。最终 HeadLen Commit 开始后异常永久 fault，Dispose 不重试发布；取消资源归还异常不承诺同一 File 可继续使用。
重开接受 RBF3 的 Truncated / CompletedTail / None，报告不替代此前调用结果。

数值 ticket 没有追加尝试身份。B 取消后，C 可以复用相同 offset/length；旧 B ticket 的 checked read 可能读到 C，epoch 只能拒绝旧 Builder/writer。
调用方 MUST 不把被取消尝试及其旧引用作为完成证据；地址复用后需按新尝试重新取得完成资格。含旧引用的构建状态不得直接沿用资格发布，已经完成的其他帧仍作为物理事实保留。
本阶段不向 SizedPtr 加 generation，也不建立跨文件事务。

### spec [S-RBF-NO-BATCH-HOLES] 单文件不引入预留空洞

每文件 MUST 保持一个活跃 Builder，只锁定当前帧；不为后续帧写空洞或绕过物理顺序。不同文件可分别打开一个 SizedAppend，组合完成、耐久和发布资格属于上层。
调用方须先确定最终 stored 尺寸，包括地址字段及压缩/编码结果。互引验收使用固定宽度地址；本阶段不提供变长地址或压缩长度的固定点求解器。

## 调用示例（待实施 API）

```csharp
var size = RbfFile.MeasureWriteSize(payloadLength, metaLength).Unwrap();
using var builder = file.BeginAppend(payloadLength, metaLength, out var ticket);
// ticket 可供另一个文件的帧编码引用；当前文件仍被该 Builder 占用。
builder.PayloadAndMeta.Write(payloadBytes);
builder.PayloadAndMeta.Write(metaBytes);
var completed = builder.EndAppend(tag).Unwrap();
// 成功时 completed == ticket；file.TailOffset 推进 size.AppendLength。
```

示例长度须匹配实际 byte spans，Write 使用 System.Buffers 的扩展方法。只有成功完成后才能通过本文件门面 checked-read；耐久另行调用 DurableFlush。

## 当前使用证据与范围

本轮核对发现以下尺寸用例；这里提出迁移入口，不把下游修复纳入 S1。

| 使用位置 | 暴露的需求 | 本阶段处理 |
| --- | --- | --- |
| [EventJournal.Refs](../../src/EventJournal/EventJournal.Refs.cs) 的 catalog 容量预检与固定尺寸限制 | 下游复制开销，仍有 RBF1 的旧计算 | 提供正向 Measure；旧库适配独立处理 |
| [Rbf3SmallAppendOutputTests](../../tests/Rbf.Tests/Internal/Rbf3SmallAppendOutputTests.cs) 的目标 total length fixture | 从追加预算求最大 payload | 提供逆向预算入口；保留独立 wire golden 预期 |
| [ThresholdProbe](../../experiments/RbfFastOpen/Rbf3ProductionProbe/ThresholdProbe.cs) 的尺寸构造 | 实验也依赖固定开销和内部 layout | 新实验可用公共入口；既有实验及证据不在本轮重写 |

当前 DurableGraph 生产代码仍使用未知尺寸 Begin/End 路径，尚无新接口接入证据；用户明确提出的跨文件互引需求以 S1 独立 public 验收建立资格。
读取目标 buffer 已可按 ticket.Length 分配，空文件起点和后继物理位置也已有公共入口，不另增这些 helper。

## 实施片与独立验收

实现沿用 Idle/Building 及现有 fault。Preparing、Finalizing、CommitAttempt 是操作边界的说明，不新增独立状态机。

| 场景 | 必须保有的证据 |
| --- | --- |
| Measure/预算试算 | 0、padding 余数、meta/max 合计、负数及极大输入；正逆向一致与独立格式向量 |
| Begin 初始化失败 | 文件与 tail 未变，未发布新 Builder/epoch；可重试资格按现有资源合同 |
| 短写、超写、meta 冲突、借用/reservation 未结束 | 输出前拒绝；Advance 与 ReserveSpan 都受限，规定的纠正重试成立 |
| 不同拆分但相同合计；不同长度但相同 padding 后尺寸 | 声明的 meta 确定尾部划分；检查精确总量而非只比较 ticket |
| 未知尺寸、default、stale、重复 End | 旧模式与生命周期拒绝保持，旧 Builder 不能读取新声明 |
| A/B 两文件先取 ticket 再互引 | 固定宽度引用；成功 ticket 相等，完整读取及冷重开后引用一致 |
| A 完成、B 取消、C 重用 B 的地址 | A 可存在；B 无完成资格，裸 ticket 不能识别尝试更替 |
| 最终输出中断、完成后资源异常 | 现有取消/fault 和重开合同保持，不追认提前 ticket 为成功 |
| RBF1/RBF3 空/非空只读打开 | Format 明确，RBF1 原读取容量不变，Measure 只计算新写 RBF3 |

1. S1-A：实现尺寸 DTO、正向/逆向纯计算及共享 runtime 验证、公开 XML 和边界测试。格式 enum/属性可在本片独立提交。
2. S1-B：在现有 owner/epoch 路径实现分立声明、out ticket、两个 End 重载、Advance/ReserveSpan 预算及输出前 guards。
3. S1-C：补双文件互引、取消地址复用、资源及输出失败证据；独立审阅并复验未知尺寸路径，同步 RBF 当前接口文档。

测试归 tests/Rbf.Tests，不新增下游依赖，不重复 EscapeKey/CRC 算法测试。按仓库要求先 build Release，再匹配配置 test；旧库已知编译断点单列，不以其适配扩大本阶段范围。
RBF 独立 public 消费例子须能执行尺寸计算和双文件互引；源码资格与后续隔离包消费资格分别记录。真实文件实验如需性能对照使用 W:，不设没有实证的吞吐承诺。

## 原 Ready 阻断项的裁决与出口

| ID | 定稿处理 |
| --- | --- |
| S1-Q1 | 分立长度、RbfWriteSize 两个尺寸；Measure 返回参数错误，预算 Try 区分非法参数和无可行值 |
| S1-Q2 | 共用 Builder，通过 Begin 的 out SizedPtr 签发；无 Ticket 属性，由 owner/epoch 约束 |
| S1-Q3 | 精确总量、冻结 meta、Advance/ReserveSpan 提前拒绝，短写等可纠正重试，取消地址允许复用 |
| S1-Q4 | 初始化完成后发布；初始资源准备不等于全帧预分配；finalize/最终 Commit 边界沿用现有实现 |
| S1-Q5 | RbfFormat 1/3、不可变属性、显式内部映射；早拒 factory 延后 |

本次 Ready 表示合同和验收映射定稿；尚无新增 API 的 build/test、终止实验或包消费结果。
出口要求单文件已知尺寸追加、公共尺寸计算、格式信息及所有 ticket/lifecycle 行为已实现并独立验收，不存在 FrameStore 反向依赖。实际测试和 commit 身份写入阶段验收记录后，才能标 Accepted。
