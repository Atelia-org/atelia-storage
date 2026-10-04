# RBF3 下游适配 baseline 初步调查报告

状态：**初步现象调查报告**，只汇总与确认事实，不提出也不实施适配方案。调查基线为 `main @ e6ad4d2`（`e6ad4d2bb637388fee6bae83c28f91d8c92278c4`，工作树干净，与 `origin/main` 同步）。

调查日期：2026-10-03。平台：Linux。.NET 命令串行执行：`dotnet build Atelia.Storage.slnx -c Release`；测试使用 `dotnet test ... -c Release --no-build`。

## 1. 背景

当前 main 的 RBF 新建文件采用 **RBF3**（不兼容新格式：4B 长度单位、单尾 EscapeKey、marker-free XOR），RBF1 保留只读及原 ticket 兼容。根 README 已声明"RbfSegmentStore / EventJournal 的适配属于后续阶段"，即下游目录在该声明时点尚未随 RBF3 变更同步。

### 1.1 相关提交链（时间倒序）

| 提交 | 内容 |
| --- | --- |
| `0999851` | `feat(rbf): implement RBF3 framing and bounded tail recovery`——RBF3 核心实现（64 文件，+3988/−917），新增 `RbfWireCodec` / `RbfAppendImpl` / `RbfProfile` / `TailBlock`，重写 `RbfTailRecovery` / `RbfLayout` |
| `2f46408` / `dabfa21` / `92e05fc` | docs：RBF3 生产资格与成本、独立代码评审、修复计划 |
| `00329fd` | `feat(data): implement zero-first XOR escaping`——`Atelia.Data` 新增 `XorEscape`（`src/Data/Binary/XorEscape.cs`），RBF3 EscapeKey 机制的依赖底座 |
| `5711c47` | `feat(rbf): recover a single incomplete terminal frame on open`——**在 RBF3 之前已改变 `RbfFile.OpenExisting` 签名并删除 `IRbfFile.Truncate`** |
| `811e7b1` / `0e0df09` / `7667b9c` / `dc11774` 等 | docs/research：EscapeKey、word 长度、tiny-key 搜索调研 |

下游同步状态：`0999851` 与 `5711c47` 均未触碰 `src/RbfSegmentStore` 与 `src/EventJournal`。两个下游目录最后一次触碰停留在 2026-09-29（`RbfSegmentStore` 最后代码对齐为 `d812750`，`EventJournal` 最后触碰为 `5288bd5`），即仍对齐在 RBF3 落地之前的 RBF1 契约上。

### 1.2 与下游直接相关的 API / 格式变更要点

- **`RbfFile.OpenExisting` 签名变更**（`5711c47` 引入，HEAD 现状见 `src/Rbf/RbfFile.cs:53`）：可写打开由 `OpenExisting(string, RbfCacheMode)` 变为 `OpenExisting(string, out RbfTailRecoveryReport, RbfCacheMode = RbfCacheMode.Slots16)`。`OpenReadOnlyExisting` 签名未变。
- `IRbfFile.Truncate` 被删除（`5711c47`）；下游源码经搜索无调用点，非破坏点。
- `RbfFile.MaxPayloadAndMetaLength` 因 RBF3 固定开销 28B（RBF1 为 24B）整体缩小 4B（`src/Rbf/RbfFile.cs:16`）。
- `CreateNew` 只写 RBF3 Fence；可写 `OpenExisting` 走 RBF3 单尾恢复路径，**RBF1 可写打开被拒绝**（`docs/Rbf/rbf-interface.md:223`）。
- `RbfTailRecoveryAction` 新增 `CompletedTail = 4`；`CompletedFence` / `CompletedTombstone` 保留为 RBF1 历史值，普通打开不再产生（`src/Rbf/RbfTailRecoveryReport.cs:6-13`）。
- 离线恢复 API（`RbfRecovery` 等）对 RBF3 抛 `NotSupportedException`（`src/Rbf/RbfRecovery.cs:40-41`）。
- RBF3 恢复动作表：短前缀/body 未完成→`Truncated`；body 完整（`L-4≤R≤L+3`）→`CompletedTail`（最多补 8B 原 Key/Fence 后缀，不再合成 RBF1 式墓碑）（`docs/Rbf/rbf-interface.md:231-238`）。

