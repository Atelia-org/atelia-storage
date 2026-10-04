# S5：ref / branch / tag、历史与可选 checkpoint

状态：**Draft；先完成命名核心，启动规模优化独立取得资格**。
前置：[S0](00-architecture-decisions.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)。扩展同一个控制事实协议，不新增根权威。

## 本阶段目标

核心为 branch name → RefId、不可变 tag → data FrameAddress、current 查询与 ref 历史。沿用 S4 双上下文、RecordToken、Prepare/Commit 和同步 data barrier。
默认完整 control 回放；一份 snapshot 可作为后续独立增强。全量历史、累计 ref 状态和长期启动 SLA 必须分别计成本，不能仅因拆开控制流就声明有界。
项目仍是 VersionStore 与其测试；旧 EventJournal 提供经验，不成为业务类型、目录或代码依赖。

## term `Branch` 命名可变 ref

名称绑定到稳定 RefId，调用方解析后保持 exact RefId/revision，不在每次变更时按名字重新找对象。

## term `Tag` 命名不可变根

一次建立后不改变的 name → data FrameAddress；tag 的事实 token 来自其创建记录。建议与 Branch 使用独立名称空间，同名 Tag 再创建拒绝。

## 核心操作与扩展边界

| 范围 | 操作/效果 | 持久单元 |
| --- | --- | --- |
| 核心 | CreateBranch：创建新 ref 并绑定名字 | 一条创建记录 |
| 核心 | OpenBranch/ListBranches、ReadHead | 已接受事实的内存投影 |
| 核心 | Publish：通用 expected token CAS，允许选合法旧根 | S4 根变更记录；MoveRef 不另设同义事实 |
| 核心 | CreateTag/ResolveTag | 一条不可变绑定 / 事实地址查询 |
| 核心 | ReadHistory | 按 ref 前一 token 的 streaming/分页查询 |
| 便利 | Fork：选择已接受 revision 的根，再 CreateBranch | 同一创建记录；默认无额外 source authority |
| 可选扩展 | Archive：closed + 解除名字绑定 | 若支持，必须一条控制记录 |

明确 fork provenance 成为历史可审事实时，才在创建记录增加来源字段；不默认保存用户时间戳/reason 或复用 Event.Parent。
Archive/name reuse 的首版需求尚未由用户决定；不阻塞核心验收。rename/tag delete、多 parent merge、通用 fast-forward 后置到真实消费者要求，不把它们全部列成 Ready 问卷。

## 候选合同

### spec [S-VS-NAMES-ATOMIC] 命名变更仍单记录生效

CreateBranch MUST 将 ref 创建与名称绑定作为一个记录生效；CreateTag 同样通过 S4 准备/屏障/单记录 flush 协议。
若支持 Archive，关闭确切 RefId 和解除名字 MUST 同记录；不拆 allocation/Init/Bind 或 Close/Archive。
旧 reader 对未知控制 kind/version 拒绝，不跳过不支持的业务效果。checkpoint 等准备不改变本次名称状态。

### spec [S-VS-STABLE-REFS] 名称与稳定 ref 身份分离

对象变更 MUST 使用 exact RefId/revision；同 root 的不同 ref、同名重建不复用旧创建 token。
若支持 Archive/reuse，旧 handle 不变成新对象、历史仍可按旧身份查询。closed/missing、CAS 不匹配、重复 tag、非法名字在业务输出前拒绝。
旧 root 的 fork/tag/rewind 走 data barrier，无需复制帧；消费者承担其选择的旧闭包资格。

### spec [A-VS-FACT-QUERY-THEN-READ] 地址选择与内容接受分别取得资格

替代草案 `[A-VS-SELECTED-CHECKED]`（DEPRECATED）。ReadHead/OpenBranch/ResolveTag MUST 返回已接受控制事实中的 revision/root，明确不证明当前 root bytes 健康或图合法。
消费者接受内容前通过 data FrameStore.ReadFrame 完整 CRC 读取并解码；坏必要根报错，不改 head、不回退旧根，不 eager 读取所有无关 tag 根。
若以后提供 checked resolve 便利接口，应交付已物化 frame/buffer，避免查地址时读一次又让消费者重复读。没有“缓存资格永久健康”的隐含保证。

### spec [A-VS-QUERY-COSTS-EXPLICIT] 首版成本明确且无隐藏索引

替代草案 `[A-VS-BOUNDED-OPEN]`（DEPRECATED）：核心不承诺未经资格的有界 Open。
默认 Open MUST 完整扫描 control 主链并 checked-read 每条控制事实，成本 O(控制记录数及其 bytes)，不扫描 data payload。
内存保留所有已创建 ref 的当前状态/最后 token，以及 names/tags；若开启 Archive，也保留 closed 状态，成本 O(累计 refs + names + tags)，不声称只随 live refs 增长。
历史沿已接受当前 token 的 ref 前驱读取，校验 token/ref/CRC；成本 O(请求历史长度)。ReadHead 不遍历完整 reflog；Inspect 仍依 S4 显式完整主链回放，不由局部历史或 catalog 查无记录证明 Absent。

