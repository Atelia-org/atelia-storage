# RBF 尾帧重构方案：辩证复核记录

日期：2026-10-01。目标：[建议重构方案](rbf-tail-recovery-refactoring.md)。使用 `dialectical-simplification`，三位独立 reviewer 分别承担 Demand skeptic、Minimal architect、Semantic defender；主线程独立核对源码并裁决。完成两轮，第 2 轮实质争点已收敛，不再为一致措辞增加第 3 轮。所有 reviewer 仅审查，文件由主线程统一修订。

## 1. 需求台账与证据来源

| 来源 | 已确定要求或事实 |
| --- | --- |
| 用户最初六条规则 | 默认单残尾自愈；墓碑优先；完整 FrameBytes 缺 Fence 保留；精简动作报告；日常逻辑 frame 原子性；灾难救援单列 |
| 用户指出现有工具 | `RollingCrc.Scanner` 已有流式单 pass 逆向字节扫描，不能假定缺少性能工具；用户未要求在线强制重复扫描 |
| 用户本轮明确边界 | 无兼容性包袱，只有兄弟项目少量试用；可以 API breaking，尽可能 wire-compatible |
| 用户复核后明确分类 | 最后一帧物理长度完整但 CRC 错误：停止自动恢复，交离线工具；这是 CRC 校验失败的数据损坏，不属于残缺 |
| 当前代码/测试 | 单 writer、opaque payload、Trailer 墓碑位与 CRC 布局、Builder reservation 阻止未提交输出、Scan 默认隐藏墓碑、现有离线 candidate/Audit/hooks |
| 真实消费者 | SegmentStore active 与历史 ConfirmDurable、EventJournal event/ref/control 日志、Toolkit；当前 strict 合同是现状，不是不可变兼容义务 |
| 作者原提案而非用户法律 | append-only、固定 tag0 不兼容就截整帧、每次随机读验 Fence、公开 Auto/Strict、报告常驻 IRbfFile、三阶段兼容接线、一律 body/Fence 双 flush |

证据优先级为当前用户决定 → 当前源码/测试/真实调用 → 具体故障反例 → 衍生文档 → 假想未来需求。目标与 adjacent 文档同源的断言不互相充当独立证据。当前 working tree 的未提交 Audit/inspector 等按实际文件核对，不覆盖或回滚。

## 2. 交叉质询后裁决

| 位置/主张 | 当前消费者与复杂度 | 最小机制及删除反例 | 裁决/置信度 |
| --- | --- | --- | --- |
| 原 §4.1：EOF 候选 + Rolling 候选 + Header 成员检查 | 所有普通打开；重复读取、候选窗口/过滤/回退与成员授权两套控制 | Header 连续 metadata 已唯一确定尾部；删额外搜索不损语义。删成员证据则误认内嵌帧 | merge/delete；高 |
| 原 §4.1：可信 anchor 新 API | control snapshot 现有 boundary，但当前公开 record/CRC 不自认证身份和成员来源；新增信任契约 | 无可信起点首片从 Header；真实有界打开预算要求出现时再定可信起点，不加 sidecar | defer；高 |
| 原 §5：append-only 不兼容就截 B | 无业务消费者要求保全最多19B未完校验/元信息；部分普通 Trailer 无法直接加墓碑位 | 资格后 trim 到 C、flush、补固定墓碑；保留 HeadLen/L/coverage。直接原地改 descriptor/CRC 有混合校验崩溃反例 | simplify；前缀模型内高，实际I/O待测 |
| CRC 反求 tag 保全 Trailer CRC | 可以救部分 CRC 已写而 descriptor 未写的镜像；增加反算算法和资格分支 | trim 普遍覆盖合法 E<F，不需反求；普通墓碑位已写为0时反算tag也不能只追加转换 | defer；高 |
| 原 §5.3：一律 body→flush→Fence→flush | 恢复器；多一个屏障和断点类别，正常 Append 也没有该保证 | 前缀模型纯追加只需最终 flush；trim→flush 仍保留。乱序设备镜像不属于该模型，不能用它自证额外屏障必需 | simplify；高 |
| 原 §6：Mode、接口常驻报告、公开失败分类 | 真实要求是动作报告与历史不修复；旧参数/模式/API实现负担无兼容依据 | 默认可写自动恢复，Open out 报告；只读验证；内部历史 handle 无 mutation；既有异常附 detail | simplify；高 |
| 原 §7.2：每次随机读新 Fence 检查 | 已恢复打开与 fault guard 封闭正常残尾；离线读缺 Fence FrameBytes 有真实用途 | 保持 FrameBytes 读取，Fence 在打开/Scan/boundary 校验。未找到 accepted 模型内新增检查保护的半帧反例 | defer；高 |
| 现 `IRbfFile.Truncate` | 仓内调用仅测试；允许删 Header/旧帧或扩洞，增加实例/缓存状态 | 删普通公开面；在线协调器内部 SetLength(B/C)，离线显式截断各自负责；fixture直接制损坏文件 | delete；高 |
| fault 仅加 facade guard | FrameInfo、缓存命中、零meta、旧枚举器可绕过；真实 partial write 使 TailOffset 未推进 | 一份共享 fault 事实，所有新操作 guard；已物化数据不撤销。删它会允许覆盖残尾并留下旧长度残留 | keep/simplify；高 |
| 原 §9：三阶段先 Strict 再 Auto | 无兼容负担；真实历史/业务引用边界不能省 | 一次必要调用审计和纵切；历史只flush、业务跳合法墓碑但不越损坏，引用仍拒绝墓碑/缺失 | delete/keep；高 |

