using Atelia.Rbf;
using Atelia.VersionStore.Tests.Forks;
using Atelia.VersionStore.Tests.History;
using Atelia.VersionStore.Tests.Runtime;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

public sealed class PublicRevisionReadTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedOldRevisionReadsOriginalBytesAfterBothOwnersColdReopen(bool readOnly) {
        using var fixture = new ForkFixture();
        RefRevision[] revisions = fixture.SeedClosed(7, [0, 0xff, 0x80], [3, 4]);
        long recordedAt;
        using (var owner = fixture.Open()) {
            var snapshot = owner.ReadRevision(revisions[0]).Unwrap();
            recordedAt = snapshot.RecordedAtUnixMs;
            Assert.Equal(revisions[0], snapshot.Revision);
        }
        fixture.Data.Dispose();
        using var data = readOnly ? DataStore.OpenReadOnly(fixture.DataRoot) : DataStore.Open(fixture.DataRoot);
        using var cold = readOnly ? Store.OpenReadOnly(fixture.Root, data) : Store.Open(fixture.Root, data);
        var old = cold.ReadRevision(revisions[0]).Unwrap();
        Assert.Equal(revisions[0], old.Revision);
        Assert.Equal(recordedAt, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 0, 0xff, 0x80 }, old.Payload.ToArray());
        Assert.Equal(revisions[1], cold.ReadRef(7u).Unwrap().Revision);
    }

    [Fact]
    public void InheritedHistoryRevisionCanBeReadExactlyInsideAndAfterVisitor() {
        using var fixture = new HistoryFixture();
        RefRevision[] source = fixture.SeedPublic(1, [1], [2], [3]);
        RefRevision child;
        using (var writer = fixture.Open()) { child = writer.ForkRef(2, source[1], out _).Unwrap(); }
        using var owner = fixture.Open();
        var inherited = new List<RefBytesSnapshot>();
        var history = owner.ReadRefHistory(child.RefId, HistoryFixture.Unlimited, snapshot => {
            owner.EnsureUsable();
            var exact = owner.ReadRevision(snapshot.Revision).Unwrap();
            Assert.Equal(snapshot.Revision, exact.Revision);
            Assert.Equal(snapshot.RecordedAtUnixMs, exact.RecordedAtUnixMs);
            Assert.Equal(snapshot.Payload.ToArray(), exact.Payload.ToArray());
            inherited.Add(exact);
            return true;
        }).Unwrap();
        Assert.Equal(HistoryEnd.Complete, history);
        Assert.Equal(new[] { child, source[1], source[0] }, inherited.Select(value => value.Revision));
        Assert.Equal(new byte[] { 2 }, owner.ReadRevision(inherited[1].Revision).Unwrap().Payload.ToArray());
        Assert.Equal(source[2], owner.ReadRef(source[0].RefId).Unwrap().Revision);
    }

    [Fact]
    public void ExactReadDoesNotLocateOldPointByScanningLocalSuffixOrConfirmAnyOutput() {
        using var fixture = new RuntimeFixture();
        RefId id = fixture.Seed(payload: [1, 2], recordedAt: long.MinValue);
        RefBytesSnapshot issued = fixture.Store.ReadRef(id).Unwrap();
        fixture.Store.PublishRef(id, [3], out _).Unwrap();
        fixture.Store.PublishRef(id, [4], out _).Unwrap();
        var file = fixture.Files.Opened[id.LocalId];
        int scans = file.ReverseScanCalls;
        int flushes = file.FlushCalls;
        file.PooledReads.Clear();
        fixture.Operations.Clear();
        var old = fixture.Store.ReadRevision(issued.Revision).Unwrap();
        Assert.Equal(issued.Revision, old.Revision);
        Assert.Equal(long.MinValue, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
        Assert.Equal(scans, file.ReverseScanCalls);
        Assert.Equal(flushes, file.FlushCalls);
        Assert.DoesNotContain("data:confirm", fixture.Operations);
        Assert.Contains(issued.Revision.Ticket, file.PooledReads);
        Assert.All(file.PooledReads, ticket => Assert.True(ticket.Offset <= issued.Revision.Ticket.Offset));

        fixture.Store.ReadRef(id).Unwrap();
        fixture.Hooks.ConfirmDataDurable = () => throw new IOException("publication barrier");
        Assert.Throws<IOException>(() => fixture.Store.PublishRef(id, [5], out _));
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
        fixture.Store.Dispose();
        Assert.Equal(issued.Revision, old.Revision);
        Assert.Equal(long.MinValue, old.RecordedAtUnixMs);
        Assert.Equal(new byte[] { 1, 2 }, old.Payload.ToArray());
    }

    [Fact]
    public void DefaultAndForeignPublicRevisionAreRejectedBeforeDiscoveryIo() {
        using var fixture = new RuntimeFixture();
        using var foreign = new PublicVersionStoreFixture();
        RefRevision other = foreign.SeedClosed();
        Assert.Equal("VersionStore.ArgumentError", fixture.Store.ReadRevision(default).Error!.ErrorCode);
        Assert.Equal("VersionStore.ArgumentError", fixture.Store.ReadRevision(other).Error!.ErrorCode);
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
        RefRevision[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1),
            ForkFixture.Bytes(value: 0xa2), ForkFixture.Bytes(value: 0xa3));
        if (initial) { fixture.FlipPayload(source[0]); }
        if (suffix) { fixture.FlipPayload(source[3]); }
        using var owner = fixture.Open();
        Assert.Equal(ForkFixture.Bytes(value: 0xa1), owner.ReadRevision(source[1]).Unwrap().Payload.ToArray());
        owner.EnsureUsable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NecessaryTargetCrcOrFollowingFenceFailureIsReturnedWithoutFault(bool fence) {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0),
            ForkFixture.Bytes(value: 0xa1), ForkFixture.Bytes(value: 0xa2));
        if (fence) { fixture.FlipFollowingFence(source[0]); }
        else { fixture.FlipPayload(source[0]); }
        using var owner = fixture.Open();
        var rejected = owner.ReadRevision(source[0]);
        Assert.True(rejected.IsFailure);
        Assert.StartsWith("Rbf.", rejected.Error!.ErrorCode);
        Assert.Equal(source[2], owner.ReadRevision(source[2]).Unwrap().Revision);
        owner.EnsureUsable();
    }

    [Fact]
    public void SavedRevisionOutsideControlledTruncatedCompletedRangeDoesNotFallBack() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, [1], [2]);
        long completedEnd;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.RefPath(7), RbfCacheMode.Off)) {
            completedEnd = file.GetPhysicalOffsetImmediatelyAfter(source[0].Ticket);
        }
        // Controlled corruption only: the production protocol forbids deleting completed positions.
        using (var stream = new FileStream(fixture.RefPath(7), FileMode.Open, FileAccess.Write, FileShare.None)) {
            stream.SetLength(completedEnd);
        }
        using var owner = fixture.Open();
        Assert.Equal("VersionStore.InvalidRefRecord", owner.ReadRevision(source[1]).Error!.ErrorCode);
        Assert.Equal(source[0], owner.ReadRevision(source[0]).Unwrap().Revision);
    }
}
