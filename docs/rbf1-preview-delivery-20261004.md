# RBF1 维护系列首次公开交付

日期：2026-10-04。五包版本：**`0.2.0-rbf1-preview.1`**。发布来源：`3e9554e2ea70f769607e80b3fc11a85506050533`；不可变 tag：`v0.2.0-rbf1-preview.1`；维护分支：`RBF1`。
本记录在发布后补写，不属于包的源码 commit。

## 范围与身份

基线是 Galatea/SessionJournal 已消费的 `5288bd55b4a5181942dadd76516f799002508144`。发布准备仅修改 README、CI 和公开回读脚本，没有修改五库运行时 C# 源码。继续使用 RBF1 帧格式和 EventJournal/SegmentStore v2 目录协议；已采用 v2 的消费数据无需再次迁移。它不自动打开旧 v1 journal 目录。
包名、namespace 和 assembly identity 保留，使用独立 `rbf1-preview` 版本系列；消费者明确固定版本，不跟随 Storage main/RBF3。

| 包 | 公开版本 |
| --- | --- |
| [Atelia.Primitives](https://www.nuget.org/packages/Atelia.Primitives/0.2.0-rbf1-preview.1) | `0.2.0-rbf1-preview.1` |
| [Atelia.Data](https://www.nuget.org/packages/Atelia.Data/0.2.0-rbf1-preview.1) | `0.2.0-rbf1-preview.1` |
| [Atelia.Rbf](https://www.nuget.org/packages/Atelia.Rbf/0.2.0-rbf1-preview.1) | `0.2.0-rbf1-preview.1` |
| [Atelia.RbfSegmentStore](https://www.nuget.org/packages/Atelia.RbfSegmentStore/0.2.0-rbf1-preview.1) | `0.2.0-rbf1-preview.1` |
| [Atelia.EventJournal](https://www.nuget.org/packages/Atelia.EventJournal/0.2.0-rbf1-preview.1) | `0.2.0-rbf1-preview.1` |

消费方只需直接引用 `Atelia.EventJournal`，其传递依赖还原同版五包。当前 v2 消费项目的 `StorageStrictTailOpen` 为 true；不引入新的业务依赖。

## 验收与发布

- 本地 .NET SDK `10.0.201`，Release build 成功，45 条既有 XML 警告、0 error；源码测试 **1,034 passed / 0 failed / 0 skipped**。
- 干净准备提交打出独立 dev 预检包；`eng/Test-Package.ps1 -AdditionalSegmentSmoke` 验证 EventJournal 与直接 SegmentStore 的独立 public PackageReference、五包资产/来源/PDB 和 90 个本地源码 checksum。
- [准确发布 SHA 的 CI](https://github.com/Atelia-org/atelia-storage/actions/runs/37170973994) 在 Windows、Ubuntu 均成功，包含 build/test/pack/隔离两个 smoke。
- [正式发布任务](https://github.com/Atelia-org/atelia-storage/actions/runs/37171412733) 只 dispatch 一次，完成 OIDC Trusted Publishing 登录、五包上传和公开回读。未覆盖任何已有版本或移动旧 tag。
- `Verify-Published.ps1 -Project All` 对五包逐个运行 `dotnet nuget verify --all`，核对仓库签名、ID/version/source commit、候选与公开 nuspec/LICENSE/README/DLL/XML 资产，并从仅 nuget.org 的隔离 cache 还原运行 EventJournalSmoke。
- 独立消费实际还原五包同版，并逐包核对缓存签名包 SHA-256；公开报告 sourceRevision 与候选 manifest 都等于 `3e9554e`。

本机归档位于 `artifacts/rbf1-release-37171412733-1/`，下载的是正式 Actions 候选，未重新 Pack 冒充正式字节。其中包含 candidate manifest、`verified-packages.json`、`published-check.json`。NuGet 仓库签名会改变整包哈希，候选和公开哈希分开记录。

| 包 | 正式候选 SHA-256 | 公开签名包 SHA-256 |
| --- | --- | --- |
| Primitives | `2a2fd0e0e3d1099874444c0efa5d17e08dff7aa790886476c01c7b53ea63611e` | `84213f5a08135ac4155b5325b6024c80ddf14ad95f75f2826da6c62dd4f54bac` |
| Data | `86274b0dabb3c2d3b70ac18a856dd46eadad6482833eea90d5030feee65ece8e` | `3388c785de06403322212edfb256ef2dab2fe27eda3fc7849b5e111302eb237b` |
| Rbf | `bb5056f6c1f314695a4c1430f81c3b6eb01ab1596314ed758d802d1599516994` | `da52d3ba35ccaf7b0c90ae79ee13acedf92b98ef0a99bec27124ce62251a9358` |
| RbfSegmentStore | `2dc02ccbed4a9b000869216a6ca0ac7220a31134699c28a823559aa4f09f52f0` | `69f77cf91c9419d8c7a19df9d8d9c9a520654e42ded9b4e3c75351acb6c1bed1` |
| EventJournal | `744e53c3c45aed7d7b9065b01b563cab4207f21a1de3dd6a04adfd06a2241f71` | `6377794b4b7582194edf6e305da2f6276334012e678b3cd18e40876c7a085c3d` |

源码、两平台 CI、候选和公开包消费取得上述资格。消费应用的切换/回归结果由 atelia 仓 `docs/Galatea/storage-rbf1-public-upgrade-20261004.md` 单独记录；本记录不冒称实际服务重启、硬件断电或全部应用历史数据审计。静态 PDB/local source 校验也不冒称远端 Source Link 下载已验收。
