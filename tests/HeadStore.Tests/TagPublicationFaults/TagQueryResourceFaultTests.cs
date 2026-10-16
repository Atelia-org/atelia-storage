using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.Runtime;
using Atelia.HeadStore.Tests.TagFaults;
using Atelia.HeadStore.Tests.Tags;
using Xunit;

namespace Atelia.HeadStore.Tests.TagPublicationFaults;

public sealed class TagQueryResourceFaultTests {
    [Fact]
    public void ResolveUsesActualMetadataReadsAndOnlyTheUniqueTargetsCompleteRead() {
        using var f = new TagPublicationFaultFixture();
        byte[] target = TagTestWire.MemberSequence(683);
        TagBucketSeed seed = f.SeedBucket(TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName),
            TagPublicationFaultFixture.Record(payload: target));

        HeadSetTag retained = f.Store.ReadTag(TagPublicationFaultFixture.Name).Unwrap();

        TagPublicationFile file = Assert.Single(f.Tags.Opened);
        TagReadRequest[] reads = file.LoggedReads();
        Assert.All(seed.Tickets, ticket => Assert.Contains(reads, read => read.Offset == ticket.Offset + 4
            + seed.Records[Array.IndexOf(seed.Tickets, ticket)].Payload.Length && read.Length == 23));
        Assert.DoesNotContain(reads, read => read.Offset == seed.Tickets[0].Offset && read.Length == seed.Tickets[0].Length);
        Assert.Single(reads, read => read.Offset == seed.Tickets[1].Offset && read.Length == seed.Tickets[1].Length);
        Assert.Equal(new[] { 23, 23, seed.Tickets[1].Length }, file.Rents);
        Assert.Equal(1, file.DisposeCalls);
        f.Store.Dispose();
        Assert.Equal(target, retained.MemberBytes());
        f.Data.EnsureUsable();
    }

    [Theory]
    [InlineData("missing", "HeadStore.TagNotFound")]
    [InlineData("uncertain", "HeadStore.TagLookupUncertain")]
    [InlineData("duplicate", "HeadStore.DuplicateTagName")]
    [InlineData("header", "HeadStore.InvalidTagBucketHeader")]
    [InlineData("root", "Rbf.")]
    public void OrdinaryContentFailureClosesItsReaderWithoutFaultingWriter(string reason, string code) {
        using var f = new TagPublicationFaultFixture();
        if (reason == "header") { f.SeedBucketWithVersion(1); }
        else if (reason == "duplicate") { f.SeedBucket(TagPublicationFaultFixture.Record(), TagPublicationFaultFixture.Record()); }
        else if (reason == "uncertain") {
            byte[] meta = TagFaultFixture.Metadata(TagPublicationFaultFixture.OtherName);
            meta[^1] ^= 1;
            f.SeedBucket(new TagFaultRecord(TagFaultFixture.Payload(), meta));
        }
        else if (reason == "root") {
            TagBucketSeed seed = f.SeedBucket(TagPublicationFaultFixture.Record(), TagPublicationFaultFixture.Record(TagPublicationFaultFixture.OtherName));
            f.CorruptRoot(seed.Tickets[0]);
        }
        else { f.SeedBucket(); }

        var result = f.Store.ReadTag(TagPublicationFaultFixture.Name);

        Assert.True(result.IsFailure);
        Assert.StartsWith(code, result.Error!.ErrorCode);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.ListHeads().IsSuccess);
        Assert.DoesNotContain("data:confirm", f.Events);
    }

    [Theory]
    [InlineData(false, "metadata")]
    [InlineData(false, "complete")]
    [InlineData(false, "rent")]
    [InlineData(true, "metadata")]
    [InlineData(true, "complete")]
    [InlineData(true, "rent")]
    public void ActualReadOrRentExceptionPropagatesAndHealthyCleanupKeepsOwnerUsable(bool readOnly, string stage) {
        using var f = new TagPublicationFaultFixture(readOnly);
        TagBucketSeed seed = f.SeedBucket(TagPublicationFaultFixture.Record());
        Exception failure = stage == "rent" ? new OutOfMemoryException("full root rent") : new IOException(stage);
        f.Tags.ConfigureOpen = file => {
            file.BeforeRead = (offset, length, raw) => {
                bool atMetadata = !raw && offset == seed.Tickets[0].Offset + 4 + 12 && length == 23;
                bool atComplete = !raw && offset == seed.Tickets[0].Offset && length == seed.Tickets[0].Length;
                if ((stage == "metadata" && atMetadata) || (stage == "complete" && atComplete)) { throw failure; }
            };
            file.BeforeRent = length => { if (stage == "rent" && length == seed.Tickets[0].Length) { throw failure; } };
        };

        Assert.Same(failure, Record.Exception(() => f.Store.ReadTag(TagPublicationFaultFixture.Name)));

        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Tags.ConfigureOpen = null;
        Assert.Equal(seed.Records[0].Payload, f.Store.ReadTag(TagPublicationFaultFixture.Name).Unwrap().MemberBytes());
        if (!readOnly) { Assert.NotNull(f.Store.CreateTag(TagPublicationFaultFixture.OtherName, [f.SelectedHead], out _).Unwrap()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulOrHealthyMissingResultCannotEscapeAnOwnedCloseFailure(bool found) {
        using var f = new TagPublicationFaultFixture();
        f.SeedBucket(found ? [TagPublicationFaultFixture.Record()] : []);
        var close = new IOException("named close");
        f.Tags.ConfigureOpen = file => file.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name)));

        f.AssertFaultedAndCleaned();
    }

    [Fact]
    public void QueryPrimaryReadErrorPrecedesNamedCachedAndLockCloseErrorsExactlyOnce() {
        using var f = new TagPublicationFaultFixture();
        TagBucketSeed seed = f.SeedBucket(TagPublicationFaultFixture.Record());
        f.Store.ReadHead(f.SeedHead()).Unwrap();
        var primary = new IOException("small meta read");
        var bucketClose = new IOException("named close");
        var cachedClose = new IOException("cached ref close");
        var lockClose = new IOException("owner lock close");
        f.Tags.ConfigureOpen = file => {
            file.BeforeRead = (offset, length, raw) => {
                if (!raw && offset == seed.Tickets[0].Offset + 4 + 12 && length == 23) { throw primary; }
            };
            file.DisposeFailure = bucketClose;
        };
        f.Heads.Opened[1].DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;

        AggregateException observed = Assert.Throws<AggregateException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name));

        Assert.Equal(new Exception[] { primary, bucketClose, cachedClose, lockClose }, observed.Flatten().InnerExceptions);
        Assert.Equal(new[] { "bucket:close", "close:1", "lock:close" }, f.Events.Where(e => e.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathPreflightErrorIsOrdinaryButOwnedCleanupMarkerFaultsTheOwner(bool cleanupFailure) {
        using var f = new TagPublicationFaultFixture();
        var failure = new IOException("directory qualification");
        if (cleanupFailure) { f.Tags.ResolveCleanupFailure = failure; }
        else { f.Tags.ResolveFailure = failure; }

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name)));

        if (cleanupFailure) { f.AssertFaultedAndCleaned(); }
        else {
            Assert.Equal(0, f.Lock.DisposeCalls);
            f.Tags.ResolveFailure = null;
            Assert.NotNull(f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out _).Unwrap());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackendOpenCleanupMarkerFaultsEitherOwnerMode(bool readOnly) {
        using var f = new TagPublicationFaultFixture(readOnly);
        f.SeedBucket();
        var failure = new IOException("open temporary close");
        f.Tags.OpenCleanupFailure = failure;

        Assert.Same(failure, Assert.Throws<IOException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name)));

        Assert.Empty(f.Tags.Opened);
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryInsideHistoryUsesAnIndependentBucketSlotAndClosesTheHeadOnlyOnce(bool closeFails) {
        using var f = new TagPublicationFaultFixture();
        HeadId id = f.SeedHead();
        f.SeedBucket(TagPublicationFaultFixture.Record());
        var close = new IOException("nested named close");
        if (closeFails) { f.Tags.ConfigureOpen = file => file.DisposeFailure = close; }
        Func<HeadEntry, bool> visitor = _ => {
            Assert.Equal(id, f.Store.ReadHead(id).Unwrap().Id.HeadId);
            f.Store.ReadTag(TagPublicationFaultFixture.Name).Unwrap();
            return true;
        };

        if (closeFails) {
            Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadHeadHistory(id, new HeadHistoryBudget(100, 100), visitor)));
            Assert.Equal(new[] { "bucket:close", "close:1", "lock:close" }, f.Events.Where(e => e.Contains("close", StringComparison.Ordinal)));
            f.AssertFaultedAndCleaned();
        }
        else {
            Assert.Equal(HeadHistoryEnd.Complete, f.Store.ReadHeadHistory(id, new HeadHistoryBudget(100, 100), visitor).Unwrap());
            Assert.Equal(1, f.Heads.OpenCalls); // ReadHead borrowed the active history file.
            Assert.Equal(1, f.Heads.Opened[1].DisposeCalls);
            Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
            Assert.Equal(0, f.Lock.DisposeCalls);
        }
    }

    [Fact]
    public void PendingAfterPrivateFailureIsPreservedByReadOnlyAndCancelledByWriterWithoutAdoption() {
        using var f = new TagPublicationFaultFixture();
        var primary = new IOException("private header flush");
        f.Heads.ConfigurePending = file => { file.FlushFailure = primary; file.FlushThenThrow = true; };
        var outcome = PublicationOutcome.Confirmed;
        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome)));
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        f.AssertFaultedAndCleaned();
        byte[] pending = File.ReadAllBytes(f.PendingPath);

        using (var cold = HeadStore.OpenReadOnly(f.Storage.Root, f.Data)) {
            Assert.Equal("HeadStore.TagNotFound", cold.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
            Assert.Equal(pending, File.ReadAllBytes(f.PendingPath));
            Assert.False(File.Exists(f.BucketPath));
        }
        using (var cold = HeadStore.Open(f.Storage.Root, f.Data)) {
            Assert.False(File.Exists(f.PendingPath));
            Assert.False(File.Exists(f.BucketPath));
            Assert.Equal("HeadStore.TagNotFound", cold.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
            Assert.NotNull(cold.CreateTag(TagPublicationFaultFixture.Name, [f.SelectedHead], out outcome).Unwrap());
        }
        using var resolved = HeadStore.OpenReadOnly(f.Storage.Root, f.Data);
        Assert.Equal(f.SelectedHead, Assert.Single(resolved.ReadTag(TagPublicationFaultFixture.Name).Unwrap().Entries).Key);
    }

    [Fact]
    public void InitializationLengthRejectionPrecedesWritableFactoryAndLeavesOwnerHealthy() {
        var operations = new TagBackendFaultOperations();
        using var f = new TagPublicationFaultFixture(operations: operations);
        operations.Events = f.Events;
        Directory.CreateDirectory(Path.GetDirectoryName(f.BucketPath)!);
        File.WriteAllBytes(f.BucketPath, [0]);

        Assert.Throws<InvalidDataException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name));

        Assert.Equal(1, operations.Probe!.DisposeCalls);
        Assert.Equal(0, operations.OpenCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(new byte[] { 0 }, File.ReadAllBytes(f.BucketPath));
        File.Delete(f.BucketPath);
        f.SeedBucket();
        Assert.Equal("HeadStore.TagNotFound", f.Store.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
        TagPublicationWindowTests.AssertOrdered(f.Events, "probe:close", "rbf:open");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealRbfFactoryFailureFaultsWriterButKeepsReadOnlyOwnerHealthy(bool readOnly) {
        using var f = new TagPublicationFaultFixture(readOnly);
        Directory.CreateDirectory(Path.GetDirectoryName(f.BucketPath)!);
        File.WriteAllBytes(f.BucketPath, new byte[512]); // Long enough for I, but not an RBF file.

        Assert.Throws<InvalidDataException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name));

        if (!readOnly) { f.AssertFaultedAndCleaned(); }
        else {
            Assert.Equal(0, f.Lock.DisposeCalls);
            File.Delete(f.BucketPath);
            f.SeedBucket();
            Assert.Equal("HeadStore.TagNotFound", f.Store.ReadTag(TagPublicationFaultFixture.Name).Error!.ErrorCode);
        }
    }

    [Fact]
    public void ProbePrimaryAndCloseFailureMarkFaultBeforeEnteringRbfAndKeepErrorOrder() {
        var primary = new IOException("probe length");
        var close = new IOException("probe close");
        var operations = new TagBackendFaultOperations { ProbeLengthFailure = primary, ProbeCloseFailure = close };
        using var f = new TagPublicationFaultFixture(operations: operations);
        operations.Events = f.Events;
        f.SeedBucket();

        AggregateException observed = Assert.Throws<AggregateException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name));

        Assert.Equal(new Exception[] { primary, close }, observed.Flatten().InnerExceptions);
        Assert.Equal(1, operations.Probe!.DisposeCalls);
        Assert.Equal(0, operations.OpenCalls);
        Assert.Equal(new[] { "probe:close", "lock:close" }, f.Events.Where(e => e.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidRecoveryReportCannotAdoptInitializationPrefixAndClosesReturnedRbfBeforeLock(bool missingOffset) {
        var operations = new TagBackendFaultOperations {
            RecoveryOverride = new RbfTailRecoveryReport(RbfTailRecoveryAction.CompletedTail, 64, 64,
                missingOffset ? null : RbfScanBoundary.Empty.EndExclusive, null)
        };
        using var f = new TagPublicationFaultFixture(operations: operations);
        operations.Events = f.Events;
        f.SeedBucket();

        Assert.Throws<InvalidDataException>(() => f.Store.ReadTag(TagPublicationFaultFixture.Name));

        Assert.Equal(1, operations.Returned!.DisposeCalls);
        Assert.Equal(new[] { "probe:close", "close:99", "lock:close" }, f.Events.Where(e => e.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
        Assert.Equal(1, operations.Returned.DisposeCalls);
    }
}

/// <summary>实际 probe/工厂；恢复报告覆盖是模拟保护检查，并非真实恢复故障。</summary>
internal sealed class TagBackendFaultOperations : HeadStoreFactoryOperations {
    internal List<string> Events = [];
    internal Exception? ProbeLengthFailure;
    internal Exception? ProbeCloseFailure;
    internal RbfTailRecoveryReport? RecoveryOverride;
    internal TagProbeStream? Probe;
    internal TrackedHeadFile? Returned;
    internal int OpenCalls;

    internal override FileStream ProbeTagBucket(string path) => Probe = new TagProbeStream(path, this);
    internal override IRbfFile OpenTagBucket(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
        OpenCalls++;
        Events.Add("rbf:open");
        IRbfFile file = base.OpenTagBucket(path, readOnly, out recovery);
        if (RecoveryOverride is not { } overridden) { return file; }
        recovery = overridden;
        Returned = new TrackedHeadFile(99, file, Events);
        return Returned;
    }
}

internal sealed class TagProbeStream(string path, TagBackendFaultOperations operations)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) {
    internal int DisposeCalls;
    public override long Length => operations.ProbeLengthFailure is { } error ? throw error : base.Length;
    protected override void Dispose(bool disposing) {
        if (disposing) {
            DisposeCalls++;
            operations.Events.Add("probe:close");
        }
        base.Dispose(disposing);
        if (disposing && operations.ProbeCloseFailure is { } error) { throw error; }
    }
}
