using Atelia.HeadStore.Internal.Runtime;
using Xunit;

namespace Atelia.HeadStore.Tests.Runtime;

public class HeadStoreCreationTests {
    [Fact]
    public void Create_AutomaticAndSpecifiedUseDataFirstPrivatePublication_WithoutReopeningResult() {
        using var f = new RuntimeFixture(reservedUpper: 256, autoHeadIdHighWatermark: 300);
        f.Hooks.GetRecordedAtUnixMs = () => { f.Operations.Add("clock"); return -123; };
        f.Files.OpenFailure = new IOException("Creation must not reopen its newly published ref.");
        var first = f.Store.CreateHead("{\"hello\":1}"u8, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(301u, first.HeadId.GetLocalId(f.Identity));
        Assert.Equal(new[] {
            "prepare:301", "clock", "data:confirm", "pending:create", "append:0", "append:0",
            "flush:0", "close:0", "rename:301"
        }, f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(0, f.Files.ListCalls);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        var specified = f.Store.CreateHead(20u, [5], out var specifiedOutcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, specifiedOutcome);
        Assert.Equal(20u, specified.HeadId.GetLocalId(f.Identity));
        var next = f.Store.CreateHead([6], out var nextOutcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, nextOutcome);
        Assert.Equal(302u, next.HeadId.GetLocalId(f.Identity));
        f.Files.OpenFailure = null;
        var read = f.Store.ReadHead(first.HeadId).Unwrap();
        Assert.Equal(first, read.Id);
        Assert.Equal(-123, read.RecordedAtUnixMs);
        Assert.Equal("{\"hello\":1}"u8.ToArray(), read.Payload.ToArray());
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.False(File.Exists(f.Files.PendingPath));
    }

    [Fact]
    public void Create_CapturesOnlyEffectiveSliceBeforeBarrier() {
        using var f = new RuntimeFixture();
        byte[] input = [10, 11, 12, 13, 14, 15];
        f.Hooks.ConfirmDataDurable = () => {
            Array.Fill(input, (byte)90);
            f.Data.ConfirmDurable();
        };
        var created = f.Store.CreateHead(input.AsSpan(2, 3), out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Array.Fill(input, (byte)0);
        Assert.Equal(new byte[] { 12, 13, 14 }, f.Store.ReadHead(created.HeadId).Unwrap().Payload.ToArray());
        Assert.Equal(3, f.Files.PendingFiles[0].AppendedPayload!.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_048_576)]
    public void Create_AcceptsOpaquePayloadAtExactLengthBounds(int length) {
        using var f = new RuntimeFixture();
        byte[] payload = Enumerable.Range(0, length).Select(index => (byte)(index * 17)).ToArray();
        var created = f.Store.CreateHead(payload, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(payload, f.Store.ReadHead(created.HeadId).Unwrap().Payload.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_048_577)]
    public void Create_RejectsBadLengthWithoutTargetOrBarrierAndKeepsOwnerHealthy(int length) {
        using var f = new RuntimeFixture();
        Assert.True(f.Store.CreateHead(new byte[length], out var autoOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, autoOutcome);
        Assert.True(f.Store.CreateHead(1u, new byte[length], out var specifiedOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, specifiedOutcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(18u, f.Store.CreateHead([1], out _).Unwrap().HeadId.GetLocalId(f.Identity));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(18u)]
    [InlineData(uint.MaxValue)]
    public void Create_RejectsSpecifiedIdOutsideReservedDomainBeforeTargetAndBarrier(uint localId) {
        using var f = new RuntimeFixture();
        Assert.True(f.Store.CreateHead(localId, [1], out var outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(1u, f.Store.CreateHead(1u, [1], out _).Unwrap().HeadId.GetLocalId(f.Identity));
    }

    [Fact]
    public void Create_EmptyReservedDomainStartsAutomaticAtOne() {
        using var f = new RuntimeFixture(reservedUpper: 0);
        Assert.True(f.Store.CreateHead(1u, [1], out var specifiedOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, specifiedOutcome);
        Assert.Empty(f.Operations);
        Assert.Equal(1u, f.Store.CreateHead([1], out var autoOutcome).Unwrap().HeadId.GetLocalId(f.Identity));
        Assert.Equal(PublicationOutcome.Confirmed, autoOutcome);
    }

    [Fact]
    public void Create_OccupiedBadFormalTargetRejectsBeforeBarrierAndDoesNotBurnAutomaticId() {
        using var f = new RuntimeFixture();
        f.Seed();
        File.WriteAllBytes(f.Files.Paths[1], [99]);
        var rejected = f.Store.CreateHead(1u, [2], out var outcome);
        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new[] { "prepare:1" }, f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(18u, f.Store.CreateHead([3], out _).Unwrap().HeadId.GetLocalId(f.Identity));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(f.Files.Paths[1]));
    }

    [Fact]
    public void Create_LastAutomaticIdExhaustsOnlyAutomaticDomain_AndSpecifiedRemainsAvailable() {
        using var f = new RuntimeFixture(reservedUpper: HeadStore.MaxLocalHeadId - 1);
        Assert.Equal(HeadStore.MaxLocalHeadId, f.Store.CreateHead([1], out var lastOutcome).Unwrap().HeadId.GetLocalId(f.Identity));
        Assert.Equal(PublicationOutcome.Confirmed, lastOutcome);
        f.Operations.Clear();
        var exhausted = f.Store.CreateHead([2], out var outcome);
        Assert.True(exhausted.IsFailure);
        Assert.Equal("HeadStore.HeadIdExhausted", exhausted.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(1u, f.Store.CreateHead(1u, [3], out _).Unwrap().HeadId.GetLocalId(f.Identity));
        f.Operations.Clear();
        Assert.True(f.Store.CreateHead([4], out var nextOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, nextOutcome);
        Assert.Empty(f.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_InitializesOutcomeBeforeDisposedGuard(bool specified) {
        using var f = new RuntimeFixture();
        f.Store.Dispose();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => {
            if (specified) { f.Store.CreateHead(1u, [1], out outcome); }
            else { f.Store.CreateHead([1], out outcome); }
        });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
    }

    [Fact]
    public void Create_ReadOnlyRejectsBothMutationsBeforeIoAndBarrier() {
        using var f = new RuntimeFixture(readOnly: true, readOnlyData: true);
        Assert.True(f.Store.CreateHead([1], out var autoOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, autoOutcome);
        Assert.True(f.Store.CreateHead(1u, [1], out var specifiedOutcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, specifiedOutcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Fact]
    public void Create_TargetAndClockPreparationExceptionsDoNotFaultHealthyOwner() {
        using var f = new RuntimeFixture();
        var failure = new IOException("preparation");
        f.Files.PrepareFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        f.Files.PrepareFailure = null;
        f.Hooks.GetRecordedAtUnixMs = () => throw failure;
        outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Hooks.GetRecordedAtUnixMs = null;
        Assert.Equal(18u, f.Store.CreateHead([1], out _).Unwrap().HeadId.GetLocalId(f.Identity));
    }

    [Fact]
    public void Create_TargetNecessaryCleanupFailureStopsOwnerWithNotAttempted() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException("prepare enumerator close");
        f.Files.PrepareCleanupFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_DataConfirmationFailureStopsBeforeAnyPendingOutput() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException("data confirm");
        f.Hooks.ConfirmDataDurable = () => throw failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_PendingFactoryExceptionStopsOwnerWithNotAttempted() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException("private CreateNew");
        f.Files.PendingCreateFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Contains("data:confirm", f.Operations);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_PendingFactoryOwnedCleanupFailureStopsAndCleansRetainedResources() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException("private factory cleanup");
        f.Files.PendingCreateCleanupFailure = failure;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("header-before")]
    [InlineData("header-after")]
    [InlineData("entry-before")]
    [InlineData("entry-after")]
    [InlineData("flush-before")]
    [InlineData("flush-after")]
    [InlineData("close")]
    [InlineData("closed-before-rename")]
    public void Create_PrivateOutputExceptionsRemainNotAttempted_CloseEveryResourceOnceAndKeepPending(string stage) {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException(stage);
        f.Files.ConfigurePending = file => {
            if (stage.StartsWith("header", StringComparison.Ordinal) || stage.StartsWith("entry", StringComparison.Ordinal)) {
                file.AppendFailure = failure;
                file.AppendFailureCall = stage.StartsWith("header", StringComparison.Ordinal) ? 1 : 2;
                file.AppendThenThrow = stage.EndsWith("after", StringComparison.Ordinal);
            }
            else if (stage.StartsWith("flush", StringComparison.Ordinal)) {
                file.FlushFailure = failure;
                file.FlushThenThrow = stage.EndsWith("after", StringComparison.Ordinal);
            }
            else if (stage == "close") { file.DisposeFailure = failure; }
        };
        if (stage == "closed-before-rename") { f.Hooks.BeforeNewHeadRename = () => throw failure; }
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([2], out outcome); }));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.False(f.Files.Paths.ContainsKey(18));
        Assert.True(File.Exists(f.Files.PendingPath));
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Store.Dispose();
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_PrivateAppendRejectionIsAnInternalPlanFailureAndStopsOwner(int call) {
        using var f = new RuntimeFixture();
        f.Files.ConfigurePending = file => {
            file.AppendError = new HeadStoreRuntimeError("InjectedPlanRejection", "Precalculated private append rejected.");
            file.AppendErrorCall = call;
        };
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<InvalidOperationException>(() => { f.Store.CreateHead([1], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.True(File.Exists(f.Files.PendingPath));
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_RenameCallExceptionKeepsUnknownWhetherOrNotNativeMoveTookEffect(bool moved) {
        using var f = new RuntimeFixture();
        var failure = new IOException("rename");
        f.Files.RenameFailure = failure;
        f.Files.RenameThenThrow = moved;
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.CreateHead([1], out outcome); }));
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(moved, f.Files.Paths.ContainsKey(18));
        Assert.Equal(!moved, File.Exists(f.Files.PendingPath));
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_PostRenameResultFailureKeepsConfirmedAndPublishedFormalFile() {
        using var f = new RuntimeFixture();
        var failure = new OutOfMemoryException("result projection");
        f.Hooks.AfterPublicationConfirmed = () => throw failure;
        var outcome = PublicationOutcome.NotAttempted;
        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => { f.Store.CreateHead([4, 5], out outcome); }));
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.True(f.Files.Paths.ContainsKey(18));
        Assert.True(File.Exists(f.Files.Paths[18]));
        Assert.False(File.Exists(f.Files.PendingPath));
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_PrimaryAndCleanupFailuresTryPendingRetainedHeadAndLockExactlyOnce() {
        using var f = new RuntimeFixture();
        f.Store.ReadHead(f.Seed()).Unwrap();
        var primary = new IOException("private append");
        var pendingClose = new IOException("private close");
        var formalClose = new IOException("retained close");
        var lockClose = new IOException("lock close");
        f.Files.ConfigurePending = file => { file.AppendFailure = primary; file.DisposeFailure = pendingClose; };
        f.Files.Opened[1].DisposeFailure = formalClose;
        f.Lock.DisposeFailure = lockClose;
        var outcome = PublicationOutcome.Confirmed;
        var observed = Assert.Throws<AggregateException>(() => { f.Store.CreateHead([1], out outcome); });
        Assert.Equal(new Exception[] { primary, pendingClose, formalClose, lockClose }, observed.InnerExceptions);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        f.Store.Dispose();
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Fact]
    public void Create_AllowsUnrelatedActiveDataBuilder() {
        using var f = new RuntimeFixture();
        using var builder = f.Data.BeginAppend();
        f.Data.Append(23, [2]).Unwrap();
        var created = f.Store.CreateHead([3], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 3 }, f.Store.ReadHead(created.HeadId).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void ListHeads_ReturnsCompleteOwnedImmutablePathIdsIncludingBadContentAndMax() {
        using var f = new RuntimeFixture();
        HeadId low = f.Seed(1);
        HeadId high = f.Seed(0x00800000);
        HeadId max = f.Seed(HeadStore.MaxLocalHeadId);
        File.WriteAllBytes(f.Files.Paths[1], [99]);
        var listed = f.Store.ListHeads().Unwrap();
        Assert.Equal(3, listed.Count);
        Assert.True(new HashSet<HeadId> { low, high, max }.SetEquals(listed));
        Assert.Equal(new[] { "list" }, f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.False(listed is HeadId[]);
        if (listed is IList<HeadId> mutableProjection) {
            Assert.True(mutableProjection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => { mutableProjection[0] = default; });
        }
        if (listed is System.Collections.ICollection collection) {
            Assert.False(collection.SyncRoot is HeadId[]);
        }
        f.Files.Paths.Clear();
        f.Store.Dispose();
        Assert.Equal(3, listed.Count);
        Assert.Contains(low, listed);
        Assert.Contains(high, listed);
        Assert.Contains(max, listed);
    }

    [Fact]
    public void ListHeads_EmptyIsNormalAndOrdinaryDiscoveryFailureDoesNotFault() {
        using var f = new RuntimeFixture();
        Assert.Empty(f.Store.ListHeads().Unwrap());
        var failure = new IOException("list discovery");
        f.Files.ListFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.ListHeads(); }));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ListFailure = null;
        Assert.Empty(f.Store.ListHeads().Unwrap());
        Assert.DoesNotContain("data:confirm", f.Operations);
    }

    [Fact]
    public void ListHeads_NecessaryDiscoveryCleanupFailureStopsOwnerAndCleansLockLast() {
        using var f = new RuntimeFixture(readOnly: true);
        f.Store.ReadHead(f.Seed()).Unwrap();
        var failure = new IOException("list enumerator close");
        f.Files.ListCleanupFailure = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.ListHeads(); }));
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => { f.Store.ListHeads(); });
        f.Data.EnsureUsable();
    }

    [Fact]
    public void InternalConstructor_RejectsHighWatermarkBelowReservedDomainWithoutTakingOwnership() {
        using var f = new RuntimeFixture();
        var nextLock = new RecordingOwnerLock(f.Operations);
        Assert.Throws<ArgumentOutOfRangeException>(() => {
            new HeadStore(f.Data, f.Identity, f.DataIdentity, 17, false, f.Files, nextLock,
                autoHeadIdHighWatermark: 16);
        });
        Assert.Equal(0, nextLock.DisposeCalls);
        Assert.Empty(f.Operations);
        f.Data.EnsureUsable();
    }
}
