# 多项目仓库的按包 NuGet 发布方案

状态：方案与落地记录；2026-09-27 提出，2026-09-28 用本仓首片公开发版和 `atelia-completion` 本地候选实测修订。本文供两仓分别落地；公开发布范围仍以当前会话授权为准。

## 目标与边界

一个仓库继续共同开发、构建和测试多个项目；每个公开 NuGet 包按自己的内容决定是否产生新版本。一次发布可以只含一个新包。消费方仍通过普通 `PackageReference` 从公开源还原，不需要检出兄弟仓库。已发布的同一包 ID/版本不以新内容覆盖，来源和公开包哈希可追溯。

这不是跨仓共享发布框架。两个仓库复用下面的规则和验收标准，各自保留当前打包、Source Link、签名、CI 与公开发布入口。源码测试仍覆盖整个受影响的项目图；按包选择仅决定哪些包生成新身份并推送。

## 方案提出时的现状（2026-09-27）

| 仓库 | 项目依赖 | 原有发布假设 |
| --- | --- | --- |
| `atelia-storage` | `Primitives`、`Data` 为底层；`Rbf` 依赖两者；`RbfSegmentStore` 还依赖 `Rbf`；`EventJournal` 依赖其余四者 | `eng/Pack.ps1` 用一个 `Version` 生成五包；`eng/Test-Package.ps1` 与 `eng/PackageMetadataCheck/Program.cs` 断言五包同版；`.github/workflows/publish.yml` 推送整个 manifest。README 也承诺同一存储版本。 |
| `atelia-completion` | `Diagnostics` 和 `Completion.Abstractions` 无内部依赖；`Completion` 与 `Completion.Tools` 都依赖这两者，彼此无依赖 | `eng/Pack.ps1` 用一个 `Version` 生成四包；`eng/Test-Package.ps1` 的三种消费者都按同版验证；`.github/workflows/publish.yml` 推送整个 manifest，并要求 `v<version>` 标签。 |

两仓日常源码开发均用 `ProjectReference`。`dotnet pack` 会把项目引用表示成包依赖，而非将被引用项目的程序集自动打进当前包。[dotnet pack 文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-pack)。因此“只从循环里去掉未变项目”不成立：新包可能引用不存在的同版依赖，或者用当前源码编译、却声明兼容旧包。生产包清单仍以各仓的 `eng/Pack.ps1` 为准，项目文件只提供依赖边。

真实下游也有同版假设：`durable-graph` 的 `eng/StorageDependency.props`、`eng/Prepare-Storage.ps1` 与多个直接 `PackageReference` 共用一个 `StoragePackageVersion`，准备脚本还要求五包同版同来源；`atelia` 的 Storage/Completion 依赖属性及多个直接包引用也分别共用单一版本。发布方支持混合版本，不等于这些消费仓已经能采用它。

## 发布合同

