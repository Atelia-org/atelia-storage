# atelia-storage

Atelia 的 .NET 10 存储基础库，使用 MIT 许可证。五个库保留原有 namespace 与包名，按同一个存储版本一起交付：

| NuGet 包 | 作用与入口 |
| --- | --- |
| `Atelia.Primitives` | [结果与错误类型](docs/Primitives/AteliaResult/README.md) |
| `Atelia.Data` | [二进制数据结构与缓冲区](docs/Data/) |
| `Atelia.Rbf` | [Reversible Binary Framing](docs/Rbf/rbf-interface.md) |
| `Atelia.RbfSegmentStore` | [分段存储、writer lease 与恢复约定](src/RbfSegmentStore/README.md) |
| `Atelia.EventJournal` | [事件、parent chain、branch/ref 与只读打开](src/EventJournal/README.md) |

首次公开发布尚未完成；当前从固定源码 commit 生成本地包。消费应用默认使用明确版本的 `PackageReference`，例如只引用 `Atelia.EventJournal` 即可获得其余四个传递依赖。存储版本独立于 DurableGraph 等消费库版本。

```xml
<PackageReference Include="Atelia.EventJournal" Version="0.1.1-dev.20260913.2" />
```

下面的 public API 示例来自可执行的 [EventJournalSmoke](examples/EventJournalSmoke/Program.cs)。`Unwrap()` 用于已知应成功的示例；生产调用可检查结果的错误信息。`EventFrame.Payload` 是借用的 span，必须在 frame Dispose 前使用。

```csharp
using Atelia.EventJournal;

EventAddress child;
using (var journal = EventJournal.CreateNew(path)) {
    EventAddress root = journal.AppendEventFrame(null, "root"u8).Unwrap();
    child = journal.AppendEventFrame(root, "child"u8).Unwrap();
    using EventFrame frame = journal.ReadEvent(child).Unwrap();
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(frame.Payload));
}
using (var journal = EventJournal.OpenReadOnlyExisting(path)) {
    using EventFrame frame = journal.ReadEvent(child).Unwrap();
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(frame.Payload));
}
```

`CreateNew` 用于新目录；已有可写日志的打开及默认尾部恢复行为、只读入口的校验且不修改保证、单 writer 和 lease/Dispose 规则均见对应库指南。只读打开损坏尾部会报告错误，不能用可写打开替代只读检查。

## 构建与包验证

需要 Git、.NET 10 SDK、PowerShell 7；不需要原 atelia 仓或其两个 Analyzer 项目。Windows/Linux 使用相同命令，.NET 操作串行执行：

Windows 的三个极限偏移测试使用稀疏文件，TEMP 所在文件系统需要支持稀疏文件，无须为测试准备约 1 TiB 空闲空间。

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet test Atelia.Storage.slnx -c Release --no-build
# 先提交源码；从干净 HEAD 打包。不同内容必须使用新版本。
./eng/Pack.ps1 -Version 0.1.1-dev.20260913.2 -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version 0.1.1-dev.20260913.2 -FeedDirectory ./artifacts/feed -WorkDirectory ../storage-package-smoke-1
```

`Pack.ps1` 是五个生产包清单和打包算法的唯一入口，要求 origin 指向 `https://github.com/Atelia-org/atelia-storage.git`，拒绝未提交的源码。输出五个 nupkg、五个 snupkg 与 `manifest.<版本>.json`，记录 commit 和 SHA256。相同版本只允许复用匹配来源且所有 hash 相符的既有产物，不覆盖内容。

`Test-Package.ps1` 要求一个尚不存在的仓外目录，建立独立 NuGet 配置、缓存和 MSBuild 配置边界。它只通过一个 EventJournal PackageReference 执行 create → append → checked read → close → read-only reopen，再验证实际五包版本、包资产、来源 commit 与 PDB Source Link 的本地源码 checksum。它不证明远端 Source Link 已可下载，也不替代消费应用的跨版本旧数据验证。验证目录保留日志材料、assets 与包 manifest，便于复查。

修改消费库与存储库时，消费仓使用显式 `UseStorageSources=true`、`StorageSourceRoot=<绝对路径>` 联调；默认仍走其固定包版本。源码模式用于 build/test，消费仓产包先由本仓生成新版本 S，再按包模式生成消费库版本 G。

## 文档与 Agent 入口

- 从 [AGENTS.md](AGENTS.md) 和以上库指南进入；[RBF 规范](docs/Rbf/) 与 [EventJournal 设计](docs/EventJournal/) 给出格式与语义约定。
- 来源与历史提取范围见 [extraction-origin](docs/extraction-origin.md)。历史讨论保留原始语境，不代表当前活动入口。
- 排查某个包时先读消费仓的 `eng/StorageDependency.props`，用其 `StorageSourceRevision` 打开本仓对应 commit 的指南/源码；不要用最新分支解释旧包。Source Link 提供定位与调试信息，包引用不会自动把本仓 AGENTS.md 或指南注入 Agent 上下文。
- CI 复用上述构建/验证入口；公开发布采用手动 workflow 与 nuget.org Trusted Publishing，须先配置仓权限及包所有权。仓库内不保存 token。
