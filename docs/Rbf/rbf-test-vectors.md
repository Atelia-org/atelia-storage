# RBF3 测试向量

当前主线仅读写 RBF3。原 §0–§7 的 RBF1 byte-wire 向量和旧恢复案例已移至[移除前快照](https://github.com/Atelia-org/atelia-storage/blob/043f77aa68700568499ceb965c049f8362e4253f/docs/Rbf/rbf-test-vectors.md)，保留 §8 编号以维持现行链接。移除边界见[退役记录](../rbf1-retirement.md)。以下历史生产证据保持原 revision 身份。

## 8. RBF3独立向量与生产资格

本节对应[格式合同](rbf-format.md)及[接口合同](rbf-interface.md)，使用生产Header/Fence `52 42 46 33`（LE `0x33464252`），不复用历史byte RBF2文件。以下hex由独立bitwise CRC32C参考计算：多项式 `0x82F63B78`，init/final XOR均为 `0xFFFFFFFF`，标准检查串 `123456789` 的CRC为 `0xE3069283`；Trailer对 `descriptor LE || tag LE || U LE` 逐byte逆序计算后按BE存储。GOLD-001–003是tag0的字面参考；正式探针的6个Python fixtures使用tag11/17/23/29/31/37，属于另一组独立wire，不把其通过写成这三组字节已逐项公共writer比对。

### 8.1 独立wire参考

下列完整文件均为Header4 + FrameBytes L + Fence4，tag/meta/padding均为0；ticket.Offset=4、ticket.Length=L bytes。

**RBF3-GOLD-001：空payload，Zero成功。** L=28，U=7，K=0，PayloadCRC=0，TrailerCRC=`0xEC007B47`。

```text
52 42 46 33
07 00 00 00
00 00 00 00
EC 00 7B 47 00 00 00 00 00 00 00 00 07 00 00 00
00 00 00 00
52 42 46 33
```

**RBF3-GOLD-002：payload恰为Fence word，tiny选择Key1。** payload=`52 42 46 33`，L=32，U=8，K=1；PayloadCRC=`0x88A3793B`（LE存储），TrailerCRC=`0x931347CA`（BE存储）。编码body不存在aligned Fence。

```text
52 42 46 33
08 00 00 00
53 42 46 33 3A 79 A3 88
92 13 47 CA 01 00 00 00 01 00 00 00 09 00 00 00
01 00 00 00
52 42 46 33
```

**RBF3-GOLD-003：合法高Key与encoded TailLen高位。** 空payload，L=28，U=7，K=`0x80000001`，两plaintext CRC同GOLD-001。encoded TailLenUnits=`0x80000006`，先XOR得到U7再验范围。此向量验证合法wire/reader与Key重建，不要求默认Zero策略为该内容选择高Key。

```text
52 42 46 33
07 00 00 00
01 00 00 80
ED 00 7B C7 01 00 00 80 01 00 00 80 06 00 00 80
01 00 00 80
52 42 46 33
```

### 8.2 长度、CRC及兼容资格

| ID | 独立输入/触发 | 期望 |
| --- | --- | --- |
| RBF3-LEN-001 | U=7、U=2^26-1 | 对应L=28、M=268435452；ticket及读取范围仍为bytes |
| RBF3-LEN-002 | 完整U=0..6、2^26、uint.MaxValue、0x40000007 | range先于shift；拒绝且不得mask/别名成28B |
| RBF3-LEN-003 | U低2bits非零（包括最小U7） | 合法，不要求 U 低 2bits 为零 |
| RBF3-CRC-001 | Trailer用LE(U)封CRC，再XOR | 按原wire字节通过；误改成LE(L)后验CRC应拒绝 |
| RBF3-KEY-001 | 高Key、rawKey==F、encoded Trailer word==F | 高Key合法；后两者拒绝；Key不能因Fence下界省检查 |
| RBF3-COMPAT-002 | RBF1、RBF2或未知Header、0–3B Header | 只读/可写打开均在任何修改前拒绝；bytes不变 |
| RBF3-ADDR-001 | 合法frame起点=SizedPtr.MaxOffset | 末帧末端/Fence可超MaxOffset；不得因单位变更拒绝合法起点 |

### 8.3 全byte前缀、恢复与再次终止

在GOLD-001/002/003及多帧真实RBF3写出上，对最后帧的每个byte cut独立分类；先建立真实B并检查直接前驱结构。原像只作测试oracle，恢复输入不能获取writer计划、Key选择过程或完整目标文件。

| ID | 已有前缀 | 期望 |
| --- | --- | --- |
| RBF3-TAIL-001 | 无suffix/闭合尾 | None；不写不截 |
| RBF3-TAIL-002 | 真实B后的任意1–3B raw HeadLenUnits | Truncated到B，不猜U、无低2bits guard |
| RBF3-TAIL-003 | 完整合法U且4≤R<L-4 | 未完成body截到B，不求Key/partial Trailer或合成墓碑 |
| RBF3-TAIL-004 | 完整body、已有Key 0–3B | K*=encodedTailUnits XOR U；保留body/原ticket，补原Key/Fence缺失后缀 |
| RBF3-TAIL-005 | 完整Key、已有Fence 0–3B | 仅补原Fence缺失后缀 |
| RBF3-TAIL-006 | 完整TrailerCRC/descriptor/Key/padding/Fence矛盾 | 拒绝且不改，不fallback越过首坏候选 |
| RBF3-TAIL-007 | structure健康但payload/PayloadCRC坏，包含待补尾/直接前驱 | Open依结构裁决，原坏内容保留；checked-read拒绝，不能丢帧回退 |
| RBF3-TAIL-008 | 恢复补写Key/Fence每个byte前缀再次终止、SetLength前/后 | 重复恢复同一帧或同一截断边界，幂等；不改旧CRC/body |
| RBF3-TAIL-009 | read-only输入需动作；SetLength/write/flush抛错 | 只读bytes不变；修改异常关闭handle，禁止改动作/复用cache |
| RBF3-TAIL-010 | 正常 RBF3 encoded body | marker-free 正常前缀仅真实 Fence 可定位，不以局部 CRC 倒推成员 |

### 8.4 读取入口与观测资格

- 普通ticket ReadFrame/pooled每次检查两CRC；info管线复用创建时TrailerCRC/Key资格，每次完整读检查PayloadCRC，不要求重复验TrailerCRC。普通ticket读不验外部Fence。forward/reverse/info/meta/boundary/cache及零meta、meta相位1–3、跨chunk均需覆盖，reverse仍不能读头；离线共享Write输入须冻结。
- Header-only逻辑读4B；RBF3非空健康结构Open请求总字节≤39且独立于N/L；异常逆扫≤M+7。记录请求/返回字节、read调用与配置，不能将逻辑上界写成物理SSD或延迟保证。
- Append和Builder真输入使用独立LE(U)/CRC裁判；全部Result拒绝在首次footer修改前。Builder准备异常取消而非同实例重试；真实Push/flush异常永久fault；Pool只归还一次，wire cache不被解码修改。
- 生产进程终止覆盖Append、截尾前/后、补原Key/Fence各byte前缀与再次恢复。有限READY检查点结合writer前缀证明，不声称Windows syscall内任意时机或断电已验证。

**历史生产结果（0999851）**：0999851对应源码内容的Release RBF测试670/670通过，其中RbfWireCodecTests有59项独立scalar codec资格；正式Python6fixtures×Off/Slots16共12views及两份真实writer全wire互证通过。658个Append/Builder小帧cuts、4个Header拒绝、35次managed READY kill、I/O故障及内容/结构正交测试、9个健康Open与2个异常计量、48个7样本成本rows已记录并独立复核。真实最大FrameBytes268435452的caller/pooled和phase1 meta通过。精确身份、PDB源码checksum、扫描跨度与聚合请求的区别、性能分配及独立裁判限度见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)；GOLD-001–003仍按上文保持字面参考身份。下游、整仓solution、冷盘/断电/任意syscall终止、pack/publish不属于本轮RBF资格。

