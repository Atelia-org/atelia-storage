using System.Buffers.Binary;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.Public;

public sealed class PublicLifecycleTests {
    [Fact]
    public void BorrowedDataBuilderRemainsActiveAcrossFactoryAndUnrelatedRefPublication() {
        using var fixture = new PublicVersionStoreFixture();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var revision = store.CreateRef([0xff], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new byte[] { 0xff }, store.ReadRef(revision.RefId).Unwrap().Payload.ToArray());
            Assert.Throws<InvalidOperationException>(() => { fixture.Data.ReadFrame(early); });
        }
        fixture.Data.EnsureUsable();
        var writer = builder.PayloadAndMeta;
        writer.GetSpan(1)[0] = 0x57;
        writer.Advance(1);
        Assert.Equal(early, builder.EndAppend(3).Unwrap());
        fixture.Data.ConfirmDurable();
        using var frame = fixture.Data.ReadFrame(early).Unwrap();
        Assert.Equal(new byte[] { 0x57 }, frame.PayloadAndMeta.ToArray());
    }

    [Fact]
    public void PublicCreatePublishDisposeAndColdOwnersAcceptSavedRefIdsAndLocalIds() {
        using var fixture = new PublicVersionStoreFixture();
        byte[] identity;
        RefId reserved;
        RefId automatic;
        RefRevision published;
        RefBytesSnapshot oldSnapshot;
        IReadOnlyList<RefId> oldList;
        byte[] json = "{\"phase\":\"done\"}"u8.ToArray();
        using (var store = Store.Create(fixture.Root, fixture.Data, 256)) {
            identity = store.VersionStoreId.ToArray();
            Assert.Equal(16, identity.Length);
            Assert.Contains(identity, value => value != 0);
            Assert.False(store.IsReadOnly);
            Assert.Equal(256u, store.ReservedRefIdUpperBound);
            Assert.Empty(store.ListRefs().Unwrap());
            Assert.Equal(new[] { "creating", "refs", "versionstore.format", "versionstore.lock" },
                Directory.EnumerateFileSystemEntries(fixture.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            byte[] gate = File.ReadAllBytes(fixture.GatePath);
            Assert.Equal(44, gate.Length);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(gate));
            Assert.Equal(identity, gate.AsSpan(4, 16).ToArray());
            Assert.Equal(fixture.Data.StoreId.ToArray(), gate.AsSpan(20, 16).ToArray());
            Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(gate.AsSpan(36)));

            reserved = store.CreateRef(7, [0xff, 0x00, 0xfe], out var outcome).Unwrap().RefId;
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            automatic = store.CreateRef([0x80], out outcome).Unwrap().RefId;
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(7u, reserved.LocalId);
            Assert.Equal(257u, automatic.LocalId);
            oldSnapshot = store.ReadRef(reserved).Unwrap();
            published = store.PublishRef(reserved, json, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(json, store.ReadRef(7u).Unwrap().Payload.ToArray());
            Assert.Equal(published, store.ReadRef(reserved).Unwrap().Revision);
            oldList = store.ListRefs().Unwrap();
        }
        Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, oldSnapshot.Payload.ToArray());
        Assert.Equal(new uint[] { 7, 257 }, oldList.Select(id => id.LocalId).Order());
        fixture.Data.EnsureUsable();
        var address = fixture.Data.Append(9, [4]).Unwrap();
        fixture.Data.ConfirmDurable();
        fixture.Data.Dispose();

        using (var data = DataStore.Open(fixture.DataRoot))
        using (var reopened = Store.Open(fixture.Root, data)) {
            Assert.Equal(identity, reopened.VersionStoreId.ToArray());
            Assert.Equal(256u, reopened.ReservedRefIdUpperBound);
            Assert.Equal(published, reopened.ReadRef(reserved).Unwrap().Revision);
            Assert.Equal(automatic, reopened.ReadRef(257u).Unwrap().Revision.RefId);
            Assert.Equal(258u, reopened.CreateRef([0xfd], out var outcome).Unwrap().RefId.LocalId);
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            using var frame = data.ReadFrame(address).Unwrap();
            Assert.Equal(new byte[] { 4 }, frame.PayloadAndMeta.ToArray());
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.True(readOnly.IsReadOnly);
            Assert.Equal(published, readOnly.ReadRef(reserved).Unwrap().Revision);
            Assert.Equal(json, readOnly.ReadRef(reserved).Unwrap().Payload.ToArray());
            Assert.Equal(new uint[] { 7, 257, 258 }, readOnly.ListRefs().Unwrap().Select(id => id.LocalId).Order());
        }
    }

    [Fact]
    public void InputSliceIsCapturedAndSameBytesPublishCreatesAnotherRevision() {
        using var fixture = new PublicVersionStoreFixture();
        RefBytesSnapshot initial;
        RefBytesSnapshot second;
        byte[] input = [0xa1, 0xff, 0x00, 0xfe, 0xa2];
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var first = store.CreateRef(input.AsSpan(1, 3), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            input.AsSpan().Fill(0x66);
            initial = store.ReadRef(first.RefId).Unwrap();
            Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, initial.Payload.ToArray());
            var next = store.PublishRef(first.RefId, initial.Payload, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(first, next);
            Assert.Equal(first.RefId, next.RefId);
            second = store.ReadRef(first.RefId).Unwrap();
            Assert.Equal(next, second.Revision);
        }
        fixture.Data.Dispose();
        Assert.Equal(initial.Payload.ToArray(), second.Payload.ToArray());
        Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, initial.Payload.ToArray());
    }

    [Fact]
    public void IndependentVersionStoresMayBorrowOneDataButRefIdsKeepSeparateIdentities() {
        using var fixture = new PublicVersionStoreFixture();
        string otherRoot = Path.Combine(fixture.Sandbox, "other-store");
        using var first = Store.Create(fixture.Root, fixture.Data);
        using var second = Store.Create(otherRoot, fixture.Data);
        var firstId = first.CreateRef([0xff], out _).Unwrap().RefId;
        var secondId = second.CreateRef([0xfe], out _).Unwrap().RefId;
        Assert.Equal(firstId.LocalId, secondId.LocalId);
        Assert.NotEqual(firstId, secondId);
        Assert.NotEqual(first.VersionStoreId.ToArray(), second.VersionStoreId.ToArray());
        Assert.True(second.ReadRef(firstId).IsFailure);
        Assert.True(second.ReadRef(default(RefId)).IsFailure);
        Assert.True(second.ReadRef(0u).IsFailure);
        var outcome = PublicationOutcome.Confirmed;
        Assert.True(second.PublishRef(firstId, [1], out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new byte[] { 0xfe }, second.ReadRef(secondId).Unwrap().Payload.ToArray());
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void WriterExcludesAllOwnersReadersShareAndDisposeReleasesPermanentLock() {
        using var fixture = new PublicVersionStoreFixture();
        RefId id;
        using (var writer = Store.Create(fixture.Root, fixture.Data)) {
            id = writer.CreateRef([1], out _).Unwrap().RefId;
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        }
        var before = fixture.Snapshot();
        using (var first = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            using var second = Store.OpenReadOnly(fixture.Root, fixture.Data);
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.Equal(id, second.ReadRef(id).Unwrap().Revision.RefId);
            Assert.True(first.CreateRef([2], out var outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(first.CreateRef(1, [2], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(first.PublishRef(id, [2], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        using (var reopened = Store.Open(fixture.Root, fixture.Data)) {
            reopened.Dispose();
            reopened.Dispose();
            var outcome = PublicationOutcome.Confirmed;
            Assert.Throws<ObjectDisposedException>(() => { reopened.CreateRef([3], out outcome); });
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.True(File.Exists(fixture.LockPath));
        Assert.Equal(0, new FileInfo(fixture.LockPath).Length);
        fixture.Data.EnsureUsable();
    }
}