## 2. 现象汇总

### 2.1 编译失败

Release build 实际输出（主工作树直接构建，`/tmp/rbf3-baseline-build.log` 留档）：

| 工程 | 结果 | 错误 |
| --- | --- | --- |
| `Primitives` / `Data` / `Rbf` | 编译通过 | 仅既有 XML 注释警告 |
| `RbfSegmentStore` | **失败** | `CS1620` × 2 |
| `EventJournal` | 被跳过（依赖 `RbfSegmentStore` 编译失败） | 无直接输出 |
| `EventJournal.Tests` / `EventJournal.Toolkit.Tests` | 被跳过 | 无直接输出 |
| `RbfSegmentStore.Tests` | 编译通过（其产物测试见 2.2） | — |
| `Rbf.Tests` / `Data.Tests` / `Primitives.Tests` | 编译通过 | — |

`RbfSegmentStore` 的两个编译错误均为可写打开旧两参调用：

1. `src/RbfSegmentStore/RbfSegmentStore.cs:141` —— `RbfFile.OpenExisting(path, Options.CacheMode)`（`ConfirmDurable` 的 historical segment 临时可写句柄）。
2. `src/RbfSegmentStore/RbfSegmentStore.cs:184` —— `OpenDiscovered` 三元分支中的 `RbfFile.OpenExisting(activePath, options.CacheMode)`。

两处错误均为 `CS1620: Argument 2 must be passed with the 'out' keyword`，直接原因即 1.2 所述 `out RbfTailRecoveryReport` 第二参数。

### 2.2 EventJournal 编译错误的暴露方法与结果

由于 EventJournal 在直接构建中被上游失败屏蔽，为确认其自身的编译状态，在 `/tmp` 下另建了同 commit 的 throwaway `git worktree`，**仅**将上述两处 `RbfSegmentStore` 调用临时改为 `OpenExisting(..., out _, <cacheMode>)` 后重新串行构建。该 worktree 仅为取证，不构成适配方案；调查结束后已 `git worktree remove --force` 清理，主工作树保持干净。

最小适配结果：

| 工程 | 结果 |
| --- | --- |
| `RbfSegmentStore` | 编译通过 |
| `EventJournal` | **仍失败**：`CS1620` × 1 |
| `RbfSegmentStore.Tests` | 编译通过 |
| `EventJournal.Tests` / `EventJournal.Toolkit.Tests` | 未产出测试 DLL |

EventJournal 的编译错误：

3. `src/EventJournal/EventJournal.Refs.cs:332` —— `RbfFile.OpenExisting(path, options.RefOpLogOptions.CacheMode)`（ref-op-log 可写打开），同样是缺少 `out RbfTailRecoveryReport` 第二参数。

**未发现** EventJournal 因 `Append` / `TailMeta` / `EscapeKey` 等 RBF3 相关签名变化产生的其他编译期错误；`RbfFrameBuilder.EndAppend` 的 public 签名未变（0999851 仅新增 internal 故障注入重载）。

### 2.3 单元测试结果矩阵

直接构建后对已编译测试工程逐个串行 `dotnet test -c Release --no-build`（整体 solution test 因编译失败不可用；worktree 中则适配后逐个执行）：

| 测试工程 | 结果 |
| --- | --- |
| `Primitives.Tests` | 75/75 通过 |
| `Rbf.Tests` | 670/670 通过 |
| `Data.Tests` | 288/288 通过 |
| `RbfSegmentStore.Tests` | 48 例：46 通过，**2 失败** |
| `EventJournal.Tests` | 未运行（上游 `EventJournal` 编译失败） |
| `EventJournal.Toolkit.Tests` | 未运行（同上） |

`RbfSegmentStore.Tests` 的 2 个失败用例（worktree 最小适配后仍失败，主工作树因编译失败无法运行）：

