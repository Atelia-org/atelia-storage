using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Tests.ConditionalUpdateIntegration;
using Atelia.HeadStore.Tests.History;
using Xunit;

namespace Atelia.HeadStore.Tests.Public;

public sealed class ImportedEntryValidationTests {
    [Theory]
    [InlineData("header", "HeadStore.InvalidHeadRecord")]
    [InlineData("before-header", "HeadStore.InvalidHeadRecord")]
    [InlineData("short", "Rbf.ArgumentError")]
    [InlineData("interior", null)]
    [InlineData("shorter-at-real-fence", "Rbf.FramingError")]
    [InlineData("longer-at-real-fence", "Rbf.FramingError")]
    [InlineData("cross-frame-at-real-fence", "Rbf.FramingError")]
    [InlineData("beyond-completed", "HeadStore.InvalidHeadRecord")]
    public void ImportedNonEntryCoordinatesNeverFallBackOrCreateFork(string candidate, string? expectedError) {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entries = fixture.SeedPublic(1, HistoryFixture.Bytes(value: 0x31),
            HistoryFixture.Bytes(value: 0x32), HistoryFixture.Bytes(value: 0x33));
        SizedPtr first = entries[0].Ticket;
        SizedPtr second = entries[1].Ticket;
        SizedPtr header;
        long tail;
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(1), RbfCacheMode.Off)) {
            var scan = file.ScanForward(showTombstone: true).GetEnumerator();
            Assert.True(scan.MoveNext());
            header = scan.Current.Ticket;
            tail = file.TailOffset;
            // All valid fixtures are ordinary public publications, including their real Fence.
            foreach (HeadEntryId entry in entries) { file.GetScanBoundaryAfter(entry.Ticket).Unwrap(); }
        }
        SizedPtr requested = candidate switch {
            "header" => header,
            "before-header" => SizedPtr.Create(0, 4),
            "short" => SizedPtr.Create(first.Offset, 4),
            "interior" => SizedPtr.Create(first.Offset + 4, 32),
            "shorter-at-real-fence" => SizedPtr.Create(second.Offset + 4, second.Length - 4),
            "longer-at-real-fence" => SizedPtr.Create(second.Offset - 4, second.Length + 4),
            "cross-frame-at-real-fence" => SizedPtr.Create(first.Offset,
                checked((int)(second.Offset + second.Length - first.Offset))),
            "beyond-completed" => SizedPtr.Create(tail, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(candidate))
        };
        if (candidate.EndsWith("at-real-fence", StringComparison.Ordinal)) {
            byte[] image = File.ReadAllBytes(fixture.HeadPath(1));
            Assert.True(image.AsSpan(checked((int)(requested.Offset + requested.Length)), 4).SequenceEqual("RBF3"u8));
        }
        HeadEntryId imported = Import(entries[0].HeadId, requested);
        using var store = fixture.Open();
        AssertRejected(fixture, store, imported, expectedError);
        Assert.Equal(entries[2], store.ReadHead(entries[0].HeadId).Unwrap().Id);
        Assert.Equal(entries[0], store.ReadEntry(Import(entries[0].HeadId, first)).Unwrap().Id);
    }

    [Fact]
    public void NormalWriterEscapesAnEntireEmbeddedFrameAndFenceBeforeImportedCoordinatesAreUsed() {
        using var fixture = new HistoryFixture();
        HeadEntryId inner = fixture.SeedPublic(1, HistoryFixture.Bytes(value: 0x47))[0];
        byte[] innerImage = File.ReadAllBytes(fixture.HeadPath(1));
        byte[] embedded = innerImage.AsSpan(checked((int)inner.Ticket.Offset), inner.Ticket.Length + 4).ToArray();
        Assert.True(embedded.AsSpan(inner.Ticket.Length).SequenceEqual("RBF3"u8));
        HeadEntryId outer = fixture.SeedPublic(2, embedded, [0x71])[0];
        // RBF3 HeadLen is 4B; both the embedded frame and its Fence are aligned in the payload.
        SizedPtr interior = SizedPtr.Create(outer.Ticket.Offset + 4, inner.Ticket.Length);
        byte[] outerImage = File.ReadAllBytes(fixture.HeadPath(2));
        Assert.False(outerImage.AsSpan(checked((int)(interior.Offset + interior.Length)), 4).SequenceEqual("RBF3"u8));
        using var store = fixture.Open();
        Assert.Equal(embedded, store.ReadEntry(Import(outer.HeadId, outer.Ticket)).Unwrap().Payload.ToArray());
        AssertRejected(fixture, store, Import(outer.HeadId, interior), expectedError: null);
    }

    [Theory]
    [InlineData(false, "Rbf.CrcMismatch")]
    [InlineData(true, "Rbf.FramingError")]
    public void ImportedCorruptExactTargetCannotFallBackToHealthyCurrent(bool fence, string expectedError) {
        using var fixture = new HistoryFixture();
        HeadEntryId[] entries = fixture.SeedPublic(1, HistoryFixture.Bytes(value: 0x21),
            HistoryFixture.Bytes(value: 0x22), HistoryFixture.Bytes(value: 0x23));
        if (fence) { fixture.FlipFollowingFence(entries[0]); }
        else { fixture.FlipPayload(entries[0]); }
        // Deliberate byte corruption tests accessed damage only, not the normal-writer membership proof.
        using var store = fixture.Open();
        Assert.Equal(entries[2], store.ReadHead(entries[0].HeadId).Unwrap().Id);
        AssertRejected(fixture, store, Import(entries[0].HeadId, entries[0].Ticket), expectedError);
        store.EnsureUsable();
    }

    [Fact]
    public void ImportedMissingHeadAndOutOfContextHandleAreRejectedByAllExactEntrypoints() {
        using var fixture = new HistoryFixture();
        HeadEntryId entry = fixture.SeedPublic(1, [1])[0];
        using var store = fixture.Open();
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(store.HeadStoreId);
        HeadId missing = ImportHead(ZeroPreservingXor32.Apply(17, key));
        Assert.Equal(17u, store.GetLocalHeadId(missing).Unwrap()); // Decoding does not prove existence.
        AssertRejected(fixture, store, Import(missing, entry.Ticket), "HeadStore.HeadNotFound");
        HeadId wrongContext = ImportHead(ZeroPreservingXor32.Apply(1, key ^ 0x80000000));
        AssertRejected(fixture, store, Import(wrongContext, entry.Ticket), "HeadStore.HeadNotFound");
        AssertRejected(fixture, store, default, "HeadStore.HeadNotFound");
    }

    [Fact]
    public void ZeroLengthCannotBeImportedEvenWithNonzeroOffsetAndValidHead() {
        using var fixture = new HistoryFixture();
        HeadEntryId entry = fixture.SeedPublic(1, [1])[0];
        byte[] encoded = new byte[HeadEntryId.EncodedSize];
        Assert.True(entry.HeadId.TryWrite(encoded));
        BinaryPrimitives.WriteUInt64LittleEndian(encoded.AsSpan(4), (ulong)(entry.Ticket.Offset / 4) << 26);
        Assert.False(HeadEntryId.TryRead(encoded, out HeadEntryId rejected));
        Assert.Equal(default, rejected);
    }

    [Theory]
    [InlineData(0u, 8)]
    [InlineData(1u, 4)]
    [InlineData(3u, 8)]
    public void ImportedCompleteRbfFrameStillRequiresPublicationKindAndMetadata(uint tag, int metadataLength) {
        using var fixture = new HistoryFixture();
        // Normal RBF writer output can be physically valid yet fail the HeadStore envelope contract.
        HeadEntryId[] entries = fixture.WriteMirror(1, null,
            new HistoryRecord([1]), new HistoryRecord([2], Tag: tag, MetadataLength: metadataLength),
            new HistoryRecord([3]));
        using (IRbfFile file = RbfFile.OpenReadOnlyExisting(fixture.HeadPath(1), RbfCacheMode.Off)) {
            file.GetScanBoundaryAfter(entries[1].Ticket).Unwrap();
        }
        using var store = fixture.Open();
        AssertRejected(fixture, store, Import(entries[1].HeadId, entries[1].Ticket), "HeadStore.InvalidHeadRecord");
        Assert.Equal(entries[2], store.ReadHead(entries[0].HeadId).Unwrap().Id);
    }

    [Fact]
    public void ImportedUncommittedCuIsRejectedAsAnchorButSkippedBehindLaterPublication() {
        using var fixture = new HistoryFixture();
        HeadEntryId initial = fixture.SeedPublic(1, [1])[0];
        fixture.SeedPublic(2, [2]);
        // A valid CU frame is written normally, but its required peer is never completed.
        var group = ConditionalIntegrationRecords.AppendGroup(fixture,
            new Dictionary<uint, byte[]> { [1] = [3], [2] = [4] }, 1);
        using var store = fixture.Open();
        HeadEntryId imported = Import(initial.HeadId, group[1].Ticket);
        AssertRejected(fixture, store, imported, "HeadStore.InvalidHeadRecord", "HeadStore.InvalidForkOrigin");
        Assert.Equal(initial, store.ReadHead(initial.HeadId).Unwrap().Id);
        HeadEntryId later = store.PublishHead(initial.HeadId, [5], out _).Unwrap();
        var delivered = new List<HeadEntryId>();
        Assert.Equal(HeadHistoryEnd.Complete,
            store.ReadEntryHistory(Import(later.HeadId, later.Ticket), HistoryFixture.Unlimited, entry => {
                delivered.Add(entry.Id);
                return true;
            }).Unwrap());
        Assert.Equal(new[] { later, initial }, delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedWeakIdentityCanSelectHealthyTargetWithSameKeyOrSameHighKeyByte(bool differentKey) {
        using var fixture = new HistoryFixture();
        HeadEntryId target = fixture.SeedPublic(1, [0x51])[0];
        using var store = fixture.Open();
        byte[] otherIdentity = store.HeadStoreId.ToArray();
        otherIdentity[15] ^= 1;
        uint ownerKey = BinaryPrimitives.ReadUInt32LittleEndian(otherIdentity);
        byte[] codeword = new byte[HeadId.EncodedSize];
        Assert.True(target.HeadId.TryWrite(codeword));
        uint targetBits = BinaryPrimitives.ReadUInt32LittleEndian(codeword);
        uint otherKey = ownerKey;
        uint otherLocal = 1;
        if (differentKey) {
            // Avoid the permutation's zero/key fixed points while keeping the same high byte.
            for (uint delta = 1; ; delta++) {
                otherKey = ownerKey ^ delta;
                otherLocal = ZeroPreservingXor32.Apply(targetBits, otherKey);
                if (otherLocal > 0 && otherLocal <= HeadStore.MaxLocalHeadId && otherLocal != 1) { break; }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(otherIdentity, otherKey);
            Assert.NotEqual(ownerKey, otherKey);
            Assert.Equal(ownerKey >> 24, otherKey >> 24);
        }
        Assert.False(store.HeadStoreId.SequenceEqual(otherIdentity));
        HeadId importedHead = ImportHead(ZeroPreservingXor32.Apply(otherLocal, otherKey));
        HeadEntryId imported = Import(importedHead, target.Ticket);
        Assert.Equal(target, imported); // Full original identity is intentionally absent from the codeword.
        Assert.Equal(1u, store.GetLocalHeadId(importedHead).Unwrap());
        Assert.Equal(target, store.ReadEntry(imported).Unwrap().Id);
        var delivered = new List<HeadEntryId>();
        Assert.Equal(HeadHistoryEnd.Complete, store.ReadEntryHistory(imported, HistoryFixture.Unlimited, entry => {
            delivered.Add(entry.Id);
            return true;
        }).Unwrap());
        Assert.Equal(new[] { target }, delivered);
        HeadId fork = store.ForkHead(3, imported, out PublicationOutcome outcome).Unwrap();
        Assert.Equal(PublicationOutcome.Confirmed, outcome);
        Assert.Equal(target, store.ReadHead(fork).Unwrap().Id);
    }

    private static HeadId ImportHead(uint encoded) {
        Span<byte> bytes = stackalloc byte[HeadId.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, encoded);
        Assert.True(HeadId.TryRead(bytes, out HeadId result));
        return result;
    }

    private static HeadEntryId Import(HeadId head, SizedPtr ticket) {
        Span<byte> bytes = stackalloc byte[HeadEntryId.EncodedSize];
        Assert.True(head.TryWrite(bytes));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], ticket.Packed);
        Assert.True(HeadEntryId.TryRead(bytes, out HeadEntryId result));
        return result;
    }

    private static void AssertRejected(HistoryFixture fixture, HeadStore store, HeadEntryId candidate,
        string? expectedError, string? expectedForkError = null) {
        var read = store.ReadEntry(candidate);
        Assert.True(read.IsFailure);
        AssertError(read.Error!.ErrorCode, expectedError);
        int callbacks = 0;
        var history = store.ReadEntryHistory(candidate, HistoryFixture.Unlimited, _ => { callbacks++; return true; });
        Assert.True(history.IsFailure);
        AssertError(history.Error!.ErrorCode, expectedError);
        Assert.Equal(0, callbacks);
        HeadId[] headsBefore = store.ListHeads().Unwrap().ToArray();
        var fork = store.ForkHead(200, candidate, out PublicationOutcome outcome);
        Assert.True(fork.IsFailure);
        AssertError(fork.Error!.ErrorCode, expectedForkError ?? expectedError);
        Assert.Equal(PublicationOutcome.NotAttempted, outcome);
        Assert.False(File.Exists(fixture.HeadPath(200)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "creating")));
        Assert.Equal(headsBefore, store.ListHeads().Unwrap());
        store.EnsureUsable();
    }

    private static void AssertError(string actual, string? expected) {
        if (expected is null) { Assert.StartsWith("Rbf.", actual); }
        else { Assert.Equal(expected, actual); }
    }
}
