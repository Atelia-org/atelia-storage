# RootMap 基元组合探针

2026-10-10 修订。实验项目，不属于 solution 或生产 pack；合同权威为 [S4](../../docs/FrameStore-VersionStore/04-versionstore-publication.md) 的 `[F-VS-ROOTMAP-BPV1]`。

直接引用主线 public Binary/FrameStore/Rbf API，组合 `VarUInt32(count) + (ordinary BareString + FrameAddress VarInt)×count`。地址通过 `MeasureVarInt` / `WriteVarInt` / `ReadVarInt` 消费真实 public codec：`VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())`；原有 fixed12 `TryRead/TryWrite` 保留。Binary 当前依赖精确 K4os.Compression.LZ4 `[1.3.8]`，本探针不调用压缩，也不复制 FrameStore/RBF framing encoder。

```powershell
dotnet build experiments/RootMapCodecProbe/Probe.csproj -c Release
dotnet run --project experiments/RootMapCodecProbe/Probe.csproj -c Release --no-build
```

**历史证据**：2026-10-09 的 synthetic/fixed12 版本使用 SDK 10.0.201、.NET 10.0.5、Windows；最终 Release 增量 build 为 0 warning / 0 error，run 的 47 个检查通过，原始 stdout 保存于 [历史结果 JSON](evidence/2026-10-09-result.json)。首次依赖重建出现 41 条已有 Data/Rbf XML 文档 warning。该 JSON 保持原样，不能作为本轮 VarInt/public FrameAddress 版本的运行证据。

**本轮结果**：Windows 10.0.22000、SDK 10.0.201 / .NET 10.0.5，Release build 为 0 warning / 0 error；run 的 **118 项检查通过**，真实签发地址为 3B，双项 RootMap 为 20B，冷重开读回 2 帧。原始 stdout 见[本轮结果 JSON](evidence/2026-10-10-varint-result.json)，完整集成验证见[实施记录](../../docs/FrameStore-VersionStore/02-framestore-varint-address-implementation.md)。

检查覆盖：

- 独立 empty/`A` RootMap golden，最小 3B 与最大 15B 地址 golden；手写 fixed12 输入仅用于纯 codec 数值向量，包含历史高位输入。一般 map、12 个 Ordinal key 和 1MiB 向量使用真实 FrameStore 签发地址；key 包含 empty、NUL、BOM、大小写、未配对 surrogate、组合/预组合字符及 supplementary 字符，逆序编码仍内容等值。
- 真实临时目录 `Create → Append` 首帧 `→ BeginAppend(known size, out early) → EndAppend` 次帧 `→ ConfirmDurable → Dispose → OpenReadOnly`。提前地址在 End 前后各以 public VarInt codec 编码并比对相等；独立宿主文件保存 RootMap bytes，关闭后重新读取并恢复两地址，再用 public `ReadFrame` 比对地址、tag、payload/meta 与 StoreId。宿主文件是实验容器，不是 VersionStore Snapshot 或 publication authority；不据此声称宿主文件的断电耐久。
- BPV 合法另一种字符串表示及冗余 count/string header；地址 FileId/ticket 的合法冗余编码，包括扩展到完整 5B+10B 宽度。同一解码 key 的不同字符串或地址表示仍拒重。默认 UTF8/UnicodeEncoding replacement 路径另作对照。
- 最小/最大 RootMap golden 和真实双项 map 的每个短前缀；count 溢出/不可能数量、巨大字符串声明、key 侵占最小地址 suffix、非法 UTF8、零 FileId、非法 offset/length、两种 VarUInt 的 overflow/continuation overflow，以及输入重复/null/default 和宿主 trailing 拒绝。
- 内部 prefix 解码保留宿主后字段；RootMap 与 public 地址失败均不提交原 cursor，含已消费宿主 prefix 的非零 cursor。地址截断检查 `EndOfStreamException`、非法数值/溢出检查 `InvalidDataException`；default 的 Measure/Write 检查 `InvalidOperationException`，Write 拒绝前无输出。不可能 count 在 map 构造前拒绝，已知 key/suffix 不足在调用 ReadString 前拒绝；观察计数是调用路径，不是分配字节测量。
- writer 按每个地址的 `MeasureVarInt()` 精确预检总尺寸，再调用 public writer；reader 对至多 1MiB 的输入窗口解析，按真实 `ConsumedCount` 提交，包含宽容接受的冗余字节，不以重编码尺寸代替输入预算。
- 实际编码/读取恰好 1MiB 的单项 map，边界 key 长度按真实地址尺寸动态计算，超过一字节拒绝。给其 count 或地址 FileId 添加一个合法冗余 byte 后，按实际 codeword 大小拒绝；小预算也覆盖等于/超过。公共 RBF 最大 payload 与再加一字节仅做纯 Measure 检查。

独立 golden 的推导：FileId=1、offset=4、length=28 的 fixed12 输入为 `01 00 00 00 07 00 00 04 00 00 00 00`。SizedPtr 的 offset/length 单位分别为 1/7；依据交错位分配，序列化值为 `0x107`（不等于 Packed 的 `0x04000007`），Base128 为 `87 02`，故地址为 `01 87 02`、一项 `A` RootMap 为 `01 03 41 01 87 02`。最大 FileId 和全部置位的 SizedPtr 分别产生 `FF FF FF FF 0F` 和九个 `FF` 加 `01`，合计 15B。这些常量由数值及位分配手工推出，未通过同一 encoder round-trip 生成 golden。

262,143 是由 1MiB、最小 count 前缀及每项至少 `1B empty key + 3B address = 4B` 推出的保守数量界，不是另外的 quota，也不意味着那么多 empty key 合法。读 key 前为当前地址保留至少 3B，为每个后续 entry 保留至少 4B；实际 header、key bytes、地址尺寸与唯一性会进一步限制数量。

本轮覆盖真实 FrameStore 持久文件和地址签发后的正常关闭/只读重开；没有实现 VersionStore、Snapshot/RefHeads、跨文件发布或业务恢复，也没有进程终止、断电、Linux 或包消费资格。没有分配接近 RBF 硬界的 map，也没有测量峰值 RSS/吞吐。1MiB 是工程政策，探针验证其边界执行，不能证明它足够满足所有应用或不会 OOM。
