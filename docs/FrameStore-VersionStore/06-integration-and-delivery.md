# S6：消费者验证、公共包与交付

状态：**Draft；实施、包发布和消费者切换均未执行**。
前置：[S0](00-architecture-decisions.md)、[S1](01-rbf-sized-append.md)、[S2](02-framestore-core.md)、[S3](03-framestore-batches-and-durability.md)、[S4](04-versionstore-publication.md)、[S5](05-versionstore-names-and-indexes.md)。
本阶段验证前序合同的组合，不作为前序层运行正确性的反向依赖。
依赖 S5 的命名核心；可选 snapshot/Archive 等只按声明的支持范围验收，不阻塞明确不包含它们的核心候选。

## 本阶段目标

证明两个新库承载中性 Frame 存储及统一根发布；完成独立源码、进程中断、包消费和交付证据。
DurableGraph 是需求来源，实际接入必须先定位其当前源码/合同，再按明确范围实施。当前环境没有该项目实证，不使用历史 tag 文档替代。

## 候选验收模型

| 模型 | 构建内容 | 用来证明 |
| --- | --- | --- |
| 简单 immutable 状态根 | 两类自定义 binary records，引用此前根及新增帧 | 不依赖 EventFrameHeader/Parent |
| 循环引用图根 | A↔B/self-reference，跨段依赖和 root | 提前地址、batch 及全集耐久确认 |

两个模型用同一套 public API 写入 data、Prepare/Commit 发布到私有 control、创建 branch/tag、切换旧根、关闭并冷重开。Fork 复用合法旧根创建分支，不默认承诺来源 provenance；Archive/snapshot 仅在候选明确支持时加入。
这些是仓内可独立运行的最小消费者，不是 DurableGraph 已接入的证据。

## 候选合同

### spec [S-INTEGRATION-PUBLIC-BOUNDARY] 集成通过 public API

新消费者 MUST 用两个新库的 public API，不能通过 InternalsVisibleTo、旧 EventJournal 类型或业务库引用补齐缺失能力。
如果失败暴露前序合同缺口，应回到相应 S1–S5 修订/复验。集成层不复制开销公式、创建第二个根权威或绕过耐久协议。

### spec [R-INTEGRATION-CRASH-EVIDENCE] 进程中断验证真实组合

资格 MUST 覆盖地址规划、批次各帧、data barrier、Prepare 的控制 metadata/轮转、发布输出、control flush、库内投影及应用状态安装窗口。
使用子进程中断/真实磁盘镜像验证冷重开，并保留源 commit、SDK、平台、阶段和预期/实际结果；单元 fault injection 不能冒充进程中断实证。
恢复结果只能来自完整记录及其资格；Unknown/异长度地址复用通过实际 control 主链确认 token，不用旧 ticket 读取失败或最新 head 证明 Absent。
补重复 Commit 先于 data IO 拒绝、data/control context 混用与独立 fault、Prepare 后新增 data 被 Commit barrier 覆盖，以及应用安装失败不撤销 Confirmed。

### spec [S-DELIVERY-PACK-OWNER] 包入口有单一负责人

新增生产包清单及 pack 顺序 MUST 只在 `eng/Pack.ps1` 注册。现有五包与两个新包需要可明确选择各自交付闭包；不能复制另一个脚本内的生产包清单。
同时适配 package-mode 依赖、smoke、metadata/assets/Source Link 校验与 CI。现有 All/selective 的实际格式限制必须审查，不能只在 ValidateSet 增加名字就声称完成。
旧栈 smoke 与新栈 smoke 分别报告，不能用新栈证据掩盖旧基线已知问题。

### spec [S-DELIVERY-ISOLATED-SMOKE] 隔离包消费必需

候选交付 MUST 运行扩展后的 `eng/Test-Package.ps1`，仓外独立 workspace/cache/config，仅通过 public PackageReference 消费 FrameStore/VersionStore。
验证实际资产闭包、精确逐包版本、manifest/StorageSourceRevision、包 hash、静态 Source Link 与本地源码 checksum；新包闭包不得带入 RbfSegmentStore/EventJournal 或 atelia 业务依赖。
新内容用新版本，pack 要求干净已提交树及显式版本/输出目录。公开发布另依当前会话授权和账号能力。

### spec [S-DELIVERY-SEPARATE-EVIDENCE] 交付状态分别报告

源码测试、子进程恢复、Windows/Linux、隔离候选包、公开签名包回读、消费者升级及真实实例切换 MUST 分别记录。
源级通过不代表 package API 可用，静态 Source Link 不代表远端下载成功，最小图消费者不代表 DurableGraph 领域正确性。

## 仓库接入清单

| 入口 | 前序/本阶段责任 |
| --- | --- |
| 四个新 csproj、solution | S2/S4 创建和接入，S6 核对 build/test/package 身份 |
| RBF 新 API 与 XML | S1 完成；S6 核对公共包入口可消费 |
| 新库源码/测试与指南 | S2–S5 形成；S6 将 Accepted 合同映射到 public smoke |
| `eng/Pack.ps1` | S6 注册包和明确选择/版本算法 |
| `eng/Test-Package.ps1`、metadata helper | S6 增加隔离新闭包 smoke，不削弱旧校验 |
| `eng/Verify-Published.ps1`、发布 workflow/CI | 若纳入公开交付，按实际授权和新 manifest 形态适配 |
| 根 README / AGENTS.md | 项目实际存在、交付事实变化后更新；本轮只增加设计入口 |
| 旧库维护 | 另立修复工作包；不得为支持新栈修改旧职能/数据 |

## 实施片

1. S6-A：完善从 V1 起已有的 public API 消费者与跨冷进程 golden 资格，验证两种状态模型和双上下文生命周期。
2. S6-B：串行构建/测试、集成进程中断和 Windows/Linux 平台证据；旧基线问题明确分开处理。
3. S6-C：扩展唯一 pack 清单及选择算法、候选 manifest、隔离 smoke、CI。
4. S6-D：按授权提交/打包，完成隔离候选包消费，形成可审查交付记录。
5. S6-E：定位并评估 DurableGraph 接缝；只有接入范围已明确时修改消费者、固定包/revision 和验证领域恢复。

每片允许独立评审/返工。所有 .NET 重命令串行；先 build Release，再同配置 `dotnet test ... -c Release --no-build`。package smoke 使用既有脚本的独立输出边界。
最终应运行完整 solution 的适当验收；如果旧维护问题尚未解决，记录整体未完成，不删除项目、不替换测试含义、不制造干净树。

## Ready 阻断项与出口

| ID | 需审定 |
| --- | --- |
| S6-Q1 | 新旧包选择方式、独立版本与 manifest 闭包 |
| S6-Q2 | public smoke 项目/入口与 Source Link/资产校验扩展 |
| S6-Q3 | 两平台恢复资格、资源/规模阈值及证据保存路径 |
| S6-Q4 | 若纳入真实接入：DurableGraph 实际 API/数据需求和切换范围；不阻塞独立库/包核心 |

最低出口：两个新库的 Accepted 源码闭包、组合恢复资格和隔离公共包消费证据。
公开发布或真实消费者切换若未纳入当前工作，交付记录明确停在哪一层；不将库可用与已切换混为一个状态。
