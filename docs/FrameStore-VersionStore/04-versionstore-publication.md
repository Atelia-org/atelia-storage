# S4：VersionStore 提交对象、发布凭据与故障结果

状态：**Draft；2026-10-04 分离提交对象和 ref 发布，采用私有 FrameLog；codec/API 尚未冻结**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)。先形成无需名称的提交与 ref 核心。

## 本阶段目标与存储归属

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份，直接引用 FrameStore，不引用旧库/业务库。
VersionStore **借入一个 data FrameStore，拥有一个私有 control FrameLog**。状态帧与不可变提交对象使用 data 的无业务顺序分配合同；发布记录使用 S2 显式日志合同。控制记录不混入 data，FrameStore 不解释 VersionStore codec。

VersionStore 格式门持久绑定 data StoreId，并核对不同的 control StoreId/用途/版本。两者不得是同一存储。Open 先核对借入 store 的身份/访问模式，再打开控制 writer；错误 data context 不触发控制修尾。
StateRoot/CommitAddress 只解释于 data；发布位置只解释于 control。即使数字相同也不能互用；裸地址不能反推调用方原始来源，调用方仍需提供同 data 上下文的完成来源。
VersionStore Dispose 释放全部私有资源，不 Dispose 借入 data。data 必须在使用期间存活；写发布需要可写 data owner。双方及派生对象由同一个 driver 串行使用，不提供跨实例 CAS。

## term `Commit-Address` 不可变提交的地址

候选 CommitAddress 为 data FrameAddress 的窄 wrapper；其持久坐标继承 S2 上下文，不包含 control token、branch 名称或分配序号。它不自动证明提交已耐久或已经被发布，也不是内容 hash。

## 提交对象与三个不同身份

候选首版提交 codec 仅包含 kind/version、StateRoot 和可空单 ParentCommit。VersionStore 编解码该小帧；StateRoot 的 payload/meta 始终由应用解释。具体 tag/codec 标识、长度与 wire 在 S4-A 审定。

| 身份 | 所在上下文 | 含义 |
| --- | --- | --- |
| StateRoot: FrameAddress | data | 应用状态入口；状态帧之间可以循环引用 |
| CommitAddress | data | 不可变提交，引用 StateRoot 与 parent |
| RefRevision: RecordToken | control | 某次 ref 发布事实；不是 CommitAddress |

### spec [S-VS-COMMIT-IMMUTABLE] 提交创建不改变 ref

CreateCommit MUST 将一个已完成 StateRoot 与可选 parent 编码为一个不可变 data frame；成功返回完成提交的地址，不额外调用耐久屏障、不更新 ref/name，也不追加控制事实。底层必要轮转仍按 S2 执行，不能把完成返回值当作耐久确认。
发布已有提交不重写其对象。相同 StateRoot、不同 parent 或多次创建可以得到不同提交；首版不承诺去重、内容寻址或状态相同即提交相同。
提交及状态的新增依赖由后面的同步 data barrier 一并确认。提交输出失败遵守 data 的 owned fault；data 已 fault 时 VersionStore 同样停用，但不虚称 control 已输出失败。完整 orphan 可保留，未知输出不得被当作成功签发的提交或自动重试。

### spec [S-VS-PARENT-LOGICAL] 提交祖先使用逻辑引用

首版候选 ParentCommit MUST 为同 data 上下文中已有完整提交的地址或 null；CreateCommit 不接受未完成预约作为 parent，也不以 offset/文件编号/地址大小判断祖先。
正常创建只引用此前已完成且不再改写的提交，因此形成无环单 parent 关系；多个孩子共享 parent 可以分叉。应用状态帧的循环引用不扩大成提交 parent 的循环。
ReadCommit MUST 完整 CRC 读取并校验提交 codec、StateRoot/parent 地址字段，再返回小型 CommitInfo；不读取整个应用图，也不将任意合法 bytes 宣称为真实创建来源。
正常创建的来源约束、重开后提交资格与是否检查 parent 链须在 S4-Q6 定稿。外部伪造/改写及已知 parent 环不能用正常 writer 的无环证明掩盖；显式审计应报告错误。

（Informative）同一个提交可被多个 branch/tag 复用。提交 parent 描述状态谱系，ref previous token 描述该对象的发布历史，两者没有一一对应关系。

## term `Record-Token` 控制事实身份

