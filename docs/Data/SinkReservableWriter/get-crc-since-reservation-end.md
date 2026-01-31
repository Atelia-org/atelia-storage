# SinkReservableWriter：从 reservation 末尾计算 CRC32C

## 背景与问题
`SinkReservableWriter` 支持 reservation（预留/回填）。当存在一个“很早创建但很晚才 Commit”的 reservation（典型：帧头 `HeadLen`），writer 的 flush 机制会刻意阻塞从该 reservation 起的后续数据下推。

RBF 帧构建场景需要在 `EndAppend` 时计算 Payload 的 CRC32C，但此时 `HeadLen` 仍处于 pending 状态，因此“在 Sink 层边写边算 CRC”会得到错误的结果（因为后续 payload 字节尚未被 push 到 sink）。

## 重构目标
为 `SinkReservableWriter` 增加一个仅在具体实现类型上可用的成员函数，用于计算“从某个 pending reservation 的末尾”到“当前已写入数据末尾（WrittenEnd）”之间的 CRC32C。

该能力用于支持 RBF 等上层协议在存在 long-lived reservation 时仍能正确得到 CRC。

## API 形状（草案）
```csharp
public uint GetCrcSinceReservationEnd(
    int reservationToken,
    uint initValue = RollingCrc.DefaultInitValue,
    uint finalXor = RollingCrc.DefaultFinalXor
);
```

- 所属类型：`Atelia.Data.SinkReservableWriter`
- 依赖：`Atelia.Data.Hashing.RollingCrc`
- 该函数为纯计算：MUST NOT 修改 writer 的可观测状态（除诊断日志外）。

## 语义定义
- **起点**：`reservationToken` 对应 reservation 的“末尾”（`Offset + Length`）。
- **终点**：当前 writer 的“最新已写入边界”，即最后一次 `Advance` 之后的 `WrittenLength`（不包含任何尚未 `Advance` 的借用缓冲区）。
- **覆盖内容**：从起点到终点的所有字节，按其在 writer 内的逻辑顺序串接。

CRC 计算方式：
- 使用 raw rolling 形式累积：`crcRaw = RollingCrc.CrcForward(crcRaw, span)`
- 最终返回 `crcRaw ^ finalXor`

## 约束与失败语义（强约束版本）
该方法只为当前 RBF 使用场景提供能力，采用“强约束、早失败”的设计，以避免 silent corruption。

调用前置条件：
- MUST：writer 未 `Dispose`。
- MUST：`_hasLastSpan == false`（即不存在未 `Advance` 的借用 span/memory）。若违反，抛 `InvalidOperationException`。
- MUST：`reservationToken` 有效且处于 **pending** 状态。若 token 无效/已 Commit，抛 `InvalidOperationException`。
- MUST：当前 **仅存在一个** pending reservation，且它就是 `reservationToken`。若 `PendingReservationCount != 1`，抛 `InvalidOperationException`。

行为边界：
- 允许起点 == 终点：此时返回空 payload 的 CRC（由 `initValue`/`finalXor` 决定）。
- 不承诺支持“区间内存在其他 pending reservation”的情况（直接失败）。

## 实现要点（建议）
- 通过 `ReservationTracker` 定位 `reservationToken` 对应的 `ReservationEntry`，获得：`Chunk`、`Offset`、`Length`。
- 从该 entry 所在 chunk 开始，按 active chunks 的顺序遍历到最后一个 chunk。
  - 第一个 chunk：从 `entry.Offset + entry.Length` 扫到 `chunk.DataEnd`。
  - 后续 chunk：从 `chunk.DataBegin` 扫到 `chunk.DataEnd`（原则上在上述强约束下通常是从 0 开始；但用 `DataBegin` 可规避少量已 push 的前缀）。
- 对每段 span 调用 `RollingCrc.CrcForward` 累积。
- 不得触发 flush；不得修改 `DataBegin/DataEnd/_writtenLength/_pushedLength/_reservations`。

## 在 RBF 的落地方式
- `RbfFrameBuilder` 在写入 Padding 之后、写入 Tail（PayloadCrc/Trailer/Fence）之前：
  - 用 `_headLenReservationToken` 调用 `GetCrcSinceReservationEnd(...)`，得到 `payloadCrc`。
  - 然后按现有流程写入 Tail。

## 测试要点（最小集）
建议增加 `Data.Tests` 的实现级测试（针对 `SinkReservableWriter`）：
- 仅 1 个 pending reservation + 写入 payload：返回值等于 `RollingCrc.CrcForward(payload)`。
- `_hasLastSpan == true`（GetSpan 后不 Advance）：调用该方法抛 `InvalidOperationException`。
- 存在 2 个 pending reservation：调用该方法抛 `InvalidOperationException`。
- token 无效（Reset 后旧 token）：调用该方法抛 `InvalidOperationException`。
