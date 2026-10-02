# RBF2 writer 预处理实验接点

本目录比较单份尾 Key 方案的预处理成本，生产 RBF 仍写 RBF1。实验不改变生产源码、故障语义或文件格式入口。真实文件写入、重开与系统调用实验由 runner 放到 `W:`；`PrototypeCodec` 本身只访问内存。

## 代码证据与最小接点

- [`RbfAppendImpl`](../../src/Rbf/Internal/RbfAppendImpl.cs) 的 `WriteWithCrc` / `WriteMidWithCrc` 在写出的同时递增 PayloadCRC；大块 payload、TailMeta 可直接交给 `RandomAccess.Write`，头尾使用至多 4KiB 缓冲。新 writer 在选 Key 前须知道 PayloadCRC、TrailerCodeword 和全部 body words，因此不能沿用“边首次看内容、边发布 encoded bytes”的顺序。
- [`RbfFileImpl.CommitFromBuilder`](../../src/Rbf/Internal/RbfFileImpl.cs) 在参数和 pending reservation 检查之后追加 padding、获取 CRC、追加 tail、回填 HeadLen，最后提交 HeadLen reservation。首 reservation 尚 pending 时，[`SinkReservableWriter.FlushCommittedData`](../../src/Data/SinkReservableWriter.cs) 停止向 sink 发布，现 Builder 已持有全帧的未提交字节。
- `SinkReservableWriter.GetCrcSinceReservationEnd` 能只读遍历 pending 数据计算 CRC，但 `GetActiveChunks` 是 private。实验的 `byte[][] Chunks` 是已冻结的 payload+meta chunk 视图占位，不能当作生产 Builder 已有同名 API。最窄生产接点仍是对 pending 范围作一次同步只读遍历；编码留在 RBF，并在真正发布时使用累计 body byte offset。
- [`RollingCrc`](../../src/Data/Hashing/RollingCrc.cs) 的 PayloadCRC 为 forward，TrailerCodeword 为 backward。原型直接调用这些实现：PayloadCRC 存 LE；Trailer 前 4B CRC 存 BE，余下 descriptor/tag/TailLen 存 LE。

## 四种 Key 搜索

设输入长为 `I`，padding 为 `P=(-I)&3`，FrameLength 为 `L=I+P+28`，body word 数 `m=(L-8)/4`。对 plaintext word `w`，禁用 Key 为 `w XOR 0x32464252`。只标记候选 `[0,m]` 即足够：m 个 word 最多排除 m 个候选。四种策略均返回最小未被禁用的 Key，用同一输出互证。

| 策略 | 第一次禁 Key 扫描 | 何时 fallback | 临时 bitmap 数据空间 |
| --- | --- | --- | --- |
| FullBitmap | 标记 `[0,m]` 的完整 bitmap | 无 | `4*ceil((m+1)/32)`，上限 8MiB |
| SmallBitmap | 标记 `[0,min(m,255)]`，32B stack bitmap | 256 个候选全被禁用时完整 bitmap 再扫 | 32B；fallback 时至多 8MiB + 32B |
| ZeroFirst | 相位对齐的 uint span `Contains(Fence)`；跨 chunk word 单独拼接 | Key0 被禁用时完整 bitmap 再扫 | 常见分支无 bitmap；fallback 至多 8MiB |
| ZeroThenOne | 先同 ZeroFirst 检测 Key0，被禁用再用 `Contains(Fence XOR 1)` 检测 Key1 | Key0、Key1 都被禁用时完整 bitmap 再扫 | Key0/Key1 分支无 bitmap；fallback 至多 8MiB |

上限按 `MaxFrameLength=(1<<28)-4` 计算。小 bitmap 的 fallback 是确定性正确路径，不是 retry 猜 Key。定向 Key 检测遇到第一个禁止 word 即可停扫，后续定向或完整 bitmap 从 body 起点重新扫描。各策略 fallback 不再计算 PayloadCRC。ZeroThenOne 只增加固定的 Key1 检测，没有逐 Key 无界循环；最小帧也有 m=5，因此 Key1 一定在充分候选范围内。最先可用的 Key0/Key1 立即返回，否则由完整 bitmap 得到相同 mex，至多开始三次禁 Key 扫描。

