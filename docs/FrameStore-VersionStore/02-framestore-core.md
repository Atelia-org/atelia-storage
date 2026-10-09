# S2：FrameStore 核心、地址与文件生命周期

状态：**Draft；2026-10-05 确认首帧 header、自动归还与 config 数量上限；2026-10-07 允许活跃 Builder 期间确认和随机读取已完成输出；2026-10-08 确认地址固定 12B codec；2026-10-09 定稿 D2 资源基线与 D5 文件 header；其余 API/格式门/目录及恢复维护细节待工程定稿，项目尚未创建**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)。本阶段独立于发布和命名层。

## 本阶段目标

创建 `src/FrameStore/FrameStore.csproj`、`tests/FrameStore.Tests/FrameStore.Tests.csproj`，采用 `Atelia.FrameStore` 身份。
提供独立的多文件不可变 Frame 分配器：Create/Open/OpenReadOnly、申请并完成单帧、随机读取、不透明地址、恢复及同步耐久确认。普通分配不提供业务上的全局顺序。
首版支持多个 active 文件、每文件独占租给一个 Builder，文件申请/归还串行且可嵌套；成功完成或健康取消后可以复用文件。普通帧可以交错构建、按不同于申请次序的顺序完成。达到软阈值后文件归档只读。
按需引用 Rbf/Data/Primitives，不引用旧库或业务项目。本阶段的定稿、实施与验收只包含分配、读取、文件生命周期和耐久确认。

## 已确认与待定的边界

| 事项 | 当前结论 |
| --- | --- |
| 单帧追加能力 | 保留 RBF 的 buffer Append、未知尺寸 Builder、分立长度的已知尺寸 Builder；签名细节待定 |
| 构建自由 | 每文件一个 Builder；owner 可持多个未完成 Builder；租借可嵌套；首版调用仍串行 |
| 文件选择 | 从 active 当前可分配文件中选择数值 FileId 最小者；低编号忙时跳过，不等待 |
| 轮转 | 三种追加均在成功完成后按 TailOffset 事后检查；大于阈值才停止新追加 |
| 磁盘生命周期 | 私有 creating → active → 按编号分桶的 archive；不维护 active manifest/status |
| 归档桶 | 固定 1024 个编号一个桶；精确路径字符串与格式版本待定 |
| 地址编码 | 固定 12B，完整 uint FileId 与 SizedPtr；基础地址无预留或内容见证字段 |
| 耐久 | 确认调用时全部必要已完成 active 输出，包括 leased 文件；未完成 Builder 不获得资格，archive 资格来自移动前 flush |
| 活跃构建期间随机读取 | 同文件已完成前缀可以读；未完成新帧不可读，扫描相关入口仍遵循 RBF guard |
| 首帧 meta/header | 固定 24B payload：格式版本 1、16B StoreId、uint FileId；按首物理帧位置识别，不占用用户 tag 范围 |
| 成功 EndAppend | 正常成功返回前自动归还租借和配额，无需再 Dispose；归档维护入口待定 |
| Builder 数量准入 | 可选 config 的 MaxOutstandingBuilders，默认 32；只计已签发且未结束的 Builder，超限立即拒绝；同步 Append 最多另占一个短期租借 |
| 句柄与缓存基线 | 可写 owner 保留 active 句柄，显式使用 RbfCacheMode.Off；archive 随机读按操作打开/关闭，不引入 idle 淘汰或 reader pool |

本表是状态导航，具体合同以下文条款为准；Draft 不表示已实现或已有平台资格。

## term `FrameStore-Context` 存储上下文

一个持有持久 StoreId、访问模式及实例生命周期的存储 owner。它拥有文件组织、所有读写句柄和 fault 事实；首版由调用方串行使用。整个 store 只有一个可写 owner，不能仅靠某个 RBF 文件的独占打开替代这个约束；锁定入口在 S2-Q3 定稿。
StoreId 为创建时生成的随机非零 128-bit 值，以 16 个 opaque bytes 作为 canonical 编码，在 create-only 格式门中持久化；各文件 header 逐字节复制并比较同一值，不重新生成、不转储 CLR Guid 内存或依赖其默认字节序。格式门的完整记录 codec 仍在 S2-Q1 定稿。路径移动不改变身份；复制与外部改写不由本协议自动协调。

## 地址表示与固定编码

FrameAddress 对外是不透明、可持久编码的局部地址；首版采用 `FileId + SizedPtr`，正整数 `uint` 文件编号、0/default 非法，ticket 的范围由 RBF/Data 公共 API 校验。不向普通调用方提供字段拆解、地址算术、大小排序或相邻帧推算合同。
StoreId 属于上下文，不必重复塞进每个数据引用。地址相等只在同一个 store 中有意义；相同数值在另一个 store 可能恰好也合法，**裸地址无法检测调用方原始来源错误**。
跨 owner 的 Builder 拒绝由实例生命周期负责；持久上层绑定负责选择正确 store。二进制格式由下条锁定；公开 codec 入口、文本表示与错误载体在 S2-A 关闭。
成功完成的帧在首版存续期间不重新分配、不改写、不删除。整个文件从 active 移到 archive 只改变容器路径，不改变 FileId、ticket 或帧字节，不属于帧重定位。地址不透明不等于已经提供可搬迁的逻辑对象 ID；未来改变定位编码必须另行处理兼容性。取消或截断的未完成预约没有稳定身份保证。

### spec [F-FS-FRAME-ADDRESS-12B] 基础地址使用固定 12B codec

本条遵循 S0 `[S-FS-ADDRESS-FIXED12]`。首版 FrameAddress 的 canonical 二进制编码 MUST 恰为 12 bytes，字段布局如下：

| byte 范围（半开） | 字段 | 编码 |
| --- | --- | --- |
| `[0,4)` | FileId | 完整 uint32，LittleEndian，0 非法 |
| `[4,12)` | Ticket | 完整 `SizedPtr.Packed` ulong，LittleEndian |

