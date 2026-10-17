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

格式2/HeadSetTag历史迁移与包资格见[16验收记录](../../docs/HeadStore/16-headset-tags-acceptance.md)，Windows四个隔离PackageReference消费者已通过；当前格式3/引用初始化fork与16B独立key/value成员表的包资格按该记录新增轮次单列，此前候选不自动取得本片资格。

失败抛异常并返回非零，成功 stdout 仅给计数摘要和保留路径。包交付使用仓库 `eng/Test-Package.ps1` 的隔离 cache/config 和 assets/source 检查；本程序不是完整 API 验收。跨 Head 的失败/进程终止、物理恢复、断电、性能和应用恢复策略仍各自需要独立证据。
