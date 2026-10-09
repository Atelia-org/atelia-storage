# S5：ref 历史、不可变 tag 与 branch 名称

状态：**Draft；2026-10-07 采用完整 RootMap、真实 ref 历史、单文件 tag 桶、统一 branch 绑定与命名 fork 的目录共同发布；2026-10-08 同步 S2 固定 12B 地址；API、路径编码及其余 codec 尚未实施/冻结**。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)。扩展 S4 的完整根快照与局部发布协议，不增加全局事实日志。

## 目标与最小公开操作

每个 ref 的当前/历史值以及 tag 的内容都为应用解释的完整 `{key => FrameAddress}`。ref 有独立稳定 RefId；对外可见 branch 绑定已正式发布的 ref，可以给既有 ref 加 alias，也可以与新 ref 共同发布；tag 冻结创建时的字典，而不是跟踪 ref。两类名称空间独立。

| 候选操作 | 效果 | 首版持久单元 |
| --- | --- | --- |
| ReadRefHistory(refId, budget) | 从固定上界向前枚举 checked 快照 | 真实 ref RBF3 文件链，无另写索引 |
| CreateTag(name, roots) / ResolveTag(name) | 创建/读取不可变完整字典 | 目标桶的一条完整 tag record |
| CreateBranch(name, refId) / ResolveBranch(name) | 为既有 ref 建立/解析不可变名称绑定 | 该 ref 的 names 中一个 create-only 绑定文件 |
| CreateBranchFromRoots(name, roots) | 原子创建命名 fork，返回新 RefId 与初始 revision | 私有 RefId 容器内的 ref 与初始绑定，一次目录发布 |
| ListBranches | 枚举正式 branch 名称与 RefId | 全部正式 ref 容器的 names 文件集合 |
| 匿名 fork / rewind 便利流程 | 旧字典新建未命名 ref / 追加回原 ref | 调用 S4 CreateRef / PublishRef |

CreateBranchFromRoots 是组合入口的候选代码名，具体签名在 Ready 定稿；它不改变两个基础操作手工组合仍是两次发布的语义。首版不提供 branch rename/unbind/delete/archive/name reuse、tag 修改/删除、差分或 state-checkpoint。命名 fork 的共同初始化是窄创建协议，不扩展为多个既有 ref 的事务；通用应用谱系与 ReadAncestors 不在 VersionStore 中定义。

## ref 历史与反事实分支

### spec [A-VS-REF-HISTORY-CHECKED] 历史枚举返回真实且自有的快照

ReadRefHistory MUST 先 checked-read 本 ref 的实际末 Snapshot，取得固定完整上界 revision，再按真实 RBF 逆向链枚举各条 Snapshot；每条执行完整 CRC、kind/version、身份与 RootMap codec 检查。它包含重复同值快照和 rewind，不用地址大小、应用 parent 或独立 previous 字段推算成员。
枚举 MUST 使用 `showTombstone: true`，只跳过该版本明确规定的库 meta/header；未知 kind/version、tombstone、损坏或 RBF `TerminationError` 必须显式传播，不能静默跳过或当正常历史结束。必须区分遇到合法初始化边界、达到调用方预算、取消/释放与错误；有限预算不宣称全历史审计成功。
返回的 RootMap MUST 是自有完整值，枚举结束或释放后仍可拿来 fork/tag/rewind；不得暴露受 enumerator/pool 生命周期约束的 Span、RbfFrame 或字典。RefRevision 绑定真实位置，首版不提供任意裸 revision 的随机历史读取。
首版同 owner 有活跃历史枚举器时 MUST 拒绝 mutation，枚举器结束/Dispose 后解除；序列/枚举器须有明确 owner/epoch 和资源归还。固定上界及该排斥保证历史不会被本次查询中插入的新发布改变，跨实例并发读取/写入资格不作首版承诺。

