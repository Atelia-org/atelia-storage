---
title: "RBF3 读取资源与输出成本实施验收"
status: "Accepted"
normative: false
---

# RBF3 读取资源与输出成本实施验收

2026-10-03完成[重构方案](rbf3-resource-and-output-refactoring.md)：修复两处pooled读的重复归还窗口，并在W:冻结对照后将RBF3一次打包输出范围扩大到**total≤8192B**。RBF全套696/696通过。改动没有改变wire bytes、CRC职责、原ticket、普通打开或故障恢复规则。

## 交付与独立验收

| 提交 | 内容与入口 |
| --- | --- |
| `f36ad5c3a29af152c25bbd2063c14dd0dca4b386` | 两处读取局部ownership＋唯一finally；内部失败释放接点；[5个focused case](../../tests/Rbf.Tests/Internal/RbfPooledCleanupTests.cs) |
| `55a92e811706eb3ff34dd0d1bdb66c502f0b7a77` | [阈值probe](../../experiments/RbfFastOpen/Rbf3ProductionProbe/ThresholdProbe.cs)、[冻结/比较/JIT runner](../../experiments/RbfFastOpen/Rbf3ProductionProbe/threshold_compare.py)及[操作入口](../../experiments/RbfFastOpen/Rbf3ProductionProbe/README.md#4kib--8kib-输出阈值比较入口) |
| `d74fb974f995884c911465b9cdb25a89dea138d2` | RBF3专用8KiB常量及两处引用，legacy 4KiB常量不变；[14个新输出case](../../tests/Rbf.Tests/Internal/Rbf3SmallAppendOutputTests.cs) |

读取实现、实验、输出回归分别委派；独立reviewer从源码、fixture、raw samples和JIT作反审阅。主线程亲自读diff/测试、裁决阈值、执行全部.NET步骤并核对证据。C3曾先接入旧body：3个失败用例实际观察到两次清理尝试，另2个通过；修复后同5个case全通过。测试首次Shared.Return后才注入异常，重复只计数/抛错；读异常的普通释放回调也有此保护，避免坏实现污染Shared。成功测试回调若误入只抛错、不提前归还仍被结果持有的数组。

完整帧及非空TailMeta的成功对象先构造，再交接本地ownership；default/生命周期/profile/ticket guard和空meta在Rent前，FrameInfo仍复用TrailerCRC资格。Return异常不被重试，可能替换原读异常或待返回Result。确定性注入不表示已观察到Shared真实OOM，也不承诺任意资源失败后健康续用。

## 测试、原始材料与构建身份

原始目录为 `W:/RbfFastOpen/rbf3-resource-output-20261003`，TEMP/TMP为其`temp`子目录。.NET步骤由主线程串行执行，每组先Release build，再匹配`--no-build` test。

| 验证 | 实际结果 | 原始材料（相对原始目录） |
| --- | --- | --- |
| C3旧body＋失败接点 | build 0 errors、43 XML warnings；5个case中3失败、2通过，失败均为Expected1/Actual2次清理 | `logs/c3-red-build.log`、`logs/c3-red-test.log`、`test-results/c3-red.trx`、`red-source/` |
| C3修复 | incremental build 0 errors、20既有XML warnings；focused5/5，全套682/682，无跳过 | `logs/c3-green-build.log`、`logs/c3-green-test.log`、`logs/c3-full-test.log`及对应TRX |
| 实验基线/候选 | 两次Release build分别41/20既有XML warnings，均0 errors；完整输出分别冻结 | `logs/threshold-*-build.log`、`baseline/`、`candidate/` |
| 输出修复 | build 0 errors、41既有XML warnings；focused14/14，全套696/696，无跳过 | `logs/output-build.log`、`logs/output-focused-test.log`、`logs/output-full-test.log`及对应TRX |

[机器可读快照](review-evidence/rbf3-resource-output-20261003.json)保存SDK10.0.201、runtime .NET10.0.5、Windows x64、AMD Ryzen7 1700及W: NTFS事实、118份最终验证源码/配置/测试哈希、两套各91份实验源码/配置和14份Release产物哈希、实际加载程序集身份、raw成对结果和日志/TRX/JIT身份。主线程独立核对了两次比较的**3002份原始工件哈希**。快照与W:`evidence.json`相同，SHA256为`326b79b26509592c7ec0e8bac79cc0a7531cf39c626b1e314c239a332c0a5e72`。

**测试与性能的实际编译身份分别记录。** C3 green在以2290601为父提交、含未提交C3修复的工作树上运行，binary ProductVersion为`1.0.0+229060143fbdae94f427b7a574bb55f4f1666b76`；其内容随后提交为f36ad5c。性能两组都在f36ad5c父提交构建，包含当时未提交的probe源码，候选另带三处受控阈值变更；两份源码/配置闭包只在Append源文件有差异，Data/Primitives DLL相同。最终696项是在55a92e8父提交、含阈值及新测试的工作树构建，四个验证DLL ProductVersion均为`1.0.0+55a92e811706eb3ff34dd0d1bdb66c502f0b7a77`。最终源码/测试内容与d74fb97一致，生产源码也与冻结候选逐文件相同；这不是clean d74fb97的另一次binary执行或包交付证明。

## 阈值测量与选择

两个冻结Release bundle由独立子进程加载。每组7轮交替A/B、B/A；第一组每case目标8MiB且最多16384次，第二组16MiB且最多65536次。15个布局覆盖total512、4096/8192两侧、4–8KiB和65536B，代表meta0/3及payload phase0/1。输入用生产selector，实际首尾Key分别记录，非零Key无需相等。固定`DOTNET_TieredCompilation=0`、`DOTNET_TC_QuickJitForLoops=0`，计时不安装write hooks，写调用数另pass在CreateNew之后采集。首次指JIT/进程Shared pool预热后的新File首Append，工厂与Dispose不在计时内；同File暖态分配、scratch保留及DurableFlush分别记录。

下表为暖态**逐对B/A延迟比值**中位数，小于1表示候选更快。两组批量设置不同，合并比值只作探索汇总；七轮与中位数本身不构成统计置信证明。

| Key0 total bytes | 首组7对 | 确认7对 | 合并14对 | 候选胜出 |
| --- | ---: | ---: | ---: | ---: |
| 4100 | 0.491 | 0.517 | 0.499 | 14/14 |
| 6144 | 0.488 | 0.500 | 0.499 | 14/14 |
| 8188 | 0.602 | 0.546 | 0.571 | 14/14 |
| 8192 | 0.513 | 0.568 | 0.561 | 14/14 |

四个目标的库write-call从4次变为1次，对应合并中位延迟下降约43–50%。小帧和外区/非零Key方向混合，首组与确认组多有反转：512B Key0合并比值1.039（6/14对更快）、512B marker为1.004（7/14对更快）；4096B Key0为1.014。不能排除数个百分点的小帧变化，也没有证明非零Key普遍加速。接受8KiB的依据是目标收益各14/14可复验、没有跨两组一致的明显控制区退化、条件栈成本有界，以及4–8KiB非零Key的File保留scratch从1MiB降为0。此选择仅限所测大小与环境，没有workload分布时不声称整体吞吐提高。

所有比较还核对了真实raw Key、实际加载DLL路径/hash、4100B Key0的阈值行为、请求长度/顺序及scratch首用/暖态/Dispose。allocated为0仍可能保留1MiB scratch；库write-call数不表示设备IO数或原子写。Python独立bitwise CRC32C/units/XOR oracle完整复核了**1680个实际端点wire**，并核对其对应真实文件首尾及EOF。中间批量帧没有逐帧独立审计。

JIT另pass捕获同设置FullOpts反汇编：两版均保存8个寄存器64B、静态`sub rsp,456`；打包分支IG24/25条件localloc由4096B改为8192B，并对同量MEMZERO。该方法正常打包路径自身保留量约8712B；探页循环的`jae`会瞬时再下移一页后恢复，不能把8712B称为严格rsp峰值。大帧路径没有全局预留8KiB，整调用链峰值没有测量。主线程接受这个明确的分支成本；真实消费者栈预算或小帧回退另触发单一替代实验。

## 新输出分支的故障资格

新增14个case由公共工厂创建并使用生产selector。6144B帧、meta3/payload phase1的Key0与marker输入，各实际写prefix2、128、6136、6138、6142B后抛IOException。用同一owned handle观察物理长度及prefix，断言原tail未推进、旧FrameInfo和门面共享永久fault、Dispose关闭handle。只读重开拒绝且字节不变；可写重开对2/128B截到原边界，对完整encoded Trailer/部分Key/部分Fence补原后缀，保留原prefix、原ticket和Key，公共完整CRC读与独立wire校验通过；再次打开为None。

Head2B尚无Key证据，只用marker输入保证零Key被排除；其他prefix从同次实际encoded payload推导Key并验证恢复保留。8192/8196B×两类Key四个边界验证请求次数/长度/连续offset、独立wire及公共读/只读重开。scratch字段证据来自冻结probe；新xUnit文件没有该字段断言。上述是实际部分写入后抛异常，不是外部进程终止或断电实验。

本轮Data C1、MaxOffset组合和完整Builder成本保持原触发式延期；下游适配、整solution、Data.Tests重跑、外部终止矩阵、PackageReference smoke与pack/publish没有执行。原0999851与c4377fd快照保留自己的源码身份，未重标为本次验收。
