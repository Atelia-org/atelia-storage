# S5：ref 历史、不可变 tag 与 branch 名称

状态：**Draft；2026-10-07 采用完整 RootMap、真实 ref 历史枚举、单文件 tag 桶和 create-only branch 绑定；API、路径及 codec 尚未实施/冻结**。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个 ref 的当前/历史值以及 tag 的内容都为应用解释的完整 `{key => FrameAddress}`。ref 有独立稳定 RefId；branch 只绑定名称到已经正式发布的 ref；tag 冻结创建时的字典，而不是跟踪 ref。两类名称空间独立。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadRefHistory(refId, budget) | 从固定上界向前枚举 checked 快照 | 真实 ref RBF3 文件链，无另写索引 |
| CreateTag(name, roots) / ResolveTag(name) | 创建/读取不可变完整字典 | 目标桶的一条完整 tag record |
| CreateBranch(name, refId) / ResolveBranch(name) | 建立/解析不可变名称绑定 | 一个 create-only 绑定记录文件 |
| ListBranches | 枚举正式 branch 名称与 RefId | 正式名称文件集合 |
| Fork / rewind 便利流程 | 旧字典新建 ref / 追加回原 ref | 调用 S4 的基础操作 |

首版不提供 branch rename/unbind/delete/archive/name reuse、tag 修改/删除、差分或 state-checkpoint。fork 不是跨文件事务；通用应用谱系与 ReadAncestors 不在 VersionStore 中定义。

## ref 历史与反事实分支

### spec [A-VS-REF-HISTORY-CHECKED] 历史枚举返回真实且自有的快照

ReadRefHistory MUST 先 checked-read 本 ref 的实际末 Snapshot，取得固定完整上界 revision，再按真实 RBF 逆向链枚举各条 Snapshot；每条执行完整 CRC、kind/version、身份与 RootMap codec 检查。它包含重复同值快照和 rewind，不用地址大小、应用 parent 或独立 previous 字段推算成员。
枚举 MUST 使用 `showTombstone: true`，只跳过该版本明确规定的库 meta/header；未知 kind/version、tombstone、损坏或 RBF `TerminationError` 必须显式传播，不能静默跳过或当正常历史结束。必须区分遇到合法初始化边界、达到调用方预算、取消/释放与错误；有限预算不宣称全历史审计成功。
返回的 RootMap MUST 是自有完整值，枚举结束或释放后仍可拿来 fork/tag/rewind；不得暴露受 enumerator/pool 生命周期约束的 Span、RbfFrame 或字典。RefRevision 绑定真实位置，首版不提供任意裸 revision 的随机历史读取。
首版同 owner 有活跃历史枚举器时 MUST 拒绝 mutation，枚举器结束/Dispose 后解除；序列/枚举器须有明确 owner/epoch 和资源归还。固定上界及该排斥保证历史不会被本次查询中插入的新发布改变，跨实例并发读取/写入资格不作首版承诺。

（Informative）Gym 选择任意旧快照 `R`：fork 调用 `CreateRef(R.Roots)` 得到新 RefId，再可选绑定 branch，旧 ref 不变；rewind 调用 `PublishRef(oldRefId, R.Roots)` 追加新 revision，保留原历史。data 根帧被共享，不复制其图。应用可在其状态帧内保留 tick、RNG、父状态、分支来源或推演规则。

## 不可变 tag 与固定分桶

### spec [S-VS-TAG-ROOTS-IMMUTABLE] tag 一次冻结完整根字典

每个 tag MUST 保存 `TagName + 完整 RootMap` 的一条 RBF3 record，使用 S4 的私有拷贝、输入/容量 guard、data ConfirmDurable、Append 与 DurableFlush 协议。tag 从某 ref/历史快照创建时复制当时字典，不持有可变 ref 间接绑定。同名再创建 MUST 在输出前拒绝，即使字典同值。
TagName 的比较采用明确稳定的名称政策；默认候选为 Ordinal。路由 MUST 使用固定、跨进程复现的字符串 hash 与固定桶规则，不使用进程随机化的 string.GetHashCode。记录保留完整原名；hash 相同但名称不同不是同名，MUST 按完整名字比较。
首版每桶一个 RBF3 文件、不轮转；库 meta/header 及桶身份必须 checked。首次只在私有文件写完整 header，flush/close 后 create-only 公开空桶，再按 S4 普通 CreateTag 的 Append/flush 协议发布首条 tag。空桶初始化只是 metadata 准备，不提前设置本次 tag Confirmed；首条 tag 不随桶 rename 一起生效。空桶 rename 异常但尚未尝试 tag Append 时，本次 tag 为 NotAttempted，VersionStore 停用并重开检查桶状态；已正式发布的合法空桶保留。不得发布半个有效桶，桶为空不同于格式缺失或损坏。下一条记录的起点超过 SizedPtr.MaxOffset 时明确拒绝/维护，不隐式新建分段；合法末记录可越过起点上界，与 S4 同一容量规则。
查找与重名检查需扫描目标桶，或按需建立可重建的内存 name→实际记录位置表；建表需真实链 checked-read 与正常结束，不能将错误当未找到。ResolveTag 返回选定记录的 checked、自有 RootMap；缓存不能提供永久 CRC 健康保证。发现目标桶的未知记录/损坏/冲突名须报错。磁盘索引不属于首版。

