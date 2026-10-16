using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagPublicationFaults;

/// <summary>真实 RBF 上的受控 append/flush/close/rename 窗口，保留本次 tag 而非空桶的发布证据。</summary>
public sealed class TagPublicationWindowTests {
    [Fact]
    public void ExistingBucketLookupAndAppendUseOneHandleAndNeverCompletelyReadOtherRoot() {
        using var f = new TagPublicationFaultFixture();
        TagBucketSeed seed = f.SeedBucket(TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName));

        Assert.NotNull(f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out PublicationOutcome outcome).Unwrap());

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(1, f.Tags.OpenCalls);
        TagPublicationFile file = Assert.Single(f.Tags.Opened);
        Assert.Equal(1, file.AppendCalls);
        Assert.Equal(1, file.FlushCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(0, f.Heads.PendingCreateCalls);
        Assert.Equal(0, f.Tags.RenameCalls);
        AssertOrdered(f.Events, "bucket:open", "clock", "data:confirm", "bucket:append", "bucket:flush", "bucket:close");
        Assert.Contains(file.LoggedReads(), read => read.Offset == seed.Tickets[0].Offset + 4 + seed.Records[0].Payload.Length
            && read.Length == seed.Records[0].Metadata.Length);
        Assert.DoesNotContain(file.LoggedReads(), read => read.Offset == seed.Tickets[0].Offset && read.Length == seed.Tickets[0].Length);
    }

    [Fact]
    public void MissingBucketPublishesEmptyHeaderBeforeTheTagAndCapturesInputBeforeTheBarrier() {
        using var f = new TagPublicationFaultFixture();
        HeadId[] payload = [f.SelectedHead];
        HeadEntryId expected = f.Store.ReadHead(payload[0]).Unwrap().Id;
        var outcome = PublicationOutcome.Confirmed;
        f.Hooks.ConfirmDataDurable = () => {
            f.Events.Add("data:confirm");
            Array.Clear(payload);
            f.Data.ConfirmDurable();
        };
        f.Hooks.BeforeNewTagBucketRename = () => {
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(1, Assert.Single(f.Heads.PendingFiles).DisposeCalls);
            Assert.False(File.Exists(f.BucketPath));
        };
        f.Hooks.AfterTagBucketPublished = () => {
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(File.Exists(f.BucketPath));
            Assert.False(File.Exists(f.PendingPath));
        };

        Assert.NotNull(f.Store.CreateTag(TagPublicationFaultFixture.Name, payload, out outcome).Unwrap());

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        AssertOrdered(f.Events, "clock", "data:confirm", "tags:ensure", "pending:create", "append:0", "flush:0",
            "close:0", "bucket:rename", "bucket:open", "bucket:append", "bucket:flush", "bucket:close");
        Assert.Equal(1, f.Tags.OpenCalls); // Lookup and Append retain this same formal handle.
        TagPublicationFile formal = Assert.Single(f.Tags.Opened);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(formal.AppendedMeta!.AsSpan(8)));
        Assert.Equal(1, formal.DisposeCalls);
        Assert.Equal(expected, Assert.Single(f.Store.ReadTag(TagPublicationFaultFixture.Name).Unwrap().Entries).Value);
        Assert.Equal(0, f.Lock.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFailureIsNotAttemptedAndStopsBeforeDirectoryPrivateOrFormalOutput(bool existingBucket) {
        using var f = new TagPublicationFaultFixture();
        if (existingBucket) { f.SeedBucket(); }
        f.Store.ReadHead(f.SeedHead()).Unwrap();
        var failure = new IOException("data confirm");
        f.Hooks.ConfirmDataDurable = () => { f.Events.Add("data:confirm"); throw failure; };
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Tags.EnsureCalls);
        Assert.Equal(0, f.Heads.PendingCreateCalls);
        Assert.Equal(0, f.Tags.RenameCalls);
        Assert.All(f.Tags.Opened, file => { Assert.Equal(0, file.AppendCalls); Assert.Equal(0, file.FlushCalls); });
        Assert.False(File.Exists(f.PendingPath));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData("directory-before")]
    [InlineData("directory-after")]
    [InlineData("create")]
    [InlineData("header-before")]
    [InlineData("header-after")]
    [InlineData("header-check")]
    [InlineData("flush-before")]
    [InlineData("flush-after")]
    [InlineData("close")]
    [InlineData("before-rename")]
    public void EmptyBucketInitializationFailureDoesNotAttemptTheTagAndFaultsOnce(string stage) {
        using var f = new TagPublicationFaultFixture();
        var failure = new IOException(stage);
        if (stage.StartsWith("directory", StringComparison.Ordinal)) {
            f.Tags.EnsureFailure = failure;
            f.Tags.EnsureThenThrow = stage.EndsWith("after", StringComparison.Ordinal);
        }
        if (stage == "create") { f.Heads.PendingCreateFailure = failure; }
        f.Heads.ConfigurePending = file => {
            if (stage is "header-before" or "header-after") {
                file.AppendFailure = failure;
                file.AppendThenThrow = stage == "header-after";
            }
            if (stage == "header-check") { file.ScanFailure = failure; }
            if (stage is "flush-before" or "flush-after") {
                file.FlushFailure = failure;
                file.FlushThenThrow = stage == "flush-after";
            }
            if (stage == "close") { file.DisposeFailure = failure; }
        };
        if (stage == "before-rename") { f.Hooks.BeforeNewTagBucketRename = () => throw failure; }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Tags.RenameCalls);
        Assert.False(File.Exists(f.BucketPath));
        Assert.Empty(f.Tags.Opened);
        f.AssertFaultedAndCleaned();
    }

    [Fact]
    public void PrivateHeaderAppendResultRejectionIsAnInvariantFailureWithNoTagAttempt() {
        using var f = new TagPublicationFaultFixture();
        f.Heads.ConfigurePending = file => file.AppendError = new HeadStoreRuntimeError("InjectedPlanRejection", "Prepared header rejected.");
        var outcome = PublicationOutcome.Confirmed;

        Assert.Throws<InvalidOperationException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(0, f.Tags.RenameCalls);
        Assert.True(File.Exists(f.PendingPath));
        Assert.False(File.Exists(f.BucketPath));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyBucketRenameExceptionStaysNotAttemptedWhetherOrNotTheMoveOccurred(bool moved) {
        using var f = new TagPublicationFaultFixture();
        var failure = new IOException("empty bucket rename");
        f.Tags.RenameFailure = failure;
        f.Tags.RenameThenThrow = moved;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(moved, File.Exists(f.BucketPath));
        Assert.Equal(!moved, File.Exists(f.PendingPath));
        Assert.Empty(f.Tags.Opened);
        f.AssertFaultedAndCleaned();
        using var cold = HeadStore.Open(f.Storage.Root, f.Data);
        Assert.False(File.Exists(f.PendingPath));
        Assert.Equal("HeadStore.TagNotFound", cold.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyBucketPublishedOrReopenExceptionCannotBecomeAConfirmedTag(bool atReopen) {
        using var f = new TagPublicationFaultFixture();
        var failure = new IOException(atReopen ? "formal reopen" : "after empty bucket");
        if (atReopen) { f.Tags.OpenFailure = failure; }
        else { f.Hooks.AfterTagBucketPublished = () => throw failure; }
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.True(File.Exists(f.BucketPath));
        Assert.False(File.Exists(f.PendingPath));
        f.AssertFaultedAndCleaned();
        using var cold = HeadStore.OpenReadOnly(f.Storage.Root, f.Data);
        Assert.Equal("HeadStore.TagNotFound", cold.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(false, "append-before")]
    [InlineData(false, "append-after")]
    [InlineData(false, "flush-before")]
    [InlineData(false, "flush-after")]
    [InlineData(true, "append-before")]
    [InlineData(true, "append-after")]
    [InlineData(true, "flush-before")]
    [InlineData(true, "flush-after")]
    public void FormalAppendOrFlushExceptionPreservesUnknownAndStopsTheOwner(bool existingBucket, string stage) {
        using var f = new TagPublicationFaultFixture();
        if (existingBucket) { f.SeedBucket(); }
        var failure = new IOException(stage);
        f.Tags.ConfigureOpen = file => {
            if (stage.StartsWith("append", StringComparison.Ordinal)) {
                file.AppendFailure = failure;
                file.AppendThenThrow = stage.EndsWith("after", StringComparison.Ordinal);
            }
            else {
                file.FlushFailure = failure;
                file.FlushThenThrow = stage.EndsWith("after", StringComparison.Ordinal);
            }
        };
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));

        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).AppendCalls);
        Assert.True(File.Exists(f.BucketPath));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormalAppendResultRejectionIsNotAttemptedAndCloseSuccessKeepsOwnerUsable(bool existingBucket) {
        using var f = new TagPublicationFaultFixture();
        if (existingBucket) { f.SeedBucket(); }
        var rejection = new HeadStoreRuntimeError("InjectedAppendRejection", "The public Append Result proves pre-I/O rejection.");
        f.Tags.ConfigureOpen = file => file.AppendError = rejection;
        var outcome = PublicationOutcome.Confirmed;

        Assert.Same(rejection, f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome).Error);

        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        TagPublicationFile formal = Assert.Single(f.Tags.Opened);
        Assert.Equal(1, formal.AppendCalls);
        Assert.Equal(0, formal.FlushCalls);
        Assert.Equal(1, formal.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Tags.ConfigureOpen = null;
        Assert.Equal("HeadStore.TagNotFound", f.Store.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
        Assert.NotNull(f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedTagCannotBeDowngradedByCloseOrDeliveryFailure(bool closeFailure) {
        using var f = new TagPublicationFaultFixture();
        var failure = new OutOfMemoryException(closeFailure ? "confirmed close" : "confirmed delivery");
        if (closeFailure) { f.Tags.ConfigureOpen = file => file.DisposeFailure = failure; }
        else { f.Hooks.AfterPublicationConfirmed = () => throw failure; }
        var outcome = PublicationOutcome.NotAttempted;

        HeadId selected = f.SelectedHead;
        HeadEntryId expected = f.Store.ReadHead(selected).Unwrap().Id;
        Assert.Same(failure, Assert.Throws<OutOfMemoryException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [selected], out outcome)));

        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        f.AssertFaultedAndCleaned();
        using var cold = HeadStore.OpenReadOnly(f.Storage.Root, f.Data);
        Assert.Equal(expected, Assert.Single(cold.ReadTag(TagPublicationFaultFixture.Name).Unwrap().Entries).Value);
    }

    [Fact]
    public void PublicationPrimaryErrorPrecedesFormalCachedHeadAndLockCloseErrorsWithLockLast() {
        using var f = new TagPublicationFaultFixture();
        f.SeedBucket();
        f.Store.ReadHead(f.SeedHead()).Unwrap();
        var primary = new IOException("tag append");
        var tagClose = new IOException("tag close");
        var headClose = new IOException("ref close");
        var lockClose = new IOException("lock close");
        f.Tags.ConfigureOpen = file => { file.AppendFailure = primary; file.DisposeFailure = tagClose; };
        f.Heads.Opened[1].DisposeFailure = headClose;
        f.Lock.DisposeFailure = lockClose;
        var outcome = PublicationOutcome.Confirmed;

        AggregateException observed = Assert.Throws<AggregateException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome));

        Assert.Equal(new Exception[] { primary, tagClose, headClose, lockClose }, observed.Flatten().InnerExceptions);
        Assert.Equal(PublicationOutcome.Unknown, outcome);
        Assert.Equal(new[] { "bucket:close", "close:1", "lock:close" }, f.Events.Where(e => e.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    internal static void AssertOrdered(List<string> events, params string[] expected) {
        int position = -1;
        foreach (string item in expected) {
            position = events.FindIndex(position + 1, value => value == item);
            Assert.True(position >= 0, $"Missing ordered operation {item}: {string.Join(", ", events)}");
        }
    }
}
