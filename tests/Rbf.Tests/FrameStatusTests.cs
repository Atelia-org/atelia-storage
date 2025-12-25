using Atelia.Rbf;
using Xunit;

namespace Atelia.Rbf.Tests;

/// <summary>
/// FrameStatus 位域格式测试。
/// </summary>
/// <remarks>
/// 验证 [F-FRAMESTATUS-VALUES] 位域布局。
/// </remarks>
public class FrameStatusTests
{
    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(2, 0x01)]
    [InlineData(3, 0x02)]
    [InlineData(4, 0x03)]
    public void CreateValid_HasCorrectValue(int statusLen, byte expectedValue)
    {
        var status = FrameStatus.CreateValid(statusLen);
        Assert.Equal(expectedValue, status.Value);
        Assert.True(status.IsValid);
        Assert.False(status.IsTombstone);
        Assert.Equal(statusLen, status.StatusLen);
        Assert.True(status.IsMvpValid);
    }

    [Theory]
    [InlineData(1, 0x80)]
    [InlineData(2, 0x81)]
    [InlineData(3, 0x82)]
    [InlineData(4, 0x83)]
    public void CreateTombstone_HasCorrectValue(int statusLen, byte expectedValue)
    {
        var status = FrameStatus.CreateTombstone(statusLen);
        Assert.Equal(expectedValue, status.Value);
        Assert.False(status.IsValid);
        Assert.True(status.IsTombstone);
        Assert.Equal(statusLen, status.StatusLen);
        Assert.True(status.IsMvpValid);
    }

    [Fact]
    public void FromByte_AllMvpValidValues_Work()
    {
        // Valid values: 0x00-0x03, 0x80-0x83
        byte[] validValues = [0x00, 0x01, 0x02, 0x03, 0x80, 0x81, 0x82, 0x83];
        foreach (var value in validValues)
        {
            var status = FrameStatus.FromByte(value);
            Assert.True(status.IsMvpValid, $"Value 0x{value:X2} should be MVP valid");
        }
    }

    [Theory]
    [InlineData(0x04)] // Reserved bit set
    [InlineData(0x10)] // Reserved bit set
    [InlineData(0x40)] // Reserved bit set
    [InlineData(0xFF)] // Old Tombstone value - now invalid (reserved bits set)
    public void FromByte_ReservedBitsSet_NotMvpValid(byte value)
    {
        var status = FrameStatus.FromByte(value);
        Assert.False(status.IsMvpValid, $"Value 0x{value:X2} should NOT be MVP valid");
    }

    [Theory]
    [InlineData(0x00, false, 1)]
    [InlineData(0x03, false, 4)]
    [InlineData(0x80, true, 1)]
    [InlineData(0x83, true, 4)]
    public void FromByte_ExtractsFieldsCorrectly(byte value, bool isTombstone, int statusLen)
    {
        var status = FrameStatus.FromByte(value);
        Assert.Equal(isTombstone, status.IsTombstone);
        Assert.Equal(!isTombstone, status.IsValid);
        Assert.Equal(statusLen, status.StatusLen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void CreateValid_InvalidStatusLen_Throws(int statusLen)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStatus.CreateValid(statusLen));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void CreateTombstone_InvalidStatusLen_Throws(int statusLen)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameStatus.CreateTombstone(statusLen));
    }

    [Fact]
    public void Equality_Works()
    {
        var a = FrameStatus.CreateValid(2);
        var b = FrameStatus.CreateValid(2);
        var c = FrameStatus.CreateTombstone(2);

        Assert.True(a == b);
        Assert.False(a == c);
        Assert.True(a != c);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ToString_ReturnsReadableFormat()
    {
        var valid = FrameStatus.CreateValid(2);
        var tombstone = FrameStatus.CreateTombstone(3);

        Assert.Contains("Valid", valid.ToString());
        Assert.Contains("StatusLen=2", valid.ToString());
        Assert.Contains("Tombstone", tombstone.ToString());
        Assert.Contains("StatusLen=3", tombstone.ToString());
    }
}
