# RBF writer / reader 编码成本专项

支撑 [实施专项文档](../../docs/Rbf/rbf-codec-implementation-study.md)与[普通打开重构方案](../../docs/Rbf/rbf-open-fast-path-refactoring.md)。只含实验，生产仍为 RBF1。比较有界 Key 搜索、XOR / uint32加减法、普通解码与融合CRC，使用生产CRC和cache；RBF1真实API作参照。

固定 .NET10.0.201、Python3标准库、PowerShell7。从仓库根串行执行：

```powershell
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-new-run
# 较短的正确性和I/O冒烟
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-quick-run -Quick
# 独立复测修正后的I/O / 轮转读核与实际最大帧
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-io-run -IoOnly
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-focused-run -Focused
# 独立ZeroThenRandom / 两次有效随机候选后bitmap比较
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-random-run -RandomSearch
# Zero失败后，EscapePayload≤256B使用一个ulong；独立CPU专项
./experiments/RbfCodecCost/Run-Probe.ps1 -OutputDirectory W:/RbfCodecCost/my-tiny-run -TinyKey
```

输出必须是W:新/空目录。runner先Release build、禁用tiered compilation、运行C#、Python独立wire互证、记录CPU/SDK/NTFS卷及源码hash；日志在根，数据在 `data/`。失败文件保留；没有递归清理。普通完整run测CPU+I/O+meta；`-Focused`另跑7样本轮转读核和一个真实约256MiB最大帧。

| 文件 | 作用 |
| --- | --- |
| PrototypeCodec.cs | 七Key策略、plaintext CRC/footer、完整body禁Key、跨chunk carry、可选连续Serialize |
| XorTransform.cs | 相位正确的Vector/ulong XOR、融合coverage CRC；独立uint加减对照 |
| StreamCodec.cs | 借用输入、有界输出、Key0 direct-write、小帧合写；本地完整checked-read |
| CpuProbe.cs / Measurement.cs | 34 workloads、15ops、5样本，校准与allocation计量；连续reader核不消耗chunk列表 |
| IoProbe.cs | W:顺序append+独立batch durable，生产Append/旧Builder参照，轮转warm checked-read；metrics计时外 |
| MetadataProbe.cs | 使用真实生产reader/cache读20B footer与meta；重复读取后尾/meta encoded bytes未改 |
| FocusedProbe.cs | 同plaintext与copy工作的轮转读核；Key301、meta65535、最大FrameBytes真实W:round-trip |
| RandomSearchProbe.cs / RandomSearchCorrectness.cs | 系统CSPRNG搜索、无bitmap/两次有效随机候选后fallback，7轮转样本和强制失败反例 |
| TinyKeyProbe.cs / export_tiny_snapshot.py | 256B分帧区间内一个ulong标记、跨chunk/边界/shift别名反例；7轮转CPU证据与快照导出 |
| verify_random_vectors.py / export_random_snapshot.py | 独立bitwise CRC32C显式Key wire裁判；原始hash守护后的新不可变快照导出 |
| Correctness.cs / verify_vectors.py | guards/alias/phase/carry/CRC失败；四策略wire一致及Python独立语言裁判 |
| WRITER-NOTES.md / READER-NOTES.md | agent局部源审查与原型契约；最终裁决以专项文档为准 |

固定汇总：[codec-cost-5711c47-20261002.json](results/codec-cost-5711c47-20261002.json)。原始W:目录、sourcehash及阶段差异在汇总中；旧阶段不替换或声称全部hash匹配当前入口。源码hash针对运行时工作树原始字节；Git依 `.gitattributes` 转换换行的重新checkout可能改变字节hash，不能据此推断逻辑变化。最初byte-XOR短测和不对等metrics I/O被排除，CPU工作核保持，修正I/O与补测分别记录。

CPU的Split/连续destination是计时外fixture；Prepare的Footer/PreparedFrame与bitmap是实验heap分配，不等于生产必要分配。输出workspace在计时前获得，尚未计Pool获取/归还。wire cache复用不等于已实现生产RBF2 API；Builder pending visitor/原地guard仍只是源码支持的设计。加法未测完整writer/非对齐preview真实I/O。所有读取标为OS warm，不证明物理cold SSD、断电、生产恢复、包消费或solution通过。

ZeroThenRandom 新证据：[zero-then-random-0e0df09-20261003.json](results/zero-then-random-0e0df09-20261003.json)，正式run在 `W:/RbfCodecCost/random-formal-20261003`。185 CPU/20 I/O记录，均7样本轮转；0成功无RNG，失败后 `RandomNumberGenerator.Fill` 抽全uint32并拒绝0/Fence。无bitmap版一直完整检测到成功，比较版最多两次有效随机候选扫描后fallback；rejection draws不受这个扫描预算约束。

新CPU的Key/扫描/bitmap分布属于额外未计时准备，不是计时实例的Key；旧入口Environment.Samples=5为未使用的旧阶段默认，实际7样本由数组和快照 `MeasuredProtocol` 明确。旧四种策略仍保持wire相同；新随机策略只要求marker-free、decode和CRC相同。旧MetadataProbe的高Key meta/cache资格、全uint随机writer的进程恢复模型尚未补测；实验完整读已改为raw Key!=Fence，不改旧FastOpen或旧immutable结果。

导出正式新快照：

```powershell
python -B experiments/RbfCodecCost/export_random_snapshot.py W:/RbfCodecCost/my-random-run experiments/RbfCodecCost/results/my-new-snapshot.json
```

exporter拒绝覆盖已有快照，核对185/20行、7样本和全部测量源码hash；其自身允许运行后仅证据派生说明变化，并明确记录。使用 `-RandomSearch -Quick` 可缩短为小/中帧与8MiB I/O冒烟，不做最大帧，也不满足正式导出的185/20门槛。

小帧标量bitmap正式证据：[tiny-ulong-7667b9c-20261003.json](results/tiny-ulong-7667b9c-20261003.json)。`-TinyKey`仅运行CPU专项（固定7轮转样本），不测磁盘吞吐；104行/26workloads/4策略，新增448帧与112个Python独立wire向量。EscapePayload包含raw HeadLen，阈值按`FrameLength-4≤256`判定，实际编码body最多252B/63words。Zero成功仍直接返回；失败后一个ulong完整标记0..63、取最小可用值，超过阈值仍系统随机。标量及carry属于固定局部状态，ScratchBytes/BitmapBytes均不计数组分配；所有策略共同的PreparedFrame/Footer分配仍计时。

```powershell
python -B experiments/RbfCodecCost/export_tiny_snapshot.py W:/RbfCodecCost/my-tiny-run experiments/RbfCodecCost/results/my-new-tiny-snapshot.json
```

该exporter拒绝覆盖并核对104行、7样本、448帧和全部测量源码hash；正式目录`W:/RbfCodecCost/tiny-ulong-formal-20261003`。阈值内非零键收益及Zero路径波动见专项§3.2，不声称256B是跨平台最优阈值。
