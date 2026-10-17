using Atelia.Data;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Tests.Runtime;
using Xunit;

namespace Atelia.HeadStore.Tests.ForkFaults;

public sealed class ForkPublicationFaultTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOldSourceIsCheckedBeforeDataAndSourceFlush_ThenHeaderOnlyChildPreservesSourceTime(bool specified) {
        using var f = new ForkFaultFixture();
        byte[] payload = [0xff, 0, 0x80, 0x7f];
        HeadEntry source = f.Store.ReadHead(f.Seed(payload: payload)).Unwrap();
        HeadEntryId suffix = f.Store.PublishHead(source.Id.HeadId, [9, 8], out _).Unwrap();
        ForkSourceFile reader = f.Files.Sources[1];
        int previousReverseCalls = reader.ReverseCalls;
        reader.ResetReadLog();
        f.Operations.Clear();
        f.Hooks.GetRecordedAtUnixMs = () => { f.Operations.Add("clock"); return -456; };

        HeadId child = Fork(f.Store, source.Id, specified, out PublicationOutcome outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(TargetId(specified), child.LocalId);
        Assert.NotEqual(source.Id.HeadId, child);
        Assert.Equal(new[] {
            "data:confirm", "flush:1", "pending:create", "append:0",
            "flush:0", "close:0", $"rename:{TargetId(specified)}"
        }, PublicationOperations(f.Operations));
        Assert.True(f.Operations.IndexOf("boundary:1") < f.Operations.IndexOf("data:confirm"));
        Assert.Equal(new[] { source.Id.Ticket }, reader.BoundaryTickets);
        Assert.Equal(previousReverseCalls, reader.ReverseCalls);
        Assert.Equal(0, reader.BoundaryReverseCalls);
        Assert.Equal(1, f.Backend.OpenCalls);
        Assert.Equal(0, f.Backend.ListCalls);
        Assert.Equal(2, f.Backend.Opened[1].FlushCalls); // PublishHead and this exact source confirmation.
        Assert.Equal(1, f.Backend.PendingFiles[0].DisposeCalls);
        HeadEntry childValue = f.Store.ReadHead(child).Unwrap();
        Assert.Equal(source.Id, childValue.Id);
        Assert.Equal(payload, childValue.Payload.ToArray());
        Assert.Equal(123, childValue.RecordedAtUnixMs);
        Assert.Equal(123, source.RecordedAtUnixMs);
        Assert.Equal(suffix, f.Store.ReadHead(source.Id.HeadId).Unwrap().Id);

        // Dispose flushes the real read log, including complete reads through RbfFrameInfo.
        f.Store.Dispose();
        Assert.Equal(4, reader.CountCompleteReads(source.Id.Ticket)); // Fork and inherited ReadHead each check payload and anchor Fence.
        Assert.Equal(1, reader.CountCompleteReads(suffix.Ticket)); // Only the explicit current read above touches the suffix.
        Assert.Equal(payload, source.Payload.ToArray());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void ForkOfForkFlushesOnlyItsExactSourceAndLeavesAncestorAndSourceHeadsUnchanged() {
        using var f = new ForkFaultFixture();
        HeadEntryId ancestor = f.Store.ReadHead(f.Seed(payload: [4, 3, 2, 1])).Unwrap().Id;
        HeadId emptyFork = f.Store.ForkHead(2u, ancestor, out _).Unwrap();
        HeadEntryId source = f.Store.ReadHead(emptyFork).Unwrap().Id;
        f.Operations.Clear();

        HeadId child = f.Store.ForkHead(3u, source, out PublicationOutcome outcome).Unwrap();

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new[] { "flush:1", "flush:0" }, f.Operations.Where(op => op.StartsWith("flush:", StringComparison.Ordinal)));
        Assert.DoesNotContain("forward:2", f.Operations);
        Assert.DoesNotContain("boundary:2", f.Operations);
        Assert.DoesNotContain("append:1", f.Operations);
        Assert.DoesNotContain("append:2", f.Operations);
        Assert.Equal(ancestor, source);
        Assert.Equal(source, f.Store.ReadHead(emptyFork).Unwrap().Id);
        Assert.Equal(ancestor, f.Store.ReadHead(ancestor.HeadId).Unwrap().Id);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, f.Store.ReadHead(child).Unwrap().Payload.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SourceFlushExceptionBeforeOrAfterRealFlushIsNotAttempted_WithoutAnyChildOutput(bool specified, bool flushed) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
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
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
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
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
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
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("header-before")]
    [InlineData("header-after")]
    [InlineData("flush-before")]
    [InlineData("flush-after")]
    [InlineData("close")]
    [InlineData("closed-before-rename")]
    public void PrivateChildFailuresKeepNotAttempted_AndCloseOwnedResourcesOnceWithLockLast(string stage) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        var failure = new IOException(stage);
        if (stage == "create") { f.Files.PendingCreateFailure = failure; }
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

        Assert.Same(failure, Assert.Throws<IOException>(() => { f.Store.ForkHead(source, out outcome); }));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.Opened[1].FlushCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.False(f.Files.Paths.ContainsKey(18));
        Assert.Equal(stage != "create", File.Exists(f.Files.PendingPath));
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Files.Opened[1].DisposeCalls);
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Store.Dispose();
        Assert.All(f.Files.PendingFiles, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(1)]
    public void PrivateAppendResultRejectionStopsAsPlanFailureWithNotAttempted(int call) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        f.Files.ConfigurePending = file => {
            file.AppendError = new HeadStoreRuntimeError("InjectedPlanRejection", "Prepared child append rejected.");
            file.AppendErrorCall = call;
        };
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<InvalidOperationException>(() => { f.Store.ForkHead(source, out outcome); });

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(1, f.Files.PendingFiles[0].DisposeCalls);
        Assert.Equal(0, f.Files.RenameCalls);
        Assert.True(File.Exists(f.Files.PendingPath));
        Assert.Equal("lock:close", f.Operations[^1]);
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RenameExceptionIsUnknownWhetherOrNotTheFormalMoveOccurred(bool specified, bool moved) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
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
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PostRenameProjectionFailurePreservesConfirmedAndTheFormalChild(bool specified) {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
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
        Assert.Throws<InvalidOperationException>(() => f.Store.ListHeads());
        f.Data.EnsureUsable();
    }

    [Fact]
    public void PrimaryPrivateFailurePrecedesPendingSourceOtherHeadAndLockCleanupFailures() {
        using var f = new RuntimeFixture();
        HeadEntryId source = f.Store.ReadHead(f.Seed()).Unwrap().Id;
        f.Store.ReadHead(f.Seed(3)).Unwrap();
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

        var observed = Assert.Throws<AggregateException>(() => { f.Store.ForkHead(source, out outcome); });

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

    internal static AteliaResult<HeadId> Fork(HeadStore store, HeadEntryId source, bool specified, out PublicationOutcome outcome) =>
        specified ? store.ForkHead(2u, source, out outcome) : store.ForkHead(source, out outcome);

    private static uint TargetId(bool specified) => specified ? 2u : 18u;

    private static string[] PublicationOperations(List<string> operations) => operations.Where(op =>
        op == "clock" || op == "data:confirm" || op == "pending:create" || op == "close:0"
        || op.StartsWith("flush:", StringComparison.Ordinal) || op.StartsWith("append:", StringComparison.Ordinal)
        || op.StartsWith("rename:", StringComparison.Ordinal)).ToArray();
}
