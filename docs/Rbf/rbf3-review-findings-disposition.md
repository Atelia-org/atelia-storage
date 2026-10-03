---
title: "RBF3 审阅 finding 裁决"
status: "Repairs accepted; deferred boundaries retained"
normative: false
---

# RBF3 审阅 finding 裁决

最小模型是：**初始化成功才发布 Builder；未初始化 FrameInfo 明确拒绝；同一 File 的共享操作由调用方串行；两个 Data writer 的每次 chunk 租赁至多尝试一次 Return；进入最终 Commit 后的异常仍保守停止实例。** 沿用现有状态机、epoch、Reader fault 和 `IsRented`，不增加公开 API、发布状态或租赁框架。

2026-10-03，[0999851 原审阅](rbf3-code-design-review-0999851.md)的窄修复已实施，源码冻结于 `c4377fd84e2a47e349e60badc9e4ad3a717f9d96`。完成行为、各提交与本次验证身份见[修复验收记录](rbf3-review-repairs-acceptance.md)。本文件保留裁决依据、仍需保持的边界和延期触发条件，不再作为已完成工作包的实施入口；现行行为由[接口合同](rbf-interface.md)及[格式合同](rbf-format.md)定义。

实施后发现的RBF pooled读取C3及Key0输出阈值专题已由[读取资源与输出成本重构记录](rbf3-resource-and-output-refactoring.md)收束，具体696/696与W:测量见[后续验收](rbf3-resource-and-output-acceptance.md)。下面的裁决保留当时身份；本页Data chunk C2及其历史证据保持。

## 裁决依据

用户已确定先专注 RBF 文件；RbfSegmentStore/EventJournal 适配另阶段，真实读写实验使用 W:。RBF3 新 writer、RBF1 只读原 ticket/容量、4B units、最大物理帧长度268435452，以及进程终止后的顺序写出前缀模型均保持。Open 的结构资格不替代完整读的内容 CRC；本轮不改 wire-format、恢复动作或持久地址。

当前源码和测试保留一个 open Builder、epoch 拒绝旧副本、可纠正 Result 拒绝后的同 Builder 重试，以及共享 Reader fault。合法 FrameInfo 创建时取得 TrailerCRC/Key 资格，完整读每次验 PayloadCRC；已有 info 在活跃 Builder 期间的串行历史读取仍允许。默认 struct 没有该资格，但不据此给合法句柄增加重复 Trailer 校验。

需求质疑、最小架构和语义防守三个独立工作包先取证，再交叉质询；只有重复 Return 的处置进入第三轮。裁决依据明确用户决定、源码、测试和反例，不以票数或同源文档互证。RBF 构造路径固定 Shared pool、DebugLog=null；Data 的 custom pool/debug 选项不变成 RBF 公共能力。未发现依赖任意 OOM 后续写的当前消费者，因此删除整组异常加固必须先于下游适配的门禁。

## 已收束裁决

| Finding | 裁决与已完成范围 | 仍保留的边界 |
| --- | --- | --- |
| R1：BeginAppend 提前 Building | **simplify**：Reset/HeadLen reservation 成功后才发布 epoch/Building；撤回失败时额外立即 Reset 的建议，未加入该 catch | 不承诺任意初始化或 cleanup 异常后可继续。初始化失败留下的尚未尝试归还的已入队缓冲由 File 持有；下一 Begin 先成功 Reset，或 Dispose 清理 |
| R2：default FrameInfo 空 meta 成功 | **simplify**：四个读入口统一初始化异常；收窄严重度 | 未发现正确调用绕过 CRC 或破坏持久数据的轨迹。合法空 meta、Reader Dispose/fault 和正常 info 快路径保持 |
| R3：串行合同缺失、并发安全误述 | **keep**：接口、类型指南、cache 说明及 XML 统一串行合同 | 不新增锁、线程归属检查或并发 reader；纯值属性和已物化数据按原生命周期使用 |
| R4：RecoveryHint 缺失 | **simplify**：补两条现有参数拒绝的纠正提示 | 错误码、Result 和无输出拒绝行为保持；提示文本不是业务协议 |
| I1：chunks 接口枚举装箱 | **simplify**：Sink 两处直接遍历 `_chunks`，删除私有转发方法 | 使用同一 struct 枚举器及 fail-fast；不承诺完整 Builder 零分配或吞吐提升 |
| I2：旧读容量与新写容量说明 | **simplify**：删除未定义上界并明确 profile 的 byte 公式 | 当前 runtime 未发现拒读问题；公开写容量不作为旧格式读取上界，不新增容量查询 API |
| C2：Return 失败后 Dispose 重复归还 | **simplify**：两个 Data writer 的 Reset/回收，共四处在 Return 前消耗 `IsRented` 资格 | 每次租赁至多一次尝试，不保证异常时已回池、全部清理成功或失败 writer 可复用；同一数组的新租赁仍可正常归还 |
| RBF1 最大容量公开读取 | **keep**：补一个稀疏最大旧帧的 public Open/info 正例 | 只证明结构与元信息资格，不验证 PayloadCRC 或完整读取 |

