# RbfSegmentStore Layout Unification 设计方案

> **状态**：Refactor Design / 待实施
> **日期**：2026-07-23
> **相关基线**：[RbfSegmentStore 设计基线](rbf-segment-store-design.md)、[Event Ref Store 设计基线](event-ref-store-design.md)
> **目标代码**：`src/RbfSegmentStore`、`src/EventJournal`

## 1. 结论

把 `src/EventJournal/RefObjectStore.cs` 的 segment 生命周期能力合并到 `src/RbfSegmentStore` 是合理且可行的。

合理性：

- `RefObjectStore` 当前重复实现了 segment file 命名、创建、打开、轮转、连续编号扫描、active-tail recovery 等基础能力；这些正是 `RbfSegmentStore` 的职责边界。
- 两者的差异主要是路径 layout：Event data 适合 bucketed layout，单个 ref object 的 reflog 更适合 flat lightweight layout。
- `RbfSegmentStore` 本来不解释 frame tag / payload，把 RefMove 的 tag 校验、`RefId` 校验和 move replay 保留在 EventJournal 层，职责边界仍然清楚。
- `EventJournal` 已经引用 `RbfSegmentStore` project，依赖方向天然成立，不需要让 `RbfSegmentStore` 反向依赖 EventJournal。

可行性：

- `RbfSegmentStore` 的 lease API 已能覆盖 `RefObjectStore.AppendMove` 与 `ReadAllMoves` 的实际需求。
- `RefObjectStoreOptions` 与 `RbfSegmentStoreOptions` 字段高度重叠；只需要为 ref object 给出不同默认 segment size 和 flat layout。
- 不引入 manifest 也可行：layout identity 由 store root 下的固定目录名决定；打开时通过目录扫描验证，而不是读取额外状态文件。

本轮重构应删除独立的 `RefObjectStore` segment 管理实现，但可以保留一个很薄的 EventJournal 内部 helper，例如 `RefMoveStore`，只负责 RefMove codec / tag / `RefId` 业务校验，不再持有路径、轮转、恢复算法。

## 2. 新 Layout 规则

`RbfSegmentStore` 支持两种 segment file layout。layout 不写 manifest，而由 store root 下的目录名区分。

### 2.1 Bucketed Layout

用于高吞吐、长期增长的 Event data store。

```text
<store>/
  buckets/
    000000/
      00000001.rbf
      ...
      000003ff.rbf
    000001/
      00000400.rbf
      ...
      000007ff.rbf
```

规则：

- layout root 固定为 `<store>/buckets/`。
- bucket 目录名仍为 `segmentNumber >> 10` 的 6 位小写 hex。
- segment 文件名仍为完整 `segmentNumber` 的 8 位小写 hex，后缀 `.rbf`。
- `SegmentNumber = 0` 保留，不允许存在 `00000000.rbf`。
- 打开时拒绝错误 bucket、错误文件名、bucket 与文件名不一致、编号缺口、`buckets/` 下的直接文件。

这是现有 `RbfSegmentStore` bucketed 语义的重命名：旧的 `<store>/segments/<bucket>/<file>.rbf` 不再作为新格式接受。

### 2.2 Flat Segments Layout

用于轻量级、小规模、每个对象独立增长的 reflog / ref object store。

```text
<store>/
  segments/
    00000001.rbf
    00000002.rbf
```

规则：

- layout root 固定为 `<store>/segments/`。
- segment 文件直接位于 `segments/` 下，中间没有 bucket 目录。
- 文件名为完整 `segmentNumber` 的 8 位小写 hex，后缀 `.rbf`。
- `SegmentNumber = 0` 保留。
- 打开时拒绝 `segments/` 下的子目录、错误文件名、编号缺口。

用于 ref object 时，目录结构变为：

```text
event-journal/
  refs/
    objects/
      0123456789abcdef/
        segments/
          00000001.rbf
          00000002.rbf
```

也就是说，`refs/objects/<ref-id>/` 是一个 `RbfSegmentStore` root，内部选择 flat `segments/` layout。

## 3. Layout 发现与创建

不引入 manifest 后，目录 inventory 就是唯一 layout truth。

建议 API 形状：

```csharp
public enum RbfSegmentStoreLayout {
    Bucketed,
    Flat
}

public sealed class RbfSegmentStoreOptions {
    public RbfSegmentStoreLayout NewStoreLayout { get; init; } = RbfSegmentStoreLayout.Bucketed;
    public long SegmentSizeThresholdBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public int HistoricalReaderPoolCapacity { get; init; } = 32;
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;
}
```

`NewStoreLayout` 只用于创建新 store；打开已有 store 时从目录名发现 layout。`RbfSegmentStore` 实例应暴露实际 layout：

```csharp
public RbfSegmentStoreLayout Layout { get; }
```

创建 / 打开规则：

