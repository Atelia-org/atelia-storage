# EventJournal 使用指南

`Atelia.EventJournal` 是 `RbfSegmentStore` 之上的 EventFrame parent-chain 层。当前 MVP 只实现不可变单帧 Event、TailMeta v1 header codec、raw append/read 和 parent chain traversal；branch/ref 层留给后续 `event-ref-store-design.md`。

## 文件布局

EventJournal 根目录为后续 refs 预留空间，事件数据位于 `events/`：

```text
<journal>/
  events/
    buckets/
      000000/
        00000001.rbf
```

## 基本使用

```csharp
using Atelia.EventJournal;

using var journal = EventJournal.CreateNew(path);

EventAddress root = journal.AppendEventFrame(
    parent: null,
    payload: "root"u8,
    opaqueEventKind: 1,
    hint: new AddressHint(0x1000)
).Unwrap();

EventAddress child = journal.AppendEventFrame(root, "child"u8).Unwrap();

using EventFrame frame = journal.ReadEvent(child).Unwrap();
ReadOnlySpan<byte> payload = frame.Payload;
EventFrameHeader header = frame.Header;
```

`ReadEventHeaderPreview` 只读取 RBF TailMeta，适合 parent walk 和预筛选。`ReadEventHeaderChecked` / `ReadEvent` 会完整读取 RBF frame 并校验 payload CRC，适合接受外部地址、推进 refs 前验证和返回 payload。

## Traversal

`ReadAncestorChain(head)` 返回 `head -> parent -> ... -> root`。`ReadChronologicalChain(head)` 返回 `root -> ... -> head`，会使用临时 `List<EventAddress>`，空间复杂度为 `O(n)`。

## Refs

Refs 使用两层结构：`refs/ref-op-log.rbf` 保存 branch name 到稳定 `RefId` 的绑定历史，`refs/objects/<ref-id>/segments/00000001.rbf` 等 flat segment 文件保存单个 ref 的 move chain/reflog。

```csharp
using var journal = EventJournal.OpenOrCreate(path);

RefId main = journal.CreateBranch("main", startPoint: null).Unwrap();

CommitToRefOutcome first = journal.CommitToRef(
  branchName: "main",
  expectedHead: null,
  payload: "first"u8
).Unwrap();

CommitToRefOutcome second = journal.CommitToRef(
  branchName: "main",
  expectedHead: first.EventAddress,
  payload: "second"u8
).Unwrap();

EventAddress? head = journal.GetHead(main);
IReadOnlyList<RefMoveFrame> reflog = journal.ReadReflog(main).Unwrap();
```

`AdvanceRef` 是 fast-forward 风格推进：`newHead.Parent` 必须等于 `expectedOldHead`。reset/rewind/retarget 使用 `MoveRef`，删除 active branch name 使用 `ArchiveRef`。
