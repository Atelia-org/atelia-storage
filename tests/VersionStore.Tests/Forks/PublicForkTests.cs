using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Forks;

public sealed class PublicForkTests {
    [Fact]
    public void PublicOldPointForkOfForkKeepsDistinctNodesAndExactPrefixesAcrossBothOwnersColdReopen() {
        using var fixture = new ForkFixture();
        byte[][] aBytes = [[0xff], "{\"x\":1}"u8.ToArray(), "{ \"x\":2 }"u8.ToArray(), [0xfe]];
        RefRevision[] a = new RefRevision[4];
        RefRevision b0;
        RefRevision b1;
        RefRevision c0;
        RefRevision c1;
        RefBytesSnapshot savedSource;
        List<RefBytesSnapshot> saved;
        byte[] identity;
        using (var store = fixture.Create()) {
            identity = store.VersionStoreId.ToArray();
            a[0] = store.CreateRef(7, aBytes[0], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            for (int index = 1; index < a.Length; index++) {
                a[index] = store.PublishRef(a[0].RefId, aBytes[index], out outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
            }
            savedSource = store.ReadRef(a[0].RefId).Unwrap();
            RefRevision selected = default;
            Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(a[0].RefId, ForkFixture.Unlimited, snapshot => {
                if (snapshot.Revision != a[2]) { return true; }
                selected = snapshot.Revision;
                return false;
            }).Unwrap());
            Assert.Equal(a[2], selected);
            long beforeFork = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            b0 = store.ForkRef(19, selected, out outcome).Unwrap();
            long afterFork = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(19u, b0.RefId.LocalId);
            Assert.NotEqual(a[2], b0);
            var bInitial = store.ReadRef(b0.RefId).Unwrap();
            Assert.Equal(aBytes[2], bInitial.Payload.ToArray());
            Assert.InRange(bInitial.RecordedAtUnixMs, beforeFork, afterFork);
            Assert.Equal(savedSource.Revision, store.ReadRef(a[0].RefId).Unwrap().Revision);
            Assert.Equal(savedSource.RecordedAtUnixMs, store.ReadRef(a[0].RefId).Unwrap().RecordedAtUnixMs);
            Assert.Equal(savedSource.Payload.ToArray(), store.ReadRef(a[0].RefId).Unwrap().Payload.ToArray());

            b1 = store.PublishRef(b0.RefId, [0xb1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            c0 = store.ForkRef(b1, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(257u, c0.RefId.LocalId);
            Assert.Equal(b1, store.ReadRef(b0.RefId).Unwrap().Revision);
            c1 = store.PublishRef(c0.RefId, [0xc1], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishRef(a[0].RefId, [0xa4], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishRef(b0.RefId, [0xb2], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            saved = ForkFixture.Collect(store, c0.RefId);
            Assert.Equal(new[] { c1, c0, b1, b0, a[2], a[1], a[0] }, saved.Select(snapshot => snapshot.Revision));
            Assert.Equal(saved[1].Payload.ToArray(), saved[2].Payload.ToArray());
            Assert.NotEqual(saved[1].Revision, saved[2].Revision);
            Assert.Equal(saved[3].Payload.ToArray(), saved[4].Payload.ToArray());
            Assert.NotEqual(saved[3].Revision, saved[4].Revision);
        }
        fixture.Data.EnsureUsable();
        fixture.Data.Dispose();
        RefRevision coldFork;
        using (var data = DataStore.Open(fixture.DataRoot))
        using (var reopened = Store.Open(fixture.Root, data)) {
            Assert.Equal(identity, reopened.VersionStoreId.ToArray());
            Assert.Equal(saved.Select(snapshot => snapshot.Revision), ForkFixture.Collect(reopened, c0.RefId).Select(snapshot => snapshot.Revision));
            // An inherited revision selected from history can be used after the visitor returns.
            RefRevision inherited = default;
            Assert.Equal(HistoryEnd.VisitorStopped, reopened.ReadRefHistory(c0.RefId, ForkFixture.Unlimited, snapshot => {
                if (snapshot.Revision != a[1]) { return true; }
                inherited = snapshot.Revision;
                return false;
            }).Unwrap());
            Assert.Equal(a[1], inherited);
            // A revision issued by an older owner keeps its persistent context and exact point.
            coldFork = reopened.ForkRef(a[1], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(258u, coldFork.RefId.LocalId);
            Assert.Equal(aBytes[1], reopened.ReadRef(coldFork.RefId).Unwrap().Payload.ToArray());
            Assert.Equal(new[] { coldFork, a[1], a[0] }, ForkFixture.Collect(reopened, coldFork.RefId).Select(snapshot => snapshot.Revision));
            RefRevision inheritedFork = reopened.ForkRef(20, inherited, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new[] { inheritedFork, a[1], a[0] }, ForkFixture.Collect(reopened, inheritedFork.RefId).Select(snapshot => snapshot.Revision));
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            var cold = ForkFixture.Collect(readOnly, c0.RefId);
            Assert.Equal(saved.Select(snapshot => snapshot.Revision), cold.Select(snapshot => snapshot.Revision));
            Assert.Equal(saved.Select(snapshot => snapshot.RecordedAtUnixMs), cold.Select(snapshot => snapshot.RecordedAtUnixMs));
            Assert.Equal(saved.Select(snapshot => snapshot.Payload.ToArray()), cold.Select(snapshot => snapshot.Payload.ToArray()));
            Assert.Equal(new uint[] { 7, 19, 20, 257, 258 }, readOnly.ListRefs().Unwrap().Select(id => id.LocalId).Order());
            Assert.Equal(new[] { coldFork, a[1], a[0] }, ForkFixture.Collect(readOnly, coldFork.RefId).Select(snapshot => snapshot.Revision));
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
            "maximum" => new byte[Store.MaxRootPayloadLength],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (kind is "random" or "maximum") { new Random(117).NextBytes(payload); }
        byte[] expected = payload.ToArray();
        RefBytesSnapshot sourceSnapshot;
        RefBytesSnapshot cloneSnapshot;
        List<RefBytesSnapshot> history;
        using (var store = fixture.Create()) {
            RefRevision source = store.CreateRef(7, payload, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            payload.AsSpan().Fill(0x77);
            sourceSnapshot = store.ReadRef(source.RefId).Unwrap();
            RefRevision clone = store.ForkRef(source, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            cloneSnapshot = store.ReadRef(clone.RefId).Unwrap();
            Assert.Equal(expected, cloneSnapshot.Payload.ToArray());
            Assert.NotEqual(sourceSnapshot.Revision, cloneSnapshot.Revision);
            history = ForkFixture.Collect(store, clone.RefId);
            Assert.Equal(new[] { clone, source }, history.Select(snapshot => snapshot.Revision));
            store.PublishRef(source.RefId, [0x11], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            store.PublishRef(clone.RefId, [0x22], out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
        }
        fixture.Data.Dispose();
        Assert.Equal(expected, sourceSnapshot.Payload.ToArray());
        Assert.Equal(expected, cloneSnapshot.Payload.ToArray());
        Assert.All(history, snapshot => Assert.Equal(expected, snapshot.Payload.ToArray()));
    }

    [Fact]
    public void UnrelatedActiveDataBuilderDoesNotBlockForkOrBecomeCompleted() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create();
        RefRevision source = store.CreateRef(7, [0xff], out _).Unwrap();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        RefRevision clone = store.ForkRef(source, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 0xff }, store.ReadRef(clone.RefId).Unwrap().Payload.ToArray());
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
    public void HistoryRejectsBothForkMutationsBeforeCreatingAndSelectedRevisionCanForkAfterReturn() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, [1], [2]);
        using var store = fixture.Open();
        RefRevision selected = default;
        Assert.Equal(HistoryEnd.VisitorStopped, store.ReadRefHistory(source[0].RefId, ForkFixture.Unlimited, snapshot => {
            var automatic = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.HistoryActive", store.ForkRef(snapshot.Revision, out automatic).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, automatic);
            var specified = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.HistoryActive", store.ForkRef(19, snapshot.Revision, out specified).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, specified);
            selected = snapshot.Revision;
            return false;
        }).Unwrap());
        fixture.AssertNoChild(store, 19);
        fixture.AssertNoChild(store, 257);
        RefRevision clone = store.ForkRef(19, selected, out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new[] { clone, source[1], source[0] }, ForkFixture.Collect(store, clone.RefId).Select(snapshot => snapshot.Revision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultAndForeignRevisionRejectAndDoNotConsumeTargetOrAutomaticNumber(bool specified) {
        using var fixture = new ForkFixture();
        using var other = new ForkFixture();
        using var store = fixture.Create();
        using var foreignStore = other.Create();
        RefRevision source = store.CreateRef(7, [1], out _).Unwrap();
        RefRevision foreign = foreignStore.CreateRef(7, [2], out _).Unwrap();
        foreach (RefRevision invalid in new[] { default(RefRevision), foreign }) {
            var outcome = PublicationOutcome.Confirmed;
            var result = specified ? store.ForkRef(19, invalid, out outcome) : store.ForkRef(invalid, out outcome);
            Assert.Equal("VersionStore.ArgumentError", result.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertNoChild(store, 19);
        fixture.AssertNoChild(store, 257);
        var confirmed = PublicationOutcome.NotAttempted;
        RefRevision clone = specified ? store.ForkRef(19, source, out confirmed).Unwrap()
            : store.ForkRef(source, out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(specified ? 19u : 257u, clone.RefId.LocalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyForkRejectsWithInitializedOutcomeAndLeavesFilesUnchanged(bool specified) {
        using var fixture = new ForkFixture();
        RefRevision source = fixture.SeedClosed(7, new byte[] { 1 })[0];
        var before = fixture.SnapshotFiles();
        using (var store = fixture.Open(readOnly: true)) {
            var outcome = PublicationOutcome.Confirmed;
            var result = specified ? store.ForkRef(19, source, out outcome) : store.ForkRef(source, out outcome);
            Assert.Equal("VersionStore.ReadOnly", result.Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(source, store.ReadRef(source.RefId).Unwrap().Revision);
            Assert.Single(store.ListRefs().Unwrap());
        }
        fixture.AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedForkInitializesOutcomeBeforeLifecycleException(bool specified) {
        using var fixture = new ForkFixture();
        var store = fixture.Create();
        RefRevision source = store.CreateRef(7, [1], out _).Unwrap();
        store.Dispose();
        var outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => {
            if (specified) { store.ForkRef(19, source, out outcome); }
            else { store.ForkRef(source, out outcome); }
        });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void SpecifiedTargetsAreReservedCreateOnlyAndTheirRejectionsKeepAutomaticHighWatermark() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create();
        RefRevision source = store.CreateRef(7, [1], out _).Unwrap();
        foreach (uint invalid in new uint[] { 0, 257, uint.MaxValue }) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.Equal("VersionStore.ArgumentError", store.ForkRef(invalid, source, out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        var occupied = PublicationOutcome.Confirmed;
        Assert.True(store.ForkRef(7, source, out occupied).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, occupied);
        RefRevision child = store.ForkRef(19, source, out var confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        occupied = PublicationOutcome.Confirmed;
        Assert.True(store.ForkRef(19, source, out occupied).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, occupied);
        Assert.Equal(child, store.ReadRef(19u).Unwrap().Revision);
        Assert.Equal(source, store.ReadRef(7u).Unwrap().Revision);
        RefRevision automatic = store.ForkRef(source, out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(257u, automatic.RefId.LocalId);
        RefRevision next = store.CreateRef([3], out confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
        Assert.Equal(258u, next.RefId.LocalId);
    }

    [Fact]
    public void DefaultReservedDomainStartsAtOneAndAutomaticForkSharesTheCreateRefWatermark() {
        using var fixture = new ForkFixture();
        using var store = fixture.Create(reservedUpper: 0);
        RefRevision source = store.CreateRef([1], out _).Unwrap();
        Assert.Equal(1u, source.RefId.LocalId);
        var outcome = PublicationOutcome.Confirmed;
        Assert.True(store.ForkRef(1, source, out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        RefRevision fork = store.ForkRef(source, out outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(2u, fork.RefId.LocalId);
        Assert.Equal(3u, store.CreateRef([3], out outcome).Unwrap().RefId.LocalId);
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
    }

    [Fact]
    public void ForkDoesNotAppendChildRegistrationOrAlterPublicSourceFileBytes() {
        using var fixture = new ForkFixture();
        RefRevision[] source = fixture.SeedClosed(7, ForkFixture.Bytes(value: 0xa0), ForkFixture.Bytes(value: 0xa1));
        byte[] sourceFileBefore = File.ReadAllBytes(fixture.RefPath(7));
        using (var store = fixture.Open()) {
            RefRevision child = store.ForkRef(source[0], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(source[1], store.ReadRef(source[1].RefId).Unwrap().Revision);
            Assert.Equal(new[] { child, source[0] }, ForkFixture.Collect(store, child.RefId).Select(snapshot => snapshot.Revision));
        }
        Assert.Equal(sourceFileBefore, File.ReadAllBytes(fixture.RefPath(7)));
    }
}