- `CreateNew` 要求 `<store>` 不存在，按 `options.NewStoreLayout` 创建 `buckets/` 或 `segments/`，并创建 `SegmentNumber = 1`。
- `OpenExisting` 要求 `<store>/buckets/` 或 `<store>/segments/` 恰好存在一个，并且至少有一个合法 segment。
- `OpenOrCreate` 若 layout root 不存在，按 `options.NewStoreLayout` 创建；若发现已有 layout root，则按已有目录打开。
- 若 `buckets/` 与 `segments/` 同时存在，必须抛出 `InvalidDataException`，不得猜测。
- 若只存在 `<store>/segments/<bucket>/<file>.rbf` 这种旧 bucketed 形态，按新 flat layout 校验会失败。这是可接受的 breaking change。

## 4. EventJournal 目录结构调整

重构后的 EventJournal MVP 目录建议为：

```text
event-journal/
  events/
    buckets/
      000000/
        00000001.rbf

  refs/
    ref-op-log.rbf
    objects/
      0123456789abcdef/
        segments/
          00000001.rbf
```

含义：

- `events/` 是 `RbfSegmentStore` root，使用 `Bucketed` layout。
- `refs/objects/<ref-id>/` 是 `RbfSegmentStore` root，使用 `Flat` layout。
- `refs/ref-op-log.rbf` 仍是单个低频 RBF 文件；它不是 segment store，继续由 EventJournal 直接打开。

该结构让目录名承担 layout 识别职责：

- `buckets` 表示按高位分桶。
- `segments` 表示无分桶、直接放 segment 文件。

## 5. EventJournal Options 调整

当前 `RefObjectStoreOptions` 与 `RbfSegmentStoreOptions` 重叠，重构后建议删除 `RefObjectStoreOptions`，改成明确区分三类选项：

```csharp
public sealed class EventJournalOptions {
    public RbfSegmentStoreOptions EventSegmentStoreOptions { get; init; } = new() {
        NewStoreLayout = RbfSegmentStoreLayout.Bucketed,
        SegmentSizeThresholdBytes = 64L * 1024 * 1024 * 1024
    };

    public RbfSegmentStoreOptions RefSegmentStoreOptions { get; init; } = new() {
        NewStoreLayout = RbfSegmentStoreLayout.Flat,
        SegmentSizeThresholdBytes = 64L * 1024 * 1024
    };

    public RefOpLogOptions RefOpLogOptions { get; init; } = new();
}

public sealed class RefOpLogOptions {
    public RbfCacheMode CacheMode { get; init; } = RbfCacheMode.Slots16;
    public bool RecoverActiveTailOnOpen { get; init; } = true;
}
```

如果想减少第一轮改动量，也可暂时保留 `SegmentStoreOptions` 名称给 event data，但不建议继续保留 `RefObjectStoreOptions`：它的名字会暗示旧的独立 store 仍是事实源。

## 6. RefObjectStore 的替代方式

删除 `RefObjectStore.cs` 中独立维护 segment 文件的实现。替代结构建议为：

- `RbfSegmentStore` 负责路径、create/open/open-or-create、active segment 发现、轮转、reader pool、tail recovery。
- EventJournal 内部 thin helper 负责 RefMove 业务语义。

thin helper 可以叫 `RefMoveStore` 或直接做成 `EventJournal.Refs.cs` 的私有方法。若保留类型，职责应限制为：

```csharp
internal sealed class RefMoveStore : IDisposable {
    public static RefMoveStore CreateNew(string refObjectRoot, RefId refId, RbfSegmentStoreOptions options);
    public static RefMoveStore OpenExisting(string refObjectRoot, RefId refId, RbfSegmentStoreOptions options);

    public uint ActiveSegmentNumber { get; }
    public AteliaResult<FrameAddress> AppendMove(in RefMoveFrame move);
    public AteliaResult<IReadOnlyList<RefMoveFrame>> ReadAllMoves();
}
```

实现要点：

- `refObjectRoot` 是 `refs/objects/<ref-id-hex>`。
- 创建 / 打开底层使用 `RbfSegmentStore.CreateNew/OpenExisting(refObjectRoot, options)`。
- `AppendMove` 借 `OpenActiveWriter()`，调用 `lease.File.Append(RefMoveFrameTag, payload)`，`DurableFlush()` 后返回 `FrameAddress(ticket, lease.SegmentNumber)`。
- `ReadAllMoves` 从 `1..ActiveSegmentNumber` 逐段借 `OpenReader(segmentNumber)` 并扫描。
- tag 必须等于 `RefMoveFrameTag`。
- `TailMetaLength` 必须为 `0`。
- frame 内 `RefId` 必须等于当前 ref object 的 `RefId`。
- 首帧仍必须是 `Init` 且 `MoveSequenceNumber == 1`。
- 后续 move sequence 连续性继续由 `LoadRefState/TryApplyMove` 校验，或前移到 helper 中统一校验。

