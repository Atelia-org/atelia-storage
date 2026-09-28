using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class CatalogSnapshotTests {
    private const string EmptyHex = "454a4353010040004400000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000962ba6f3";
    private const string LiveHex = "454a4353010040006900000000000000480100000000000010000004010000006be942590100000001000000010000000000000000000000000000000000000004006d61696e1f0000040000000005007361766564160000040000000001000000000000007917f201";

    [Fact]
    public void EmptyCodecMatchesIndependentFrozenVector() {
        var snapshot = new CatalogSnapshot(RbfScanBoundary.Empty, new(), new());
        using var stream = new MemoryStream();
        CatalogSnapshotCodec.Write(stream, snapshot);
        Assert.Equal(EmptyHex, Convert.ToHexStringLower(stream.ToArray()));
        stream.Position = 0;
        var decoded = CatalogSnapshotCodec.Read(stream, "test.snapshot");
        Assert.Equal(RbfScanBoundary.Empty, decoded.Boundary);
        Assert.Equal(0, decoded.LiveCount);
        Assert.Equal("454a464d0200100000000000b47af8d3", Convert.ToHexStringLower(JournalFormat.Encode()));
    }

    [Fact]
    public void LiveCodecMatchesRealPublicWritesAndIndependentVector() {
        string root = Path.Combine(Path.GetTempPath(), "catalog-codec-" + Guid.NewGuid().ToString("N"));
        try {
            EventAddress target;
            RefId id;
            using (var journal = EventJournal.CreateNew(root)) {
                target = journal.AppendEventFrame(null, []).Unwrap();
                id = journal.CreateBranch("main", target).Unwrap();
                journal.CreateTag("saved", target).Unwrap();
            }
            using var log = RbfFile.OpenReadOnlyExisting(Path.Combine(root, "refs", "ref-op-log.rbf"));
            var reverse = log.ScanReverse(showTombstone: true).GetEnumerator();
            Assert.True(reverse.MoveNext());
            var boundary = log.GetScanBoundaryAfter(reverse.Current.Ticket).Unwrap();
            using var stream = new MemoryStream();
            CatalogSnapshotCodec.Write(stream, new(boundary, new() { ["main"] = id }, new() { ["saved"] = target }));
            Assert.Equal(LiveHex, Convert.ToHexStringLower(stream.ToArray()));
            stream.Position = 0;
            var decoded = CatalogSnapshotCodec.Read(stream, "test.snapshot");
            Assert.Equal(boundary, decoded.Boundary);
            Assert.Equal(id, decoded.Branches["main"]);
            Assert.Equal(target, decoded.Tags["saved"]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Theory]
    [InlineData(36)] // Impossible count, rejected before any dictionary capacity reservation.
    [InlineData(40)]
    [InlineData(45)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    public void HeaderMutationsAreRejectedBeforeInstallation(int offset) {
        byte[] bytes = Convert.FromHexString(EmptyHex);
        bytes[offset] ^= 0x80;
        RollingCrc.SealCodewordForward(bytes);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<StorageOpenException>(() => CatalogSnapshotCodec.Read(stream, "test.snapshot"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(68)]
    public void TruncationCrcAndTrailingBytesRejected(int size) {
        byte[] bytes = Convert.FromHexString(EmptyHex);
        if (size == 68) { bytes[67] ^= 1; }
        else { Array.Resize(ref bytes, size); }
        using var stream = new MemoryStream(bytes);
        Assert.Throws<StorageOpenException>(() => CatalogSnapshotCodec.Read(stream, "test.snapshot"));
    }

    [Fact]
    public void EmptyAnchorCannotInventLiveBindings() {
        byte[] bytes = Convert.FromHexString(LiveHex);
        bytes.AsSpan(16, 20).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 4);
        bytes[44] = 0;
        RollingCrc.SealCodewordForward(bytes);
        using var stream = new MemoryStream(bytes);
        Assert.Throws<StorageOpenException>(() => CatalogSnapshotCodec.Read(stream, "test.snapshot"));
    }
}
