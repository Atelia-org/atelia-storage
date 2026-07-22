Searched for regex `RotationThreshold|rotationThreshold|MaxOffset|MaxFrameLength|FindLastValid|RecoverableTail|Truncate` (`**/{src,tests,docs/Rbf,docs/StateJournal}/**`), 244 results

我准备在当前基线中加入两个“候选、未锁定”小节：一是把 16-byte 内存地址、32-bit hint、canonicality/校验规则和 wire-size 代价写清；二是把 segment threshold 定义为 size+age 的运维分片策略，并给出首轮 16/64/256 GiB 基准点。核心判断是：smart pointer 可行，但只有“目标绑定、不可变、可校验的 hint”适合进入 `EventAddress`；任意可变用户标签应留在上层 wrapper。

Generating patch (62 lines) in [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md)

文档已把“可行但有契约”的边界落下来了。现在做最便宜的判别检查：确认 Markdown patch 无空白错误、章节和关键约束确实出现；通过后我再给出文件系统差异与推荐布局的完整结论。

Ran terminal command:  git diff --check -- docs/EventJournal/event-journal-requirements-and-design.md && rg -n "Address Hint 候选扩展|Segment 阈值选择原则|canonical address|64 GiB" docs/EventJournal/event-journal-requirements-and-design.md

验证已通过。外部资料也支持同一个结论：1 TiB 对三者都不是寻址极限问题，真正差异在于 ext4/XFS 主要校验文件系统元数据而 RBF 仍需负责用户数据完整性，ZFS 则额外提供 block checksum/冗余自愈；我再做一次精确边界核对和编辑器诊断，避免把理论上限与默认配置混为一谈。

