using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public sealed class InspectionOwnerHandoffTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CallbackCanDisposeAndReopenForArchivalBeforeOldDirectoryEnumeratorUnwinds(bool audit, bool readOnly) {
        using var fixture = new PublicStoreFixture();
        var owner = Store.Create(fixture.Root);
        var address = owner.Append(7, [1, 2, 3]).Unwrap();
        owner.ConfirmDurable();
        if (readOnly) {
            owner.Dispose();
            owner = Store.OpenReadOnly(fixture.Root);
        }
        using (owner) {
            int callbacks = 0;
            void Visit() {
                callbacks++;
                owner.Dispose();
                // The old directory iterator is still on the scan stack. The new owner may
                // immediately move the scanned file because all old data handles and the lock are closed.
                using var next = Store.Open(fixture.Root, PublicStoreFixture.InitializationBoundary);
                Assert.False(File.Exists(fixture.Active(1)));
                Assert.True(File.Exists(fixture.Archive(1)));
                PublicStoreFixture.AssertFrame(next, address, 7, [1, 2, 3]);
                next.Append(8, [4]).Unwrap();
                next.ConfirmDurable();
            }
            if (audit) { Assert.Throws<ObjectDisposedException>(() => owner.Audit(_ => Visit())); }
            else { Assert.Throws<ObjectDisposedException>(() => owner.Inventory(_ => Visit())); }
            Assert.Equal(1, callbacks);
        }
        using var final = Store.OpenReadOnly(fixture.Root);
        Assert.Equal(2L, final.Audit(_ => { }).Unwrap());
        PublicStoreFixture.AssertFrame(final, address, 7, [1, 2, 3]);
    }
}