变化按可观察项计：在线定位从三条路径收敛成一条；删除公开 Mode、IRbfFile 常驻报告属性、普通 Truncate 和拟新增公开失败分类；三阶段兼容迁移删除；不再新增普通随机读 Fence 分流。保留一个恢复协调器、四种动作、一份 fault 事实、已有 wire format 与离线 scanner。方案正文从 237 行修订为 190 行；行数不是正确性依据。

## 3. 决定性反例与明确撤回

**内嵌帧恰落 EOF。** 外层合法 HeadLen 后的 payload 包含 `[Fence][内层完整Frame][Fence]`，在内层 Fence 后切断。内层局部 framing/全 CRC 正确，外层仍未完成。这不是恶意改文件，而是合法 opaque payload 的 writer 前缀。[当前测试](../../tests/Rbf.Tests/Internal/RbfPrefixInspectorTests.cs)已有内嵌帧基础案例，但现例是完整外层 CRC 被破坏；精确 EOF cut 必须新增正式验收。三位 reviewer 保留成员证据，Semantic defender 撤回本轮必须新增可信 anchor API 的建议。

**固定 tag0 不兼容并不证明不存在墓碑。** Semantic defender 的独立 CRC 计算：L=32、普通 descriptor=0、tag=0x12345678，Trailer CRC=0x12549409；墓碑 descriptor=0x80000000、tag=0 时 CRC=0x93D75BDC，但 tag=0x70A1B585 时 CRC 又等于0x12549409。现 tag 全值域自由，某些半写状态可以反求 tag 保留已有 CRC。由此撤回旧提案“不兼容固定编码即无法墓碑补齐”的泛化。不过完整普通 descriptor 的墓碑位已经写为0时，tag反算仍不能只追加转换；统一 trim-to-C 更小。

**trim-to-C 闭合。** C=B+L-20，资格限定 E<F。先删除 C..E 的最多19B校验/Trailer，flush 后再写固定墓碑后缀：原残尾、EOF=C、墓碑CRC/Trailer前缀、完整墓碑缺Fence、闭合墓碑都可按同表解释。不能对完整普通 FrameBytes 或已知矛盾做 trim。Demand skeptic 与 Semantic defender 均明确撤回 append-only keep；主线程接受该更小协议，没有加入新的 marker/WAL。

**真实下游冲突。** [SegmentStore](../../src/RbfSegmentStore/RbfSegmentStore.cs) `ConfirmDurable` 历史路径当前调用可写 `OpenExisting`，全局默认变化会误修冻结历史。[EventJournal](../../src/EventJournal/EventJournal.cs) `ComputeNextSequenceNumber`、[RefMoveStore](../../src/EventJournal/RefMoveStore.cs) `ReadEndpoint`、[control replay](../../src/EventJournal/EventJournal.Refs.cs) `ReplayRefOpLog` 当前拒绝墓碑或要求普通业务帧贴 EOF。control replay 的 end/预算依赖物理遍历，不能单改 showTombstone=false。主线程依据这些调用删除兼容波次，保留具体接入义务。

**共享 fault。** `RbfFrameInfo` 直接持 reader；零 TailMeta 早退、普通 `ValidateTicket` 的固定 EOF 空值早退和已取得枚举器不经过 File facade。只增加 File guard 不满足“故障后新操作拒绝”。不追溯撤销已返回 span，只连接既有读取入口到一份不可逆 fault 事实。

## 4. 主线程独立 probe 与证据限制

执行无文件写入的 Python 内存镜像计算，按源码 CRC32C/逆序 Trailer 算法编码；标准向量 `123456789 → 0xE3069283` 通过。payload长度0/1/7/12/31/64；282个原始断点、8,816个恢复补写断点、114个suffix trim，枚举镜像均能归于部分HeadLen、合法残帧、完整缺Fence或闭合帧；另验证内嵌 EOF 局部闭合与外层残帧同时成立。

probe 检查的是模型编码/分类，不调用拟实现的生产恢复器，不操作真实 SetLength，不代表每种 OS 文件长度失败或断电镜像。三位 reviewer 的独立轨迹核对与这份计算只支持前缀模型闭合；实施必须补真实 handle 的 SetLength/flush/hooks、资源释放、包消费和选定环境故障证据。

## 5. 产品边界与实施范围

复核后用户已明确裁决：最后一帧物理长度完整但 CRC 校验失败属于数据损坏，不属于残缺；停止自动恢复并交离线工具。撤回此前“待选择/建议保守拒绝”的措辞，固定为分类规则；不能因同时缺 Fence 而补 Fence、转墓碑或截断。普通只读/冻结历史不隐藏获取写权限；writer可修打开是日常自动恢复入口。

保持 L、允许撤回最多19B未完校验/Trailer 是本次建议的协议选择，不宣称它们是用户原先要求。需要逐字节取证保全、任意断电自动继续或真正 O(1) 打开时，应重新明确产品契约和证据，不在首片预建框架。

本次只修订建议方案与本记录；未修改运行时、未运行 dotnet build/test、未提交、打包或发布。实际库行为仍以当前源码/测试为准。