## 变更日志

| 日期 | 版本 | 变更 |
|------|------|------|
| 2026-10-03 | 0.41 | 保留RBF1历史byte-wire身份；增加独立RBF3 units/Key/CRC参考与三动作/双读/异常资格；生产测试与另一组Python wire互证结果见实施记录§11 |
| 2026-02-01 | 0.40 | **Breaking Change 适配**：完整重写以对齐 rbf-format.md v0.40；FrameBytes 布局重构（旧 FrameStatus → 新 FrameDescriptor + TailMeta + 固定 TrailerCodeword）；双 CRC 机制（PayloadCrc + TrailerCrc）；最小帧长度 20→24；删除所有 FrameStatus/StatusLen 相关测试向量；新增 FrameDescriptor/TailMeta/TrailerCodeword 测试向量；新增 §3.3 CRC 职责分离测试；新增 §4.3 逆向扫描 TrailerCrc 测试；新增 §5 RbfFrameInfo 与 TailMeta 测试 |
| 2026-01-12 | 0.13 | **Tombstone 过滤测试**：更新 SCAN-EMPTY-002 拆分为两个子用例；新增 SCAN-TOMBSTONE-FILTER-001 |
| 2026-01-07 | 0.12 | **SizedPtr 迁移**：将旧版地址指针相关测试向量迁移为 `SizedPtr` + `ReadFrame` 行为向量 |
| 2025-12-28 | 0.11 | 更新关联规范版本：rbf-format.md v0.15 → v0.16, rbf-interface.md v0.16 → v0.17 |
| 2025-12-28 | 0.10 | 更新关联规范版本：rbf-interface.md v0.15 → v0.16 |
| 2025-12-28 | 0.9 | 新增 §4 ScanReverse 接口行为 |
| 2025-12-28 | 0.8 | 适配 rbf-format.md v0.15：修正 RBF-BAD-004 最小帧长度边界 |
| 2025-12-25 | 0.7 | 适配 rbf-format.md v0.14：FrameStatus 位域格式 |
| 2025-12-24 | 0.6 | 适配 rbf-format.md v0.12：Pad→FrameStatus |
| 2025-12-24 | 0.5 | 适配 rbf-format.md v0.11：FrameTag 扩充为 4B |
| 2025-12-23 | 0.4 | 适配 rbf-format.md v0.10+：术语更新 |
| 2025-12-23 | 0.3 | 适配 rbf-format.md v0.10：统一 Fence 术语 |
| 2025-12-23 | 0.2 | 适配 rbf-format.md v0.9：更新条款 ID 映射 |
| 2025-12-22 | 0.1 | 初始版本：从 mvp-test-vectors.md 提取 Layer 0 测试向量 |
