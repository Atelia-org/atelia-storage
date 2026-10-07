# S2：FrameStore 核心、地址与文件生命周期

状态：**Draft；2026-10-05 确认首帧 header、成功 EndAppend 自动归还、通过 config 文件限制未归还 Builder 数量，并收窄为独立核心合同；wire/API/header codec/config 与恢复细节待工程定稿**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)。本阶段独立于发布和命名层。

## 本阶段目标

创建 `src/FrameStore/FrameStore.csproj`、`tests/FrameStore.Tests/FrameStore.Tests.csproj`，采用 `Atelia.FrameStore` 身份。
提供独立的多文件不可变 Frame 分配器：Create/Open/OpenReadOnly、申请并完成单帧、随机读取、不透明地址、恢复及同步耐久确认。普通分配不提供业务上的全局顺序。
首版支持多个 active 文件、每文件独占租给一个 Builder，文件申请/归还串行且可嵌套；成功完成或健康取消后可以复用文件。普通帧可以交错构建、按不同于申请次序的顺序完成。达到软阈值后文件归档只读。
按需引用 Rbf/Data/Primitives，不引用旧库或业务项目。本阶段的定稿、实施与验收只包含分配、读取、文件生命周期和耐久确认。

## 已确认与待定的边界

| 事项 | 当前结论 |
| --- | --- |
| 单帧追加能力 | 保留 RBF 的 buffer Append、未知尺寸 Builder、分立长度的已知尺寸 Builder；签名细节待定 |
| 构建自由 | 每文件一个 Builder；owner 可持多个未完成 Builder；租借可嵌套；首版调用仍串行 |
| 文件选择 | 从 active 当前可分配文件中选择数值 FileId 最小者；低编号忙时跳过，不等待 |
| 轮转 | 三种追加均在成功完成后按 TailOffset 事后检查；大于阈值才停止新追加 |
| 磁盘生命周期 | 私有 creating → active → 按编号分桶的 archive；不维护 active manifest/status |
| 归档桶 | 固定 1024 个编号一个桶；精确路径字符串与格式版本待定 |
| 耐久 | 没有未归还 Builder 时确认全部必要 active 输出；archive 的资格来自移动前 flush |
| 首帧 meta/header | 必须采用并至少承载版本解释；字段/codec/tag/校验顺序由 Coding Agent 定稿 |
| 成功 EndAppend | 正常成功返回前自动归还租借和配额，无需再 Dispose；归档维护入口待定 |
| Builder 数量准入 | config 文件提供可调整上限；超限立即拒绝；不增加精确总内存账本 |

本表是状态导航，具体合同以下文条款为准；Draft 不表示已实现或已有平台资格。

## term `FrameStore-Context` 存储上下文

一个持有持久 StoreId、访问模式及实例生命周期的存储 owner。它拥有文件组织、所有读写句柄和 fault 事实；首版由调用方串行使用。整个 store 只有一个可写 owner，不能仅靠某个 RBF 文件的独占打开替代这个约束；锁定入口在 S2-Q3 定稿。
候选 StoreId 为随机非零 128-bit 值，在 create-only 格式门中持久化；路径移动不改变身份。复制与外部改写不由本协议自动协调。

## 地址的候选表示

FrameAddress 对外是不透明、可持久编码的局部地址；首版内部候选采用 `FileId + SizedPtr`（旧草案称 SegmentId），正整数 `uint` 文件编号、0/default 非法，ticket 的范围由 RBF/Data 公共 API 校验。不向普通调用方提供字段拆解、地址算术、大小排序或相邻帧推算合同。
StoreId 属于上下文，不必重复塞进每个数据引用。地址相等只在同一个 store 中有意义；相同数值在另一个 store 可能恰好也合法，**裸地址无法检测调用方原始来源错误**。
跨 owner 的 Builder/plan 拒绝由实例生命周期负责；持久上层绑定负责选择正确 store。精确 wire、端序及文本表示在 S2-A 关闭。
成功完成的帧在首版存续期间不重新分配、不改写、不删除。整个文件从 active 移到 archive 只改变容器路径，不改变 FileId、ticket 或帧字节，不属于帧重定位。地址不透明不等于已经提供可搬迁的逻辑对象 ID；未来改变定位编码必须另行处理兼容性。取消或截断的未完成预约没有稳定身份保证。