MUST 按字段显式编码与解码，不转储 CLR struct 内存，不追加对齐 padding；Ticket 使用 Packed 的完整 64 bits，不改为 `SizedPtr.Serialize()` 的交错值或 varint，不将 offset/length 各压成 uint。解码须精确消费一条 12B 地址，拒绝截短输入、非法 FileId 及不满足既有 ticket 约束的值；容量与帧资格消费 RBF/Data 公共合同，恢复 Packed 本身不产生真实帧、主链成员或来源证明。FileId 与完整 Packed 决定同一上下文内的值相等。
MUST 保留 SizedPtr 当前完整可表示范围，不新增单文件 4GiB/16GiB 硬界，不因地址编码把 MaxOffset 改成帧末端上界。基础地址不编码 StoreId、预留字段、CRC、fingerprint 或 generation。已知尺寸 Begin 签发的 FileId、ticket 及其 12B 编码在正常 End 后 MUST 保持相同；健康取消或修尾后的地址复用仍按既有定位合同处理，不承诺取消尝试身份。
（Informative）CLR 内存布局与持久 codec 分离。可优先评估 private SizedPtr + uint FileId 的 Sequential/Pack=4 布局，也可用三个私有 uint 保存 FileId/PackedLow/PackedHigh，按需拼回 SizedPtr。两者不改变公开不透明性或 wire；内存大小、数组步长、嵌套容器及调用成本在目标运行时验证，不把 12B wire 当作通用内存 ABI 或速度保证。

## term `Completed-Frame` 已完成帧

成功 append 或重开后存在的完整 frame 事实。Completed 不等于本次确认耐久，也不等于业务状态已发布；数值地址本身不提供这些资格。
RBF ticket checked read 证明 CRC，不独自证明主链成员；正常签发来源、扫描发现和消费者记录的来源约束不能省略。

## 候选入口与职责

| 入口 | 本层负责 | 消费者负责 |
| --- | --- | --- |
| Create/Open/OpenReadOnly | 格式门、目录发现、owned handles、恢复 | 选择 store/mode；不把 Open 当全库 audit |
| Append/两种 BeginAppend | 文件租借、提前地址、RBF 完成及 fault | tag、stored payload/meta 编码 |
| ReadFrame | 寻址、完整 CRC、buffer 生命周期 | 解码及业务合法性 |
| Preview/显式 inventory 或 audit | 明确信任级别、实际帧发现与错误 | 不以枚举顺序推断业务关系 |
| ConfirmDurable | 同步确认 owner 的全部完成输出 | opaque 引用闭包及业务生效 |

普通分配器不承诺 ScanForward/ScanReverse 的全局语义，也不提供“地址之后”的业务范围。显式 inventory/audit 可扫描真实文件发现帧，但其枚举次序不构成发布顺序；精确 API 与成本另在 S2-Q4 定稿。

只提供 owned 分配/read/Builder，不公开可绕过本层 bookkeeping 的裸 `IRbfFile`。本层不认识上层控制记录或提交 codec。首帧识别、完整用户 tag 范围与扫描分类遵循 `[F-FS-META-FIRST]`；inventory/audit 的具体返回类型仍在 S2-Q4 定稿。

三种追加能力已确认，下面仅为签名候选，不是已存在的 public API：

```csharp
AteliaResult<FrameAddress> Append(
    uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default);
FrameBuilder BeginAppend();
FrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out FrameAddress address);
```

未知尺寸模式在正常 EndAppend 时签发地址；已知尺寸模式在 Begin 时取得绑定地址。FrameBuilder 保留 PayloadAndMeta、reservation/回填及 RBF 的 EndAppend 用法；其薄包装负责观察完成、取消、文件归还和共享 fault。Writer 的类型和 guard 转发仍待定，不返回可绕过本层生命周期的裸 Builder。

已知尺寸入口按 S1 保留 payloadLength、tailMetaLength 两个声明：调用公共尺寸 API 校验/试算，再通过 BeginAppend(payloadLength, tailMetaLength, out ticket) 取得当前帧的绑定地址。尺寸不用于提前轮转。
正常完成调用 EndAppend(tag)，使用 Begin 的 meta 声明；显式 meta 参数只能核对一致，不能改变划分。FrameStore 的 owned API 签名仍由 S2 审定，不在本层退化为仅保存合计长度。

## 分配与租借合同

### spec [S-FS-ALLOCATOR-UNORDERED] 普通分配不赋予业务顺序

普通 FrameStore MUST 以独立地址表达帧引用，不承诺分配次序与业务顺序一致。调用方 MUST NOT 用文件编号、offset 或物理相邻关系推断 Parent、版本新旧或当前状态。
每个文件仍顺序追加；不同文件可以交错完成。首版的串行调用约束不建立业务上的全局排序。应用通过帧内引用表达关系，其构建中间帧的申请、完成和物理排列不形成上层发布协议的输入。

### spec [S-FS-LEASE-EXCLUSIVE] 每个 Builder 独占一个文件

FrameStore MUST 在 BeginAppend 成功前租借一个可分配文件，直到正常完成或健康取消后归还。同文件不得同时接受第二个 Builder 或完整 Append；再次申请 MUST 选择其他空闲文件，没有合适文件时才创建新文件，不能隐式提交/取消已有 Builder。
归还后未达到轮转条件的文件可以再次分配，不能把“暂时被占用”当成永久 sealed。可纠正短写、meta 冲突、未提交 reservation 等拒绝按 S1 保留同一个 Builder 和租借；参数错误不借机更换文件或地址。
文件选择按下面的最低编号规则；未归还 Builder 的数量准入由 `[A-FS-BUILDER-LIMIT]` 定义，句柄策略由 `[A-FS-HANDLE-BASELINE]` 定义。不增加精确总资源配额，也不承诺无限嵌套。

### spec [S-FS-END-AUTO-RETURN] 成功提交自动结束租借

EndAppend MUST 在正常成功返回前自动结束相应 Builder 的租借，释放未归还 Builder 配额并登记新的未确认完成输出；调用方无需再调用 Dispose 才能申请新的 Builder。ConfirmDurable 本身不要求租借已归还，遵循 `[A-FS-DURABLE-COMPLETED-OUTPUTS]`。
可纠正短写、meta 冲突、未提交 reservation 等拒绝不归还文件、不释放配额；健康 Dispose/取消归还。成功后 Dispose、Builder 的值副本与旧 Writer 不得二次计数或影响新的租借。
成功完成后超过软阈值的文件必须立即停止新分配，自动归还不意味着重新变为可租借。物理归档维护的调用入口及异常报告仍在 S2-Q4 定稿；归还和正常成功返回不代替 durable flush。

### spec [A-FS-BUILDER-LIMIT] config 文件限制未归还 Builder 数量

