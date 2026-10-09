# FrameStore 批量地址规划器设想

日期：2026-10-09。状态：**设想阶段候选草稿；需求未验证、API 未冻结、未接纳或实施，不属于 MVP 的 Ready 条件**。
候选依赖：[S1 RBF 已知尺寸追加](../01-rbf-sized-append.md)、[S2 FrameStore 核心](../02-framestore-core.md)、[S3 交错 Builder 与耐久确认](../03-framestore-interleaved-builders-and-durability.md)。依赖方向为扩展使用核心；核心定稿、实施和验收不等待本候选。

本文为 Informative，只记录未来可能采用的资源优化及 API 草图；没有新增实施合同、持久格式或资源保证。

## term `Frame-Batch` 批量地址规划候选

候选代码名 FrameBatch，表示先规划一组已知尺寸帧的全部地址，再逐帧填充的能力。只有在真实工作负载需要大量提前地址，且现有 Builder 数量或文件资源成为瓶颈时，再评估它的价值。
希望研究的额外能力是：提前取得 N 个地址时，不必同时持有 N 个活跃 Builder，并可能将多个计划帧安排到少数文件。实际可降低哪些资源、降低多少，尚无实现或测量证据。

## 与嵌套 Builder 的区别

S2/S3 的 MVP 设计通过多个已知尺寸 Builder 支持 self-reference、跨文件互引、交错填充和乱序完成。每个 Builder 独占一个 RBF 文件；同一 RBF 文件在当前 Builder 完成或取消前不能再次 Begin，见 [RBF 单 Builder 与尺寸合同](../../Rbf/rbf-interface.md)。

| 维度 | MVP 的多个已知尺寸 Builder | 本候选要研究的能力 |
| --- | --- | --- |
| 提前地址与循环引用 | 已支持，地址由实际租借签发 | 复用相同地址编码和业务引用方式 |
| 同时取得 N 个地址 | 持有 N 个 Builder，分别占用 N 个文件，受数量 config 限制 | 先保留地址计划，再以少量活跃 Builder 写出 |
| 同文件多个未来位置 | 当前入口不提供 | 可研究内部规划同文件的连续位置，并按物理顺序写出 |
| 乱序完成与取消 | 各 Builder 独立完成或取消 | 同文件有顺序约束；计划剩余地址的处理待定 |

如果实现只是循环调用普通 BeginAppend，它没有提供上述额外能力，也没有形成独立公开 API 的必要。

已知尺寸 Begin 不按声明尺寸预分配整帧。当前 RBF Builder 在最终提交前保留已构建的帧字节；先取得全部空 Builder、再逐个填充和完成，并不天然占用全部声明 payload 大小之和。评估应分别记录文件数、打开句柄、活跃 Builder、缓冲峰值和吞吐，不能仅凭预约数量推定内存收益。

## API 草图

以下仅是签名示意，名称、类型、结果/错误载体、所有权及配置均待研究，不是可编译的完整声明或已冻结 API：

```csharp
// 候选尺寸值：分别声明最终 stored payload/meta 字节长度。
public readonly record struct FrameWriteLengths(
    int PayloadLength, int TailMetaLength);

// FrameStore 上的候选成员。
FrameBatchPlan PlanBatch(IReadOnlyList<FrameWriteLengths> frames);

// FrameBatchPlan 上的候选成员。
IReadOnlyList<FrameAddress> Addresses { get; }
FrameBuilder BeginNext();
void Dispose();
```

设想调用轨迹是：声明全部 stored 长度 → 创建 owner 绑定的私有计划并取得地址 → 编码互引 → 逐帧 BeginNext/填充/EndAppend → 应用确认依赖全部完成 → 调用既有 ConfirmDurable。是否能在计划建立前预编码、计划能否与普通追加交错、同时可持有几个实际 Builder，都须单独定稿。
PlanBatch 可能需要保留文件与位置，不能预先承诺它无 I/O 或只是纯计算。尺寸与追加占用由 RBF 公共 API 计算；FrameAddress 继续采用 S2 的固定 12B codec，调用方仍不拆解 FileId 或自行计算 offset。

Addresses 中的计划地址只表达拟定位置与尺寸，不提供读取、完成、耐久或发布资格。BeginNext 建立实际 Builder 时，需要证明其实际地址与计划一致；提前地址的数值本身不建立尝试身份，取消后也可能被复用。
可研究取消整个计划时保留已完成帧、放弃尚未完成预约；单项取消、计划剩余位置能否继续使用、失败后的 guard 与配额释放方式尚未决定。已完成帧与包含取消地址的旧业务图仍分别遵循 S2/S3 的来源和闭包责任。

## 复用边界

规划器如接纳，优先复用现有 RBF3 Builder、文件初始化、owned fault、Dispose 和 S2 ConfirmDurable。它只管理构建资源，不提供跨帧原子提交、业务闭包证明、独立 DurabilityReceipt 或根发布资格；发布仍由 VersionStore 负责。
多个计划位置落在同一文件时，需要按物理顺序形成完整追加前缀，不能越过未完成位置写未来空洞。计划如何占用文件、与最低可分配 FileId 规则及完成后软轮转衔接，属于扩展协议，不能暗中改变普通追加合同。
没有提出新的持久 batch 日志或恢复状态。若未来方案需要它们，须重新论证需求与复杂度，不能借本草稿视为已获接纳。

## 接纳前需回答的问题

| ID | 需研究 |
| --- | --- |
| FSBATCH-Q1 | 真实工作负载是否需要一次取得大量地址；现有嵌套 Builder 的资源瓶颈与目标预算是什么 |
| FSBATCH-Q2 | 是否能用较少文件/句柄/Builder 达成目标；相比仅逐帧填充空 Builder，实际收益是什么 |
| FSBATCH-Q3 | PlanBatch/Addresses/BeginNext 的准确类型、所有权、配额与错误；计划地址如何绑定实际输出 |
| FSBATCH-Q4 | 文件与位置保留、普通追加准入、最低 FileId、首帧初始化、容量硬界及软轮转如何衔接 |
| FSBATCH-Q5 | 整体或单项取消、剩余计划、地址复用、异常与进程中断后的完整帧及旧构建状态如何处理 |

这些是本候选的研究问题，不是 S2/S3 的 Ready 阻断项。仅在需求得到确认且最小方案能证明资源收益后，再决定是否接纳并形成独立实施片；届时补充 public 消费、取消/中断、地址一致性与资源成本证据。
