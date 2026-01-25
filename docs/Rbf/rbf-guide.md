---
docId: "rbf-guide"
title: "RBF 使用指南 (Usage Guide)"
produce_by:
      - "wish/W-0009-rbf/wish.md"
---

# RBF 使用指南 (Usage Guide)
**本文档性质**：Informative（非规范性），提供常见场景的代码范例。
规范性定义请见 [rbf-interface.md](rbf-interface.md)。

---

## 5. 使用示例 (Informative)

本节为参考示例，不属于 RBF 层规范。FrameTag 的具体取值与语义由上层定义。

### 5.1 简单写入 (Append)

适用于数据已在内存中准备好的简单场景。

```csharp
void SimpleWrite(IRbfFile file, uint myTag, byte[] data) {
    // 直接写入，原子性（要么全写进，要么全不写进）
    SizedPtr ptr = file.Append(myTag, data);

    Console.WriteLine($"Written at: {ptr.Offset}, Length: {ptr.Length}");
    // 此时 TailOffset 已自动推进
}
```

### 5.2 流式/复杂写入 (BeginAppend)

适用于数据量大、需要流式生成或零拷贝拼接的场景。
建议配合`System.Buffers.BufferExtensions`使用`RbfFrameBuilder.Payload`

```csharp
void StreamingWrite(IRbfFile file, uint myTag, IEnumerable<byte[]> chunks) {
    // 1. 开启事务
    // 注意：builder 是 ref struct，最好配合 using 确保即使异常也能 Dispose
    using var builder = file.BeginAppend();

    // 2. 获取 Writer (IBufferWriter<byte>)
    var writer = builder.Payload;

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
        SizedPtr ptr = builder.EndAppend(myTag);

        // EndAppend 后不能再写入，Dispose 变为无操作
    }
    catch (Exception ex) {
        // 4. 自动回滚 (Auto-Abort)
        // 若发生异常导致 EndAppend 未被调用，
        // 退出 using 块触发 Dispose 时，会自动执行 Auto-Abort。
        // 该帧在物理上可能由部分脏数据残留，但在逻辑上视为“不存在”。
        Console.WriteLine("Write aborted explicitly or by exception.");
        throw;
    }
}
```

### 5.3 随机读取 (ReadFrame / ReadPooledFrame)

适用于根据索引（SizedPtr）回查数据的场景。有两种读取方式：

**方式一：Buffer 外置（zero-copy，调用方提供 buffer）**

```csharp
void RandomAccessWithBuffer(IRbfFile file, SizedPtr ticket) {
    // 调用方提供足够大的 buffer
    Span<byte> buffer = stackalloc byte[ticket.Length];

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
        Span<byte> buffer = stackalloc byte[info.Ticket.Length];
        var result = file.ReadFrame(in info, buffer);
        if (result.IsFailure) {
            Console.WriteLine($"Read failed: {result.Error.Message}");
            break;
        }

        if (IsStateRestored(result.Value)) break;
    }
}
```
