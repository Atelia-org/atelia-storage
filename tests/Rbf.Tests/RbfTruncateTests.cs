using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfFile.Truncate 单元测试。</summary>
/// <remarks>
/// 规范引用：
/// - Task 8.4: Truncate 单元测试
/// - @[S-RBF-TRUNCATE-REQUIRES-NONNEGATIVE-4B-ALIGNED]
/// </remarks>
public class RbfTruncateTests : IDisposable {
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

    // ========== 参数校验测试 ==========

    /// <summary>1. 负数：Truncate(-1) → 抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void Truncate_NegativeLength_ThrowsArgumentOutOfRangeException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => file.Truncate(-1));
        Assert.Equal("newLengthBytes", ex.ParamName);
    }

    /// <summary>2. 非 4B 对齐（1B）：Truncate(5) → 抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void Truncate_NotAligned_1B_ThrowsArgumentOutOfRangeException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => file.Truncate(5));
        Assert.Equal("newLengthBytes", ex.ParamName);
        Assert.Contains("4-byte aligned", ex.Message);
    }

    /// <summary>3. 非 4B 对齐（2B）：Truncate(6) → 抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void Truncate_NotAligned_2B_ThrowsArgumentOutOfRangeException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => file.Truncate(6));
        Assert.Equal("newLengthBytes", ex.ParamName);
        Assert.Contains("4-byte aligned", ex.Message);
    }

    /// <summary>4. 非 4B 对齐（3B）：Truncate(7) → 抛 ArgumentOutOfRangeException。</summary>
    [Fact]
    public void Truncate_NotAligned_3B_ThrowsArgumentOutOfRangeException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => file.Truncate(7));
        Assert.Equal("newLengthBytes", ex.ParamName);
        Assert.Contains("4-byte aligned", ex.Message);
    }

    /// <summary>5. 零长度：Truncate(0) → 成功（0 是 4B 对齐的）。</summary>
    [Fact]
    public void Truncate_ZeroLength_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧确保文件有内容
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);

        // Act
        file.Truncate(0);

        // Assert
        Assert.Equal(0, file.TailOffset);
        Assert.Equal(0, new FileInfo(path).Length);
    }

    // ========== 状态检查测试 ==========

    /// <summary>6. Disposed 后调用：Dispose → Truncate → 抛 ObjectDisposedException。</summary>
    [Fact]
    public void Truncate_AfterDispose_ThrowsObjectDisposedException() {
        // Arrange
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);
        file.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => file.Truncate(4));
    }

    /// <summary>7. Builder 期间调用：BeginAppend → Truncate → 抛 InvalidOperationException。</summary>
    [Fact]
    public void Truncate_DuringActiveBuilder_ThrowsInvalidOperationException() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);
        using var builder = file.BeginAppend();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => file.Truncate(4));
        Assert.Contains("builder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>8. Builder Dispose 后调用：BeginAppend → Dispose → Truncate → 成功。</summary>
    [Fact]
    public void Truncate_AfterBuilderDispose_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 先写一帧
        var result = file.Append(0x1234, [0x01, 0x02, 0x03]);
        Assert.True(result.IsSuccess);

        // 开始 Builder 然后 Dispose
        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(10);
            span.Fill(0xAA);
            builder.PayloadAndMeta.Advance(10);
            // Dispose without EndAppend
        }

        // Act - Builder 已释放后可以 Truncate
        file.Truncate(RbfLayout.FenceSize); // 截断到 HeaderFence

        // Assert
        Assert.Equal(RbfLayout.FenceSize, file.TailOffset);
        Assert.Equal(RbfLayout.FenceSize, new FileInfo(path).Length);
    }

    // ========== 功能验证测试 ==========

    /// <summary>9. 截断到 HeaderFence：Append → Truncate(4) → TailOffset == 4 → 文件长度 == 4。</summary>
    [Fact]
    public void Truncate_ToHeaderFence_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入一帧
        var result = file.Append(0x1234, [0x01, 0x02, 0x03, 0x04]);
        Assert.True(result.IsSuccess);

        // 记录写入后的文件长度（应该大于 4）
        long originalLength = new FileInfo(path).Length;
        Assert.True(originalLength > RbfLayout.FenceSize);

        // Act - 截断到 HeaderFence
        file.Truncate(RbfLayout.FenceSize);

        // Assert
        Assert.Equal(RbfLayout.FenceSize, file.TailOffset);
        Assert.Equal(RbfLayout.FenceSize, new FileInfo(path).Length);
    }

    /// <summary>10. 截断到中间帧：Append 3 帧 → Truncate(帧2末尾) → TailOffset 正确 → 文件长度正确。</summary>
    [Fact]
    public void Truncate_ToMiddleFrame_Success() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入 3 帧
        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] payload3 = [0x21, 0x22, 0x23];

        var result1 = file.Append(0x1111, payload1);
        Assert.True(result1.IsSuccess);
        var ptr1 = result1.Value!;

        var result2 = file.Append(0x2222, payload2);
        Assert.True(result2.IsSuccess);
        var ptr2 = result2.Value!;

        var result3 = file.Append(0x3333, payload3);
        Assert.True(result3.IsSuccess);

        // 计算帧2末尾位置（帧2 Ticket.Offset + Ticket.Length + FenceSize）
        long frame2End = ptr2.Offset + ptr2.Length + RbfLayout.FenceSize;

        // Act - 截断到帧2末尾
        file.Truncate(frame2End);

        // Assert
        Assert.Equal(frame2End, file.TailOffset);
        Assert.Equal(frame2End, new FileInfo(path).Length);

        // 验证帧1和帧2仍可读
        var readResult1 = file.ReadPooledFrame(ptr1);
        Assert.True(readResult1.IsSuccess);
        using (var frame = readResult1.Value!) {
            Assert.Equal(0x1111u, frame.Tag);
            Assert.Equal(payload1, frame.PayloadAndMeta.ToArray());
        }

        var readResult2 = file.ReadPooledFrame(ptr2);
        Assert.True(readResult2.IsSuccess);
        using (var frame = readResult2.Value!) {
            Assert.Equal(0x2222u, frame.Tag);
            Assert.Equal(payload2, frame.PayloadAndMeta.ToArray());
        }
    }

    /// <summary>11. 截断后可继续 Append：Truncate → Append → ScanReverse 只看到新帧。</summary>
    [Fact]
    public void Truncate_ThenAppend_OnlyNewFrameVisible() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入一帧然后截断
        var result1 = file.Append(0x1111, [0x01, 0x02, 0x03]);
        Assert.True(result1.IsSuccess);

        file.Truncate(RbfLayout.FenceSize); // 截断到 HeaderFence

        // Act - 写入新帧
        byte[] newPayload = [0xAA, 0xBB, 0xCC, 0xDD];
        var result2 = file.Append(0x2222, newPayload);
        Assert.True(result2.IsSuccess);

        // Assert - ScanReverse 只看到新帧
        var frames = new List<(uint tag, byte[] payload)>();
        foreach (var info in file.ScanReverse()) {
            var readResult = info.ReadPooledFrame();
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
            frames.Add((frame.Tag, frame.PayloadAndMeta.ToArray()));
        }

        Assert.Single(frames);
        Assert.Equal(0x2222u, frames[0].tag);
        Assert.Equal(newPayload, frames[0].payload);
    }

    /// <summary>12. 截断后 ScanReverse：Append 3 帧 → Truncate(帧1末尾) → ScanReverse 只看到帧1。</summary>
    [Fact]
    public void Truncate_ThenScanReverse_OnlyRemainingFramesVisible() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入 3 帧
        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0x11, 0x12, 0x13, 0x14];
        byte[] payload3 = [0x21, 0x22, 0x23, 0x24];

        var result1 = file.Append(0x1111, payload1);
        Assert.True(result1.IsSuccess);
        var ptr1 = result1.Value!;

        var result2 = file.Append(0x2222, payload2);
        Assert.True(result2.IsSuccess);

        var result3 = file.Append(0x3333, payload3);
        Assert.True(result3.IsSuccess);

        // 计算帧1末尾位置
        long frame1End = ptr1.Offset + ptr1.Length + RbfLayout.FenceSize;

        // Act - 截断到帧1末尾
        file.Truncate(frame1End);

        // Assert - ScanReverse 只看到帧1
        var frames = new List<(uint tag, byte[] payload)>();
        foreach (var info in file.ScanReverse()) {
            var readResult = info.ReadPooledFrame();
            Assert.True(readResult.IsSuccess);
            using var frame = readResult.Value!;
            frames.Add((frame.Tag, frame.PayloadAndMeta.ToArray()));
        }

        Assert.Single(frames);
        Assert.Equal(0x1111u, frames[0].tag);
        Assert.Equal(payload1, frames[0].payload);
    }

    // ========== 集成测试（恢复场景） - Task 8.5 ==========

    /// <summary>
    /// 恢复场景 1: DurableFlush + Truncate 恢复。
    /// Append 3 帧 → DurableFlush → Truncate(帧2末尾) → DurableFlush → 重新 OpenExisting → ScanReverse 只看到帧1、帧2。
    /// </summary>
    [Fact]
    public void Recovery_DurableFlushAndTruncate_ReopensWithCorrectFrames() {
        // Arrange
        var path = GetTempFilePath();

        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] payload3 = [0x21, 0x22, 0x23, 0x24];

        SizedPtr ptr2;
        long frame2End;

        // Phase 1: 写入 3 帧 → DurableFlush → Truncate → DurableFlush
        using (var file = RbfFile.CreateNew(path)) {
            var result1 = file.Append(0x1111, payload1);
            Assert.True(result1.IsSuccess);

            var result2 = file.Append(0x2222, payload2);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;

            var result3 = file.Append(0x3333, payload3);
            Assert.True(result3.IsSuccess);

            file.DurableFlush();

            // 计算帧2末尾位置
            frame2End = ptr2.Offset + ptr2.Length + RbfLayout.FenceSize;
            file.Truncate(frame2End);

            file.DurableFlush();
        }

        // Phase 2: 重新打开并验证
        using (var file = RbfFile.OpenExisting(path)) {
            Assert.Equal(frame2End, file.TailOffset);

            // ScanReverse 应只看到帧1、帧2
            var frames = new List<(uint tag, byte[] payload)>();
            foreach (var info in file.ScanReverse()) {
                var readResult = info.ReadPooledFrame();
                Assert.True(readResult.IsSuccess);
                using var frame = readResult.Value!;
                frames.Add((frame.Tag, frame.PayloadAndMeta.ToArray()));
            }

            // 逆序扫描：先帧2后帧1
            Assert.Equal(2, frames.Count);
            Assert.Equal(0x2222u, frames[0].tag);
            Assert.Equal(payload2, frames[0].payload);
            Assert.Equal(0x1111u, frames[1].tag);
            Assert.Equal(payload1, frames[1].payload);
        }
    }

    /// <summary>
    /// 恢复场景 2: Truncate 到 Fence 位置。
    /// Append 帧 → Truncate(HeaderFence 后，即 4) → 验证 TailOffset == 4。
    /// </summary>
    [Fact]
    public void Recovery_TruncateToHeaderFence_TailOffsetEquals4() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入一帧
        var result = file.Append(0x1234, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        Assert.True(result.IsSuccess);
        Assert.True(file.TailOffset > RbfLayout.FenceSize);

        // Act - Truncate 到 HeaderFence 位置（4 字节）
        file.Truncate(RbfLayout.FenceSize);

        // Assert
        Assert.Equal(RbfLayout.FenceSize, file.TailOffset);
        Assert.Equal(4, file.TailOffset); // 显式验证等于 4
        Assert.Equal(4, new FileInfo(path).Length);

        // ScanReverse 应该无帧
        var frameCount = 0;
        foreach (var _ in file.ScanReverse()) {
            frameCount++;
        }
        Assert.Equal(0, frameCount);
    }

    /// <summary>
    /// 恢复场景 3: Truncate 后 BeginAppend。
    /// Append → Truncate(4) → BeginAppend → 写入数据 → EndAppend → ReadFrame 成功。
    /// </summary>
    [Fact]
    public void Recovery_TruncateThenBeginAppend_NewFrameReadable() {
        // Arrange
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        // 写入一帧
        var result1 = file.Append(0x1111, [0x01, 0x02, 0x03, 0x04]);
        Assert.True(result1.IsSuccess);

        // Truncate 到 HeaderFence
        file.Truncate(RbfLayout.FenceSize);
        Assert.Equal(4, file.TailOffset);

        // Act - 使用 BeginAppend 写入新帧
        byte[] newPayload = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22];
        uint newTag = 0x9999;
        SizedPtr newPtr;

        using (var builder = file.BeginAppend()) {
            var span = builder.PayloadAndMeta.GetSpan(newPayload.Length);
            newPayload.CopyTo(span);
            builder.PayloadAndMeta.Advance(newPayload.Length);
            newPtr = builder.EndAppend(newTag);
        }

        // Assert - ReadFrame 成功
        var readResult = file.ReadPooledFrame(newPtr);
        Assert.True(readResult.IsSuccess);
        using (var frame = readResult.Value!) {
            Assert.Equal(newTag, frame.Tag);
            Assert.Equal(newPayload, frame.PayloadAndMeta.ToArray());
        }

        // 验证 ScanReverse 只看到新帧
        var frames = new List<(uint tag, byte[] payload)>();
        foreach (var info in file.ScanReverse()) {
            var scanReadResult = info.ReadPooledFrame();
            Assert.True(scanReadResult.IsSuccess);
            using var f = scanReadResult.Value!;
            frames.Add((f.Tag, f.PayloadAndMeta.ToArray()));
        }

        Assert.Single(frames);
        Assert.Equal(newTag, frames[0].tag);
        Assert.Equal(newPayload, frames[0].payload);
    }
}
