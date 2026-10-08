# Bare Primitive Value 实施验收

日期：2026-10-08。状态：**Accepted，源码与本地候选 public PackageReference 消费均通过**。合同权威为 [BPV1 规范](bare-primitive-value.md)，使用入口为 [Atelia.Binary](../../src/Binary/README.md)。各段保留对应候选和依赖身份，当前 LZ4 增量见末节；本记录不授予 FrameStore/VersionStore、Tagged、下游迁移或公开 NuGet 发布资格。

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
| 本地四包与隔离消费 | **通过**：候选 `0.3.0-bpv1-dev.20261008043210`，来源 `1afcb65ad523d6f824e62bb66cb8aa17fbe323ee`；Rbf 三包与 Binary 单包各自独立 public API smoke、四包 metadata、88 份本地源码 checksum；见 package-smoke.log 与 package-smoke/logs |

首轮真实测试发现四个测试层失败：两处造数据在 checked 下缺少显式 wrap；nullable scalar 验证误走受控字符串入口；InlineData 属性元数据不能保留孤立 surrogate。修复测试后重建并完整重跑，未据此改变正确的生产异常或字符串合同。独立只读 reviewer 从实际生产代码与测试复核，未发现剩余生产合同违例；主线程另复核共享契约、代码、日志与包边界。

## 本地候选身份

源码提交：`1afcb65ad523d6f824e62bb66cb8aa17fbe323ee`；本记录最终状态由后续文档提交补齐，未改变已验收的生产或测试代码。干净 detached checkout 保留在 [W: source](W:/atelia-binary-implementation-20261008/source/)，避免提交或清理当前工作区已有的 FS/VS 文档修改。候选版本：`0.3.0-bpv1-dev.20261008043210`，feed 与 manifest 保留在 [W: feed](W:/atelia-binary-implementation-20261008/feed/)。

- Binary nupkg SHA256：`1e11e6c79ac775fea267a1f57513fd03728732ffa75fe6b334c705608bb29494`。
- Binary snupkg SHA256：`b4f4a5ffb84c55c3db6a2bcf8564430d651ec1459f525f9029535dc2df5680fc`。
- 四包完整身份、hash 与来源以 `manifest.0.3.0-bpv1-dev.20261008043210.json` 为准；`package-smoke/verified-packages.json` 保留验收使用的原 manifest。
- 两个 consumer 的 assets、私有 cache、日志和 Rbf 真实读写文件均保留在 [package-smoke](W:/atelia-binary-implementation-20261008/package-smoke/)。Binary 合成记录的完整预算和实际输出均为 129B；这不是性能实验或 FS/VS wire 格式资格。

## 包交付与边界

生产清单仍仅由 `eng/Pack.ps1` 定义，All 顺序为 Primitives → Data → Rbf → Binary。两个隔离应用分别只 PackageReference Rbf 或 Binary，保持三包和单包闭包；metadata 检查四包 nuspec、hash、XML、symbols/PDB、静态 Source Link 与本地 source checksums。Binary 包明确无 NuGet 依赖。CI 继续调用相同统一入口；公开验证脚本与 workflow 已同步四包，但本次不进行远端发布。

Tagged、Zlib、Guid/decimal/时间、全 nullable helper、下游实际迁移、远端 Source Link 下载、跨 runtime 压缩字节一致与性能比较未取得资格。Prepare 保留完整 owned body 和可能的候选缓冲；未来性能评估需分别记录 Measure/Prepare/Write/Read 的 CPU、allocation、准备峰值与计划保留量，不以分块 writer 宣称恒定内存。

## .NET 11 接入准备的增量验收

同日新增内部 ControlledValueStorage 与 ControlledValueCodecs：统一受控包装的分类、精确长度和显式分派，将 Brotli 状态机从公共 Prepare/read 流程移入算法实现。当前仍为 net10.0、None/Brotli 与 control 00/01/02；公共签名、enum 值、默认 None、owned plan、完整收益选择、双限额和失败 cursor 合同保持。未引入 codec registry、外部依赖或未实现的方法编号。

