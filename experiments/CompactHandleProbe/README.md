# Compact Handle 数学见证

从仓库根目录运行（Python 3，无第三方依赖）：

```powershell
python experiments/CompactHandleProbe/probe.py --output artifacts/compact-handles/math/probe.json
```

穷举 8bit 全部 key、输入及不同 key 对，比较 XOR、模加法和保留零的 XOR 的双射、逆映射与合法集合重叠分布；合法编号为 1..15。保留零的构造通过交换完整置换表的两个输出槽生成，逆映射由表反转独立求出。32bit 参考向量另外采用正反顺序的函数复合验证，并采样检查“合法 24bit 输入、key 高 8bit 不同，则错 key 译码越界”。JSON 输出确定可复算，断言失败以非零退出。

这是数学模型见证，不加载或验收生产程序集；32bit 采样不冒充全域穷举。不同 key 对的失败具有相关性，不能将汇总检出率理解为每次调用独立概率。
