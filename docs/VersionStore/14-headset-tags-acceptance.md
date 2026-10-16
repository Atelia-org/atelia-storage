# HeadSetTag 格式2实施与独立验收记录

日期：2026-10-16。状态：**Accepted，限本轮Windows源码、实际进程窗口及本地六包隔离消费**。实现提交：`886439af5395e25ed2ee831885c3a00ae106e7d8`。本页记录本轮合同到源码/测试及实际运行证据的映射。既有VersionStore、CU、重命名和RBF1退役证据保留各自来源，不自动覆盖本片。

## 变更范围

- 公共API：CreateTag仅接受HeadId的ReadOnlySpan并返回HeadSetTag；ReadTag返回固定成员表；Info/Scan改用HeadSetTagInfo/HeadSetTagScanEnd；删除任意bytes/Related overload、ResolveTag、RootPayload、TagInfo和TagScanEnd。
- 持久格式：统一版本2，门及两类header均写2；拒绝版本1门及访问时混入的旧header。TagRecord改为12B成员项与time/count/name/MetaCRC，名称接受域和桶路由保持。没有双reader或原地迁移。
- 创建顺序：稳定捕获S，data确认，再按顺序确认去重必要依赖D，最后完整TagRecord输出；窄TagCreateActive覆盖hook重入，outcome三态按实际输出/确认窗口区分。
- 查询：唯一可信目标可隔离其他unknownName；ReadTag仅验证表，Info/Scan只验证meta；Audit增加成员精确引用检查，保留原全库健康、ForkOrigin真实主链与clone验证强度。
- 普通ReadEntry/Fork不新增全链扫描。它们消费既有来源协议、精确内容/尾Fence/CU资格；正常RBF3 writer marker-free，离线构造内嵌frame不作为正常过程反例扩大范围。
- 范围外：历史ID任意组合、发布并命名事务、覆盖/删除、掉电保证、确认缓存、DG历史图书签迁移；公开发布另需会话授权。

