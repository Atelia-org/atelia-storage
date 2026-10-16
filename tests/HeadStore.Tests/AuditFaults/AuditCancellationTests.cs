using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditFaults;

public sealed class AuditCancellationTests {
    [Fact]
    public void PreCancelledRequestDoesNoEnumerationOrOpenAndKeepsReadOnlyOwnerUsable() {
        using var f = new AuditFaultFixture();
        f.SeedHead(1);
        f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.Audit(cancellation.Token)).CancellationToken);

        Assert.Empty(f.Operations.Cursors);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false, "move")]
    [InlineData(false, "current")]
    [InlineData(false, "eof")]
    [InlineData(false, "before-close")]
    [InlineData(false, "after-close")]
    [InlineData(true, "move")]
    [InlineData(true, "current")]
    [InlineData(true, "eof")]
    [InlineData(true, "before-close")]
    [InlineData(true, "after-close")]
    public void DirectoryCancellationAtRealBoundaryPreventsResultAndReleasesResources(bool tagDirectory, string stage) {
        using var f = new AuditFaultFixture();
        using var cancellation = new CancellationTokenSource();
        if (tagDirectory) {
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
            f.OrderTags(AuditFaultFixture.TagName);
        }
        else {
            AuditHeadSeed seed = f.SeedHead(1);
            f.OrderHeads(seed);
        }
        f.Operations.ConfigureCursor = (isTag, cursor) => {
            if (isTag != tagDirectory) { return; }
            if (stage == "move") { cursor.AfterMoveNext = (_, present) => { if (present) { cancellation.Cancel(); } }; }
            if (stage == "current") { cursor.AfterCurrent = cancellation.Cancel; }
            if (stage == "eof") { cursor.AfterMoveNext = (_, present) => { if (!present) { cancellation.Cancel(); } }; }
            if (stage == "before-close") { cursor.BeforeDispose = cancellation.Cancel; }
            if (stage == "after-close") { cursor.AfterDispose = cancellation.Cancel; }
        };

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.Audit(cancellation.Token)).CancellationToken);

        var targeted = Assert.Single(f.Operations.Cursors, entry => entry.IsTag == tagDirectory).Cursor;
        Assert.Equal(1, targeted.DisposeCalls);
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        if (stage is "move" or "current") { Assert.DoesNotContain(f.Files.Opened, file => file.IsTag == tagDirectory); }
        if (stage == "move") { Assert.Equal(0, targeted.CurrentCalls); }
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Operations.ConfigureCursor = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false, "open")]
    [InlineData(false, "format")]
    [InlineData(false, "forward")]
    [InlineData(false, "complete-read")]
    [InlineData(false, "rent")]
    [InlineData(false, "close")]
    [InlineData(true, "open")]
    [InlineData(true, "format")]
    [InlineData(true, "forward")]
    [InlineData(true, "complete-read")]
    [InlineData(true, "rent")]
    [InlineData(true, "close")]
    public void CancellationAtActualFileWorkDoesNotReadLaterRecordsOrFaultOwner(bool tag, string stage) {
        using var f = new AuditFaultFixture();
        using var cancellation = new CancellationTokenSource();
        Atelia.Data.SizedPtr first;
        Atelia.Data.SizedPtr later;
        if (tag) {
            AuditTagSeed seed = f.SeedTagBucket(AuditFaultFixture.TagName,
                new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()),
                new TagPublicRecord(AuditFaultFixture.SameBucketName, f.TagMembers()));
            first = seed.Tickets[0];
            later = seed.Tickets[1];
        }
        else {
            AuditHeadSeed seed = f.SeedHead(1, null, new byte[8192], [2]);
            first = seed.Tickets[0];
            later = seed.Tickets[1];
        }
        f.Files.AfterOpen = file => { if (file.IsTag == tag && stage == "open") { cancellation.Cancel(); } };
        f.Files.ConfigureOpen = file => {
            if (file.IsTag != tag) { return; }
            file.AfterAccess = access => { if (access == stage) { cancellation.Cancel(); } };
            file.BeforeRead = (offset, length, raw) => {
                if (stage == "complete-read" && !raw && offset == first.Offset && length == first.Length) { cancellation.Cancel(); }
            };
            file.BeforeRent = length => { if (stage == "rent" && length == first.Length) { cancellation.Cancel(); } };
            if (stage == "close") { file.AfterDispose = cancellation.Cancel; }
        };

        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(() => f.Store.Audit(cancellation.Token)).CancellationToken);

        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        if (stage != "close" || !tag) {
            Assert.All(f.Files.Opened, file => Assert.Equal(0, file.CompleteReads(later)));
        }
        if (stage == "complete-read") {
            Assert.Contains(f.Files.Opened, file => file.Reads.Any(read => !read.Raw && read.Offset == first.Offset && read.Length == first.Length));
        }
        if (stage == "rent") { Assert.Contains(f.Files.Opened, file => file.Rents.Contains(first.Length)); }
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.AfterOpen = null;
        f.Files.ConfigureOpen = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStaysPrimaryWhenCurrentReaderCursorCachedReaderAndLockCloseFail(bool tag) {
        using var f = new AuditFaultFixture();
        using var cancellation = new CancellationTokenSource();
        AuditHeadSeed cachedSeed = f.SeedHead(9);
        f.Store.ReadHead(cachedSeed.HeadId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);
        var cachedClose = new IOException("cached reader close");
        var readerClose = new IOException("audit reader close");
        var cursorClose = new IOException("audit cursor close");
        var lockClose = new IOException("owner lock close");
        cached.DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        if (tag) {
            f.OrderHeads();
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
        }
        else { f.OrderHeads(cachedSeed); }
        f.Operations.ConfigureCursor = (isTag, cursor) => { if (isTag == tag) { cursor.DisposeFailure = cursorClose; } };
        f.Files.ConfigureOpen = file => {
            if (file.IsTag != tag) { return; }
            file.AfterAccess = access => { if (access == "forward") { cancellation.Cancel(); } };
            file.DisposeFailure = readerClose;
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.Audit(cancellation.Token));

        Exception[] errors = AuditFaultFixture.Flatten(observed);
        Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(errors[0]).CancellationToken);
        Assert.Contains(readerClose, errors);
        Assert.Contains(cursorClose, errors);
        Assert.Contains(cachedClose, errors);
        Assert.Same(lockClose, errors[^1]);
        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }
}
