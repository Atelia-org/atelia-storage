# FrameAddress 变长编码与 RootMap 消费

日期：2026-10-10。起始基线：`7f98782`，工作树干净。用户在 R1–R3 收尾后另行批准本片；它不重新开启原自动队列。规范权威为 [S2](02-framestore-core.md) 的 `[F-FS-FRAME-ADDRESS-VARINT]` 与 [S4](04-versionstore-publication.md) 的 `[F-VS-ROOTMAP-BPV1]`。

## 变更边界

- FrameAddress 新增 `MeasureVarInt()`、`WriteVarInt(BareValueWriter)`、`ReadVarInt(ref BareValueReader)`，复用 Binary 的无符号 Base128 基元；不新增公开数值投影、Span Try 适配层或另一份 VarUInt 算法。
- 固定 12B 的 `EncodedSize/TryWrite/TryRead` 与字节布局保持，继续用于提前尺寸规划、互引与预留回填。两种地址表示由宿主 schema 显式选择，不自动识别、回退或逐条混用。
- 变长地址为 `VarUInt32(FileId) + VarUInt64(SizedPtr.Serialize())`；解码通过 `Deserialize` 恢复并共用原数值检查。trusted Create 和 Append/End 成功交付路径不增加检查。合法地址最短 3B、最长 15B，并非所有值都比固定格式短。
- reader 失败不提交复合 cursor；writer 先拒绝 default，后续 sink 失败沿用 Binary 不保证回滚的合同。数值解码不赋予 store 来源、完成或耐久资格。
- FrameStore 新增 Binary 项目依赖，并间接引用其精确 K4os.Compression.LZ4 `[1.3.8]`；codec 不使用压缩，Binary 不反向依赖存储层。FrameStore 仍为 source-only，不调整生产 pack 清单。
- RootMap 的 Draft 格式选择变长地址；1MiB 上限保持，精确输出尺寸逐地址 Measure，读取按实际消费计算。最小 entry 下界改为 4B，当前地址 suffix 下界改为 3B。CU Members 的 ref ticket 保持固定 8B Packed，不产生票据尺寸循环。

VersionStore 仍未创建。本片在 [RootMapCodecProbe](../../experiments/RootMapCodecProbe/README.md) 中完成字典组合和真实 public FrameStore 地址消费，不宣称已实现生产 ref/tag/CU 发布协议。未发布的 RootMap Draft 直接调整，不增加旧格式兼容 reader；FrameStore 门与数据文件格式不变。

## 验证

状态：**本片源码与组合验证通过。** 源码/测试随本记录同一提交交付；输入与 TRX/探针 hash、计数及实际依赖闭包见[机器记录](evidence/2026-10-10-framestore-varint.json)。旧 [R3](02-framestore-final-acceptance.md) 的源码 hash 和两平台证据保留原身份，不自动覆盖新增 codec。

环境为 Windows 10.0.22000 x64、SDK 10.0.201 / .NET 10.0.5；所有 .NET 操作串行，先 Release build，再匹配配置 `--no-build`：

| 验证 | 实际结果 |
| --- | --- |
| `dotnet build Atelia.Storage.slnx -c Release` | 0 error；41 条既有 Data/Rbf XML 文档 warning，无新增 FrameStore warning |
| `dotnet test Atelia.Storage.slnx -c Release --no-build` | 2203 passed、3 Linux-only skipped、0 failed；FrameStore 417 passed/3 skipped，其中新增 codec 25 个 case 全通过 |
| `eng/Test-Rbf1ReferenceAssets.ps1` | 7 个旧栈项目的精确 RBF1 包版本及来源全部通过；另核对 FrameStore assets 中 Primitives/Data/Rbf/Binary 均为 main project，K4os 为 1.3.8 package |
| RootMapCodecProbe Release build / run | 0 warning/error；118 项检查通过，真实两帧地址在 20B RootMap 中保存、冷重开读回；本次签发地址占 3B |
| 原 FrameStoreConsumerProbe Release build / run | 0 warning/error；6 场景、304 项检查通过，fixed12 自引用/互引/预留回填/取消复用及冷读保持 |

原始 build/test/TRX 与固定消费者结果保存在忽略目录 `artifacts/framestore-varint/`；新版 RootMap 原始结果另提交于 [2026-10-10-varint-result.json](../../experiments/RootMapCodecProbe/evidence/2026-10-10-varint-result.json)。旧 2026-10-09 fixed12 synthetic JSON 原样保留，不覆盖或重标为新版结果。

[新增测试](../../tests/FrameStore.Tests/Format/FrameAddressVarIntTests.cs)覆盖独立最小/header/高位/极值 golden、全部字段宽度、合法冗余表示的真实消费与最短重写、每个短前缀、两字段溢出、数值非法及非零 cursor 回滚、default 零 sink 调用、第二字段输出失败的原异常与已写前缀。独立 reviewer 另以 SizedPtr 位分配和 Base128 数学展开核算六组 golden，主线程对照实际代码、TRX 与 public 探针验收。

RootMap 探针覆盖 4B entry 下界、key/suffix 的分配前检查、全部 code-unit key 与 Ordinal 拒重、宿主 prefix/suffix/trailing、真实已知尺寸 Begin/End 的变长地址一致、1MiB 恰好/超限，以及地址增加合法冗余 byte 后的实际预算拒绝。复核中删除了一条未获需求支持的额外“地址窗口先扣除后续 suffix”文案：当前实现在 key 分配前检查下界，在总 RootMap 预算窗口中读地址，任一后续失败不提交整份字典；不增加第二层地址窗口来限定无产品要求的失败时点。

本轮没有改变存储平台或恢复路径，因此没有重跑进程终止、资源规模和 Linux 矩阵；新增 codec 的运行证据限定为上述 Windows 验证。未取得新的包消费、生产 VersionStore、断电或性能资格，RootMap 的 1MiB 界也不被解释为峰值内存保证。
