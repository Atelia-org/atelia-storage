using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.Data;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.ForkQueries;

public sealed class PublicForkQueryTests {
    [Fact]
    public void PublicForestIncludesBothDomainsExactSiblingPointsAndLayersAcrossColdReadOnlyOwners() {
        using var fixture = new ForkQueryFixture();
        IReadOnlyList<HeadOriginInfo> saved;
        var expected = new List<(HeadId Child, HeadId Source, SizedPtr Ticket)>();
        byte[] identity;
        using (var store = fixture.Create()) {
            identity = store.HeadStoreId.ToArray();
            HeadEntryId a0 = store.CreateHead(7, [1], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadEntryId a1 = store.PublishHead(a0.HeadId, [2, 3, 4], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadEntryId automaticRoot = store.CreateHead([9], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(33u, automaticRoot.HeadId.LocalId);
            HeadEntryId sameBytesCopy = store.CreateHead(11, [1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadId reservedSibling = store.ForkHead(12, a0, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadId automaticSibling = store.ForkHead(a0, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadId laterPointSibling = store.ForkHead(a1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadEntryId b1 = store.PublishHead(reservedSibling, [0xb1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadId grandchild = store.ForkHead(b1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            HeadId backwardsNumberedChild = store.ForkHead(13, automaticRoot, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.True(backwardsNumberedChild.LocalId < automaticRoot.HeadId.LocalId);
            HeadEntryId independentRoot = store.CreateHead(5, [5], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            expected.AddRange([
                (reservedSibling, a0.HeadId, a0.Ticket),
                (automaticSibling, a0.HeadId, a0.Ticket),
                (laterPointSibling, a1.HeadId, a1.Ticket),
                (grandchild, b1.HeadId, b1.Ticket),
                (backwardsNumberedChild, automaticRoot.HeadId, automaticRoot.Ticket)
            ]);
            saved = store.ListHeadOrigins(9).Unwrap();
            ForkQueryFixture.AssertEdges(saved, expected.ToArray());
            Assert.DoesNotContain(saved, edge => edge.ChildHeadId == a0.HeadId || edge.ChildHeadId == automaticRoot.HeadId
                || edge.ChildHeadId == independentRoot.HeadId || edge.ChildHeadId == sameBytesCopy.HeadId);
            AssertOwnedListSurface(saved);
            ForkQueryFixture.AssertEdges(store.ListHeadOrigins(int.MaxValue).Unwrap(), expected.ToArray());

            HeadId newChild = store.ForkHead(14, a1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            ForkQueryFixture.AssertEdges(saved, expected.ToArray());
            expected.Add((newChild, a1.HeadId, a1.Ticket));
            ForkQueryFixture.AssertEdges(store.ListHeadOrigins(10).Unwrap(), expected.ToArray());
        }
        fixture.Data.EnsureUsable();
        fixture.Data.Dispose();
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(identity, readOnly.HeadStoreId.ToArray());
            ForkQueryFixture.AssertEdges(readOnly.ListHeadOrigins(10).Unwrap(), expected.ToArray());
            AssertOwnedListSurface(readOnly.ListHeadOrigins(int.MaxValue).Unwrap());
        }
        // A previous call owns its array: another query, a creation, both owner closures and data
        // closure cannot replace its entries. Output order is deliberately not asserted.
        ForkQueryFixture.AssertEdges(saved, expected.Take(expected.Count - 1).ToArray());
    }

    [Fact]
    public void HistoryVisitorsQueryEveryDeclaredEdgeWithoutReplacingTheCurrentTemporaryHandle() {
        using var fixture = new ForkQueryFixture();
        HeadEntryId a0;
        HeadEntryId a1;
        HeadId b0;
        HeadEntryId b1;
        HeadId sibling;
        using (var store = fixture.Create()) {
            a0 = store.CreateHead(7, [1], out _).Unwrap();
            a1 = store.PublishHead(a0.HeadId, [2], out _).Unwrap();
            b0 = store.ForkHead(12, a1, out _).Unwrap();
            b1 = store.PublishHead(b0, [3], out _).Unwrap();
            sibling = store.ForkHead(a0, out _).Unwrap();
        }
        using var reopened = fixture.Open();
        var delivered = new List<HeadEntryId>();
        var heldLists = new List<IReadOnlyList<HeadOriginInfo>>();
        Assert.Equal(HeadHistoryEnd.Complete, reopened.ReadHeadHistory(b0, ForkQueryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            IReadOnlyList<HeadOriginInfo> edges = reopened.ListHeadOrigins(3).Unwrap();
            ForkQueryFixture.AssertEdges(edges, (b0, a1.HeadId, a1.Ticket), (sibling, a0.HeadId, a0.Ticket));
            heldLists.Add(edges);
            // A query must also leave the existing mutation guard in force.
            Assert.Equal("HeadStore.HistoryActive", reopened.CreateHead([4], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return true;
        }).Unwrap());
        Assert.Equal(new[] { b1, a1, a0 }, delivered);
        fixture.AssertReleased(7, 12, sibling.LocalId);
        HeadEntryId later = reopened.PublishHead(a0.HeadId, [5], out var afterHistory).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, afterHistory);
        Assert.NotEqual(a1, later);
        reopened.Dispose();
        foreach (IReadOnlyList<HeadOriginInfo> edges in heldLists) {
            ForkQueryFixture.AssertEdges(edges, (b0, a1.HeadId, a1.Ticket), (sibling, a0.HeadId, a0.Ticket));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveBudgetRejectsBeforeDiscoveringAnInvalidDirectItem(int maxHeads) {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        File.WriteAllBytes(Path.Combine(fixture.Root, "refs", ".unknown"), [0xff]);
        // The public RO factory does not discover all refs. The bad direct item makes an
        // accidental discovery observable without an internal I/O hook.
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal("HeadStore.ArgumentError", readOnly.ListHeadOrigins(maxHeads).Error!.ErrorCode);
        Assert.Throws<InvalidDataException>(() => { readOnly.ListHeadOrigins(1); });
    }

    [Fact]
    public void EmptyAndAllRootCollectionsSucceedAtExactBudgetAndRootsCountTowardsAnExceededBudget() {
        using var fixture = new ForkQueryFixture();
        using var store = fixture.Create();
        Assert.Empty(store.ListHeadOrigins(1).Unwrap());
        AssertOwnedListSurface(store.ListHeadOrigins(int.MaxValue).Unwrap());
        store.CreateHead(7, [1], out _).Unwrap();
        store.CreateHead(12, [1], out _).Unwrap();
        store.CreateHead([1], out _).Unwrap();
        Assert.Empty(store.ListHeadOrigins(3).Unwrap());
        Assert.Empty(store.ListHeadOrigins(int.MaxValue).Unwrap());
        var limited = store.ListHeadOrigins(2);
        Assert.True(limited.IsFailure);
        Assert.Equal("HeadStore.ForkQueryBudgetExceeded", limited.Error!.ErrorCode);
        // A pure budget refusal does not fault a healthy writer or consume a target.
        Assert.Equal(13u, store.CreateHead(13, [2], out var outcome).Unwrap().HeadId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Empty(store.ListHeadOrigins(4).Unwrap());
    }

    [Fact]
    public void PrivatePendingIsExcludedAndReadOnlyQueryLeavesAllPersistedBytesUnchanged() {
        using var fixture = new ForkQueryFixture();
        HeadEntryId root;
        HeadId child;
        using (var store = fixture.Create()) {
            root = store.CreateHead(7, [1], out _).Unwrap();
            child = store.ForkHead(root, out _).Unwrap();
        }
        byte[] rootBytes = File.ReadAllBytes(fixture.HeadPath(root.HeadId.LocalId));
        byte[] childBytes = File.ReadAllBytes(fixture.HeadPath(child.LocalId));
        File.WriteAllBytes(fixture.PendingPath, childBytes);
        using (var readOnly = fixture.Open(readOnly: true)) {
            ForkQueryFixture.AssertEdges(readOnly.ListHeadOrigins(2).Unwrap(), (child, root.HeadId, root.Ticket));
        }
        Assert.Equal(rootBytes, File.ReadAllBytes(fixture.HeadPath(root.HeadId.LocalId)));
        Assert.Equal(childBytes, File.ReadAllBytes(fixture.HeadPath(child.LocalId)));
        Assert.Equal(childBytes, File.ReadAllBytes(fixture.PendingPath));
    }

    [Fact]
    public void DisposedOwnerRejectsBeforeInvalidBudgetAndBorrowedDataRemainsUsable() {
        using var fixture = new ForkQueryFixture();
        using var store = fixture.Create();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { store.ListHeadOrigins(0); });
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void HeadOriginInfoSurfaceIsReadonlyFlatAndDoesNotSignAHeadEntryIdOrExposeRecordOperations() {
        Type type = typeof(HeadOriginInfo);
        Assert.True(type.IsValueType);
        Assert.NotNull(type.GetCustomAttribute<IsReadOnlyAttribute>());
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "ChildHeadId", "SourceEntryTicket", "SourceHeadId" }, properties.Select(property => property.Name).Order());
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(HeadId), type.GetProperty("ChildHeadId")!.PropertyType);
        Assert.Equal(typeof(HeadId), type.GetProperty("SourceHeadId")!.PropertyType);
        Assert.Equal(typeof(SizedPtr), type.GetProperty("SourceEntryTicket")!.PropertyType);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.False(typeof(IEquatable<HeadOriginInfo>).IsAssignableFrom(type));
        foreach (string absent in new[] { "Deconstruct", "IsValid", "TryRead", "TryWrite", "op_Equality", "op_Inequality" }) {
            Assert.Null(type.GetMethod(absent, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        }
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name is "Equals" or "GetHashCode");
        HeadOriginInfo empty = default;
        Assert.Equal(default, empty.ChildHeadId);
        Assert.Equal(default, empty.SourceHeadId);
        Assert.Equal(default, empty.SourceEntryTicket);
    }

    private static void AssertOwnedListSurface(IReadOnlyList<HeadOriginInfo> result) {
        Assert.NotNull(result);
        Assert.False(result is HeadOriginInfo[]);
        Assert.False(result is IList<HeadOriginInfo>);
        Assert.False(result is ICollection<HeadOriginInfo>);
        Assert.False(result is IList);
        Assert.False(result is ICollection); // No ICollection.SyncRoot path to the backing array.
        Assert.Equal(result.Count, result.ToArray().Length);
        Assert.Equal(result.Count, ((IEnumerable)result).Cast<HeadOriginInfo>().Count());
    }
}
