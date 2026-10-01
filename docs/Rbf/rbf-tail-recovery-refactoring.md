---
docId: "rbf-tail-recovery-refactoring"
title: "RBF 单个残缺尾帧自愈：重构设计与实施记录"
status: "Implemented (RBF scope)"
doc-type: "Design Proposal"
normative: false
---

# RBF 单个残缺尾帧自愈：重构设计与实施记录

日期：2026-10-01。状态：RBF 本体已实施；下游接线按用户后续授权暂缓。公开行为已同步到 [接口契约](rbf-interface.md)。本次按 `dialectical-simplification` 完成三位独立 reviewer、两轮交叉质询与主线程源码裁决；[裁决与证据记录](rbf-tail-recovery-review.md)保留需求来源、反例和撤回项。

依据当前 working tree，而非仅 HEAD `bf7d68ab3f3fbad18b87549dbf6ce2aae499c24a`。其中已有未提交的 Audit、固定 EOF candidate、读取计量、写入注入和 `RbfPrefixInspector`，均按当前源码核对。用户已明确无兼容性包袱、允许 API breaking；尽可能保持 wire format。

## 1. 最小模型

**正常可写打开统一恢复一个未完成尾帧；完整 FrameBytes 保留；未完成帧补成墓碑，无法取得长度才截掉；已知损坏不跳过。**

```text
同一个独占 handle：
    验证 Header，从 Header 连续检查主序列元信息，确定唯一尾部
    没有残尾                           → 不修改
    完整 FrameBytes，仅缺 Fence         → 补 Fence，flush
    合法未完成 FrameBytes，HeadLen 完整   → 必要时撤回未完 CRC/Trailer
                                          → 补标准墓碑，flush
    只有合法的部分 HeadLen              → 截到该帧起点，flush
    已可证明的结构/CRC 矛盾              → 拒绝，不向前寻找旧好帧
    最终验证闭合尾部，才构造正式 reader/cache 并返回动作报告
```

不新增 wire 字段、版本、sidecar、WAL、恢复历史索引、公开策略矩阵或兼容过渡层。下游只接受稳定帧流并读取动作报告，不再自行决定如何处理物理残尾。

## 2. 需求与故障边界

用户六条规则保留：默认自愈、只处理一个残帧、墓碑优先、完整 FrameBytes 缺 Fence 不丢弃、报告必要动作、日常读取逻辑 frame 原子性。尾部墓碑仍是合法物理帧，正常 Scan 默认隐藏；它的字节不是可恢复成普通业务帧的数据。

日常自动处理的输入模型是单 writer 顺序追加、既有前缀未变、最后一次未完成 append 留下字节前缀。writer I/O 异常后停止实例，只有释放后新开才能重新判定。没有落下任何字节就无需修复。修复过程中再次中断，也必须落回同一动作表。

这是拟承诺的恢复镜像模型，**不是任意断电的普遍保证**。当前 byte-cut/写入注入不能证明设备乱序落盘、空洞、旧 sector 损坏或断电后文件长度行为。Windows 多 sector 写不保证原子性；`RandomAccess.FlushToDisk` 提供文件缓冲落盘调用，不提供 byte-prefix 定理。依据：[WriteFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile)、[FlushToDisk](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.flushtodisk?view=net-10.0)。

用户已明确决定：最后一帧物理长度完整但 CRC 校验失败，属于**数据损坏路径，而非残缺尾帧**；停止自动恢复，交离线工具。即使该帧同时缺 Fence，也不能补 Fence、转墓碑或截断来掩盖 CRC 失败。已闭合帧损坏、非法 HeadLen、矛盾 Fence 和更早损坏同样拒绝，不自动丢帧。这是确定的分类规则，不再作为待选择的恢复策略。

逻辑 frame 原子性不等于每次 Append 已持久，也不等于多帧、多文件事务。完整 FrameBytes 缺 Fence 时，即使 Append 没返回，也须保留补 Fence；`CompletedFence` 不能证明 tag/ref/head 已发布。上层仍依据自己的 publication 记录与引用校验决定业务状态，禁止因修尾成功自动回退到旧 head。

