# RBF1 下游依赖与维护分支基线调查

日期：2026-10-04。Storage 工作区观察基线：`f6f1eb38557863ba5ea1634844a90f0cbe5774cf`；消费仓 `/repos/Atelia-org/atelia`：`e030902472e5587bf31a105e1e1206759d566b00`。
本次读取配置、restore 资产、现有输出和包，并创建 Git refs；没有 restore/build/test、修改消费者配置、操作会话数据或运行服务。

## 结论与 refs

当前本机普通构建采用五包同版 `0.2.0-dev.20260929020652`，其共同来源为 **`5288bd55b4a5181942dadd76516f799002508144`**。
维护分支 `RBF1` 和 annotated tag `rbf1-baseline-20261004` 都以这个 commit 为起点；tag 冻结已确认的消费基线，branch 用于后续维护。创建本地 refs，不切换当前 main，不推送远端。

该 commit 的 `src/Rbf/Internal/RbfLayout.cs` 使用 `"RBF1"u8` Fence，`RbfFile.CreateNew` 直接写入它；位于 RBF3 实施之前。选择的是实际消费基线，不是最后一个改写 RBF3 之前的 commit，也不是本仓当前 HEAD。

## 三种版本身份分别核对

| 层次 | 版本 / commit | 当前证据 |
| --- | --- | --- |
| 消费仓提交的公开默认配置 | `0.1.1-preview.2` / `976aa345f923da09e2a5cf1dc25ba592b3818b63` | `eng/StorageDependency.props`；缓存公开包 nuspec repository commit |
| 本机实际生效配置及 restore 闭包 | `0.2.0-dev.20260929020652` / `5288bd55b4a5181942dadd76516f799002508144` | ignored `eng/StorageDependency.Local.props`、MSBuild 属性求值、活动项目 assets、dev 包 manifest/nuspec/哈希 |
| 现有 Galatea 输出文件 | Release 部分旧 DLL；Debug 全旧包 | 与缓存包 DLL 逐字节比较；不能仅由当前 props 或 assets 推导旧输出已更新 |

`Directory.Build.props` 在基础配置后导入本地 override。本机 `UseStorageSources=false`、`StorageStrictTailOpen=true`，使用 PackageReference，不跟随兄弟源码仓 HEAD。
对 `Galatea.Server.csproj` 与 `SessionJournal.csproj` 仅运行 `dotnet msbuild -getProperty:UseStorageSources,StoragePackageVersion,StorageSourceRevision,StorageStrictTailOpen,RestoreConfigFile`，没有执行构建 target；两者返回相同 dev 版本与来源。

Galatea 实际项目名是 `prototypes/Galatea/Galatea.Server.csproj`，通过 ProjectReference 引用 SessionJournal 等项目；SessionJournal 及三个相关库直接引用 `Atelia.EventJournal`。Primitives/Data/Rbf/RbfSegmentStore 从其包依赖闭包取得。

## 项目与包证据

`prototypes` 下 11 个具有 Storage restore 资产的项目一致采用上述五包 dev 版本，全部为 package：

- `Galatea`（项目为 Galatea.Server）、`Galatea.Input`、`Galatea.RecapGrid`。
- `SessionJournal`、`SessionJournal.Cli`、`SessionJournal.Offline`。
- `SessionJournal.HistoryTimeline`、`SessionJournal.HistoryTimeline.O200k`。
- `SessionJournal.RecapGrid`、`SessionJournal.RecapGrid.Cadence`、`SessionJournal.RecapGrid.Hosting`。

按当前 `Atelia.sln` 的活动项目清单核对，44 个有 Storage 依赖的项目均使用这个 package 闭包，无不同版本/源码模式，所有活动项目 assets 均存在。目录中未被 solution 引用的旧 assets 不充当当前依赖证据，也没有删除它们。

