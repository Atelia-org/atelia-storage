# atelia-storage 协作入口

- 当前 HeadStore 重命名状态与验证见 [重命名记录](docs/HeadStore/15-headstore-renaming-plan.md)。既有 VersionStore 验收与包身份保持历史来源；[HeadSetTag方案](docs/HeadStore/16-headset-tags-plan.md)已实施并通过Windows源码、进程及本地包验收：统一格式2拒绝格式1，删除任意bytes/Related API；当前合同见09，实际资格见[16验收记录](docs/HeadStore/16-headset-tags-acceptance.md)，未验收项不得沿用旧Accepted。

使用简体中文交流，保留技术标识符原文。先读根 [README](README.md)，新栈任务按模块进入 [FrameStore 文档](docs/FrameStore/README.md)、[HeadStore 文档](docs/HeadStore/README.md) 或 [RBF 规范](docs/Rbf/rbf-interface.md)；跨层边界与交付见[共同入口](docs/FrameStore-HeadStore/README.md)。旧栈任务应切换到 RBF1 维护分支；归档与历史来源见 [RBF1 退役记录](docs/rbf1-retirement.md)。

- main 面向 RBF3 / FrameStore / HeadStore 新栈，当前底座为 Rbf/Data/Primitives，另有独立 Binary codec（BCL + 精确 K4os.Compression.LZ4 `[1.3.8]`）；FrameStore 已实现公开持久化闭环（见 src/FrameStore/README.md），Inventory/Audit 已实现，S2/S3 独立源码验收已 Accepted（见 docs/FrameStore/02-framestore-final-acceptance.md），已纳入本地候选包清单；FrameAddress 的变长 codec 复用 Binary，NamedFrameMap 设计采用变长地址，固定 12B 仍用于预留回填；HeadStore 已接通公开工厂、普通 Head 创建/发布/发现与冷重开；历史读取、精确 fork 创建与 ListHeadOrigins 声明图查询已接通，本轮CreateTag/ReadTag改为HeadSetTag固定成员表，ReadTagInfo/ScanTags改用HeadSetTagInfo，冷重开与新增Audit成员验证已取得本轮Windows资格，独立完整校验 Audit 只允许只读 owner；S4/S5及已实施专项已取得[独立源码验收](docs/HeadStore/12-versionstore-final-acceptance.md)，V4-F已补具名内部进程窗口和剩余组合，原source-only队列收束；新增 CU / PublishHeads 已实施并取得[独立源码验收](docs/HeadStore/07-versionstore-cross-ref-conditional-update-acceptance.md)，旧六包候选不含该增量；S6-C/D进入本地六包候选与隔离消费，结果见[交付记录](docs/FrameStore-HeadStore/13-local-package-delivery.md)；公开发布与下游迁移另行进行；续接见 [实施计划](docs/HeadStore/12-versionstore-implementation-plan.md)。身份、格式与 writer/recovery/Dispose 语义由当前 RBF 规范和测试约束；不要引入 atelia 业务依赖或其 `Analyzers.Style` / `Analyzers.Style.CodeFixes`。
- main 仅支持 RBF3。EventJournal/RbfSegmentStore、toolkit、旧栈测试及 RBF1 底层只读兼容已退役；旧栈维护和发布归 RBF1 分支，完整退役前快照保留在 `archive/rbf1-main-2026-10-16` 标签。不得为旧消费者重新加入 RBF1 适配；范围与验证见 [退役记录](docs/rbf1-retirement.md)，原设计/验收记录保留历史身份。
- 主线生产包及 pack 顺序只在 `eng/Pack.ps1` 定义，当前阶段 Primitives → Data → Rbf → Binary → FrameStore → HeadStore，Binary 不依赖前三包。不同内容用不同版本，不覆盖 feed 中既有版本；包版本独立于消费者自己的版本。
- 修改前检查 `git status`，保留已有工作。Windows 下 build/test/pack 串行；先 build Release，再用匹配配置 `dotnet test Atelia.Storage.slnx -c Release --no-build`。
- 当前 solution 仅包含主线源码与测试；验证实际 assets 不带入 RBF1 冻结包或旧栈项目。
- 主线包交付必须运行 `eng/Test-Package.ps1` 的四个隔离 public PackageReference smoke，检查 Rbf 三包、Binary + K4os 两包、FrameStore 五个 Atelia + K4os、HeadStore 六个 Atelia + K4os 闭包；consumer 仅直接引用各自的一个 Atelia 包。源码测试不替代包消费证据。该验证检查静态 Source Link 与本地源码，远端下载和消费应用旧数据兼容性需另有实证。旧 selective/额外 segment smoke 命令从历史标签或 RBF1 分支查阅。
- `eng/Pack.ps1` 要求干净已提交树及明确 Version/OutputDirectory；先完成源码评审/测试，再依当前会话授权提交和打包。网络发布依当前会话授权与账号能力进行，不把本文件视作额外授权。
- 为消费方排障时核对实际包 assets、manifest 和来源 revision，阅读对应 commit 源码。其 `UseStorageSources=true` 联调入口是显式选项；日常包消费不能自动跟随兄弟仓当前 HEAD；RBF1 消费者不得借源码联调转为 main RBF3 依赖。
- 当前仓的提取来源见 [docs/extraction-origin.md](docs/extraction-origin.md)。历史设计链接按原始来源保留，不以修历史链接为理由扩大运行时改动范围。
