# S4：VersionStore 根发布、准备凭据与故障结果

状态：**Draft；采用私有控制存储与 Prepare/Commit，codec/API 尚未冻结**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)。先形成无需名称的 ref 核心。

## 本阶段目标与存储归属

创建 `src/VersionStore/VersionStore.csproj`、`tests/VersionStore.Tests/VersionStore.Tests.csproj`，采用 `Atelia.VersionStore` 身份，直接引用 FrameStore，不引用旧库/业务库。
VersionStore **借入一个 data FrameStore，拥有一个私有 control FrameStore**。两者均用 S2 单流合同；控制记录不混入 data，FrameStore 无需认识 VersionStore tag 或额外 StreamId。

VersionStore 格式门持久绑定 data StoreId，并核对不同的 control StoreId/版本。两者不得是同一存储。Open 先核对借入 store 的身份/访问模式，再打开控制 writer；错误 data context 不触发控制修尾。
Root 的 FrameAddress 只解释于 data；发布 locator 只解释于 control。即使数字相同也不能互用；裸 root 地址不能反推调用方原始来源，消费者仍需提供同 data 上下文的完成来源。
VersionStore Dispose 释放全部私有资源，不 Dispose 借入 data。data 必须在使用期间存活；写发布需要可写 data owner。双方及派生对象由同一个 driver 串行使用，不提供跨实例 CAS。

## term `Record-Token` 控制事实身份

候选表示 `RecordToken = (ControlStoreId, FrameAddress, Nonce)`；Nonce 为每次 Prepare 新生成的非零 128-bit 随机值，取消后也不沿用。它是概率身份见证，不声称数学绝对无碰撞。
完整 append-only 控制记录的位置不再复用；Nonce 区分同起点被取消/截断后的不同尝试。不同完整位置的同 nonce 不成为同 token，不需要全历史 nonce 去重集合。
wire 内固定 control context 可由格式门绑定，避免每个字段重复存 StoreId；本条记录地址从实际主链位置取得，不必重复写进自己的 payload。

三个语义角色共用此表示，可保留窄的代码 wrapper 防误用，不另造三个分配器/时钟：

| 角色 | 表示 | 解决的问题 |
| --- | --- | --- |
| RefId | 创建记录 token | 稳定对象与名称分离 |
| RefRevision | 当前记录 token | A→B→A 仍不等于旧 revision |
| 发布尝试身份 | Prepare 给出的本条 token | Unknown 查询及地址重用 |

## term `Prepared-Publication` 已准备但尚未输出的发布

一个 owner/epoch 绑定的 handle，含 token、不可变请求、调用证据及一个 control 已知尺寸 Builder。
Prepare 完成后调用方已经取得 token，Commit 才尝试业务输出；异常对象分配不是获得身份的唯一渠道。最多一个活跃 handle，它不是持久 pending 日志或已经成立的 ref。

## term `Publication-Outcome` 调用方证据

`NotAttempted / Unknown / Confirmed`：未尝试本次业务记录、可能已有完整记录但未确认发布 flush、发布 flush 已返回。
Confirmed 单调；查询 Present 描述当前控制事实，不倒推过去调用已经收到 Confirmed。

## 最小操作和控制记录

| 操作 | 输入/效果 |
| --- | --- |
| PrepareCreateRef | 已完成初始 root 或 unborn，准备单条创建记录 |
| PreparePublish | RefId、expected token、已完成新/旧 root，准备单条根变更 |
| Prepared.Commit / Dispose | 尝试发布 / 取消未输出准备 |
| ReadHead | 返回 ref、revision、nullable data root；仅查询已接受发布事实 |
| InspectPublication | 用已持 token/请求见证确认精确事实，不只比较 head |

候选记录字段：version/kind、本条 nonce、创建以外的 ref token、前一 ref token、new root。**expected revision 就是前一 ref token**，不重复保存 expected/actual previous/old root/另一个前驱位置。
前一 ref token 供业务版本与历史定位，**不是直接物理前驱**；不能用它单独证明主链位置。时间戳/reason/source provenance 非核心必需。
创建 RefId 从本条 token 派生，不先写 allocation/Init。unborn 有合法创建 revision，null root 不等于 missing/default ref。

