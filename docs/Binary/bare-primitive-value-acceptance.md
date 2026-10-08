# Bare Primitive Value 实施验收

日期：2026-10-08。状态：**源码 Accepted；本地包消费验证待完成**。合同权威为 [BPV1 规范](bare-primitive-value.md)，使用入口为 [Atelia.Binary](../../src/Binary/README.md)。本记录不授予 FrameStore/VersionStore、Tagged、下游迁移或公开 NuGet 发布资格。

## 实施范围与具体选择

新建 `src/Binary` 与 `tests/Binary.Tests`，注册 solution。六个 public 类型与规范签名一致；生产程序集仅依赖 BCL，Data/Rbf/Primitives 和冻结旧栈没有运行时改动。Reader、Writer、Encoding 的 partial 文件按标量、字符串、受控值组织，不新增 public dispatcher 或策略层。

固定 LE、原始浮点 bits、Base128/ZigZag、UTF-16 code-unit 保真、严格 UTF-8、精确 long Measure、轻量 string plan、借入 plain bytes 和受控 owned bytes 均已实现。writer 的内部 sizeHint 上限为 16KiB；只保存 sink 引用，不回滚或拥有 sink。

受控 Prepare 先核对完整 U，再用固定大小的内部 sink 物化。VarInt 写入可请求最大宽度，sink 用小 scratch 满足比剩余容量更大的 hint，再只复制实际 Advance 的字节，保持最终 body 精确为 U。Brotli quality=3/window=22 为内部初值；以 U 大小候选缓冲尝试编码，只有 Done 才接受完整结果。未完成时仅在实际已产出字节的完整尺寸下界已不小于 Raw 时放弃，不单凭 DestinationTooSmall 猜测收益。

解压使用 instance decoder，验证 Done、C、U；U 已满时用 1B scratch 检查完成或溢出。随后 typed 内层解析和完全消费成功，才提交外 cursor。所有成功/异常路径 Dispose encoder/decoder。受控 Raw 先根据实际冗余 header 算出 U 并检查双限额，再分配。

## 条款与行为证据

golden 为手写独立 bytes；Brotli decoder fixtures 来自 Google Brotli Python bindings 1.0.9、quality=5，window=10/16/18/22/24，出处与生成命令保留在测试注释中。它们与生产初始 encoder 参数不同，不依赖生产 Write 来生成全部 reader 预期。

| 条款 | 实际测试或复核入口 |
| --- | --- |
| `[S-BPV-SCHEMA-OWNED]` | [SchemaCompositionTests](../../tests/Binary.Tests/SchemaCompositionTests.cs)、独立 [BinaryPackageSmoke](../../examples/BinaryPackageSmoke/Program.cs)：12B 地址与 consumer-owned 字典 |
| `[F-BPV-FIXED-LE]` | [ScalarTests](../../tests/Binary.Tests/ScalarTests.cs)：LE golden、所有固定宽度截断、NaN/±0/Inf/subnormal 与 CharLE bits |
| `[F-BPV-VARINT-BOUNDED]` | [VarIntTests](../../tests/Binary.Tests/VarIntTests.cs)：三宽度边界、冗余终止、overflow、最大 continuation、实际消费 |
| `[F-BPV-SIGNED-ZIGZAG]` | 同上：三宽度 min/max 与随机数据；额外真实 checked 构建 |
| `[F-BPV-STRING-DUAL]` | [StringTests](../../tests/Binary.Tests/StringTests.cs)：UTF-16 code units、surrogate/BOM、非法 UTF-8、范围与截断 |
| `[S-BPV-STRING-DEFAULT]` | 同上：独立短串 golden、最短 payload、持平 UTF-16 |
| `[A-BPV-STRING-TOLERANT]` | 同上：另一种合法编码、UTF-8 empty、冗余 header 与真实消耗 |
| `[F-BPV-BYTES-EXPLICIT]` | [BytesTests](../../tests/Binary.Tests/BytesTests.cs)、CoreContractTests：raw/长度前缀、空值、借入生命周期 |
| `[F-BPV-NULL-CONTROL]` | [ControlledValueTests](../../tests/Binary.Tests/ControlledValueTests.cs)、SchemaCompositionTests：nullable scalar、独立 null、Boolean 拒绝 02 |
| `[F-BPV-CONTROLLED-ENVELOPE]` | 同上及 [BrotliValidationTests](../../tests/Binary.Tests/BrotliValidationTests.cs)：独立 wire、C/U 冗余、U 包含内层 header、未知 control 与 C/U=0 |
| `[S-BPV-COMPRESSION-SELF-CONTAINED]` | BrotliValidationTests：五种标准 window 的独立流；PreparedValueTests：未知 enum，包括 null 输入 |
| `[A-BPV-COMPRESSION-PREPARED]` | [PreparedValueTests](../../tests/Binary.Tests/PreparedValueTests.cs)：完整收益与持平、源变更、重复写、精确预算、runtime surrogate/BOM 压缩往返 |
| `[A-BPV-CONTROLLED-READ-BOUNDED]` | ControlledValueTests、BrotliValidationTests：实际 Raw U 前置限额、Done/C/U、截断/tail/concat、U±1、非法内层、owned bytes 与失败 cursor |
| `[A-BPV-CURSOR-ATOMIC]` | 各类 reader 测试：已有前缀后失败保留 cursor；SchemaCompositionTests 演示多字段副本安装 |
| `[A-BPV-ERROR-CLASSIFIED]` | 各类截断、输入错误、调用错误、default 和 sink 故障测试 |
| `[A-BPV-WRITE-PARTIAL]` | [CoreContractTests](../../tests/Binary.Tests/CoreContractTests.cs)、ChunkedOutputTests、PreparedValueTests：确定拒绝不碰 sink，异常原样和已写前缀 |
| `[A-BPV-SIZE-EXACT]` | VarIntTests、StringTests、BytesTests、PreparedValueTests、SchemaCompositionTests：long 上界预算、默认 plan 拒绝、实际默认输出一致 |
| `[A-BPV-OUTPUT-CHUNKED]` | [ChunkedOutputTests](../../tests/Binary.Tests/ChunkedOutputTests.cs)、CoreContractTests、PreparedValueTests：合法非连续/更大块、UTF-8 pair 跨块、受控 body 与故障 |

