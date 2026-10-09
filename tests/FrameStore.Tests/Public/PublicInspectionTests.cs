using System.Buffers.Binary;
using System.Numerics;
using Atelia.Data;
using Atelia.Rbf;
using Xunit;
using Store = Atelia.FrameStore.FrameStore;

namespace Atelia.FrameStore.Tests.Public;

/// <summary>真实 public 工厂与关闭 owner 后构造的磁盘向量；不作为外部 kill 或断电证据。</summary>
public sealed class PublicInspectionTests {
    [Fact]
    public void EmptyStoreReportsZeroWithoutInventingFramesOrFiles() {
        using var fixture = new PublicStoreFixture();
        using (var store = Store.Create(fixture.Root)) { AssertEmpty(store); }
        var before = fixture.Snapshot();
        using (var cold = Store.OpenReadOnly(fixture.Root)) { AssertEmpty(cold); }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void InventoryAndAuditCoverRetainedActiveArchiveAndColdReadOnlyFiles() {
        using var fixture = new PublicStoreFixture();
        var addresses = new List<FrameAddress>();
        byte[] identity;
        long threshold = PublicStoreFixture.InitializationBoundary + RbfFile.MeasureWriteSize(8).Unwrap().AppendLength;
        using (var store = Store.Create(fixture.Root, threshold)) {
            identity = store.StoreId.ToArray();
            addresses.Add(store.Append(0, new byte[9], [1, 2]).Unwrap());
            var first = store.BeginAppend(1, 0, out var activeTwo);
            var second = store.BeginAppend(2, 1, out var activeThree);
            PublicStoreFixture.Write(second, [3, 4, 5]);
            Assert.Equal(activeThree, second.EndAppend(uint.MaxValue).Unwrap());
            PublicStoreFixture.Write(first, [6]);
            Assert.Equal(activeTwo, first.EndAppend(7).Unwrap());
            addresses.Add(activeTwo);
            addresses.Add(activeThree);
            first.Dispose();
            second.Dispose();
            store.ConfirmDurable();
            Assert.True(File.Exists(fixture.Archive(1)));
            Assert.True(File.Exists(fixture.Active(2)));
            Assert.True(File.Exists(fixture.Active(3)));
            AssertComplete(store, addresses, identity);
        }
        var before = fixture.Snapshot();
        using (var cold = Store.OpenReadOnly(fixture.Root)) { AssertComplete(cold, addresses, identity); }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void OnlyCheckedFirstTicketIsExcludedAndUserTagZeroAndTombstonesRemainVisible() {
        using var fixture = new PublicStoreFixture();
        FrameAddress ordinary;
        FrameAddress tombstone;
        using (var store = Store.Create(fixture.Root)) {
            ordinary = store.Append(0, [1, 2], [3]).Unwrap();
            tombstone = store.Append(0, [4, 5, 6], [7, 8]).Unwrap();
            store.ConfirmDurable();
        }
        MarkTombstone(fixture.Active(1), Ticket(tombstone));
        var before = fixture.Snapshot();
        using (var cold = Store.OpenReadOnly(fixture.Root)) {
            var infos = new List<FrameInfo>();
            Assert.Equal(2L, cold.Inventory(infos.Add).Unwrap());
            Assert.Equal(new[] { ordinary, tombstone }, infos.Select(info => info.Address));
            Assert.All(infos, info => Assert.Equal(0u, info.Tag));
            Assert.Equal(2, infos[0].PayloadLength);
            Assert.Equal(1, infos[0].TailMetaLength);
            Assert.False(infos[0].IsTombstone);
            Assert.Equal(3, infos[1].PayloadLength);
            Assert.Equal(2, infos[1].TailMetaLength);
            Assert.True(infos[1].IsTombstone);
            using var frame = cold.ReadFrame(tombstone).Unwrap();
            Assert.True(frame.IsTombstone);
            Assert.Equal(new byte[] { 4, 5, 6, 7, 8 }, frame.PayloadAndMeta.ToArray());
            var reports = new List<FrameFileAudit>();
            Assert.Equal(2L, cold.Audit(reports.Add).Unwrap());
            Assert.Equal(2L, Assert.Single(reports).UserFrameCount);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadDamageRemainsStructuralInventoryButFailsAuditWithoutFaultingWriter(bool archived) {
        using var fixture = new PublicStoreFixture();
        FrameAddress address;
        using (var store = archived ? Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary) : Store.Create(fixture.Root)) {
            address = store.Append(0, [1, 2, 3, 4], [5]).Unwrap();
            store.ConfirmDurable();
        }
        string path = archived ? fixture.Archive(1) : fixture.Active(1);
        FlipByte(path, Ticket(address).Offset + sizeof(uint));
        using var reopened = Store.Open(fixture.Root);
        var infos = new List<FrameInfo>();
        Assert.Equal(1L, reopened.Inventory(infos.Add).Unwrap());
        Assert.Equal(address, Assert.Single(infos).Address);
        var reports = new List<FrameFileAudit>();
        var audited = reopened.Audit(reports.Add);
        Assert.True(audited.IsFailure);
        Assert.Equal("Rbf.CrcMismatch", audited.Error!.ErrorCode);
        Assert.Empty(reports);
        Assert.True(reopened.ReadFrame(address).IsFailure);
        reopened.Append(9, [9]).Unwrap();
        reopened.ConfirmDurable();
        Assert.Equal(2L, reopened.Inventory(_ => { }).Unwrap());
    }

    [Theory]
    [InlineData("trailer")]
    [InlineData("fence")]
    public void InteriorStructuralDamageProducesAnErrorAfterTheValidPrefixInsteadOfNormalEof(string kind) {
        using var fixture = new PublicStoreFixture();
        FrameAddress[] addresses = CreateFourUserFrames(fixture);
        SizedPtr damaged = Ticket(addresses[1]);
        FlipByte(fixture.Active(1), kind == "trailer"
            ? damaged.EndOffsetExclusive - sizeof(uint) - 16
            : damaged.EndOffsetExclusive);
        var before = fixture.Snapshot();
        using (var store = Store.Open(fixture.Root)) {
            var infos = new List<FrameInfo>();
            var inventory = store.Inventory(infos.Add);
            Assert.True(inventory.IsFailure);
            Assert.Equal(kind == "trailer" ? "Rbf.CrcMismatch" : "Rbf.FramingError", inventory.Error!.ErrorCode);
            Assert.Equal(addresses[0], Assert.Single(infos).Address);
            var reports = new List<FrameFileAudit>();
            var audit = store.Audit(reports.Add);
            Assert.True(audit.IsFailure);
            Assert.Equal(inventory.Error!.ErrorCode, audit.Error!.ErrorCode);
            Assert.Empty(reports); // An incomplete file never earns its own report.
            store.ConfirmDurable();
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("tag")]
    [InlineData("payload-crc")]
    public void AColdBadArchiveHeaderIsNotAnEmptyFileOrSkippedFile(string kind) {
        using var fixture = new PublicStoreFixture();
        byte[] identity = fixture.CreateClosed();
        if (kind == "identity") { identity[0] ^= 1; }
        byte[] image = fixture.HeaderImage(identity, 1, tag: kind == "tag" ? 1u : 0u, userSuffix: true);
        if (kind == "payload-crc") { image[16] ^= 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Archive(1))!);
        File.WriteAllBytes(fixture.Archive(1), image);
        var before = fixture.Snapshot();
        using (var store = Store.Open(fixture.Root)) {
            var infos = new List<FrameInfo>();
            Assert.True(store.Inventory(infos.Add).IsFailure);
            Assert.Empty(infos);
            var reports = new List<FrameFileAudit>();
            Assert.True(store.Audit(reports.Add).IsFailure);
            Assert.Empty(reports);
            store.ConfirmDurable();
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void GoodFileReportsAndUserPrefixesDoNotTurnALaterBadFileIntoWholeStoreSuccess() {
        using var fixture = new PublicStoreFixture();
        FrameAddress healthy;
        FrameAddress prefix;
        FrameAddress damaged;
        using (var store = Store.Create(fixture.Root)) {
            var first = store.BeginAppend(1, 0, out healthy);
            var second = store.BeginAppend(1, 0, out prefix);
            PublicStoreFixture.Write(second, [2]);
            Assert.Equal(prefix, second.EndAppend(2).Unwrap());
            damaged = store.Append(3, [3]).Unwrap();
            store.Append(4, [4]).Unwrap();
            store.Append(5, [5]).Unwrap();
            PublicStoreFixture.Write(first, [1]);
            Assert.Equal(healthy, first.EndAppend(1).Unwrap());
            store.ConfirmDurable();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Archive(2))!);
        File.Move(fixture.Active(2), fixture.Archive(2));
        FlipByte(fixture.Archive(2), Ticket(damaged).EndOffsetExclusive - sizeof(uint) - 16);
        var before = fixture.Snapshot();
        using (var cold = Store.OpenReadOnly(fixture.Root)) {
            var infos = new List<FrameInfo>();
            Assert.True(cold.Inventory(infos.Add).IsFailure);
            Assert.Contains(infos, info => info.Address == prefix);
            Assert.All(infos, info => Assert.True(info.Address == healthy || info.Address == prefix));
            var reports = new List<FrameFileAudit>();
            Assert.True(cold.Audit(reports.Add).IsFailure);
            // The current shared directory visitor visits active before archive, so a good file has already reported.
            var report = Assert.Single(reports);
            Assert.Equal(1u, report.FileId);
            Assert.Equal(1L, report.UserFrameCount);
        }
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutstandingBuilderRejectsBothInspectionsAndDoesNotEndOrFaultItsLease(bool sized) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        var completed = store.Append(7, [7]).Unwrap();
        var builder = sized ? store.BeginAppend(1, 0, out _) : store.BeginAppend();
        int callbacks = 0;
        Assert.Throws<InvalidOperationException>(() => store.Inventory(_ => callbacks++));
        Assert.Throws<InvalidOperationException>(() => store.Audit(_ => callbacks++));
        Assert.Equal(0, callbacks);
        store.ConfirmDurable();
        PublicStoreFixture.AssertFrame(store, completed, 7, [7]);
        PublicStoreFixture.Write(builder, [8]);
        builder.EndAppend(8).Unwrap();
        builder.Dispose();
        Assert.Equal(2L, store.Inventory(_ => { }).Unwrap());
        Assert.Equal(2L, store.Audit(_ => { }).Unwrap());
    }

    [Fact]
    public void NullVisitorsAreRejectedAndDisposedOwnerTakesPrecedence() {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        Assert.Throws<ArgumentNullException>(() => store.Inventory(null!));
        Assert.Throws<ArgumentNullException>(() => store.Audit(null!));
        store.Append(1, [1]).Unwrap();
        Assert.Equal(1L, store.Inventory(_ => { }).Unwrap());
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Inventory(null!));
        Assert.Throws<ObjectDisposedException>(() => store.Audit(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackRejectsAllMutationsAndRecursionBeforeMaintenanceWhileRandomReadStaysLegal(bool audit) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary);
        var address = store.Append(7, [7], [8]).Unwrap();
        int callbacks = 0;
        void Visit() {
            callbacks++;
            Assert.Throws<InvalidOperationException>(() => store.Append(9, [9]));
            Assert.Throws<InvalidOperationException>(() => store.BeginAppend());
            Assert.Throws<InvalidOperationException>(() => store.BeginAppend(0, 0, out _));
            Assert.Throws<InvalidOperationException>(() => store.ConfirmDurable());
            Assert.Throws<InvalidOperationException>(() => store.Inventory(_ => { }));
            Assert.Throws<InvalidOperationException>(() => store.Audit(_ => { }));
            Assert.True(File.Exists(fixture.Active(1)));
            Assert.False(File.Exists(fixture.Archive(1)));
            PublicStoreFixture.AssertFrame(store, address, 7, [7, 8], 1);
        }
        Assert.Equal(1L, Inspect(store, audit, Visit).Unwrap());
        Assert.Equal(1, callbacks);
        store.Append(9, [9]).Unwrap();
        Assert.True(File.Exists(fixture.Archive(1))); // Rejection did not perform or suppress later maintenance.
        store.ConfirmDurable();
        Assert.Equal(2L, store.Inventory(_ => { }).Unwrap());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackExceptionPropagatesAndFinallyReleasesTheScanGuardWithoutFault(bool audit) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        store.Append(1, [1]).Unwrap();
        var thrown = new ApplicationException("visitor failure");
        Assert.Same(thrown, Record.Exception(() => Inspect(store, audit, () => throw thrown)));
        Assert.Equal(1L, Inspect(store, audit, () => { }).Unwrap());
        store.Append(2, [2]).Unwrap();
        store.ConfirmDurable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationByTheLastCallbackCannotReturnSuccessAndLeavesTheWriterHealthy(bool audit) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        store.Append(1, [1]).Unwrap();
        using var cancelled = new CancellationTokenSource();
        int callbacks = 0;
        var error = Assert.Throws<OperationCanceledException>(() => Inspect(store, audit, () => {
            callbacks++;
            cancelled.Cancel();
        }, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(1, callbacks);
        Assert.Equal(1L, Inspect(store, audit, () => { }).Unwrap());
        store.Append(2, [2]).Unwrap();
        store.ConfirmDurable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCancelledEmptyInspectionCannotReportSuccessOrInvokeAVisitor(bool audit) {
        using var fixture = new PublicStoreFixture();
        using var store = Store.Create(fixture.Root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int callbacks = 0;
        Assert.Throws<OperationCanceledException>(() => Inspect(store, audit, () => callbacks++, cancelled.Token));
        Assert.Equal(0, callbacks);
        store.Append(1, [1]).Unwrap();
        Assert.Equal(1L, Inspect(store, audit, () => { }).Unwrap());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisposeByTheLastCallbackCannotReportSuccessAndReleasesOwnerResources(bool audit, bool readOnly) {
        using var fixture = new PublicStoreFixture();
        FrameAddress address;
        using (var store = Store.Create(fixture.Root)) {
            address = store.Append(1, [1]).Unwrap();
            store.ConfirmDurable();
        }
        using (var store = readOnly ? Store.OpenReadOnly(fixture.Root) : Store.Open(fixture.Root)) {
            int callbacks = 0;
            Assert.Throws<ObjectDisposedException>(() => Inspect(store, audit, () => {
                callbacks++;
                store.Dispose();
            }));
            Assert.Equal(1, callbacks);
            store.Dispose();
            Assert.Throws<ObjectDisposedException>(() => store.Inventory(_ => { }));
        }
        using var reopened = Store.Open(fixture.Root);
        PublicStoreFixture.AssertFrame(reopened, address, 1, [1]);
        Assert.Equal(1L, reopened.Audit(_ => { }).Unwrap());
    }

    [Fact]
    public void AuditHeaderCopiesOutliveColdOwnerAndTemporaryReadersCloseBeforeTheFileCallback() {
        using var fixture = new PublicStoreFixture();
        byte[] identity;
        using (var store = Store.Create(fixture.Root, PublicStoreFixture.InitializationBoundary)) {
            identity = store.StoreId.ToArray();
            store.Append(1, [1]).Unwrap();
            store.Append(2, [2]).Unwrap();
            store.ConfirmDurable();
        }
        var before = fixture.Snapshot();
        var reports = new List<FrameFileAudit>();
        using (var cold = Store.OpenReadOnly(fixture.Root)) {
            Assert.Equal(2L, cold.Audit(report => {
                // A read-only exclusive handle performs no external mutation and proves the scan reader has closed.
                using var exclusive = new FileStream(fixture.Archive(report.FileId), FileMode.Open, FileAccess.Read, FileShare.None);
                reports.Add(report);
            }).Unwrap());
        }
        Assert.Equal(2, reports.Count);
        foreach (var report in reports) { AssertHeader(report, identity); }
        Assert.NotEqual(reports[0].FileId, reports[1].FileId);
        Assert.NotEqual(reports[0].HeaderPayload.ToArray(), reports[1].HeaderPayload.ToArray());
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(-1)]
    public void QualifiedPrivateCreationResidualDoesNotJoinEitherFormalInspection(int prefixLength) {
        using var fixture = new PublicStoreFixture();
        byte[] identity;
        using (var store = Store.Create(fixture.Root)) {
            identity = store.StoreId.ToArray();
            store.Append(1, [1]).Unwrap();
            store.ConfirmDurable();
        }
        byte[] image = fixture.HeaderImage(identity, 2);
        File.WriteAllBytes(fixture.Private(2), prefixLength < 0 ? image : image[..prefixLength]);
        var before = fixture.Snapshot();
        using (var cold = Store.OpenReadOnly(fixture.Root)) {
            var infos = new List<FrameInfo>();
            Assert.Equal(1L, cold.Inventory(infos.Add).Unwrap());
            Assert.Equal(1u, PublicStoreFixture.FileId(Assert.Single(infos).Address));
            var reports = new List<FrameFileAudit>();
            Assert.Equal(1L, cold.Audit(reports.Add).Unwrap());
            Assert.Equal(1u, Assert.Single(reports).FileId);
        }
        fixture.AssertUnchanged(before);
    }

    private static void AssertEmpty(Store store) {
        int callbacks = 0;
        Assert.Equal(0L, store.Inventory(_ => callbacks++).Unwrap());
        Assert.Equal(0L, store.Audit(_ => callbacks++).Unwrap());
        Assert.Equal(0, callbacks);
    }

    private static void AssertComplete(Store store, IReadOnlyList<FrameAddress> addresses, byte[] identity) {
        var infos = new List<FrameInfo>();
        Assert.Equal(3L, store.Inventory(info => {
            infos.Add(info);
            using var read = store.ReadFrame(info.Address).Unwrap();
            Assert.Equal(read.Tag, info.Tag);
            Assert.Equal(read.TailMetaLength, info.TailMetaLength);
            Assert.Equal(read.PayloadAndMeta.Length - read.TailMetaLength, info.PayloadLength);
            Assert.Equal(read.IsTombstone, info.IsTombstone);
        }).Unwrap());
        Assert.Equal(3, infos.Count);
        Assert.True(addresses.ToHashSet().SetEquals(infos.Select(info => info.Address)));
        var reports = new List<FrameFileAudit>();
        Assert.Equal(3L, store.Audit(reports.Add).Unwrap());
        Assert.Equal(new[] { 1u, 2u, 3u }, reports.Select(report => report.FileId).Order());
        Assert.All(reports, report => { Assert.Equal(1L, report.UserFrameCount); AssertHeader(report, identity); });
    }

    private static void AssertHeader(FrameFileAudit report, byte[] identity) {
        byte[] header = report.HeaderPayload.ToArray();
        Assert.Equal(24, header.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(identity, header.AsSpan(4, 16).ToArray());
        Assert.Equal(report.FileId, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)));
    }

    private static AteliaResult<long> Inspect(Store store, bool audit, Action visitor, CancellationToken token = default) =>
        audit ? store.Audit(_ => visitor(), token) : store.Inventory(_ => visitor(), token);

    private static FrameAddress[] CreateFourUserFrames(PublicStoreFixture fixture) {
        using var store = Store.Create(fixture.Root);
        var addresses = Enumerable.Range(1, 4).Select(tag => store.Append((uint)tag, new byte[] { (byte)tag }).Unwrap()).ToArray();
        store.ConfirmDurable();
        return addresses;
    }

    private static SizedPtr Ticket(FrameAddress address) {
        byte[] encoded = new byte[FrameAddress.EncodedSize];
        Assert.True(address.TryWrite(encoded));
        return SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(sizeof(uint))));
    }

    private static void FlipByte(string path, long offset) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int original = stream.ReadByte();
        Assert.InRange(original, 0, 255);
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
    }

    private static void MarkTombstone(string path, SizedPtr ticket) {
        // BCL-only RBF3 wire vector: raw TailKey is outside the XOR body and trailer has four u32 fields.
        // Every caller has closed its FrameStore owner before constructing this immutable fixture image.
        byte[] image = File.ReadAllBytes(path);
        int keyOffset = checked((int)ticket.EndOffsetExclusive - sizeof(uint));
        int trailerOffset = keyOffset - 16;
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(keyOffset));
        Span<byte> trailer = image.AsSpan(trailerOffset, 16);
        for (int i = 0; i < trailer.Length; i++) { trailer[i] ^= (byte)(key >> ((i & 3) * 8)); }
        uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) | 0x8000_0000u;
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], descriptor);
        uint crc = uint.MaxValue;
        for (int i = trailer.Length - 1; i >= sizeof(uint); i--) { crc = BitOperations.Crc32C(crc, trailer[i]); }
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc ^ uint.MaxValue);
        for (int i = 0; i < trailer.Length; i++) { trailer[i] ^= (byte)(key >> ((i & 3) * 8)); }
        File.WriteAllBytes(path, image);
    }
}