这样保留业务封装，但不再重复基础 segment store。

## 7. Archived Segment 策略

现有 `event-ref-store-design.md` 曾提到 `.rbf.archived`。在本次合并中建议先收紧语义：

- ref archive 的 canonical truth 仍是 `ref-op-log.rbf` 中的 `Archive` frame 与 ref object 内的 `Close` move。
- 首轮不要求 `RbfSegmentStore` 接受 `.rbf.archived` 文件。
- 若未来确实需要物理归档 segment，应作为 `RbfSegmentStore` 的通用 maintenance 能力重新设计，例如移动到独立 archive area，而不是让 EventJournal 私自在 active layout root 中放置 store 不理解的文件。

理由是 `RbfSegmentStore` 作为通用基础层，应拥有它所扫描目录中的全部文件语义；否则目录 inventory 又会出现双真源。

## 8. 施工步骤

建议按下面顺序实施：

1. 在 `RbfSegmentStore` 中引入 `RbfSegmentStoreLayout` 和 layout path abstraction。
2. 将当前 `RbfSegmentPath` 拆成可按 layout 计算 root/path/discovery 的内部 helper。
3. 把现有 bucketed root 从 `segments/` 改名为 `buckets/`。
4. 为 flat `segments/` layout 补路径、发现、创建和轮转测试。
5. 给 `RbfSegmentStore` 暴露 `Layout` 属性，并调整 README / design baseline。
6. 在 EventJournal 中新增 ref object 使用的 flat `RbfSegmentStore` helper。
7. 删除 `RefObjectStoreOptions`，把 EventJournal options 改成 event segment / ref segment / ref-op-log 三组配置。
8. 迁移 `CreateBranch`、`ForkBranch`、`AppendRefMove`、`ReadReflog`、`LoadRefState` 到新 helper。
9. 删除 `RefObjectStore.cs`，更新测试名和路径断言。
10. 更新 `event-ref-store-design.md` 与 `event-journal-requirements-and-design.md` 中旧目录示例。

## 9. 测试验收

`RbfSegmentStore.Tests` 至少覆盖：

- bucketed layout 使用 `<store>/buckets/<bucket>/<file>.rbf`。
- flat layout 使用 `<store>/segments/<file>.rbf`。
- `CreateNew` 按 `NewStoreLayout` 创建正确目录。
- `OpenExisting` 能从唯一 layout root 发现 layout。
- `OpenExisting` 拒绝 `buckets/` 与 `segments/` 同时存在。
- flat layout 拒绝子目录、错误文件名、segment `0` 和编号缺口。
- bucketed layout 继续拒绝错误 bucket、错误文件名、bucket mismatch、segment `0` 和编号缺口。
- 两种 layout 下 `OpenActiveWriter` 都能轮转，`OpenReader` 都能读取 active/historical segment。
- 两种 layout 下 active-tail recovery 行为一致。

`EventJournal.Tests` 至少覆盖：

- 创建 branch 后 ref object 路径为 `refs/objects/<ref-id>/segments/00000001.rbf`。
- ref move 轮转后路径为 `segments/00000001.rbf`、`segments/00000002.rbf`。
- `ReadReflog` 能跨 flat segments replay。
- `AdvanceRef` / `MoveRef` 仍能追加 RefMove 并更新 cache。
- event data 路径改为 `events/buckets/000000/00000001.rbf`。
- 打开已有新布局 journal 能重建 event sequence 与 branch map。

## 10. 迁移与兼容

本项目仍处于未发布阶段，建议把这次重构视为 breaking storage layout change，不保留运行时兼容层。

已有本地数据如需保留，可用一次性手工迁移：

- event data：`events/segments/<bucket>/...` 移动为 `events/buckets/<bucket>/...`。
- ref object：`refs/objects/<ref-id>/<segment>.rbf` 移动为 `refs/objects/<ref-id>/segments/<segment>.rbf`。

不建议在产品代码中自动识别旧 layout。否则 `segments/` 会同时表示“旧 bucketed root”和“新 flat root”，刚好违背“用目录名区分 layout”的设计目标。

## 11. 残余风险

- `NewStoreLayout` 只影响创建，不影响打开；这一点必须在命名和文档中写清楚，避免调用方误以为它是打开时强制校验。
- ref-op-log 仍是单文件 RBF，选项需要从旧 `RefObjectStoreOptions` 中拆出来，否则删除旧 options 时会漏掉 cache/recovery 配置。
- 如果未来真的需要物理 archived segment，必须让 `RbfSegmentStore` 统一拥有目录扫描和 archive 文件语义，不能由 EventJournal 私下放置未知后缀。
- 旧测试中引用 internal `RbfSegmentPath` 的断言较多，改 layout abstraction 时要同步更新测试 helper，避免测试继续固化旧命名。
