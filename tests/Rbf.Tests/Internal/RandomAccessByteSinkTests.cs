using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.Rbf.Tests.Internal;

/// <summary>
/// RandomAccessByteSink 测试
/// </summary>
public class RandomAccessByteSinkTests : IDisposable {
    private readonly string _tempFile;
    private readonly SafeFileHandle _handle;

    public RandomAccessByteSinkTests() {
        _tempFile = Path.GetTempFileName();
        _handle = File.OpenHandle(
            _tempFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.None
        );
    }

    public void Dispose() {
        _handle.Dispose();
        if (File.Exists(_tempFile)) {
            File.Delete(_tempFile);
        }
    }

    [Theory]
    [InlineData(0, new byte[] { 1, 2, 3, 4, 5 })]
    [InlineData(100, new byte[] { 10, 20, 30 })]
    public void Push_WritesDataAtSpecifiedOffset(long startOffset, byte[] data) {
        // Arrange
        var sink = new RandomAccessByteSink(_handle, startOffset);

        // Act
        sink.Push(data);

        // Assert
        Assert.Equal(startOffset + data.Length, sink.CurrentOffset);

        byte[] buffer = new byte[data.Length];
        RandomAccess.Read(_handle, buffer, fileOffset: startOffset);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void Reset_UpdatesWriteOffset() {
        // Arrange
        var sink = new RandomAccessByteSink(_handle, startOffset: 0);

        sink.Push([1, 2, 3]);
        Assert.Equal(3, sink.CurrentOffset);

        // Act: Reset to offset 100
        sink.Reset(100);

        // Assert
        Assert.Equal(100, sink.CurrentOffset);

        // 继续写入，应该写到 offset 100
        sink.Push([4, 5]);
        Assert.Equal(102, sink.CurrentOffset);

        // 验证文件内容
        byte[] buffer1 = new byte[3];
        RandomAccess.Read(_handle, buffer1, fileOffset: 0);
        Assert.Equal([1, 2, 3], buffer1); // 原有数据保留

        byte[] buffer2 = new byte[2];
        RandomAccess.Read(_handle, buffer2, fileOffset: 100);
        Assert.Equal([4, 5], buffer2); // 新数据写入 offset 100
    }

    [Fact]
    public void Reset_CanResetToZero() {
        // Arrange
        var sink = new RandomAccessByteSink(_handle, startOffset: 50);
        sink.Push([1, 2, 3]);

        // Act
        sink.Reset(0);

        // Assert
        Assert.Equal(0, sink.CurrentOffset);

        // 写入将覆盖开头
        sink.Push([9, 9]);
        Assert.Equal(2, sink.CurrentOffset);

        byte[] buffer = new byte[2];
        RandomAccess.Read(_handle, buffer, fileOffset: 0);
        Assert.Equal([9, 9], buffer);
    }

    [Fact]
    public void Push_EmptySpan_NoOp() {
        // Arrange
        var sink = new RandomAccessByteSink(_handle, startOffset: 10);

        // Act
        sink.Push(ReadOnlySpan<byte>.Empty);

        // Assert: offset 不变
        Assert.Equal(10, sink.CurrentOffset);
    }

    [Fact]
    public void Constructor_NullFile_ThrowsArgumentNullException() {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(
            () => new RandomAccessByteSink(null!, startOffset: 0)
        );
    }
}