候选表示 `RecordToken = (ControlStoreId, FrameAddress, Nonce)`；Nonce 为每次 Prepare 新生成的非零 128-bit 随机值，取消后也不沿用。它是概率身份见证，不声称数学绝对无碰撞。
完整 append-only 控制记录的位置不再复用；Nonce 区分同起点被取消/截断后的不同尝试。不同完整位置的同 nonce 不成为同 token，不需要全历史 nonce 去重集合。
wire 内固定 control context 可由格式门绑定，避免每个字段重复存 StoreId；本条记录地址从实际主链位置取得，不必重复写进自己的 payload。

三个语义角色共用此表示，可保留窄的代码 wrapper 防误用，不另造三个分配器/时钟：

| 角色 | 表示 | 解决的问题 |
| --- | --- | --- |
| RefId | 创建记录 token | 稳定对象与名称分离 |
| RefRevision | 当前发布记录 token | C1→C2→C1 仍不等于旧 revision |
| 发布尝试身份 | Prepare 给出的本条 token | Unknown 查询及地址重用 |

## term `Prepared-Publication` 已准备但尚未输出的发布

一个 owner/epoch 绑定的 handle，含 token、不可变请求、调用证据及一个 control 已知尺寸 Builder。
Prepare 完成后调用方已经取得 token，Commit 才尝试发布输出；异常对象分配不是获得身份的唯一渠道。最多一个活跃 handle，它不是持久 pending 日志或已经成立的 ref。
PreparedPublication.Commit() 是发布调用，与 CreateCommit/CommitInfo 中的不可变提交对象不同；签名定稿时需消除命名混淆。

## term `Publication-Outcome` 调用方证据

`NotAttempted / Unknown / Confirmed`：未尝试本次控制记录、可能已有完整记录但未确认发布 flush、发布 flush 已返回。
Confirmed 单调；查询 Present 描述当前控制事实，不倒推过去调用已经收到 Confirmed。CreateCommit 的完成结果不是 PublicationOutcome。

## 最小操作和控制记录

| 操作 | 输入/效果 |
| --- | --- |
| CreateCommit / ReadCommit | 构造不可变 data 提交 / 读取 CommitInfo，不改变 ref |
| PrepareCreateRef | 已完成初始 commit 或 unborn，准备单条创建记录 |
| PreparePublish | RefId、expected revision、已完成新/旧 commit，准备单条 ref 更新 |
| Prepared.Commit / Dispose | 尝试发布 / 取消未输出准备 |
| ReadHead | 返回 ref、revision、nullable CommitAddress；仅查询已接受发布事实 |
| InspectPublication | 用已持 token/请求见证确认精确事实，不只比较 head |

候选控制字段：version/kind、本条 nonce、创建以外的 ref token、前一 ref token、new CommitAddress。**expected revision 就是前一 ref token**，不重复保存 old commit、StateRoot、commit parent 或另一个物理前驱位置。
前一 ref token 供 ref 发布历史定位，**不是 commit parent，也不是直接物理前驱**；不能用它单独证明主链位置。时间戳/reason/source provenance 非核心必需。
创建 RefId 从本条 token 派生，不先写 allocation/Init。unborn 有合法创建 revision，null commit 不等于 missing/default ref；首版发布接口不提供将既有 head 清空的隐含操作。

## 候选发布合同

### spec [S-VS-PUBLICATION-ORDER] 控制顺序来自显式日志

VersionStore MUST 用 control FrameLog 的真实追加顺序接受控制事实，不从 data 地址或提交创建次序推算当前 head。首版所有发布由单 driver 串行进入同一私有日志；它是 per-ref CAS 与名称唯一性的实现方式，不要求数据帧存在同样的全序。
commit parent 链不能替代控制日志发现或名称恢复；若未来改用不可变 catalog 根发布，必须另行定义最后有效控制根的持久选择协议，不复用本版回放资格。

### spec [S-VS-SINGLE-RECORD] 一次 ref 变更由一个 frame 生效

一次 ref 创建/更新 MUST 由一条完整控制记录表达。CreateCommit、Prepare、checkpoint、data flush 不改变当前 head。
Unknown kind/version、坏控制 CRC、非法 token/版本链不是 incomplete append；不得忽略、补造或回退更早 head。控制记录全由私有 owner 写入。

### spec [A-VS-PREPARE-TOKEN] 输出前交付精确 token

