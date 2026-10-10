using Atelia.VersionStore.Internal.Format;
using Xunit;

namespace Atelia.VersionStore.Tests.Format;

public class StoreIdentityTests {
    [Fact]
    public void Identity_OwnsCanonicalBytesAndWritesOnlyExactPrefix() {
        byte[] source = Convert.FromHexString(FormatTestValues.IdentityHex);
        Assert.True(StoreIdentity.TryRead(source, out StoreIdentity identity));
        Array.Clear(source);
        byte[] destination = Enumerable.Repeat((byte)0xA5, 19).ToArray();
        Assert.True(identity.TryWrite(destination));
        Assert.Equal(Convert.FromHexString(FormatTestValues.IdentityHex), destination[..16]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5 }, destination[16..]);
        Assert.Equal(FormatTestValues.Identity(), identity);
        Assert.Equal(FormatTestValues.Identity().GetHashCode(), identity.GetHashCode());
    }

    [Fact]
    public void Identity_RequiresExactWidthAndNonzeroOpaqueValue() {
        for (int length = 0; length <= 17; length++) {
            byte[] source = new byte[length];
            if (length > 0) { source[^1] = 1; }
            bool success = StoreIdentity.TryRead(source, out StoreIdentity identity);
            Assert.Equal(length == 16, success);
            if (!success) { Assert.Equal(default, identity); }
        }

        Assert.False(StoreIdentity.TryRead(new byte[16], out StoreIdentity zero));
        Assert.Equal(default, zero);
        for (int index = 0; index < 16; index++) {
            byte[] sparse = new byte[16];
            sparse[index] = 1;
            Assert.True(StoreIdentity.TryRead(sparse, out _));
        }
    }

    [Fact]
    public void RejectedIdentityWrite_DoesNotChangeDestination() {
        byte[] destination = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        Assert.False(default(StoreIdentity).TryWrite(destination));
        Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
        for (int length = 0; length < 16; length++) {
            byte[] shortDestination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(FormatTestValues.Identity().TryWrite(shortDestination));
            Assert.All(shortDestination, value => Assert.Equal((byte)0xA5, value));
        }
    }

    [Fact]
    public void RandomIdentity_IsNonzeroAndCanonicallyEncodable() {
        StoreIdentity identity = StoreIdentity.CreateRandom();
        Assert.NotEqual(default, identity);
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(identity.TryWrite(bytes));
        Assert.True(StoreIdentity.TryRead(bytes, out StoreIdentity recovered));
        Assert.Equal(identity, recovered);
    }
}
