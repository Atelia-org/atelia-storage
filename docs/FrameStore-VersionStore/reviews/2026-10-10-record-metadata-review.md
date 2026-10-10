# ref 记录元信息：两轮简化裁决

日期：2026-10-10。基线：`d594665`。范围：先撰写 [S4 元信息专项](../08-versionstore-record-metadata.md)，再以 dialectical-simplification 的三个独立角色质询并交叉复核，最后同步 S0/S4/S5/S6/CU 与入口/问题索引。只有文档修订，VersionStore 尚未实施；没有新库、恢复、性能、平台或包验收结论。

## 需求账本

| 来源 | 必须保留的边界 |
| --- | --- |
| 本轮用户决定 | Snapshot/CU 固定8B有符号Unix毫秒，按TailMeta精确长度判元信息形状，不另存version；未来末尾追加固定字段 |
| 既有用户决定/协议 | VSID/ref/SizedPtr身份、完整主链、data-first、必选out PublicationOutcome、single owner/driver串行、ProcessCrashOnly |
| 当前源码/规范 | RBF描述符长度受TrailerCRC保护；meta bytes只有完整PayloadCRC覆盖；CU既有exact-first与异长必要结构消歧、S5无关源后缀仅结构定位 |
| 当前消费者/兼容 | FrameStore已Accepted source-only；VersionStore未创建且无已发布消费者，旧草案/合成探针无兼容义务 |
| 本轮工程裁决 | 原始long公开值/完整int64域、最终私有编码及barrier前一次取时；CU共享时间是writer规则，reader不增加时间相等判据 |
| 未决边界 | D3初始化/根/锁等与D8 tag名称继续原队列；CU仍独立优先候选，本轮不正式并入核心、不做实现 |

## 裁决

需求质疑、最小架构、语义防守三位reviewer独立审阅专项和必要底座；第二轮对最强反例交叉质询。主线程查阅RBF读取/布局与SizedPtr源码，独立裁决和验收集成。第二轮收敛，无需第三轮或新增产品决策。

| Verdict | 结论与具体依据 |
| --- | --- |
| simplify | 用现成TailMetaLength判精确形状，省独立version；首版只定义8B，不为未来分配字段/长度 |
| simplify | 删“必须先取时再预测tickets”：固定8B尺寸与数值无关；保留最终私有编码及barrier前一次取时 |
| simplify | 长度可来自实际已checked FrameInfo或完整帧；保留CU exact-first，普通buffer Append仍可用，不强迫额外info-first/Builder路径 |
| keep | 必要记录未知长度/坏内容硬失败；meta预览不能签发checked结果，完整CRC仍不可省 |
| keep | CU异长替代和历史无关源后缀只保持原有结构职责；strict unknown不扩大为所有观察帧的元信息审计 |
| merge | 专项唯一负责时间/编码/判形；S4负责公开结果/宿主尺寸，S5/CU消费，入口和问题索引只导航 |
| defer | 日期包装/公开时钟注入、单调水位、时间索引、跨成员时间一致性审计及未来兼容策略均无当前需要 |

第二轮三位reviewer均撤回/修订了第一轮“观察到未知meta长度就报错”的过宽表述。严格判形的作用域是必要记录解码，不能替代已有定位/否定成员所需的结构证据。

## 决定性轨迹

1. CU wanted B 的length=100，实际同起点替代length=108/meta12：必要结构合格且ticket不同已经证明旧指定成员不存在，返回false；不为替代记录增加meta schema/内容审计。若actual与wanted ticket相同、meta12，则是必要记录，unknown硬失败。
2. 旧fork点之后的无关源后缀meta12：仍只作既有RBF framing/Trailer定位；找到选中节点才完整资格化meta8。查询不接受或宣称无关后缀内容健康。
3. 强制ReadFrameInfo(oldTicket)可能从异长替代的错误footer取得CRC错误，绕过CU既有framing allowlist消歧；actual完整帧或既有真实FrameInfo提供长度即可。
4. TrailerCRC合法而时间bytes损坏：L2预览可能成功，完整PayloadCRC必须拒绝；无需给时间再加独立CRC。
5. 时钟1000→900、同毫秒fork/重发：保留实际链与位置身份；子初始重新采样但允许同值，来源一致性仍仅比较Roots。

## 集成与验证

- S4保持header24/36B、FrameLength52/64B与初始Snapshot起点60/72；Snapshot改为meta8、长度40..1MiB+36，空字典初始化104/116B。来源golden `(60,40)` 的Packed为 `0x3c00000a`，8B LE为 `0A 00 00 3C 00 00 00 00`。
- S4公开自有 `long RecordedAtUnixMs`；时间不进入RefRevision等值。S5继承/重新发布和CU预编码/Measure/Begin消费专项，header/tag/data范围保持原状。
- 独立核对五组有符号LE时间golden、来源Packed及宿主尺寸，并对10,000种payload长度检查meta0→meta8固定增加8B的对齐算术。只验证编码/尺寸推导，不冒充生产codec或事务测试。
- 检查本轮所有本地Markdown链接、唯一Clause-ID定义、LF、旧当前格式字样与git diff空白；无新增失效链接/剩余当前尺寸矛盾。历史审阅/探针/JSON未修改。
- 语义防守reviewer再次只读核验最终专项与S4/S5/CU集成，无阻断发现；主线程独立检查后提交。未运行.NET build/test，因为未修改实现、测试或项目。

最小实施出口是普通Snapshot的meta8写入/完整读回与自有时间属性；历史/fork/rewind复用，CU正式接纳后增加共享时间与尺寸验证。D3恢复前初始化保护仍需单独形成可执行方案，不能用增大的最小文件长度宣称已关闭。
