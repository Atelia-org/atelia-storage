using Xunit;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

public sealed class PublicPayloadAndRecoveryTests {
    [Theory]
    [InlineData(1)]
    [InlineData(Store.MaxRootPayloadLength)]
    public void BothPayloadLengthEndpointsRemainOpaqueAcrossCreatePublishAndColdRead(int length) {
        using var fixture = new PublicVersionStoreFixture();
        byte[] bytes = new byte[length];
        new Random(37).NextBytes(bytes);
        bytes[0] = 0xff; // Not a valid RootMap, but valid VersionStore content.
        Assert.True(RootMapCodec.Decode(bytes).IsFailure);
        RefRevision revision;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var initial = store.CreateRef(bytes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(bytes, store.ReadRef(initial.RefId).Unwrap().Payload.ToArray());
            revision = store.PublishRef(initial.RefId, bytes, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(initial, revision);
        }
        using var cold = Store.OpenReadOnly(fixture.Root, fixture.Data);
        var snapshot = cold.ReadRef(revision.RefId).Unwrap();
        Assert.Equal(revision, snapshot.Revision);
        Assert.Equal(bytes, snapshot.Payload.ToArray());
        Assert.True(RootMapCodec.Decode(snapshot.Payload).IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Store.MaxRootPayloadLength + 1)]
    public void InvalidPayloadLengthsRejectAllWriteEntrypointsWithoutChangingFilesOrFaultingOwner(int length) {
        using var fixture = new PublicVersionStoreFixture();
        RefId id = fixture.SeedClosed().RefId;
        byte[] bytes = new byte[length];
        var before = fixture.Snapshot();
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.CreateRef(bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.CreateRef(8, bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.PublishRef(id, bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(new[] { id }, store.ListRefs().Unwrap());
            fixture.Data.EnsureUsable();
        }
        fixture.AssertUnchanged(before);
        using var healthy = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(257u, healthy.CreateRef([0xff], out _).Unwrap().RefId.LocalId);
    }

    [Fact]
    public void InvalidOrOccupiedSpecifiedTargetsRejectWithoutOverwriteOrBurningAutomaticIds() {
        using var fixture = new PublicVersionStoreFixture();
        RefId id = fixture.SeedClosed().RefId;
        // A canonical, physically invalid file is occupied too; creation must not read or repair it.
        File.WriteAllBytes(fixture.RefPath(8), [0x98]);
        var before = fixture.Snapshot();
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            foreach (uint target in new uint[] { 0, 257, uint.MaxValue, 7, 8 }) {
                Assert.True(store.CreateRef(target, [1], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            Assert.True(store.PublishRef(default, [1], out var rejectedOutcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, rejectedOutcome);
            Assert.Equal(new uint[] { 7, 8 }, store.ListRefs().Unwrap().Select(value => value.LocalId).Order());
        }
        fixture.AssertUnchanged(before);
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(id, cold.ReadRef(7u).Unwrap().Revision.RefId);
        Assert.Equal(257u, cold.CreateRef([2], out _).Unwrap().RefId.LocalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadInitialPayloadDoesNotBlockNecessaryReadOfHealthyHead(bool readOnly) {
        using var fixture = new PublicVersionStoreFixture();
        byte[] initialBytes = Enumerable.Repeat((byte)0xff, 128).ToArray();
        byte[] latestBytes = Enumerable.Repeat((byte)0xfe, 128).ToArray();
        RefRevision initial;
        RefRevision latest;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            initial = store.CreateRef(initialBytes, out _).Unwrap();
            latest = store.PublishRef(initial.RefId, latestBytes, out _).Unwrap();
        }
        PublicVersionStoreFixture.FlipPayloadByte(fixture.RefPath(initial.RefId.LocalId), initial.Ticket);
        var before = fixture.Snapshot();
        using (var cold = readOnly ? Store.OpenReadOnly(fixture.Root, fixture.Data) : Store.Open(fixture.Root, fixture.Data)) {
            var current = cold.ReadRef(initial.RefId).Unwrap();
            Assert.Equal(latest, current.Revision);
            Assert.Equal(latestBytes, current.Payload.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadCurrentPayloadDoesNotFallBackToHealthyInitialSnapshot(bool readOnly) {
        using var fixture = new PublicVersionStoreFixture();
        RefRevision latest;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var initial = store.CreateRef(Enumerable.Repeat((byte)0xff, 128).ToArray(), out _).Unwrap();
            latest = store.PublishRef(initial.RefId, Enumerable.Repeat((byte)0xfe, 128).ToArray(), out _).Unwrap();
        }
        PublicVersionStoreFixture.FlipPayloadByte(fixture.RefPath(latest.RefId.LocalId), latest.Ticket);
        var before = fixture.Snapshot();
        using (var cold = readOnly ? Store.OpenReadOnly(fixture.Root, fixture.Data) : Store.Open(fixture.Root, fixture.Data)) {
            Assert.True(cold.ReadRef(latest.RefId).IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritableTargetAccessCompletesRecoverableTailAndReadOnlyKeepsOriginalBytes(bool initialIsHead) {
        using var fixture = new PublicVersionStoreFixture();
        RefRevision current;
        byte[] expected = Enumerable.Repeat((byte)0xff, 128).ToArray();
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            current = store.CreateRef(expected, out _).Unwrap();
            if (!initialIsHead) {
                expected[0] = 0xfe;
                current = store.PublishRef(current.RefId, expected, out _).Unwrap();
            }
        }
        string path = fixture.RefPath(current.RefId.LocalId);
        byte[] complete = File.ReadAllBytes(path);
        // Missing Key and Fence, with a complete body: public RBF recovery may append those bytes.
        File.WriteAllBytes(path, complete[..^8]);
        var before = fixture.Snapshot();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(current.RefId); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(complete[..^8], File.ReadAllBytes(path)); // Factory admission does not open or recover refs.
            var read = writer.ReadRef(current.RefId).Unwrap();
            Assert.Equal(current, read.Revision);
            Assert.Equal(expected, read.Payload.ToArray());
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(current, cold.ReadRef(current.RefId).Unwrap().Revision);
    }

    [Fact]
    public void PartialNextFrameIsRecoveredOnlyOnWritableTargetAccess() {
        using var fixture = new PublicVersionStoreFixture();
        RefRevision current = fixture.SeedClosed();
        string path = fixture.RefPath(current.RefId.LocalId);
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. complete, 1]);
        var before = fixture.Snapshot();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadRef(current.RefId); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(complete.Length + 1, new FileInfo(path).Length);
            Assert.Equal(current, writer.ReadRef(current.RefId).Unwrap().Revision);
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
    }

    [Fact]
    public void HeaderOnlyFormalRefOccupiesPathButCannotSupplyCurrentSnapshot() {
        using var fixture = new PublicVersionStoreFixture();
        RefRevision initial = fixture.SeedClosed();
        string path = fixture.RefPath(initial.RefId.LocalId);
        byte[] image = File.ReadAllBytes(path);
        File.WriteAllBytes(path, image[..checked((int)initial.Ticket.Offset)]);
        var before = fixture.Snapshot();
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(new[] { initial.RefId }, writer.ListRefs().Unwrap());
            Assert.True(writer.ReadRef(initial.RefId).IsFailure);
        }
        fixture.AssertUnchanged(before);
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.True(readOnly.ReadRef(initial.RefId).IsFailure);
        }
        fixture.AssertUnchanged(before);
    }
}
