using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Forks;

public sealed class PublicForkTests {
    [Fact]
    public void PublicOldPointForkOfForkKeepsDistinctNodesAndExactPrefixesAcrossBothOwnersColdReopen() {
        using var fixture = new ForkFixture();
        byte[][] aBytes = [[0xff], "{\"x\":1}"u8.ToArray(), "{ \"x\":2 }"u8.ToArray(), [0xfe]];
        HeadEntryId[] a = new HeadEntryId[4];
        HeadEntryId b0;
        HeadEntryId b1;
        HeadEntryId c0;
        HeadEntryId c1;
        HeadEntry savedSource;
        List<HeadEntry> saved;
        byte[] identity;
        using (var store = fixture.Create()) {
            identity = store.HeadStoreId.ToArray();
            a[0] = store.CreateHead(7, aBytes[0], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            for (int index = 1; index < a.Length; index++) {
                a[index] = store.PublishHead(a[0].HeadId, aBytes[index], out outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
            }
            savedSource = store.ReadHead(a[0].HeadId).Unwrap();
            HeadEntryId selected = default;
            Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(a[0].HeadId, ForkFixture.Unlimited, entry => {
                if (entry.Id != a[2]) { return true; }
                selected = entry.Id;
                return false;
            }).Unwrap());
            Assert.Equal(a[2], selected);
            long beforeFork = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            b0 = store.ForkHead(19, selected, out outcome).Unwrap();
            long afterFork = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(19u, b0.HeadId.LocalId);
            Assert.NotEqual(a[2], b0);
            var bInitial = store.ReadHead(b0.HeadId).Unwrap();
            Assert.Equal(aBytes[2], bInitial.Payload.ToArray());
            Assert.InRange(bInitial.RecordedAtUnixMs, beforeFork, afterFork);
            Assert.Equal(savedSource.Id, store.ReadHead(a[0].HeadId).Unwrap().Id);
            Assert.Equal(savedSource.RecordedAtUnixMs, store.ReadHead(a[0].HeadId).Unwrap().RecordedAtUnixMs);
            Assert.Equal(savedSource.Payload.ToArray(), store.ReadHead(a[0].HeadId).Unwrap().Payload.ToArray());

            b1 = store.PublishHead(b0.HeadId, [0xb1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            c0 = store.ForkHead(b1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(257u, c0.HeadId.LocalId);
            Assert.Equal(b1, store.ReadHead(b0.HeadId).Unwrap().Id);
            c1 = store.PublishHead(c0.HeadId, [0xc1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishHead(a[0].HeadId, [0xa4], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishHead(b0.HeadId, [0xb2], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            saved = ForkFixture.Collect(store, c0.HeadId);
            Assert.Equal(new[] { c1, c0, b1, b0, a[2], a[1], a[0] }, saved.Select(entry => entry.Id));
            Assert.Equal(saved[1].Payload.ToArray(), saved[2].Payload.ToArray());
            Assert.NotEqual(saved[1].Id, saved[2].Id);
            Assert.Equal(saved[3].Payload.ToArray(), saved[4].Payload.ToArray());
            Assert.NotEqual(saved[3].Id, saved[4].Id);
        }
        fixture.Data.EnsureUsable();
        fixture.Data.Dispose();
        HeadEntryId coldFork;
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var reopened = Store.Open(fixture.Root, data)) {
            Assert.Equal(identity, reopened.HeadStoreId.ToArray());
            Assert.Equal(saved.Select(entry => entry.Id), ForkFixture.Collect(reopened, c0.HeadId).Select(entry => entry.Id));
            // An inherited entryId selected from history can be used after the visitor returns.
            HeadEntryId inherited = default;
            Assert.Equal(HeadHistoryEnd.VisitorStopped, reopened.ReadHeadHistory(c0.HeadId, ForkFixture.Unlimited, entry => {
                if (entry.Id != a[1]) { return true; }
                inherited = entry.Id;
                return false;
            }).Unwrap());
            Assert.Equal(a[1], inherited);
            // A entryId issued by an older owner keeps its persistent context and exact point.
            coldFork = reopened.ForkHead(a[1], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(258u, coldFork.HeadId.LocalId);
            Assert.Equal(aBytes[1], reopened.ReadHead(coldFork.HeadId).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { coldFork, a[1], a[0] }, ForkFixture.Collect(reopened, coldFork.HeadId).Select(entry => entry.Id));
            HeadEntryId inheritedFork = reopened.ForkHead(20, inherited, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new[] { inheritedFork, a[1], a[0] }, ForkFixture.Collect(reopened, inheritedFork.HeadId).Select(entry => entry.Id));
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            var cold = ForkFixture.Collect(readOnly, c0.HeadId);
            Assert.Equal(saved.Select(entry => entry.Id), cold.Select(entry => entry.Id));
            Assert.Equal(saved.Select(entry => entry.RecordedAtUnixMs), cold.Select(entry => entry.RecordedAtUnixMs));
            Assert.Equal(saved.Select(entry => entry.Payload.ToArray()), cold.Select(entry => entry.Payload.ToArray()));
            Assert.Equal(new uint[] { 7, 19, 20, 257, 258 }, readOnly.ListHeads().Unwrap().Select(id => id.LocalId).Order());
            Assert.Equal(new[] { coldFork, a[1], a[0] }, ForkFixture.Collect(readOnly, coldFork.HeadId).Select(entry => entry.Id));
        }
        Assert.Equal(aBytes[3], savedSource.Payload.ToArray());
        Assert.Equal(new byte[] { 0xc1 }, saved[0].Payload.ToArray());
        Assert.Equal(aBytes[2], saved[4].Payload.ToArray());
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("json")]
    [InlineData("random")]
    [InlineData("maximum")]
    public void OpaquePayloadIsClonedByteForByteAndOwnedResultsSurviveUpdatesAndDispose(string kind) {
        using var fixture = new ForkFixture();
        byte[] payload = kind switch {
            "minimum" => [0xff],
            "json" => "{ \"x\": [1, 2], \"label\": \"fork\" }\n"u8.ToArray(),
            "random" => new byte[8192],
            "maximum" => new byte[Store.MaxHeadEntryPayloadLength],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (kind is "random" or "maximum") { new Random(117).NextBytes(payload); }
        byte[] expected = payload.ToArray();
        HeadEntry sourceEntry;
        HeadEntry cloneEntry;
        List<HeadEntry> history;
        using (var store = fixture.Create()) {
            HeadEntryId source = store.CreateHead(7, payload, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            payload.AsSpan().Fill(0x77);
            sourceEntry = store.ReadHead(source.HeadId).Unwrap();
            HeadEntryId clone = store.ForkHead(source, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            cloneEntry = store.ReadHead(clone.HeadId).Unwrap();
            Assert.Equal(expected, cloneEntry.Payload.ToArray());
            Assert.NotEqual(sourceEntry.Id, cloneEntry.Id);
            history = ForkFixture.Collect(store, clone.HeadId);
            Assert.Equal(new[] { clone, source }, history.Select(entry => entry.Id));
            store.PublishHead(source.HeadId, [0x11], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishHead(clone.HeadId, [0x22], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        fixture.Data.Dispose();
        Assert.Equal(expected, sourceEntry.Payload.ToArray());
        Assert.Equal(expected, cloneEntry.Payload.ToArray());
        Assert.All(history, entry => Assert.Equal(expected, entry.Payload.ToArray()));
    }

    [Fact]
    public void UnrelatedActiveDataBuilderDoesNotBlockForkOrBecomeCompleted() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create();
        HeadEntryId source = store.CreateHead(7, [0xff], out _).Unwrap();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        HeadEntryId clone = store.ForkHead(source, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 0xff }, store.ReadHead(clone.HeadId).Unwrap().Payload.ToArray());
        Assert.Throws<InvalidOperationException>(() => { fixture.Data.ReadFrame(early); });
        store.Dispose();
        var writer = builder.PayloadAndMeta;
        writer.GetSpan(1)[0] = 0x57;
        writer.Advance(1);
        Assert.Equal(early, builder.EndAppend(3).Unwrap());
        fixture.Data.ConfirmDurable();
        using var frame = fixture.Data.ReadFrame(early).Unwrap();
        Assert.Equal(new byte[] { 0x57 }, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void HistoryRejectsBothForkMutationsBeforeCreatingAndSelectedEntryIdCanForkAfterReturn() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, [1], [2]);
        using var store = fixture.Open();
        HeadEntryId selected = default;
        Assert.Equal(HeadHistoryEnd.VisitorStopped, store.ReadHeadHistory(source[0].HeadId, ForkFixture.Unlimited, entry => {
            var automatic = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.HistoryActive", store.ForkHead(entry.Id, out automatic).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, automatic);
            var specified = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.HistoryActive", store.ForkHead(19, entry.Id, out specified).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, specified);
            selected = entry.Id;
            return false;
        }).Unwrap());
        fixture.AssertNoChild(store, 19);
        fixture.AssertNoChild(store, 257);
        HeadEntryId clone = store.ForkHead(19, selected, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new[] { clone, source[1], source[0] }, ForkFixture.Collect(store, clone.HeadId).Select(entry => entry.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultAndForeignEntryIdRejectAndDoNotConsumeTargetOrAutomaticNumber(bool specified) {
        using var fixture = new ForkFixture();
        using var other = new ForkFixture();
        using var store = fixture.Create();
        using var foreignStore = other.Create();
        HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId foreign = foreignStore.CreateHead(7, [2], out _).Unwrap();
        foreach (HeadEntryId invalid in new[] { default(HeadEntryId), foreign }) {
            var outcome = PublicationOutcome.Confirmed;
            var result = specified ? store.ForkHead(19, invalid, out outcome) : store.ForkHead(invalid, out outcome);
            Assert.Equal("HeadStore.ArgumentError", result.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertNoChild(store, 19);
        fixture.AssertNoChild(store, 257);
        var confirmed = PublicationOutcome.NotAttempted;
        HeadEntryId clone = specified ? store.ForkHead(19, source, out confirmed).Unwrap()
            : store.ForkHead(source, out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(specified ? 19u : 257u, clone.HeadId.LocalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyForkRejectsWithInitializedOutcomeAndLeavesFilesUnchanged(bool specified) {
        using var fixture = new ForkFixture();
        HeadEntryId source = fixture.SeedClosed(7, new byte[] { 1 })[0];
        var before = fixture.SnapshotFiles();
        using (var store = fixture.Open(readOnly: true)) {
            var outcome = PublicationOutcome.Confirmed;
            var result = specified ? store.ForkHead(19, source, out outcome) : store.ForkHead(source, out outcome);
            Assert.Equal("HeadStore.ReadOnly", result.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(source, store.ReadHead(source.HeadId).Unwrap().Id);
            Assert.Single(store.ListHeads().Unwrap());
        }
        fixture.AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedForkInitializesOutcomeBeforeLifecycleException(bool specified) {
        using var fixture = new ForkFixture();
        var store = fixture.Create();
        HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
        store.Dispose();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => {
            if (specified) { store.ForkHead(19, source, out outcome); }
            else { store.ForkHead(source, out outcome); }
        });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void SpecifiedTargetsAreReservedCreateOnlyAndTheirRejectionsKeepAutomaticHighWatermark() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create();
        HeadEntryId source = store.CreateHead(7, [1], out _).Unwrap();
        foreach (uint invalid in new uint[] { 0, 257, uint.MaxValue }) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("HeadStore.ArgumentError", store.ForkHead(invalid, source, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        var occupied = PublicationOutcome.Confirmed;
        Assert.True(store.ForkHead(7, source, out occupied).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, occupied);
        HeadEntryId child = store.ForkHead(19, source, out var confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        occupied = PublicationOutcome.Confirmed;
        Assert.True(store.ForkHead(19, source, out occupied).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, occupied);
        Assert.Equal(child, store.ReadHead(19u).Unwrap().Id);
        Assert.Equal(source, store.ReadHead(7u).Unwrap().Id);
        HeadEntryId automatic = store.ForkHead(source, out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(257u, automatic.HeadId.LocalId);
        HeadEntryId next = store.CreateHead([3], out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(258u, next.HeadId.LocalId);
    }

    [Fact]
    public void DefaultReservedDomainStartsAtOneAndAutomaticForkSharesTheCreateHeadWatermark() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create(reservedUpper: 0);
        HeadEntryId source = store.CreateHead([1], out _).Unwrap();
        Assert.Equal(1u, source.HeadId.LocalId);
        var outcome = PublicationOutcome.Confirmed;
        Assert.True(store.ForkHead(1, source, out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        HeadEntryId fork = store.ForkHead(source, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2u, fork.HeadId.LocalId);
        Assert.Equal(3u, store.CreateHead([3], out outcome).Unwrap().HeadId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void ForkDoesNotAppendChildRegistrationOrAlterPublicSourceFileBytes() {
        using var fixture = new ForkFixture();
        HeadEntryId[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1));
        byte[] sourceFileBefore = File.ReadAllBytes(fixture.HeadPath(7));
        using (var store = fixture.Open()) {
            HeadEntryId child = store.ForkHead(source[0], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(source[1], store.ReadHead(source[1].HeadId).Unwrap().Id);
            Assert.Equal(new[] { child, source[0] }, ForkFixture.Collect(store, child.HeadId).Select(entry => entry.Id));
        }
        Assert.Equal(sourceFileBefore, File.ReadAllBytes(fixture.HeadPath(7)));
    }
}