## 3. 唯一定位与资格检查

### 3.1 连续主序列检查

只保留一条在线定位路径：在同 handle 捕获 EOF，从 HeaderFence 后按 HeadLen 连续步进。每个已闭合帧检查长度/地址范围、对齐、前后 Fence、HeadLen/TailLen、descriptor 和 Trailer CRC；不读取全历史 payload。第一次不能闭合的位置就是待分类尾部，不能越过真实坏帧继续搜索。

紧邻残帧的前一个已闭合帧、以及待保留的完整尾帧，另做流式完整 CRC 校验。无残帧时完整校验最后一个物理帧。成功打开不等于全历史 Payload CRC Audit，历史内容仍由随机完整读和离线审计检查。

成员检查不可省成“EOF 附近局部校验通过即可”。合法外层 payload 可以包含 `[Fence][内层完整 Frame][Fence]`，断点恰在内层 Fence 后；内层全 CRC 正确，但它不是文件主序列帧，返回它会泄露半外帧中的业务数据。此反例同样约束只读打开。

现成的 [RollingCrc.Scanner](../../src/Data/Hashing/RollingCrc.Scanner.cs)与 [RbfRecovery](../../src/Rbf/RbfRecovery.cs)已经支持跨 chunk 单 pass 逆向搜索。它解决搜索成本，不证明主序列成员。连续检查本身已经确定尾部，在线再做 Rolling/EOF 候选搜索没有增加资格证据，首片删除这条重复路径；离线救援继续复用它，不重新造 scanner，也不将离线“候选失败继续找旧 hit”接到普通打开。

正常打开最坏 O(历史帧数) 元信息读取，另加相关尾帧的内容校验成本。这是相对当前有界尾读的明确变化。可信起点可以降低成本，但当前公开 `RbfScanBoundary`/CRC 不自认证文件身份或主序列来源。本次不新增该信任契约；当下游真实打开预算要求它时，另设计明确的调用方证据边界，不新增独立持久权威。

### 3.2 尾部动作表

`E` 是初始物理 EOF；`B` 是连续检查确定的残帧起点，无残帧时等于 EOF。HeadLen 完整时，`L` 是 FrameBytes 长度，`F=B+L`，`C=F-20` 是 Payload CRC 起点。

| 尾部状态 | 唯一动作 |
| --- | --- |
| `E=B`，前面的尾帧有效；或者文件精确为 4B HeaderFence | `None` |
| B 后只有 1–3B HeadLen，已有字节存在合法长度补全 | `Truncated`，截至 B；不猜原 L |
| 完整合法 HeadLen，`E<F`，不存在已可证明的 CRC/字段矛盾 | `CompletedTombstone`，按第 4 节协议完成墓碑 |
| FrameBytes 全量有效，`F<=E<F+4`，已有 Fence 为 `RBF1` 的精确前缀 | `CompletedFence`，仅补缺少字节，不改 FrameBytes |
| CRC 校验失败的数据损坏，或已可证明的结构/Fence 矛盾；不能归入单个残帧的额外字节 | 拒绝；不修改，不补 Fence、不转墓碑、不截断，不搜索更早好帧 |

0–3B HeaderFence 新建残留不属于尾帧；Header 必须完整正确，默认拒绝，不自动初始化旧文件。

资格判断先于任何 mutation。已有 Payload CRC 字节能校验时必须匹配；descriptor/长度/已知 Trailer CRC 能证明矛盾时必须拒绝。部分 Trailer 的未知字段使 CRC 约束暂未决，不等于可恢复普通数据，也不自动等于损坏：在 B/L 已确定、`E<F` 且已知字段无矛盾时，可以放弃整个未完成帧。复用当前 inspector 的这些字节检查，不能直接把 `Unresolved` 状态当作修复授权。

