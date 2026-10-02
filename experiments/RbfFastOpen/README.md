# RBF 快开方案实验

这些实验支撑 [重构方案](../../docs/Rbf/rbf-open-fast-path-refactoring.md)。当前候选是单份尾 Key（固定开销28B，旧24B），Open 只负责结构，PayloadCRC 留给完整 ReadFrame。真实边界确认后，body 未到完整 Trailer 末端时截掉；完整 body 只补原 Key/Fence 的确定 1–8B 后缀。约 256MiB 最大帧保持，故障目标仅进程终止。

需要 Python 3 标准库与仓库固定 .NET SDK（当前10.0.201）。从仓库根运行：

```text
python -B experiments/RbfFastOpen/run_probes.py --assert-baseline-5711c47
```

runner 串行执行三个字节模型、真实 child kill、RBF1 黄金 fixture 导出、生产探针 Release build 和匹配 --no-build run，再用 Python 解码生产输出。生产探针直接 ProjectReference 当前 src/Rbf/Rbf.csproj，不使用旧 DLL；该闭包不等于 solution。

默认写新 OS 临时目录，返回目录与 summary.json。可指定 --output-directory <新目录>，必须为空；失败 log 也保留。进程/生产探针各自另留实际文件。--assert-baseline-5711c47 固定旧生产 Open requests/bytes，不把旧当前实现与新候选混为一致。

| 文件 | 当前证据 | 边界 |
| --- | --- | --- |
| probe.py | 尾 Key 编码、旧平行假链、全部 writer prefixes、CRC/footer 纳入 Key 选择、闭合墓碑；固定结构 tail 读取与完整内容验证分开 | 纯模型，不调用生产 RBF2 |
| compat_probe.py | 旧 ticket/user bytes/四黄金 fixture、Header 分派；旧完整结构主链不校验 PayloadCRC | strict_read 是完整镜像裁判，检查全部 payload；新旧两 profile 都将内容 CRC 留完整读 |
| qualification_probe.py | 最近真 marker/前驱结构、唯一 Key 重建、截尾/补尾/read-only/再次恢复；内容坏接受结构动作、完整读拒绝；固定结构请求计量 | 不能从 EOF 猜 footer 授予成员身份；无 writer oracle、无未知 Trailer 求解 |
| process_termination_probe.py | 实际 append/truncate/Key+Fence 后缀的 child kill、文件重读和幂等复开 | Python 模型决定动作、Python I/O；READY 握手点，不是生产 RBF2 或任意 syscall 内终止 |
| ProductionProbe | RBF1 Trailer 黄金向量、Python→生产 reader/writer→Python；旧 Open 指标、历史 Fence 读/boundary 职责 | 当前源码仍有旧末帧 PayloadCRC eager；不是候选结构 Open 的生产验收 |
| run_probes.py | 串行编排、七源码 hash、wire hash、布局/Open/恢复/fault 合同汇总 | 不测新 codec 延迟、设备 I/O、solution 或包消费 |

健康结构模型包含 Header4+encoded Trailer/Key/Fence24+左 Fence/HeadLen8+padding≤3，逻辑请求最多39B。逆序元信息仍需尾 Key：info解析20B；普通逆扫包含Fence验证24B。近最大帧 fixture 使用逻辑稀疏 reader 验证固定请求，**不是完整 payload CRC 黄金帧，也不是物理大文件或生产 I/O**。异常 marker 搜索仍可能读 M+7 bytes，不因 CRC 延迟而免除。

独立运行：

```text
python -B experiments/RbfFastOpen/probe.py
python -B experiments/RbfFastOpen/compat_probe.py
python -B experiments/RbfFastOpen/qualification_probe.py
python -B experiments/RbfFastOpen/process_termination_probe.py
```

child 将选定 append prefix、SetLength 或确定闭合 suffix 操作 file.flush() 到 OS，输出 READY 并等待；parent 只 kill 自己创建的 Popen，再从实际 bytes 决定动作。缺 Key/Fence 的每个追加 prefix 都复验，最终动作也在新 child 中实际完成并 kill 后再开为 None。期望完整帧只供测试裁判，资格算法不接收完整 writer image。未调用 fsync 或声称稳定介质/断电证据；握手超时默认10s，可设 --timeout-seconds，范围(0,60]，文件保留。

compat_probe.py --export-fixtures <目录> 导出 manifest；生产 --verify-fixtures <目录> 只读输入。推荐 runner 保证匹配构建与两方向互证。PayloadCRC forward/LE、TrailerCRC backward/BE 以生产固定向量、错误方向负例和完整 wire 验证。

本轮结果保存在 [tail-key-structural-open-5711c47-v2.json](results/tail-key-structural-open-5711c47-v2.json)：5226 cuts、4926 截尾、240 次补尾（含120个缺Key阶段）、11232 再次恢复状态；20 类结构坏输入拒绝（含 CRC 自洽的非法 descriptor）、22 个内容 CRC 正交场景；另外补验 Key301 非零 padding 的1241个前缀和高位 Key 的 padding1/2/3结构向量；181 次真实 child kill，四个生产 RBF1 黄金 fixture 互证。健康结构请求 36–39B，虚拟最大帧 39B。新脚本运行结果及七个源码 hash 与该不可变快照核对。

历史 [baseline-5711c47.json](results/baseline-5711c47.json)（墓碑补写）与 [process-termination-truncate-5711c47.json](results/process-termination-truncate-5711c47.json)（双 Key、完整末帧 CRC、不补 Key）均原样保留，不再是当前恢复门禁，不与新源码 hash 比对。同政策首份 [tail-key-structural-open-5711c47.json](results/tail-key-structural-open-5711c47.json) 保留初版覆盖；当前 v2 增加非零 encoded padding 相位与 CRC 自洽非法 descriptor 负例，因此只将 v2 hash 与当前源码比较。原 --full-collision 已退出实验。

writer/reader 编码成本另见 [RbfCodecCost](../RbfCodecCost/README.md)及[专项文档](../../docs/Rbf/rbf-codec-implementation-study.md)：已有 C# 原型、W: 实测和真实最大帧，不改本目录恢复模型或历史快照。

其他仓内项目的旧工厂调用编译缺口不在本实验中修复，不宣称 solution 通过。生产 RBF2 codec、SetLength/write/flush 异常、资源/cache/Builder 生命周期和生产进程 kill 均留实施验收。
