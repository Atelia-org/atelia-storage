# Event Ref Store 设计基线

> **状态**：Design Baseline / 待拆分为 Spec
> **日期**：2026-07-23
> **依赖**：[EventJournal 功能需求与粗粒度设计基线](event-journal-requirements-and-design.md)、[EventFrame Parent Chain 设计基线](event-frame-parent-chain-design.md)、[RBF Layer Interface Contract](../Rbf/rbf-interface.md)

## 1. 文档定位

本文设计 EventJournal 的 named ref / branch 层：如何把对外 `BranchName` 解析为稳定 `RefId`，如何保存 ref 当前 head，如何记录 ref move history，如何做 CAS update，如何恢复 ref 文件，以及如何把 raw Event append 和 ref advance 组合成便利 commit。

本文不设计：

- Git 风格全局 `HEAD`、symbolic HEAD 或 detached HEAD。
- `EventFrame` header codec 与 Parent chain traversal。
- payload schema、payload 索引或 v2 multi-frame payload。
- 跨 ref 原子事务。
- 复杂 name alias / rename 策略；MVP 只支持 active branch name 到 active `RefId` 的绑定。

## 2. 核心决策

MVP 不提供 Git 风格全局 `HEAD`。EventJournal 是 OOP API，不是隐式当前目录 CLI；调用方先用 `OpenBranch(name)` 把对外 branch name 解析为稳定 `RefId`，后续具体数据操作只接受 `RefId` 或 `EventAddress`，不再接受 branch name。

`BranchName`、`RefId`、ref segment 文件三者分层：

```text
event-journal/
    events/
        segments/
            000000/
                00000001.rbf

    refs/
        ref-op-log.rbf
        objects/
            0123456789abcdef/
                00000001.rbf
                00000002.rbf
                00000003.rbf.archived
```

`ref-op-log.rbf` 是低频元操作日志，用于创建 ref、记录 fork 来源、归档 ref、维护 active branch name 到 active `RefId` 的绑定。`RefId` 是 `ref-op-log.rbf` 中创建该 ref 的 `RefOpFrame` 的 RBF ticket，持久化时保存该 `SizedPtr.Packed` 64-bit 值。每个 ref object 使用以 `RefId` 派生的目录和分段 RBF 文件保存 move chain；这些 move segment 本身就是该 ref 的 reflog。

归档 ref 不是删除 move chain，而是在 `ref-op-log.rbf` 追加归档操作，并把不再活跃的 ref segment 文件改为 `.rbf.archived` 后缀或移动到归档区；active scanner 默认只扫描 `.rbf`。

该设计吸收 Git 的概念边界：不可变 Event、可变 branch ref、ref history 与 Parent history 分离、expected-old CAS。但不照抄 Git 的 loose refs / packed refs / 文本 reflog / 全局 HEAD 存储形态。

## 3. BranchName、RefId 与路径

MVP 使用严格受限的 canonical branch name，但 branch name 只是对外 well-known locator，不是 ref identity。`RefId` 才是 ref instance 的稳定身份。

建议初版约束：

- 长度：`1..128` UTF-8 bytes。
- 字符：`[a-z0-9][a-z0-9._-]*`。
- 不允许 `/`、`\`、空白、控制字符、`:`、`*`、`?`、`"`、`<`、`>`、`|`。
- 不允许 `.`、`..`、以 `.` 结尾、以 `.lock` 结尾。
- ref name 在 canonical form 中大小写敏感问题先规避：MVP 只允许 lowercase。

### 3.1 RefId

`RefId` 定义为：

```text
RefId = RefOpLog 中 RefCreate / RefFork frame 的 SizedPtr.Packed
```

注意：`RefId` 是承载 `RefCreate` / `RefFork` frame 的外部 RBF ticket，不需要也不应该写入该 frame 自己的 payload。写入时由 `IRbfFile.Append` 返回 ticket；replay 时由 `ScanForward` / `RbfFrameInfo.Ticket` 提供 ticket。上层不需要预测 RBF frame 的未来 `SizedPtr`，也不需要了解 RBF 文件布局细节。

选择 `SizedPtr` 而不是 `EventAddress` 作为 `RefId` 的原因：

- ref create / fork / archive 是低频元操作，用单独 `ref-op-log.rbf` 记录即可，不需要污染 EventFrame Parent graph。
- `SizedPtr` 是 64-bit，足以在单个 RefOpLog 文件内唯一定位创建该 ref 的 frame。
- `RefId` 可通过读取 RefOpLog 中对应 frame 验证，且不会因 branch name 删除后重建而重复。
- RefOpLog 操作频率低，MVP 不需要对它分段；若未来真的增长到需要分段，应升级为新的 RefOpLog 设计，而不是改变既有 `RefId` 语义。

