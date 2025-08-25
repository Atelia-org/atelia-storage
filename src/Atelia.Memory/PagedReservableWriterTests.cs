using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Atelia.Memory.Tests;

/// <summary>
/// PagedReservableWriter的基础功能测试
/// </summary>
public class PagedReservableWriterTests
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
        using var writer = new PagedReservableWriter(innerWriter);

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
        using var writer = new PagedReservableWriter(innerWriter);

        // Act - 写入一些数据，然后预留空间，再写入更多数据
        var span1 = writer.GetSpan(5);
        "Hello"u8.CopyTo(span1);
        writer.Advance(5);

        // 预留4字节空间用于长度字段
        var reservedSpan = writer.ReserveSpan(4, out int token);

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
        using var writer = new PagedReservableWriter(innerWriter);

        // Act - 创建多个预留空间
        var span1 = writer.ReserveSpan(2, out int token1);
        var span2 = writer.ReserveSpan(2, out int token2);

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
        var writer = new PagedReservableWriter(innerWriter);

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
        using var writer = new PagedReservableWriter(innerWriter1);

        // Act - 写入数据，重置，再用新的innerWriter写入
        writer.GetSpan(100);
        writer.Advance(50);

        // 验证第一次写入的数据存在
        var firstResult = innerWriter1.GetWrittenData();
        Assert.Equal(50, firstResult.Length);

        writer.Reset();

        // 重置后，使用新的innerWriter测试
        var innerWriter2 = new TestBufferWriter();
        using var writer2 = new PagedReservableWriter(innerWriter2);

        var span = writer2.GetSpan(10);
        "Reset"u8.CopyTo(span);
        writer2.Advance(5);

        // Assert - 新的writer应该只有新数据
        var result = innerWriter2.GetWrittenData();
        Assert.Equal("Reset"u8.ToArray(), result);
    }
}
