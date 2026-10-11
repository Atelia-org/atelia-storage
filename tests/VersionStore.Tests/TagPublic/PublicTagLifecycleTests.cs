using System.Buffers;
using System.Buffers.Binary;
using Atelia.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Xunit;
using DataStore = Atelia.FrameStore.FrameStore;
using Store = Atelia.VersionStore.VersionStore;

namespace Atelia.VersionStore.Tests.TagPublic;

public sealed class PublicTagLifecycleTests {
    [Fact]
    public void MissingOptionalDirectoryRemainsMissingAcrossReadOnlyOpenAndResolve() {
        using var fixture = new TagPublicFixture();
        Assert.False(Directory.Exists(fixture.TagsPath));
        var before = fixture.Snapshot();
        using (var store = fixture.Open(readOnly: true)) {
            Assert.Equal("VersionStore.TagNotFound", store.ResolveTag("A").Error!.ErrorCode);
            Assert.True(store.CreateTag("A", [1], out var outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        }
        fixture.AssertUnchanged(before);
        Assert.False(Directory.Exists(fixture.TagsPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(Store.MaxRootPayloadLength)]
    public void AnonymousOpaquePayloadIsCapturedAndSurvivesBothOwnersColdReopen(int length) {
        using var fixture = new TagPublicFixture();
        byte[] backing = new byte[length + 7];
        new Random(313).NextBytes(backing);
        backing[3] = 0xff; // Deliberately invalid RootMap; the bytes contract remains valid.
        byte[] expected = backing.AsSpan(3, length).ToArray();
        Assert.True(RootMapCodec.Decode(expected).IsFailure);
        RootPayload retained;
        using (var store = fixture.Open()) {
            Assert.True(store.CreateTag("A", backing.AsSpan(3, length), out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Array.Fill(backing, (byte)0x22);
            retained = store.ResolveTag("A").Unwrap();
            Assert.Equal(length, retained.Length);
            Assert.Equal(expected, retained.Bytes.ToArray());
            Assert.True(store.CreateTag("tag-48", [0x41], out outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            Assert.Equal(new byte[] { 0x41 }, store.ResolveTag("tag-48").Unwrap().Bytes.ToArray());
            Assert.True(store.CreateTag("A", expected, out outcome).IsFailure);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Empty(store.ListRefs().Unwrap());
        }
        fixture.Data.Dispose();
        using var coldData = DataStore.OpenReadOnly(fixture.DataRoot);
        using var cold = Store.OpenReadOnly(fixture.Root, coldData);
        Assert.Equal(fixture.Identity, cold.VersionStoreId.ToArray());
        Assert.Equal(expected, cold.ResolveTag("A").Unwrap().Bytes.ToArray());
        Assert.Equal(expected, retained.Bytes.ToArray());
        Assert.True(RootMapCodec.Decode(retained.Bytes).IsFailure);
    }

    [Fact]
    public void ArrayPoolAndStackInputsCanBeReusedImmediatelyAfterConfirmedCreation() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        byte[] rented = ArrayPool<byte>.Shared.Rent(128);
        try {
            rented.AsSpan().Fill(0x31);
            Assert.True(store.CreateTag("pooled", rented.AsSpan(17, 3), out var outcome).Unwrap());
            Assert.Equal(PublicationOutcome.Confirmed, outcome);
            rented.AsSpan().Fill(0x72);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
        Span<byte> stack = stackalloc byte[] { 0xff, 0, 0x81 };
        Assert.True(store.CreateTag("stack", stack, out var stackOutcome).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, stackOutcome);
        stack.Clear();
        RootPayload saved = store.ResolveTag("pooled").Unwrap();
        Assert.Equal(new byte[] { 0x31, 0x31, 0x31 }, saved.Bytes.ToArray());
        Assert.Equal(new byte[] { 0xff, 0, 0x81 }, store.ResolveTag("stack").Unwrap().Bytes.ToArray());
        store.Dispose();
        Assert.Equal(new byte[] { 0x31, 0x31, 0x31 }, saved.Bytes.ToArray());
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
        using (var store = fixture.Open()) {
            for (int index = 0; index < names.Length; index++) {
                Assert.True(store.CreateTag(names[index], [(byte)index], out var outcome).Unwrap());
                Assert.Equal(PublicationOutcome.Confirmed, outcome);
            }
            for (int index = 0; index < names.Length; index++) {
                Assert.Equal(new[] { (byte)index }, store.ResolveTag(names[index]).Unwrap().Bytes.ToArray());
            }
        }
        Assert.True(File.Exists(Path.Combine(fixture.TagsPath, "00000351.rbf")));
        Assert.True(File.Exists(Path.Combine(fixture.TagsPath, "00000174.rbf")));
        Assert.True(File.Exists(Path.Combine(fixture.TagsPath, "000001aa.rbf")));
        Assert.True(File.Exists(Path.Combine(fixture.TagsPath, "00000106.rbf")));
        Assert.True(File.Exists(Path.Combine(fixture.TagsPath, "00000265.rbf")));
        Assert.Equal(names.Select(TagPublicFixture.Bucket).Distinct().Count(), Directory.GetFiles(fixture.TagsPath).Length);
        using var cold = fixture.Open(readOnly: true);
        for (int index = 0; index < names.Length; index++) {
            Assert.Equal(new[] { (byte)index }, cold.ResolveTag(names[index]).Unwrap().Bytes.ToArray());
        }
    }

    [Fact]
    public void InvalidNamesAndPayloadLengthsRejectBeforeOpeningPoisonedTargetBuckets() {
        using var fixture = new TagPublicFixture();
        string tooLong = new('A', 1025);
        Directory.CreateDirectory(fixture.TagsPath);
        foreach (string name in new[] { "", tooLong, "A" }) { File.WriteAllBytes(fixture.BucketPath(name), []); }
        var before = fixture.Snapshot();
        using (var store = fixture.Open()) {
            foreach (string? name in new string?[] { null, "", tooLong, string.Concat(Enumerable.Repeat("\uD83D\uDE00", 513)) }) {
                Assert.True(store.CreateTag(name!, [1], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
                Assert.True(store.ResolveTag(name!).IsFailure);
            }
            foreach (int length in new[] { 0, Store.MaxRootPayloadLength + 1 }) {
                Assert.True(store.CreateTag("A", new byte[length], out var outcome).IsFailure);
                Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            }
            Assert.Empty(store.ListRefs().Unwrap());
        }
        fixture.AssertUnchanged(before);
        using var healthy = fixture.Open();
        Assert.True(healthy.CreateTag("other", [1], out var confirmed).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, confirmed);
    }

    [Fact]
    public void CreationWritesAnAnonymousRecordWithItsOwnPreparationTimeAndCheckedMetadata() {
        using var fixture = new TagPublicFixture();
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var store = fixture.Open()) { Assert.True(store.CreateTag("A", [0xff], out _).Unwrap()); }
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
        using var metadata = forward.Current.ReadPooledTailMeta().Unwrap();
        Assert.True(RollingCrc.CheckCodewordForward(metadata.TailMeta));
        Assert.InRange(BinaryPrimitives.ReadInt64LittleEndian(metadata.TailMeta), before, after);
        var fields = new BareValueReader(metadata.TailMeta[8..^4]);
        Assert.Equal(0u, fields.ReadVarUInt32());
        Assert.Equal("A", fields.ReadString(3072));
        Assert.Equal(metadata.TailMeta.Length - 12, fields.ConsumedCount); // No RelatedTicket for id0.
        Assert.False(forward.MoveNext());
        Assert.Null(forward.TerminationError);
    }

    [Fact]
    public void TagsDoNotAllocateRefsAndRefUpdatesDoNotAlterTagOrUnrelatedRefFiles() {
        using var fixture = new TagPublicFixture();
        RefRevision initial;
        using (var store = fixture.Open()) { initial = store.CreateRef(7, [0x41], out _).Unwrap(); }
        byte[] originalRef = File.ReadAllBytes(fixture.RefPath(7));
        using (var store = fixture.Open()) {
            Assert.True(store.CreateTag("A", [0x41], out _).Unwrap());
            Assert.Equal(new[] { initial.RefId }, store.ListRefs().Unwrap());
        }
        Assert.Equal(originalRef, File.ReadAllBytes(fixture.RefPath(7)));
        byte[] originalTag = File.ReadAllBytes(fixture.BucketPath("A"));
        using (var store = fixture.Open()) {
            store.PublishRef(initial.RefId, [0x42], out _).Unwrap();
            Assert.Equal(257u, store.CreateRef([0x43], out _).Unwrap().RefId.LocalId);
            Assert.Equal(new byte[] { 0x41 }, store.ResolveTag("A").Unwrap().Bytes.ToArray());
        }
        Assert.Equal(originalTag, File.ReadAllBytes(fixture.BucketPath("A")));
    }

    [Fact]
    public void HistoryCallbackCanResolveTagsAndRejectsTagMutationWithoutCreatingItsBucket() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        RefRevision initial = store.CreateRef(7, [1], out _).Unwrap();
        RefRevision latest = store.PublishRef(initial.RefId, [2], out _).Unwrap();
        store.CreateTag("A", [0xff, 0], out _).Unwrap();
        var delivered = new List<RefRevision>();
        var result = store.ReadRefHistory(initial.RefId, new(8, 64), snapshot => {
            delivered.Add(snapshot.Revision);
            Assert.Equal(new byte[] { 0xff, 0 }, store.ResolveTag("A").Unwrap().Bytes.ToArray());
            Assert.Equal("VersionStore.TagNotFound", store.ResolveTag("Release").Error!.ErrorCode);
            Assert.Equal("VersionStore.HistoryActive", store.CreateTag("Release", [3], out var outcome).Error!.ErrorCode);
            Assert.Equal(PublicationOutcome.NotAttempted, outcome);
            Assert.Equal(latest, store.ReadRef(initial.RefId).Unwrap().Revision);
            return true;
        });
        Assert.Equal(HistoryEnd.Complete, result.Unwrap());
        Assert.Equal(new[] { latest, initial }, delivered);
        Assert.False(File.Exists(fixture.BucketPath("Release")));
        Assert.True(store.CreateTag("Release", [3], out var outcome).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        fixture.AssertBucketReleased("A");
    }

    [Fact]
    public void UnrelatedActiveDataBuilderRemainsActiveAfterTagPublicationAndOwnerDispose() {
        using var fixture = new TagPublicFixture();
        using var store = fixture.Open();
        using var builder = fixture.Data.BeginAppend(1, 0, out var early);
        Assert.True(store.CreateTag("A", [0xff], out var outcome).Unwrap());
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(new byte[] { 0xff }, store.ResolveTag("A").Unwrap().Bytes.ToArray());
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
    public void DisposedOwnerInitializesOutcomeBeforeThrowingAndRetainedPayloadRemainsOwned() {
        using var fixture = new TagPublicFixture();
        var store = fixture.Open();
        store.CreateTag("A", [0xff], out _).Unwrap();
        RootPayload saved = store.ResolveTag("A").Unwrap();
        store.Dispose();
        PublicationOutcome outcome = PublicationOutcome.Confirmed;
        Assert.Throws<ObjectDisposedException>(() => { store.CreateTag(null!, [], out outcome); });
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.Throws<ObjectDisposedException>(() => { store.ResolveTag(null!); });
        Assert.Equal(new byte[] { 0xff }, saved.Bytes.ToArray());
        fixture.Data.EnsureUsable();
        using var reopened = fixture.Open();
        Assert.Equal(new byte[] { 0xff }, reopened.ResolveTag("A").Unwrap().Bytes.ToArray());
    }
}
