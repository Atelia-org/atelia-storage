using Atelia.Data.Hashing;
using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Format;

public class FormatGateTests {
    [Fact]
    public void Gate_IndependentGoldenHasExactRolesAndCanonicalCrc() {
        byte[] golden = Convert.FromHexString(FormatTestValues.GateHex);
        Assert.Equal(44, golden.Length);
        Assert.True(FormatGateCodec.TryRead(golden, out FormatGate gate));
        Assert.Equal(FormatTestValues.Identity(), gate.HeadStoreId);
        Assert.Equal(FormatTestValues.DataIdentity(), gate.DataStoreId);
        Assert.Equal(256u, gate.ReservedHeadIdUpperBound);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 47).ToArray();
        Assert.True(FormatGateCodec.TryWrite(gate, destination));
        Assert.Equal(golden, destination[..44]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[44..]);
    }

    [Fact]
    public void Gate_RejectsEveryShortPrefixTrailingBytesAndSingleBitMutation() {
        byte[] golden = Convert.FromHexString(FormatTestValues.GateHex);
        for (int length = 0; length < golden.Length; length++) {
            Assert.False(FormatGateCodec.TryRead(golden.AsSpan(0, length), out FormatGate gate));
            Assert.Equal(default, gate);
        }

        Assert.False(FormatGateCodec.TryRead([.. golden, 0], out FormatGate trailing));
        Assert.Equal(default, trailing);
        for (int bit = 0; bit < golden.Length * 8; bit++) {
            byte[] changed = golden.ToArray();
            changed[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(FormatGateCodec.TryRead(changed, out FormatGate gate));
            Assert.Equal(default, gate);
        }
    }

    [Theory]
    [InlineData(0)] // Unknown version, with CRC repaired independently of this codec.
    [InlineData(1)] // Zero HeadStoreId.
    [InlineData(2)] // Zero DataStoreId.
    [InlineData(3)] // Reserved upper bound Max.
    public void Gate_ValidCrcDoesNotAuthorizeInvalidFields(int mutation) {
        byte[] changed = Convert.FromHexString(FormatTestValues.GateHex);
        switch (mutation) {
            case 0: changed[0] = 1; break;
            case 1: changed.AsSpan(4, 16).Clear(); break;
            case 2: changed.AsSpan(20, 16).Clear(); break;
            case 3: changed.AsSpan(36, 4).Fill(0xFF); break;
        }

        RollingCrc.SealCodewordForward(changed);
        Assert.True(RollingCrc.CheckCodewordForward(changed));
        Assert.False(FormatGateCodec.TryRead(changed, out FormatGate gate));
        Assert.Equal(default, gate);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue - 1)]
    public void Gate_BothRolesMayHaveSameIdentityAndLegalBoundaryValues(uint upperBound) {
        StoreIdentity identity = FormatTestValues.Identity();
        FormatGate expected = new(identity, identity, upperBound);
        byte[] wire = new byte[44];
        Assert.True(FormatGateCodec.TryWrite(expected, wire));
        Assert.True(FormatGateCodec.TryRead(wire, out FormatGate actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RejectedGateWrite_DoesNotPartiallyModifyDestination() {
        StoreIdentity identity = FormatTestValues.Identity();
        FormatGate[] invalid = [default, new(default, identity, 0), new(identity, default, 0), new(identity, identity, uint.MaxValue)];
        foreach (FormatGate gate in invalid) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, 48).ToArray();
            Assert.False(FormatGateCodec.TryWrite(gate, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }

        FormatGate valid = new(identity, identity, 0);
        for (int length = 0; length < 44; length++) {
            byte[] destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(FormatGateCodec.TryWrite(valid, destination));
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        }
    }
}
