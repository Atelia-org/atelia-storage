# RootMap 基元组合探针

2026-10-09。实验项目，不属于 solution 或生产 pack；合同权威为 [S4](../../docs/FrameStore-VersionStore/04-versionstore-publication.md) 的 `[F-VS-ROOTMAP-BPV1]`。

直接引用主线 public Binary/Rbf API，组合 `VarUInt32(count) + (ordinary BareString + 12B synthetic address)×count`。Binary 当前依赖精确 K4os.Compression.LZ4 `[1.3.8]`，本探针不调用压缩。SyntheticAddress 只模拟字段宽度和公共数值下界，**不是尚未实施的 FrameAddress codec**。

```powershell
dotnet build experiments/RootMapCodecProbe/Probe.csproj -c Release
dotnet run --project experiments/RootMapCodecProbe/Probe.csproj -c Release --no-build
```

SDK 10.0.201，.NET 10.0.5，Windows；最终 Release 增量 build 为 0 warning / 0 error，run 的 47 个检查通过，原始 stdout 保存于 [结果 JSON](evidence/2026-10-09-result.json)。首次依赖重建出现 41 条已有 Data/Rbf XML 文档 warning，没有修改生产源码以清理它们。

检查覆盖：

- 手写独立 empty/`A` golden，12 个 Ordinal key，包括 empty、NUL、BOM、大小写、未配对 surrogate、组合/预组合字符及 supplementary 字符；逆序编码仍内容等值。
- BPV 合法另一种字符串表示及冗余 count/string header；同一解码 key 的不同表示仍拒重。默认 UTF8/UnicodeEncoding 的 replacement 路径另作对照。
- 每个 golden 短前缀、count 溢出/不可能数量、巨大字符串声明、key 侵占地址 suffix、非法 UTF8、坏 synthetic address、输入重复/null/default 和宿主 trailing 拒绝。
- 内部 prefix 解码保留宿主后字段；RootMap 失败不提交原 cursor。不可能 count 在 map 构造前拒绝，已知 key/suffix 不足在调用 ReadString 前拒绝；观察计数是调用路径，不是分配字节测量。
- 实际编码/读取恰好 1MiB 的单项 map，超过一字节拒绝；给其 count 加一个合法冗余 byte 后，按实际 codeword 大小拒绝。小预算也覆盖等于/超过；公共 RBF 最大 payload 与再加一字节仅做纯 Measure 检查。

80,659 是由 1MiB、最小 count 前缀及每项至少 13B 推出的保守数量界，不是另外的 quota，也不意味着那么多 empty key 合法。实际 header、key bytes 与唯一性会进一步限制数量。

本探针没有持久文件、FrameStore/VersionStore、格式门/header、真实地址签发、barrier/发布/恢复、进程终止、Linux 或包消费资格；没有分配接近 RBF 硬界的 map，也没有测量峰值 RSS/吞吐。1MiB 是工程政策，探针验证其边界执行，不能证明它足够满足所有应用或不会 OOM。
