# RBF 快开实验与历史证据

当前 main 仅支持 RBF3，生产实验入口为 [Rbf3ProductionProbe](Rbf3ProductionProbe/README.md)。其中保留独立 wire 校验、恢复/进程终止、性能与输出阈值实验；RBF1 fixture 只作为拒绝打开且不修改文件的负例。

父目录 Python 模型及 `ProductionProbe` 属于历史 RBF1 / byte-length RBF2 研究，已从 main 移除。完整代码和复现说明保存在 [固定历史 revision](https://github.com/Atelia-org/atelia-storage/tree/043f77aa68700568499ceb965c049f8362e4253f/experiments/RbfFastOpen)，需要复现时使用该 revision 的独立 checkout。

[results](results/) 的不可变历史快照继续保留，供 [快开设计与实施记录](../../docs/Rbf/rbf-open-fast-path-refactoring.md) 追溯。这些快照的格式、恢复政策、源码 hash 与测试数字保持原始身份，不是当前 RBF3 实现的运行门禁。旧编码成本材料见 [RbfCodecCost](../RbfCodecCost/README.md)。
