
## Page-Cache行为
默认情况下（未使用 O_DIRECT / 未显式禁用缓存），Linux 的 page cache 以页为单位缓存文件数据。你用 .NET 的 RandomAccess.Read 读取任意 20B 时，内核会把涉及到的整页放进 page cache；如果 20B 全在同一页里，就缓存那一页；如果跨两个相邻页，就会把这两页都缓存（有时还可能触发少量 readahead，但随机读通常不会扩大很多）。

更细一点的解释（结合 linux-page-cache-randomread-INV.md 的结论）：

.NET 的 RandomAccess.Read 在 Linux 上走 pread/preadv，属于普通缓冲 I/O 路径，所以会走 page cache。
page cache 的基本粒度是页（通常 4KB，但由内核配置决定），内核会将涉及的页完整填充入缓存，而不是只缓存 20B 片段。
读取时，内核会把偏移向下对齐到页边界，再把覆盖范围向上对齐到页边界，这就是为什么跨页会缓存多个页。
如果读模式表现为“随机”，默认 readahead 很弱；如果内核判断有顺序性，可能会多读一些页（但不是强制的）。
只有使用 O_DIRECT（或特定 posix_fadvise/madvise 组合）才可能绕过或弱化 page cache；否则就是页级缓存。

## RBF读取调研

| 调用点 | 读取大小 | 触发场景 |
|:-------|:---------|:---------|
| RbfFile.cs | 4B | 打开文件时验证 HeaderFence（一次性） |
| RbfReadImpl.ReadTrailerBefore.cs | 20B | **ScanReverse 热路径**——每帧一次 syscall |
| RbfReadImpl.ReadFrameInfo.cs | 16B | 按 SizedPtr 读元信息 |
| RbfReadImpl.ReadFrame.cs | 整帧 | 按需读完整帧 |
| RbfFrameInfo.cs | TailMeta 区域 | 成员方法，直接 `RandomAccess.Read` |
| RbfFrameInfo.cs | TailMeta 区域 | 同上，pooled 版 |

**核心问题**：`ScanReverse` 的典型工作流是 `ReadTrailerBefore` 反复调 20B 小读，逐帧从尾向头移动。OS readahead 向前预取，但我们向后走——完全白费。之后调用方常常还对每帧调 `ReadTailMeta`（又一次小读），两者在同一区域的相邻位置，却各发一次 syscall。

---

### 技术复杂度评估

**核心机制**：一个持有 `byte[]` 缓冲的内部类，记录已缓存的 `[start, start+length)` 区间。读请求命中区间时 memcpy，miss 时读一个更大的块（方向向前偏移改为向后偏移）。

### 我对这类预读缓存的熟悉度

**足够熟悉**。这就是 `BufferedStream` / 数据库 page cache 的简化版本，唯一的 twist 是预读方向反转——标准做法是 `Read([offset, offset+prefetchSize))`，改为 `Read([max(headerEnd, offset+requestSize-prefetchSize), offset+requestSize))`。

具体来说：
- 不需要 LRU/eviction（单文件单 buffer）
- 不需要锁（RBF 单线程模型 + Building 期间禁读）
- 由于RBF是Append-Only，所以缓存永不失效

---

## 面向"局部随机"的策略分析：

- **传统顺序预取（readahead）完全无效**，Linux 会自动关闭
- **基于块大小的隐式预取**才有价值：读取一个块时，该块内的相邻数据被自动缓存
- **步幅检测**（stride detection）：理论上可检测"每隔 N 字节读一次"的模式，但实践中：
  - CPU 硬件预取器做得比软件好
  - 文件 I/O 层面的步幅检测开销通常不值得
- **最佳策略**: 选择合适的块大小 + 不做显式预取