FrameStore MUST 按本条从可选 config 文件或缺失时的默认值取得可调整的未归还 Builder 上限。额度已满时，新的 BeginAppend MUST 在租借/创建文件与输出之前立即确定拒绝，不等待已有 Builder、不抢占、不产生 writer fault。
计数按实际签发的 Builder 租借，不按值副本、Writer 副本或后续 Dispose 次数重复计算。Begin 初始化失败不占用成功签发配额；可纠正提交拒绝保留配额，正常成功 EndAppend 或健康取消释放一次。
对当前返回 Builder 的签名，超限使用 InvalidOperationException，诊断包含有效上限和当前占用数量；不得签发可用 Builder 或提前地址。若底层 Builder 已准备但 owned wrapper 无法签发，必须取消内部准备并归还租借；清理失败遵循共享 fault。已完整初始化并发布到 active 的文件不会因此被盲删，文件事实与 Builder 配额不同。
本条只计对外签发的 Builder。完整 buffer Append MUST NOT 消耗此额度，也不通过 BeginAppend 转写；它消费 RBF 的完整 Append 能力，同步借用输入并完成一个短期独占文件租借。首版单 driver 串行且不允许重入，因此最多额外存在一个这样的租借；设有效上限为 M，用户帧构建/输出的同时租借数最多为 M + 1。Append 仍遵循 owner/模式/fault/参数、最低编号选择与 RBF 容量规则，不等待或结束已有 Builder。
额度满时仍可合法 Append、完成/取消已有 Builder、随机读取已完成前缀及 ConfirmDurable；不保留特殊 Root 槽位，也不使未完成依赖获得发布资格。真正需要同时持有 N 个提前地址的轨迹仍须 N 个 Builder 槽位；完整 Append 不提供提前地址，不能由它推导任意互引图都能在有限槽位内构建。
计数只建立 Builder 数量准入，不承诺总文件数、打开句柄或总内存硬上限，也不维护精确总字节数账本。非 Result 异常可能在 RBF finalize 阶段取消构建或进入 fault，不能把所有 End 失败都当成可纠正拒绝；终结/清理异常与 owned wrapper 的精确表达由 S2-Q4 统一定稿，不增加第二套生命周期。

配置使用 store 根目录的可选 `framestore.config.json`，UTF-8 JSON object，首版只接受大小写精确匹配的 `MaxOutstandingBuilders`：

```json
{ "MaxOutstandingBuilders": 32 }
```

| 输入/入口 | 首版行为 |
| --- | --- |
| 文件不存在，或 object 中省略该属性 | 使用默认 32；空 object 合法 |
| 属性存在 | 必须是 `[1, int.MaxValue]` 内的 JSON 整数；不预分配相应数量的文件、数组或 Builder |
| 0、负数、溢出、小数、字符串、null、非 object、重复/未知属性、非法 JSON | 确定拒绝；不静默回退默认，不接受注释或尾逗号 |
| 权限/I/O 等读取失败 | 传播相应错误；仅文件确实不存在才作为缺失配置 |
| 可写 Create/Open | 在任何数据恢复、创建清理或初始化/追加输出前一次读取并校验；有效值固定于整个 owner 生命周期 |
| 运行期间修改 | 当前 owner 不受影响，下次可写打开生效；不做热更新 |
| OpenReadOnly | 不消费写入准入配置，不因该文件缺失或非法而拒绝只读数据访问 |

Create/Open 不自动补写缺失 config；32 是便于小规模交错构建的可调工程起点，不是测量得出的最优值或资源保证。配置是运行策略，不存 StoreId、active 集合、租借状态或下一编号，不进入不可变 header、数据输出登记或 ConfirmDurable 范围；它存在也不证明 store 已创建。修改配置无需新的持久发布协议。
配置预检不替代 store 独占资格；任何恢复、私有残留清理或初始化输出前仍须取得 S2-Q3 的独占。Create 面对尚不存在的根目录时按缺失 config 处理，不为了读取配置先创建 store；已有目录是否可接受仍消费 create-only 格式门与 S2-Q3 的创建协议。不能把根目录缺失作为 Open 缺失 store 的成功回退。

### spec [A-FS-HANDLE-BASELINE] 首版保留 active 句柄且不建立 reader pool

可写 owner MUST 完成全部 active 的 RBF 恢复与必需 header 检查，成功打开后保留其 owned RBF 句柄，直到归档或 owner Dispose；健康 idle active 不做容量淘汰、超时关闭或按需重开。各数据文件的 RBF 工厂调用 MUST 显式使用 RbfCacheMode.Off，不隐式继承默认的每文件读缓存，也不增加缓存配置或长期 FrameInfo 表。同文件随机读取直接复用这一句柄，Building 时沿用 completed-prefix 资格。
仅只读访问且尚无 owned 句柄的文件，包括 archive 随机读取，按操作打开、检查身份/header、完整物化读结果后关闭，再向调用方移交结果；结果不借用 reader。读取或关闭异常时必须释放所有尚未移交的自有结果，不能因已读成功但关闭失败而泄漏 buffer；成功移交的结果独立于 reader 生命周期。扫描若需该文件句柄，保持到当前文件枚举结束或 Dispose，并在取消/错误时清理；不签发引用已关闭 reader 的延迟结果，不建立历史 reader pool。只读 owner 的目录/编号/header 检查可逐文件关闭，不适用可写 active 全保留策略。
文件结束 Builder 租借不等于文件句柄关闭，也不等于耐久确认。必要完成输出登记继续按 `[A-FS-DURABLE-COMPLETED-OUTPUTS]` 覆盖全部 active/leased；归档仍先 flush 再 close/move。Open 失败须尝试释放此前打开的全部 owned 资源；不能因为文件数多于当前 Builder 上限而跳过文件、删除文件或改变最低编号选择。
资源成本按实际 active 数计算：曾用更高数量上限或旧构建峰值留下的 active 可以多于当前 M，降低 config 不主动归档、删除或忽略它们。在固定配置/阈值、新文件仅无候选时创建且停止分配文件及时归档的健康运行中，仍可继续追加的 active（含已租出文件）受历史同时租借峰值约束；从空 store 开始的这一峰值最多为 M + 1。该推导不覆盖旧配置遗留，也不限制尚未完成归档的停止分配文件；D7 必须闭合维护时机，不能任其累积而声称总 active 数已受限。
（Informative）此基线让租借、历史读取和 dirty 屏障直接复用 active 句柄，避免首版引入闲置淘汰后的再次恢复/确认与关闭异常路径。若实际 active/句柄压力要求淘汰，另与 S2-Q4 的维护边界一起审定；缓存失效不能清除必要输出登记，Dispose 本身也不替代 DurableFlush。

