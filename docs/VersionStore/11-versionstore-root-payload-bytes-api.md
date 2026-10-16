# HeadStore：root payload bytes 公面与所有权

命名更新：2026-10-16 活跃合同采用 HeadStore/Head/Entry；既有 Accepted 仍对应原 VersionStore revision，本轮重命名验证见[13记录](13-headstore-renaming-plan.md)。格式版本、路径与 Clause-ID 不变；[14 HeadSetTag](14-named-head-snapshots-plan.md)尚未实施。

日期：2026-10-11。状态：**既有 Head/历史/fork/tag 的 source-only 源码资格 Accepted；批量 CU 已正式接纳并取得本轮独立源码验收 Accepted**。既有范围见[最终源码验收](12-versionstore-final-acceptance.md)，新增 `A-VS-BYTES-BATCH` 接线与证据见[CU 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。本篇是 S4/S5/tag/CU 的 bytes API 与长度/生命周期唯一权威；不改变身份、根锁、单 driver、ProcessCrashOnly、data-first 或三态证据。旧六包候选不含本轮 CU，程序集拆分另行设计。

## 最小公面

单 Head 输入使用 ReadOnlySpan<byte>，有效长度就是 Span.Length；不另传 length、capacity、IMemoryOwner 或编码 callback。普通 Head 写入返回 HeadEntryId，避免发布后为返回 bytes 再分配结果。ReadHead、ReadEntry 和历史 visitor 交付 HeadEntry。所有发布 mutation 保持必选 out PublicationOutcome，第一条语句初始化 NotAttempted。

~~~csharp
AteliaResult<HeadEntryId> CreateHead(
    ReadOnlySpan<byte> payload, out PublicationOutcome outcome);
AteliaResult<HeadEntryId> CreateHead(
    uint destLocalHeadId, ReadOnlySpan<byte> payload,
    out PublicationOutcome outcome);
AteliaResult<HeadEntryId> PublishHead(
    HeadId headId, ReadOnlySpan<byte> payload,
    out PublicationOutcome outcome);
AteliaResult<IReadOnlyDictionary<HeadId, HeadEntryId>> PublishHeads(
    IReadOnlyDictionary<HeadId, ReadOnlyMemory<byte>> changes,
    out PublicationOutcome outcome);
AteliaResult<HeadEntry> ReadHead(HeadId headId);
AteliaResult<HeadEntry> ReadHead(uint localHeadId);
AteliaResult<HeadEntry> ReadEntry(HeadEntryId entryId);

public sealed class HeadEntry {
    private readonly byte[] _bytes; // 由内部构造接收自有数组
    public HeadEntryId Id { get; }
    public long RecordedAtUnixMs { get; }
    public ReadOnlySpan<byte> Payload => _bytes;
}
~~~

代码块展示公共形状与 getter 实现，省略内部构造。结果普通 class、内部构造，无 public setter/init、结构等值/结构 hash、owner/reader 或 Dispose；null 不代表 missing/default 成功。Span 属性每次访问由对象持有的私有 GC byte[] 产生；Span 自身不作为 class 字段。时间直接消费元信息专项，payload 不包含 TailMeta。

ReadEntry 只接受库签发、同持久 HSID 的 nondefault EntryId。精确完成位置、完整 CRC/尾 Fence/CU 资格与无回退合同唯一消费 S4 `[A-VS-READ-REVISION]`；返回输入 EntryId、原时间及 root bytes，排除 TailMeta/CU Members，不确认 data 或控制数据。saved EntryId 可在同库重开后使用，裸位置与跨进程导入仍不开放。

### spec [A-VS-BYTES-INPUT] 同步捕获，调用方保持稳定

owner/mutation/参数准入后先按实际 Span.Length 检查 1..1,048,576B；公共常量 HeadStore.MaxHeadEntryPayloadLength=1,048,576，非可配置。默认/空 Span 因0B确定拒绝，与合法空 NamedFrameMap 的1B codeword不同；不要把 missing 与空内容混用。仅复制有效 slice，不复制 backing capacity，不截断/拆记录，也不扫描任何 payload 内容。

库在任何本次 data barrier/发布输出前完成精确长度私有副本、完整 frame/起点检查及必要确定准备。输入原 buffer 由调用方拥有；同步调用期间必须有效、不被修改/释放/归池且不得通过输入访问重入 owner/data。返回或抛出后可立即重用/归还；库不保留原 buffer，不转移 ownership。纯输入复制/分配/确定拒绝本身保持 NotAttempted，不新增健康 owner fault。实际 owned 资源清理、恢复、取消、I/O 与确认后异常仍按 S4 原合同，不由复制规则重分类。

### spec [A-VS-BYTES-OWNED] 结果独立于 owner 和 pooled 帧

公开 root bytes MUST 持有独立 GC byte[]，不引用调用方输入、RBF pooled frame、扫描器 scratch 或可归还内存。只有正常只读接口，无公开 byte[]/Memory<byte>/ReadOnlyMemory<byte> backing 投影；HeadEntry.Payload 提供 ReadOnlySpan<byte>。正常接口不能改写；反射、Unsafe、MemoryMarshal 等绕过只读/所有权合同的操作不在保证内。私有副本不能与用户可写输入共享。

读取时先完成必要 framing/CRC/尺寸/发布资格，再从 pooled 帧复制实际 root bytes，并在交付前归还 pooled 帧。结果在调用结束、后续查询、owner fault/Dispose 后有效，不需要 Dispose；对象/Span 正常引用保持数组存活，最终由 GC 回收。只读 Span 是访问视图，GC 自有 buffer 才是生命周期来源。应用要放入需要 ReadOnlyMemory/Stream 的处理链时可自行 ToArray，不为这类未来接口提前增加租借结果。

ResolveTag 只交付 root bytes，使用窄共享 payload 容器，不造假的 default EntryId。tag 的时间、可选关联坐标及只读取元信息的 TagInfo / ReadTagInfo / ScanTags 公面由 [09 专项](09-versionstore-immutable-tags.md)定义，不加入 RootPayload：

~~~csharp
public sealed class RootPayload {
    private readonly byte[] _bytes; // 由内部构造接收自有数组
    public int Length => _bytes.Length;
    public ReadOnlySpan<byte> Bytes => _bytes;
}
AteliaResult<bool> CreateTag(
    string name, ReadOnlySpan<byte> payload,
    out PublicationOutcome outcome);
AteliaResult<bool> CreateTag(
    string name, ReadOnlySpan<byte> payload,
    HeadEntryId relatedRevision, out PublicationOutcome outcome);
AteliaResult<RootPayload> ResolveTag(string name);
~~~

RootPayload 同样由库内部构造并持有自有 GC array；不新增 identity、version、equals/hash 或 Dispose。HeadEntry 的内部 buffer 表示由实现选择，不要求再分配 RootPayload 容器或重复 bytes。两个 CreateTag 入口均捕获调用方传入的实际 bytes，不回显输入，成功值恒为 true；同名拒绝为失败，不引入 false 成功、幂等或 no-op。原入口产生无关联 tag；新入口只记录同库、nondefault、库签发 relatedRevision 的关联注记，在 I/O 前拒绝非法关联值。它不从源帧取 bytes、不比较 payload、不重读或确认源文件；名称、关联及读取资格唯一消费09，不能由相同 bytes 推出精确来源。

ResolveTag 交付前仍完整校验命中记录的 RBF 内容 CRC 与 root-payload envelope；仅通过独立元信息 CRC 的 TagInfo 不能替代这份 bytes 资格。健康 missing 仍是 TagNotFound；存在名称无法确认的坏记录且零命中时，按09报告缺失不确定，不返回 TagNotFound。

### spec [A-VS-BYTES-BATCH] 批量输入只增加可放入集合的视图

PublishHeads 接收 `IReadOnlyDictionary<HeadId, ReadOnlyMemory<byte>> changes`，返回 `AteliaResult<IReadOnlyDictionary<HeadId, HeadEntryId>>`；成功值是自有只读字典，正常接口及集合 cast 不能改写 backing。没有 payload 输出或部分成功，所有成员共享一个 outcome。入口第一条语句初始化 NotAttempted；先逐项枚举并检查持久上下文、default/实际重复 HeadId 和 1..1MiB 长度，以私有集合捕获；不继承 comparer、不信任 Count。枚举器的必要 Dispose/清理也 MUST 在 barrier/输出前完成，失败不交付捕获结果；按私有实际成员数选择路径。空批次拒绝，一个成员 MUST 使用普通 SingleEntry 发布路径，两个及以上 MUST 使用 [07 CU](07-versionstore-cross-ref-conditional-update.md)。全部目标必须为同库既有正式 Head，不创建或分配新 HeadId。

ReadOnlyMemory 仅用于放入普通集合，不转移 ownership；调用方在整个同步调用期间保持集合和全部 backing buffer 稳定、有效且不重入。库访问 Span 后分别复制有效 slice，全部副本完成才进入发布协议。单成员路径与 PublishHead 共用捕获后的普通 SingleEntry 内核，直接使用已捕获的私有 bytes，不再次复制 payload 或调用另一公开 mutation。自定义枚举器/MemoryManager 访问可能执行调用方代码，沿用 single-driver 前提，不新增并发快照/违约诊断。

总临时 payload 内存为 Σ实际长度，另计成员表及结果；1MiB不是整批总预算，不承诺合法批次永不 OOM。纯输入捕获及输入枚举器清理失败无本次输出，本身保持 NotAttempted、不新增健康 owner fault；若同时发生实际 owned 资源清理失败，仍按 S4 停用。正式输出尝试后的未确认异常保持 Unknown；全部确认后包括结果构造/冻结/清理异常保持 Confirmed，并按 S4 停用，不降级。每帧完整容量及起点仍用 RBF 公共 MeasureWriteSize 检查，不能把 payload 上限当物理 frame 上限。

## codec 与故障边界

内容解释与失败边界唯一消费 [S4](04-versionstore-publication.md) `[S-VS-ROOTS-OPAQUE]`；data 闭包与屏障责任消费 `[S-VS-ROOTS-AFTER-DATA-CONFIRM]`。可选 [NamedFrameMapCodec](10-versionstore-rootmap-payload.md)由应用显式调用，不进入本篇 API 的发布/读取资格。

## 验收与成本

覆盖1B/1MiB/0B/超限、只复制 slice、输入修改与归池、stackalloc/数组/池来源；read/历史/tag 结果在 pooled 归还与 owner Dispose 后仍有效，后续读取不改变旧结果。证明所有核心路径不调用 NamedFrameMap decoder，并覆盖 JSON UTF-8、有界随机 bytes 与内容 codec 失败独立于 CRC/事务资格。批次实际重复/错上下文、同一 buffer 多个slice、捕获失败先于输出、整批 Outcome/确认后结果安装异常与 CU 元数据独立定位均须验证；纯输入失败与同时发生 owned 清理失败的停用差异也须覆盖。

小根对象下选择捕获/交付复制以换取简单 ownership；没有零复制、公开 Builder、streaming、Lease/Dispose、codec callback 或通用预算框架。测量发现复制/RSS 成为实际瓶颈后再单独评估。普通 Head、历史、精确 fork 与 tag 的既有接线见实施计划；CU 已正式接纳，本轮接线/独立源码资格见 [07 验收](07-versionstore-cross-ref-conditional-update-acceptance.md)。RBF pooled 生命周期与 FrameStore FrameRead 是底座参考，不替代本结果模型的实际证据。
