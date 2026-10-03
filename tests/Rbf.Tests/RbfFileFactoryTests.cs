using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf.Internal.Tests;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>RbfFile.CreateNew / OpenExisting 工厂方法测试。</summary>
public class RbfFileFactoryTests : IDisposable {
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

    /// <summary>CreateNew 创建的文件长度为 4，内容为 RBF3 HeaderFence。</summary>
    [Fact]
    public void CreateNew_CreatesFileWithHeaderFence() {
        // Arrange
        var path = GetTempFilePath();

        // Act
        using (var rbf = RbfFile.CreateNew(path)) {
            Assert.Equal(4, rbf.TailOffset);
        }

        // Assert - 按字节断言，不依赖常量
        var content = File.ReadAllBytes(path);
        Assert.Equal(4, content.Length);
        Assert.Equal(0x52, content[0]); // 'R'
        Assert.Equal(0x42, content[1]); // 'B'
        Assert.Equal(0x46, content[2]); // 'F'
        Assert.Equal(0x33, content[3]); // '3'
    }

    /// <summary>CreateNew 在文件已存在时抛出 IOException。</summary>
    [Fact]
    public void CreateNew_FailsIfFileExists() {
        // Arrange
        var path = GetTempFilePath();
        File.WriteAllText(path, "existing content");

        // Act & Assert
        Assert.Throws<IOException>(() => RbfFile.CreateNew(path));
    }

    /// <summary>OpenExisting 成功打开有效的 RBF 文件，TailOffset 正确。</summary>
    [Fact]
    public void OpenExisting_SucceedsWithValidFile() {
        // Arrange
        var path = GetTempFilePath();
        using (var created = RbfFile.CreateNew(path)) {
            // 创建后立即关闭
        }

        // Act
        using var opened = RbfFile.OpenExisting(path, out _);

        // Assert
        Assert.Equal(4, opened.TailOffset);
    }

    [Fact]
    public void OpenReadOnlyExisting_ReadsButCannotAppend() {
        var path = GetTempFilePath();
        Atelia.Data.SizedPtr ticket;
        using (var created = RbfFile.CreateNew(path)) {
            ticket = created.Append(
                7,
                new byte[] { 1, 2, 3, 4 }
            ).Unwrap();
        }
        byte[] before = File.ReadAllBytes(path);

        using var opened = RbfFile.OpenReadOnlyExisting(path);
        using var frame = opened.ReadPooledFrame(ticket).Unwrap();

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame.PayloadAndMeta.ToArray());
        Assert.Throws<InvalidOperationException>(
            () => opened.Append(8, new byte[] { 5, 6, 7, 8 })
        );
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>OpenExisting 在 HeaderFence 不匹配时抛出 InvalidDataException。</summary>
    [Fact]
    public void OpenExisting_FailsWithInvalidHeaderFence() {
        // Arrange - 创建内容非 RBF1 的文件
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00, 0x00 });

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.Contains("HeaderFence", ex.Message);
    }

    /// <summary>OpenExisting 在文件不存在时抛出 FileNotFoundException。</summary>
    [Fact]
    public void OpenExisting_FailsIfFileNotExists() {
        // Arrange
        var path = GetTempFilePath();
        // 不创建文件

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => RbfFile.OpenExisting(path, out _));
    }

    /// <summary>OpenExisting 在文件小于 4 字节时抛出 InvalidDataException。</summary>
    [Fact]
    public void OpenExisting_FailsWhenFileTooShort() {
        // Arrange - 创建小于 4 字节的文件
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x52, 0x42, 0x46 }); // 只有 3 字节

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.Contains("HeaderFence", ex.Message);
    }

    /// <summary>Malformed partial HeadLen cannot authorize recovery.</summary>
    [Fact]
    public void OpenExisting_FailsWithImpossiblePartialHeadLen() {
        // Arrange - 创建有效 HeaderFence 但长度非 4B 对齐的文件 (5 字节)
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x52, 0x42, 0x46, 0x31, 0xFF }); // 5 字节

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.NotEmpty(ex.Message);
        Assert.Equal(new byte[] { 0x52, 0x42, 0x46, 0x31, 0xFF }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("RBF2")]
    [InlineData("RBF4")]
    public void UnknownProfile_RejectsBothOpenModes_WithoutChangingBytes(string magic) {
        var path = GetTempFilePath();
        byte[] before = System.Text.Encoding.ASCII.GetBytes(magic);
        File.WriteAllBytes(path, before);
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(path));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void LegacyRbf1_ReadOnlyPreservesTicketAndContent_WritableRejectsWithoutMutation() {
        var path = GetTempFilePath();
        Atelia.Data.SizedPtr ticket;
        using (var fixture = RawRbfTestFile.CreateLegacy(path)) {
            ticket = fixture.Append(123, "legacy"u8, "meta"u8).Unwrap();
        }
        byte[] before = File.ReadAllBytes(path);
        using (var file = RbfFile.OpenReadOnlyExisting(path)) {
            using var frame = file.ReadPooledFrame(ticket).Unwrap();
            Assert.Equal(ticket, frame.Ticket);
            Assert.Equal("legacymeta"u8.ToArray(), frame.PayloadAndMeta.ToArray());
            Assert.Equal(123u, frame.Tag);
            using var meta = file.ReadPooledTailMeta(ticket).Unwrap();
            Assert.Equal("meta"u8.ToArray(), meta.TailMeta.ToArray());
            Assert.Throws<InvalidOperationException>(() => file.Append(1, []));
        }
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>最大旧容量的闭合单帧结构正例；不验证 PayloadCRC 或执行完整帧读取。</summary>
    [Fact]
    public void LegacyRbf1_MaximumPayload_ReadOnlyOpenPreservesTicketAndFrameInfo() {
        var path = GetTempFilePath();
        var ticket = SizedPtr.Create(4, SizedPtr.MaxLength);
        long fenceOffset = ticket.Offset + ticket.Length;
        long fileLength = fenceOffset + 4;
        const uint tag = 123;

        using (var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
            SparseRbfTestFile.MarkSparse(handle, path);
            RandomAccess.SetLength(handle, fileLength);

            Span<byte> head = stackalloc byte[8];
            "RBF1"u8.CopyTo(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)ticket.Length);
            RandomAccess.Write(handle, head, 0);

            Span<byte> trailer = stackalloc byte[16];
            trailer.Clear(); // Descriptor: no tombstone, meta or padding.
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[8..], tag);
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], (uint)ticket.Length);
            RollingCrc.SealCodewordBackward(trailer);
            RandomAccess.Write(handle, trailer, fenceOffset - trailer.Length);
            RandomAccess.Write(handle, "RBF1"u8, fenceOffset);
            // Payload and PayloadCRC remain sparse zeros; only structure/info is qualified.
        }

        using var file = RbfFile.OpenReadOnlyExisting(path, RbfCacheMode.Off);
        var info = file.ReadFrameInfo(ticket).Unwrap();

        Assert.Equal(fileLength, file.TailOffset);
        Assert.Equal(268435452, info.Ticket.Length);
        Assert.Equal(ticket, info.Ticket);
        Assert.Equal(268435428, info.PayloadLength);
        Assert.Equal(RbfFile.MaxPayloadAndMetaLength + 4, info.PayloadLength);
        Assert.Equal(0, info.TailMetaLength);
        Assert.Equal(tag, info.Tag);
        Assert.False(info.IsTombstone);
    }
}