#### 实际资源成本（Informative / Derived）

当前 RBF 已知/未知尺寸 Begin 均不预分配声明长度，而是从 HeadLen reservation 开始；最早 reservation 阻止提交前的帧 bytes 输出。多个 Builder 的实际租用 chunks 相加，GetSpan/GetMemory 的 sizeHint 与池数组容量也影响占用。已知声明只限制实际 Advance/Reserve 消费，不限制借用容量；即使很小的声明也可能先申请很大的空 chunk，不能用 `M × 声明长度` 或 `M × 最大帧长度` 当作内存硬上界。
成功 End 的 Commit 与健康取消 Reset 会把 chunks 归还池；这不保证进程 RSS 立即下降，writer 的增长目标和容器容量也可以跨租借保留。完整 Append 保持输入 borrowed：当前 RBF 小帧使用至多 8 KiB stack buffer，非零转义键的大帧可为每个打开文件保留一个申请尺寸为 1 MiB 的 scratch，直至 File Dispose；实际池数组可更大。显式 Off 消除默认 Slots16 的每文件 64 KiB pages + 8 KiB scratch，不能据此承诺吞吐更优。
因此资源预算应分别报告实际 active/停止分配文件数、活跃 Builder 的实际 chunk 占用、Append scratch、调用方预编码输入及尚未 Dispose 的自有读结果。及时完成/取消、合理 sizeHint 和释放读结果由消费者控制；数量配置不是完整资源账本。以上依据当前 [RBF Writer](../../src/Rbf/RbfPayloadWriter.cs)、[chunk 归还](../../src/Data/SinkReservableWriter.cs)、[完整 Append](../../src/Rbf/Internal/RbfAppendImpl.cs) 与[读缓存](../../src/Rbf/ReadCache/ReverseReadCache.cs)源码推导，不是 FrameStore 已实施或资源测量证据。

### spec [S-FS-LOWEST-FILE-FIRST] 从可分配 active 文件中选择最低编号

普通 FrameStore 的三种追加入口 MUST 共用这一选择规则：通过参数/owner/资源准入后，从已发布且初始化合法、未租出、未停止分配、TailOffset <= RotationThreshold 的 active 文件中，选择数值 FileId 最小者；没有候选时才创建新文件。
低编号文件仍被租出时 MUST 跳过，不等待它归还、不隐式结束其 Builder。健康归还且仍可分配后，该文件重新按编号参与选择；达到归档条件但尚未完成物理移动的文件不再参与选择。
文件编号按数值比较，不依赖 FS 枚举顺序。是否缓存了打开句柄不改变选择优先级，未缓存的候选可按需打开；不能仅因句柄未打开而改选高编号或新建文件。
选择不依据预计帧长度、预计完成时间或文件剩余软阈值额度，不做轮询/负载均衡，也不声称公平调度或按帧顺序分配。owner 已 fault 或选择后的文件打开/校验失败时按既有错误规则处理，不能静默跳过坏文件继续选择另一份。
（Informative）这个规则倾向先填充较早文件；嵌套租借仍使用其他文件。小型 active 集合可以在内存按编号维护可分配项，索引可从目录重建，不构成全历史分段表或新的持久元数据。

### spec [S-FS-LEASE-SERIAL] 租借与归还串行且可嵌套

文件选择、创建、租借、归还、归档及耐久屏障 MUST 串行执行；首版 FrameStore 及派生对象由调用方串行使用。多个尚未结束的 Builder 不等于同一时刻并行调用。
Builder 的文件、epoch 和构建资源 MUST 独立绑定；未来不同 Builder 各由一个线程使用是单独扩展，不在首版声明并发安全。EndAppend 自动归还时，设计应区分文件内提交与 owner 登记，避免把整个编码/CRC/EscapeKey/写出过程绑定到一个全局当前 Builder。

### spec [S-FS-RANDOM-READ-COMPLETED-PREFIX] 活跃构建期间可随机读取已完成前缀

FrameStore 的指定地址随机读取 MUST 消费 RBF `[S-RBF-RANDOM-READ-COMPLETED-PREFIX]`，允许在目标文件 Builder 活跃时串行读取此前已完成帧，不另设文件或整个 owner 的 Building 读取禁令。同文件通过现有 owned reader 读取，不开第二个同文件句柄绕过 FileShare.None。
Building 期间，请求必须连同帧后 Fence 完整落在当前已完成前缀内；未完成 Builder 的提前地址和跨越边界的范围先于实际读取拒绝。该范围检查不替代真实帧解析、完整内容校验或地址来源责任，裸数值范围不证明主链成员或原始 store。精确范围与错误分类由 RBF 公共合同定义，本层不复制布局常量；本条不扩大 Idle 随机读取的既有合同。
owner 生命周期、共享 fault、串行调用和读结果所有权 MUST 保持；缓存命中也不能绕过这些检查。新申请的扫描、扫描边界和物理后继查询入口仍遵循各自 RBF Building guard；此前已取得的 sequence/枚举器继续按其捕获的完成上界及既有串行、生命周期/fault 合同使用，本轮不增加禁令。具体边界消费 RBF `[S-RBF-SCAN-IDLE-ONLY]`。

## 候选合同

### spec [F-FS-OWN-FORMAT] 新库有独立格式身份

FrameStore MUST 有自己的格式版本、持久 StoreId 和 create-only 格式门，不复用旧目录身份。目录布局按本层版本解释，active 集合不再另存 locator/manifest/status；格式门不随普通租借和轮转反复更新。
新库文件 MUST 是 RBF3；active 和 archive 的实际格式通过 S1 的已打开格式信息检查，不能仅信自家 marker 或文件长度。
Create 为 create-only；Open 不创建缺失 store。格式未知、metadata CRC/字段坏、必要文件缺失明确分类；I/O 保留原异常资格。
格式门发布前的初始创建残留是 incomplete creation，不宣称已有可打开实例；不得盲目覆盖该目录。只读不修尾、不改定位元数据或派生缓存。

### spec [A-FS-CONTEXT-ADDRESS] 地址与上下文资格明确

