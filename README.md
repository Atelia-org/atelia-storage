# atelia-storage

当前 HeadStore 合同已转为引用初始化 fork：新 fork 只发布准确来源 header，返回新 HeadId，不产生克隆 Entry；格式4统一拒绝1/2/3，HeadSetTag成员以16B分别保存所选Head及实际Entry owner。合同归[HeadStore文档](docs/HeadStore/README.md)，本轮实际源码、进程与包资格见[现有验收记录中的新增轮次](docs/HeadStore/12-versionstore-final-acceptance.md)，未验证范围不沿用旧Accepted。[重命名记录](docs/HeadStore/15-headstore-renaming-plan.md)及[格式2 HeadSetTag验收](docs/HeadStore/16-headset-tags-acceptance.md)继续保留历史来源。

紧凑Handle合同见[04](docs/HeadStore/04-versionstore-publication.md)：HeadId为4B上下文编码，本地编号上限0x00FFFFFF；跨库误用检测允许碰撞，GetLocalHeadId替代HeadId.LocalId。内部字段仍为raw local，完整128bit库身份保留在低频门/header；旧轮次资格不代表格式4已验证。

Atelia 的 .NET 10 存储基础库，使用 MIT 许可证。main 面向 **RBF3 / FrameStore / HeadStore 新栈**：当前底座为 `Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf`，另有独立的 `Atelia.Binary` 基元 codec（BCL + 纯托管 K4os.Compression.LZ4 `[1.3.8]`）；FrameStore 已实现[公开持久化闭环](src/FrameStore/README.md)，Inventory/Audit 同步物理检查已实现、已纳入本地候选包清单；HeadStore 已接通普通 Head 公开持久化闭环、有界历史读取、精确 fork 创建与声明分叉图查询，并提供tag/元信息与独立只读Audit；本轮tag改为捕获所选Heads当前位置的HeadSetTag，ReadTag交付固定成员表，Audit检查成员引用，以及多 Head 条件提交 `PublishHeads`（见 [实施计划](docs/HeadStore/12-versionstore-implementation-plan.md)）。

下游反馈驱动的公面增量也已实施：HS 精确 `ReadEntry`、组合 `EnsureUsable` 和惰性 `RecoveryReports`，FS 用户 `Length` 与 `Payload`/`TailMeta` 切片。获准队列已收束，分片源码与实际运行资格见[增量验收](docs/FrameStore-HeadStore/14-downstream-api-refinement-acceptance.md)；旧六包候选不含 CU 或这些 API，包交付另行验证。

RBF1 专属源码、测试、toolkit、工程脚本及设计资料已从 main 退役；main 的 `Atelia.Rbf` 只支持 RBF3。RBF1 维护与公开交付归 `RBF1` 分支，退役前完整 main 快照由 `archive/rbf1-main-2026-10-16` 标签保留，范围、历史来源和验证见 [RBF1 退役记录](docs/rbf1-retirement.md)。不在 main 建立第二份旧栈归档副本。

