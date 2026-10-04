# S2：FrameStore 核心、地址与文件生命周期

状态：**Draft；2026-10-04 改为不透明分配合同，并分离窄日志能力；wire/API 尚未冻结**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)。本阶段独立于发布和命名层。

## 本阶段目标

创建 `src/FrameStore/FrameStore.csproj`、`tests/FrameStore.Tests/FrameStore.Tests.csproj`，采用 `Atelia.FrameStore` 身份。
提供独立的多文件不可变 Frame 分配器：Create/Open/OpenReadOnly、申请并完成单帧、随机读取、不透明地址、恢复及同步耐久确认。普通分配不提供业务上的全局顺序。
首版候选后端仍一个 active 文件、历史文件 sealed，内部串行调用 RBF；不同 owner 使用同一合同。单 active 是当前实现范围，不是调用方可据以解释地址的语义。
同一项目提供窄的 `FrameLog` 候选能力，供确实需要追加顺序的消费者使用。按需引用 Rbf/Data/Primitives，不引用旧库或业务项目，不增加第三个生产项目或通用多流调度平台。

## term `FrameStore-Context` 存储上下文

一个持有持久 StoreId、访问模式及实例生命周期的存储 owner。它拥有文件组织、所有读写句柄和 fault 事实，调用方串行使用。
候选 StoreId 为随机非零 128-bit 值，在 create-only 格式门中持久化；路径移动不改变身份。复制与外部改写不由本协议自动协调。

## 地址的候选表示

FrameAddress 对外是不透明、可持久编码的局部地址；首版内部候选仍采用 `SegmentId + SizedPtr`，正整数 `uint` 文件编号、0/default 非法，ticket 的范围由 RBF/Data 公共 API 校验。不向普通调用方提供字段拆解、地址算术、大小排序或相邻帧推算合同。
StoreId 属于上下文，不必重复塞进每个数据引用。地址相等只在同一个 store 中有意义；相同数值在另一个 store 可能恰好也合法，**裸地址无法检测调用方原始来源错误**。
跨 owner 的 Builder/plan 拒绝由实例生命周期负责；持久上层绑定负责选择正确 store。精确 wire、端序及文本表示在 S2-A 关闭。
成功完成的帧在首版存续期间不搬迁、不改写、不删除。地址不透明不等于已经提供可搬迁的逻辑对象 ID；未来改变定位编码必须另行处理兼容性。取消或截断的未完成预约没有稳定身份保证。

## term `Completed-Frame` 已完成帧

成功 append 或重开后存在的完整 frame 事实。Completed 不等于本次确认耐久，也不等于业务状态已发布；数值地址本身不提供这些资格。
RBF ticket checked read 证明 CRC，不独自证明主链成员；正常签发来源、扫描发现和消费者记录的来源约束不能省略。

## 候选入口与职责

| 入口 | 本层负责 | 消费者负责 |
| --- | --- | --- |
| Create/Open/OpenReadOnly | 格式门、定位元数据、owned handles、恢复 | 选择 store/mode；不把 Open 当全库 audit |
| Allocate/已知尺寸 Builder | 文件分配、提前地址、RBF 完成及 fault | tag、stored payload/meta 编码 |
| ReadFrame | 寻址、完整 CRC、buffer 生命周期 | 解码及业务合法性 |
| Preview/显式 inventory 或 audit | 明确信任级别、实际帧发现与错误 | 不以枚举顺序推断业务关系 |
| ConfirmDurable | 同步确认 owner 的全部完成输出 | opaque 引用闭包及业务生效 |

普通分配器不承诺 ScanForward/ScanReverse 的全局语义，也不提供“地址之后”的业务范围。显式 inventory/audit 可扫描真实文件发现帧，但其枚举次序不构成发布顺序；精确 API 与成本另在 S2-Q4 定稿。

只提供 owned 分配/read/Builder，不公开可绕过本层 bookkeeping 的裸 `IRbfFile`。独立消费者可使用全部 data tag；本层不认识上层控制记录或提交 codec。

已知尺寸入口按 S1 保留 payloadLength、tailMetaLength 两个声明：先调用 MeasureWriteSize 取得容量，再通过 BeginAppend(payloadLength, tailMetaLength, out ticket) 取得当前帧的绑定地址。
正常完成调用 EndAppend(tag)，使用 Begin 的 meta 声明；显式 meta 参数只能核对一致，不能改变划分。FrameStore 的 owned API 签名仍由 S2 审定，不在本层退化为仅保存合计长度。

## term `Frame-Log` 显式有序的帧日志能力

候选代码名 FrameLog，属于 Atelia.FrameStore 项目。它拥有独立 StoreId/句柄，复用分配器后端，但通过窄 facade 只开放串行 Append/已知尺寸 Builder、随机读取、ConfirmDurable 和日志扫描；不开放批量随机预约或底层可写 owner。
候选格式门持久标记 Allocator/Log 用途，Open 核对用途，禁止把普通数据目录直接当日志或绕过 facade 向日志输出。确切 factory、用途字段与复用代码边界尚未冻结。