（Informative）Gym 选择任意旧快照 `R`：匿名 fork 调用 `CreateRef(R.Roots)`；需要名称时调用 `CreateBranchFromRoots(name, R.Roots)` 共同发布新 ref 与初始绑定；rewind 调用 `PublishRef(oldRefId, R.Roots)` 追加新 revision，保留原历史。旧 ref 不变，data 根帧被共享，不复制其图。应用可在其状态帧内保留 tick、RNG、父状态、分支来源或推演规则。

## 不可变 tag 与固定分桶

### spec [S-VS-TAG-ROOTS-IMMUTABLE] tag 一次冻结完整根字典

每个 tag MUST 保存 `TagName + 完整 RootMap` 的一条 RBF3 record，使用 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` 的私有拷贝、输入/容量 guard、data ConfirmDurable、Append 与 DurableFlush 协议；无关 data Builder 尚未归还不阻断 tag 创建，所需依赖仍必须由应用保证完成。tag 从某 ref/历史快照创建时复制当时字典，不持有可变 ref 间接绑定。同名再创建 MUST 在输出前拒绝，即使字典同值。
tag 内容直接复用 S4 RootMap codec，包括其消费的 S2 固定 12B FrameAddress；名字、RefId 和 RefRevision 的 codec 属于各自合同，不因地址固定 12B 而获得同样宽度。
TagName 的比较采用明确稳定的名称政策；默认候选为 Ordinal。路由 MUST 使用固定、跨进程复现的字符串 hash 与固定桶规则，不使用进程随机化的 string.GetHashCode。记录保留完整原名；hash 相同但名称不同不是同名，MUST 按完整名字比较。
首版每桶一个 RBF3 文件、不轮转；库 meta/header 及桶身份必须 checked。首次只在私有文件写完整 header，flush/close 后 create-only 公开空桶，再按 S4 普通 CreateTag 的 Append/flush 协议发布首条 tag。空桶初始化只是 metadata 准备，不提前设置本次 tag Confirmed；首条 tag 不随桶 rename 一起生效。空桶 rename 异常但尚未尝试 tag Append 时，本次 tag 为 NotAttempted，VersionStore 停用并重开检查桶状态；已正式发布的合法空桶保留。不得发布半个有效桶，桶为空不同于格式缺失或损坏。下一条记录的起点超过 SizedPtr.MaxOffset 时明确拒绝/维护，不隐式新建分段；合法末记录可越过起点上界，与 S4 同一容量规则。
查找与重名检查需扫描目标桶，或按需建立可重建的内存 name→实际记录位置表；建表需真实链 checked-read 与正常结束，不能将错误当未找到。ResolveTag 返回选定记录的 checked、自有 RootMap；缓存不能提供永久 CRC 健康保证。发现目标桶的未知记录/损坏/冲突名须报错。磁盘索引不属于首版。

## branch 绑定与命名 fork

### spec [S-VS-REF-ID-STABLE] 名称与稳定 ref 身份分离

对外可见 branch 名称 MUST 绑定已正式发布的 stable RefId，不把名称本身当作快照 revision。CreateBranch 只命名既有 ref；命名 fork 使新 ref 与初始名称共同达到公开资格。解析后的更新使用 exact RefId；多个 branch 可命名同一 ref，不为此复制 ref/data。RefId 不因 RootMap 同值而合并，也不随 fork 复用源 ref 身份。
首版创建后名称绑定不再改变；不存在同名删除再建的身份重用。空 RootMap ref 有效，missing ref 不被隐式创建；损坏 ref 不被当 missing。绑定目标 ref 缺失/身份不符时报错，不按名称选择其他对象。

### spec [A-VS-BRANCH-NAMES-GLOBAL] branch 名称在全部正式 ref 中唯一

所有 branch 绑定 MUST 采用同一种 checked 文件表示，放在 S4 正式 `refs/<RefId>/names/` 中；初始绑定与后加 alias 不使用不同的格式或发现来源。记录至少包含 version/kind、VersionStoreId、完整 BranchName 和 exact RefId，且 RefId 必须与所属容器及目标 ref 身份一致。
名称比较、路径安全编码/hash 在 Ready 定稿；默认候选为 Ordinal，tag 与 branch 名称空间仍独立。hash 碰撞必须完整名字比较，不能覆盖另一名称；非法路径字符、保留名、大小写差异不得被 OS 路径语义悄悄转换成错误名称绑定。
ResolveBranch / ListBranches 与所有名称创建的查重 MUST 覆盖全部正式 ref 的 names，或使用由该完整集合建立的可重建内存名称表；creating 内容不参与。查重不能只探测目标 ref 的局部 names。同名再次创建必须在 data barrier、私有输出之前确定拒绝，即使目标 RefId 或 RootMap 同值。
扫描 MUST checked-read 绑定、核对完整名字/所属身份并正常结束；目录枚举、I/O、CRC、未知版本/codec 错误或跨容器重复 fullname 必须报错，不能当未找到、先找到者获胜或成功的部分列表。完整扫描及正常结束之前不得安装可用于证明名称缺失的内存表；命中查询也不能跳过其所依赖的全局唯一性资格。目标 ref 缺失/身份不符仍按 `[S-VS-REF-ID-STABLE]` 报错，不回退到另一个对象；名称发现不要求扫描 ref 的全部 Snapshot 历史。
名称创建全程 MUST 持有 S4 单 writer/driver 准入且不允许操作交错，包括全局查重、私有准备及最终发布；成功发布后更新相同 owner 的派生表，安装失败仍遵循 Confirmed/停用规则。局部 no-overwrite 只保护具体目标路径，不能单独保证不同 RefId 目录间的名称唯一性。只读查询不修复名称，不采用私有准备；首版不承诺跨实例实时可见或多 writer 竞争。

### spec [S-VS-BRANCH-BIND-CREATE-ONLY] 给既有 ref 添加不可变名称绑定

CreateBranch MUST 先验证名字、目标已发布 ref 与 `[A-VS-BRANCH-NAMES-GLOBAL]` 的全局重名准入，将完整记录写入正式 names 之外的私有文件，flush/close 后同文件系统 no-overwrite rename 到目标 ref 的 names 路径；正式安装成功才设置 Confirmed。名称层不得在正式 names 中留下半写文件。失败/不确定结果使用 S4 PublicationOutcome 与停用/重开规则，不在私有 flush 后宣称名称已建立。
该 create-only 操作遵守 S4 owner 模式/生命周期及历史 mutation guard，但无需再次遍历/flush data 图，也不因无关 data Builder 尚未完成而拒绝：它只命名已经存在的 ref，不发布新的 RootMap。新 alias 失败不得删除、移动或重建既有 ref，也不得撤销其已有名称；只清理可证明私有的准备文件。
`CreateRef(roots)` 与 `CreateBranch(name, refId)` 手工调用仍是两个独立步骤，MUST NOT 宣称跨操作原子；第二步失败可留下第一步已正式成立且 ListRefs 可发现的 ref。需要共同公开时 MUST 使用下面的组合协议，不追加 allocation 日志或回滚已发布 ref。

### spec [S-VS-NAMED-FORK-PUBLISH] 命名 fork 由一个目录共同发布

本条消费 S0 `[S-VS-NAMED-FORK-ATOMIC]` 与 S4 `[S-VS-REF-DIRECTORY-PUBLISH]`。组合入口 MUST 保证：**命名 fork 成功发布时，新 ref 与 branch 一起可见；发布前，普通查询两者都不可见。失败可以留下私有准备文件，但不会留下公开的未绑定 ref。**
实现 MUST 按以下步骤执行，不以两个公开 API 的串接代替：

1. 在同一串行 owner 准入下，完成生命周期/模式/历史 mutation guard、全局名称唯一性、RootMap 私有拷贝与编码，以及 ref 和绑定的容量检查；确定拒绝不输出、不调用 data barrier。
2. 按 S4 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]` 调用 data ConfirmDurable；应用保证本根新增依赖已完成，无关 Builder 可继续构建，失败不进入本次发布输出。
3. 通过 S4 私有初始化步骤准备新 RefId 容器内的完整 header、初始 Snapshot 和 names 中的一份完整初始绑定；采用与 alias 完全相同的绑定 codec。所有本次写入文件 flush/close，仍不向调用方交付已发布 RefId/revision。
4. 消费 S4 的一次同文件系统 no-overwrite 目录 rename，将整个容器发布到正式 refs。此步骤同时建立 ref 和初始名称的发现边界；不得先公开 ref、先正式占名、追加最终 Bind，或发布后再 promote 文件。
5. 按 S4 PublicationOutcome 先记录 Confirmed，再安装内存名称/ref 投影并正常返回新 RefId 与初始 revision；后续内存失败不撤销或降级发布证据。

