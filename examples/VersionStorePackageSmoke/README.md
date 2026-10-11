# VersionStore public PackageReference smoke

此 .NET 10 console consumer 只直接引用精确版本的 `Atelia.VersionStore`，通过传递依赖取得 FrameStore/Binary/Rbf/Data/Primitives 和 K4os。将整个目录复制到仓外后可独立试用，无源码引用或仓库配置依赖。

```powershell
dotnet restore VersionStorePackageSmoke.csproj -p:StoragePackageVersion=<version> --source <local-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project VersionStorePackageSmoke.csproj -c Release --no-restore -p:StoragePackageVersion=<version> -- <fresh-work-directory>
```

参数必须是尚不存在的目录；程序保留 data/versions。一次运行检查真实 data 的 RootMap 创建/发布、独立 opaque bytes ref、每次 PublicationOutcome、同 bytes 的不同 revision、预算历史选取 exact 旧点、预留与自动 fork、ListForks、关联/匿名 immutable tag、拒绝覆盖、ReadTagInfo/ScanTags，以及关闭两层后的只读冷重开和完整 Audit。每条公开 root 都实际读回 data；VS Audit 本身不遍历应用 data。VS Dispose 后再次操作 data，展示借入资源仍由调用方管理。

失败抛异常并返回非零，成功 stdout 仅给计数摘要和保留路径。包交付使用仓库 `eng/Test-Package.ps1` 的隔离 cache/config 和 assets/source 检查；进程终止、性能、跨 ref 事务和应用恢复策略各自需要独立证据。
