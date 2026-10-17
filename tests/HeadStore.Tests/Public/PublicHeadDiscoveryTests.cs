using Xunit;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

public sealed class PublicHeadDiscoveryTests {
    [Fact]
    public void BadCanonicalContentsStillOccupyHighWatermarkWithoutBeingOpened() {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        File.WriteAllBytes(fixture.HeadPath(300), [0xff]);
        File.WriteAllBytes(fixture.PendingPath, [0xff, 0x00]);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.Equal(new uint[] { 7, 300 }, writer.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)).Order());
            Assert.Equal(20u, writer.CreateHead(20, [2], out var outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(301u, writer.CreateHead([3], out outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new uint[] { 7, 20, 300, 301 }, writer.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)).Order());
        }
        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(fixture.HeadPath(300)));
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(300u); });
            Assert.Equal(new byte[] { 0xff }, readOnly.ReadHead(7u).Unwrap().Payload.ToArray());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(302u, cold.CreateHead([4], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
    }

    [Fact]
    public void ReadOnlyColdOpenDoesNotDiscoverUnrelatedInvalidHeadNamesOrCancelPending() {
        using var fixture = new PublicHeadStoreFixture();
        HeadId healthy = fixture.SeedClosed().HeadId;
        string unexpected = Path.Combine(fixture.Root, "refs", ".unknown");
        File.WriteAllBytes(unexpected, [0x65]);
        File.WriteAllBytes(fixture.PendingPath, [0xfe, 0xed]);
        var before = fixture.CaptureFiles();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Equal(healthy, readOnly.ReadHead(7u).Unwrap().Id.HeadId);
            Assert.Throws<InvalidDataException>(() => { readOnly.ListHeads(); });
            Assert.Equal(new byte[] { 0xff }, readOnly.ReadHead(healthy).Unwrap().Payload.ToArray());
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
        using var fixture = new PublicHeadStoreFixture();
        HeadId healthy = fixture.SeedClosed().HeadId;
        // A full formally valid ref image is as uncommitted here as an arbitrary prefix.
        byte[] pending = length < 0 ? File.ReadAllBytes(fixture.HeadPath(7)) : Enumerable.Repeat((byte)0xff, length).ToArray();
        File.WriteAllBytes(fixture.PendingPath, pending);
        var before = fixture.CaptureFiles();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Equal(new[] { healthy }, readOnly.ListHeads().Unwrap());
            Assert.Equal(healthy, readOnly.ReadHead(7u).Unwrap().Id.HeadId);
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.False(File.Exists(fixture.PendingPath));
            Assert.Equal(new[] { healthy }, writer.ListHeads().Unwrap());
            Assert.Equal(257u, writer.CreateHead([0xfe], out var outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        Assert.Equal(before[Path.Combine("refs", "00000007.rbf")], File.ReadAllBytes(fixture.HeadPath(7)));
    }

    [Theory]
    [InlineData("Pending.rbf")]
    [InlineData(".pending.rbf")]
    [InlineData("other.rbf")]
    public void WorkspaceUnknownItemsAreRetainedAndRejectedInBothModes(string name) {
        using var fixture = new PublicHeadStoreFixture();
        fixture.SeedClosed();
        File.WriteAllBytes(Path.Combine(fixture.Root, "creating", name), [0xff]);
        var before = fixture.CaptureFiles();
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
        Assert.Throws<InvalidDataException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void ReservedDomainIsNotPrecreatedAndSpecifiedCreationNeverAdvancesAutomaticNumber() {
        using var fixture = new PublicHeadStoreFixture();
        using (var store = Store.Create(fixture.Root, fixture.Data, 256)) {
            Assert.Empty(store.ListHeads().Unwrap());
            Assert.Equal(7u, store.CreateHead(7, [7], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(257u, store.CreateHead([1], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(20u, store.CreateHead(20, [2], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(258u, store.CreateHead([3], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(new uint[] { 7, 20, 257, 258 }, store.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)).Order());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(259u, cold.CreateHead([4], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
    }

    [Fact]
    public void ZeroReservationAllowsAutomaticOneAndRejectsAllSpecifiedCreation() {
        using var fixture = new PublicHeadStoreFixture();
        using var store = Store.Create(fixture.Root, fixture.Data);
        Assert.Equal(0u, store.ReservedHeadIdUpperBound);
        Assert.True(store.CreateHead(1, [1], out var outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.True(store.CreateHead(0, [1], out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(store.ListHeads().Unwrap());
        Assert.Equal(1u, store.CreateHead([1], out outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void LastAutomaticIdDoesNotWrapAndExhaustionLeavesReservedSlotsUsableAfterColdOpen() {
        using var fixture = new PublicHeadStoreFixture();
        using (var store = Store.Create(fixture.Root, fixture.Data, HeadStore.MaxLocalHeadId - 1)) {
            Assert.Equal(HeadStore.MaxLocalHeadId, store.CreateHead([0xff], out var outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal("HeadStore.HeadIdExhausted", store.CreateHead([1], out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.CreateHead(HeadStore.MaxLocalHeadId, [1], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(1u, store.CreateHead(1, [2], out outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new uint[] { 1, HeadStore.MaxLocalHeadId }, store.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)).Order());
        }
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal("HeadStore.HeadIdExhausted", cold.CreateHead([3], out var coldOutcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, coldOutcome);
        Assert.Equal(2u, cold.CreateHead(2, [4], out coldOutcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
        Assert.Equal(PublicationOutcome.Confirmed, coldOutcome);
    }

    [Fact]
    public void ListHeadsReturnsOwnedReadOnlyUnsignedPathIdentitiesWithoutReadingContents() {
        using var fixture = new PublicHeadStoreFixture();
        using (var empty = Store.Create(fixture.Root, fixture.Data, 256)) { }
        uint[] numbers = [1, 256, 0x800000, HeadStore.MaxLocalHeadId];
        foreach (uint localId in numbers) { File.WriteAllBytes(fixture.HeadPath(localId), [0xff]); }
        IReadOnlyList<HeadId> result;
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            result = store.ListHeads().Unwrap();
            Assert.Equal(numbers, result.Select(id => id.GetLocalId(fixture.Identity)).Order());
            if (result is IList<HeadId> collection) {
                Assert.True(collection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => collection.Clear());
            }
            Assert.Equal("HeadStore.HeadIdExhausted", store.CreateHead([1], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.Equal(numbers, result.Select(id => id.GetLocalId(fixture.Identity)).Order());
        using var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data);
        Assert.Equal(result.OrderBy(id => id.GetLocalId(fixture.Identity)), readOnly.ListHeads().Unwrap().OrderBy(id => id.GetLocalId(fixture.Identity)));
    }

    [Fact]
    public void ListHeadsCannotExposeBackingArrayThroughCollectionSyncRoot() {
        using var fixture = new PublicHeadStoreFixture();
        IReadOnlyList<HeadId> result;
        HeadId first;
        HeadId second;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            first = store.CreateHead([1], out _).Unwrap().HeadId;
            second = store.CreateHead([2], out _).Unwrap().HeadId;
            result = store.ListHeads().Unwrap();
            Assert.False(result is HeadId[]);
            if (result is System.Collections.ICollection collection) {
                // Array.AsReadOnly exposes the actual array here despite rejecting IList writes.
                Assert.False(collection.SyncRoot is HeadId[]);
            }
            if (result is IList<HeadId> mutableProjection) {
                Assert.True(mutableProjection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => { mutableProjection[0] = default; });
            }
            Assert.True(new HashSet<HeadId> { first, second }.SetEquals(result));
        }
        Assert.True(new HashSet<HeadId> { first, second }.SetEquals(result));
        Assert.True(new HashSet<HeadId> { first, second }.SetEquals(
            ((System.Collections.IEnumerable)result).Cast<HeadId>()));
    }
}
