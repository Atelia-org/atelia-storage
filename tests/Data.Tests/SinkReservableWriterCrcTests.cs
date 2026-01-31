using System;
using Atelia.Data.Hashing;
using Xunit;

namespace Atelia.Data.Tests;

/// <summary>
/// SinkReservableWriter.GetCrcSinceReservationEnd 测试
/// </summary>
public class SinkReservableWriterCrcTests {
    #region 正确性测试

    [Fact]
    public void GetCrcSinceReservationEnd_SinglePendingReservation_ReturnsCorrectCrc() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 预留 4 字节（模拟 HeadLen）
        _ = writer.ReserveSpan(4, out int token, tag: "HeadLen");

        // 写入 payload
        var payload = "Hello, World!"u8;
        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);

        // Act
        uint actualCrc = writer.GetCrcSinceReservationEnd(token);

        // Assert：期望 CRC 等于 payload 的 CRC
        uint expectedCrc = RollingCrc.CrcForward(payload);
        Assert.Equal(expectedCrc, actualCrc);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_EmptyPayload_ReturnsEmptyCrc() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 预留 4 字节
        _ = writer.ReserveSpan(4, out int token);

        // 不写入任何 payload

        // Act
        uint actualCrc = writer.GetCrcSinceReservationEnd(token);

        // Assert：空 payload 的 CRC（initValue ^ finalXor）
        uint expectedCrc = RollingCrc.DefaultInitValue ^ RollingCrc.DefaultFinalXor;
        Assert.Equal(expectedCrc, actualCrc);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_LargePayloadAcrossChunks_ReturnsCorrectCrc() {
        // Arrange: 使用默认 chunk 大小，写入大 payload 以跨越多个 chunk
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 预留 4 字节
        _ = writer.ReserveSpan(4, out int token);

        // 写入多批 payload 以跨越多个 chunk（默认 minChunk=1024）
        byte[] fullPayload = new byte[5000];
        Random.Shared.NextBytes(fullPayload);

        int written = 0;
        while (written < fullPayload.Length) {
            int chunkSize = Math.Min(500, fullPayload.Length - written);
            var span = writer.GetSpan(chunkSize);
            fullPayload.AsSpan(written, chunkSize).CopyTo(span);
            writer.Advance(chunkSize);
            written += chunkSize;
        }

        // Act
        uint actualCrc = writer.GetCrcSinceReservationEnd(token);

        // Assert
        uint expectedCrc = RollingCrc.CrcForward(fullPayload);
        Assert.Equal(expectedCrc, actualCrc);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_CustomInitAndFinalXor_ReturnsCorrectCrc() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out int token);

        var payload = "Test"u8;
        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);

        uint customInit = 0x12345678u;
        uint customXor = 0xABCDEF01u;

        // Act
        uint actualCrc = writer.GetCrcSinceReservationEnd(token, customInit, customXor);

        // Assert
        uint expectedCrc = RollingCrc.CrcForward(customInit, payload) ^ customXor;
        Assert.Equal(expectedCrc, actualCrc);
    }

    #endregion

    #region 失败测试

    [Fact]
    public void GetCrcSinceReservationEnd_HasUnadvancedSpan_ThrowsInvalidOperationException() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out int token);

        // GetSpan 但不 Advance
        _ = writer.GetSpan(10);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.GetCrcSinceReservationEnd(token)
        );
        Assert.Contains("Advance", ex.Message);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_MultiplePendingReservations_ThrowsInvalidOperationException() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 创建两个 pending reservation
        _ = writer.ReserveSpan(4, out int token1);
        _ = writer.ReserveSpan(4, out int token2);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.GetCrcSinceReservationEnd(token1)
        );
        Assert.Contains("exactly 1 pending reservation", ex.Message);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_InvalidToken_ThrowsInvalidOperationException() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out _);

        int invalidToken = 9999;

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.GetCrcSinceReservationEnd(invalidToken)
        );
        Assert.Contains("Invalid or already committed", ex.Message);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_CommittedToken_ThrowsInvalidOperationException() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out int token);
        writer.Commit(token);

        // 需要创建新的 reservation 以便调用不因"0 pending"失败
        _ = writer.ReserveSpan(4, out _);

        // Act & Assert：用已 commit 的 token 调用
        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.GetCrcSinceReservationEnd(token)
        );
        Assert.Contains("Invalid or already committed", ex.Message);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_AfterReset_OldTokenInvalid() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out int oldToken);

        // Reset
        writer.Reset();

        // 创建新的 reservation
        _ = writer.ReserveSpan(4, out _);

        // Act & Assert：旧 token 应该失效
        var ex = Assert.Throws<InvalidOperationException>(
            () => writer.GetCrcSinceReservationEnd(oldToken)
        );
        Assert.Contains("Invalid or already committed", ex.Message);
    }

    [Fact]
    public void GetCrcSinceReservationEnd_Disposed_ThrowsObjectDisposedException() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        var writer = new SinkReservableWriter(sink);
        _ = writer.ReserveSpan(4, out int token);
        writer.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(
            () => writer.GetCrcSinceReservationEnd(token)
        );
    }

    #endregion

    #region 状态不变性测试

    [Fact]
    public void GetCrcSinceReservationEnd_DoesNotModifyWriterState() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        _ = writer.ReserveSpan(4, out int token);

        var payload = "Test"u8;
        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);

        // 记录调用前状态
        long writtenBefore = writer.WrittenLength;
        long pushedBefore = writer.PushedLength;
        int pendingCountBefore = writer.PendingReservationCount;

        // Act
        _ = writer.GetCrcSinceReservationEnd(token);

        // Assert：状态应该不变
        Assert.Equal(writtenBefore, writer.WrittenLength);
        Assert.Equal(pushedBefore, writer.PushedLength);
        Assert.Equal(pendingCountBefore, writer.PendingReservationCount);
    }

    #endregion
}
