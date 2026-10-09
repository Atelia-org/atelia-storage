# S3：FrameStore 交错构建、循环引用与同步耐久屏障

状态：**Draft；2026-10-04 采用独占文件租借与交错完成；2026-10-05 确认自动归还、数量上限 config；2026-10-07 取消全部归还的屏障前置；项目尚未创建，完整 batch planner 仍为待定优化**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)。本层不解释根发布或 payload 引用。

## 本阶段目标

在一个 FrameStore 中通过多个已知尺寸 Builder 先取得不透明地址，再构建可互相引用的 immutable frames。调用方表达引用关系，不管理文件选择、offset 或完成次序。
消费 S2 的 owner 级同步 ConfirmDurable，验证交错构建后的耐久资格；它确认物理完成输出，不证明 opaque 业务闭包。项目仍是 FrameStore 与其测试，不建持久 pending-batch 日志。

多个 Builder 的租借/归还仍串行，每文件一个 Builder；本阶段证明交错生命周期，不宣称多线程并行。基本构建能力来自 S2，不要求先有完整批次规划器。

## term `Frame-Batch` 大规模批量地址规划候选

FrameBatch 是先规划全部地址、再逐帧填充的候选优化，目标是在大量预约时降低同时持有的 writer/文件/缓冲数量。该优化可另行评估，但不作为首版循环引用或发布的前置，也未冻结公开 API。
直接租借能解决嵌套构建和预算内自引用/跨文件互引；预取成千上万地址可能需要不同资源策略。需求出现时再比较纯规划、逐帧填充等方案，不能把交错 Builder 的实证扩大为任意规模批次资格。

## 候选合同

### spec [A-FS-EARLY-ADDRESSES] 已知尺寸租借直接取得互引地址

调用方 MUST 提供每个 Builder 最终 stored payloadLength/tailMetaLength；FrameStore 使用 S1 的 BeginAppend(payloadLength, tailMetaLength, out ticket) 建立实际租借并签发 FrameAddress，不只保留合计长度。
签发多个 Builder 时，各自绑定不同 RBF 文件。已有 Builder 不因下一次申请而提交、取消或移动；提前地址只表达位置与尺寸，不证明完成、耐久或此次尝试身份。正常 EndAppend(tag) 使用 Begin 声明的 meta 长度。
调用方可取得全部所需地址后编码互引；未知尺寸 Builder 不提供提前地址，也不能靠猜测未来长度获得同等资格。容量试算调用 RBF 公共 API，不复制布局常量。
互引消费 S2 `[F-FS-FRAME-ADDRESS-12B]` 的固定宽度 codec：每个基础地址字段恰占 12B，可以在 Begin 前计入 stored payloadLength；不根据内容 CRC、未来 witness 或 varint 的结果改变该字段长度。正常完成前后地址编码一致，不要求完成后再修补已写入其他帧的引用。

### spec [S-FS-BUILD-INTERLEAVED] 活跃 Builder 可交错填充并独立完成

预算内多个 Builder MUST 可以交错填充、回填和按不同于申请次序的顺序完成；每文件仍只追加当前帧，不写未来空洞。正常完成的已知尺寸地址必须等于 Begin 签发地址。
可纠正短写/声明冲突遵循 S1，只影响相应 Builder 的提交准入；健康取消仅放弃其未完成帧并归还文件，不回滚其他已经完成的帧。输出或取消资源异常遵循 S2 的共享 fault。
完成一个 Builder 不释放其他 Builder 的生命周期，也不赋予整个对象图完成或可发布资格。调用方按真实业务闭包确认所需帧已经完成，并放弃任何包含取消地址的旧构建状态。

### spec [R-FS-BATCH-ORPHANS] 中断保留完整帧且放弃构建状态

中断后完整 frames 保留，残尾由 RBF 处理；不回滚完整帧，不从残缺 bytes 恢复 batch，不写第二套 batch 账本。
未完成 Builder/handle 的资格失效；数值地址可能重用。消费者 MUST 放弃包含取消地址的旧构建状态；A 已完成、B 被取消后 C 占用 b，不能把旧 A↔B 根当作新状态。
读取完整 orphan 是合法物理读取；采用它必须重新建立完成及业务闭包来源，不能沿用旧 provisional 资格。

## 构建与耐久是不同资格