`RefId` 的显示形式使用 16 位小写 hex，例如 `0123456789abcdef`。文档和诊断输出 MAY 使用短前缀，但文件路径 MUST 使用完整 16 位 hex。

### 3.2 Ref Object 路径

ref move chain 使用按 `RefId` 分目录、按 ref-local segment number 分段的文件。目录名已经承载 `RefId`，segment 文件名只使用 ref-local segment number：

```text
refs/objects/<ref-id-hex>/<segment-number-8hex>.rbf
```

示例：

```text
refs/objects/0123456789abcdef/00000001.rbf
refs/objects/0123456789abcdef/00000002.rbf
refs/objects/0123456789abcdef/00000003.rbf.archived
```

规则：

- ref-local segment number 从 `1` 开始。
- active segment 使用 `.rbf` 后缀。
- archived segment 使用 `.rbf.archived` 后缀；active scanner 默认忽略 archived 文件。
- 文件名不重复 `RefId`；若文件脱离目录，必须依赖 frame 内部 `RefId` 或外部诊断信息确认身份。
- segment 文件内部仍必须在 frame 中保存并校验 `RefId`，不能只信路径。

## 4. RefOpLog

`refs/ref-op-log.rbf` 是 branch name 到 `RefId` 映射的 canonical source。打开 store 时 replay RefOpLog 即可得到当前 active branch name map。

MVP `RefOpFrame` 类型：

| Operation | 语义 |
|:----------|:-----|
| `Create` | 分配新 ref instance；frame ticket 即新 `RefId` |
| `Fork` | 从既有 ref/source head 分配新 ref instance；frame ticket 即新 `RefId` |
| `BindName` | 将 active branch name 绑定到已有 `RefId`，发布给 `OpenBranch` |
| `Archive` | 归档 ref instance，并释放其 active branch name 绑定 |

`RefId` 是 `Create` / `Fork` frame 的 `SizedPtr.Packed`。`BindName` / `Archive` frame 引用已有 `RefId`。

概念字段：

| 字段 | 含义 |
|:-----|:-----|
| `FormatVersion` | RefOp schema 版本 |
| `Operation` | `Create` / `Fork` / `BindName` / `Archive` |
| `BranchName` | canonical branch name；bind/archive 时使用 |
| `RefId` | bind/archive 引用的 ref id；create/fork 由 frame ticket 推导，不写入 payload |
| `SourceRefId` | fork 来源 ref，可空 |
| `SourceMoveSequenceNumber` | fork 来源 move number，可空 |
| `SourceHead` | fork 时观察到的来源 head，可空 |
| `StartHead` | create/fork 后 ref 的初始 head，可空表示 unborn |
| `UtcUnixTimeMilliseconds` | UTC 记录时间 |
| `ReasonKind` | opaque reason kind |

规则：

- `Create` / `Fork` 写入成功后，其 frame ticket 才成为 `RefId`。
- `Create` / `Fork` frame payload 不包含自身 `RefId`；这是从 RBF frame ticket 派生的外部身份。
- 创建时 branch name MUST 未绑定到 active ref；若同名 ref 已存在且未归档，创建失败。
- `Fork` 必须记录 source 信息；这些信息用于审计和后续 ref graph 分析，不改变 Event Parent graph。
- `BindName` 只能指向已完成 `Init` 的 ref object。`OpenBranch(name)` 只识别最后一个有效 `BindName` 仍处于 active 的绑定。
- `Archive` 释放 branch name，但不删除 RefOpLog 中的历史，也不改写 ref move chain。
- branch name map 是 RefOpLog replay 的派生结果，可缓存但不是唯一事实源。

### 4.1 创建与发布顺序

`CreateBranch` / `ForkBranch` 使用两阶段发布，避免 half-created branch 被 `OpenBranch` 看见：

```text
Append RefOpFrame(Create/Fork) -> RefId
Create refs/objects/<ref-id>/00000001.rbf
Append RefMoveFrame(Init) to ref object
DurableFlush ref object
Append RefOpFrame(BindName, branchName, refId)
DurableFlush ref-op-log.rbf
```

崩溃语义：

- `Create/Fork` durable、`Init` 未完成：留下未发布的 orphan ref allocation；`OpenBranch` 不可见，诊断器可报告。
- `Init` durable、`BindName` 未完成：留下未发布的 orphan ref object；`OpenBranch` 不可见，诊断器可报告或后续救援。
- `BindName` durable 后，branch name 才对公共 API 可见。

