using Atelia.Rbf;
using Atelia.VersionStore.Tests.History;
using Xunit;

namespace Atelia.VersionStore.Tests.ConditionalUpdateIntegration;

public sealed class ConditionalRevisionReadTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EstablishedCuReadsOnlyRootAndOriginalTimeAtOldExactPoint(bool readOnly) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        byte[] root = [0, 0xff, 0x80, 3];
        IReadOnlyDictionary<RefId, RefRevision> group;
        RefBytesSnapshot original;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, root);
            original = writer.ReadRef(fixture.Id(1)).Unwrap();
            writer.PublishRef(fixture.Id(1), [9], out _).Unwrap();
            writer.PublishRef(fixture.Id(2), [8], out _).Unwrap();
        }
        using var owner = fixture.Open(readOnly);
        var exact = owner.ReadRevision(group[fixture.Id(1)]).Unwrap();
        Assert.Equal(group[fixture.Id(1)], exact.Revision);
        Assert.Equal(original.RecordedAtUnixMs, exact.RecordedAtUnixMs);
        Assert.Equal(root, exact.Payload.ToArray());
        Assert.Equal(new byte[] { 9 }, owner.ReadRef(fixture.Id(1)).Unwrap().Payload.ToArray());
        owner.Dispose();
        Assert.Equal(root, exact.Payload.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IssuedCuWithControlledMissingPeerReturnsReadErrorAndNeverFallsBack(bool badTargetFence) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<RefId, RefRevision> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, HistoryFixture.Bytes());
            writer.PublishRef(fixture.Id(1), [3], out _).Unwrap();
            writer.PublishRef(fixture.Id(1), [4], out _).Unwrap();
        }
        RefRevision selected = group[fixture.Id(1)];
        // Controlled corruption removes a previously completed peer. The selected value was issued publicly.
        using (var stream = new FileStream(fixture.RefPath(2), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(group[fixture.Id(2)].Ticket.Offset);
        }
        if (badTargetFence) { fixture.FlipFollowingFence(selected); }
        using var owner = fixture.Open();
        Assert.Equal("VersionStore.InvalidRefRecord", owner.ReadRevision(selected).Error!.ErrorCode);
        Assert.Equal("VersionStore.InvalidForkOrigin", owner.ForkRef(3, selected, out var outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.RefPath(3)));
        Assert.Equal(new byte[] { 4 }, owner.ReadRef(fixture.Id(1)).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
        // Even a bad target Fence does not replace the already established nonpublication error.
    }

    [Fact]
    public void NecessaryCuPeerCrcErrorIsPreservedInsteadOfBecomingNonpublication() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<RefId, RefRevision> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, HistoryFixture.Bytes());
            writer.PublishRef(fixture.Id(1), [3], out _).Unwrap();
            writer.PublishRef(fixture.Id(2), [4], out _).Unwrap();
        }
        fixture.FlipPayload(group[fixture.Id(2)]);
        using var owner = fixture.Open();
        var read = owner.ReadRevision(group[fixture.Id(1)]);
        Assert.Equal("Rbf.CrcMismatch", read.Error!.ErrorCode);
        Assert.Equal(new byte[] { 3 }, owner.ReadRef(fixture.Id(1)).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
    }

    [Fact]
    public void HistoryAndNestedTagVisitorsCanReadExactCuWithoutChargingOuterBudget() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<RefId, RefRevision> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, [3]);
            writer.CreateTag("saved", [9], group[fixture.Id(1)], out _).Unwrap();
        }
        using var owner = fixture.Open();
        int deliveries = 0;
        int tagDeliveries = 0;
        var history = owner.ReadRefHistory(fixture.Id(1), new(1, 9), snapshot => {
            deliveries++;
            Assert.Equal(group[fixture.Id(1)], snapshot.Revision);
            owner.EnsureUsable();
            Assert.Equal(snapshot.Revision, owner.ReadRevision(snapshot.Revision).Unwrap().Revision);
            Assert.Equal(group[fixture.Id(2)], owner.ReadRevision(group[fixture.Id(2)]).Unwrap().Revision);
            Assert.Equal(TagScanEnd.Complete, owner.ScanTags(_ => {
                tagDeliveries++;
                owner.EnsureUsable();
                Assert.Equal(snapshot.Revision, owner.ReadRevision(snapshot.Revision).Unwrap().Revision);
                Assert.Equal(group[fixture.Id(2)], owner.ReadRevision(group[fixture.Id(2)]).Unwrap().Revision);
                return true;
            }).Unwrap());
            return false;
        });
        Assert.Equal(HistoryEnd.VisitorStopped, history.Unwrap());
        Assert.Equal(1, deliveries);
        Assert.Equal(1, tagDeliveries);
        int rejectedDeliveries = 0;
        var insufficient = owner.ReadRefHistory(fixture.Id(1), new(1, 8), _ => { rejectedDeliveries++; return false; });
        Assert.Equal("VersionStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
        Assert.Equal(0, rejectedDeliveries);
    }

    private static IReadOnlyDictionary<RefId, RefRevision> Publish(VersionStore owner, HistoryFixture fixture, byte[] root) =>
        owner.PublishRefs(new Dictionary<RefId, ReadOnlyMemory<byte>> {
            [fixture.Id(1)] = root,
            [fixture.Id(2)] = HistoryFixture.Bytes(value: 0x61)
        }, out _).Unwrap();
}
