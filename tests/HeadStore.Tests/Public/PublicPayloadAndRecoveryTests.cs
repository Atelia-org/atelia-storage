using Xunit;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

public sealed class PublicPayloadAndRecoveryTests {
    [Theory]
    [InlineData(1)]
    [InlineData(Store.MaxHeadEntryPayloadLength)]
    public void BothPayloadLengthEndpointsRemainOpaqueAcrossCreatePublishAndColdRead(int length) {
        using var fixture = new PublicHeadStoreFixture();
        byte[] bytes = new byte[length];
        new Random(37).NextBytes(bytes);
        bytes[0] = 0xff; // Not a valid NamedFrameMap, but valid HeadStore content.
        Assert.True(NamedFrameMapCodec.Decode(bytes).IsFailure);
        HeadEntryId entryId;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var initial = store.CreateHead(bytes, out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(bytes, store.ReadHead(initial.HeadId).Unwrap().Payload.ToArray());
            entryId = store.PublishHead(initial.HeadId, bytes, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(initial, entryId);
        }
        using var cold = Store.OpenReadOnly(fixture.Root, fixture.Data);
        var entry = cold.ReadHead(entryId.HeadId).Unwrap();
        Assert.Equal(entryId, entry.Id);
        Assert.Equal(bytes, entry.Payload.ToArray());
        Assert.True(NamedFrameMapCodec.Decode(entry.Payload).IsFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Store.MaxHeadEntryPayloadLength + 1)]
    public void InvalidPayloadLengthsRejectAllWriteEntrypointsWithoutChangingFilesOrFaultingOwner(int length) {
        using var fixture = new PublicHeadStoreFixture();
        HeadId id = fixture.SeedClosed().HeadId;
        byte[] bytes = new byte[length];
        var before = fixture.CaptureFiles();
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            var outcome = PublicationOutcome.Confirmed;
            Assert.True(store.CreateHead(bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.CreateHead(8, bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(store.PublishHead(id, bytes, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(new[] { id }, store.ListHeads().Unwrap());
            fixture.Data.EnsureUsable();
        }
        fixture.AssertUnchanged(before);
        using var healthy = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(257u, healthy.CreateHead([0xff], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
    }

    [Fact]
    public void InvalidOrOccupiedSpecifiedTargetsRejectWithoutOverwriteOrBurningAutomaticIds() {
        using var fixture = new PublicHeadStoreFixture();
        HeadId id = fixture.SeedClosed().HeadId;
        // A canonical, physically invalid file is occupied too; creation must not read or repair it.
        File.WriteAllBytes(fixture.HeadPath(8), [0x98]);
        var before = fixture.CaptureFiles();
        using (var store = Store.Open(fixture.Root, fixture.Data)) {
            foreach (uint target in new uint[] { 0, 257, uint.MaxValue, 7, 8 }) {
                Assert.True(store.CreateHead(target, [1], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            Assert.True(store.PublishHead(default, [1], out var rejectedOutcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, rejectedOutcome);
            Assert.Equal(new uint[] { 7, 8 }, store.ListHeads().Unwrap().Select(value => value.GetLocalId(fixture.Identity)).Order());
        }
        fixture.AssertUnchanged(before);
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(id, cold.ReadHead(7u).Unwrap().Id.HeadId);
        Assert.Equal(257u, cold.CreateHead([2], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadInitialPayloadDoesNotBlockNecessaryReadOfHealthyHead(bool readOnly) {
        using var fixture = new PublicHeadStoreFixture();
        byte[] initialBytes = Enumerable.Repeat((byte)0xff, 128).ToArray();
        byte[] latestBytes = Enumerable.Repeat((byte)0xfe, 128).ToArray();
        HeadEntryId initial;
        HeadEntryId latest;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            initial = store.CreateHead(initialBytes, out _).Unwrap();
            latest = store.PublishHead(initial.HeadId, latestBytes, out _).Unwrap();
        }
        PublicHeadStoreFixture.FlipPayloadByte(fixture.HeadPath(initial.HeadId.GetLocalId(fixture.Identity)), initial.Ticket);
        var before = fixture.CaptureFiles();
        using (var cold = readOnly ? Store.OpenReadOnly(fixture.Root, fixture.Data) : Store.Open(fixture.Root, fixture.Data)) {
            var current = cold.ReadHead(initial.HeadId).Unwrap();
            Assert.Equal(latest, current.Id);
            Assert.Equal(latestBytes, current.Payload.ToArray());
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BadCurrentPayloadDoesNotFallBackToHealthyInitialEntry(bool readOnly) {
        using var fixture = new PublicHeadStoreFixture();
        HeadEntryId latest;
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var initial = store.CreateHead(Enumerable.Repeat((byte)0xff, 128).ToArray(), out _).Unwrap();
            latest = store.PublishHead(initial.HeadId, Enumerable.Repeat((byte)0xfe, 128).ToArray(), out _).Unwrap();
        }
        PublicHeadStoreFixture.FlipPayloadByte(fixture.HeadPath(latest.HeadId.GetLocalId(fixture.Identity)), latest.Ticket);
        var before = fixture.CaptureFiles();
        using (var cold = readOnly ? Store.OpenReadOnly(fixture.Root, fixture.Data) : Store.Open(fixture.Root, fixture.Data)) {
            Assert.True(cold.ReadHead(latest.HeadId).IsFailure);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritableTargetAccessCompletesRecoverableTailAndReadOnlyKeepsOriginalBytes(bool initialIsHead) {
        using var fixture = new PublicHeadStoreFixture();
        HeadEntryId current;
        byte[] expected = Enumerable.Repeat((byte)0xff, 128).ToArray();
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            current = store.CreateHead(expected, out _).Unwrap();
            if (!initialIsHead) {
                expected[0] = 0xfe;
                current = store.PublishHead(current.HeadId, expected, out _).Unwrap();
            }
        }
        string path = fixture.HeadPath(current.HeadId.GetLocalId(fixture.Identity));
        byte[] complete = File.ReadAllBytes(path);
        // Missing Key and Fence, with a complete body: public RBF recovery may append those bytes.
        File.WriteAllBytes(path, complete[..^8]);
        var before = fixture.CaptureFiles();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(current.HeadId); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(complete[..^8], File.ReadAllBytes(path)); // Factory admission does not open or recover refs.
            var read = writer.ReadHead(current.HeadId).Unwrap();
            Assert.Equal(current, read.Id);
            Assert.Equal(expected, read.Payload.ToArray());
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
        using var cold = Store.Open(fixture.Root, fixture.Data);
        Assert.Equal(current, cold.ReadHead(current.HeadId).Unwrap().Id);
    }

    [Fact]
    public void PartialNextFrameIsRecoveredOnlyOnWritableTargetAccess() {
        using var fixture = new PublicHeadStoreFixture();
        HeadEntryId current = fixture.SeedClosed();
        string path = fixture.HeadPath(current.HeadId.GetLocalId(fixture.Identity));
        byte[] complete = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. complete, 1]);
        var before = fixture.CaptureFiles();
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.Throws<InvalidDataException>(() => { readOnly.ReadHead(current.HeadId); });
        }
        fixture.AssertUnchanged(before);
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(complete.Length + 1, new FileInfo(path).Length);
            Assert.Equal(current, writer.ReadHead(current.HeadId).Unwrap().Id);
        }
        Assert.Equal(complete, File.ReadAllBytes(path));
    }

    [Fact]
    public void HeaderOnlyFormalHeadOccupiesPathButCannotSupplyCurrentEntry() {
        using var fixture = new PublicHeadStoreFixture();
        HeadEntryId initial = fixture.SeedClosed();
        string path = fixture.HeadPath(initial.HeadId.GetLocalId(fixture.Identity));
        byte[] image = File.ReadAllBytes(path);
        File.WriteAllBytes(path, image[..checked((int)initial.Ticket.Offset)]);
        var before = fixture.CaptureFiles();
        using (var writer = Store.Open(fixture.Root, fixture.Data)) {
            Assert.Equal(new[] { initial.HeadId }, writer.ListHeads().Unwrap());
            Assert.True(writer.ReadHead(initial.HeadId).IsFailure);
        }
        fixture.AssertUnchanged(before);
        using (var readOnly = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            Assert.True(readOnly.ReadHead(initial.HeadId).IsFailure);
        }
        fixture.AssertUnchanged(before);
    }
}