因此不需要为 RBF 增加 dry-run ticket 预测 API；RefStore 只依赖现有 `Append` 返回的 `SizedPtr` 和 replay 时的 frame ticket。

## 5. Ref Move Chain

`RefMoveFrame` 是 ref object segment 中的唯一 MVP frame 类型。每个 frame 表示一次 ref head 状态转移。

概念字段：

| 字段 | 含义 |
|:-----|:-----|
| `FormatVersion` | RefMove schema 版本 |
| `RefId` | 当前 ref instance identity，必须与路径匹配 |
| `MoveSequenceNumber` | ref-local 单调 move 序号 |
| `UtcUnixTimeMilliseconds` | UTC 记录时间 |
| `Operation` | `Init` / `Advance` / `Move` / `Close` |
| `ExpectedOldTarget` | API 调用方声明的旧 head，用于 CAS |
| `OldTarget` | 写入该 move 前观察到的旧 head |
| `NewTarget` | move 后的新 head；`null` 表示 unborn 或 closed |
| `ReasonKind` | opaque reason kind，供上层分类 |
| `Note` | 可选调试文本或小型 opaque note，MVP 可不实现 |

MVP 可以采用私有二进制 header + 可选短 note，或直接先用固定二进制 frame payload。具体 offset 和 CRC 留给 RefMoveFrame Spec；本文先固定语义和恢复规则。

### 5.1 Operation 语义

| Operation | `OldTarget` | `NewTarget` | 语义 |
|:----------|:------------|:------------|:-----|
| `Init` | `null` | `EventAddress?` | 初始化 ref move chain；`NewTarget=null` 表示 unborn ref |
| `Advance` | non-null or null | non-null | 正常推进到新 Event，通常 `new.Parent == old` |
| `Move` | any | `EventAddress?` | 显式 reset / rewind / retarget，可到 null |
| `Close` | any | `null` | 关闭 ref move chain，通常由 archive 操作触发 |

`Advance` 与 `Move` 的区别是语义和审计信息，不是存储能力差异。MVP reader 不需要通过 Parent 验证 `Advance` 的拓扑关系；writer SHOULD 在写入 `Advance` 前验证新 Event 的 Parent 与 expected old head 匹配。需要强约束时可在 API 层把不满足 fast-forward 的操作降级为显式 `Move`。

### 5.2 首帧规则

一个合法 ref object 的第一个有效业务 frame MUST 是 `Init`，且 `MoveSequenceNumber == 1`。只有 RBF header fence、空 ref segment、首个 frame 不是 `Init`、或首帧 `RefId` 与路径不匹配，均为 malformed ref object。

Unborn branch 使用 `Init(NewTarget=null)` 表达，不用空文件表达。

## 6. 当前 Head 与 Reflog

给定 `RefId` 后，replay 该 ref object 的 move segments 即可得到：

- 当前 head：最后一个有效 move 的 `NewTarget`。
- 当前状态：active / unborn / closed。
- reflog：该文件中所有有效 `RefMoveFrame`。

规则：

- `Close` 后 ref object 处于 closed 状态；默认 active branch name map 不再指向它。
- archived segment 文件仍可用于 reflog 查询和诊断。
- 物理压缩或移除 archived segment 是后续 maintenance 能力，不进入 MVP。
- 当前 head cache 可以存在，但必须是 replay 派生物，不是唯一事实源。

## 7. API 形态

候选 API：

```csharp
public interface IEventRefStore : IDisposable {
    AteliaResult<RefId> OpenBranch(string branchName);
    IReadOnlyList<string> ListBranches();

    AteliaResult<RefId> CreateBranch(string branchName, EventAddress? startPoint);

    AteliaResult<RefId> ForkBranch(
        string branchName,
        RefId sourceRefId,
        EventAddress sourceHead
    );

    EventAddress? GetHead(RefId refId);

    AteliaResult AdvanceRef(
        RefId refId,
        EventAddress? expectedOldHead,
        EventAddress newHead,
        uint reasonKind = 0
    );

    AteliaResult MoveRef(
        RefId refId,
        EventAddress? expectedOldHead,
        EventAddress? newHead,
        uint reasonKind = 0
    );

    AteliaResult ArchiveRef(
        RefId refId,
        EventAddress? expectedOldHead,
        uint reasonKind = 0
    );

    RefMoveSequence ReadReflog(RefId refId);
}
```

`CommitToRef` 属于上一层便利 API，可组合 raw append 与 `AdvanceRef`：

