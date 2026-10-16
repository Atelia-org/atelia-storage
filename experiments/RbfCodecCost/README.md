# RBF 历史编码成本证据

运行代码已从 main 移除：这些实验比较 RBF1 生产 API 与历史 byte-length RBF2 原型，Data 专项也使用该历史 wire fixture，不能直接运行来验收当前 RBF3。

完整代码、运行说明及 writer/reader 审查笔记保存在 [固定历史 revision](https://github.com/Atelia-org/atelia-storage/tree/043f77aa68700568499ceb965c049f8362e4253f/experiments/RbfCodecCost)。需要复现实验时使用该 revision 的独立 checkout。

[results](results/) 的不可变测量快照继续保留，为 [编码成本研究](../../docs/Rbf/rbf-codec-implementation-study.md) 和 RBF3 设计决策提供历史证据。其格式、源码 hash、环境及验收边界沿用原始身份，不作为当前主线的运行资格。

当前生产实验入口见 [RBF3 production probe](../RbfFastOpen/Rbf3ProductionProbe/README.md)。