具体实现与回归证据已归[验收记录](rbf3-review-repairs-acceptance.md)，不再在此保留伪流程、实现顺序或重复测试命令。内部 `builderPool` 仅供测试构造 Builder writer，公开工厂仍使用 Shared；原 `appendPool` 仍仅用于 Append scratch。

## 保留边界与延期触发

| 裁决 | 对象与边界 | 重新处理的触发条件 |
| --- | --- | --- |
| **keep** | 最终 Commit 的外层 fault catch。完整 frame/Fence 已输出后，回收异常仍可能发生在 tail 发布前；Dispose/Reset 不能解除该 fault | 始终保留，除非另有可证明的发布协议取代。tail、PushedLength 或 sink offset 不变不能证明抛异常的写入零输出 |
| **keep** | 正常 Auto-Abort 不输出，资源归还正常完成的健康实例回到 Idle；归还/内存不足等系统异常可传播 | 本轮已同步实际释放行为的合同边界，不增加吞异常、聚合异常或异常后续写机制 |
| **defer / C1** | CreateChunk 从 Rent 到成功入队前的局部租赁所有权窗口；不等同于已证永久托管内存泄漏，不与 R1 捆绑 | 修改 CreateChunk、出现未归还实证，或明确要求 custom pool 的失败租赁核算 |
| **defer** | 精确区分最终 Commit 内首次 Push 前的失效；当前保守 catch 不新增 publication 状态 | 引入可抛回调、可恢复发布操作，或出现真实公共路径的反例；旧私有 DebugLog 注入本身不构成触发 |
| **defer** | MaxOffset × public Open/恢复组合矩阵；未发现当前算术错误，不作修复或下游适配前置门禁 | 偏移/扫描几何改动或公开入口反例出现时补少量组合。稀疏洞的局部算术、闭合主链和真实 writer 证据分别标注 |
| **defer** | 4KiB 多 span/Key0 输出次数优化及完整 Builder 成本优化 | 有当前 workload 的逐帧 write-call 与成本测量后独立比较；历史观测不直接证明 syscall 因果或 XOR 速度优势 |

同实例并发、任意资源故障后继续写及精确可恢复发布阶段没有当前需求；出现明确消费者或新产品决定后再独立设计。下游适配、包消费、pack/publish 仍属于后续阶段。

## 裁决来源与证据限制

[原始 Data 反例快照](review-evidence/0999851-cleanup-probe.json)及[探针源码](review-evidence/0999851-cleanup-probe.cs)保留在修复前的0999851身份：Reset 后仅 Dispose 仍重复 Return，Commit 已成功 Push 后仅 Dispose 也重复 Return。这是 custom pool 接受后抛异常的确定性模拟，不是 Shared 真实 OOM 或实际生产污染。官方 [.NET10.0.5 SharedArrayPool 源码](https://github.com/dotnet/runtime/blob/v10.0.5/src/libraries/System.Private.CoreLib/src/System/Buffers/SharedArrayPool.cs#L111-L165)支持“接收后还可能分配”的可达性；这使仅靠“不复用失败 writer”延期 C2 的方案不成立。

原审阅的立即 Reset 建议、R2 的数据完整性外推，以及普遍 cleanup 复用、公开 IsValid、通用 lease/注入框架、整组适配门禁均已撤回。本次修复验证单独记录；0999851 的正式终止/性能快照与原审阅探针保持历史身份，未重标为此次修复证据。
