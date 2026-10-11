using Atelia.Rbf.Internal;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateFaults;

public sealed class ConditionalUpdatePublicationFaultTests {
    [Fact]
    public void DataBarrierFailureIsNotAttemptedAndClosesOwnedRefsOnceWithRootLockLast() {
        using var f = new ConditionalUpdateFaultFixture();
        var before = f.Snapshot();
        var store = f.Open();
        var failure = new IOException("CU data barrier");
        f.Hooks.ConfirmDataDurable = () => throw failure;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.All(f.Operations.Files, file => { Assert.Equal(0, file.BeginCalls); Assert.Equal(0, file.FlushCalls); });
        f.AssertFaultCleanup();
        f.Close();
        f.AssertUnchanged(before);
        f.AssertColdOld();
    }

    [Theory]
    [InlineData("begin", 1u, PublicationOutcome.NotAttempted)]
    [InlineData("end", 1u, PublicationOutcome.NotAttempted)]
    [InlineData("begin", 2u, PublicationOutcome.Unknown)]
    [InlineData("end", 2u, PublicationOutcome.Unknown)]
    public void ActionBeforeBeginOrEndUsesWholeBatchEvidenceAndCancelsTheOnlyActiveBuilder(string phase, uint failedId,
        PublicationOutcome expected) {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new IOException($"CU {phase}:{failedId}");
        f.Operations.ConfigureOpen = file => file.ProbeIdleBeforeClose = true;
        if (phase == "begin") { f.Hooks.BeforeConditionalUpdateBegin = id => { if (id == failedId) { throw failure; } }; }
        else { f.Hooks.BeforeConditionalUpdateEnd = id => { if (id == failedId) { throw failure; } }; }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome)));

        Assert.Equal(expected, outcome);
        Assert.All(f.Operations.Files, file => Assert.Equal(0, file.FlushCalls));
        f.AssertFaultCleanup();
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.IdleCloseProbes));
        if (phase == "end") {
            var canceled = Assert.Single(f.Operations.Files, file => file.Id == failedId);
            Assert.Equal(1, canceled.BeginCalls);
            Assert.NotNull(canceled.LastBuilder);
            // The captured original Builder epoch must already be terminal before its owner closes.
            Assert.ThrowsAny<InvalidOperationException>(() => { _ = canceled.LastBuilder!.Value.PayloadAndMeta.GetSpan(1); });
        }
        f.AssertColdOld();
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    [InlineData(2u, true)]
    [InlineData(3u, false)]
    [InlineData(3u, true)]
    public void AllCompleteEndsRemainUnknownAfterAnyRefFlushFailureAndColdReadAcceptsTheWholeGroup(uint failedId, bool flushed) {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new IOException("CU member flush");
        f.Operations.ConfigureOpen = file => {
            if (file.Id == failedId) { file.FlushFailure = failure; file.FlushThenThrow = flushed; }
        };
        var changes = f.Changes((1, [0xa1]), (2, [0xb1, 2]), (3, [0xc1, 3, 4]));
        var outcome = PublicationOutcome.NotAttempted;
        int complete = 0;
        f.Hooks.AfterRefRecordAppended = _ => complete++;

        Assert.Same(failure, Assert.Throws<IOException>(() => store.PublishRefs(changes, out outcome)));

        Assert.Equal(3, complete);
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        f.AssertFaultCleanup();
        f.Close();
        using var cold = f.OpenPublic(readOnly: true);
        foreach (var pair in changes) {
            var current = cold.ReadRef(pair.Key).Unwrap();
            Assert.Equal(pair.Value.ToArray(), current.Payload.ToArray());
            Assert.Equal(2, ConditionalUpdateFaultFixture.History(cold, pair.Key).Length);
        }
        Assert.Equal(6, cold.Audit().Unwrap().SnapshotCount);
    }

    [Fact]
    public void ConfirmedIsRecordedBeforeResultInstallationFailureAndTheColdGroupIsStillPublished() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new OutOfMemoryException("CU result installation");
        var outcome = PublicationOutcome.NotAttempted;
        f.Hooks.AfterPublicationConfirmed = () => {
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.All(f.Operations.Files, file => Assert.Equal(1, file.FlushCalls));
            throw failure;
        };

        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome)));

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        f.AssertFaultCleanup();
        f.Close();
        using var cold = f.OpenPublic(readOnly: true);
        Assert.Equal(new byte[] { 0xa1 }, cold.ReadRef(1u).Unwrap().Payload.ToArray());
        Assert.Equal(new byte[] { 0xb1 }, cold.ReadRef(2u).Unwrap().Payload.ToArray());
    }

    [Theory]
    [InlineData(1u, PublicationOutcome.NotAttempted)]
    [InlineData(2u, PublicationOutcome.Unknown)]
    public void PredictedTicketMismatchStopsWithoutEndingTheMismatchedBuilder(uint failedId, PublicationOutcome expected) {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        f.Operations.ConfigureOpen = file => { file.WrongBeginTicket = file.Id == failedId; file.ProbeIdleBeforeClose = true; };
        var outcome = PublicationOutcome.Confirmed;
        int completed = 0;
        f.Hooks.AfterRefRecordAppended = _ => completed++;

        Assert.Throws<InvalidOperationException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome));

        Assert.Equal(expected, outcome);
        Assert.Equal(failedId == 1 ? 0 : 1, completed);
        Assert.All(f.Operations.Files, file => Assert.Equal(0, file.FlushCalls));
        f.AssertFaultCleanup();
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.IdleCloseProbes));
        f.AssertColdOld();
    }

    [Fact]
    public void ActualFirstEndOutputFailureIsUnknownAndWritableColdOpenMakesOnlyPhysicalRecovery() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var failure = new IOException("real CU output entry");
        var outcome = PublicationOutcome.Confirmed;
        string target = Path.GetFullPath(f.RefPath(1));
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => request.Path == target ? throw failure : request.RequestedBytes
        };
        try {
            Assert.Same(failure, Assert.Throws<IOException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome)));
        }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        f.AssertFaultCleanup();
        f.Close();
        using (var recovered = f.OpenPublic()) {
            Assert.Equal(f.Initial[1], recovered.ReadRef(1u).Unwrap().Revision);
            Assert.Equal(f.Initial[2], recovered.ReadRef(2u).Unwrap().Revision);
        }
        f.AssertColdOld();
    }

    [Fact]
    public void CanceledBuilderAndEveryCleanupAreSingleUseAndPrimaryFailurePrecedesCloseFailures() {
        using var f = new ConditionalUpdateFaultFixture();
        var store = f.Open();
        var primary = new IOException("before first CU End");
        var firstClose = new IOException("first owned close");
        var secondClose = new IOException("second owned close");
        var lockClose = new IOException("root lock close");
        f.Operations.ConfigureOpen = file => { file.ProbeIdleBeforeClose = true; file.DisposeFailure = file.Id == 1 ? firstClose : secondClose; };
        f.Operations.Locks[^1].DisposeFailure = lockClose;
        f.Hooks.BeforeConditionalUpdateEnd = _ => throw primary;
        var outcome = PublicationOutcome.Confirmed;

        var observed = Assert.Throws<AggregateException>(() => store.PublishRefs(f.Changes((1, [0xa1]), (2, [0xb1])), out outcome));

        Assert.Equal(new Exception[] { primary, firstClose, secondClose, lockClose }, Flatten(observed));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        f.AssertFaultCleanup();
        Assert.All(f.Operations.Files, file => Assert.Equal(1, file.IdleCloseProbes));
    }

    private static Exception[] Flatten(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Flatten).ToArray() : [error];
}
