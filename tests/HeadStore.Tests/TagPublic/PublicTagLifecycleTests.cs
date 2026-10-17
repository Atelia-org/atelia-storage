using System.Buffers;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.HeadStore.HeadStore;

namespace Atelia.HeadStore.Tests.TagPublic;

public sealed class PublicTagLifecycleTests {
    [Fact]
    public void MissingOptionalDirectoryRemainsMissingAcrossReadOnlyOpenAndRead() {
        using var fixture = new TagPublicFixture();
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("HeadStore.TagNotFound", store.ReadTag("A").Error!.ErrorCode);
            Assert.True(store.CreateTag("A", [default], out var outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(fixture.TagsPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Store.MaxHeadEntryPayloadLength)]
    public void SelectedEntryIsCapturedAndSurvivesBothOwnersColdReopen(int length) {
        using var fixture = new TagPublicFixture();
        byte[] expected = new byte[length];
        new Random(313).NextBytes(expected);
        HeadSetTag retained;
        HeadEntryId initial;
        using (var store = fixture.Open()) {
            initial = store.CreateHead(7, expected, out _).Unwrap();
            HeadId[] backing = [default, initial.HeadId, default];
            retained = store.CreateTag("A", backing.AsSpan(1, 1), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Array.Clear(backing);
            store.PublishHead(initial.HeadId, [0x22], out _).Unwrap();
            Assert.Equal(initial, retained.Entries[initial.HeadId]);
            Assert.Equal(initial, store.ReadTag("A").Unwrap().Entries[initial.HeadId]);
            Assert.Equal("HeadStore.DuplicateTagName", store.CreateTag("A", [initial.HeadId], out outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(new[] { initial.HeadId }, store.ListHeads().Unwrap());
        }
        fixture.Data.Dispose();
        using var coldData = DataStore.OpenReadOnly(fixture.DataRoot);
        using var cold = Store.OpenReadOnly(fixture.Root, coldData);
        Assert.Equal(fixture.Identity, cold.HeadStoreId.ToArray());
        Assert.Equal(initial, cold.ReadTag("A").Unwrap().Entries[initial.HeadId]);
        Assert.Equal(expected, cold.ReadEntry(retained.Entries[initial.HeadId]).Unwrap().Payload.ToArray());
    }

    [Fact]
    public void ArrayPoolAndStackHeadInputsCanBeReusedImmediatelyAfterConfirmedCreation() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId initial = store.CreateHead(7, [0x31], out _).Unwrap();
        HeadId[] rented = ArrayPool<HeadId>.Shared.Rent(128);
        try {
            rented[17] = initial.HeadId;
            store.CreateTag("pooled", rented.AsSpan(17, 1), out var outcome).Unwrap();
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Array.Clear(rented);
        }
        finally { ArrayPool<HeadId>.Shared.Return(rented); }
        Span<HeadId> stack = stackalloc HeadId[] { initial.HeadId };
        store.CreateTag("stack", stack, out var stackOutcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, stackOutcome);
        stack.Clear();
        HeadSetTag saved = store.ReadTag("pooled").Unwrap();
        Assert.Equal(initial, saved.Entries[initial.HeadId]);
        Assert.Equal(initial, store.ReadTag("stack").Unwrap().Entries[initial.HeadId]);
        store.Dispose();
        Assert.Equal(initial, saved.Entries[initial.HeadId]);
    }

    [Fact]
    public void NamesPreserveAllUtf16CodeUnitsAndCollidingNamesRemainIndependentAcrossColdOpen() {
        using var fixture = new TagPublicFixture();
        Assert.Equal(1024, Store.MaxTagNameCodeUnits);
        string[] names = [
            "A", "Release", "release", "tag-48", "tag-65", " ", "\0", "\r\n", "/../\\", "\uFEFF",
            new string((char)0xD800, 1), new string((char)0xDC00, 1), new string((char)0xD800, 1024),
            string.Concat(Enumerable.Repeat("\uD83D\uDE00", 512))
        ];
        HeadEntryId[] entries = new HeadEntryId[names.Length];
        using (var store = fixture.Open()) {
            for (int index = 0; index < names.Length; index++) {
                entries[index] = store.CreateHead((uint)index + 1, [(byte)index], out _).Unwrap();
                store.CreateTag(names[index], [entries[index].HeadId], out var outcome).Unwrap();
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
            }
            for (int index = 0; index < names.Length; index++) {
                Assert.Equal(entries[index], Assert.Single(store.ReadTag(names[index]).Unwrap().Entries).Value);
            }
        }
        foreach (string bucket in new[] { "00000351.rbf", "00000174.rbf", "000001aa.rbf", "00000106.rbf", "00000265.rbf" }) {
            Assert.True(File.Exists(Path.Combine(fixture.TagsPath, bucket)));
        }
        Assert.Equal(names.Select(TagPublicFixture.Bucket).Distinct().Count(), Directory.GetFiles(fixture.TagsPath).Length);
        using var cold = fixture.Open(readOnly: true);
        for (int index = 0; index < names.Length; index++) {
            Assert.Equal(entries[index], Assert.Single(cold.ReadTag(names[index]).Unwrap().Entries).Value);
        }
    }

    [Fact]
    public void InvalidNamesAndEmptySelectionRejectBeforeOpeningPoisonedTargetBuckets() {
        using var fixture = new TagPublicFixture();
        string tooLong = new('A', 1025);
        Directory.CreateDirectory(fixture.TagsPath);
        foreach (string name in new[] { "", tooLong, "A" }) { File.WriteAllBytes(fixture.BucketPath(name), []); }
        var before = fixture.CaptureFiles();
        using (var store = fixture.Open()) {
            foreach (string? name in new string?[] { null, "", tooLong, string.Concat(Enumerable.Repeat("\uD83D\uDE00", 513)) }) {
                Assert.True(store.CreateTag(name!, [default], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.True(store.ReadTag(name!).IsFailure);
            }
            Assert.True(store.CreateTag("A", [], out var rejected).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, rejected);
            Assert.Empty(store.ListHeads().Unwrap());
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        HeadId head = healthy.CreateHead(1, [1], out _).Unwrap().HeadId;
        healthy.CreateTag("other", [head], out var confirmed).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Fact]
    public void CreationWritesFixedCountWithItsOwnPreparationTimeAndCheckedMetadata() {
        using var fixture = new TagPublicFixture();
        HeadId head = fixture.SeedHead();
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var store = fixture.Open()) { store.CreateTag("A", [head], out _).Unwrap(); }
        long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.BucketPath("A"), RbfCacheMode.Off);
        var forward = file.ScanForward(showTombstone: true).GetEnumerator();
        Assert.True(forward.MoveNext());
        Assert.Equal(0u, forward.Current.Tag);
        Assert.Equal(24, forward.Current.PayloadLength);
        using (var header = file.ReadPooledFrame(forward.Current.Ticket).Unwrap()) {
            Assert.Equal(fixture.Header("A"), header.PayloadAndMeta.ToArray());
        }
        Assert.True(forward.MoveNext());
        Assert.Equal(1u, forward.Current.Tag);
        Assert.Equal(16, forward.Current.PayloadLength);
        using var metadata = forward.Current.ReadPooledTailMeta().Unwrap();
        Assert.True(RollingCrc.CheckCodewordForward(metadata.TailMeta));
        Assert.InRange(BinaryPrimitives.ReadInt64LittleEndian(metadata.TailMeta), before, after);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(metadata.TailMeta[8..]));
        var fields = new BareValueReader(metadata.TailMeta[12..^4]);
        Assert.Equal("A", fields.ReadString(3072));
        Assert.Equal(metadata.TailMeta.Length - 16, fields.ConsumedCount);
        Assert.False(forward.MoveNext());
        Assert.Null(forward.TerminationError);
    }

    [Fact]
    public void TagsDoNotAllocateHeadsAndHeadUpdatesDoNotAlterTagOrUnrelatedHeadFiles() {
        using var fixture = new TagPublicFixture();
        HeadEntryId initial;
        using (var store = fixture.Open()) { initial = store.CreateHead(7, [0x41], out _).Unwrap(); }
        byte[] originalHead = File.ReadAllBytes(fixture.HeadPath(7));
        using (var store = fixture.Open()) {
            store.CreateTag("A", [initial.HeadId], out _).Unwrap();
            Assert.Equal(new[] { initial.HeadId }, store.ListHeads().Unwrap());
        }
        Assert.Equal(originalHead, File.ReadAllBytes(fixture.HeadPath(7)));
        byte[] originalTag = File.ReadAllBytes(fixture.BucketPath("A"));
        using (var store = fixture.Open()) {
            store.PublishHead(initial.HeadId, [0x42], out _).Unwrap();
            Assert.Equal(257u, store.CreateHead([0x43], out _).Unwrap().HeadId.GetLocalId(fixture.Identity));
            Assert.Equal(initial, store.ReadTag("A").Unwrap().Entries[initial.HeadId]);
        }
        Assert.Equal(originalTag, File.ReadAllBytes(fixture.BucketPath("A")));
    }

    [Fact]
    public void HistoryCallbackCanReadTagsAndRejectsTagMutationWithoutCreatingItsBucket() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId initial = store.CreateHead(7, [1], out _).Unwrap();
        HeadEntryId latest = store.PublishHead(initial.HeadId, [2], out _).Unwrap();
        store.CreateTag("A", [initial.HeadId], out _).Unwrap();
        var delivered = new List<HeadEntryId>();
        var result = store.ReadHeadHistory(initial.HeadId, new(8, 64), entry => {
            delivered.Add(entry.Id);
            Assert.Equal(latest, store.ReadTag("A").Unwrap().Entries[initial.HeadId]);
            Assert.Equal("HeadStore.TagNotFound", store.ReadTag("Release").Error!.ErrorCode);
            Assert.Equal("HeadStore.HistoryActive", store.CreateTag("Release", [initial.HeadId], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(latest, store.ReadHead(initial.HeadId).Unwrap().Id);
            return true;
        });
        Assert.Equal(HeadHistoryEnd.Complete, result.Unwrap());
        Assert.Equal(new[] { latest, initial }, delivered);
        Assert.False(File.Exists(fixture.BucketPath("Release")));
        store.CreateTag("Release", [initial.HeadId], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        fixture.AssertBucketReleased("A");
    }

    [Fact]
    public void UnrelatedActiveDataBuilderRemainsActiveAfterTagPublicationAndOwnerDispose() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        HeadEntryId initial = store.CreateHead(7, [0xff], out _).Unwrap();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        store.CreateTag("A", [initial.HeadId], out var outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(initial, store.ReadTag("A").Unwrap().Entries[initial.HeadId]);
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
    public void DisposedOwnerInitializesOutcomeBeforeThrowingAndRetainedTagRemainsOwned() {
        using var fixture = new TagPublicFixture();
        var store = fixture.Open();
        HeadEntryId initial = store.CreateHead(7, [0xff], out _).Unwrap();
        store.CreateTag("A", [initial.HeadId], out _).Unwrap();
        HeadSetTag saved = store.ReadTag("A").Unwrap();
        store.Dispose();
        PublicationOutcome outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { store.CreateTag(null!, [], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Throws<ObjectDisposedException>(() => { store.ReadTag(null!); });
        Assert.Equal(initial, saved.Entries[initial.HeadId]);
        fixture.Data.EnsureUsable();
        using var reopened = fixture.Open();
        Assert.Equal(initial, reopened.ReadTag("A").Unwrap().Entries[initial.HeadId]);
    }
}
