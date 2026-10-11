# VersionStore 分片实施与续接

更新：2026-10-11。本文是实施导航，不增加设计权威。行为以 [S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)、[08](08-versionstore-record-metadata.md) 至 [11](11-versionstore-root-payload-bytes-api.md) 为准；[S6](06-integration-and-delivery.md) 管组合与交付。[07 CU](07-versionstore-cross-ref-conditional-update.md) 仍是未并入主线的候选。

## 调度原则与整体结构

每轮先看 git 状态、当前源码及本文完成项，保留其他工作；选择能独立验证的一条路径。实施者可以调整内部表示和助手，不能把缺少实现重新扩展成已关闭的设计问题。若出现合同反例，记录具体失败轨迹与受阻分片，再调整计划。不要用 `NotImplementedException`、伪成功或 public 裸句柄导入填满候选 API。

核心只发布不透明 bytes。应用可显式调用 `RootMapCodec.Encode/Decode`，也可使用 JSON 或自己的格式；普通 ref、fork、历史、tag 和未来 CU 都不能调用 RootMap decoder 来判发布健康。值和结果不依赖 owner：RefId 包含持久 VSID，RefRevision 包含完整 RefId/ticket，RefBytesSnapshot 拥有 GC 数组。字段 codec 只证明格式，不能自行签发来源或主链资格。

建议维持当前窄分层：

| 位置 | 责任及依赖 |
| --- | --- |
| `RefId` / `RefRevision` / `RefBytesSnapshot` / `PublicationOutcome` | 公共值与自有结果；可信内部构造只保存已资格化字段 |
| `Internal/Format` | VSID、44B 门、24/36B ref header、4B local 字段、meta8、规范路径；无文件 I/O |
| `Internal/Records` | 从已取得的 public IRbfFile 校验本次需要的 header/head，先元信息尺寸再完整读取、复制结果；不拥有文件 |
| `VersionStore` / `Internal/Runtime` | 身份/模式 guard、输入捕获、data-first、PublicationOutcome 与 owned 资源清理；通过窄文件接缝取得目标 |
| `Internal/Storage` / `Internal/Platform` | 根准入/锁/门 I/O/发现/唯一 pending/原生不覆盖发布/按模式打开；不解释应用 bytes |
| `RootMapCodec` | 独立可选应用 codec，使用 Binary 和 FrameAddress 公共 codec；不进入 owner 的调用链 |

VersionStore 与 FrameStore 均保持 source-only，新栈只引用 main 项目。现有 RBF1 冻结参考及 pack 清单不变。FrameStore 的 S2/S3 Accepted 不自动覆盖新的借入 guard 或 VS 实现。

## V0：已完成骨架和关键局部（2026-10-10）

该轮从 `9bc5527` 的干净树开始，提交为 `7cca95c`。项目加入 solution，完成以下局部，历史验证记录见下方：

- FrameStore 公共 `EnsureUsable()` 直接复用现有生命周期/fault 检查，允许只读及活跃 Builder，无 I/O 或确认副作用。
- VS 身份/值、门/header/meta/path codec 和独立 golden；保持 VSID、DataStoreId、RefId、SizedPtr 与 FrameAddress 各自职责。
- 普通 ref header/head 的必要读取：不预读初始或源 payload，不因内容不可解码拒绝，不回退坏 head；pooled 帧在交付自有结果前归还。
- 已有 ref 的 ReadRef/PublishRef 运行内核：先输入/容量检查，再 data barrier，正式 Append 前 Unknown，flush 后立即 Confirmed；停用时完整清理且锁最后，不 Dispose 借入 data。
- 独立 RootMapCodec：有效 Unicode、Ordinal、变长地址、1MiB 实际 codeword 界、exact-consume 与自有冻结结果。

**V0 当时没有生产目录 backend 或公开 Create/Open/OpenReadOnly；下述缺口已由 V1 部分补齐。** owner 仅能经内部资格移交构造，测试 backend 可接入真实 RBF 文件并注入失败；这证明局部读写/状态机，不证明 VS 根/锁/路径/pending/恢复的公开闭环。没有 CreateRef、ForkRef、ListRefs、历史、tag、CU 或独立完整校验入口；也不为它们提供占位方法。V0 的 internal constructor 借入复检不能替代 factory 在任何 VS I/O 前的准入检查。

内部文件接缝是可替换实施边界，不冻结为 public 扩展点。当前已有 ref 句柄随 owner 保留；后续大量 ref/历史临时句柄的策略须在对应路径明确 owned 与 borrowed 责任，不把现有测试 backend 或缓存自动当作已验收的资源方案。

## V1：普通 ref 公开持久化闭环（2026-10-11）

本轮从 `7cca95c` 的干净树开始，按平台、目录工厂、创建内核、公开验收与独立审查分包实施，未修改 FrameStore/RBF 底座或 pack 清单。现已接通：

