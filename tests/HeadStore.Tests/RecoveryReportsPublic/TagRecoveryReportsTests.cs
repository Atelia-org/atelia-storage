using Xunit;

namespace Atelia.HeadStore.Tests.RecoveryReportsPublic;

public sealed class TagRecoveryReportsTests {
    [Theory]
    [InlineData("resolve", false)]
    [InlineData("resolve", true)]
    [InlineData("info", false)]
    [InlineData("info", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    public void EveryNamedBucketOpenReportsRecoveryAndHealthyReopensDoNotReplaceIt(string channel, bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        const string name = "recovery-named-tag";
        using (var seed = fixture.Create()) {
            Assert.True(seed.CreateTag(name, [0xff, 2, 3], out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Empty(seed.RecoveryReports);
        }
        string path = fixture.OnlyTagPath();
        uint bucket = RecoveryReportsFixture.LocalId(path);
        TailImage image = RecoveryReportsFixture.Damage(path, completeTail);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => {
                if (channel == "resolve") { readOnly.ResolveTag(name); }
                else { readOnly.ReadTagInfo(name); }
            });
            Assert.Empty(readOnly.RecoveryReports);
            readOnly.EnsureUsable();
            fixture.AssertUnchangedWithHashes(before);
        }
        using (var writer = fixture.Open()) {
            var view = writer.RecoveryReports;
            Assert.Empty(view);
            switch (channel) {
                case "resolve": Assert.Equal(new byte[] { 0xff, 2, 3 }, writer.ResolveTag(name).Unwrap().Bytes.ToArray()); break;
                case "info": Assert.Equal(name, writer.ReadTagInfo(name).Unwrap().Name); break;
                case "create":
                    Assert.Equal("HeadStore.DuplicateTagName", writer.CreateTag(name, [4], out var outcome).Error!.ErrorCode);
                    Assert.Equal(PublicationOutcome.NotAttempted, outcome); // Physical repair is independent of publication.
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(channel));
            }
            image.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.TagBucket, bucket);
            HeadStoreRecoveryReport saved = view[0];
            RecoveryReportsFixture.AssertReleased(path);
            Assert.Equal(new byte[] { 0xff, 2, 3 }, writer.ResolveTag(name).Unwrap().Bytes.ToArray());
            Assert.Equal(name, writer.ReadTagInfo(name).Unwrap().Name);
            Assert.Equal(TagScanEnd.Complete, writer.ScanTags(_ => true).Unwrap());
            Assert.Equal(new[] { saved }, view);
            RecoveryReportsFixture.AssertReleased(path);
        }
        image.AssertRestored();
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0u, true)]
    [InlineData(1023u, false)]
    [InlineData(1023u, true)]
    public void ScanReportsEndpointBucketBeforeVisitorAndNamedBorrowAddsNoAction(uint bucket, bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        string name = RecoveryReportsFixture.NameForBucket(bucket);
        using (var seed = fixture.Create()) {
            Assert.True(seed.CreateTag(name, [0xfa, 9], out _).Unwrap());
        }
        string path = fixture.OnlyTagPath();
        Assert.Equal(bucket, RecoveryReportsFixture.LocalId(path));
        TailImage image = RecoveryReportsFixture.Damage(path, completeTail);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ScanTags(_ => throw new InvalidOperationException("No visitor precedes qualification.")); });
            Assert.Empty(readOnly.RecoveryReports);
            fixture.AssertUnchangedWithHashes(before);
        }
        using (var writer = fixture.Open()) {
            var view = writer.RecoveryReports;
            Assert.Empty(view);
            int visits = 0;
            Assert.Equal(TagScanEnd.Complete, writer.ScanTags(info => {
                visits++;
                Assert.Equal(name, info.Name);
                image.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.TagBucket, bucket);
                Assert.Equal(name, writer.ReadTagInfo(name).Unwrap().Name);
                Assert.Equal(new byte[] { 0xfa, 9 }, writer.ResolveTag(name).Unwrap().Bytes.ToArray());
                Assert.Single(view); // Both named reads borrow this scan's already-open bucket.
                return true;
            }).Unwrap());
            Assert.Equal(1, visits);
            RecoveryReportsFixture.AssertReleased(path);
            HeadStoreRecoveryReport saved = view[0];
            Assert.Equal(TagScanEnd.Complete, writer.ScanTags(_ => true).Unwrap());
            Assert.Equal(name, writer.ReadTagInfo(name).Unwrap().Name);
            Assert.Equal(new[] { saved }, view); // Fresh healthy opens retain the original action.
        }
        image.AssertRestored();
    }
}