## 候选合同

### spec [S-VS-SINGLE-RECORD] 一次根变更由一个 frame 生效

一次创建/根变更 MUST 由一条完整控制记录表达。Prepare、checkpoint、data flush 不改变当前根。
Unknown kind/version、坏控制 CRC、非法 token/版本链不是 incomplete append；不得忽略、补造或回退更早根。控制记录全由私有 owner 写入。

### spec [A-VS-PREPARE-TOKEN] 输出前交付精确 token

Prepare MUST 先检查 ref/expected/root、名称等确定输入、codec 长度/容量及可预测内存准备；按需完成操作前 checkpoint、控制轮转，最后建立 sized Builder 并返回 handle。
根来源限本 data owner 成功追加的帧、已接受旧发布或显式重新确认/重建闭包的完整 orphan；guard 检查地址值域/上下文，不声称能从裸数字证明原始来源或解析 opaque 闭包。
签发 handle 之前的分配失败须清理已建立 Builder；清理异常不承诺继续使用。控制 metadata 输出异常让相关 owner/composite 停止，本次 publication 仍 NotAttempted。
准备 payload 在内存中编码完成，尚不 EndAppend。活跃 handle 排斥其他 VersionStore 操作，特别是读取/扫描/checkpoint/轮转控制文件；token/outcome 等纯值可访问。
调用方若需要进程终止后的精确追踪，须在 Commit 前保存 token/请求；库不另写 pending 账本。

### spec [A-VS-TOKEN-CAS] CAS 与 handle guard 在 IO 前

替代草案 `[A-VS-REVISION-CAS]`（DEPRECATED）。CAS MUST 比较当前 RefRevision token，不比较 root。
错误 owner/epoch、default/已终结/重复 Commit、CAS 不匹配及未完成 data plan/Builder 的 guard 必须先于 data barrier。拒绝不追加、不修改 ref、不因为 guard 而 fault。
一个 handle 至多一次 Commit 尝试；终结状态不能被第二次调用改写，旧 Confirmed 证据尤其不能被覆盖。Prepare 后其他 data 追加是独立操作；Commit 必须重验其最新完成状态。

### spec [S-VS-COMMIT-BARRIER] Commit 同步确认最新依赖再发布

替代草案 `[S-VS-DEPENDENCIES-FIRST]`（DEPRECATED）：不接收 receipt，以本次同步调用建立顺序。
Commit MUST 执行 data ConfirmDurable → control EndAppend → control ConfirmDurable → 内部 ref 投影安装。
屏障在 Commit 内，覆盖 Prepare 后所有已完成 data 输出；失败不得进入 control EndAppend。control Builder 与 data 是不同 owner，不妨碍这次串行确认。
EndAppend 成功先结束 Building，才可确认 control。过程中不调用业务 callback，也不允许另一项应用操作交错。旧 root 走同一屏障，不要求重写数据。
消费者保证合法闭包已构建完成；根及其新增依赖属于 data owner，引用多个独立 data stores 的事务不在此版本范围。

### spec [R-VS-PUBLICATION-UNKNOWN] 输出异常保留实际证据

进入 control 最终追加尝试后的异常 MUST 保守保留 Unknown，停用 VersionStore；control FrameStore 是否 fault 遵守其自身输出/资源边界，不能虚称 data 也发生输出失败。
确定的 pre-I/O Result 拒绝仍 NotAttempted；进入 EndAppend 后无法从异常证明未输出时不降为确定失败。释放不重试、不回滚。
control flush 返回后必须先设置 Confirmed，再安装内部投影/释放/返回；后续异常仍 Confirmed，composite 停止并重开。

### spec [R-VS-REPLAY-COMPLETE] 重开只解释完整控制事实

可写重开接受各自 FrameStore/RBF 恢复，再按真实 control 主链 checked-read 并验证 codec、ref identity 和 expected 链，形成投影。
CompletedTail 完整合法记录恢复其效果；Truncated 后无此记录则旧发布继续有效。完整坏控制记录不回退，不用消费者 validator 定义事实是否存在。
Root 字段校验 data context/地址格式；完整图闭包及当前 root 内容健康不由控制回放自动证明。发现坏根时读取报错，不改已发布 head。只读不隐式修尾。

