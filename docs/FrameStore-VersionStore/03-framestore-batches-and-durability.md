# S3：FrameStore 批次、循环引用与同步耐久屏障

状态：**Draft；2026-10-04 改为不透明批量预约与全 owner 耐久；完成次序仍采用串行候选**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)。本层不解释根发布或 payload 引用。

## 本阶段目标

在一个 FrameStore 中先预约已知 stored 尺寸的一组不透明地址，再构建可互相引用的 immutable frames。调用方表达引用关系，不管理文件选择或 offset。
提供 owner 级同步 ConfirmDurable；它确认物理完成输出，不证明 opaque 业务闭包。项目仍是 FrameStore 与其测试，不建持久 pending-batch 日志。

## term `Frame-Batch` 排他的批量地址预约

候选代码名 FrameBatch：持每帧分立的 stored payloadLength/tailMetaLength、不透明地址及完成状态，最多一个活跃 plan；不要求复制全部 payload。描述符 index 对应请求项，不赋予业务排序或物理相邻资格。
自引用和跨文件互引都在最终范围内。首个纵向片可以明确只支持单文件并在输出前拒绝越界，不将它当成最终跨文件交付。FrameLog 不暴露此批量分配入口。

## 候选合同

### spec [A-FS-BATCH-PLAN] 批次先规划全部地址

调用方 MUST 提供每帧最终 stored payload/meta 尺寸；FrameStore 内部通过 S1 精确尺寸和 S2 后端分配规则预约所有地址，不要求调用方知道文件数量或轮转位置。
规划描述符 MUST 保留两个长度，MeasureWriteSize 和实际 BeginAppend(payloadLength, tailMetaLength, out ticket) 使用同一对输入；不能只持合计长度或 padding 后的 FrameLength。正常 EndAppend(tag) 消费已声明的 meta 长度。
签发计划前完成算术/容量/编号和计划预算预检。其他 append 不得抢占位置；尚未创建段的地址只是 provisional，不写空洞、不产生完成资格。
采用同一布局/轮转算法的纯规划与实际追加，不在 batch 层复制 RBF 字节公式。压缩/编码的最终长度由消费者先确定。

### spec [S-FS-BATCH-ORDERED] 按物理顺序完成（DEPRECATED）

由 `[A-FS-BATCH-FILL-SERIAL]` 替代；物理排序留在后端，不成为消费者解释帧的依据。

### spec [A-FS-BATCH-FILL-SERIAL] 首版串行填充预约项

首版候选 MUST 通过类似 BeginNext 的入口按描述符次序交付一个当前 Builder，不能跳过未完成项；这是 API 的显式准入限制，不承诺“任意完成次序”。调用方预先取得全部地址，故可先编码任意引用，再按此入口输出。
后端 MUST 遵循单文件 RBF 追加约束，跨文件也由同一个 driver 串行；每帧实际 ticket 必须等于预约结果。调用方不通过公开物理地址决定当前可写项。
确定的可纠正短写/参数拒绝遵循 S1，不自动中断 batch；全部项完成才释放整批完成资格及排他控制。取消或输出失败后禁止继续使用旧计划。
若今后允许按 index 任意填充，需要先设计缓冲预算、调度与中断裁决；不能仅因地址不透明就宣称该能力已经成立。

### spec [R-FS-BATCH-ORPHANS] 中断保留完整帧且放弃构建状态

中断后完整 frames 保留，残尾由 RBF 处理；不回滚完整帧，不从残缺 bytes 恢复 batch，不写第二套 batch 账本。
未完成 plan/handle 的资格失效；数值地址可能重用。消费者 MUST 放弃包含取消地址的旧构建状态；A 已完成、B 被取消后 C 占用 b，不能把旧 A↔B 根当作新状态。
读取完整 orphan 是合法物理读取；采用它必须重新建立完成及业务闭包来源，不能沿用旧 provisional 资格。

### spec [A-FS-DURABLE-PREFIX] 同步确认完成前缀（DEPRECATED）

由 `[A-FS-DURABLE-OWNER]` 替代。全部完成输出的集合不要求对外存在一个可排序的地址前缀。更早的 `[A-FS-DURABLE-SET]`、`[S-FS-RECEIPT-SCOPE]` 也保持 DEPRECATED。

### spec [A-FS-DURABLE-OWNER] 同步确认 owner 的全部完成输出

