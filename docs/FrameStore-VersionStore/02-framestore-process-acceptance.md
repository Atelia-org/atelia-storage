# FrameStore 跨进程与中断取证（R1）

日期：2026-10-10。实施基线：`210bf40`。本轮消费 [S2](02-framestore-core.md) 和[收尾计划 R1](02-framestore-completion-plan.md)，补充实际公开 FrameStore 的跨进程证据。探针入口为 [FrameStoreProcessProbe](../../experiments/FrameStoreProcessProbe/README.md)。源码能力、系统验收和包资格分别判断，S2 尚未整体 Accepted。

状态：**R1 公开过程取证完成；三项内部窗口仍待 R3 / 必要时 R4 补证。** 本轮增加 source-only 探针和公开 writer.Length 的语义说明，没有修改生产行为、solution 或包清单。

## 实际执行与独立验收

两个 `gpt-6.1-sol / xhigh` 工作包分别实现探针和独立反例审查；主线程复核实际源码、串行 build/test、执行两平台探针并读取 JSON 结果。探针只直接引用公开 FrameStore，10 个隔离 fixture 覆盖 empty / active / 全 archive 的 owner 矩阵，以及 7 个强杀阶段。

| 环境 / 验证 | 实际结果 |
| --- | --- |
| Windows 11 build 22000 / x64 / E: ReFS / SDK 10.0.201 / .NET 10.0.5 | 最终探针 Release build 0 warnings / 0 errors；317/317 检查、80 个子进程全部退出，其中 13 个按握手后强杀；22.15 秒 |
| WSL2 Ubuntu / x64 / `/dev/sde` ext4 / 同 SDK/runtime | 隔离源码构建成功；317/317 检查、80 个子进程全部退出，其中 13 个强杀；16.30 秒 |
| 同 Linux，`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` | 317/317 检查、80 个子进程全部退出，其中 13 个强杀；19.99 秒。显式 native flock 仍独立维持 owner 模式矩阵 |
| Windows solution Release build / matching `--no-build` | 构建成功，2163 passed / 3 Linux-only skipped / 0 failed；其中 FrameStore 377 passed / 3 skipped |
| 新旧依赖边界 | 旧栈七项目精确 RBF1 package/source 检查通过；FrameStore 实际 assets 的 Rbf/Data/Primitives 均为 main project |

Windows solution 和 Linux 首次从源码构建各有 41 项既存 Data/Rbf XML warnings；不是新探针警告。Linux kernel 为 `6.18.33.2-microsoft-standard-WSL2`，隔离目录 `/tmp/atelia-framestore-process-w3NZPP`。本轮未重新运行整个 Linux solution；表中的三组过程结果分别独立记录，不把 951 次断言合并成 951 个不同向量，也不把耗时解释为性能基准。

实际冷重开均通过 Inventory/Audit 和按保存地址完整读取；RO 前后目录集合、文件长度和 SHA-256 不变。未确认的完整 Append 输出在三组运行中均仍存在且内容精确一致，这是实际观察，不升级为未确认输出必保留的合同。已确认输出、StoreId 及归档前后的原 12B 地址保留。

三种 Builder 场景中，每文件已完成前缀为 116B；写入 2MiB 未完成逻辑数据后物理长度仍为 116B。writer 初始 Length=4，写入后为 2097156，活跃 ConfirmDurable 前后逻辑长度不变；三个 active 的完成前缀都在该次确认后强杀、冷重开时保留。每份恢复报告均为 `None`，original/final 长度相等，未完成提前地址未读到普通用户帧。这里只证明这些公开停点的物理事实。

初次运行修正了探针的两处消费假设：Windows native error 32 位于 IOException 的内层 Win32Exception；writer.Length 含内部 HeadLen，应比较同一 Builder 操作前后的逻辑差值。随后完整重跑通过。FramePayloadWriter 的 XML 和 public README 已补说明；没有为探针改变已有 API 行为。

可重跑命令见探针 README。[已提交机器可读摘要](../../experiments/FrameStoreProcessProbe/evidence/2026-10-10-summary.json)包含三次最终结果的 hash、环境、检查/子进程数量、逐场景恢复报告及探针源码 hash；`baseline=210bf40` 不冒充包含本轮探针的提交。探针、本文和摘要由同次提交绑定。完整现场留在忽略的 `artifacts/framestore-process-windows-accepted/`、`artifacts/framestore-process-linux/`；TRX 在 `artifacts/framestore-process-windows-tests/`，Linux 输入 tar 与 SHA-256 清单在 `artifacts/framestore-process-source.*` 及 `framestore-process-source-sha256.json`。主线程核对最终 114 项源码/构建输入与 Linux 快照相同（不含后续文档编辑）。

