# S1 单文件尺寸与提前 ticket 验收记录

> 后续合同变更说明（2026-10-07）：本文保留 `8ab98bf` 的原始验收证据；当时的门面 Building 读取规则已由 RBF `[S-RBF-RANDOM-READ-COMPLETED-PREFIX]` 放宽为允许指定 ticket 读取已完成前缀。新改进资格另见[本轮记录](reviews/2026-10-07-completed-output-improvements.md)，不重标本文测试为新合同验收。

> 地址编码导航（2026-10-08）：本文源码 smoke 的“12B 地址”是已选定 RBF 文件上下文内的 offset(long) + length(int) 示例编码，见 [EncodeTicket](../../examples/RbfSizedAppendSmoke/Program.cs)。新 FrameStore 的 [S2](02-framestore-core.md) `[F-FS-FRAME-ADDRESS-12B]` 则编码 uint FileId + 完整 SizedPtr.Packed；二者宽度相同、格式与上下文不同。本文不构成新 FrameAddress codec 的实现或包消费资格。

日期：2026-10-04。状态：**Accepted，单文件源码资格已实现并独立验收**。
实施合同与新增条款的唯一入口为 [S1](01-rbf-sized-append.md)；本记录只映射交付物和证据，不新增合同。

## 来源与构建身份

| 身份 | 本轮记录 |
| --- | --- |
| 实施合同提交 | `c940ed63921f6fa2a942bbf131957d3d7f101626` |
| 运行时实现提交 | `8ab98bf58a8a575e2418aba6be12097951ed8d1d`，包含源代码、测试和独立 public smoke |
| 实际 build/test 工作树或提交 | `c940ed6` 加实施工作树；通过后提交为 `8ab98bf`。95 个源/测试/示例文件的 SHA256 快照在构建后、提交后均核对一致；不把提交前运行重标为 clean HEAD 构建 |
| SDK/runtime、Release 构建日志及测试日志 | Windows win-x64；SDK 10.0.201 / Host 10.0.5；本地日志在 `artifacts/s1-sized-append/` |
| 独立代码/文档验收 | `repair_rbf_init` 独立审阅尺寸/格式/互引；`findings_architect` 独立审阅 Builder/失败边界；主线程亲自核实源码、整合、构建、运行和证据身份，最终无剩余 finding |

本地快照为 `build-sources.json`，SHA256 `A4A82331D90E78E486FDC847AF16C2C7DD7CAA14518257320D007ACD67DD3FE9`。RBF 实现 DLL 的 SHA256 为 `301742EF9A2C20F52116ECA64E9D6BC88327D680ED0009272670BAFD7C50AF52`；测试与 smoke 实际使用的 RBF DLL 哈希相同。测试 DLL 的 SHA256 为 `781978FB447F4D78E1E78D08F25793340D6EDB12048B2015E74586632D09AD89`。日志、TRX 和快照保存在被忽略的本地 artifacts 目录，不作为 Git 随附或公开包资产。

## 本片交付范围

定稿入口包括 RbfWriteSize、MeasureWriteSize、TryGetMaxPayloadLengthForAppendBudget、RbfFormat/IRbfFile.Format，以及 BeginAppend(payloadLength, tailMetaLength, out ticket)。既有无参数 Begin 保留；共用 RbfFrameBuilder、PayloadAndMeta writer、owner/epoch，End 改为一参数与显式二参数两个重载。

本片不改 RBF1/RBF3 wire、恢复分类或已发布包，不创建 FrameStore/VersionStore。下游 EventJournal/SegmentStore/DurableGraph 的适配和既有性能实验重写不作为本片交付；普通 RBF 输出测试 fixture 可消费预算查询，独立 wire/CRC golden 预期保留。

## 可观察覆盖映射

下表映射实现合同、实际源码和测试。主线程已核实全部入口，并以匹配 Release 构建的一次完整 RBF 测试运行闭合覆盖。

