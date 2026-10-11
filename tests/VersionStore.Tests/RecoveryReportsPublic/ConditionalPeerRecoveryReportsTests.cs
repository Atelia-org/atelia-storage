using Xunit;

namespace Atelia.VersionStore.Tests.RecoveryReportsPublic;

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
        RefRevision a;
        RefRevision b;
        using (var seed = fixture.Create()) {
            RefRevision initialA = seed.CreateRef(7, [1], out _).Unwrap();
            RefRevision initialB = seed.CreateRef(19, [2], out _).Unwrap();
            var changes = new Dictionary<RefId, ReadOnlyMemory<byte>> {
                [initialA.RefId] = new byte[] { 3 },
                [initialB.RefId] = new byte[] { 4 }
            };
            var group = seed.PublishRefs(changes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            a = group[initialA.RefId];
            b = group[initialB.RefId];
        }
        TailImage image = RecoveryReportsFixture.Damage(fixture.RefPath(b.RefId.LocalId), completeTail);
        var before = fixture.Snapshot();
        using (var readOnly = fixture.Open(readOnly: true)) {
            Assert.Throws<InvalidDataException>(() => RefRecoveryReportsTests.Access(readOnly, a, channel));
            Assert.Empty(readOnly.RecoveryReports);
            readOnly.EnsureUsable();
            fixture.AssertUnchangedWithHashes(before);
        }
        using (var writer = fixture.Open()) {
            var view = writer.RecoveryReports;
            Assert.Empty(view);
            RefRecoveryReportsTests.Access(writer, a, channel);
            image.AssertReport(Assert.Single(view), VersionStoreRecoveryFileKind.Ref, b.RefId.LocalId);
            VersionStoreRecoveryReport saved = view[0];
            RecoveryReportsFixture.AssertReleased(image.Path); // A necessary peer is a temporary, not a cached ref.
            RefRecoveryReportsTests.Access(writer, a, channel); // The next peer factory returns None.
            Assert.Equal(b, writer.ReadRef(b.RefId).Unwrap().Revision);
            Assert.Equal(a, writer.ReadRef(a.RefId).Unwrap().Revision);
            Assert.Equal(new[] { saved }, view);
        }
        image.AssertRestored();
    }
}
