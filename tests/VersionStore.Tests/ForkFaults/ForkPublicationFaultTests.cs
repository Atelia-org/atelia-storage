using Atelia.Data;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Runtime;
using Atelia.VersionStore.Tests.Runtime;
using Xunit;

namespace Atelia.VersionStore.Tests.ForkFaults;

public sealed class ForkPublicationFaultTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOldSourceIsReadTwiceBeforeDataAndSourceFlush_ThenChildIsPublishedWithFreshTime(bool specified) {
        using var f = new ForkFaultFixture();
        byte[] payload = [0xff, 0, 0x80, 0x7f];
        RefBytesSnapshot source = f.Store.ReadRef(f.Seed(payload: payload)).Unwrap();
        RefRevision suffix = f.Store.PublishRef(source.Revision.RefId, [9, 8], out _).Unwrap();
        ForkSourceFile reader = f.Files.Sources[1];
        int previousReverseCalls = reader.ReverseCalls;
        reader.ResetReadLog();
        f.Operations.Clear();
        f.Hooks.GetRecordedAtUnixMs = () => { f.Operations.Add("clock"); return -456; };

        RefRevision child = Fork(f.Store, source.Revision, specified, out PublicationOutcome outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(TargetId(specified), child.RefId.LocalId);
        Assert.NotEqual(source.Revision, child);
        Assert.Equal(new[] {
            "clock", "data:confirm", "flush:1", "pending:create", "append:0", "append:0",
            "flush:0", "close:0", $"rename:{TargetId(specified)}"
        }, PublicationOperations(f.Operations));
        Assert.True(f.Operations.IndexOf("boundary:1") < f.Operations.IndexOf("data:confirm"));
        Assert.Equal(new[] { source.Revision.Ticket }, reader.BoundaryTickets);
        Assert.Equal(previousReverseCalls, reader.ReverseCalls);
        Assert.Equal(0, reader.BoundaryReverseCalls);
        Assert.Equal(1, f.Backend.OpenCalls);
        Assert.Equal(0, f.Backend.ListCalls);
        Assert.Equal(2, f.Backend.Opened[1].FlushCalls); // PublishRef and this exact source confirmation.
        Assert.Equal(1, f.Backend.PendingFiles[0].DisposeCalls);
        RefBytesSnapshot childValue = f.Store.ReadRef(child.RefId).Unwrap();
        Assert.Equal(child, childValue.Revision);
        Assert.Equal(payload, childValue.Payload.ToArray());
        Assert.Equal(-456, childValue.RecordedAtUnixMs);
        Assert.Equal(123, source.RecordedAtUnixMs);
        Assert.Equal(suffix, f.Store.ReadRef(source.Revision.RefId).Unwrap().Revision);

        // Dispose flushes the real read log, including complete reads through RbfFrameInfo.
        f.Store.Dispose();
        Assert.Equal(2, reader.CountCompleteReads(source.Revision.Ticket));
        Assert.Equal(1, reader.CountCompleteReads(suffix.Ticket)); // Only the explicit current read above touches the suffix.
        Assert.Equal(payload, source.Payload.ToArray());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void ForkOfForkFlushesOnlyItsExactSourceAndLeavesAncestorAndSourceHeadsUnchanged() {
        using var f = new ForkFaultFixture();
        RefRevision ancestor = f.Store.ReadRef(f.Seed(payload: [4, 3, 2, 1])).Unwrap().Revision;
        RefRevision source = f.Store.ForkRef(2u, ancestor, out _).Unwrap();
        f.Store.ReadRef(source.RefId).Unwrap();
        f.Operations.Clear();

        RefRevision child = f.Store.ForkRef(3u, source, out PublicationOutcome outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new[] { "flush:2", "flush:0" }, f.Operations.Where(op => op.StartsWith("flush:", StringComparison.Ordinal)));
        Assert.DoesNotContain("forward:1", f.Operations);
        Assert.DoesNotContain("boundary:1", f.Operations);
        Assert.DoesNotContain("append:1", f.Operations);
        Assert.DoesNotContain("append:2", f.Operations);
        Assert.Equal(source, f.Store.ReadRef(source.RefId).Unwrap().Revision);
        Assert.Equal(ancestor, f.Store.ReadRef(ancestor.RefId).Unwrap().Revision);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, f.Store.ReadRef(child.RefId).Unwrap().Payload.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SourceFlushExceptionBeforeOrAfterRealFlushIsNotAttempted_WithoutAnyChildOutput(bool specified, bool flushed) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        var failure = new IOException("exact source flush");
        f.Files.Opened[1].FlushFailure = failure;
        f.Files.Opened[1].FlushThenThrow = flushed;
        f.Operations.Clear();
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new[] { "data:confirm", "flush:1", "close:1", "lock:close" },
            f.Operations.Where(op => op == "data:confirm" || op == "flush:1" || op == "close:1" || op == "lock:close"));
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.False(f.Files.Paths.ContainsKey(TargetId(specified)));
        Assert.False(File.Exists(f.Files.PendingPath));
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Store.Dispose();
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFailureStopsBeforeSourceFlushAndPending(bool specified) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        var failure = new IOException("data confirm");
        f.Hooks.ConfirmDataDurable = () => { f.Operations.Add("data:confirm"); throw failure; };
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Files.PendingCreateCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("header-before")]
    [InlineData("header-after")]
    [InlineData("snapshot-before")]
    [InlineData("snapshot-after")]
    [InlineData("flush-before")]
    [InlineData("flush-after")]
    [InlineData("close")]
    [InlineData("closed-before-rename")]
    public void PrivateChildFailuresKeepNotAttempted_AndCloseOwnedResourcesOnceWithLockLast(string stage) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        var failure = new IOException(stage);
        if (stage == "create") { f.Files.PendingCreateFailure = failure; }
        f.Files.ConfigurePending = file => {
            if (stage.StartsWith("header", StringComparison.Ordinal) || stage.StartsWith("snapshot", StringComparison.Ordinal)) {
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
        if (stage == "closed-before-rename") { f.Hooks.BeforeNewRefRename = () => throw failure; }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.ForkRef(source, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.False(f.Files.Paths.ContainsKey(18));
        Assert.Equal(stage != "create", File.Exists(f.Files.PendingPath));
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Store.Dispose();
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PrivateAppendResultRejectionStopsAsPlanFailureWithNotAttempted(int call) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        f.Files.ConfigurePending = file => {
            file.AppendError = new VersionStoreRuntimeError("InjectedPlanRejection", "Prepared child append rejected.");
            file.AppendErrorCall = call;
        };
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<InvalidOperationException>(() => { f.Store.ForkRef(source, out outcome); });

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.True(File.Exists(f.Files.PendingPath));
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RenameExceptionIsUnknownWhetherOrNotTheFormalMoveOccurred(bool specified, bool moved) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        var failure = new IOException("rename");
        f.Files.RenameFailure = failure;
        f.Files.RenameThenThrow = moved;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(moved, f.Files.Paths.ContainsKey(TargetId(specified)));
        Assert.Equal(!moved, File.Exists(f.Files.PendingPath));
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostRenameProjectionFailurePreservesConfirmedAndTheFormalChild(bool specified) {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        var failure = new OutOfMemoryException("child result");
        f.Hooks.AfterPublicationConfirmed = () => throw failure;
        var outcome = PublicationOutcome.NotAttempted;

        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => { Fork(f.Store, source, specified, out outcome); }));

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.True(File.Exists(f.Files.Paths[TargetId(specified)]));
        Assert.False(File.Exists(f.Files.PendingPath));
        Assert.Equal(1, f.Files.OpenCalls); // The source was already open; result construction never reopens the child.
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListRefs());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void PrimaryPrivateFailurePrecedesPendingSourceOtherRefAndLockCleanupFailures() {
        using var f = new RuntimeFixture();
        RefRevision source = f.Store.ReadRef(f.Seed()).Unwrap().Revision;
        f.Store.ReadRef(f.Seed(3)).Unwrap();
        var primary = new IOException("child append");
        var pendingClose = new IOException("pending close");
        var sourceClose = new IOException("source close");
        var otherClose = new IOException("other close");
        var lockClose = new IOException("lock close");
        f.Files.ConfigurePending = file => { file.AppendFailure = primary; file.DisposeFailure = pendingClose; };
        f.Files.Opened[1].DisposeFailure = sourceClose;
        f.Files.Opened[3].DisposeFailure = otherClose;
        f.Lock.DisposeFailure = lockClose;
        var outcome = PublicationOutcome.Confirmed;

        var observed = Assert.Throws<AggregateException>(() => { f.Store.ForkRef(source, out outcome); });

        Assert.Equal(new Exception[] { primary, pendingClose, sourceClose, otherClose, lockClose }, observed.InnerExceptions);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new[] { "close:0", "close:1", "close:3", "lock:close" },
            f.Operations.Where(op => op.StartsWith("close:", StringComparison.Ordinal) || op == "lock:close"));
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Files.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Store.Dispose();
        f.Store.Dispose();
        Assert.All(f.Files.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    internal static AteliaResult<RefRevision> Fork(VersionStore store, RefRevision source, bool specified, out PublicationOutcome outcome) =>
        specified ? store.ForkRef(2u, source, out outcome) : store.ForkRef(source, out outcome);

    private static uint TargetId(bool specified) => specified ? 2u : 18u;

    private static string[] PublicationOperations(List<string> operations) => operations.Where(op =>
        op == "clock" || op == "data:confirm" || op == "pending:create" || op == "close:0"
        || op.StartsWith("flush:", StringComparison.Ordinal) || op.StartsWith("append:", StringComparison.Ordinal)
        || op.StartsWith("rename:", StringComparison.Ordinal)).ToArray();
}
