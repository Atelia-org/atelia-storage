using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public class LegacyArchiveTests {
    [Fact]
    public void ValidRbf1ArchiveIsRejectedByFrameStoreReadersWithoutRepair() {
        using var fixture = new PublicStoreFixture();
        FrameAddress address;
        using (var store = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary)) {
            address = store.Append(1, [1]).Unwrap();
            store.ConfirmDurable();
        }
        File.WriteAllBytes(fixture.Archive(1), "RBF1"u8.ToArray());
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(fixture.Archive(1), RbfCacheMode.Off));
        Assert.Throws<InvalidDataException>(() => RbfFile.OpenExisting(fixture.Archive(1), out _));
        // Opening discovers archive names; content qualification is deferred until read/inspection.
        using (var store = Store.OpenReadOnly(fixture.Root)) {
            // RBF rejects the unsupported header before FrameStore can qualify its own header.
            Assert.Throws<InvalidDataException>(() => store.ReadFrame(address));
            Assert.Throws<InvalidDataException>(() => store.Inventory(_ => Assert.Fail("No user frame is qualified.")));
            Assert.Throws<InvalidDataException>(() => store.Audit(_ => Assert.Fail("No file report is qualified.")));
        }
        fixture.AssertUnchanged(before);
    }
}
