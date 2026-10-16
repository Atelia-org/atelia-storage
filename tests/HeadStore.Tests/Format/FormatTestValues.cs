using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Format;

internal static class FormatTestValues {
    internal const string IdentityHex = "0102030405060708090A0B0C0D0E0F10";
    internal const string DataIdentityHex = "1112131415161718191A1B1C1D1E1F20";
    // Independent contract golden: CRC32C over first 40 bytes is 0x6a2a319e, stored LE.
    internal const string GateHex = "010000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20000100009E312A6A";
    internal const string HeaderHex = "010000000102030405060708090A0B0C0D0E0F10EFCDAB89";
    // Source local=0x76543210, raw Packed for offset=60 / length=40 is 0x3c00000a; not Serialize().
    internal const string ForkHeaderHex = HeaderHex + "103254760A00003C00000000";

    internal static StoreIdentity Identity() {
        return ReadIdentity(IdentityHex);
    }

    internal static StoreIdentity DataIdentity() {
        return ReadIdentity(DataIdentityHex);
    }

    internal static StoreIdentity ReadIdentity(string hex) {
        Assert.True(StoreIdentity.TryRead(Convert.FromHexString(hex), out StoreIdentity identity));
        return identity;
    }
}
