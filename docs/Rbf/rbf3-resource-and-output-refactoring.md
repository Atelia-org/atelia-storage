---
title: "RBF3 读取资源与输出成本重构记录"
status: "Implemented; follow-up triggers recorded"
normative: false
---

# RBF3 读取资源与输出成本重构记录

本方案已实施：两处 pooled 读取采用局部 ownership 与唯一 finally；RBF3 的一次打包输出上限为 **total 8192B**。两组 W: 成对实验、JIT 栈检查、696/696 RBF 测试和实际构建身份见[实施验收](rbf3-resource-and-output-acceptance.md)。已完成步骤从活动 backlog 移出。

原方案以源码70d1009为基础，于2290601完成三方两轮辩证审阅；本轮代码、实验分别由有界工作包实施，主线程独立复核并串行验证。原[修复验收](rbf3-review-repairs-acceptance.md)保持c4377fd的历史身份。

## 已落地的最小模型

| 决定 | 实际实现与理由 |
| --- | --- |
| C3：每次失败租赁至多一次 Return 尝试 | [完整帧读取](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs)和[TailMeta读取](../../src/Rbf/RbfFrameInfo.cs)各一个 `ownedBuffer`、一个 finally；pooled class及成功Result先构造，再清除本地责任 |
| Shared 成功所有权 | Rent及成功对象Dispose继续使用Shared；仅增加内部per-call失败释放回调，Reader和结果对象没有新增pool字段，public API形状不变 |
| 资格与异常边界 | 租赁前guard、合法空meta、FrameInfo复用TrailerCRC、每次完整读验证PayloadCRC保持；cleanup异常可能替换原异常/待返回Result，不保证池已接收或任意资源故障后的复用 |
| 一项固定输出候选 | [AppendRbf3](../../src/Rbf/Internal/RbfAppendImpl.cs)专用常量及对应stackalloc为8192B；`total=Align4(payload+meta)+32`，含末Fence。超过此值的Key0仍借用直接输出，非零Key仍用1MiB scratch；旧internal byte writer的4KiB常量保持 |
| 保留当前故障模型 | 最终Commit的保守fault、健康Auto-Abort的零输出和资源异常边界沿用已确认模型；新打包路径的真实部分写入仍永久fault，公开重开按实际prefix恢复 |
| 阈值选择有明确成本 | 四个4–8KiB Key0目标各14/14对胜出，合并成对中位延迟下降约43–50%；小帧与非零Key方向混合，512B Key0合并中位约增加3.9%，不能承诺零回退。打包分支保留8KiB且清零同量，栈限制见验收 |

实现范围是RBF文件本身；保留RBF3新写/RBF1只读原ticket与容量、4B units、Fence≥2^26、约256MiB上限、串行共享操作及进程终止留下顺序prefix的合同。下游适配与包交付仍各自验收。

## 后续触发条件

| 延期项 | 再启动条件及最小方向 |
| --- | --- |
| 输出阈值的进一步调整 | 真实workload显示小帧清零成本占主导、可复验明显回退或目标调用链栈预算不接受8KiB时，再比较两个4KiB打包等单一替代；按大小限定结果，不引入公共调参/自动阈值搜索 |
| 更大Key0帧及完整Builder成本 | 当前workload的逐帧成本/调用数证据支持独立比较时再启动，已取得的4–8KiB收益不外推到它们 |
| Data C1：Rent至chunk成功入队前的ownership窗口 | 修改CreateChunk、出现未归还实证，或明确custom pool失败租赁核算需求时，在两个现有方法处理局部责任 |
| MaxOffset×public Open/恢复组合 | 偏移/扫描几何改变或公开入口反例时补少量组合；稀疏洞fixture与合法writer主链分别取证 |

通用lease/异常聚合框架、public pool、精确publication状态、任意OOM后继续写、同实例并发没有本轮需求。上述触发事项不是当前实施剩余步骤。
