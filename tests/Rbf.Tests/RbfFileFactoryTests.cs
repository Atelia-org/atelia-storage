using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFile.CreateNew / OpenExisting 工厂方法测试。
/// </summary>
public class RbfFileFactoryTests : IDisposable {
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

    /// <summary>
    /// CreateNew 创建的文件长度为 4，内容为 HeaderFence (0x52 0x42 0x46 0x31)。
    /// </summary>
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
        Assert.Equal(0x31, content[3]); // '1'
    }

    /// <summary>
    /// CreateNew 在文件已存在时抛出 IOException。
    /// </summary>
    [Fact]
    public void CreateNew_FailsIfFileExists() {
        // Arrange
        var path = GetTempFilePath();
        File.WriteAllText(path, "existing content");

        // Act & Assert
        Assert.Throws<IOException>(() => RbfFile.CreateNew(path));
    }

    /// <summary>
    /// OpenExisting 成功打开有效的 RBF 文件，TailOffset 正确。
    /// </summary>
    [Fact]
    public void OpenExisting_SucceedsWithValidFile() {
        // Arrange
        var path = GetTempFilePath();
        using (var created = RbfFile.CreateNew(path)) {
            // 创建后立即关闭
        }

        // Act
        using var opened = RbfFile.OpenExisting(path);

        // Assert
        Assert.Equal(4, opened.TailOffset);
    }

    /// <summary>
    /// OpenExisting 在 HeaderFence 不匹配时抛出 InvalidDataException。
    /// </summary>
    [Fact]
    public void OpenExisting_FailsWithInvalidHeaderFence() {
        // Arrange - 创建内容非 RBF1 的文件
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00, 0x00 });

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path));
        Assert.Contains("HeaderFence mismatch", ex.Message);
    }

    /// <summary>
    /// OpenExisting 在文件不存在时抛出 FileNotFoundException。
    /// </summary>
    [Fact]
    public void OpenExisting_FailsIfFileNotExists() {
        // Arrange
        var path = GetTempFilePath();
        // 不创建文件

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => RbfFile.OpenExisting(path));
    }

    /// <summary>
    /// OpenExisting 在文件小于 4 字节时抛出 InvalidDataException。
    /// </summary>
    [Fact]
    public void OpenExisting_FailsWhenFileTooShort() {
        // Arrange - 创建小于 4 字节的文件
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x52, 0x42, 0x46 }); // 只有 3 字节

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path));
        Assert.Contains("file too short", ex.Message);
    }

    /// <summary>
    /// OpenExisting 在文件长度非 4B 对齐时抛出 InvalidDataException。
    /// </summary>
    /// <remarks>规范引用：@[S-RBF-DECISION-4B-ALIGNMENT-ROOT]</remarks>
    [Fact]
    public void OpenExisting_FailsWhenLengthNotAligned() {
        // Arrange - 创建有效 HeaderFence 但长度非 4B 对齐的文件 (5 字节)
        var path = GetTempFilePath();
        File.WriteAllBytes(path, new byte[] { 0x52, 0x42, 0x46, 0x31, 0xFF }); // 5 字节

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(path));
        Assert.Contains("not 4-byte aligned", ex.Message);
    }
}