最终目录 rename 前失败，本次业务结果为 NotAttempted；开始 rename 后无法证明结果为 Unknown，停用并重开按实际正式容器裁决，不盲重试。正常发布或重开接受后，ResolveBranch 所得 RefId 与 ListRefs/ReadRef 定位的是同一完整对象。私有残留不占公开名称，不自动续建；只读不采用/清理它，清理不删除正式目录。完整坏记录必须报错，不能借此把已发布对象回退为不存在。
匿名 fork 仍用 CreateRef；命名 fork 复制所选自有 RootMap 而共享 data 帧，不改变源 ref、复制业务图或写入框架 Parent。给既有 ref 的多个 alias 继续使用 `[S-VS-BRANCH-BIND-CREATE-ONLY]`，不会另开 ref 历史。原子性只覆盖一个新容器及其初始名称，不提供跨既有 ref/tag 的通用事务或多个命名 fork 的整批原子性。

## 查询、启动与成本

### spec [A-VS-ROOTS-QUERY-THEN-READ] 查询根字典后由应用接受内容

ReadRef/ResolveTag MUST 返回 checked 发布记录里的 RootMap，ReadRef 同时返回实际 revision；名称解析返回 stable RefId。发布记录健康不证明引用帧或业务图健康。应用按这些地址调用 data FrameStore.ReadFrame 完整读取和解码；缺/坏必要数据报错，不改 RootMap、回退 ref 或重绑定 tag。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

