# HeadStore public PackageReference smoke

此 .NET 10 console consumer 只直接引用精确版本的 `Atelia.HeadStore`，通过传递依赖取得 FrameStore/Binary/Rbf/Data/Primitives 和 K4os。将整个目录复制到仓外后可独立试用，无源码引用或仓库配置依赖。

```powershell
dotnet restore HeadStorePackageSmoke.csproj -p:StoragePackageVersion=<version> --source <local-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project HeadStorePackageSmoke.csproj -c Release --no-restore -p:StoragePackageVersion=<version> -- <fresh-work-directory>
```

参数必须是尚不存在的目录；程序保留 data/heads。一次运行检查真实 data 的 NamedFrameMap 创建/发布、独立 opaque bytes Head、每次 PublicationOutcome、同 bytes 的不同 EntryId、预算历史选取 exact 旧点、预留与自动引用初始化 fork（只发布header且返回新HeadId，无克隆Entry）、ListHeadOrigins、HeadSetTag当前成员捕获、拒绝覆盖、ReadTagInfo/ScanTags，以及关闭两层后的只读冷重开和完整 Audit。两条双成员tag分别在相应Head当前时捕获历史A+opaque、opaque+reservedFork；后续推进与冷重开后仍按成员ID读回原值。每条NamedFrameMap root都实际读回data；HS Audit 本身不遍历应用 data。HS Dispose 后再次操作 data，展示借入资源仍由调用方管理。

独立的 batch-data/batch-heads 场景保留旧场景及其计数，新增单成员普通 SingleEntry 与双成员 CU 的 `PublishHeads`、Confirmed、单成员有效 slice 捕获、一个 CU 成员被普通 SingleEntry 覆盖后旧组仍可精确读取。通过传递依赖的 public RBF reader 按已签发 ticket 核对实际记录种类；`ReadHead` 与 `ReadEntry`（包含旧普通 EntryId 和旧 CU EntryId）在热 owner、两层都关闭后的可写及只读冷重开中取得一致 bytes/坐标，并创建CU subset/pair两条tag，单成员表不吸收未选择peer，双成员表保留完整组合；两层冷重开后按成员ID精确读，并独立核对Audit的2 Heads/6 entries，tag成员验证不增加EntryCount。各模式调用 HS `EnsureUsable`；健康场景 `RecoveryReports.Count` 恒为 0，保存的视图在 owner Dispose 后仍可读，不制造物理修尾。

独立reference-forks场景建立source与child/sibling/emptyGrandchild共4个Head；shared-source tag保留4个keys指向同一source Entry，连续空fork不生成克隆。child首次发布及source随后推进后仅有3条真实Entry、3条来源边、1个tag；热读取与RW/RO双层冷开检查所选Head身份和真实Entry身份各自保持。

程序还通过传递 `Atelia.Data` 公面验证 `ChunkedReservableWriter` / `SinkReservableWriter` 的普通 borrow 跨 `Commit → Advance`，以及 Chunked 外层对两种 inner writer 的 `Advance(0)`、`Reset`、`Dispose` 嵌套取消；结束外层借用后，inner 保留已推进的输出和可用性。仅检查这些成功输出/取消路径，不证明任意 pool、sink 或清理故障的行为。

格式2/HeadSetTag历史迁移与包资格见[16验收记录](../../docs/HeadStore/16-headset-tags-acceptance.md)，Windows四个隔离PackageReference消费者已通过；格式3/引用初始化fork与16B独立key/value成员表的包资格按该记录新增轮次单列。当前 consumer 使用格式4；此前候选不自动取得本轮资格，必须以本轮真实包消费结果为准。

失败抛异常并返回非零，成功 stdout 仅给计数摘要和保留路径。包交付使用仓库 `eng/Test-Package.ps1` 的隔离 cache/config 和 assets/source 检查；本程序不是完整 API 验收。跨 Head 的失败/进程终止、物理恢复、断电、性能和应用恢复策略仍各自需要独立证据。

紧凑 Handle 检查覆盖 `HeadId` 的4B和 `HeadEntryId` 的16B托管尺寸、24bit本地文件号上限，以及经传递依赖调用 Data 的 `ZeroPreservingXor32.Apply` 零保留与往返。文件号通过活的 owner 的 `GetLocalHeadId` 解释；冷重开后仍验证保留 Handle 的纯值身份、发现集合与精确 Entry。

独立 `handles-data/handles-heads` 场景覆盖 Handle encoding v1 的公开 codec：双方 owner 关闭后，分别把空 fork C 的 `HeadId` 和实际 owner A 的旧点 `HeadEntryId` 保存为 `selected-head.v1.bin`（4B）及 `bookmarked-entry.v1.bin`（12B）；应用 schema 文件说明版本和字段种类。重新打开双方 owner 前先从文件 `TryRead`，验证等值/hash及选择身份。对恢复的 C 分别调用 `PublishHead` 和单成员 `PublishHeads` 保存相同 bytes，得到不同 EntryId，同时 A 保持原位置。恢复的旧点用于 `ReadEntry`、`ForkHead` 和 `ReadEntryHistory`；从 C 的准确点得到 C→A 的固定历史，分页在后续追加后仍不吸收新后缀，续页只跳过首个重复 anchor，并覆盖根处仅含重复 anchor 的结束页。只读冷开重复检查历史及3 Heads/6 entries/2 origins的Audit。已有 batch 场景同时在热、RW和RO冷开中验证从 codec 恢复的普通及CU准确历史前缀、原bytes。

这里的文件往返仍在同一 consumer 进程中；真正 writer 退出后启动 reader、无 owner 解码的进程证据由 [PersistentHandleProcessTests](../../tests/HeadStore.Tests/SystemIntegration/PersistentHandleProcessTests.cs) 与 [source-only smoke](../../tools/HeadStore.Smoke/README.md) 提供。新增公面是否进入包以本轮实际隔离消费结果为准。