## 4. 墓碑优先协议：撤回未完成后缀，再补齐

### 4.1 固定编码

保留原 HeadLen、原 L 与已有 coverage 字节；缺失 coverage 填 0。全部 `L-24` coverage 字节作为不解释的 payload，`TailMetaLength=0`、`PaddingLen=0`、`IsTombstone=true`、保留位为 0、`FrameTag=0`，两项 CRC 重新正确编码，最后追加 Fence。

L 是 4B 对齐，因此这种编码符合当前 wire format。Tag 0 只是固定墓碑编码取值，不在 RBF 层保留其业务值域。墓碑不承诺恢复原 payload/meta 分界或原业务 Tag。

### 4.2 最小写入协议

- `E<=C`：从 E 顺序补 coverage 的缺失字节，随后追加正确 Payload CRC、墓碑 Trailer 和 Fence。
- `C<E<F`：先 `SetLength(C)`，立即 flush；成功后再连续追加正确 Payload CRC、墓碑 Trailer 和 Fence。
- 补齐结束后最终 flush，验证闭合尾部，才报告成功。纯补 Fence 也是追加缺少字节后最终一次 flush。
- 部分 HeadLen 无法确定 L 时，仅 `SetLength(B)`、flush、验证，报告 Truncated。

`C<E<F` 的撤回最多删除 19B 未完成 CRC/Trailer，不碰 HeadLen、L、coverage 或前帧。这是完成墓碑的内部步骤，最终动作仍是 CompletedTombstone；截到 B 删除整个残帧才是 Truncated。它不保全这些无业务可用性的尾部校验/元信息字节；若要求逐字节取证保全，应在恢复之前制作离线副本。

不原地翻墓碑 bit，不回写 HeadLen，不反求 CRC 修正 tag，不缩 L 替换为空墓碑。旧版“固定 tag0 编码不兼容就截掉整帧”删去：不兼容不等于不存在合法墓碑，撤回未完成后缀就能统一完成。

### 4.3 再次中断闭包

```text
初始合格残帧
  trim 未开始                     → 原残帧
  trim 成功，但 flush 未完成        → 关闭；重开核对实际镜像
  trim + flush 完成               → 同 B/L，EOF=C，coverage 完整、CRC 缺失
  追加 CRC/Trailer 的任意字节前缀    → 同 B/L 的合法墓碑前缀
  FrameBytes 完成                 → 完整墓碑缺 Fence
  Fence 1–3B                      → 完整墓碑 + 合法 Fence 前缀
  Fence 完成                      → 完整墓碑；再次打开 None
```

新的残尾可再次撤回到同一个 C 并重补，完整 FrameBytes 则只补 Fence。每次已有矛盾必须先拒绝，不能通过 trim 抹除证据。

保留 trim→flush 屏障，建立新后缀续写前的持久长度；删除旧提案额外的 FrameBytes→Fence 屏障。此前缀模型下连续追加加最终 flush 已足够，额外屏障不能把第一次写入前的任意断电镜像变成可恢复前缀。

任何 SetLength/write/flush 异常都使本次打开失败并关闭 handle，不在失败句柄上改走另一种动作。重开只根据实际镜像重新判断，不承诺 I/O 失败一定只留下旧长度或新长度。

单帧现有上限为 `SizedPtr.MaxLength = 256 MiB - 4B`，缺失 coverage 补零最坏接近这一规模。采用固定小 buffer 流式读/写/校验，不整帧租内存，不新增补齐预算矩阵。保留 L 是本次不回写 HeadLen 的简单协议选择，不是 API 兼容义务；缩 L 需要另一份完整重复中断证明，本次暂缓。

## 5. API、报告与实例生命周期

可破坏 API，直接让可写普通打开自动修复，用 out 显式返回一次打开报告：

