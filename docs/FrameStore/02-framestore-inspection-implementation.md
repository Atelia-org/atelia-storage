# FrameStore 同步物理检查

日期：2026-10-09。实施基线：`2883531`。本片消费 [S2](02-framestore-core.md) 的 `[A-FS-INVENTORY-AUDIT]`，接续[公开持久化闭环](02-framestore-persistence-implementation.md)。

状态：**本片已完成源码验收；S2 仍为 Draft，完整系统资格另行取得。**

## 范围与接口

公开 owner 新增 `Inventory(Action<FrameInfo>, CancellationToken)` 与 `Audit(Action<FrameFileAudit>, CancellationToken)`，均同步返回 `AteliaResult<long>`，成功值为实际正式集合中完整访问的用户帧总数。无延迟枚举器、公开 reader、暂停 cursor 或持久文件目录。

Inventory 完整校验每个文件的首帧 header，按真实主链检查用户帧 framing/TrailerCRC 并交付纯值 FrameInfo；不校验用户 PayloadCRC。Audit 对全部用户帧执行完整读取并及时释放 pooled 结果，文件正常 EOF、必要临时 reader 关闭成功后才交付 FrameFileAudit。报告含独立的 24B decoded header 副本，不依赖 owner 存活。两者包含后续 tag=0 和墓碑帧，只省略 checked 首 ticket。

两个入口要求零活跃 Builder，整个调用期间设置一个 ScanActive 位。回调中的 Append、两种 BeginAppend、ConfirmDurable、递归扫描在维护或 I/O 前拒绝，不 fault；随机 ReadFrame 合法。回调 Dispose owner 也合法，但回调返回后的状态检查会终止扫描，即使它是最后一个回调。取消、visitor 异常和普通读错误不自行停用健康 writer；实际 owned 关闭/释放失败仍停用 owner。

## 目录与资源

目录 backend 和 Open 共用正式名称验证器。扫描先流式发现完整 max 与 active/archive 冲突，再只读检查 creating 残留资格，最后再次流式发现并检查文件。两遍名称发现仍为 O(A+B+H)，只保留临时 active 编号集合；不累积 archive 编号或所有扫描结果，不在扫描中恢复、删除私有残留或归档。调用方回调累积结果的成本另计。

writer 的 active 扫描复用 retained 句柄；archive 和只读 owner 的文件逐一打开 RO/Off reader，成功打开后立即登记到 owner 的单一临时槽，再检查 header。正常结束、错误清理和 owner Dispose 都先取出并清空槽位再关闭；回调 Dispose 后栈上扫描器不再推进。owner Dispose 尝试临时文件、其余数据资源，最后释放控制锁。目录枚举器在各自调用栈清理。

锁最后针对 owner 台账资源，目录枚举器仍可能在 Dispose 返回后的栈清理中释放，但不能再次推进。对应 public 测试在 Inventory/Audit 回调中 Dispose 旧 writer/RO owner，立即重开同根 writer、归档正在扫描的原 active 并追加，再验证旧扫描拒绝成功及地址可读；不新增可重入 cleanup 注册表。

每次 RBF MoveNext=false 都检查 TerminationError。回调之后、继续 I/O 之前和整次成功返回前检查 owner 状态与取消。必要关闭失败不能产生文件或全库成功报告；主异常与清理异常按既有 CleanupErrors 保序汇总，所有退出路径清除 ScanActive。

## 验收

本轮由目录后端、运行内核、public 验收三个 `gpt-6.1-sol / xhigh` 工作包实施，并另设只读独立审查；主线程维护公开类型/工厂转发、裁决跨包边界、复核实际源码并串行 build/test。

新增 73 个展开用例：public 物理检查 30 项、回调内 owner 移交 4 项、运行内核 20 项、目录后端 19 项。损坏文件均在 owner 关闭后构造；错误帧之后保留合法尾帧，以确保覆盖扫描中途的 TerminationError，而不是仅证明打开阶段拒绝。

