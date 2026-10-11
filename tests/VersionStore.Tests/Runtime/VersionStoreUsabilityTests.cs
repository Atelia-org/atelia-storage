using System.Collections;
using System.Reflection;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;

namespace Atelia.VersionStore.Tests.Runtime;

public sealed class VersionStoreUsabilityTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CombinedGuardAcceptsHealthyModesWithoutDiscoveryOrConfirmation(bool readOnly, bool readOnlyData) {
        using var fixture = new RuntimeFixture(readOnly, readOnlyData);
        fixture.Operations.Clear();
        fixture.Store.EnsureUsable();
        Assert.Empty(fixture.Operations);
        Assert.Equal(readOnly, fixture.Store.IsReadOnly);
        Assert.Equal(readOnlyData, fixture.Data.IsReadOnly);
        Assert.Equal(0, fixture.Files.OpenCalls);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void CombinedGuardAllowsDirtyDataAndActiveBuilderWithoutCompletingIt() {
        using var fixture = new RuntimeFixture();
        fixture.Data.Append(1, [2]).Unwrap();
        using var builder = fixture.Data.BeginAppend(1, 0, out var address);
        var writer = builder.PayloadAndMeta;
        writer.GetSpan(1)[0] = 3;
        fixture.Operations.Clear();
        fixture.Store.EnsureUsable();
        Assert.Empty(fixture.Operations);
        writer.Advance(1);
        Assert.Equal(address, builder.EndAppend(2).Unwrap());
        fixture.Data.ConfirmDurable();
        using var frame = fixture.Data.ReadFrame(address).Unwrap();
        Assert.Equal(new byte[] { 3 }, frame.PayloadAndMeta.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedDataGuardFailureDoesNotFaultVsOrChangePureQueryAndDisposeBoundary(bool disposed) {
        using var fixture = new RuntimeFixture();
        RefId id = fixture.Seed(payload: [7]);
        RefBytesSnapshot saved = fixture.Store.ReadRef(id).Unwrap();
        byte[] identity = fixture.Store.VersionStoreId.ToArray();
        if (disposed) { fixture.Data.Dispose(); }
        else { FaultBorrowedData(fixture.Data); }
        fixture.Operations.Clear();
        if (disposed) {
            var native = Assert.Throws<ObjectDisposedException>(fixture.Data.EnsureUsable);
            var combined = Assert.Throws<ObjectDisposedException>(fixture.Store.EnsureUsable);
            Assert.Equal(native.ObjectName, combined.ObjectName);
            Assert.Equal(native.Message, combined.Message);
        }
        else {
            var native = Assert.Throws<InvalidOperationException>(fixture.Data.EnsureUsable);
            var combined = Assert.Throws<InvalidOperationException>(fixture.Store.EnsureUsable);
            Assert.Equal(native.Message, combined.Message);
        }
        Assert.Empty(fixture.Operations);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        Assert.Equal(identity, fixture.Store.VersionStoreId.ToArray());
        Assert.False(fixture.Store.IsReadOnly);
        Assert.Equal(17u, fixture.Store.ReservedRefIdUpperBound);
        Assert.Single(fixture.Store.ListRefs().Unwrap());
        Assert.Equal(saved.Revision, fixture.Store.ReadRevision(saved.Revision).Unwrap().Revision);
        Assert.Equal(saved.Revision, fixture.Store.ReadRef(id).Unwrap().Revision);
        Assert.Equal("VersionStore.ArgumentError", fixture.Store.ReadRevision(default).Error!.ErrorCode);
        fixture.Store.Dispose();
        fixture.Store.Dispose();
        Assert.Equal(1, fixture.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
        Assert.Equal("lock:close", fixture.Operations[^1]);
        Assert.Equal(new byte[] { 7 }, saved.Payload.ToArray());
    }

    [Fact]
    public void PublicationDataBarrierStillFaultsVsAndPreservesNotAttempted() {
        using var fixture = new RuntimeFixture();
        RefId id = fixture.Seed();
        RefRevision revision = fixture.Store.ReadRef(id).Unwrap().Revision;
        fixture.Data.Dispose();
        Assert.Throws<ObjectDisposedException>(fixture.Store.EnsureUsable);
        Assert.Equal(revision, fixture.Store.ReadRevision(revision).Unwrap().Revision);
        PublicationOutcome outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => fixture.Store.PublishRef(id, [8], out outcome));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, fixture.Files.Opened[1].AppendCalls);
        Assert.Equal(1, fixture.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
        Assert.Equal("lock:close", fixture.Operations[^1]);
        Assert.Throws<InvalidOperationException>(fixture.Store.EnsureUsable);
    }

    [Fact]
    public void VsFaultThenDisposeTakePriorityOverDataAndRevisionParametersWithoutIo() {
        using var fixture = new RuntimeFixture();
        RefId id = fixture.Seed();
        fixture.Store.ReadRef(id).Unwrap();
        fixture.Hooks.ConfirmDataDurable = () => throw new IOException("barrier");
        Assert.Throws<IOException>(() => fixture.Store.PublishRef(id, [2], out _));
        fixture.Data.Dispose();
        fixture.Operations.Clear();
        var fault = Assert.Throws<InvalidOperationException>(fixture.Store.EnsureUsable);
        Assert.Contains("VersionStore is faulted", fault.Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.ReadRevision(default));
        Assert.Empty(fixture.Operations);
        fixture.Store.Dispose();
        fixture.Operations.Clear();
        var closed = Assert.Throws<ObjectDisposedException>(fixture.Store.EnsureUsable);
        Assert.Equal(typeof(VersionStore).FullName, closed.ObjectName);
        Assert.Throws<ObjectDisposedException>(() => fixture.Store.ReadRevision(default));
        Assert.Empty(fixture.Operations);
    }

    private static void FaultBorrowedData(DataStore data) {
        data.Append(1, [9]).Unwrap();
        // A narrow test-only handle fault drives the real ConfirmDurable failure path;
        // it does not set lifecycle flags or simulate the native guard implementation.
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        object core = typeof(DataStore).GetField("_core", members)!.GetValue(data)!;
        var active = (IEnumerable)core.GetType().GetField("_active", members)!.GetValue(core)!;
        object entry = Assert.Single(active.Cast<object>());
        var file = (IRbfFile)entry.GetType().GetField("File", members)!.GetValue(entry)!;
        file.Dispose();
        Assert.Throws<ObjectDisposedException>(data.ConfirmDurable);
        Assert.Throws<InvalidOperationException>(data.EnsureUsable);
    }
}
