---
title: "RBF3 读取资源与输出成本重构方案"
status: "Ready for focused implementation; output threshold remains experimental"
normative: false
---

# RBF3 读取资源与输出成本重构方案

最小方案是：**两处 pooled 读取用局部 ownership 与唯一 finally 释放，成功对象继续拥有 Shared buffer；保持最终 Commit 的保守 fault；Key0 的小中帧合并输出先作 W: 对照实验，再决定是否改一个固定阈值。** 安全修复与性能实验分别交付。

本方案基于2026-10-03的源码 `70d1009e78a73342a0c0fdc8ffed7731dec58173`，承接[已完成修复的验收](rbf3-review-repairs-acceptance.md)。本轮只形成方案，没有实施下面的 runtime 修复、故障注入或性能测量。此前 Data chunk 的 C2 已修复；下面的 C3 是另外两处 RBF pooled 读取窗口。

## 1. 范围与需求账本

| 需求/事实 | 来源及当前使用者 | 本方案保留的边界 |
| --- | --- | --- |
| 先专注 RBF 文件；下游适配另阶段；真实 I/O 用 W: SSD | 用户本会话明确决定 | 不实施 RbfSegmentStore/EventJournal 适配，不将它们的编译/测试失败作为本阶段门禁 |
| RBF3 新写、RBF1 只读原 ticket/容量、4B units、Fence≥2^26、约256MiB帧上限 | 用户决定；当前[格式合同](rbf-format.md)及源码 | 不改 wire-format、EscapeKey 选择策略、CRC 或普通打开恢复动作 |
| 同一 File 及派生共享操作由调用方串行；故障模型是进程终止留下顺序前缀 | 用户故障范围；当前[接口合同](rbf-interface.md) | 不加入锁、线程绑定、断电协议或通用 OOM 恢复 |
| ticket 完整读验证两项 CRC；FrameInfo 复用 TrailerCRC/Key 资格，每次完整读验证 PayloadCRC | 用户澄清；当前 RbfReadImpl 与 RbfFrameInfo | 不为修资源释放重复验 TrailerCRC；TailMeta 预览仍是 L2 |
| 成功 pooled 结果拥有缓冲，调用者 Dispose；失败时方法负责释放 | 当前 public API、两个 pooled 结果类和读取测试 | 每次租赁至多一次 Return 尝试；不等同于保证池已接受，也不承诺 cleanup 与原异常的优先级 |
| 最终 Commit 后输出成功、回收失败时，tail 尚未推进，实例仍必须停止 | 当前 Rbf3WriterFaultTests 的真实36B输出＋custom pool异常轨迹 | 保留共享永久 fault，不凭 tail/PushedLength 未变判定零输出 |
| 健康 Auto-Abort 不输出；资源正常归还才保证回到 Idle | 已同步的接口合同和测试 | 资源异常可能传播；已经完成，不新增取消状态机 |
| Key0 大帧减少复制；小帧已有一次 stack buffer 写入 | 当前 AppendRbf3；普通 Append 使用者 | 性能收益尚未测量，不先引入 scatter/gather、公共调参 API 或所有帧复制 |

同源的裁决、验收、接口说明不是三份独立运行时证据。尚无需要任意 OOM 后续写、可替换 public read pool 或自动调优阈值的当前消费者；没有需要用户补充的产品决定。

## 2. C3：pooled 读取的唯一归还责任

### 2.1 源码证据与失败轨迹

- [ReadPooledFrameCore](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs) 在读/CRC Result 失败分支调用 Shared.Return，外围 catch 再调用一次。普通内容 CRC 失败即可进入该分支。
- [ReadPooledTailMeta](../../src/Rbf/RbfFrameInfo.cs) 的 short-read 分支同样先 Return 再构造错误；Return 抛异常会进入第二次 Return。即使第一次 Return 正常，随后分配错误字符串/对象失败也会进入 catch 并重复归还。
- [RbfPooledFrame](../../src/Rbf/RbfPooledFrame.cs) 与 [RbfPooledTailMeta](../../src/Rbf/RbfPooledTailMeta.cs) 的 Dispose 已先 Interlocked.Exchange 清掉 `_buffer` 再 Return；保留这一行为。

关键轨迹是：`Rent(A) → Read 返回失败 → Return(A) 已接受/也可能未接受后抛异常 → catch 再 Return(A)`。若第一次已接受，第二次可能把同一数组再次放入池，后续不同租赁可能别名。当前是源码级条件反例，尚未用 RBF 专项注入复现，不能称为已经观察到 Shared 实际 OOM/池污染。TailMeta short read 也不应被外推为正常工厂允许外部改写已提交帧。