1. **身份以包为单位。** 包 ID + 版本唯一确定公开产物。版本号无需跨包对齐。人工决定要发布的包及各自的新 SemVer 版本；文档变动、仓库提交和自动文件差异都不直接决定包版本。[.NET 库版本指导](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/versioning)。
2. **人工输入只描述本次新包。** 对每个新包给出 ID、新版本及仓内各直接依赖的最低版本。先用既有公开依赖对目标项目做包模式编译；若它调用了旧包没有的 API，就把对应依赖纳入待发范围，并沿依赖方向复验，不能只按最初想发布的项目或 Git diff 猜测范围。新版本此前不得发布；对于公开发布，未在本次新发的依赖版本必须已在 nuget.org 可用。旧包原有的依赖合同不可改写。某个入口实际解析到的完整闭包由该入口的 restore 生成，不存在一份适用于所有消费者的“全仓目标版本图”。包文件、nuspec、哈希和来源 commit 是生成的证据，不是第二份可手改决策。
3. **直接依赖下限必须经过编译验证。** 每个新包的 nuspec 中，仓内直接依赖的下限由上述输入决定，默认不设上限或精确锁定。发布构建针对所声明的包版本编译，并核对编译时 `project.assets.json` 中的直接依赖版本恰为所声明的下限；传递约束悄然抬高它，或出现 downgrade，均应修正计划后重试。若必须使用某个新依赖 API，就先打对应新包，或提高到已经公开的版本。NuGet 通常按最低适用版本还原，但具体入口的闭包还受其他依赖约束影响。[NuGet 版本范围](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning)；[依赖解析规则](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution)；[.NET 库依赖建议](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/dependencies)。
4. **底层变更不自动牵连上层。** 当上层旧包仍能按其原依赖合同工作时，它保留旧版本；若消费者只更新上层包也必须获得底层修复，则上层要发布新包并提高依赖下限。改变上层公共合同、实际需要新底层 API 或旧版本组合存在运行时错误，也要更新相应上层包。不能把 NuGet 的传递依赖当作自动升级机制。
5. **保留现有来源与字节约束。** 从干净的已提交源码打包，按仓库现有 SDK/Source Link 规则验证新产物；旧包按其已发布字节及原来源证据复用，不能拿当前 HEAD 重建旧版本。本机预验候选只能证明该次本地构建；发布 workflow 必须对自己即将上传的冻结候选重新完成包消费验证，并核对上传前哈希，不以相同 commit/SDK 推定跨机器候选字节相同。nuget.org 自动加仓库签名后的下载包是另一字节阶段，另记哈希，不要求两个 ZIP 哈希相等。包版本与程序集版本、磁盘格式版本是不同身份，分别核查兼容性。[签名包说明](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference)；[NuGet 推送 API](https://learn.microsoft.com/en-us/nuget/api/package-publish-resource)。

一次只发 `EventJournal` 的输入形状（示意，不规定文件格式）：

```text
publish Atelia.EventJournal <新候选版本>
  requires Atelia.Primitives >= 0.1.1-preview.2
  requires Atelia.Data >= 0.1.1-preview.2
  requires Atelia.Rbf >= 0.1.1-preview.2
  requires Atelia.RbfSegmentStore >= 0.1.2-preview.1
```

只发无内部依赖的 `Data` 时，输入只有它的 ID 和新版本。`EventJournal` 旧包仍按自己的旧 nuspec 解析依赖；要让只引用旧 `EventJournal` 的消费者取得新 `Data`，须由消费者显式引用新 `Data`，或发布提高下限的新 `EventJournal`。`atelia-completion` 同理，且单发 `Completion.Tools` 不需要决定无依赖边的 `Completion` 版本。CLI 参数或一次性 JSON 均可承载输入；每仓实施时只保留一种规范输入，不另建长期维护的“最新版本目录”。

## 最小实现路径

1. **先支持单包候选，再按真实依赖顺序交付。** 对较短的依赖链且没有批次同时可见要求时，采用连续单包发布：底层候选通过隔离验证并公开可还原后，再让上层新包声明并编译该公开版本。`atelia-storage` 首片原计划只打 `EventJournal`；针对旧版 `RbfSegmentStore 0.1.1-preview.2` 的实际编译在 `ConfirmDurable` 处失败，因此先发新 `RbfSegmentStore`，再发新 `EventJournal`，三个未变包沿用旧版。`atelia-completion` 可独立以 `Completion` 或 `Completion.Tools` 验证同一合同。先沿用各仓 `eng/Pack.ps1` 的生产包清单，只选择其子集；日常仍做整仓源码构建与受影响测试，不先建设任意 DAG 调度器。
2. **发布构建使用声明的包依赖。** 日常项目继续使用 `ProjectReference`；仅打包模式让新包的仓内直接依赖解析为 `PackageReference`。发布构建与源码构建使用隔离的 `obj`/缓存，或明确重新 restore 并核验 assets，防止两个引用图共用旧资产。旧依赖从公开源取得并冻结；按依赖顺序先发布已验证的新底层包，再从公开源取得它供上层构建。若将来确需同批打包未公开的新依赖，须另行实现候选 feed 与中断续跑验证，不能假称现有单包脚本已支持。
3. **生成逐包证据。** 对新包保存 ID、版本、源码 commit、SDK、上传前 nupkg/snupkg 哈希与直接依赖下限；对参与验证的旧包保存实际取得的公开包哈希、元数据中的原来源，并复用既有验收记录。来源记录不可得时如实标注，不用当前 HEAD 冒充旧包来源。若记录本地预验候选哈希，应明确它不是上传字节；必须归档发布 workflow 实际验证并推送的候选哈希。生成的证据核对输入和 nuspec；公开回读再单独记录签名后包哈希，并核对身份、仓库 commit 与包资产，不能直接比较两个阶段的整包哈希。
4. **按新入口隔离验收。** 用新候选包与冻结的公开旧包组成固定 feed，在新目录与新 NuGet 缓存中验证。每个新入口都要有仅直接引用该包的 `PackageReference` 探针，检查实际 `project.assets.json`、仓内闭包、包身份/哈希并编译；复用相关公开 API 行为 smoke。`Completion` 与 `Completion.Tools` 的现有联合 smoke 不能代替各自的单入口探针。新包继续按对应源码 commit 做当前 Source Link/PDB 验证；旧包不在每次发布时重新检出历史源码。存储旧数据或 Completion 行为兼容承诺另做对应见证。
5. **推送与公开可还原分开判断。** workflow 校验候选证据与字节，每次仅推本次新包。`push` 成功后 nuget.org 仍可能在验证和索引；用有界等待从公开源下载签名包、核对内容与实际还原闭包，再开始依赖它的上层发布。[nuget.org 发布与索引](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package)。等待超时属于“已推送、公开回读未完成”；保留 workflow 上传的候选、manifest 和推送记录，之后单独重跑只读的 `Verify-Published.ps1`，不重启含 `push` 的 workflow，也不重打同版。若将来同批推多个新包，按依赖顺序处理，发生部分成功时逐包确认公开身份后只继续未完成项，不用 `--skip-duplicate` 隐藏冲突。

`atelia-completion` 在方案提出时的 workflow 用 `v<version>` 标签绑定单一版本。按包模式现使用含包 ID 与版本的标签，例如 `Atelia.Completion.Tools-v0.1.1-preview.1`；旧共版标签保持原义。`atelia-storage` 的新版本固定文档链接使用包专属标签或明确 commit，不能让一个 `v<version>` 暗指全仓同版。

下游迁移单列验收：`durable-graph` 的直接包引用、`Prepare-Storage.ps1` 和 receipt/manifest 同版同来源断言，以及 `atelia` 的单一 Storage/Completion 版本属性、准备脚本和直接引用，都需要按实际包 ID 选择版本并记录来源。第一片先证明发布方与独立消费者；只有完成这些消费仓的正常 build/test 和包消费验证，才宣称它们已支持混合版本。

## 验收场景

| 场景 | 必须看到的结果 |
| --- | --- |
| 只改 `EventJournal` 或 `Completion.Tools` | 只生成/推送目标包新版本；其独立入口还原到声明下限的旧底层包；其余包无新版本。 |
| 新包使用新版底层 API | 针对旧公开包的发布构建失败；先验证并发布底层新包，再用该公开版本构建上层，nuspec 下限与实际编译包一致。 |
| 底层包单独修复 | 只发底层新包可成功；仅引用旧上层的入口仍可能还原旧底层。要求它自动获得修复时，显式发布上层新包。 |
| 混合版本与重复候选 | 独立入口验证实际闭包；缺包、错误下限、错哈希、旧版重建或无法核对的同 ID/版本冲突均拒绝发布。 |
| 推送成功但公开源暂时不可下载 | 记录已推送状态并等候公开回读；超时后只读核验，不重打或盲目重复推送。 |

## 暂不纳入

不拆仓，不构建跨仓通用发布服务，不自动从 Git diff 推断 SemVer，不自动给所有反向依赖涨版，不改变现有公开包字节，不借本方案承诺磁盘格式或旧数据兼容。已验证的是连续单包构建、发布和验收；同批多包候选、调度与续跑自动化等出现真实需求时再实现。

本仓首次实战的版本、公开包哈希、两次发布任务、混合依赖闭包和旧数据见证记录在[按包预览版交付记录](selective-preview-delivery.md)。

`atelia-completion` 已在本地实现四个项目的单包候选、隔离消费与按包发布 workflow，并保留旧四包同版路径。四个单包候选各自通过独立 `PackageReference` smoke；旧四包路径也通过 Pack 与 smoke。公开回读脚本针对现有 `0.1.0-preview.3` 四包完成只读演练；该演练使用去签名的公开包作为合成候选，未替代未来新版本的实际上传验证。本次没有在 completion 仓公开新包。
