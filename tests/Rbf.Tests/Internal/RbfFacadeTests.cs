using Atelia.Data;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

/// <summary>RbfFileImpl (Facade) 状态管理测试。</summary>
/// <remarks>
/// 职责：验证 Facade 正确维护状态、正确转发返回值。
/// 不验证：帧格式细节（已在 RbfRawOpsTests 中覆盖）。
/// </remarks>
public class RbfFacadeTests : IDisposable {
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

    // ========== 测试用例 ==========

    /// <summary>验证 Append 正确更新 TailOffset 并返回正确的 SizedPtr。</summary>
    [Fact]
    public void Append_UpdatesTailOffset() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];
        uint tag = 0x12345678;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload);
            Assert.True(result.IsSuccess);
            ptr = result.Value!;
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = new FrameLayout(payload.Length).FrameLength;

        // SizedPtr 指向 HeaderFence(4) 之后的位置
        Assert.Equal(RbfLayout.FirstFrameOffset, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);

        // TailOffset = HeaderFence(4) + Frame + Fence(4)
        Assert.Equal(RbfLayout.FenceSize + expectedHeadLen + RbfLayout.FenceSize, tailOffset);
    }

    /// <summary>验证多帧追加：偏移序列正确。</summary>
    [Fact]
    public void Append_MultiFrame_OffsetsCorrect() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload1 = [0xAA, 0xBB]; // 2 字节
        byte[] payload2 = [0xCC, 0xDD, 0xEE, 0xFF, 0x11]; // 5 字节
        uint tag1 = 0x11111111;
        uint tag2 = 0x22222222;

        // Act
        SizedPtr ptr1, ptr2;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result1 = rbf.Append(tag1, payload1);
            Assert.True(result1.IsSuccess);
            ptr1 = result1.Value!;
            var result2 = rbf.Append(tag2, payload2);
            Assert.True(result2.IsSuccess);
            ptr2 = result2.Value!;
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int headLen1 = new FrameLayout(payload1.Length).FrameLength;
        int headLen2 = new FrameLayout(payload2.Length).FrameLength;

        // 第一帧位置
        Assert.Equal(RbfLayout.FirstFrameOffset, ptr1.Offset); // HeaderFence 后
        Assert.Equal(headLen1, ptr1.Length);

        // 第二帧位置
        // secondFrameOffset = HeaderFence(4) + headLen1 + Fence(4)
        long expectedOffset2 = RbfLayout.FenceSize + headLen1 + RbfLayout.FenceSize;
        Assert.Equal(expectedOffset2, ptr2.Offset);
        Assert.Equal(headLen2, ptr2.Length);

        // TailOffset
        long expectedTailOffset = RbfLayout.FenceSize + headLen1 + RbfLayout.FenceSize + headLen2 + RbfLayout.FenceSize; // HeaderFence + F1 + Fence + F2 + Fence
        Assert.Equal(expectedTailOffset, tailOffset);
    }

    /// <summary>验证空 payload 场景的状态更新（v0.40 格式）。</summary>
    [Fact]
    public void Append_EmptyPayload_UpdatesTailOffset() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = []; // 空 payload
        uint tag = 0xDEADBEEF;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload);
            Assert.True(result.IsSuccess);
            ptr = result.Value!;
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = new FrameLayout(0).FrameLength;
        Assert.Equal(24, expectedHeadLen); // v0.40 最小帧长度为 24

        Assert.Equal(RbfLayout.FirstFrameOffset, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);
        Assert.Equal(RbfLayout.FenceSize + expectedHeadLen + RbfLayout.FenceSize, tailOffset);
    }

    /// <summary>验证大 payload 场景的状态更新。</summary>
    [Fact]
    public void Append_LargePayload_UpdatesTailOffset() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = new byte[2048]; // 2KB payload，超过 512B 阈值
        uint tag = 0xCAFEBABE;

        // Act
        SizedPtr ptr;
        long tailOffset;
        using (var rbf = RbfFile.CreateNew(path)) {
            var result = rbf.Append(tag, payload);
            Assert.True(result.IsSuccess);
            ptr = result.Value!;
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = new FrameLayout(payload.Length).FrameLength;

        Assert.Equal(RbfLayout.FirstFrameOffset, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);
        Assert.Equal(RbfLayout.FenceSize + expectedHeadLen + RbfLayout.FenceSize, tailOffset);
    }

    // ========== ReadPooledFrame 集成测试 ==========

    /// <summary>验证 Append 后 ReadPooledFrame 能正确读回帧数据（闭环测试）。</summary>
    [Fact]
    public void ReadPooledFrame_AfterAppend_ReturnsCorrectFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        uint tag = 0x87654321;

        // Act & Assert
        using (var rbf = RbfFile.CreateNew(path)) {
            var appendResult = rbf.Append(tag, payload);
            Assert.True(appendResult.IsSuccess);
            var ptr = appendResult.Value!;

            // ReadPooledFrame 应该能正确读取刚写入的帧
            var result = rbf.ReadPooledFrame(ptr);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            using var frame = result.Value;
            Assert.Equal(tag, frame.Tag);
            Assert.Equal(payload, frame.PayloadAndMeta.ToArray());
            Assert.False(frame.IsTombstone);
            Assert.Equal(ptr.Offset, frame.Ticket.Offset);
            Assert.Equal(ptr.Length, frame.Ticket.Length);
        }
    }

    /// <summary>验证 ReadPooledFrame 不会改变 TailOffset。</summary>
    [Fact]
    public void ReadPooledFrame_DoesNotChangeTailOffset() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x11, 0x22, 0x33];
        uint tag = 0xABCDEF00;

        // Act
        using var rbf = RbfFile.CreateNew(path);
        var appendResult = rbf.Append(tag, payload);
        Assert.True(appendResult.IsSuccess);
        var ptr = appendResult.Value!;
        long tailOffsetBefore = rbf.TailOffset;

        // 执行 ReadPooledFrame
        var result = rbf.ReadPooledFrame(ptr);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        using var frame = result.Value;  // 确保 Dispose

        long tailOffsetAfter = rbf.TailOffset;

        // Assert - TailOffset 应保持不变
        Assert.Equal(tailOffsetBefore, tailOffsetAfter);
    }
}
