# S2：FrameStore 核心、地址与文件生命周期

状态：**Draft；单流/owned API 与恢复算法已有候选默认，wire/API 尚未冻结**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)。本阶段独立于发布和命名层。

## 本阶段目标

创建 `src/FrameStore/FrameStore.csproj`、`tests/FrameStore.Tests/FrameStore.Tests.csproj`，采用 `Atelia.FrameStore` 身份。
提供独立的多段不可变 Frame 存储：Create/Open/OpenReadOnly、单帧追加和读取、中性地址、轮转、恢复及同步耐久确认。
每个实例一个追加流、一个 active segment；历史段 sealed。按需引用 Rbf/Data/Primitives，不引用旧库或业务项目。不同实例使用同一合同，不增加 StreamId/多流调度平台。

## term `FrameStore-Context` 存储上下文

一个持有持久 StoreId、访问模式及实例生命周期的存储 owner。它拥有文件组织、所有读写句柄和 fault 事实，调用方串行使用。
候选 StoreId 为随机非零 128-bit 值，在 create-only 格式门中持久化；路径移动不改变身份。复制与外部改写不由本协议自动协调。

## 地址的候选表示

FrameAddress 采用 `SegmentId + SizedPtr` 的局部坐标；首版建议正整数 `uint` segment 编号，0/default 非法，ticket 的范围由 RBF/Data 公共 API 校验。
StoreId 属于上下文，不必重复塞进每个数据引用。地址相等只在同一个 store 中有意义；相同数值在另一个 store 可能恰好也合法，**裸地址无法检测调用方原始来源错误**。
跨 owner 的 Builder/plan 拒绝由实例生命周期负责；持久上层绑定负责选择正确 store。精确 wire、端序及文本表示在 S2-A 关闭。

## term `Completed-Frame` 已完成帧

成功 append 或重开后存在的完整 frame 事实。Completed 不等于本次确认耐久，也不等于业务状态已发布；数值地址本身不提供这些资格。
RBF ticket checked read 证明 CRC，不独自证明主链成员；正常签发来源、扫描发现和消费者记录的来源约束不能省略。

## 候选入口与职责

| 入口 | 本层负责 | 消费者负责 |
| --- | --- | --- |
| Create/Open/OpenReadOnly | 格式门、定位元数据、owned handles、恢复 | 选择 store/mode；不把 Open 当全库 audit |
| Append/已知尺寸 Builder | segment 分配、精确地址、RBF 完成及 fault | tag、stored payload/meta 编码 |
| ReadFrame | 寻址、完整 CRC、buffer 生命周期 | 解码及业务合法性 |
| Preview/扫描 | 明确信任级别、主链遍历与错误 | 需要内容资格时 checked read |
| ConfirmDurable | 同步确认 owner 的全部完成输出 | opaque 引用闭包及业务生效 |

中性扫描边界能力一并提供：GetScanBoundaryAfter(FrameAddress) 取得 `SegmentId + RbfScanBoundary`，ScanForward(boundary) 复验 anchor 后遍历该段后缀及后续段；空边界从既有 RBF 空边界构造。边界、StoreId/格式和 CRC 校验由本层封装，不暴露 raw file；受证局部 anchor 仍不等于整段历史审计。

只提供 owned append/read/Builder，不公开可绕过本层 bookkeeping 的裸 `IRbfFile`。独立消费者可使用全部 data tag；本层不认识上层控制记录。

已知尺寸入口按 S1 保留 payloadLength、tailMetaLength 两个声明：先调用 MeasureWriteSize 取得容量，再通过 BeginAppend(payloadLength, tailMetaLength, out ticket) 取得当前帧的绑定地址。
正常完成调用 EndAppend(tag)，使用 Begin 的 meta 声明；显式 meta 参数只能核对一致，不能改变划分。FrameStore 的 owned API 签名仍由 S2 审定，不在本层退化为仅保存合计长度。

## 候选合同

