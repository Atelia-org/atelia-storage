# VersionStore Linux 与有限资源取证（V4-C）

日期：2026-10-11。生产源码基线为 `1176bd2255577b5b626f1171647b170adb444795`。本轮消费[实施计划](12-versionstore-implementation-plan.md)的 V4-C，补 WSL2/Linux 回归和 public 资源消费者；不修改生产协议或持久格式。源码、真实进程、有限资源与包消费各自保持证据边界。

## 探针与测量口径

[VersionStore.Smoke](../../tools/VersionStore.Smoke/README.md) 的 `resource-write/resource-read` 仅调用 public API；支持16、64、256个 ref。每组包含来源、子 fork 和孙 fork，第一组来源在精确 fork 后继续追加随规模增长的后缀。每个 ref 有一份 tag，约半数 tag 有意落入同一桶。应用使用1KiB不透明 root 和256B data 记录，并逐项核对实际地址、完整 payload、历史来源和相关注记。

只读消费者分两个新 owner：第一个重复三次 ListRefs、ListForks、完整继承历史、ScanTags、Audit 和有限 named tag 查询；第二个重复三次全部 ReadRef，单独观察 owner 缓存。普通读取缓存不能归因于历史或声明图遍历。每次冷读都由父进程检查完整文件清单和 SHA-256 不变；此处“冷”只表示新进程/owner，不表示清除了 OS 文件缓存。

写进程另测所有创建 owner 关闭后的新 RW owner 打开及正式集合发现，以及连续 CreateTag 批次（包含 ReadRef head 捕获）。RW 重开仍处于同一写进程、runtime/OS 缓存已热；ForkRef 创建只参与夹具准备，未独立计时。

计时及当前线程累计分配区间包含 public 调用、应用 callback 与结果校验；资源采样在区间外。Windows 记录进程总 handle；Linux 记录 `/proc/self/fd`，另按实际链接目标统计夹具根、data 和 versions 的 fd。两者不能直接数值比较。WorkingSet、GC heap 和总句柄是采样观察，不是精确存活对象、池占用或操作峰值；不做 RSS/延迟性能门限，也不从一次运行推导吞吐承诺。

Linux 专属 fd 核对要求临时遍历返回后 versions 句柄不增长、同一 owner 反复 ReadRef 不新增缓存项、owner 关闭后夹具句柄归零。Windows 总 handle 含运行时噪声，结合现有所有权源码和清理测试解释，不能独立证明所有路径无泄漏。进程测试仍只取证 V4-B 已列明的六个 public 停点；内部 pending/rename/flush 中途、断电和设备失败并未因此取得资格。

## 执行证据

Windows 为 Windows 10.0.22000 / x64 / SDK10.0.201，测试及资源夹具实际位于 W:/TEMP（NTFS）；仓库位于 E:。Linux 为 WSL2 Ubuntu24.04.4 / kernel6.18.33.2-microsoft-standard-WSL2 / SDK10.0.201，执行用户root。固定HEAD归档解到 `/var/tmp/atelia-storage-v4c-1176bd2.3f2q07/repo`，源码、独立NuGet cache、bin/obj与新栈TMPDIR均在 `/dev/sde` ext4，未用 `/mnt/e` 充当Linux测试文件系统。最终增量只覆盖Program/PublicProcessTests并新增ResourceScenario，其余归档内容未变。

| 检查 | 实际结果与边界 |
| --- | --- |
| Windows Release build | 0 error；41项既有Data/Rbf XML warning，未作范围外清理 |
| Windows新增资源定向 | 2/2通过，6个正常子进程；同样2项在完整回归中再次执行，不重复计入独立用例数 |
| Windows完整solution | 3431通过、0失败、9项Linux专属跳过；VS1168通过/6跳过 |
| Linux固定基线Release build | 0 error、41项既有XML warning；7个旧栈项目精确RBF1版本/来源校验通过 |
| Linux固定基线ext4完成组 | 8组完整TRX：3262通过、0失败、2项Windows专属跳过；VS1171通过/1跳过，9项Linux专属全部实际通过 |
| Linux冻结EventJournal补回归 | 同一build，独立 `/dev/shm` tmpfs TMPDIR，174通过；其2个Windows guard早返不算Linux物理路径资格 |
| Linux最终增量 | Release build 0 error/0 warning；SystemIntegration 21/21通过，77个child/10次强杀；未重复未改生产库的完整用例 |

首次Linux完整solution命令在8组完成后由主线程协调停止：旧EventJournal仍执行2500次CreateBranch/2499次ArchiveRef及4097次追加/推进，现场目录持续增长、线程等待ext4 journal commit，无测试失败。为避免冻结参考的大量fsync拖延新栈资格，保留8份TRX和中断现场，以原build在tmpfs完整重跑旧174项；没有截短循环、修改测试或改变fsync调用。停止触发的MSB4166与launcher实际exit0均原样保留，派生状态明确 **Interrupted**；缺少完整集合时，exit0不能证明整套通过。不能把分组覆盖写成“单次完整ext4 solution通过”，也不能据tmpfs宣称旧栈获得ext4耐久或性能资格。

