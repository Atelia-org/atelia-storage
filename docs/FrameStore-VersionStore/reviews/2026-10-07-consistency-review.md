# 2026-10-07：FrameStore / VersionStore 整组一致性复核

状态：**Informative / 文档审阅与修正；S1 Accepted、S2–S6 Draft；未创建新项目，未运行 build/test 或平台实验**。
三个只读 subagent 分别审阅 FrameStore 核心、VersionStore 发布/历史/命名及阶段间契约；主线程核对当前 RBF 公共合同与源码，统一修订后独立复核。本文记录问题与裁决，正式要求仍在各阶段，不建立另一套规范。

## 范围与结论

覆盖 S0–S6、目录 README、design-questions、FrameLog 候选与现有两份审阅记录；S1 的签名/尺寸/起点资格对照当前源码，历史验收保留其实际身份。
未发现需要改变三种追加、多 active 租借、软轮转、完整 RootMap、单文件 ref、分桶 tag 或 create-only branch 的模型缺陷。下列边界与权威位置问题已修正，并同步相应验收向量。

| 问题 | 失败轨迹或不一致 | 处理与权威位置 |
| --- | --- | --- |
| data 屏障范围过宽 | S0/S3 的“任何发布”会把纯 branch 命名也纳入，因无关 data Builder 而拒绝 | RootMap 的 CreateRef/PublishRef/CreateTag 执行屏障；CreateBranch 只确认自己的绑定记录。见 [S0](../00-architecture-decisions.md)、[S3](../03-framestore-batches-and-durability.md)、[S4](../04-versionstore-publication.md)、[S5](../05-versionstore-names-and-indexes.md) |
| 空桶 rename 与 tag 业务结果混淆 | 首次 CreateTag 尚未 Append，header-only 桶 rename 异常被通用表误记 Unknown | 本次 tag 为 NotAttempted，metadata 可能不确定、实例停止；重开检查并保留合法空桶。S4/S5 |
| 起点硬界误作文件总长度上界 | 用 MaxOffset - TailOffset 作硬预算，会额外拒绝最后一个合法帧 | 分别检查帧尺寸与起点，允许合法末帧末端/Fence 越界；下一次追加确定拒绝。S4/S5 与 [S6](../06-integration-and-delivery.md) |
| ref 创建与更新的恢复向量混用 | CreateRef 正式 rename 前没有“旧字典” | 已有 ref 更新为完整旧/新；创建为不存在/完整初始 ref，不能补空。S6 |
| 裸地址来源检测承诺不清 | S4 的“绑定上下文校验”可能被理解为自动识别另一 store 的合法数值地址 | 库检查格式门身份、生命周期与值域；裸地址原始来源及完成/闭包仍由应用保证。S4 对齐 [S2](../02-framestore-core.md) |
| 编号恢复例子缺少交错状态 | 低编号仍 active，高编号已 archive；仅取一个集合的最大值可能复用编号 | 最大已发布编号覆盖两个集合；空最高桶继续查实际编号。S2 与 [问题索引](../design-questions.md) |
| 核心屏障双写且权威在后序 | S2 独立提供 ConfirmDurable，却在 S3 再定义同一规范 | `[A-FS-DURABLE-OWNER]` 原文归入 S2、ID/语义不变；S3 和扩展引用核心，组合资格单独验证 |
| 导航/历史身份残留 | S3 图仍称批量预约、目录仍称 sealed、历史备注仍定位 Commit 草案 | 同步交错构建/archive 术语；只更新旧审阅的范围备注，保留历史正文 |

## 候选比较与取舍

保留 header-only 空桶初始化，再用普通 Append/flush 发布首 tag。把首 tag 放进私有桶并随 rename 一起发布也可设计，但会新增首条/后续两套业务发布边界，本轮只修正结果分类即可。
保留 Publication 同时覆盖字典与名称事实，精确限定 data 屏障范围。让 branch 也强制屏障会引入无关 data IO/Builder 拒绝；从 Publication 排除 branch 又会牵动统一结果证据，均无必要。
基础屏障在 S2 唯一定义，S3 消费并验证。留在 S3 会迫使独立 S2 反向依赖后序，或继续维护两份同义规则。全仓未发现指向原 S3 条款位置的 Markdown 深链；Clause-ID 保留且仅声明一次。
裸地址保持局部编码，不加入重复 StoreId。首版可写 VersionStore 仍借入可写 data owner，branch 不新增 data 屏障；不增加“仅 branch 可写、data 只读”的额外能力组合。

## 仍须工程定稿的事项

S2 的 header/config/address/格式门 wire、目录/编号算法、独占与平台 rename、归档维护及资源异常，S4 的 codec/结果证据/目录模式，以及 S5 的名称/hash/路径与历史生命周期，继续在所属 Ready 表关闭。
这些事项不由本次文字修订取得实施资格。FrameLog、ref/tag 分段、差分/checkpoint、随机 revision 读、CAS 与精确尝试追踪继续保持原有延期或候选身份。

## 验证边界

核对 [S1](../01-rbf-sized-append.md) 与 [RBF 接口](../../Rbf/rbf-interface.md)，并检查 [起点校验源码](../../../src/Rbf/Internal/RbfFrameWriteCore.cs) 与 [公共预算入口](../../../src/Rbf/RbfFile.cs)；预算只试算占用，不检查文件位置。
修改后的独立只读复核通过；最后补齐 README 纵向实施表中屏障归属的导航措辞。静态检查覆盖 14 份文档、133 个本地链接（含 3 个锚点链接）、代码围栏、表格、尾随空格及 `git diff --check`，均通过；修订前后 88 个 Clause-ID 声明全部保留且无重复，只有 `[A-FS-DURABLE-OWNER]` 移至 S2。
这些检查用于核对本轮文档修订，不能替代后续源码、进程中断和两平台验收。
本轮仅修改文档；不将旧源码测试、历史评估或本次静态检查重标为新库已实现/可交付。
