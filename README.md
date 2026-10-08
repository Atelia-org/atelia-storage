# atelia-storage

Atelia 的 .NET 10 存储基础库，使用 MIT 许可证。main 面向 **RBF3 / FrameStore / VersionStore 新栈**：当前底座为 `Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`，另有独立的 `Atelia.Binary` 基元 codec（BCL + 纯托管 K4os.Compression.LZ4 `[1.3.8]`）；FrameStore / VersionStore 尚未创建。

现有 EventJournal、RbfSegmentStore、toolkit 及其测试保留原路径，作为 **RBF1 旧栈的冻结参考代码**。旧栈维护和公开交付归 `RBF1` 分支；main 不为它们适配新的底层 API。本轮依赖和交付入口拆分已 **Accepted**，方案与实际证据统一见 [RBF1 参考代码过渡方案](docs/rbf1-reference-transition.md)。

| 当前边界 | 底层与交付方式 | 入口 |
| --- | --- | --- |
| main 底座及后续新栈 | main 源码；新建文件只写 RBF3，保留 RBF1 只读和旧 ticket 兼容；pack 顺序 Primitives → Data → Rbf → Binary；Binary 不依赖前三包 | [RBF 接口](docs/Rbf/rbf-interface.md)、[格式](docs/Rbf/rbf-format.md)、[新栈分阶段设计](docs/FrameStore-VersionStore/README.md) |
| main 旧栈参考代码 | Rbf/Data/Primitives 精确 PackageReference `[0.2.0-rbf1-preview.1]`；两个旧库 `IsPackable=false`，不引用 main 底层源码 | [EventJournal](src/EventJournal/README.md)、[SegmentStore](src/RbfSegmentStore/README.md)、[toolkit](tools/EventJournal.Toolkit/README.md) |
| RBF1 维护线 | 旧栈维护与公开发版按该分支及具体包来源；不把 main 的 RBF3 行为写回旧合同 | [过渡方案与公开包来源](docs/rbf1-reference-transition.md) |

当前 solution 同时保留两组源码和测试；依赖图在项目及消费进程边界隔离。这不表示一个消费程序能够同时装载两个同名 `Atelia.Rbf` 实现，也不提供运行时桥接或格式迁移。

## 已公开的 RBF1 包与历史事实

以下五包属于已公开的 **RBF1 系列**，来源 commit 为 `3e9554e2ea70f769607e80b3fc11a85506050533`；它们不是当前 main RBF3 源码的包交付资格。包指南固定到该来源，不能用 main 指南替代。