### spec [S-FS-ALLOCATOR-UNORDERED] 普通分配不赋予业务顺序

普通 FrameStore MUST 以独立地址表达帧引用，不承诺分配次序与业务顺序一致。调用方 MUST NOT 用文件编号、offset 或物理相邻关系推断 Parent、版本新旧或当前状态。
分配器 MAY 以顺序追加作为内部实现。首版单 driver、一个活跃 Builder 及批次完成次序的限制需显式声明；它们不建立业务上的全局排序。

### spec [S-FS-LOG-ORDERED] 日志显式提供完整帧的追加顺序

FrameLog MUST 定义跨文件的唯一追加顺序，保证成功完成的记录位置稳定，并在恢复后按真实 RBF 主链枚举完整帧；不按调用方提交的地址集合排序来代替真实扫描。
在 S0 故障模型内，日志轮转、取消和恢复不得丢弃已完成记录或重排它们。未完成尾的地址可重用，位置本身仍不证明某次追加尝试身份。
顺序能力 MUST 由显式日志入口取得；同一个持久 owner 不得同时接受可绕过日志 bookkeeping 的分配入口。首版不支持修改现有 owner 的用途或导入任意外部文件。

### spec [A-FS-LOG-CURSOR] 日志扫描边界封装布局

候选 GetScanBoundaryAfter(FrameAddress) MUST 完整校验指定帧与尾 Fence，产生绑定 log context 的不透明 cursor；ScanForward(cursor) 复验 anchor 后遍历真实后缀及后续文件。全量回放使用显式起始入口，不从 default 地址猜测 genesis。
cursor 内部可包含文件编号及 RbfScanBoundary，调用方不拆解或自行推算。健康局部 anchor 不等于整个 prefix 已审计，任意随机 CRC 读取不单独证明日志主链成员。完整回放、随机读、边界和内容校验的资格各自报告。

## 候选合同

### spec [F-FS-OWN-FORMAT] 新库有独立格式身份

FrameStore/FrameLog MUST 有自己的格式版本、持久 StoreId、用途标记和定位元数据，不复用旧目录身份。首版后端定位元数据为 active locator；普通分配合同不要求未来所有后端采用同一文件组织。
新库文件 MUST 是 RBF3；active 和 sealed 的实际格式通过 S1 的已打开格式信息检查，不能仅信自家 marker 或文件长度。
Create 为 create-only；Open 不创建缺失 store。格式未知、metadata CRC/字段坏、必要文件缺失明确分类；I/O 保留原异常资格。
格式门发布前的初始创建残留是 incomplete creation，不宣称已有可打开实例；不得盲目覆盖该目录。只读不修尾、不改定位元数据或派生缓存。

### spec [A-FS-CONTEXT-ADDRESS] 地址与上下文资格明确

地址 MUST 明确非法值、segment/ticket 范围、编码、相等和默认规则；Builder/plan 绑定 owner 与 epoch。
来自成功追加、真实主链扫描或已接受上层事实的地址具有明确来源；任意合法数值及一次随机 CRC 读取不自动证明其 intended identity。
只有中性定位信息进入 FrameAddress；Parent、graph schema、提交关系等在上层记录中。跨用途/owner 的 handle 与 cursor 在访问文件前拒绝。

### spec [R-FS-OPEN-RECOVERS] 可写打开接受 RBF 恢复

可写打开 MUST 让 RBF 处理 active 的单尾恢复；报告供诊断，Action 不驱动业务回滚或发布。
Open 不重复读取整个尾 payload 取得结构资格；业务 metadata 作事实前仍 checked-read。sealed 历史只读严格访问，残尾不被自动改写。
首版后端默认只定点访问格式门、locator、active 与唯一 next/staging；历史缺段/坏内容按需读取或完整扫描时报告。有限 Open 不宣称已验证全部历史文件。若未来增加多个 active 文件，必须先重新设计发现、恢复与耐久登记，不能沿用本算法的资格。

### spec [S-FS-ROTATION-NEXT-SLOT] 轮转只有一个未发布 next 槽位

替代草案 `[S-FS-ROTATION-RECOVERABLE]`（DEPRECATED）：固定下面的唯一裁决，不保留“完成或取消任选”的默认。

本条约束首版单 active 后端及其日志用途。普通地址不向调用方暴露轮转关系；allocator 的批次预约由下层内部执行同一规则。

轮转 MUST 为旧 active 完成并 flush → create-only 私有 staging 空文件并 flush → create-only rename 为 canonical next → 原子替换 locator → 释放旧 writer → 签发新追加地址。
只有旧 flush 返回后才封段；新文件在 locator 发布之前没有业务 frame，不签发可开始输出的追加句柄。计划可以提前给出未来段的数值地址，但它没有文件存在/可写/完成资格，不能绕过 locator 先输出。每次至多一个 staging 和一个下一编号，不枚举库存挑最大编号。
locator temp 完整写入、flush/close 后原子替换；locator 指向必要新文件而该文件缺失时拒绝，不恢复成旧 locator。操作模型是进程终止、OS/FS 仍运行；目录项断电耐久不在此合同内。