Prepare MUST 先检查 ref/expected/commit、名称等确定输入、codec 长度/容量及可预测内存准备；按需完成操作前 checkpoint、控制轮转，最后建立 sized Builder 并返回 handle。
目标来源限成功 CreateCommit、已接受旧发布中的提交，或显式重新确认/重建闭包的完整 orphan。候选 Prepare 对目标执行 ReadCommit，确认当前完整内容和 codec；unborn 创建不读取目标。该读取不验证应用状态图，也不独自证明原始创建来源。
确定的 owner/CAS/未完成 data plan/Builder 拒绝先于目标读取；无活跃 data 构建时才准备发布。重开后如何恢复可接受目标来源属于 S4-Q6；guard 不声称从裸数字证明来源或解析 opaque 闭包。
签发 handle 之前的分配失败须清理已建立 Builder；清理异常不承诺继续使用。控制 metadata 输出异常让相关 owner/composite 停止，本次 publication 仍 NotAttempted。
准备控制 payload 在内存中编码完成，尚不 EndAppend。活跃 handle 排斥其他 VersionStore 操作，特别是读取/扫描/checkpoint/轮转控制文件；token/outcome 等纯值可访问。借入 data 的独立构建按下面的 Commit guard 处理。
调用方若需要进程终止后的精确追踪，须在 Commit 前保存 token/请求；库不另写 pending 账本。

### spec [A-VS-TOKEN-CAS] CAS 与 handle guard 在 IO 前

替代草案 `[A-VS-REVISION-CAS]`（DEPRECATED）。CAS MUST 比较当前 RefRevision token，不比较提交地址或 StateRoot。
错误 owner/epoch、default/已终结/重复 Commit、CAS 不匹配及未完成 data plan/Builder 的 guard 必须先于 data barrier。拒绝不追加、不修改 ref、不因为 guard 而 fault。
一个 handle 至多一次 Commit 尝试；终结状态不能被第二次调用改写，旧 Confirmed 证据尤其不能被覆盖。Prepare 后其他 data 分配是独立操作；Commit 必须重验其最新完成状态。

### spec [S-VS-COMMIT-BARRIER] Commit 同步确认最新依赖再发布

替代草案 `[S-VS-DEPENDENCIES-FIRST]`（DEPRECATED）：不接收 receipt，以本次同步调用建立顺序。
Commit MUST 执行 data ConfirmDurable → control EndAppend → control ConfirmDurable → 内部 ref 投影安装。
屏障覆盖目标提交及 Prepare 后所有已完成 data 输出；失败不得进入 control EndAppend。control Builder 与 data 是不同 owner，不妨碍这次串行确认。
EndAppend 成功先结束 Building，才可确认 control。过程中不调用业务 callback，也不允许另一项应用操作交错。旧提交走同一屏障，不要求重写数据。
消费者保证合法状态闭包已构建完成；VersionStore 负责自己提交格式的结构与 parent 来源合同。全部必要新增状态/提交依赖属于 data owner，引用多个独立 data stores 的事务不在此版本范围。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

进入 control 最终追加尝试后的异常 MUST 保守保留 Unknown，停用 VersionStore；control FrameLog 是否 fault 遵守其自身输出/资源边界，不能虚称 data 也发生输出失败。
确定的 pre-I/O Result 拒绝仍 NotAttempted；进入 EndAppend 后无法从异常证明未输出时不降为确定失败。释放不重试、不回滚。
control flush 返回后必须先设置 Confirmed，再安装内部投影/释放/返回；后续异常仍 Confirmed，composite 停止并重开。

### spec [R-VS-REPLAY-COMPLETE] 重开只解释完整控制事实

可写重开接受 data FrameStore/control FrameLog 各自的 RBF 恢复，再按真实 control 主链 checked-read 并验证 codec、ref identity 和 expected 链，形成投影。
CompletedTail 完整合法控制记录恢复其效果；Truncated 后无此记录则旧发布继续有效。完整坏控制记录不回退，不用消费者 validator 定义事实是否存在。
CommitAddress 字段校验 data context/地址格式；控制回放不 eager 读取全部提交、parent 链或状态图。发现坏必要提交/状态根时读取报错，不改已发布 head。只读不隐式修尾。

### spec [A-VS-INSPECT-MAIN-CHAIN] 精确查询来自真实主链

替代草案 `[A-VS-EXACT-INSPECTION]`（DEPRECATED）。首版 Inspect MUST 显式回放完整 control 主链，按实际 ticket 长度、完整 CRC 和控制因果验证，不依赖 snapshot/current head 的负面结果。

