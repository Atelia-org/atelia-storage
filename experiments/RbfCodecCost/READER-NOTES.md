# Reader XOR / uint32 模加法实验审查

范围：只审查当前读路径并实现内存核函数；没有修改生产 RBF、cache、CRC 或公共 API。`XorTransform.cs` 的 `AddTransform` 是用户追加研究的有界对照，不决定候选格式。性能结论由主线程的 Release 构建、正确性验收和 W: 实测补充。

## 1. 当前生产接点与最小接入

源码事实以当前 checkout 为准，候选布局见 [普通打开重构方案](../../docs/Rbf/rbf-open-fast-path-refactoring.md)。

| 当前接点 | 当前行为 | 单尾 Key 的最窄接入 |
| --- | --- | --- |
| [RandomAccessReader.Read](../../src/Rbf/ReadCache/RandomAccessReader.cs) / [ReverseReadCache.ReadWithCache](../../src/Rbf/ReadCache/ReverseReadCache.cs) | wire 页、scratch、passthrough 都复制或读取到传入 span；缓存不公开借用页 | 保留 encoded wire cache；在返回的 caller / rented span 解码，不添加 plaintext cache |
| [ReadFrameCore](../../src/Rbf/Internal/RbfReadImpl.ReadFrame.cs) | 读取恰好 ticket 长度，短读先返回失败，再按策略解析 | 完成短读检查后读 raw TailKey，只解码 `[4,L-4)` body；HeadLen 和 TailKey 保持 raw；CRC 后才构造 frame |
| 同文件 `ValidateAndParseCore` | HeadLen、Trailer、TailLen、PayloadCRC 校验 | 用新 profile 具体布局计算偏移；decode 后继续现有正向 PayloadCRC 与反向 TrailerCRC |
| 同文件 `FrameInfoReadPolicy` | 当前快路径复用 info 的元信息，跳过 Trailer 解析/CRC，仅验 HeadLen、推导长度和 PayloadCRC | 不误称当前快路径重新检查两项 CRC；候选方案要求两项 CRC 时，应校验本次读取的 decoded Trailer，并核对 info，增加固定 16B 内存检查即可 |
| [ReadFrameInfo](../../src/Rbf/Internal/RbfReadImpl.ReadFrameInfo.cs) | 只读 Trailer16，验 TrailerCRC、保留位、TailLen 和 payload 长度 | 固定读 encoded Trailer16 + raw TailKey4；局部 span 解码 Trailer，保留内部 profile / TailKey；不读头和 payload |
| [ReadTrailerBefore](../../src/Rbf/Internal/RbfReadImpl.ReadTrailerBefore.cs) | 一次读 Trailer16 + Fence4，校验 Fence 后解析 Trailer | 逆扫一次读 encoded Trailer16 + TailKey4 + Fence4，共 24B；区别于不读 Fence 的 FrameInfo20B |
| [RbfFrameInfo.ReadTailMeta](../../src/Rbf/RbfFrameInfo.cs) | 只读 meta，零长度直接成功；pooled 只租 meta 大小 | XOR 直接原地 decode meta slice，phase=`PayloadLength & 3`；维持不计算 PayloadCRC 的原职责 |
| [GetScanBoundaryAfter](../../src/Rbf/Internal/RbfReadImpl.ScanBoundary.cs) | 完整读、Fence 校验，再对 tag/meta长度/tombstone/明文内容作见证 CRC | 沿用解码后的 frame 数据与原见证定义；不能对 encoded bytes 求业务内容见证 |

`TailKey == 0` 不能用于版本分派：新格式也允许 Key0。profile 由已打开 reader 的 Header 分派事实提供；public SizedPtr / ticket 保持物理 offset+length。新增内部信息可以绑定 reader/profile 和 TailKey，不需公共 codec 或版本化 ticket。所有长度、最大值、meta/padding 推导必须使用所选 profile 的具体布局，不能在旧 `FrameLayout` 上仅把总长度减 4B 而继续沿用错误 footer 偏移。

`RandomAccessReader` 明确不是线程安全对象；本原型不承诺把 reader/cache 变为并发安全。缓存里的 encoded bytes 在读取期间不可原地 XOR：同一页可能覆盖邻帧、含不同 Key，也可能再次用于 reverse/info/meta 读取。否则第一次读后的 plaintext 会在第二次读再次变换，缓存与磁盘语义分离。

## 2. XOR 相位与原型契约

`Copy(source,destination,key,bodyByteOffset,mode)` 和 `InPlace(bytes,key,bodyByteOffset,mode)` 的 offset 是**切片首 byte 相对 body 起点**，即 frame offset4 为 offset0。函数不接收 file offset，也不把每个新 span/chunk 当作新 body。

