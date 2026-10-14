# VersionStore public PackageReference smoke

此 .NET 10 console consumer 只直接引用精确版本的 `Atelia.VersionStore`，通过传递依赖取得 FrameStore/Binary/Rbf/Data/Primitives 和 K4os。将整个目录复制到仓外后可独立试用，无源码引用或仓库配置依赖。

```powershell
dotnet restore VersionStorePackageSmoke.csproj -p:StoragePackageVersion=<version> --source <local-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project VersionStorePackageSmoke.csproj -c Release --no-restore -p:StoragePackageVersion=<version> -- <fresh-work-directory>
```

参数必须是尚不存在的目录；程序保留 data/versions。一次运行检查真实 data 的 RootMap 创建/发布、独立 opaque bytes ref、每次 PublicationOutcome、同 bytes 的不同 revision、预算历史选取 exact 旧点、预留与自动 fork、ListForks、关联/匿名 immutable tag、拒绝覆盖、ReadTagInfo/ScanTags，以及关闭两层后的只读冷重开和完整 Audit。每条 RootMap root 都实际读回 data；VS Audit 本身不遍历应用 data。VS Dispose 后再次操作 data，展示借入资源仍由调用方管理。

独立的 batch-data/batch-versions 场景保留旧场景及其计数，新增单成员普通 Snapshot 与双成员 CU 的 `PublishRefs`、Confirmed、单成员有效 slice 捕获、一个 CU 成员被普通 Snapshot 覆盖后旧组仍可精确读取。通过传递依赖的 public RBF reader 按已签发 ticket 核对实际记录种类；`ReadRef` 与 `ReadRevision`（包含旧普通 revision 和旧 CU revision）在热 owner、两层都关闭后的可写及只读冷重开中取得一致 bytes/坐标，并独立核对完整 Audit 的 2 refs / 6 snapshots。各模式调用 VS `EnsureUsable`；健康场景 `RecoveryReports.Count` 恒为 0，保存的视图在 owner Dispose 后仍可读，不制造物理修尾。

程序还通过传递 `Atelia.Data` 公面验证 `ChunkedReservableWriter` / `SinkReservableWriter` 的普通 borrow 跨 `Commit → Advance`，以及 Chunked 外层对两种 inner writer 的 `Advance(0)`、`Reset`、`Dispose` 嵌套取消；结束外层借用后，inner 保留已推进的输出和可用性。仅检查这些成功输出/取消路径，不证明任意 pool、sink 或清理故障的行为。

失败抛异常并返回非零，成功 stdout 仅给计数摘要和保留路径。包交付使用仓库 `eng/Test-Package.ps1` 的隔离 cache/config 和 assets/source 检查；本程序不是完整 API 验收。跨 ref 的失败/进程终止、物理恢复、断电、性能和应用恢复策略仍各自需要独立证据。
