using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Tests.TagPublic;
using Xunit;

namespace Atelia.VersionStore.Tests.AuditFaults;

public sealed class AuditResourceTests {
    [Fact]
    public void WritableOwnerRejectsBeforeAnyAuditIo() {
        using var f = new AuditFaultFixture(readOnly: false);
        f.SeedRef(1);
        f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1]));

        var result = f.Store.Audit();

        Assert.Equal("VersionStore.ReadOnlyRequired", result.Error!.ErrorCode);
        Assert.Empty(f.Operations.Cursors);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.AssertNoWrites();
    }

    [Fact]
    public void DisposedOwnerRejectsBeforeAnyAuditIo() {
        using var f = new AuditFaultFixture();
        f.Store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => f.Store.Audit());

        Assert.Empty(f.Operations.Cursors);
        Assert.Empty(f.Files.Opened);
        Assert.Equal(1, f.Lock.DisposeCalls);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false, "move")]
    [InlineData(false, "current")]
    [InlineData(true, "move")]
    [InlineData(true, "current")]
    public void ActualDirectoryAccessExceptionWithHealthyCleanupPreservesOwner(bool tag, string stage) {
        using var f = new AuditFaultFixture();
        if (tag) { f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1])); }
        else { f.SeedRef(1); }
        var primary = new IOException("directory " + stage);
        f.Operations.ConfigureCursor = (isTag, cursor) => {
            if (isTag != tag) { return; }
            if (stage == "move") { cursor.MoveNextFailure = primary; }
            else { cursor.CurrentFailure = primary; }
        };

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.Audit()));

        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Operations.ConfigureCursor = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyFactoryExceptionWithHealthyCleanupPreservesOwner(bool tag) {
        using var f = new AuditFaultFixture();
        if (tag) { f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1])); }
        else { f.SeedRef(1); }
        var primary = new IOException("read-only factory open");
        if (tag) { f.Files.TagOpenFailure = primary; }
        else { f.Files.RefOpenFailure = primary; }

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.Audit()));

        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Empty(f.Files.Opened);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.TagOpenFailure = null;
        f.Files.RefOpenFailure = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false, "complete-read")]
    [InlineData(false, "rent")]
    [InlineData(true, "complete-read")]
    [InlineData(true, "rent")]
    public void ExceptionAtActualCompleteReadOrRentPreservesOriginalExceptionAndOwner(bool tag, string stage) {
        using var f = new AuditFaultFixture();
        Atelia.Data.SizedPtr ticket = tag
            ? f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, new byte[8192])).Tickets[0]
            : f.SeedRef(1, null, new byte[8192]).Tickets[0];
        var primary = new IOException("actual " + stage);
        f.Files.ConfigureOpen = file => {
            if (file.IsTag != tag) { return; }
            file.BeforeRead = (offset, length, raw) => {
                if (stage == "complete-read" && !raw && offset == ticket.Offset && length == ticket.Length) { throw primary; }
            };
            file.BeforeRent = length => { if (stage == "rent" && length == ticket.Length) { throw primary; } };
        };

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.Audit()));

        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.ConfigureOpen = null;
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Fact]
    public void CorruptInitialSnapshotIsContentFailureAndDoesNotFaultReadOnlyOwnerOrRepairBytes() {
        using var f = new AuditFaultFixture();
        AuditRefSeed seed = f.SeedRef(1, null, AuditFaultFixture.OpaquePayload(), [2]);
        f.CorruptRefPayload(seed, 0);
        byte[] before = File.ReadAllBytes(f.Storage.RefPath(1));

        var result = f.Store.Audit();

        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(f.Storage.RefPath(1)));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(seed.Tickets[1], f.Store.ReadRef(seed.RefId).Unwrap().Revision.Ticket);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyFactoryRejectsRealIncompleteEofWithoutRepairOrFault(bool tag) {
        using var f = new AuditFaultFixture();
        string path;
        if (tag) {
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1]));
            path = f.BucketPath(AuditFaultFixture.TagName);
        }
        else { f.SeedRef(1); path = f.Storage.RefPath(1); }
        byte[] complete = File.ReadAllBytes(path);
        byte[] incomplete = complete[..^8];
        File.WriteAllBytes(path, incomplete); // Controlled mirror: complete body without the final Key/Fence.

        Assert.Throws<InvalidDataException>(() => f.Store.Audit());

        Assert.Equal(incomplete, File.ReadAllBytes(path));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Empty(f.Files.Opened);
        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        File.WriteAllBytes(path, complete);
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderCloseFailurePreventsReportAndClosesCachedReaderBeforeLockExactlyOnce(bool tag) {
        using var f = new AuditFaultFixture();
        AuditRefSeed cachedSeed = f.SeedRef(9);
        f.Store.ReadRef(cachedSeed.RefId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);
        if (tag) {
            f.OrderRefs();
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1]));
        }
        else { f.OrderRefs(cachedSeed); }
        var close = new IOException("audit reader close");
        f.Files.ConfigureOpen = file => { if (file.IsTag == tag) { file.DisposeFailure = close; } };

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.Audit()));

        Assert.Equal(1, cached.DisposeCalls);
        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CursorCloseFailurePreventsReportAndClosesCachedReaderBeforeLockExactlyOnce(bool tag) {
        using var f = new AuditFaultFixture();
        AuditRefSeed cachedSeed = f.SeedRef(9);
        f.Store.ReadRef(cachedSeed.RefId).Unwrap();
        if (tag) {
            f.OrderRefs();
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1]));
        }
        else { f.OrderRefs(cachedSeed); }
        var close = new IOException("audit cursor close");
        f.Operations.ConfigureCursor = (isTag, cursor) => { if (isTag == tag) { cursor.DisposeFailure = close; } };

        Assert.Same(close, Assert.Throws<IOException>(() => f.Store.Audit()));

        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadErrorPrecedesReaderCursorCachedReaderAndLockCloseErrors(bool tag) {
        using var f = new AuditFaultFixture();
        AuditRefSeed cachedSeed = f.SeedRef(9);
        f.Store.ReadRef(cachedSeed.RefId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);
        Atelia.Data.SizedPtr ticket;
        if (tag) {
            f.OrderRefs();
            ticket = f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, [1])).Header;
        }
        else { f.OrderRefs(cachedSeed); ticket = cachedSeed.Header; }
        var primary = new IOException("actual header read");
        var readerClose = new IOException("audit reader close");
        var cursorClose = new IOException("audit cursor close");
        var cachedClose = new IOException("cached reader close");
        var lockClose = new IOException("root lock close");
        cached.DisposeFailure = cachedClose;
        f.Lock.DisposeFailure = lockClose;
        f.Operations.ConfigureCursor = (isTag, cursor) => { if (isTag == tag) { cursor.DisposeFailure = cursorClose; } };
        f.Files.ConfigureOpen = file => {
            if (file.IsTag != tag) { return; }
            file.BeforeRead = (offset, length, raw) => {
                if (!raw && offset == ticket.Offset && length == ticket.Length) { throw primary; }
            };
            file.DisposeFailure = readerClose;
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.Audit());

        Exception[] errors = AuditFaultFixture.Flatten(observed);
        Assert.Same(primary, errors[0]);
        Assert.Equal(5, errors.Length);
        Assert.Contains(readerClose, errors);
        Assert.Contains(cursorClose, errors);
        Assert.Contains(cachedClose, errors);
        Assert.Same(lockClose, errors[^1]);
        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }

    [Fact]
    public void ForkCloneChildReadErrorPrecedesBothLiveReaderCloseFailuresAndReleasesLockLast() {
        using var f = new AuditFaultFixture();
        AuditRefSeed source = f.SeedRef(1);
        AuditRefSeed child = f.SeedRef(2, new ForkOrigin(source.RefId, source.Tickets[0]), source.Payloads[0]);
        f.OrderRefs(source, child);
        var primary = new IOException("actual child clone read");
        var childClose = new IOException("clone child close");
        var sourceClose = new IOException("clone source close");
        f.Files.ConfigureOpen = file => {
            file.BeforeRead = (offset, length, raw) => {
                if (raw) { return; }
                if (file.Id == source.RefId.LocalId && offset == source.Tickets[0].Offset && length == source.Tickets[0].Length) {
                    file.DisposeFailure = sourceClose;
                }
                if (file.Id == child.RefId.LocalId && offset == child.Tickets[0].Offset && length == child.Tickets[0].Length) {
                    // Defer the close fault until this actual complete clone read: discovery closes remain healthy.
                    Assert.Equal(2, f.Files.ActiveFiles);
                    file.DisposeFailure = childClose;
                    throw primary;
                }
            };
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.Audit());

        Assert.Equal(new Exception[] { primary, childClose, sourceClose }, AuditFaultFixture.Flatten(observed));
        Assert.Equal(2, f.Files.MaximumActiveFiles);
        Assert.Equal(new[] { "ref:close:2", "ref:close:1", "lock:close" }, f.Closes()[^3..]);
        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }

    [Fact]
    public void ManyRefsUseAtMostTwoShortReadersAndDoNotPopulateOwnerCache() {
        using var f = new AuditFaultFixture();
        const int count = 80;
        AuditRefSeed source = f.SeedRef(1);
        for (uint id = 2; id <= count; id++) {
            f.SeedRef(id, new ForkOrigin(source.RefId, source.Tickets[0]), source.Payloads[0]);
        }
        byte[][] before = Enumerable.Range(1, count).Select(id => File.ReadAllBytes(f.Storage.RefPath((uint)id))).ToArray();

        VersionStoreAudit first = f.Store.Audit().Unwrap();

        Assert.Equal((long)count, first.RefCount);
        Assert.Equal((long)count, first.SnapshotCount);
        Assert.Equal((long)count - 1, first.ForkCount);
        Assert.InRange(f.Files.MaximumActiveFiles, 1, 2);
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(0, f.Files.ListCalls); // The full target set is not first materialized through ListRefs.
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        int opens = f.Files.Opened.Count;
        Assert.Equal(count, f.Store.Audit().Unwrap().RefCount);
        Assert.True(f.Files.Opened.Count > opens);
        Assert.Equal(0, f.Files.ActiveFiles);
        for (int index = 0; index < count; index++) { Assert.Equal(before[index], File.ReadAllBytes(f.Storage.RefPath((uint)index + 1))); }
        f.AssertNoWrites();
    }

    [Fact]
    public void IndependentAuditReadersDoNotCloseOrReplacePreviouslyCachedRef() {
        using var f = new AuditFaultFixture();
        AuditRefSeed source = f.SeedRef(1, null, AuditFaultFixture.OpaquePayload(), [2]);
        f.SeedRef(2, new ForkOrigin(source.RefId, source.Tickets[0]), source.Payloads[0]);
        f.Store.ReadRef(source.RefId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);

        VersionStoreAudit retained = f.Store.Audit().Unwrap();

        Assert.Equal(2, retained.RefCount);
        Assert.Equal(0, cached.DisposeCalls);
        Assert.Equal(1, f.Files.ActiveFiles);
        Assert.Contains(f.Files.Opened, file => file.Id == 1 && !ReferenceEquals(file, cached));
        Assert.All(f.Files.Opened.Where(file => !ReferenceEquals(file, cached)), file => Assert.Equal(1, file.DisposeCalls));
        int opens = f.Files.Opened.Count;
        Assert.Equal(source.Tickets[1], f.Store.ReadRef(source.RefId).Unwrap().Revision.Ticket);
        Assert.Equal(opens, f.Files.Opened.Count);
        f.Store.Dispose();
        Assert.Equal(1, cached.DisposeCalls);
        Assert.Equal(2, retained.RefCount);
        Assert.Equal(3, retained.SnapshotCount);
        f.Data.EnsureUsable();
        f.AssertNoWrites();
    }

    [Fact]
    public void OpaqueApplicationBytesAndActiveDataBuilderDoNotTriggerDataAuditDecodeOrConfirmation() {
        using var f = new AuditFaultFixture();
        byte[] opaque = AuditFaultFixture.OpaquePayload();
        Assert.True(RootMapCodec.Decode(opaque).IsFailure);
        f.SeedRef(1, null, opaque);
        f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, opaque));
        using var builder = f.Data.BeginAppend(3, 0, out _);
        var writer = builder.PayloadAndMeta;
        new byte[] { 1, 2, 3 }.CopyTo(writer.GetSpan(3));
        writer.Advance(3);

        VersionStoreAudit report = f.Store.Audit().Unwrap();

        Assert.Equal(1, report.RefCount);
        Assert.Equal(1, report.TagCount);
        Assert.True(builder.EndAppend(42).IsSuccess); // The same active data lease remains usable.
        f.Store.Dispose();
        f.Data.EnsureUsable();
        f.AssertNoWrites();
    }
}
