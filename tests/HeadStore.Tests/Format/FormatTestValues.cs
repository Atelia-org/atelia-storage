using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests.Format;

internal static class FormatTestValues {
    internal const string IdentityHex = "0102030405060708090A0B0C0D0E0F10";
    internal const string DataIdentityHex = "1112131415161718191A1B1C1D1E1F20";
    // Independent bitwise CRC32C over first 40 bytes: 0xcedeeb0a, stored LE.
    internal const string GateHex = "040000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20000100000AEBDECE";
    internal const string HeaderHex = "040000000102030405060708090A0B0C0D0E0F10EFCDAB00";
    // Source local=0x00543210, raw Packed for offset=60 / length=40 is 0x3c00000a; not Serialize().
    internal const string ForkHeaderHex = HeaderHex + "103254000A00003C00000000";

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