## 可选增强：一份 snapshot，精确边界，不建多套磁盘索引

本增强不作为首次命名核心的前置；达到实际 Open/heap 目标后，可以单独 Ready/Implementing/Accepted。先选全部-ref snapshot，不同时实现 live-only head/history 页面索引和独立 toolkit。

## term `Catalog-Snapshot` 已接受控制前缀的投影

保存当前 ref/name/tag 表、head token/closed 状态及精确 durable control 边界。边界表达须覆盖 segment 与 RBF anchor；本层调用 S2 公共扫描边界能力，不拿私有 RBF 句柄自行算位置。
它是生产 replay/fold 的派生结果；其自身 CRC/格式、context、anchor 与 suffix 被验证，但正常 Open 不重新重算整个 prefix 来证明 snapshot 每个表项。完整事实审计是独立资格。

### spec [S-VS-FACTS-AUTHORITATIVE] snapshot 不制造发布事实

已采用的 snapshot MUST 绑定精确控制边界，加载后从受证边界回放实际 suffix；不能遗漏 snapshot 后完整记录、补造坏事实或改成旧根。
snapshot CRC/context/anchor 坏时明确失败/维护，不静默全量 fallback。调用方可以**显式选 FullReplay**，只读事实从 genesis 重建内存投影，成本无有界承诺；这不改坏 snapshot，也不容忍坏控制事实。
Inspect 的历史负面查询仍显式全控制回放；snapshot 没有某个旧 token不证明它不存在。快照并不使普通 Open 等同全历史 audit。

### spec [S-VS-CHECKPOINT-BEFORE] checkpoint 在业务首写前

需要 checkpoint 时 MUST 在 S4 control Builder 打开前，确认操作前控制事实 durable → 生成精确 boundary/投影 → temp 写入/flush/close → 原子发布 snapshot → 再建立本次 PreparedPublication。
先完成确定输入/CAS/整条记录容量与内存准备；checkpoint 输出/安装异常使 composite 停止，本次 publication NotAttempted。
发布 durable 后不追加必需 checkpoint 改变操作结果；snapshot 后已完整的业务 suffix 仍按事实生效。

### spec [A-VS-INDEXED-OPEN-BUDGET] 增强资格须明确 control suffix 与表成本

宣称 indexed/bounded Open 前 MUST 定稿最大 control suffix 条数和 bytes、单记录最大长度、snapshot 表大小/内存及工作集假设。
控制只有私有 VersionStore writer，Prepare 预检可在下条记录使 suffix 超预算前 checkpoint；data 可任意增长而不改变这个控制预算。
框架写入/轮转 metadata、snapshot anchor 完整读的成本单列；单记录尺寸由 codec 上限和 S1 Measure 得到，不复制旧 248/252 或 RBF3 开销。
合法旧 snapshot + 超预算 suffix 按明确维护/显式 FullReplay 处理；缺 snapshot 的 indexed 模式不隐式全量回放。表随累计对象增长，预算合同不能写成所有库固定 O(1)。

## 故障与独立验收

核心覆盖 CreateBranch/Tag 不完整被截掉则整项未生效、CompletedTail 合法则整项成立、Unknown 精确查询、ABA/旧根采用、名称/角色混用及内容加载失败不回退。
可选 Archive 验证 closed/unbind 原子和同名新身份；未实现时明确 API 不支持，不填造 pending archive 状态。

snapshot 增强另外覆盖 temp/replace/边界/投影安装中断、坏/缺 snapshot、明确 FullReplay、旧事实坏 CRC、suffix 预算和大量未发布 data 不影响控制 IO。
首版可交付库内验证/重放及测试工具；独立 audit/rebuild CLI、candidate 发布框架按实际运维需求另立工作包。旧 toolkit 不添加新库格式解释。

## 实施片与 Ready 项

1. S5-A（核心）：名称政策与单记录 Branch/Tag codec，沿用 S4 Prepare/Commit。
2. S5-B（核心）：current/历史 public 查询，旧 root、token 角色及一次完整内容读取的例子。
3. S5-C（独立增强）：有规模证据后实现一份全部-ref snapshot、边界/checkpoint 与 indexed 预算。
4. S5-D（条件扩展）：Archive/provenance 或独立运维工具，只有范围明确时实施和验收。

| Ready 项 | 需定稿 |
| --- | --- |
| S5-Q1 | 核心名称字符/长度/case/namespace 与 codec；推荐默认并标明产品语义 |
| S5-Q2 | 单记录命名、列表/历史生命周期及错误签名 |
| S5-Q3 | 若启动增强：实际表规模、control suffix/bytes、anchor 和 checkpoint codec |
| S5-Q4 | 若启动 Archive/provenance：确切产品语义和旧身份查询 |

核心出口是 ref/branch/tag 与历史正确性；snapshot/归档/独立工具分别报告支持范围。未取得增强资格时，不把完整回放库声明为长期有界打开，也不把它与消费者升级/包交付混为一项。
