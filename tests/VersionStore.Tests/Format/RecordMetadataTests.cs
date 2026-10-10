using Atelia.VersionStore.Internal.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Format;

public class RecordMetadataTests {
    [Theory]
    [InlineData(0L, "0000000000000000")]
    [InlineData(-1L, "FFFFFFFFFFFFFFFF")]
    [InlineData(0x0102030405060708L, "0807060504030201")]
    [InlineData(long.MinValue, "0000000000000080")]
    [InlineData(long.MaxValue, "FFFFFFFFFFFFFF7F")]
    public void Time_IndependentGoldensAcceptFullSignedWireRange(long expected, string hex) {
        byte[] golden = Convert.FromHexString(hex);
        Assert.True(RecordMetadataCodec.TryRead(golden, out long actual));
        Assert.Equal(expected, actual);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 11).ToArray();
        Assert.True(RecordMetadataCodec.TryWrite(expected, destination));
        Assert.Equal(golden, destination[..8]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[8..]);
    }

    [Fact]
    public void Meta8_RejectsEveryShortLengthAndUnknownLongShapes() {
        foreach (int length in Enumerable.Range(0, 12).Where(length => length != 8)) {
            byte[] wire = Enumerable.Repeat((byte)0xFF, length).ToArray();
            long output = 123;
            Assert.False(RecordMetadataCodec.TryRead(wire, out output));
            Assert.Equal(0, output);
        }
    }

    [Fact]
    public void RejectedMetadataWrite_PreservesAllDestinationBytes() {
        for (int length = 0; length < 8; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(RecordMetadataCodec.TryWrite(long.MinValue, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }
    }

    [Fact]
    public void TimeValues_MayRepeatAndGoBackwardsWithoutState() {
        long[] values = [123, 123, 122, 0, -1];
        foreach (long value in values) {
            byte[] wire = new byte[8];
            Assert.True(RecordMetadataCodec.TryWrite(value, wire));
            Assert.True(RecordMetadataCodec.TryRead(wire, out long decoded));
            Assert.Equal(value, decoded);
        }
    }
}