- `Create(rootPath, data, reservedRefIdUpperBound = 0)`、`Open`、`OpenReadOnly`：借入可用性/模式检查先于 VS I/O；fresh 根锁前及锁后重检；门关闭后再校验布局、发现 H、裁决 pending。正式 ref 惰性打开，不在工厂统一恢复。
- Windows/Linux 窄平台实现：精确受管名称、ordinary/no-follow、同 volume/mount、永久 owner 锁和原生不覆盖移动。Linux 分支已写入并审查，运行资格单列，不由 Windows 结果推定。
- 两种 `CreateRef`、`ListRefs` 及原有 `ReadRef/PublishRef` 生产 backend。默认 L=0；指定创建限预留区空闲槽，自动使用 H+1，不补洞；正式坏内容仍占号并列出。
- 新 ref 先捕获 bytes/准备元信息和尺寸，data barrier 后私有初始化，flush/close 后移动。私有阶段保持 NotAttempted，正式移动调用前 Unknown，原生移动正常返回后立即 Confirmed，再无分配置 H/交付结果。移动入口允许原生调用前的资格前检，成功后不再读取、分配或清理。
- 工厂失败与运行期停用均单次清理 owned 资源、锁最后，保留原始错误顺序，不 Dispose 借入 data。pending 冷开只按实际名称/类型/同文件系统取消，不读内容或认领旧尝试。

本轮不提供历史、fork、tag、完整校验入口或 CU。没有增加公开裸句柄导入、故障注入 API 或通用文件系统框架。测试中的受控 flush/close/rename 异常与手工截尾分别记录，不是 OS 故障、真实进程终止或断电证据。

已解决的集成接缝：内部助手以 `scoped ref PublicationOutcome` 直接更新调用方证据；意外私有 Append 拒绝视为已预计算计划的不变量失效，停用并留下冷开可取消的 pending；Linux canonical 名称与大小写别名共存也拒绝；平台缺项探测不以宽泛异常捕获吞掉 owned 清理失败。这些修正未改变持久格式或产品范围。

## 后续顺序与验收出口

| 分片 | 可审查交付物 | 必须保留的边界与验收 |
| --- | --- | --- |
| V1：已接通普通 ref 公开闭环 | Create/Open/OpenReadOnly、自动/指定 CreateRef、ReadRef/PublishRef/ListRefs；正式目录 backend 接入现有内核 | public `Create → CreateRef → PublishRef → ReadRef → Dispose → Open/RO → ReadRef`；借入/模式在 VS I/O 前检查；门 close 后才发现/清 pending；可写 H 先完整发现，RO 不为发号全扫；唯一 pending 只取消不采用；原生同 FS no-overwrite rename；坏门/别名/特殊文件/错 data/失败清理与锁最后 |
| V2：历史、精确 fork 与分叉图 | RBF 指定边界逆扫公共增量先独立验收；ReadRefHistory、两种 ForkRef、ListForks | 历史预算/早停与 callback Dispose；源旧点不扫描后缀；fork exact bytes clone、data 后源 flush、单文件发布；图仅读 header 并迭代检查来源集合/环，结果自有。若工作量过大，按底座增量→历史/fork→声明图分轮 |
| V3：不可变 tag | 09 的名称/路由/请求范围扫描、空桶初始化、CreateTag/ResolveTag | tag header 的初始化长度保护不同于 ref；局部坏 A 不阻断健康 B，零命中且名称不确定不能当 missing；空桶 rename 不推进 tag outcome；与历史 visitor 及 bytes 结果互操作 |
| V4：独立完整校验与系统验收 | 只读完整检查入口、public 消费例子、进程终止/资源与两平台矩阵、S4/S5/S6 条款映射 | 日常必要读取与完整校验分别取证；新旧/循环图、坏初始健康 head、应用解码失败、所有发布与清理窗口；不得将内部模拟、字节镜像或源码测试标成 public factory、真实 kill、断电或包资格 |

V1 已在本轮接通，后续不重做工厂或建立另一套发布路径。下一轮优先 V2-A：先实施并独立验证 RBF 的指定边界逆扫，再接 `ReadRefHistory` 的固定上界、预算、回调早停和资源清理。V2-B 接精确 `ForkRef` 与继承历史；V2-C 接 `ListForks` 声明图，若可在同轮可靠验收可合并。没有先实现全部历史就不能创建 fork 的底层依赖，但按此顺序更容易分别取得成员/遍历与发布证据。后续真实文件实验按 S4/S5 使用 W:；Linux 验证另留身份与结果。

V2 的底座增量权威为 [RBF 边界逆扫计划](../Rbf/rbf-reverse-boundary-plan.md)。普通当前值与普通 PublishRef 不依赖它，V1 已独立验收。ForkOrigin 元数据可在 V0 编解码，但只有 V2 的正式源读取/确认才建立链接资格。

