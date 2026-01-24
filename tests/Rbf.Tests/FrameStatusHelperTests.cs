// [DEPRECATED] FrameStatusHelper 在 v0.40 中已被 TrailerCodewordHelper 替代。
// 本测试文件保留仅为文档目的，所有测试已禁用。
// 参见 TrailerCodewordHelperTests.cs 获取 v0.40 格式的测试。

using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// [DEPRECATED] FrameStatusHelper 测试（v0.40 已废弃）。
/// </summary>
/// <remarks>
/// v0.40 使用 TrailerCodeword 中的 FrameDescriptor 替代 FrameStatus。
/// IsTombstone 现在存储在 FrameDescriptor 的 bit31。
/// </remarks>
public class FrameStatusHelperTests {
    // 所有测试已移除，因为 FrameStatusHelper 在 v0.40 中不再使用。
    // 相关测试已迁移到 TrailerCodewordHelperTests.cs。

    [Fact]
    public void Placeholder_AllTestsDeprecated() {
        // v0.40 使用 TrailerCodewordHelper 替代 FrameStatusHelper
        Assert.True(true, "FrameStatusHelper is deprecated in v0.40. See TrailerCodewordHelperTests.");
    }
}