## term `Completed-Frame` 已完成帧

成功 append 或重开后存在的完整 frame 事实。Completed 不等于本次确认耐久，也不等于业务状态已发布；数值地址本身不提供这些资格。
RBF ticket checked read 证明 CRC，不独自证明主链成员；正常签发来源、扫描发现和消费者记录的来源约束不能省略。

## 候选入口与职责

| 入口 | 本层负责 | 消费者负责 |
| --- | --- | --- |
| Create/Open/OpenReadOnly | 格式门、目录发现、owned handles、恢复 | 选择 store/mode；不把 Open 当全库 audit |
| Append/两种 BeginAppend | 文件租借、提前地址、RBF 完成及 fault | tag、stored payload/meta 编码 |
| ReadFrame | 寻址、完整 CRC、buffer 生命周期 | 解码及业务合法性 |
| Preview/显式 inventory 或 audit | 明确信任级别、实际帧发现与错误 | 不以枚举顺序推断业务关系 |
| ConfirmDurable | 同步确认 owner 的全部完成输出 | opaque 引用闭包及业务生效 |

普通分配器不承诺 ScanForward/ScanReverse 的全局语义，也不提供“地址之后”的业务范围。显式 inventory/audit 可扫描真实文件发现帧，但其枚举次序不构成发布顺序；精确 API 与成本另在 S2-Q4 定稿。

只提供 owned 分配/read/Builder，不公开可绕过本层 bookkeeping 的裸 `IRbfFile`。本层不认识上层控制记录或提交 codec。首帧 header 已确定采用，但其识别和 tag 可用范围必须在 S2-Q6 显式定稿，不能暗中改变消费者范围。

三种追加能力已确认，下面仅为签名候选，不是已存在的 public API：

```csharp
AteliaResult<FrameAddress> Append(
    uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);
FrameBuilder BeginAppend();
FrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out FrameAddress address);
```

未知尺寸模式在正常 EndAppend 时签发地址；已知尺寸模式在 Begin 时取得绑定地址。FrameBuilder 保留 PayloadAndMeta、reservation/回填及 RBF 的 EndAppend 用法；其薄包装负责观察完成、取消、文件归还和共享 fault。Writer 的类型和 guard 转发仍待定，不返回可绕过本层生命周期的裸 Builder。

已知尺寸入口按 S1 保留 payloadLength、tailMetaLength 两个声明：调用公共尺寸 API 校验/试算，再通过 BeginAppend(payloadLength, tailMetaLength, out ticket) 取得当前帧的绑定地址。尺寸不用于提前轮转。
正常完成调用 EndAppend(tag)，使用 Begin 的 meta 声明；显式 meta 参数只能核对一致，不能改变划分。FrameStore 的 owned API 签名仍由 S2 审定，不在本层退化为仅保存合计长度。

## 分配与租借合同

### spec [S-FS-ALLOCATOR-UNORDERED] 普通分配不赋予业务顺序

普通 FrameStore MUST 以独立地址表达帧引用，不承诺分配次序与业务顺序一致。调用方 MUST NOT 用文件编号、offset 或物理相邻关系推断 Parent、版本新旧或当前状态。
每个文件仍顺序追加；不同文件可以交错完成。首版的串行调用约束不建立业务上的全局排序。应用通过帧内引用表达关系，其构建中间帧的申请、完成和物理排列不形成上层发布协议的输入。

### spec [S-FS-LEASE-EXCLUSIVE] 每个 Builder 独占一个文件

FrameStore MUST 在 BeginAppend 成功前租借一个可分配文件，直到正常完成或健康取消后归还。同文件不得同时接受第二个 Builder 或完整 Append；再次申请 MUST 选择其他空闲文件，没有合适文件时才创建新文件，不能隐式提交/取消已有 Builder。
归还后未达到轮转条件的文件可以再次分配，不能把“暂时被占用”当成永久 sealed。可纠正短写、meta 冲突、未提交 reservation 等拒绝按 S1 保留同一个 Builder 和租借；参数错误不借机更换文件或地址。
文件选择按下面的最低编号规则；未归还 Builder 的数量准入由 config 提供，具体文件/默认/计数细节在 S2-Q7 定稿。reader 与空闲句柄的实现策略仍在 S2-Q5 定稿，不增加精确总资源配额，也不承诺无限嵌套。

