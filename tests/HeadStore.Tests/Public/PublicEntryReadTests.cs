using Atelia.Rbf;
using Atelia.HeadStore.Tests.Forks;
using Atelia.HeadStore.Tests.History;
using Atelia.HeadStore.Tests.Runtime;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

public sealed class PublicEntryReadTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedOldEntryIdReadsOriginalBytesAfterBothOwnersColdReopen(bool readOnly) {
        using var fixture = new ForkFixture();
        HeadEntryId[] entryIds = fixture.SeedClosed(7, [0, 0xff, 0x80], [3, 4]);
        long recordedAt;
        using (var owner = fixture.Open()) {
            var entry = owner.ReadEntry(entryIds[0]).Unwrap();
            recordedAt = entry.RecordedAtUnixMs;
            Assert.Equal(entryIds[0], entry.Id);
        }
        fixture.Data.Dispose();
        using var data = readOnly ? DataStore.OpenReadOnly(fixture.DataRoot) : DataStore.Open(fixture.DataRoot);
        using var cold = readOnly ? Store.OpenReadOnly(fixture.Root, data) : Store.Open(fixture.Root, data);
        var old = cold.ReadEntry(entryIds[0]).Unwrap();
        Assert.Equal(entryIds[0], old.Id);
        Assert.Equal(recordedAt, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 0, 0xff, 0x80 }, old.Payload.ToArray());
        Assert.Equal(entryIds[1], cold.ReadHead(7u).Unwrap().Id);
    }

    [Fact]
    public void InheritedHistoryEntryIdCanBeReadExactlyInsideAndAfterVisitor() {
        using var fixture = new HistoryFixture();
        HeadEntryId[] source = fixture.SeedPublic(1, [1], [2], [3]);
        HeadId child;
        using (var writer = fixture.Open()) { child = writer.ForkHead(2, source[1], out _).Unwrap(); }
        using var owner = fixture.Open();
        var inherited = new List<HeadEntry>();
        var history = owner.ReadHeadHistory(child, HistoryFixture.Unlimited, entry => {
            owner.EnsureUsable();
            var exact = owner.ReadEntry(entry.Id).Unwrap();
            Assert.Equal(entry.Id, exact.Id);
            Assert.Equal(entry.RecordedAtUnixMs, exact.RecordedAtUnixMs);
            Assert.Equal(entry.Payload.ToArray(), exact.Payload.ToArray());
            inherited.Add(exact);
            return true;
        }).Unwrap();
        Assert.Equal(HeadHistoryEnd.Complete, history);
        Assert.Equal(new[] { source[1], source[0] }, inherited.Select(value => value.Id));
        Assert.Equal(new byte[] { 2 }, owner.ReadEntry(inherited[0].Id).Unwrap().Payload.ToArray());
        Assert.Equal(source[2], owner.ReadHead(source[0].HeadId).Unwrap().Id);
    }

    [Fact]
    public void ExactReadDoesNotLocateOldPointByScanningLocalSuffixOrConfirmAnyOutput() {
        using var fixture = new RuntimeFixture();
        HeadId id = fixture.Seed(payload: [1, 2], recordedAt: long.MinValue);
        HeadEntry issued = fixture.Store.ReadHead(id).Unwrap();
        fixture.Store.PublishHead(id, [3], out _).Unwrap();
        fixture.Store.PublishHead(id, [4], out _).Unwrap();
        var file = fixture.Files.Opened[id.GetLocalId(fixture.Identity)];
        int scans = file.ReverseScanCalls;
        int flushes = file.FlushCalls;
        file.PooledReads.Clear();
        fixture.Operations.Clear();
        var old = fixture.Store.ReadEntry(issued.Id).Unwrap();
        Assert.Equal(issued.Id, old.Id);
        Assert.Equal(long.MinValue, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
        Assert.Equal(scans, file.ReverseScanCalls);
        Assert.Equal(flushes, file.FlushCalls);
        Assert.DoesNotContain("data:confirm", fixture.Operations);
        Assert.Contains(issued.Id.Ticket, file.PooledReads);
        Assert.All(file.PooledReads, ticket => Assert.True(ticket.Offset <= issued.Id.Ticket.Offset));

        fixture.Store.ReadHead(id).Unwrap();
        fixture.Hooks.ConfirmDataDurable = () => throw new IOException("publication barrier");
        Assert.Throws<IOException>(() => fixture.Store.PublishHead(id, [5], out _));
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
        fixture.Store.Dispose();
        Assert.Equal(issued.Id, old.Id);
        Assert.Equal(long.MinValue, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
    }

    [Fact]
    public void DefaultAndForeignPublicEntryIdAreRejectedBeforeDiscoveryIo() {
        using var fixture = new RuntimeFixture();
        using var foreign = new PublicHeadStoreFixture();
        HeadEntryId other = HeadEntryId.Create(HeadIdTestExtensions.ForeignHeadId(fixture.Identity, 7), foreign.SeedClosed().Ticket);
        Assert.Equal("HeadStore.HeadNotFound", fixture.Store.ReadEntry(default).Error!.ErrorCode);
        Assert.Equal("HeadStore.HeadNotFound", fixture.Store.ReadEntry(other).Error!.ErrorCode);
        Assert.Empty(fixture.Operations);
        Assert.Equal(0, fixture.Files.ResolveCalls);
        Assert.Equal(0, fixture.Files.OpenCalls);
        fixture.Store.EnsureUsable();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HealthyOldPointIgnoresUnrelatedInitialAndSuffixPayloadCorruption(bool initial, bool suffix) {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1),
            ForkFixture.Bytes(value: 0xa2), ForkFixture.Bytes(value: 0xa3));
        if (initial) { fixture.FlipPayload(source[0]); }
        if (suffix) { fixture.FlipPayload(source[3]); }
        using var owner = fixture.Open();
        Assert.Equal(ForkFixture.Bytes(value: 0xa1), owner.ReadEntry(source[1]).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryTargetCrcOrFollowingFenceFailureIsReturnedWithoutFault(bool fence) {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0),
            ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        if (fence) { fixture.FlipFollowingFence(source[0]); }
        else { fixture.FlipPayload(source[0]); }
        using var owner = fixture.Open();
        var rejected = owner.ReadEntry(source[0]);
        Assert.True(rejected.IsFailure);
        Assert.StartsWith("Rbf.", rejected.Error!.ErrorCode);
        Assert.Equal(source[2], owner.ReadEntry(source[2]).Unwrap().Id);
        owner.EnsureUsable();
    }

    [Fact]
    public void SavedEntryIdOutsideControlledTruncatedCompletedRangeDoesNotFallBack() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, [1], [2]);
        long completedEnd;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(7), RbfCacheMode.Off)) {
            completedEnd = file.GetPhysicalOffsetImmediatelyAfter(source[0].Ticket);
        }
        // Controlled corruption only: the production protocol forbids deleting completed positions.
        using (var stream = new FileStream(fixture.HeadPath(7), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(completedEnd);
        }
        using var owner = fixture.Open();
        Assert.Equal("HeadStore.InvalidHeadRecord", owner.ReadEntry(source[1]).Error!.ErrorCode);
        Assert.Equal(source[0], owner.ReadEntry(source[0]).Unwrap().Id);
    }
}