地址 MUST 明确非法值、segment/ticket 范围、编码、相等和默认规则；Builder 绑定 owner 与 epoch。
来自成功追加、真实主链扫描或已接受上层事实的地址具有明确来源；任意合法数值及一次随机 CRC 读取不自动证明其 intended identity。
只有中性定位信息进入 FrameAddress；Parent、graph schema、提交关系等在上层记录中。跨 owner 或旧 epoch 的 Builder/Writer/read handle 在访问文件前拒绝。

### spec [R-FS-OPEN-RECOVERS] 可写打开接受 RBF 恢复

可写打开 MUST 枚举 active 的规范文件，按 `[F-FS-META-FIRST]` 先检查完整初始化的长度下界，再让 RBF 分别处理每个文件的单尾恢复并校验首帧；报告按 FileId 关联供诊断，Action 不驱动业务回滚或发布。所有需恢复的 active 文件成功打开并完成本层初始化检查后，才能向外签发新 Builder。
Open 不重复读取整个尾 payload 取得结构资格；业务 metadata 作事实前仍 checked-read。archive 历史只读严格访问，残尾不被自动改写。恢复后的 TailOffset 大于当前阈值时，文件不可再租借，按归档协议处理。
正常 Open 核对格式门、私有创建槽位、active 集合及编号恢复所需目录信息；历史缺段/坏内容按需读取或完整 inventory/audit 时报告。枚举目录名不等于审计历史帧，不宣称已验证全部历史文件。只读只验证，不恢复、不移档、不清理创建残留。

### spec [S-FS-SOFT-THRESHOLD] 完成后大于阈值才停止追加

三种追加 MUST 共用事后检查：成功完成帧并取得新的 TailOffset 后，只有 TailOffset > RotationThreshold 才停止该文件的新分配并进入归档；等于阈值仍可追加。未知尺寸与已知尺寸不使用不同轮转算法。
正常取消和可纠正拒绝不推进 TailOffset，不据此触发尺寸轮转。成功完成的合法 RBF 帧不得因使文件超过目标阈值被拒绝、改地址或挪到另一文件。
阈值不保证单文件尺寸不超过该值。初始化边界 <= 阈值 <= SizedPtr.MaxOffset；初始化边界由 `[F-FS-META-FIRST]` 定义，默认值及重开时阈值变化的规则在 S2-Q2 定稿。既有大文件不为新阈值拆分或重写。
（Informative）固定阈值下，新文件只在未超过阈值时开始下一帧，因此正常尺寸超出量最多是一次最大合法追加占用；用 RBF 公共 MeasureWriteSize 计算，不复制 wire 常量。物理长度查询可用 TailOffset，不要求每帧额外 FileInfo/stat。

### spec [F-FS-BUCKETED-PATHS] 归档路径由编号固定计算

首版归档布局 MUST 固定 BucketShift=10：bucket = FileId >> 10，slot = FileId & 1023。每桶覆盖 1024 个编号，与帧大小、完成顺序和内存分段表无关。FileId=0 非法不意味着其他桶的 slot=0 非法。
active 使用完整 FileId 命名，archive 由 bucket/slot 确定唯一位置；精确端序、名称宽度/大小写及路径格式在 S2-Q1/Q2 定稿。桶大小属于格式规则，不能在已存在 store 上随实例配置变化。
随机读取 MUST 按编号定位 archive 或 active，不依赖全历史分段表；只有路径确实不存在才尝试另一个允许位置，不把权限、I/O、格式或 CRC 错误当作缺失回退。

### spec [S-FS-DIRECTORY-STATES] 目录是文件生命周期事实

creating 是未发布的私有初始化槽位；active 是可能继续追加且重开时需检查/恢复的集合；archive 是已按本协议 flush 后移入的只读文件。当前“已租出”只保存在内存，不写状态帧或 status 文件。
规范文件不得同编号同时出现在 active 与 archive；发现时 MUST 报协议矛盾，不能任选、覆盖或删一份掩盖问题。所需文件不存在时明确缺失，不寻找其他编号替代。正常 Open 不承担发现所有未知历史文件的全库 audit。
FileId 首版采用递增非零 uint；没有可用新编号时 MUST 在新文件创建及其地址签发前拒绝，不回绕、不缩短编号。已完成帧及其文件不删除。下一编号倾向由实际目录恢复最大已发布编号后取得，不新增持久计数器；空桶、创建残留及缺号的具体裁决仍在 S2-Q2 定稿。
（Informative）可枚举 active 与归档桶名，再检查最高相关桶的规范文件；最高桶为空时继续寻找实际已发布编号。下一编号的恢复必须覆盖 active 与 archive 两个集合；低编号文件仍租出、高编号文件先完成归档是合法情形，不能假定两个集合按编号分界。FS 枚举顺序不是数值顺序。没有计数器时须计入桶目录枚举成本，不沿用“健康 Open 与历史规模无关”的承诺。

### spec [R-FS-CREATION-PRIVATE] 初始化完整后才发布文件

新文件 MUST create-only 写入私有 creating 槽位，完成本层初始化并 flush/close，再同文件系统、不覆盖地 rename 到 active；成功登记后才能打开供追加、签发 Builder/提前地址。目录管理串行，因此每次至多一个未发布创建槽位。
creating 不接受用户帧、不签发地址。0–3B 的未完成 RBF Header 只可能是私有创建残留，不削弱 RBF 普通 Open 的拒绝规则。本层初始化边界必须包括完整首帧 meta/header，由其 RBF ticket 与公共边界 API 确定；RbfScanBoundary.Empty.EndExclusive 只表示底层空 RBF，不再表示可发布的 FrameStore 文件。
在可证明为本层内部初始化的范围内，重开可写 owner 取消未发布创建槽位；完整初始化也可取消，不必补发布。精确残留前缀与损坏拒绝规则仍在 S2-Q3 定稿，消费下述固定 header schema；不能仅据文件名或一份合法 header 删除未知内容，只读不清理。

### spec [S-FS-ARCHIVE-AFTER-FLUSH] 先确认内容再归档

归档 MUST 在目标文件没有活跃租借时按停止分配 → DurableFlush 返回 → 关闭 writer → 同文件系统、不覆盖的 rename 到计算出的 archive 路径执行。其他文件仍有活跃 Builder 不阻断该文件归档。桶目录可以提前创建，空桶本身不表示某文件已完成归档。
写句柄采用现有 RBF FileShare.None，先关闭再移动，不为轮转擅自改变 RBF 共享规则。active/archive/creating 必须处于同一文件系统；不接受跨卷复制删除作为本协议的 rename。
进程终止、OS/FS 仍运行是本轮故障模型；具体 Windows/Linux 原子 rename 入口仍需 S2-Q3 的实现与实证，不以方法名 File.Move 当作平台资格，也不宣称断电目录项耐久。