| NuGet 包 | RBF1 公开版本 | 固定来源入口 |
| --- | --- | --- |
| `Atelia.Primitives` | `0.2.0-rbf1-preview.1` | [结果与错误类型](https://github.com/Atelia-org/atelia-storage/blob/3e9554e2ea70f769607e80b3fc11a85506050533/docs/Primitives/AteliaResult/README.md) |
| `Atelia.Data` | `0.2.0-rbf1-preview.1` | [二进制数据结构与缓冲区](https://github.com/Atelia-org/atelia-storage/tree/3e9554e2ea70f769607e80b3fc11a85506050533/docs/Data/) |
| `Atelia.Rbf` | `0.2.0-rbf1-preview.1` | [RBF1 接口](https://github.com/Atelia-org/atelia-storage/blob/3e9554e2ea70f769607e80b3fc11a85506050533/docs/Rbf/rbf-interface.md) |
| `Atelia.RbfSegmentStore` | `0.2.0-rbf1-preview.1` | [旧栈 segment 指南](https://github.com/Atelia-org/atelia-storage/blob/3e9554e2ea70f769607e80b3fc11a85506050533/src/RbfSegmentStore/README.md) |
| `Atelia.EventJournal` | `0.2.0-rbf1-preview.1` | [旧栈 journal 指南](https://github.com/Atelia-org/atelia-storage/blob/3e9554e2ea70f769607e80b3fc11a85506050533/src/EventJournal/README.md) |

旧消费者使用明确版本的 PackageReference，存储包版本独立于 DurableGraph 等消费库版本。例如，以下引用消费 RBF1 系列，不消费 main 新栈：

```xml
<PackageReference Include="Atelia.EventJournal" Version="[0.2.0-rbf1-preview.1]" />
```

main 保留的 v2 journal/segment 布局包含 `journal.format`、`active.segment`、`catalog.snapshot`；这里 v2 指旧栈目录布局，不能据此推定使用 RBF3。它们日常打开严格且不自动修尾，旧目录升级合同见对应参考指南和 [toolkit](tools/EventJournal.Toolkit/README.md)。

此前 `0.1.2-preview.1` 的不可变 `CreateTag` / `ResolveTag` 公开交付及混合版本闭包见 [selective 历史交付记录](docs/selective-preview-delivery.md)和[已发布指南](https://github.com/Atelia-org/atelia-storage/blob/Atelia.EventJournal-v0.1.2-preview.1/src/EventJournal/README.md#不可变-tag)。更早五包同版候选见 [tag 历史验收](docs/EventJournal/immutable-tags-delivery.md)。历史格式、源码/包、平台与迁移结论保留各自身份，不重标为本轮结果。

## 构建与主线包验证

需要 Git、精确的 .NET SDK **10.0.201**、PowerShell 7；`global.json` 禁用 SDK roll-forward。CI 从同一文件安装 SDK，不需要原 atelia 仓或其两个 Analyzer 项目。Windows/Linux 使用相同入口，.NET 操作串行执行。Windows 极限偏移测试使用稀疏文件，TEMP 所在文件系统需要支持稀疏文件。

主线入口如下。RBF1 过渡阶段的 Windows Release 完整测试 1525/1525 与 W: 三包隔离消费已通过；该阶段构建及候选身份见[过渡验收记录](docs/rbf1-reference-transition.md#6-验收记录)。

```powershell
dotnet build Atelia.Storage.slnx -c Release
./eng/Test-Rbf1ReferenceAssets.ps1
dotnet test Atelia.Storage.slnx -c Release --no-build
# 先提交源码；从干净 HEAD 打包。不同内容必须使用新版本。
$version = "0.3.0-rbf3-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
./eng/Pack.ps1 -Version $version -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version $version -FeedDirectory ./artifacts/feed -WorkDirectory "../storage-package-smoke-$version"
```

solution build/test 同时覆盖 main 底座和冻结旧栈参考项目；旧栈底层从 nuget.org 还原精确 RBF1 包，固定依赖由 [eng/Rbf1Reference.props](eng/Rbf1Reference.props) 维护。assets 检查入口核对旧栈实际底层包版本及来源 commit；main 底座及其测试仍使用源码项目，其依赖图也需复核。不能以 solution 共存推定依赖隔离已成立。

`eng/Pack.ps1` 是主线生产包清单和 pack 顺序的唯一入口，当前输出 Primitives、Data、Rbf、Binary 四包及对应 symbols/manifest。Pack 要求干净已提交树、明确 Version/OutputDirectory 和正确 origin；相同版本只能复用来源与 hash 匹配的产物，不覆盖 feed 中已有内容。默认 All 指当前四包；旧栈 selective 发布和额外 segment smoke 不属于 main 入口，历史命令从 RBF1 分支及交付记录查阅。

`eng/Test-Package.ps1` 在尚不存在的仓外目录建立独立 NuGet cache/config 与 MSBuild 边界，两个独立 consumer 分别直接 PackageReference Rbf 和 Binary，检查 Rbf 三包闭包、Binary + K4os.Compression.LZ4/1.3.8 两包闭包与 public API。包 manifest、静态 Source Link 和本地源码 checksum 检查保留；源码测试不替代包消费，远端 Source Link、真实应用旧数据兼容和公开发布各自需要证据。验证 workspace 保留日志、assets 与 manifest。

候选容器规范化和确定性构建规则由 `eng/Pack.ps1` 维护；应区分候选 hash 与 nuget.org 签名后的公开包 hash。同平台重打包比较完整 nupkg/snupkg，不能只比较 DLL。本轮只准备本地四包候选及隔离消费，不执行公开发布；发布资格和参数仍需按具体提交另行验收。

消费仓日常使用自己的固定 PackageReference；显式源码联调选项不得让旧栈自动跟随 main 底层源码。排障时先核对实际 assets、包 manifest 和来源 commit，再阅读对应源码。

## 文档与 Agent 入口

公共 binary codec 的规范见 [Bare Primitive Value](docs/Binary/bare-primitive-value.md)，实现入口为 [Atelia.Binary](src/Binary/README.md)：schema 驱动的基元、宽容 reader、精确尺寸与显式受控 Brotli/LZ4 block。源码/包资格单列于[实施验收](docs/Binary/bare-primitive-value-acceptance.md)。[Tagged Value](docs/Binary/tagged-value-intent.md) 仍仅为意向，优先评估 CBOR 等成熟标准。

新栈主入口为 [FrameStore / VersionStore 分阶段设计](docs/FrameStore-VersionStore/README.md)。[S1 公共尺寸计算与提前 ticket](docs/FrameStore-VersionStore/01-rbf-sized-append.md)已 Accepted，源码资格见[阶段验收记录](docs/FrameStore-VersionStore/01-rbf-sized-append-acceptance.md)；S2–S6 仍为 Draft。S1 历史测试与本轮依赖拆分结果分开记录，不以 Accepted 推定新项目或包已交付。

- 协作约束见 [AGENTS.md](AGENTS.md)；本轮新旧依赖边界、状态与验收见 [过渡方案](docs/rbf1-reference-transition.md)。
- 旧栈参考指南与 [EventJournal 历史设计](docs/EventJournal/)提供模型和事实背景，不是 main 新栈的规范权威。具体公开包以固定来源为准。
- 来源与原始提取范围见 [extraction-origin](docs/extraction-origin.md)。历史讨论保留原始语境，不自动成为当前活动入口。
- CI 与主线四包交付入口同步；`eng/Verify-Published.ps1` 分别核验 Rbf 三包与 Binary + K4os 两包公开闭包，不能把本地候选消费当作公开源验证。仓库内不保存 token，网络发布须有明确会话授权。