### spec [S-FS-END-AUTO-RETURN] 成功提交自动结束租借

EndAppend MUST 在正常成功返回前自动结束相应 Builder 的租借，释放未归还 Builder 配额并登记完成输出；调用方无需再调用 Dispose 才能申请新的 Builder 或进行 ConfirmDurable。
可纠正短写、meta 冲突、未提交 reservation 等拒绝不归还文件、不释放配额；健康 Dispose/取消归还。成功后 Dispose、Builder 的值副本与旧 Writer 不得二次计数或影响新的租借。
成功完成后超过软阈值的文件必须立即停止新分配，自动归还不意味着重新变为可租借。物理归档维护的调用入口及异常报告仍在 S2-Q4 定稿；归还和正常成功返回不代替 durable flush。

### spec [A-FS-BUILDER-LIMIT] config 文件限制未归还 Builder 数量

FrameStore MUST 从 config 文件取得可调整的未归还 Builder 上限。额度已满时，新的 BeginAppend MUST 在租借/创建文件与输出之前立即确定拒绝，不等待已有 Builder、不抢占、不产生 writer fault。
计数按实际签发的 Builder 租借，不按值副本、Writer 副本或后续 Dispose 次数重复计算。Begin 初始化失败不占用成功签发配额；可纠正提交拒绝保留配额，正常成功 EndAppend 或健康取消释放一次。
配置只建立 Builder 数量准入，不承诺总文件数、打开句柄或总内存硬上限，也不维护精确总字节数账本。候选属性名 MaxOutstandingBuilders；config 不存 active 集合、租借状态或下一编号。
配置文件名称/位置/格式、默认和缺失/非法配置行为、生效时机、一次性 Append 的短期内部租借准入仍在 S2-Q7 定稿。首版建议打开时一次性载入有效配置，修改在下次打开生效，不引入热更新机制；该读取策略尚未冻结。

### spec [S-FS-LOWEST-FILE-FIRST] 从可分配 active 文件中选择最低编号

普通 FrameStore 的三种追加入口 MUST 共用这一选择规则：通过参数/owner/资源准入后，从已发布且初始化合法、未租出、未停止分配、TailOffset <= RotationThreshold 的 active 文件中，选择数值 FileId 最小者；没有候选时才创建新文件。
低编号文件仍被租出时 MUST 跳过，不等待它归还、不隐式结束其 Builder。健康归还且仍可分配后，该文件重新按编号参与选择；达到归档条件但尚未完成物理移动的文件不再参与选择。
文件编号按数值比较，不依赖 FS 枚举顺序。是否缓存了打开句柄不改变选择优先级，未缓存的候选可按需打开；不能仅因句柄未打开而改选高编号或新建文件。
选择不依据预计帧长度、预计完成时间或文件剩余软阈值额度，不做轮询/负载均衡，也不声称公平调度或按帧顺序分配。owner 已 fault 或选择后的文件打开/校验失败时按既有错误规则处理，不能静默跳过坏文件继续选择另一份。
（Informative）这个规则倾向先填充较早文件；嵌套租借仍使用其他文件。小型 active 集合可以在内存按编号维护可分配项，索引可从目录重建，不构成全历史分段表或新的持久元数据。

### spec [S-FS-LEASE-SERIAL] 租借与归还串行且可嵌套

文件选择、创建、租借、归还、归档及耐久屏障 MUST 串行执行；首版 FrameStore 及派生对象由调用方串行使用。多个尚未结束的 Builder 不等于同一时刻并行调用。
Builder 的文件、epoch 和构建资源 MUST 独立绑定；未来不同 Builder 各由一个线程使用是单独扩展，不在首版声明并发安全。EndAppend 自动归还时，设计应区分文件内提交与 owner 登记，避免把整个编码/CRC/EscapeKey/写出过程绑定到一个全局当前 Builder。
首版随机读取只因目标文件存在活跃 Builder 而受 RBF 限制；其他空闲/归档文件可以串行读取。不额外禁止整个 owner 的读取，不通过第二个同文件句柄绕过 FileShare.None 或 RBF guard。

