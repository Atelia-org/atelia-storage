# 下游反馈驱动的 FrameStore / VersionStore 公面完善计划

状态：**两轮 dialectical-simplification 已收敛，方案可进入实施；四项增量均尚未实施。** 源码基线：`e989ba6`，2026-10-11。本文是下一轮施工备忘，统一记录跨层变更、范围和验收；现行合同仍在各模块规范。实施时将选定行为归入对应规范，删除本文已完成的施工项，以链接保留验收证据，不积累第二份完成历史。

## 1. 需求账本与证据边界

| 来源 | 当前需求或必须保留的行为 |
| --- | --- |
| 本轮用户指令 | 先形成重构备忘并做辩证审查；下一轮才实施，不在本轮改生产代码或持久格式 |
| DurableGraph 本轮反馈 | 只保存轻量 revision 后按需取 root bytes；观察访问期间的物理恢复；缓存命中前检查 backing 寿命；直接统计用户写入长度与拆分读取 payload/meta |
| 当前源码与规范 | 单 owner/driver 串行；VS 借入实际 FS、不拥有它；自有结果独立于 owner；ref 位置不删除/复用，正常持有期间无外部改写；RBF3 / ProcessCrashOnly |
| 当前 CU 实现 | 发布/当前值/历史/fork/Audit 共用 CU 共同资格；合法未提交 CU 可以被当前值跳过，必要错误不能伪装为未提交；exact 来源不能改选旧点 |
| 当前消费与兼容约束 | 仓内实验使用 writer.Length 差值或不变性检查；主线已有本地候选，非冻结公开 API。改变 Length 仍是公开行为变更，旧候选和验收不因此变化 |
| 源码检索与旧 DG 合同 | DG 的生产入口仍基于旧栈；本轮反馈是新栈接入需求，不是已运行的新栈消费验收。旧全库重开屏障不能直接证明新 VS 需要全库确认 |
| 尚未明确的需求 | 无新增历史确认的对象/调用时机；长期 owner 的真实 ref 数及句柄预算；是否需要跨进程导入精确 revision，或禁止 writable 自动恢复 |

反馈与上轮主线程分析来自同一次提议，不是两份独立证据。以下源码、测试和历史运行记录分别说明已经存在的机制、反例和实测范围，不证明新增 API 已验收：

- [FS Length façade](../../src/FrameStore/FramePayloadWriter.cs)、[共享 lease](../../src/FrameStore/Internal/Runtime/FrameLease.cs)、[FrameRead](../../src/FrameStore/FrameRead.cs)；原合同见 [S2](../FrameStore/02-framestore-core.md) `[A-FS-OWNED-BUILDER]`。
- [VS exact fork](../../src/VersionStore/VersionStore.Fork.cs)、[共用发布读取](../../src/VersionStore/VersionStore.ConditionalRead.cs)、[生命周期检查](../../src/VersionStore/VersionStore.cs)；资格见 [S4](../VersionStore/04-versionstore-publication.md)、[S5](../VersionStore/05-versionstore-names-and-indexes.md)、[CU](../VersionStore/07-versionstore-cross-ref-conditional-update.md)。
- [ref backend](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.cs)、[tag backend](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.Tags.cs)、[RBF report](../../src/Rbf/RbfTailRecoveryReport.cs)与 [RBF 恢复合同](../Rbf/rbf-interface.md)。
- [精确来源校验反例](../../tests/VersionStore.Tests/Forks/ForkSourceValidationTests.cs)、[嵌套伪帧反例](../../tests/VersionStore.Tests/AuditPublic/PublicRefAuditTests.cs)、[惰性恢复测试](../../tests/VersionStore.Tests/Public/PublicPayloadAndRecoveryTests.cs)、[实际 ref 缓存规模](../VersionStore/12-versionstore-linux-resource-acceptance.md)。

## 2. 选定候选：复用现有机制，补四处公面

### 2.1 精确读取库签发 revision

```csharp
public AteliaResult<RefBytesSnapshot> ReadRevision(RefRevision revision);
```

