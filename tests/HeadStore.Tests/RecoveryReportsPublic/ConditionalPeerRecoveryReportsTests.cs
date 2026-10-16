using Xunit;

namespace Atelia.HeadStore.Tests.RecoveryReportsPublic;

public sealed class ConditionalPeerRecoveryReportsTests {
    [Theory]
    [InlineData("current", false)]
    [InlineData("current", true)]
    [InlineData("exact", false)]
    [InlineData("exact", true)]
    [InlineData("history", false)]
    [InlineData("history", true)]
    public void NecessaryPeerRecoveryIsReportedEvenWhenLocalMemberOpenedHealthy(string channel, bool completeTail) {
        using var fixture = new RecoveryReportsFixture();
        HeadEntryId a;
        HeadEntryId b;
        using (var seed = fixture.Create()) {
            HeadEntryId initialA = seed.CreateHead(7, [1], out _).Unwrap();
            HeadEntryId initialB = seed.CreateHead(19, [2], out _).Unwrap();
            var changes = new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [initialA.HeadId] = new byte[] { 3 },
                [initialB.HeadId] = new byte[] { 4 }
            };
            var group = seed.PublishHeads(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            a = group[initialA.HeadId];
            b = group[initialB.HeadId];
        }
        TailImage image = RecoveryReportsFixture.Damage(fixture.HeadPath(b.HeadId.LocalId), completeTail);
        var before = fixture.CaptureFiles();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => HeadRecoveryReportsTests.Access(readOnly, a, channel));
            Assert.Empty(readOnly.RecoveryReports);
            readOnly.EnsureUsable();
            fixture.AssertUnchangedWithHashes(before);
        }
        using (var writer = fixture.Open()) {
            var view = writer.RecoveryReports;
            Assert.Empty(view);
            HeadRecoveryReportsTests.Access(writer, a, channel);
            image.AssertReport(Assert.Single(view), HeadStoreRecoveryFileKind.Head, b.HeadId.LocalId);
            HeadStoreRecoveryReport saved = view[0];
            RecoveryReportsFixture.AssertReleased(image.Path); // A necessary peer is a temporary, not a cached ref.
            HeadRecoveryReportsTests.Access(writer, a, channel); // The next peer factory returns None.
            Assert.Equal(b, writer.ReadHead(b.HeadId).Unwrap().Id);
            Assert.Equal(a, writer.ReadHead(a.HeadId).Unwrap().Id);
            Assert.Equal(new[] { saved }, view);
        }
        image.AssertRestored();
    }
}
