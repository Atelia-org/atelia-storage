# 长期运行性能方案的辩证简化审查记录

日期：2026-09-29。目标：[综合方案](bounded-online-io-design.md)。源码基线 `bb7c4fb3eb6477783c70ee61bc62b832be195d07`。

使用 `dialectical-simplification` 技能。用户授权撰写并完善设计，未授权实现或数据迁移；三位 reviewer 全程只读，主线程在质询结束后统一修订。未运行性能基准、故障实验或源码测试，下面的裁决属于代码与执行轨迹推导。

## 1. 共同边界与独立立场

三位 reviewer 收到同一份完整初稿、其§1需求账本及源码基线；Round 1 互不查看结论。证据优先级：本轮用户决定 > 当前源码/测试/真实调用方 > 可执行故障轨迹 > 旧设计 > 未来推测。

| 角色 | 第一轮重点 | 第二轮修订/保留 |
| --- | --- | --- |
| Demand skeptic | 反对未证实的兼容层、范围API、通用repair/prune；发现快照旧峰值成本 | 接受next probe、reader硬上限与完整发布门；收窄“保留兼容入口必然两套引擎”的表述 |
| Minimal architect | per-ref末move已经兼有历史与当前状态；发现缩容漏洞；限制cache承诺 | 接受统一新格式、延期范围API/repair，明确冷读退化与一次发布门 |
| Semantic defender | locator回退、尾帧局部语义、tag三态、suffix读侧预算 | 接受缩容条件和外围删减；明确严格停维代价；撤回对现CLI审计范围的不充分推断 |

主线程独立核查补充：`ComputeNextSequenceNumber` 每次全量扫描；RBF公开正扫无起始边界参数；反向枚举无结果不一定代表4B合法空文件；兄弟SessionJournal仍固定旧存储包；其offline validator另有selected-chain业务扫描，不是仅靠open判定健康。

## 2. 核心裁决与失败证据

每项压缩保留位置、主张、需求来源、当前消费者、复杂度、替代方案、删除失败和置信度。

| 位置/主张 | 需求来源与当前消费者 | 复杂度/最小替代 | 删除或不修正的失败轨迹 | 裁决/置信度 |
| --- | --- | --- | --- | --- |
| 方案§6：末move恢复；不另存head | 用户日常/审计分离；RefMoveFrame、SessionJournal.GetHead、BranchRewind reflog | 保留allocation+Init+tail常数帧读取；不加head文件或prev pointer | 去掉tail读取需重放M条；另存head会产生move成功/head发布失败的新同步窗口 | **keep** tail，**delete**额外head checkpoint；高 |
| §5：固定locator | store分段、用户长期成本；全部Open/append | 每store一记录，只在rotation更新；不扫描目录 | 删除locator则重新寻找max segment；错误信旧locator可能退回旧head/重复序号 | **keep**；高 |
| §5：next定点probe及弱断电合同 | 发布轨迹、ConfirmDurable现有合同；writable reopen | 一次精确active+1路径检查，不加目录fsync平台 | next已写且成功flush，locator断电回旧但CRC有效；若不probe即继续old；若next也消失，probe也无法证明安全 | **simplify**：可见矛盾停维，明确未扩大硬件保证；高 |
| §6：尾帧局部语义 | TryApplyMove/codec实际分工；GetHead/Advance/Move | 小型局部验证器，历史关系仍audit | codec可解码但Close.New非空/重复Init等非法状态被当当前状态；Init来源或RefId不匹配 | **keep**局部校验，**simplify**全链验证；高 |
| §7：快照按live几何缩容 | 固定当前集合的目标；ArchiveRef | 发布前判断L0>2×max(1024,Lnext)，先写操作前快照 | L0=百万，archive至1而suffix仍≤Q；每次reopen继续读百万旧snapshot | **simplify**周期规则；高 |
| §7：reader也限suffix | 有界打开目标；所有Open | 直接boundary seek，Q条硬限、单记录长度上限；复用RBF底层dataStart | 旧但CRC有效snapshot配多年后缀，writer预算不能限制reader；ScanForward+Skip仍扫全prefix | **keep**双侧预算，**simplify**窄scan入口；高 |
| §7：保留单oplog/RefId | 稳定身份；现RefId.Packed和消费者长期持有 | 不分段/不压缩，保留容量上限；非兼容禁令 | 重排后原ticket指向不同创建；简单分段复用ticket造成身份碰撞 | **keep**，**defer**身份重造；高 |
| §7：前置checkpoint和tag三态 | TagPublicationException及故障测试 | checkpoint在本次业务append前；健康状态与publication outcome分开 | 已Confirmed后checkpoint异常被误报Unknown会误导重试；前置失败不等于尝试过tag | **keep**三态；高 |
| §8：磁盘cache与ref binding | 自动历史head积累的源码证据；现全量遍历API | 一个预算内memory cache+prefix reuse；删disk cache/binding/tail-merge | 不删则大量head文件、redirect二次累计、额外强引用越过预算；删除有冷读多一遍Parent的真实代价 | **delete/merge**；高 |
| §8：范围API | 未发现实际新API caller；现SessionJournalEventReader转发全量API | 整体延期，不只延期接入却先造接口 | 删除候选API没有当前调用方失效；每步全量请求的二次总量仍存在，须坦诚说明 | **defer**；中高（本地搜索范围） |
| §9：toolkit范围 | audit是用户明确；rebuild是新必需索引的运维要求 | audit+健康事实索引候选重建；通用repair延期，不设cache-prune命令 | 无repair时普通中断坏尾也可能停机；不能据audit存在宣传恢复完备 | **keep**audit/rebuild，**defer**repair，**delete**prune；高 |
| §11：实现切片与发布门 | 本轮仅设计，无中间发行需求 | 内部分片，一次完整v2门禁 | 只交尾读而catalog仍全量时误报“v2长期成本已解决” | **merge**发布门；高 |

