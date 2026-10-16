using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.RecoveryReportsFailures;

public sealed class RecoveryReportFailureTests {
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HeadRepairSurvivesHeaderOrMissingEntryRejectionAndOwnerDispose(bool invalidHeader) {
        using var f = new RecoveryReportFailureFixture();
        f.RewriteHead(1, invalidHeader ? 2u : 1u, entry: invalidHeader);
        byte[] repaired = RecoveryReportFailureFixture.PartialNextFrame(f.HeadPath(1));
        var store = f.Open();
        var saved = store.RecoveryReports;

        Assert.True(store.ReadHead(f.Initial[1].HeadId).IsFailure);

        var report = Assert.Single(saved);
        Assert.Equal(HeadStoreRecoveryFileKind.Head, report.FileKind);
        Assert.Equal(1u, report.LocalId);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Recovery.Action);
        Assert.Equal(repaired.Length + 1L, report.Recovery.OriginalLength);
        Assert.Equal(repaired.LongLength, report.Recovery.FinalLength);
        store.EnsureUsable();
        store.Dispose();
        Assert.Same(saved, store.RecoveryReports);
        Assert.Equal(report, Assert.Single(saved));
        Assert.Equal(repaired, File.ReadAllBytes(f.HeadPath(1)));
        f.AssertSingleCloseAndLockLast();
    }

    [Fact]
    public void RepairedConditionalPeerWithInvalidSelfTicketIsAHardFailureAndKeepsItsReport() {
        using var f = new RecoveryReportFailureFixture();
        SizedPtr a = f.Predict(1, 29);
        SizedPtr b = f.Predict(2, 29);
        Assert.Equal(a, f.Append(1, 2, RecoveryReportFailureFixture.Envelope(a, b)));
        SizedPtr wrongSelf = SizedPtr.Create(b.Offset + 4, b.Length);
        Assert.Equal(b, f.Append(2, 2, RecoveryReportFailureFixture.Envelope(a, wrongSelf)));
        byte[] repaired = RecoveryReportFailureFixture.MissingTail(f.HeadPath(2));
        var store = f.Open();
        var saved = store.RecoveryReports;

        Assert.True(store.ReadHead(f.Initial[1].HeadId).IsFailure);
        int delivered = 0;
        Assert.True(store.ReadHeadHistory(f.Initial[1].HeadId, new(10, 100), _ => { delivered++; return true; }).IsFailure);

        Assert.Equal(0, delivered);
        var report = Assert.Single(saved);
        Assert.Equal(HeadStoreRecoveryFileKind.Head, report.FileKind);
        Assert.Equal(2u, report.LocalId);
        Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Recovery.Action);
        Assert.Equal(b, report.Recovery.FrameTicket);
        Assert.Equal(2, f.Events.Count(entry => entry == "factory:Head:2"));
        store.Dispose();
        Assert.Equal(repaired, File.ReadAllBytes(f.HeadPath(2)));
        Assert.Equal(report, Assert.Single(store.RecoveryReports));
        f.AssertSingleCloseAndLockLast();
    }

    [Fact]
    public void TagRepairSurvivesNecessaryHeaderRejection() {
        using var f = new RecoveryReportFailureFixture();
        uint bucket = TagPublicFixture.Bucket(RecoveryReportFailureFixture.TagName);
        f.RewriteTagHeader((bucket + 1) % 1024);
        byte[] repaired = RecoveryReportFailureFixture.PartialNextFrame(f.TagPath);
        var store = f.Open();

        Assert.Equal("HeadStore.InvalidTagBucketHeader", store.ReadTag(RecoveryReportFailureFixture.TagName).Error!.ErrorCode);

        var report = Assert.Single(store.RecoveryReports);
        Assert.Equal(HeadStoreRecoveryFileKind.TagBucket, report.FileKind);
        Assert.Equal(bucket, report.LocalId);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Recovery.Action);
        Assert.Equal(1, Assert.Single(f.Operations.Files).DisposeCalls);
        store.EnsureUsable();
        store.Dispose();
        Assert.Equal(repaired, File.ReadAllBytes(f.TagPath));
        Assert.Equal(report, Assert.Single(store.RecoveryReports));
        f.AssertSingleCloseAndLockLast();
    }

    [Fact]
    public void TagInitializationProtectionKeepsActualRepairBeforePrimaryThenCleanupErrors() {
        using var f = new RecoveryReportFailureFixture();
        f.WriteIncompleteInitialization();
        var closeFailure = new IOException("bucket close after initialization rejection");
        var lockFailure = new IOException("lock close after initialization rejection");
        f.Operations.ConfigureOpen = (file, _) => file.DisposeFailure = closeFailure;
        var store = f.Open();
        var saved = store.RecoveryReports;
        f.Operations.Locks[0].DisposeFailure = lockFailure;

        var error = Assert.Throws<AggregateException>(() => store.ReadTag(RecoveryReportFailureFixture.TagName));

        Assert.Collection(ErrorsInObservationOrder(error),
            primary => Assert.Contains("initialization prefix", Assert.IsType<InvalidDataException>(primary).Message),
            cleanup => Assert.Same(closeFailure, cleanup),
            cleanup => Assert.Same(lockFailure, cleanup));
        var report = Assert.Single(saved);
        Assert.Equal(HeadStoreRecoveryFileKind.TagBucket, report.FileKind);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Recovery.Action);
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive, report.Recovery.AffectedFrameOffset);
        Assert.Equal(RbfScanBoundary.Empty.EndExclusive, report.Recovery.FinalLength);
        Assert.Throws<InvalidOperationException>(() => store.EnsureUsable());
        f.AssertSingleCloseAndLockLast();
        Assert.Same(saved, store.RecoveryReports);
        Assert.Equal(report, Assert.Single(saved));
        Assert.Equal(report.Recovery.FinalLength, new FileInfo(f.TagPath).Length);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("visitor")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    public void ScanVisitorTerminationKeepsRepairAndClosesTheBorrowedBucketOnce(string termination) {
        using var f = new RecoveryReportFailureFixture();
        RecoveryReportFailureFixture.MissingTail(f.TagPath);
        var store = f.Open();
        var saved = store.RecoveryReports;
        var primary = new IOException("tag visitor");
        using var cancellation = new CancellationTokenSource();
        int delivered = 0;
        bool Visit(HeadSetTagInfo _) {
            delivered++;
            Assert.Single(saved);
            if (termination == "visitor") { throw primary; }
            if (termination == "cancel") { cancellation.Cancel(); }
            if (termination == "dispose") { store.Dispose(); }
            return true;
        }

        Exception? error = Record.Exception(() => store.ScanTags(Visit, cancellation.Token));

        Assert.Equal(1, delivered);
        if (termination == "visitor") { Assert.Same(primary, error); }
        else if (termination == "cancel") { Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken); }
        else { Assert.IsType<ObjectDisposedException>(error); }
        var report = Assert.Single(saved);
        Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Recovery.Action);
        Assert.Equal(1, Assert.Single(f.Operations.Files).DisposeCalls);
        if (termination != "dispose") { store.EnsureUsable(); store.Dispose(); }
        f.AssertSingleCloseAndLockLast();
        Assert.Same(saved, store.RecoveryReports);
        Assert.Equal(report, Assert.Single(saved));
        f.Data.EnsureUsable();
    }

    [Fact]
    public void VisitorAndCloseFailuresPreservePrimaryOrderAndReportAfterOwnerFault() {
        using var f = new RecoveryReportFailureFixture();
        RecoveryReportFailureFixture.MissingTail(f.TagPath);
        var primary = new IOException("visitor primary");
        var closeFailure = new IOException("bucket cleanup");
        var lockFailure = new IOException("lock cleanup");
        f.Operations.ConfigureOpen = (file, _) => file.DisposeFailure = closeFailure;
        var store = f.Open();
        var saved = store.RecoveryReports;
        f.Operations.Locks[0].DisposeFailure = lockFailure;

        var error = Assert.Throws<AggregateException>(() => store.ScanTags(_ => throw primary));

        Assert.Collection(ErrorsInObservationOrder(error),
            actual => Assert.Same(primary, actual), actual => Assert.Same(closeFailure, actual), actual => Assert.Same(lockFailure, actual));
        Assert.Throws<InvalidOperationException>(() => store.EnsureUsable());
        var report = Assert.Single(saved);
        Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Recovery.Action);
        f.AssertSingleCloseAndLockLast();
        Assert.Equal(report, Assert.Single(store.RecoveryReports));
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryVisitorFailureOrCancellationRetainsRepairAfterTemporaryHeadClose(bool cancel) {
        using var f = new RecoveryReportFailureFixture();
        RecoveryReportFailureFixture.MissingTail(f.HeadPath(1));
        var store = f.Open();
        var saved = store.RecoveryReports;
        var primary = new IOException("history visitor");
        using var cancellation = new CancellationTokenSource();
        int delivered = 0;
        bool Visit(HeadEntry _) {
            delivered++;
            Assert.Single(saved);
            if (!cancel) { throw primary; }
            cancellation.Cancel();
            return true;
        }

        Exception? error = Record.Exception(() => store.ReadHeadHistory(f.Initial[1].HeadId, new(10, 100), Visit, cancellation.Token));

        Assert.Equal(1, delivered);
        if (cancel) { Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken); }
        else { Assert.Same(primary, error); }
        Assert.Equal(1, Assert.Single(f.Operations.Files).DisposeCalls);
        var report = Assert.Single(saved);
        Assert.Equal(HeadStoreRecoveryFileKind.Head, report.FileKind);
        Assert.Equal(RbfTailRecoveryAction.CompletedTail, report.Recovery.Action);
        store.EnsureUsable();
        store.Dispose();
        f.AssertSingleCloseAndLockLast();
        Assert.Equal(report, Assert.Single(store.RecoveryReports));
    }

    [Fact]
    public void NotAttemptedMutationCanRepairItsTargetWithoutProbingOrConfirmingBorrowedData() {
        using var f = new RecoveryReportFailureFixture();
        f.RewriteHead(1, 1, entry: false);
        byte[] repaired = RecoveryReportFailureFixture.PartialNextFrame(f.HeadPath(1));
        var store = f.Open();
        // A new data usability probe or confirmation would throw before the local missing-Entry Result.
        f.Data.Dispose();
        var outcome = PublicationOutcome.Confirmed;

        var result = store.PublishHead(f.Initial[1].HeadId, [0x99], out outcome);

        Assert.True(result.IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.DataConfirmCalls);
        Assert.Equal(0, Assert.Single(f.Operations.Files).AppendCalls);
        Assert.Equal(0, f.Operations.Files[0].FlushCalls);
        var report = Assert.Single(store.RecoveryReports);
        Assert.Equal(RbfTailRecoveryAction.Truncated, report.Recovery.Action);
        store.Dispose();
        Assert.Equal(repaired, File.ReadAllBytes(f.HeadPath(1)));
        Assert.Equal(report, Assert.Single(store.RecoveryReports));
        f.AssertSingleCloseAndLockLast();
    }

    [Fact]
    public void FactoryThatRepairsBytesThenThrowsHasNoSuccessfulReportAndStillFaultsAndCleansUp() {
        using var f = new RecoveryReportFailureFixture();
        byte[] repaired = RecoveryReportFailureFixture.MissingTail(f.HeadPath(1));
        var primary = new IOException("factory failed after actual repair before return");
        f.Operations.ThrowAfterOpen = primary;
        var store = f.Open();
        var saved = store.RecoveryReports;

        Assert.Same(primary, Assert.Throws<IOException>(() => store.ReadHead(f.Initial[1].HeadId)));

        Assert.Equal(RbfTailRecoveryAction.CompletedTail, f.Operations.LastRecovery.Action);
        Assert.Empty(saved);
        Assert.Equal(repaired, File.ReadAllBytes(f.HeadPath(1)));
        Assert.Throws<InvalidOperationException>(() => store.EnsureUsable());
        f.AssertSingleCloseAndLockLast();
        Assert.Same(saved, store.RecoveryReports);
        Assert.Empty(store.RecoveryReports);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacityIsPreparedAtFactoryEntryAndSuccessfulRepairIsRecordedBeforeHeaderScan(bool tag) {
        using var f = new RecoveryReportFailureFixture();
        var log = new HeadStoreRecoveryLog();
        var entries = (List<HeadStoreRecoveryReport>)typeof(HeadStoreRecoveryLog)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;
        Assert.Equal(0, entries.Capacity);
        RecoveryReportFailureFixture.MissingTail(tag ? f.TagPath : f.HeadPath(1));
        int factoryEntries = 0;
        int headerScans = 0;
        f.Operations.BeforeOpen = (kind, id) => {
            factoryEntries++;
            Assert.Equal(tag ? HeadStoreRecoveryFileKind.TagBucket : HeadStoreRecoveryFileKind.Head, kind);
            Assert.Equal(tag ? TagPublicFixture.Bucket(RecoveryReportFailureFixture.TagName) : 1u, id);
            Assert.Empty(log.Reports);
            Assert.True(entries.Capacity >= entries.Count + 1);
        };
        f.Operations.ConfigureOpen = (file, recovery) => {
            Assert.Empty(log.Reports); // The narrow RBF factory has not returned yet.
            int preparedCapacity = entries.Capacity;
            file.BeforeScan = () => {
                headerScans++;
                Assert.Equal(recovery, Assert.Single(log.Reports).Recovery);
                Assert.Equal(preparedCapacity, entries.Capacity);
            };
        };
        var store = f.OpenWithLog(log);

        if (tag) { Assert.True(store.ReadTag(RecoveryReportFailureFixture.TagName).IsSuccess); }
        else { Assert.True(store.ReadHead(f.Initial[1].HeadId).IsSuccess); }

        Assert.Equal(1, factoryEntries);
        Assert.True(headerScans > 0);
        Assert.Same(log.Reports, store.RecoveryReports);
        Assert.Single(store.RecoveryReports);
        store.Dispose();
        f.AssertSingleCloseAndLockLast();
    }

    // CleanupErrors may preserve a backend aggregate as the first outer exception.
    // AggregateException.Flatten moves later outer leaves before earlier nested leaves.
    private static IEnumerable<Exception> ErrorsInObservationOrder(Exception error) {
        if (error is AggregateException aggregate) {
            foreach (Exception inner in aggregate.InnerExceptions) {
                foreach (Exception observed in ErrorsInObservationOrder(inner)) { yield return observed; }
            }
        }
        else { yield return error; }
    }
}