本轮命令为 `dotnet build Atelia.Storage.slnx -c Release`、`dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/framestore-process-windows-tests`、`eng/Test-Rbf1ReferenceAssets.ps1`，随后串行执行探针 README 中的 Release build / `--no-build`，传入新输出目录及 `--revision 210bf40-plus-R1-probe`。未打包、发布或推送。

## 证据边界

探针通过父子进程消息确认公开操作已到达的阶段，再终止自己创建的进程，并用新进程打开同一目录。消息中的地址和载荷摘要用于实验比对，不是业务发布记录。只消费 ProcessCrashOnly：进程终止后 OS 和文件系统继续运行，不证明断电耐久。

当前 RBF Builder 在 HeadLen reservation 提交前保留全部未完成内容；`BeginAppend`、写入 PayloadAndMeta，甚至大量 Advance，都不代表该用户帧已写入磁盘。具体实现见 [RbfFileImpl](../../src/Rbf/Internal/RbfFileImpl.cs) 的 Begin 与 End 路径。因此，在活跃 Builder 阶段强杀取得的是未完成构建丢弃、已完成前缀保留和多个 active 冷重开的证据，不能将 `RecoveryReports.Action=None` 称作执行了修尾。

多个 active 的 `CompletedTail` / `Truncated` 行为已有 [PublicAdmissionTests](../../tests/FrameStore.Tests/Public/PublicAdmissionTests.cs) 的 `WritableOpenRecoversMultipleActiveTailsAndReadOnlyNeverMutatesThem` 验证。它在 owner 关闭后构造尾部残留；此证据继续标为构造镜像，不重标为本轮真实强杀。完整帧已经写出但未取得 ConfirmDurable 成功返回时，是否仍然可读依实际磁盘事实判断，不断言它必须消失。

## 内部窗口的现有证据与待补项

| 窗口 | 当前证据 | 尚未取得的资格 |
| --- | --- | --- |
| 门缺失、短门、坏门；必要布局缺失 | PublicAdmissionTests、DirectoryFrameStoreFilesTests 的真实目录/字节残留和重复拒绝 | 不声称在每个目录创建或写门 syscall 内实际强杀 |
| 私有初始化的全部 `0..I` 合法前缀、非法候选及先于 active 恢复的裁决 | 同上；public RBF 前缀入口测试 | 实际部分写入来自构造镜像，不是本轮 public 停点 |
| active 用户尾部不完整 | 多 active 构造镜像的恢复报告与原地址读取；只读前后 bytes 比较 | 本轮不截获 End/Append 输出中的 syscall |
| 归档 flush / close / move 失败与单次清理 | FrameStoreCoreTests 的真实 RBF 薄包装故障注入；目录 backend 不覆盖移动测试 | 不声称真实 OS close 失败或每个 rename 中断点均已命中 |
| 门已实存完整合法 24B 后 Flush / Close 失败 | DirectoryFrameStoreFiles.InitializeEmptyStore 保留门，工厂清理不回滚的源码路径 | **待补受控失败验证**：工厂不签发 owner、锁释放，随后按合法门重开；默认 FileStream.Write 返回可能仅写入托管 buffer，须先证明实际门内容 |
| 私有残留核验后的必要 Close 失败 | QualifyPrivateCreation 在 CloseOwned / ThrowIfAny 之后才 DeleteFile 的源码顺序 | **待补受控失败验证**：不删除、不恢复 active、不签发 owner，清理保持主错误及单次关闭 |
| Create 初始预检与取得锁之间另一 Create 完成 | FrameStoreFactory 获锁后 CheckFreshInput 的源码双检；既有 bootstrap / 已有门拒绝测试 | **待补受控竞争验证**：A 预检后暂停，B 创建并释放，A 获锁后重新检查并拒绝 |

后三项作为具名缺口交给 R3 / 必要时 R4，不由进程探针成功消除。下一步优先评估窄的 internal、逐调用测试接缝，复用真实工厂路径；不新增公开故障开关、全局回调或通用文件系统抽象。完整条款/证据裁决仍属于 R3，本表不是全部 S2 的验收映射。