## 候选合同

### spec [F-FS-OWN-FORMAT] 新库有独立格式身份

FrameStore MUST 有自己的格式版本、持久 StoreId 和 create-only 格式门，不复用旧目录身份。目录布局按本层版本解释，active 集合不再另存 locator/manifest/status；格式门不随普通租借和轮转反复更新。
新库文件 MUST 是 RBF3；active 和 archive 的实际格式通过 S1 的已打开格式信息检查，不能仅信自家 marker 或文件长度。
Create 为 create-only；Open 不创建缺失 store。格式未知、metadata CRC/字段坏、必要文件缺失明确分类；I/O 保留原异常资格。
格式门发布前的初始创建残留是 incomplete creation，不宣称已有可打开实例；不得盲目覆盖该目录。只读不修尾、不改定位元数据或派生缓存。

### spec [A-FS-CONTEXT-ADDRESS] 地址与上下文资格明确

地址 MUST 明确非法值、segment/ticket 范围、编码、相等和默认规则；Builder/plan 绑定 owner 与 epoch。
来自成功追加、真实主链扫描或已接受上层事实的地址具有明确来源；任意合法数值及一次随机 CRC 读取不自动证明其 intended identity。
只有中性定位信息进入 FrameAddress；Parent、graph schema、提交关系等在上层记录中。跨 owner 或旧 epoch 的 Builder/Writer/read handle 在访问文件前拒绝。

### spec [R-FS-OPEN-RECOVERS] 可写打开接受 RBF 恢复

可写打开 MUST 枚举 active 的规范文件，并让 RBF 分别处理每个文件的单尾恢复；报告按 FileId 关联供诊断，Action 不驱动业务回滚或发布。所有需恢复的 active 文件成功打开并完成本层初始化检查后，才能向外签发新 Builder。
Open 不重复读取整个尾 payload 取得结构资格；业务 metadata 作事实前仍 checked-read。archive 历史只读严格访问，残尾不被自动改写。恢复后的 TailOffset 大于当前阈值时，文件不可再租借，按归档协议处理。
正常 Open 核对格式门、私有创建槽位、active 集合及编号恢复所需目录信息；历史缺段/坏内容按需读取或完整 inventory/audit 时报告。枚举目录名不等于审计历史帧，不宣称已验证全部历史文件。只读只验证，不恢复、不移档、不清理创建残留。

### spec [S-FS-ROTATION-NEXT-SLOT] 轮转只有一个未发布 next 槽位（DEPRECATED）

此前单 active/locator 协议及更早的 `[S-FS-ROTATION-RECOVERABLE]` 已由 `[S-FS-SOFT-THRESHOLD]`、`[S-FS-DIRECTORY-STATES]`、`[R-FS-CREATION-PRIVATE]` 和 `[S-FS-ARCHIVE-AFTER-FLUSH]` 替代。保留锚点，不沿用其 locator 裁决或单 active 健康 Open 成本。

### spec [S-FS-SOFT-THRESHOLD] 完成后大于阈值才停止追加

三种追加 MUST 共用事后检查：成功完成帧并取得新的 TailOffset 后，只有 TailOffset > RotationThreshold 才停止该文件的新分配并进入归档；等于阈值仍可追加。未知尺寸与已知尺寸不使用不同轮转算法。
正常取消和可纠正拒绝不推进 TailOffset，不据此触发尺寸轮转。成功完成的合法 RBF 帧不得因使文件超过目标阈值被拒绝、改地址或挪到另一文件。
阈值不保证单文件尺寸不超过该值。初始化边界 <= 阈值 <= SizedPtr.MaxOffset；初始化边界在 S2-Q6 定稿，默认值及重开时阈值变化的规则在 S2-Q2 定稿。既有大文件不为新阈值拆分或重写。
（Informative）固定阈值下，新文件只在未超过阈值时开始下一帧，因此正常尺寸超出量最多是一次最大合法追加占用；用 RBF 公共 MeasureWriteSize 计算，不复制 wire 常量。物理长度查询可用 TailOffset，不要求每帧额外 FileInfo/stat。

