# FrameAddress 运行时布局与调用成本探针

独立 public ProjectReference 消费者，无 IVT，不参加 solution 或 pack。用于补足 [S2](../../docs/FrameStore-VersionStore/02-framestore-core.md) 的地址成本记录；不建立 CLR 布局或性能门槛。

```powershell
dotnet build experiments/FrameAddressLayoutProbe/Probe.csproj -c Release
dotnet run --project experiments/FrameAddressLayoutProbe/Probe.csproj -c Release --no-build
```

输出 JSON 至 stdout，失败抛异常并返回非零。只分配有限内存，不创建 store。记录目标运行时的 struct 尺寸、相邻数组元素步长、嵌入 `byte + FrameAddress + byte` 值容器的尺寸，以及 1/1024/65536 元素数组的线程分配量（含数组头与对齐）。

先预热 10000 次，再取 3 个各 200000 次的样本；每次调用 public TryWrite、TryRead 与强类型 Equals，并最终核对独立 golden bytes。计时与线程分配不包含样本报告构造；包含循环、检查及计时成本，tiered JIT 和宿主负载仍可影响结果。不将这些数值当作独立方法延迟、业务吞吐承诺或固定布局 ABI。

最终两平台实测见 `evidence/` 与 [R3 源码验收](../../docs/FrameStore-VersionStore/02-framestore-final-acceptance.md)。固定 wire 宽度继续由 `FrameAddress.EncodedSize` 的 12B 合同决定。