| 当前边界 | 底层与交付方式 | 入口 |
| --- | --- | --- |
| main | 仅 RBF3；pack 顺序 Primitives → Data → Rbf → Binary → FrameStore → HeadStore；Binary 不依赖前三包 | [RBF 接口](docs/Rbf/rbf-interface.md)、[格式](docs/Rbf/rbf-format.md)、[FrameStore 文档](docs/FrameStore/README.md)、[HeadStore 文档](docs/HeadStore/README.md)、[跨层决策与交付](docs/FrameStore-HeadStore/README.md) |
| RBF1 维护线 | 旧栈维护与公开发版按该分支及具体包来源 | [RBF1 分支](https://github.com/Atelia-org/atelia-storage/tree/RBF1)、[历史过渡与公开包来源](docs/rbf1-reference-transition.md) |

已公开的 `0.2.0-rbf1-preview.1` 五包属于 RBF1 系列，来源 commit 为 `3e9554e2ea70f769607e80b3fc11a85506050533`。旧消费者继续使用明确版本的 PackageReference 和[对应来源指南](https://github.com/Atelia-org/atelia-storage/blob/3e9554e2ea70f769607e80b3fc11a85506050533/README.md)；main 不提供 RBF1 读取、运行时桥接或格式迁移。存储包版本独立于消费者自己的版本。

## 构建与主线包验证

需要 Git、精确的 .NET SDK **10.0.201**、PowerShell 7；`global.json` 禁用 SDK roll-forward。CI 从同一文件安装 SDK，不需要原 atelia 仓或其两个 Analyzer 项目。Windows/Linux 使用相同入口，.NET 操作串行执行。Windows 极限偏移测试使用稀疏文件，TEMP 所在文件系统需要支持稀疏文件。

主线入口如下。RBF1 过渡阶段的 Windows Release 完整测试 1525/1525 与 W: 三包隔离消费仅是历史资格，见[过渡验收记录](docs/rbf1-reference-transition.md#6-验收记录)；当前退役改动的验证见[退役记录](docs/rbf1-retirement.md)。

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet test Atelia.Storage.slnx -c Release --no-build
# 先提交源码；从干净 HEAD 打包。不同内容必须使用新版本。
$version = "0.3.0-rbf3-dev.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
./eng/Pack.ps1 -Version $version -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version $version -FeedDirectory ./artifacts/feed -WorkDirectory "../storage-package-smoke-$version"
```

solution build/test 覆盖 main 底座、Binary、FrameStore 和 HeadStore；旧栈项目、冻结包配置及专属 assets 检查已随 RBF1 退役移除。

`eng/Pack.ps1` 是主线生产包清单和 pack 顺序的唯一入口，固定输出 Primitives、Data、Rbf、Binary、FrameStore、HeadStore 六包及对应 symbols/manifest，不提供项目选择参数。Pack 要求干净已提交树、明确 Version/OutputDirectory 和正确 origin；相同版本只能复用来源与 hash 匹配的产物，不覆盖 feed 中已有内容。旧栈 selective 发布和额外 segment smoke 的历史命令从 RBF1 分支及交付记录查阅。

`eng/Test-Package.ps1` 在尚不存在的仓外目录建立独立 NuGet cache/config 与 MSBuild 边界，四个独立 consumer 分别直接精确 PackageReference Rbf、Binary、FrameStore、HeadStore；检查 Rbf 三包、Binary + K4os 两包、FrameStore 五个 Atelia + K4os、HeadStore 六个 Atelia + K4os 的闭包与 public API。包 manifest、静态 Source Link 和本地源码 checksum 检查保留；源码测试不替代包消费，远端 Source Link、真实应用旧数据兼容和公开发布各自需要证据。验证 workspace 保留日志、assets 与 manifest。

候选容器规范化和确定性构建规则由 `eng/Pack.ps1` 维护；应区分候选 hash 与 nuget.org 签名后的公开包 hash。同平台重打包比较完整 nupkg/snupkg，不能只比较 DLL。当前准备本地六包候选及隔离消费，入口与实际结果见[本地交付记录](docs/FrameStore-HeadStore/13-local-package-delivery.md)，不执行公开发布；发布资格和参数仍需按具体提交另行验收。

消费仓日常使用自己的固定 PackageReference；显式源码联调只适用于匹配 main RBF3 合同的消费者。排障时先核对实际 assets、包 manifest 和来源 commit，再阅读对应源码。

## 文档与 Agent 入口

公共 binary codec 的规范见 [Bare Primitive Value](docs/Binary/bare-primitive-value.md)，实现入口为 [Atelia.Binary](src/Binary/README.md)：schema 驱动的基元、宽容 reader、精确尺寸与显式受控 Brotli/LZ4 block。源码/包资格单列于[实施验收](docs/Binary/bare-primitive-value-acceptance.md)。[Tagged Value](docs/Binary/tagged-value-intent.md) 仍仅为意向，优先评估 CBOR 等成熟标准。

新栈文档按 [FrameStore](docs/FrameStore/README.md) 和 [HeadStore](docs/HeadStore/README.md) 归类，[共同入口](docs/FrameStore-HeadStore/README.md)保留跨层决策与交付。[S1 公共尺寸计算与提前 ticket](docs/Rbf/01-rbf-sized-append.md)已 Accepted，源码资格见[阶段验收记录](docs/Rbf/01-rbf-sized-append-acceptance.md)；S2/S3 已取得[FrameStore 独立源码验收](docs/FrameStore/02-framestore-final-acceptance.md)，S4/S5及已实施专项取得[HeadStore 独立源码验收](docs/HeadStore/12-versionstore-final-acceptance.md)，两个新库已注册到主线包清单。V4-F已补具名内部进程窗口和剩余组合；新增CU / `PublishHeads`取得[独立源码验收](docs/HeadStore/07-versionstore-cross-ref-conditional-update-acceptance.md)，旧六包候选不包含该增量；S6-C/D本地候选与隔离包消费资格见[交付记录](docs/FrameStore-HeadStore/13-local-package-delivery.md)，公开发布与下游迁移另行进行。历史测试与当前增量分开记录，不以源码 Accepted 推定包已交付。

- 协作约束见 [AGENTS.md](AGENTS.md)；当前 RBF1 维护边界、归档与验收见 [退役记录](docs/rbf1-retirement.md)。
- 旧栈指南与 [EventJournal 历史设计](https://github.com/Atelia-org/atelia-storage/tree/043f77aa68700568499ceb965c049f8362e4253f/docs/EventJournal)保留在 Git 历史中，提供模型和事实背景，不是 main 新栈的规范权威。具体公开包以固定来源为准。
- 来源与原始提取范围见 [extraction-origin](docs/extraction-origin.md)。历史讨论保留原始语境，不自动成为当前活动入口。
- CI 与主线六包交付入口同步；`eng/Verify-Published.ps1` 核验六包来源及四个独立公开消费闭包，不能把本地候选消费当作公开源验证。仓库内不保存 token，网络发布须有明确会话授权。
