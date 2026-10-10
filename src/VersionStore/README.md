# Atelia.VersionStore

本项目是 VersionStore 的首个 source-only 实施片，参加 solution 构建与测试，暂不打包。设计入口为 [S4](../../docs/FrameStore-VersionStore/04-versionstore-publication.md)；当前进度、验证及后续分片见[实施计划](../../docs/FrameStore-VersionStore/12-versionstore-implementation-plan.md)。

VersionStore 借入一个 FrameStore，发布不透明 root-payload bytes。每个 ref 是独立 RBF3 文件中的完整快照序列；应用选择内容 codec 并保证 data 依赖闭包。普通 ref 不承担跨 ref 事务，CU 仍是独立候选。

当前源码包含：

- 持久上下文绑定的 `RefId`、完整位置 `RefRevision`、自有 `RefBytesSnapshot` 与 `PublicationOutcome`。
- 内部格式门、ref header/ForkOrigin、meta8 和规范路径 codec。
- 普通 ref 必要 header/head 校验、opaque bytes 自有读取、data-first 的 `PublishRef` 和锁最后的清理内核。
- 独立可选 `RootMapCodec.Encode/Decode`：有效 Unicode key、Ordinal、变长 FrameAddress、1MiB 上限及完整 codeword 解码。它不在发布内核中自动运行。

**尚无生产目录 backend 或公开 Create/Open/OpenReadOnly 工厂。** `VersionStore` 只有内部资格移交构造，当前读写内核通过内部测试 backend 接入真实 RBF 文件。没有公开裸句柄导入或占位工厂；不能把本轮局部测试当成已可创建、打开真实 VersionStore 的公开持久化闭环。下一片实现根/锁/门/唯一 pending、正式集合与新 ref 文件发布，才接通真实工厂。

`RootMapCodec` 已可独立消费。应用从 FrameStore 正常追加取得地址，用 `Encode(roots)` 生成自有 byte[]；取得 root bytes 后，显式 `Decode(payload)` 得到自有 Ordinal 冻结字典。空 RootMap 编成单字节 `00`，而 VersionStore 的空 payload 不合法。JSON 或其他编码的应用无需使用 RootMapCodec。

写入内核按实际 slice 长度捕获私有 bytes，在任何 data barrier/发布输出前完成确定检查；先 `data.ConfirmDurable()`，再正式追加和 flush。`out PublicationOutcome` 区分本次未尝试、结果未知和已确认，异常不得抹掉已取得证据。VS 停用会受控清理自身文件与锁，不 Dispose 借入 data；自有结果可跨 owner Dispose 保留。当前值读取不解码内容、不预读初始/源历史、不回退损坏 head。

项目仅引用 main 的 FrameStore/Binary/Rbf/Data/Primitives，不引用冻结 RBF1 库或业务包。目录准入、真实冷重开、历史/fork、tag、独立完整校验、进程终止、两平台与包资格按实施计划分别取得。