| 验证 | 实际结果 |
| --- | --- |
| Windows solution Release build | 0 warnings / 0 errors；首次生产项目重编译只有既存 41 项 Data/Rbf XML warnings |
| Windows solution `--no-build` | 当时整库 2159 passed / 3 Linux-only skipped / 0 failed；FrameStore 373/376，其余七组 1786/1786 |
| Windows 最终 FrameStore 验证 | 加强 callback 内随机读断言后 Release build + 全项目测试仍为 373 passed / 3 skipped；之后增加 owner 移交用例，再 build + 定向测试 4/4 通过。最终有效覆盖 377 passed / 3 skipped，总计 380 项 |
| WSL2 Ubuntu / x64 / ext4 / .NET SDK 10.0.201 | 最终源码隔离至 `/tmp/atelia-framestore-inspection-Y7heTh`，不共享 Windows bin/obj；Release build 成功，FrameStore 379 passed / 1 Windows-only skipped / 0 failed，共 380 项，包含新增 owner 移交场景 |
| 新旧依赖边界 | `eng/Test-Rbf1ReferenceAssets.ps1` 七个旧栈项目通过，精确包/source 保持；FrameStore/test 的实际 Rbf/Data/Primitives assets 仍为 main project |

Windows 整体有效通过 2163 项，不重复累计复跑的同一用例。TRX 保留在忽略的 `artifacts/framestore-inspection-windows/`、`framestore-inspection-windows-final/` 和 `framestore-inspection-windows-handoff/`。初次整库编译曾发现 public 测试误引用 RBF internal 错误类型，已改为检查公开 AteliaError.ErrorCode；未增加跨项目 friend。

Linux 证据保留于 `artifacts/framestore-inspection-linux/`；kernel 为 `6.18.33.2-microsoft-standard-WSL2`，文件系统为 `/dev/sde` 的 ext4。候选 tar 与 305 个 C# 文件的 SHA256 清单保留于 `artifacts/framestore-inspection-source*`，最终源码与 Linux 输入逐一匹配。Linux 本轮只运行 FrameStore 项目，不宣称整 solution、独立 Linux 机器或其他文件系统资格；各平台 skipped 项未计作该平台通过。

行为证据覆盖空库/零用户帧、retained active 与 archive/冷只读、tag=0/墓碑、header 身份/形状/CRC、Inventory 与 Audit 的用户 PayloadCRC 区别、坏中间帧与正常 EOF、前缀回调不能证明全库成功、Builder/重入准入先于维护、回调随机读/抛错/取消/Dispose、header 副本保留、临时 reader 关闭后才 Audit 回调、callback 主异常与关闭失败的汇总、单次关闭及锁最后、只读不修尾/不清 creating。目录后端逐个 checkpoint 注入取消后以排他打开验证私有临时 reader 已释放，bytes 保持原样。

独立审查未发现剩余阻断缺陷，促成了前缀 Audit 报告非空断言及目录枚举器/owner 锁边界澄清。相对文件链接核对 144 项通过；`git diff --check` 通过。

## 剩余边界

本片完善 FrameStore 的物理检查功能，S2 仍为 Draft。真实子进程中断与关键发布窗口、系统资源/规模成本和完整 S2 验收映射仍需后续证据；不将构造损坏文件或薄装饰器故障注入称作真实 kill/断电测试。

目录枚举器关闭失败由单次清理路径和独立源码复核约束，本轮不为该 BCL 对象加入生产注入接口，也不宣称真实 OS 关闭故障、OOM 或每个 syscall 窗口均已实测。完整审计只覆盖实际发现的正式集合，不证明未知整文件丢失、业务依赖闭包或发布资格。

FrameStore 保持 source-only，不改变 `eng/Pack.ps1` 的四包清单；未实施 VersionStore、打包、PackageReference smoke、发布或推送。
