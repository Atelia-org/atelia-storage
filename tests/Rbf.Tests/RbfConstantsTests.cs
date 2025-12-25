using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// RbfConstants 单元测试。
/// </summary>
/// <remarks>
/// 验证 RBF 魔数定义符合规范条款 [F-FENCE-DEFINITION]。
/// </remarks>
public class RbfConstantsTests
{
    /// <summary>
    /// [F-FENCE-DEFINITION]: Fence = 0x31464252
    /// </summary>
    [Fact]
    public void Fence_HasCorrectValue()
    {
        Assert.Equal(0x31464252u, RbfConstants.Fence);
    }

    /// <summary>
    /// FenceBytes 与 Fence 值一致（little-endian 解释）。
    /// </summary>
    [Fact]
    public void FenceBytes_MatchesFenceValue()
    {
        var bytes = RbfConstants.FenceBytes;
        var fromBytes = BitConverter.ToUInt32(bytes);
        Assert.Equal(RbfConstants.Fence, fromBytes);
    }

    /// <summary>
    /// FenceBytes 是 "RBF1" 的 ASCII 表示。
    /// </summary>
    [Fact]
    public void FenceBytes_IsRBF1InAscii()
    {
        var bytes = RbfConstants.FenceBytes;
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'B', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'1', bytes[3]);
    }

    /// <summary>
    /// FenceLength 为 4，与 FenceBytes.Length 一致。
    /// </summary>
    [Fact]
    public void FenceLength_Is4()
    {
        Assert.Equal(4, RbfConstants.FenceLength);
        Assert.Equal(RbfConstants.FenceLength, RbfConstants.FenceBytes.Length);
    }
}
