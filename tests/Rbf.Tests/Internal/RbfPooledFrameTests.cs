using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>RbfPooledFrame 和 ReadPooledFrame 测试（v0.40 格式）。</summary>
/// <remarks>
/// 测试内容：
/// - ReadPooledFrame 成功路径
/// - RbfPooledFrame.Dispose 归还 buffer
/// - 幂等 Dispose（多次调用安全）
/// - struct 复制后 Dispose 安全
/// </remarks>
public class RbfPooledFrameTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string GetTempFilePath() {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch { /* 忽略清理错误 */ }
        }
    }

    // ========== 辅助方法 ==========

    /// <summary>构造一个有效帧的字节数组（v0.40 格式）。</summary>
    /// <remarks>
    /// v0.40 布局：[HeadLen][Payload][TailMeta][Padding][PayloadCrc][TrailerCodeword]
    /// </remarks>
    private static byte[] CreateValidFrameBytes(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        var layout = new FrameLayout(payload.Length);
        int frameLen = layout.FrameLength;

        byte[] frame = new byte[frameLen];
        Span<byte> span = frame;

        // 1. HeadLen (offset 0)
        BinaryPrimitives.WriteUInt32LittleEndian(span[..FrameLayout.HeadLenSize], (uint)frameLen);

        // 2. Payload (offset 4)
        payload.CopyTo(span.Slice(FrameLayout.PayloadOffset, payload.Length));

        // 3. Padding（清零）
        if (layout.PaddingLength > 0) {
            span.Slice(layout.PaddingOffset, layout.PaddingLength).Clear();
        }

        // 4. PayloadCrc（覆盖 Payload + TailMeta + Padding）
        var payloadCrcCoverage = span.Slice(FrameLayout.PayloadCrcCoverageStart, layout.PayloadCrcCoverageLength);
        uint payloadCrc = RollingCrc.CrcForward(payloadCrcCoverage);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(layout.PayloadCrcOffset, FrameLayout.PayloadCrcSize), payloadCrc);

        // 5. TrailerCodeword
        layout.FillTrailer(span.Slice(layout.TrailerCodewordOffset, TrailerCodewordHelper.Size), tag, isTombstone);

        return frame;
    }

    /// <summary>构造带 HeaderFence + Frame + Fence 的完整文件内容。</summary>
    private static byte[] CreateValidFileWithFrame(uint tag, ReadOnlySpan<byte> payload, bool isTombstone = false) {
        byte[] frameBytes = CreateValidFrameBytes(tag, payload, isTombstone);
        int totalLen = RbfLayout.FenceSize + frameBytes.Length + RbfLayout.FenceSize;
        byte[] file = new byte[totalLen];

        RbfLayout.Fence.CopyTo(file.AsSpan(0, RbfLayout.FenceSize));
        frameBytes.CopyTo(file.AsSpan(RbfLayout.FenceSize));
        RbfLayout.Fence.CopyTo(file.AsSpan(RbfLayout.FenceSize + frameBytes.Length, RbfLayout.FenceSize));

        return file;
    }

    // ========== 测试用例 ==========

    /// <summary>验证 ReadPooledFrame 成功时返回带 Owner 的帧。</summary>
    [Fact]
    public void ReadPooledFrame_Success_ReturnsFrameWithOwner() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        uint tag = 0x12345678;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        // Act
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;

        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
        Assert.False(frame.IsTombstone);
        Assert.Equal(ptr.Offset, frame.Ticket.Offset);
        Assert.Equal(ptr.Length, frame.Ticket.Length);
        // class 版本无需校验 internal Owner，Dispose 行为由测试覆盖
    }

    /// <summary>验证 RbfPooledFrame.Dispose 归还 buffer（无异常）。</summary>
    [Fact]
    public void RbfPooledFrame_Dispose_ReturnsBuffer() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB, 0xCC];
        uint tag = 0xDEADBEEF;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        var result = rbfFile.ReadPooledFrame(ptr);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var frame = result.Value;

        // 保存 Payload 数据用于对比
        byte[] payloadCopy = frame.PayloadAndMeta.ToArray();

        // Act: Dispose 应该归还 buffer
        frame!.Dispose();

        // Assert: Dispose 成功完成（无异常）
        // 注：Dispose 后 Payload 变为 dangling，不再访问
        Assert.Equal(payload, payloadCopy); // 验证之前数据正确
    }

    /// <summary>验证 RbfPooledFrame 多次 Dispose 安全（幂等）。</summary>
    [Fact]
    public void RbfPooledFrame_DoubleDispose_Safe() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x11, 0x22, 0x33, 0x44];
        uint tag = 0xCAFEBABE;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        var result = rbfFile.ReadPooledFrame(ptr);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var frame = result.Value;

        // Act: 多次 Dispose
        frame.Dispose();
        frame.Dispose();
        frame.Dispose();

        // Assert: 无异常（幂等性）
    }

    /// <summary>验证 struct 复制后双方 Dispose 都安全。</summary>
    [Fact]
    public void RbfPooledFrame_CopyThenBothDispose_Safe() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x55, 0x66, 0x77, 0x88];
        uint tag = 0xFACEFACE;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        var result = rbfFile.ReadPooledFrame(ptr);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var frame1 = result.Value;

        // 复制 struct（共享同一 Owner）
        var frame2 = frame1;

        // Act: 两个副本都 Dispose
        frame1.Dispose();
        frame2.Dispose();

        // Assert: 无异常（PooledBufferOwner 保证幂等释放）
    }

    /// <summary>验证 ReadPooledFrame 失败时 buffer 已自动归还。</summary>
    [Fact]
    public void ReadPooledFrame_Failure_BufferReturned() {
        // Arrange
        var path = GetTempFilePath();
        byte[] fileContent = CreateValidFileWithFrame(0x12345678, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        // 尝试从超出文件尾的位置读取（会导致短读错误）
        var ptr = SizedPtr.Create(1000, 20);

        // Act
        var result = RbfReadImpl.ReadPooledFrame(handle, ptr);

        // Assert: 失败，且无需调用 Dispose（buffer 已在内部归还）
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.IsType<RbfArgumentError>(result.Error);
        // 注：无法直接验证 buffer 已归还，但代码路径确保了这一点
    }

    /// <summary>验证 ReadPooledFrame 大 payload（触发真正的 ArrayPool 使用）。</summary>
    [Fact]
    public void ReadPooledFrame_LargePayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[8192]; // 8KB
        new Random(42).NextBytes(payload);
        uint tag = 0xB16F4A7E;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        // Act
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;

        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
    }

    /// <summary>验证 ReadPooledFrame 空 payload。</summary>
    [Fact]
    public void ReadPooledFrame_EmptyPayload_Succeeds() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [];
        uint tag = 0xE7737700;

        using var rbfFile = RbfFile.CreateNew(path);
        var appendResult = rbfFile.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;

        // Act
        var result = rbfFile.ReadPooledFrame(ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;

        Assert.Equal(tag, frame.Tag);
        Assert.Empty(frame.PayloadAndMeta.ToArray());
    }

    /// <summary>验证 ReadPooledFrame 墓碑帧解码正确。</summary>
    [Fact]
    public void ReadPooledFrame_Tombstone_DecodesCorrectly() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB, 0xCC];
        uint tag = 0x11223344;
        byte[] fileContent = CreateValidFileWithFrame(tag, payload, isTombstone: true);
        File.WriteAllBytes(path, fileContent);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);

        int frameLen = new FrameLayout(payload.Length).FrameLength;
        var ptr = SizedPtr.Create(RbfLayout.FenceSize, frameLen);

        // Act
        var result = RbfReadImpl.ReadPooledFrame(handle, ptr);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;

        Assert.Equal(tag, frame.Tag);
        Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
        Assert.True(frame.IsTombstone);
    }
}
