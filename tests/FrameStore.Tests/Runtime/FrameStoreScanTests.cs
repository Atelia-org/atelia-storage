using Atelia.FrameStore.Internal.Format;
using Atelia.Rbf;
using Xunit;

namespace Atelia.FrameStore.Tests.Runtime;

public sealed class FrameStoreScanTests {
    [Fact]
    public void EmptyReadOnlyScans_ReturnZeroWithoutOpeningOrCreatingDataFiles() {
        using var fixture = new RuntimeFixture(writable: false);
        Assert.Equal(0L, fixture.Core.Inventory(_ => Assert.Fail("Empty Inventory has no frame callback.")).Unwrap());
        Assert.Equal(0L, fixture.Core.Audit(_ => Assert.Fail("Empty Audit has no file callback.")).Unwrap());
        Assert.Equal(2, fixture.Files.ScanVisitCalls);
        Assert.Equal(0, fixture.Files.ScanOpenCalls);
        Assert.Empty(fixture.Files.Created);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void Inventory_ReusesRetainedActiveSkipsOnlyHeaderAndAllowsRandomRead() {
        using var fixture = new RuntimeFixture();
        var first = fixture.Core.Append(0, [1, 2], [3]).Unwrap();
        var second = fixture.Core.Append(uint.MaxValue, []).Unwrap();
        var infos = new List<FrameInfo>();
        Assert.Equal(2L, fixture.Core.Inventory(info => {
            infos.Add(info);
            using var read = fixture.Core.ReadFrame(info.Address).Unwrap();
            Assert.Equal(info.Tag, read.Tag);
        }).Unwrap());
        Assert.Equal(new[] { first, second }, infos.Select(info => info.Address));
        Assert.Equal(0u, infos[0].Tag);
        Assert.Equal(2, infos[0].PayloadLength);
        Assert.Equal(1, infos[0].TailMetaLength);
        Assert.Equal(uint.MaxValue, infos[1].Tag);
        Assert.Equal(0, fixture.Files.ScanOpenCalls);
        Assert.Equal(0, fixture.Files.Created[1].DisposeCalls);
        Assert.Equal(0, fixture.Files.Created[1].FlushCalls);
        fixture.Core.Dispose();
        Assert.Equal(first, infos[0].Address);
        Assert.Equal(2, infos[0].PayloadLength);
    }

    [Fact]
    public void ScanAdmission_ChecksOwnerThenVisitorThenBuilderBeforeAnyDirectoryOrMaintenanceWork() {
        using var fixture = new RuntimeFixture(threshold: FileHeaderCodec.InitializationBoundary);
        var held = fixture.Core.BeginAppend();
        fixture.Core.Append(1, [1]).Unwrap(); // A stopped second file makes accidental maintenance observable.
        fixture.Operations.Clear();
        Assert.Throws<ArgumentNullException>(() => fixture.Core.Inventory(null!));
        Assert.Throws<ArgumentNullException>(() => fixture.Core.Audit(null!));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.Inventory(_ => { }));
        Assert.Throws<InvalidOperationException>(() => fixture.Core.Audit(_ => { }));
        Assert.Empty(fixture.Operations);
        Assert.Equal(0, fixture.Files.ScanVisitCalls);
        Assert.Equal(1, fixture.Core.OutstandingBuilders);
        Assert.False(fixture.Core.IsFaulted);
        held.Dispose();
        fixture.Core.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.Inventory(null!));
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.Audit(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackMutationAndRecursiveScanGuards_PrecedeMaintenanceAndLeaveOwnerHealthy(bool audit) {
        using var fixture = new RuntimeFixture(threshold: FileHeaderCodec.InitializationBoundary);
        var address = fixture.Core.Append(0, [1]).Unwrap();
        fixture.Operations.Clear();
        void RejectMutation() {
            Assert.Throws<InvalidOperationException>(() => fixture.Core.Append(2, []));
            Assert.Throws<InvalidOperationException>(() => fixture.Core.BeginAppend());
            Assert.Throws<InvalidOperationException>(() => fixture.Core.BeginAppend(-1, 0, out _));
            Assert.Throws<InvalidOperationException>(() => fixture.Core.ConfirmDurable());
            Assert.Throws<InvalidOperationException>(() => fixture.Core.Inventory(_ => { }));
            Assert.Throws<InvalidOperationException>(() => fixture.Core.Audit(_ => { }));
            Assert.Throws<ArgumentNullException>(() => fixture.Core.Inventory(null!));
            using var read = fixture.Core.ReadFrame(address).Unwrap();
            Assert.Equal(new byte[] { 1 }, read.PayloadAndMeta.ToArray());
        }
        long count = audit
            ? fixture.Core.Audit(_ => RejectMutation()).Unwrap()
            : fixture.Core.Inventory(_ => RejectMutation()).Unwrap();
        Assert.Equal(1L, count);
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(0, fixture.Core.OutstandingBuilders);
        Assert.DoesNotContain(fixture.Operations, operation => operation.StartsWith("flush:") ||
            operation.StartsWith("close:") || operation.StartsWith("archive:") || operation.StartsWith("create:"));
        fixture.Core.ConfirmDurable(); // ScanActive was cleared on the normal exit.
        Assert.Contains("archive:1", fixture.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationFromLastCallback_ThrowsAndLeavesHealthyOwnerReusable(bool audit) {
        using var fixture = new RuntimeFixture();
        fixture.Core.Append(1, [1]).Unwrap();
        using var cancellation = new CancellationTokenSource();
        var error = audit
            ? Assert.Throws<OperationCanceledException>(() => fixture.Core.Audit(_ => cancellation.Cancel(), cancellation.Token))
            : Assert.Throws<OperationCanceledException>(() => fixture.Core.Inventory(_ => cancellation.Cancel(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(1u, fixture.Core.Append(2, [2]).Unwrap().FileId);
        Assert.Equal(2L, fixture.Core.Inventory(_ => { }).Unwrap());
    }

    [Fact]
    public void CancellationAtEntryAndAfterEmptyBackendCompletion_CannotReturnSuccess() {
        using var fixture = new RuntimeFixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Core.Inventory(_ => { }, canceled.Token));
        Assert.Equal(0, fixture.Files.ScanVisitCalls);
        using var lateCancellation = new CancellationTokenSource();
        fixture.Files.BeforeScanCompletion = lateCancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => fixture.Core.Audit(_ => { }, lateCancellation.Token));
        Assert.False(fixture.Core.IsFaulted);
        fixture.Files.BeforeScanCompletion = null;
        Assert.Equal(0L, fixture.Core.Inventory(_ => { }).Unwrap());
    }

    [Fact]
    public void LastInventoryCallbackDispose_TakesTemporarySlotOnceThenClosesRetainedDataAndLock() {
        using var fixture = new RuntimeFixture(formalMax: 2);
        var retained = fixture.Adopt(1); // Header-only active supplies no Inventory callback.
        CreateArchive(fixture, 2);
        fixture.Operations.Clear();
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.Inventory(_ => fixture.Core.Dispose()));
        var temporary = Assert.Single(fixture.Files.Scanned);
        Assert.Equal(1, temporary.DisposeCalls);
        Assert.Equal(1, retained.DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
        Assert.Equal(new[] { "close:2", "close:1", "lock:close" },
            fixture.Operations.Where(operation => operation.StartsWith("close:") || operation == "lock:close"));
        fixture.Core.Dispose();
        Assert.Equal(1, temporary.DisposeCalls);
    }

    [Fact]
    public void Audit_ClosesTemporaryReaderBeforeCallbackAndHeaderCopySurvivesOwnerDispose() {
        using var fixture = new RuntimeFixture();
        CreateArchive(fixture, 2, tag: 0);
        FrameFileAudit retained = default;
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.Audit(report => {
            retained = report;
            Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
            fixture.Core.Dispose();
        }));
        Assert.Equal(2u, retained.FileId);
        Assert.Equal(1L, retained.UserFrameCount);
        Assert.Equal(FileHeaderCodec.PayloadSize, retained.HeaderPayload.Length);
        Assert.True(FileHeaderCodec.TryValidate(retained.HeaderPayload.Span, fixture.Files.Identity, 2));
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        Assert.Equal("lock:close", fixture.Operations[^1]);
    }

    [Fact]
    public void DisposeAfterEmptyBackendCompletion_IsObservedByFinalSuccessGuard() {
        using var fixture = new RuntimeFixture();
        fixture.Files.BeforeScanCompletion = fixture.Core.Dispose;
        Assert.Throws<ObjectDisposedException>(() => fixture.Core.Inventory(_ => { }));
        Assert.Equal(1, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void VisitorExceptionAndTemporaryCloseFailure_PreserveOrderedExceptionsAndNeverRetryClose() {
        using var fixture = new RuntimeFixture(formalMax: 2);
        var retained = fixture.Adopt(1);
        CreateArchive(fixture, 2);
        var primary = new ApplicationException("Visitor failed.");
        var cleanup = new IOException("Temporary reader close failed.");
        fixture.Files.ScanCloseFailure = cleanup;
        var aggregate = Assert.Throws<AggregateException>(() => fixture.Core.Inventory(_ => throw primary));
        Assert.Equal(new Exception[] { primary, cleanup }, aggregate.InnerExceptions);
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        Assert.Equal(0, retained.DisposeCalls);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => fixture.Core.Inventory(null!));
        fixture.Core.Dispose();
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        Assert.Equal(1, retained.DisposeCalls);
        Assert.Equal("lock:close", fixture.Operations[^1]);
    }

    [Fact]
    public void AuditCloseFailure_PreventsFileCallbackFaultsOwnerAndDoesNotReleaseLockEarly() {
        using var fixture = new RuntimeFixture();
        CreateArchive(fixture, 1);
        var cleanup = new IOException("Temporary close failure.");
        fixture.Files.ScanCloseFailure = cleanup;
        Assert.Same(cleanup, Assert.Throws<IOException>(() => fixture.Core.Audit(_ => Assert.Fail("Close failed before callback."))));
        Assert.True(fixture.Core.IsFaulted);
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        fixture.Core.Dispose();
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        Assert.Equal(1, fixture.Lock.DisposeCalls);
    }

    [Fact]
    public void HeaderAndUserReadErrors_ReturnOriginalErrorAndLeaveWriterHealthy() {
        using var fixture = new RuntimeFixture(formalMax: 2);
        CreateArchive(fixture, 2);
        var headerError = new FrameStoreStateError("Injected header query error.");
        fixture.Files.ScanHeaderError = headerError;
        Assert.Same(headerError, fixture.Core.Inventory(_ => Assert.Fail("Header failed.")).Error);
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
        fixture.Files.ScanHeaderError = null;
        var readError = new FrameStoreStateError("Injected user full-read error.");
        fixture.Files.ScanReadError = readError;
        Assert.Same(readError, fixture.Core.Audit(_ => Assert.Fail("User CRC read failed.")).Error);
        Assert.False(fixture.Core.IsFaulted);
        Assert.All(fixture.Files.Scanned, file => Assert.Equal(1, file.DisposeCalls));
        fixture.Files.ScanReadError = null;
        Assert.Equal(1L, fixture.Core.Audit(_ => { }).Unwrap());
        fixture.Core.Append(7, [7]).Unwrap();
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("open")]
    [InlineData("header")]
    [InlineData("read")]
    [InlineData("visitor")]
    public void PureQueryExceptions_PreserveIdentityCleanTemporaryReaderAndClearScanActive(string phase) {
        using var fixture = new RuntimeFixture(formalMax: 1);
        CreateArchive(fixture, 1);
        var failure = new IOException("Pure query failure.");
        switch (phase) {
            case "directory": fixture.Files.ScanVisitFailure = failure; break;
            case "open": fixture.Files.ScanOpenFailure = failure; break;
            case "header": fixture.Files.ScanHeaderFailure = failure; break;
            case "read": fixture.Files.ScanReadFailure = failure; break;
        }
        Assert.Same(failure, Assert.Throws<IOException>(() => {
            if (phase == "visitor") { fixture.Core.Inventory(_ => throw failure); }
            else { fixture.Core.Audit(_ => { }); }
        }));
        Assert.False(fixture.Core.IsFaulted);
        Assert.All(fixture.Files.Scanned, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, fixture.Lock.DisposeCalls);
        fixture.Core.Append(9, [9]).Unwrap(); // ScanActive was cleared on the exceptional exit.
    }

    [Fact]
    public void MoveNextTerminationError_IsReturnedInsteadOfTreatingBadMiddleTrailerAsEof() {
        using var fixture = new RuntimeFixture();
        var file = fixture.Files.CreateActive(2);
        var first = file.Append(1, [1]).Unwrap();
        file.Append(2, [2]).Unwrap();
        file.Dispose();
        fixture.Files.ArchiveClosed(2);
        string path = fixture.Files.ArchivePath(2);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[checked((int)first.EndOffsetExclusive - 1)] ^= 1;
        File.WriteAllBytes(path, bytes);
        var result = fixture.Core.Inventory(_ => Assert.Fail("The first user trailer is corrupt."));
        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.False(fixture.Core.IsFaulted);
        Assert.Equal(1, Assert.Single(fixture.Files.Scanned).DisposeCalls);
    }

    private static FrameAddress CreateArchive(RuntimeFixture fixture, uint fileId, uint tag = 1) {
        var file = fixture.Files.CreateActive(fileId);
        FrameAddress address;
        try { address = FrameAddress.Create(fileId, file.Append(tag, [1, 2], [3]).Unwrap()); }
        finally { file.Dispose(); }
        fixture.Files.ArchiveClosed(fileId);
        return address;
    }
}