| 中断后实际位置 | 本层解释 |
| --- | --- |
| 私有 creating 残留，没有已发布文件 | 按已证明的初始化残留范围处理；无用户地址资格 |
| active 中合法已初始化文件 | 逐文件 RBF 恢复；重新检查阈值，必要时重新 flush/归档 |
| 文件已关闭但仍在 active | 按 active 处理，不依赖丢失的进程内 sealed 标志 |
| 文件只在计算出的 archive 位置 | 归档已发生，按只读访问；前置 flush 由正常协议保证 |
| 同编号两处都有，或所需文件两处都无 | 协议矛盾或文件缺失，拒绝；不补造、不挑副本 |

### spec [F-FS-META-FIRST] 首帧必须是文件 meta/header

每个新文件 MUST 把第一个 RBF frame 用作 FrameStore 自有 meta/header，至少提供版本解释能力。MUST 在 creating 中完成该帧、取得完整 CRC 资格并完成初始化 flush/close，再发布到 active；裸 RBF Header Fence 不足以成为已初始化 FrameStore 文件。
header 保持标准 RBF3 帧，RBF 不感知其业务类型。初始化完整的 active/archive 中缺少、损坏或无法解释必需 header 是错误，不自动补造，也不把它当成可丢弃的普通用户残尾接纳。

**首版记录。** header MUST 使用 Tag=0、TailMetaLength=0、IsTombstone=false；RBF 解码后的 payload MUST 恰为 24 bytes：

| payload byte 范围（半开） | 字段 | 编码与校验 |
| --- | --- | --- |
| `[0,4)` | FrameStoreFormatVersion | uint32 LittleEndian，首版为 1，须与格式门的版本相等 |
| `[4,20)` | StoreId | 16 个 canonical opaque bytes，非全零，须与格式门逐字节相等 |
| `[20,24)` | FileId | 完整 uint32 LittleEndian，非零，须与本文件规范路径所表达的编号相等 |

检查以 owner 已确定的期望版本、StoreId、FileId 为输入：Open 从已校验格式门和正式规范路径取得；Create 从本次拟发布格式门的固定值及预定目标 FileId 取得，不要求 creating 已有公开格式门或正式路径。随后发布的格式门与目标路径 MUST 使用同一组值；MUST NOT 从被检查的 header 反向认领期望身份。表中的格式门/路径比较在创建时对应这些拟发布值。

版本统一解释 store 布局及文件 header，不另立 HeaderSchemaVersion。按字段显式编码，精确消费 24B，不使用 struct dump、varint 或文本身份。此固定记录可用 BCL 的字段读写实现，不要求通用 schema/Tagged Value 框架。RBF 完整 CRC 已覆盖 header 内容和 tag，不增加 payload magic、额外 CRC、预留字段、业务 schema、发布序号、时间戳、索引或可变状态。未知版本、错身份、错形状及内容损坏均拒绝；不猜旧格式或默认身份。header 不替代格式门，也不检测裸 FrameAddress 的原始来源错误。

**初始化边界。** 令 `H = RbfFile.MeasureWriteSize(24, 0)`，`I = RbfScanBoundary.Empty.EndExclusive + H.AppendLength`。合法 header 的 Ticket.Offset MUST 等于 Empty.EndExclusive，Ticket.Length MUST 等于 H.FrameLength；其完整后界由 `GetPhysicalOffsetImmediatelyAfter(ticket)` 或 checked `GetScanBoundaryAfter(ticket).EndExclusive` 取得，并须等于 I。实现 MUST 消费这些公共 API，不复制 RBF wire 开销。当前 RBF3 推导为 52B frame ticket、60B 初始化文件；该数字是格式推导，不是测量结果。仅含完整 header、没有用户帧的文件合法，首个用户帧从 I 开始；只含底层 HeaderFence 的文件不合法。

**共同首帧检查。** 在 Idle 的 owned RBF 句柄上先要求 Format=Rbf3；调用 `ScanForward(showTombstone: true)`，只取第一个物理帧。MoveNext 返回 false 时先传播 TerminationError，没有错误才报缺少 header；不得跳过墓碑、寻找后续同 tag 帧或从 EOF 猜 header。先核对首位置、H.FrameLength、payload 24B、meta 0、tag 0 和非墓碑，拒绝错形状而不按其长度申请大 buffer；再以该真实 ticket 执行完整 `ReadFrame` 或 `info.ReadFrame`，取得完整 CRC 资格后解码并核对版本/StoreId/FileId。正常 Open 的这一步不遍历用户历史；扫描结构资格或随机 CRC 读取单独不能替代首位置与身份检查。

**创建顺序。** `CreateNew(creating, Off) → Append(0, encodedHeader, noTailMeta) → 共同首帧检查 → GetScanBoundaryAfter(headerTicket) → DurableFlush → close → 不覆盖 rename 到 active`。Append 返回 ticket 必须等于首帧检查所得 ticket，TailOffset 与 checked boundary 必须均为 I；creating 中不允许后续用户帧。只有完成这条初始化协议并发布、登记后的文件才能供普通追加使用。失败不签发用户地址，残留的所属/删除资格仍按 S2-Q3，不因 CRC 合格自动认领或补发布。

**可写打开顺序。** 在 store 独占及已知格式门资格成立后、任何可写 `RbfFile.OpenExisting` 前，MUST 用公共文件长度入口检查该 active 的实际长度 >= I；过短直接拒绝且不交给 RBF 修复。只读长度查询须在进入 RBF 的 FileShare.None 工厂前关闭；该预检不解析 RBF wire，也不替代独占资格，I/O/权限/缺失错误保留。随后调用 `OpenExisting(path, out recovery, Off)`，再做共同首帧检查。非 None 恢复若 AffectedFrameOffset 缺失或小于 I，MUST 拒绝；合法用户残尾从 I 或更后位置开始。所有 active 通过后才签发 owner/Builder；失败按 owned 资源合同清理已打开句柄，不补造 header 或身份。

（Informative / Derived）长度前检有必要：正式文件中完整 header body 缺 Key/Fence 时，RBF 可以 CompletedTail。只在恢复后拒绝会留下已修好的文件，下一次 Action=None 就可能接纳；正常 v1 header 的任意未完成前缀都短于 I，前检直接阻断这一窗口。RBF 补尾不改变 HeadLen/body/descriptor，因此错长度、meta 或 payload 不能被补成合法固定 header。该论证消费当前正常 marker-free 顺序前缀模型；不新增任意镜像修复能力。