后续 .NET 11 GA 的 Zstandard、RFC 1951 裸 Deflate，以及可选的独立 RFC 1950 ZLib 接入步骤，保存在 [规范 TODO](bare-primitive-value.md#增量压缩方法接入todo-net-11-ga)、[ValueCompression XML doc](../../src/Binary/ValueCompression.cs) 和 [ControlledValueCodecs 注释](../../src/Binary/ControlledValueCodecs.cs)。本节不授予这些未来 codec 实现资格。

| 增量验证 | 结果与证据 |
| --- | --- |
| Release solution build / `--no-build` tests | 0 error，41 条既有非 Binary XML warning；**1725/1725**，其中 Binary **183/183** |
| 独立 checked build/test | 生产 Binary 与测试同时启用 checked；0 warning / 0 error，**183/183** |
| 公开 API 与包基线比较 | 从首轮候选 DLL 和当前 DLL 独立反射比较，六个 exported 类型的 **83 项**公开类型/成员/参数及默认值/enum 常量描述完全相同；既有 24 个 XML member IDs 也相同 |
| 未知 control | 对 03..FF 的每个值分别测试只有 control、截短 C/U 和貌似合法包装，均在解析长度前拒绝且保留外 cursor |
| 依赖与已有工作 | 七组冻结旧栈实际 assets 仍为精确 RBF1 package；主线 Rbf 仍为项目依赖，Binary 无 package/project 依赖；FS/VS 15 份文档 hash 与本轮开始一致 |
| 本地四包与隔离消费 | 候选 `0.3.0-bpv1-dev.20261008055055` **通过**：Rbf 三包和 Binary 单包闭包分别经纯 public PackageReference smoke 验证；四包 metadata、symbols/PDB、静态 Source Link 和 **90** 份本地 source checksum 通过，其中 Binary 为 14 份 |

源码提交：`aea7e8aa68e3b2b7c9d394784dbd8210a098146e`。原始日志、TRX、API 比较及 hash 核验保存在 [W: 增量验收目录](W:/atelia-binary-compression-extension-prep-20261008-054125/)。上文首轮候选身份保留原意；本次干净 detached [source](W:/atelia-binary-compression-extension-prep-20261008-054125/source/)、[feed 与 manifest](W:/atelia-binary-compression-extension-prep-20261008-054125/feed/)、[隔离 cache、assets 与日志](W:/atelia-binary-compression-extension-prep-20261008-054125/package-smoke/) 分开保留。

- Binary nupkg SHA256：`b2af9da0c6660fd64a156de68c65cdd6d8432df8a571e4ba57ac5899a1d270df`。
- Binary snupkg SHA256：`8638a7cffb20309f3f50918b7f7c93bc80c61bec1571182b62e48a56a2591d9a`。
- 四包完整身份以 `manifest.0.3.0-bpv1-dev.20261008055055.json` 为准；后续仅补齐本记录的文档提交不改变候选来源。

本次没有公开发布、远端 Source Link 下载或性能测试；也没有将 .NET 11 预览 API 调查当作 GA 实现资格。

## LZ4 block 的增量验收

同日用户明确授权 Binary 采用轻量 NuGet codec。新增 `ValueCompression.Lz4Block=2` 与 wire control=03，仅标准独立 LZ4 block，继续使用既有 C/U 包装和 Prepare/plan/Write/Read 签名。None/Brotli 与默认行为保持；现有 83 项 public API 描述未变，只增加一个 enum 成员，仍为六个 exported 类型。当前未知 control 为 04..FF，未实现的 .NET 11 方法继续保留 TODO。

依赖为精确 K4os.Compression.LZ4 `[1.3.8]`，NuGet 下载包 206853B，实际 net6.0 DLL 70656B，无该目标的传递依赖/native/RID 资产；生产 net10 assets 实际选择该 DLL。仅依赖基础 block 包，不增加 Streams、Pickler、xxHash 或公开依赖类型。Rbf 仍为原三包闭包，Binary consumer 仅直接 PackageReference Binary，完整闭包改为 Binary + K4os.Compression.LZ4/1.3.8。

结构预检按标准扫描 token/长度/offset、跳过 literal 内容，证明完整 C 与 U，拒绝 offset=0、外部 dictionary 引用和非法末尾规则，再分配 U 并调用完整 Decode。K4os safe decoder 会容忍零 offset，不能单凭其成功接受它。FAST 的受限输出 -1 回退经过上游 1.3.8 的 x32/x64 源码核对与实际完整容量对比测试；先检查 encoder 的完整 U 上限 0x7E000000，再物化，避免把不支持的输入混作尺寸不足。无异常吞掉 policy；没有增加第二份 payload 复制或自写 match 复制器。

独立手写 golden 覆盖 UTF-8/UTF-16、empty、实际冗余 header、literal 扩展、overlap match；负例覆盖截短、尾随/块拼接、U±1、零/越界 offset、长度和末尾约束、非法 typed 内层与失败 cursor。另验证混合 None/Brotli/Lz4Block、owned 快照/重复写、1MiB 原文回退、完整码字收益及 HC/MAX 编码可读。首轮一条 UTF-16 golden 误写 header=04，按原有 2B payload 合同修为 02；无需修改正确的生产字符串逻辑。

| 增量验证 | 结果与证据 |
| --- | --- |
| Release solution build/tests | 最终增量 build 0 warning / 0 error；**1756/1756**，Binary **214/214**（增量 31）；首次 build 的 41 条既有非 Binary XML warning 保留原意 |
| 独立 checked build/test | 生产 Binary 与测试同时启用 checked，0 warning / 0 error、**214/214**；供应方包保持原发行 DLL，并非重新 checked 编译供应方源码 |
| 包入口 mock | parser、双 consumer、原坏 assets/manifest/环境恢复负例通过；新增未知外部依赖、直接绕过 Binary 引用 codec、codec 缺失/版本不符/target 缺失共七组 assets 负例；无真实 dotnet 消费资格含义 |
| 依赖与边界复核 | RBF1 七组 assets 固定值仍通过，Binary 实际净新增一个无传递依赖的 package，FS/VS 15 份文档 hash 保持；规范 18 个 clause ID 保持 |

原始日志、TRX、下载包和上游源码核对、反射比较、依赖与文档 hash 保存在 [W: LZ4 验收目录](W:/atelia-binary-lz4-20261008-055841/)。当前源码段由包含本段的提交固定；本轮本地候选包身份及真实隔离消费结果由后续记录补齐。以前的纯 BCL/single-package 资格只描述其原候选，不替代 LZ4 版本的资格。

没有进行本项目吞吐/CPU benchmark 或接近 2GiB 的极限分配试验，不能据此承诺速度倍数、恒定内存或极限内存资格。块缺少内容 checksum；合法等长内容损坏仍由外层保护。Windows x64 的本轮实测不替代其他平台/架构、远端 Source Link 下载或公开发布证据。