## branch 只命名既有 ref

### spec [S-VS-REF-ID-STABLE] 名称与稳定 ref 身份分离

branch 名称 MUST 绑定已正式发布的 stable RefId，不把名称本身当作快照 revision。解析后的更新使用 exact RefId；多个 branch 可命名同一 ref，不为此复制 ref/data。RefId 不因 RootMap 同值而合并，也不随 fork 复用源 ref 身份。
首版创建后名称绑定不再改变；不存在同名删除再建的身份重用。空 RootMap ref 有效，missing ref 不被隐式创建；损坏 ref 不被当 missing。绑定目标 ref 缺失/身份不符时报错，不按名称选择其他对象。

### spec [S-VS-BRANCH-BIND-ATOMIC] branch 名称绑定由一个正式文件生效

CreateBranch MUST 先验证名字、目标已发布 ref 与重名准入，将 version/kind、完整原名和 RefId 编码到私有记录文件，flush/close 后同文件系统 no-overwrite rename 到正式路径；正式安装成功才设置 Confirmed。失败/不确定结果使用 S4 PublicationOutcome 与停用/重开规则，不在私有 flush 后宣称名称已建立。
名称到路径的安全编码/hash 路由在 Ready 定稿；任何 hash 冲突必须完整名字核对且不能误覆盖另一名称。非法路径字符、保留名、大小写差异不得被 OS 路径语义悄悄转换成错误名称绑定。该 create-only 单记录遵守 S4 的 owner 模式/生命周期准入，但无需再次遍历/flush data 图，也不因无关 data Builder 尚未完成而拒绝：它只引用已经存在的 ref，不发布新的 RootMap；自己的绑定记录仍必须 flush/close 并正式发布。
`CreateRef(roots)` 与 `CreateBranch(name, refId)` 是两个独立步骤，MUST NOT 宣称跨文件原子。绑定失败可留下有效且由 S4 ListRefs 可发现的 unbound ref；不追加 allocation 操作日志，不回滚或删除已发布 ref。便利 CreateBranchFromRoots/Fork 若提供，必须保留这一结果语义。

## 查询、启动与成本

### spec [A-VS-ROOTS-QUERY-THEN-READ] 查询根字典后由应用接受内容

ReadRef/ResolveTag MUST 返回 checked 发布记录里的 RootMap，ReadRef 同时返回实际 revision；名称解析返回 stable RefId。发布记录健康不证明引用帧或业务图健康。应用按这些地址调用 data FrameStore.ReadFrame 完整读取和解码；缺/坏必要数据报错，不改 RootMap、回退 ref 或重绑定 tag。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

Open MUST NOT 默认回放全部 ref 历史或扫描 data 图，也不因此宣称全库 O(1)。目录/格式门、名称及文件枚举的实际成本须报告；惰性打开只把检查延后到目标访问，不把未检查对象说成健康。
当前 ReadRef 成本为 header 与完整末 Snapshot bytes，不随本 ref 历史帧数增长；ReadRefHistory 为 O(请求快照数 + 对应 bytes)，预算未覆盖的历史不计作已验证。tag 首次查找/重名检查的扫描为 O(目标桶记录数 + bytes)，建表的内存为 O(该桶名称数)，后续定位仍需选中记录读取。不承诺分桶即可 O(1) 查询或长期无限容量。
ListRefs/ListBranches 成本与正式文件/名称数相关，不要求加载这些 ref 的全部历史；资源、文件句柄缓存及枚举生命期单列。首版无 control 全量投影、全部尝试账本、磁盘 catalog/checkpoint 或隐式第二套索引。

## 工程 codec 与后续扩展

| 单元 | 最小字段 | Ready 选择 |
| --- | --- | --- |
| tag 桶 header | version/kind、VersionStoreId、桶身份 | tag、初始化边界、桶数量/hash 与文件路径 |
| tag record | version/kind、完整 TagName、S4 RootMap | 名称政策/限额及 codec 复用 |
| branch 绑定文件 | version/kind、完整 BranchName、RefId | RBF3 单记录或其他小型 checked 编码、路径碰撞与发现验证 |
| history 结果 | RefId、RefRevision、自有 RootMap | 枚举签名、预算/结束/错误与 Dispose |

