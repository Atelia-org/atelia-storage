using System.Buffers.Binary;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.Public;

public sealed class PublicLifecycleTests {
    [Fact]
    public void BorrowedDataBuilderRemainsActiveAcrossFactoryAndUnrelatedHeadPublication() {
        using var fixture = new PublicHeadStoreFixture();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var entryId = store.CreateHead([0xff], out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new byte[] { 0xff }, store.ReadHead(entryId.HeadId).Unwrap().Payload.ToArray());
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
    public void PublicCreatePublishDisposeAndColdOwnersAcceptSavedHeadIdsAndLocalIds() {
        using var fixture = new PublicHeadStoreFixture();
        byte[] identity;
        HeadId reserved;
        HeadId automatic;
        HeadEntryId published;
        HeadEntry oldEntry;
        IReadOnlyList<HeadId> oldList;
        byte[] json = "{\"phase\":\"done\"}"u8.ToArray();
        using (var store = Store.Create(fixture.Root, fixture.Data, 256)) {
            identity = store.HeadStoreId.ToArray();
            Assert.Equal(16, identity.Length);
            Assert.Contains(identity, value => value != 0);
            Assert.False(store.IsReadOnly);
            Assert.Equal(256u, store.ReservedHeadIdUpperBound);
            Assert.Empty(store.ListHeads().Unwrap());
            Assert.Equal(new[] { "creating", "refs", "versionstore.format", "versionstore.lock" },
                Directory.EnumerateFileSystemEntries(fixture.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            byte[] gate = File.ReadAllBytes(fixture.GatePath);
            Assert.Equal(44, gate.Length);
            Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(gate));
            Assert.Equal(identity, gate.AsSpan(4, 16).ToArray());
            Assert.Equal(fixture.Data.StoreId.ToArray(), gate.AsSpan(20, 16).ToArray());
            Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(gate.AsSpan(36)));

            reserved = store.CreateHead(7, [0xff, 0x00, 0xfe], out var outcome).Unwrap().HeadId;
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            automatic = store.CreateHead([0x80], out outcome).Unwrap().HeadId;
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(7u, reserved.GetLocalId(fixture.Identity));
            Assert.Equal(257u, automatic.GetLocalId(fixture.Identity));
            oldEntry = store.ReadHead(reserved).Unwrap();
            published = store.PublishHead(reserved, json, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(json, store.ReadHead(7u).Unwrap().Payload.ToArray());
            Assert.Equal(published, store.ReadHead(reserved).Unwrap().Id);
            oldList = store.ListHeads().Unwrap();
        }
        Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, oldEntry.Payload.ToArray());
        Assert.Equal(new uint[] { 7, 257 }, oldList.Select(id => id.GetLocalId(fixture.Identity)).Order());
        fixture.Data.EnsureUsable();
        var address = fixture.Data.Append(9, [4]).Unwrap();
        fixture.Data.ConfirmDurable();
        fixture.Data.Dispose();

        using (var data = DataStore.Open(fixture.DataRoot))
        using (var reopened = Store.Open(fixture.Root, data)) {
            Assert.Equal(identity, reopened.HeadStoreId.ToArray());
            Assert.Equal(256u, reopened.ReservedHeadIdUpperBound);
            Assert.Equal(published, reopened.ReadHead(reserved).Unwrap().Id);
            Assert.Equal(automatic, reopened.ReadHead(257u).Unwrap().Id.HeadId);
            Assert.Equal(258u, reopened.CreateHead([0xfd], out var outcome).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            using var frame = data.ReadFrame(address).Unwrap();
            Assert.Equal(new byte[] { 4 }, frame.PayloadAndMeta.ToArray());
        }
        using (var data = DataStore.OpenReadOnly(fixture.DataRoot))
        using (var readOnly = Store.OpenReadOnly(fixture.Root, data)) {
            Assert.True(readOnly.IsReadOnly);
            Assert.Equal(published, readOnly.ReadHead(reserved).Unwrap().Id);
            Assert.Equal(json, readOnly.ReadHead(reserved).Unwrap().Payload.ToArray());
            Assert.Equal(new uint[] { 7, 257, 258 }, readOnly.ListHeads().Unwrap().Select(id => id.GetLocalId(fixture.Identity)).Order());
        }
    }

    [Fact]
    public void InputSliceIsCapturedAndSameBytesPublishCreatesAnotherEntryId() {
        using var fixture = new PublicHeadStoreFixture();
        HeadEntry initial;
        HeadEntry second;
        byte[] input = [0xa1, 0xff, 0x00, 0xfe, 0xa2];
        using (var store = Store.Create(fixture.Root, fixture.Data)) {
            var first = store.CreateHead(input.AsSpan(1, 3), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            input.AsSpan().Fill(0x66);
            initial = store.ReadHead(first.HeadId).Unwrap();
            Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, initial.Payload.ToArray());
            var next = store.PublishHead(first.HeadId, initial.Payload, out outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.NotEqual(first, next);
            Assert.Equal(first.HeadId, next.HeadId);
            second = store.ReadHead(first.HeadId).Unwrap();
            Assert.Equal(next, second.Id);
        }
        fixture.Data.Dispose();
        Assert.Equal(initial.Payload.ToArray(), second.Payload.ToArray());
        Assert.Equal(new byte[] { 0xff, 0x00, 0xfe }, initial.Payload.ToArray());
    }

    [Fact]
    public void IndependentHeadStoresMayBorrowOneDataButRetainTheirOwnLocalHeads() {
        using var fixture = new PublicHeadStoreFixture();
        string otherRoot = Path.Combine(fixture.Sandbox, "other-store");
        using var first = Store.Create(fixture.Root, fixture.Data);
        using var second = Store.Create(otherRoot, fixture.Data);
        var firstId = first.CreateHead([0xff], out _).Unwrap().HeadId;
        var secondId = second.CreateHead([0xfe], out _).Unwrap().HeadId;
        Assert.Equal(firstId.GetLocalId(first), secondId.GetLocalId(second));

        Assert.NotEqual(first.HeadStoreId.ToArray(), second.HeadStoreId.ToArray());
        Assert.True(second.ReadHead(HeadIdTestExtensions.ForeignHeadId(second, 1)).IsFailure);
        Assert.True(second.ReadHead(default(HeadId)).IsFailure);
        Assert.True(second.ReadHead(0u).IsFailure);
        var outcome = PublicationOutcome.Confirmed;
        Assert.True(second.PublishHead(HeadIdTestExtensions.ForeignHeadId(second, 1), [1], out outcome).IsFailure);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Equal(new byte[] { 0xfe }, second.ReadHead(secondId).Unwrap().Payload.ToArray());
        fixture.Data.EnsureUsable();
    }

    [Fact]
    public void WriterExcludesAllOwnersReadersShareAndDisposeReleasesPermanentLock() {
        using var fixture = new PublicHeadStoreFixture();
        HeadId id;
        using (var writer = Store.Create(fixture.Root, fixture.Data)) {
            id = writer.CreateHead([1], out _).Unwrap().HeadId;
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.OpenReadOnly(fixture.Root, fixture.Data); });
        }
        var before = fixture.CaptureFiles();
        using (var first = Store.OpenReadOnly(fixture.Root, fixture.Data)) {
            using var second = Store.OpenReadOnly(fixture.Root, fixture.Data);
            Assert.ThrowsAny<IOException>(() => { using var unexpected = Store.Open(fixture.Root, fixture.Data); });
            Assert.Equal(id, second.ReadHead(id).Unwrap().Id.HeadId);
            Assert.True(first.CreateHead([2], out var outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(first.CreateHead(1, [2], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.True(first.PublishHead(id, [2], out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        using (var reopened = Store.Open(fixture.Root, fixture.Data)) {
            reopened.Dispose();
            reopened.Dispose();
            var outcome = PublicationOutcome.Confirmed;
            Assert.Throws<ObjectDisposedException>(() => { reopened.CreateHead([3], out outcome); });
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        Assert.True(File.Exists(fixture.LockPath));
        Assert.Equal(0, new FileInfo(fixture.LockPath).Length);
        fixture.Data.EnsureUsable();
    }
}