保留这些机制由具体反例支撑，不是按reviewer票数决定。没有引入新的持久head权威、route DAG、数据库引擎或后台checkpoint调度器。

主线程另外执行了快照公式的纯计数模型：从1,000,000项已发布snapshot逐个archive到1项，按方案的发布前预算/缩容条件触发9次checkpoint；最后snapshot为1,954项、suffix为1,953条，共3,907条日常控制记录。每一步断言suffix≤Q、snapshot项数≤2×max(1024,当前live)均成立。它验证的是该反例及公式，不是文件I/O基准、实现测试或任意工作负载的形式证明。

## 3. 唯一第三轮：generic旧路径是否保留

第二轮存在实际差异：Minimal architect、Semantic defender建议新RbfSegmentStore版本统一locator；Demand skeptic指出旧入口可以与新入口共享实现，不能将兼容等同于“两套引擎”。

第三轮只要求决定性证据：是否存在同一新版本必须读取旧generic store的真实调用方/发布承诺，或统一locator必然造成巨大额外改造的代码反例。未找到这样的本地证据；Demand skeptic接受统一locator作为本次明确breaking版本的范围选择，并保留“兼容入口可共享，但当前没有必须保留的需求”的限定。

裁决：新runtime统一locator，拒旧，无EventJournal fallback；独立旧binary/源码作为后续迁移解释器。必须提升受影响包版本、明确依赖下限并执行直接SegmentStore/public PackageReference smoke。未找到本地独立调用方不等于nuget没有外部用户；这由明确breaking release承担，而不是假称不存在兼容影响。

## 4. 纠正的假设与事实冲突

- 初稿Q只按上次live数给预算，却承诺当前live数有界：补几何缩容，读侧也硬限预算。
- 初稿locator表允许old+next继续使用old：改为立即MaintenanceRequired；不将next probe包装为完整断电保证。
- 用户决定的是历史验证离线，并未明确要求取消自动修尾：严格无修复是方案建议，已单列停机/恢复能力的变化。
- 删除disk cache不是所有读取都提速：全量冷读可能额外Parent walk，operation临时内存不受retained-cache预算覆盖。
- Semantic defender第一轮推测CLI仅靠open声称全库健康，主线程读到`SessionJournalOfflineValidator`另调用`ScanCheckedAuditEvents`后，该推测明确撤回。最终只要求清楚区分storage物理审计与selected-chain领域审计。
- DurableGraph是tag需求来源，但本次未找到其当前项目；不把派生设计文档当成独立消费者实证。

## 5. 可观察的减法与未解决边界

相对初稿删除/延期：新runtime的legacy路径、range public API候选交付、`repair-tail`与`cache-prune`两个toolkit命令、ref-local tail-merge/binding。保留三个持久入口记录种类（format、locator、catalog snapshot）；增加的是已有协议的边界检查和缩容条件，没有增加持久权威种类。

本次没有需要靠新产品需求才能决定的架构阻塞。方案中仍应由用户在批准实施时明确接受的可见取舍已写明：strict坏尾停维且首版无通用修复、弱目录断电保证、单oplog容量上限、checkpoint O(live)延迟尖峰、删除cache后的冷遍历代价。它们是建议而非已经被本次设计请求授权的上线行为。

验收状态：文档已按上述裁决修订；源码、包、真实性能曲线、硬件断电行为、消费者实际迁移均未验证或实施。下一步若授权施工，应以综合方案的唯一交付门执行，不把本记录当成测试通过或数据升级证据。
