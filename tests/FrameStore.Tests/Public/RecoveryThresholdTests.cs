using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public class RecoveryThresholdTests {
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    [InlineData(false, -1)]
    public void WritableOpenClassifiesAtRecoveredTailBeforeReturningOwner(bool completeTail, int thresholdDelta) {
        using var fixture = new PublicStoreFixture();
        FrameAddress address;
        using (var store = Store.Create(fixture.Root)) {
            address = store.Append(17, [1, 2, 3]).Unwrap();
            store.ConfirmDurable();
        }
        byte[] image = File.ReadAllBytes(fixture.Active(1));
        // Controlled residue, with no live owner: partial final Fence versus partial next HeadLen.
        byte[] residue = completeTail ? image[..^1] : [.. image, 1];
        File.WriteAllBytes(fixture.Active(1), residue);
        long threshold = image.Length + thresholdDelta;
        bool shouldArchive = thresholdDelta < 0;
        using (var reopened = Store.Open(fixture.Root, threshold)) {
            var report = reopened.RecoveryReports[1];
            Assert.Equal(completeTail ? RbfTailRecoveryAction.CompletedTail : RbfTailRecoveryAction.Truncated, report.Action);
            Assert.Equal(residue.Length, report.OriginalLength);
            Assert.Equal(image.Length, report.FinalLength);
            // Completion from T to T+1 must archive; truncation from T+1 to T must retain active.
            Assert.Equal(shouldArchive, File.Exists(fixture.Archive(1)));
            Assert.Equal(!shouldArchive, File.Exists(fixture.Active(1)));
            PublicStoreFixture.AssertFrame(reopened, address, 17, [1, 2, 3]);
            var next = reopened.Append(18, [4]).Unwrap();
            Assert.Equal(shouldArchive ? 2u : 1u, PublicStoreFixture.FileId(next));
            reopened.ConfirmDurable();
        }
        using var cold = Store.OpenReadOnly(fixture.Root);
        PublicStoreFixture.AssertFrame(cold, address, 17, [1, 2, 3]);
    }
}
