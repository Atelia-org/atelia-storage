using Atelia.Rbf;
using System.Reflection;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class TagIoFailureTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(), "atelia-tag-io-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void HistoricalTargetConfirmationFailure_DoesNotAttemptPublication() {
        if (!OperatingSystem.IsWindows()) { return; } // Windows enforces file sharing against writable opens.
        EventAddress target;
        using (var journal = EventJournal.CreateNew(_path, new() {
            EventSegmentStoreOptions = new() { SegmentSizeThresholdBytes = 8 }
        })) {
            target = journal.AppendEventFrame(null, "old"u8).Unwrap();
            journal.AppendEventFrame(target, "new"u8).Unwrap();
        }
        string segment = Path.Combine(_path, "events", "buckets", "000000", "00000001.rbf");
        string log = Path.Combine(_path, "refs", "ref-op-log.rbf");
        byte[] before = File.ReadAllBytes(log);
        using (var blocker = File.OpenHandle(segment, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var journal = EventJournal.OpenExisting(_path)) {
            Assert.True(journal.ReadEventHeaderChecked(target).IsSuccess);
            var error = Assert.Throws<TagPublicationException>(() => journal.CreateTag("blocked", target));
            Assert.Equal(TagPublicationOutcome.NotAttempted, error.Outcome);
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.Throws<InvalidOperationException>(() => journal.CreateTag("retry", target));
        }
        Assert.Equal(before, File.ReadAllBytes(log));
        using var reopened = EventJournal.OpenReadOnlyExisting(_path);
        Assert.Equal("EventJournal.TagNotFound", reopened.ResolveTag("blocked").Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedPublicationHandle_ReportsUnknownAtAppendOrFlush(bool closeAfterAppend) {
        EventAddress target;
        using (var journal = EventJournal.CreateNew(_path)) {
            target = journal.AppendEventFrame(null, "target"u8).Unwrap();
            var log = (IRbfFile)typeof(EventJournal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!;
            journal.TagPublicationProbe = stage => {
                if (stage == (closeAfterAppend ? TagPublicationStage.AfterAppend : TagPublicationStage.BeforeAppend)) {
                    log.Dispose();
                }
            };
            var error = Assert.Throws<TagPublicationException>(() => journal.CreateTag("uncertain", target));
            Assert.Equal(TagPublicationOutcome.Unknown, error.Outcome);
            Assert.IsType<ObjectDisposedException>(error.InnerException);
            Assert.Throws<InvalidOperationException>(() => journal.ResolveTag("uncertain"));
        }
        using var reopened = EventJournal.OpenReadOnlyExisting(_path);
        var resolved = reopened.ResolveTag("uncertain");
        if (closeAfterAppend) { Assert.Equal(target, resolved.Unwrap()); }
        else { Assert.Equal("EventJournal.TagNotFound", resolved.Error!.ErrorCode); }
    }

    public void Dispose() {
        if (Directory.Exists(_path)) { Directory.Delete(_path, recursive: true); }
    }
}
