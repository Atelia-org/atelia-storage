# RBF3：有界初始帧前缀核验

状态：**Draft；2026-10-09 选定支撑私有初始化残留裁决的机制，尚未提供生产 public API**。本合同只新增纯核验能力，不修改已 Accepted 的 S1、现有 Open/恢复或 RBF1/RBF3 格式。前置为 [RBF 接口](rbf-interface.md)、[RBF3 格式](rbf-format.md)与现有 Data CRC/XOR 核。

## 必要性与范围

一个上层私有文件可能在 HeaderFence 或第一帧顺序输出中断后留下短前缀。现有可写工厂先恢复再返回，不能用它核验“改写前是否仅含允许初始化”；只读工厂又拒绝不完整尾。为核验这种受限内容而在上层复制 wire encoder，或另建临时 RBF 生成比较镜像，会增加格式权威或 I/O/残留。

本入口只回答：这些实存 bytes 是否是指定内容的一份合法 RBF3 初始单帧文件的前缀？它不打开文件、不恢复、不删除、不认证历史作者，也不签发主链成员、地址、完成或耐久资格。当前设计消费者为 FrameStore 私有 data 初始化；具体私有角色、身份与清理授权由上层协议给出。

### spec [A-RBF-INITIAL-FRAME-PREFIX] 核验预期初始单帧的有界字节前缀

拟新增唯一纯入口；这是待实施合同：

```csharp
// RbfFile 的新增静态方法
public static bool IsInitialFramePrefix(
    ReadOnlySpan<byte> prefix, uint tag, ReadOnlySpan<byte> payload);
```

预期文件固定为 RBF3 HeaderFence 后的一帧，指定 tag/payload、TailMetaLength=0、非墓碑及合法 zero padding/两项 CRC/Key/尾 Fence。payload 长度 MUST 为 0..232B；超过上界抛 ArgumentOutOfRangeException。该上界使完整 plaintext body 至多 63 words，首版只提供这个有界域，不增加 meta/墓碑/历史 profile、任意文件前缀或通用输出镜像 API。

对合法参数，返回 true 当且仅当 prefix 是**某个合法 Key**下的预期文件 codeword 的等长初始部分，含空前缀和完整文件；超过完整长度或不兼容返回 false。MUST 核对每个已观察字节，包括已有 HeadLen/body/raw Key/Fence 的部分字节；不能按 4B 对齐截掉末尾。核验不绑定默认 selector 的最小键或确定性，不把当前 writer 的唯一输出提升为 wire 法。

完整文件匹配包含预期 payload、形状、padding 和两项 CRC 的资格；短前缀只获得所观察范围的兼容资格。尚未出现的身份/CRC bytes 不能被称作已校验，true 也不能单独授权删除任意文件。MUST 不执行 I/O、恢复、输出、RNG、callback 或堆 buffer 分配；输入同步借用，调用期间保持稳定。

## 最小机制与可行性依据

RBF 内部复用现有 layout、plaintext footer/CRC 与 Data XOR 核，准备至多 252B 的预期 plaintext body；完整初始化镜像最多 268B。不要在调用层复制这些 wire 规则。

1. 逐 byte 比较已有 HeaderFence/HeadLen 与预期固定部分；先拒绝超长或固定部分不符。
2. 不足第一个完整 body word 时，已观察 body 只约束 Key 的至多 3 个 bytes，至少还有 256 个补全。63 个 body words 加上 `Key != Fence` 至多禁用 64 个 Key，因此必有合法补全；无需随机或枚举尝试。
3. 首个完整 body word 存在时，由实际 encoded word XOR 预期 plaintext word 得到唯一 Key。拒绝 Key==Fence，验证**完整预期 body** 经该 Key 编码后没有 aligned Fence；随后比较全部实存 body/raw Key/尾 Fence bytes。

这是一个受限 codeword 前缀谓词，不是 raw 文件 parser 或修复器；不存在可替代上层发布事实的新 token/状态。一般 RBF3 合法 Key 不要求最小或确定性，故直接重跑当前 selector 生成唯一镜像不是本合同。

[有界探针](../../experiments/PrivateInitializationPrefixProbe/README.md)在 Windows / .NET 10.0.5 / E: 通过 3,139 项断言：真实 writer 的 0/4/高位 Key、0..60B 各长度的实际部分输出、完整文件逐 bit 损坏、身份/后缀和另一份 36B 合法小帧反例，以及 0..232B 域的边界样本。高位 Key 使用既有 internal seam；原型使用现有 RBF 内部 core，尚非 public 入口。该实验证明有界机制可执行，不证明 FrameStore、普通类型/no-follow、清理、进程 kill、Linux、rename、断电或包资格。

## 实施与验收交接

实施时把谓词放在 RBF 并复用生产 core，补公开消费和独立向量，确认任何拒绝都不修改输入或文件；未完成前，上层不能用可写 Open、宽松长度检查或 internal friend 绕过这项准入。实现的局部函数/栈布局由实现者选定。

验收包括固定区域各截短、部分 Key bytes、推导到禁键、合法非默认 Key、body marker-free、所有已有 closure bytes、超长、完整 CRC/预期字段不符、padding 余数和 232/233B 边界。源码资格、上层残留清理、平台中断及包资格各自取得；本设计不重标现有 S1 或 RBF 的历史证据。