仅接受 nondefault、同持久 VSID 的库签发值；不接受裸 ticket、不提供位置构造或导入/导出。先检查 VS 自身 owner，再在发现 I/O 前拒绝 default/foreign 值。支持 RO/RW owner，读取期间仍按既有模式执行惰性打开。没有新发布、data barrier、源 flush 或 PublicationOutcome。

复用 exact fork 的完成范围/必要 header、ReadFrameInfo 形状预检、ReadPublication 完整 CRC/envelope/meta/CU 共同资格及成功目标的 GetScanBoundaryAfter 尾 Fence 检查。共同 exact 助手保留已有 `PublicationRead(Snapshot?)`：必要错误原样传播，null 仅代表合法未成立 CU；ReadRevision 在这一分支生成读取错误，fork 保留 InvalidForkOrigin，不建立通用错误适配器。已判定未成立的路径仍按现有 fork 顺序失败，不额外执行成功目标的 Fence 核验。

ReadPublication 已从 pooled frame 捕获独占 GC bytes 并归还 pooled 帧。ReadRevision 直接交付这份 RefBytesSnapshot；fork 同步把这份尚未交付调用方的私有 snapshot.Payload 传给私有发布助手，避免原 `CaptureExactPublication` 的第二次 ToArray。私有 PublishNewRef 可接受 ReadOnlySpan，但仅消费已经捕获的自有 bytes，不保存 span 或跨异步边界；普通 CreateRef/PublishRef 的用户输入捕获继续保留。fork 仍按 data barrier → exact source flush → child output 顺序执行。

返回 `Revision == 输入`、原 RecordedAtUnixMs 与仅 root bytes，不泄露 CU Members 或 TailMeta。exact CU 未成立时失败，不能回退。必要 peer/CRC/结构/I/O/清理错误消费现有规则；普通校验失败不额外 fault，实际 owned 恢复/清理失败按既有停用合同处理。history/tag visitor 内允许串行精确读取，借用匹配已有句柄，不新增永久 peer 缓存。

成员来源继续依赖“已签发 revision + 完整位置不删除/复用 + 无外部改写”，无需为重新定位本地目标扫描来源后缀或重新证明前序链；RBF 工厂当前 EOF 资格和 CU peer 的必要后缀消歧仍保留。CRC 与后 Fence 本身不能证明真实主链成员；嵌套伪帧反例阻止将这条路径推广为任意位置读取。库关闭后保存的值可用于同库重开；当前没有跨进程匿名 revision 导入能力。CU 仍可能涉及多个文件及必要消歧，不承诺 O(1) I/O。

不重写历史遍历内核：它继续使用带 beforeWork/checkState 的共同发布读取，所有必需工作仍计费。visitor 自行调用 ReadRevision 是独立读取，不自动纳入外层 HistoryBudget；不为新入口引入预算或重入框架。

实施时更新 S4 `[A-VS-REF-REVISION-VALUE]`、S5 精确来源描述、11 bytes 公面及 src README；保留裸位置/导入仍不开放的边界。

### 2.2 FS 用户长度与读取切片

`FramePayloadWriter.Length` 改为本次 Builder 的用户 `Reserved + Advanced` 累计 bytes，从 0 开始，包含 Payload 和 TailMeta。known-size 的声明容量不等于已使用长度；unknown-size 到 End 才确定 meta 分界。GetSpan/GetMemory、TryGetReservedSpan、回填和 Commit 不增加 Length；成功 ReserveSpan 立即计入，Advance 按实际 count 计入，确定参数/协议/容量拒绝不增加；Advance(0) 仅归还借用。底层异常继续消费现有 fault/lease 合同，不扩展成 Data writer 的通用事务回滚保证。

只在 FS façade 建立底层长度原点，不改 RBF/Data 计数、预算、CRC 或 finalize。共享 lease 保存一次原点，不硬编码 HeadLen=4、不为每个 writer 副本另计数。选定落点为首次受 guard 保护的 **FrameBuilder.PayloadAndMeta getter**，在 writer 对外返回前捕获；不能等第一次 Length 访问才捕获，也不在内层 Begin 成功后的旧 catch 中新增失败点。具体字段表示由实现者选择。

```csharp
public ReadOnlySpan<byte> Payload { get; }
public ReadOnlySpan<byte> TailMeta { get; }
```

