# 不可变 tag：实现合同

2026-09-27。需求来源：DurableGraph DB-084；本片交付 EventJournal 与独立包验证，DG 接入另行完成。

## 范围与接口

- `AteliaResult<bool> CreateTag(string name, EventAddress target)`：成功为 true；名称、重复名、目标校验失败返回 error，未尝试发布。
- `AteliaResult<EventAddress> ResolveTag(string name)`：名称不存在为 `EventJournal.TagNotFound`；每次解析 checked-read 目标，坏目标返回 `TagTargetInvalid`。
- 独立 ordinal 名称空间；名称规则与 branch 相同：1..128 ASCII 字节，`[a-z0-9][a-z0-9._-]*`，禁止末尾 `.` / `.lock`。同名 branch 合法；同名 tag 无论目标相同与否均拒绝。
- 只提供创建与解析，无 move/delete/archive/checkout/list 或通用 ref-kind 框架。接受任意有效 EventFrame，不解释领域 State/Event；坐标校验不能证明跨仓来源。
- 延续单 driver 串行模型。只读可解析，禁止创建。Dispose 或 tag 发布故障后拒绝数据操作。

## 唯一持久权威与格式

复用 `refs/ref-op-log.rbf`，增加 `TagBindingFrameTag = 0x54464A45`（LE `EJFT`），不修改 `RefOpFrame` v1、事件格式或目录布局。每次创建只追加一个无 TailMeta、非 tombstone 的 RBF frame。

payload 为 LE 固定 32 字节头加名称：

| 偏移 | 类型 | 内容 |
|---|---|---|
| 0 | u32 | magic `0x47544A45`（`EJTG`） |
| 4 | u16 | version = 1 |
| 6 | u16 | header length = 32 |
| 8 | 16 bytes | 非空 `EventAddressCodec` target |
| 24 | u16 | 名称字节长度 1..128 |
| 26 | 6 bytes | reserved = 0 |
| 32 | ASCII | 名称，长度必须精确匹配 |

RBF CRC 覆盖 payload。replay 显式扫描 tombstone 并拒绝它，严格拒绝未知 frame tag/version、非法字段和重复 tag。扫描重建两个独立索引；所有打开入口逐个 checked-read tag 目标，坏目标使打开失败。

新 reader 可读旧 journal；第一次创建 tag 后，旧 reader 在未知 RBF frame tag 处拒绝打开，不能静默忽略 tag。不提供降级写入或迁移格式。完整新帧仍是有效 RBF，不会仅因语义新颖而被格式 recovery 丢弃。

## 发布与故障

先完成 guard、名称/重复/目标校验、codec 分配和 dictionary capacity 预留，再执行：

1. `RbfSegmentStore.ConfirmDurable(target.SegmentNumber)` 确认目标段；不轮转，历史 idle reader 释放后使用临时可写句柄。它不校验整个文件。
2. 追加 tag frame。
3. `ref-op-log.DurableFlush()` 返回，建立确认发布的证据。
4. 安装预留容量的内存绑定，返回 true；不调用领域代码。

确认阶段起异常抛 `TagPublicationException`，携带 `TagPublicationOutcome`：`NotAttempted`（尚未调用 Append）、`Unknown`（Append 开始至日志 flush 返回前）、`Confirmed`（flush 已返回）。全部这些异常使 journal fault；只能 Dispose，严格重开后解析真实绑定，不能自动重试 Create 或修尾。普通预校验返回 error 不 fault；准备分配异常未尝试发布。RBF Append 返回的 error 按其前置校验合同保证没有 I/O，直接作为失败结果返回且不 fault；它与可能已部分写入的异常不同。

成功只承诺现有 RBF 文件 flush 合同；不新增目录 fsync 或硬件断电保证。目标段确认覆盖本地 EventFrame 字节，不递归确认 parent 或 payload 引用的外部数据。DG 必须在调用前完成 Graph/Schema 等依赖确认；既有 `HistoryJournal.ConfirmDurable` 的 events → ref objects → ref-op-log 顺序覆盖 tag publication 文件。

默认可写打开仍保留原有 recovery 行为；查询不确定创建结果必须关闭三个 `RecoverActiveTailOnOpen`，或使用 `OpenReadOnlyExisting`。严格打开、解析和 tag 创建均不修尾。Dispose 释放资源，不回滚已追加数据。

## 验收映射

| 合同 | 证据入口 |
|---|---|
| 不可变、独立名字、branch move/archive 不影响、跨重开、历史段 | `ImmutableTagTests` |
| 校验失败无追加、只读、Dispose | `ImmutableTagTests` |
| 重复持久记录、CRC/字段/未知格式/目标/tombstone/坏尾 | `ImmutableTagTests` |
| 发布三个阶段、fault 后拒绝操作、严格重开结果、实际句柄失败 | `ImmutableTagTests` / `TagIoFailureTests` |
| 段确认不轮转与 lease 保护 | `RbfSegmentStoreConfirmDurableTests` |
| public PackageReference 跨重开解析 | `examples/EventJournalSmoke` + `eng/Test-Package.ps1` |

源码、包与旧 reader 验证结果在交付记录中维护。测试故障注入验证软件边界，不是断电实验。
