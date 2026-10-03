using Atelia.Rbf.ReadCache;
using Xunit;

namespace Atelia.Rbf.Internal.Tests;

public sealed class RbfReadMetricsTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rbf-metrics-{Guid.NewGuid()}.bin");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void DirectShortRead_SeparatesRequestsAndActualReturns() {
        File.WriteAllBytes(_path, new byte[16]);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new RandomAccessReader(handle);
        using var metrics = RbfReadMetrics.Begin();

        Assert.Equal(4, reader.Read(new byte[12], 12));

        Assert.Equal(new RbfReadMetricsSnapshot(1, 12, 4, 1, 12, 4, 0, 0), metrics.Snapshot());
    }

    [Fact]
    public void PageFill_CountsOnlyReturnedBytesOutsideLogicalRequest_AsOverfetch() {
        File.WriteAllBytes(_path, new byte[512]);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new ReverseReadCache(handle);
        using var metrics = RbfReadMetrics.Begin();

        Assert.Equal(20, reader.Read(new byte[20], 100));
        Assert.Equal(new RbfReadMetricsSnapshot(1, 20, 20, 1, 4096, 512, 492, 0), metrics.Snapshot());
        metrics.Reset();
        Assert.Equal(20, reader.Read(new byte[20], 100));
        Assert.Equal(new RbfReadMetricsSnapshot(1, 20, 20, 0, 0, 0, 0, 0), metrics.Snapshot());
    }

    [Fact]
    public void OpenHeader_IsIncludedBeforeFacadeIsReturned() {
        using (var created = RbfFile.CreateNew(_path)) { }
        using var metrics = RbfReadMetrics.Begin();

        using var opened = RbfFile.OpenReadOnlyExisting(_path);

        Assert.Equal(new RbfReadMetricsSnapshot(1, 4, 4, 1, 4, 4, 0, 1), metrics.Snapshot());
    }

    [Fact]
    public void NonemptyRbf3Open_ReadsBoundedTailStructure_WithoutAuditingPayloadOrHistory() {
        byte[] oldPayload = new byte[128 * 1024];
        using (var created = RbfFile.CreateNew(_path)) {
            for (int i = 0; i < 8; i++) { created.Append((uint)i, oldPayload).Unwrap(); }
            created.Append(99, new byte[16 * 1024]).Unwrap();
        }
        using var metrics = RbfReadMetrics.Begin();
        using var opened = RbfFile.OpenReadOnlyExisting(_path);
        var measured = metrics.Snapshot();
        Assert.Equal(1, measured.HeaderReadCalls);
        Assert.InRange(measured.ReadCalls, 1, 8);
        Assert.InRange(measured.RawReturnedBytes, 4, 64);
        Assert.Equal(measured.RawRequestedBytes, measured.RawReturnedBytes);
        Assert.Equal(0, measured.ReadAheadReturnedBytes);
    }

    [Fact]
    public void InvalidHeader_StillReportsReadAndScopeCanBeDisposed() {
        File.WriteAllBytes(_path, new byte[4]);
        var metrics = RbfReadMetrics.Begin();
        try {
            Assert.Throws<InvalidDataException>(() => RbfFile.OpenReadOnlyExisting(_path));
            Assert.Equal(new RbfReadMetricsSnapshot(1, 4, 4, 1, 4, 4, 0, 1), metrics.Snapshot());
        }
        finally {
            metrics.Dispose();
        }
        Assert.Null(RbfReadMetrics.Current);
    }

    [Fact]
    public void DisabledAndDisposedScopes_DoNotAccumulateOrRemainCurrent() {
        File.WriteAllBytes(_path, new byte[16]);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new RandomAccessReader(handle);
        Assert.Null(RbfReadMetrics.Current);
        Assert.Equal(4, reader.Read(new byte[4], 0));
        var metrics = RbfReadMetrics.Begin();
        Assert.Equal(4, reader.Read(new byte[4], 0));
        metrics.Dispose();
        var completed = metrics.Snapshot();
        Assert.Null(RbfReadMetrics.Current);
        Assert.Equal(4, reader.Read(new byte[4], 0));
        Assert.Equal(completed, metrics.Snapshot());
        metrics.Dispose();
        Assert.Throws<ObjectDisposedException>(() => metrics.Reset());
    }

    [Fact]
    public void NestedScopes_RestoreParentAndRejectOutOfOrderDisposal() {
        File.WriteAllBytes(_path, new byte[16]);
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new RandomAccessReader(handle);
        using var outer = RbfReadMetrics.Begin();
        Assert.Equal(4, reader.Read(new byte[4], 0));
        using (var inner = RbfReadMetrics.Begin()) {
            Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            Assert.Equal(4, reader.Read(new byte[4], 4));
            Assert.Equal(1, inner.Snapshot().ReadCalls);
            Assert.Equal(1, outer.Snapshot().ReadCalls);
        }
        Assert.Same(outer, RbfReadMetrics.Current);
        Assert.Equal(4, reader.Read(new byte[4], 8));
        Assert.Equal(2, outer.Snapshot().ReadCalls);
    }

    [Fact]
    public void OtherThread_HasIndependentScope() {
        using var metrics = RbfReadMetrics.Begin();
        RbfReadMetricsSnapshot otherSnapshot = default;
        bool startedWithoutScope = false;
        bool endedWithoutScope = false;
        var thread = new Thread(() => {
            startedWithoutScope = RbfReadMetrics.Current is null;
            using (var other = RbfReadMetrics.Begin()) {
                other.ReadRequested(7);
                other.ReadReturned(3);
                otherSnapshot = other.Snapshot();
            }
            endedWithoutScope = RbfReadMetrics.Current is null;
        });
        thread.Start();
        thread.Join();

        Assert.True(startedWithoutScope);
        Assert.True(endedWithoutScope);
        Assert.Equal(7, otherSnapshot.RequestedBytes);
        Assert.Equal(3, otherSnapshot.ReturnedBytes);
        Assert.Equal(default(RbfReadMetricsSnapshot), metrics.Snapshot());
        Assert.Same(metrics, RbfReadMetrics.Current);
    }
}
