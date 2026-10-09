# FrameStore 公开持久化闭环

日期：2026-10-09。实施基线：`886389c`。前一片的格式与运行核心证据见[首个源码切片](02-framestore-core-implementation.md)。本片消费 [S2](02-framestore-core.md)，范围为真实 `Create → Append/BeginAppend → ReadFrame → ConfirmDurable → 归档 → Dispose → Open/OpenReadOnly`。

状态：**本片已完成源码验收；S2 仍为 Draft，尚未实施 Inventory/Audit 或完成全部系统资格。**

## 接口与所有权

公开 `Atelia.FrameStore.FrameStore` 是唯一目录工厂入口。Create/Open 接受已定单一 `rotationThresholdBytes`，OpenReadOnly 不消费写策略；StoreId 以只读的 16 个 opaque bytes 交付，IsReadOnly 表示访问模式，RecoveryReports 按 FileId 保留本次可写打开的 RBF 物理恢复诊断。诊断不证明业务发布或上次调用成功。

工厂按门、正式名称/max、私有残留、active 初始化前检/恢复/header、统一归档维护的顺序取得资格。未移交文件先清理，已移交文件由 core 清理，控制锁始终最后释放；正常返回前不会向调用方交付 owner。ReadFrame 的成功 buffer 在临时 reader 关闭成功后移交，不依赖 store 继续存活。

生产目录 backend 复用已有格式 codec、首帧检查、运行核心及 public RBF。私有文件只通过 `RbfFile.IsInitialFramePrefix` 核验原始实存 bytes，不进入恢复；合格残留在 writer 下删除，在 reader 下保留。没有新增 manifest、修复门、隐式目录升级或全历史句柄表。

## 实际根与平台边界

调用方选择根及其可信父路径；只把路径绝对化，不建立祖先句柄链或物理别名登记。根的末组件及受管组件须是实际普通目录/文件，拒绝 symlink、junction/reparse、FIFO、设备等特殊项。Create 只允许创建末级根，父目录须已存在。固定名称以实际直接枚举取得精确拼写；Open 不建立根其他内容的永久白名单。

owner 锁和稳定目录假设共同支持先做类型/名称资格、再用已有 public RBF 路径工厂打开；不增加底座 handle 导入。此协议不协调裸 RBF 或外部并发改写、替换、复制，也不承诺防御敌对 TOCTOU。受管 creating/active/archive、归档桶及其数据叶文件必须同文件系统；移动使用原生不覆盖 rename，不能退回复制后删除。

Windows 使用 no-follow 元数据检查与同句柄共享模式锁；Linux 使用 no-follow 类型检查和显式 checked nonblocking flock。仅支持这两个平台，缺少所需原语时拒绝。Windows 要求能取得实际 volume GUID，不能取得的卷/UNC 拒绝；Linux 要求 libc/kernel 提供 statx 所需 type/size/mount 字段和 renameat2 RENAME_NOREPLACE，原生 flag 分支限定 x86/x64/arm64，当前运行证据为 x64。逐叶与父目录比较 device/mount，未取得 privileged bind-mounted leaf 实测。机制选择不自行证明其他环境的平台资格。

## 验证与剩余范围

本片由四个 `gpt-6.1-sol / xhigh` 有界工作包完成平台、RBF 前缀、目录 backend 与独立 public 验收，主线程负责公开工厂、接口裁决、代码复核和串行 build/test。独立验收者复核实际所有权/失败顺序。复核和运行促成了父目录重复枚举成本、格式错误分类、调用方 TEMP/root 大小写边界及 junction 测试清理修正。

| 验证 | 实际结果 |
| --- | --- |
| Windows solution Release build | 成功；最终增量构建 0 warnings / 0 errors，首次重编译有既存 41 项 Data/Rbf XML warnings |
| Windows solution `--no-build` | 其余七组全部通过 1786 项（Rbf 865、Data 288、Primitives 75、Binary 214、EventJournal 174、Segment 48、Toolkit 122）；FrameStore 唯一失败为新 junction 夹具的清理，随后修正 |
| Windows 最终 FrameStore Release build + `--no-build` | 304 passed / 3 Linux-only skipped / 0 failed；共 307 项。夹具修正及最终平台改动后仅重复受影响的 FrameStore 项目 |
| WSL2 Ubuntu / x64 / ext4 / .NET SDK 10.0.201 | 从候选源码隔离复制至 Linux `/tmp`，不共享 Windows bin/obj；FrameStore Release build 成功，306 passed / 1 Windows-only skipped / 0 failed |
| Linux RBF 回归 | 同一隔离源码 Release build 成功，Rbf.Tests 865/865 通过（含前缀入口 30 case 与零分配断言） |
| 新旧依赖边界 | `eng/Test-Rbf1ReferenceAssets.ps1` 七项目精确 RBF1 package/source 通过；FrameStore/test 的实际 assets 均为 main Rbf/Data/Primitives project |

Windows 本轮合计有效通过 2090 项，另外 3 项 Linux 专属向量在 Linux 实际通过；不把被跳过向量计作相应平台的通过证据。Linux 不是整 solution 或独立机器资格；本次 kernel 为 `6.18.33.2-microsoft-standard-WSL2`。详细 TRX 留在忽略的 `artifacts/framestore-persistence-windows/`、`framestore-persistence-windows-final/` 和 `framestore-persistence-linux/`；候选源码 SHA256 清单与 tar 保存了 Linux 测试输入。

Public 验收覆盖完整创建/三种追加、多个独占 Builder 乱序完成、leased 旧前缀读取/确认、阈值升降与 Open 排空、归档及按原 12B 地址冷重开；涵盖 0..23B 门、0..I-1B 正式 header 的重复拒绝、0..I 私有合法初始化前缀的 RO 保留与 writer 取消、坏门/私有/名称/双位置先于 active 恢复、两个 active 的独立物理恢复、坏身份/形状/CRC、archive 按需内容检查、配置与 owner 互斥。平台测试实际覆盖 Windows junction、Linux symlink/FIFO、跨 mount 拒绝和不覆盖移动。

字节前缀/残留构造属于中断后事实验收，不等同真实 kill。创建/读/关闭/移动失败后的故障与单次清理，结合真实目录错误向量、已有运行核心薄装饰器故障注入及本轮工厂/backend源码独立审查取得；没有宣称模拟所有 OS 关闭失败、真实 OOM 或每个原生 syscall 窗口。正式名称发现是 O(A+B+H)，另有 active 排序 O(A log A) 和实际类型 metadata、恢复、定点维护检查成本；未测规模性能。Windows/Linux 源码测试与包消费资格分别报告。

Inventory/Audit、ScanActive/visitor 重入及临时资源槽仍属下一片。真实进程中断、资源/规模成本与完整 S2 Ready 审核另行取得；FrameStore 保持 source-only，不加入包清单，本片不实施 VersionStore。

文档相对路径核对通过 161 项；RBF 接口中已有的原仓 `agent-team/meeting` 历史链接保留原身份，本片不修迁移来源链接。`git diff --check` 通过。未打包、发布、推送或执行 PackageReference smoke；本片没有新包交付。