公共 RBF 工厂先结构恢复，再返回可用于内容读取的对象；带合法残尾的 active 不能先用 OpenReadOnlyExisting 检验 header。因此上述顺序不承诺任何 header 拒绝前都没有物理修尾：长度足够但版本/身份/CRC 错的 active，可能先发生其用户尾结构恢复，再被拒绝。不会据此签发 owner、补造首帧或回退旧身份，不增加 raw parser、prefix-open API 或回滚恢复动作。只读/归档用 `OpenReadOnlyExisting(path, Off)` 后执行同一首帧检查；残尾由只读 RBF 拒绝，不恢复、不清理。

**用户范围与展示。** header 仅在上述首物理位置具有系统意义；普通 Append/EndAppend MUST 继续接受整个 uint tag 值域，包括 0。普通追加和用户 inventory 不签发 header 的 FrameAddress；inventory/preview 只省略已校验首 ticket，不能按 tag 过滤后续用户帧。audit MUST 校验并以文件元信息报告 header，不能把坏 header 当空用户集合。原生 RBF 扫描照常包含这份标准帧，不改变底座过滤规则，不增加 showHeader 开关。ReadFrame 保持中性随机读取合同，不增加针对自行编码 header 地址的特殊拒绝；数值 codec 与 CRC 成功仍不赋予用户来源或发布资格。具体 inventory/audit 类型仍归 S2-Q4。

本条的公共调用顺序依据当前 [RBF 工厂](../../src/Rbf/RbfFile.cs)、[首位置扫描](../../src/Rbf/RbfForwardEnumerator.cs)及[结构恢复](../../src/Rbf/Internal/RbfTailRecovery.cs)；不是 FrameStore 已实施的证明。

### spec [S-FS-OWNED-FAULT] Owned 操作共享 fault

owned append、最终 Builder 提交、flush、轮转和 metadata 输出 MUST 共享 fault；确定参数/state guard 不 fault，RBF preparation/Commit 边界按 S1。
输出异常后停止实例，缓存命中也不绕过 guard；取消资源异常不承诺可继续用。Dispose 尝试释放全部 owned resources，并定稿异常汇总规则。
成功 EndAppend 自动归还已确认；归档/资源释放失败不撤销此前已完成帧，维护入口及维护异常如何映射到返回结果仍在 S2-Q4 定稿。耐久确认范围由下面的 `[A-FS-DURABLE-COMPLETED-OUTPUTS]` 唯一定义，Open 与完成资格不替代该屏障。

### spec [A-FS-DURABLE-COMPLETED-OUTPUTS] 同步确认调用时的全部必要完成输出

ConfirmDurable MUST 同步确认调用时本 owner 所有已完成、尚未确认的必要输出；成功返回是本调用栈对这些输出的耐久证据。MUST NOT 仅因仍有未归还 Builder 而拒绝；未完成 Builder 保持构建内容、声明、epoch、租借与配额，不被隐式提交、取消或赋予完成/耐久资格。首版所有调用串行，屏障执行期间不允许 EndAppend 或其他调用交错，因而本次完成边界不变化。
archive 文件在移动前已 flush；所有必要 active 文件的完成输出都必须确认，包括仍 leased 的文件中此前已完成的输出，不能跳过 leased 文件或只确认一个当前文件。每个重开的 active 即使 Action=None 也纳入首次确认。这个证明利用目录归档协议，不要求调用方认识文件或比较地址。
leased 文件成功 flush 可清除其此次已完成输出的未确认登记；该 Builder 后续成功 EndAppend MUST 重新登记新完成输出为未确认。后续输出必须由新的屏障确认，不能把文件或租借“以前 flush 过”解释为未来帧的耐久证据。健康取消也不让该次提前地址获得资格；位置可能复用，地址来源责任不因屏障改变。
任一 flush 失败 MUST 按 `[S-FS-OWNED-FAULT]` 停止整个 FrameStore owner；其他文件上的 Builder、旧 Writer 及缓存命中也必须在访问 owned 状态前拒绝，受控 Dispose 仍负责资源清理。不能只 fault 单个 RBF 后让其余 Builder 继续。失败不撤销已有完整 bytes，也不产生成功耐久返回。
不允许裸 RBF writer 或外部文件导入绕过登记。范围可包含不相关完整 orphan，无须选择每帧集合；它不解析业务依赖闭包，也不覆盖未来多个 data owner。
首版不提供 DurabilityReceipt、集合合并/复用/过期协议。多个 active 已是当前设计范围，必要输出必须全部覆盖；只 flush 根所在文件不满足本条。未来并发屏障须另建合同。

## 单帧资格与实施片

已知尺寸为 `Opened → Building(address known) → Completed`；未知尺寸只在正常完成时取得地址。Builder 完成/取消与文件归还的边界由 owned wrapper 明确，值拷贝、旧 epoch、重复 End/Dispose 不得二次归还或改变别人的租借。
ConfirmDurable 消费本阶段 `[A-FS-DURABLE-COMPLETED-OUTPUTS]` 的核心合同，不解析业务依赖闭包；交错构建与循环引用的消费资格在后续阶段独立验证。
业务发布不在本阶段建立。

1. S2-A：创建项目/solution；实现不透明 FrameAddress 与固定 12B codec，定稿公开入口/文本/错误和内部布局，以及格式门完整 codec、路径与模式；实施本阶段已定的 StoreId/header codec 和数量上限 config。
2. S2-B：三种 owned 追加、可嵌套独占租借/归还、交错完成、随机读取、基础耐久确认、buffer/fault/Dispose。
3. S2-C：统一事后轮转、编号恢复、creating/active/archive 中断状态与多个 active 的独立恢复。
4. S2-D：进程中断/资源失败、只读、错误格式、规模和 public 指南资格。