Checked [](file:///repos/focus/atelia/docs/EventJournal/event-journal-requirements-and-design.md), no problems found

已提取 https://openzfs.github.io/openzfs-docs/man/master/7/zfsconcepts.7.html

**1 TiB 单文件**
三类文件系统都能承载，但“能承载”和“适合作为 EventJournal 默认 segment”是两回事。

| 文件系统 | 1 TiB 单文件 | 主要注意点 |
|---|---|---|
| ext4 | 常见 4 KiB block + extent 配置下完全可行，理论单文件约 16 TiB | extent/delayed allocation 适合顺序追加；metadata checksum 不保护用户数据，仍需 RBF CRC |
| XFS | 属于常规大文件工作负载 | extent B+tree 和 allocation group 擅长大文件；同样不为普通 file data 提供端到端 checksum |
| OpenZFS | 没有容量压力 | CoW、block checksum、冗余下可自愈；需关注 `recordsize`、同步写和 snapshot 空间 |

在 ZFS 上，append 不会复制整个 1 TiB 文件，只会写新 block、更新相关间接 block，并可能重写最后一个未填满的 record。对顺序 journal 可从 `recordsize=1M` 开始测试，但随机小 frame 读取和频繁 `fsync` 可能更适合较小值。`sync=standard` 应保持开启。

1 TiB 单文件的主要问题是 EventJournal 自己的运维边界：

- 完整 scrub、复制、恢复、隔离和 repack 的最小工作单元过大。
- closed 历史无法逐段 sealed 和一次性归档。
- inode、extent metadata、误操作或介质故障的影响面集中。
- 删除、迁移和恢复单个对象的耗时更难控制。
- active backup 对象长期持续变化。

正常 tail recovery 不应扫描整个 segment，而应由“最大可能撕裂 frame”限制；但完整校验和损坏调查仍受 segment 大小直接影响。

**Segment 阈值建议**
建议使用：

```text
达到 size soft threshold
OR
达到 max age
=> 在下一个 frame 边界轮转
```

首轮可以采用：

- 默认 soft threshold：`64 GiB`
- 基准档：`16 GiB / 64 GiB / 256 GiB`
- 压力测试档：`1 TiB`
- max age：每日或每周轮转
- hard overshoot：最多一个 frame，并在 Spec 中明确

64 GiB 的量级比较平衡：

- 显著大于当前 256 MiB frame hard cap。
- 1 PiB 总数据约 16,384 个 segment，目录分桶后仍很好管理。
- 完整复制、校验和隔离仍是现实可执行的任务。
- 低吞吐场景由 age rotation 避免一个 segment 活跃数年。

我已把 Address Hint 的 canonicality、wire-size 代价以及 segment threshold 的选择原则补入 event-journal-requirements-and-design.md 和 event-journal-requirements-and-design.md。`git diff --check` 与编辑器诊断均通过。

已创建 4 个待办事项

已进行更改。

---

更新了待办事项列表

我会选几类与 EventJournal 真正同构的系统来比较：Kafka/Pulsar 这类 segment log、PostgreSQL/MySQL WAL、RocksDB/LSM，以及 SQLite 单文件路线。重点不是罗列默认值，而是提炼它们为什么切段、哪些元数据按段保存、以及“小文件过多”通常在哪个规模开始反噬。

先核对这些系统的实际分段参数和生命周期，因为默认值只是表象，真正可迁移的是它们把删除、恢复、校验、复制和压缩绑定到什么粒度。

Compacted conversation几类系统给出的共同图景已经很清楚：它们几乎都没有在“一个永久增长的大文件”和“每条记录一个文件”之间二选一，而是构造分层粒度。PostgreSQL 把 WAL 固定切段以便回收和归档，Kafka 用 size/time 双触发滚动，RocksDB/Lucene 用 immutable files 换取并行与原子生命周期管理，再通过 merge 抑制小文件长尾；SQLite 的单文件优势则主要来自部署和可携带性，内部仍必须靠 WAL checkpoint 控制活跃增量。接下来我把这些经验压缩成对 EventJournal 有约束力的结论，并特别区分哪些 LSM 教训并不适用于我们。

有，而且最值得借鉴的不是某个“行业标准文件大小”，而是一套共同结构：

> **只保留极少量 mutable active files，把历史固化为数量受控的 immutable sealed segments；再用 manifest、目录分桶和 FD cache 管理这些 segment。**

这比“一个永久增长的大文件”或“海量小文件”都稳健。

**经典系统对照**

| 系统 | 典型文件粒度 | 为什么这样设计 | 对 EventJournal 的启示 |
|---|---:|---|---|
| PostgreSQL WAL | 默认 16 MiB，可在初始化时设为 1 MiB 至 1 GiB | segment 是归档、复制、回收单位 | 小 segment 主要服务日志运输，不代表数据库数据文件也应这么小 |
| PostgreSQL relation | 默认每 1 GiB 切一个物理文件 | 避免超大单文件带来的平台和运维问题 | 逻辑对象可以无限增长，但物理文件仍然分段 |
| Kafka log | 默认约 1 GiB，并支持按时间滚动 | retention、复制、恢复、索引都以 segment 为单位 | 与 EventJournal 最相似，active segment 少，closed segments immutable |
| RocksDB SST | 常见几十至数百 MiB | 太多文件增加 read amplification、index/filter 内存和 FD 压力；太大则增加 compaction 成本 | “小文件问题”主要来自需要搜索多个文件及持续 compaction，不可直接套到地址直达的 EventJournal |
| Lucene segment | 小于 16 MiB 的 segment 会积极合并，普通 merge 默认避免超过约 5 GiB | 平衡文件数、搜索并行度、merge 成本 | immutable segment 很好，但长期小文件尾巴需要治理 |
| SQLite | 主数据库单文件，WAL 默认约 1000 pages 后 checkpoint | 单文件优化部署、复制和可携带性，但活跃增量仍不能无限增长 | 单文件适合应用容器，不适合几十年永久 append-only history |

这些数字横跨 16 MiB 到数 GiB，说明不存在脱离生命周期语义的“正确大小”。

**共同经验**

1. **文件大小由最慢运维动作决定，而非文件系统上限决定。**

   需要预算的动作包括：校验、复制、恢复、重新下载、归档、迁移、损坏隔离。可以用一个朴素上限：

   $$
   S_{\text{segment}} \le B_{\text{effective}} \times T_{\text{operation-budget}}
   $$

   例如有效恢复带宽为 200 MiB/s，希望单段在 10 分钟内处理完，则上限约为 117 GiB。64 GiB 会比较从容，1 TiB 则明显超出这个预算。

2. **active 与 sealed 必须是不同生命周期。**

   经典日志系统通常只有一个或少数几个 active files。滚动之后，segment 立即成为 immutable，可以独立校验、备份、复制和缓存。EventJournal 应采取同样模型，而不是让所有历史都留在一个仍可增长的文件里。

3. **size 和 age 通常同时控制滚动。**

   推荐条件仍然是：

   ```text
   non-empty && (size >= soft-size-limit || age >= max-age)
   ```

   只按 size，低流量 segment 可能数年不封口；只按 age，突发写入可能产生超大 segment。阈值应是 soft limit，当前 frame 不拆分，最大 overshoot 为一个 frame。

4. **大量文件真正昂贵的通常不是 `read()`，而是控制面。**

   主要成本是目录枚举、`stat/open/close`、FD 数量、每文件索引与 checksum 元数据、备份系统的对象列表，以及启动时逐文件发现。解决办法不是退回单文件，而是：

   - `SegmentNumber` 到路径采用确定性映射；
   - 目录按编号高位分桶；
   - 使用 bounded FD cache；
   - catalog 是可重建的 derived artifact；
   - 不要求启动时打开或扫描全部 segment；
   - 避免为每个 segment 产生一串不必要的 sidecar files。

5. **“小文件读放大”对 EventJournal 没有 LSM 那么严重。**

   RocksDB 查询可能需要检查多个 SST，而 `FrameAddress { SegmentNumber, Ticket }` 能直接定位一个文件和 byte range，查找复杂度不会随 segment 数量线性增长。因此，只要 segment locator 和 FD cache 做好，几千乃至几万个 sealed segments 是合理规模，并不属于典型的“小文件灾难”。

6. **不要为了减少文件数而轻易重写 canonical history。**

   LSM/Lucene 的 merge 会改变物理布局，这对含稳定 `FrameAddress` 的 EventJournal 不合适。若未来确实需要冷归档，可以增加物理存储层：

   ```text
   Frame/RBF record         随机读取与完整性单位
   EventJournal segment     寻址、封口、校验和恢复单位
   Archive bundle/object    冷存储、传输和长期归档单位
   ```

   bundle 可以容纳多个 sealed segments，通过 locator 保持原 `SegmentNumber + Ticket` 不变，而不是给事件重新分配地址。

**对当前方案的判断**

以 1 PiB 历史数据计算：

| Segment 大小 | Segment 数量 |
|---:|---:|
| 16 GiB | 65,536 |
| 64 GiB | 16,384 |
| 256 GiB | 4,096 |
| 1 TiB | 1,024 |

16,384 个确定命名、分桶存放、按需打开的文件完全可以治理；而 1 TiB segment 会显著放大校验、迁移和恢复长尾。因此，当前提出的 **64 GiB soft limit，加每日或每周 age rotation，并比较 16/64/256 GiB**，与经典系统经验是一致的。

最关键的设计判断是：**EventJournal 更像 Kafka 的永久 immutable segmented log，而不是 PostgreSQL 的可回收 WAL，也不是需要反复 compaction 的 RocksDB。** 所以应借鉴 Kafka 的 active/sealed 生命周期和双触发滚动，借鉴数据库的 catalog、checksum、FD cache 和恢复预算，但不照搬它们的具体默认文件大小。

已创建 3 个待办事项
