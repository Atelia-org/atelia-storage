# S6-C/D：主线本地候选包交付

日期：2026-10-11。实施基线：`252ba77`。用户已明确转入包交付，先供 `Atelia-org/atelia-storage-2` 试用；公开 nuget.org preview 和真实消费者迁移另行进行。本记录不重标既有[源码验收](../VersionStore/12-versionstore-final-acceptance.md)和进程实验。

## 包身份与兼容边界

本轮保留 NuGet 包 `Atelia.Rbf`、程序集 `Atelia.Rbf.dll` 和 namespace `Atelia.Rbf`。文件格式编号由文件 Header 决定：当前实现只新建/写入 RBF3，RBF1 只读并保留旧 ticket，未知 Header 拒绝；这不表示旧 RBF1 writer 能消费 RBF3，也不表示冻结 EventJournal/RbfSegmentStore 能直接升级到 main。格式合同以 [RBF 接口](../Rbf/rbf-interface.md)为准。

wire-format、PackageId、AssemblyName 和 namespace 是不同的边界。包版本用于明确选择交付物，格式 Header 用于裁决磁盘内容。Microsoft 建议对重大重写考虑新包，并非要求每次格式变化都改名；程序集改名自身也是二进制兼容变化。仅改 Rbf 的包名/DLL 名不能完成两栈共存，重复 namespace/type 和共享 Data/Primitives 的版本/API 仍须另行解决。[.NET breaking changes](https://learn.microsoft.com/dotnet/standard/library-guidance/breaking-changes)、[版本指南](https://learn.microsoft.com/dotnet/standard/library-guidance/versioning)。用户已选择先保留名称开展本地试用。

本地候选使用新的 `0.3.0-rbf3-local.<唯一编号>` 预发布版本。版本标签说明当前交付线，不能替代读取格式校验；API/格式兼容承诺以具体合同和来源 revision 为准。NuGet 预发布标签只表达预发布身份，不自动建立兼容保证。[NuGet 版本规则](https://learn.microsoft.com/nuget/concepts/package-versioning)。每次 API 或内容调整生成新版本，不覆盖旧文件或缓存。

## 生产清单与验收闭包

`eng/Pack.ps1` 是生产注册及 pack 顺序唯一入口：Primitives → Data → Rbf → Binary → FrameStore → VersionStore。同一候选六包使用同一显式版本；这不是要求消费者自己的包也采用该版本。main 的发布 workflow 只接受此 All 集合。EventJournal/RbfSegmentStore 不打包、不发布，保留冻结测试与精确 RBF1 参考依赖；其维护发布归 RBF1 分支。

| 独立 consumer 直接引用 | 实际批准闭包 |
| --- | --- |
| `Atelia.Rbf` | Primitives、Data、Rbf |
| `Atelia.Binary` | Binary、K4os.Compression.LZ4 1.3.8 |
| `Atelia.FrameStore` | Primitives、Data、Rbf、Binary、FrameStore、K4os 1.3.8 |
| `Atelia.VersionStore` | 六个主线包、K4os 1.3.8 |

四个 consumer 均只直接引用一个 Atelia 包，使用精确 `[候选版本]`。打包使用 SDK 标准 ProjectReference 依赖范围；同候选闭包通过真实 assets 和缓存 hash 验证，未来混版兼容未由本轮证明。调用方若主动覆盖传递依赖，需要重新核对完整闭包。

隔离验证在仓外新目录运行，独立 NuGet cache/config 与空 MSBuild 导入边界，Atelia 包只能从本地 feed 还原。检查六包 manifest、源 revision、nupkg/snupkg hash、nuspec 直接依赖、程序集身份、XML、portable PDB、静态 Source Link 和本地源码 checksum。不会以 ProjectReference 或仓内 build 成功代替包消费。

FrameStore consumer 验证三种追加、提前地址、交错 Builder、确认/归档/冷重开、两种地址 codec 和 Inventory/Audit。VersionStore consumer 验证 RootMap/不透明 bytes、发布/历史/精确 fork/声明图、tag/元信息/扫描、冷重开/只读 Audit 和实际 data 读取。它们不重复压力或进程中断矩阵。

## 试用方法

候选实际路径、版本、源 revision 和验证结果在本篇末尾记录。下游只需直接引用 `Atelia.VersionStore`；仅使用 FrameStore 时直接引用 `Atelia.FrameStore`。不要同时带入旧 EventJournal/RbfSegmentStore。

```xml
<PackageReference Include="Atelia.VersionStore" Version="[实际候选版本]" />
```

将本地 feed 的绝对目录加入下游 NuGet.Config；使用 source mapping 将本次所用的六个 Atelia ID 指向本地 feed。下游若有其他 Atelia 包，应为它们保留自己的源，不要把整个 `Atelia.*` 前缀重定向到只含六包的目录。K4os 1.3.8 从既有公共源还原。显式固定版本，每轮更新核对 `project.assets.json`；仍需 .NET SDK 10.0.201。

```powershell
./eng/Pack.ps1 -Version <新候选版本> -OutputDirectory <本地feed目录>
./eng/Test-Package.ps1 -Version <同一版本> -FeedDirectory <同一feed目录> -WorkDirectory <尚不存在的仓外目录>
```

征求下游反馈重点是 API 是否能自然表达实际流程、Result/异常及 PublicationOutcome 是否易于正确消费、bytes/data 的生命周期和历史/fork/tag 组合是否满足需求。已记录的性能观察不能替代下游工作负载测量。反馈调整后再生成新本地版本；nuget.org 发布、OIDC policy 配置及公开包签名回读未在本轮执行。

## 本轮结果

**S6-C/D Accepted（本地候选及限定两平台隔离消费）。** 候选版本 `0.3.0-rbf3-local.20261011081109`，包来源 commit `72d32709bb15ae2f7984f3f42632269d5ffca7b2`；后续记录提交不是该候选的来源，也不覆盖或重新生成这一版本。

- 本地 feed：`E:/repos/Atelia-org/atelia-storage/artifacts/feeds/0.3.0-rbf3-local.20261011081109`。包含六个 nupkg、六个 snupkg、hash/source manifest。
- 可转交的 zip：`artifacts/feeds/storage-trial-0.3.0-rbf3-local.20261011081109.zip`，包含同一六包及 symbols/manifest、可搬迁 NuGet.Config 和试用说明。
- Windows Release solution 构建 0 error、41 项既有 XML warning；完整源码回归 **3455 passed / 9 platform skips / 0 failed**，7 个冻结参考项目仍使用精确 RBF1 包。metadata helper 单独构建 0 error、0 warning。
- 四个 consumer 消费入口的纯 mock 通过 102 个错误 assets、18 个错误 manifest、10 个旧参数拒绝及 source-only 复制、不变复用、失败清理/环境恢复等守卫。这是脚本控制流证据，与实际包消费分开。
- Windows 在 `W:/storage-package-smoke-0.3.0-rbf3-local.20261011081109` 的新隔离 workspace/cache 实际消费六包；四个 consumer 全通过，源码 checksum 总计 **164**。FrameStore 为 4 用户帧/3 文件；VersionStore 为 4 refs/7 snapshots/2 forks/2 tags/5 data 帧。
- WSL2 Ubuntu 24.04.4 在 ext4 的全新检出与仓外 cache 消费**同一份 Windows 候选**，四个 consumer、六包 metadata、164 源码 checksum 与 3/2/6/7 个包的实际闭包均通过；未做 Linux 重打包或同平台确定性重打包。Linux 源文件使用对应 Windows 构建的原始 bytes 并验证 Git tree/clean 状态，三份既有 CRLF 文件保留在准备 receipt 中。

首次候选 `0.3.0-rbf3-local.20261011080835`（来源 `bdd544f`）的 FrameStore smoke 失败：归档真实位于 `archive/000000/*.rbf`，fixture 只枚举直接文件。修正 consumer 的递归检查后新提交、新版本，生产存储实现未改；旧候选和失败日志保留，不能用它们冒充最终通过。最终候选由主线程与独立 reviewer 核对，实际 hash、日志、assets 和来源见[机器证据](evidence/2026-10-11-local-package-delivery.json)。

下游直接引用示例：

```xml
<PackageReference Include="Atelia.VersionStore" Version="[0.3.0-rbf3-local.20261011081109]" />
```

feed 中另附可通过 `dotnet restore --configfile <feed>/NuGet.Config` 使用的源配置；同目录路径可搬迁，六个确切 Atelia ID 映射到该 feed，其余包保留 nuget.org。该交接配置另在全新私有 cache 做过真实 VersionStore restore，实际七包闭包及六个 Atelia 包的 hash 与 manifest 相符。它适用于独立试用，合并进已有多源配置时保留下游自己的源约束。

若从源码仓复验既有候选，`Test-Package.ps1` 要求干净 checkout 与 manifest 的来源 revision 完全一致，应使用对应 commit 的隔离检出。下游直接消费 feed 不需要源码仓 checkout；后续文档提交不改变已有包的 provenance。

下一步是收集 storage-2 的真实 API/错误/恢复用法反馈，再决定是否修改 API 并生成新的本地版本。公开 nuget.org 上传、六包 Trusted Publisher policy、签名包回读、远端 Source Link checksum/下载以及真实消费者切换均未执行；本地 Accepted 不授予这些资格。三份既有 CRLF 工作树文件还需在公开交付前规范化并重新打包验证远端源码 bytes，当前本地 Source Link 检查不能替代该步骤。