V4-B的19个public场景在Linux基线中全部通过：71个child、10个真实SIGKILL（exit137）、61个正常退出，stderr为空。六次锁拒绝都为原生code11，三个在FrameStore层、三个在data已打开后的VersionStore层。每个verify经RW再两次RO；全部data恢复Action实际为None、文件长度不变，未取得物理修尾或内部写入停点证据。最终Windows21项包含这19项及新增2项资源检查，共77个child、10次强杀。

机器可读汇总和日志/TRX/逐进程记录的摘要见 [V4-C证据](evidence/2026-10-11-versionstore-v4c.json)。本轮生产库源码未改，新栈仍使用main ProjectReference，旧栈仍是精确RBF1包；没有打包、发布或业务迁移。S4/S5/S6保持Draft/source-only，下一片是应用策略最小示例，再做最终条款复核。

## 有限规模结果与解释

| refs/tags | 本地Snapshot | forks | 继承历史交付总数 | data帧 | 实际tag桶 | 最大单桶记录 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16 | 53 | 10 | 83 | 43 | 8 | 8 |
| 64 | 213 | 42 | 339 | 171 | 32 | 32 |
| 256 | 853 | 170 | 1363 | 683 | 122 | 128 |

每档有两个新读进程，循环内查询各重复3次，下表相应列为6个样本的中位数；两层RO Open与FrameStore.Audit则各有2个样本，保留在机器摘要。RW Open及连续CreateTag每档仅一个样本。16/64使用SystemIntegration夹具，256由独立父进程驱动；它们不是隔离了硬件、运行时和系统负载的性能比较实验。

单位ms；括号口径见上文。两平台/文件系统不同，不作速度竞赛。

| 平台 | refs | VS RW Open（单次） | 全历史（中位） | ScanTags（中位） | Audit（中位） | 全部CreateTag（单次） |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Windows/NTFS | 16 | 24.50 | 163.04 | 23.30 | 85.82 | 315.18 |
| Windows/NTFS | 64 | 48.73 | 501.71 | 77.56 | 279.92 | 1535.63 |
| Windows/NTFS | 256 | 207.15 | 1634.14 | 226.41 | 915.12 | 16282.19 |
| WSL2/ext4 | 16 | 2.46 | 15.47 | 1.27 | 4.38 | 1465.54 |
| WSL2/ext4 | 64 | 3.29 | 73.71 | 4.56 | 19.53 | 5610.65 |
| WSL2/ext4 | 256 | 6.10 | 418.35 | 17.46 | 77.16 | 23412.82 |

两平台三档的所有写/读进程都成功，两个冷只读进程前后的文件集合与SHA-256不变。Linux256写进程总时长132.90s，两次读进程分别2.36/2.41s；它使用独立300s超时，未加入日常60s子进程测试。

Linux六次读进程中，遍历owner的versions路径fd在打开后及三轮遍历后始终为1（锁）；独立ReadRef owner首次全读后分别为17/65/257，再读两轮不增长，两个owner阶段各自Dispose后均归零。Windows总handle有未归因的小幅变化：最终16档进程关闭全部owner后相对beforeOpen增加3或4，64/256增加5；256档ReadRef阶段新增256且保持稳定，关闭后相应下降。未观察到Linux临时文件句柄累积，不能据此替Windows总handle归因或宣称任意负载无泄漏。

六个读进程的checkpoint WorkingSet采样范围：Windows约28.1–48.3MiB，Linux约41.6–55.5MiB；这不是实际操作峰值。全历史批次的Windows当前线程累计分配中位数约1.67/8.40/61.32MB（十进制），包含应用验证和临时结果，与RSS/存活量不同。

当前源码支持两项成本解释，但本轮没有剖析各项耗时占比：

- 全ref历史批次逐个进入未缓存ref，`ResolveRef → TryRequireFile/TryQualify → RequireExactComponent` 会反复枚举refs目录，检查实际拼写与别名；临时句柄不缓存的策略不意味着全批次目录工作量为线性。这里不能把增长归因于扫描了fork点后的来源后缀；消费者明确验证继承前缀固定。
- 每次named tag请求先资格化全部已建桶路径，再扫描目标桶meta。连续hot桶创建还需对不断增长的记录集合查重；此向量同时增长普通桶数。测得的批次还包含ReadRef捕获、data barrier与fsync，不能只用桶内记录数解释总耗时。

这些是后续按实际业务负载评估优化的依据，不作为功能缺陷、不据此新增性能门限或持久索引。Windows总handle变化尚未归因，Linux路径fd证据只能证明其本平台的有限向量；本轮不宣称任意负载永不泄漏。ForkRef创建独立成本、精确资源峰值和应用工作负载/SLO仍未测量。