独立的 [.NET10.0.5 SharedArrayPool.Return 源码](https://github.com/dotnet/runtime/blob/v10.0.5/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs#L111-L165)先写 TLS slot，随后可能创建 partitions；由此推断“接收后仍可能分配失败”可达，不等于故障已经实测。

这是清理控制流的问题，不依赖异常后继续使用 File 才会发生；因此“失败后仅 Dispose”不能消除第一次调用内部的第二次 Return。

### 2.2 最小实现

两个方法各保留一个局部 `byte[]? ownedBuffer`，租赁在 try 前完成。Rent之后的Result拒绝和读取/解码/结果构造异常都从同一个finally退出；删除失败分支中的Return和原来的回收catch。成功结果及 `AteliaResult<T>` 都先构造完，再清掉本地归还责任：

```csharp
byte[]? ownedBuffer = ArrayPool<byte>.Shared.Rent(requiredLength);
try {
    var read = ReadAndValidate(ownedBuffer.AsSpan(0, requiredLength));
    if (!read.IsSuccess) { return read.Error!; }

    AteliaResult<PooledResult> success = CreatePooledResult(ownedBuffer, read.Value);
    ownedBuffer = null; // 此后只有返回对象负责 Dispose
    return success;
}
finally {
    if (ownedBuffer is not null) {
        if (returnOnFailure is null) { ArrayPool<byte>.Shared.Return(ownedBuffer); }
        else { returnOnFailure(ownedBuffer); } // internal per-call 测试接点
    }
}
```

这是控制流伪代码，不要求抽取 `ReadAndValidate`/`CreatePooledResult` 或引入通用类型。pooled class 构造须在本地责任清除前；当前 `AteliaResult<T>` 是 ref struct，非 null 对象的成功转换不新增堆分配，显式 success 局部只表达交接顺序。finally 中的 Return 没有重试入口；它的异常可以替换读异常或待返回的 Result，本轮不规定双异常优先级。

现有 ticket/profile/Reader 可用性检查与 BufferRentObserver 留在 Rent 前。合法零 TailMeta 继续不租 buffer；default FrameInfo、disposed/fault Reader 仍在早退前拒绝。无 File 写入或 tail 更新，不新增 writer fault。正常失败且 Return 成功时保留原错误码/Result。

### 2.3 注入边界与验收

Rent 和成功结果 Dispose 始终使用 Shared。仅在两个真实读取主体中提供 **internal per-call `Action<byte[]>? returnOnFailure`**，默认 null 直接调用 Shared.Return。回调替代失败出口的释放操作，不是 allocator API，不保存在 Reader 或结果对象，不影响正常成功路径。

接点位置明确为：`RbfReadImpl` 这个 internal class 的两个既有 ReadPooledFrame 入口加末尾可选回调，并传入同一泛型 core；`RbfFrameInfo.ReadPooledTailMeta()` 保持 public 无参形状，转发到带回调的 internal 重载，后者容纳原方法主体。默认参数和无参入口不创建回调对象。预期仅改两个读取源码文件；Reader、两个成功结果类、cache、工厂及 Data writer不变。不得另写 cleanup helper 作为唯一受测路径。

新增一个 focused 测试文件，回调计数限定在单次调用/租赁，不按数组地址永久禁用归还。先把回调接到旧失败分支与旧 catch 的两处 Return，得到重复尝试的反例，再改为唯一 finally。测试协议避免真实重复归还污染 Shared：

1. **释放后抛出**：ticket/info 完整读各一次 CRC拒绝，以及非空 TailMeta short read。回调首次真正 Shared.Return 后抛指定异常；若第二次进入，只计数并报重复，不再实际 Return。修复后须只有一次尝试，传播首次注入异常。short-read fixture 使用 cache Off、无 fixedEof 的 reader，优先在测试派生类的既有 `ReadWithCache` 接点受控返回短读且保持镜像不变；也可先取得合法info再截短镜像，但只能作为负向模拟。明确观察一次 Rent 请求及一次失败释放，避免 fixedEof 的 Fence 门禁在 Rent 前拒绝而形成假覆盖。
2. **读取抛异常**：复用现有 ReadObserver 注入固定异常；回调正常 Shared.Return 一次，验证原读异常保留。两个释放主体挑代表调用覆盖，不展开 profile/cache 笛卡尔矩阵。
3. **成功移交**：合法完整帧和非空 TailMeta，回调若进入，只计数并抛错、绝不实际 Return。正确代码须回调零次且内容正确，再正常 Dispose。若忘记清掉本地责任，测试会失败且不会提前把仍被结果引用的数组放回 Shared。

既有成功/重复Dispose、空meta/default/disposed/fault及高Key/phase/cache测试继续复用，原有 Dispose 先 Exchange 再 Return 由源码核对，无须为未改的结果类新增 custom pool 能力。成功构造异常由 ownership 顺序及源码复核覆盖，不增加分配故障工厂回调。注入异常是确定性的清理反例，不是真实 Shared OOM；不承诺任意资源失败后继续使用。

## 3. 最终 Commit 与 Auto-Abort：保持已确认的模型

[FinalCommitReturnFailure_AfterCompleteOutputPermanentlyFaultsSharedReader](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs) 已证明完整frame/Fence先于Return异常到达真实文件；TailOffset仍是旧值。去掉最终Commit fault catch后，Builder.Dispose/Reset可回到Idle，下一Append就可能从旧tail覆盖这段输出。这个具体失败足以保留当前机制，不需要再细分“已经接受/尚未接受”的发布状态。

重开可能把物理完整帧识别为正常尾，这是从格式与打开规则推导的后果，不是该测试已经执行的 reopen 结果；也不能把它当成业务提交。此项没有新的 runtime 修复任务。Auto-Abort 的健康零输出、资源异常可传播也已经同步合同，保持当前窄行为。

## 4. Key0 输出成本：先比较一个小候选

### 4.1 当前行为与候选

[AppendRbf3](../../src/Rbf/Internal/RbfAppendImpl.cs) 的判断用 `totalLength = FrameLength + FenceSize`，不是 payload 长度。当前 total≤4096B 时 stack 打包后一次写；total>4096B 且 Key0 时写 Head、非空 payload、非空 meta、closure，通常3或4次。非零 Key 使用每 File 保留的1MiB scratch 合并输出。因此4KiB payload 加结构后已进入多次写路径。

先只比较“RBF3 小帧阈值4096B → 8192B”的候选，复用原有打包分支。RBF3 专用常量及对应 stackalloc 的上限一起改，避免无意改变旧 internal byte writer/边界测试；超过8192B的 Key0 保留借用直接输出。最大打包 buffer 从4KiB变为8KiB；新进入路径的 Key0 原本只有几十B头尾栈缓冲，而候选使用8KiB，不能笼统声称每次调用只多4KiB。实际栈峰值须在目标JIT核对，小帧也须测回退。候选区间用一次复制换取一次 write-call，不保证合入。

两个4KiB打包输出可保持更小的源级栈上界，但需要新中帧分支及填充控制；暂不同时实现。只有8KiB受实测栈约束或出现明显小帧回退、且减少输出次数确有收益时，才比较此替代。当前不用旧RBF1首尾算法、全Key0的1MiB scratch或自动阈值搜索。

同时测非零 Key：统一小帧阈值也会改变它们的路径，不能仅报告 Key0 收益。输入需确认生产 selector 实际选出的 Key；强制合法 Key 只用于隔离核成本，不能冒充正常 public Append 测量。

### 4.2 实验与收束条件

实验在 W: 单独目录串行运行、交替 A/B 顺序；固定 SDK/runtime/Release/source commit 或工作树内容哈希。复用[生产探针](../../experiments/RbfFastOpen/Rbf3ProductionProbe/MeasurementProbe.cs)的 public Append 计时与独立校验，在新结果文件中记录，不覆盖0999851或c4377fd历史证据。

- 起始比较挑一个小帧回退对照（如total64/512B）、4–8KiB目标区间、4096/8192B两侧边界及一个大帧对照（64KiB或1MiB）。沿用7轮成对样本作为探索默认，保留原始数据；轮数不是置信证明，按噪声追加样本，明显不胜出即可停止。
- meta/4B phase挑代表布局；Key0 与真实marker导致非零Key分开报告，不铺满尺寸×phase×Key×首次/暖态组合。用 `total=Align4(payload+meta)+32` 生成RBF3边界，另断言layout值。
- 成本包括每帧 public Append 延迟、分配、首次/暖生命周期及 File 保留 scratch；DurableFlush 单独计时并核对帧内容。写次数/请求长度用现有 [RbfWriteInstrumentation](../../src/Rbf/Internal/RbfWriteInstrumentation.cs)在单独计数 pass 采集；计时 pass 不装 write hooks，避免 AsyncLocal/记录成本改变比较。它是 library write-call 计数，不是设备 I/O 或 OS 原子写保证。
- 不给所有尺寸预设一个普遍收益百分比。只有收益在成对样本中稳定、相关大小/非零 Key无明显回退且栈预算可接受，才把固定8192B阈值作为独立小提交；否则维持4096B并保存结果。没有实际 workload 分布时只作按大小限定的结论，不宣称整体吞吐提升。

若候选胜出，再为新合并分支补真实 partial-write→fault 与少量Head/body/Key/Fence前缀的 public重开检查，复用现有恢复分类。wire bytes未变，库write-call数变化不新增恢复语义；本项不要求重跑独立process-kill矩阵。新证据明确标注实际部分写入后抛异常，不将它称为外部终止或断电测试，也不因一次buffer write取消fault保护。

## 5. 延期事项与不再追加的机制

| 裁决 | 项目 | 最小处理/再启动条件 |
| --- | --- | --- |
| simplify | C3 两个读取释放窗口 | 单一finally、成功转移；两文件的失败释放回调接点，不增加lease类型 |
| keep | 最终 Commit fault、健康 Auto-Abort、CRC资格及串行合同 | 沿用已确认机制与原测试，不重新设计 publication/OOM 恢复 |
| defer | Key0 扩大小帧范围 | 上述 W: 比较是准入证据；测不到可复验收益则保留当前实现 |
| defer | Data C1：Rent 到 chunk 入队之间的责任窗口 | 修改 CreateChunk、出现未归还实证或明确 custom pool 失败租赁核算需求时，再在两个现有方法中处理局部 ownership；本轮不连带修改 Data |
| defer | MaxOffset×public Open/恢复组合 | 偏移/扫描几何改动或公开路径反例再补少量组合；稀疏洞 fixture 不冒充合法主链 |
| delete | 草案拟增readerPool、两个结果pool字段；通用租赁/异常聚合框架、公共pool/阈值配置、精确publication状态和自动调参 | Shared-only失败测试不需要新的成功池义务；局部控制流和固定候选已足够 |

## 6. 最小实施切片与文档维护

1. **C3 独立修复**：先增真实旧body的确定性反例，再改两处cleanup和失败释放接点；主线程检查成功ownership、唯一释放点、零长度与guard顺序，再以W: TEMP/TMP串行Release build及匹配 `--no-build` 的RBF测试验收。本阶段只改RBF源码/测试；可运行Data测试复核依赖，不实施其延期项。
2. **性能独立实验**：使用冻结基线与一个8KiB候选。先形成可复核的新测量；候选不胜出也是交付结果，不必为了完成任务合入 runtime 改动。
3. **仅在胜出后改阈值**：独立提交阈值与必要路径回归，重新取得当前源码/测试/局部输出证据。没有必要重新跑所有历史专题；新增变化的输出边界必须有新身份的证据。

实施后本方案作为活动 backlog 删除已完成切片，结果移入新的验收记录；原[审阅裁决](rbf3-review-findings-disposition.md)和[历史修复验收](rbf3-review-repairs-acceptance.md)只加后续入口，不重标旧结果。当前就把此方案加入[文档索引](README.md)，保持未来实施入口明确。包交付与下游验证仍单独授权和验收。

## 7. 辩证审阅

需求质疑、最小架构、语义防守分别完整读稿与源码形成首轮独立结论，再交叉质询；第二轮已收束，无实质分歧需要第三轮。主线程以具体租赁/成功返回反例和源码裁决，不按票数。

| 初稿方案或审阅疑点 | 质询后的裁决 |
| --- | --- |
| reader固定pool及成功对象携带pool，便于custom pool测试 | **撤回**。反例中的同池Dispose义务以新增custom成功Rent为前提；当前Shared-only产品无需它。架构/语义reviewer接受失败专用回调，拟改源码5文件减为2文件，拟增3个实例pool引用减为0 |
| AteliaResult的先构造步骤是否另需分配故障测试 | **澄清**。当前非null成功转换不分配；必须保留的是pooled class构造前的本地责任与成功移交顺序，不加入构造故障工厂 |
| TailMeta短读fixture怎样确认到达失败释放 | **修正**。fixedEof的范围/Fence拒绝可能发生在Rent前；测试须证明进入目标读取分支，并限定为负向模拟 |
| 扩阈值的栈成本、七轮采样的证据强度 | **修正**。最大buffer与新进入分支成本分别陈述；样本按噪声收束，Key0、非零Key与小帧回退均有对照 |
| 合并分支的故障验收是否包含外部进程终止 | **收窄**。保持顺序prefix/fault规则，补新分支实际部分写入和代表恢复边界；未变化的wire分类沿用原测试 |

没有尚需用户决定的产品语义。剩余未知是8KiB候选的性能/栈成本，交给后续实验；C1和MaxOffset仍按原触发条件延期。本轮完成文档与交叉审阅，未实施runtime/实验，也不把此前677/677测试或历史终止/性能结果重标为本方案验收。