（Informative / Derived）本节及后面的故障算例消费 S2 的 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`、completed-prefix 随机读取、租借/fault 合同与 S0 的根字典发布原则，不增加第二套屏障规则。

每个 Builder 按 `Leased → Building → Completed/Abandoned` 结束并归还，输出异常按 owner Faulted 处理。ConfirmDurable 确认调用时全部必要完成输出，未归还 Builder 保持原状；既不阻断旧完整输出的确认，也不因此成为 Completed/Durable。
不保存一个可合并的 Durable batch 对象。后续新增帧不撤销此前已确认帧的 bytes，也不自动确认新增输出；内存中 dirty 文件/位置登记仅用于实现，磁盘 active 集合以目录为准，不写第二套状态账本。

独立用户可以显式调用 ConfirmDurable。上层若发布引用本 owner 数据的完整根字典，必须在自己的实际输出前调用最新屏障；没有公共 receipt 可供它延迟消费。只命名一个既有已发布对象的操作不重新发布数据引用，本层不要求它额外调用 data 屏障。
选择旧地址与新旧帧混合闭包走同一机制，不要求复制旧帧。archive 资格来自移档前 flush，reopened active 本次再确认。本层不解释提交、fork 或 tag。
消费者仍负责闭包：合法既有帧及本次完成帧都属于正确数据上下文，所引用的新增依赖已完成。barrier 不会解析 payload 找出遗失的 B。

## 循环引用与故障算例

串行申请已知尺寸 A、B 得到 a/b；A 编码 b、B 编码 a，可以先完成 B、再完成 A。Root 在其后构建或另行租借，编码 a/b。发布包含这些数据引用的根字典前，应用必须保证 A、B 及 Root 的实际新增依赖全部完成，再确认 owner 的全部必要完成输出；无关的 C 仍 Building 不阻断此次确认或发布。a/b/r 不表达业务顺序或文件相邻关系。
两者同时未完成时必须分别租借文件，因此互引天然覆盖跨文件；self-reference 只需单个已知尺寸 Builder。无需纯 planner、尚未创建文件的地址或消费者自行管理 offset。
已归档文件已经确认输出，其余 active 都须由屏障确认；只确认 Root 所在文件是不合格实现。

独立 A 的依赖全部完成而 B 仍构建时，可以确认并发布 A。B 所租文件中若有较早完成、尚未确认且被 A 引用的 D，屏障必须连 D 一起确认，不能跳过 leased 文件。B 的内存内容、epoch 和租借不变，B 后续完成仍需新的确认。构建 B 期间也可按地址串行读取同文件历史帧 D，结果不由 active 分配位置决定。
若 A 真正引用尚未完成的 B，即使 ConfirmDurable 对现有完整输出成功，也不能发布该闭包；应用必须先完成 B。屏障没有业务图解析，不能从成功返回倒推出引用目标已完成。B 取消后 C 复用 b，也不能使旧 A↔B 状态获得原始来源资格。

在 A↔B 互引且尚未合法发布根的轨迹中，A 完成后 B 中断，A 只是完整 orphan；后续追加可能重用 b，旧构建状态不可直接采用。独立 A 若已经完成数据确认及根发布，无关 B 的中断或取消不能撤销 A 的发布。FrameStore 不知道哪个根已经对外发布。
若 ConfirmDurable 抛错，没有成功耐久返回，owning FrameStore 停止；完整 bytes 在重开后可存在，不等于此前调用被确认成功。
同一 owner 其他文件上的 Builder/Writer 也随共享 fault 停止，不能继续完成 B；资源清理由 owned Dispose 处理。

## 预算、实施片与验收

未归还 Builder 的数量上限由 S2 config 文件提供，超限立即拒绝；首版不建立精确总内存/总句柄的第二套配额账本。每帧容量沿用 RBF；当前 RBF Builder 在最终提交前保留整帧缓冲，多个 Builder 的成本相加，数量限制不等于内存硬保证。
成功 EndAppend 自动归还并释放数量配额，健康取消同样释放；可纠正拒绝保留。Ready 时在 S2 统一定稿 config 默认/解析/生效与计数，本阶段验证互引消费轨迹和实际资源成本；没有真实目标规模时不宣称吞吐/延迟或无限规模构建。只有真实大规模预约需求出现时，再讨论降低资源占用的 batch 优化。

1. S3-A：用 S2 public 追加入口形成 self-reference、双文件 A↔B 和乱序完成的最小消费者。
2. S3-B：验证嵌套申请、取消/reuse、文件复用和最大帧跨软阈值；资源预算遵循 S2。
3. S3-C：验证 S2 全 owner 完成输出 barrier 在 Builder 未归还、多 active/leased 首次再确认、后续新完成输出重新登记、旧/新帧混合及每个 flush/资源异常中的消费资格。
4. S3-D：public API 循环引用和进程中断资格；记录 IO、内存及真实预算。

覆盖跨文件 A↔B、单帧 self-reference、互引 A 完成/B 缺失、独立 A 已发布后无关 B 中断/取消不干扰 A、取消地址重用、生命周期/fault guard 拒绝、多个 active 首次再确认、跨文件 flush 中断及旧 root 采用。
FrameStore 单测不依赖发布库。没有公开 receipt 的伪造/过期测试；改测 owned 写入不可绕过、未归还 Builder 不阻断已完成输出的屏障且不获得资格、leased 文件旧 dirty 输出必须确认、后续新帧重新 dirty、成功返回前全部必要文件确认，以及 flush 失败停用所有 Builder。
同文件历史随机读取成功，未完成 ticket 和帧后 Fence 跨边界先拒绝；不通过第二个句柄或缓存逃过 owned fault。扫描等入口仍受原 guard，不据此声称并发读写资格。

## Ready 阻断项与出口

| ID | 需定稿 |
| --- | --- |
| S3-Q1 | 嵌套互引的 public 消费轨迹、数量 config/计数与实际资源成本；大规模 batch 是否有真实需求 |
| S3-Q2 | 成功自动归还已确认；值拷贝/重复操作、可纠正拒绝、取消/reuse、归档维护与资源异常资格 |
| S3-Q3 | 消费 S2 ConfirmDurable 的交错/多 active/leased/重开轨迹、后续新完成输出与失败向量；核心登记/错误统一由 S2-Q4 定稿 |
| S3-Q4 | 旧/orphan 帧来源、新旧闭包使用示例和地址重用资格 |

最终出口是预算内交错构建、跨文件互引与全 owner 同步耐久可独立验收。完整 FrameBatch 优化没有定稿/实现不阻塞本出口；消费者管理业务闭包，不管理各文件 flush 顺序。多线程执行另有独立合同与证据。
