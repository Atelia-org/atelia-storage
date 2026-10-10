# VersionStore：ref 记录时间与 TailMeta 格式

日期：2026-10-10。状态：**Draft；用户采用固定 8B Unix 毫秒时间、以 TailMeta 长度判别元信息格式；VersionStore 尚未实施**。

本文是 S4 的公共记录元信息子合同，不是新增实施阶段。规范底座为 [RBF 接口](../Rbf/rbf-interface.md)与[RBF 格式](../Rbf/rbf-format.md)。[S4](04-versionstore-publication.md)负责普通 ref 的 kind、header、RootMap、结果类型及完整宿主尺寸；[S5](05-versionstore-names-and-indexes.md)负责历史/fork；[CU 候选](07-versionstore-cross-ref-conditional-update.md)负责联合提交资格。它们引用本合同，不各自定义时间编码。

## 需求与范围

| 需求 | 来源与边界 |
| --- | --- |
| 每条 ref 快照携带通用记录时间 | 本轮用户确认，用于展示和诊断；不要求应用额外保存时间映射 |
| 固定 8B 时间，不另加 record version 字节 | 本轮用户确认；TailMetaLength 已由 RBF 描述符提供并受 TrailerCRC 保护 |
| 元信息格式只在尾部追加固定字段 | 本轮用户确认的演进方向；既有字段偏移、宽度、单位与语义不变 |
| 保留实际主链历史、位置身份与原子发布资格 | 现行设计；时间不能代替 ref/SizedPtr 或 CU Members |
| 不提供旧草案兼容、时间索引、单调时钟或通用元信息框架 | VersionStore 无实现/已发布消费者；无本轮需求 |

范围仅为普通 Snapshot 和未来接纳的 ConditionalUpdate；RefHeader、tag 桶 header/TagRecord、data 帧不因此增加时间字段。CU 仍是独立优先候选，本文不将其并入 S4/S5 核心。

## term `RecordedAtUnixMs` 本次发布记录的生成时间

### spec [S-VS-RECORD-TIME] 一次取时，随新记录保存

RecordedAtUnixMs MUST 表示库在本次发布的私有准备阶段读取的 UTC Unix 毫秒时间，即自 `1970-01-01T00:00:00Z` 起的有符号毫秒数，不计闰秒。它不是业务事件时间、源数据创建时间、fsync 完成时间、调用正常返回时间或某个旧尝试已 Confirmed 的证明。

CreateRef、ForkRef、PublishRef（含同值重发/rewind）MUST 为本次新记录重新取时；所得数值可以与来源或其他记录相同。库在必要输入/来源资格建立后、最终记录私有编码前取时；取时及编码 MUST 在本次 data/源确认和发布输出前完成。读回返回记录中保存的值，不重新取时；fork/rewind 复制 Roots，不复制来源记录的时间。

一次 CU 联合提交 MUST 只取时一次，全部 fresh 成员使用同一值；它须在最终元信息私有编码及 data barrier/发布输出前确定；固定 8B 尺寸允许先预测 tickets 再取时。时间既不进入 Members，也不充当事务标识；成员时间是否相等不取代或扩充共同发布判据。

时间 MAY 重复或回退。MUST NOT 为其保存持久水位、跨重开修正或强制单调递增，本层不以它决定发布/历史遍历顺序、去重 revision、判定 head/fork 因果、恢复事务或提前结束严格历史查询。应用可自行作近似年代展示/排序，不因此获得存储顺序保证。当前 ref 帧顺序、不可变 ForkOrigin 与 CU Members 继续承担各自职责。

普通读取、历史和发布结果保存本帧的自有时间标量；时间不参与 RefRevision 等值/hash 或 ForkOrigin 的 Roots 一致性比较。具体公开结果属性由 S4 定义，不另立元信息结果类型或租借。

## spec [F-VS-RECORD-TAILMETA] 固定字段，以精确长度判别形状

首版每条 Snapshot/CU 的 TailMeta MUST 恰为 8B：

| byte 范围（半开） | 字段 | 编码 |
| --- | --- | --- |
| `[0,8)` | RecordedAtUnixMs | 完整 int64 LittleEndian |

首版选择原始 long 表示与完整 int64 wire 值域；零就是 epoch，负数就是 epoch 之前的时间，没有 missing/default/sentinel。公开传递原始有符号毫秒数，不以 CLR DateTime 的范围或日期格式化能力限制 wire 值。库正常写入从 UTC 系统时钟取得该值，不增加应用传入时间、时钟配置或持久时间状态。

VersionStore 格式门/ref header 选择本层格式规则；记录形状由文件上下文、FrameTag 和 RbfFrameInfo.TailMetaLength 共同判别。TailMetaLength 不在 meta 中重复编码，不增加 version、flags、presence、padding 预留、Magic 或独立 CRC。现行 Snapshot 的 payload 保持纯 RootMap；CU payload 仍由自身 schema 定义，不能把 meta 长度当成所有 payload schema 的通用版本。

对本次需要完整资格化的 Snapshot/CU，reader MUST 精确接受 8B；0B、短/长形状与其他未知长度报错，不回退旧 head、不当 CU 事务不完整、不自动截出已知前缀接受未知扩展。RefHeader 的 0B 由其 kind 合同另行允许，不表示无时间 Snapshot。VersionStore 尚未实施，首版 VersionStoreFormatVersion 仍为 1，不增加旧无时间草案兼容。

