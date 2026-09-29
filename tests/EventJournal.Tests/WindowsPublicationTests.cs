using Atelia.Rbf;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class WindowsPublicationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "windows-catalog-publication-" + Guid.NewGuid().ToString("N"));
    private static EventJournalOptions NoCache => new() { RefOpLogOptions = new() { CacheMode = RbfCacheMode.Off } };

    [Fact]
    public void CheckpointDenyDeleteSharingConflictLeavesTagNotAttemptedAndOriginalCatalogReadable() {
        if (!OperatingSystem.IsWindows()) { return; } // This case requires Windows deny-delete sharing semantics.

        EventAddress target;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) { target = journal.AppendEventFrame(null, []).Unwrap(); }
        string logPath = Path.Combine(_root, "refs", "ref-op-log.rbf");
        string snapshotPath = Path.Combine(_root, "refs", CatalogSnapshotCodec.FileName);
        // Reach Q with valid unpublished allocations, using one flush rather than 1024 public fsync operations.
        using (var log = RbfFile.OpenExisting(logPath, RbfCacheMode.Off)) {
            var allocation = new RefOpFrame(RefOpOperation.Create, "orphan", default, default, 0, null, null, 0, 0);
            byte[] payload = RefOpFrameCodec.Encode(in allocation);
            for (int i = 0; i < 1024; i++) { log.Append(EventJournal.RefOpFrameTag, payload).Unwrap(); }
            log.DurableFlush();
        }
        byte[] logBefore = File.ReadAllBytes(logPath);
        byte[] snapshotBefore = File.ReadAllBytes(snapshotPath);
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            Assert.Equal((0L, 1024L), journal.CatalogCounts);
            var stages = new List<string>();
            journal.OperationProbe = stages.Add;
            using (var blocker = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                // d+r > Q requires the checkpoint before the tag's first append.
                var error = Assert.Throws<TagPublicationException>(() => journal.CreateTag("blocked", target));
                Assert.Equal(TagPublicationOutcome.NotAttempted, error.Outcome);
                Exception native = Assert.IsAssignableFrom<Exception>(error.InnerException);
                // Windows overwrite move can report either deny-delete failure mapping.
                // Pair each exact managed type with its HRESULT; arbitrary I/O failures do not pass.
                Assert.True(native.GetType() == typeof(IOException) && native.HResult == unchecked((int)0x80070020)
                    || native.GetType() == typeof(UnauthorizedAccessException) && native.HResult == unchecked((int)0x80070005),
                    $"Unexpected replacement failure: {native.GetType().Name}, HRESULT=0x{native.HResult:x8}.");
                Assert.Contains("CheckpointBeforeReplace", stages);
                Assert.DoesNotContain("CheckpointAfterReplace", stages);
                Assert.Equal(snapshotBefore, File.ReadAllBytes(snapshotPath));
                Assert.Throws<InvalidOperationException>(() => journal.ResolveTag("blocked"));
                Assert.Throws<InvalidOperationException>(() => journal.ListBranches());
            }
        }
        Assert.Equal(logBefore, File.ReadAllBytes(logPath));
        Assert.Equal(snapshotBefore, File.ReadAllBytes(snapshotPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "refs"), "*.tmp"));
        using (var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache)) {
            Assert.Equal((0L, 1024L), reopened.CatalogCounts);
            Assert.Equal("EventJournal.TagNotFound", reopened.ResolveTag("blocked").Error!.ErrorCode);
            Assert.Empty(reopened.ListBranches());
            using var frame = reopened.ReadEvent(target).Unwrap();
        }
        // Same directory, metadata, name and target, with only the deny-delete handle removed.
        // The real public checkpoint now succeeds, ruling out persistent ACL/read-only/path failures.
        using (var retry = EventJournal.OpenExisting(_root, NoCache)) {
            retry.CreateTag("blocked", target).Unwrap();
            Assert.Equal((0L, 1L), retry.CatalogCounts);
        }
        Assert.False(snapshotBefore.AsSpan().SequenceEqual(File.ReadAllBytes(snapshotPath)));
        using var final = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Equal(target, final.ResolveTag("blocked").Unwrap());
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
