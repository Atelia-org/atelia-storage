# 2026-10-03：FrameStore / VersionStore 辩证审阅与裁决

> 后续合同变更说明（2026-10-07）：活跃 Builder 不再阻断已完成输出的确认或同文件随机读取；RootMap 所需依赖已完成时可独立发布。本文件正文仍描述其审阅时点的候选，不能据此恢复全局无 Builder guard。当前合同与本轮证据见[改进记录](2026-10-07-completed-output-improvements.md)。

> 历史范围说明（2026-10-07 更新）：本文件保留 2026-10-03 审阅过程与当时的候选，不重写其历史结论。后续会话已将普通 FrameStore 改为不透明分配抽象，并把 FrameLog 分离为可选扩展；VersionStore 进一步采用完整 RootMap、单文件 ref 和独立命名，不再保留本文的 Commit/Parent/Prepared 控制模型。当前合同入口为[目录 README](../README.md)。本记录不构成新模型已完成独立审阅或实施验收的证据。

状态：**设计审阅完成、候选文档已修订；无运行时实施或资格测试**。
观察基线 `70d1009e78a73342a0c0fdc8ffed7731dec58173`。本记录是来源/裁决证据，不新增用户决策，也不是后续施工 backlog。

## 范围、需求来源与过程

用户授权带 subagents 逐步完善八份设计文档，没有授权本次运行时实现、提交或包发布。
三位 reviewer 分别担任需求质疑、最小架构和语义防守；Round 1 完整独立盲读，交付 7/6/8 项九字段 findings；Round 2 交叉质询最强相反轨迹；Round 3 仅讨论剩余布局/身份分歧。主线程独立查源码，依据证据裁决后修改，最后再审修订文本。

| 来源等级 | 本轮保留的约束 |
| --- | --- |
| 当前用户决策 | 两新生产项目与各自测试；旧库仅维护；RBF3 完整帧/自动尾恢复；旧格式只读、不混写转写；中性地址与 root 最后发布；跨文件互引；阶段单向 |
| 当前 RBF 实现/规范 | 单 Builder、owner/epoch、串行、preparation/Commit/fault；CompletedTail/Truncated；只读不修尾；普通 CRC ticket 不证明主链成员 |
| 独立因果反例 | root ABA、未知输出、地址重用、遗漏非根文件耐久、业务单记录原子效果 |
| 派生草案 | 公开 receipt、三套身份表示、共享/多流选项、每次 head 全 CRC、有界启动/索引/toolkit 门槛；均允许挑战 |
| 暂无消费者实证 | 当前环境未定位 DurableGraph；保留用户明确需求，不声称已接入或用自造算例反向证明规模要求 |

八份原草案来自同一提案，不能互为独立佐证。旧栈经验不自动成为新栈全部功能/性能承诺。既有 RBF 验收是历史证据，本轮没有重跑。

## 最小闭合模型

```text
consumer: data 地址规划 → 完成状态/结束 plan
VersionStore.Prepare: 校验/准备 → 必要控制 checkpoint/轮转 → 给 caller token
VersionStore.Commit: guard → data ConfirmDurable → control EndAppend
                     → control ConfirmDurable → Confirmed → ref 投影
consumer: 收到 Confirmed → 安装应用状态
```

FrameStore 每实例单流。VersionStore 借 data、拥有私有 control，持久绑定二者身份，Dispose 不释放 borrowed data。Root 在 data 坐标解释、记录 token 在 control 坐标解释；复用同一 FrameAddress 类型，不增加 StreamId 平台或跨库双根提交。
每个 root 的新增依赖都在 data owner 中；同步全前缀 barrier 代替公共 receipt。状态闭包由消费者负责，不用 opaque payload 回调推断。
三种身份角色共用 RecordToken；Prepare 是调用资格，不是持久业务阶段。事实仍由一条完整控制 frame 生效。

## 核心裁决与具体失败轨迹