| 切片 | bodyByteOffset |
| --- | ---: |
| 整个 body / payload 首字节 | 0 |
| TailMeta 首字节 | PayloadLength |
| padding 首字节 | PayloadLength + TailMetaLength |
| PayloadCRC 首字节 | CoverageLength |
| Trailer16 首字节 | CoverageLength + 4 |
| writer 的 padding+CRC+Trailer Footer 数组 | CoverageLength - PaddingLength |
| 任意 chunk | 实际累计 body 字节数 |

输出字节为 `source[i] XOR LE(Key)[(bodyByteOffset+i) mod 4]`。Vector 路径在 LE host 使用旋转后的 `Vector<uint>` 直接重解释为 byte mask；BE host 按 byte 构造同一 mask。Vector 长度是完整的 4B 周期；正向 scalar 路径和 vector 剩余部分均按 LE ulong/4/2/1B 处理，最后2B后继续正确 byte 相位。span 内存地址可以非对齐。Key0 的 Copy 用 `CopyTo`，InPlace 不写。

参数错误（负 offset、destination 太短、非法 mode）在写入前抛出。Copy 仅写 source 长度，不改 destination 后缀；distinct-buffer 时保留 source，exact alias 等价 InPlace。右移重叠采用逆序 scalar，避免覆盖未来 source；左移重叠可前向处理。该额外契约不要求生产调用者采用重叠 buffer。

最小相位反例：Key=`0x01020304`，明文全零。完整 encoded body 为 `04 03 02 01 ...`；body offset1 的 meta 切片应 decode `03 02 01 ...`。对该 slice 错传 offset0 会得到 `07 01 03 ...`。跨 chunk 重新从相位0开始同样出错。Key1 的高三 byte 都为0，不能独立证明非零相位正确，应包括 Key301 和四 byte 非零的 key。

## 3. 融合 coverage 解码与 PayloadCRC

`XorTransform.DecodeCoverageAndCrc(Span<byte>,uint,int=0)` 对**只包含 payload+meta+padding 的 span**原地解码，返回 finalized forward CRC32C。它不处理存储 CRC、Trailer、raw Key 或 Fence，也不判断成功/失败。init/final xor 取生产 `RollingCrc` 常量；正向 ulong/4/2/1 顺序取自 [RollingCrc.impl.cs](../../src/Data/Hashing/RollingCrc.impl.cs)。Key0 直接调用 `RollingCrc.CrcForward`，不写入。

融合循环每次 load encoded ulong，XOR 后 store plaintext，并把**同一 plaintext ulong**交给 `BitOperations.Crc32C`，避免立即为 CRC 再 load 一次。4/2/1 尾处理继续正确相位。此函数每次重新初始化 CRC，不是可跨多个 chunk 连续调用的 incremental CRC API；整 coverage 连续内存符合当前 full-read buffer 路径。

对于新 L 字节 FrameBytes，coverage=`[4,L-24)`，encoded CRC+Trailer=`[L-24,L-4)`，raw Key=`[L-4,L)`。融合接入可先仅 decode 20B CRC+Trailer，验 Trailer/descriptor/长度后，再对 coverage 融合解码并比较原存储 PayloadCRC。覆盖范围必须精确：把 footer 也传入融合函数会改变 CRC 输入，还可能导致之后二次 decode。

Vector 全 body 解码后再调用生产 `RollingCrc` 的方案更直接；融合减少重复读，但使用串行 CRC 依赖的 ulong 循环，可能限制 XOR 吞吐。是否优于 Vector+CRC、在小帧是否值得分支，不能只由循环次数决定。主线程比较两种候选，本文不预先断言性能收益。XOR scalar 的最初 byte 循环在短测后已改为正常 word 实现，不把旧 byte-XOR 与 uint-add 的耗时差作为算法结论。Trailer 的反向 CRC、BE 存储保持独立，不能用本正向函数替代。

## 4. uint32 模加法对照的边界

`AddTransform.CopyWords` / `InPlaceWords` 对完整 LE uint word 独立做 `unchecked(w + key)` 编码、`unchecked(w - key)` 解码。长度必须为 4 的倍数；调用方保证 source 对应 body word 边界，物理 span 地址不要求对齐。Vector<uint> 在 little-endian host 启用，其他 host 用明确 LE 的 scalar 路径。Key0、重叠、destination guard 与 XOR 的契约一致。

`DecodeCoverageAndCrcWords` 的 coverage 必须含完整 words；ulong load 中两条 uint lane 分别减 key，再合成 plaintext ulong 作 forward CRC。禁止直接把两份 key 拼为 ulong 后做一次减法，低 word 的 borrow 会错误传播到高 word。例如 encoded low=0、high=1、Key1 应 decode 成 low=`FFFFFFFF`、high=0；一次 ulong 减法会把 high 也多减1。

