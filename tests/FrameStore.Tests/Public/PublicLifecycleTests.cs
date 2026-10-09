using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

public sealed class PublicLifecycleTests {
    [Fact]
    public void EmptyCreateColdReopenAndFirstAllocationPreserveIdentity() {
        using var fixture = new PublicStoreFixture();
        byte[] identity;
        using (var store = Store.Create(fixture.Root)) {
            identity = store.StoreId.ToArray();
            Assert.Equal(16, identity.Length);
            Assert.Contains(identity, b => b != 0);
            Assert.False(store.IsReadOnly);
            Assert.Empty(store.RecoveryReports);
            store.ConfirmDurable();
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "active")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "archive")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "creating")));
        }
        using (var readOnly = Store.OpenReadOnly(fixture.Root)) {
            Assert.True(readOnly.IsReadOnly);
            Assert.Equal(identity, readOnly.StoreId.ToArray());
            Assert.Empty(readOnly.RecoveryReports);
        }
        FrameAddress first;
        using (var reopened = Store.Open(fixture.Root)) {
            Assert.Equal(identity, reopened.StoreId.ToArray());
            Assert.Empty(reopened.RecoveryReports);
            first = reopened.Append(0, [1, 2], [3]).Unwrap();
            Assert.Equal(1u, PublicStoreFixture.FileId(first));
            reopened.ConfirmDurable();
        }
        using var cold = Store.OpenReadOnly(fixture.Root);
        PublicStoreFixture.AssertFrame(cold, first, 0, [1, 2, 3], 1);
    }

    [Fact]
    public void InterleavedBuildersBuildingBarrierAndColdAddressesRemainIndependent() {
        using var fixture = new PublicStoreFixture();
        FrameAddress first;
        FrameAddress second;
        FrameAddress sync;
        using (var store = Store.Create(fixture.Root)) {
            var a = store.BeginAppend(3, 1, out var early);
            var oldWriter = a.PayloadAndMeta;
            var b = store.BeginAppend();
            PublicStoreFixture.Write(b, [5, 6]);
            second = b.EndAppend(2).Unwrap();
            Assert.Equal(2u, PublicStoreFixture.FileId(second));
            // Synchronous Append reuses completed file 2 while file 1 remains exclusively leased.
            sync = store.Append(3, [7]).Unwrap();
            Assert.Equal(2u, PublicStoreFixture.FileId(sync));
            PublicStoreFixture.AssertFrame(store, second, 2, [5, 6]);
            store.ConfirmDurable();
            Assert.Throws<InvalidOperationException>(() => store.ReadFrame(early));
            PublicStoreFixture.Write(a, [1, 2, 3, 4]);
            first = a.EndAppend(1).Unwrap();
            Assert.Equal(early, first);
            Assert.True(a.EndAppend(1).IsFailure);
            Assert.Throws<InvalidOperationException>(() => oldWriter.Advance(0));
            a.Dispose();
            b.Dispose();
            store.ConfirmDurable();
        }
        using var cold = Store.OpenReadOnly(fixture.Root);
        PublicStoreFixture.AssertFrame(cold, first, 1, [1, 2, 3, 4], 1);
        PublicStoreFixture.AssertFrame(cold, second, 2, [5, 6]);
        PublicStoreFixture.AssertFrame(cold, sync, 3, [7]);
    }

    [Fact]
    public void LeasedCompletedPrefixRemainsReadableAndConfirmDoesNotEndBuilder() {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        var completed = store.Append(8, [1, 2]).Unwrap();
        var builder = store.BeginAppend(1, 0, out var early);
        Assert.Equal(PublicStoreFixture.FileId(completed), PublicStoreFixture.FileId(early));
        var writer = builder.PayloadAndMeta;
        writer.GetMemory(1).Span[0] = 3;
        store.ConfirmDurable(); // A borrowed buffer remains borrowed and the Builder remains active.
        PublicStoreFixture.AssertFrame(store, completed, 8, [1, 2]);
        Assert.Throws<InvalidOperationException>(() => builder.EndAppend(9));
        writer.Advance(1);
        Assert.Equal(early, builder.EndAppend(9).Unwrap());
        store.ConfirmDurable();
        PublicStoreFixture.AssertFrame(store, early, 9, [3]);
    }

    [Fact]
    public void ArchiveMaintenanceHappensAfterSuccessfulReturnAndPreservesColdAddresses() {
        using var fixture = new PublicStoreFixture();
        FrameAddress first;
        FrameAddress second;
        using (var store = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary)) {
            first = store.Append(uint.MaxValue, [1]).Unwrap();
            Assert.True(File.Exists(fixture.Active(1)));
            Assert.False(File.Exists(fixture.Archive(1)));
            PublicStoreFixture.AssertFrame(store, first, uint.MaxValue, [1]);
            second = store.Append(0, [2]).Unwrap();
            Assert.True(File.Exists(fixture.Archive(1)));
            Assert.False(File.Exists(fixture.Active(1)));
            Assert.Equal(2u, PublicStoreFixture.FileId(second));
            store.ConfirmDurable();
            Assert.True(File.Exists(fixture.Archive(2)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Root, "active")));
        }
        using (var reopened = Store.Open(fixture.Root)) {
            PublicStoreFixture.AssertFrame(reopened, first, uint.MaxValue, [1]);
            PublicStoreFixture.AssertFrame(reopened, second, 0, [2]);
            var next = reopened.Append(3, [3]).Unwrap();
            Assert.Equal(3u, PublicStoreFixture.FileId(next)); // A higher threshold never reopens archives for writing.
        }
        using var cold = Store.OpenReadOnly(fixture.Root);
        PublicStoreFixture.AssertFrame(cold, first, uint.MaxValue, [1]);
        PublicStoreFixture.AssertFrame(cold, second, 0, [2]);
    }

    [Fact]
    public void ReadResultOutlivesDisposedOwnerAndControlLockIsReleased() {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        var address = store.Append(7, [1, 2], [3]).Unwrap();
        using var read = store.ReadFrame(address).Unwrap();
        store.Dispose();
        store.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, read.PayloadAndMeta.ToArray());
        using (var nextOwner = Store.Open(fixture.Root)) { PublicStoreFixture.AssertFrame(nextOwner, address, 7, [1, 2, 3], 1); }
        read.Dispose();
        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { _ = read.PayloadAndMeta.Length; });
        Assert.Throws<ObjectDisposedException>(() => store.ReadFrame(default));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "framestore.lock")));
        Assert.Equal(0, new FileInfo(Path.Combine(fixture.Root, "framestore.lock")).Length);
    }

    [Fact]
    public void ReadOnlyRejectsMutationAndOwnerLockCoordinatesAllModes() {
        using var fixture = new PublicStoreFixture();
        using (var writer = Store.Create(fixture.Root)) {
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root); });
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root); });
        }
        var before = fixture.Snapshot();
        using (var first = Store.OpenReadOnly(fixture.Root)) {
            using var second = Store.OpenReadOnly(fixture.Root);
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root); });
            Assert.Throws<InvalidOperationException>(() => first.Append(0, []));
            Assert.Throws<InvalidOperationException>(() => first.BeginAppend());
            Assert.Throws<InvalidOperationException>(() => first.BeginAppend(0, 0, out _));
            Assert.Throws<InvalidOperationException>(() => first.ConfirmDurable());
            Assert.True(first.ReadFrame(default).IsFailure);
        }
        fixture.AssertUnchanged(before);
        using var reopened = Store.Open(fixture.Root);
    }

    [Fact]
    public void EqualThresholdAllowsAnotherAppendAndReopenRecomputesUnarchivedStoppedState() {
        using var fixture = new PublicStoreFixture();
        long firstTail = PublicStoreFixture.InitializationBoundary + RbfFile.MeasureWriteSize(1).Unwrap().AppendLength;
        FrameAddress first;
        FrameAddress second;
        using (var store = Store.Create(fixture.Root, firstTail)) {
            first = store.Append(1, [1]).Unwrap();
            second = store.Append(2, [2]).Unwrap();
            Assert.Equal(1u, PublicStoreFixture.FileId(second)); // Exactly equal was still available.
        }
        Assert.False(File.Exists(fixture.Archive(1))); // Dispose performs no maintenance.
        using (var higher = Store.Open(fixture.Root)) {
            var third = higher.Append(3, [3]).Unwrap();
            Assert.Equal(1u, PublicStoreFixture.FileId(third)); // Old in-memory stopped state is not persistent.
        }
        using (var lower = Store.Open(fixture.Root, PublicStoreFixture.InitializationBoundary)) {
            Assert.True(File.Exists(fixture.Archive(1))); // Lower threshold drains before returning the owner.
            Assert.False(File.Exists(fixture.Active(1)));
            PublicStoreFixture.AssertFrame(lower, first, 1, [1]);
            PublicStoreFixture.AssertFrame(lower, second, 2, [2]);
        }
    }

    [Fact]
    public void BuilderConfigurationRejectsBeforeOutputButFullQuotaAllowsBufferAppend() {
        using var fixture = new PublicStoreFixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "framestore.config.json"), "{\"MaxOutstandingBuilders\":1}");
        using (var store = Store.Create(fixture.Root)) {
            var builder = store.BeginAppend(1, 0, out var first);
            Assert.Throws<InvalidOperationException>(() => store.BeginAppend());
            Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "active")));
            var appended = store.Append(2, [2]).Unwrap();
            Assert.Equal(2u, PublicStoreFixture.FileId(appended));
            PublicStoreFixture.Write(builder, [1]);
            Assert.Equal(first, builder.EndAppend(1).Unwrap());
            using var reused = store.BeginAppend(0, 0, out var next);
            Assert.Equal(1u, PublicStoreFixture.FileId(next));
            store.ConfirmDurable();
        }
        File.WriteAllText(Path.Combine(fixture.Root, "framestore.config.json"), "bad json");
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root); });
        using (var readOnly = Store.OpenReadOnly(fixture.Root)) { Assert.True(readOnly.IsReadOnly); }
        fixture.AssertUnchanged(before);
    }
}
