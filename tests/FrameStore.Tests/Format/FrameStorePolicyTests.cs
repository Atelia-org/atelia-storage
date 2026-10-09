using System.Text;
using Atelia.Data;
using Atelia.FrameStore.Internal.Format;
using Xunit;

namespace Atelia.FrameStore.Tests.Format;

public class FrameStorePolicyTests {
    [Theory]
    [InlineData("{}", 32)]
    [InlineData(" { } \r\n", 32)]
    [InlineData("{\"MaxOutstandingBuilders\":1}", 1)]
    [InlineData("{\"MaxOutstandingBuilders\":32}", 32)]
    [InlineData("{\"MaxOutstandingBuilders\":2147483647}", int.MaxValue)]
    [InlineData("{\"MaxOutstanding\\u0042uilders\":2}", 2)]
    public void Configuration_AcceptsOnlyDeclaredIntegerProperty(string json, int expected) {
        Assert.True(FrameStoreConfiguration.TryParse(Encoding.UTF8.GetBytes(json), out FrameStoreConfiguration configuration));
        Assert.Equal(expected, configuration.MaxOutstandingBuilders);
        Assert.Equal(32, FrameStoreConfiguration.Default.MaxOutstandingBuilders);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{\"MaxOutstandingBuilders\":0}")]
    [InlineData("{\"MaxOutstandingBuilders\":-1}")]
    [InlineData("{\"MaxOutstandingBuilders\":2147483648}")]
    [InlineData("{\"MaxOutstandingBuilders\":999999999999999999999999999}")]
    [InlineData("{\"MaxOutstandingBuilders\":1.0}")]
    [InlineData("{\"MaxOutstandingBuilders\":1.5}")]
    [InlineData("{\"MaxOutstandingBuilders\":1e0}")]
    [InlineData("{\"MaxOutstandingBuilders\":\"32\"}")]
    [InlineData("{\"MaxOutstandingBuilders\":null}")]
    [InlineData("{\"MaxOutstandingBuilders\":false}")]
    [InlineData("{\"MaxOutstandingBuilders\":[]}")]
    [InlineData("{\"MaxOutstandingBuilders\":{}}")]
    [InlineData("{\"maxOutstandingBuilders\":32}")]
    [InlineData("{\"MaxOutstandingBuilders\":1,\"MaxOutstandingBuilders\":2}")]
    [InlineData("{\"MaxOutstandingBuilders\":1,\"MaxOutstanding\\u0042uilders\":2}")]
    [InlineData("{\"MaxOutstandingBuilders\":1,\"Unknown\":2}")]
    [InlineData("{\"RotationThresholdBytes\":1024}")]
    [InlineData("{\"MaxOutstandingBuilders\":1,}")]
    [InlineData("{/* comment */}")]
    [InlineData("{\"MaxOutstandingBuilders\":/* comment */1}")]
    [InlineData("{} // comment")]
    [InlineData("{} {}")]
    [InlineData("{\"MaxOutstandingBuilders\":01}")]
    [InlineData("{\"MaxOutstandingBuilders\":+1}")]
    [InlineData("{\"MaxOutstandingBuilders\":1")]
    public void Configuration_InvalidInputDoesNotSilentlyUseDefault(string json) {
        Assert.False(FrameStoreConfiguration.TryParse(Encoding.UTF8.GetBytes(json), out FrameStoreConfiguration configuration));
        Assert.Equal(0, configuration.MaxOutstandingBuilders);
    }

    [Fact]
    public void Configuration_RejectsInvalidUtf8() {
        byte[] source = [0x7B, 0x22, 0xFF, 0x22, 0x3A, 0x31, 0x7D];
        Assert.False(FrameStoreConfiguration.TryParse(source, out _));
    }

    [Fact]
    public void RotationThreshold_AcceptsClosedRangeAndUnalignedValues() {
        long initialization = FileHeaderCodec.InitializationBoundary;
        RotationThreshold.Validate(initialization);
        RotationThreshold.Validate(initialization + 1);
        RotationThreshold.Validate(SizedPtr.MaxOffset - 1);
        RotationThreshold.Validate(SizedPtr.MaxOffset);
        RotationThreshold.Validate(RotationThreshold.DefaultBytes);
        Assert.Equal(64L * 1024 * 1024 * 1024, RotationThreshold.DefaultBytes);
        Assert.NotEqual(0L, (initialization + 1) % 4);
    }

    [Fact]
    public void RotationThreshold_RejectsOutsideRangeWithCorrectParameter() {
        foreach (long invalid in new[] { long.MinValue, -1, 0, FileHeaderCodec.InitializationBoundary - 1, SizedPtr.MaxOffset + 1, long.MaxValue }) {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => RotationThreshold.Validate(invalid));
            Assert.Equal("rotationThresholdBytes", error.ParamName);
        }
    }
}
