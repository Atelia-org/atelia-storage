using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Atelia.HeadStore.Internal.Runtime;
using Xunit;

namespace Atelia.HeadStore.Tests.Runtime;

public class HeadStoreRuntimeTests {
    [Fact]
    public void Publish_ConfirmsRealDataThenAppendsAndFlushes_AndReadKeepsOwnedOldBytes() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed(payload: [2, 3], recordedAt: -1);
        var old = f.Store.ReadHead(id).Unwrap();
        f.Data.Append(23, [99]).Unwrap();
        f.Hooks.GetRecordedAtUnixMs = () => { f.Operations.Add("clock"); return 456; };
        f.Operations.Clear();
        var published = f.Store.PublishHead(id, [4, 5, 6], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(id, published.HeadId);
        Assert.NotEqual(old.Id, published);
        Assert.True(f.Operations.IndexOf("clock") < f.Operations.IndexOf("data:confirm"));
        Assert.True(f.Operations.IndexOf("data:confirm") < f.Operations.IndexOf("append:1"));
        Assert.True(f.Operations.IndexOf("append:1") < f.Operations.IndexOf("flush:1"));
        var current = f.Store.ReadHead(1u).Unwrap();
        Assert.Equal(published, current.Id);
        Assert.Equal(456, current.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 4, 5, 6 }, current.Payload.ToArray());
        Assert.Equal(new byte[] { 2, 3 }, old.Payload.ToArray());
        Assert.Equal(-1, old.RecordedAtUnixMs);
        Assert.Equal(1, f.Files.OpenCalls);
        f.Store.Dispose();
        Assert.Equal(new byte[] { 4, 5, 6 }, current.Payload.ToArray());
        Assert.Equal(new byte[] { 2, 3 }, old.Payload.ToArray());
        f.Data.EnsureUsable();
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
    }

    [Fact]
    public void Publish_CapturesOnlyEffectiveInputSliceBeforeBarrier() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        byte[] input = [70, 71, 12, 13, 14, 72];
        f.Hooks.ConfirmDataDurable = () => {
            // Deliberate internal seam mutation demonstrates that captured output no longer borrows input.
            Array.Fill(input, (byte)99);
            f.Data.ConfirmDurable();
        };
        f.Store.PublishHead(id, input.AsSpan(2, 3), out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Array.Fill(input, (byte)0);
        Assert.Equal(new byte[] { 12, 13, 14 }, f.Store.ReadHead(id).Unwrap().Payload.ToArray());
        Assert.Equal(3, f.Files.Opened[1].AppendedPayload!.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_048_576)]
    public void Publish_AcceptsExactPayloadLengthBoundaries(int length) {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        byte[] input = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
        f.Store.PublishHead(id, input, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(input, f.Store.ReadHead(id).Unwrap().Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_577)]
    public void Publish_RejectsBadLengthBeforeTargetAndBarrier_WithoutFault(int length) {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Operations.Clear();
        var rejected = f.Store.PublishHead(id, new byte[length], out var outcome);
        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(id, f.Store.ReadHead(id).Unwrap().Id.HeadId);
    }

    [Fact]
    public void ReadAndPublish_RejectDefaultAndWrongStoreBeforeIo() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        StoreIdentity.TryRead(Enumerable.Repeat((byte)0xAA, 16).ToArray(), out var otherIdentity);
        var wrong = HeadId.Create(otherIdentity, 1);
        Assert.True(f.Store.ReadHead(default(HeadId)).IsFailure);
        Assert.True(f.Store.ReadHead(0u).IsFailure);
        Assert.True(f.Store.ReadHead(wrong).IsFailure);
        Assert.True(f.Store.PublishHead(default, [7], out var defaultOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, defaultOutcome);
        Assert.True(f.Store.PublishHead(wrong, [7], out var wrongOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, wrongOutcome);
        Assert.Empty(f.Operations);
        Assert.Equal(id, f.Store.ReadHead(1u).Unwrap().Id.HeadId);
    }

    [Fact]
    public void Publish_RejectsUnrepresentableRealTailBeforeClockAndDataBarrier() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        // The decorator presents the next real-tail value without creating a terabyte fixture file.
        f.Files.NextTailOffset = SizedPtr.MaxOffset + SizedPtr.Alignment;
        f.Hooks.GetRecordedAtUnixMs = () => throw new Exception("clock should not run");
        var rejected = f.Store.PublishHead(id, [7], out var outcome);
        Assert.True(rejected.IsFailure);
        Assert.Equal("HeadStore.HeadCapacityExceeded", rejected.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Files.Opened[1].AppendCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.Opened[1].TailOffsetOverride = null;
        f.Hooks.GetRecordedAtUnixMs = null;
        Assert.Equal(id, f.Store.ReadHead(id).Unwrap().Id.HeadId);
    }

    [Fact]
    public void Publish_AllowsUnrelatedActiveDataBuilder() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        using var builder = f.Data.BeginAppend();
        f.Data.Append(34, [8, 9]).Unwrap();
        f.Store.PublishHead(id, [11], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 11 }, f.Store.ReadHead(id).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void Publish_SameBytesCreateDistinctEntryId_ClockMayRepeatAndRegress() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed(recordedAt: 5);
        var first = f.Store.ReadHead(id).Unwrap();
        f.Hooks.GetRecordedAtUnixMs = () => 5;
        var second = f.Store.PublishHead(id, first.Payload, out _).Unwrap();
        Assert.NotEqual(first.Id, second);
        Assert.Equal(5, f.Store.ReadHead(id).Unwrap().RecordedAtUnixMs);
        f.Hooks.GetRecordedAtUnixMs = () => -9;
        var third = f.Store.PublishHead(id, first.Payload, out _).Unwrap();
        Assert.NotEqual(second, third);
        Assert.Equal(-9, f.Store.ReadHead(id).Unwrap().RecordedAtUnixMs);
    }

    [Fact]
    public void ClockFailure_RemainsNotAttemptedAndDoesNotFault() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        var failure = new IOException("clock");
        f.Hooks.GetRecordedAtUnixMs = () => throw failure;
        var outcome = PublicationOutcome.Confirmed;
        var observed = Assert.Throws<IOException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Same(failure, observed);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(new byte[] { 1 }, f.Store.ReadHead(id).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void DataBarrierFailure_NotAttempted_StopsOwnerAndCleansAllResourcesLockLast() {
        using var f = new RuntimeFixture();
        HeadId first = f.Seed(1);
        HeadId second = f.Seed(2);
        f.Store.ReadHead(first).Unwrap();
        f.Store.ReadHead(second).Unwrap();
        var failure = new IOException("data barrier");
        f.Hooks.ConfirmDataDurable = () => throw failure;
        var outcome = PublicationOutcome.Confirmed;
        var observed = Assert.Throws<IOException>(() => { f.Store.PublishHead(first, [4], out outcome); });
        Assert.Same(failure, observed);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.All(f.Files.Opened.Values, file => { Assert.Equal(0, file.AppendCalls); Assert.Equal(1, file.DisposeCalls); });
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ReadHead(first); });
        f.Store.Dispose();
        f.Store.Dispose();
        Assert.All(f.Files.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppendException_Unknown_EvenBeforeOutputOrAfterCompleteFrame(bool appendThenThrow) {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Store.ReadHead(id).Unwrap();
        var failure = new IOException("append");
        var file = f.Files.Opened[1];
        file.AppendFailure = failure;
        file.AppendThenThrow = appendThenThrow;
        var outcome = PublicationOutcome.NotAttempted;
        var observed = Assert.Throws<IOException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Same(failure, observed);
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(0, file.FlushCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        using var reopened = RbfFile.OpenReadOnlyExisting(f.Files.Paths[1], RbfCacheMode.Off);
        var actual = HeadRecordReader.ReadCurrent(reopened, f.Identity, id).Unwrap();
        Assert.Equal(new byte[] { appendThenThrow ? (byte)7 : (byte)1 }, actual.Payload.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlushException_RemainsUnknown_EvenAfterInnerFlushCompleted(bool flushThenThrow) {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Store.ReadHead(id).Unwrap();
        var failure = new IOException("flush");
        var file = f.Files.Opened[1];
        file.FlushFailure = failure;
        file.FlushThenThrow = flushThenThrow;
        var outcome = PublicationOutcome.NotAttempted;
        var observed = Assert.Throws<IOException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Same(failure, observed);
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(1, file.AppendCalls);
        Assert.Equal(1, file.FlushCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
    }

    [Fact]
    public void AppendFailureResult_RestoresNotAttemptedWithoutFaultOrFlush() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Store.ReadHead(id).Unwrap();
        var rejected = new HeadStoreRuntimeError("TestPreIoReject", "test-only pre-I/O rejection");
        f.Files.Opened[1].AppendError = rejected;
        var result = f.Store.PublishHead(id, [7], out var outcome);
        Assert.True(result.IsFailure);
        Assert.Same(rejected, result.Error);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(new byte[] { 1 }, f.Store.ReadHead(id).Unwrap().Payload.ToArray());
        f.Files.Opened[1].AppendError = null;
        f.Store.PublishHead(id, [8], out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void ConfirmedDeliveryFailure_PreservesConfirmedAndActualNewHead() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        var failure = new OutOfMemoryException("delivery");
        f.Hooks.AfterPublicationConfirmed = () => throw failure;
        var outcome = PublicationOutcome.NotAttempted;
        var observed = Assert.Throws<OutOfMemoryException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Same(failure, observed);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        using var reopened = RbfFile.OpenReadOnlyExisting(f.Files.Paths[1], RbfCacheMode.Off);
        Assert.Equal(new byte[] { 7 }, HeadRecordReader.ReadCurrent(reopened, f.Identity, id).Unwrap().Payload.ToArray());
        Assert.Throws<InvalidOperationException>(() => { f.Store.ReadHead(id); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void MutationAndCleanupFailures_AggregatePrimaryFirst_StillCloseEveryFileAndLockOnce() {
        using var f = new RuntimeFixture();
        HeadId first = f.Seed(1);
        HeadId second = f.Seed(2);
        f.Store.ReadHead(first).Unwrap();
        f.Store.ReadHead(second).Unwrap();
        var primary = new IOException("append");
        var closeFirst = new IOException("close first");
        var closeSecond = new IOException("close second");
        var closeLock = new IOException("close lock");
        f.Files.Opened[1].AppendFailure = primary;
        f.Files.Opened[1].DisposeFailure = closeFirst;
        f.Files.Opened[2].DisposeFailure = closeSecond;
        f.Lock.DisposeFailure = closeLock;
        var outcome = PublicationOutcome.NotAttempted;
        var observed = Assert.Throws<AggregateException>(() => { f.Store.PublishHead(first, [7], out outcome); });
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(new Exception[] { primary, closeFirst, closeSecond, closeLock }, observed.InnerExceptions);
        Assert.Equal("lock:close", f.Operations[^1]);
        f.Store.Dispose();
        f.Store.Dispose();
        Assert.All(f.Files.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void DisposeFailure_TriesOtherResourcesThenLock_AndNeverRetries() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed(1)).Unwrap();
        f.Store.ReadHead(f.Seed(2)).Unwrap();
        var failure = new IOException("close first");
        f.Files.Opened[1].DisposeFailure = failure;
        var observed = Assert.Throws<IOException>(() => f.Store.Dispose());
        Assert.Same(failure, observed);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.All(f.Files.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        f.Store.Dispose();
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void OrdinaryReadAndTargetErrors_DoNotFaultOrDisposeHealthyWriter() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        Assert.True(f.Store.ReadHead(2u).IsFailure);
        var targetFailure = new IOException("resolve");
        f.Files.ResolveFailure = targetFailure;
        Assert.Same(targetFailure, Assert.Throws<IOException>(() => { f.Store.ReadHead(id); }));
        f.Files.ResolveFailure = null;
        f.Store.ReadHead(id).Unwrap();
        var readFailure = new IOException("ordinary header scan entry");
        f.Files.Opened[1].ScanFailure = readFailure;
        Assert.Same(readFailure, Assert.Throws<IOException>(() => { f.Store.ReadHead(id); }));
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(readFailure, Assert.Throws<IOException>(() => { f.Store.PublishHead(id, [7], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.Opened[1].ScanFailure = null;
        Assert.Equal(id, f.Store.ReadHead(id).Unwrap().Id.HeadId);
    }

    [Fact]
    public void WritableOpenFailure_StopsOwnerButPreservesNotAttempted() {
        using var f = new RuntimeFixture();
        HeadId first = f.Seed(1);
        HeadId second = f.Seed(2);
        f.Store.ReadHead(first).Unwrap();
        var failure = new IOException("writable open/recovery");
        f.Files.OpenFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.PublishHead(second, [7], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ReadHead(first); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void NecessaryTargetCleanupFailure_StopsOwnerAndClosesRetainedHeadsLockLast() {
        using var f = new RuntimeFixture();
        HeadId first = f.Seed(1);
        HeadId second = f.Seed(2);
        f.Store.ReadHead(first).Unwrap();
        var failure = new IOException("target enumerator close");
        f.Files.ResolveCleanupFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.PublishHead(second, [7], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Equal(1, f.Files.OpenCalls);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ReadHead(first); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void ReadOnlyOpenFailure_IsOrdinary_WhileOwnedCleanupFailureStopsOwner() {
        using var f = new RuntimeFixture(readOnly: true);
        HeadId first = f.Seed(1);
        HeadId second = f.Seed(2);
        f.Store.ReadHead(first).Unwrap();
        var openFailure = new IOException("readonly open");
        f.Files.OpenFailure = openFailure;
        Assert.Same(openFailure, Assert.Throws<IOException>(() => { f.Store.ReadHead(second); }));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.OpenFailure = null;
        var cleanupFailure = new IOException("owned cleanup");
        f.Files.OpenCleanupFailure = cleanupFailure;
        Assert.Same(cleanupFailure, Assert.Throws<IOException>(() => { f.Store.ReadHead(second); }));
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
    }

    [Fact]
    public void ReadOnlyStore_RejectsMutationFirst_AndCanBorrowReadOnlyData() {
        using var f = new RuntimeFixture(readOnly: true, readOnlyData: true);
        HeadId id = f.Seed();
        Assert.True(f.Store.IsReadOnly);
        Assert.Equal(17u, f.Store.ReservedHeadIdUpperBound);
        Assert.Equal(16, f.Store.HeadStoreId.Length);
        Assert.True(f.Store.PublishHead(id, [7], out var outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(id, f.Store.ReadHead(1u).Unwrap().Id.HeadId);
        Assert.Contains("open:1:ro", f.Operations);
        Assert.DoesNotContain("data:confirm", f.Operations);
        f.Store.Dispose();
        f.Data.EnsureUsable();
    }

    [Fact]
    public void DisposeAfterBorrowedDataDisposed_StillCleansVs_AndEntryInitializesOutcome() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Store.ReadHead(id).Unwrap();
        f.Data.Dispose();
        f.Store.Dispose();
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
    }

    [Fact]
    public void RealBorrowedDataDisposedAtBarrier_StopsVsBeforeAppend() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Store.ReadHead(id).Unwrap();
        f.Hooks.ConfirmDataDurable = null;
        f.Data.Dispose();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { f.Store.PublishHead(id, [7], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.Opened[1].AppendCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ReadHead(id); });
    }

    [Fact]
    public void CachedHeadIdentityCanBeReadByAnotherInternalOwnerOfSamePersistentStore() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        var previous = f.Store.ReadHead(id).Unwrap();
        f.Store.Dispose();
        var nextFiles = new TestHeadStoreFiles(f.Operations);
        nextFiles.Paths.Add(1, f.Files.Paths[1]);
        var nextLock = new RecordingOwnerLock(f.Operations);
        using var reopened = new HeadStore(f.Data, f.Identity, f.DataIdentity, 17, false, nextFiles, nextLock);
        Assert.Equal(previous.Id, reopened.ReadHead(id).Unwrap().Id);
        Assert.Equal(f.Store.HeadStoreId.ToArray(), reopened.HeadStoreId.ToArray());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void InternalBorrowAdmission_RejectsModeBindingAndDisposedDataBeforeBackend() {
        using var f = new RuntimeFixture(readOnly: true, readOnlyData: true);
        var candidateLock = new RecordingOwnerLock(f.Operations);
        Assert.Throws<ArgumentException>(() => {
            new HeadStore(f.Data, f.Identity, f.DataIdentity, 0, false, f.Files, candidateLock);
        });
        StoreIdentity.TryRead(Enumerable.Repeat((byte)0xAA, 16).ToArray(), out var wrongBinding);
        Assert.Throws<ArgumentException>(() => {
            new HeadStore(f.Data, f.Identity, wrongBinding, 0, true, f.Files, candidateLock);
        });
        f.Data.Dispose();
        Assert.Throws<ObjectDisposedException>(() => {
            new HeadStore(f.Data, f.Identity, f.DataIdentity, 0, true, f.Files, candidateLock);
        });
        Assert.Empty(f.Operations);
        Assert.Equal(0, candidateLock.DisposeCalls);
    }

    [Fact]
    public void MetadataWrittenByPublish_IsExactlyEightLittleEndianBytes() {
        using var f = new RuntimeFixture();
        HeadId id = f.Seed();
        f.Hooks.GetRecordedAtUnixMs = () => 0x0102030405060708;
        f.Store.PublishHead(id, [6], out _).Unwrap();
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, f.Files.Opened[1].AppendedMeta);
        Assert.Equal(0x0102030405060708, f.Store.ReadHead(id).Unwrap().RecordedAtUnixMs);
    }
}
