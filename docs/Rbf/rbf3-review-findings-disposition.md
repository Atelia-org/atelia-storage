---
title: "RBF3 审阅 finding 处理方案"
status: "Ready for implementation; production changes not yet applied"
normative: false
---

# RBF3 审阅 finding 处理方案

最小处理模型是：**初始化成功才发布 Builder；未初始化 FrameInfo 明确拒绝；同一 File 的共享操作由调用方串行；每个租赁的 Return 尝试至多一次；进入最终 Commit 后的异常仍保守停止实例。** 沿用现有状态机、epoch、Reader fault 和 `IsRented`，不增加发布状态、公开有效性属性或租赁框架。

2026-10-03，源码对象为 `0999851207978fe947ecabccb1093e5774d007df`，本轮起点为 `dabfa21`。本方案处置[上一轮审阅](rbf3-code-design-review-0999851.md)的 R1–R4、I1/I2 及补测建议。需求质疑、最小架构、语义防守三个独立工作包先分别取证，再交叉质询；仅 Data 重复 Return 的处置进入第三轮。主线程核对源码并运行独立 Data 反例探针，不按 reviewer 票数裁决。

本轮交付处理方案和证据，生产修复尚未实施。本文件是后续小切片的工作入口，原审阅是发现记录，现行接口/格式仍由其权威合同定义。

## 1. 需求与证据边界

| 必须保留的事实 | 来源及实际范围 |
| --- | --- |
| 专注 RBF；下游适配另案；真实实验使用 W: | 用户当前与此前决定。下游源码只用于核对调用，不加入其 build/test 或适配工作 |
| RBF3 新 writer、RBF1 只读原 ticket/容量；4B units 与最大物理帧长度268435452 | 用户已接受的格式决定与当前 codec/tests；本切片不改 wire-format |
| Open 只做结构资格，完整读负责内容 CRC；恢复只截未完成尾或补原 Key/Fence | 用户决定及现行源码/合同；仍限进程终止顺序前缀，不增加断电模型 |
| 合法 FrameInfo 创建时已验 TrailerCRC/Key，完整读每次验 PayloadCRC | 用户澄清及当前源码/测试；默认值不具资格，但不因此重新校验合法句柄的 Trailer |
| 一个 open Builder、旧副本由 epoch 拒绝；未输出准备阶段与实际输出故障有区别 | 源码、测试及接口合同。可纠正 Result 拒绝的同 Builder 重试规则不变 |
| 已输出或最终 Commit 中的异常不能靠 Reset 解除共享 fault | 当前 writer/sink/测试。tail 或 PushedLength 不变不能证明异常的写入没有部分输出 |
| Reader/cache 与 Data writer 非线程安全 | 当前源码。没有同一实例并发支持要求；不要求固定 OS thread，只要求不重叠的串行调用 |
| RBF 内部使用 Shared pool、DebugLog=null；Data 有公开 custom pool 能力 | 当前构造路径。旧反射/DebugLog 注入不等于 RBF 公共配置，当前消费者未显示依赖任意 OOM 后续写 |

