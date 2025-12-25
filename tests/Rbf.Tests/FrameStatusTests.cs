using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// FrameStatus 枚举测试。
/// </summary>
public class FrameStatusTests
{
    [Fact]
    public void Valid_HasCorrectValue()
    {
        Assert.Equal((byte)0x00, (byte)FrameStatus.Valid);
    }

    [Fact]
    public void Tombstone_HasCorrectValue()
    {
        Assert.Equal((byte)0xFF, (byte)FrameStatus.Tombstone);
    }
}
