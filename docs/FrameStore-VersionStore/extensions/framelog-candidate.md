# FrameLog：FrameStore 有序帧日志候选扩展

日期：2026-10-05；2026-10-07 同步 VersionStore 简化。状态：**Draft / 可选扩展候选；需求、去留、API 与实施范围尚未确认；新栈首版不依赖本扩展**。
候选依赖：[S0 边界](../00-architecture-decisions.md)、[S1 RBF 能力](../01-rbf-sized-append.md)、[S2 FrameStore 核心](../02-framestore-core.md)；交错构建的消费资格可参阅 [S3](../03-framestore-interleaved-builders-and-durability.md)。依赖方向为扩展使用核心；核心定稿、实施和 Accepted 出口不等待本扩展。

本文承接原 S2 中的日志术语、`[S-FS-LOG-ORDERED]`、`[A-FS-LOG-CURSOR]` 及相关工程问题和验收要求，保留条款标识用于追溯。下文 MUST/MUST NOT 描述选用该候选后拟建立的合同，不表示已确认需要实施。

## 候选目的与范围

为需要稳定追加顺序和续读的消费者提供不解释 payload 的帧序列。追加次序不表示业务因果、提交祖先、状态合法性或已确认耐久；这些资格由各自所属层负责。
应用构建 State 的中间帧仍使用普通 FrameStore，支持嵌套 Builder 和交错完成。VersionStore 不得以这些帧的申请次序、完成次序、文件编号或物理排列解释发布事实。

普通 inventory 按 `(FileId, offset)` 排列不足以取得此能力：低编号文件中的帧晚于高编号文件中的帧完成时，新发现的帧可能插到此前扫描结果之前。日志候选需要证明新增完整帧只延长序列尾部，不能把任意确定排列当作追加顺序。

## term `Frame-Log` 显式有序的帧日志能力

候选代码名 FrameLog，复用 FrameStore 的文件生命周期能力。现有候选落点为 Atelia.FrameStore 项目内的窄 facade，拥有独立 StoreId/句柄，只开放串行 Append/已知尺寸 Builder、随机读取、ConfirmDurable 和日志扫描；最多一个尚未完成的日志 Builder，不开放普通分配器的交错构建、批量随机预约或底层可写 owner。
上述落点、入口范围与类型均未冻结，不新增第三个生产项目或通用多流调度平台。普通分配器已确认的三种追加能力不因本候选而收窄。

单 Builder 仍不足以独自证明跨文件顺序：可追加后缀、恢复后的文件选择和扫描顺序必须独立定稿，不能直接套用普通分配器的任意空闲文件复用策略。仅把 MaxOutstandingBuilders 配置成 1 不建立日志合同；若采用单 Builder 限制，它必须由扩展 guard 保证，不能被运行配置放宽。
当前格式候选持久区分 Allocator/Log 用途，Open 核对用途，禁止把普通数据目录直接当日志或绕过 facade 输出。确切 factory、用途字段、版本与代码复用边界仍待定；此用途字段不是 S2 核心格式的必需项。

## 候选合同

### spec [S-FS-LOG-ORDERED] 日志显式提供完整帧的追加顺序

FrameLog MUST 定义跨文件的唯一追加顺序，保证成功完成的记录位置稳定，并在恢复后按真实 RBF 主链枚举完整帧；不按调用方提交的地址集合排序来代替真实扫描。
在 S0 故障模型内，日志轮转、取消和恢复不得丢弃已完成记录或重排它们。未完成尾的地址可重用，位置本身仍不证明某次追加尝试身份。
顺序能力 MUST 由显式日志入口取得；同一个持久 owner 不得同时接受可绕过日志 bookkeeping 的分配入口。首版候选不支持修改现有 owner 的用途或导入任意外部文件。

### spec [A-FS-LOG-CURSOR] 日志扫描边界封装布局

候选 GetScanBoundaryAfter(FrameAddress) MUST 完整校验指定帧与尾 Fence，产生绑定 log context 的不透明 cursor；ScanForward(cursor) 复验 anchor 后遍历真实后缀及后续文件。全量回放使用显式起始入口，不从 default 地址猜测 genesis。
cursor 内部可包含文件编号及 RbfScanBoundary，调用方不拆解或自行推算。健康局部 anchor 不等于整个 prefix 已审计，任意随机 CRC 读取不单独证明日志主链成员。完整回放、随机读、边界和内容校验的资格各自报告。

