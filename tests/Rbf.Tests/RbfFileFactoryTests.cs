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
        // Arrange - 创建内容不是受支持 RBF3 的文件
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

    /// <summary>Unsupported format cannot authorize recovery, even with a partial body.</summary>
    [Fact]
    public void OpenExisting_UnsupportedHeaderWithPartialBodyIsUnchanged() {
        // Arrange - 创建不受支持的历史 HeaderFence 和单字节 body
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x52, 0x42, 0x46, 0x31, 0xFF }); // 5 字节

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _));
        Assert.NotEmpty(ex.Message);
        Assert.Equal(new byte[] { 0x52, 0x42, 0x46, 0x31, 0xFF }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("RBF1")]
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

    [Theory]
    [InlineData(RbfCacheMode.Off)]
    [InlineData(RbfCacheMode.Slots16)]
    public void Rbf1CompleteHistoricalImage_RejectsAllOpenEntrypointsWithoutMutation(RbfCacheMode cacheMode) {
        var path = GetTempFilePath();
        // Historical empty frame: byte length 24, tag 0x11223344 and sealed TrailerCRC.
        // Literal bytes exercise rejection without retaining an RBF1 encoder in main.
        byte[] before = Convert.FromHexString("524246311800000000000000605F43DC00000000443322111800000052424631");
        File.WriteAllBytes(path, before);

        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path, out _, cacheMode));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(path, cacheMode));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