```csharp
public static IRbfFile OpenExisting(
    string path, out RbfTailRecoveryReport recovery,
    RbfCacheMode cacheMode = RbfCacheMode.Slots16);

public enum RbfTailRecoveryAction {
    None, CompletedFence, CompletedTombstone, Truncated
}

public readonly record struct RbfTailRecoveryReport(
    RbfTailRecoveryAction Action,
    long OriginalLength,
    long FinalLength,
    long? AffectedFrameOffset,
    SizedPtr? FrameTicket);
```

None 的受影响位置和 ticket 为空；CompletedFence 给保留帧 ticket；CompletedTombstone 给墓碑 ticket；Truncated 不给可读 ticket。仅成功打开才有有效报告，不存恢复历史，不把报告加到每个 `IRbfFile`。`CreateNew` 不需要虚构 None 报告。

`OpenReadOnlyExisting` 仍是真正只读，需恢复则拒绝，不暗中申请写权限，不提供虚拟修复视图。离线 candidate 固定 EOF 机制保持独立。格式/资格失败沿用 `InvalidDataException`，附位置和简短 detail；标准权限/设备 I/O 异常原样传播。暂不新增公开 failure taxonomy、Auto/Strict 枚举或可写兼容模式。

从普通 `IRbfFile` 删除 `Truncate`：仓内生产调用未发现，它允许任意截断 Header/旧帧乃至扩洞，与普通稳定帧流抽象冲突。限定在线协调器内部 `SetLength(B/C)`；离线 `RbfRecovery.TruncateToSuggestedTail` 保留自己的显式边界验证。损坏测试直接用 fixture 操作文件，不保留公共兼容 wrapper。

加入一份不可逆 writer fault 标记。原始 Append/Builder 输出或 DurableFlush 异常后，新读写操作与 MoveNext 拒绝，只允许释放和 Dispose；前置 Result/guard 失败不 fault。File、reader、已有 FrameInfo 和枚举器共享同一 fault 事实，guard 覆盖缓存命中、零 TailMeta 和无 I/O 早退，不能仅在 facade 上检查。已物化 buffer/span 与值属性不追溯撤销。

`SinkReservableWriter.Dispose` 当前仅 Reset、归还资源，不再次输出；保留未提交 Builder Auto-Abort 不落盘的既有语义。Dispose 仍须尝试释放全部 owned resources，防单个异常泄漏 file handle。不创建第二套恢复状态机或持久 fault 记录。

普通 `ReadFrame` 继续验证 FrameBytes；不把每次读取增加 Fence I/O、分裂离线读取接口作为本次必要改动。Fence 检查放在打开、Scan 和现有 `GetScanBoundaryAfter`。这些入口及 fault 生命周期已经封闭日常残尾，不改变 CRC 信任层级和 tombstone 默认过滤。

## 6. 实施范围与后续下游接线

| 文件/符号 | 必要改动 |
| --- | --- |
| `RbfFile`、新 `Internal/RbfTailRecovery` | 同 handle 的唯一资格/动作流程；恢复后才建正式 reader/cache；只读只验证 |
| `RbfPrefixInspector` / `RbfReadImpl.ReadFrameInfo` | 提取可共用的元信息检查与尾部字节资格；保留离线全 Payload CRC Audit，不在线跑全库 Audit |
| `RbfReadImpl` | 增加流式完整帧校验，避免恢复时租 ticket.Length 整帧；不增每次读的 Fence 新合同 |
| `IRbfFile`、`RbfFileImpl`、reader/FrameInfo/枚举器 | out 报告、删除普通 Truncate、显式只读 guard、共享 fault 检查与资源释放 |
| `RbfWriteInstrumentation` | 修复 write/flush 复用当前 hooks；增加 SetLength 注入与观察点 |
| `RbfRecovery`、Rolling CRC | 保持离线候选搜索及显式截断；不引入在线候选回退 |
| tests 与 interface/guide/format/recovery 文档 | 记录默认语义与反例，删除已废弃 API 测试，保留线格式向量 |