CU 在正式选型同步 S0/S4/S5 前不进入上述默认队列；尤其不能提前给普通 Snapshot 分配 Members 或让 API 暗中提供跨 ref 事务。包注册、隔离 PackageReference smoke、发布及真实业务仓切换按 S6 另立明确交付片，不由源码骨架自动开启。

## 每轮结束时的交接要求

更新本轮完成项、实际验证命令/结果、尚未取得的资格，以及下一轮最小闭环。后续实现如果改变 internal 接缝，同步修改调用方和有意义的测试；公开行为或持久格式的改变回写所属权威，不只改本文。

当前未发现需要改变产品设计的阻碍。后续关键实现依赖是 RBF 边界逆扫、历史跨文件资源/预算管理及既定 fork/tag 协议；大量 ref 的句柄保留策略与系统验收仍须按实际路径取证，不把已定机制的接线重新扩展为设计待定项。

## V0 历史验证记录（2026-10-10）

V0 已完成源码集成、主线程验证和独立反例复核，无未解决的 P1/P2。命令、源码输入摘要、TRX 与日志摘要见 [V0 证据](evidence/2026-10-10-versionstore-v0.json)。

- `dotnet build Atelia.Storage.slnx -c Release --nologo -v minimal`：最终增量构建 0 error、0 warning；此前底座构建出现的 41 项既有 XML 文档 warning 未作范围外修订。
- `dotnet test Atelia.Storage.slnx -c Release --no-build --logger trx --results-directory artifacts/versionstore-v0/tests`：2410 通过、0 失败、3 项 Linux 专属测试在 Windows 跳过。其中 VersionStore 202 项（Format 79、Values 10、Records 40、RootMap 43、Runtime 30），FrameStore 422 项通过（包含新 guard 的 5 项）。
- `eng/Test-Rbf1ReferenceAssets.ps1`：7 个冻结参考项目保持精确 RBF1 包。VersionStore assets 中 FrameStore/Binary/Rbf/Data/Primitives 均为 main project，外部 codec 依赖为 K4os.Compression.LZ4 1.3.8。

复核中已补齐路径资格检查的 owned 临时资源清理失败标记，并让 owner teardown 直接枚举句柄表、在 finally 中最后释放锁，避免清理路径上的额外集合分配。真实 RBF 内容损坏由记录层测试验证；运行层故障注入使用实际经过的扫描入口，不把未被调用的装饰器读方法计作覆盖。

当前 PublishRef 会读取并复制旧 head 的 payload，以保守确认其完整性。后续可在保持坏 head 裁决一致的前提下避免旧 payload 的结果复制；这是局部优化，不阻塞 V1。当前没有发现需调整持久格式或发布协议的反例。

以上是 V0 当时的源码及内部接线资格，未涵盖真实 VS 根/公开工厂；该部分的后续证据见 V1。进程终止、Linux 平台与包消费仍未取得资格，FrameStore 原有验收不能代替它们。


## V1 验证记录（2026-10-11）

主线程最终 Release 构建 0 error、0 warning；完整 `dotnet test Atelia.Storage.slnx -c Release --no-build` 为 **2618 通过、0 失败、9 项 Linux 专属测试跳过**。VersionStore 为 **410 通过、6 跳过**，包含 V0 的 202 项及本轮新增 214 项（208 通过、6 跳过）。独立源码及关键断言复核无未解决 P1/P2。精确命令、最终 TRX、日志摘要、源码摘要和依赖闭包见 [V1 证据](evidence/2026-10-11-versionstore-v1.json)。

公开生命周期与目录工厂测试在 W: 使用真实 FrameStore/RBF/VersionStore；覆盖两层冷重开、模式/持续借入、active Builder、锁矩阵、全部短门前缀与字段错误、创建中断镜像、门 close 先于发现/取消、惰性恢复、pending 只取消不采用、H 耗尽/低号仍可建、必要读不预审初始、坏 head 不回退及自有结果。Runtime/工厂窄注入另覆盖 barrier 计数与创建各相位异常、确认后交付失败、主错优先和锁最后单次清理。

`eng/Test-Rbf1ReferenceAssets.ps1` 验证 7 个冻结参考项目仍使用精确 RBF1 包；VS 的 FrameStore/Binary/Rbf/Data/Primitives 都是 main project，外部依赖为 K4os.Compression.LZ4 1.3.8。没有打包、发布、push 或消费者迁移。

Windows 测试和独立复核支持本轮普通 ref 公开闭环；真实进程终止、跨进程锁、跨 volume/mount、Linux 执行与系统完整验收仍留 V4。没有产品决策阻塞，下一片按上面的 V2-A 推进。
