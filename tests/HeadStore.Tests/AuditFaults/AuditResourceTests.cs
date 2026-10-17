using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditFaults;

public sealed class AuditResourceTests {
    [Fact]
    public void WritableOwnerRejectsBeforeAnyAuditIo() {
        using var f = new AuditFaultFixture(readOnly: false);
        f.SeedHead(1);
        f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));

        var result = f.Store.Audit();

        Assert.Equal("HeadStore.ReadOnlyRequired", result.Error!.ErrorCode);
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
        if (tag) { f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers())); }
        else { f.SeedHead(1); }
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
        if (tag) { f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers())); }
        else { f.SeedHead(1); }
        var primary = new IOException("read-only factory open");
        if (tag) { f.Files.TagOpenFailure = primary; }
        else { f.Files.HeadOpenFailure = primary; }

        Assert.Same(primary, Assert.Throws<IOException>(() => f.Store.Audit()));

        Assert.All(f.Operations.Cursors, entry => Assert.Equal(1, entry.Cursor.DisposeCalls));
        Assert.DoesNotContain(f.Files.Opened, file => file.IsTag == tag);
        Assert.Equal(0, f.Lock.DisposeCalls);
        f.Files.TagOpenFailure = null;
        f.Files.HeadOpenFailure = null;
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
            ? f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers())).Tickets[0]
            : f.SeedHead(1, null, new byte[8192]).Tickets[0];
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
    public void CorruptInitialEntryIsContentFailureAndDoesNotFaultReadOnlyOwnerOrRepairBytes() {
        using var f = new AuditFaultFixture();
        AuditHeadSeed seed = f.SeedHead(1, null, AuditFaultFixture.OpaquePayload(), [2]);
        f.CorruptHeadPayload(seed, 0);
        byte[] before = File.ReadAllBytes(f.Storage.HeadPath(1));

        var result = f.Store.Audit();

        Assert.True(result.IsFailure);
        Assert.StartsWith("Rbf.", result.Error!.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(f.Storage.HeadPath(1)));
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.Equal(seed.Tickets[1], f.Store.ReadHead(seed.HeadId).Unwrap().Id.Ticket);
        f.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyFactoryRejectsRealIncompleteEofWithoutRepairOrFault(bool tag) {
        using var f = new AuditFaultFixture();
        string path;
        if (tag) {
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
            path = f.BucketPath(AuditFaultFixture.TagName);
        }
        else { f.SeedHead(1); path = f.Storage.HeadPath(1); }
        byte[] complete = File.ReadAllBytes(path);
        byte[] incomplete = complete[..^8];
        File.WriteAllBytes(path, incomplete); // Controlled mirror: complete body without the final Key/Fence.

        Assert.Throws<InvalidDataException>(() => f.Store.Audit());

        Assert.Equal(incomplete, File.ReadAllBytes(path));
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.DoesNotContain(f.Files.Opened, file => file.IsTag == tag);
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
        AuditHeadSeed cachedSeed = f.SeedHead(9);
        f.Store.ReadHead(cachedSeed.HeadId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);
        if (tag) {
            f.OrderHeads();
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
        }
        else { f.OrderHeads(cachedSeed); }
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
        AuditHeadSeed cachedSeed = f.SeedHead(9);
        f.Store.ReadHead(cachedSeed.HeadId).Unwrap();
        if (tag) {
            f.OrderHeads();
            f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
        }
        else { f.OrderHeads(cachedSeed); }
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
        AuditHeadSeed cachedSeed = f.SeedHead(9);
        f.Store.ReadHead(cachedSeed.HeadId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);
        Atelia.Data.SizedPtr ticket;
        if (tag) {
            f.OrderHeads();
            ticket = f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers())).Header;
        }
        else { f.OrderHeads(cachedSeed); ticket = cachedSeed.Header; }
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
    public void SourceEntryReadErrorForHeaderOnlyForkPrecedesReaderCloseFailureAndReleasesLockLast() {
        using var f = new AuditFaultFixture();
        AuditHeadSeed source = f.SeedHead(1);
        AuditHeadSeed child = f.SeedHead(2, new ForkOrigin(source.HeadId, source.Tickets[0]));
        f.OrderHeads(child, source);
        var primary = new IOException("actual referenced source read");
        var sourceClose = new IOException("source reader close");
        f.Files.ConfigureOpen = file => {
            file.BeforeRead = (offset, length, raw) => {
                if (!raw && file.Id == source.HeadId.GetLocalId(f.Identity)
                    && offset == source.Tickets[0].Offset && length == source.Tickets[0].Length) {
                    // Discovery closes are healthy; fault only the actual source Entry reader.
                    file.DisposeFailure = sourceClose;
                    throw primary;
                }
            };
        };

        var observed = Assert.Throws<AggregateException>(() => f.Store.Audit());

        Assert.Equal(new Exception[] { primary, sourceClose }, AuditFaultFixture.Flatten(observed));
        Assert.Equal(1, f.Files.MaximumActiveFiles);
        Assert.Equal(new[] { "ref:close:1", "lock:close" }, f.Closes()[^2..]);
        f.AssertFaultedAndCleaned();
        f.AssertNoWrites();
    }

    [Fact]
    public void ManyHeaderOnlyForksShareOneCheckedSourceAndDoNotPopulateOwnerCache() {
        using var f = new AuditFaultFixture();
        const int count = 80;
        AuditHeadSeed source = f.SeedHead(1);
        for (uint id = 2; id <= count; id++) {
            f.SeedHead(id, new ForkOrigin(source.HeadId, source.Tickets[0]));
        }
        byte[][] before = Enumerable.Range(1, count).Select(id => File.ReadAllBytes(f.Storage.HeadPath((uint)id))).ToArray();

        HeadStoreAudit first = f.Store.Audit().Unwrap();

        Assert.Equal((long)count, first.HeadCount);
        Assert.Equal(1L, first.EntryCount);
        Assert.Equal((long)count - 1, first.OriginCount);
        Assert.Equal(1, f.Files.MaximumActiveFiles);
        Assert.Equal(0, f.Files.ActiveFiles);
        Assert.Equal(0, f.Files.ListCalls); // The full target set is not first materialized through ListHeads.
        Assert.All(f.Files.Opened, file => Assert.Equal(1, file.DisposeCalls));
        int opens = f.Files.Opened.Count;
        Assert.Equal(count, f.Store.Audit().Unwrap().HeadCount);
        Assert.True(f.Files.Opened.Count > opens);
        Assert.Equal(0, f.Files.ActiveFiles);
        for (int index = 0; index < count; index++) { Assert.Equal(before[index], File.ReadAllBytes(f.Storage.HeadPath((uint)index + 1))); }
        f.AssertNoWrites();
    }

    [Fact]
    public void IndependentAuditReadersDoNotCloseOrReplacePreviouslyCachedHead() {
        using var f = new AuditFaultFixture();
        AuditHeadSeed source = f.SeedHead(1, null, AuditFaultFixture.OpaquePayload(), [2]);
        f.SeedHead(2, new ForkOrigin(source.HeadId, source.Tickets[0]), source.Payloads[0]);
        f.Store.ReadHead(source.HeadId).Unwrap();
        AuditFile cached = Assert.Single(f.Files.Opened);

        HeadStoreAudit retained = f.Store.Audit().Unwrap();

        Assert.Equal(2, retained.HeadCount);
        Assert.Equal(0, cached.DisposeCalls);
        Assert.Equal(1, f.Files.ActiveFiles);
        Assert.Contains(f.Files.Opened, file => file.Id == 1 && !ReferenceEquals(file, cached));
        Assert.All(f.Files.Opened.Where(file => !ReferenceEquals(file, cached)), file => Assert.Equal(1, file.DisposeCalls));
        int opens = f.Files.Opened.Count;
        Assert.Equal(source.Tickets[1], f.Store.ReadHead(source.HeadId).Unwrap().Id.Ticket);
        Assert.Equal(opens, f.Files.Opened.Count);
        f.Store.Dispose();
        Assert.Equal(1, cached.DisposeCalls);
        Assert.Equal(2, retained.HeadCount);
        Assert.Equal(3, retained.EntryCount);
        f.Data.EnsureUsable();
        f.AssertNoWrites();
    }

    [Fact]
    public void OpaqueApplicationBytesAndActiveDataBuilderDoNotTriggerDataAuditDecodeOrConfirmation() {
        using var f = new AuditFaultFixture();
        byte[] opaque = AuditFaultFixture.OpaquePayload();
        Assert.True(NamedFrameMapCodec.Decode(opaque).IsFailure);
        f.SeedHead(1, null, opaque);
        f.SeedTagBucket(AuditFaultFixture.TagName, new TagPublicRecord(AuditFaultFixture.TagName, f.TagMembers()));
        using var builder = f.Data.BeginAppend(3, 0, out _);
        var writer = builder.PayloadAndMeta;
        new byte[] { 1, 2, 3 }.CopyTo(writer.GetSpan(3));
        writer.Advance(3);

        HeadStoreAudit report = f.Store.Audit().Unwrap();

        Assert.Equal(1, report.HeadCount);
        Assert.Equal(1, report.TagCount);
        Assert.True(builder.EndAppend(42).IsSuccess); // The same active data lease remains usable.
        f.Store.Dispose();
        f.Data.EnsureUsable();
        f.AssertNoWrites();
    }
}
