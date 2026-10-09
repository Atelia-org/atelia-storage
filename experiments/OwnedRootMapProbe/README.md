# RootMap 自有集合的 BCL 探针

本探针只验证 .NET 10 集合的拥有权、普通公开集合接口与重复 key 行为，不实现 FrameStore / VersionStore。`int` 是不可变 value 的占位，不是 FrameAddress codec；不测 wire、发布、恢复、业务闭包或性能。

使用仓根 `global.json` 指定的 SDK：

```powershell
dotnet build experiments/OwnedRootMapProbe/Probe.csproj -c Release
dotnet run --project experiments/OwnedRootMapProbe/Probe.csproj -c Release --no-build
```

2026-10-09 在 Windows、SDK 10.0.201 / runtime 10.0.5 上执行，Release build 无警告/错误，探针断言通过。[原始 JSON](evidence/2026-10-09-result.json)记录实际 runtime、平台、具体集合类型与各入口结果；没有 PackageReference、ProjectReference，也未注册到 solution 或 pack 清单。

| 检查 | 观察 |
| --- | --- |
| ReadOnlyDictionary 包装私有 Dictionary；map / Keys / Values 的 ICollection.SyncRoot | 0、1、4、100 项均返回真正的 backing Dictionary；从该入口改写后，map 能读到新值 |
| FrozenDictionary 的同一组入口及直接 IDictionary 修改尝试 | 同样四组大小均无上述 backing Dictionary 逃逸，直接修改不能成功；部分 Keys/Values 的 SyncRoot 抛 NotSupportedException |
| 直接 ToFrozenDictionary 的重复 Ordinal key | 后一值覆盖前一值，不能用它独自实现拒绝重复的 codec/输入合同 |
| 逐项私有 Ordinal Dictionary.TryAdd，随后冻结 | 重复项被拒绝；不继承输入的 OrdinalIgnoreCase 比较器；修改原输入不影响已冻结结果 |
| 两份不同插入顺序的冻结字典 | Count + Ordinal key lookup + value 比较可判断内容相同；改变一个 value 则不同 |

因此当前设计选用“逐项 checked 捕获 → FrozenDictionary → IReadOnlyDictionary 公面”。这既满足现有快照拥有权要求，也无需自定义公开 RootMap/Builder/比较器框架。仅实现 IReadOnlyDictionary 的正确私有封装或其他不可变集合也可能成立；本探针没有证明 FrozenDictionary 的内存或性能最优。

观察限于程序列出的常规集合接口和四组大小，不构成所有 .NET 版本、集合特化或平台的全面审计，也不保证防御反射、Unsafe 或违反 immutable marshal 所有权约定的改写。FrozenDictionary 的临时捕获/构建成本和新库实际资格仍须在实施阶段验证。
