# Binary public PackageReference smoke

此示例只直接引用 `Atelia.Binary`，不引用存储项目或其他 Atelia 包。它定义一个示意应用 schema：版本、按 Ordinal 排序的字符串 key 到 12B 地址的字典、受控 string/bytes 和 null。这里的地址字段是 `uint FileId + ulong PackedTicket`；不解释或验证 SizedPtr，也不冻结 FrameStore/VersionStore 的 wire format。

由主线交付入口从干净已提交树验证：

```powershell
./eng/Pack.ps1 -Version <new-version> -OutputDirectory ./artifacts/feed
./eng/Test-Package.ps1 -Version <new-version> -FeedDirectory ./artifacts/feed -WorkDirectory W:/new-binary-package-smoke
```

验证器分别建立 Rbf/Binary 两个纯 PackageReference consumer。Rbf 保持原三包闭包；Binary assets 只能包含单个 `Atelia.Binary` Atelia 包。All manifest 描述四包，metadata 和本地 Source Link checksum 覆盖全部候选及 symbols。示例核对完整记录的 long 尺寸预算、12B little-endian 字段、受控压缩的实际收益、可变输入快照和完整读回。

这提供候选 public 包消费证据。公开发布、远端 Source Link 下载、FrameStore/VersionStore 格式和性能分别需要独立证据。
