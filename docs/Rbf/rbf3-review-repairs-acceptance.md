---
title: "RBF3 审阅修复验收"
status: "Accepted for the Data/RBF source and test scope"
normative: false
---

# RBF3 审阅修复验收

后续pooled读取C3与8KiB打包输出已完成，见[独立后续验收](rbf3-resource-and-output-acceptance.md)；本页的c4377fd测试、binary与历史资格保持原身份。

2026-10-03，R1/R2/R4、Data C2/I1、R3/I2 及 Commit/Auto-Abort 合同收尾已完成，RBF1 最大容量已补公开结构读取正例。最终 clean 源码 `c4377fd84e2a47e349e60badc9e4ad3a717f9d96` 的 Release RBF build 成功，RBF 测试 **677/677**；Data 对应已提交修复内容的 Release build 成功，测试 **288/288**。两组均无失败或跳过，Data 的提交前构建身份如下单独说明。

裁决与仍延期的触发条件见[审阅 finding 裁决](rbf3-review-findings-disposition.md)。本记录只验收 Data/RBF 源码和测试闭包，不新增公共 API、File 状态或 wire-format；下游适配另阶段。

## 完成行为与提交

主线程依源码、diff 和实际测试逐包验收，语义防守 reviewer 对各包只读独立复核均无阻塞。

| 提交 | 完成行为 | 对应源码或测试 |
| --- | --- | --- |
| `e6ad4d2` | C2：Sink/Chunked 的 Reset 与回收四处，在 Return 前消耗该租赁的归还资格。I1：Sink 的两个 foreach 直接使用具体 queue，删除私有转发方法 | [SinkReservableWriter](../../src/Data/SinkReservableWriter.cs)、[ChunkedReservableWriter](../../src/Data/ChunkedReservableWriter.cs)、[租赁回归测试](../../tests/Data.Tests/ReservableWriterPoolReturnTests.cs) |
| `06ad4b8` | R1：Reset/HeadLen reservation 成功后发布 Builder epoch/Building；内部可选 builderPool 与 Append scratch pool 分开。补首次 Rent 失败、取消归还异常和输出后回收异常的生命周期检查 | [RbfFileImpl](../../src/Rbf/Internal/RbfFileImpl.cs)、[Rbf3WriterFaultTests](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs) |
| `8ee4d77` | R2：default FrameInfo 的四个读入口统一抛初始化 InvalidOperationException；guard 先于空 meta 早退、租赁和 I/O | [RbfFrameInfo](../../src/Rbf/RbfFrameInfo.cs)、[Rbf3ReaderTests](../../tests/Rbf.Tests/Internal/Rbf3ReaderTests.cs) |
| `1405408` | R4：RBF3 的 meta 超限、合计容量超限补 RecoveryHint；保留参数拒绝无输出、不推进 tail | [RbfAppendImpl](../../src/Rbf/Internal/RbfAppendImpl.cs)、[Rbf3WriterTests](../../tests/Rbf.Tests/Internal/Rbf3WriterTests.cs) |
| `1bc0d97` | 最大 RBF1 稀疏帧经 public OpenReadOnlyExisting/ReadFrameInfo，保留原 byte ticket 及268435428B payload容量 | [RbfFileFactoryTests](../../tests/Rbf.Tests/RbfFileFactoryTests.cs)、[稀疏 fixture](../../tests/Rbf.Tests/Internal/SparseRbfTestFile.cs) |
| `c4377fd` | R3/I2：统一共享操作的串行合同、旧读容量与新写容量公式；明确最终 Commit 保守 fault 及 Auto-Abort 资源归还异常的边界 | [接口合同](rbf-interface.md)、[类型指南](rbf-type-bone.md)、[cache 说明](rbf-read-cache.md)、[IRbfFile](../../src/Rbf/IRbfFile.cs)、[RbfFrameBuilder](../../src/Rbf/RbfFrameBuilder.cs) |

两个 Data writer 的每次 chunk 租赁至多一次 Return 尝试不等于保证回池；同一数组的新租赁仍可归还。资源故障后只验收安全收尾，不承诺失败 writer 可复用。最终 Commit 完整输出后 Return 抛异常时，tail 仍未发布，实例及旧 FrameInfo/扫描器保持共享永久 fault；释放不解除它。

## 验证结果与身份

机器可读[本次证据快照](review-evidence/rbf3-review-repairs-20261003.json)记录 SDK10.0.201、runtime .NET10.0.5、Release、源码身份、135份源码/配置/测试 SHA256、15份日志/TRX SHA256 和6份 binary SHA256/ProductVersion。主线程已逐一核对这些哈希全部吻合，冻结提交后 src/tests 没有变化。

原始材料位于 `W:/RbfFastOpen/rbf3-review-repairs-20261003`，TEMP/TMP 使用其 `temp` 子目录；build/test 由主线程串行执行，每组先 Release build，再以匹配配置 `--no-build` test。