Open MUST NOT 默认回放全部 ref 历史或扫描 data 图，也不因此宣称全库 O(1)。目录/格式门、名称及文件枚举的实际成本须报告；惰性打开只把检查延后到目标访问，不把未检查对象说成健康。
当前 ReadRef 成本为 header 与完整末 Snapshot bytes，不随本 ref 历史帧数增长；ReadRefHistory 为 O(请求快照数 + 对应 bytes)，预算未覆盖的历史不计作已验证。tag 首次查找/重名检查的扫描为 O(目标桶记录数 + bytes)，建表的内存为 O(该桶名称数)，后续定位仍需选中记录读取。不承诺分桶即可 O(1) 查询或长期无限容量。
ListRefs 成本与正式 RefId 目录数相关；branch 首次解析/全局查重/ListBranches 需要遍历正式 ref 目录及 names 记录，成本为 O(ref 目录数 + binding 数 + 所需 checked bytes)，不扫描全部 Snapshot 历史。即使大量 ref 未命名、只有少数 branch，也可能付出全部目录发现成本；不能按名字编码直接访问一个全局正式路径而宣称 O(1)。
实现 MAY 惰性建立唯一的可重建内存 name→RefId/绑定位置表；其内存为 O(binding 数)，构建须消费 `[A-VS-BRANCH-NAMES-GLOBAL]` 的完整资格，后续定位仍需所选记录/目标的检查。名称解析后可保留 stable RefId，直接 ReadRef 不依赖该表或任何初生名称。目录/名称、句柄缓存及枚举生命周期的实际成本须报告，不增加持久名称索引、control 全量投影、全部尝试账本或磁盘 catalog/checkpoint。

## 工程 codec 与后续扩展