```text
CommitToRef(refName, expectedHead, payload):
    refId = OpenBranch(refName)
    newEvent = AppendEventFrame(parent = expectedHead, payload)
    result = AdvanceRef(refId, expectedHead, newEvent)
```

若 Event append 成功但 `AdvanceRef` 因 CAS 失败，API 应报告 ref 未推进，并可返回 orphan `EventAddress` 供上层救援或诊断。

## 8. CAS 与写入协议

所有 mutating ref API MUST 接收 `expectedOldHead`。写入前必须：

1. 通过 `RefId` 打开并 replay ref object move segments 到当前状态。
2. 比较当前 head 与 `expectedOldHead`。
3. 不一致则返回 CAS failure，不写 frame。
4. 若新 target 非 null，使用 EventFrame checked read 验证目标 Event 存在且完整。
5. 追加 `RefMoveFrame`。
6. 对 ref RBF 文件执行 `DurableFlush`。

Event 与 ref 的组合提交顺序：

```text
Append EventFrame
DurableFlush Event segment
Append RefMoveFrame
DurableFlush ref file
```

因此 Event durable、ref move 未 durable 时只产生 orphan Event，不会产生悬空 ref。

## 9. 打开与恢复

打开 EventJournal 时，refs 层先恢复并 replay `refs/ref-op-log.rbf`，得到 active branch name map 与 known `RefId` 集合；随后可按需打开具体 ref object：

1. 对 `ref-op-log.rbf` 执行 active-tail recovery。
2. replay `RefOpFrame`，重建 branch name 到 active `RefId` 的映射。
3. 验证每个 active `RefId` 的 object 路径可由 `RefId` 派生。
4. 按需对 ref object active segment 执行 tail recovery。
5. replay `RefMoveFrame`，遇到 malformed frame 或 target invalid 时停止该 ref object 的 replay，并报告 corruption。
6. 最后一个有效 move 决定当前 ref head。

故障边界：

- `ref-op-log.rbf` 损坏会影响 branch name map；必须恢复到最后一个完整 RefOpFrame。
- 单个 ref object 损坏只影响该 ref；其他 refs 不受影响。
- ref object active segment tail 撕裂可截断到最后一个完整 move。
- closed historical Event segment corruption 仍由 EventFrame 层报告；refs 层不得截断 data segment。
- ref target invalid 时，reader MUST 回退到该 ref 更早的有效 move，或将该 ref 标记为 corrupted；不能接受悬空 head。

## 10. Crash Matrix

| 崩溃点 | 恢复结果 |
|:-------|:---------|
| EventFrame append 未完整 | 没有可见 Event，ref 不变 |
| EventFrame durable，RefMoveFrame 未写 | orphan Event，ref 不变 |
| RefOpFrame 撕裂 | branch name map 恢复到前一个完整 RefOpFrame |
| RefMoveFrame 撕裂 | ref recovery 截到前一个完整 move |
| RefMoveFrame durable，flush 前崩溃 | 取决于 RBF durable 边界；reopen 只接受完整有效 move |
| RefMoveFrame durable 但 target invalid | 回退到更早有效 move，并报告 corruption |

## 11. 待后续细化的问题

这些问题不阻塞 `BranchName -> RefId -> RefSegmentFile` MVP，但需要在后续独立设计：

1. RefOpFrame 的精确 fixed binary schema、CRC 和 note 编码。
2. branch rename / alias 是否需要；MVP 只做 create/fork/archive。
3. archived segment 的物理归档路径、重名策略和冲突检测。
4. 是否允许 slash path 风格 branch name；若允许，name locator 的编码策略是什么。
5. ref checkpoint/cache：refs 数量很大时如何加速 list/open。
6. RefOpLog 是否需要分段；MVP 因操作频率低固定为单 RBF 文件。
7. 多 ref 原子事务是否需要；MVP 不提供。

## 12. 验收标准

- `CreateBranch` / `ForkBranch` 返回稳定 `RefId`，reopen 后 `OpenBranch(name)` 得到相同 `RefId`。
- 删除/归档后重建同名 branch 会得到不同 `RefId`。
- `AdvanceRef(RefId, ...)` 在 expected old head 不匹配时不写入 move。
- `MoveRef` / `ArchiveRef` 记录 reflog 或 RefOpLog，且不修改任何 EventFrame。
- ref object 首帧不是 `Init` 或 `RefId` 不匹配时被判为 malformed。
- ref object tail 撕裂后恢复到前一个完整 move。
- ref target invalid 时不接受悬空 head。
- 每个 ref object 的损坏不会阻止其他 ref object replay。
- `CommitToRef` 在 Event durable、ref CAS 失败时能报告 orphan EventAddress。
