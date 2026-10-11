using Xunit;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

public sealed class PublicRefDiscoveryTests {
    [Fact]
    public void BadCanonicalContentsStillOccupyHighWatermarkWithoutBeingOpened() {
        using var fixture = new PublicVersionStoreFixture();
        fixture.SeedClosed();
        File.WriteAllBytes(fixture.RefPath(300), [0xff]);
        File.WriteAllBytes(fixture.PendingPath, [0xff, 0x00]);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.Equal(new uint[] { 7, 300 }, writer.ListRefs().Unwrap().Select(id => id.LocalId).Order());
            Assert.Equal(20u, writer.CreateRef(20, [2], out var outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(301u, writer.CreateRef([3], out outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new uint[] { 7, 20, 300, 301 }, writer.ListRefs().Unwrap().Select(id => id.LocalId).Order());
        }
        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(fixture.RefPath(300)));
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(300u); });
            Assert.Equal(new byte[] { 0xff }, readOnly.ReadRef(7u).Unwrap().Payload.ToArray());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(302u, cold.CreateRef([4], out _).Unwrap().RefId.LocalId);
    }

    [Fact]
    public void ReadOnlyColdOpenDoesNotDiscoverUnrelatedInvalidRefNamesOrCancelPending() {
        using var fixture = new PublicVersionStoreFixture();
        RefId healthy = fixture.SeedClosed().RefId;
        string unexpected = Path.Combine(fixture.Root, "refs", ".unknown");
        File.WriteAllBytes(unexpected, [0x65]);
        File.WriteAllBytes(fixture.PendingPath, [0xfe, 0xed]);
        var before = fixture.Snapshot();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Equal(healthy, readOnly.ReadRef(7u).Unwrap().Revision.RefId);
            Assert.Throws<InvalidDataException>(() => { readOnly.ListRefs(); });
            Assert.Equal(new byte[] { 0xff }, readOnly.ReadRef(healthy).Unwrap().Payload.ToArray());
        }
        fixture.AssertUnchanged(before);
        Assert.Throws<InvalidDataException>(() => { using var unexpectedOwner = Store.Open(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
        fixture.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(-1)]
    public void PendingBytesAreNeverAdoptedReadOnlyKeepsThemAndWriterOnlyCancels(int length) {
        using var fixture = new PublicVersionStoreFixture();
        RefId healthy = fixture.SeedClosed().RefId;
        // A full formally valid ref image is as uncommitted here as an arbitrary prefix.
        byte[] pending = length < 0 ? File.ReadAllBytes(fixture.RefPath(7)) : Enumerable.Repeat((byte)0xff, length).ToArray();
        File.WriteAllBytes(fixture.PendingPath, pending);
        var before = fixture.Snapshot();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Equal(new[] { healthy }, readOnly.ListRefs().Unwrap());
            Assert.Equal(healthy, readOnly.ReadRef(7u).Unwrap().Revision.RefId);
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.Equal(new[] { healthy }, writer.ListRefs().Unwrap());
            Assert.Equal(257u, writer.CreateRef([0xfe], out var outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        Assert.Equal(before[Path.Combine("refs", "00000007.rbf")], File.ReadAllBytes(fixture.RefPath(7)));
    }

    [Theory]
    [InlineData("Pending.rbf")]
    [InlineData(".pending.rbf")]
    [InlineData("other.rbf")]
    public void WorkspaceUnknownItemsAreRetainedAndRejectedInBothModes(string name) {
        using var fixture = new PublicVersionStoreFixture();
        fixture.SeedClosed();
        File.WriteAllBytes(Path.Combine(fixture.Root, "creating", name), [0xff]);
        var before = fixture.Snapshot();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void ReservedDomainIsNotPrecreatedAndSpecifiedCreationNeverAdvancesAutomaticNumber() {
        using var fixture = new PublicVersionStoreFixture();
        using (var store = Store.Create(fixture.Root, fixture.Data, 256)) {
            Assert.Empty(store.ListRefs().Unwrap());
            Assert.Equal(7u, store.CreateRef(7, [7], out _).Unwrap().RefId.LocalId);
            Assert.Equal(257u, store.CreateRef([1], out _).Unwrap().RefId.LocalId);
            Assert.Equal(20u, store.CreateRef(20, [2], out _).Unwrap().RefId.LocalId);
            Assert.Equal(258u, store.CreateRef([3], out _).Unwrap().RefId.LocalId);
            Assert.Equal(new uint[] { 7, 20, 257, 258 }, store.ListRefs().Unwrap().Select(id => id.LocalId).Order());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(259u, cold.CreateRef([4], out _).Unwrap().RefId.LocalId);
    }

    [Fact]
    public void ZeroReservationAllowsAutomaticOneAndRejectsAllSpecifiedCreation() {
        using var fixture = new PublicVersionStoreFixture();
        using var store = Store.Create(fixture.Root, fixture.Data);
        Assert.Equal(0u, store.ReservedRefIdUpperBound);
        Assert.True(store.CreateRef(1, [1], out var outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.True(store.CreateRef(0, [1], out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(store.ListRefs().Unwrap());
        Assert.Equal(1u, store.CreateRef([1], out outcome).Unwrap().RefId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void LastAutomaticIdDoesNotWrapAndExhaustionLeavesReservedSlotsUsableAfterColdOpen() {
        using var fixture = new PublicVersionStoreFixture();
        using (var store = Store.Create(fixture.Root, fixture.Data, uint.MaxValue - 1)) {
            Assert.Equal(uint.MaxValue, store.CreateRef([0xff], out var outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal("VersionStore.RefIdExhausted", store.CreateRef([1], out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.CreateRef(uint.MaxValue, [1], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(1u, store.CreateRef(1, [2], out outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new uint[] { 1, uint.MaxValue }, store.ListRefs().Unwrap().Select(id => id.LocalId).Order());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal("VersionStore.RefIdExhausted", cold.CreateRef([3], out var coldOutcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, coldOutcome);
        Assert.Equal(2u, cold.CreateRef(2, [4], out coldOutcome).Unwrap().RefId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, coldOutcome);
    }

    [Fact]
    public void ListRefsReturnsOwnedReadOnlyUnsignedPathIdentitiesWithoutReadingContents() {
        using var fixture = new PublicVersionStoreFixture();
        using (var empty = Store.Create(fixture.Root, fixture.Data, 256)) { }
        uint[] numbers = [1, 256, 0x80000000, uint.MaxValue];
        foreach (uint localId in numbers) { File.WriteAllBytes(fixture.RefPath(localId), [0xff]); }
        IReadOnlyList<RefId> result;
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            result = store.ListRefs().Unwrap();
            Assert.Equal(numbers, result.Select(id => id.LocalId).Order());
            if (result is IList<RefId> collection) {
                Assert.True(collection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => collection.Clear());
            }
            Assert.Equal("VersionStore.RefIdExhausted", store.CreateRef([1], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.Equal(numbers, result.Select(id => id.LocalId).Order());
        using var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data);
        Assert.Equal(result.OrderBy(id => id.LocalId), readOnly.ListRefs().Unwrap().OrderBy(id => id.LocalId));
    }

    [Fact]
    public void ListRefsCannotExposeBackingArrayThroughCollectionSyncRoot() {
        using var fixture = new PublicVersionStoreFixture();
        IReadOnlyList<RefId> result;
        RefId first;
        RefId second;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            first = store.CreateRef([1], out _).Unwrap().RefId;
            second = store.CreateRef([2], out _).Unwrap().RefId;
            result = store.ListRefs().Unwrap();
            Assert.False(result is RefId[]);
            if (result is System.Collections.ICollection collection) {
                // Array.AsReadOnly exposes the actual array here despite rejecting IList writes.
                Assert.False(collection.SyncRoot is RefId[]);
            }
            if (result is IList<RefId> mutableProjection) {
                Assert.True(mutableProjection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => { mutableProjection[0] = default; });
            }
            Assert.True(new HashSet<RefId> { first, second }.SetEquals(result));
        }
        Assert.True(new HashSet<RefId> { first, second }.SetEquals(result));
        Assert.True(new HashSet<RefId> { first, second }.SetEquals(
            ((System.Collections.IEnumerable)result).Cast<RefId>()));
    }
}