同一 marker 禁用证明仍成立：每个 plaintext word w 唯一排除 `key=unchecked(F-w)`；m 个 words 在 `[0,m]` 中仍至少留下一个候选。raw key 的界不变，正常 writer 任意进程终止前缀的完整 aligned body word 仍不等于 F。尾部唯一 Key 重建相应为 `unchecked(encodedTailLen-L)`，不用 XOR 的公式。此结论不证明任意损坏镜像可局部认证。

但加法的 byte 不能独立变换。Key1 下：

```text
plaintext FF 00 00 00  -> encoded 00 01 00 00
plaintext 00 01 00 00  -> encoded 01 01 00 00
```

只看 encoded 后3B，两者都是 `01 00 00`，plaintext 后3B 却分别是 `00 00 00` 和 `01 00 00`。meta/preview 非对齐读取必须扩到完整 body words，前后各最多3B，再 decode 并切回原区间。不能把两个任意非对齐 chunks 各自按 uint 相加；需保留最多3B跨 chunk word，或让 serialization 输出按完整 words 分块。

padding>0 的结构校验也必须读最后完整 coverage word4B，再检查其中 padding；不能只读 encoded padding1–3B。所以同一读取规划的健康 Open 上界为 `Header4 + Tail24 + Left8 + PaddingWord4 = 40B`，最多4次读；XOR 的39B上界不能原样套用。FrameInfo20B、reverse24B 都是完整 words，大小无需改变。空 meta 仍不读/不租 meta buffer，但非空 pooled meta 若要读扩展 words，必须容纳扩展范围或用有界 stack scratch 处理边界。

加法在预览边界增加 I/O 和 slicing 接入；XOR 只需维持相位。哪个选择更好，需同时看整帧 CPU 结果和短切片调用成本，不以核函数吞吐替代集成判断。

## 5. 生命周期、错误返回与验收反例

成功读取的 `RbfFrame` 引用 caller buffer；`RbfPooledFrame` 引用租来的 buffer。解码不引入新 lease，也不在 buffer 归还后继续使用 span。[RbfPooledFrame.Dispose](../../src/Rbf/RbfPooledFrame.cs) 通过 Interlocked.Exchange 幂等归还，属性在 Dispose 后抛出；已经取得的借用 span 仍不可再用。零 meta 不租 buffer；非空 meta 只读对应区域，不能为了获得 Key 或相位物化完整帧。

完整短读先失败，不对尚未填满的 buffer 求 CRC或解析 footer；失败路径不返回有效 frame。CRC 失败时 caller buffer 可以已被解码，API没有回滚 buffer 的承诺；pooled 路径的 Result失败/异常仍各自归还一次，只有成功时转移所有权。reader disposed/write-fault/cancellation 与已有短读错误要保留，XOR 不能绕过 `EnsureUsable` / `ValidateTicket`。普通 ticket full-read 不自动检查 ticket 外 Fence，boundary / reverse 原有职责保持。

主线程正确性验收应独立采用 byte 模型，覆盖：

- Key0/1/301/`01020304`/F/`FFFFFFFF`；offset0..3和 `int.MaxValue`；空串、小于 Vector width、width±1、跨多向量尾段；任意 source/destination 内存偏移。
- Copy source 不变、destination guards不变；exact alias、左右移位重叠；短 destination/负 phase/非法 mode 在动作前拒绝。
- 整帧处理与任意 chunk 划分输出一致；独立 Trailer 反向 CRC黄金向量及 Payload正向 CRC；Body/CRC/Trailer 边界不重置相位。
- 融合函数等于“独立 byte decode + 生产 RollingCrc”，覆盖 ulong/4/2/1、非零 phase、空 coverage、footer guards不变；同一 span 不得为了验证再 decode 一次。
- Add carry/borrow/overflow、两个 uint lane 独立、byte移位overlap；非4倍数预拒绝；同encoded suffix反例；完整word扩读和切回 meta 的结果。
- encoded cache 连续复读、邻帧不同 Key、meta→full-read→reverse 的混合访问；完整帧 CRC错误不可返回 frame、pooled异常/短读只归还一次、Dispose后不使用借用 span。

本工作包在内存中用独立 bitwise CRC Python 模型互证了5292个XOR fusion算术向量和198个模加法独立lane向量；它们不替代C#执行。未运行 .NET、benchmark 或真实文件 I/O；主线程串行运行和独立验收后，才可把实测结果写为证据。正向 scalar 和 LE vector mask 优化后须重跑 C# correctness，先前短测保留为历史证据。
