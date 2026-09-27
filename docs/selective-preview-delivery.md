# 首次按包发布：RbfSegmentStore 与 EventJournal

2026-09-28。发布源码 commit：`883f995847f9bc9b92801f4f5f1f0997f54ff7d7`；本记录在发布后补写，不属于两个包的源码 commit。

## 公开组合

| 包 | 公开版本 | 来源与用途 |
| --- | --- | --- |
| `Atelia.Primitives`、`Atelia.Data`、`Atelia.Rbf` | `0.1.1-preview.2` | 已发布旧包，来源 `976aa345f923da09e2a5cf1dc25ba592b3818b63`；本次没有新版本。 |
| [`Atelia.RbfSegmentStore`](https://www.nuget.org/packages/Atelia.RbfSegmentStore/0.1.2-preview.1) | `0.1.2-preview.1` | 新增 `ConfirmDurable` 等当前源码能力；[包专属标签](https://github.com/Atelia-org/atelia-storage/tree/Atelia.RbfSegmentStore-v0.1.2-preview.1)。 |
| [`Atelia.EventJournal`](https://www.nuget.org/packages/Atelia.EventJournal/0.1.2-preview.1) | `0.1.2-preview.1` | 新增不可变 tag；直接依赖上述新 `RbfSegmentStore` 与三个旧包；[包专属标签](https://github.com/Atelia-org/atelia-storage/tree/Atelia.EventJournal-v0.1.2-preview.1)。 |

初次尝试让新 `EventJournal` 只依赖四个 `0.1.1-preview.2` 时，真实包模式编译在 `_segments.ConfirmDurable(...)` 报 `CS1061`。旧 `RbfSegmentStore` 没有该 API；[tag 发布合同](EventJournal/immutable-tags-design.md)要求确认目标段且不轮转。随后先单独发布新 `RbfSegmentStore`，再单独发布新 `EventJournal`。这证实了[按包方案](selective-nuget-release-design.md)中的“按声明的最低依赖包编译”门槛确实能发现源码测试掩盖的错误。

## 构建、隔离消费与公开回读

在 Windows/.NET SDK 10.0.201，Release 源码构建成功；五个项目 777 个测试通过，0 失败/跳过。完整构建的 45 条 XML 文档警告与原基线相同。提交 `883f995` 的[常规 CI](https://github.com/Atelia-org/atelia-storage/actions/runs/36332318515)在 Windows 与 Ubuntu 均通过，包括旧五包同版打包及隔离 smoke。

本地分别从干净提交打出单包候选，并运行 `eng/Test-Package.ps1 -Project RbfSegmentStore` 与 `-Project EventJournal`。两次都只直接引用目标包；实际还原版本分别是“四包：三个旧版加新 SegmentStore”和“五包：三个旧版加新 SegmentStore/EventJournal”。前者验证 `ConfirmDurable`、关闭与只读重开，并校验 5 个新包源码 checksum；后者验证创建事件、不可变 tag、branch move、关闭与只读重开，并校验 17 个新包源码 checksum。旧包使用 nuget.org 的签名 nupkg，核对哈希、身份与原来源 commit。

[SegmentStore 发布任务](https://github.com/Atelia-org/atelia-storage/actions/runs/36332331898)和[EventJournal 发布任务](https://github.com/Atelia-org/atelia-storage/actions/runs/36332903984)均通过 Trusted Publishing、推送、公开下载、签名包资产核对及仅使用 nuget.org 的独立 `PackageReference` smoke。主线程另从两个 GitHub 任务下载候选与 manifest，在 Windows 用 `eng/Verify-Published.ps1` 对两个包各复验一次，均通过。公开 `EventJournal` 的 `project.assets.json` 实际闭包为 `Primitives/Data/Rbf 0.1.1-preview.2` 加 `RbfSegmentStore/EventJournal 0.1.2-preview.1`。

| 包 | GitHub 上传前候选 SHA256 | nuget.org 签名包 SHA256 |
| --- | --- | --- |
| `Atelia.RbfSegmentStore` | `0b7644c0a4eba4ef50de15b1f9e9f5c4309ddfa2b7a10eaebcd8c33739598245` | `ef070b92c88d3f5393a38006f25b1d4886b9ea336d43b3c2dc964e33bf17e28e` |
| `Atelia.EventJournal` | `645a94c21e60a0e31afa430f287ace6a664a019a067bd4addf189ae72a174c73` | `631d799d6b066ce8f60a854406a9f06ce9820403b538894b6f42c2da5817ee4d` |

nuget.org 会给上传包加仓库签名，因此不能要求上传前后整包哈希相等。本机 Windows 候选哈希也不同于 GitHub Ubuntu 候选哈希；本次不声称跨操作系统字节可复现。公开回读比较了 nuspec、LICENSE、README、DLL、XML 等内容及实际还原的旧包哈希；新包 PDB/本地源码 checksum 在发布前的隔离验证中检查。

## 旧数据见证与边界

在仓外全新 `storage-tag-compat-public-20260928` 中，旧 `EventJournal 0.1.1-preview.2` 创建 journal；仅从 nuget.org 还原的新 `EventJournal 0.1.2-preview.1` 严格打开并读取旧事件，创建 tag、移动 branch 后关闭重开，旧文件仍保留原字节前缀。旧 reader 的只读、严格可写和默认 recovery 打开均以未知 tag frame 明确拒绝，拒绝前后文件路径与 SHA256 不变。OldReader 实际还原五个旧版；NewReader 实际还原上述三旧两新组合。

本次没有迁移 `durable-graph` 或 `atelia` 的单一版本/来源 pin；它们仍须按[方案](selective-nuget-release-design.md)另行改为逐包固定。没有验证远端 Source Link 下载、硬件断电或消费应用全部历史数据；上述旧数据见证只覆盖实际构造的样本。
