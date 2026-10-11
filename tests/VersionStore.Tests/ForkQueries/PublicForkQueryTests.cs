using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.Data;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.ForkQueries;

public sealed class PublicForkQueryTests {
    [Fact]
    public void PublicForestIncludesBothDomainsExactSiblingPointsAndLayersAcrossColdReadOnlyOwners() {
        using var fixture = new ForkQueryFixture();
        IReadOnlyList<ForkInfo> saved;
        var expected = new List<(RefId Child, RefId Source, SizedPtr Ticket)>();
        byte[] identity;
        using (var store = fixture.Create()) {
            identity = store.VersionStoreId.ToArray();
            RefRevision a0 = store.CreateRef(7, [1], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision a1 = store.PublishRef(a0.RefId, [2, 3, 4], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision automaticRoot = store.CreateRef([9], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(33u, automaticRoot.RefId.LocalId);
            RefRevision sameBytesCopy = store.CreateRef(11, [1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision reservedSibling = store.ForkRef(12, a0, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision automaticSibling = store.ForkRef(a0, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision laterPointSibling = store.ForkRef(a1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision b1 = store.PublishRef(reservedSibling.RefId, [0xb1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision grandchild = store.ForkRef(b1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            RefRevision backwardsNumberedChild = store.ForkRef(13, automaticRoot, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.True(backwardsNumberedChild.RefId.LocalId < automaticRoot.RefId.LocalId);
            RefRevision independentRoot = store.CreateRef(5, [5], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            expected.AddRange([
                (reservedSibling.RefId, a0.RefId, a0.Ticket),
                (automaticSibling.RefId, a0.RefId, a0.Ticket),
                (laterPointSibling.RefId, a1.RefId, a1.Ticket),
                (grandchild.RefId, b1.RefId, b1.Ticket),
                (backwardsNumberedChild.RefId, automaticRoot.RefId, automaticRoot.Ticket)
            ]);
            saved = store.ListForks(9).Unwrap();
            ForkQueryFixture.AssertEdges(saved, expected.ToArray());
            Assert.DoesNotContain(saved, edge => edge.ChildRefId == a0.RefId || edge.ChildRefId == automaticRoot.RefId
                || edge.ChildRefId == independentRoot.RefId || edge.ChildRefId == sameBytesCopy.RefId);
            AssertOwnedListSurface(saved);
            ForkQueryFixture.AssertEdges(store.ListForks(int.MaxValue).Unwrap(), expected.ToArray());

            RefRevision newChild = store.ForkRef(14, a1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            ForkQueryFixture.AssertEdges(saved, expected.ToArray());
            expected.Add((newChild.RefId, a1.RefId, a1.Ticket));
            ForkQueryFixture.AssertEdges(store.ListForks(10).Unwrap(), expected.ToArray());
        }
        fixture.Data.EnsureUsable();
        fixture.Data.Dispose();
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.Equal(identity, readOnly.VersionStoreId.ToArray());
            ForkQueryFixture.AssertEdges(readOnly.ListForks(10).Unwrap(), expected.ToArray());
            AssertOwnedListSurface(readOnly.ListForks(int.MaxValue).Unwrap());
        }
        // A previous call owns its array: another query, a creation, both owner closures and data
        // closure cannot replace its entries. Output order is deliberately not asserted.
        ForkQueryFixture.AssertEdges(saved, expected.Take(expected.Count - 1).ToArray());
    }

    [Fact]
    public void HistoryVisitorsQueryEveryDeclaredEdgeWithoutReplacingTheCurrentTemporaryHandle() {
        using var fixture = new ForkQueryFixture();
        RefRevision a0;
        RefRevision a1;
        RefRevision b0;
        RefRevision b1;
        RefRevision sibling;
        using (var store = fixture.Create()) {
            a0 = store.CreateRef(7, [1], out _).Unwrap();
            a1 = store.PublishRef(a0.RefId, [2], out _).Unwrap();
            b0 = store.ForkRef(12, a1, out _).Unwrap();
            b1 = store.PublishRef(b0.RefId, [3], out _).Unwrap();
            sibling = store.ForkRef(a0, out _).Unwrap();
        }
        using var reopened = fixture.Open();
        var delivered = new List<RefRevision>();
        var heldLists = new List<IReadOnlyList<ForkInfo>>();
        Assert.Equal(HistoryEnd.Complete, reopened.ReadRefHistory(b0.RefId, ForkQueryFixture.Unlimited, snapshot => {
            delivered.Add(snapshot.Revision);
            IReadOnlyList<ForkInfo> edges = reopened.ListForks(3).Unwrap();
            ForkQueryFixture.AssertEdges(edges, (b0.RefId, a1.RefId, a1.Ticket), (sibling.RefId, a0.RefId, a0.Ticket));
            heldLists.Add(edges);
            // A query must also leave the existing mutation guard in force.
            Assert.Equal("VersionStore.HistoryActive", reopened.CreateRef([4], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            return true;
        }).Unwrap());
        Assert.Equal(new[] { b1, b0, a1, a0 }, delivered);
        fixture.AssertReleased(7, 12, sibling.RefId.LocalId);
        RefRevision later = reopened.PublishRef(a0.RefId, [5], out var afterHistory).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, afterHistory);
        Assert.NotEqual(a1, later);
        reopened.Dispose();
        foreach (IReadOnlyList<ForkInfo> edges in heldLists) {
            ForkQueryFixture.AssertEdges(edges, (b0.RefId, a1.RefId, a1.Ticket), (sibling.RefId, a0.RefId, a0.Ticket));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveBudgetRejectsBeforeDiscoveringAnInvalidDirectItem(int maxRefs) {
        using var fixture = new ForkQueryFixture();
        using (var initialized = fixture.Create()) { }
        File.WriteAllBytes(Path.Combine(fixture.Root, "refs", ".unknown"), [0xff]);
        // The public RO factory does not discover all refs. The bad direct item makes an
        // accidental discovery observable without an internal I/O hook.
        using var readOnly = fixture.Open(readOnly: true);
        Assert.Equal("VersionStore.ArgumentError", readOnly.ListForks(maxRefs).Error!.ErrorCode);
        Assert.Throws<InvalidDataException>(() => { readOnly.ListForks(1); });
    }

    [Fact]
    public void EmptyAndAllRootCollectionsSucceedAtExactBudgetAndRootsCountTowardsAnExceededBudget() {
        using var fixture = new ForkQueryFixture();
        using var store = fixture.Create();
        Assert.Empty(store.ListForks(1).Unwrap());
        AssertOwnedListSurface(store.ListForks(int.MaxValue).Unwrap());
        store.CreateRef(7, [1], out _).Unwrap();
        store.CreateRef(12, [1], out _).Unwrap();
        store.CreateRef([1], out _).Unwrap();
        Assert.Empty(store.ListForks(3).Unwrap());
        Assert.Empty(store.ListForks(int.MaxValue).Unwrap());
        var limited = store.ListForks(2);
        Assert.True(limited.IsFailure);
        Assert.Equal("VersionStore.ForkQueryBudgetExceeded", limited.Error!.ErrorCode);
        // A pure budget refusal does not fault a healthy writer or consume a target.
        Assert.Equal(13u, store.CreateRef(13, [2], out var outcome).Unwrap().RefId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Empty(store.ListForks(4).Unwrap());
    }

    [Fact]
    public void PrivatePendingIsExcludedAndReadOnlyQueryLeavesAllPersistedBytesUnchanged() {
        using var fixture = new ForkQueryFixture();
        RefRevision root;
        RefRevision child;
        using (var store = fixture.Create()) {
            root = store.CreateRef(7, [1], out _).Unwrap();
            child = store.ForkRef(root, out _).Unwrap();
        }
        byte[] rootBytes = File.ReadAllBytes(fixture.RefPath(root.RefId.LocalId));
        byte[] childBytes = File.ReadAllBytes(fixture.RefPath(child.RefId.LocalId));
        File.WriteAllBytes(fixture.PendingPath, childBytes);
        using (var readOnly = fixture.Open(readOnly: true)) {
            ForkQueryFixture.AssertEdges(readOnly.ListForks(2).Unwrap(), (child.RefId, root.RefId, root.Ticket));
        }
        Assert.Equal(rootBytes, File.ReadAllBytes(fixture.RefPath(root.RefId.LocalId)));
        Assert.Equal(childBytes, File.ReadAllBytes(fixture.RefPath(child.RefId.LocalId)));
        Assert.Equal(childBytes, File.ReadAllBytes(fixture.PendingPath));
    }

    [Fact]
    public void DisposedOwnerRejectsBeforeInvalidBudgetAndBorrowedDataRemainsUsable() {
        using var fixture = new ForkQueryFixture();
        using var store = fixture.Create();
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => { store.ListForks(0); });
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void ForkInfoSurfaceIsReadonlyFlatAndDoesNotSignARefRevisionOrExposeRecordOperations() {
        Type type = typeof(ForkInfo);
        Assert.True(type.IsValueType);
        Assert.NotNull(type.GetCustomAttribute<IsReadOnlyAttribute>());
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(new[] { "ChildRefId", "SourceRefId", "SourceSnapshotTicket" }, properties.Select(property => property.Name).Order());
        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.Equal(typeof(RefId), type.GetProperty("ChildRefId")!.PropertyType);
        Assert.Equal(typeof(RefId), type.GetProperty("SourceRefId")!.PropertyType);
        Assert.Equal(typeof(SizedPtr), type.GetProperty("SourceSnapshotTicket")!.PropertyType);
        Assert.False(typeof(IDisposable).IsAssignableFrom(type));
        Assert.False(typeof(IEquatable<ForkInfo>).IsAssignableFrom(type));
        foreach (string absent in new[] { "Deconstruct", "IsValid", "TryRead", "TryWrite", "op_Equality", "op_Inequality" }) {
            Assert.Null(type.GetMethod(absent, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        }
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name is "Equals" or "GetHashCode");
        ForkInfo empty = default;
        Assert.Equal(default, empty.ChildRefId);
        Assert.Equal(default, empty.SourceRefId);
        Assert.Equal(default, empty.SourceSnapshotTicket);
    }

    private static void AssertOwnedListSurface(IReadOnlyList<ForkInfo> result) {
        Assert.NotNull(result);
        Assert.False(result is ForkInfo[]);
        Assert.False(result is IList<ForkInfo>);
        Assert.False(result is ICollection<ForkInfo>);
        Assert.False(result is IList);
        Assert.False(result is ICollection); // No ICollection.SyncRoot path to the backing array.
        Assert.Equal(result.Count, result.ToArray().Length);
        Assert.Equal(result.Count, ((IEnumerable)result).Cast<ForkInfo>().Count());
    }
}