### spec [F-FS-BUCKETED-PATHS] 归档路径由编号固定计算

首版归档布局 MUST 固定 BucketShift=10：bucket = FileId >> 10，slot = FileId & 1023。每桶覆盖 1024 个编号，与帧大小、完成顺序和内存分段表无关。FileId=0 非法不意味着其他桶的 slot=0 非法。
active 使用完整 FileId 命名，archive 由 bucket/slot 确定唯一位置；精确端序、名称宽度/大小写及路径格式在 S2-Q1/Q2 定稿。桶大小属于格式规则，不能在已存在 store 上随实例配置变化。
随机读取 MUST 按编号定位 archive 或 active，不依赖全历史分段表；只有路径确实不存在才尝试另一个允许位置，不把权限、I/O、格式或 CRC 错误当作缺失回退。

### spec [S-FS-DIRECTORY-STATES] 目录是文件生命周期事实

creating 是未发布的私有初始化槽位；active 是可能继续追加且重开时需检查/恢复的集合；archive 是已按本协议 flush 后移入的只读文件。当前“已租出”只保存在内存，不写状态帧或 status 文件。
规范文件不得同编号同时出现在 active 与 archive；发现时 MUST 报协议矛盾，不能任选、覆盖或删一份掩盖问题。所需文件不存在时明确缺失，不寻找其他编号替代。正常 Open 不承担发现所有未知历史文件的全库 audit。
FileId 首版候选为递增非零 uint，不回绕；已完成帧及其文件不删除。下一编号倾向由实际目录恢复最大已发布编号后取得，不新增持久计数器；空桶、创建残留、编号耗尽及缺号的具体裁决仍在 S2-Q2 定稿。
（Informative）可枚举 active 与归档桶名，再检查最高相关桶的规范文件；最高桶为空时继续寻找实际已发布编号。下一编号的恢复必须覆盖 active 与 archive 两个集合；低编号文件仍租出、高编号文件先完成归档是合法情形，不能假定两个集合按编号分界。FS 枚举顺序不是数值顺序。没有计数器时须计入桶目录枚举成本，不沿用“健康 Open 与历史规模无关”的承诺。

### spec [R-FS-CREATION-PRIVATE] 初始化完整后才发布文件

新文件 MUST create-only 写入私有 creating 槽位，完成本层初始化并 flush/close，再同文件系统、不覆盖地 rename 到 active；成功登记后才能打开供追加、签发 Builder/提前地址。目录管理串行，因此每次至多一个未发布创建槽位。
creating 不接受用户帧、不签发地址。0–3B 的未完成 RBF Header 只可能是私有创建残留，不削弱 RBF 普通 Open 的拒绝规则。本层初始化边界必须包括完整首帧 meta/header，由其 RBF ticket 与公共边界 API 确定；RbfScanBoundary.Empty.EndExclusive 只表示底层空 RBF，不再表示可发布的 FrameStore 文件。
在可证明为本层内部初始化的范围内，重开可写 owner 取消未发布创建槽位；完整初始化也可取消，不必补发布。精确残留前缀与损坏拒绝规则须在 S2-Q3/Q6 定稿，不能据文件名删除未知用户内容；只读不清理。

### spec [S-FS-ARCHIVE-AFTER-FLUSH] 先确认内容再归档

归档 MUST 在没有活跃租借时按停止分配 → DurableFlush 返回 → 关闭 writer → 同文件系统、不覆盖的 rename 到计算出的 archive 路径执行。桶目录可以提前创建，空桶本身不表示某文件已完成归档。
写句柄采用现有 RBF FileShare.None，先关闭再移动，不为轮转擅自改变 RBF 共享规则。active/archive/creating 必须处于同一文件系统；不接受跨卷复制删除作为本协议的 rename。
进程终止、OS/FS 仍运行是本轮故障模型；具体 Windows/Linux 原子 rename 入口仍需 S2-Q3 的实现与实证，不以方法名 File.Move 当作平台资格，也不宣称断电目录项耐久。