ConfirmDurable MUST 在没有活跃 Builder/未完成 plan 时，同步确认本 owner 所有已完成、尚未确认的输出；成功返回是本调用栈的耐久证据。
首版后端的 sealed 文件在 S2 轮转前已 flush；当前 active 的完成输出确认后即可覆盖此前全部输出。重开 active 即使 Action=None 也纳入首次确认。这个证明利用内部封段不变量，不要求调用方认识文件或比较地址。
flush 失败按 owned fault 停止实例；不允许裸 RBF writer 或外部文件导入绕过登记。未来多个数据 owner 的闭包不在此屏障范围内。
范围可包含不相关完整 orphan，无须选择每帧集合。先前 flush 后又追加的 frame，必须由新的屏障确认；上层不接受调用方的“以前 flush 过”标志替代实际同步确认。
首版不提供 DurabilityReceipt、集合合并/复用/过期协议。若未来同一个 owner 有多个 active 文件，必须登记并确认它们的全部必要输出；只 flush 根所在文件不满足本条。
FrameLog 的 ConfirmDurable 复用此规则；日志中的追加次序不代替成功 flush 返回。

## 构建与耐久是不同资格

`Planning → Planned → Writing → Complete`；取消为 Abandoned，可能输出异常为 Faulted。Complete 释放构建控制，之后 ConfirmDurable 可取得耐久。
不保存一个可合并的 Durable batch 对象。后续新 batch 不撤销此前已确认帧的 bytes，也不自动确认新增输出；内部 dirty 文件/位置登记仅用于实现，不是新的持久权威。

独立用户可以显式调用 ConfirmDurable。任何上层的同步发布必须在自己的实际输出前调用最新屏障；没有公共 receipt 可供它延迟消费。
选择旧地址与新旧帧混合闭包走同一机制，不要求复制旧帧。sealed 资格来自封段协议，reopened active 本次再确认。本层不解释提交、fork 或 tag。
消费者仍负责闭包：合法既有帧及本次完成帧都属于正确数据上下文，所引用的新增依赖已完成。barrier 不会解析 payload 找出遗失的 B。

## 循环引用与故障算例

预约 A、B、Root 得到 a/b/r；A 编码 b、B 编码 a，Root 编码 a/b。通过串行入口完成所有帧，释放 plan；调用方可将 r 交给上层，任何发布前仍需确认 owner 的全部完成输出。a/b/r 不表达创建先后或文件相邻关系。
跨段时每次封段已经确认旧输出，最终 active 仍须确认；只确认 Root 所在文件而不建立这个封段不变量是不合格实现。

若 A 完成后 B 中断，A 只是完整 orphan；新计划可能重用 b，旧构建状态不可直接采用。FrameStore 不知道哪个根已经对外发布。
若 ConfirmDurable 抛错，没有成功耐久返回，owning FrameStore 停止；完整 bytes 在重开后可存在，不等于此前调用被确认成功。

## 预算、实施片与验收

计划的 frame 数、段数和描述符内存必须有明确准入界限；每帧容量沿用 RBF。payload 可由消费者逐帧编码/提供，本层只同时持有当前 Builder 的缓冲。
Ready 时定稿可配置界限、默认和超预算错误；没有真实目标规模时不宣称具体吞吐/延迟或“无限循环图”。

1. S3-A：定稿 descriptor/预算，完成单段精确 plan、排他和取消；支持早期循环根 proof。
2. S3-B：扩展同一 planner 到多段，验证阈值/MaxOffset/编号及实际轮转一致。
3. S3-C：完成全 owner barrier、reopen/sealed 规则、旧/新帧混合及每个 flush/资源异常。
4. S3-D：public API 循环引用和进程中断资格；记录 IO、内存及真实预算。

覆盖同段/跨段 A↔B、self-reference、A 完成/B 缺失、取消地址重用、guard 拒绝、active 首次再确认、跨段 flush 中断及旧 root 采用。
FrameStore 单测不依赖发布库。没有公开 receipt 的伪造/过期测试；改测 owned 写入不可绕过、未完成 plan 不能通过屏障、成功返回前全部确认。

## Ready 阻断项与出口

| ID | 需定稿 |
| --- | --- |
| S3-Q1 | 不透明地址 descriptors、计划预算、内部纯分配规划和 epoch/排他规则 |
| S3-Q2 | BeginNext 完成次序是否足够；跨文件完成返回、可纠正拒绝、取消/释放/fault 签名 |
| S3-Q3 | ConfirmDurable 的 reopened active/dirty/sealed 实现与错误 |
| S3-Q4 | 旧/orphan 帧来源、新旧闭包使用示例和地址重用资格 |

最终出口是预算内跨段构建与全 owner 同步耐久可独立验收。单段 proof 可以先成立，不取消跨文件能力；消费者管理业务闭包，不管理各 segment flush 顺序。
