using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfFrame 结构测试。
/// </summary>
public class RbfFrameTests
{
    [Fact]
    public void RbfFrame_CalculatesFrameLength()
    {
        var frame = new RbfFrame(
            FileOffset: 100,
            FrameTag: 0x12345678,
            PayloadOffset: 108,
            PayloadLength: 10,
            Status: FrameStatus.CreateValid(2)); // PayloadLen=10 → StatusLen=2

        // PayloadLen=10, StatusLen=2, FrameLen = 16 + 10 + 2 = 28
        Assert.Equal(28, frame.FrameLength);
    }

    [Fact]
    public void RbfFrame_CalculatesStatusLength()
    {
        var frame = new RbfFrame(
            FileOffset: 100,
            FrameTag: 0x12345678,
            PayloadOffset: 108,
            PayloadLength: 10,
            Status: FrameStatus.CreateValid(2)); // PayloadLen=10 → StatusLen=2

        // PayloadLen=10, 10 % 4 = 2, StatusLen = 2
        Assert.Equal(2, frame.StatusLength);
    }

    [Fact]
    public void RbfFrame_EmptyPayload_HasCorrectLengths()
    {
        var frame = new RbfFrame(
            FileOffset: 4,
            FrameTag: 1,
            PayloadOffset: 12,
            PayloadLength: 0,
            Status: FrameStatus.CreateValid(4)); // PayloadLen=0 → StatusLen=4

        Assert.Equal(20, frame.FrameLength);
        Assert.Equal(4, frame.StatusLength);
    }

    [Fact]
    public void RbfFrame_Tombstone_PreservesStatus()
    {
        var frame = new RbfFrame(
            FileOffset: 4,
            FrameTag: 1,
            PayloadOffset: 12,
            PayloadLength: 0,
            Status: FrameStatus.CreateTombstone(4)); // PayloadLen=0 → StatusLen=4

        Assert.True(frame.Status.IsTombstone);
    }
}