| 中断后实际位置 | 本层解释 |
| --- | --- |
| 私有 creating 残留，没有已发布文件 | 按已证明的初始化残留范围处理；无用户地址资格 |
| active 中合法已初始化文件 | 逐文件 RBF 恢复；重新检查阈值，必要时重新 flush/归档 |
| 文件已关闭但仍在 active | 按 active 处理，不依赖丢失的进程内 sealed 标志 |
| 文件只在计算出的 archive 位置 | 归档已发生，按只读访问；前置 flush 由正常协议保证 |
| 同编号两处都有，或所需文件两处都无 | 协议矛盾或文件缺失，拒绝；不补造、不挑副本 |

### spec [F-FS-META-FIRST] 首帧必须是文件 meta/header

每个新文件 MUST 把第一个 RBF frame 用作 FrameStore 自有 meta/header，至少提供版本解释能力。MUST 在 creating 中完成该帧、取得完整 CRC 资格并完成初始化 flush/close，再发布到 active；裸 RBF Header Fence 不足以成为已初始化 FrameStore 文件。
header 保持标准 RBF3 帧，RBF 不感知其业务类型。初始化完整的 active/archive 中缺少、损坏或无法解释必需 header 是错误，不自动补造，也不把它当成可丢弃的普通用户残尾接纳。
字段与 codec 由 Coding Agent 研究决定，不再需要用户重新决定是否采用。优先评估最小版本与 StoreId/FileId 绑定，用于版本解释、文件与 store/路径编号绑定及独立文件诊断；后两项、tag、字段 wire 和识别规则尚未冻结。暂不加入 Parent、业务 schema、发布序号、时间戳、索引或可变状态。
header 不替代 create-only store 格式门，不解决裸 FrameAddress 的原始来源错误，也不保存可调整的 Builder 配额。固定首帧识别、用户 tag 范围、inventory/audit 的展示、首个用户帧边界、最小初始化尺寸及 header 校验与 active 修尾的 public API 顺序仍须在 S2-Q6 闭合。

### spec [S-FS-OWNED-FAULT] Owned 操作共享 fault

owned append、最终 Builder 提交、flush、轮转和 metadata 输出 MUST 共享 fault；确定参数/state guard 不 fault，RBF preparation/Commit 边界按 S1。
输出异常后停止实例，缓存命中也不绕过 guard；取消资源异常不承诺可继续用。Dispose 尝试释放全部 owned resources，并定稿异常汇总规则。
成功 EndAppend 自动归还已确认；归档/资源释放失败不撤销此前已完成帧，维护入口及维护异常如何映射到返回结果仍在 S2-Q4 定稿。耐久确认范围由下面的 `[A-FS-DURABLE-OWNER]` 唯一定义，Open 与完成资格不替代该屏障。

### spec [A-FS-DURABLE-OWNER] 同步确认 owner 的全部完成输出

ConfirmDurable MUST 在没有未归还 Builder 时，同步确认本 owner 所有已完成、尚未确认的输出；成功返回是本调用栈的耐久证据。如将来纳入独立 batch plan，未完成 plan 同样不得越过该屏障，精确 guard 在新合同中定义。
archive 文件在移动前已 flush；所有必要 active 文件的完成输出都必须确认，不能只确认一个当前文件。每个重开的 active 即使 Action=None 也纳入首次确认。这个证明利用目录归档协议，不要求调用方认识文件或比较地址。
flush 失败按 owned fault 停止实例；不允许裸 RBF writer 或外部文件导入绕过登记。未来多个数据 owner 的闭包不在此屏障范围内。
范围可包含不相关完整 orphan，无须选择每帧集合。先前 flush 后又追加的 frame，必须由新的屏障确认；上层不接受调用方的“以前 flush 过”标志替代实际同步确认。
首版不提供 DurabilityReceipt、集合合并/复用/过期协议。多个 active 已是当前设计范围，必要输出必须全部覆盖；只 flush 根所在文件不满足本条。

## 单帧资格与实施片

已知尺寸为 `Opened → Building(address known) → Completed`；未知尺寸只在正常完成时取得地址。Builder 完成/取消与文件归还的边界由 owned wrapper 明确，值拷贝、旧 epoch、重复 End/Dispose 不得二次归还或改变别人的租借。
ConfirmDurable 消费本阶段 `[A-FS-DURABLE-OWNER]` 的核心合同，不解析业务依赖闭包；交错构建与循环引用的消费资格在后续阶段独立验证。
批量计划不作为基本交错构建的前置；业务发布不在本阶段建立。