两属性从已经完整校验的 `PayloadAndMeta` 切片，不复制或重复解码；先取得该 span，继承 pooled buffer 的 Dispose guard，meta=0 的空切片也不能绕过它。FrameRead 独立于 FS owner 存活，但其 Dispose 后新 getter 拒绝，先前 span 失效。保留现有 PayloadAndMeta 和 TailMetaLength。

这是 Length 的公开语义变更，不增加第二个同义长度属性或旧计数兼容模式。仓内差值调用仍成立；审查其他消费者的绝对值假设并写迁移说明。物理尺寸继续使用 RBF 公共 MeasureWriteSize。实施时同步 S2 `[A-FS-OWNED-BUILDER]` 和 src README 的旧投影说明，不修改旧验收或 feed 包。

### 2.3 VS 公共组合可用性检查

```csharp
public void EnsureUsable() {
    EnsureOwnerUsable();
    _data.EnsureUsable();
}
```

把现有 private EnsureUsable 更名为 EnsureOwnerUsable，并保持其内部调用范围；新增公共方法组合检查 VS 自身及实际 borrowed data。自身 Disposed 优先，再检查自身 fault，最后检查 data。RO/RW、活跃 Builder 和现有 visitor 状态都不影响这份寿命资格；无 I/O、模式提升、恢复、确认或内容健康检查。

data guard 原异常传播，本次纯准入失败不额外 fault/清理 VS；真正发布中的 data barrier 异常仍停用 VS 并保留既有 Outcome。调用方仍须维持 data 寿命并串行操作，检查不是持续租借。纯属性继续是保存值；自有 snapshots/revision/recovery reports 不要求 guard。Dispose 不调用组合 guard，不 Dispose data。

实施时在 S4 `[S-VS-DATA-BORROW]` 增加明确公共组合入口，保持“纯发布目录查询不自动探测 data”的现行边界；不建立健康状态查询框架。

### 2.4 VS 惰性物理恢复报告

建议新增一个 owner 累计、仅记录成功 RBF open 返回的非 None 动作的 `RecoveryReports`。FS 现有 factory 报告继续包括每个 active 的 None；统一的是物理动作含义与资格边界，不强求两层报告在相同时点填充或使用相同容器。

候选公共形状：

```csharp
public enum VersionStoreRecoveryFileKind { Ref, TagBucket }

public readonly record struct VersionStoreRecoveryReport(
    VersionStoreRecoveryFileKind FileKind,
    uint LocalId,
    RbfTailRecoveryReport Recovery);

public IReadOnlyList<VersionStoreRecoveryReport> RecoveryReports { get; }
```

Ref 的 LocalId 为非零本地 ref 编号，TagBucket 的 LocalId 为 0..1023 桶号；该列表属于本 VSID。独立日志导出时一并保存 owner 的 VersionStoreId，不再公开路径、TagName、reader、lease、TxId 或另一套文件身份。报告只是诊断值，不能签发 RefRevision。

getter 返回累计台账的**只读、只追加视图**，不复制全集：Count 可以增长，已有项及其顺序不变。观察操作增量先保存 beforeCount，操作后保存 afterCount，按 `[beforeCount, afterCount)` 读取；需要固定集合或异步交付日志时由调用方复制。枚举不提供跨调用快照保证。视图只持有动作值集合，不持有 owner/backend、文件、锁、lease 或捕获 owner 的 delegate；fault/Dispose 后 getter、Count/indexer 和既有视图仍可读，不调用可用性 guard。采用只开放读接口的私有薄封装，不暴露 ICollection.SyncRoot 或其他可写 backing 通道。

初始列表为空。普通 ref、history/ListForks 临时 ref、CU peer、named tag、ScanTags 桶的 writable opens 都必须通过共同记录通道。cache/扫描句柄借用不产生新报告；以后同文件 None 不覆盖旧动作。仅保留实际修尾动作，内存 O(动作数)，不为所有健康打开建统计目录，不承诺报告量硬上限。

