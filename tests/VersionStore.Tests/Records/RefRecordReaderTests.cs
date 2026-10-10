using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.VersionStore.Internal.Format;
using Atelia.VersionStore.Internal.Records;
using Xunit;

namespace Atelia.VersionStore.Tests.Records;

public sealed class RefRecordReaderTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly string _path;

    public RefRecordReaderTests() => _path = NewPath("ref");

    private static StoreIdentity Identity {
        get {
            Assert.True(StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out var identity));
            return identity;
        }
    }

    private static RefId Id => RefId.Create(Identity, 0x89abcdef);

    private string NewPath(string role) {
        string parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
        string path = Path.Combine(parent, $"versionstore-record-{role}-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { File.Delete(path); }
    }

    private static byte[] Header(ForkOrigin? origin = null, RefId? id = null) {
        byte[] payload = new byte[origin.HasValue ? RefHeaderCodec.WithOriginPayloadSize : RefHeaderCodec.NoOriginPayloadSize];
        Assert.True(RefHeaderCodec.TryWrite(Identity, id ?? Id, origin, payload));
        return payload;
    }

    private static byte[] Metadata(long time) {
        byte[] metadata = new byte[RecordMetadataCodec.EncodedSize];
        Assert.True(RecordMetadataCodec.TryWrite(time, metadata));
        return metadata;
    }

    private static byte[] Bytes(int length, int seed = 17) {
        byte[] payload = new byte[length];
        new Random(seed).NextBytes(payload);
        if (payload.Length > 0) { payload[0] = 0xff; }
        return payload;
    }

    private static SizedPtr AppendSnapshot(IRbfFile file, ReadOnlySpan<byte> payload, long time = -1) =>
        file.Append(RefRecordCodec.SnapshotTag, payload, Metadata(time)).Unwrap();

    private void FlipByte(long offset) {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int original = stream.ReadByte();
        Assert.True(original >= 0);
        stream.Position = offset;
        stream.WriteByte((byte)(original ^ 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1048576)]
    public void CurrentSnapshotKeepsOpaqueBytesAndFullRevision(int length) {
        byte[] payload = Bytes(length);
        SizedPtr ticket;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            ticket = AppendSnapshot(file, payload, 0x0102030405060708);
            file.DurableFlush();
        }

        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        RefBytesSnapshot snapshot = RefRecordReader.ReadCurrent(reader, Identity, Id).Unwrap();
        Assert.Equal(payload, snapshot.Payload.ToArray());
        Assert.Equal(0x0102030405060708, snapshot.RecordedAtUnixMs);
        Assert.Equal(Id, snapshot.Revision.RefId);
        Assert.Equal(ticket, snapshot.Revision.Ticket);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CurrentSnapshotReturnsTheStoredSignedTime(long recordedAtUnixMs) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(file, new byte[] { 0xff }, recordedAtUnixMs);
        Assert.Equal(recordedAtUnixMs, RefRecordReader.ReadCurrent(file, Identity, Id).Unwrap().RecordedAtUnixMs);
    }

    [Theory]
    [InlineData(false, 60L)]
    [InlineData(true, 72L)]
    public void HeaderQualificationSavesThePublicSuccessorAndDoesNotOpenItsOrigin(bool hasOrigin, long expectedOffset) {
        SizedPtr sourceTicket = SizedPtr.Create(60, RbfFile.MeasureWriteSize(1, RecordMetadataCodec.EncodedSize).Value.FrameLength);
        ForkOrigin? origin = hasOrigin ? new ForkOrigin(RefId.Create(Identity, 2), sourceTicket) : null;
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(RefRecordCodec.HeaderTag, Header(origin)).Unwrap();

        CheckedRefHeader header = RefRecordReader.CheckHeader(file, Identity, Id).Unwrap();
        Assert.Equal(Id, header.RefId);
        Assert.Equal(headerTicket, header.HeaderTicket);
        Assert.Equal(file.GetPhysicalOffsetImmediatelyAfter(headerTicket), header.InitialSnapshotOffset);
        Assert.Equal(expectedOffset, header.InitialSnapshotOffset);
        Assert.Equal(origin, header.Origin);

        SizedPtr initial = AppendSnapshot(file, new byte[] { 0xff });
        Assert.Equal(expectedOffset, initial.Offset);
        Assert.Equal(initial, RefRecordReader.CheckInitialization(file, Identity, Id).Unwrap());
        Assert.Equal(initial, RefRecordReader.ReadCurrent(file, header).Unwrap().Revision.Ticket);
    }

    [Fact]
    public void HeaderOnlyQualifiesItsHeaderButCannotSupplyCurrentOrInitialization() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        Assert.True(RefRecordReader.CheckHeader(file, Identity, Id).IsSuccess);
        Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
        Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.CheckInitialization(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BareFenceCannotSupplyAHeader() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        Assert.Equal("VersionStore.InvalidRefHeader", RefRecordReader.CheckHeader(file, Identity, Id).Error!.ErrorCode);
    }

    [Theory]
    [InlineData(23, 0, 0)]
    [InlineData(25, 0, 0)]
    [InlineData(35, 0, 0)]
    [InlineData(37, 0, 0)]
    [InlineData(24, 1, 0)]
    [InlineData(24, 0, 1)]
    [InlineData(4096, 0, 0)]
    public void InvalidFirstShapeCannotBeReplacedWithALaterHeader(int length, int tag, int metaLength) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append((uint)tag, Bytes(length), new byte[metaLength]).Unwrap();
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(file, new byte[] { 0xff });
        Assert.Equal("VersionStore.InvalidRefHeader", RefRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("local")]
    [InlineData("zeroLocal")]
    public void FullRbfCrcDoesNotPermitAdoptingUnexpectedHeaderFields(string field) {
        byte[] payload = Header();
        switch (field) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(payload, 2); break;
            case "identity": payload[4] ^= 1; break;
            case "local": BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 7); break;
            case "zeroLocal": BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 0); break;
        }
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, payload).Unwrap();
        AppendSnapshot(file, new byte[] { 0xff });
        Assert.Equal("VersionStore.InvalidRefHeader", RefRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderPayloadCrcFailureIsRequiredEvenWithAHealthyHead() {
        SizedPtr header;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            header = file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            AppendSnapshot(file, Bytes(128));
            file.DurableFlush();
        }
        FlipByte(header.Offset + header.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        var scan = reader.ScanForward(showTombstone: true).GetEnumerator();
        Assert.True(scan.MoveNext());
        Assert.StartsWith("Rbf.", RefRecordReader.ReadCurrent(reader, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BadInitialPayloadDoesNotRevokeAHealthyCurrentHead() {
        SizedPtr initial;
        SizedPtr head;
        byte[] expected = Bytes(128, 18);
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            initial = AppendSnapshot(file, Bytes(128));
            head = AppendSnapshot(file, expected, -500);
            file.DurableFlush();
        }
        // Corrupt the middle of a publicly written frame, without building or resealing RBF wire bytes.
        FlipByte(initial.Offset + initial.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        RefBytesSnapshot snapshot = RefRecordReader.ReadCurrent(reader, Identity, Id).Unwrap();
        Assert.Equal(head, snapshot.Revision.Ticket);
        Assert.Equal(-500, snapshot.RecordedAtUnixMs);
        Assert.Equal(expected, snapshot.Payload.ToArray());
        Assert.StartsWith("Rbf.", RefRecordReader.CheckInitialization(reader, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BadHeadPayloadFailsInsteadOfReturningTheHealthyInitialSnapshot() {
        SizedPtr head;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            AppendSnapshot(file, Bytes(128));
            head = AppendSnapshot(file, Bytes(128, 18));
            file.DurableFlush();
        }
        FlipByte(head.Offset + head.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        Assert.StartsWith("Rbf.", RefRecordReader.ReadCurrent(reader, Identity, Id).Error!.ErrorCode);
        Assert.True(RefRecordReader.CheckInitialization(reader, Identity, Id).IsSuccess);
    }

    [Theory]
    [InlineData(128, 0, 8)]
    [InlineData(128, 2, 8)]
    [InlineData(128, 1, 0)]
    [InlineData(128, 1, 7)]
    [InlineData(128, 1, 9)]
    [InlineData(0, 1, 8)]
    [InlineData(1048577, 1, 8)]
    public void InvalidLatestEnvelopeAndCapacityFailWithoutFallback(int length, int tag, int metadataLength) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(file, Bytes(128));
        file.Append((uint)tag, Bytes(length), new byte[metadataLength]).Unwrap();
        Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void OversizedSnapshotIsRejectedBeforeAnyCompleteFrameRead() {
        string readLog = NewPath("read-log");
        SizedPtr oversized;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            oversized = file.Append(RefRecordCodec.SnapshotTag,
                Bytes(VersionStore.MaxRootPayloadLength + 1), Metadata(-1)).Unwrap();
            file.SetupReadLog(readLog);
            Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
        }

        string[] reads = File.ReadAllLines(readLog)
            .Where(line => !line.StartsWith('#') && !line.StartsWith("seq,", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(reads);
        Assert.All(reads, line => {
            int requested = int.Parse(line.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(requested < oversized.Length, $"An oversized complete-frame read was requested: {line}");
        });
    }

    [Fact]
    public void InvalidInitialEnvelopeDoesNotBecomeADailyCurrentValuePrecheck() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        file.Append(2, Bytes(128), Metadata(-1)).Unwrap();
        SizedPtr head = AppendSnapshot(file, Bytes(128, 18));
        Assert.Equal(head, RefRecordReader.ReadCurrent(file, Identity, Id).Unwrap().Revision.Ticket);
        Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.CheckInitialization(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void ForwardTerminationErrorPropagatesInsteadOfBecomingMissingHeader() {
        SizedPtr header;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            header = file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            AppendSnapshot(file, Bytes(128));
            file.DurableFlush();
        }
        FlipByte(header.Offset);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        var scan = reader.ScanForward(showTombstone: true).GetEnumerator();
        Assert.False(scan.MoveNext());
        Assert.NotNull(scan.TerminationError);
        var result = RefRecordReader.CheckHeader(reader, Identity, Id);
        Assert.Equal(scan.TerminationError, result.Error);
    }

    [Fact]
    public void SnapshotBytesRemainOwnedAfterPoolReuseQueriesAndFileDisposal() {
        byte[] original = Bytes(128);
        RefBytesSnapshot snapshot;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
            SizedPtr first = AppendSnapshot(file, original, -1);
            snapshot = RefRecordReader.ReadCurrent(file, Identity, Id).Unwrap();
            Array.Fill(original, (byte)0);
            byte[] rented = ArrayPool<byte>.Shared.Rent(first.Length);
            try { Array.Fill(rented, (byte)0x55); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
            AppendSnapshot(file, Bytes(128, 18), 0);
            Assert.Equal(0, RefRecordReader.ReadCurrent(file, Identity, Id).Unwrap().RecordedAtUnixMs);
            Assert.Equal(first, snapshot.Revision.Ticket);
        }
        Assert.Equal(Bytes(128), snapshot.Payload.ToArray());
        Assert.Equal(-1, snapshot.RecordedAtUnixMs);
    }

    [Fact]
    public void ReaderRequestsPhysicalScansAndDoesNotOwnItsFile() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(file, Bytes(128));
        var wrapper = new ObservedRbfFile(file);
        Assert.True(RefRecordReader.ReadCurrent(wrapper, Identity, Id).IsSuccess);
        Assert.True(wrapper.ForwardShowTombstone);
        Assert.True(wrapper.ReverseShowTombstone);
        Assert.Equal(0, wrapper.DisposeCount);
        Assert.True(file.Append(RefRecordCodec.SnapshotTag, Bytes(128), Metadata(0)).IsSuccess);
    }

    [Fact]
    public void HeadMustEndAtTheAdmittedHandlesActualEof() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(file, Bytes(128));
        var wrapper = new ObservedRbfFile(file) { TailOffsetDelta = 4 };
        Assert.Equal("VersionStore.InvalidRefRecord", RefRecordReader.ReadCurrent(wrapper, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderQualificationRejectsAnUnsupportedFormatBeforeScanning() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var wrapper = new ObservedRbfFile(file) { ReportedFormat = RbfFormat.Rbf1 };
        Assert.Equal("VersionStore.InvalidRefHeader", RefRecordReader.CheckHeader(wrapper, Identity, Id).Error!.ErrorCode);
        Assert.False(wrapper.ForwardShowTombstone);
    }

    [Fact]
    public void HeaderQualificationCannotBeReusedOnAnotherHandleOrWithDefaultIdentity() {
        using var first = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        first.Append(RefRecordCodec.HeaderTag, Header()).Unwrap();
        AppendSnapshot(first, Bytes(128));
        CheckedRefHeader header = RefRecordReader.CheckHeader(first, Identity, Id).Unwrap();
        using var second = RbfFile.CreateNew(NewPath("other"), RbfCacheMode.Off);
        Assert.Throws<ArgumentException>(() => RefRecordReader.ReadCurrent(second, header));
        Assert.Throws<ArgumentException>(() => RefRecordReader.CheckHeader(first, default, Id));
        Assert.Throws<ArgumentException>(() => RefRecordReader.CheckHeader(first, Identity, default));
    }

    // This decorator observes the public contract while every scan/frame comes from an actual public RBF file.
    private sealed class ObservedRbfFile(IRbfFile file) : IRbfFile {
        internal bool ForwardShowTombstone { get; private set; }
        internal bool ReverseShowTombstone { get; private set; }
        internal int DisposeCount { get; private set; }
        internal long TailOffsetDelta { get; init; }
        internal RbfFormat? ReportedFormat { get; init; }
        public RbfFormat Format => ReportedFormat ?? file.Format;
        public long TailOffset => file.TailOffset + TailOffsetDelta;
        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => file.Append(tag, payload, tailMeta);
        public RbfFrameBuilder BeginAppend() => file.BeginAppend();
        public RbfFrameBuilder BeginAppend(int payloadLength, int tailMetaLength, out SizedPtr ticket) => file.BeginAppend(payloadLength, tailMetaLength, out ticket);
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ticket) => file.ReadPooledFrame(ticket);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ticket, Span<byte> buffer) => file.ReadFrame(ticket, buffer);
        public RbfForwardSequence ScanForward(bool showTombstone = false) { ForwardShowTombstone = showTombstone; return file.ScanForward(showTombstone); }
        public RbfReverseSequence ScanReverse(bool showTombstone = false) { ReverseShowTombstone = showTombstone; return file.ScanReverse(showTombstone); }
        public AteliaResult<RbfScanBoundary> GetScanBoundaryAfter(SizedPtr ticket) => file.GetScanBoundaryAfter(ticket);
        public AteliaResult<RbfForwardSequence> ScanForward(RbfScanBoundary boundary, bool showTombstone = false) => file.ScanForward(boundary, showTombstone);
        public long GetPhysicalOffsetImmediatelyAfter(SizedPtr ticket) => file.GetPhysicalOffsetImmediatelyAfter(ticket);
        public AteliaResult<OptionalRbfFrameInfo> ReadFrameInfoImmediatelyAfter(SizedPtr ticket) => file.ReadFrameInfoImmediatelyAfter(ticket);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => file.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => file.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => file.ReadPooledTailMeta(ticket);
        public void DurableFlush() => file.DurableFlush();
        public void SetupReadLog(string? path) => file.SetupReadLog(path);
        public void Dispose() { DisposeCount++; file.Dispose(); }
    }
}