规范权威：[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[CU](07-versionstore-cross-ref-conditional-update.md)、[09 HeadSetTag](09-versionstore-immutable-tags.md)、[11 bytes](11-versionstore-root-payload-bytes-api.md)。决策与施工来源：[14方案](14-headset-tags-plan.md)。

## 合同与代码验收映射

| 验收边界 | 必须观察 | 测试/证据入口 | 当前状态 |
| --- | --- | --- | --- |
| 格式门与两类header | v2写入；v1门拒绝且不自动改写；v2门下旧Head/桶header按访问拒绝 | Format、Tags测试及公开错版镜像 | Windows通过 |
| canonical表/自然容量 | 非空、严格递增唯一非零ID、完整Packed ticket、精确12*n；meta count与真实T限制先于集合分配；大于1MiB合法表不沿用旧payload界 | Tags codec/capacity测试 | Windows通过 |
| 名称与元信息 | 1..1024 code units、lone surrogate、BPV另一合法表示/冗余前缀、固定1024桶及碰撞；time/count/name独立CRC | Tags与TagMetadataPublic测试、TagGoldenScenario | Windows通过 |
| 唯一可信目标 | unknownName在前/后，唯一可信目标仍成功；zero+unknown缺失不确定；重复可信目标失败；合法meta坏表保持占名 | TagPublic/TagMetadataPublic查询测试 | Windows通过 |
| public生命周期 | S只含实际选择，单成员合法；输入slice复制/后改不影响；后续发布、冷重开、成员exact读及自有集合 | [HeadSetTagBehaviorTests](../../tests/HeadStore.Tests/HeadSetTagPublic/HeadSetTagBehaviorTests.cs) | Windows通过 |
| CU子集与fork | 只选A但A来自A/B CU时D含A/B且S只含A；peer后续组不递归；fork子记录不折叠成源 | 同上、[HeadSetTagPublicationFaultTests](../../tests/HeadStore.Tests/HeadSetTagFaults/HeadSetTagPublicationFaultTests.cs) | Windows通过 |
| 稳定捕获与owner | 所有mutation重入返回TagCreateActive；selected/clock/dependencies/append/confirmed hook Dispose后不继续I/O；所有退出恢复guard | [HeadSetTagCaptureTests](../../tests/HeadStore.Tests/HeadSetTagFaults/HeadSetTagCaptureTests.cs) | Windows通过 |
| data/D确认与outcome | data→去重且排序Dflush→TagRecord；data/必要宿主/peer确认前后错误均NotAttempted；不缓存未选择peer；确认后异常仍Confirmed且冷读固定表 | [HeadSetTagPublicationFaultTests](../../tests/HeadStore.Tests/HeadSetTagFaults/HeadSetTagPublicationFaultTests.cs) | Windows通过 |
| Audit正式引用 | ReadTag能恢复表而成员缺失/损坏/未成立时，ReadEntry和Audit拒绝；不以Info健康替代成员资格 | [PublicAuditBehaviorTests](../../tests/HeadStore.Tests/AuditPublic/PublicAuditBehaviorTests.cs)、HeadSetTagBehaviorTests | Windows通过 |
| Audit取消/cleanup | tag阶段member/peer实际content读取取消；普通取消后owner可复用；取消叠加peer/member/bucket/cursor/cached/rootLock close失败仍取消优先、各资源一次且锁最后 | [HeadSetTagAuditResourceTests](../../tests/HeadStore.Tests/HeadSetTagFaults/HeadSetTagAuditResourceTests.cs) | Windows通过 |
| Audit成本/计数 | 连续两轮不增长永久cache；tag成员验证不重复增加EntryCount；失败不返回完整统计 | 同上 | Windows通过 |
| 普通精确读取边界 | 保留原无全链扫描的ReadEntry/Fork及CU exact判据；成员来源协议与Audit强检查分开 | [PublicEntryReadTests](../../tests/HeadStore.Tests/Public/PublicEntryReadTests.cs)、Forks、ConditionalUpdateIntegration及独立源码复核 | Windows通过 |

## 实际进程窗口

源码hook抛错、受控离线镜像与真实Process.Kill分别记录，不相互替代。父进程必须核对READY的PID/窗口/outcome，实际kill并等待退出，再启动fresh进程；RO前后比较文件清单/bytes，不以测试线程抛异常冒充进程中断。

| 窗口 | 实际测试入口 | 必须观察 | 结果 |
| --- | --- | --- | --- |
| dependencies-confirmed | [HeadSetTagProcessTests](../../tests/HeadStore.Tests/InternalProcess/HeadSetTagProcessTests.cs)、[Host](../../tests/HeadStore.Tests/InternalProcess/HeadSetTagProcessHost.cs) | data与必要CU宿主/peer/子Head已确认，TagRecord尚未输出；无具名半表，NotAttempted窗口事实与独立RBF观察相符 | Windows通过 |
| record-appended | 同上 | 完整TagRecord追加后未确认；旧调用Unknown，冷开按完整记录资格裁决，不倒推Confirmed | Windows通过 |
| confirmed | 同上 | 桶flush正常后尚未交付；捕获A/B CU中A及fork子，不把未选择B放入表；fresh RO/RW/RO exact读与Audit固定一致 | Windows通过 |
| tag-body-partial / tag-body-complete | [InternalTailProcessTests](../../tests/HeadStore.Tests/InternalProcess/InternalTailProcessTests.cs) | 真正短写后kill；partial截回、complete缺TailKey/Fence完成尾；格式2重验，RO拒残尾且不改文件，RW恢复后再次RO稳定 | Windows通过 |
| tag-private / tag-empty / tag-append | [InternalPublicationProcessTests](../../tests/HeadStore.Tests/InternalProcess/InternalPublicationProcessTests.cs) | 现有具名窗口迁新API；pending/合法空桶与正式成员表发布事实分开 | Windows通过 |
| public名称/路由golden | [PublicCombinationProcessTests](../../tests/HeadStore.Tests/SystemIntegration/PublicCombinationProcessTests.cs)、[TagGoldenScenario](../../tools/HeadStore.Smoke/TagGoldenScenario.cs) | 独立物理格式2表/meta读取；名称/routing固定向量与两个fresh冷读，成员exact值及文件不变 | Windows通过 |

以上沿ProcessCrashOnly，不证明原生设备掉电、flush遗漏必然丢失或任意OS/文件系统表现。平台结果填写实际运行环境，未运行的平台保持未覆盖。

## 主线程实际验证记录

| 资格 | 实际命令/来源/工件 | 结果 |
| --- | --- | --- |
| 独立源码复核 | 基线 `f856548`；主线程复核生产改动，独立 reviewer 检查新增模型/codec、创建/读取/Audit、故障资源边界和测试保真 | 通过，无阻塞项 |
| Release build | `dotnet build Atelia.Storage.slnx -c Release`；`artifacts/headset-tags/build-solution-final.log` | 0 warnings、0 errors |
| Release完整solution test | `dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/headset-tags/tests-final`；六份TRX与 `test-summary.json` | 3388 passed、0 failed、10 platform skips；HeadStore 1466 passed、6 skips |
| 新增故障/资源向量 | 同次完整测试中的 `HeadSetTagFaults`，含真实依赖flush前后故障、CU Unknown冷重开再确认、重入、Dispose与Audit取消/多级close失败 | 21/21通过；另有16个public与9个canonical向量通过 |
| 实际进程中断 | `artifacts/headset-tags/process-evidence/` 保存PID、READY、kill/exitCode及fresh验证；新窗口PID依次为39880、22988、43252，均killed=true、exitCode=-1 | 三窗口及迁移后的既有进程用例均通过 |
| Windows/Linux资格 | Windows / .NET SDK 10.0.201；本次未运行Linux；10项平台条件跳过不计作通过 | Windows通过；Linux未新增资格 |
| 本地六包候选 | `0.5.0-headset-local.20261016140613`；来源为干净的 `886439a`；manifest位于 `artifacts/headset-tags/feed/0.5.0-headset-local.20261016140613/` | 六包及symbols/manifest通过 |
| 四个隔离PackageReference消费者 | `W:/AteliaStorageHeadSetTag-package-0.5.0-headset-local.20261016140613`；Rbf三包、Binary+K4os、FrameStore五个Atelia+K4os、HeadStore六个Atelia+K4os；各consumer只直接引用一个顶层Atelia包 | 全部通过；169个本地源码checksum、PDB、静态Source Link与来源通过 |
| 公开发布/DG迁移 | 不属于本页默认完成范围 | 未进行 |

包smoke须覆盖集合tag、CU subset/pair、fork子捕获、两层冷重开与成员ReadEntry，并核对actual assets、来源与checksum。源码通过不替代包消费；旧版本/manifest/hash不覆盖重用。最终状态只能依据本表实际证据改写，不能将尚未执行的测试列成Accepted。

补充核验：源码消费 `.rename/HeadStorePackageSmoke.Source.csproj` 的 Release build/run 已通过（日志 `artifacts/headset-tags/build-source-consumer.log`、`source-smoke.log`），其 ProjectReference 证据不替代上表包消费。完整suite分项为 Primitives 75、Data 339、Binary 214、Rbf 856、FrameStore 438 + 4 skips、HeadStore 1466 + 6 skips。首次迁移测试暴露的六项版本期望/夹具/声明读取断言已修正，最终完整重跑全部通过，没有降低生产校验或删除有效旧测试来制造通过。

## 本地包复验与交付边界

实际从干净实现提交执行：

```powershell
./eng/Pack.ps1 -Version 0.5.0-headset-local.20261016140613 -OutputDirectory artifacts/headset-tags/feed/0.5.0-headset-local.20261016140613
./eng/Test-Package.ps1 -Version 0.5.0-headset-local.20261016140613 -FeedDirectory artifacts/headset-tags/feed/0.5.0-headset-local.20261016140613 -WorkDirectory W:/AteliaStorageHeadSetTag-package-0.5.0-headset-local.20261016140613
```

完整日志见 `artifacts/headset-tags/pack-final.log`、`package-consumers.log` 及隔离目录的 `logs/`。首次pack遇到Windows构建输出DLL复制的MSB3021访问拒绝，未生成正式manifest；保留partial stage后从相同干净提交重试成功，未改生产代码或覆盖已完成候选。包的固定来源始终为 `886439a`，后续验收文档提交不改变其身份。复验应在该来源的干净checkout使用新的仓外WorkDirectory，同版本仅可复用来源及hash完全相同的候选。

HeadStore包消费者实际验证双成员表、fork子身份、CU subset/pair、格式2 header、两层冷重开、成员ReadEntry与只读Audit。没有推送Git提交、公开发布NuGet包或修改下游pin；未测试远端Source Link下载、Linux新增资格及设备掉电。调用方按硬切API/格式2使用，不提供旧bytes/Related适配、双reader或数据迁移。