### spec [F-FS-OWN-FORMAT] 新库有独立格式身份

FrameStore MUST 有自己的格式版本、持久 StoreId 和 active locator，不复用旧目录身份。
新库文件 MUST 是 RBF3；active 和 sealed 的实际格式通过 S1 的已打开格式信息检查，不能仅信自家 marker 或文件长度。
Create 为 create-only；Open 不创建缺失 store。格式未知、metadata CRC/字段坏、必要文件缺失明确分类；I/O 保留原异常资格。
格式门发布前的初始创建残留是 incomplete creation，不宣称已有可打开实例；不得盲目覆盖该目录。只读不修尾、不改定位元数据或派生缓存。

### spec [A-FS-CONTEXT-ADDRESS] 地址与上下文资格明确

地址 MUST 明确非法值、segment/ticket 范围、编码、相等和默认规则；Builder/plan 绑定 owner 与 epoch。
来自成功追加、真实主链扫描或已接受上层事实的地址具有明确来源；任意合法数值及一次随机 CRC 读取不自动证明其 intended identity。
只有中性定位信息进入 FrameAddress；Parent、graph schema 等在消费者记录中。

### spec [R-FS-OPEN-RECOVERS] 可写打开接受 RBF 恢复

可写打开 MUST 让 RBF 处理 active 的单尾恢复；报告供诊断，Action 不驱动业务回滚或发布。
Open 不重复读取整个尾 payload 取得结构资格；业务 metadata 作事实前仍 checked-read。sealed 历史只读严格访问，残尾不被自动改写。
默认只定点访问格式门、locator、active 与唯一 next/staging；历史缺段/坏内容按需读取或完整扫描时报告。有限 Open 不宣称已验证全部历史文件。

### spec [S-FS-ROTATION-NEXT-SLOT] 轮转只有一个未发布 next 槽位

替代草案 `[S-FS-ROTATION-RECOVERABLE]`（DEPRECATED）：固定下面的唯一裁决，不保留“完成或取消任选”的默认。

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
ConfirmDurable 的基础规则：无 outstanding Builder 时确认整个 owner 的完成输出，flush 失败不给成功返回。精确批次排他边界在下一合同形成。

1. S2-A：创建项目/solution；定稿 FrameAddress、StoreId/格式门、locator codec、模式/错误。
2. S2-B：单段 owned 追加/读取、已知尺寸 Builder、基础耐久确认、buffer/fault/Dispose；可以作为早期纵向片的独立子合同。
3. S2-C：segment 路径/阈值、跨段扫描边界与完整 next-slot 状态机，保持纯地址规划和实际轮转一致。
4. S2-D：进程中断/资源失败、只读、错误格式、规模和 public 指南资格。

至少验证两种无 EventHeader 记录、旧地址稳定、FrameAddress 的 context 限制、sealed RBF1 拒绝、各 rotation 窗口、locator 前后异常、active 恢复及完整内容损坏。
健康 Open 的探测与历史段数无关；冷历史 reader、异常尾扫描、完整 payload 和 audit 的成本另外报告。
项目遵循仓库 SDK/test pin，不复制生产包清单。源码可用不等于包已交付。

## Ready 阻断项与出口

| ID | 需定稿 |
| --- | --- |
| S2-Q1 | FrameAddress/StoreId/格式门/locator 的精确 codec；上下文检查的实际保证 |
| S2-Q2 | 单流 segment 路径、轮转阈值、超阈值单帧与容量预检算法 |
| S2-Q3 | staging/rename/locator 的平台原子替换、各前缀判定与资源顺序 |
| S2-Q4 | owned API、中性扫描边界、基础 ConfirmDurable、buffer/fault/Dispose 签名 |
| S2-Q5 | reader pool、单帧内存及实际资格预算 |

完整出口包含多段独立使用/恢复，不依赖发布库存在。单段子合同可以先 Accepted，不等于整个 S2 已完成。