| 合同/边界 | 可观察验收 | 用例与交付物入口 | 结果 |
| --- | --- | --- | --- |
| 正向 Measure | 纯计算；空帧、padding 余数、meta 上限、合计容量及极大/负数输入；frame 与 append 分开 | [RbfWriteSizeTests](../../tests/Rbf.Tests/Internal/RbfWriteSizeTests.cs) 的独立向量、边界及 public Append 对照 | 通过 |
| 追加预算 | 普通不足 false/0，成功允许 0；非法参数异常；封顶及 long.MaxValue；Measure(max) 不超预算，未封顶时 max+1 不 fit | [RbfWriteSizeTests](../../tests/Rbf.Tests/Internal/RbfWriteSizeTests.cs)；[输出 fixture](../../tests/Rbf.Tests/Internal/Rbf3SmallAppendOutputTests.cs) 通过预算构造输入，保留独立 wire 预期 | 通过 |
| Format | 空/非空 RBF1/RBF3 的公开值正确；Header 已分派身份，旧 ticket 读取和只读资格保留 | [RbfFormatTests](../../tests/Rbf.Tests/Internal/RbfFormatTests.cs)；既有 legacy 容量测试 | 通过 |
| 成功已知尺寸追加 | out ticket 起点/尺寸正确；Begin 不推进 tail；End 自动采用 meta，成功 ticket 相等、内容分界正确 | [RbfSizedAppendTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendTests.cs)；下述双文件 public 验收 | 通过 |
| 精确声明及容量 guards | 相同 padding 后尺寸不能掩盖短写；meta 冲突可纠正；Advance/ReserveSpan 超限不改变 writer；借用和 reservation 纠正重试 | [RbfSizedAppendTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendTests.cs)；[Rbf3WriterFaultTests](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs) 的未 Advance 借用用例 | 通过 |
| default/stale/closed 与旧模式 | 两种 End 共用 epoch；旧对象不读取新声明；未知尺寸默认 meta=0、显式 meta 保持；单 Builder/读门面规则保留 | [RbfSizedAppendTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendTests.cs)；既有 [RbfFrameBuilderTests](../../tests/Rbf.Tests/Internal/RbfFrameBuilderTests.cs) | 通过 |
| 起点边界 | 负的 synthetic owner 起点及超过 MaxOffset 在签发前拒绝；起点等于 MaxOffset 可提交，末端/Fence 不额外受起点上限限制 | [RbfSizedAppendFailureTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendFailureTests.cs)、[RbfFrameBuilderTests](../../tests/Rbf.Tests/Internal/RbfFrameBuilderTests.cs) | 通过 |
| 初始化及资源异常 | Begin Rent 失败不发布 epoch/Building；准备异常取消；取消 Return 异常无输出且 File.Dispose 不再次 Return；最终完整输出后 Return 异常永久共享 fault | [Rbf3WriterFaultTests](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs) 的 sized/unknown 参数化用例 | 通过 |
| A/B 双文件互引 | 两个非相等的提前 ticket；完成后完整读回，再关闭并只读重开，固定宽度引用保持一致 | [RbfSizedAppendAcceptanceTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendAcceptanceTests.cs)、[RbfSizedAppendSmoke](../../examples/RbfSizedAppendSmoke/README.md) | 通过 |
| 取消及地址复用 | A 已完成且含 B 早 ticket 的物理事实保留；B 取消不改文件/tail，取消后旧 ticket 读失败；C 可复用同一 SizedPtr，旧数值可读到 C，不能证明 B 完成 | [RbfSizedAppendAcceptanceTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendAcceptanceTests.cs) | 通过 |
| 最终输出异常 | 真实请求产生 0/partial/full bytes 后失败，tail 仍旧、共享 fault、Dispose 不重发；重开按实际 prefix 得到 None/Truncated/CompletedTail，旧内容保留 | [RbfSizedAppendFailureTests](../../tests/Rbf.Tests/Internal/RbfSizedAppendFailureTests.cs)，复用既有真实写 hook 和恢复分类 | 通过 |

## 实际验证结果

| 检查 | 配置与实际范围 | 结果/日志 |
| --- | --- | --- |
| RBF Release build | `dotnet build tests/Rbf.Tests/Rbf.Tests.csproj -c Release --nologo -v:minimal`，最终增量构建 0 error / 0 warning | 通过；`rbf-build.log` |
| S1 新增类 | 同一次完整运行中：RbfWriteSizeTests 63、RbfSizedAppendTests 30、Acceptance 2、Failure 11、Format 3；从 TRX 核对，不另称运行过 filtered tests | **109/109**；`rbf/rbf-tests.trx` |
| 完整 RBF tests | `dotnet test tests/Rbf.Tests/Rbf.Tests.csproj -c Release --no-build`；含既有未知模式与扩展的资源/偏移边界用例 | **818/818**，0 skipped；`rbf-test.log` |
| Data 共享 writer 回归 | 本次 solution build 成功生成 Data.Tests 的 Release 二进制；随后 `dotnet test tests/Data.Tests/Data.Tests.csproj -c Release --no-build` | **288/288**，0 skipped；`data-test.log` |
| 独立 public 源码 smoke | 独立项目 Release build 后，`dotnet run --project examples/RbfSizedAppendSmoke/RbfSizedAppendSmoke.csproj -c Release --no-build -- W:/RbfSizedAppendRun` | 通过；`smoke-build.log` / `smoke-run.log`；12B 地址对应 40B frame / 44B append，双文件互引重开读回 |
| Solution build | `dotnet build Atelia.Storage.slnx -c Release`；报 RbfSegmentStore.cs:141、184 两处既有 CS1620，旧 OpenExisting 调用缺 out，41 个既有 XML warning；未修改下游 | **未通过**；`solution-build.log`。整体构建失败，因此未以可能陈旧的下游二进制执行全 solution `--no-build` 测试 |
| 文档与实现复核 | 定稿签名、异常语义、所有权/epoch、无新增预约状态或下游范围；新增相对链接、Markdown fence 与 `git diff --check` | 通过；两项独立静态审阅加主线程实际验收 |

首次定点构建因新增测试遗漏 `using Atelia.Data` 未通过；主线程补齐后重新构建并运行上述最终测试。初次日志保留为 `rbf-build-initial.log`，不计作通过证据。执行顺序遵守 Windows .NET 操作串行；源码层资格不依赖下游适配成功。

## 证据资格与未覆盖范围

- public smoke 是独立源码消费者，未证明 NuGet 包资产、依赖闭包、Source Link 或包安装后的行为。本片不打包、发布或切换消费者 pin；包资格需后续隔离 PackageReference smoke。
- 输出失败用例使用既有 hook 完成真实顺序前缀写入，再抛异常；完整输出后异常也按既有 fault 合同处理。这不是外部进程终止、实杀、断电或设备原子写实验，不重标历史证据为 S1 结果。
- 本片无性能对照或吞吐承诺；公共试算入口与测试 fixture 迁移不为此前阈值测量生成新资格。
- 双文件互引证明各帧位置和内容成立，不证明跨文件原子提交、引用闭包耐久或根发布。变长引用与压缩长度的固定点求解不在本片。
- S2–S6 仍为 Draft；新 FrameStore/VersionStore 和真实下游接入另行形成源码、测试与包证据。

## 闭合结论

S1 的公共试算、格式投影、分立声明、提前 ticket 与原生命周期合同已实现并独立验收，状态闭合为 Accepted。RBF 接口、S0、阶段目录及根 README 同步此源码资格；S2–S6 仍为 Draft，solution、包和下游资格保持以上边界。
