using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfFrameBuilder 基本功能测试。</summary>
/// <remarks>
/// 规范引用：
/// - Task 7.7: RbfFrameBuilder 基本功能测试
/// - @[A-RBF-FRAME-BUILDER]
/// - @[S-RBF-BUILDER-SINGLE-OPEN]
/// - @[S-RBF-TAILOFFSET-UPDATE]
/// </remarks>
public class RbfFrameBuilderTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    /// <summary>生成一个不存在的临时文件路径。</summary>
    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try {
                if (File.Exists(path)) {
                    File.Delete(path);
                }
            }
            catch {
                // 忽略清理错误
            }
        }
    }

    // ========== 正常路径测试 ==========

    /// <summary>正常路径：BeginAppend → 写入数据 → EndAppend → ReadFrame 验证帧可读。</summary>
    [Fact]
    public void BeginAppend_WriteData_EndAppend_FrameIsReadable() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        uint tag = 0x12345678;

        // Act
        SizedPtr ptr;
        using (var file = RbfFile.CreateNew(path)) {
            using (var builder = file.BeginAppend()) {
                // 写入 payload
                var span = builder.PayloadAndMeta.GetSpan(payload.Length);
                payload.CopyTo(span);
                builder.PayloadAndMeta.Advance(payload.Length);

                // 提交帧
                ptr = builder.EndAppend(tag);
            }

            // 验证帧可读
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess);
            using var frame = result.Value!;
            Assert.Equal(tag, frame.Tag);
            Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
            Assert.False(frame.IsTombstone);
        }
    }

    /// <summary>空 Payload：BeginAppend → EndAppend(tag, 0) → 验证最小帧。</summary>
    [Fact]
    public void EndAppend_EmptyPayload_CreatesMinimalFrame() {
        // Arrange
        var path = GetTempFilePath();
        uint tag = 0xDEADBEEF;

        // Act
        SizedPtr ptr;
        using (var file = RbfFile.CreateNew(path)) {
            using (var builder = file.BeginAppend()) {
                // 不写入任何数据，直接提交
                ptr = builder.EndAppend(tag);
            }

            // 验证最小帧
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess);
            using var frame = result.Value!;
            Assert.Equal(tag, frame.Tag);
            Assert.True(frame.PayloadAndMeta.IsEmpty);

            // 验证帧长度为最小帧长度（24 字节）
            int expectedMinFrameLength = new FrameLayout(0).FrameLength;
            Assert.Equal(24, expectedMinFrameLength);
            Assert.Equal(expectedMinFrameLength, ptr.Length);
        }
    }

    /// <summary>带 TailMeta：写入 Payload + TailMeta → EndAppend(tag, tailMetaLen) → ReadTailMeta 验证内容。</summary>
    [Fact]
    public void EndAppend_WithTailMeta_TailMetaIsReadable() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC];
        uint tag = 0x87654321;

        // Act
        SizedPtr ptr;
        using (var file = RbfFile.CreateNew(path)) {
            using (var builder = file.BeginAppend()) {
                // 写入 payload
                var payloadSpan = builder.PayloadAndMeta.GetSpan(payload.Length);
                payload.CopyTo(payloadSpan);
                builder.PayloadAndMeta.Advance(payload.Length);

                // 写入 tailMeta
                var tailMetaSpan = builder.PayloadAndMeta.GetSpan(tailMeta.Length);
                tailMeta.CopyTo(tailMetaSpan);
                builder.PayloadAndMeta.Advance(tailMeta.Length);

                // 提交帧，指定 tailMetaLength
                ptr = builder.EndAppend(tag, tailMeta.Length);
            }

            // 验证 TailMeta 可读
            var readResult = file.ReadPooledTailMeta(ptr);
            Assert.True(readResult.IsSuccess);
            using var pooledTailMeta = readResult.Value!;
            Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());
        }
    }

    /// <summary>使用 Reservation：ReserveSpan → 写入数据 → Commit → EndAppend。</summary>
    [Fact]
    public void EndAppend_WithReservation_Success() {
        // Arrange
        var path = GetTempFilePath();
        uint tag = 0xCAFEBABE;

        // Act
        SizedPtr ptr;
        using (var file = RbfFile.CreateNew(path)) {
            using (var builder = file.BeginAppend()) {
                // 预留 4 字节用于写入长度
                var reservedSpan = builder.PayloadAndMeta.ReserveSpan(4, out var token, tag: "length");

                // 写入实际数据
                byte[] data = [0x11, 0x22, 0x33, 0x44, 0x55];
                var dataSpan = builder.PayloadAndMeta.GetSpan(data.Length);
                data.CopyTo(dataSpan);
                builder.PayloadAndMeta.Advance(data.Length);

                // 回填预留的长度
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(reservedSpan, data.Length);
                builder.PayloadAndMeta.Commit(token);

                // 提交帧
                ptr = builder.EndAppend(tag);
            }

            // 验证帧可读
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess);
            using var frame = result.Value!;
            Assert.Equal(tag, frame.Tag);

            // 验证 payload 内容：4 字节长度 + 5 字节数据
            var payloadAndMeta = frame.PayloadAndMeta;
            Assert.Equal(9, payloadAndMeta.Length);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payloadAndMeta);
            Assert.Equal(5, length);
            Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }, payloadAndMeta.Slice(4).ToArray());
        }
    }

    // ========== 异常路径测试 ==========

    /// <summary>重复 EndAppend：应抛出 InvalidOperationException。</summary>
    [Fact]
    public void EndAppend_CalledTwice_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var builder = file.BeginAppend();

        // Act - 第一次 EndAppend
        builder.EndAppend(0x1234);

        // Act & Assert - 第二次 EndAppend 应抛出异常
        var ex = Assert.Throws<InvalidOperationException>(() => builder.EndAppend(0x5678));
        Assert.Contains("EndAppend", ex.Message);

        // Cleanup
        builder.Dispose();
    }

    /// <summary>重复 Dispose：幂等，不抛异常。</summary>
    [Fact]
    public void Dispose_CalledTwice_Idempotent() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var builder = file.BeginAppend();

        // Act - 多次 Dispose
        builder.Dispose();
        builder.Dispose();
        builder.Dispose();

        // 不应抛出异常
    }

    /// <summary>未提交 Reservation 时 EndAppend：应抛出 InvalidOperationException。</summary>
    [Fact]
    public void EndAppend_WithUncommittedReservation_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // 预留空间但不 Commit
        _ = builder.PayloadAndMeta.ReserveSpan(4, out var token, tag: "uncommitted");

        // 写入一些数据
        var span = builder.PayloadAndMeta.GetSpan(4);
        span[0] = 0x11;
        builder.PayloadAndMeta.Advance(4);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => builder.EndAppend(0x1234));
        Assert.Contains("reservation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>TailMetaLength 超过 PayloadAndMetaLength：应抛出 InvalidOperationException。</summary>
    [Fact]
    public void EndAppend_TailMetaLengthExceedsPayloadAndMetaLength_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // 写入 4 字节数据
        byte[] data = [0x01, 0x02, 0x03, 0x04];
        var span = builder.PayloadAndMeta.GetSpan(data.Length);
        data.CopyTo(span);
        builder.PayloadAndMeta.Advance(data.Length);

        // Act & Assert - tailMetaLength (10) 超过 payloadAndMetaLength (4)
        var ex = Assert.Throws<InvalidOperationException>(() => builder.EndAppend(0x1234, tailMetaLength: 10));
        Assert.Contains("tailMetaLength", ex.Message);
    }

    /// <summary>TailMetaLength 超过 MaxTailMetaLength：应抛出 InvalidOperationException。</summary>
    /// <remarks>
    /// 覆盖 RbfFrameBuilder.EndAppend 中的约束：
    /// <code>
    /// if (tailMetaLength > FrameLayout.MaxTailMetaLength) {
    ///     throw new InvalidOperationException(...)
    /// }
    /// </code>
    /// MaxTailMetaLength = 65535 (ushort.MaxValue)
    /// 需要先写入足够多的数据，避免被 tailMetaLength > payloadAndMetaLength 分支先拦截。
    /// </remarks>
    [Fact]
    public void EndAppend_TailMetaLengthExceedsMaxTailMetaLength_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // MaxTailMetaLength = 65535，需要写入 >= 65536 字节数据
        // 以确保 tailMetaLength (65536) <= payloadAndMetaLength，避免被其他分支拦截
        int dataLength = 66_000; // > MaxTailMetaLength + 1
        var span = builder.PayloadAndMeta.GetSpan(dataLength);
        span.Clear(); // 无需填充实际数据，清零即可
        builder.PayloadAndMeta.Advance(dataLength);

        // Act & Assert - tailMetaLength = 65536 超过 MaxTailMetaLength (65535)
        int invalidTailMetaLength = ushort.MaxValue + 1; // 65536
        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.EndAppend(0x1234, tailMetaLength: invalidTailMetaLength)
        );
        Assert.Contains("MaxTailMetaLength", ex.Message);
    }

    // ========== TailOffset 验证测试 (Task 7.4) ==========

    /// <summary>EndAppend 后 TailOffset 正确：TailOffset == startOffset + frameLength + FenceSize。</summary>
    [Fact]
    public void EndAppend_TailOffsetIsCorrect() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0xABCDEF00;

        // Act
        using var file = RbfFile.CreateNew(path);
        long startTailOffset = file.TailOffset;
        Assert.Equal(RbfLayout.FirstFrameOffset, startTailOffset); // HeaderFence 后

        SizedPtr ptr;
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            ptr = builder.EndAppend(tag);
        }

        // Assert
        // TailOffset = startOffset + frameLength + FenceSize
        long expectedTailOffset = ptr.Offset + ptr.Length + RbfLayout.FenceSize;
        Assert.Equal(expectedTailOffset, file.TailOffset);
    }

    /// <summary>Builder 生命周期内 TailOffset 不变：BeginAppend → 写数据 → 验证 TailOffset 未变。</summary>
    [Fact]
    public void Builder_TailOffsetUnchangedDuringLifecycle() {
        // Arrange
        var path = GetTempFilePath();

        // Act
        using var file = RbfFile.CreateNew(path);
        long initialTailOffset = file.TailOffset;

        using (var builder = file.BeginAppend()) {
            // 写入大量数据
            byte[] data = new byte[1000];
            Random.Shared.NextBytes(data);
            var span = builder.PayloadAndMeta.GetSpan(data.Length);
            data.CopyTo(span);
            builder.PayloadAndMeta.Advance(data.Length);

            // 在 EndAppend 前 TailOffset 应保持不变
            Assert.Equal(initialTailOffset, file.TailOffset);

            // 提交后 TailOffset 更新
            builder.EndAppend(0x1234);
        }

        // EndAppend 后 TailOffset 应已更新
        Assert.NotEqual(initialTailOffset, file.TailOffset);
    }

    // ========== ReadFrame L3 完整校验测试 ==========

    /// <summary>验证提交的帧通过 L3 完整校验（ReadFrame 成功）。</summary>
    [Fact]
    public void EndAppend_FramePassesL3Validation() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[256];
        Random.Shared.NextBytes(payload);
        uint tag = 0x11223344;

        // Act
        SizedPtr ptr;
        using var file = RbfFile.CreateNew(path);
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            ptr = builder.EndAppend(tag);
        }

        // Assert - ReadFrame 使用 Span buffer，执行 L3 完整校验
        byte[] buffer = new byte[ptr.Length];
        var result = file.ReadFrame(ptr, buffer);
        Assert.True(result.IsSuccess, $"ReadFrame failed: {result.Error}");

        var frame = result.Value!;
        Assert.Equal(tag, frame.Tag);
        // Payload = PayloadAndMeta - TailMeta
        var actualPayload = frame.PayloadAndMeta.Slice(0, frame.PayloadAndMeta.Length - frame.TailMetaLength);
        Assert.Equal(payload, actualPayload.ToArray());
    }

    /// <summary>带 TailMeta 的帧也通过 L3 完整校验。</summary>
    [Fact]
    public void EndAppend_WithTailMeta_FramePassesL3Validation() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD];
        uint tag = 0x55667788;

        // Act
        SizedPtr ptr;
        using var file = RbfFile.CreateNew(path);
        using (var builder = file.BeginAppend()) {
            // 写入 payload
            var payloadSpan = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(payloadSpan);
            builder.PayloadAndMeta.Advance(payload.Length);

            // 写入 tailMeta
            var tailMetaSpan = builder.PayloadAndMeta.GetSpan(tailMeta.Length);
            tailMeta.CopyTo(tailMetaSpan);
            builder.PayloadAndMeta.Advance(tailMeta.Length);

            ptr = builder.EndAppend(tag, tailMeta.Length);
        }

        // Assert - ReadFrame 执行 L3 完整校验
        byte[] buffer = new byte[ptr.Length];
        var result = file.ReadFrame(ptr, buffer);
        Assert.True(result.IsSuccess, $"ReadFrame failed: {result.Error}");

        var frame = result.Value!;
        Assert.Equal(tag, frame.Tag);
        // Payload = PayloadAndMeta 前段, TailMeta = PayloadAndMeta 后段
        var actualPayload = frame.PayloadAndMeta.Slice(0, frame.PayloadAndMeta.Length - frame.TailMetaLength);
        var actualTailMeta = frame.PayloadAndMeta.Slice(frame.PayloadAndMeta.Length - frame.TailMetaLength);
        Assert.Equal(payload, actualPayload.ToArray());
        Assert.Equal(tailMeta, actualTailMeta.ToArray());
    }

    // ========== 多帧序列测试 ==========

    /// <summary>连续使用 Builder 写入多帧，偏移序列正确。</summary>
    [Fact]
    public void MultipleBuilders_OffsetsCorrect() {
        // Arrange
        var path = GetTempFilePath();
        var frames = new List<(SizedPtr ptr, byte[] payload, uint tag)>();

        // Act
        using var file = RbfFile.CreateNew(path);
        for (int i = 0; i < 3; i++) {
            byte[] payload = new byte[10 + i * 5];
            Random.Shared.NextBytes(payload);
            uint tag = (uint)(0x1000 + i);

            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            var ptr = builder.EndAppend(tag);

            frames.Add((ptr, payload, tag));
        }

        // Assert - 每帧可读且内容正确
        foreach (var (ptr, expectedPayload, expectedTag) in frames) {
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess);
            using var frame = result.Value!;
            Assert.Equal(expectedTag, frame.Tag);
            Assert.Equal(expectedPayload, frame.PayloadAndMeta.ToArray());
        }

        // 验证帧偏移序列
        for (int i = 1; i < frames.Count; i++) {
            var prev = frames[i - 1].ptr;
            var curr = frames[i].ptr;
            long expectedOffset = prev.Offset + prev.Length + RbfLayout.FenceSize;
            Assert.Equal(expectedOffset, curr.Offset);
        }
    }

    /// <summary>混合使用 Append 和 Builder 写入，帧序列正确。</summary>
    [Fact]
    public void MixedAppendAndBuilder_FramesCorrect() {
        // Arrange
        var path = GetTempFilePath();
        var ptrs = new List<SizedPtr>();

        // Act
        using var file = RbfFile.CreateNew(path);

        // 第一帧：使用 Append
        byte[] payload1 = [0x01, 0x02, 0x03];
        var result1 = file.Append(0x1111, payload1);
        Assert.True(result1.IsSuccess);
        ptrs.Add(result1.Value!);

        // 第二帧：使用 Builder
        byte[] payload2 = [0x04, 0x05, 0x06, 0x07];
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload2.Length);
            payload2.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload2.Length);
            ptrs.Add(builder.EndAppend(0x2222));
        }

        // 第三帧：使用 Append
        byte[] payload3 = [0x08, 0x09];
        var result3 = file.Append(0x3333, payload3);
        Assert.True(result3.IsSuccess);
        ptrs.Add(result3.Value!);

        // Assert - 所有帧可读
        Assert.Equal(3, ptrs.Count);

        var frame1 = file.ReadPooledFrame(ptrs[0]);
        Assert.True(frame1.IsSuccess);
        using (var f = frame1.Value!) {
            Assert.Equal(0x1111u, f.Tag);
            Assert.Equal(payload1, f.PayloadAndMeta.ToArray());
        }

        var frame2 = file.ReadPooledFrame(ptrs[1]);
        Assert.True(frame2.IsSuccess);
        using (var f = frame2.Value!) {
            Assert.Equal(0x2222u, f.Tag);
            Assert.Equal(payload2, f.PayloadAndMeta.ToArray());
        }

        var frame3 = file.ReadPooledFrame(ptrs[2]);
        Assert.True(frame3.IsSuccess);
        using (var f = frame3.Value!) {
            Assert.Equal(0x3333u, f.Tag);
            Assert.Equal(payload3, f.PayloadAndMeta.ToArray());
        }
    }

    // ========== Task 7.8: Auto-Abort 测试 ==========
    // 规范引用：@[S-RBF-BUILDER-DISPOSE-ABORTS-UNCOMMITTED-FRAME]

    /// <summary>Zero I/O Abort（小 Payload）：BeginAppend → 写 100B → Dispose → 验证文件无变化。</summary>
    [Fact]
    public void AutoAbort_SmallPayload_ZeroIO() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧确保文件有初始内容
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);

        // 记录 Dispose 前的文件长度
        long originalLength = new FileInfo(path).Length;

        // Act - BeginAppend → 写 100B → Dispose（不调用 EndAppend）
        using (var builder = file.BeginAppend()) {
            byte[] payload = new byte[100];
            Random.Shared.NextBytes(payload);
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            // Dispose without EndAppend - Auto-Abort
        }

        // Assert - 文件长度不变（Zero I/O）
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    /// <summary>Zero I/O Abort（大 Payload）：BeginAppend → 写 100KB → Dispose → 验证文件无变化。</summary>
    /// <remarks>HeadLen reservation 阻塞 flush，任意大小 payload 都是 Zero I/O Abort。</remarks>
    [Fact]
    public void AutoAbort_LargePayload_ZeroIO() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧确保文件有初始内容
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);

        // 记录 Dispose 前的文件长度
        long originalLength = new FileInfo(path).Length;

        // Act - BeginAppend → 写 100KB → Dispose（不调用 EndAppend）
        using (var builder = file.BeginAppend()) {
            byte[] payload = new byte[100 * 1024]; // 100KB
            Random.Shared.NextBytes(payload);
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            // Dispose without EndAppend - Auto-Abort
        }

        // Assert - 文件长度不变（Zero I/O，HeadLen reservation 阻塞了 flush）
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    /// <summary>Zero I/O Abort（带 Reservation）：BeginAppend → ReserveSpan → 写数据 → Commit → Dispose → 验证文件无变化。</summary>
    [Fact]
    public void AutoAbort_WithReservation_ZeroIO() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧确保文件有初始内容
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);

        // 记录 Dispose 前的文件长度
        long originalLength = new FileInfo(path).Length;

        // Act - BeginAppend → ReserveSpan → 写数据 → Commit → Dispose
        using (var builder = file.BeginAppend()) {
            // 预留 4 字节
            var reservedSpan = builder.PayloadAndMeta.ReserveSpan(4, out var token, tag: "length");

            // 写入数据
            byte[] data = new byte[50];
            Random.Shared.NextBytes(data);
            var span = builder.PayloadAndMeta.GetSpan(data.Length);
            data.CopyTo(span);
            builder.PayloadAndMeta.Advance(data.Length);

            // 回填并 Commit
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(reservedSpan, data.Length);
            builder.PayloadAndMeta.Commit(token);

            // Dispose without EndAppend - Auto-Abort
        }

        // Assert - 文件长度不变（Zero I/O）
        Assert.Equal(originalLength, new FileInfo(path).Length);
    }

    /// <summary>Abort 后可继续 Append：Dispose 后 Append 成功。</summary>
    [Fact]
    public void AutoAbort_ThenAppend_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act - BeginAppend → 写数据 → Dispose（Auto-Abort）
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(10);
            span.Fill(0xAA);
            builder.PayloadAndMeta.Advance(10);
            // Dispose without EndAppend
        }

        // Act - Abort 后 Append
        byte[] payload = [0x11, 0x22, 0x33];
        var result = file.Append(0x5678, payload);

        // Assert
        Assert.True(result.IsSuccess);
        var ptr = result.Value!;
        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x5678u, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>Abort 后可继续 BeginAppend：Dispose 后 BeginAppend 成功。</summary>
    [Fact]
    public void AutoAbort_ThenBeginAppend_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act - BeginAppend → 写数据 → Dispose（Auto-Abort）
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(10);
            span.Fill(0xBB);
            builder.PayloadAndMeta.Advance(10);
            // Dispose without EndAppend
        }

        // Act - Abort 后 BeginAppend
        SizedPtr ptr;
        using (var builder = file.BeginAppend()) {
            byte[] payload = [0x44, 0x55, 0x66];
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            ptr = builder.EndAppend(0x9ABC);
        }

        // Assert
        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x9ABCu, frame.Tag);
        Assert.Equal(new byte[] { 0x44, 0x55, 0x66 }, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>Abort 后 TailOffset 不变：验证 file.TailOffset 与 Abort 前相同。</summary>
    [Fact]
    public void AutoAbort_TailOffsetUnchanged() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧确保有初始 TailOffset
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);
        long originalTailOffset = file.TailOffset;

        // Act - BeginAppend → 写数据 → Dispose（Auto-Abort）
        using (var builder = file.BeginAppend()) {
            byte[] payload = new byte[200];
            Random.Shared.NextBytes(payload);
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            // Dispose without EndAppend
        }

        // Assert - TailOffset 不变
        Assert.Equal(originalTailOffset, file.TailOffset);
    }

    // ========== Task 7.9: 单 Builder 约束与 Append 互斥测试 ==========
    // 规范引用：@[S-RBF-BUILDER-SINGLE-OPEN]

    /// <summary>重复 BeginAppend：BeginAppend → BeginAppend → 抛出 InvalidOperationException。</summary>
    [Fact]
    public void BeginAppend_DuplicateCall_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder1 = file.BeginAppend();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => file.BeginAppend());
        Assert.Contains("active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dispose 后可 BeginAppend：BeginAppend → Dispose → BeginAppend → 成功。</summary>
    [Fact]
    public void BeginAppend_AfterDispose_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一个 Builder - Dispose
        using (var builder1 = file.BeginAppend()) {
            // 不提交，直接 Dispose
        }

        // Act - 第二个 Builder
        SizedPtr ptr;
        using (var builder2 = file.BeginAppend()) {
            byte[] payload = [0x77, 0x88];
            var span = builder2.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder2.PayloadAndMeta.Advance(payload.Length);
            ptr = builder2.EndAppend(0xDEAD);
        }

        // Assert
        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0xDEADu, frame.Tag);
    }

    /// <summary>EndAppend 后可 BeginAppend：BeginAppend → EndAppend → BeginAppend → 成功。</summary>
    [Fact]
    public void BeginAppend_AfterEndAppend_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一个 Builder - EndAppend
        using (var builder1 = file.BeginAppend()) {
            var span = builder1.PayloadAndMeta.GetSpan(4);
            span.Fill(0x11);
            builder1.PayloadAndMeta.Advance(4);
            builder1.EndAppend(0x1111);
        }

        // Act - 第二个 Builder
        SizedPtr ptr;
        using (var builder2 = file.BeginAppend()) {
            byte[] payload = [0x99, 0xAA, 0xBB];
            var span = builder2.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder2.PayloadAndMeta.Advance(payload.Length);
            ptr = builder2.EndAppend(0x2222);
        }

        // Assert
        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x2222u, frame.Tag);
        Assert.Equal(new byte[] { 0x99, 0xAA, 0xBB }, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>Builder 期间 Append 抛异常：BeginAppend → Append → 抛出 InvalidOperationException。</summary>
    [Fact]
    public void Append_DuringActiveBuilder_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => file.Append(0x1234, [0x01]));
        Assert.Contains("active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Dispose 后可 Append：BeginAppend → Dispose → Append → 成功。</summary>
    [Fact]
    public void Append_AfterDispose_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // BeginAppend → Dispose
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(10);
            span.Fill(0xCC);
            builder.PayloadAndMeta.Advance(10);
            // Dispose without EndAppend
        }

        // Act - Append
        byte[] payload = [0xDD, 0xEE, 0xFF];
        var result = file.Append(0x3456, payload);

        // Assert
        Assert.True(result.IsSuccess);
        var readResult = file.ReadPooledFrame(result.Value!);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x3456u, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>EndAppend 后可 Append：BeginAppend → EndAppend → Append → 成功。</summary>
    [Fact]
    public void Append_AfterEndAppend_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // BeginAppend → EndAppend
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(5);
            span.Fill(0x22);
            builder.PayloadAndMeta.Advance(5);
            builder.EndAppend(0x4444);
        }

        // Act - Append
        byte[] payload = [0x10, 0x20, 0x30, 0x40];
        var result = file.Append(0x5555, payload);

        // Assert
        Assert.True(result.IsSuccess);
        var readResult = file.ReadPooledFrame(result.Value!);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x5555u, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }
}
