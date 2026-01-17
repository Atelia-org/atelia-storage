using Atelia.Data;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFileImpl (Facade) 状态管理测试。
/// </summary>
/// <remarks>
/// 职责：验证 Facade 正确维护状态、正确转发返回值。
/// 不验证：帧格式细节（已在 RbfRawOpsTests 中覆盖）。
/// </remarks>
public class RbfFacadeTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// 生成一个不存在的临时文件路径。
    /// </summary>
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

    /// <summary>
    /// 验证 Append 正确更新 TailOffset 并返回正确的 SizedPtr。
    /// </summary>
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
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = RbfConstants.ComputeFrameLen(payload.Length, out _);

        // SizedPtr 指向 Genesis(4) 之后的位置
        Assert.Equal(4L, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);

        // TailOffset = Genesis(4) + Frame + Fence(4)
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset);
    }

    /// <summary>
    /// 验证多帧追加：偏移序列正确。
    /// </summary>
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
            ptr1 = rbf.Append(tag1, payload1);
            ptr2 = rbf.Append(tag2, payload2);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int headLen1 = RbfConstants.ComputeFrameLen(payload1.Length, out _);
        int headLen2 = RbfConstants.ComputeFrameLen(payload2.Length, out _);

        // 第一帧位置
        Assert.Equal(4L, ptr1.Offset); // Genesis(4) 后
        Assert.Equal(headLen1, ptr1.Length);

        // 第二帧位置
        // secondFrameOffset = Genesis(4) + headLen1 + Fence(4)
        long expectedOffset2 = 4 + headLen1 + 4;
        Assert.Equal(expectedOffset2, ptr2.Offset);
        Assert.Equal(headLen2, ptr2.Length);

        // TailOffset
        long expectedTailOffset = 4 + headLen1 + 4 + headLen2 + 4; // Genesis + F1 + Fence + F2 + Fence
        Assert.Equal(expectedTailOffset, tailOffset);
    }

    /// <summary>
    /// 验证空 payload 场景的状态更新。
    /// </summary>
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
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = RbfConstants.ComputeFrameLen(0, out _);
        Assert.Equal(20, expectedHeadLen); // 验证计算

        Assert.Equal(4L, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset);
    }

    /// <summary>
    /// 验证大 payload 场景的状态更新。
    /// </summary>
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
            ptr = rbf.Append(tag, payload);
            tailOffset = rbf.TailOffset;
        }

        // Assert - 只验证状态和返回值
        int expectedHeadLen = RbfConstants.ComputeFrameLen(payload.Length, out _);

        Assert.Equal(4L, ptr.Offset);
        Assert.Equal(expectedHeadLen, ptr.Length);
        Assert.Equal(4 + expectedHeadLen + 4, tailOffset);
    }

    // ========== ReadPooledFrame 集成测试 ==========

    /// <summary>
    /// 验证 Append 后 ReadPooledFrame 能正确读回帧数据（闭环测试）。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_AfterAppend_ReturnsCorrectFrame() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        uint tag = 0x87654321;

        // Act & Assert
        using (var rbf = RbfFile.CreateNew(path)) {
            var ptr = rbf.Append(tag, payload);

            // ReadPooledFrame 应该能正确读取刚写入的帧
            var result = rbf.ReadPooledFrame(ptr);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            using var frame = result.Value;
            Assert.Equal(tag, frame.Tag);
            Assert.Equal(payload, frame.Payload.ToArray());
            Assert.False(frame.IsTombstone);
            Assert.Equal(ptr, frame.Ticket);
        }
    }

    /// <summary>
    /// 验证 ReadPooledFrame 不会改变 TailOffset。
    /// </summary>
    [Fact]
    public void ReadPooledFrame_DoesNotChangeTailOffset() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x11, 0x22, 0x33];
        uint tag = 0xABCDEF00;

        // Act
        using var rbf = RbfFile.CreateNew(path);
        var ptr = rbf.Append(tag, payload);
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