| 恢复镜像 | 唯一候选裁决 |
| --- | --- |
| locator=旧，next/staging 都无 | RBF 恢复旧 active，继续使用 |
| locator=旧，只有自家 staging，长度小于空 Header 边界 | owned incomplete creation，无业务事实；可写取消该槽位，只读不改它 |
| locator=旧，只有自家 staging，长度等于空 Header 边界 | 只读打开并通过 S1.Format 确认为合法空 RBF3 后，可写取消；只读不改它 |
| locator=旧，canonical next 为合法空 RBF3，staging 无 | 可写再次确认旧 active，然后完成 locator 发布；只读仍以旧 locator 读 |
| locator=新，新 active 存在 | 接受新 active；业务残尾交给 RBF，旧段严格只读 |
| canonical next 有业务内容/坏格式，staging 完整坏格式/超出创建边界，或二者同时出现 | 协议矛盾，明确拒绝，不删不明内容、不挑更早段 |

空 Header 边界使用 `RbfScanBoundary.Empty.EndExclusive`。0–3B 只允许出现在未发布的私有创建槽位；不修改 RBF 对 incomplete Header 的拒绝规则。
staging 所有权来自格式门所属目录、协议专用固定名称及“不签发地址/不写业务帧”的范围；它不是任意 orphan 清理权限。未知其他文件由显式 inventory 检查，不纳入正常 Open 的全目录承诺。
轮转阈值、超阈值单帧及编号耗尽必须在 S2-C 审定为确定算法；同一规则用于追加和地址规划。

### spec [S-FS-OWNED-FAULT] Owned 操作共享 fault

owned append、最终 Builder 提交、flush、轮转和 metadata 输出 MUST 共享 fault；确定参数/state guard 不 fault，RBF preparation/Commit 边界按 S1。
输出异常后停止实例，缓存命中也不绕过 guard；取消资源异常不承诺可继续用。Dispose 尝试释放全部 owned resources，并定稿异常汇总规则。
成功 sealed 的耐久资格来自封段协议；重开的 writable active 前缀在第一次耐久确认中必须重新确认，不能用 Open 成功替代 flush 返回。

## 单帧资格与实施片

`Opened → Building(address known) → Completed`。Completed 后可同步 ConfirmDurable；batch 完成与业务发布尚不在本阶段建立。
ConfirmDurable 的基础规则：无 outstanding Builder 时确认整个 owner 的完成输出，flush 失败不给成功返回。精确批次排他边界在下一合同形成；日志未完成 Builder 同样不能取得确认。
首版候选 facade 在活跃 Builder 期间拒绝 owner 的读取/扫描，不绕过 RBF 的共享状态限制；已物化数据按原生命周期使用。是否需要更宽的历史读取能力仍须定稿，不把“随机读取”写成无条件可交错访问。

1. S2-A：创建项目/solution；定稿不透明 FrameAddress、StoreId/格式门、用途与 locator codec、模式/错误。
2. S2-B：单文件 owned 分配/随机读取、已知尺寸 Builder、基础耐久确认、buffer/fault/Dispose；形成 allocator 与窄日志入口的独立子合同。
3. S2-C：内部文件路径/阈值、FrameLog 跨文件扫描边界与完整 next-slot 状态机，保持纯地址预约和实际轮转一致。
4. S2-D：进程中断/资源失败、只读、错误格式、规模和 public 指南资格。

至少验证两种无 EventHeader 记录、旧地址稳定、FrameAddress 的 context 限制、sealed RBF1 拒绝、各 rotation 窗口、locator 前后异常、active 恢复及完整内容损坏。
日志另外验证用途拒绝、跨文件唯一顺序、取消后的完整主链、cursor 混用与坏 anchor；allocator 示例只按地址取回，不把顺序扫描作为引用关系的来源。
健康 Open 的探测与历史段数无关；冷历史 reader、异常尾扫描、完整 payload 和 audit 的成本另外报告。
项目遵循仓库 SDK/test pin，不复制生产包清单。源码可用不等于包已交付。

## Ready 阻断项与出口

| ID | 需定稿 |
| --- | --- |
| S2-Q1 | 不透明 FrameAddress 的持久 codec、StoreId/用途/locator 编码与版本；上下文检查的实际保证 |
| S2-Q2 | 单 active 后端的文件路径、轮转阈值、超阈值单帧与容量预检算法 |
| S2-Q3 | staging/rename/locator 的平台原子替换、各前缀判定与资源顺序 |
| S2-Q4 | allocator/FrameLog factory 与窄 facade、cursor/inventory、Builder 活跃期读取、ConfirmDurable 和资源签名 |
| S2-Q5 | reader pool、单帧内存及实际资格预算 |

完整出口包含多段独立使用/恢复，不依赖发布库存在。单段子合同可以先 Accepted，不等于整个 S2 已完成。