| 单元 | 最小字段 | Ready 选择 |
| --- | --- | --- |
| tag 桶 header | version/kind、VersionStoreId、桶身份 | tag、初始化边界、桶数量/hash 与文件路径 |
| tag record | version/kind、完整 TagName、S4 RootMap | 名称政策/限额及 codec 复用 |
| branch 绑定文件 | version/kind、VersionStoreId、完整 BranchName、RefId | 初始/alias 同一 checked codec、names 路径碰撞及全局发现验证 |
| history 结果 | RefId、RefRevision、自有 RootMap | 枚举签名、预算/结束/错误与 Dispose |

未来 ref 分段/轮转、tag 分片、差分与可重建索引均为独立后续片，需实际容量/工作集证据及自己的定位/恢复合同；不预建 segment manifest 或 checkpoint 缓存。业务 Parent/RNG/operationId 可放在应用已引用对象中，不扩展中立字典 codec。外部工具 exactly-once、跨 ref 事务和自动 merge 均无框架保证。

## 实施片、Ready 工程定稿与验收

1. S5-A：逆向 history checked 枚举、固定上界、预算/错误/排斥 mutation、返回值所有权；形成冷重开选择旧快照的 public 例子。
2. S5-B：tag 名称/hash 分桶、唯一性和 data-first 单记录创建/查询；无持久索引。
3. S5-C：统一 names 绑定、全局发现/唯一性、既有 ref alias，以及复用 S4 容器发布的原子命名 fork；保留匿名 CreateRef 与手工两步的独立结果。
4. S5-D：LLM tool-loop 多根状态与 Gym 旧快照反事实消费轨迹、进程终止 failpoint 及独立 public 验收；真实文件实验使用 W:。

| Ready 项 | 需定稿 |
| --- | --- |
| S5-Q1 | 名称比较/编码/字符/限额、独立命名空间、bucket hash/数量/路径；同名拒绝 |
| S5-Q2 | history 结果/预算/错误/终止、owner/epoch/Dispose 与 mutation guard；不扩成随机 ReadRevision |
| S5-Q3 | header-only 空桶初始化/恢复与首 tag 正常追加、codec、扫描或惰性内存索引及 selected record checked-read |
| S5-Q4 | 统一绑定 codec/names 路径、全局扫描或惰性内存表、alias 文件发布、命名 fork 组合 API/容器发布/Outcome 与规模成本 |

验收覆盖历史返回字典在枚举释放后仍可发布/创建 tag、R0→R1→R0 仍保留三个 revision、预算结束与损坏区分、tombstone/unknown/CRC/TerminationError 不跳过、枚举期间 mutation 拒绝及释放后恢复、空字典与 missing、hash 碰撞/同名同值拒绝、tag 不随 ref 更新、多个 branch 同 ref、fork 不改源/不复制 data、应用数据读取坏时不回退。故障证据沿用 S4；首版 Accepted 不要求分段、差分、checkpoint、业务谱系或精确历史尝试追踪。
命名 fork 覆盖每个私有文件的初始化/flush/close、目录 rename 前后及内存安装失败：普通 ResolveBranch/ListBranches 与 ListRefs/ReadRef 只见共同未发布或共同已发布，绝不由私有残留公开单独 ref；只读不修改残留，重开不自动续作。覆盖正式 ref/绑定完整坏 CRC/身份/codec 明确报错，Unknown 后检查实际状态且不盲重试，重复名称先于 barrier/输出拒绝，跨不同 RefId 的同名记录冲突及全局扫描失败不当 absent。
兼容向量覆盖 standalone CreateRef 正常列出未命名 ref、手工两步失败仍保留第一步 ref、alias 失败不损害既有 ref/其他名称、empty RootMap、多个 aliases 与命名 fork 使用同一 codec/发现方式、source history 不变。目录 rename 的资格独立于 file rename；补大量未命名 ref/少量 branch 的冷查名测量，不用未来内存表掩盖首版发现成本。
补无关 data Builder 活跃时以已完成闭包创建 tag/fork ref，以及借助同文件历史随机读取构建新状态；CreateBranch 仍只确认自己的绑定，不新增 data 屏障。分别验证历史枚举器的 VersionStore mutation guard 与无关 data Builder 不阻断发布的资格。

