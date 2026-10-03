---
docId: "rbf-format"
title: "RBF 二进制格式规范（Layer 0）"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 二进制格式规范（Layer 0）
**文档定位**：Layer 0，定义 RBF 文件的线格式（wire format）。
文档层级与规范遵循见 [README.md](README.md)。

**状态**：RBF1/RBF3合同已同步，RBF3实施验收待完成 | **版本**：0.41 | **创建日期**：2025-12-22

2026-10-03：用户授权落实RBF3。历史RBF1布局与byte向量保留；历史实验RBF2不是生产格式，旧实验hash/结果不得重标为RBF3证据。新向量及待验证事项见[测试向量§8](rbf-test-vectors.md#8-rbf3独立向量与生产资格)。

## 1. 范围与分层
本文档（Layer 0）只定义：
- RBF 文件的 **线格式（wire format）**：Fence/Magic、Frame 字节布局、对齐与 CRC32C
- 损坏判定（Framing/CRC）与恢复相关的扫描行为（Reverse Scan / Resync）

本文档不定义：
- Frame payload 的业务语义（由上层定义）

---

## 2. 常量与 Fence

### 2.1 Fence 常量定义

本节定义 @`Fence` 的线格式常量值。
关于 @`Fence` 的布局模式与 @`HeaderFence` 定义，参见 @[F-FENCE-IS-SEPARATOR-NOT-FRAME](rbf-decisions.md)。

## spec [F-FENCE-RBF1-ASCII-4B] Fence值定义

| 属性 | 值 |
|------|-----|
| 值（Value） | `RBF1`（ASCII: `52 42 46 31`） |
| 长度 | 4 字节 |
| 编码 | ASCII 字节序列写入（非 u32 端序），读取时按字节匹配 |

### spec [F-FENCE-RBF3-ASCII-4B] RBF3 Fence值定义

| 属性 | 值 |
| --- | --- |
| 值 | `RBF3`，ASCII bytes `52 42 46 33`；按LE u32解释为 `F=0x33464252` |
| 长度 | 4 bytes |
| 下界 | `F≥2^26`，即 `0x04000000`（67108864） |

### spec [F-HEADER-PROFILE-DISPATCH] 文件级profile分派

文件HeaderFence MUST 完整匹配RBF1或RBF3，并由owned handle一次确定整个文件的profile；MUST NOT 用EOF、试解码或payload magic派发。新建文件 MUST 只使用RBF3；RBF1只读保留原字节、旧ticket与容量，普通可写打开在修改前拒绝。未知Header、0–3B Header及实验RBF2 MUST 拒绝。流中所有Fence MUST 与HeaderFence相同；不支持mixed，也不提供公共codec配置或长度模式。

---

## 3. Wire Layout

### spec [F-FRAMEBYTES-LAYOUT] FrameBytes布局
FrameBytes从raw头长度字段开始，不包含前后Fence。profile MUST 由 @[F-HEADER-PROFILE-DISPATCH] 确定：下表保留RBF1 byte布局，RBF3采用 @[F-RBF3-FRAMEBYTES-LAYOUT]。两者public ticket、offset、buffer与内容长度均以bytes表示。
*FrameBytes **不包含** 前后 Fence。*

#### RBF1：旧byte长度布局

PayloadCodeword:
| 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|
| HeadLen | u32 LE | 4 | FrameBytes 总长度（不含 Fence） |
| Payload | bytes | N | `N >= 0`；业务数据 |
| TailMeta | bytes | M | `M >= 0`；用户元数据（原 PayloadTrailer） |
| Padding | bytes | 0-3 | 为对齐进行的填充 |
| PayloadCrc32C | u32 LE | 4 | Crc32C(Payload+TailMeta+Padding) |

TrailerCodeword (Fixed 16 Bytes):
| 字段 | 类型 | 长度 | 说明 |
|------|------|------|------|
| TrailerCrc32C | u32 **BE** | 4 | Crc32C(FrameDescriptor+FrameTag+TailLen) |
| FrameDescriptor| u32 LE | 4 | 描述符：Tombstone, PaddingLen, TailMetaLen |
| FrameTag | u32 LE | 4 | 帧类型标识符（见 @[F-FRAME-TAG-WIRE-ENCODING]） |
| TailLen | u32 LE | 4 | MUST 等于 HeadLen |

### spec [F-RBF3-FRAMEBYTES-LAYOUT] RBF3单尾Key布局

令物理FrameBytes长度为L bytes、wire长度为U个4B units。MUST 有 `L=4U`，`28≤L≤M=2^28-4=268435452`，`7≤U<2^26`；最大U为67108863。

```text
raw HeadLenUnits LE(U) 4B
XOR_K(Payload || TailMeta || zero Padding || PayloadCRC LE || TrailerCodeword16)
raw TailKey LE(K) 4B
后接 RBF3 Fence 4B（不计入L）
```

TrailerCodeword明文仍为16B：`TrailerCRC BE || FrameDescriptor LE || FrameTag LE || TailLenUnits LE(U)`。HeadLenUnits不参与XOR，TailLenUnits及两项存储CRC参与连续body XOR。新固定开销为28B，payload起点仍为frame+4；相同物理上限下payload+TailMeta最大容量比RBF1少4B。相邻帧共享一个Fence。

| 区域 | 相对FrameBytes起点的byte范围 |
| --- | --- |
| raw HeadLenUnits | `[0,4)` |
| Payload+TailMeta+Padding | `[4,L-24)` |
| encoded PayloadCRC | `[L-24,L-20)` |
| encoded TrailerCodeword | `[L-20,L-4)` |
| encoded TailLenUnits（body末word） | `[L-8,L-4)` |
| raw TailKey | `[L-4,L)` |
| 外部Fence | `[L,L+4)` |

### spec [F-RBF3-LENGTH-VALIDATION] units先验与byte归一化

读取完整raw HeadLenUnits或解码后的TailLenUnits时，MUST 先检查完整uint32值 `7≤U<2^26`，再计算 `L=U<<2`；MUST NOT mask高bits、先shift后验范围或要求U低2bits为零。`0x40000007` 等非法字段不能因uint32左移别名为28B而通过。encoded TailLenUnits可占全uint32，MUST 先按Key XOR还原再验证U。

序列化 MUST 先由byte布局得到合法L，再写 `U=L>>2`。只在profile的具体wire序列化/解析边界换算；内部FrameInfo/layout、SizedPtr与所有API长度继续使用bytes，不新增公开units类型或第二份持久长度。

### spec [F-RBF3-XOR-MARKER-FREE] 连续word XOR与真实边界

Key为完整uint32，MUST 满足 `K!=F`。plaintext body按连续LE uint32 words划分，`m=(L-8)/4=U-2`；编码与解码均为 `E(w)=w XOR K`。Writer MUST 在完整plaintext CRC/Trailer完成后选择Key，使每个encoded body word都不等于F；跨span/chunk MUST 保留word carry及累计byte相位，不能重置相位。默认选择使用Data的ZeroThenTinyBitmapRandom，不要求非零键最小、确定性wire或有限随机尝试次数。

raw HeadLenUnits满足 `U<2^26≤F`，raw TailKey不等于F，body没有aligned F，物理长度4U维持全局4B网格。因此正常writer的任意顺序写出byte前缀中，完整aligned Fence只在真实边界出现。仅禁止全局4B aligned Fence，不禁止非对齐滑动窗口。此证明限进程终止且OS/文件系统继续运行，不认证任意位损坏、手工镜像、断电或设备乱序。

Open只检查其已读取的encoded Trailer words不等于F，不遍历完整body重证writer invariant。普通ticket完整读解码后检查两项CRC、长度、descriptor与padding；FrameInfo完整读按接口合同复用已验证Trailer/Key资格并检查本次PayloadCRC。两种入口均不要求另作完整encodedbody marker资格扫描。完整body与raw Key的输出约束由writer建立，不能以CRC代替主链成员证明。

每个body word最多排除一个Key；RBF3的 `m≤2^26-3<F`，故 `[0,m]` 至少有一个可用且不等于F的键，完整bitmap至多8MiB。FullBitmap只为对照/未来候选，不是默认实现；该界不能推广到Data近2GiB int域输入。全uint32随机路径仍须独立拒绝Key==F。

### spec [S-RBF-PAYLOADLENGTH-FORMULA] 物理布局与内容容量

两profile均先得到合法物理长度L，再计算：`PayloadLength=L-FixedOverhead-TailMetaLength-PaddingLength`，结果 MUST 非负；`TailMetaLength≤65535`，`PaddingLength≤3`。RBF1 FixedOverhead=24B、最小L=24；RBF3 FixedOverhead=28B、最小L=28。物理L最大均为SizedPtr.MaxLength，最大payload+TailMeta分别为M-24与M-28。Padding仍由 @[F-PADDING-CALCULATION] 计算，存储值必须为零。

### spec [F-FRAME-TAG-WIRE-ENCODING] FrameTag线格式编码
FrameTag 是 4 字节 u32 LE 帧类型标识符，位于 Frame 尾部。
RBF 层不保留任何 FrameTag 值，全部值域由上层定义。

### spec [F-FRAME-DESCRIPTOR-LAYOUT] FrameDescriptor布局
FrameDescriptor 是 Frame 尾部 TrailerCodeword 中的 4 字节控制字（u32 LE），统一编码元属性。

| Bit (MSB-LSB) | 字段 | 说明 |
|:--------------|:-----|:-----|
| 31 | IsTombstone | 1 = 墓碑帧，0 = 正常帧 |
| 30-29 | PaddingLen | Payload 填充字节数 (0-3) |
| 28-16 | Reserved | 保留位，MUST 为 0 |
| 15-0 | TailMetaLen | 用户元数据长度 (0-65535) |

### spec [F-TRAILER-CRC-BIG-ENDIAN] TrailerCrc32C按大端序存储
为了逆序用CRC扫描Codeword时兼容检查固定CRC residual的算法，TrailerCrc32C MUST 按BigEndian存储。*逐字节逆序CRC时，等效于顺序LE。*

**反误用护栏（Normative）**：仅 `TrailerCrc32C` 为 BE 存储；TrailerCodeword 中其余字段（`FrameDescriptor`、`FrameTag`、`TailLen`）仍按各自 u32 **LE** 解码。

*示例（Informative）*：给定 TrailerCodeword 字节序列 `[AA BB CC DD] [11 22 33 44] [55 66 77 88] [99 AA BB CC]`：
- `TrailerCrc32C` = `0xAABBCCDD`（按 BE 读取前 4 字节）
- `FrameDescriptor` = `0x44332211`（按 LE 读取第 5-8 字节）
- `FrameTag` = `0x88776655`（按 LE 读取第 9-12 字节）
- `TailLen` = `0xCCBBAA99`（按 LE 读取第 13-16 字节）

### spec [F-TRAILER-CRC-COVERAGE] TrailerCrc32C覆盖范围
`TrailerCrc32C` MUST 覆盖 TrailerCodeword 中除自身以外的所有字段。
> TrailerCrc32C = crc32c_backward(FrameDescriptor LE + FrameTag LE + profile尾长度 LE)

- MUST 覆盖实际plaintext wire bytes：FrameDescriptor (4B) + FrameTag (4B) + profile尾长度 (4B)。RBF1尾长度存L，RBF3尾长度存U；RBF3覆盖的是LE(U)，MUST NOT 先把末字段改写成L再验CRC。
- MUST NOT 覆盖：HeadLen、Payload、TailMeta、Padding、CRC32C、TrailerCrc32C 本身、任何 Fence。

CRC先在plaintext上计算并存储，然后才对完整body XOR；读端先解码Trailer再按其原wire字节验证。PayloadCRC按覆盖字节正向计算；TrailerCRC从末尾向前逐byte计算，其结果按BE存储。遍历方向与字段存储端序是两个独立规则。

### spec [F-PADDING-CALCULATION] Padding长度计算
**depends:[S-RBF-DECISION-4B-ALIGNMENT-ROOT]**
**depends:[F-FRAMEBYTES-LAYOUT]**

为满足 4 字节对齐约束，`Padding` 长度由 `Payload` 和 `TailMeta` 的总长度决定：
> PaddingLen = (4 - ((PayloadLen + TailMetaLen) % 4)) % 4

Writer MUST 将计算出的 `PaddingLen` 填入 `FrameDescriptor` 的 Bit 30-29，并在 `TailMeta` 之后写入相应数量的填充字节（值为 0）。Reader MUST 使用 `FrameDescriptor` 中的 `PaddingLen` 来正确定位 `TailMeta` 和 `Payload` 的边界。

---

## 4. CRC32C

### 4.1 覆盖范围

### spec [F-PAYLOAD-CRC-COVERAGE]  PayloadCrc32C覆盖范围
> PayloadCrc32C = crc32c(Payload + TailMeta + Padding)

`PayloadCrc32C`（即 PayloadCodeword 中的 Checksum）MUST 覆盖：Payload、TailMeta 和 Padding。
`PayloadCrc32C` MUST NOT 覆盖：HeadLen、TrailerCodeword、PayloadCrc32C 本身、任何 Fence。
RBF3额外raw TailKey同样不在PayloadCRC覆盖内；PayloadCRC仍对plaintext coverage计算，不对encoded bytes计算。

### 4.2 算法约定

### spec [F-CRC32C-CASTAGNOLI-REFLECTED] CRC32C算法约定
CRC 算法为 CRC32C（Castagnoli），采用 Reflected I/O 约定：
- 多项式（Normal）：`0x1EDC6F41`
- 多项式（Reflected）：`0x82F63B78`
- 初始值：`0xFFFFFFFF`
- 最终异或：`0xFFFFFFFF`
- 输入/输出：bit-reflected

规范参考：IETF RFC 3720 Appendix B (iSCSI CRC)。

*注（非规范性）：在 .NET（.NET 6+）可使用 `System.Numerics.BitOperations.Crc32C(uint crc, byte data)` 作为逐字节累加原语；需对每字节循环调用，并在开始前应用初始值、结束后应用最终异或。*

---

## 5. 损坏判定与失败策略

### spec [F-FRAMING-FAIL-REJECT] Framing校验失败策略
Reader MUST 按已确定profile验证{§2、§3、§4}中的结构、对齐与值域约束；writer的全body marker-free输出条件与读端资格范围按 @[F-RBF3-XOR-MARKER-FREE] 区分。派生清单可能滞后，MUST 以本规范为准。
任何违反上述 SSOT 约束的情况（包括但不限于保留位非零、对齐错误或 Fence 不匹配），Reader MUST 视为 Framing 校验失败（损坏）。
具体失败策略（例如：逆向扫描的硬停止 vs 修复工具的 Resync）由相应操作条款定义。

### spec [F-CRC-FAIL-REJECT] CRC校验失败策略
CRC32C 校验不匹配 MUST 视为帧损坏。
Reader MUST NOT 将损坏帧作为有效数据返回。

普通Open只做结构资格，不验证PayloadCRC；RBF1仅只读全结构链，RBF3可写按[接口合同](rbf-interface.md#spec-s-rbf-open-recovers-single-incomplete-tail-普通打开与单尾帧恢复)截掉未完成body或补原Key/Fence后缀。完整body通过TrailerCRC及结构后，MUST 以 `K*=LE_u32(encoded TailLenUnits) XOR U` 重建原Key，并验证已有Key/Fence前缀；MUST NOT 补零或合成墓碑。内容CRC错误由checked-read拒绝，不能借此丢帧；已呈现完整结构错误仍拒绝且不修改。

---

## 6. 逆向扫描与 Resync

### 6.1 逆向扫描（Reverse Scan）

### spec [R-REVERSE-SCAN-RETURNS-VALID-FRAMES-TAIL-TO-HEAD] 逆向扫描契约
本条款定义 Reverse Scan 的**规范性契约（可观察行为）**。

Reverse Scan MUST 满足：
1. **输出定义**：输出为"通过 framing 校验的 Frame 元信息序列"，顺序 MUST 为 **从尾到头**（最新在前）。
2. **合法性判定（SSOT）**：候选 Frame 是否可被产出 MUST 以 §3 的布局约束与 §5 的 framing 规则为准，并且 MUST 通过尾部元信息校验（见 @[R-REVERSE-SCAN-USES-TRAILER-CRC]）。
3. **CRC 职责分离**：Reverse Scan MUST NOT 校验 `PayloadCrc32C`；完整帧的 Content 校验由随机读取路径负责。
4. **失败策略**：当候选 Frame 的 framing 或尾部元信息校验失败时，Reverse Scan MUST 硬停止（终止迭代），不得尝试 Resync 继续扫描。

### spec [R-REVERSE-SCAN-USES-TRAILER-CRC] 逆向扫描使用TrailerCrc32C校验尾部元信息
为满足 @[S-RBF-DECISION-REVERSESCAN-TAIL-ORIENTED](rbf-decisions.md)，Reverse Scan MUST 在不读取 Frame 头部字段的前提下校验尾部关键元信息。

- Reverse Scan MUST 验证 `TrailerCrc32C`（覆盖范围见 @[F-TRAILER-CRC-COVERAGE]）。
- Reverse Scan MUST NOT 读取 `HeadLen` 也 MUST NOT 执行 `HeadLen == TailLen` 的交叉校验。

### derived [H-REVERSE-SCAN-TAIL-ORIENTED-RATIONALE] 尾部导向逆向扫描设计理由
- 大帧场景下，逆向扫描只需读取尾部附近的定长 TrailerCodeword 即可迭代元信息，避免对 payload 的读取。
- 用 `TrailerCrc32C` 替代 `HeadLen == TailLen`，将可靠性焦点移到“尾部元信息”的可验证性。

### 6.2 Resync 规则

### spec [R-RESYNC-SCAN-BACKWARD-4B-TO-HEADER-FENCE] Resync行为规则
Resync 为“恢复/修复工具路径”的能力：用于在存在损坏数据时尽可能找回后续 Fence 边界。

- 当工具选择执行 Resync 时，MUST NOT 信任损坏候选的 TailLen 做跳跃。
- Resync 模式 MUST 以 4 字节为步长向前搜索 Fence。
- Resync 扫描 MUST 在抵达 HeaderFence（偏移 0）时停止。

本轮离线RbfRecovery/Fence search/Trailer search/显式truncate工具只保留RBF1语义，对RBF3及未知Header MUST 显式拒绝；未来RBF3离线救援另案。普通RBF3 Open的真实边界定位与单尾动作不属于坏候选fallback Resync。

---

## 7. SizedPtr 与 Wire Format 的对应关系（Interface Mapping）

本节用于定义“接口层凭据（`SizedPtr`）”与“线格式（FrameBytes）”之间的对应关系。
这是跨层映射规则，不是对 wire layout 的重复定义。

### spec [S-RBF-SIZEDPTR-WIRE-MAPPING] SizedPtr与FrameBytes映射
当上层以 @`SizedPtr` 表示一个 Frame 的位置与长度时：
- `OffsetBytes` MUST 指向该Frame的raw头长度字段起点（即FrameBytes起点）。
- `LengthBytes` MUST 等于物理FrameBytes长度L，不含Fence：RBF1为HeadLen，RBF3为 `4*HeadLenUnits`。
- `OffsetBytes`与L均须4B对齐，起点不超过SizedPtr.MaxOffset；末帧末端及外部Fence MAY 超过MaxOffset，MUST NOT 额外限制合法起点/容量。

### derived [H-RBF-SCANREVERSE-TAILLEN-IS-SSOT] 逆向扫描以TailLen为长度SSOT
为满足 @[S-RBF-DECISION-REVERSESCAN-TAIL-ORIENTED](rbf-decisions.md)，逆向扫描在定位上一帧时 MAY 以 `TailLen` 作为长度 SSOT。
这允许Reverse Scan不读取raw头字段：RBF1直接从TailLen得到L，RBF3从TailKey解码TailLenUnits、先验范围后得到L。随机读取的权威映射是SizedPtr.Length等于物理L（RBF3为4U），不是等于wire U。

---

## 9. 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.41 | 2026-10-03 | RBF1只读旧byte布局保留；增加RBF3 Header、4B units、单尾Key、28B开销、marker-free与原LE(U) TrailerCRC；同步结构Open/补原后缀和byte ticket映射；生产验收待完成 |
| 0.40 | 2026-01-24 | **Wire Format Breaking Change**: 重构 Trailer 结构 为固定 16 字节的 `TrailerCodeword`；引入 `FrameDescriptor` (u32) 统一管理 Padding/TailMetaLen/Tombstone；重命名 PayloadTrailer 为 TailMeta；引入 `PayloadCrc32C` 与 `TrailerCrc32C` 双校验机制 |
| 0.32 | 2026-01-10 | 修正 @[F-CRC32C-CASTAGNOLI-REFLECTED]：删除对不存在的 `.NET System.IO.Hashing.Crc32C` 的引用，改为引用 RFC 3720；添加 `BitOperations.Crc32C` 作为非规范性实现提示 |
| 0.31 | 2026-01-09 | **AI-Design-DSL 格式迁移**：将所有条款标识符转换为 DSL 格式（design/hint/term）；将设计理由拆分为独立 hint 条款；添加 @`DataTail` 术语定义 |
| 0.30 | 2026-01-07 | §3.2 @[F-FRAMEBYTES-LAYOUT] 的 FrameStatus 描述去除对齐语义双写，改为引用 @[F-STATUSLEN-ENSURES-4B-ALIGNMENT]（由公式定义 StatusLen 并保证 payload+status 对齐） |