1. `RbfSegmentStoreTests.OpenActiveWriter_RotatesWhenThresholdReached`（`tests/RbfSegmentStore.Tests/RbfSegmentStoreTests.cs:165`）——`Assert.Equal()` 失败：期望 `32`，实际 `36`。触发点是 `lease.File.TailOffset` 与阈值轮转判断；RBF3 4B 长度单位使最小帧开销从 24B 变为 28B，测试对 TailOffset 的预期未随新格式更新。
2. `SegmentLocatorTests.NonemptyShortTailIsRejectedUnchanged(length: 5)`（`tests/RbfSegmentStore.Tests/SegmentLocatorTests.cs:44`）——期望 5B 非空短尾被 `RbfSegmentStore.OpenExisting` 以 `InvalidTail` 拒绝，实际未抛异常。RBF3 恢复语义（`CompletedTail` 最多补 8B）改变了短尾的分类边界。

这两个失败与编译失败同源：均为 RBF3 格式/恢复合同变更后，`RbfSegmentStore` 层的实现与测试预期尚未对齐。测试是否应更新预期、还是 SegmentStore 层应映射新的恢复语义，属于后续解决方案研究范围，本报告不做判断。

## 3. 已识别的运行时语义风险（未表现为编译错误）

以下为代码与文档 diff 中可证实、但尚无失败现象佐证的适配风险，后续研究需逐项确认：

1. **默认容量推导变化**：`RbfFile.MaxPayloadAndMetaLength` 缩小 4B 会改变 EventJournal 默认 `MaxLogicalPayloadLength` 推导链（`src/EventJournal/EventJournalOptions.cs:10`、`:45`；`src/EventJournal/EventFrameHeader.cs:118`）。
2. **RBF1 可写拒绝**：`CreateNew` 只产 RBF3；可写 `OpenExisting` 不再接受 RBF1（`docs/Rbf/rbf-interface.md:223`）。既有 v1 journal/ref-op-log/segment 文件的可写路径如何处置（迁移 vs 拒绝）未被下游代码体现。
3. **恢复语义差异**：`RbfTailRecoveryAction.CompletedTail` 与 RBF3 不合成墓碑的恢复策略，会改变下游"打开时是否报告恢复动作"的合同面（`RbfTailRecoveryReport` 目前下游调用点均以 `out _` 形态可丢弃，但 SegmentStore 的 strict-tail 校验和 EventJournal 的 `InvalidTail` 分类可能需要重审）。
4. **离线恢复不可用**：`RbfRecovery` 等 API 对 RBF3 抛 `NotSupportedException`；若下游或 toolkit 依赖离线恢复路径，需确认覆盖范围。
5. **Open 不校验 PayloadCRC**：RBF3 下 Open 只做结构资格检查，内容损坏由后续 `ReadFrame` 拒绝（`docs/Rbf/rbf-interface.md:223-229`）；SegmentStore 的 `ValidateActiveTail` 语义需对照确认。

## 4. 未覆盖事项

- 未做任何打包（`eng/Pack.ps1`）或隔离 `PackageReference` smoke；本报告仅覆盖源码 build/test 层。
- 未运行 EventJournal 相关测试，也未验证 `RbfTailRecoveryReport` 是否应向 SegmentStore/EventJournal 层透出。
- 未做 Windows 平台验证。
- 未调查消费应用（兄弟仓）通过包引用受到的影响。
- 两个 `RbfSegmentStore.Tests` 失败仅记录现象，未判定"改实现"还是"改测试预期"。

## 5. 建议的后续研究问题

1. `OpenExisting` 的恢复报告下游应如何消费：丢弃、透出为日志，还是映射进 `StorageOpenException` 语义？
2. `RbfSegmentStore` 层的短尾分类与轮转阈值测试，按 RBF3 合同应如何重述？
3. EventJournal 的 `MaxLogicalPayloadLength` 默认值变化是否构成对上层调用方的行为破坏，需要在冻结合同中显式声明吗？
4. RBF1 既有文件的可写路径处理策略（只读降级、显式迁移入口、或拒绝），与 v2 目录布局迁移如何衔接？
