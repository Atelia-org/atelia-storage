# S1：RBF 精确尺寸、已知尺寸追加与格式资格

状态：**Draft，阶段候选要求与建议 API，未实施**。
前置规范：[S0](00-architecture-decisions.md)及现有 [RBF 接口](../Rbf/rbf-interface.md)、[格式](../Rbf/rbf-format.md)。本阶段不依赖 FrameStore 或 VersionStore。

## 本阶段目标与现状

增加单文件能力：在内容写入前准确计算追加尺寸，并在打开已知尺寸 Builder 时取得锁定本次追加位置的 ticket。
当前 `IRbfFile.BeginAppend()` 不接受尺寸，ticket 由 `EndAppend()` 返回；一个文件只允许一个活跃 Builder。
70d1009 已澄清初始化先完成再发布 Building、最终 Commit 异常保守 fault、取消时资源异常可传播，新增入口须继承这些边界。

## term `Frame-Measure` 精确物理尺寸

给定 stored payload/meta 长度得到的结果，至少区分 `FrameLength`（SizedPtr 长度，不含尾 Fence）与 `AppendLength`（TailOffset 推进量，含尾 Fence）。它不预留文件位置。

## term `Sized-Append` 已知尺寸追加

在当前文件尾部建立唯一活跃 Builder，声明写入长度并提前返回本次 frame 的 ticket。提前 ticket 只有位置和尺寸资格，没有读取、耐久或发布资格。

## 候选合同

### spec [A-RBF-MEASURE-WRITE-SIZE] 提供精确新写尺寸计算

替代草案 `[A-RBF-EXACT-MEASURE]`（DEPRECATED）：这是后续层必须消费的基础能力，不再只是建议提供。

RBF MUST 提供无 I/O 的确定计算，输入是最终 stored payload 和 TailMeta 长度；参数验证、padding、开销与真正 Append 使用同一个布局实现。
结果 MUST 区分 frame 长度和追加占用，检查负数、meta 上限、合计容量及算术溢出。不得要求下游知道 EscapeKey、长度单位或固定开销。
新 writer 只采用 RBF3，因此新写尺寸接口不需要公共 profile 参数。RBF1 读取容量继续按原 profile 解释。

（Informative）当前 RBF3 为 `L = 28 + Align4(payload + meta)`、追加推进 `L + 4`。此公式说明需求；实现和后续消费者应调用统一计算入口，而不是各自复制公式。

### spec [A-RBF-EXPOSE-OPENED-FORMAT] 提供已打开文件的格式信息

RBF MUST 提供只读、不可变的已打开格式信息，例如 `IRbfFile.Format` 的 RBF1/RBF3 enum 值；复用 Header 分派结果，不另读文件，不由本接口选择 writer profile。
当前 `RbfProfile` 是 internal、只读 factory 同时接受两个格式，文件为空时二者长度也相同；下游不能用只读状态、长度或可写探测代替格式资格。
该属性允许新库的只读历史文件明确拒绝 RBF1，不在下游复制 Header 字节。RBF1 的原只读容量仍保留。

这是打开后检查；非法 RBF1 可能先付出原有 O(历史) 只读资格成本。只有真实的早拒需求成立时再评估 factory 的 expected-format guard，不同时增加另一套探测工具。

### spec [A-RBF-EARLY-TICKET] 已知尺寸 Builder 提前给出 ticket

新入口 MUST 在成功建立 Builder 后给出 ticket，位置绑定当前追加起点；Builder 活跃期间禁止同文件其他追加。
所有可预测参数、位置和资源准备拒绝 MUST 先于向调用方签发可用 Builder。最终成功 ticket MUST 与提前 ticket 相等。
提前 ticket 不推进 TailOffset，不创建可读的已完成 frame，也不意味着 bytes 已输出。Dispose/取消后该追加未完成，原位置可能被复用。

### spec [S-RBF-DECLARED-LENGTH] 写入长度与声明一致

完成时 MUST 验证实际 payload/meta 长度与声明；超写在 Advance/可提交长度边界拒绝，短写不能完成。需要规定借出的 span 容量大于剩余长度时的行为。
拒绝属于 pre-I/O 参数/state 还是进入最终提交后的 fault，MUST 与现有 preparation/Commit 边界一致。不能因为长度不符而输出可被恢复成另一条有效帧的声明。

### spec [R-RBF-SIZED-LIFECYCLE] 保留现有提交与取消边界

