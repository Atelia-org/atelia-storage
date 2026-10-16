using Atelia.HeadStore.Tests.TagPublic;
using Xunit;

namespace Atelia.HeadStore.Tests.AuditFaults;

public sealed class AuditActiveGuardTests {
    [Fact]
    public void HistoryVisitorAuditRejectionDoesNotTouchHistoryReaderOrStartDiscovery() {
        using var f = new AuditFaultFixture();
        AuditHeadSeed seed = f.SeedHead(1, null, [1], [2]);
        int visits = 0;

        HeadHistoryEnd end = f.Store.ReadHeadHistory(seed.HeadId, new HeadHistoryBudget(long.MaxValue, long.MaxValue), _ => {
            visits++;
            AuditFile history = Assert.Single(f.Files.Opened);
            int opens = f.Files.Opened.Count;
            int cursors = f.Operations.Cursors.Count;

            var rejected = f.Store.Audit();

            Assert.Equal("HeadStore.HistoryActive", rejected.Error!.ErrorCode);
            Assert.Equal(opens, f.Files.Opened.Count);
            Assert.Equal(cursors, f.Operations.Cursors.Count);
            Assert.Equal(0, history.DisposeCalls);
            Assert.Equal(1, f.Files.ActiveFiles);
            return true;
        }).Unwrap();

        Assert.Equal(HeadHistoryEnd.Complete, end);
        Assert.Equal(2, visits);
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }

    [Fact]
    public void TagVisitorAuditRejectionDoesNotCloseCurrentBucketOrCursor() {
        using var f = new AuditFaultFixture();
        f.SeedTagBucket(AuditFaultFixture.TagName,
            new TagPublicRecord(AuditFaultFixture.TagName, [1]),
            new TagPublicRecord(AuditFaultFixture.SameBucketName, [2]));
        int visits = 0;

        TagScanEnd end = f.Store.ScanTags(_ => {
            visits++;
            AuditFile bucket = Assert.Single(f.Files.Opened);
            var cursor = Assert.Single(f.Operations.Cursors).Cursor;
            int opens = f.Files.Opened.Count;

            var rejected = f.Store.Audit();

            Assert.Equal("HeadStore.TagScanActive", rejected.Error!.ErrorCode);
            Assert.Equal(opens, f.Files.Opened.Count);
            Assert.Single(f.Operations.Cursors);
            Assert.Equal(0, bucket.DisposeCalls);
            Assert.Equal(0, cursor.DisposeCalls);
            return true;
        }).Unwrap();

        Assert.Equal(TagScanEnd.Complete, end);
        Assert.Equal(2, visits);
        Assert.Equal(1, Assert.Single(f.Files.Opened).DisposeCalls);
        Assert.Equal(1, Assert.Single(f.Operations.Cursors).Cursor.DisposeCalls);
        Assert.Equal(0, f.Lock.DisposeCalls);
        Assert.True(f.Store.Audit().IsSuccess);
        f.AssertNoWrites();
    }
}
