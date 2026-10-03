---
docId: "rbf-guide"
title: "RBF 使用指南 (Usage Guide)"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 使用指南 (Usage Guide)
**本文档性质**：Informative（非规范性），提供常见场景的代码范例。
规范性定义请见 [rbf-interface.md](rbf-interface.md)。

本轮示例按已实施的RBF1/RBF3合同同步；源码0999851对应内容的Release RBF测试670/670与正式生产证据见[实施记录§11](rbf-open-fast-path-refactoring.md#11-本轮最终实施与验收记录)。示例不是独立测试产物。

## 打开、兼容与单尾恢复

`RbfFile.CreateNew(path)` 只创建RBF3；`OpenReadOnlyExisting` 读取RBF1或RBF3，保留RBF1原字节、旧ticket与旧容量。`OpenExisting` 只接受RBF3，并在同一独占handle上按结构修复单个残尾；RBF1可写、历史实验RBF2与未知Header拒绝。没有公共格式配置，也不自动转码。

```csharp
using var file = RbfFile.OpenExisting(path, out var recovery);
Console.WriteLine($"Recovery={recovery.Action}, {recovery.OriginalLength}->{recovery.FinalLength}");
```

RBF3恢复只产生None、Truncated、CompletedTail：未完成body截掉，完整body仅补原Key/Fence缺失后缀。Open不校验PayloadCRC，成功只说明所检查的分帧结构可用；业务消费前仍须完整读。普通ticket读每次检查两CRC；FrameInfo在创建时已经验证TrailerCRC，info完整读复用这份不可变元信息资格并检查本次PayloadCRC。恢复报告不证明上层事务提交。只读打开不执行恢复，需动作时拒绝且文件bytes不变。

SizedPtr、TailOffset、payload/meta与buffer长度始终为bytes。RBF3的4B units只在wire边界转换，调用方不要将ticket.Length除以4或手工改写持久ticket。

---

## 5. 使用示例 (Informative)

本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义。

### 5.1 简单写入 (Append)

适用于数据已在内存中准备好的简单场景。

```csharp
void SimpleWrite(IRbfFile file, uint myTag, byte[] data) {
    // 可预见拒绝在I/O之前返回Failure；输出异常后须释放实例并重开。
    var result = file.Append(myTag, data);
    if (result.IsFailure) {
        Console.WriteLine($"Append failed: {result.Error!.Message}");
        return;
    }
    SizedPtr ptr = result.Value;

    Console.WriteLine($"Written at: {ptr.Offset}, Length: {ptr.Length}");
    // 此时 TailOffset 已自动推进
}
```

### 5.2 流式/复杂写入 (BeginAppend)

适用于数据量大、需要流式生成或零拷贝拼接的场景。
建议配合`System.Buffers.BufferExtensions`使用`RbfFrameBuilder.PayloadAndMeta`

```csharp
void StreamingWrite(IRbfFile file, uint myTag, IEnumerable<byte[]> chunks) {
    // 1. 开启事务
    // 注意：builder 是 readonly struct（值类型），避免复制或跨生命周期使用
    // builder.PayloadAndMeta 返回的是 RbfPayloadWriter（值类型 wrapper），
    // 每次调用会校验 epoch；如上转为 IReservableBufferWriter 会装箱。
    // 最好配合 using 确保即使异常也能 Dispose
    using var builder = file.BeginAppend();

    // 2. 获取 Writer (IBufferWriter<byte>)
    var writer = builder.PayloadAndMeta;

    try {
        foreach (var chunk in chunks) {
            // IBufferWriter 标准范式：GetSpan -> CopyTo -> Advance
            // 不依赖额外的扩展方法，手动管理内存与推进
            var span = writer.GetSpan(chunk.Length);
            chunk.CopyTo(span);
            writer.Advance(chunk.Length);
        }

        // 3. 结束追加 (EndAppend)
        // 只有 EndAppend 后，数据才对 Read 即刻可见，TailOffset 才会推进
        var result = builder.EndAppend(myTag);
        if (result.IsFailure) {
            Console.WriteLine($"EndAppend failed: {result.Error!.Message}");
            return;
        }
        SizedPtr ptr = result.Value;

        // EndAppend 后不能再写入，Dispose 变为无操作
    }
    catch (Exception ex) {
        // 4. 自动回滚 (Auto-Abort)
        // 若发生异常导致 EndAppend 未被调用，
        // 退出 using 块触发 Dispose 时，会自动执行 Auto-Abort。
        // 未发布的owned chunks被取消；首次footer准备异常禁止原builder重试。
        // 实际文件输出异常会永久fault，Dispose不能解除，需要释放File后重开。
        Console.WriteLine("Write aborted explicitly or by exception.");
        throw;
    }
}
```

### 5.3 随机读取 (ReadFrame / ReadPooledFrame)

适用于根据索引（SizedPtr）回查数据的场景。有两种读取方式：

**方式一：Buffer 外置（zero-copy，调用方提供 buffer）**

```csharp
void RandomAccessWithBuffer(IRbfFile file, SizedPtr ticket, Span<byte> buffer) {
    // buffer.Length必须至少为ticket.Length bytes；大帧也可用下面的pooled入口。

    var result = file.ReadFrame(ticket, buffer);

    if (result.IsFailure) {
        Console.WriteLine($"Read failed: {result.Error.Message}");
        return;
    }

    // 帧视图指向 buffer 内部
    RbfFrame frame = result.Value;
    Console.WriteLine($"Tag={frame.Tag}, PayloadLen={frame.Payload.Length}");
    // frame 生命周期受限于 buffer 作用域
}
```

**方式二：Pooled 读取（自动管理 buffer）**

```csharp
void RandomAccessPooled(IRbfFile file, SizedPtr ticket) {
    var result = file.ReadPooledFrame(ticket);

    if (result.IsFailure) {
        // 失败时 buffer 已自动归还
        Console.WriteLine($"Read failed: {result.Error.Message}");
        return;
    }

    // 使用 using 确保 buffer 归还
    using RbfPooledFrame frame = result.Value;

    Console.WriteLine($"Tag={frame.Tag}, PayloadLen={frame.Payload.Length}");

    // 如需长期持有数据，必须在 Dispose 前拷贝
    byte[] safePayload = frame.Payload.ToArray();
    // Dispose 后 frame.Payload 不可再访问
}
```

### 5.4 逆向扫描 (ScanReverse)

适用于恢复、重放或查找最新记录。

```csharp
void RecoverState(IRbfFile file) {
    // 默认不显示 Tombstone
    var sequence = file.ScanReverse(showTombstone: false);

    // 只能使用 foreach (duck-typed)
    foreach (RbfFrameInfo info in sequence) {
        // 这里的 info 只包含元信息（不含 payload）
        Console.WriteLine($"Found Frame: Tag={info.Tag}, PayloadLen={info.PayloadLength}");

        // 如需完整数据与 CRC 校验，显式读取
        var result = info.ReadPooledFrame();
        if (result.IsFailure) {
            Console.WriteLine($"Read failed: {result.Error!.Message}");
            break;
        }
        using var frame = result.Value;
        if (IsStateRestored(frame)) break;
    }
}
```

### 5.5 正向扫描 (ScanForward)

适用于健康 RBF 文件的正序 replay。`ScanForward` 产出 `RbfFrameInfo`，不读取 payload，也不校验 `PayloadCrc32C`；业务处理前如需完整校验，应再调用 `ReadFrame` / `ReadPooledFrame`。

```csharp
void ReplayForward(IRbfFile file) {
    var sequence = file.ScanForward(showTombstone: false);

    foreach (RbfFrameInfo info in sequence) {
        Console.WriteLine($"Replay Frame: Tag={info.Tag}, PayloadLen={info.PayloadLength}");

        using var frame = info.ReadPooledFrame().Value;
        Apply(frame);
    }
}
```
