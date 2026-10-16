using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Tests.Runtime;
using Xunit;
using static Atelia.HeadStore.Tests.ForkFaults.ForkPublicationFaultTests;

namespace Atelia.HeadStore.Tests.ForkFaults;

public sealed class ForkPreflightFaultTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedGuardInitializesOutcomeBeforeThrowingForBothEntrypoints(bool specified) {
        using var f = new RuntimeFixture();
        f.Store.Dispose();
        f.Operations.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<ObjectDisposedException>(() => { Fork(f.Store, default, specified, out outcome); });

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(1, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultedGuardInitializesOutcomeAndDoesNotRetryCleanupOrOutput(bool specified) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        f.Files.Opened[1].FlushFailure = new IOException("source flush");
        Assert.Throws<IOException>(() => { f.Store.ForkHead(source, out _); });
        f.Operations.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<InvalidOperationException>(() => { Fork(f.Store, source, specified, out outcome); });

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyGuardRejectsBeforeTargetSourceAndBarriers(bool specified) {
        using var f = new RuntimeFixture(readOnly: true, readOnlyData: true);
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        f.Operations.Clear();

        var result = Fork(f.Store, source, specified, out PublicationOutcome outcome);

        Assert.True(result.IsFailure);
        Assert.Equal("HeadStore.ReadOnly", result.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Files.PrepareCalls);
        Assert.Equal(0, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryVisitorRejectsMutationWithoutAnyForkPreparationAndReleasesGuard(bool specified) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        f.Operations.Clear();

        Assert.Equal(HeadHistoryEnd.VisitorStopped, f.Store.ReadHeadHistory(source.HeadId,
            new HeadHistoryBudget(long.MaxValue, long.MaxValue), entry => {
                var outcome = PublicationOutcome.Confirmed;
                var rejected = Fork(f.Store, entry.Id, specified, out outcome);
                Assert.True(rejected.IsFailure);
                Assert.Equal("HeadStore.HistoryActive", rejected.Error!.ErrorCode);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.Equal(0, f.Files.PrepareCalls);
                Assert.Equal(0, f.Files.Opened[1].FlushCalls);
                Assert.Equal(0, f.Files.PendingCreateCalls);
                Assert.DoesNotContain("data:confirm", f.Operations);
                return false;
            }).Unwrap());

        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(Fork(f.Store, source, specified, out PublicationOutcome completed).IsSuccess);
        Assert.Equal(PublicationOutcome.Confirmed, completed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DefaultOrForeignEntryIdRejectsWithoutSourceOrTargetIo(bool specified, bool foreign) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.CreateHead(1u, [7], out _).Unwrap();
        StoreIdentity.TryRead(Convert.FromHexString("2122232425262728292A2B2C2D2E2F30"), out StoreIdentity foreignIdentity);
        // This internal value only exercises the persistent-context guard; it is not a public entryId constructor.
        HeadEntryId input = foreign ? HeadEntryId.Create(HeadId.Create(foreignIdentity, 1), source.Ticket) : default;
        int pendingBefore = f.Files.PendingCreateCalls;
        f.Operations.Clear();

        var rejected = Fork(f.Store, input, specified, out PublicationOutcome outcome);

        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(pendingBefore, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(Fork(f.Store, source, specified, out _).IsSuccess);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(18u)]
    [InlineData(uint.MaxValue)]
    public void SpecifiedIdOutsideReservedDomainRejectsWithoutOpeningSourceOrAttemptingBarriers(uint localId) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.CreateHead(1u, [7], out _).Unwrap();
        int preparedBefore = f.Files.PrepareCalls;
        int pendingBefore = f.Files.PendingCreateCalls;
        f.Operations.Clear();

        var rejected = f.Store.ForkHead(localId, source, out PublicationOutcome outcome);

        Assert.True(rejected.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(preparedBefore, f.Files.PrepareCalls);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(pendingBefore, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.ForkHead(2u, source, out _).IsSuccess);
    }

    [Fact]
    public void OccupiedBadDestinationRejectsBeforeOpeningSourceAndDoesNotBurnAutomaticId() {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.CreateHead(1u, [7], out _).Unwrap();
        f.Seed(2);
        File.WriteAllBytes(f.Files.Paths[2], [99]);
        int pendingBefore = f.Files.PendingCreateCalls;
        f.Operations.Clear();

        var rejected = f.Store.ForkHead(2u, source, out PublicationOutcome outcome);

        Assert.True(rejected.IsFailure);
        Assert.Equal("HeadStore.HeadAlreadyExists", rejected.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new[] { "prepare:2" }, f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(0, f.Files.ResolveCalls);
        Assert.Equal(pendingBefore, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(f.Files.Paths[2]));
        Assert.Equal(18u, f.Store.ForkHead(source, out _).Unwrap().HeadId.LocalId);
    }

    [Fact]
    public void ExhaustedAutomaticDomainRejectsWithoutOpeningSource_WhileReservedTargetStillWorks() {
        using var f = new RuntimeFixture(autoHeadIdHighWatermark: uint.MaxValue);
        HeadEntryId source = f.Store.CreateHead(1u, [7], out _).Unwrap();
        int pendingBefore = f.Files.PendingCreateCalls;
        f.Operations.Clear();

        var rejected = f.Store.ForkHead(source, out PublicationOutcome outcome);

        Assert.True(rejected.IsFailure);
        Assert.Equal("HeadStore.HeadIdExhausted", rejected.Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Empty(f.Operations);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(pendingBefore, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(2u, f.Store.ForkHead(2u, source, out PublicationOutcome specified).Unwrap().HeadId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, specified);
        f.Operations.Clear();
        Assert.True(f.Store.ForkHead(source, out PublicationOutcome again).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, again);
        Assert.Empty(f.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryTargetPreparationExceptionKeepsOwnerHealthyAndNotAttempted(bool specified) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.CreateHead(1u, [7], out _).Unwrap();
        var failure = new IOException("target qualification");
        f.Files.PrepareFailure = failure;
        int pendingBefore = f.Files.PendingCreateCalls;
        f.Operations.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.OpenCalls);
        Assert.Equal(pendingBefore, f.Files.PendingCreateCalls);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.PrepareFailure = null;
        Assert.True(Fork(f.Store, source, specified, out _).IsSuccess);
    }

    [Fact]
    public void TargetQualificationCleanupFailureStopsOwnerBeforeSourceAndChildOutput() {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        var failure = new IOException("target enumerator close");
        f.Files.PrepareCleanupFailure = failure;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.ForkHead(source, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SourceReadOrClockExceptionBeforeBarrierDoesNotFaultHealthyOwner(bool specified, bool clockFailure) {
        using var f = new ForkFaultFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        var failure = new IOException(clockFailure ? "child clock" : "exact source info");
        if (clockFailure) { f.Hooks.GetRecordedAtUnixMs = () => throw failure; }
        else { f.Files.Sources[1].FrameInfoFailure = failure; }
        f.Operations.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Backend.Opened[1].FlushCalls);
        Assert.Equal(0, f.Backend.PendingCreateCalls);
        Assert.Equal(0, f.Backend.Opened[1].DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Hooks.GetRecordedAtUnixMs = null;
        f.Files.Sources[1].FrameInfoFailure = null;
        Assert.True(Fork(f.Store, source, specified, out _).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundaryHelperRejectionIsPropagatedBeforeBarrierAndDoesNotFaultOwner(bool specified) {
        using var f = new ForkFaultFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        ForkSourceFile reader = f.Files.Sources[1];
        reader.ResetReadLog();
        var rejected = new HeadStoreRuntimeError("InjectedBoundaryRejection", "The exact source boundary was rejected.");
        reader.BoundaryError = rejected;
        f.Operations.Clear();

        var result = Fork(f.Store, source, specified, out PublicationOutcome outcome);

        Assert.True(result.IsFailure);
        Assert.Same(rejected, result.Error);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new[] { source.Ticket }, reader.BoundaryTickets);
        Assert.DoesNotContain("data:confirm", f.Operations);
        Assert.Equal(0, f.Backend.Opened[1].FlushCalls);
        Assert.Equal(0, f.Backend.PendingCreateCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        reader.BoundaryError = null;
        Assert.True(Fork(f.Store, source, specified, out _).IsSuccess);
        f.Store.Dispose();
        Assert.Equal(4, reader.CountCompleteReads(source.Ticket)); // Two real full reads in each attempt.
        f.Data.EnsureUsable();
    }
}
