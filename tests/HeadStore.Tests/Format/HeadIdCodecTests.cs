using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Format;

public class HeadIdCodecTests {
    [Theory]
    [InlineData(1u, "01000000")]
    [InlineData(0x80000000u, "00000080")]
    [InlineData(0x89ABCDEFu, "EFCDAB89")]
    [InlineData(uint.MaxValue, "FFFFFFFF")]
    public void LocalField_IndependentLittleEndianGoldens(uint localId, string hex) {
        StoreIdentity identity = FormatTestValues.Identity();
        byte[] golden = Convert.FromHexString(hex);
        Assert.True(HeadIdCodec.TryRead(golden, identity, out HeadId headId));
        Assert.Equal(localId, headId.LocalId);
        Assert.True(headId.BelongsTo(identity));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 7).ToArray();
        Assert.True(HeadIdCodec.TryWrite(headId, identity, destination));
        Assert.Equal(golden, destination[..4]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[4..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void LocalField_RejectsWrongWidthAndClearsResult(int length) {
        byte[] source = Enumerable.Repeat((byte)0xFF, length).ToArray();
        HeadId output = HeadId.Create(FormatTestValues.Identity(), 7);
        Assert.False(HeadIdCodec.TryRead(source, FormatTestValues.Identity(), out output));
        Assert.Equal(default, output);
    }

    [Fact]
    public void LocalField_RejectsZeroAndMissingContext() {
        Assert.False(HeadIdCodec.TryRead(new byte[4], FormatTestValues.Identity(), out HeadId zero));
        Assert.Equal(default, zero);
        Assert.False(HeadIdCodec.TryRead(Convert.FromHexString("EFCDAB89"), default, out HeadId contextless));
        Assert.Equal(default, contextless);
    }

    [Fact]
    public void LocalWrite_RejectsDefaultForeignContextAndShortCapacityBeforeWriting() {
        StoreIdentity identity = FormatTestValues.Identity();
        HeadId valid = HeadId.Create(identity, 7);
        HeadId[] invalid = [default, HeadId.Create(FormatTestValues.DataIdentity(), 7)];
        foreach (HeadId headId in invalid) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 7).ToArray();
            Assert.False(HeadIdCodec.TryWrite(headId, identity, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }

        byte[] missingContext = Enumerable.Repeat((byte)0xA5, 7).ToArray();
        Assert.False(HeadIdCodec.TryWrite(valid, default, missingContext));
        Assert.All(missingContext, value => Assert.Equal((byte)0xA5, value));
        for (int length = 0; length < 4; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(HeadIdCodec.TryWrite(valid, identity, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }
    }
}