| 判定 | 目标/最小机制 | 来源及删除失败 |
| --- | --- | --- |
| keep | RBF Measure、early ticket、排他 plan、最终跨段能力 | 用户已选；A 的 b 未锁定会被另一帧占用，或下游重新自算物理公式 |
| simplify | 全 data owner 同步 barrier；删除 public receipt/fence 与集合代数 | 用户耐久顺序；仅 flush Root 文件遗漏另一文件 A；token 也无法解释 A 对取消 b 的旧业务引用 |
| merge | RefId=create token，revision=current token，attempt=prepared token | A→B→A 不能通过旧 CAS；P 截断后 Q 同地址不得冒充 P；不需三个 clock |
| simplify | 私有 control FrameStore + borrowed data FrameStore | 共享流最后 snapshot 后百万 data、零 VersionStore 操作，控制 count checkpoint 无法限制扫描；复用两实例比多流平台更窄 |
| keep | Prepare 先交 token，Commit 才业务输出 | 只从异常取得 token 时，输出后 OOM 会失去查询渠道；不用库内 pending 日志 |
| keep | Inspect 真实控制主链全回放，按 actual ticket/nonce/请求匹配 | P 旧 ticket 的 length 不适用于复用位置 Q；随机 Read 失败/CRC 成功均不证明历史 absence/成员 |
| keep | 一条业务记录、Confirmed 单调 | Close/Archive 分开产生 closed-but-named；发布 durable 后库投影异常不能撤销事实 |
| simplify | 唯一 staging/next 槽位，未发布空 next 完成定位 | Create Header 2B 中断不在 RBF tail 恢复范围；“存在 next 就失败”不能恢复正常创建窗口 |
| simplify | head/tag 查控制事实；FrameStore.ReadFrame 负责内容接受 | 每次只查 expected/head 就全读大 root，再让 consumer 读第二遍；缩小元信息 promise 后 CRC 仍不可删 |
| simplify | 核心 Branch/Tag/Publish/history；Fork 复用创建，Archive 条件扩展 | 无消费者证据要求全部旧操作；支持 Archive 时 closed/unbind 单记录和新身份不可删 |
| defer | live-only 磁盘 head/history 页面、完整 audit CLI、自动 retry/idempotency | 首个正确 publish 没有这些仍能成立；实际规模/运维需求触发，不能给不存在功能冒发资格 |
| keep | snapshot 被采用后的 exact boundary/先 checkpoint 协议 | 旧 snapshot 忽略后续完整 Publish B 会静默回到 A；默认回放与增强启动资格分开 |

## 三轮中改变的主张与保留意见

- 语义防守撤回“公共窄 fence 必需”：发布 Commit 同步确认、无交错，fence 没有额外守卫职责。
- 需求质疑撤回“最终跨段 planner 等新需求再做”：缺少本地 DG 不取消明确方向；仅首个 proof 可以单段。
- 最小架构撤回“共享流 + 控制 suffix 预算即可有界”：独立 data 写入不受 VersionStore checkpoint 约束。
- 身份讨论最初有两条安全路：caller 预持 nonce ID + 全主链查询（要全局前置拒重需 O(控制历史) seen set/扫描）；或地址+nonce token + 一项 prepared Builder。主线程选择后者，复用已有 Builder 生命周期，保持当前表 O(refs/names/tags)。未把前者宣布为错误。
- 需求质疑仍偏好 A/nonce 作为最早协议实验，但经具体 trace 接受 C/Prepare 为最终一致 Draft。裁决不是三票多数；二者正确性边界都明确，选择 C 是为了避免保留 tag 协议、混排扫描与未来布局返工。
- barrier 从 Prepare 移至 Commit，防止 Prepare 后新增 data 使用过时确认；重复 Commit guard 必须先于这个 barrier，避免拒绝前先 flush/fault borrowed data。

## 新发现的上游缺口与事实纠正

| 问题 | 当前证据 | 修正 |
| --- | --- | --- |
| sealed 只读拒绝 RBF1 没有 public 格式入口 | [IRbfFile](../../../src/Rbf/IRbfFile.cs)、[internal RbfProfile](../../../src/Rbf/Internal/RbfProfile.cs)、[factory](../../../src/Rbf/RbfFile.cs) | S1 提供 immutable 格式属性；不复制 Header、不用 writable open 探测 |
| early ticket 只锁当前 Builder，不能独自保证未来批次位置 | [Begin/Commit](../../../src/Rbf/Internal/RbfFileImpl.cs) | S3 owned plan 控制后续位置，完整前缀可 orphan、旧构建状态失效 |
| checked ticket 不证明主链 | [RBF 接口](../../Rbf/rbf-interface.md)、[forward scan](../../../src/Rbf/RbfForwardEnumerator.cs) | Inspect 从真实主链验证，不依旧 ticket length/cached catalog absence |
| 旧 GetHead 并非每次 CRC root | [GetHead/LoadRefState](../../../src/EventJournal/EventJournal.Refs.cs)、[ResolveTag](../../../src/EventJournal/EventJournal.Tags.cs) | 不把旧目标校验经验泛化为新元信息查询强制全读 |
| MemoryInstalled 混用库投影与应用 graph | 当前用户协议 + 原 S4 状态图 | 明确两责任；应用异常不凭空 fault 健康库，也不撤销 Confirmed |
| StoreId 绑定被误解为裸地址来源证明 | 局部 FrameAddress 候选 | 同数字可跨 store 合法；上下文/来源前提明确，不声称自动推断 caller 意图 |

