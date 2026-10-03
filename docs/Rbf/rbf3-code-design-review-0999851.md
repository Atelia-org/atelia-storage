---
title: "RBF3 代码设计审阅：0999851"
status: "Review complete; fixes and improvements proposed"
normative: false
---

# RBF3 代码设计审阅：0999851

后续的[辩证处置方案](rbf3-review-findings-disposition.md)已重新裁决各 finding：收窄 R1/R2 的承诺及严重度，保留最小修复与 Commit catch，并补入普通 Dispose 可触发的 Data 重复 Return 证据。实施以该方案的范围和验收为准；本文保留原始发现记录。

2026-10-03。代码对象为 `0999851207978fe947ecabccb1093e5774d007df`，比较基线为 `ddb9c53`；当前文档基线为 `2f46408`。本轮是代码设计审阅，不修改生产源码或替下游设计。四个独立只读工作包分别审恢复、writer、reader、公开契约和测试假设，主线程复核并用 W: 小探针验证关键反例。

没有发现 RBF3 units、XOR、结构快开或单尾恢复正常路径的新阻断。建议在下游适配前修复两个入口状态缺口，并明确公开串行使用合同。它们是继承问题；这次重构没有引入这些位置的缺陷，但更清晰的资格和预发布失败语义使其值得现在收敛。另有一项新诊断回归和一个很小的分配优化。

## 1. 建议修复

### R1 / P2：BeginAppend 初始化失败后，文件卡在 Building

[RbfFileImpl.cs](../../src/Rbf/Internal/RbfFileImpl.cs) 第157–166行先递增 epoch、进入 Building，再 Reset/ReserveSpan；[SinkReservableWriter.cs](../../src/Data/SinkReservableWriter.cs) 第36–42、217–221行中的 Rent、chunk入队、reservation登记可能失败。异常退出时没有 Builder 返回，也没有恢复 Idle，普通 Append、BeginAppend 和门面读取无法继续；调用方没有可用 Builder 去取消它。

W: 探针将私有 builder writer 换为第一次 Rent 确定性失败的池，复现：BeginAppend抛出注入的 OutOfMemoryException，文件仍只有4B Header，TailOffset=4、fault=false，但状态为Building，后续Append/BeginAppend均拒绝。不是实际耗尽内存，也没有改生产源。该控制流在 `ddb9c53` 已存在。

最小修正：保持Idle，先完成writer/sink初始化和HeadLen reservation，成功后才发布新epoch/Building；初始化失败Reset部分缓冲并重抛，TailOffset不动、不标write fault。验证失败后原异常保留、文件不变、普通Append和新Builder可以继续、旧epoch不能影响新Builder。现有准备失败测试覆盖Append/EndAppend，未覆盖BeginAppend自身失败。

清理范围需准确：Reset只归还已进入chunks的buffer。Data CreateChunk在Rent之后、chunk成功入队之前若发生分配异常，仍有局部租赁所有权窗口；完整加固应让该方法在入队前失败时归还租赁，入队后交由writer负责。它也是继承问题，本轮只有源码依据，未注入该分配失败；默认托管数组可被GC回收，不能称为已证的永久内存泄漏。

### R2 / P2：default FrameInfo 的元信息读取会伪造有效资格

[RbfFrameInfo.cs](../../src/Rbf/RbfFrameInfo.cs) 第70–75、117–122行以 `Reader?.ValidateTicket` 跳过null reader，然后零meta直接返回成功：

```csharp
RbfFrameInfo info = default;
info.ReadTailMeta(Span<byte>.Empty); // success，ticket=(0,0)
info.ReadPooledTailMeta();          // success，ticket=(0,0)
```

W: 对实际生产二进制确认了两项success；同一默认句柄的两种完整读取则抛NullReferenceException。默认值没有创建时的TrailerCRC资格，不能当作L2成功句柄。此问题在 `ddb9c53` 已存在，与正常FrameInfo复用已验证TrailerCRC的决策无冲突。