CRC 与禁 Key 扫描是不同 loop，本原型没有宣称“同一次内存访问完成 CRC 和筛 Key”。FullBitmap/SmallBitmap/ZeroFirst 没有 fallback 时，输入经历一次 CRC 遍历、一次禁 Key 遍历，再由发布路径读取和编码；fallback 加一次禁 Key 遍历。ZeroThenOne 的 Key1 分支另有一次定向扫描，完整 fallback 至多另加两次扫描。`BodyWordScanPasses` 只计已开始的禁 Key 扫描，定向扫描可能提前停止。`BitmapBytes` 为 Prepare 分配的 heap bitmap 数据字节；`ScratchBytes` 为峰值临时 bitmap 数据空间，包含小 bitmap 的 32B stack 空间，排除 CLR 对象头及固定大小游标。

完整 body 的所有 word 都参加禁 Key 搜索，包括跨 payload/meta chunk 的 word、零 padding、PayloadCRC 和 TrailerCodeword。Footer 为最多 23B 的独立明文数组，仅含 padding+PayloadCRC+Trailer；原输入没有被复制或修改。Scanner 保留最多 3B carry，chunk 长度或空 chunk 不改变相位。Footer 开始的 body byte offset 为 `CoverageLength-PaddingLength`；TrailerCodeword 的起点才是 `CoverageLength+4`。

### uint32 模加法候选

若完整 32-bit word 的编码改为 `unchecked(w+Key)`，禁用 Key 为 `unchecked(F-w)`，仍然是每个 word 排除一个候选，因此 `[0,m]` bitmap 和 m+1 存在性证明不变。Key0 的禁用条件也仍为 `w==F`，可以复用相同零 Key 检测；Full/Small 的标记循环只把 XOR 换成减法。它不减少 CRC、禁 Key 扫描或 bitmap 分配的阶段数，是否降低指令成本须由 root 的候选 kernel 实测。

模加法需要把跨 chunk word 拼接完整后作加法，再按字节输出；读入必须减去 Key。XOR 的重复四字节 mask 可以任意 byte slice 转换，模加法的进位/借位使非对齐 slice 需要邻接 word 的字节或 carry 上下文。特别是 payload/meta 边界和结构 Open 的 1–3B padding 检查，不能直接照搬 XOR 的局部 byte 解码。本次不为模加法增加新格式或公共 codec 抽象，先分别检验完整 word kernel 成本与局部结构读取可执行性。

## 发布与故障语义

`PreparedFrame` 借用原 `byte[][]` 及每个 chunk；从 Prepare 开始到发布结束，数组身份、长度与字节必须稳定。公开数组是实验的简单载体，不是新的可变生产缓存契约。Prepare 只作校验、CRC 和 Key 选择，可在任何文件写入前完成；参数失败没有发布，也不令 writer faulted。

`Serialize` 是逐字节互证用的可选连续输出入口：写 raw HeadLen，经 `XorTransform.Copy` 按累计 body offset 编码每个原 chunk 及 Footer，再写 raw TailKey 和 Fence。它不要求生产路径先分配完整 encoded frame。流式发布可复用 bounded scratch：Key0 的原输入可直接写；非零 Key 用固定大小缓冲转换，每次转换仍传入原 body 的累计 offset。是否把头尾并入一次 write、是否采用 pool、缓冲多大，应由实测决定。

发布顺序仍是单 writer 的顺序 append；文件写入开始之后的 write/flush 异常必须沿当前 RBF 的 faulted→Dispose/reopen 边界处理。原型不模拟取消、不改变 ticket、不提供断电保证。进程终止恢复仍依靠未闭合前缀的截断或完整 body 的尾 Key/Fence 补写，Key 选择不能在发布过程中依未冻结输入变化。

## 验收与待测项

独立验收应比较四策略 Key 与完整 wire，校验两项 CRC、内部对齐 Fence 不出现、raw Key 不等 Fence，并覆盖：空输入；空 chunk；1/2/3B 分块与 padding；payload/meta 边界跨 word；Footer 自身排除候选；最先出现的 Fence 跨 chunk；Key0 禁用但 Key1 可用；Key0/Key1 均禁用（包括跨 chunk）；排除 0..255 的 SmallBitmap fallback；低 Key 连续禁用至 300 的完整 fallback；反复禁用同一 Key；MetaLength 65535、tag uint.MaxValue 与 frame 上限拒绝。

结果应分别报告 Prepare、XOR 写出/读入和完整管线。FullBitmap 的清零/GC 分配、ZeroFirst 的 span SIMD 搜索、fallback 的二次扫、chunk 数量与非对齐访问会影响不同分布，不能在未测之前指定生产默认策略或按大小自动切换。边际空间是 bitmap 或编码 scratch；现有 Builder 已缓冲的输入空间应单列，不能算作这个实验新增的整帧副本。
