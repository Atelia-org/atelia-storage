# Atelia.FrameStore

FrameStore 已实现 [S2](../../docs/FrameStore-VersionStore/02-framestore-core.md) 的公开持久化闭环：真实目录 `Create/Open/OpenReadOnly`、三种追加、交错 Builder、随机读取、同步耐久确认及归档。S2/S3 已取得[独立源码验收](../../docs/FrameStore-VersionStore/02-framestore-final-acceptance.md)。项目参加 solution 构建和测试，保持 source-only、暂不打包；VersionStore 尚未创建。

```csharp
using Atelia.FrameStore;
using Store = Atelia.FrameStore.FrameStore;

FrameAddress address;
using (var store = Store.Create(rootPath)) {
    address = store.Append(1, "hello"u8).Unwrap();
    store.ConfirmDurable();
}
using (var store = Store.OpenReadOnly(rootPath)) {
    using var frame = store.ReadFrame(address).Unwrap();
    // frame.PayloadAndMeta 是已完整校验且独立拥有的 bytes。
}
```

`Create` 只接受缺失末级根或合格空根（可带 config/锁 bootstrap）；父目录须存在。`Open` 不创建缺失 store；`OpenReadOnly` 不恢复、移档或清理私有残留。可写 Create/Open 的可选 `rotationThresholdBytes` 默认 64GiB，超过阈值的完成帧仍正常返回，后续合法写准入、ConfirmDurable 或 Open 才执行归档维护。

`BeginAppend()` 提供未知尺寸 Builder；`BeginAppend(payloadLength, tailMetaLength, out address)` 在声明最终 stored 长度后提供提前地址。多个 Builder 可嵌套、交错并乱序完成，每个独占一个文件。其 `PayloadAndMeta` writer 的借用须经 Advance 归还，成功 EndAppend 自动归还租借；提前地址不证明完成、来源或耐久，取消后的坐标可能复用。普通 buffer Append 不消耗 Builder 配额。可选 `framestore.config.json` 只接受 `MaxOutstandingBuilders`，默认 32。

writer 的 `Length` 保留 RBF 已写/预留的逻辑累计长度，包含内部 HeadLen；统计用户操作的逻辑增量应比较同一 Builder 操作前后的差值。该属性不表示物理输出量；当前实现到 EndAppend 提交 HeadLen 前会保留未完成内容。

资源成本按实际 active 数计算：健康取消 Builder 后仍保留可写文件句柄，降低 M 后重开也会接管全部历史 active。大 sizeHint 可以租到远大于声明 payload 的 buffer；End/取消及 FrameRead.Dispose 归还池，不保证进程工作集立即下降。独立循环图、M=1 回填示例和 Windows/Linux 有界资源基线见 [R2 消费与资源记录](../../docs/FrameStore-VersionStore/02-framestore-consumer-resource-acceptance.md)。

每个 owner 由调用方串行使用；writer 排斥其他全部 owner，多个 reader 可共享。持锁期间不得从外部改写、替换、删除或移动受管项。Windows/Linux 实现拒绝受管 symlink/reparse/FIFO 等特殊项，移动严格同文件系统且不覆盖；Windows 需要 volume GUID，Linux 需要 statx 的类型/大小/mount identity 及 renameat2，不支持的环境拒绝打开。这是本地文件系统机制资格，不承诺任意网络文件系统或敌对路径环境。

`StoreId` 是持久身份的 16 个 opaque bytes，`IsReadOnly` 表示访问模式。`RecoveryReports` 按 FileId 保留本次可写 Open 的物理恢复报告；Create/RO 为空。格式/布局不合格拒绝，真实 I/O 错误保持原异常；ReadFrame 的非法 default 地址和底座读取拒绝使用 Result，实际缺失路径仍抛文件缺失异常。owner 输出/清理故障后停止使用，Dispose 按单次释放规则先关数据、最后关锁，不隐式确认。

成功的 `FrameRead` 独立拥有 buffer，需调用方 Dispose，可在 store 关闭后使用；已取得的 span 不得越过 FrameRead.Dispose。地址必须同时知道所属 store；不公开裸 RBF writer 或路径导入。

`FrameAddress.TryWrite/TryRead` 保持固定 12B，适用于提前尺寸规划和地址预留回填。普通已知地址另可使用 `MeasureVarInt()`、`WriteVarInt(BareValueWriter)` 和 `ReadVarInt(ref BareValueReader)`：编码为 `VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())`，合法地址占 3～15B，不保证每个值都短于 12B。记录 schema 必须明确选择格式，不能自动探测或失败后切换。变长 writer 输出最短表示，reader 接受 Binary 规定的有界冗余表示；复合地址读取失败不推进 reader，实际消费不能用 Measure 反推。default 不能度量或输出，writer 下游异常不保证回滚。

FrameStore 为变长地址 codec 引用 `Atelia.Binary`，间接引入其精确 K4os.Compression.LZ4 `[1.3.8]` 依赖；地址 codec 不调用压缩。格式/测试及真实 RootMap 地址组合证据见[变长编码实施记录](../../docs/FrameStore-VersionStore/02-framestore-varint-address-implementation.md)。

`Inventory(visitor)` 同步返回实际正式集合的用户帧元信息，检查 framing/TrailerCRC；`Audit(visitor)` 额外完整检查每帧 CRC，逐文件报告 24B header 的独立副本和用户帧数。两者含用户 tag=0 与墓碑，只跳过经过检查的首帧 header；成功 Result 的 long 是用户帧总数。文件顺序不定义业务历史，实际集合审计不证明业务引用闭包或所有历史文件仍存在。

扫描要求零活跃 Builder。回调中可以 ReadFrame，不能 Append、BeginAppend、ConfirmDurable 或递归扫描；这些拒绝不会停用 owner。visitor 异常直接传播，CancellationToken 取消抛 OperationCanceledException；已交付的前缀不等于扫描成功。回调可以 Dispose owner，但整次扫描随之拒绝成功返回。FrameInfo 不持有 reader，FrameFileAudit 的 header 副本可跨 owner 生命周期保留。

后续范围与自动迭代进度见[FrameStore 收尾计划](../../docs/FrameStore-VersionStore/02-framestore-completion-plan.md)。

实现与测试证据见[同步物理检查](../../docs/FrameStore-VersionStore/02-framestore-inspection-implementation.md)、[公开持久化闭环](../../docs/FrameStore-VersionStore/02-framestore-persistence-implementation.md)及[首个源码切片](../../docs/FrameStore-VersionStore/02-framestore-core-implementation.md)。公开返回点的真实强杀、跨进程锁与冷重开见[过程取证 R1](../../docs/FrameStore-VersionStore/02-framestore-process-acceptance.md)；内部具名窗口及 S2/S3 逐项证据已在[最终源码验收 R3](../../docs/FrameStore-VersionStore/02-framestore-final-acceptance.md)闭合。源码、平台测试与包消费资格分别判断。
