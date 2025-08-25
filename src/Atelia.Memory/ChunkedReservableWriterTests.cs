using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Atelia.Memory.Tests;

/// <summary>
/// ChunkedReservableWriter的基础功能测试
/// </summary>
public class ChunkedReservableWriterTests
{
    /// <summary>
    /// 简单的内存缓冲区实现，用于测试
    /// </summary>
    private class TestBufferWriter : IBufferWriter<byte>
    {
        private readonly MemoryStream _stream = new();
        private int _position = 0;

        public void Advance(int count)
        {
            _position += count;
            if (_position > _stream.Length)
                _stream.SetLength(_position);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            int requiredSize = _position + Math.Max(sizeHint, 1024);
            if (_stream.Length < requiredSize)
                _stream.SetLength(requiredSize);
            return _stream.GetBuffer().AsMemory(_position, (int)_stream.Length - _position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            int requiredSize = _position + Math.Max(sizeHint, 1024);
            if (_stream.Length < requiredSize)
                _stream.SetLength(requiredSize);
            return _stream.GetBuffer().AsSpan(_position, (int)_stream.Length - _position);
        }

        public byte[] GetWrittenData()
        {
            var result = new byte[_position];
            Array.Copy(_stream.GetBuffer(), 0, result, 0, _position);
            return result;
        }
    }

    [Fact]
    public void BasicWriteTest()
    {
        // Arrange
        var innerWriter = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter);

        // Act
        var span = writer.GetSpan(10);
        "Hello"u8.CopyTo(span);
        writer.Advance(5);

        // Assert
        var result = innerWriter.GetWrittenData();
        Assert.Equal("Hello"u8.ToArray(), result);
    }

    [Fact]
    public void ReservationBasicTest()
    {
        // Arrange
        var innerWriter = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter);

        // Act - 写入一些数据，然后预留空间，再写入更多数据
        var span1 = writer.GetSpan(5);
        "Hello"u8.CopyTo(span1);
        writer.Advance(5);

        // 预留4字节空间用于长度字段
        var reservedSpan = writer.ReserveSpan(4, out int token, "len-field");

        var span2 = writer.GetSpan(5);
        "World"u8.CopyTo(span2);
        writer.Advance(5);

        // 此时innerWriter应该只有"Hello"，因为预留空间阻止了后续数据的flush
        var partialResult = innerWriter.GetWrittenData();
        Assert.Equal("Hello"u8.ToArray(), partialResult);

        // 回填预留空间（模拟写入长度）
        BitConverter.GetBytes(5).CopyTo(reservedSpan); // "World"的长度
        writer.Commit(token);

        // 现在所有数据都应该被flush
        var finalResult = innerWriter.GetWrittenData();
        var expected = new byte[14]; // "Hello" + 4字节长度 + "World"
        "Hello"u8.CopyTo(expected.AsSpan(0, 5));
        BitConverter.GetBytes(5).CopyTo(expected.AsSpan(5, 4));
        "World"u8.CopyTo(expected.AsSpan(9, 5));