独立静态算术另核对 U=128/C=127→129/131、C=125→129/129、C=124→129/128，仅最后选择压缩；`1+int.MaxValue=2147483648L`，`MeasureBytes(int.MaxValue)=2147483652L`。源代码直接核对 owned U 超限先于物化；本次不分配接近 2GiB 的真实 payload，不能把静态算术说成实际极限内存或吞吐资格。

## 真实验证

环境：Windows、精确 SDK 10.0.201、.NET 10.0.5。构建/测试/pack 入口串行执行。原始日志与 TRX 保留在 [W: 验收目录](W:/atelia-binary-implementation-20261008/)。

| 验证 | 结果与证据 |
| --- | --- |
| `dotnet build Atelia.Storage.slnx -c Release` | 最终增量构建 0 warning / 0 error；首次完整构建有 41 条现有非 Binary 的 XML 文档 warning，未以本次任务清理 |
| `dotnet test Atelia.Storage.slnx -c Release --no-build` | **1725/1725**：Binary183、Primitives75、Data288、Rbf835、Segment48、EJ174、toolkit122；见 release-tests-final.log / release-test-final |
| `eng/Test-Rbf1ReferenceAssets.ps1` | 七组旧栈实际 assets 仍是精确 RBF1 `[0.2.0-rbf1-preview.1]`，来源 `3e9554e2ea70f769607e80b3fc11a85506050533` |
| 独立 artifacts 下 build/test Binary.Tests，`-p:CheckForOverflowUnderflow=true` | 生产与测试均按 checked 编译；**183/183**，见 checked-build.log / checked-tests.log / checked-test-results |
| `eng/Test-Package.EntryPoint.Tests.ps1` | parser、双 consumer、22 类坏 assets（含空集合/交叉依赖）、7 类坏 manifest、旧参数拒绝、不可变复用和失败环境恢复通过；所有 git/dotnet 为 mock，不替代真实消费 |
| 静态核验 | 18 条款唯一且都有向量映射；尺寸算术、空白、`git diff --check` 通过；现有 FS/VS 15 份文件 hash 与任务开始一致 |
| 本地四包与隔离消费 | 待在本次源码提交的干净 checkout 执行 Pack 与 Test-Package，再更新本记录 |

首轮真实测试发现四个测试层失败：两处造数据在 checked 下缺少显式 wrap；nullable scalar 验证误走受控字符串入口；InlineData 属性元数据不能保留孤立 surrogate。修复测试后重建并完整重跑，未据此改变正确的生产异常或字符串合同。独立只读 reviewer 从实际生产代码与测试复核，未发现剩余生产合同违例；主线程另复核共享契约、代码、日志与包边界。

## 包交付与边界

生产清单仍仅由 `eng/Pack.ps1` 定义，All 顺序为 Primitives → Data → Rbf → Binary。两个隔离应用分别只 PackageReference Rbf 或 Binary，保持三包和单包闭包；metadata 检查四包 nuspec、hash、XML、symbols/PDB、静态 Source Link 与本地 source checksums。Binary 包明确无 NuGet 依赖。CI 继续调用相同统一入口；公开验证脚本与 workflow 已同步四包，但本次不进行远端发布。

Tagged、Zlib、Guid/decimal/时间、全 nullable helper、下游实际迁移、远端 Source Link 下载、跨 runtime 压缩字节一致与性能比较未取得资格。Prepare 保留完整 owned body 和可能的候选缓冲；未来性能评估需分别记录 Measure/Prepare/Write/Read 的 CPU、allocation、准备峰值与计划保留量，不以分块 writer 宣称恒定内存。
