# FrameStore public 循环图消费者探针

独立消费 [S2](../../docs/FrameStore-VersionStore/02-framestore-core.md) 的公开 API，取得 [S3](../../docs/FrameStore-VersionStore/03-framestore-interleaved-builders-and-durability.md) 和[收尾计划 R2](../../docs/FrameStore-VersionStore/02-framestore-completion-plan.md) 所要求的构建轨迹。项目仅直接 `ProjectReference` 当前 `src/FrameStore`，不使用 `InternalsVisibleTo`，不进入 solution、生产包清单或 VersionStore。

从仓库根串行运行，需要仓库指定的 .NET 10 SDK；Windows 或 Linux 的实际文件系统须满足 FrameStore 的准入要求：

```powershell
dotnet build experiments/FrameStoreConsumerProbe/Probe.csproj -c Release
$revision = git rev-parse HEAD
dotnet run --project experiments/FrameStoreConsumerProbe/Probe.csproj -c Release --no-build -- --revision $revision
```

默认新建 `artifacts/framestore-consumer-probe/<UTC时间>-<随机值>`；也可以用 `--output-directory artifacts/framestore-consumer-windows-1` 指定尚不存在的目录。已有目录或文件拒绝复用，探针不删除任何数据。成功退出码 0，场景/断言失败 1，参数或结果输出失败 2。失败时保留已建立的 store、地址证据和已执行的断言。

Linux 使用相同项目，仍须新建输出目录；例如：

```bash
dotnet build experiments/FrameStoreConsumerProbe/Probe.csproj -c Release
dotnet run --project experiments/FrameStoreConsumerProbe/Probe.csproj -c Release --no-build -- --output-directory artifacts/framestore-consumer-linux-1 --revision "$(git rev-parse HEAD)"
```

| 场景 | 实际消费轨迹及断言 |
| --- | --- |
| 单帧自引用及归档 | sized Begin 得到 a，用 public 12B codec 将 a 写入自己的引用字段；End 地址不变；64B 软阈值触发后续 ConfirmDurable 归档，同地址在归档前后及新 RO owner 中均解码为 7→7 |
| A↔B 正序完成 | A 写 header 后再申请 B，两者分别 reserve 引用字段，按 B/A 次序回填和 Commit，再按 A/B 次序 End |
| A↔B 逆序完成 | 相同申请和交错填充轨迹，按 B/A 次序 End；Dispose 后新 RO owner 解码为与正序完成相同的 1→2,2→1 图 |
| A↔B 逆序申请 | 先申请并写 B header，再申请并写 A header；按 A/B 次序 End，Dispose 后新 RO owner 仍解码为相同 identity 的 1→2,2→1 图，不按申请顺序指定逻辑身份 |
| M=1 + 完整 Append | A 占用唯一 Builder 槽并预留引用；第二 Builder 被拒绝，完整 Append B 仍成功并指向 a；通过 `TryGetReservedSpan` 回填 A 的 b、Commit、End，重开读回相同 A↔B 图 |
| 取消及地址复用 | 先申请 B、再申请 A，A 完成并引用未完成的 B；Confirm 不使 B 完成。取消 B 时消费者放弃旧图；同尺寸 C 复用 b，旧 Builder/Writer 的 End、借用、Advance、Length、reservation/Commit 被拒绝，旧 Dispose 不取消 C。新 RO owner 对 b 的合法物理读取实际得到 identity=3 的 C；消费者要求 identity=2 的 B，因此拒绝旧 A/B 图，新 C→C 图成立 |

消费者定义固定 24B 的 node payload：`FSN1` 4B magic、LittleEndian `uint identity`、LittleEndian `uint expectedTargetIdentity`、FrameAddress 的唯一固定 12B 引用字段。最后一个字段使用 `TryWrite/TryRead`，不解释 FileId、offset 或 SizedPtr 的内部布局。identity 是消费者的业务校验，FrameStore 只校验物理内容和帧格式，不证明引用原始来源。本例不同的 B/C identity 使替换可被检测；数值地址或 identity 单独不能恢复原构建尝试的资格，消费者仍须在取消时无条件放弃旧状态。

活跃 Builder 的提前地址读取在产生 Result 之前被 Building completed-prefix guard 以 `InvalidOperationException` 拒绝。探针将这个生命周期拒绝与普通读取 Result failure 分开记录；确认已完成 A 或 B 的输出不使其未完成依赖可读。

每个场景先 ConfirmDurable，在 writer Dispose 后从 `consumer-addresses.json` 重新载入 StoreId 和 12B 地址，再创建新的 `OpenReadOnly` owner，沿 payload 中解码的引用遍历循环图。地址文件仅为探针的输入/结果证据，不是业务根发布协议。图比较按 node identity 和引用关系进行，不按地址数值、文件名或 Inventory 枚举顺序推定业务关系。输出的文件名排序仅用于展示归档布局。

`result.json` 包含运行环境、probe/FrameStore assembly SHA-256、调用方提供的 revision、各场景真实事件顺序、保存地址位置、读回 node/引用、归档布局、消费者来源拒绝及每条实际断言；`assertionCount` 是实际断言总数，stdout 输出紧凑 JSON 摘要。固定六个小 store、十一个完成用户帧和一个取消 Builder，单次输入有界，不提供后台服务或持续负载。

这证明新 owner 的重开消费，未清空 OS 文件缓存，也不测试进程中断或断电。`callerSuppliedRevision` 是调用方声明，不自动证明 dirty 工作区与该提交一致。当前项目验证源码 public API；资源/规模、NuGet 包消费、VersionStore 发布和完整 S2 系统验收由独立证据裁决，本文不预先宣称平台运行通过。