本次实施用户明确收紧为 RBF 本体，允许保留 `src/EventJournal`、`src/RbfSegmentStore`、`tools/EventJournal.Toolkit` 的编译错误和单元测试失败；没有修改这些消费者。以下表格保留为后续适配的设计输入，**不属于本次验收**。不做兼容 wrapper 或先 Strict 后 Auto 的迁移层。上层完整崩溃恢复设计、catalog/rotation/业务事务另行处理。

| 当前真实调用 | 后续适配必须解决的边界 |
| --- | --- |
| `RbfSegmentStore.OpenDiscovered` active 可写打开 | 使用自动修复，显式接收/转发报告；验证最终物理尾部 |
| `RbfSegmentStore.ConfirmDurable` 历史段的可写 `OpenExisting` | 改为内部零修改 handle：保留必要现有文件检查并 flush；不能通过自动恢复工厂修冻结历史 |
| historical reader、只读、Audit/candidate | 保持无 mutation；不默认修全库存；Audit 保持独立坏文件检查能力 |
| `EventJournal.ComputeNextSequenceNumber`、`RefMoveStore.ReadEndpoint` | 当前把最后物理帧当业务尾或拒绝墓碑；显式区分物理尾与最后普通业务帧，处理仅墓碑/墓碑后缀，不把跳完整墓碑当越坏帧救援 |
| `EventJournal.ReplayRefOpLog` | 当前拒绝墓碑；继续物理 showTombstone 遍历以推进 end 和计算物理预算，墓碑不 Decode/不应用业务操作；不能简单默认隐藏而让 end/预算脱离 EOF |
| tag/ref/publication 引用检查 | 缺失或墓碑目标仍拒绝；物理自愈不能授权退旧 head、复用旧 publication 或吞掉已发布依赖损坏 |

后续下游只负责自身业务序列、publication、rotation 与引用完整性，不复制残帧物理策略。默认自愈与只读不修复的差别来自访问权限和文件角色，不是一组供下游自由排列的恢复分类。

## 7. 验收与未完成证据

验收必须覆盖：Append 与 Builder Commit 的每个原始输出断点；部分 HeadLen；coverage、CRC、Trailer 各断点；有效 FrameBytes 完整时缺 4/3/2/1B Fence；完整长度帧 CRC 失败（含同时缺 Fence）归数据损坏且不修改；内嵌完整 RBF 恰落 EOF；已知字段矛盾无修改；更早损坏不跳过；墓碑默认隐藏与连续墓碑；恢复后 Append/正反 Scan/随机读/二次打开；只读。historical ConfirmDurable 和上层引用损坏属于后续下游适配验收。

trim 过程另测 SetLength 前后、trim flush 前后、追加每个字节、最终 flush 前后的真实失败/重开。断言只影响同一 B 后的尾帧、HeadLen/L/coverage 保持、B 前字节不变、失败无成功报告、handle 正确释放。guard 测试须覆盖旧 FrameInfo、零 TailMeta、缓存命中与已取得枚举器。

主线程已做独立内存镜像 probe：6 种 payload 长度、282 个原始断点、8,816 个恢复补写断点，其中 114 个需要撤回 suffix；CRC32C 标准向量通过，所有枚举的补写前缀保持可分类，并复现内嵌 EOF 反例。**它没有调用生产修复实现或真实 SetLength，不是运行时验收和断电证据。** reviewer 的模型轨迹与该 probe 不能相互冒充独立设备证据。

本次按用户范围，仅构建与测试 RBF 及其必要依赖，Windows 下串行执行：

```powershell
dotnet build tests/Rbf.Tests/Rbf.Tests.csproj -c Release
dotnet test tests/Rbf.Tests/Rbf.Tests.csproj -c Release --no-build
```

包交付从干净已提交树使用新版本，运行 `eng/Test-Package.ps1` 的隔离 public PackageReference smoke，覆盖 breaking out 报告、默认恢复、只读拒绝和消费者闭包；源码测试不替代包消费证据。进程 kill、实际文件长度/flush 故障与选定环境断电试验分别记录。

