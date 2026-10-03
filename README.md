# atelia-storage

Atelia 的 .NET 10 存储基础库，使用 MIT 许可证。五个库保留原有 namespace 与包名。

当前 main 包含 **EventJournal/SegmentStore v2 未公开发布的 breaking 候选**：布局包含 `journal.format`、`active.segment` 与 `catalog.snapshot`，日常打开严格且有界，不自动修尾，旧目录需要另行迁移。当前候选见 [EventJournal 指南](src/EventJournal/README.md)、[SegmentStore 指南](src/RbfSegmentStore/README.md)、[离线 toolkit](tools/EventJournal.Toolkit/README.md)和[交付记录](docs/EventJournal/bounded-online-io-delivery.md)。

当前 main 的 RBF 新建文件采用 **RBF3**（4B 长度单位、单尾 EscapeKey）；RBF1 保留只读及原 ticket 兼容。格式与恢复合同见 [RBF 规范](docs/Rbf/rbf-format.md)，实施资格见 [普通打开重构记录](docs/Rbf/rbf-open-fast-path-refactoring.md)。RbfSegmentStore / EventJournal 的适配属于后续阶段。

下表仍是 nuget.org 已发布包事实，包链接固定到已发布源码；不能用 main 指南替代旧包行为。历史公开包曾按同一版本交付；新包可以按项目独立发版：