原审阅、interface 和类型骨架是同一仓的说明，不能互当独立证明。当前代码/测试与明确用户决定优先。`.NET 10.0.5` 的 Shared pool Return 可能在已接收数组后继续分配，证据见[官方源码](https://github.com/dotnet/runtime/blob/v10.0.5/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs#L111-L165)；生产 OOM 频率和实际污染没有实测。

## 2. 逐项裁决

| Finding | 裁决 / 处理 | 理由及最小范围 |
| --- | --- | --- |
| R1：BeginAppend 提前 Building | **simplify / 修复** | 状态漏洞真实，但属于入口健壮性；仅重排初始化与发布，删除立即 Reset 的额外 catch 和“任意异常后可继续”的承诺 |
| R2：default FrameInfo 空 meta 成功 | **simplify / 修复，收窄严重度** | 统一初始化异常；没有发现正确调用读取坏数据的轨迹，不作为 CRC 绕过或持久数据损坏描述 |
| R3：串行合同缺失/并发安全误述 | **keep / 修正文档与 XML** | 错误说明会诱导同实例并发；不添加锁、线程归属检查或并发 reader |
| R4：RecoveryHint 缺失 | **simplify / 诊断补齐** | 两条提示即可；不做新错误框架或将提示文字变成业务协议 |
| I1：chunks 接口枚举装箱 | **simplify / 小优化** | 两处直接 foreach `_chunks`，删除仅转发的私有 GetActiveChunks；保留同一 struct 枚举器的 fail-fast |
| I1 附带的 pre-Push 精确失效问题 | **keep** 外层 Commit catch；**defer** 精确细分 | 注入现象真实，但不是公开生产回调；不据此增加 publication 状态。post-Push 回收异常是必须保留 catch 的强反例 |
| I2：旧读容量与新写容量说明 | **simplify / 文档修正** | 当前 runtime 正确；删除未定义 MaxPayloadLength，给 byte 容量公式，不新增公开 profile/capacity API |
| C1：CreateChunk 入队前租赁窗口 | **defer / 独立 Data 加固** | 不等于永久托管内存泄漏；不作为 R1 的前置。触发：修改 CreateChunk、出现未归还实证或明确要求 custom pool 的失败租赁核算 |
| C2：Return 失败后 Dispose 重复归还 | **simplify / 同小切片修复** | 正常异常收尾就能触发，不能仅靠“不复用”搁置。使用现有 IsRented，Sink 两处必修，Chunked 两处同形同步 |
| MaxOffset × Open/恢复组合 | **defer / 非前置门禁** | 未发现当前算术错误。偏移/扫描几何改动或公开入口反例出现时补少量组合，避免完整矩阵 |
| RBF1 最大容量公开读取 | **keep / 一个兼容回归正例** | 明确的旧容量义务；稀疏结构 fixture 即可，不租256MiB完整 payload，不与主修复形成依赖 |

**删除的假需求**：整组 OOM/资源加固必须先于下游适配；普遍 cleanup 异常后可继续；新的发布状态、公开 IsValid、通用 lease/注入框架；完整 Builder 零分配或吞吐提升保证。没有 finding 被无证据删除为假阳性；纠正的是严重度、证据外推和捆绑门禁。

## 3. 可执行的最小实现

### 3.1 R1：准备完成后才发布 Builder

修改 [RbfFileImpl.BeginAppend](../../src/Rbf/Internal/RbfFileImpl.cs)。保留前置检查与 cache 失效，随后按如下顺序执行：

```text
检查 Dispose / fault / writable / Idle / tail范围
InvalidateCacheFrom(tail)
writer.Reset()                            // 失败即退出，不发布Builder
sink.Reset(tail)
ReserveSpan(HeadLen, out localToken)      // 此时仍为Idle
设置 frameStart、HeadLen token
epoch++，closeReason=None
fileState=Building                       // 最后发布
返回带新epoch的Builder
```

初始化失败不加第二次 Reset，也不调用只在 Building 有效的 AbortBuilder。尚未尝试 Return、且已成功入队的部分缓冲继续由 File 持有；下一 Begin 的首个 Reset 或 File.Dispose 清理它们。这样不会遮蔽初始化原异常，也不会创建可输出的 Builder。失败不推进 tail、不新增 write fault；下一 Begin 必须先完成 Reset，才可发布新 epoch。

在生产默认 sizing 下，一次新初始化只预留4B HeadLen，至多新增一个普通 chunk：首次4KiB，尺寸策略增长后最多64KiB。该上界不覆盖整个 File 内存，也不消除 C1 的入队前窗口。若 Reset 本身抛异常，不承诺同实例继续使用或已完全清理；C2 只解决重复归还尝试。

长期回归采用现有 internal 构造函数末尾的可选 `ArrayPool<byte>? builderPool = null`，只传给 `_builderWriter`。公开工厂不暴露、不传入它，仍用 Shared；现有 `appendPool` 保持只用于 Append scratch。一个明确内部参数比替换 readonly 私有字段更稳定，不增加字段、writer 类型或通用注入入口。

### 3.2 R2：四入口统一初始化拒绝

在 [RbfFrameInfo](../../src/Rbf/RbfFrameInfo.cs) 增加一个私有 `RequireReader()`，返回 `Reader ?? throw new InvalidOperationException("Frame info is not initialized.")`。四个 Read 方法先取 reader：

- 两个 meta 入口再执行原 ValidateTicket，然后才可零 meta 早退或 Rent。
- 两个完整读入口将非空 reader 转交现有 RbfReadImpl，保留其生命周期、fault、ticket 和 PayloadCRC 检查。

异常风格沿用 default Builder/PayloadWriter，不新增 RbfStateError 分支、有效性字段或公共 IsValid。值属性、相等性和合法空 meta 行为不变；已取得 info 在活跃 Builder 期间的串行历史读取仍有效。顺带修正两个完整读 XML 中容易被理解为“每次重新验 Trailer”的旧说明。

### 3.3 C2：归还资格在调用 Return 前消耗

对 [SinkReservableWriter](../../src/Data/SinkReservableWriter.cs) 与 [ChunkedReservableWriter](../../src/Data/ChunkedReservableWriter.cs) 的 Reset、TryRecycleFlushedChunks，共四个 tracked Return 点采用：

```csharp
if (c.IsRented) {
    c.IsRented = false;
    _pool.Return(c.Buffer);
}
```

更新 [ReservableWriterChunk.IsRented](../../src/Data/ReservableWriterChunk.cs) 的内部说明：它表示该 chunk 尚可尝试归还这次租赁。必须在调用前置 false；成功后才改无法处理“池已接收却抛异常”。不用新增 helper、catch 或字段。

保证是**每次租赁至多一次 Return 尝试**，不是数组对象终身只能 Return 一次，也不是必定回池。Return 在接收前失败可能失去缓存机会；接收结果未知时不重试更安全。失败 writer 的缓冲可能已属于 pool，不能继续访问或写入；本补丁不承诺资源故障后的 writer 复用、所有清理成功或保留双异常中的第一异常。

Chunked 与 Sink 使用同一现有 ownership 字段且有同形代码，四处同步是本方案选定的小范围一致性修正，不扩成两个 writer 的全面 OOM 加固。C1 的局部租赁交接另案保留。

同时纠正现行 Auto-Abort 的说明冲突：`RbfFrameBuilder.Dispose` XML、接口里的 Builder 示例及 `[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]` 只将“不变量破坏”列为不抛异常的例外，但当前实现的 Return 系统异常已经直接传播。实施时明确：正常取消不主动抛状态错误，不输出、不解除已有 fault；资源归还/内存不足等系统异常可能传播，发生后不保证恢复可写。资源归还正常完成的健康取消仍回到 Idle。这里补齐的是已有释放行为的边界，不新增吞异常、聚合异常或异常恢复机制，也不修改根决策。

### 3.4 I1 与最终 Commit：具体遍历，保留失效边界

删除 Sink 的私有 GetActiveChunks，将 FlushCommittedData 和 GetCrcSinceReservationEnd 的两个 foreach 直接遍历 `_chunks`。不改为索引 for，不改变 CRC、chunk 顺序、reservation 或输出算法。

保留 CommitFromBuilder 的外层 catch。精确说明“调用最终 Commit 开始解除输出屏障/尝试发布；其异常保守永久 fault”，不要称为“已保证写出字节”。CRC/footer/选Key等此前准备阶段的取消规则与可纠正 Result 重试规则不变。

不可删除 catch 的轨迹：Push 全部成功 → frame/Fence 已输出 → 回收 Return 抛异常 → tail 尚未推进。若只靠 sink 的写失败标记，Dispose 后新 Append 可能从旧 tail 覆盖已经输出的帧并留下旧尾巴。另有抛异常但已部分输出的 Push，因此不以 PushedLength/CurrentOffset 为零解除 fault。

现有 pre-Push DebugLog 探针只说明保守失效边界；生产 RBF 的 DebugLog 为 null。I1 移除已知装箱点后，没有发现需新增发布状态的正常公共 pre-Push 路径。若未来引入可抛回调、可恢复发布操作，或出现真实公共路径反例，再审精确分界。

### 3.5 R3/R4/I2：修正说明与诊断

R3：以 IRbfFile XML 和接口合同为公开入口，统一说明同一 File 及其派生对象访问共享 reader/cache、构建状态或执行 I/O 的操作由调用方串行；包括 Dispose 与枚举推进。无需固定线程；独立只读实例可并行。已物化数据和纯元信息值属性按原有生命周期使用，不扩大限制。修正 `rbf-type-bone.md` 的“核心线程安全/并发读”、`rbf-read-cache.md` 的 Building 禁读概括、`RbfReadImpl.ReadFrame.cs` 的“无状态，并发安全”及 RandomAccessByteSink 将单 Builder 等同串行保证的说明。保留门面 Building 拒读与既有 info 例外。

R4：在 AppendRbf3 两个 RbfArgumentError 中分别补“缩减meta”和“缩减/拆分payload”的 RecoveryHint，沿用现有错误码和 Result；不提取新错误工厂。

I2：接口合同 PayloadLength 段删除未定义 MaxPayloadLength，引用已验证 ticket/info 的 byte 长度并明确：

```text
PayloadLength = ticket.Length - overhead - TailMetaLength - PaddingLength
PayloadLength + TailMetaLength + PaddingLength <= SizedPtr.MaxLength - overhead
overhead: RBF1=24B，RBF3=28B
对应上界: RBF1=268435428B，RBF3=268435424B
```

公开 MaxPayloadAndMetaLength 仍只约束新 writer 的 Append/EndAppend，不作为通用读取上界。meta 不重复扣减；不改当前 codec 或增加公开容量接口。

## 4. 实施顺序与验收

一个窄切片即可：先做 Data 的 C2/I1，再做 RBF 的 R1/R2/R4；同切片同步 R3/I2/Commit 说明，最后加入旧容量结构正例。这个顺序方便分工与审阅，不把全部完成设置为下游适配的产品硬门禁。

| 回归故事 | 最小验收 |
| --- | --- |
| R1：第一次 Rent 抛，随后恢复 | internal builderPool 一次失败；原异常、零输出、tail不变；已有读与普通Append可用，新Begin成功。把失败插在已关闭旧Builder与新成功Builder之间，验证旧副本不能取消/写入/提交新Builder |
| R2：default四读取入口 | 一个case组检查四入口统一InvalidOperationException；空caller buffer与两个pooled入口同样拒绝，不需要I/O或pool接点 |
| 合法info与空meta | 复用现有reader/fault/Dispose测试，保留零meta不Rent、fault/Dispose先于早退、正常Trailer资格复用和每次PayloadCRC；补实际缺项即可 |
| C2：Reset或回收Return失败后Dispose | 两个writer各覆盖两条路径。Data已有custom pool记录接收后一次抛异常；后续仅Dispose，断言同一租赁周期不再次Return。另保留健康Reset后的正常重租/归还，不能禁止数组合法复用 |
| RBF取消中的资源释放异常 | 使用同一个internal builderPool接点，归还接受后一次抛：Builder.Dispose传播原资源异常、不输出、不推进tail；随后File.Dispose关闭owned handle且不重复Return该租赁，不断言健康续写 |
| R4：已有参数拒绝 | 在现有超长meta等断言中检查hint非空、错误码及文件/tail不变；不固定英文全文，不为第二条字符串独立新增256MiB分配或validator框架 |
| I1：遍历行为 | 使用既有Data CRC/XOR/负面测试与RBF Builder测试；不新增锁定40B数字的单元测试或把完整Builder零分配设为门禁 |
| RBF1最大容量公开正例 | 一个从offset4开始的稀疏最大旧帧：正确Header、HeadLen、TrailerCRC、padding/Fence；公开OpenReadOnlyExisting→ReadFrameInfo保留原ticket及payload=268435428。只验结构/info，不声称PayloadCRC或完整读取通过 |

不为每个 CLR 分配点增加 allocator/DebugLog 注入。MaxOffset 组合若以后实施，不能将现有 SparseRbfTestFile 的洞当作合法正常历史；它已显式标为绕过public Open的算术fixture。局部合成结构、闭合主链与真实writer/全内容CRC证据要分别标注。

Windows 的 .NET 验证由主线程串行，TEMP/TMP 设到 W: 新目录。按用户已确定的下游边界，只验证本次 Data/RBF 闭包：

```powershell
dotnet build tests/Data.Tests/Data.Tests.csproj -c Release
dotnet build tests/Rbf.Tests/Rbf.Tests.csproj -c Release
dotnet test tests/Data.Tests/Data.Tests.csproj -c Release --no-build
dotnet test tests/Rbf.Tests/Rbf.Tests.csproj -c Release --no-build
```

实施后独立复核四个Return点、状态发布次序、default guard及合同差异，再记录实际结果。不存在格式迁移、公共 API 新能力或新增状态；仅新增一个内部 pool 参数和一个私有 reader accessor，删除一个私有遍历转发方法。此切片不进行pack/publish，也不重跑与改动无关的完整crash/性能矩阵。C1、精确pre-Push细分、MaxOffset补测及4KiB输出次数优化保留明确触发条件，不混入本次验收。

## 5. 本轮证据与裁决变化

[新Data反例快照](review-evidence/0999851-cleanup-probe.json)与[原始探针源码](review-evidence/0999851-cleanup-probe.cs)记录本轮新运行；原项目、build和stdout位于 `W:/RbfFastOpen/rbf3-findings-dialectic-20261003/DataResetProbe`。Release build为0warnings/0errors，运行退出0，runtime .NET10.0.5；两个复制DLL hash匹配既有正式0999851 manifest，88项源码manifest保持不变。

| 实测公开Data轨迹 | 结果 |
| --- | --- |
| 两chunk，第二次Return注入接受后异常，随后仅Dispose | Reset路径ReturnIds=`[1,2,1,2]`，重复2次，未Push |
| Commit释放Head reservation，回收第二chunk时同类异常，随后仅Dispose | 两次Push共8196B成功，ReturnIds=`[1,2,2]`，重复1次 |

这些是custom pool的确定性模拟，不是Shared pool真实OOM、实际RBF文件写出或crash验收。官方Shared源码支持“接受之后还可能分配”的生产可达性；不把它称为已发生的生产数据污染。归档程序断言的是0999851的反例，修复后的回归断言应改为没有重复尝试，不能原样当作修复通过门禁。

最终变化：三位reviewer都接受R1晚发布及R2初始化异常；反例使“Data cleanup全部搁置”收窄为保留C2小修复，而非全面异常恢复。原审阅的立即Reset建议撤回；R2的数据完整性外推撤回；Commit精确新状态与整组适配门禁不成立。RBF核心format/recovery未发现新问题。不存在需要用户先裁定的产品选择；未来若要求任意资源故障后继续写、同实例并发或精确可恢复发布阶段，才独立立项。

本轮没有重跑上轮670项RBF测试、35点终止实验或完整性能探针；这些旧证据及既有快照原样保留。方案落实后从此文件移除已完成工作包，并把实现结果保存在单独验收记录，避免让活跃计划变成历史日志。
