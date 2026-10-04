# Rbf public PackageReference smoke

此示例用于 main 的 Primitives/Data/Rbf 三包候选验证，仅直接引用 `Atelia.Rbf`，由公开 API 取得 Data/Primitives 传递依赖。

从干净已提交源码生成唯一版本候选后运行：

```powershell
./eng/Pack.ps1 -Version <new-version> -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version <new-version> -FeedDirectory ./artifacts/feed -WorkDirectory W:/new-rbf-package-smoke
```

WorkDirectory 必须是尚不存在的仓外目录。验证器创建独立 cache/config，传入候选版本，检查三包资产、来源与本地 Source Link checksum。示例覆盖尺寸/预算、分立长度的提前 ticket、两文件互引、完整读取、只读冷重开与无恢复动作的健康可写重开；文件和证据保留供复查。

这是候选包消费证据；公开发布、远端 Source Link 下载、性能、进程终止和跨文件事务分别需要验证。源码消费示例另见 [RbfSizedAppendSmoke](../RbfSizedAppendSmoke/README.md)。
