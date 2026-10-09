# 私有初始化前缀探针

研究基线：`a7794cf`。验证 RBF3 私有初始化前缀的有界机制，**没有新增 RBF public API 或 FrameStore 实现**。

原型复用 RBF 内部 layout/footer 与 Data CRC/XOR，判断实存 bytes 是否兼容某个合法 Key 下的预期 HeaderFence + 一帧。使用仓库既有 `Atelia.UnifiedRootProbe` friend，仅为研究取证；生产 FrameStore 不得以此替代 [待实施公共合同](../../docs/Rbf/rbf-initial-frame-prefix.md)。

```powershell
dotnet build experiments/PrivateInitializationPrefixProbe/Probe.csproj -c Release
dotnet run --project experiments/PrivateInitializationPrefixProbe/Probe.csproj -c Release --no-build
# 可传另一 JSON 路径，保留已有结果
dotnet run --project experiments/PrivateInitializationPrefixProbe/Probe.csproj -c Release --no-build -- artifacts/private-prefix-new-result.json
```

[2026-10-09 结果](evidence/2026-10-09-result.json)：SDK 10.0.201、.NET 10.0.5、Windows/E:，**3,139 项断言通过**，按组记录；这不是 3,139 个独立场景族。Release build 成功，现有 Data/Rbf XML 注释产生 41 个警告，没有更改相关源码。

- 三份完整 60B 初始化：默认 Key=0；StoreId 含四个禁止候选 word，使默认 Key=4；既有 seam 指定合法高位 Key=0x81234567。完整文件由 public RO/scan/checked read 验证 CRC 与 payload。
- 每份遍历 0..60B：在既有 instrumentation 注入**真实前缀写入后 IOException**，验证输出确为完整镜像前缀、请求顺序及谓词不修改文件。4B 情况停在 CreateNew 后；60B 正常完成。没有运行进程 kill。
- 完整镜像每个 bit 翻转都拒绝；错预期 StoreId/FileId/版本/tag、RBF1、附加 suffix、推导出的禁用 Key 拒绝。
- 不足一个 encoded body word 的部分 Key 可有合法补全，故部分字节变化仍可被接受；它不认证历史作者或尚未出现的身份。
- 另一个完整合法 36B 小帧虽 <=60B 仍拒绝；合法 header 后用户帧也拒绝。单看长度或 header 不足以授权删除。
- payload 0/1/2/3/4/231/232B 的全部镜像前缀通过，233B 拒绝超出有界证明域。

每次在 `artifacts/private-initialization-prefix-probe/<GuidN>/` 保留独立 fixture，不递归删除；证据中的 `designBaseline` 是起始设计/生产源码基线，不表示新探针已包含于该提交。探针是独立非 pack 项目，不加入 solution、生产包清单或运行时依赖。

证据只覆盖 prefix 算法、真实部分写入和公面完整文件读取。根/路径/ordinary/no-follow、正式 max 与私有槽准入、删除/关闭失败、完整 FrameStore、两平台/rename/断电/包消费均未验收。

