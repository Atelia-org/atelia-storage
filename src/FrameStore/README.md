# Atelia.FrameStore

FrameStore 的首个源码切片：按 [S2 核心设计](../../docs/FrameStore-VersionStore/02-framestore-core.md)建立格式和 owner 运行内核，供后续目录工厂接入。项目参加 solution 构建和测试，暂不打包；VersionStore 尚未创建。

当前可用的局部包括不透明 `FrameAddress` 的唯一 12B codec、内部格式/路径/config codec、首物理 header 的完整校验，以及 `FrameBuilder` / `FramePayloadWriter` / `FrameRead` 的所有权模型。内部运行核心负责 active 文件选择、一次性租借、完成登记、耐久确认和共享故障。

**当前没有可供应用打开 store 的公开工厂。** 测试经内部边界移交已初始化的真实 RBF 文件；这证明局部状态机和 RBF 组合行为，不证明完整目录准入、恢复或进程中断协议。未实施的文件系统层不能被临时目录测试后端替代。不要增加绕过根/锁/header 资格的公开句柄注册入口。

实现范围、证据与后续接入约束见 [首个源码切片记录](../../docs/FrameStore-VersionStore/02-framestore-core-implementation.md)。S2 仍为 Draft，完整设计合同仍以 S2 为准。
