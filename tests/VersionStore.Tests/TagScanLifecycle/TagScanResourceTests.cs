using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Tests.TagPublic;
using Xunit;

namespace Atelia.VersionStore.Tests.TagScanLifecycle;

public sealed class TagScanResourceTests {
    [Fact]
    public void VisitorFalseDoesNotAdvanceCurrentFramesOrOpenTheNextBucket() {
        using var f = new TagScanFaultFixture();
        byte[] invalid = TagPublicFixture.Metadata(TagScanFaultFixture.SameBucketName);
        invalid[^1] ^= 1;
        var seed = f.SeedBucket(TagScanFaultFixture.Name,
            new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192]),
            new TagPublicRecord(TagScanFaultFixture.SameBucketName, [2], invalid));
        f.SeedBucket(TagScanFaultFixture.OtherBucketName, new TagPublicRecord(TagScanFaultFixture.OtherBucketName, [3]));
        f.Order(TagScanFaultFixture.Name, TagScanFaultFixture.OtherBucketName);
        int readsAtCallback = 0;
        TagInfo? retained = null;

        Assert.Equal(TagScanEnd.VisitorStopped, f.Store.ScanTags(info => {
            retained = info;
            readsAtCallback = Assert.Single(f.Tags.Opened).Reads.Count;
            return false;
        }).Unwrap());

        var file = Assert.Single(f.Tags.Opened);
        Assert.Equal(readsAtCallback, file.Reads.Count);
        Assert.Single(file.Rents);
        Assert.Equal(seed.Records[0].Metadata.Length, file.Rents[0]);
        Assert.DoesNotContain(file.LoggedReads(), read => read.Offset == seed.Tickets[0].Offset && read.Length == seed.Tickets[0].Length);
        Assert.DoesNotContain(file.LoggedReads(), read => read.Offset >= seed.Tickets[1].Offset);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).MoveNextCalls);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Store.Dispose();
        Assert.Equal(TagScanFaultFixture.Name, retained!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackCancellationPrecedesFalseAndDoesNotFaultWriter(bool visitorContinues) {
        using var f = new TagScanFaultFixture();
        using var cancellation = new CancellationTokenSource();
        RefId id = f.SeedRef();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]),
            new TagPublicRecord(TagScanFaultFixture.SameBucketName, [2]));
        f.Order(TagScanFaultFixture.Name);
        int readsAtCallback = 0;
        int callbacks = 0;

        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => f.Store.ScanTags(_ => {
            callbacks++;
            readsAtCallback = Assert.Single(f.Tags.Opened).Reads.Count;
            cancellation.Cancel();
            return visitorContinues;
        }, cancellation.Token)).CancellationToken);

        Assert.Equal(1, callbacks);
        Assert.Equal(readsAtCallback, Assert.Single(f.Tags.Opened).Reads.Count);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        Assert.True(f.Store.PublishRef(id, [3], out _).IsSuccess);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("oom")]
    [InlineData("guard")]
    public void VisitorExceptionIsOriginalAndHealthyCleanupKeepsWriterAndGuardUsable(string kind) {
        using var f = new TagScanFaultFixture();
        RefId id = f.SeedRef();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        Exception primary = kind switch {
            "io" => new IOException("visitor IO"),
            "oom" => new OutOfMemoryException("visitor OOM"),
            _ => new InvalidOperationException("visitor guard")
        };
        TagInfo? retained = null;

        Assert.Same(primary, Record.Exception(() => f.Store.ScanTags(info => { retained = info; throw primary; })));

        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        Assert.True(f.Store.PublishRef(id, [2], out _).IsSuccess);
        f.Store.Dispose();
        Assert.Equal(TagScanFaultFixture.Name, retained!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackDisposeBeforeFalseClosesBucketCursorCacheAndHistoryOnceWithLockLast(bool insideHistory) {
        using var f = new TagScanFaultFixture();
        RefId id = f.SeedRef();
        RefId cached = f.SeedRef(2);
        f.Store.ReadRef(cached).Unwrap();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        TagInfo? retained = null;
        Action scan = () => f.Store.ScanTags(info => { retained = info; f.Store.Dispose(); return false; });

        if (insideHistory) {
            Assert.Throws<ObjectDisposedException>(() => f.Store.ReadRefHistory(id, new(100, 100), _ => { scan(); return true; }));
        }
        else { Assert.Throws<ObjectDisposedException>(scan); }

        Assert.All(f.Tags.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Refs.Opened.Values, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(insideHistory ? 2 : 1, f.Refs.Opened.Count);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Events[^1]);
        f.Store.Dispose();
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal(TagScanFaultFixture.Name, retained!.Name);
        f.Storage.Data.EnsureUsable();
    }

    [Fact]
    public void CrossBucketNamedCloseFailureCaughtByVisitorStillFaultsScanAndClosesAllOwnedSlotsBeforeLock() {
        using var f = new TagScanFaultFixture();
        RefId cached = f.SeedRef();
        f.Store.ReadRef(cached).Unwrap();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.SeedBucket(TagScanFaultFixture.OtherBucketName, new TagPublicRecord(TagScanFaultFixture.OtherBucketName, [2]));
        f.Order(TagScanFaultFixture.Name, TagScanFaultFixture.OtherBucketName);
        var close = new IOException("nested named close");
        f.Tags.ConfigureOpen = file => { if (f.Tags.Opened.Count == 1) { file.DisposeFailure = close; } };
        TagInfo? retained = null;

        Assert.Throws<InvalidOperationException>(() => f.Store.ScanTags(info => {
            retained = info;
            Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadTagInfo(TagScanFaultFixture.OtherBucketName)));
            return false;
        }));

        Assert.Equal(2, f.Tags.Opened.Count);
        Assert.Equal(new[] { "directory:close", "bucket:close", "bucket:close", "directory:close", "close:1", "lock:close" },
            f.Events.Where(entry => entry.Contains("close", StringComparison.Ordinal)));
        Assert.Equal(TagScanFaultFixture.Name, retained!.Name);
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("false")]
    public void BucketCloseFailurePrecedesNormalScanTerminationAndFaultsWriter(string end) {
        using var f = new TagScanFaultFixture();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var close = new IOException("scan bucket close");
        f.Tags.ConfigureOpen = file => file.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ScanTags(_ => end == "complete")));

        f.AssertFaultedAndCleaned();
    }

    [Fact]
    public void ScanCloseFailureInsideHistoryReleasesIndependentBucketCursorAndHistorySlotsOnce() {
        using var f = new TagScanFaultFixture();
        RefId id = f.SeedRef(twoSnapshots: true);
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var close = new IOException("nested scan bucket close");
        f.Tags.ConfigureOpen = file => file.DisposeFailure = close;
        int visits = 0;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ReadRefHistory(id, new(100, 1000), _ => {
            visits++;
            f.Store.ScanTags(_ => false).Unwrap();
            return true;
        })));

        Assert.Equal(1, visits);
        Assert.Equal(new[] { "bucket:close", "directory:close", "close:1", "lock:close" },
            f.Events.Where(entry => entry.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("false")]
    [InlineData("record-error")]
    public void CursorCloseFailurePrecedesResultAndFaultsWriter(string end) {
        using var f = new TagScanFaultFixture();
        byte[] meta = TagPublicFixture.Metadata(TagScanFaultFixture.Name);
        if (end == "record-error") { meta[^1] ^= 1; }
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1], meta));
        f.Order(TagScanFaultFixture.Name);
        var close = new IOException("tag cursor close");
        f.Operations.ConfigureEnumerator = cursor => cursor.DisposeFailure = close;

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.ScanTags(_ => end == "complete")));

        f.AssertFaultedAndCleaned();
    }

    [Theory]
    [InlineData("move")]
    [InlineData("current")]
    public void CursorAccessExceptionWithHealthyCloseDoesNotFaultWriter(string stage) {
        using var f = new TagScanFaultFixture();
        RefId id = f.SeedRef();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var primary = new IOException("cursor access");
        f.Operations.ConfigureEnumerator = cursor => {
            if (stage == "move") { cursor.MoveNextFailure = primary; }
            else { cursor.CurrentFailure = primary; }
        };

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.ScanTags(_ => true)));

        Assert.Empty(f.Tags.Opened);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Operations.ConfigureEnumerator = null;
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        Assert.True(f.Store.PublishRef(id, [3], out _).IsSuccess);
    }

    [Theory]
    [InlineData(false, "io")]
    [InlineData(false, "oom")]
    [InlineData(true, "io")]
    [InlineData(true, "oom")]
    public void ActualMetadataReadOrRentExceptionPropagatesWithoutFaultingHealthyOwner(bool readOnly, string stage) {
        using var f = new TagScanFaultFixture(readOnly);
        var seed = f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192]));
        f.Order(TagScanFaultFixture.Name);
        Exception primary = stage == "io" ? new IOException("actual small meta read") : new OutOfMemoryException("small meta rent");
        f.Tags.ConfigureOpen = file => {
            file.BeforeRead = (offset, length, raw) => {
                if (stage == "io" && !raw && offset == seed.Tickets[0].Offset + 4 + 8192
                    && length == seed.Records[0].Metadata.Length) { throw primary; }
            };
            file.BeforeRent = _ => { if (stage == "oom") { throw primary; } };
        };
        int visitors = 0;

        Assert.Same(primary, Record.Exception(() => f.Store.ScanTags(_ => { visitors++; return true; })));

        Assert.Equal(0, visitors);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Tags.ConfigureOpen = null;
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
        if (!readOnly) {
            f.Operations.EntryPaths = null;
            Assert.True(f.Store.CreateTag("after-actual-read-error", [1], out _).Unwrap());
        }
    }

    [Fact]
    public void ActualMetadataShortReadReturnsRbfFailureAndClosesHealthyOwnerResources() {
        using var f = new TagScanFaultFixture();
        var seed = f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192]));
        f.Order(TagScanFaultFixture.Name);
        byte[] complete = File.ReadAllBytes(f.BucketPath(TagScanFaultFixture.Name));
        long metaOffset = seed.Tickets[0].Offset + 4 + 8192;
        bool injected = false;
        f.Tags.ConfigureOpen = file => file.BeforeRead = (offset, length, raw) => {
            if (!injected && !raw && offset == metaOffset && length == seed.Records[0].Metadata.Length) {
                injected = true;
                TagScanFaultFixture.TruncateOwnedBucket(file, metaOffset + 7);
            }
        };
        int visitors = 0;

        var result = f.Store.ScanTags(_ => { visitors++; return true; });

        Assert.True(injected);
        Assert.Equal("Rbf.ArgumentError", result.Error!.ErrorCode);
        Assert.Contains("Short read for TailMeta", result.Error.Message);
        Assert.Equal(0, visitors);
        var file = Assert.Single(f.Tags.Opened);
        Assert.Contains(file.LoggedReads(), read => read.Offset == metaOffset
            && read.Length == seed.Records[0].Metadata.Length && read.Returned == 7);
        Assert.Equal(1, file.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        File.WriteAllBytes(f.BucketPath(TagScanFaultFixture.Name), complete);
        f.Tags.ConfigureOpen = null;
        Assert.Equal(TagScanEnd.Complete, f.Store.ScanTags(_ => true).Unwrap());
    }

    [Theory]
    [InlineData("head")]
    [InlineData("trailer")]
    [InlineData("fence")]
    public void RealForwardTerminationErrorCannotBecomeCompleteAfterEarlierVisitors(string region) {
        using var f = new TagScanFaultFixture();
        var records = Enumerable.Range(0, 5).Select(_ => new TagPublicRecord(TagScanFaultFixture.Name, new byte[8192])).ToArray();
        var seed = f.SeedBucket(TagScanFaultFixture.Name, records);
        f.Order(TagScanFaultFixture.Name);
        long damaged = region switch {
            "head" => seed.Tickets[2].Offset,
            "trailer" => seed.Tickets[2].EndOffsetExclusive - 8,
            _ => seed.Tickets[2].EndOffsetExclusive
        };
        f.FlipClosedBucketByte(TagScanFaultFixture.Name, damaged);
        AteliaError? expected;
        int expectedVisitors = 0;
        using (IRbfFile raw = RbfFile.OpenReadOnlyExisting(f.BucketPath(TagScanFaultFixture.Name), RbfCacheMode.Off)) {
            var cursor = raw.ScanForward(showTombstone: true).GetEnumerator();
            while (cursor.MoveNext()) { if (cursor.Current.Tag == 1) { expectedVisitors++; } }
            expected = cursor.TerminationError;
        }
        Assert.NotNull(expected);
        var retained = new List<TagInfo>();

        var result = f.Store.ScanTags(info => { retained.Add(info); return true; });

        Assert.True(result.IsFailure);
        Assert.Equal(expected.ErrorCode, result.Error!.ErrorCode);
        Assert.Equal(expectedVisitors, retained.Count);
        Assert.NotEmpty(retained);
        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Store.Dispose();
        Assert.All(retained, info => Assert.Equal(TagScanFaultFixture.Name, info.Name));
    }

    [Fact]
    public void DirectoryMoveNextPrimaryPrecedesCursorCloseFailureAndLockIsReleasedLast() {
        using var f = new TagScanFaultFixture();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var primary = new IOException("directory MoveNext");
        var close = new IOException("directory Dispose");
        f.Operations.ConfigureEnumerator = cursor => { cursor.MoveNextFailure = primary; cursor.DisposeFailure = close; };

        var observed = Assert.Throws<AggregateException>(() => f.Store.ScanTags(_ => true));

        Assert.Equal(new Exception[] { primary, close }, observed.Flatten().InnerExceptions);
        Assert.Empty(f.Tags.Opened);
        Assert.Equal(new[] { "directory:close", "lock:close" }, f.Events.Where(entry => entry.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    [Fact]
    public void VisitorPrimaryPrecedesBucketCursorCachedAndLockCloseErrorsAndAllResourcesCloseOnce() {
        using var f = new TagScanFaultFixture();
        RefId cached = f.SeedRef();
        f.Store.ReadRef(cached).Unwrap();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        var primary = new IOException("visitor primary");
        var bucketClose = new IOException("bucket close");
        var cursorClose = new IOException("cursor close");
        var cacheClose = new IOException("cache close");
        var lockClose = new IOException("lock close");
        f.Tags.ConfigureOpen = file => file.DisposeFailure = bucketClose;
        f.Operations.ConfigureEnumerator = cursor => cursor.DisposeFailure = cursorClose;
        f.Refs.Opened[cached.LocalId].DisposeFailure = cacheClose;
        f.Lock.DisposeFailure = lockClose;

        var observed = Assert.Throws<AggregateException>(() => f.Store.ScanTags(_ => throw primary));

        Exception[] errors = observed.Flatten().InnerExceptions.ToArray();
        Assert.Equal(new Exception[] { primary, bucketClose, cursorClose, cacheClose, lockClose }, errors);
        Assert.Equal(new[] { "bucket:close", "directory:close", "close:1", "lock:close" },
            f.Events.Where(entry => entry.Contains("close", StringComparison.Ordinal)));
        f.AssertFaultedAndCleaned();
    }

    [Fact]
    public void CursorSlotIsClearedBeforeReentrantDisposeAndCannotBeClosedTwice() {
        using var f = new TagScanFaultFixture();
        f.SeedBucket(TagScanFaultFixture.Name, new TagPublicRecord(TagScanFaultFixture.Name, [1]));
        f.Order(TagScanFaultFixture.Name);
        f.Operations.ConfigureEnumerator = cursor => cursor.BeforeDispose = f.Store.Dispose;

        Assert.Throws<ObjectDisposedException>(() => f.Store.ScanTags(_ => false));

        Assert.Equal(1, Assert.Single(f.Tags.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Enumerators).DisposeCalls);
        Assert.Equal(1, f.Lock.DisposeCalls);
        Assert.Equal("lock:close", f.Events[^1]);
        f.Storage.Data.EnsureUsable();
    }
}
