# FrameStore public PackageReference smoke

此 .NET 10 console consumer 只直接引用精确版本的 `Atelia.FrameStore`，通过传递依赖消费 Binary/Rbf/Data/Primitives 和 K4os。将整个目录复制到仓外后可独立试用，无源码引用或仓库配置依赖。

```powershell
dotnet restore FrameStorePackageSmoke.csproj -p:StoragePackageVersion=<version> --source <local-feed> --source https://api.nuget.org/v3/index.json
dotnet run --project FrameStorePackageSmoke.csproj -c Release --no-restore -p:StoragePackageVersion=<version> -- <fresh-work-directory>
```

参数必须是尚不存在的目录；程序保留 data 与地址编码文件。一次运行检查 buffer/未知尺寸/已知尺寸三种追加、交错 Builder 和提前地址互引、显式 ConfirmDurable、小阈值归档、可写及只读冷重开后原地址读取、固定与变长地址 codec、EnsureUsable、Inventory/Audit 和 FrameRead 自有 bytes 生命周期。失败抛异常并返回非零，成功 stdout 仅给小摘要和保留路径。

包交付使用仓库 `eng/Test-Package.ps1` 的隔离 cache/config 和 assets/source 检查。此程序验证健康路径的公开包消费；进程终止、性能、远端 Source Link 和应用数据兼容另有独立证据。