## 与核心能力的复用边界

日志的 ConfirmDurable 候选复用 S2 的 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`；活跃日志 Builder 不阻断旧完成输出的确认，也不因此成为完成/耐久记录。随机读取消费 S2 的 completed-prefix 合同；本轮不放宽日志扫描及 cursor 构建期间 guard。追加次序不代替成功 flush 返回。owned handles、fault、Dispose、首帧 header、creating/active/archive 协议和公共 RBF 能力尽量复用核心实现，不复制 layout/CRC/EscapeKey 或另建耐久权威。
扩展需单独建立有序文件选择与轮转/恢复规则。普通 FrameStore 的最低可分配 FileId 规则不承担这项保证；若复用后端，需要证明日志 facade 无法调用普通分配入口破坏顺序。
日志如何展示或过滤首帧 meta/header、保留用户 tag 范围、形成用户帧起始边界，在本扩展定稿；不作为 S2 的日志验收要求回填核心。

## 工程问题与接纳条件

| ID | 待定内容 |
| --- | --- |
| FSLOG-Q1 | 是否存在稳定追加序列/续读的实际需求，是否接纳及本轮是否实施；提供不认识 VersionStore 的最小消费者 |
| FSLOG-Q2 | 具体类或能力接口、factory、独立身份、用途字段/版本与混用拒绝；项目及后端复用边界 |
| FSLOG-Q3 | 可追加尾文件选择、单 Builder guard、取消与轮转、创建中断和恢复后的唯一文件顺序；不退回旧文件追加的证明 |
| FSLOG-Q4 | 起始入口、cursor 表示/生命周期、anchor 资格、跨文件续读、扫描终止与损坏报告、header 展示及是否支持逆扫 |
| FSLOG-Q5 | owned 资源/epoch/fault、EndAppend 自动归还、维护错误及 ConfirmDurable 与核心的复用方式 |
| FSLOG-Q6 | 源码 public 消费、进程中断、两平台与规模资格；若纳入包交付则验证 FrameStore 包入口，不另造日志生产包 |

接纳条件是形成可独立说明和验收的有序日志合同，且普通 FrameStore 的格式、调用自由和核心出口不为未选用的能力付出不必要成本。日志扩展的缺失或未定稿不影响 S2/S3 核心资格。

## 独立实施与验收候选

仅在需求和范围确认后，再安排扩展工作包：先关闭用途/API 与有序分配协议，再完成跨文件扫描、恢复与 public 消费资格。复用已 Accepted 核心子合同，但不把核心通过当作日志顺序已经成立的证据。

验收至少覆盖：

- 两种不透明记录，在单 Builder 串行追加、软轮转和冷重开后得到同一完整序列。
- 用途/owner 混用拒绝，无法经普通分配或裸 RBF 绕过扩展 guard；配置不能放宽单 Builder 约束。
- 健康取消、可纠正拒绝、地址复用、重复 End/Dispose 与旧 epoch，不产生假记录或二次归还。
- 跨文件顺序、日志末尾中断与目录生命周期中断；已完成记录不丢弃、不重排。
- 起始/续读边界、cursor 混用、坏 anchor、header 过滤及扫描损坏，区分结构扫描与内容 CRC 资格。
- 完成输出与成功耐久确认分别报告，维护/flush 异常按各自边界处理。

## VersionStore 候选的状态

[S4](../04-versionstore-publication.md) 与 [S5](../05-versionstore-names-and-indexes.md) 已采用单文件 ref 的完整字典快照、独立 branch 绑定和分桶 tag。它们不需要跨文件发布全序；旧“私有 control FrameLog”草案已被替代，不能用其历史引用证明本扩展具有实际消费者。
本扩展只有在独立有序日志需求明确后才讨论接纳；不以 data 帧的物理排列替代应用因果关系，也不把 ref 将来的局部分段自动升级为 FrameStore 核心要求。
