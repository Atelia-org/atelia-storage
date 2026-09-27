# 不可变 tag 交付记录

2026-09-27，Windows / .NET SDK 10.0.201。对应 [实现合同](immutable-tags-design.md) 与 DurableGraph DB-084 的上游交付片。

## 结论与版本

上游设计、实现、独立源码评审、源码测试和隔离包消费验收完成。没有剩余阻塞项。

- 实现 commit / `StorageSourceRevision`：`deb55672106c8a8966e4b04a75bedf0b1523be7f`。
- 本地开发包版本：`0.1.2-dev.20260927.1`，五包同版本，未发布到远端。
- 本地 feed：`artifacts/tag-feed`，包含五个 nupkg、五个 snupkg 与 `manifest.0.1.2-dev.20260927.1.json`。
- EventJournal nupkg SHA256：`ecbd0a4bfd6e4d0bb2ba23733cada05b1ec1185ba83a1a4bba643b96ce8ae3be`；其余 hash 见 manifest。

本记录是打包后的文档补充；复验包来源时应使用上述实现 commit 的干净 checkout，不能把后续文档 commit 当作包来源。

## 源码验证

```powershell
dotnet build Atelia.Storage.slnx -c Release
dotnet test Atelia.Storage.slnx -c Release --no-build
git diff --check
```

最终构建成功，增量构建 0 warning / 0 error。最初基线完整构建已有 45 个 XML documentation warning；打包时也会重现相关 warning，均不来自新增代码。

| 测试项目 | 通过数 |
|---|---:|
| Primitives | 75 |
| Data | 212 |
| Rbf | 388 |
| RbfSegmentStore | 23 |
| EventJournal | 79 |
| 合计 | 777 |

失败 0、跳过 0；相较基线增加 38 个用例。包括跨重开、分支移动/归档、同名拒绝、只读字节不变、格式固定字节见证、未知格式/重复记录/坏目标/tombstone/CRC/坏尾、发布阶段故障与实际文件句柄错误。Windows 历史段共享冲突用例在本次环境实际执行；非 Windows 平台需要另行验证。

## 隔离包消费

在实现 commit 干净树上执行并通过：

```powershell
./eng/Pack.ps1 -Version 0.1.2-dev.20260927.1 -OutputDirectory ./artifacts/tag-feed
./eng/Test-Package.ps1 -Version 0.1.2-dev.20260927.1 -FeedDirectory ./artifacts/tag-feed -WorkDirectory ../storage-package-smoke-0.1.2-dev.20260927.1
```

仅通过 public `PackageReference` 验证 create → append → tag → 同名 branch move → checked read → close → read-only reopen → resolve。五包版本、资产、来源 revision、portable PDB / 静态 Source Link 及 83 个本地 source checksum 通过。

本机隔离材料保留在 `E:/repos/Atelia-org/storage-package-smoke-0.1.2-dev.20260927.1`，其中 `verified-packages.json` 是已验证 manifest。未测试远端 Source Link 下载、跨操作系统包字节复现或断电恢复；本次产物是本地开发包，不是公开签名发布。

## 真实旧包兼容性

在仓外隔离项目 `E:/repos/Atelia-org/storage-tag-compat-20260927-01` 中，`OldReader` 明确引用 nuget.org 的 `0.1.1-preview.2`，`NewReader` 明确引用上述本地新包；两者的 `project.assets.json` 留存实际依赖版本。

1. 旧包 CreateNew、AppendEventFrame、CreateBranch 生成旧 journal。
2. 新包严格 OpenExisting，读取旧 payload，创建 tag，移动原 branch 到 null；关闭重开后 tag 仍定位旧 event。检查所有旧文件仍保留原字节前缀。
3. 旧包分别执行只读、严格可写、默认 recovery 打开，三者均抛 `InvalidDataException`：`ref-op-log contains unexpected frame tag 0x54464A45.`。
4. 每次旧包拒绝后比较全部文件路径及 SHA256，确认字节不变。

全部通过。`OldReader/Program.cs` 与 `NewReader/Program.cs` 保留本机复验代码；这证明本次构造样本的新读旧、旧拒新边界，不声称覆盖 DG 全部历史数据。

## DurableGraph 接入交接

下游下一片可以固定新版本/revision，接入：

```csharp
journal.CreateTag(name, selectedEvent).Unwrap();
EventAddress resolved = journal.ResolveTag(name).Unwrap();
```

DG 仍需完成本次打开的 `CheckpointAddress` 来源验证、合法检查点校验及新打开地址签发，先确认 Graph/Schema 等依赖，并将 `TagPublicationException.Outcome` 映射至自身 publication/fault 合同。当前 journal 的文件顺序保持 events → ref objects → ref-op-log；无需额外 tag 文件枚举。

本次未修改 DG 源码或固定包依赖；State / Event-only、ref-only CreateBranch + Checkout 等领域级验收属于下游接入片。公开发布和远端推送也未执行。