**以后扩展的约束。** 同一记录 kind 的新 meta schema 只可在现有末尾追加确定宽度字段，每种已定义形状具有唯一长度，先前字段保持不变。当前不分配未来长度/字段，也不实现注册表、版本协商或动态可选字段。新 reader 接受哪些旧长度需在届时 schema 中列明；旧 reader 对未知长度仍拒绝。改变既有字段意义或不兼容 payload 时，须另作 FrameTag/所属格式版本决策，不能复用相同 meta 长度冒充同一形状。

## spec [R-VS-RECORD-META-READ] 结构判形，完整内容资格后交付

reader MUST 从经校验的实际 RbfFrameInfo 或完整 RBF 帧取得 TailMetaLength 判形，不能从“目前读到多少 bytes”、Ticket.Length 或带 padding 的长度猜 schema。本地扫描已提供 FrameInfo 的路径，先检查 kind/meta/tombstone 与宿主完整尺寸再租完整帧；CU peer 的 exact-first 路径按 wanted ticket 检查必要容量界，允许直接完整读取后取得实际 meta 长度，不额外调用 ReadFrameInfo(oldTicket)。最终须具备 framing/TrailerCRC、PayloadCRC、精确 payload codec 与 meta 形状资格才交付自有结果。必要记录截短、未知长度或坏内容沿所属错误通道传播。

本条只约束本次需要完整资格化的记录。CU 已以实际异长 ticket 与必要结构否定旧成员、已取得不完整见证而未访问的其他 peer，以及历史定位中的无关源后缀，均不因此增加 VersionStore meta 判形或内容审计；其已观察到的 RBF 结构/I/O 错误仍按既有路径传播。未知 meta 长度不能把本地/同 ticket 必要记录的解码错误降成 CU 不完整，也不能反向扩大已有结构定位的职责。

RBF ReadTailMeta/ReadPooledTailMeta 仅提供 L2 预览：TrailerCRC 保护长度描述符，不保护 meta bytes；PayloadCRC 覆盖 payload、TailMeta 和 padding。预览不能签发 checked 时间/RefSnapshot/RefRevision，也不能决定 CU 生效。正常 checked 读取已经完整验证整帧，不再为时间字段添加一份 CRC 或额外 I/O。

## 尺寸与实施出口

正常取时可使用 `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`；[官方定义](https://learn.microsoft.com/en-us/dotnet/api/system.datetimeoffset.tounixtimemilliseconds?view=net-10.0)确认 UTC epoch、毫秒、负数与不计闰秒。这是可用的 BCL 实现入口，不增加公开日期转换接口。

宿主 MUST 用公共 `MeasureWriteSize(payloadLength, 8)` 校验完整尺寸。普通 buffer 追加向 Append 单独传入 payload 与8B tailMeta；若使用已知尺寸 Builder，调用 `BeginAppend(payloadLength, 8, out ticket)`，随后顺序输出 payload 与8B meta。固定宽度不随时间数值变化，不产生 CU 自身 ticket 尺寸的循环；预测 ticket 仍不是预约、完成或耐久凭据。

（Informative）按当前 RBF3 的 4B 对齐，原 meta0 改为 meta8，任意合法相同 payload 的 FrameLength/AppendLength 均增加 8B。空 RootMap payload=1B 的 Snapshot 从 32B 增至 40B；9B 的 version+时间方案会增加 8B 或 12B，因此首版不保留该 version 字节。header/SourceSnapshotTicket 范围、完整 Snapshot 容量与初始化后界只在 S4 推导，不在本专项重复维护。

首个安全实施片是普通 Snapshot 的 meta8 写入、完整读回与 RefSnapshot 时间属性；随后历史/fork/rewind 复用同一 codec，CU 接纳时复用一次取时与固定 meta。实施向量须覆盖 `0`、`-1`、完整高位/极值、受控取时重复/回退、同值重发/子初始重新取时（允许同值）、必要记录未知/截短长度与坏内容不回退，以及 CU 尺寸预测/共享时间、异长替代与无关源后缀不增加 meta 审计。wire 极值测试不表示系统时钟能正常生成这些日期；时钟控制可由内部测试机制完成，不因此增加公开时钟 API。

时间字段独立 golden：`0` → `00 00 00 00 00 00 00 00`；`-1` → `FF FF FF FF FF FF FF FF`；`0x0102030405060708` → `08 07 06 05 04 03 02 01`；`long.MinValue` → `00 00 00 00 00 00 00 80`；`long.MaxValue` → `FF FF FF FF FF FF FF 7F`。不以相同 encoder round-trip 代替这些符号/端序向量。

本合同不解决 S4 可变初始 Snapshot 的恢复前保护，不改变 ProcessCrashOnly 模型、PublicationOutcome、借入 data 所有权、tag 的独立范围或 CU 选型状态。编码/尺寸算术与历史探针不是 VersionStore 新库、平台、恢复或包消费验收。

两轮独立质询、反例与集成验证见[本次审阅记录](reviews/2026-10-10-record-metadata-review.md)。
