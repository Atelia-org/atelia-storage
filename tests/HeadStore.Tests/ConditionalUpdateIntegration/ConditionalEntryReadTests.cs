using Atelia.Rbf;
using Atelia.HeadStore.Tests.History;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateIntegration;

public sealed class ConditionalEntryReadTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EstablishedCuReadsOnlyRootAndOriginalTimeAtOldExactPoint(bool readOnly) {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        byte[] root = [0, 0xff, 0x80, 3];
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        HeadEntry original;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, root);
            original = writer.ReadHead(fixture.Id(1)).Unwrap();
            writer.PublishHead(fixture.Id(1), [9], out _).Unwrap();
            writer.PublishHead(fixture.Id(2), [8], out _).Unwrap();
        }
        using var owner = fixture.Open(readOnly);
        var exact = owner.ReadEntry(group[fixture.Id(1)]).Unwrap();
        Assert.Equal(group[fixture.Id(1)], exact.Id);
        Assert.Equal(original.RecordedAtUnixMs, exact.RecordedAtUnixMs);
        Assert.Equal(root, exact.Payload.ToArray());
        Assert.Equal(new byte[] { 9 }, owner.ReadHead(fixture.Id(1)).Unwrap().Payload.ToArray());
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
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, HistoryFixture.Bytes());
            writer.PublishHead(fixture.Id(1), [3], out _).Unwrap();
            writer.PublishHead(fixture.Id(1), [4], out _).Unwrap();
        }
        HeadEntryId selected = group[fixture.Id(1)];
        // Controlled corruption removes a previously completed peer. The selected value was issued publicly.
        using (var stream = new FileStream(fixture.HeadPath(2), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(group[fixture.Id(2)].Ticket.Offset);
        }
        if (badTargetFence) { fixture.FlipFollowingFence(selected); }
        using var owner = fixture.Open();
        Assert.Equal("HeadStore.InvalidHeadRecord", owner.ReadEntry(selected).Error!.ErrorCode);
        Assert.Equal("HeadStore.InvalidForkOrigin", owner.ForkHead(3, selected, out var outcome).Error!.ErrorCode);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.HeadPath(3)));
        Assert.Equal(new byte[] { 4 }, owner.ReadHead(fixture.Id(1)).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
        // Even a bad target Fence does not replace the already established nonpublication error.
    }

    [Fact]
    public void NecessaryCuPeerCrcErrorIsPreservedInsteadOfBecomingNonpublication() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, HistoryFixture.Bytes());
            writer.PublishHead(fixture.Id(1), [3], out _).Unwrap();
            writer.PublishHead(fixture.Id(2), [4], out _).Unwrap();
        }
        fixture.FlipPayload(group[fixture.Id(2)]);
        using var owner = fixture.Open();
        var read = owner.ReadEntry(group[fixture.Id(1)]);
        Assert.Equal("Rbf.CrcMismatch", read.Error!.ErrorCode);
        Assert.Equal(new byte[] { 3 }, owner.ReadHead(fixture.Id(1)).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
    }

    [Fact]
    public void HistoryAndNestedTagVisitorsCanReadExactCuWithoutChargingOuterBudget() {
        using var fixture = new HistoryFixture();
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        IReadOnlyDictionary<HeadId, HeadEntryId> group;
        using (var writer = fixture.Open()) {
            group = Publish(writer, fixture, [3]);
            writer.CreateTag("saved", [9], group[fixture.Id(1)], out _).Unwrap();
        }
        using var owner = fixture.Open();
        int deliveries = 0;
        int tagDeliveries = 0;
        var history = owner.ReadHeadHistory(fixture.Id(1), new(1, 9), entry => {
            deliveries++;
            Assert.Equal(group[fixture.Id(1)], entry.Id);
            owner.EnsureUsable();
            Assert.Equal(entry.Id, owner.ReadEntry(entry.Id).Unwrap().Id);
            Assert.Equal(group[fixture.Id(2)], owner.ReadEntry(group[fixture.Id(2)]).Unwrap().Id);
            Assert.Equal(TagScanEnd.Complete, owner.ScanTags(_ => {
                tagDeliveries++;
                owner.EnsureUsable();
                Assert.Equal(entry.Id, owner.ReadEntry(entry.Id).Unwrap().Id);
                Assert.Equal(group[fixture.Id(2)], owner.ReadEntry(group[fixture.Id(2)]).Unwrap().Id);
                return true;
            }).Unwrap());
            return false;
        });
        Assert.Equal(HeadHistoryEnd.VisitorStopped, history.Unwrap());
        Assert.Equal(1, deliveries);
        Assert.Equal(1, tagDeliveries);
        int rejectedDeliveries = 0;
        var insufficient = owner.ReadHeadHistory(fixture.Id(1), new(1, 8), _ => { rejectedDeliveries++; return false; });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
        Assert.Equal(0, rejectedDeliveries);
    }

    private static IReadOnlyDictionary<HeadId, HeadEntryId> Publish(HeadStore owner, HistoryFixture fixture, byte[] root) =>
        owner.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [fixture.Id(1)] = root,
            [fixture.Id(2)] = HistoryFixture.Bytes(value: 0x61)
        }, out _).Unwrap();
}