至少验证两种无 EventHeader 记录、三种追加、嵌套租借与乱序完成、取消后文件复用、旧地址跨归档稳定、FrameAddress 的 context 限制、archive RBF1 拒绝、创建/flush/close/rename 各窗口、多个 active 独立恢复及完整内容损坏。
地址向量覆盖固定 12B/端序的独立 bytes、FileId 高位与 uint.MaxValue、Packed 高低 32 bits 的往返、SizedPtr 最大起点/长度且末端可越过起点上界、0/default 与截短输入拒绝、嵌在复合记录中仅消费 12B、Begin/End 编码相等、归档和冷重开不改编码、编号耗尽不回绕。codec 验证与内部 struct/数组布局验证分开，不宣称格式解码能检测错 store 或取消预约复用。
覆盖阈值以下/等于/超过、最大帧超阈值仍成功、可纠正失败不归还、成功后不等 Dispose 即可重新申请/确认、重复 Dispose 不二次释放、不覆盖归档目标、1024 边界与 slot=0、编号耗尽/空桶、仅根文件 flush 不充分；首帧 header 覆盖初始化中断、缺失/损坏、未知版本、字段绑定和用户扫描规则。
header 独立 bytes 向量固定 version=1、StoreId 为 hex 01 至 10、FileId=0x89ABCDEF：decoded payload 为 `01 00 00 00 | 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 | EF CD AB 89`，不是原文件 escaped bytes。覆盖精确 24B、截短/多字节/meta/墓碑/错 tag、全零/错 StoreId、0/错 FileId、未知/门不一致版本、HeadLen/TrailerCRC/PayloadCRC/Fence 损坏；坏首帧后存在合法同 tag 帧仍拒绝，合法 header 后同 tag 用户帧仍读出。验证无用户帧的初始化文件合法、首用户 ticket 从 I 开始、原生 RBF 可见和用户 inventory/audit 分类。正式 active 的每个短于 I 的 header 字节前缀在可写 Open 前拒绝，重复打开仍不修改/接纳；尤其覆盖仅缺 Key/Fence 的完整 body。header 后用户帧残尾按 RBF 恢复，report 不得触及初始化区；长度足够但坏身份/header CRC 的文件仍拒绝，即使底层已先修用户尾。私有 creating 中断留给目录协议裁决，不把初始化校验当删除授权。
屏障覆盖 A 完成/B 仍 Building 时成功确认 A、B 租借文件内更早 dirty 帧也被确认、B 内容及租借不变、B 后续完成须重新确认、B 健康取消不获得资格、reopened active Action=None 首次确认，以及任一文件 flush 失败后所有 owned Builder/Writer 停用。
随机读取覆盖目标文件 Building 时成功读取旧完整帧、提前地址及包含帧后 Fence 的跨边界范围在 I/O 前拒绝、CRC/解析/Dispose/fault 仍传播、同一历史读取不受租借分配结果影响。扫描/扫描边界/物理后继保留原 guard，不以随机读取放宽宣称并发或扫描已开放。
数量配置覆盖 M=1/32、满额 Begin 先于租借/创建/输出拒绝、初始化失败不占额度、短写拒绝仍占额度、成功 EndAppend/健康取消释放一次，以及有效/缺失/空 object/非法/重复或未知属性/读取错误、实例固定和下次打开生效；只读忽略写入配置。覆盖满额时合法 Append 仍成功、无关 Builder/ConfirmDurable 不受影响，以及 M=1 时先取得 A 地址、Append B 指向 A、再回填完成 A 的 public 轨迹；不以数量上限测试冒充总内存预算证据。
资源基线覆盖所有工厂显式 Off、active idle 句柄保留与 archive 随机读结束后的关闭/自有结果、扫描提前结束和错误时释放、Open 中途失败清理、旧高上限留下的 active 数大于当前 M 仍完整检查并首次再确认。记录大 sizeHint、重复大 Append 后 scratch/池回收及实际句柄成本；不把配置降低等同于总资源立即缩减，也不宣称没有归档维护的 active 集合受 M 限制。
文件选择覆盖乱序目录枚举、最低编号忙时选下一空闲文件、健康取消/归还后重新优先低编号、尚未移档但已停止分配的低编号排除，以及仅当全部候选不可分配时新建。覆盖低号仍 active / 高号先 archive 后的重开编号恢复，以及最高归档桶为空时仍取得两个集合的实际最大编号。失去缓存句柄不改变选择结果，坏文件不被静默跳过。
最小消费者只按地址取回并解释引用；不同申请/完成次序构建相同逻辑图时，业务结果不依赖文件选择或枚举次序。
健康 Open 成本包含 config 读取、active 文件数、编号恢复所需桶名枚举/有限桶内目录项及小型 header 校验；不宣称与全部历史规模无关。冷历史 reader、异常尾扫描、完整 payload 和 audit 的成本另外报告。
项目遵循仓库 SDK/test pin，不复制生产包清单。源码可用不等于包已交付。

## Ready 阻断项与出口

| ID | 需定稿或实施验证 |
| --- | --- |
| S2-Q1 | 固定 12B FrameAddress codec 的公开入口、文本/错误与目标平台验证、内部 struct 布局；格式门完整 codec/发布协议与上下文保证，复用已定版本 1 及 StoreId 的 16B canonical 编码 |
| S2-Q2 | 精确路径字符串、软阈值默认/变更、递增编号恢复/空桶/缺号/耗尽及目录规模成本 |
| S2-Q3 | store 独占入口、creating 残留裁决、同文件系统不覆盖 rename 的两平台入口与中断实证 |
| S2-Q4 | 成功提交自动归还已确认；Builder/Writer/读结果及 inventory/audit 签名、全 active/leased/重开完成输出登记与后续重新 dirty、completed-prefix 随机读取、归档维护入口、跨文件共享 fault/Dispose |
| S2-Q5 | 保留可写 active 句柄、显式 Off、只读按操作开关且无 reader pool 的基线已定；验证身份/读结果/扫描资源归属、Open 失败清理、历史峰值与实际资源成本，不做精确总资源配额 |
| S2-Q6 | 24B header/单一格式版本/身份绑定、首位置识别、长度前检及 RBF 恢复后 checked-read、初始化与用户 tag/扫描合同已定；实施上述 bytes、拒绝、重复打开和用户残尾验收，不再作为待定设计 |
| S2-Q7 | 可选 framestore.config.json/默认 32/严格校验/实例固定、Builder-only 计数与 Append 的额外短租借已定；验证先拒绝、失败不占额、释放一次、满额 Append 和配置/生效向量 |

完整出口包含多个 active 的交错构建、软轮转、独立使用/恢复，不依赖发布库或其他扩展存在。单文件子合同可以先 Accepted，但不等于文件租借、多文件恢复或整个 S2 已完成。未来多线程 Builder 资格不由首版串行结果推定。
