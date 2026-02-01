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

    /// <summary>
    /// 验证 Reset 后 CRC 计算正确性：第二轮写入的 CRC 应基于新数据，不受旧数据影响。
    /// </summary>
    [Fact]
    public void Reset_ClearsChunks_CrcComputationCorrect() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 第一轮写入
        _ = writer.ReserveSpan(4, out var token1, "head");
        byte[] data1 = [1, 2, 3, 4, 5, 6, 7, 8];
        data1.CopyTo(writer.GetSpan(data1.Length));
        writer.Advance(data1.Length);
        var crc1 = writer.GetCrcSinceReservationEnd(token1);

        writer.Reset();

        // 第二轮写入（不同数据）
        _ = writer.ReserveSpan(4, out var token2, "head");
        byte[] data2 = [9, 9, 9, 9];
        data2.CopyTo(writer.GetSpan(data2.Length));
        writer.Advance(data2.Length);
        var crc2 = writer.GetCrcSinceReservationEnd(token2);

        // Act & Assert：CRC 应基于新数据计算，不受旧数据影响
        Assert.NotEqual(crc1, crc2);

        // 验证 crc2 是对 [9, 9, 9, 9] 的正确 CRC
        var expectedCrc2 = Hashing.RollingCrc.CrcForward([9, 9, 9, 9]);
        Assert.Equal(expectedCrc2, crc2);
    }

    /// <summary>
    /// 验证 Reset 切换 sink 后，数据写入新 sink。
    /// </summary>
    [Fact]
    public void Reset_WithNewSink_UsesNewSink() {
        // Arrange
        var sink1 = new TestHelpers.CollectingWriter();
        var sink2 = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink1);

        // 写入到 sink1
        _ = writer.ReserveSpan(4, out var token1);
        byte[] data1 = [1, 2, 3];
        data1.CopyTo(writer.GetSpan(data1.Length));
        writer.Advance(data1.Length);
        writer.Commit(token1);

        Assert.Equal(7, sink1.Data().Length); // 4 (reservation) + 3 (payload)

        // Reset 切换到 sink2
        writer.Reset(sink2);

        // 写入到 sink2
        _ = writer.ReserveSpan(4, out var token2);
        byte[] data2 = [4, 5, 6, 7, 8];
        data2.CopyTo(writer.GetSpan(data2.Length));
        writer.Advance(data2.Length);
        writer.Commit(token2);

        // Assert
        Assert.Equal(7, sink1.Data().Length);  // sink1 数据不变
        Assert.Equal(9, sink2.Data().Length);  // 4 (reservation) + 5 (payload)
    }

    /// <summary>
    /// 验证 Reset(null) 保持当前 sink。
    /// </summary>
    [Fact]
    public void Reset_WithNullSink_KeepsCurrentSink() {
        // Arrange
        var sink = new TestHelpers.CollectingWriter();
        using var writer = new SinkReservableWriter(sink);

        // 写入第一批数据
        _ = writer.ReserveSpan(4, out var token1);
        byte[] data1 = [1, 2, 3];
        data1.CopyTo(writer.GetSpan(data1.Length));
        writer.Advance(data1.Length);
        writer.Commit(token1);

        int lengthAfterFirst = sink.Data().Length;

        // Reset with null (keep current sink)
        writer.Reset(null);

        // 写入第二批数据
        _ = writer.ReserveSpan(4, out var token2);
        byte[] data2 = [4, 5];
        data2.CopyTo(writer.GetSpan(data2.Length));
        writer.Advance(data2.Length);
        writer.Commit(token2);

        // Assert：数据继续写入同一个 sink
        Assert.Equal(lengthAfterFirst + 4 + 2, sink.Data().Length);
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