| NuGet 包 | 本次公开组合 | 作用与入口 |
| --- | --- | --- |
| `Atelia.Primitives` | `0.1.1-preview.2` | [结果与错误类型](https://github.com/Atelia-org/atelia-storage/blob/v0.1.1-preview.2/docs/Primitives/AteliaResult/README.md) |
| `Atelia.Data` | `0.1.1-preview.2` | [二进制数据结构与缓冲区](https://github.com/Atelia-org/atelia-storage/tree/v0.1.1-preview.2/docs/Data/) |
| `Atelia.Rbf` | `0.1.1-preview.2` | [Reversible Binary Framing](https://github.com/Atelia-org/atelia-storage/blob/v0.1.1-preview.2/docs/Rbf/rbf-interface.md) |
| `Atelia.RbfSegmentStore` | `0.1.2-preview.1` | [分段存储、writer lease 与恢复约定](https://github.com/Atelia-org/atelia-storage/blob/Atelia.RbfSegmentStore-v0.1.2-preview.1/src/RbfSegmentStore/README.md) |
| `Atelia.EventJournal` | `0.1.2-preview.1` | [事件、tag、branch/ref 与只读打开](https://github.com/Atelia-org/atelia-storage/blob/Atelia.EventJournal-v0.1.2-preview.1/src/EventJournal/README.md) |

五个包通过 nuget.org 公开分发。消费应用使用明确版本的 `PackageReference`，正常还原不需要克隆或构建本仓；只引用 `Atelia.EventJournal` 即可获得其余四个传递依赖。下面的入口版本会还原上表所示的混合版本闭包，存储包版本独立于 DurableGraph 等消费库版本。

```xml
<PackageReference Include="Atelia.EventJournal" Version="0.1.2-preview.1" />
```

下面的 public API 示例来自可执行的 [EventJournalSmoke](https://github.com/Atelia-org/atelia-storage/blob/Atelia.EventJournal-v0.1.2-preview.1/examples/EventJournalSmoke/Program.cs)。`Unwrap()` 用于已知应成功的示例；生产调用可检查结果的错误信息。`EventFrame.Payload` 是借用的 span，必须在 frame Dispose 前使用。

```csharp
using Atelia.EventJournal;

EventAddress child;
using (var journal = EventJournal.CreateNew(path)) {
    EventAddress root = journal.AppendEventFrame(null, "root"u8).Unwrap();
    child = journal.AppendEventFrame(root, "child"u8).Unwrap();
    journal.CreateTag("saved", child).Unwrap();
    using EventFrame frame = journal.ReadEvent(child).Unwrap();
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(frame.Payload));
}
using (var journal = EventJournal.OpenReadOnlyExisting(path)) {
    using EventFrame frame = journal.ReadEvent(journal.ResolveTag("saved").Unwrap()).Unwrap();
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(frame.Payload));
}
```

`CreateNew` 用于新目录；上述已发布版本的可写打开默认尾部恢复行为、只读校验、单 writer 和 lease/Dispose 规则见对应已发布源码指南；当前 main 的 v2 已移除自动修尾。只读打开损坏尾部会报告错误，不能用可写打开替代只读检查。

## 构建与包验证

需要 Git、精确的 .NET SDK **10.0.201**、PowerShell 7；`global.json` 禁用 SDK roll-forward，以免相同源码和包版本随编译器漂移。CI 从同一文件安装 SDK。不需要原 atelia 仓或其两个 Analyzer 项目。Windows/Linux 使用相同命令，.NET 操作串行执行：

Windows 的极限偏移测试使用稀疏文件，TEMP 所在文件系统需要支持稀疏文件，无须为测试准备约 1 TiB 空闲空间。

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet test Atelia.Storage.slnx -c Release --no-build
# 先提交源码；从干净 HEAD 打包。不同内容必须使用新版本。
$version = "0.2.0-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
./eng/Pack.ps1 -Version $version -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version $version -FeedDirectory ./artifacts/feed -WorkDirectory "../storage-package-smoke-$version" -AdditionalSegmentSmoke
```

上面的命令保留五包同版候选验证；示例版本不是公开发布版本决定。main 的 v2 EventJournal 依赖本次新增 RBF API，须使用五包候选闭包，不能沿用下面旧依赖的 selective 命令。包消费、Windows 和真实迁移证据分别记录，不以源码测试代替。

下面两个 selective 示例保留已发布版本的交付方式，仅用于对应旧源码标签（`Atelia.RbfSegmentStore-v0.1.2-preview.1` / `Atelia.EventJournal-v0.1.2-preview.1`），不用于当前 main。单独制作 `RbfSegmentStore` 候选时，在干净的已提交源码树执行；三个底层依赖明确选用已发布包：

```powershell
$version = "0.1.2-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$dependencies = @{
    'Atelia.Primitives' = '0.1.1-preview.2'
    'Atelia.Data' = '0.1.1-preview.2'
    'Atelia.Rbf' = '0.1.1-preview.2'
}
./eng/Pack.ps1 -Project RbfSegmentStore -Version $version -DependencyVersions $dependencies -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Project RbfSegmentStore -Version $version -FeedDirectory ./artifacts/feed -WorkDirectory "../storage-segment-smoke-$version"
```

`EventJournal` 的 tag 实现依赖 `RbfSegmentStore.ConfirmDurable`；旧版 `RbfSegmentStore 0.1.1-preview.2` 没有这个 API。下面用已发布的 `0.1.2-preview.1` 构建单独的 `EventJournal` 开发候选，使用与上例不同的输出目录：

```powershell
$version = "0.1.2-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$dependencies = @{
    'Atelia.Primitives' = '0.1.1-preview.2'
    'Atelia.Data' = '0.1.1-preview.2'
    'Atelia.Rbf' = '0.1.1-preview.2'
    'Atelia.RbfSegmentStore' = '0.1.2-preview.1'
}
./eng/Pack.ps1 -Project EventJournal -Version $version -DependencyVersions $dependencies -OutputDirectory ./artifacts/eventjournal-feed
./eng/Test-Package.ps1 -Project EventJournal -Version $version -FeedDirectory ./artifacts/eventjournal-feed -WorkDirectory "../storage-eventjournal-smoke-$version"
```

`Pack.ps1` 是五个生产包清单和打包算法的唯一入口，要求 origin 指向 `https://github.com/Atelia-org/atelia-storage.git`，拒绝未提交的源码。它在源码仓选择并核对 SDK；相对输出路径仍按调用者的 PowerShell 当前目录解析。五包同版模式输出五个 nupkg、五个 snupkg 与 `manifest.<版本>.json`；单包模式输出一个新 nupkg/snupkg、所需的三个或四个 nuget.org 已签名依赖包与 `manifest.Atelia.<项目>.<版本>.json`。两种 manifest 均记录新包 commit、SDK 版本和 SHA256，单包 manifest 另记录旧依赖各自的原来源和公开包哈希。相同版本只允许复用匹配来源且所有 hash 相符的既有产物，不覆盖内容。

.NET 10 的 NuGet 容器包含随构建变化的时间和 OPC 标识。Pack 在发布前的 staging 内固定 entry 排序/时间、OPC 路径与关系 Id，采用不压缩 ZIP，明确移除 nuspec 的 repository branch 并按依赖 id 排序；生成的包元数据和 API XML 统一为 UTF-8 LF，文档内容与 XML 语义保留，二进制 payload 不经重写。已有签名的包被拒绝。

Pack 临时固定 CLI 语言为 en-US，并启用 `StorageDeterministicPack`：编译前仅将 SDK 的 GlobalUsings、AssemblyInfo 与 TargetFramework 属性这三个 `obj` 生成文件统一为 UTF-8 LF 和固定生成注释，不修改运行时源码。普通 build/test 不启用此步骤。同平台复验同一 commit/版本时应比较对应 nupkg/snupkg 的 SHA256，不能只比较 DLL。此前公开版本的 Windows 与 Linux 候选整包哈希实测不同；应分别记录上传前候选与 nuget.org 签名后公开包的哈希，不把本机重打包当作公开发布字节。

`Test-Package.ps1` 要求一个尚不存在的仓外目录，建立独立 NuGet 配置、缓存和 MSBuild 配置边界。每个 smoke 仅直接引用其目标包，验证公开 API。当前 `EventJournal` smoke 覆盖 branch/ref/tag、fork/rewind、event/ref rotation、body/Parent 及 strict writable/read-only reopen；`RbfSegmentStore` smoke 覆盖三段轮转、ConfirmDurable、checked read、boundary scan 与 strict reopen。All 模式的 `-AdditionalSegmentSmoke` 在同一隔离 workspace 增跑直接 SegmentStore 消费，并核对四包 assets 闭包；非 All 模式拒绝该参数。验证器核对实际依赖版本、包资产、新包来源 commit 与 PDB Source Link 的本地源码 checksum；单包模式对旧包校验公开签名字节、身份及其各自来源。它不证明远端 Source Link 已可下载，也不替代消费应用的跨版本旧数据验证。验证目录保留日志材料、assets 与包 manifest，便于复查。

修改消费库与存储库时，消费仓使用显式 `UseStorageSources=true`、`StorageSourceRoot=<绝对路径>` 联调；默认仍走其固定包版本。源码模式用于 build/test，消费仓产包先由本仓生成新版本 S，再按包模式生成消费库版本 G。

## 文档与 Agent 入口

已发布 `0.1.2-preview.1` 提供不可变 `CreateTag` / `ResolveTag`，见 [tag 合同](docs/EventJournal/immutable-tags-design.md)、[已发布源码使用指南](https://github.com/Atelia-org/atelia-storage/blob/Atelia.EventJournal-v0.1.2-preview.1/src/EventJournal/README.md#不可变-tag)和[本次公开交付记录](docs/selective-preview-delivery.md)。旧 `EventJournal 0.1.1-preview.2` 没有该 API；首次写入 tag 后旧版本 reader 会拒绝打开。此前的五包同版本地开发包验收保留在[历史交付记录](docs/EventJournal/immutable-tags-delivery.md)。

- 从 [AGENTS.md](AGENTS.md) 和以上库指南进入；[RBF 规范](docs/Rbf/) 与 [EventJournal 设计](docs/EventJournal/) 给出格式与语义约定。需要对应已发布源码时，使用上表中的固定包标签。
- 来源与历史提取范围见 [extraction-origin](https://github.com/Atelia-org/atelia-storage/blob/v0.1.1-preview.2/docs/extraction-origin.md)。历史讨论保留原始语境，不代表当前活动入口。
- 排查某个包时核对该包的版本与来源 commit，再打开对应源码；消费仓目前的单一 `StoragePackageVersion` / `StorageSourceRevision` 只描述旧同版组合，采用本次混合组合前需要逐包固定。Source Link 提供定位与调试信息，包引用不会自动把本仓 AGENTS.md 或指南注入 Agent 上下文。
- CI 复用上述构建/验证入口；公开发布采用手动 workflow 与 nuget.org Trusted Publishing，须先配置仓权限及包所有权。选择性发布每次仅推送所选的一个新包；发布后用 `eng/Verify-Published.ps1` 从公开源复查签名包和独立消费闭包。仓库内不保存 token。
