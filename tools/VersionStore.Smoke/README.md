# VersionStore source-only public consumer

这是独立 console consumer，只直接 `ProjectReference` 主线 `VersionStore`，经其依赖使用 FrameStore、Rbf、Binary/Data 的 public API；`IsPackable=false`。没有 `InternalsVisibleTo`、反射、internal API、EventJournal/RbfSegmentStore 或业务依赖。它不是 PackageReference、公开包交付或 DurableGraph 接入证据。

先在仓根串行完成 Release 构建，然后用两个独立进程写入与冷读；`graph-write` 的应用 root 必须缺失或为空，不自动删除已有数据：

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- graph-write E:/temp/versionstore-graph
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- graph-read E:/temp/versionstore-graph
```

Linux 命令相同，换成 Linux 文件系统上的 fresh 路径。graph 成功输出单行 JSON，包含 `status: "ok"`、`scenario`、`pid` 和校验计数。失败写 stderr 并返回非零。写进程退出前关闭 VersionStore 和借入的 data；读进程通过两个真实 `OpenReadOnly` 工厂重新打开 `<root>/data` 与 `<root>/versions`。父测试核对每条消息的 `pid` 等于其当前 `Process.Id`，等待旧进程退出后才启动新进程；OS 可以复用已经退出的 PID。

图场景使用两类私有应用 binary record：node 的两个固定12B地址表示 A↔B 及各自 self-reference；state 保存 generation、计数及工作区值。四个 RootMap key 为 `A`、`B`、`counter`、`工作区/😀`。应用显式调用 `RootMapCodec.Encode/Decode`；VersionStore 只接收完整 bytes。两份地址的 FileId 比较使用公开固定地址 wire，不依赖 internal 属性。

已知尺寸 Builder 在写内容前取得地址，先写第一份 node 的前缀，再写/完成第二份 node，最后完成第一份 node；多代图交换 A/B 的申请顺序。每代同时覆盖普通 buffer `Append`、未知尺寸 `BeginAppend()` 与已知尺寸 `BeginAppend(..., out address)`，只依据实际返回地址读取，断言循环跨文件。采用128B软轮转阈值；六代图共24个应用帧。

版本流程包括指定 ref 1、相同 bytes 重复发布及新旧 generation 分离；历史 visitor 停在非 head 的精确 revision，调用返回后分别创建预留 ref 2 与自动 ref 3，再独立推进。ref 2 的公开 head 再自动 fork 为 ref 4；来源后续推进不会扩大子 ref 的继承前缀。ref 1 rewind 追加旧 bytes，创建历史/rewind关联 tag 和分支匿名 tag，再推进来源。

冷读核对四个完整 RootMap、所有实际 node/self/peer 地址、完整继承历史、三条精确 ForkOrigin 声明、三份不可变 tag、`ReadTagInfo/ScanTags` 关联坐标及保存时间。独立只读 `VersionStore.Audit` 应得到4 refs、11个物理本地 snapshots、3 forks、3 tags；随后 FrameStore `Audit` 校验24个图帧。返回的快照、RootPayload、TagInfo 和解码字典在两个 owner Dispose 后再次使用。

除 graph/resource/tool/generation/dynamic-ref/tag-golden 场景外的 CLI 参数交给 `ProcessScenario.Run`；以下命令供父测试驱动，每行 JSON 都带真实 `pid`：

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

父测试包含原有19项、71个子进程（10次 READY 后实际 `Process.Kill()`），两档资源向量的2项、6个正常子进程，下述应用策略的7项、25个子进程（6次强杀、2次预期拒绝），以及公开组合补证的2项、6个子进程（1次强杀）。每个 child 都等待退出并核对结果。进程超时/失败也只清理自己持有的 Process。stdout/stderr、命令、PID、kill 与退出码可保留在上述证据目录；路径使用 fresh GUID 临时目录，父测试结束后清理夹具。具体一次运行的事实以实施计划的证据摘要为准。

## 公开组合补证

[DynamicRefScenario.cs](DynamicRefScenario.cs) 用一个独立应用目录 ref 1 指向已登记对象 ref 2；公开自动创建 ref 3 返回 `Confirmed` 后，尚未调用目录 `PublishRef` 就输出 READY 并保持两层 owner。父测试核对 READY/当前 PID 并实际 kill。两个新进程分别以 RO 冷开，检查 `ListRefs` 含 ref 3、目录仍只指向 ref 2、目录与新对象的完整 ticket/bytes 保持原值，并从 ref 3 payload 识别业务 `ObjectId`。夹具 manifest 只记录实验预期，不增加目录协议或自动补登记策略。

[TagGoldenScenario.cs](TagGoldenScenario.cs) 固定消费 [09](../../docs/VersionStore/09-versionstore-immutable-tags.md) 与 `TagNameTests` 的独立向量：`A → 00000351`、`Release → 00000174`、`release → 000001aa`、单 code unit `U+D800 → 00000265`，`tag-48/tag-65 → 00000106`。consumer 不复制 hash/路由算法；名称按原 UTF-16 code units/Ordinal 检查，输出 code unit hex 避免 JSON 替换 lone surrogate。writer 正常关闭双方 owner 并退出，两个独立新进程核对完整不同 payload、`ReadTagInfo/ScanTags` 时间及匿名/Related 元信息，以及源继续发布后保持的旧 Related 坐标；经 public RBF/Binary/CRC API 另观察固定路径、RBF3 桶头和每条完整记录至正常 EOF。父测试独立比对实际桶路径和固定 code units、writer/read 的 payload/metadata，并在每次冷读后比对整棵目录清单及 SHA-256 不变。

```powershell
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- tag-golden-write E:/temp/versionstore-tag-golden
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- tag-golden-read E:/temp/versionstore-tag-golden
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --filter FullyQualifiedName~PublicCombinationProcessTests
```

CLI 入口为 `dynamic-ref-worker/dynamic-ref-read` 与 `tag-golden-write/tag-golden-read`；两个写入口的 root 必须缺失或为空。动态 worker 只接受父进程强杀，任何 stdin 或 EOF 都失败，使用上面的父测试入口驱动。它只证明公开创建成功后的应用目录窗口；tag golden 只证明这组六个独立向量和具名格式观察。内部写入中途、物理残尾、包消费与断电另行取证，本 consumer 保持 Public 身份。

## 应用策略示例

[ToolLoopScenario.cs](ToolLoopScenario.cs) 用 `runtime/history/tasks/context` 四个 RootMap 根保存应用记录。先正常确认冻结请求与身份的 Started，才允许调用文件模拟的外部 backend。`tool-worker <root> <checkpoint>` 在 `started`、`external-completed` 或 `result-published` 输出 READY 后保持 owner 等待父测试强杀；任何 stdin 或 EOF 都失败。不要用普通输入退出冒充中断。

`tool-recover <root>` 在新进程打开双方 writer，依据持久应用 phase 决策：

| 实际状态 | 应用策略 |
| --- | --- |
| Started，查询无 receipt | 返回 uncertain；不重试外发，也不追加同值 Started |
| Started，存在匹配 receipt | 核对两库身份、operation/attempt 和冻结请求后发布 Result |
| Result 已发布 | 从应用状态完成；不查询 backend，不重复执行或结算 |

`tool-read <root>` 只读核验任一阶段的四根记录与历史。父测试验证两次恢复不重复发布、只读不改任何文件、已发布 Result 即使移除 receipt 仍可恢复，以及错误 attempt/request receipt 在任何恢复发布前拒绝。`tool-backend-receipt.json` 是示例外部服务的 receipt，不是 VersionStore 的恢复权威；无 receipt 不证明外部未执行。示例的 settlement 是发布记录中的应用结果计入，不是另一个外部付款动作，不提供任意外部调用 exactly-once。

[GenerationScenario.cs](GenerationScenario.cs) 用受控 Task 完成顺序送入同一个串行 driver。每次回包读取最新 head，分别检查 generation、operationId 及该 slot 是否已结算。九次回包接受三次、拒绝六次；第二代先合并 B 再合并 A，保留两个结果。拒绝不会追加 revision 或 data；下一次有效发布保存已观察的拒绝计数。冷读验证五份快照、二十个应用帧、当前 A=101/B=202，以及跨代累计结算值314。

```powershell
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- generation-write E:/temp/versionstore-generation
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- generation-read E:/temp/versionstore-generation
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ApplicationProcessTests
```

应用 schema、请求身份、回包策略及 RootMap 解码都在 consumer。VersionStore 没有新增 CAS、多线程 writer、外部调度或自动恢复状态机。

## 有限资源探针

`resource-write <root> <size>` 建立16、64或256个 ref 的有限夹具；root 必须缺失或为空。`resource-read <root>` 在另一个进程以只读 owner 验证并观察资源：

```powershell
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- resource-write E:/temp/versionstore-resource-64 64
dotnet run --project tools/VersionStore.Smoke -c Release --no-build -- resource-read E:/temp/versionstore-resource-64
```

向量包括来源继续增长后的精确 fork 继承、同桶聚集的 tag、三次重复发现/历史/元信息/完整校验，以及独立 owner 上的全 ref 当前值缓存对照。 写进程另测关闭后的新 RW owner 打开与连续 CreateTag 批次（含 ReadRef 捕获）；ForkRef 创建本身未独立计时。输出单行 JSON 保留每次操作耗时、累计托管分配与区间外资源快照。Windows 的进程总 handle 和 Linux 的 fd 不具有相同口径；Linux 另检查指向夹具根的实际 fd。RSS、GC heap 与进程总句柄只作观察，不作为吞吐或任意规模无泄漏的保证；采样点不表示操作中的真实峰值。

SystemIntegration 中16/64两档分别写一次、在两个新进程冷只读验证，并由父进程独立核对文件清单与SHA-256不变；256档可单独运行。具体测量范围、样本与平台结论见[实施计划](../../docs/VersionStore/12-versionstore-implementation-plan.md)。

该模型只证明公开组合和应用自行解释的图。Audit 不替代应用引用闭包检查；tag 关联注记不授予 RefRevision/ForkRef 能力。真实进程终止、Windows/WSL2 Linux、资源规模、故障窗口、隔离包消费与断电资格分别取证，不能由普通图成功推定。