上游 `artifacts/dev-feed/manifest.0.2.0-dev.20260929020652.json` 的 sourceRevision 与五个包 nuspec repository commit 都等于 `5288bd55`。逐包核对 feed 的 SHA-256、全局缓存 nupkg 字节、解包 DLL，以及活动 assets 的 SHA-512，全部一致。

| 包（均为 `0.2.0-dev.20260929020652`） | nupkg SHA-256 |
| --- | --- |
| Atelia.Primitives | `7b21d85c506de0e04d1224b6ac452e6e4b005957741553cb3b70e22c20c514dd` |
| Atelia.Data | `2c60f014d14ff482c1c8a2435e55d41969c83d26b222174b6ef6b53348c3ab82` |
| Atelia.Rbf | `cdea31879c7e7235961f7a151c069fae03666bdc779951480cf7d68db0a867e6` |
| Atelia.RbfSegmentStore | `d75b848ab41dbb0c6b77c60d85d09d225f55783c7a90fb60597f59e60e4eeca6` |
| Atelia.EventJournal | `a9a6807a573ae41060d3a3cc875b44a76136de9b78d53e3984afd9555c0608bf` |

这些材料证明本机包来源与消费闭包，没有重跑隔离 PackageReference smoke、PDB/source checksum 或远端包下载。本机过去的包验证和数据切换可从消费仓 `docs/Galatea/storage-v2-local-upgrade-20260929.md` 查阅，本次不将其重标为新验收。

## Galatea 现有输出的额外发现

`prototypes/Galatea/bin/Release/net10.0` 中，EventJournal/RbfSegmentStore/Rbf DLL 与 dev 包相同，Data/Primitives DLL 与旧公开包相同；Release `.deps.json` 则将五包全部标为 dev 版本。这是现有输出字节与声明版本的区别，本次没有推断其形成原因或更改输出。
Debug 的五个 DLL 和 `.deps.json` 均对应旧公开版本。

| 库 | 当前 restore/包来源 | Release DLL 来源（字节匹配） | Debug DLL 来源（字节匹配） |
| --- | --- | --- | --- |
| Data | `5288bd55` | `976aa345` | `976aa345` |
| Primitives | `5288bd55` | `976aa345` | `976aa345` |
| Rbf | `5288bd55` | `5288bd55` | `976aa345` |
| RbfSegmentStore | `5288bd55` | `5288bd55` | `976aa345` |
| EventJournal | `5288bd55` | `5288bd55` | `976aa345` |

两个 commit 间 Data 与 Primitives 的源码子树完全相同（不仅 API 相同）：Data tree 为 `3430662f5a80d91cc348469a97efb46c832920db`，Primitives tree 为 `661ef0a635fd614bb71108acfb2e6748d2ba2579`。所以 `5288bd55` 可完整承载当前源码能力，不能因此把旧 DLL 的包身份写成新包。
本次没有检查运行中进程的已加载 assembly；现有 bin 文件不证明真实服务当下加载的版本。

## 维护范围建议

`RBF1` 从当前已消费的完整五库基线维护修复；旧公开默认仍由既有 `v0.1.1-preview.2` tag 定位。RBF1 是帧格式，EventJournal v1/v2 是目录协议：本维护基线包含 v2 format gate、strict open 与离线 v1→v2 toolkit，不承诺直接打开所有旧目录。消费仓已经使用的 v2 数据范围见其切换记录。

后续维护包使用新的独立版本，保持消费者的版本/来源 pin 配套，不能用相同版本覆盖 feed。main 继续形成 RBF3、FrameStore、VersionStore；是否移除 main 的 RBF1 读取与旧栈项目，需要明确修订格式/API 和现有阶段决策，并独立取得资格。本次没有修改这些合同或移除兼容代码。

本记录保存在当前 main 工作区，尚未提交；它不在指向历史源码的 baseline tag tree 中。baseline tag 的注释另存消费版本、来源和调查摘要，供以后独立查询。