源码实施与验证记录见下节。未打包、发布或运行断电试验；源码验收不替代包消费或设备断电证据。


## 8. RBF 本体实施记录

2026-10-01，按 `bounded-delegation` 拆为恢复实现、共享 fault 生命周期、测试适配三个工作包，另安排独立只读复核；主线程审阅源码并串行构建/测试验收。未发现需修改 wire format 的需求，离线 `RbfRecovery` / Rolling scanner 未改。

- [RbfTailRecovery](../../src/Rbf/Internal/RbfTailRecovery.cs) 在同 handle 上连续检查 HeadLen 主序列元信息，仅对最后已闭合帧与残尾做完整流式 CRC；资格通过后执行动作，最终验证后工厂才构造 reader/cache。
- [RbfTailRecoveryReport](../../src/Rbf/RbfTailRecoveryReport.cs) 与 [RbfFile](../../src/Rbf/RbfFile.cs) 实施 breaking out 报告 API；只读只验证，普通 `IRbfFile.Truncate` 已删除。
- writer 故障事实只存在于 `RandomAccessReader`，facade、Builder sink、旧 FrameInfo 与枚举器共享；未提交 Builder Dispose 不输出，File/reader 的 nested finally 尝试释放全部资源。
- 共用 `RbfPrefixInspector.InspectSuffix` 做已定位尾部资格，离线全 Payload CRC Audit 保留。新增读取计入已有 metrics，不把打开成本伪装为仅一次 Header 读取。普通打开不校验全历史 Payload CRC，旧帧内容损坏由完整随机读/离线 Audit 发现。

[恢复验收](../../tests/Rbf.Tests/Internal/RbfTailRecoveryAcceptanceTests.cs) 使用真实文件和真实 `RandomAccess` 写入：6 种 payload 长度、Append/Builder、非空 TailMeta、逐字节原始尾帧前缀；小帧原始写输出逐字节注入，200000B 帧在每个观察到的 raw write 起点/中点/末字节/终点采样。墓碑续写在小帧五个原始残尾位置逐字节再次中断，跨 64KiB 的大帧补零另验证流式成功和已有 coverage 保全。包含 trim/flush、部分 HeadLen 截断、纯 Fence 写/flush 前后异常及重开；失败释放独占 handle，B 前字节保持，已知 CRC/descriptor/Fence 矛盾拒绝无修改。内嵌帧 EOF、连续墓碑、报告、追加、正反扫描、完整随机读、只读与二次打开均有验收。

[writer 故障测试](../../tests/Rbf.Tests/Internal/RbfWriterFaultTests.cs) 覆盖两缓存模式、旧 FrameInfo（含零 meta）、缓存命中、已终止枚举器、Builder Abort、flush 前后失败、前置失败不 fault 与已物化数据仍可用。原有 wire 向量、正常读写、扫描、离线 scanner、固定 EOF candidate、prefix inspector 与缓存测试继续保留；刻意损坏的局部算法 fixture 使用测试专属 `RawRbfTestFile` 绕过公开打开，公开恢复验收坚持公开入口。

最终验收：上述 RBF Release 构建通过，0 错误；完整 RBF 测试 **504/504 通过，0 失败、0 跳过，46 秒**。`git diff --check`、文档空白/代码围栏与实施设计相对链接检查通过。生产项目重新编译时仍有 22 个既有 XML 注释警告，最后增量构建没有警告。

期间有一轮在 `File.OpenHandle` 获取临时文件独占 handle 时出现一次 sharing violation，尚未进入恢复流程；检查未发现 fixture 句柄遗漏，相关 12 个逐字节用例复跑全部通过，随后完整 504 项也通过。没有增加生产重试或放宽独占锁；缺少竞争进程实证，不能确定其来源。

三个下游项目及其测试未编辑，tracked diff 与实施前一致。全 solution/downstream 构建测试按用户要求暂缓；未打包或发布。没有 package smoke、进程 kill 或设备断电证据，不将 byte-prefix 注入扩大解释为任意断电可靠性。
