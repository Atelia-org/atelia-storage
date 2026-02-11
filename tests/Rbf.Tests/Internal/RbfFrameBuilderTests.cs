using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

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
                var endResult = builder.EndAppend(tag);
                Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
                ptr = endResult.Value;
            }

            // 验证帧可读
            var readResult = file.ReadPooledFrame(ptr);
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
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
                var endResult = builder.EndAppend(tag);
                Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
                ptr = endResult.Value;
            }

            // 验证最小帧
            var readResult = file.ReadPooledFrame(ptr);
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
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
                var result = builder.EndAppend(tag, tailMeta.Length);
                Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
                ptr = result.Value;
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
                var endResult = builder.EndAppend(tag);
                Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
                ptr = endResult.Value;
            }

            // 验证帧可读
            var readResult = file.ReadPooledFrame(ptr);
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
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

    /// <summary>TailMetaLength 超过 MaxTailMetaLength：应返回失败结果。</summary>
    /// <remarks>
    /// 覆盖 RbfFrameBuilder.EndAppend 中的约束：
    /// <code>
    /// if (tailMetaLength &gt; FrameLayout.MaxTailMetaLength) {
    ///     return Failure(...)
    /// }
    /// </code>
    /// MaxTailMetaLength = 65535 (ushort.MaxValue)
    /// 需要先写入足够多的数据，避免被 tailMetaLength &gt; payloadAndMetaLength 分支先拦截。
    /// </remarks>
    [Fact]
    public void EndAppend_TailMetaLengthExceedsMaxTailMetaLength_ReturnsFailure() {
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
        var result = builder.EndAppend(0x1234, tailMetaLength: invalidTailMetaLength);
        Assert.False(result.IsSuccess);
        Assert.Contains("MaxTailMetaLength", result.Error!.Message);
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
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
            ptr = result.Value;
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
            var result = builder.EndAppend(0x1234);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
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
            var endResult = builder.EndAppend(tag);
            Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
            ptr = endResult.Value;
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

            var endResult = builder.EndAppend(tag, tailMeta.Length);
            Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
            ptr = endResult.Value;
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
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend frame {i} failed: {result.Error}");
            var ptr = result.Value;

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
            var result2 = builder.EndAppend(0x2222);
            Assert.True(result2.IsSuccess, $"EndAppend failed: {result2.Error}");
            ptrs.Add(result2.Value);
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
            var result = builder.EndAppend(0x9ABC);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
            ptr = result.Value;
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
            var result = builder2.EndAppend(0xDEAD);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
            ptr = result.Value;
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
            var result1 = builder1.EndAppend(0x1111);
            Assert.True(result1.IsSuccess, $"First EndAppend failed: {result1.Error}");
        }

        // Act - 第二个 Builder
        SizedPtr ptr;
        using (var builder2 = file.BeginAppend()) {
            byte[] payload = [0x99, 0xAA, 0xBB];
            var span = builder2.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder2.PayloadAndMeta.Advance(payload.Length);
            var result2 = builder2.EndAppend(0x2222);
            Assert.True(result2.IsSuccess, $"Second EndAppend failed: {result2.Error}");
            ptr = result2.Value;
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

    // ========== Task 7.10: 与 ScanReverse 集成测试 ==========
    // 规范引用：验证 Builder 写入的帧可被 ScanReverse 正确扫描

    /// <summary>混合写入：Append + BeginAppend/EndAppend 交替 → ScanReverse 返回正确顺序。</summary>
    [Fact]
    public void ScanReverse_MixedAppendAndBuilder_ReturnsCorrectOrder() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload1 = [0x01, 0x02, 0x03];
        byte[] payload2 = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] payload3 = [0x11, 0x22];
        byte[] payload4 = [0x77, 0x88, 0x99];

        using var file = RbfFile.CreateNew(path);

        // Frame 1: 使用 Append
        var result1 = file.Append(0x1111, payload1);
        Assert.True(result1.IsSuccess);

        // Frame 2: 使用 Builder
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload2.Length);
            payload2.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload2.Length);
            var result2 = builder.EndAppend(0x2222);
            Assert.True(result2.IsSuccess, $"EndAppend Frame2 failed: {result2.Error}");
        }

        // Frame 3: 使用 Append
        var result3 = file.Append(0x3333, payload3);
        Assert.True(result3.IsSuccess);

        // Frame 4: 使用 Builder
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload4.Length);
            payload4.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload4.Length);
            var result4 = builder.EndAppend(0x4444);
            Assert.True(result4.IsSuccess, $"EndAppend Frame4 failed: {result4.Error}");
        }

        // Act: ScanReverse 应按逆序返回（Frame4 → Frame3 → Frame2 → Frame1）
        var frames = new List<(uint tag, int payloadLen)>();
        foreach (var info in file.ScanReverse()) {
            frames.Add((info.Tag, info.PayloadLength));
        }

        // Assert
        Assert.Equal(4, frames.Count);
        Assert.Equal((0x4444u, payload4.Length), frames[0]); // Frame 4 (Builder)
        Assert.Equal((0x3333u, payload3.Length), frames[1]); // Frame 3 (Append)
        Assert.Equal((0x2222u, payload2.Length), frames[2]); // Frame 2 (Builder)
        Assert.Equal((0x1111u, payload1.Length), frames[3]); // Frame 1 (Append)
    }

    /// <summary>大帧写入：Builder 写入 &gt; 4KB payload → ScanReverse + ReadFrame 验证完整性。</summary>
    [Fact]
    public void ScanReverse_LargePayloadFromBuilder_VerifyIntegrity() {
        // Arrange
        var path = GetTempFilePath();
        byte[] largePayload = new byte[5 * 1024]; // 5KB > 4KB 边界
        Random.Shared.NextBytes(largePayload);
        uint tag = 0xB16F8A3E; // "Big Frame"

        using var file = RbfFile.CreateNew(path);

        // 使用 Builder 写入大帧
        SizedPtr ptr;
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(largePayload.Length);
            largePayload.CopyTo(span);
            builder.PayloadAndMeta.Advance(largePayload.Length);
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
            ptr = result.Value;
        }

        // Act: ScanReverse 获取帧信息
        RbfFrameInfo? scannedInfo = null;
        foreach (var info in file.ScanReverse()) {
            scannedInfo = info;
            break; // 只有一帧
        }

        Assert.NotNull(scannedInfo);
        var frameInfo = scannedInfo.Value;

        // Assert: 帧信息正确
        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(largePayload.Length, frameInfo.PayloadLength);
        Assert.Equal(ptr.Offset, frameInfo.Ticket.Offset);
        Assert.Equal(ptr.Length, frameInfo.Ticket.Length);

        // 通过 ReadFrame 验证完整内容
        var readResult = frameInfo.ReadPooledFrame();
        Assert.True(readResult.IsSuccess, $"ReadFrame failed: {readResult.Error}");
        using var frame = readResult.Value!;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(largePayload, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>TailMeta 读取：Builder 带 TailMeta → ScanReverse → ReadTailMeta 验证内容。</summary>
    [Fact]
    public void ScanReverse_WithTailMeta_ReadTailMetaVerifyContent() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] tailMeta = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        uint tag = 0xAE7A7A6; // "Meta Tag"

        using var file = RbfFile.CreateNew(path);

        // 使用 Builder 写入带 TailMeta 的帧
        using (var builder = file.BeginAppend()) {
            // 写入 payload
            var payloadSpan = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(payloadSpan);
            builder.PayloadAndMeta.Advance(payload.Length);

            // 写入 tailMeta
            var tailMetaSpan = builder.PayloadAndMeta.GetSpan(tailMeta.Length);
            tailMeta.CopyTo(tailMetaSpan);
            builder.PayloadAndMeta.Advance(tailMeta.Length);

            var result = builder.EndAppend(tag, tailMeta.Length);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
        }

        // Act: ScanReverse 获取帧信息
        RbfFrameInfo? scannedInfo = null;
        foreach (var info in file.ScanReverse()) {
            scannedInfo = info;
            break;
        }

        Assert.NotNull(scannedInfo);
        var frameInfo = scannedInfo.Value;

        // 验证帧信息
        Assert.Equal(tag, frameInfo.Tag);
        Assert.Equal(tailMeta.Length, frameInfo.TailMetaLength);

        // 使用 ReadTailMeta 验证内容
        var tailMetaResult = frameInfo.ReadPooledTailMeta();
        Assert.True(tailMetaResult.IsSuccess);
        using var pooledTailMeta = tailMetaResult.Value!;
        Assert.Equal(tailMeta, pooledTailMeta.TailMeta.ToArray());

        // 同时验证完整帧内容
        var frameResult = frameInfo.ReadPooledFrame();
        Assert.True(frameResult.IsSuccess);
        using var frame = frameResult.Value!;
        Assert.Equal(tag, frame.Tag);
        // PayloadAndMeta = payload + tailMeta
        Assert.Equal(payload.Length + tailMeta.Length, frame.PayloadAndMeta.Length);
        Assert.Equal(payload, frame.PayloadAndMeta.Slice(0, payload.Length).ToArray());
        Assert.Equal(tailMeta, frame.PayloadAndMeta.Slice(payload.Length).ToArray());
    }

    /// <summary>多帧序列：连续 5 次 BeginAppend/EndAppend → ScanReverse 验证逆序正确。</summary>
    [Fact]
    public void ScanReverse_MultipleBuilderFrames_VerifyReverseOrder() {
        // Arrange
        var path = GetTempFilePath();
        var expectedFrames = new List<(uint tag, byte[] payload)>();

        using var file = RbfFile.CreateNew(path);

        // 连续 5 次 BeginAppend/EndAppend
        for (int i = 1; i <= 5; i++) {
            byte[] payload = new byte[10 + i * 5]; // 不同大小的 payload
            Random.Shared.NextBytes(payload);
            uint tag = (uint)(0x1000 * i);

            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend frame {i} failed: {result.Error}");

            expectedFrames.Add((tag, payload));
        }

        // Act: ScanReverse
        var scannedFrames = new List<(uint tag, int payloadLen)>();
        foreach (var info in file.ScanReverse()) {
            scannedFrames.Add((info.Tag, info.PayloadLength));
        }

        // Assert: 验证逆序（Frame 5 → 4 → 3 → 2 → 1）
        Assert.Equal(5, scannedFrames.Count);
        for (int i = 0; i < 5; i++) {
            int expectedIndex = 4 - i; // 逆序
            Assert.Equal(expectedFrames[expectedIndex].tag, scannedFrames[i].tag);
            Assert.Equal(expectedFrames[expectedIndex].payload.Length, scannedFrames[i].payloadLen);
        }

        // 验证每帧内容
        int frameIndex = 4; // 从最后一帧开始（逆序）
        foreach (var info in file.ScanReverse()) {
            var readResult = info.ReadPooledFrame();
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
            Assert.Equal(expectedFrames[frameIndex].tag, frame.Tag);
            Assert.Equal(expectedFrames[frameIndex].payload, frame.PayloadAndMeta.ToArray());
            frameIndex--;
        }
    }

    /// <summary>ScanReverse 迭代器终止状态：Builder 帧正常扫描后 TerminationError 为 null。</summary>
    [Fact]
    public void ScanReverse_BuilderFrames_TerminationErrorIsNull() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入几帧
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(4);
            span.Fill(0xAA);
            builder.PayloadAndMeta.Advance(4);
            var r1 = builder.EndAppend(0x1111);
            Assert.True(r1.IsSuccess);
        }

        file.Append(0x2222, [0xBB, 0xCC]);

        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(8);
            span.Fill(0xDD);
            builder.PayloadAndMeta.Advance(8);
            var r3 = builder.EndAppend(0x3333);
            Assert.True(r3.IsSuccess);
        }

        // Act
        var enumerator = file.ScanReverse().GetEnumerator();
        int count = 0;
        while (enumerator.MoveNext()) {
            count++;
        }

        // Assert
        Assert.Equal(3, count);
        Assert.Null(enumerator.TerminationError);
    }

    /// <summary>ScanReverse Ticket 可用于直接 ReadFrame：验证 Builder 帧的 Ticket 有效。</summary>
    [Fact]
    public void ScanReverse_BuilderFrame_TicketUsableForReadFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE];
        uint tag = 0x12345678;

        using var file = RbfFile.CreateNew(path);

        // 使用 Builder 写入
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            var endResult = builder.EndAppend(tag);
            Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
        }

        // Act: 通过 ScanReverse 获取 Ticket，然后用 Ticket 读取帧
        SizedPtr ticket = default;
        foreach (var info in file.ScanReverse()) {
            ticket = info.Ticket;
            break;
        }

        // 使用 Ticket 直接调用 ReadFrame
        byte[] buffer = new byte[ticket.Length];
        var readResult = file.ReadFrame(ticket, buffer);

        // Assert
        Assert.True(readResult.IsSuccess);
        var frame = readResult.Value;
        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    // ========== Phase 3: Builder 连续使用测试 ==========
    // 规范引用：wish/W-0009-rbf/stage/10/task-phase3-builder-reuse.md

    /// <summary>连续多次 BeginAppend → Commit：验证数据独立。</summary>
    [Fact]
    public void BuilderReuse_MultipleCommits_NoDataLeakage() {
        // Arrange
        var path = GetTempFilePath();
        var frames = new List<(SizedPtr ptr, byte[] payload, uint tag)>();

        using var file = RbfFile.CreateNew(path);

        // Act: 连续 5 次 BeginAppend → EndAppend
        for (int i = 0; i < 5; i++) {
            byte[] payload = new byte[10 + i * 5]; // 不同大小
            Random.Shared.NextBytes(payload);
            uint tag = (uint)(0x1000 + i);

            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend frame {i} failed: {result.Error}");
            var ptr = result.Value;

            frames.Add((ptr, payload, tag));
        }

        // Assert: 每帧可读且内容正确（无数据泄漏）
        for (int i = 0; i < frames.Count; i++) {
            var (ptr, expectedPayload, expectedTag) = frames[i];
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess, $"Frame {i} read failed: {result.Error}");
            using var frame = result.Value!;
            Assert.Equal(expectedTag, frame.Tag);
            Assert.Equal(expectedPayload, frame.PayloadAndMeta.ToArray());
        }
    }

    /// <summary>BeginAppend → Abort → BeginAppend → Commit：验证 Abort 后可继续写入。</summary>
    [Fact]
    public void BuilderReuse_AfterAbort_NoDataLeakage() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一帧：写入后 abort
        byte[] abortedPayload = [0xAB, 0x0A, 0x7E, 0xD0];
        using (var builder1 = file.BeginAppend()) {
            var span = builder1.PayloadAndMeta.GetSpan(abortedPayload.Length);
            abortedPayload.CopyTo(span);
            builder1.PayloadAndMeta.Advance(abortedPayload.Length);
            // 不调用 EndAppend，直接 Dispose → Abort
        }

        // 第二帧：正常写入
        byte[] committedPayload = [0xC0, 0xAA, 0x17, 0xED];
        SizedPtr ptr;
        using (var builder2 = file.BeginAppend()) {
            var span = builder2.PayloadAndMeta.GetSpan(committedPayload.Length);
            committedPayload.CopyTo(span);
            builder2.PayloadAndMeta.Advance(committedPayload.Length);
            var result = builder2.EndAppend(0x2222);
            Assert.True(result.IsSuccess, $"EndAppend failed: {result.Error}");
            ptr = result.Value;
        }

        // Assert: 只有一帧，且内容是第二次写入的
        var frameCount = 0;
        foreach (var _ in file.ScanReverse()) {
            frameCount++;
        }
        Assert.Equal(1, frameCount);

        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x2222u, frame.Tag);
        Assert.Equal(committedPayload, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>交替 Commit 和 Abort：验证状态隔离正确。</summary>
    [Fact]
    public void BuilderReuse_AlternatingCommitAndAbort_StateIsolation() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var committedFrames = new List<(SizedPtr ptr, byte[] payload)>();

        // Act: 交替 Commit 和 Abort
        for (int i = 0; i < 6; i++) {
            byte[] payload = new byte[8 + i];
            Random.Shared.NextBytes(payload);
            uint tag = (uint)(0x1000 + i);

            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);

            if (i % 2 == 0) {
                // 偶数帧 Commit
                var result = builder.EndAppend(tag);
                Assert.True(result.IsSuccess, $"EndAppend frame {i} failed: {result.Error}");
                var ptr = result.Value;
                committedFrames.Add((ptr, payload));
            }
            // 奇数帧 Abort（Dispose 不调用 EndAppend）
        }

        // Assert: 只有 3 帧（0, 2, 4）被提交
        var frameCount = 0;
        foreach (var _ in file.ScanReverse()) {
            frameCount++;
        }
        Assert.Equal(3, frameCount);

        // 验证每帧内容
        for (int i = 0; i < committedFrames.Count; i++) {
            var (ptr, expectedPayload) = committedFrames[i];
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess, $"Committed frame {i} read failed");
            using var frame = result.Value!;
            Assert.Equal(expectedPayload, frame.PayloadAndMeta.ToArray());
        }
    }

    /// <summary>大数据量连续写入：连续写入大帧后仍能正常写入。</summary>
    [Fact]
    public void BuilderReuse_LargePayloads_NoMemoryLeakOrCorruption() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var frames = new List<(SizedPtr ptr, byte[] payload)>();

        // Act: 写入 3 个大帧（各 50KB）
        for (int i = 0; i < 3; i++) {
            byte[] payload = new byte[50 * 1024]; // 50KB
            Random.Shared.NextBytes(payload);
            uint tag = (uint)(0xB160 + i);

            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder.PayloadAndMeta.Advance(payload.Length);
            var result = builder.EndAppend(tag);
            Assert.True(result.IsSuccess, $"EndAppend large frame {i} failed: {result.Error}");
            var ptr = result.Value;

            frames.Add((ptr, payload));
        }

        // Assert: 每帧内容完整正确
        for (int i = 0; i < frames.Count; i++) {
            var (ptr, expectedPayload) = frames[i];
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess, $"Large frame {i} read failed");
            using var frame = result.Value!;
            Assert.Equal(expectedPayload, frame.PayloadAndMeta.ToArray());
        }
    }

    /// <summary>带 Reservation 的连续写入：验证 Reservation 状态正确重置。</summary>
    [Fact]
    public void BuilderReuse_WithReservation_StateResetCorrectly() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var frames = new List<(SizedPtr ptr, int length, byte[] data)>();

        // Act: 连续 3 次带 Reservation 的写入
        for (int i = 0; i < 3; i++) {
            using var builder = file.BeginAppend();

            // 预留 4 字节用于写入长度
            var reservedSpan = builder.PayloadAndMeta.ReserveSpan(4, out var token, tag: "length");

            // 写入实际数据
            byte[] data = new byte[20 + i * 10];
            Random.Shared.NextBytes(data);
            var dataSpan = builder.PayloadAndMeta.GetSpan(data.Length);
            data.CopyTo(dataSpan);
            builder.PayloadAndMeta.Advance(data.Length);

            // 回填预留的长度
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(reservedSpan, data.Length);
            builder.PayloadAndMeta.Commit(token);

            // 提交帧
            var endResult = builder.EndAppend((uint)(0x3000 + i));
            Assert.True(endResult.IsSuccess, $"EndAppend frame {i} failed: {endResult.Error}");
            var ptr = endResult.Value;
            frames.Add((ptr, data.Length, data));
        }

        // Assert: 每帧内容正确
        for (int i = 0; i < frames.Count; i++) {
            var (ptr, expectedLength, expectedData) = frames[i];
            var result = file.ReadPooledFrame(ptr);
            Assert.True(result.IsSuccess);
            using var frame = result.Value!;

            // 验证 payload 内容：4 字节长度 + 数据
            var payloadAndMeta = frame.PayloadAndMeta;
            Assert.Equal(4 + expectedLength, payloadAndMeta.Length);
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payloadAndMeta);
            Assert.Equal(expectedLength, length);
            Assert.Equal(expectedData, payloadAndMeta.Slice(4).ToArray());
        }
    }

    /// <summary>文件 Dispose 后 Builder 资源正确释放。</summary>
    [Fact]
    public void BuilderReuse_FileDispose_BuilderResourcesReleased() {
        // Arrange
        var path = GetTempFilePath();

        // Act
        using (var file = RbfFile.CreateNew(path)) {
            // 写几帧确保 BeginAppend 连续可用
            for (int i = 0; i < 3; i++) {
                using var builder = file.BeginAppend();
                var span = builder.PayloadAndMeta.GetSpan(4);
                span.Fill((byte)i);
                builder.PayloadAndMeta.Advance(4);
                var result = builder.EndAppend((uint)(0x4000 + i));
                Assert.True(result.IsSuccess, $"EndAppend frame {i} failed: {result.Error}");
            }
        }

        // Assert: 文件可以重新打开读取
        using var reopenedFile = RbfFile.OpenExisting(path);
        var frameCount = 0;
        foreach (var _ in reopenedFile.ScanReverse()) {
            frameCount++;
        }
        Assert.Equal(3, frameCount);
    }

    /// <summary>Abort 后未提交 Reservation 的情况。</summary>
    [Fact]
    public void BuilderReuse_AbortWithUncommittedReservation_NextBuildSucceeds() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一帧：创建 Reservation 但 Abort
        using (var builder1 = file.BeginAppend()) {
            _ = builder1.PayloadAndMeta.ReserveSpan(4, out var token, tag: "uncommitted");
            var span = builder1.PayloadAndMeta.GetSpan(10);
            span.Fill(0xAA);
            builder1.PayloadAndMeta.Advance(10);
            // Abort: 不 Commit reservation，不 EndAppend
        }

        // 第二帧：正常写入（验证 Reservation 状态已重置）
        byte[] payload = [0x11, 0x22, 0x33];
        SizedPtr ptr;
        using (var builder2 = file.BeginAppend()) {
            var span = builder2.PayloadAndMeta.GetSpan(payload.Length);
            payload.CopyTo(span);
            builder2.PayloadAndMeta.Advance(payload.Length);
            var endResult = builder2.EndAppend(0x5555);
            Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");
            ptr = endResult.Value;
        }

        // Assert
        var readResult = file.ReadPooledFrame(ptr);
        Assert.True(readResult.IsSuccess);
        using var frame = readResult.Value!;
        Assert.Equal(0x5555u, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    // ========== Task-04: 状态机转移矩阵与 epoch 防误用测试 ==========
    // 规范引用：Stage 11 Task-04 - 覆盖状态转移矩阵与 epoch 误用场景

    /// <summary>状态机转移：Idle → Building（BeginAppend）→ Idle（EndAppend 成功）。</summary>
    [Fact]
    public void StateMachine_Idle_BeginAppend_Building_EndAppend_Idle() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Idle: 可 Append
        var appendResult = file.Append(0x1111, [0x01]);
        Assert.True(appendResult.IsSuccess, "Idle state should allow Append");

        // Idle → Building: BeginAppend
        using var builder = file.BeginAppend();

        // Building: 不可再 BeginAppend
        Assert.Throws<InvalidOperationException>(() => file.BeginAppend());

        // Building: 不可 Append
        Assert.Throws<InvalidOperationException>(() => file.Append(0x2222, [0x02]));

        // Building → Idle: EndAppend 成功
        var endResult = builder.EndAppend(0x3333);
        Assert.True(endResult.IsSuccess);

        // 回到 Idle: 可 Append
        var append2 = file.Append(0x4444, [0x04]);
        Assert.True(append2.IsSuccess, "After EndAppend should be Idle, allowing Append");
    }

    /// <summary>状态机转移：Idle → Building（BeginAppend）→ Idle（Dispose/Abort）。</summary>
    [Fact]
    public void StateMachine_Idle_BeginAppend_Building_Dispose_Idle() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Idle → Building
        var builder = file.BeginAppend();

        // Building: 不可 Append
        Assert.Throws<InvalidOperationException>(() => file.Append(0x1111, [0x01]));

        // Building → Idle: Dispose（Auto-Abort）
        builder.Dispose();

        // 回到 Idle: 可 Append
        var appendResult = file.Append(0x2222, [0x02]);
        Assert.True(appendResult.IsSuccess, "After Dispose/Abort should be Idle");
    }

    /// <summary>状态机：连续状态切换（多次 Building → Idle）。</summary>
    [Fact]
    public void StateMachine_MultipleTransitions() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        for (int i = 0; i < 5; i++) {
            // Idle → Building
            using var builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(4);
            span.Fill((byte)i);
            builder.PayloadAndMeta.Advance(4);

            if (i % 2 == 0) {
                // Building → Idle via EndAppend
                var result = builder.EndAppend((uint)(0x1000 + i));
                Assert.True(result.IsSuccess);
            }
            // 奇数次：Building → Idle via Dispose
        }

        // 最终状态应为 Idle
        var finalAppend = file.Append(0xFFFF, [0xFF]);
        Assert.True(finalAppend.IsSuccess, "Final state should be Idle");
    }

    /// <summary>Epoch 防误用：旧 Builder 引用在新周期 EndAppend 返回 Failure。</summary>
    /// <remarks>
    /// Builder 为值类型，旧引用保存旧 epoch。
    /// 新 BeginAppend 后旧引用尝试 EndAppend，应因 epoch 不匹配而失败。
    /// </remarks>
    [Fact]
    public void Epoch_StaleBuilder_EndAppend_ReturnsFailure() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一周期：获取 Builder 并写入数据
        var builder1 = file.BeginAppend();
        var span1 = builder1.PayloadAndMeta.GetSpan(4);
        span1.Fill(0x11);
        builder1.PayloadAndMeta.Advance(4);

        // 不调用 Dispose，保留 builder1 引用
        // 但调用 EndAppend 成功提交（此时 file 回到 Idle）
        var result1 = builder1.EndAppend(0x1111);
        Assert.True(result1.IsSuccess, $"First EndAppend should succeed: {result1.Error}");

        // 第二周期：新的 Builder（新 epoch）
        var builder2 = file.BeginAppend();
        var span2 = builder2.PayloadAndMeta.GetSpan(4);
        span2.Fill(0x22);
        builder2.PayloadAndMeta.Advance(4);

        // Act: 尝试用"旧引用"提交（epoch 不匹配）
        var result2 = builder1.EndAppend(0x2222);
        Assert.False(result2.IsSuccess, "Stale builder should fail");
        Assert.Contains("epoch", result2.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Rbf.StateError", result2.Error.ErrorCode);

        // 当前 builder2 仍可成功提交
        var result3 = builder2.EndAppend(0x3333);
        Assert.True(result3.IsSuccess, $"Second EndAppend should succeed: {result3.Error}");
    }

    /// <summary>Epoch 防误用：旧 Builder Dispose 不影响新周期。</summary>
    /// <remarks>
    /// Builder 为值类型，旧引用 Dispose 应被忽略，不影响新周期。
    /// </remarks>
    [Fact]
    public void Epoch_StaleBuilder_Dispose_NoEffect() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 第一周期
        var builder1 = file.BeginAppend();
        var span1 = builder1.PayloadAndMeta.GetSpan(4);
        span1.Fill(0x11);
        builder1.PayloadAndMeta.Advance(4);
        var result1 = builder1.EndAppend(0x1111);
        Assert.True(result1.IsSuccess);

        // 第二周期
        var builder2 = file.BeginAppend();
        var span = builder2.PayloadAndMeta.GetSpan(4);
        span.Fill(0x33);
        builder2.PayloadAndMeta.Advance(4);

        // Act: 旧引用 Dispose（epoch 不匹配，应被忽略）
        builder1.Dispose();

        // Assert: builder2 不受影响，EndAppend 成功
        var result2 = builder2.EndAppend(0x3333);
        Assert.True(result2.IsSuccess, $"Builder2 EndAppend should succeed: {result2.Error}");
    }

    /// <summary>旧 writer 持有 + epoch 失效：旧 writer 调用应抛异常。</summary>
    [Fact]
    public void PayloadWriter_StaleEpoch_ThrowsOnUse() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var builder1 = file.BeginAppend();
        var writer1 = builder1.PayloadAndMeta;

        // 结束第一周期（提交或 Abort）
        var endResult = builder1.EndAppend(0x1111);
        Assert.True(endResult.IsSuccess, $"EndAppend failed: {endResult.Error}");

        // 开启新周期，epoch 递增
        using var builder2 = file.BeginAppend();

        // Act & Assert: 旧 writer 调用应失败（epoch 不匹配）
        var ex = Assert.Throws<InvalidOperationException>(() => writer1.GetSpan(1));
        Assert.Contains("epoch", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Cleanup: 让新周期正常结束
        var span2 = builder2.PayloadAndMeta.GetSpan(1);
        span2[0] = 0x42;
        builder2.PayloadAndMeta.Advance(1);
        var result2 = builder2.EndAppend(0x2222);
        Assert.True(result2.IsSuccess, $"Builder2 EndAppend should succeed: {result2.Error}");
    }

    // ========== Task-04: EndAppend 失败返回 RbfArgumentError / RbfStateError 测试 ==========

    /// <summary>EndAppend 失败：负数 tailMetaLength 返回 RbfArgumentError。</summary>
    [Fact]
    public void EndAppend_NegativeTailMetaLength_ReturnsRbfArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // 写入数据
        var span = builder.PayloadAndMeta.GetSpan(10);
        span.Fill(0x42);
        builder.PayloadAndMeta.Advance(10);

        // Act
        var result = builder.EndAppend(0x1234, tailMetaLength: -1);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("tailMetaLength", result.Error!.Message);
        Assert.Equal("Rbf.ArgumentError", result.Error.ErrorCode);
    }

    /// <summary>EndAppend 失败：重复调用返回 RbfStateError。</summary>
    [Fact]
    public void EndAppend_Duplicate_ReturnsRbfStateError() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var builder = file.BeginAppend();

        // 第一次成功
        var result1 = builder.EndAppend(0x1234);
        Assert.True(result1.IsSuccess);

        // Act: 第二次调用
        var result2 = builder.EndAppend(0x5678);

        // Assert
        Assert.False(result2.IsSuccess);
        Assert.Contains("EndAppend", result2.Error!.Message);
        Assert.Equal("Rbf.StateError", result2.Error.ErrorCode);

        builder.Dispose();
    }

    /// <summary>EndAppend 失败：Dispose 后调用返回 RbfStateError。</summary>
    [Fact]
    public void EndAppend_AfterDispose_ReturnsRbfStateError() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        var builder = file.BeginAppend();
        builder.Dispose();

        // Act
        var result = builder.EndAppend(0x1234);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("disposed", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Rbf.StateError", result.Error.ErrorCode);
    }

    /// <summary>EndAppend 失败：未提交 Reservation 返回 RbfStateError。</summary>
    [Fact]
    public void EndAppend_PendingReservation_ReturnsRbfStateError() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // 创建 Reservation 但不 Commit
        _ = builder.PayloadAndMeta.ReserveSpan(4, out _, tag: "pending");
        var span = builder.PayloadAndMeta.GetSpan(10);
        span.Fill(0x55);
        builder.PayloadAndMeta.Advance(10);

        // Act
        var result = builder.EndAppend(0x1234);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("reservation", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Rbf.StateError", result.Error.ErrorCode);
    }

    /// <summary>EndAppend 失败：tailMetaLength 超出 payloadAndMetaLength 返回 RbfArgumentError。</summary>
    [Fact]
    public void EndAppend_TailMetaExceedsPayload_ReturnsRbfArgumentError() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // 写入 5 字节
        var span = builder.PayloadAndMeta.GetSpan(5);
        span.Fill(0x66);
        builder.PayloadAndMeta.Advance(5);

        // Act: tailMetaLength = 10 > 5
        var result = builder.EndAppend(0x1234, tailMetaLength: 10);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("tailMetaLength", result.Error!.Message);
        Assert.Equal("Rbf.ArgumentError", result.Error.ErrorCode);
    }

    /// <summary>EndAppend 失败：File 已 Dispose 返回 RbfStateError。</summary>
    /// <remarks>
    /// Builder 为值类型；此处覆盖 File Dispose 后的状态拒绝路径。
    /// </remarks>
    [Fact]
    public void EndAppend_FileDisposed_ReturnsRbfStateError() {
        // Arrange
        var path = GetTempFilePath();
        RbfFrameBuilder builder = default;

        using (var file = RbfFile.CreateNew(path)) {
            builder = file.BeginAppend();
            var span = builder.PayloadAndMeta.GetSpan(4);
            span.Fill(0x77);
            builder.PayloadAndMeta.Advance(4);
        }  // File Dispose

        // Act: File 已 Dispose，Builder 尝试 EndAppend
        var result = builder.EndAppend(0x1111);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Rbf.StateError", result.Error!.ErrorCode);

        builder.Dispose();
    }
}