最小修正：四个实例读入口统一拒绝未初始化Reader，在零meta早退、租赁和任何I/O之前执行；返回明确状态失败或确定的初始化异常，避免NullReferenceException。补default句柄的四入口测试，并保留合法空meta、Dispose/fault及正常info快路径测试。现有测试主要使用工厂返回或nonnull reader的内部fixture，没有覆盖public struct默认值。

### R3 / P2：公开串行使用合同缺失，且骨架文档声称可并发读

[IRbfFile.cs](../../src/Rbf/IRbfFile.cs) 第8行只限制一个open Builder。实际Append始终处于Idle，多个调用可同时取得相同tail起点，且共享scratch/cache/状态；单个独占文件handle不能排除同一对象内的多个调用。[RandomAccessReader.cs](../../src/Rbf/ReadCache/RandomAccessReader.cs) 已明确thread-unsafe，[缓存设计](rbf-read-cache.md)采用单线程模型，但[类型骨架](rbf-type-bone.md)第42、96行仍声称核心线程安全和并发读取。

这是既有公开说明缺口；没有运行并发stress或把它宣称为新回归。最小措施是统一声明同一File及其Builder、FrameInfo、扫描器中访问该File/reader/cache或发起I/O的操作由调用方串行执行，而非新增锁或线程安全能力；不扩大到已物化数据或元信息值属性的读取。不同只读实例的并行使用与同一实例共享cache不同。活跃Builder期间在同一串行流程中读取已有FrameInfo的已接受行为仍可保留。

### R4 / P3：新Append参数拒绝丢失RecoveryHint

[RbfAppendImpl.cs](../../src/Rbf/Internal/RbfAppendImpl.cs) 第40–44行新RBF3分支的meta超限、合计长度超限错误没有RecoveryHint；旧Append对应分支有缩减meta/拆分payload提示，[接口合同](rbf-interface.md)第203行要求校验失败包含提示。

W: 通过公开Append传入65536B meta复现失败且RecoveryHint=null，TailOffset仍为4。补回两条提示即可；这是本轮新增分支的诊断回归，字节正确性不受影响。

## 2. 值得实施的最小改进

### I1：让Data内部chunks遍历使用具体struct枚举器

[SinkReservableWriter.cs](../../src/Data/SinkReservableWriter.cs) 第49行将SlidingQueue返回为IEnumerable，第89、470行两个foreach因此走[SlidingQueue.cs](../../src/Data/SlidingQueue.cs)第253行的接口枚举器，装箱struct。RBF正常Builder的Flush/CRC路径都经过这里。

W: 对生产程序集中的三元素 `SlidingQueue<int>` 执行10000次预热后的枚举：具体类型为0B，IEnumerable为400000B当前线程托管分配，即40B/次。没有使用实际Builder的chunk实例；该结果仅证明这条枚举路径的分配，不代表整个Builder可变成零分配，也没有重测端到端吞吐。

最小改动是将私有GetActiveChunks返回类型改为 `SlidingQueue<ReservableWriterChunk>`，或直接遍历 `_chunks`。不改变公开API、算法或格式；两处foreach保留fail-fast版本检查。这既降低成本，又消除当前生产Commit首次Push前一个已知装箱OOM触发点。

故障边界仍需审慎：[RbfFileImpl.cs](../../src/Rbf/Internal/RbfFileImpl.cs) 第307–309行把整个Commit异常标为永久fault，即使首次Push尚未开始。探针通过替换私有writer、在Data的Commit调试回调中注入异常，观察到未输出、4B Header不变却fault=true；**生产RBF不开放该回调**，正常路径的装箱OOM仍只是源码推导，没有实际耗尽内存测试。接口“Commit/Push前取消”和“实际输出开始后fault”措辞有精度差异，不将此解释成已证的数据损坏或正常适配阻断。