- 完整健康回放正常结束，唯一 token/请求匹配：Present，即使 head 后来移动或归档。
- 同样正常结束，无匹配：Absent。P 截断后 Q 同起点但不同长度，以 Q 的实际主链位置判断，不能把旧 ticket 读取失败当证据。
- context 不符、缺必要控制文件、framing/CRC/字段/因果错、冲突见证或 I/O：错误/Unverifiable，不能报告 Absent。

随机 checked ticket 不单独证明主链成员。未来若优化查询，必须另取受证边界/成员资格；首版诚实 O(全部控制记录) IO、临时当前 ref 投影，不建全部 nonce 索引、不自动 retry。Present 不替代目标提交或状态图的健康检查。

## 故障矩阵与两个内存安装责任

| 位置 | 本次发布证据 | owner/恢复 |
| --- | --- | --- |
| CreateCommit 输出失败 | 尚未进入发布；无本次 publication token | data 按自身边界 fault；data fault 时停用 VersionStore，完整 orphan 的采用另行确认 |
| 确定 guard / 重复 Commit | 未开始新尝试；旧 handle 证据不变 | 无发布输出，无额外 data flush |
| Prepare 的目标读取失败 | NotAttempted，尚未签发 handle | 内容/来源错误不得回退；I/O 按所涉 owner 合同处理 |
| Prepare 的 metadata/初始化失败 | NotAttempted | 相关 owner/composite 按输出/清理范围停止 |
| Commit data flush 失败 | NotAttempted | data fault；VersionStore 停止，control 只做取消释放 |
| control EndAppend/flush 异常 | Unknown，除非证明 pre-I/O 拒绝 | control 自身 fault 规则；重开查询完整事实 |
| control flush 返回，ref 投影安装/释放失败 | Confirmed | VersionStore 停止，重开恢复新版本 |
| Publish 返回后应用安装失败 | 已 Confirmed | 应用停止旧 materialized state，从已发布提交加载 StateRoot |

库内步骤为 `Validated → Prepared → DependenciesDurable → PublicationAttempted → PublicationDurable → RefProjectionInstalled`。
**应用安装**在收到 Confirmed 后由中层完成；库不会自动观测/回滚应用失败，也不因此声称健康 data owner 已 fault。应用安装不是另一条发布事实或 Commit callback。

## 实施片、预算与出口

1. S4-A：创建项目；定稿 CommitAddress/提交 codec、parent 来源、双 context 格式绑定、RecordToken/control codec 及 unborn。
2. S4-B：CreateCommit/ReadCommit、Prepare/Commit/取消、同步 barrier、内部投影与单记录完整回放，形成单文件 unnamed ref 纵向例子。
3. S4-C：Inspect、handle/owner/epoch、地址异长度重用、Confirmed/OOM/资源失败与冷重开。
4. S4-D：两种 opaque 状态根、提交分叉与 ref rewind、进程中断资格；完成控制日志多文件组合后才声明整个阶段 Accepted。

初始 Open/Inspect 明确 O(全部控制历史)，不扫描 data payload；内存基线 O(ref 数) 加当前 frame/请求缓冲，不建立全部 commit 索引。ReadCommit 定点读取小提交帧；完整祖先链及状态图成本另列。
测试用真实 FrameStore/FrameLog public API；覆盖同根不同提交、同提交多次发布、ABA、旧提交、data barrier 前 guard、data/control 独立 fault、cancel 复用、CompletedTail/Truncated、读取限制与两种内存安装失败。

| Ready 项 | 需定稿 |
| --- | --- |
| S4-Q1 | token/wrapper/local wire、nonce 生成及概率唯一性说明 |
| S4-Q2 | 提交与控制 codec/tag、Create/unborn/previous 的精确字段和地址检查 |
| S4-Q3 | data/control factory/用途/模式、Dispose 和 composite fault |
| S4-Q4 | Prepare/Commit 句柄生命周期、提交创建结果、确定拒绝和保守 Unknown 边界 |
| S4-Q5 | Inspect 返回/异常、完整回放预算与 token 保存示例 |
| S4-Q6 | parent/目标来源、冷重开后的资格恢复、orphan 采用及校验成本边界 |

出口是独立的不可变提交与根发布核心。初始全量控制回放不是最终启动规模资格；数据与控制分离也不替代实际平台/包/消费者验收。