未来 ref 分段/轮转、tag 分片、差分与可重建索引均为独立后续片，需实际容量/工作集证据及自己的定位/恢复合同；不预建 segment manifest 或 checkpoint 缓存。业务 Parent/RNG/operationId 可放在应用已引用对象中，不扩展中立字典 codec。外部工具 exactly-once、跨 ref 事务和自动 merge 均无框架保证。

## 实施片、Ready 工程定稿与验收

1. S5-A：逆向 history checked 枚举、固定上界、预算/错误/排斥 mutation、返回值所有权；形成冷重开选择旧快照的 public 例子。
2. S5-B：tag 名称/hash 分桶、唯一性和 data-first 单记录创建/查询；无持久索引。
3. S5-C：create-only branch 绑定、解析/列举、unbound ref 发现；fork/tag/rewind 便利流程与两步失败结果。
4. S5-D：LLM tool-loop 多根状态与 Gym 旧快照反事实消费轨迹、进程终止 failpoint 及独立 public 验收；真实文件实验使用 W:。

| Ready 项 | 需定稿 |
| --- | --- |
| S5-Q1 | 名称比较/编码/字符/限额、独立命名空间、bucket hash/数量/路径；同名拒绝 |
| S5-Q2 | history 结果/预算/错误/终止、owner/epoch/Dispose 与 mutation guard；不扩成随机 ReadRevision |
| S5-Q3 | header-only 空桶初始化/恢复与首 tag 正常追加、codec、扫描或惰性内存索引及 selected record checked-read |
| S5-Q4 | branch 绑定文件 codec、temp/flush/close/no-overwrite rename、碰撞、ListBranches/ListRefs 与便利流程结果 |

验收覆盖历史返回字典在枚举释放后仍可发布/创建 tag、R0→R1→R0 仍保留三个 revision、预算结束与损坏区分、tombstone/unknown/CRC/TerminationError 不跳过、枚举期间 mutation 拒绝及释放后恢复、空字典与 missing、hash 碰撞/同名同值拒绝、tag 不随 ref 更新、多个 branch 同 ref、fork 不改源/不复制 data、创建 ref 成功但绑定失败留下可发现 ref、应用数据读取坏时不回退。故障证据沿用 S4；首版 Accepted 不要求分段、差分、checkpoint、业务谱系或精确历史尝试追踪。

## 废弃合同导航

以下只保留历史锚点，不是首版能力；旧完整草案可从 Git 历史追溯。

### spec [S-VS-NAMES-ATOMIC] 命名变更仍单记录生效（DEPRECATED）

DEPRECATED；创建 ref 与命名不再是同一事实，替代为 `[S-VS-BRANCH-BIND-ATOMIC]` 与 `[S-VS-TAG-ROOTS-IMMUTABLE]`。

### spec [S-VS-STABLE-REFS] 名称与稳定 ref 身份分离（DEPRECATED）

DEPRECATED；移除 Commit token/CAS/Archive 范围，稳定身份由 `[S-VS-REF-ID-STABLE]` 定义。

### spec [A-VS-FACT-QUERY-THEN-READ] 地址选择与内容接受（DEPRECATED）

DEPRECATED；原提交地址查询亦已退出首版，现由 `[A-VS-ROOTS-QUERY-THEN-READ]` 定义。

### spec [A-VS-QUERY-COMMIT-ADDRESS] 查询提交地址后分别接受内容（DEPRECATED）

DEPRECATED；无独立 Commit 对象，替代为 `[A-VS-ROOTS-QUERY-THEN-READ]`。

### spec [S-VS-HISTORIES-SEPARATE] 提交祖先与 ref 发布历史分别遍历（DEPRECATED）

DEPRECATED；业务祖先归应用，ref 历史改由 `[A-VS-REF-HISTORY-CHECKED]` 枚举真实局部链。

### spec [S-VS-FACTS-AUTHORITATIVE] snapshot 不制造发布事实（DEPRECATED）

DEPRECATED；catalog snapshot 退出首版，发布事实读取由 S4 `[R-VS-LOCAL-COMPLETE]` 定义。

### spec [S-VS-CHECKPOINT-BEFORE] checkpoint 在发布首写前（DEPRECATED）

DEPRECATED；无 checkpoint/Prepared 协议，直接使用 S4 `[S-VS-ROOTS-BARRIER]`。

### spec [A-VS-INDEXED-OPEN-BUDGET] 增强资格须明确 control suffix 与表成本（DEPRECATED）

DEPRECATED；无 control suffix/indexed Open 模式，首版实际成本由 `[A-VS-QUERY-COSTS-EXPLICIT]` 定义。