| 实际验证 | 结果 | 原始材料，相对于上述目录 |
| --- | --- | --- |
| Data Release build | 0 errors、21个既有 XML warnings | `logs/data-build.log` |
| Data 全测试 | 288/288，新增6个case，无跳过 | `logs/data-test.log`、`test-results/data.trx` |
| R1 生命周期定向测试 | 20/20，无跳过 | `logs/rbf-r1-test.log`、`test-results/rbf-r1.trx` |
| R2/R4 读取及写入定向测试 | 51/51，无跳过 | `logs/rbf-r2-r4-test.log`、`test-results/rbf-r2-r4.trx` |
| RBF1 最大旧容量定向测试 | 1/1，无跳过 | `logs/rbf-legacy-test.log`、`test-results/rbf-legacy.trx` |
| 最终 clean c4377fd RBF Release build | 0 errors、41个既有 XML warnings | `logs/rbf-final-build.log` |
| 最终 RBF 全测试 | 677/677，相比原670项新增7个case，无跳过 | `logs/rbf-final-test.log`、`test-results/rbf-final.trx` |

Data 288项执行的是提交 `e6ad4d2bb637388fee6bae83c28f91d8c92278c4` 前、与该提交内容一致的工作树构建；其 Data.Tests DLL 及复制的 Data DLL ProductVersion 为 `1.0.0+92e05fc76a4a05e637e8e24d6e6d35236aa14a76`。最终冻结树的41份 Data 源码/测试文件 blob 仍与 e6ad4d2 相同，证据 JSON 明确记录该关系。**这不是在 clean c4377fd 上重新构建并执行的 Data.Tests。**

最终 RBF 全测试则来自 clean c4377fd 的重新构建；Rbf.Tests 与三个生产 DLL（Data/Rbf/Primitives）的 ProductVersion 均为 `1.0.0+c4377fd84e2a47e349e60badc9e4ad3a717f9d96`。当前生产 Data DLL 与早期 Data.Tests 目录的复制 DLL 分别记录，不能按 DLL 名称混用其身份。

## 关键回归及资格边界

- [Data 租赁测试](../../tests/Data.Tests/ReservableWriterPoolReturnTests.cs)对两个 writer 分别验证 Reset/Commit 的 Return 接受后抛异常，随后 Dispose 不再次归还同一租赁；另验证同一数组重新租借后可归还新的租赁。6个case使用 custom pool 确定性模拟，没有制造 Shared pool 真实 OOM。
- [RBF 生命周期测试](../../tests/Rbf.Tests/Internal/Rbf3WriterFaultTests.cs)验证初始化 Rent 失败保留原异常、无读写/flush/SetLength、tail和文件不变，既有读和普通 Append 可用，新 Builder 成功且旧副本不能影响它。取消 Return 异常后只验收 Dispose 关闭 handle且不重复归还；不以此证明失败后能健康续写。
- 同一生命周期测试的 `FinalCommitReturnFailure_AfterCompleteOutputPermanentlyFaultsSharedReader` 在真实文件中观察到完整 frame/Fence 输出后再注入 Return 异常，验证 tail未推进、共享 fault不被取消解除。它支撑保留 Commit 外层 catch，不证明普遍 OOM 恢复能力。
- [default-info 测试](../../tests/Rbf.Tests/Internal/Rbf3ReaderTests.cs)覆盖四入口的统一初始化异常；现有读取测试继续覆盖合法空meta、fault/Dispose、资格复用与每次 PayloadCRC。[RecoveryHint 测试](../../tests/Rbf.Tests/Internal/Rbf3WriterTests.cs)覆盖超长meta拒绝的错误码、非空提示和无输出/tail不变；合计容量分支的第二条提示由源码/diff核对，没有另分配256MiB输入只为测试提示文字。
- [最大旧容量测试](../../tests/Rbf.Tests/RbfFileFactoryTests.cs)是正确 Header/HeadLen/TrailerCRC/Fence 的闭合稀疏结构正例，public Open/info 得到原 `SizedPtr(4,268435452)` 与268435428B payload长度。payload及PayloadCRC保留稀疏零值，未调用完整 ReadFrame，不能外推内容 CRC 或全文兼容验收。

C1 入队前租赁窗口、精确 pre-Push 失效细分、MaxOffset × Open/恢复矩阵、4KiB 输出次数及完整 Builder 成本优化仍按[裁决触发条件](rbf3-review-findings-disposition.md#保留边界与延期触发)延期。公开工厂仍固定 Shared，同一 File 的共享操作仍由调用方串行；不新增线程安全能力或公共 pool配置。

本次未重跑35点进程终止实验或完整性能探针，未进行下游适配/build/test、整仓 solution 验证、PackageReference smoke、pack/publish。0999851的[正式生产快照](../../experiments/RbfFastOpen/Rbf3ProductionProbe/results/rbf3-0999851-20261003.json)、[原审阅及探针](rbf3-code-design-review-0999851.md)保持原身份；其终止/性能结果不是本次 c4377fd 的新测量。