已知尺寸入口 MUST 使用现有 Builder 生命周期及串行规则，不创建第二个独立的提交权威。
健康取消不输出帧且不推进 tail；资源归还异常不承诺继续使用。最终 Commit 开始后异常永久 fault，Dispose 不重试发布。重开接受 RBF3 的 Truncated / CompletedTail / None，报告不替代调用结果。

### spec [S-RBF-NO-BATCH-HOLES] 单文件不引入预留空洞

本阶段 MUST 保持每文件一个活跃 Builder；只锁定当前帧，不为后续帧写空洞或绕过物理顺序。不同文件可分别打开一个 SizedAppend；其组合原子性属于上层。

## 建议 API 形态（未冻结）

建议采用一个尺寸结果值和一个 `BeginAppend(payloadLength, tailMetaLength)` 重载；已知尺寸 Builder 具有提前 ticket 的可访问入口。已打开格式通过一个纯属性暴露。
未知尺寸的既有 BeginAppend 保留当前用途。需要审定：共用 Builder 类型并按模式提供 ticket，还是使用窄的已知尺寸 Builder 类型；哪一种能避免调用方误用未声明尺寸的 Ticket。

实现审定还需明确 tag/meta 在 Begin 还是 End 提供，AteliaResult 与异常形态，以及 stale epoch、重复 End、default Builder 的拒绝行为。
方法名称、参数表和类型布局在本阶段 Ready 时一次闭合，不能让调用方根据实现内部字段预测 ticket。
空新文件的起点可使用既有 `RbfScanBoundary.Empty.EndExclusive`；不增加另一套 HeaderSize/长度公式。

## 状态与失败轨迹

`Idle → Preparing → Building(ticket known) → Finalizing → CommitAttempt → Idle(frame complete)`。
Preparing 失败不签发 Builder；Building 的可纠正 pre-I/O 拒绝遵守既有合同；CommitAttempt 异常进入 Faulted。任何取消路径均不追认提前 ticket 为已完成地址。

| 场景 | 必须保有的证据 |
| --- | --- |
| Begin 资源准备失败 | 原文件与 tail 未变，无幽灵 Building |
| 少写、超写、meta 声明冲突 | 有确定拒绝，错误没有进入输出 |
| A/B 两文件先取 ticket 再互引 | 完成后两个读取结果及地址精确一致 |
| A 完成、B 取消 | A 可存在，B ticket 不能得到完成资格 |
| 最终输出中断 | 重开后的存在/不存在由 RBF 决定，提前 ticket 不证明成功 |
| 完成后资源释放异常 | 按最新保守 fault 合同停用并重开 |

## 实施片与独立验收

1. S1-A：审定尺寸 DTO/API、已打开格式信息、公开 XML；实现纯计算，共用 layout，增加尺寸向量、空/非空 RBF1/RBF3 格式及边界测试。
2. S1-B：审定已知尺寸 Builder；实现声明长度、提前 ticket 及单一 epoch/state 路径。
3. S1-C：补生命周期、资源异常、跨文件互引和输出中断资格；复验现有未知尺寸入口。

测试归 `tests/Rbf.Tests`。这里验证 RBF 新增 public 能力，后续 FrameStore 不重复底层 EscapeKey/CRC 算法测试。
按仓库要求先 build Release，再匹配配置 test；只引用 RBF 的独立消费例子应能执行尺寸计算和双文件互引。

## 进入 Ready 前的阻断项

| ID | 必须关闭的问题 | 建议起点 |
| --- | --- | --- |
| S1-Q1 | 尺寸输入/返回值及错误形态 | 两个长度输入，显式 frame/append 两个长度 |
| S1-Q2 | 提前 ticket 的类型和有效期 | 限已知尺寸模式，共用 owned epoch |
| S1-Q3 | 超写/短写纠正及取消规则 | 将确定长度拒绝放在输出前 |
| S1-Q4 | 长度校验与最终 Commit 的资源边界 | 继承 70d1009 澄清后的合同 |
| S1-Q5 | 格式属性的 public 类型/值及资格成本 | 已解析 Header 的 immutable 值，不选择 writer profile |

## 出口

单文件已知尺寸 append、纯尺寸计算、已打开格式信息及所有 ticket/lifecycle 拒绝行为已审定、实现并独立验收；不存在 FrameStore 反向依赖。具体测试与 commit 身份填写本阶段验收记录后，阶段才能标 Accepted。
