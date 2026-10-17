using Atelia.HeadStore.Internal.Format;
using Xunit;

namespace Atelia.HeadStore.Tests;

internal static class HeadIdTestExtensions {
    internal static HeadId ForeignHeadId(HeadStore owner, uint localId = 1) {
        Assert.True(StoreIdentity.TryRead(owner.HeadStoreId, out var identity));
        return ForeignHeadId(identity, localId);
    }

    internal static HeadId ForeignHeadId(StoreIdentity identity, uint localId = 1) {
        byte[] bytes = new byte[16];
        identity.TryWrite(bytes);
        bytes[3] ^= 0x80; // Deterministically outside this owner's 24bit local range.
        Assert.True(StoreIdentity.TryRead(bytes, out var foreign));
        return HeadId.Create(foreign, localId);
    }

    internal static uint GetLocalId(this HeadId id, byte[] identityBytes) {
        Assert.True(StoreIdentity.TryRead(identityBytes, out var identity));
        return id.GetLocalId(identity);
    }

    internal static uint GetLocalId(this HeadId id, HeadStore owner) {
        Assert.True(StoreIdentity.TryRead(owner.HeadStoreId, out var identity));
        return id.GetLocalId(identity);
    }
}
