# atelia-storage 协作入口

使用简体中文交流，保留技术标识符原文。先读根 [README](README.md)，再按任务阅读 [EventJournal](src/EventJournal/README.md)、[RbfSegmentStore](src/RbfSegmentStore/README.md) 和 [RBF 规范](docs/Rbf/rbf-interface.md)。

- 本仓维护五个 .NET 10 存储基础库；身份、格式与 writer/recovery/Dispose 语义由现有指南和测试约束。不要引入 atelia 业务依赖或其 `Analyzers.Style` / `Analyzers.Style.CodeFixes`。
- 五个生产项目与 pack 顺序只在 `eng/Pack.ps1` 定义；不同内容用不同版本，不覆盖 feed 中既有版本。包版本独立于消费者自己的版本。
- 修改前检查 `git status`，保留已有工作。Windows 下 build/test/pack 串行；先 build Release，再用匹配配置 `dotnet test Atelia.Storage.slnx -c Release --no-build`。
- 包交付必须运行 `eng/Test-Package.ps1` 的隔离 public PackageReference smoke；源码测试不替代包消费证据。该验证检查静态 Source Link 与本地源码，远端下载和消费应用旧数据兼容性需另有实证。
- `eng/Pack.ps1` 要求干净已提交树及明确 Version/OutputDirectory；先完成源码评审/测试，再依当前会话授权提交和打包。网络发布依当前会话授权与账号能力进行，不把本文件视作额外授权。
- 为消费方排障时核对包 manifest 和其 `StorageSourceRevision`，阅读对应 commit 源码。其 `UseStorageSources=true` 联调入口是显式选项；日常包消费不能自动跟随兄弟仓当前 HEAD。
- 当前仓的提取来源见 [docs/extraction-origin.md](docs/extraction-origin.md)。历史设计链接按原始来源保留，不以修历史链接为理由扩大运行时改动范围。