        Assert.Equal(expected, finalResult);
    }

    [Fact]
    public void MultipleReservationsTest()
    {
        // Arrange
        var innerWriter = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter);

        // Act - 创建多个预留空间
        var span1 = writer.ReserveSpan(2, out int token1, "r1");
        var span2 = writer.ReserveSpan(2, out int token2, "r2");

        // 写入一些普通数据
        var normalSpan = writer.GetSpan(4);
        "Test"u8.CopyTo(normalSpan);
        writer.Advance(4);

        // 此时应该没有数据被flush，因为有未提交的预留空间
        Assert.Empty(innerWriter.GetWrittenData());

        // 按顺序提交预留空间
        span1[0] = 0x01;
        span1[1] = 0x02;
        writer.Commit(token1);

        span2[0] = 0x03;
        span2[1] = 0x04;
        writer.Commit(token2);

        // 现在所有数据都应该被flush
        var result = innerWriter.GetWrittenData();
        var expected = new byte[] { 0x01, 0x02, 0x03, 0x04 }.Concat("Test"u8.ToArray()).ToArray();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DisposeTest()
    {
        // Arrange
        var innerWriter = new TestBufferWriter();
        var writer = new ChunkedReservableWriter(innerWriter);

        // Act - 写入一些数据然后释放
        var span = writer.GetSpan(100);
        writer.Advance(50);
        writer.Dispose();

        // Assert - 应该能正常释放，不抛异常
        Assert.Throws<ObjectDisposedException>(() => writer.GetSpan(10));
    }

    [Fact]
    public void ResetTest()
    {
        // Arrange
        var innerWriter1 = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter1);

        // Act - 写入数据，重置，再用新的innerWriter写入
        writer.GetSpan(100);
        writer.Advance(50);

        // 验证第一次写入的数据存在
        var firstResult = innerWriter1.GetWrittenData();
        Assert.Equal(50, firstResult.Length);

        writer.Reset();

        // 重置后，使用新的innerWriter测试
        var innerWriter2 = new TestBufferWriter();
        using var writer2 = new ChunkedReservableWriter(innerWriter2);

        var span = writer2.GetSpan(10);
        "Reset"u8.CopyTo(span);
        writer2.Advance(5);

        // Assert - 新的writer应该只有新数据
        var result = innerWriter2.GetWrittenData();
        Assert.Equal("Reset"u8.ToArray(), result);
    }

    [Fact]
    public void PassthroughRestorationTest()
    {
        var innerWriter = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter);

        // 初始应为直通模式
        Assert.True(writer.IsPassthrough);

        // 先写入一些直通数据
        var preSpan = writer.GetSpan(5);
        "Hello"u8.CopyTo(preSpan);
        writer.Advance(5);

        // 建立 reservation 进入缓冲模式
        var reserved = writer.ReserveSpan(4, out int token, "block-A");
        Assert.False(writer.IsPassthrough);

        var tailSpan = writer.GetSpan(5);
        "World"u8.CopyTo(tailSpan);
        writer.Advance(5);

        // 此时预留阻塞，innerWriter 仅应包含 "Hello"
        Assert.Equal("Hello"u8.ToArray(), innerWriter.GetWrittenData());

        // 填充并提交 reservation，触发 flush + 回收 + 回退直通
        reserved[0] = 1; reserved[1] = 2; reserved[2] = 3; reserved[3] = 4;
        writer.Commit(token);
        Assert.True(writer.IsPassthrough); // 已回退

        // 再次直接写入（应直通不再缓存）
        var more = writer.GetSpan(3);
        "ABC"u8.CopyTo(more);
        writer.Advance(3);

        var data = innerWriter.GetWrittenData();
        // 期望顺序：Hello + reservation(1..4) + World + ABC
        var expected = new byte[5 + 4 + 5 + 3];
        "Hello"u8.CopyTo(expected.AsSpan(0,5));
        new byte[]{1,2,3,4}.CopyTo(expected.AsSpan(5,4));
        "World"u8.CopyTo(expected.AsSpan(9,5));
        "ABC"u8.CopyTo(expected.AsSpan(14,3));
        Assert.Equal(expected, data);
    }

    [Fact]
    public void PassthroughBufferedCycleMultipleTimes()
    {
        var innerWriter = new TestBufferWriter();
        using var writer = new ChunkedReservableWriter(innerWriter);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(writer.IsPassthrough);

            // 进入缓冲
            var res = writer.ReserveSpan(2, out int token, $"cycle-{cycle}");
            Assert.False(writer.IsPassthrough);
            res[0] = (byte)cycle; res[1] = (byte)(cycle + 1);
            writer.Commit(token);
            Assert.True(writer.IsPassthrough);

            // 直通写入一个标记字节
            var s = writer.GetSpan(1);
            s[0] = 0xFF;
            writer.Advance(1);
        }

        var bytes = innerWriter.GetWrittenData();
        // 每个 cycle：2 bytes reserved + 1 byte marker
        Assert.Equal(3 * 3, bytes.Length);
        // 简单结构验证：每第三字节应为 0xFF
        for (int i = 2; i < bytes.Length; i += 3)
            Assert.Equal(0xFF, bytes[i]);
    }
}