采用上述具体枚举器后，在当前RBF生产配置中，writer的DebugLog为null，已校验token的移除/解绑、具体队列枚举和合法chunk的span构造没有其他已知正常pre-Push分配源。因此建议先做具体枚举器这个小改动，并澄清Commit故障边界；不先引入额外publication状态。仍须保留Commit外层catch：一次Push成功之后的后续输出、回收或ArrayPool.Return异常必须永久fault。不得用PushedLength/CurrentOffset判断一次抛异常的写入是否零输出，它们只在成功返回后推进。

### I2：明确写容量与旧格式读容量的区别

[RbfFile.cs](../../src/Rbf/RbfFile.cs) 的公开 `MaxPayloadAndMetaLength=268435424` 已用注释限定Append/EndAppend，但不应成为通用read端容量上界。RBF1固定开销24B，RBF3为28B，两者均满足 `PayloadLength+TailMetaLength+PaddingLength <= SizedPtr.MaxLength-overhead`，以及 `PayloadLength=ticket.Length-overhead-TailMetaLength-PaddingLength`。相应上界分别为268435428与268435424，不重复扣除meta。运行时codec正确保留旧容量，没有发现拒读问题。

[接口合同](rbf-interface.md)第451行仍有未定义MaxPayloadLength。应补两profile的读取公式，并明确从ticket/info的已验证byte长度读取旧文件。无需新增公共profile配置或容量查询框架。

## 3. 测试补强与通过的设计边界

建议补两个公开入口组合边界：

- MaxOffset帧起点的healthy Open、缺Key/部分Fence、未完成body恢复。现有tests分别测最大起点写入和起点4处最大长度恢复，未把二者组合；稀疏合成fixture只验证边界算术，不冒充完整历史正常writer证明。静态审查及540组纯内存扫描几何检查未发现现有算术错误。
- RBF1最大容量结构fixture经公开OpenReadOnlyExisting/ReadFrameInfo取得旧ticket/容量。现有codec/layout算术测试不能代替该公开入口组合；如果fixture不提供真实PayloadCRC，要明确只验证结构和元信息资格。

独立审阅和主线程源码核对未发现以下边界的新缺陷：Header决定profile且普通工厂显式传递；完整U先验范围再shift；原LE(U)参与TrailerCRC；全uint Key先解码再验长度；缓存保持encoded；meta相位和byte ticket不混用units；逆扫不新增读头；正常FrameInfo复用尾部资格并逐次验PayloadCRC；首坏marker不fallback；完整body只补原Key/Fence；未完成body截回确认边界；修尾后flush和最终结构再验；故障/Dispose守卫及恢复失败关闭handle。

已记录的4KiB合写阈值、Builder分配、最坏异常扫描、历史内容/结构延迟检测、RBF1只读、离线RBF3救援延期不算新发现。仍限进程终止顺序前缀模型，不增加断电、任意手工镜像、冷盘或下游/包消费要求。

## 4. 证据与后续实施顺序

[审阅证据快照](review-evidence/0999851-probe.json)保留观察结果、源码/日志hash及三个引用二进制hash，均与既有[正式0999851生产快照](../../experiments/RbfFastOpen/Rbf3ProductionProbe/results/rbf3-0999851-20261003.json)的binary manifest吻合。原始源码/项目/build/run/result位于 `W:/RbfFastOpen/rbf3-review-0999851-20261003`；runtime .NET10.0.5。Release小探针build成功、0errors/1个nullable warning，运行退出0。注入路径和实际内存耗尽、独立队列分配和全Builder性能严格区分。

本轮没有重跑670项完整RBF测试或正式35点终止实验，不把上轮证据称作本轮新运行；未改生产源码或下游。

建议下一修复切片先做R1、R2、R3、R4与I1/I2，补对应有行为意义的回归检查，再串行Release构建和匹配RBF测试。若修改Data私有遍历，也跑Data.Tests。不改wire-format和恢复政策，不增框架；MaxOffset/旧容量组合测试可同切片补入。4KiB合写阈值及完整Builder成本优化另行实测比较。
