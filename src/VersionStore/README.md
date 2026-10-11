# Atelia.VersionStore

本项目是 source-only 的 VersionStore，参加 solution 构建与测试，暂不打包。设计入口为 [S4](../../docs/FrameStore-VersionStore/04-versionstore-publication.md)；当前进度、验证及后续分片见[实施计划](../../docs/FrameStore-VersionStore/12-versionstore-implementation-plan.md)。

VersionStore 借入一个 FrameStore，发布不透明 root-payload bytes。每个 ref 是独立 RBF3 文件中的完整快照序列；应用选择内容 codec 并保证 data 依赖闭包。普通 ref 不承担跨 ref 事务，CU 仍是独立候选。

当前源码包含：

- 持久上下文绑定的 `RefId`、完整位置 `RefRevision`、自有 `RefBytesSnapshot` 与 `PublicationOutcome`。
- 内部格式门、ref header/ForkOrigin、meta8 和规范路径 codec。
- 真实目录与公开 `Create/Open/OpenReadOnly`，独立 owner 锁、绑定 data 的格式门和唯一 pending 工作区。
- 自动/预留区指定 `CreateRef`、`ListRefs`、必要 header/head 校验、opaque bytes 自有读取与 data-first 的 `PublishRef`。
- `ReadRefHistory`：显式 `HistoryBudget`、固定完成尾上界、逐项 visitor、自有快照与 exact ForkOrigin 前缀接续；预算、早停和访问范围分别裁决。
- 私有初始化后一次不覆盖文件移动发布；异常停用、资源单次清理和锁最后释放。
- 独立可选 `RootMapCodec.Encode/Decode`：有效 Unicode key、Ordinal、变长 FrameAddress、1MiB 上限及完整 codeword 解码。它不在发布内核中自动运行。

`Create(rootPath, data, reservedRefIdUpperBound: 0)` 建立空库，默认没有预留低号；自动创建从 1 开始。指定创建只允许 1..预留上界的空闲槽，自动创建使用正式集合水位加一。`Open(rootPath, data)` 独占打开并取消合格 pending；`OpenReadOnly(rootPath, data)` 共享只读打开，保留 pending，不恢复 ref。各工厂先检查实际借入 data 的可用性与模式，随后核对持久绑定。调用方保持 data 存活并串行使用两层；VS Dispose 不关闭 data。

核心公开闭环为 `Create → CreateRef → PublishRef → ReadRef/ReadRefHistory → Dispose → Open/RO → ReadRef/ReadRefHistory`。当前还没有 `ForkRef` 创建、`ListForks`、tag 或完整校验入口；没有占位方法。继承历史测试使用受控来源文件，不代表公开 fork 发布协议已接通。Windows 源码测试与 Linux、进程终止、包消费资格分别记录，见实施计划。

历史调用示例：

```csharp
var result = store.ReadRefHistory(refId, new HistoryBudget(100, 1000), snapshot => {
    // snapshot 与 Payload 在调用结束或 owner Dispose 后仍可保存使用。
    return true; // false：接收当前项后立即正常停止。
});
```

`Complete` 只证明所访问的继承发布前缀，`VisitorStopped` 不检查更早节点；额度不足返回 `VersionStore.HistoryBudgetExceeded`，不伪装完整结果。工作步计进入 ref、每次 MoveNext 和完整帧读取，包含边界 helper 的重复完整读；它不是物理 I/O、内存或时间上限。callback 可以读取当前值，但同 owner 的 mutation 和递归历史被拒绝；需要 fork/rewind 时在历史返回后执行。callback Dispose/取消/异常或必要关闭失败均不能作为正常结束。

`RootMapCodec` 已可独立消费。应用从 FrameStore 正常追加取得地址，用 `Encode(roots)` 生成自有 byte[]；取得 root bytes 后，显式 `Decode(payload)` 得到自有 Ordinal 冻结字典。空 RootMap 编成单字节 `00`，而 VersionStore 的空 payload 不合法。JSON 或其他编码的应用无需使用 RootMapCodec。

写入内核按实际 slice 长度捕获私有 bytes，在任何 data barrier/发布输出前完成确定检查；先 `data.ConfirmDurable()`，再正式追加和 flush。`out PublicationOutcome` 区分本次未尝试、结果未知和已确认，异常不得抹掉已取得证据。VS 停用会受控清理自身文件与锁，不 Dispose 借入 data；自有结果可跨 owner Dispose 保留。当前值读取不解码内容、不预读初始/源历史、不回退损坏 head。

项目仅引用 main 的 FrameStore/Binary/Rbf/Data/Primitives，不引用冻结 RBF1 库或业务包。ForkRef/ListForks、tag、独立完整校验、进程终止、两平台与包资格按实施计划分别取得。
