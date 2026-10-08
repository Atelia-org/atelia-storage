# atelia-storage 协作入口

使用简体中文交流，保留技术标识符原文。先读根 [README](README.md)，再按任务阅读 [EventJournal](src/EventJournal/README.md)、[RbfSegmentStore](src/RbfSegmentStore/README.md) 和 [RBF 规范](docs/Rbf/rbf-interface.md)。

- main 面向 RBF3 / FrameStore / VersionStore 新栈，当前底座为 Rbf/Data/Primitives，另有独立纯 BCL Binary codec；两个新项目尚未创建。身份、格式与 writer/recovery/Dispose 语义由当前 RBF 规范和测试约束；不要引入 atelia 业务依赖或其 `Analyzers.Style` / `Analyzers.Style.CodeFixes`。
- EventJournal/RbfSegmentStore、toolkit 及旧栈测试保留为冻结参考，底层三个包精确固定为 `[0.2.0-rbf1-preview.1]`，不得改回 main 底层 ProjectReference 或新增运行时适配。旧栈维护和发布归 RBF1 分支；本轮拆分状态与验收见 [过渡方案](docs/rbf1-reference-transition.md)，原设计/验收记录保留历史身份。
- 主线生产包及 pack 顺序只在 `eng/Pack.ps1` 定义，当前阶段 Primitives → Data → Rbf → Binary，Binary 不依赖前三包；两个旧库 `IsPackable=false`。不同内容用不同版本，不覆盖 feed 中既有版本；包版本独立于消费者自己的版本。
- 修改前检查 `git status`，保留已有工作。Windows 下 build/test/pack 串行；先 build Release，再用匹配配置 `dotnet test Atelia.Storage.slnx -c Release --no-build`。
- 当前 solution 保留新旧两组测试；验证实际 assets 中旧栈底层是精确 RBF1 package、新栈是 main project。共存不代表同一消费进程可混用两个同名 Atelia.Rbf 实现。
- 主线包交付必须运行 `eng/Test-Package.ps1` 的隔离纯 Rbf 和纯 Binary public PackageReference smoke，分别检查三包和单包闭包；源码测试不替代包消费证据。该验证检查静态 Source Link 与本地源码，远端下载和消费应用旧数据兼容性需另有实证。旧 selective/额外 segment smoke 命令从历史标签或 RBF1 分支查阅。
- `eng/Pack.ps1` 要求干净已提交树及明确 Version/OutputDirectory；先完成源码评审/测试，再依当前会话授权提交和打包。网络发布依当前会话授权与账号能力进行，不把本文件视作额外授权。
- 为消费方排障时核对实际包 assets、manifest 和来源 revision，阅读对应 commit 源码。其 `UseStorageSources=true` 联调入口是显式选项；日常包消费不能自动跟随兄弟仓当前 HEAD，旧栈参考也不得借源码联调转为 main 新栈依赖。
- 当前仓的提取来源见 [docs/extraction-origin.md](docs/extraction-origin.md)。历史设计链接按原始来源保留，不以修历史链接为理由扩大运行时改动范围。
