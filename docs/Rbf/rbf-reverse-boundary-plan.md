# RBF：按已校验边界逆向扫描的窄计划

状态：**Draft / 已选定设计，公共入口尚未实施**。本篇记录拟新增的逆扫重载，不修改当前 [RBF 接口规范](rbf-interface.md)，不构成已有 API、探针或 Accepted 验收证据。

## 目标与拟新增入口

VersionStore 的继承历史需要从已选定的旧 source ticket 向前读取；当前 `ScanReverse(bool)` 只能从已打开文件的实际完成尾部开始，定位成本随 source 后续更新增长。本计划复用现有 `RbfScanBoundary` 和 boundary 校验，只增加一个公开重载：

```csharp
// 拟新增到 IRbfFile；当前接口与源码尚无此重载。
AteliaResult<RbfReverseSequence> ScanReverse(
    RbfScanBoundary boundary, bool showTombstone = false);
```

扫描从 `boundary.EndExclusive` 向前，包含 anchor 本身，再继续更早帧；anchor 为 tombstone 时按既有过滤规则处理，`showTombstone: true` 才保证交付它。`RbfScanBoundary.Empty` 合法，返回空序列，即使文件还包含后续帧。现有 EOF 逆扫不变。

## 一个共用的 boundary 校验

以下为拟实施合同，准入及错误类别沿用现有 `ScanForward(boundary, ...)`，不得把新重载理解为当前可调用能力：

1. 先执行相同的 Dispose、共享 write fault 与 Idle guard；Building 时在读取前拒绝，不完成或取消 Builder。
2. 要求 `EndExclusive` 为全局 4B 对齐，且 `4 <= EndExclusive <= TailOffset`。
3. 无 anchor 时只接受精确 `RbfScanBoundary.Empty`，不接受 default 或其他空 anchor 组合。
4. 有 anchor 时要求其公共物理后界等于 `EndExclusive`；复用现有完整帧读取与尾 Fence 检查，并比较 `AnchorContentCrc32C`。内容见证的定义直接消费当前接口规范，不引入第二种 hash 或编码。
5. 全部核验通过才返回从 `EndExclusive` 开始的 `RbfReverseSequence`；此准入不为成员证明扫描 prefix，也不解析 anchor 之后的 suffix。

Malformed 参数返回 `RbfArgumentError`；framing、完整 CRC 与内容见证不符返回相应 RBF 错误；生命周期/Building/fault guard 及 I/O 异常沿现有同类入口传播。核验不修改文件，不 flush 或恢复。后续枚举沿用现有 framing/TrailerCRC、tombstone 过滤、生命周期与 `TerminationError` 规则；完整 payload 读取仍按需执行，不能把结构扫描当成内容审计。

## 来源与资格边界

`RbfScanBoundary` 是 public 可构造数据，不是 trusted token，不携文件身份或主链成员来源证明。校验成功只说明指定 anchor 的局部完整帧、内容见证和尾 Fence 合格；不认证任意外部 ticket、复制镜像或整个 prefix。

VersionStore 在自己的已 checked 身份上下文中，依据已签发 `RefRevision`，或自身协议写入并从 checked header 读取的 `ForkOrigin`，结合已发布历史不删除/复用、单 driver 串行及无外部改写模型使用旧位置。目标自身的完整 CRC、kind/meta、容量与 root bytes 资格不能因已有位置值而省略；来源 byte clone 等 VS 语义仍由 VS 检查。

RBF3 的 marker-free 正常 writer 前缀保证全局对齐 Fence 只在真实边界出现，依据为 [RBF3 格式](rbf-format.md) `[F-RBF3-XOR-MARKER-FREE]` 的写出约束。它只支撑正常 writer 的 ProcessCrashOnly 模型，不认证任意位损坏、手工重新 seal、断电或设备乱序镜像；本计划不扩大这份证明。RBF1 可沿其现有 profile 结构规则支持该通用重载，但不新增 RBF1 的局部 anchor 成员承诺，也不改变其只读及全结构冷开资格。

本计划不新增持久见证、格式字段、索引、裸文件导入或绕过普通 factory 的能力。公开 factory 仍检查当前 EOF 并按自身合同恢复/拒绝；坏当前尾可能使冷开失败，不能宣称历史旧点因此必然可冷读。“不解析 suffix”只描述新重载自身，factory 成本与资格单列。

## 实现接点与成本

可提取复用 [RbfFileImpl](../../src/Rbf/Internal/RbfFileImpl.cs) 的 `ScanForward(boundary, ...)` 现有 boundary 检查；通过后构造 `new RbfReverseSequence(_reader, boundary.EndExclusive, showTombstone)`。只共享这一个校验流程，不增加 boundary 类型、扫描 lease 或通用信任框架。

现有 [GetScanBoundaryAfter](../../src/Rbf/Internal/RbfReadImpl.ScanBoundary.cs) 会完整读取 anchor 并检查尾 Fence；重载重新核验后，枚举器仍可能再次读取其结构，调用方取得 payload 时也可能再次完整读取。不得承诺一次读取或由暖缓存推定冷读成本。VersionStore 历史必须给 helper 内每次真实完整读取及枚举推进计工作步，先检查额度再执行；不能只给最外层 helper 计费而漏掉重复工作。

## 必要验收

| 向量 | 必须证明 |
| --- | --- |
| 旧 anchor 与很多后续帧 | 新重载的逻辑读取不解析 suffix；首项为 anchor，继续只向前；cache 邻页预读与 factory EOF 资格另计 |
| Empty / 末 anchor / tombstone | 非空文件上 Empty 也为空；末 anchor 与现有 EOF 逆扫顺序一致；过滤与 showTombstone 保持既有行为 |
| 非法边界 | default、错误空组合、未对齐、范围越界、anchor/end 不一致及内容见证不符均在交付序列前拒绝 |
| anchor 损坏 | 目标 HeadLen、CRC、结构或尾 Fence 坏均拒绝且不改 bytes；不越过坏 anchor 找更早好帧 |
| 无关 prefix 内容坏 | 好 anchor 可以准入；之后扫到坏 payload 的结构扫描与完整读取分别遵守既有 CRC 职责，不提前全审计 |
| 状态与资源 | Dispose、Building、共享 fault、I/O、pool 归还及异常退出沿既有规则；枚举错误不伪装正常 EOF |
| profile 与冷开 | RBF1 沿既定只读/profile 检查，不增加成员承诺；RBF3 正常 writer 嵌套 payload 向量；坏当前尾仍受 factory 拒绝/恢复约束 |
| VS 接线 | 已签发旧 revision 与持久来源分别使用其既定资格；真实重复完整读全部计历史预算，早停不访问更早初始或下一来源 |

完成公共入口、针对性源码测试及独立接线复核后，再同步 `rbf-interface.md` 的可用接口与资格说明。此前 VS 文档应显式引用本篇 planned 前置，不以此计划宣称生产或平台验收完成。
