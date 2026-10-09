using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Format;

public class FormatRecordTests {
    private const string IdentityHex = "0102030405060708090A0B0C0D0E0F10";
    // Standard CRC32C independently calculated over version=1 + these 16 bytes: 0x69285c7d, stored LE.
    private const string GateHex = "010000000102030405060708090A0B0C0D0E0F107D5C2869";
    private const string HeaderHex = "010000000102030405060708090A0B0C0D0E0F10EFCDAB89";

    [Fact]
    public void Identity_OwnsExactOpaqueBytesWithoutGuidConversion() {
        byte[] source = Convert.FromHexString(IdentityHex);
        Assert.True(StoreIdentity.TryRead(source, out StoreIdentity identity));
        Array.Fill(source, (byte)0);
        byte[] encoded = Enumerable.Repeat((byte)0xA5, 18).ToArray();
        Assert.True(identity.TryWrite(encoded));
        Assert.Equal(Convert.FromHexString(IdentityHex), encoded[..16]);
        Assert.Equal(new byte[] { 0xA5, 0xA5 }, encoded[16..]);
        Assert.True(StoreIdentity.TryRead(encoded.AsSpan(0, 16), out StoreIdentity same));
        Assert.Equal(identity, same);
        Assert.Equal(identity.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void Identity_RejectsZeroAndWrongWidthButAcceptsSingleNonzeroByte() {
        Assert.False(StoreIdentity.TryRead(new byte[16], out StoreIdentity zero));
        Assert.Equal(default, zero);
        Assert.False(StoreIdentity.TryRead(new byte[15], out _));
        Assert.False(StoreIdentity.TryRead(new byte[17], out _));
        byte[] sparse = new byte[16];
        sparse[15] = 1;
        Assert.True(StoreIdentity.TryRead(sparse, out _));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 16).ToArray();
        Assert.False(default(StoreIdentity).TryWrite(destination));
        Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        StoreIdentity identity = Identity();
        byte[] shortDestination = Enumerable.Repeat((byte)0xA5, 15).ToArray();
        Assert.False(identity.TryWrite(shortDestination));
        Assert.All(shortDestination, value => Assert.Equal((byte)0xA5, value));
    }

    [Fact]
    public void Gate_IndependentCodewordRoundtripsExactRecordAndPreservesTail() {
        byte[] wire = Convert.FromHexString(GateHex);
        Assert.True(FormatGateCodec.TryRead(wire, out StoreIdentity identity));
        Assert.Equal(Identity(), identity);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 27).ToArray();
        Assert.True(FormatGateCodec.TryWrite(identity, destination));
        Assert.Equal(wire, destination[..24]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[24..]);
    }

    [Theory]
    [InlineData("020000000102030405060708090A0B0C0D0E0F10BA44EC30")] // Unknown version with correct CRC.
    [InlineData("010000000000000000000000000000000000000083A1868B")] // Zero StoreId with correct CRC.
    [InlineData("010000000102030405060708090A0B0C0D0E0F1069285C7D")] // CRC in the wrong byte order.
    [InlineData("010000000102030405060708090A0B0C0D0E0F107D5C28")]
    [InlineData("010000000102030405060708090A0B0C0D0E0F107D5C286900")]
    public void Gate_InvalidRecordNeverClaimsIdentity(string hex) {
        StoreIdentity identity = Identity();
        Assert.False(FormatGateCodec.TryRead(Convert.FromHexString(hex), out identity));
        Assert.Equal(default, identity);
    }

    [Fact]
    public void Gate_EverySingleBitMutationIsRejected() {
        byte[] original = Convert.FromHexString(GateHex);
        for (int bit = 0; bit < original.Length * 8; bit++) {
            byte[] mutated = original.ToArray();
            mutated[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(FormatGateCodec.TryRead(mutated, out StoreIdentity identity));
            Assert.Equal(default, identity);
        }
    }

    [Fact]
    public void Header_IndependentPayloadRequiresExpectedIdentityAndFileId() {
        byte[] payload = Convert.FromHexString(HeaderHex);
        Assert.True(FileHeaderCodec.TryValidate(payload, Identity(), 0x89abcdef));
        Assert.False(FileHeaderCodec.TryValidate(payload, Identity(), 0x89abcdee));
        Assert.False(FileHeaderCodec.TryValidate(payload, default, 0x89abcdef));
        Assert.False(FileHeaderCodec.TryValidate(payload, Identity(), 0));
        byte[] otherId = Convert.FromHexString(IdentityHex);
        otherId[0] ^= 1;
        Assert.True(StoreIdentity.TryRead(otherId, out StoreIdentity other));
        Assert.False(FileHeaderCodec.TryValidate(payload, other, 0x89abcdef));
        byte[] destination = Enumerable.Repeat((byte)0xA5, 26).ToArray();
        Assert.True(FileHeaderCodec.TryWrite(Identity(), 0x89abcdef, destination));
        Assert.Equal(payload, destination[..24]);
        Assert.Equal(new byte[] { 0xA5, 0xA5 }, destination[24..]);
    }

    [Fact]
    public void Header_RejectsWrongWidthVersionZeroIdentityAndZeroFileId() {
        byte[] valid = Convert.FromHexString(HeaderHex);
        Assert.False(FileHeaderCodec.TryValidate(valid[..23], Identity(), 0x89abcdef));
        Assert.False(FileHeaderCodec.TryValidate([.. valid, 0], Identity(), 0x89abcdef));
        byte[] unknown = valid.ToArray();
        unknown[0] = 2;
        Assert.False(FileHeaderCodec.TryValidate(unknown, Identity(), 0x89abcdef));
        byte[] zeroIdentity = valid.ToArray();
        zeroIdentity.AsSpan(4, 16).Clear();
        Assert.False(FileHeaderCodec.TryValidate(zeroIdentity, Identity(), 0x89abcdef));
        byte[] zeroFile = valid.ToArray();
        zeroFile.AsSpan(20, 4).Clear();
        Assert.False(FileHeaderCodec.TryValidate(zeroFile, Identity(), 0x89abcdef));
        // A checked gate codeword is a different record; its CRC must not be read as this file's FileId.
        Assert.False(FileHeaderCodec.TryValidate(Convert.FromHexString(GateHex), Identity(), 0x89abcdef));
    }

    [Fact]
    public void RejectedFormatWrites_DoNotPartiallyModifyDestination() {
        byte[] destination = Enumerable.Repeat((byte)0xA5, 24).ToArray();
        Assert.False(FormatGateCodec.TryWrite(default, destination));
        Assert.False(FileHeaderCodec.TryWrite(default, 1, destination));
        Assert.False(FileHeaderCodec.TryWrite(Identity(), 0, destination));
        Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        byte[] shortDestination = Enumerable.Repeat((byte)0xCD, 23).ToArray();
        Assert.False(FormatGateCodec.TryWrite(Identity(), shortDestination));
        Assert.False(FileHeaderCodec.TryWrite(Identity(), 1, shortDestination));
        Assert.All(shortDestination, value => Assert.Equal((byte)0xCD, value));
    }

    [Fact]
    public void HeaderBoundary_IsDerivedFromPublicRbfWriteSize() {
        RbfWriteSize size = RbfFile.MeasureWriteSize(24, 0).Value;
        Assert.Equal(size.FrameLength, FileHeaderCodec.HeaderFrameLength);
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive + size.AppendLength, FileHeaderCodec.InitializationBoundary);
    }

    private static StoreIdentity Identity() {
        Assert.True(StoreIdentity.TryRead(Convert.FromHexString(IdentityHex), out StoreIdentity identity));
        return identity;
    }
}
