# ConditionalUpdate 研究探针

对应 [ConditionalUpdate 跨 ref 事务设计稿](../../docs/FrameStore-VersionStore/07-versionstore-cross-ref-conditional-update.md)。只使用生产 Rbf 的 public API，通过合成 payload 验证共同成员表、提前 ticket、残尾恢复和未完成位置复用；不是 VersionStore 实现或最终持久格式，不注册 solution 或生产 pack。

在仓根串行运行，输出目录必须尚不存在；真实文件放 W:，不自动删除镜像：

```powershell
dotnet build experiments/VersionStoreConditionalUpdateProbe/Probe.csproj -c Release
dotnet run --project experiments/VersionStoreConditionalUpdateProbe/Probe.csproj -c Release --no-build -- W:/conditional-update-probe-new-run
```

每次留下全部原始完整/截短 RBF3 镜像与 result.json。Fixture 使用 uint RefId、Packed64 ticket、int root；root 后可带额外 bytes，用于制造不同 stored 长度。它不冻结生产 RefId 宽度、RootMap/header/身份门或 codec。验证同时保留真实逆向主链定位基线与 exact ticket 完整读/失败消歧路径；两条路径均完整验证实际记录内容，I/O/资源/lifecycle 异常不被捕获为未提交。

覆盖：

- k=2/3/4 全部完成顺序及帧级输出前缀，150 例，验证只全旧/全新并在完整前缀上符合预期。
- k=2 两种顺序、每个阶段下一帧的全部 byte 前缀，260 例；先尝试只读打开，再可写恢复，检查组只全旧/全新。
- 未完成 peer 取消后，同/异长普通 RootMap 与新 CU 复用其起点；旧表不复活。异长旧 ticket 的直接读取错误单独记录。
- 完整组后的普通更新仍保留旧组资格；旧表复制到新的自身位置被拒；实际 peer 内容 CRC 错误显式传播。
- k=3 各成员不同帧长的排列及前缀，额外 24 例；上述镜像共 1,156 次当前值比较，保守/精确结果一致。
- 成立旧组的其他两个 peer 合计追加 100 条普通记录，精确路径仍成功且零 fallback；这只验证分支，不是性能测量。
- 更短/同长/更长 peer 起点复用，fresh A/B 或新 B/C，六例使用“先预测/预编码、逐文件 Begin/End”，旧组均不复活。
- lazy CompletedTail：只先打开并普通覆盖 A，之后只读 B 拒绝，可写 B 恢复原 body，观察仍符合协议解释。
- 同 ticket 坏 CRC、异长实际替代坏 CRC、替代 CU 错 self/重复表，均显式报错；disposed peer 在 EOF 的缺席快路也必须拒绝。

2026-10-08 的 [初录结果](evidence/2026-10-08-result.json)及 [完善结果](evidence/2026-10-08-refinement-result.json)分开保留；初录没有 exact 路径，使用当时源码，不能用当前新增覆盖重标。运行时 Rbf 来源 `f33c7bdc5a366b113db85613606e07c1beca8550`，候选/探针当时未提交；SDK 10.0.201，Windows，W:。记录中的 work 是原实跑镜像位置，不是复跑应当复用的输出参数。两个版本均先在独立 console 项目研究，再保存到仓内并以相对 ProjectReference 的 Release build/run 验证。完善版 `Program.cs` SHA256 为 `C54DC2C4BB78EE87846542D48907A0CFAA1669C9E64220DFCD36D77867AF9770`，仓内最终运行位置为 `W:/storage-cu-refinement-20261008/run-repository`。

限定：这是闭合后的完整镜像切取顺序输出前缀，不是实际终止写进程或断电。没有 data FrameStore barrier、真实 VersionStore/header/身份、输出/flush failure injection、资源异常、完整历史 API、Linux 或 PackageReference 资格。文件写入、结构资格、事务资格、flush 证据和生产可用性是不同事项。
