# 下游反馈驱动的 FrameStore / VersionStore 公面完善计划

状态：**第一片已实施并完成独立源码审阅；运行与验收证据见[增量记录](14-downstream-api-refinement-acceptance.md)。下一片仅剩 VS 惰性恢复报告，尚未实施。** 更新：2026-10-11。原方案经两轮 dialectical-simplification 收敛，设计基线为 `e989ba6`，实施起点为 `89d4acd`。

本文只维护剩余跨层施工队列。已完成的精确读取、FS 用户 Length/读取切片与 VS 公共组合 guard 已归入 [FS S2](../FrameStore/02-framestore-core.md)、[VS S4](../VersionStore/04-versionstore-publication.md)、[S5](../VersionStore/05-versionstore-names-and-indexes.md)、[11 bytes 公面](../VersionStore/11-versionstore-root-payload-bytes-api.md)；不在这里保留第二份实现合同或已完成任务清单。

## 1. 剩余需求与必须保留的边界

| 来源 | 下一片需求或约束 |
| --- | --- |
| DurableGraph 反馈 | 观察一次访问期间 VS ref/tag 文件的物理修尾，不能让下游穿透底层布局 |
| 当前源码与规范 | 单 owner/driver 串行；VS 借入实际 FS、不拥有它；自有结果独立于 owner；RBF3 / ProcessCrashOnly |
| 当前恢复与 CU 实现 | 可写惰性打开可能修尾；RO 不修尾。必要 CRC/结构/peer/清理错误不能伪装成未提交或健康 missing |
| 第一片公面 | 精确读与公共 guard 已归入规范；新增报告不改变 owner-only 内部查询、发布 fault/Outcome、history 计费或 owned 清理 |
| 尚需消费者决策 | 确认范围/调用时机、真实长期句柄预算、跨进程 revision 导入、writable 禁止恢复需求；均不阻塞报告片 |

入口证据：[ref backend](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.cs)、[tag backend](../../src/VersionStore/Internal/Storage/DirectoryVersionStoreFiles.Tags.cs)、[RBF report](../../src/Rbf/RbfTailRecoveryReport.cs)与[RBF 恢复合同](../Rbf/rbf-interface.md)、[既有惰性恢复测试](../../tests/VersionStore.Tests/Public/PublicPayloadAndRecoveryTests.cs)。DG 生产入口仍基于旧栈，反馈不是已运行的新栈消费验收；旧全库重开屏障不直接成为新 VS 的需求。

## 2. 下一片：VS 惰性物理恢复报告

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


## 4. 下一片实施与验收

主线程先确定两个 backend 共用的动作集合与 owner 只读 view 接线、容量准备位置及资源移交规则，再按无冲突写入边界委派。完整结果为 ref/tag 的所有 writable opens 都记录成功修尾动作；不新增恢复算法或 RBF 失败证据协议。

必要验收：

1. ref 初始/后续补尾与截短、tag、CU peer、history/ListForks/ScanTags 都走共同通道；cache/扫描借用不增加记录，重复健康打开不增加台账。
2. 工厂成功修尾后，必要 header/初始化保护/CU 校验、visitor/取消、资源移交或关闭失败仍保留动作。单次清理与锁最后不变；NotAttempted 可以伴随修尾。
3. 既有 view Count 随新动作增长，旧项及顺序不变；before/after 固定区间只含本次新增项。普通集合 cast 不能改写 backing；保存 view 不延长 owner 文件/锁寿命，fault/Dispose 后仍可读。
4. 容量准备先于可写工厂，返回 report 后立即无分配追加；RBF 工厂失败可能已修改 bytes 却没有成功报告，不伪造 None，不承诺完整取证。RO 需恢复时拒绝且 bytes/hash 不变，FS/VS 动作不混淆。
5. 第一片 ReadRevision/guard、CU 共同资格、历史预算、发布证据和源/子确认顺序保持；新报告不增加 data 探测或确认。

先 Release build，再串行匹配配置的定向/必要完整回归，保持 RBF1 精确 package 与 main project 资产隔离。backend 打开/恢复接线变更须补 Windows 与 WSL2/ext4 的必要恢复增量，不自动重跑全部进程窗口或资源规模矩阵，不沿用旧 hash。只有实际准备新本地包时才用新版本、干净提交和四个隔离 PackageReference smoke；不自动打包、发布或迁移 DG。

## 5. 报告设计的已审查结论

原方案的两轮独立论证已收敛：报告保留成功物理动作，修尾后上层失败也不能抹去；先准备容量再调用工厂，不建立 per-open reservation/commit 或事件协议。全库确认、LRU、revision 导入及禁止 writable 恢复仍按第3节触发。

固定全集需求只来自初稿，审查双方撤回；采用只读追加 view，需要冻结由 caller 复制。不能把潜在累计复制成本写成已测性能。原 PowerShell/.NET 10.0.9 探针发现 `ReadOnlyCollection<int>(List<int>{1})` 的 `ICollection.SyncRoot` 返回原 List，可经普通 `IList<int>` cast 改写 backing；因此采用当前 RefIdList 式私有薄 view，不暴露该通道。此探针只验证 BCL 暴露反例，不是新报告 API 或两平台验收。