在 writable RBF 工厂前确保可追加下一项的集合容量；工厂成功返回 report 后立即无分配追加非 None 值，早于 tag 初始化恢复保护及 VS header/CU/必要清理。不另造 per-open 动作槽、reservation/commit 或事件协议。已有修尾后上层校验失败、visitor 异常、取消、清理失败、fault/Dispose 都不能清空动作；工厂返回句柄与报告后的资源移交失败仍须单次清理，锁最后释放。两个 backend 共用一个内部动作集合，不新增 public event 或通用 observer 框架。

报告不证明发布成立、root/data 图健康或旧调用 Confirmed。`NotAttempted` 仍可能伴随目标 lazy 修尾。RBF 工厂在 write/SetLength/flush/复检期间失败可能已修改 bytes 却没有成功报告；该边界不伪造 None，不承诺完整失败取证，不扩展 RBF 证据协议。RO 不修尾、需恢复时拒绝；借入 FS 的先前恢复只属于 FS 报告。

实施时在 S4 恢复/保存值合同归入公共报告形状，09 引用同一通道说明 tag 覆盖；RBF 和 FS 原恢复策略与报告合同保持。

## 3. 延后事项与重访条件

| 事项 | 当前裁决及触发条件 |
| --- | --- |
| 无新增历史的确认 | defer：先明确 DG 要确认全库、当前 ref、exact revision、tag 或继承历史，以及启动/Unknown 后/每次保存的时机，再设计最窄入口。仅确认已缓存 ref 不是全库；若承诺确认完整 CU 发布，须确认全部成员。现有 fork 只确认 exact source，符合 S5 的 ProcessCrashOnly 合同，不借反馈扩大为断电保证 |
| 有界 ref 缓存 | defer：当前 16/64/256 资源基线证明增长来自缓存、重复稳定且 Dispose 释放；长期 owner 的上千/上万 ref 或明确句柄预算出现时再实施。CU 批次和内部借用须 pin，缓存容量不能直接承诺总 handles 硬上限；不先指定 LRU 或公开 lease |
| revision 导入/导出 | defer：用户或 DG 明确跨进程匿名选点需求后，另立完整上下文和真实成员资格合同；ReadRevision 不扩大到裸位置 |
| writable 禁止恢复 | defer：消费者明确需要“继续写但遇残尾拒绝”才设计；现有严格 RO/Audit 足以承担当前只读取证入口 |
| CAS、命名目录、业务 Parent | 保持下游职责；本轮反馈没有要求上移，VS 发布历史不替代 DG 业务链 |

以上不是下一轮必做项，不重新把实现细节塞回已清空的 design-questions.md。

## 4. 下一轮实施边界与验收

优先形成第一片：**精确读取 + FS 长度/切片 + VS 公共 guard**。主线程先确定 helper/guard 命名、私有发布输入及错误边界；FS 包与合并后的 VS 包可以并行，VS exact/guard 共用一个写入所有者，避免跨 partial 改名冲突。主线程独立复核并整合合同与公开消费者；随后完成第二片 **VS recovery reports**。是否同轮完成第二片由第一片实际进度和复核结果决定，不为了凑齐反馈项目压缩验收。

| 包 | 代码边界与必要交付 |
| --- | --- |
| VS 精确读取与 guard | exact helper/公开入口、唯一未成立分支的错误与 fork 复用；旧点/继承点/CU、身份与完整位置、owned 返回；自身检查 rename + borrowed data 组合，保留纯查询/publication/Dispose 边界 |
| FS façade | FrameLease/FramePayloadWriter/FrameRead 与必要调用方；逻辑累计量、零复制分界及生命周期；RBF 合同不变 |
| 主线程集成与独立验收 | shared contracts、public consumer、规范/指南同步与反例检查；按实际依赖串行构建/测试 |
| 恢复信息（第二片） | 两条 backend open 接线及所有临时/owned 打开路径；非 None 动作、只读追加视图、修尾后失败、记录时点与资源移交故障 |

高价值验收必须能区分：