源级核验不等于新 API、平台 rename 或进程中断取得资格。RBF 内部 profile 当前未公开，所述属性是新增要求。

## Draft 条款替换

| DEPRECATED 原草案条款 | 新条款 |
| --- | --- |
| A-RBF-EXACT-MEASURE | A-RBF-MEASURE-WRITE-SIZE |
| S-FS-ROTATION-RECOVERABLE | S-FS-ROTATION-NEXT-SLOT |
| A-FS-DURABLE-SET / S-FS-RECEIPT-SCOPE | A-FS-DURABLE-PREFIX |
| A-VS-REVISION-CAS | A-VS-TOKEN-CAS |
| S-VS-DEPENDENCIES-FIRST | S-VS-COMMIT-BARRIER |
| A-VS-EXACT-INSPECTION | A-VS-INSPECT-MAIN-CHAIN |
| A-VS-SELECTED-CHECKED | A-VS-FACT-QUERY-THEN-READ |
| A-VS-BOUNDED-OPEN | A-VS-QUERY-COSTS-EXPLICIT；增强另有 INDEXED-OPEN-BUDGET |

用户已确认的六项 Decision 保留。术语 ID 调整为规范的 Title-Kebab；代码名不随之改写。

## 延迟触发与真正产品选择

核心 API/wire、轮转阈值/预算默认和 public 错误属于下一实施片的技术定稿，不用全部询问用户。
实际名字字符/case/长度及是否必须 Archive/name reuse、fork provenance 是产品语义；应在相应片提供默认建议，再按需求调整。
启动/heap SLA 和数据规模决定是否启用 snapshot；更小 live-only 内存才触发持久 head 页面索引。真实运维恢复流程才触发独立工具产品；多 data owners/更强断电/GC 是新范围，不从根发布自动推导。
这些未决项不妨碍 V0/V1 的设计/证明，也不允许把受限原型声明为全部阶段/最终交付完成。

## 最小安全实施片与可观察变化

V0：S1 精确尺寸、early ticket、格式信息、两 RBF 文件互引。V1：S2 单段 owned data/control + 基础 barrier + S4 unnamed ref Prepare/Commit/冷重开。随后同段循环、最终跨段/rotation、命名，再独立取得索引/包/真实消费者资格。

可观察减少：公开 DurabilityReceipt 从一个候选 proof 类型及其合并/复用/过期规则降为同步 void 成功边界；三套候选身份合为一个表示；首版磁盘索引从 catalog/head/history 候选组合降为默认零、增强最多一份全部-ref snapshot；fork/move 不新增同义事实；完整阶段闸门改为必要子合同依赖。
新增的真实成本也明确：第二个标准 FrameStore context、持久 data 绑定、一个 O(1) prepared handle、一个格式属性及中性扫描边界封装。不能只数删掉的字或术语来声称没有代价。

## 本轮证据状态

目标八份文档与此审阅记录修订；未改运行时代码、solution、项目、测试或包脚本，未提交/发布。源码读取、文档一致性核对不代替 .NET、crash、包 smoke 或 DurableGraph 实证。
三位 reviewer 在修订后完整重读八份阶段文档及本记录，发现并修复三项：

| 复核发现 | 修复与闭合证据 |
| --- | --- |
| staging 恰为完整 Header 长度时，不能仅凭长度取消 | S2 拆分 incomplete 与 complete Header；后者先只读验证合法空 RBF3，完整坏 Header/RBF1 明确拒绝。需求 reviewer 定向复核原反例已关闭 |
| S6 Ready 表可能把尚未定位的 DurableGraph 当作核心包门槛 | S6-Q4 明确只在纳入真实接入时要求，独立库/包核心不被它阻塞；定向复核通过 |
| S1 出口漏列已打开格式信息 | 出口补列此项，与 MUST、实施片和 Ready 表一致 |

其余修改后回归没有发现新的显著语义矛盾；没有再为已接受方案的偏好差异扩张机制。收敛范围是这组 Draft 的职责、因果顺序、失败轨迹与阶段边界，wire/API 和平台资格仍须在相应实施片具体审定，不声称已取得运行时正确性。

最终文档检查覆盖全部九份文件：48 个本地链接有效，20 处阶段依赖均指向前序，40 个条款 ID 与 15 个术语定义无重复，术语引用有效，六个用户 Decision ID 保留；UTF-8/LF、结尾换行、代码块配对和行尾空白检查通过。`git diff --check` 也通过；由于目标文档尚未跟踪，另行检查了全部实际文件，没有将 Git 的有限检查当作新文档证据。