### spec [A-VS-INSPECT-MAIN-CHAIN] 精确查询来自真实主链

替代草案 `[A-VS-EXACT-INSPECTION]`（DEPRECATED）。首版 Inspect MUST 显式回放完整 control 主链，按实际 ticket 长度、完整 CRC 和业务因果验证，不依赖 snapshot/current head 的负面结果。

- 完整健康回放正常结束，唯一 token/请求匹配：Present，即使 head 后来移动或归档。
- 同样正常结束，无匹配：Absent。P 截断后 Q 同起点但不同长度，以 Q 的实际主链位置判断，不能把旧 ticket 读取失败当证据。
- context 不符、缺必要文件、framing/CRC/字段/因果错、冲突见证或 I/O：错误/Unverifiable，不能报告 Absent。

随机 checked ticket 不单独证明主链成员。未来若优化查询，必须另取受证边界/成员资格；首版诚实 O(全部控制记录) IO、临时当前 ref 投影，不建全部 nonce 索引、不自动 retry。

## 故障矩阵与两个内存安装责任

| 位置 | 本次证据 | owner/恢复 |
| --- | --- | --- |
| 确定 guard / 重复 Commit | 未开始新尝试；旧 handle 证据不变 | 无业务输出，无额外 data flush |
| Prepare 的 metadata/初始化失败 | NotAttempted | 相关 owner/composite 按输出/清理范围停止 |
| Commit data flush 失败 | NotAttempted | data fault；VersionStore 停止，control 只做取消释放 |
| control EndAppend/flush 异常 | Unknown，除非证明 pre-I/O 拒绝 | control 自身 fault 规则；重开查询完整事实 |
| control flush 返回，ref 投影安装/释放失败 | Confirmed | VersionStore 停止，重开恢复新版本 |
| Publish 返回后应用安装失败 | 已 Confirmed | 应用停止旧 materialized state，加载已发布 root |

库内步骤为 `Validated → Prepared → DependenciesDurable → PublicationAttempted → PublicationDurable → RefProjectionInstalled`。
**应用安装**在收到 Confirmed 后由中层完成；库不会自动观测/回滚应用失败，也不因此声称健康 data owner 已 fault。应用安装不是另一条发布事实或 Commit callback。

## 实施片、预算与出口

1. S4-A：创建项目；定稿双 context 格式绑定、RecordToken/local codec、unborn/前驱与拒绝规则。
2. S4-B：Prepare/Commit/取消、同步 barrier、内部投影与单记录完整回放，形成单段 unnamed ref 纵向例子。
3. S4-C：Inspect、handle/owner/epoch、地址异长度重用、Confirmed/OOM/资源失败与冷重开。
4. S4-D：两种 opaque 根及进程中断资格；完成控制多段组合后才声明整个阶段 Accepted。

初始 Open/Inspect 明确 O(全部控制历史)，数据扫描不计入它；内存基线 O(ref 数) 加当前 frame/请求缓冲，完整历史流式读取。索引不是发布正确性的前置。
测试用真实 FrameStore public API；覆盖 ABA、旧 root、data barrier 前 guard、data/control 单独 fault、cancel 复用、CompletedTail/Truncated、读取限制与两种内存安装失败。

| Ready 项 | 需定稿 |
| --- | --- |
| S4-Q1 | token/wrapper/local wire、nonce 生成及概率唯一性说明 |
| S4-Q2 | 操作 codec、Create/unborn/previous 的精确字段和地址检查 |
| S4-Q3 | 双 context factory/format/模式、Dispose 和 composite fault |
| S4-Q4 | Prepare/Commit 句柄生命周期、确定拒绝和保守 Unknown 边界 |
| S4-Q5 | Inspect 返回/异常、完整回放预算与 token 保存示例 |

出口是独立根发布核心。初始全量控制回放不是最终启动规模资格；数据与控制分离也不替代实际平台/包/消费者验收。