1. ReadRevision 的旧点读取不为重新定位本地目标扫描来源后缀，工厂 EOF 和 CU 必要消歧仍保留；完整目标/Fence 或必要 peer 坏必须失败，健康目标不因无关 payload 坏拒绝。成立 CU 只返回 root，未成立 exact CU 不回退。保存值可同库重开读取；自有结果在后续读取及 owner fault/Dispose 后不变。fork 单次捕获不改变普通 Create/Publish 的输入捕获及确认顺序。
2. default/foreign revision 在 I/O 前拒绝；history/tag visitor 内嵌套读取复用匹配句柄，必要清理单次、锁最后。保留嵌套伪帧不能由裸位置签发的反例。
3. known/unknown、空帧、纯 meta、混合帧的 Length 初值/累计；预留立即计入，Commit 不重复计数，失败/未 Advance 的 guard 不变；可纠正 End 失败后仍能继续。End/取消/复用/fault/Dispose 后旧 writer 拒绝；切片含空 meta 在 FrameRead.Dispose 后同样拒绝。
4. VS/Data 各自 healthy/fault/disposed、RO 与活跃 Builder；公共组合检查无 I/O/确认，不引入额外 fault；内部纯查询仍只检查自身。data 已失效后 VS Dispose 仍完整清理。
5. 恢复片覆盖 ref 初始/后续补尾与截短、tag、CU peer、history/ListForks/ScanTags；成功修尾后必要校验失败仍保留 report。重复健康打开不增加台账；既有视图 Count 随新动作增加但旧项不变，捕获计数后的区间只含该次新增动作。普通集合 cast 不能改写 backing，保存视图不延长 owner 文件/锁寿命。
6. RBF 工厂失败无成功报告的边界保留原异常与 fault/清理行为；报告分配准备先于可写工厂。RO 残尾拒绝且文件 bytes/hash 不变，FS 与 VS 动作不混淆。

源码实施后先 Release build，再串行相同配置定向/必要完整回归，保留 RBF1 精确 package 与主线 project 资产隔离。纯 façade 变更用平台无关行为测试；改变 backend 打开/恢复接线时补 Windows 与 WSL2/ext4 的必要恢复增量，不自动重跑全部进程窗口或资源规模矩阵，也不沿用旧 hash。只有实际准备新本地包时才用新版本、干净提交和四个隔离 PackageReference smoke；本计划不自动打包、发布或迁移 DG。

## 5. 本轮审查记录

三位独立 reviewer 分别承担需求怀疑、最小架构、语义保护；完成独立论证后交换最强相反失败轨迹。主线程核对源码及现有测试反例后裁决，两轮已收敛，无需第三轮。审查只改本文及导航，不提前改现行 API/wire 规范或历史验收。

| 对象 | 裁决 | 保留理由或本轮改进 |
| --- | --- | --- |
| ReadRevision / exact fork | merge / keep | 共用已有三分读取资格，只有合法未成立分支由入口各自解释；成功 exact 保留目标 Fence，不开放裸位置，不改历史计费 |
| fork 第二次 bytes 复制 | simplify | 同步消费已经私有捕获的 snapshot.Payload；普通用户输入捕获保留，省去每次 fork 一份最多 1MiB 的重复数组 |
| FS Length / 读取切片 | simplify | 首次 guarded Builder getter 捕获共享原点，两个切片继承现有 buffer guard；不增加双口径或用户计数状态机 |
| VS guard | keep / merge | 显式组合寿命检查，内部仍 owner-only；VS exact/guard 合并写包，避免 rename 冲突 |
| 恢复动作保留与时点 | keep | 成功修尾后仍可能 VS 校验/移交失败；先准备容量，工厂成功后立即记录，fault/Dispose 不清台账 |
| 恢复全集冻结 | simplify | 固定全集需求只来自初稿，双方撤回此要求；改为只读追加视图，需要冻结由 caller 复制。不将潜在累计复制成本写成已测性能 |
| 全库确认、LRU、revision 导入、禁止 writable 恢复 | defer | 重访条件见第3节；当前选定实施片不依赖这些产品选择 |

另做了一次 PowerShell / .NET **10.0.9** 的 BCL 行为探针：`ReadOnlyCollection<int>(List<int>{1})` 的 `ICollection.SyncRoot` 返回原 List，可 cast 为 IList<int> 并改成9，view 同步变化。需求怀疑者据此撤回直接用 BCL ReadOnlyCollection 的建议；只读薄 view 复用当前 RefIdList 的边界，禁止该可写通道。此探针验证集合暴露反例，不是新增 API 或两平台验收；本轮没有构建/运行 solution 测试。