1. S2-A：创建项目/solution；定稿不透明 FrameAddress、StoreId/格式门、路径、必需首帧 header codec、数量上限 config、模式/错误。
2. S2-B：三种 owned 追加、可嵌套独占租借/归还、交错完成、随机读取、基础耐久确认、buffer/fault/Dispose。
3. S2-C：统一事后轮转、编号恢复、creating/active/archive 中断状态与多个 active 的独立恢复。
4. S2-D：进程中断/资源失败、只读、错误格式、规模和 public 指南资格。

至少验证两种无 EventHeader 记录、三种追加、嵌套租借与乱序完成、取消后文件复用、旧地址跨归档稳定、FrameAddress 的 context 限制、archive RBF1 拒绝、创建/flush/close/rename 各窗口、多个 active 独立恢复及完整内容损坏。
覆盖阈值以下/等于/超过、最大帧超阈值仍成功、可纠正失败不归还、成功后不等 Dispose 即可重新申请/确认、重复 Dispose 不二次释放、不覆盖归档目标、1024 边界与 slot=0、编号耗尽/空桶、仅根文件 flush 不充分；首帧 header 覆盖初始化中断、缺失/损坏、未知版本、字段绑定和用户扫描规则。
数量配置覆盖达到上限立即拒绝且不输出、初始化失败不占额度、短写拒绝仍占额度、成功 EndAppend/健康取消释放一次，以及有效/缺失/非法 config 与其生效策略；不以数量上限测试冒充总内存预算证据。
文件选择覆盖乱序目录枚举、最低编号忙时选下一空闲文件、健康取消/归还后重新优先低编号、尚未移档但已停止分配的低编号排除，以及仅当全部候选不可分配时新建。覆盖低号仍 active / 高号先 archive 后的重开编号恢复，以及最高归档桶为空时仍取得两个集合的实际最大编号。失去缓存句柄不改变选择结果，坏文件不被静默跳过。
最小消费者只按地址取回并解释引用；不同申请/完成次序构建相同逻辑图时，业务结果不依赖文件选择或枚举次序。
健康 Open 成本包含 config 读取、active 文件数、编号恢复所需桶名枚举/有限桶内目录项及小型 header 校验；不宣称与全部历史规模无关。冷历史 reader、异常尾扫描、完整 payload 和 audit 的成本另外报告。
项目遵循仓库 SDK/test pin，不复制生产包清单。源码可用不等于包已交付。

## Ready 阻断项与出口

| ID | 需定稿 |
| --- | --- |
| S2-Q1 | FrameAddress 持久 codec、StoreId/格式门编码与版本；上下文实际保证 |
| S2-Q2 | 精确路径字符串、软阈值默认/变更、递增编号恢复/空桶/缺号/耗尽及目录规模成本 |
| S2-Q3 | store 独占入口、creating 残留裁决、同文件系统不覆盖 rename 的两平台入口与中断实证 |
| S2-Q4 | 成功提交自动归还已确认；Builder/Writer/读结果及 inventory/audit 签名、ConfirmDurable 的全 active/重开登记与 guard/错误、归档维护入口、fault/Dispose |
| S2-Q5 | 最低编号选择已确认；空闲句柄关闭/reader pool 与实际资源成本，不做精确总资源配额 |
| S2-Q6 | 首帧 header 必需已确认；最小字段/codec、识别/tag、CRC/修尾顺序、初始化边界与用户扫描规则，交由 Coding Agent 定稿 |
| S2-Q7 | 数量上限与立即拒绝已确认；config 文件位置/格式/默认/缺失或非法值/生效策略、计数及短期 Append 租借准入 |

完整出口包含多个 active 的交错构建、软轮转、独立使用/恢复，不依赖发布库或其他扩展存在。单文件子合同可以先 Accepted，但不等于文件租借、多文件恢复或整个 S2 已完成。未来多线程 Builder 资格不由首版串行结果推定。
