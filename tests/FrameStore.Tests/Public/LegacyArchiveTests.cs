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
        using (var rbf = RbfFile.OpenReadOnlyExisting(fixture.Archive(1), RbfCacheMode.Off)) {
            Assert.Equal(RbfFormat.Rbf1, rbf.Format);
        }
        var before = fixture.Snapshot();
        // Opening discovers archive names; content qualification is deferred until read/inspection.
        using (var store = Store.OpenReadOnly(fixture.Root)) {
            var read = store.ReadFrame(address);
            var inventory = store.Inventory(_ => Assert.Fail("No user frame is qualified."));
            var audit = store.Audit(_ => Assert.Fail("No file report is qualified."));
            foreach (var error in new[] { read.Error, inventory.Error, audit.Error }) {
                Assert.NotNull(error);
                Assert.Equal("FrameStore.InvalidHeader", error.ErrorCode);
                // Distinguish format rejection from merely rejecting this empty file's absent header.
                Assert.Equal("Data files must use RBF3.", error.Message);
            }
        }
        fixture.AssertUnchanged(before);
    }
}
