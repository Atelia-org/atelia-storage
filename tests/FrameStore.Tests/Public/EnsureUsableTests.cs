using Atelia.FrameStore.Tests.Runtime;
using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public sealed class EnsureUsableTests {
    [Fact]
    public void PublicFactoriesAllowWritableAndReadOnlyWithoutChangingFiles() {
        using var fixture = new PublicStoreFixture();
        using (var writer = Store.Create(fixture.Root)) {
            writer.EnsureUsable();
        }
        // Take byte snapshots only without an owner: the writer exclusively holds the lock file.
        var before = fixture.Snapshot();
        using (var writer = Store.Open(fixture.Root)) { writer.EnsureUsable(); }
        fixture.AssertUnchanged(before);
        using (var reader = Store.OpenReadOnly(fixture.Root)) { reader.EnsureUsable(); }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void GuardLeavesDirtyOutputAndActiveBuilderUntouched() {
        using var fixture = new RuntimeFixture();
        using var owner = Wrap(fixture);
        owner.Append(1, [7]).Unwrap();
        var builder = owner.BeginAppend(1, 0, out var early);
        var file = fixture.Files.Created[1];
        fixture.Operations.Clear();
        int flushes = file.FlushCalls;
        owner.EnsureUsable();
        Assert.Empty(fixture.Operations);
        Assert.Equal(flushes, file.FlushCalls);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        PublicStoreFixture.Write(builder, [8]);
        Assert.Equal(early, builder.EndAppend(2).Unwrap());
        owner.ConfirmDurable();
        Assert.Equal(flushes + 1, file.FlushCalls);
        using var read = owner.ReadFrame(early).Unwrap();
        Assert.Equal(new byte[] { 8 }, read.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void GuardDoesNotDrainStoppedFilesOrTouchBackend() {
        using var fixture = new RuntimeFixture(threshold: PublicStoreFixture.InitializationBoundary);
        using var owner = Wrap(fixture);
        owner.Append(1, [7]).Unwrap();
        fixture.Operations.Clear();
        owner.EnsureUsable();
        Assert.Empty(fixture.Operations);
        Assert.Equal(0, fixture.Files.Created[1].FlushCalls);
        owner.ConfirmDurable();
        Assert.Contains("archive:1", fixture.Operations);
    }

    [Fact]
    public void FaultedRejectsAndDisposedTakesPriorityWhilePropertiesRemainReadable() {
        using var fixture = new RuntimeFixture();
        using var owner = Wrap(fixture);
        byte[] identity = owner.StoreId.ToArray();
        owner.Append(1, [7]).Unwrap();
        var failure = new IOException("injected flush failure");
        fixture.Files.Created[1].FlushFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(owner.ConfirmDurable));
        fixture.Operations.Clear();
        Assert.Throws<InvalidOperationException>(owner.EnsureUsable);
        Assert.Empty(fixture.Operations);
        Assert.Equal(identity, owner.StoreId.ToArray());
        Assert.False(owner.IsReadOnly);
        owner.Dispose();
        fixture.Operations.Clear();
        Assert.Throws<ObjectDisposedException>(owner.EnsureUsable);
        Assert.Empty(fixture.Operations);
        Assert.Equal(identity, owner.StoreId.ToArray());
        Assert.False(owner.IsReadOnly);
    }

    [Fact]
    public void GuardDoesNotPreventHealthyBuilderCancellation() {
        using var fixture = new RuntimeFixture();
        using var owner = Wrap(fixture);
        var builder = owner.BeginAppend();
        owner.EnsureUsable();
        builder.Dispose();
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        owner.EnsureUsable();
        owner.Append(1, [9]).Unwrap();
    }

    private static Store Wrap(RuntimeFixture fixture) =>
        new(fixture.Core, fixture.Files.Identity, readOnly: false,
            new Dictionary<uint, RbfTailRecoveryReport>());
}
