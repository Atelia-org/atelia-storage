using Atelia.VersionStore.Internal.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Format;

public class RefIdCodecTests {
    [Theory]
    [InlineData(1u, "01000000")]
    [InlineData(0x80000000u, "00000080")]
    [InlineData(0x89ABCDEFu, "EFCDAB89")]
    [InlineData(uint.MaxValue, "FFFFFFFF")]
    public void LocalField_IndependentLittleEndianGoldens(uint localId, string hex) {
        StoreIdentity identity = FormatTestValues.Identity();
        byte[] golden = Convert.FromHexString(hex);
        Assert.True(RefIdCodec.TryRead(golden, identity, out RefId refId));
        Assert.Equal(localId, refId.LocalId);
        Assert.True(refId.BelongsTo(identity));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 7).ToArray();
        Assert.True(RefIdCodec.TryWrite(refId, identity, destination));
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
        RefId output = RefId.Create(FormatTestValues.Identity(), 7);
        Assert.False(RefIdCodec.TryRead(source, FormatTestValues.Identity(), out output));
        Assert.Equal(default, output);
    }

    [Fact]
    public void LocalField_RejectsZeroAndMissingContext() {
        Assert.False(RefIdCodec.TryRead(new byte[4], FormatTestValues.Identity(), out RefId zero));
        Assert.Equal(default, zero);
        Assert.False(RefIdCodec.TryRead(Convert.FromHexString("EFCDAB89"), default, out RefId contextless));
        Assert.Equal(default, contextless);
    }

    [Fact]
    public void LocalWrite_RejectsDefaultForeignContextAndShortCapacityBeforeWriting() {
        StoreIdentity identity = FormatTestValues.Identity();
        RefId valid = RefId.Create(identity, 7);
        RefId[] invalid = [default, RefId.Create(FormatTestValues.DataIdentity(), 7)];
        foreach (RefId refId in invalid) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 7).ToArray();
            Assert.False(RefIdCodec.TryWrite(refId, identity, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }

        byte[] missingContext = Enumerable.Repeat((byte)0xA5, 7).ToArray();
        Assert.False(RefIdCodec.TryWrite(valid, default, missingContext));
        Assert.All(missingContext, value => Assert.Equal((byte)0xA5, value));
        for (int length = 0; length < 4; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(RefIdCodec.TryWrite(valid, identity, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }
    }
}
