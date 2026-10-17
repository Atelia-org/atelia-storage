using Atelia.Data;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.HeadStore.Internal.Runtime;
using Atelia.HeadStore.Internal.Storage;
using Atelia.HeadStore.Tests.History;
using Xunit;

namespace Atelia.HeadStore.Tests.ConditionalUpdateIntegration;

public sealed class ConditionalPeerFenceTests {
    [Theory]
    [InlineData(false, "current")]
    [InlineData(true, "current")]
    [InlineData(false, "exact")]
    [InlineData(true, "exact")]
    [InlineData(false, "history")]
    [InlineData(true, "history")]
    public void HistoricalPeerWithIntactBodyButBadFollowingFenceCannotEstablishCu(bool readOnly, string operation) {
        using var fixture = new HistoryFixture();
        var (group, peerCurrent) = PublishGroupWithPeerSuffix(fixture);
        byte[] peerRoot = HistoryFixture.Bytes(value: 0x62);
        HeadEntryId peerEntry = group[fixture.Id(2)];
        fixture.FlipFollowingFence(peerEntry);

        using (IRbfFile peer = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(2), RbfCacheMode.Off)) {
            using (RbfPooledFrame body = peer.ReadPooledFrame(peerEntry.Ticket).Unwrap()) {
                // The original frame bytes and CRC remain valid; only its closure is bad.
                Assert.Equal(peerEntry.Ticket, body.Ticket);
                Assert.True(body.PayloadAndMeta[..^body.TailMetaLength].EndsWith(peerRoot));
            }
            Assert.Equal("Rbf.FramingError", peer.GetScanBoundaryAfter(peerEntry.Ticket).Error!.ErrorCode);
        }

        using var owner = fixture.Open(readOnly);
        Assert.Equal(peerCurrent, owner.ReadHead(fixture.Id(2)).Unwrap().Id);
        Assert.Equal("Rbf.FramingError", owner.ReadEntry(peerEntry).Error!.ErrorCode);
        owner.EnsureUsable();

        switch (operation) {
            case "current":
                Assert.Equal("Rbf.FramingError", owner.ReadHead(fixture.Id(1)).Error?.ErrorCode);
                break;
            case "exact":
                Assert.Equal("Rbf.FramingError", owner.ReadEntry(group[fixture.Id(1)]).Error?.ErrorCode);
                break;
            case "history":
                int deliveries = 0;
                var history = owner.ReadHeadHistory(fixture.Id(1), HistoryFixture.Unlimited,
                    _ => { deliveries++; return true; });
                Assert.Equal("Rbf.FramingError", history.Error?.ErrorCode);
                Assert.Equal(0, deliveries);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
        owner.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeerFenceCompleteReadChargesItsWorkStepBeforeProbingTheFence(bool readOnly) {
        using var fixture = new HistoryFixture();
        var (group, _) = PublishGroupWithPeerSuffix(fixture);
        HeadEntryId peerEntry = group[fixture.Id(2)];
        fixture.FlipFollowingFence(peerEntry);
        var operations = new PeerFenceReadOperations(fixture.HeadPath(2), peerEntry.Ticket);
        using var owner = HeadStoreFactory.Open(fixture.Root, fixture.Data, readOnly, operations, new HeadStoreTestHooks());
        int deliveries = 0;

        // Local enter/header/record costs 5, peer enter/header/body costs 4.
        // The tenth step is the extra complete read and Fence check in GetScanBoundaryAfter.
        var insufficient = owner.ReadHeadHistory(fixture.Id(1), new(1, 9),
            _ => { deliveries++; return false; });
        Assert.Equal("HeadStore.HistoryBudgetExceeded", insufficient.Error!.ErrorCode);
        Assert.Equal(0, operations.FenceReads);
        Assert.Equal(0, deliveries);

        var sufficient = owner.ReadHeadHistory(fixture.Id(1), new(1, 10),
            _ => { deliveries++; return false; });
        Assert.Equal("Rbf.FramingError", sufficient.Error!.ErrorCode);
        Assert.Equal(1, operations.FenceReads);
        Assert.Equal(0, deliveries);
        owner.EnsureUsable();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SameTicketReplacementWithBadFenceCannotDisproveAnOldCu(bool readOnly, bool differentMembers) {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        fixture.SeedPublic(3, [3]);
        byte[] root = HistoryFixture.Bytes(64);
        var oldGroup = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = root, [2] = root }, 1);
        HeadEntryId replacement;
        using (var writer = fixture.Open()) {
            replacement = differentMembers
                ? writer.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                    [fixture.Id(2)] = root,
                    [fixture.Id(3)] = root
                }, out _).Unwrap()[fixture.Id(2)]
                : writer.PublishHead(fixture.Id(2), HistoryFixture.Bytes(28 + root.Length), out _).Unwrap();
            writer.PublishHead(fixture.Id(2), [4], out _).Unwrap();
            writer.PublishHead(fixture.Id(2), [5], out _).Unwrap();
        }
        Assert.Equal(oldGroup[2].Ticket, replacement.Ticket);
        using (var healthy = fixture.Open(readOnly: true)) {
            Assert.Equal(initial, healthy.ReadHead(fixture.Id(1)).Unwrap().Id);
        }
        fixture.FlipFollowingFence(replacement);

        using var owner = fixture.Open(readOnly);
        Assert.Equal("Rbf.FramingError", owner.ReadHead(fixture.Id(1)).Error?.ErrorCode);
        int deliveries = 0;
        var history = owner.ReadHeadHistory(fixture.Id(1), HistoryFixture.Unlimited,
            _ => { deliveries++; return true; });
        Assert.Equal("Rbf.FramingError", history.Error?.ErrorCode);
        Assert.Equal(0, deliveries);
        owner.EnsureUsable();
    }

    private static (IReadOnlyDictionary<HeadId, HeadEntryId> Group, HeadEntryId PeerCurrent)
        PublishGroupWithPeerSuffix(HistoryFixture fixture) {
        fixture.SeedPublic(1, [1]);
        fixture.SeedPublic(2, [2]);
        using var writer = fixture.Open();
        var group = writer.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
            [fixture.Id(1)] = HistoryFixture.Bytes(value: 0x61),
            [fixture.Id(2)] = HistoryFixture.Bytes(value: 0x62)
        }, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        // Two later records keep the damaged historical Fence outside the EOF
        // frame and its left Fence, so ordinary factory admission still succeeds.
        writer.PublishHead(fixture.Id(2), [3], out _).Unwrap();
        HeadEntryId current = writer.PublishHead(fixture.Id(2), [4], out _).Unwrap();
        return (group, current);
    }

    private sealed class PeerFenceReadOperations(string peerPath, SizedPtr wanted) : HeadStoreFactoryOperations {
        internal int FenceReads;

        internal override IRbfFile OpenHead(string path, bool readOnly, out RbfTailRecoveryReport recovery) {
            IRbfFile file = base.OpenHead(path, readOnly, out recovery);
            if (string.Equals(path, peerPath, StringComparison.Ordinal)) {
                ((RbfFileImpl)file).ReadObserver = (offset, length, raw) => {
                    if (!raw && offset == wanted.EndOffsetExclusive && length == sizeof(uint)) { FenceReads++; }
                };
            }
            return file;
        }
    }
}
