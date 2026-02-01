using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfFile.DurableFlush 单元测试。</summary>
/// <remarks>
/// 规范引用：
/// - Task 8.3: DurableFlush 单元测试
/// - @[S-RBF-DURABLEFLUSH-DURABILIZE-COMMITTED-ONLY]
/// </remarks>
public class RbfDurableFlushTests : IDisposable {
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

    /// <summary>正常调用：CreateNew → Append → DurableFlush → 不抛异常。</summary>
    [Fact]
    public void DurableFlush_AfterAppend_NoException() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        uint tag = 0x12345678;

        // Act & Assert - 不抛异常
        using var file = RbfFile.CreateNew(path);
        var result = file.Append(tag, payload);
        Assert.True(result.IsSuccess);

        file.DurableFlush(); // 应不抛异常
    }

    /// <summary>空文件：CreateNew → DurableFlush → 不抛异常。</summary>
    [Fact]
    public void DurableFlush_OnEmptyFile_NoException() {
        // Arrange
        var path = GetTempFilePath();

        // Act & Assert - 不抛异常
        using var file = RbfFile.CreateNew(path);

        file.DurableFlush(); // 空文件（只有 HeaderFence）也应不抛异常
    }

    /// <summary>多次调用：DurableFlush → DurableFlush → 幂等，不抛异常。</summary>
    [Fact]
    public void DurableFlush_CalledMultipleTimes_Idempotent() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0x01, 0x02, 0x03];

        // Act & Assert - 多次调用均不抛异常
        using var file = RbfFile.CreateNew(path);
        file.Append(0x1234, payload);

        file.DurableFlush();
        file.DurableFlush();
        file.DurableFlush(); // 三次调用，均应成功
    }

    // ========== 异常路径测试 ==========

    /// <summary>Disposed 后调用：Dispose → DurableFlush → 抛 ObjectDisposedException。</summary>
    [Fact]
    public void DurableFlush_AfterDispose_ThrowsObjectDisposedException() {
        // Arrange
        var path = GetTempFilePath();
        var file = RbfFile.CreateNew(path);
        file.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => file.DurableFlush());
    }

    // ========== Builder 期间测试 ==========

    /// <summary>Builder 期间调用：BeginAppend → DurableFlush → 不抛异常（允许）。</summary>
    /// <remarks>
    /// 根据 Decision 8.C：DurableFlush 允许在 active builder 期间调用。
    /// 语义：只 flush "已提交写入"，未提交的 Builder 数据不受影响。
    /// </remarks>
    [Fact]
    public void DurableFlush_DuringActiveBuilder_NoException() {
        // Arrange
        var path = GetTempFilePath();
        byte[] payload = [0xAA, 0xBB, 0xCC];

        using var file = RbfFile.CreateNew(path);

        // 先写入一帧，确保有已提交数据
        file.Append(0x1111, payload);

        // Act - 在 active builder 期间调用 DurableFlush
        using (var builder = file.BeginAppend()) {
            // 写入一些数据但不提交
            var span = builder.PayloadAndMeta.GetSpan(10);
            span.Fill(0xDD);
            builder.PayloadAndMeta.Advance(10);

            // DurableFlush 应成功（只 flush 已提交的帧）
            file.DurableFlush();

            // Builder 可以继续正常工作
            builder.EndAppend(0x2222);
        }

        // 验证两帧都可读
        int frameCount = 0;
        foreach (var _ in file.ScanReverse()) {
            frameCount++;
        }
        Assert.Equal(2, frameCount);
    }
}
