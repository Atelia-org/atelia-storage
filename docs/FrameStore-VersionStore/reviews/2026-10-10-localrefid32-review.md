# 固定 4B LocalRefId：配套设计复核

日期：2026-10-10。状态：**文档修订与两轮独立质询完成；VersionStore 仍未实施，S4–S6 仍 Draft**。核对基线 `ebb141e`；本文不是实现、恢复、平台、性能或包消费验收。

## 边界与需求来源

- **用户本轮决定**：LocalRefId 采用固定 4B LittleEndian，配套修订后使用 dialectical-simplification 检查。
- **用户既有决定**：无 branch 名称；创建时持久预留低号区；自动/指定两种创建入口，指定仅用预留号；ref 不删除、不复用。CU 是独立优先候选，本轮不将它并入核心。
- **当前实现事实**：根 README 与 FrameStore README 确认 FrameStore 已实现并取得独立源码资格；src/tests 中尚无 VersionStore，没有已发布 VersionStore 格式或生产消费者。冻结 RBF1 旧库不产生新栈兼容义务。
- **保留的设计合同**：RefId 绑定 VSID；单 owner/driver 串行、ProcessCrashOnly；data-first/Outcome；fork 来源使用 exact 完整 SizedPtr；CU 使用 self、完整成员表相等和 fresh/no 旧组补写。
- **范围外待定**：D3 初始化保护/store 创建/准入/private 与 D8 tag 名称继续原问题队列；本轮不增加实现项目、64bit 扩展、宽度协商、迁移、分配账本或事务身份。

## 两轮质询与裁决

三位 reviewer 分别承担 demand skeptic、minimal architect、semantic defender，独立阅读 S4/CU 与配套设计/diff；第二轮交叉检验最强相反场景。主线程独立核对源码、尺寸、golden 与全目录残留，按证据裁决，无需第三轮。

| 裁决 | 位置/最小模型 | 依据与具体失败边界 |
| --- | --- | --- |
| simplify | [S4](../04-versionstore-publication.md) 的固定字段、公开 LocalId/目标参数、L/H 均为 uint32；唯一新条款 `[F-VS-REF-ID-4B]` | 用户明确缩宽；一个固定 codec，无已发布格式兼容负担 |
| simplify | [S6](../06-integration-and-delivery.md) 直接消费 S4 当前格式/路径及独立向量 | 审查发现 S6 仍写旧 48B 门、8B ID、16hex 路径、28/44B header；这些会使按两文实现的写入与读取互相拒绝，已消除旧口径 |
| keep | unsigned 高位、Max 前检/checked 加法、低号独立、VSID、完整 Packed ticket | signed 发现会误认高位；Max 后回绕0；全库耗尽禁写会误阻断空闲低号；A/B 的 local=7 误传；相同 offset 不同 length 误认帧 |
| keep | [CU](../07-versionstore-cross-ref-conditional-update.md) 的 self、完整表、fresh 及错误传播 | 旧 G 的 A 完整而 B 缺，新 H 复用 B 起点；去掉这些约束会跨批拼组。字段缩宽不改变共同发布判据 |
| delete | 当前正文的旧 8B 编码条款与旧尺寸引用 | 新 4B 条款替代旧约束；不把旧锚点复用于新语义，不增加兼容别名或 DEPRECATED 入口，历史由 Git/原记录保留 |
| defer | 扩宽/协商、CLR 紧凑布局与性能优化 | 无当前消费者或测量；20B 逻辑字段之和不能推出 CLR 步长或吞吐收益 |

第二轮明确调整：S6 不必再复制整套新尺寸，以所属规范与独立向量为权威即可。新条款替代旧编码约束符合用户清洁草案要求；稳定锚点规则不能成为维持旧 8B 立场的理由。没有由此次缩宽引出的待决产品问题。

## 独立核验

| 项目 | 当前结论 |
| --- | --- |
| 格式门 | 44B；L 位于 `[36,40)`，CRC 位于 `[40,44)`；指定示例 CRC32C=`0x6a2a319e`，LE=`9E 31 2A 6A` |
| RefHeader payload | 24B/36B；自身 `[20,24)`，来源 `[24,28)`，完整 Packed ticket `[28,36)` |
| 物理尺寸 | header FrameLength=52/64B；初始 Snapshot 起点60/72；空 map 初始化文件96/108B；使用 RBF 公共 Measure 合同推导 |
| 来源 golden | SizedPtr(offset60,length32) 的完整 Packed LE=`08 00 00 3C 00 00 00 00` |
| 编号/路径 | 0 无效；L<uint.MaxValue；覆盖高位、最后自动号Max、耗尽及低号不推进H；文件名8位小写hex |
| CU 成员表 | 每项4B LocalRefId+8B ticket，无成员 padding；payload=`h+rᵢ+12k`；全组成员字段较原草案少 `4×k²` bytes |

主线程用相互独立的逐 bit/查表 CRC32C 计算，并对标准 `123456789` 向量核对；解析文档 golden 核验字段/票据，检查编号边界算术、尺寸及路径。当前直接包含的设计文档无旧 schema 残留，新 RefId 条款恰有一个定义；七份修订文档的134个链接没有新增失效目标，LF 与 `git diff --check` 通过。

源码依据：[RbfFile.MeasureWriteSize](../../../src/Rbf/RbfFile.cs)、[SizedPtr](../../../src/Data/SizedPtr.cs)、[RollingCrc](../../../src/Data/Hashing/RollingCrc.cs)。这些静态与算术检查不替代生产实现或完整宿主验收；本轮未运行 .NET build/test。

旧 RefFrameSchemaProbe 的75项、112→104→112→None反例及 CU 合成探针保留原证据/格式身份，没有改项目或重标结果。当前4B/变长RootMap的实际初始化保护仍归 D3；尺寸缩小不关闭该缺口。

最小后续实现片仍为 S4-A 的公开值、fixed4 codec 与格式门/ref header 组合，使用本轮独立向量及高位/耗尽边界测试；发布生命周期依原 D3 合同推进。普通 Snapshot 的 RootMap、FrameAddress codec 与固定8B ticket不变。
