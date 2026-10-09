# FrameStore 首个源码切片

日期：2026-10-09。设计依据：[S2](02-framestore-core.md)；开始实施时基线为 `adbb18e`。

状态：**局部源码实施；S2 仍为 Draft，完整 store 工厂与平台资格尚未取得。** 本轮目标是把后续实现必须遵守的格式和状态转换落实为代码及测试，不完成全部 S2-A–D。

后续状态：本文件保留首片完成时的证据与待办；公开工厂、目录协议和平台准入已由[公开持久化闭环](02-framestore-persistence-implementation.md)接续，当前缺口以该记录为准。

## 已进入代码的边界

| 局部 | 代码承担的职责 | 证据边界 |
| --- | --- | --- |
| FrameAddress | 固定 12B LE 编码、完整 Packed、default/数值拒绝、不透明公开值 | 数值与独立 bytes 向量，不证明来源、完成或耐久 |
| 内部格式 | opaque StoreIdentity、24B gate/header、严格路径、config、阈值 | 纯 codec，不替代真实组件/句柄准入或门发布 |
| 首帧检查 | 已打开 Idle RBF3 的首物理帧形状、完整 CRC、预期身份与初始化边界 | 不打开路径、不恢复、不据此授权删除；可写 Open 仍须先作恢复前初始化准入 |
| owner 运行核心 | 最低 FileId、Builder 配额、共享 Lease/borrow、dirty、停止分配、维护顺序、共享 fault/Dispose | 测试移交真实 RBF 文件；没有公开路径工厂 |
| owned façade | Builder/Writer 值副本共享一次租借；FrameRead 独立持有完整读取 buffer | 不外泄可绕过 bookkeeping 的裸 RBF；已交付 span 仍遵守调用方借用规则 |

项目为 [src/FrameStore](../../src/FrameStore/README.md) 和 `tests/FrameStore.Tests`，仅依赖 main Rbf/Data/Primitives。`StorageSourceOnly=true` 使它不参加打包；`eng/Pack.ps1` 仍是现有四包清单的唯一权威。冻结旧栈继续使用精确 RBF1 package。

## 后续实现的接入约束

内部文件生命周期接口是尚待真实工厂提供的接缝。生产实现必须先完成根准入、owner 锁、格式门、完整正式名称/max、私有残留裁决及 active 恢复/header 检查，再把 owned active 句柄交给运行核心；archive 的最高编号也必须计入移交的正式 max。内部测试入口不成为新的公开导入功能。

新建回调只有完成 creating header 的检查/flush/close 与正式 active 不覆盖发布后才交付句柄。失败清理须保留主错误、单次释放未移交资源，并保留无法裁决的磁盘事实。核心在输出前准备台账容量，成功后先登记正式编号，避免完整发布后出现未登记的可恢复健康路径。

归档由核心执行 fresh flush、取出 owned 句柄并单次 close，然后调用文件系统层完成不覆盖移动。移动失败停用 owner，旧完成输出保持其事实身份；下一次 Open 按实际目录位置重新裁决。测试后端的普通文件移动仅检验此调用顺序，不取得同文件系统/no-follow/原子移动的平台资格。

暂未实施：公开 Create/Open/OpenReadOnly、实际根/固定组件/ordinary/no-follow、真实 owner 锁/bootstrap、格式门 I/O、完整目录发现、私有残留取消、恢复编排、只读 owner、Inventory/Audit、真实归档路径资格及所需 RBF 公共前缀核验。后续扫描接入须补齐 ScanActive、单一临时资源槽、回调重入和终止/清理规则；不得从本轮测试推定这些功能已存在。

不增加公开 `NotImplementedException` 工厂、持久 manifest、资源池、额外 epoch 或可跳过资格的 backend 默认实现。替换内部接缝时保留现有行为测试，再增加真实 public 生命周期、失败窗口和冷重开测试。

## 验证

Windows / .NET SDK 10.0.201 下完成以下验证，测试消费 main 的真实公共 RBF API，未为 FrameStore 增加 RBF friend assembly 或生产测试开关。

| 验证 | 实际结果 |
| --- | --- |
| `dotnet build Atelia.Storage.slnx -c Release` | 最终增量构建成功，0 warnings / 0 errors；首次重编译另有底座既存 XML 注释 warnings |
| `dotnet test Atelia.Storage.slnx -c Release --no-build` | 1894/1894：当时 FrameStore 138，其余七组 1756 |
| 独立复核补测后再次 Release build + FrameStore `--no-build` 测试 | 139/139；只新增一个未知 End 异常委派测试，生产代码未改变，未重复其余七组 |
| `eng/Test-Rbf1ReferenceAssets.ps1` | 七个旧栈项目保持精确 RBF1 package 及指定来源 revision |
| FrameStore / FrameStore.Tests 实际 assets | Rbf/Data/Primitives 均为 main project；测试的 FrameStore 亦为 project |
| MSBuild 属性核验 | `AssemblyName=Atelia.FrameStore`，`StorageSourceOnly=true`，`IsPackable=false` |
| 文档相对链接与 diff whitespace | 129 个链接有效；`git diff --check` 通过 |

最终测试包括纯格式/策略 107 项、首物理 header 9 项、运行内核 23 项。运行测试覆盖三种追加、嵌套/乱序/最低编号、正式 max、满额时 buffer Append、可纠正拒绝与 borrow、成功后不再调用 tail getter 或维护、leased 旧输出屏障/读取、越阈值后的维护顺序、耗尽、独立读结果及一次性清理。创建/Append/flush/close/归档与未拥有 reader 的关闭异常采用薄测试装饰器，正常存储行为仍由真实 RBF 完成。

未知 End 异常测试通过夹具保存的底层 Builder 制造 outer 未镜像的 borrow，验证真实委派抛错后当前 Lease 终结、全部其他入口先拒绝及锁保留到 Dispose。它是局部故障注入，**不是合法的 FrameStore 消费轨迹，也不模拟完整输出后的池归还失败**；后者仍消费底座已有反例及本层 catch 的源码审查。真实 OOM、取消资源失败、进程 kill、平台锁/no-follow、目录崩溃恢复与性能成本未在本轮取得资格。

一名独立 agent 从 S2 与实际源码/测试审查格式、所有权、后验成功边界、维护/故障与清理；主线程复核关键实现并运行上述验证。复核促成了未拥有 reader 的清理故障通知接缝、异常汇总分配失败时保留首异常的退路，以及未知 End 异常补测。后续公开工厂与扫描仍须在本骨架上取得各自资格，不把这份记录提升为完整 S2 Accepted。

本地 TRX 留在忽略的 `artifacts/framestore-skeleton-tests/` 与 `artifacts/framestore-skeleton-final/`。未打包、发布或执行 PackageReference smoke；源码资格与进程中断、Linux、目录协议、性能和包交付资格分别判断。
