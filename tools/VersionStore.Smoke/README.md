# VersionStore source-only public consumer

这是独立 console consumer，只直接 `ProjectReference` 主线 `VersionStore`，经其依赖使用 FrameStore 的 public API；`IsPackable=false`。没有 `InternalsVisibleTo`、反射、internal API、EventJournal/RbfSegmentStore 或业务依赖。它不是 PackageReference、公开包交付或 DurableGraph 接入证据。

先在仓根串行完成 Release 构建，然后用两个独立进程写入与冷读；`graph-write` 的应用 root 必须缺失或为空，不自动删除已有数据：

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- graph-write E:/temp/versionstore-graph
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- graph-read E:/temp/versionstore-graph
```

Linux 命令相同，换成 Linux 文件系统上的 fresh 路径。每次成功输出单行 JSON，包含 `status: "ok"`、`scenario`、`pid` 和校验计数。失败写 stderr 并返回非零。写进程退出前关闭 VersionStore 和借入的 data；读进程通过两个真实 `OpenReadOnly` 工厂重新打开 `<root>/data` 与 `<root>/versions`。两次输出的 PID 应不同。

图场景使用两类私有应用 binary record：node 的两个固定12B地址表示 A↔B 及各自 self-reference；state 保存 generation、计数及工作区值。四个 RootMap key 为 `A`、`B`、`counter`、`工作区/😀`。应用显式调用 `RootMapCodec.Encode/Decode`；VersionStore 只接收完整 bytes。两份地址的 FileId 比较使用公开固定地址 wire，不依赖 internal 属性。

已知尺寸 Builder 在写内容前取得地址，先写第一份 node 的前缀，再写/完成第二份 node，最后完成第一份 node；多代图交换 A/B 的申请顺序。每代同时覆盖普通 buffer `Append`、未知尺寸 `BeginAppend()` 与已知尺寸 `BeginAppend(..., out address)`，只依据实际返回地址读取，断言循环跨文件。采用128B软轮转阈值；六代图共24个应用帧。

版本流程包括指定 ref 1、相同 bytes 重复发布及新旧 generation 分离；历史 visitor 停在非 head 的精确 revision，调用返回后分别创建预留 ref 2 与自动 ref 3，再独立推进。ref 2 的公开 head 再自动 fork 为 ref 4；来源后续推进不会扩大子 ref 的继承前缀。ref 1 rewind 追加旧 bytes，创建历史/rewind关联 tag 和分支匿名 tag，再推进来源。

冷读核对四个完整 RootMap、所有实际 node/self/peer 地址、完整继承历史、三条精确 ForkOrigin 声明、三份不可变 tag、`ReadTagInfo/ScanTags` 关联坐标及保存时间。独立只读 `VersionStore.Audit` 应得到4 refs、11个物理本地 snapshots、3 forks、3 tags；随后 FrameStore `Audit` 校验24个图帧。返回的快照、RootPayload、TagInfo 和解码字典在两个 owner Dispose 后再次使用。

非 graph CLI 参数原样交给 `ProcessScenario.Run`；以下命令供父测试驱动，每行 JSON 都带真实 `pid`：

| 命令 | 行为 |
| --- | --- |
| `seed <root>` | fresh 夹具建立基线 ref，关闭双方 owner 后复制完整 data 为 probe-data，并保存应用预期；仅为隔离两层锁的冷测试镜像 |
| `hold <root> <mode>` | `read/write` 或 `vs-read/vs-write`；持有 data 和 VS，输出 `ready` 后等 stdin 的精确 `release`，关闭后输出 `completed`；EOF 不是正常释放 |
| `probe <root> <mode>` | 实际打开双方 owner，返回 `opened`；`vs-*` 借入 probe-data，单独观察 VS 锁。只有精确锁文件和相应平台的原生冲突码返回 `rejected`，其他异常失败 |
| `crash-worker <root> <checkpoint>` | 到达约定公开停点后保存夹具预期并输出 `ready`，保持 owner／Builder 等父进程强杀；任何 stdin 行或 EOF 均失败 |
| `verify <root> <checkpoint>` | 新进程 RW 恢复及读取后，再两次 RO 检查精确 opaque bytes、应用 data 引用、身份、ref/fork/tag 集合及两层 Audit；输出实际 data 恢复报告 |

checkpoint 为 `unpublished-data`、`confirmed-a-active-b`、`ref-confirmed`、`create-confirmed`、`fork-confirmed`、`tag-confirmed`。活跃 B 场景用实际提前地址证明已完成旧 dirty 位于 B 租借文件、另一依赖跨文件；发布 A 后再次访问 B 的 writer，验证其仍活跃。未发布 data 是否留下不作为断言。夹具 manifest 只保存实验预期，不成为生产恢复权威。

父测试串行调度独立 consumer 进程，使用公开 JSON 结果和真实退出状态验收：

```powershell
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~.SystemIntegration."
```

设置 `VERSIONSTORE_PROCESS_EVIDENCE_DIRECTORY` 后，父调度器可保存每个 child 的 transcript；这与本 consumer 的普通 stdout JSON 分开。

父测试一轮包含19项、71个子进程，其中10个在 READY 后实际 `Process.Kill()`；每个 child 都等待退出并核对结果。进程超时/失败也只清理自己持有的 Process。stdout/stderr、命令、PID、kill 与退出码可保留在上述证据目录；路径使用 fresh GUID 临时目录，父测试结束后清理夹具。具体一次运行的事实以实施计划的证据摘要为准。

该模型只证明公开组合和应用自行解释的图。Audit 不替代应用引用闭包检查；tag 关联注记不授予 RefRevision/ForkRef 能力。真实进程终止、Windows/WSL2 Linux、资源规模、故障窗口、隔离包消费与断电资格分别取证，不能由普通图成功推定。
