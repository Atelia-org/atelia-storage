using System.Buffers;
using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.HeadStore.Internal.Format;
using Atelia.HeadStore.Internal.Records;
using Xunit;

namespace Atelia.HeadStore.Tests.Records;

public sealed class HeadRecordReaderTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly string _path;

    public HeadRecordReaderTests() => _path = NewPath("ref");

    private static StoreIdentity Identity {
        get {
            Assert.True(StoreIdentity.TryRead(Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"), out var identity));
            return identity;
        }
    }

    private static HeadId Id => HeadId.Create(Identity, 0x89abcd);

    private string NewPath(string role) {
        string parent = Directory.Exists(@"W:\") ? @"W:\" : Path.GetTempPath();
        string path = Path.Combine(parent, $"headstore-record-{role}-{Guid.NewGuid():N}.rbf");
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) { File.Delete(path); }
    }

    private static byte[] Header(ForkOrigin? origin = null, HeadId? id = null) {
        byte[] payload = new byte[origin.HasValue ? HeadHeaderCodec.WithOriginPayloadSize : HeadHeaderCodec.NoOriginPayloadSize];
        Assert.True(HeadHeaderCodec.TryWrite(Identity, id ?? Id, origin, payload));
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

    private static SizedPtr AppendEntry(IRbfFile file, ReadOnlySpan<byte> payload, long time = -1) =>
        file.Append(HeadRecordCodec.SingleEntryTag, payload, Metadata(time)).Unwrap();

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
    public void CurrentEntryKeepsOpaqueBytesAndFullEntryId(int length) {
        byte[] payload = Bytes(length);
        SizedPtr ticket;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            ticket = AppendEntry(file, payload, 0x0102030405060708);
            file.DurableFlush();
        }

        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        HeadEntry entry = HeadRecordReader.ReadCurrent(reader, Identity, Id).Unwrap();
        Assert.Equal(payload, entry.Payload.ToArray());
        Assert.Equal(0x0102030405060708, entry.RecordedAtUnixMs);
        Assert.Equal(Id, entry.Id.HeadId);
        Assert.Equal(ticket, entry.Id.Ticket);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CurrentEntryReturnsTheStoredSignedTime(long recordedAtUnixMs) {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(file, new byte[] { 0xff }, recordedAtUnixMs);
        Assert.Equal(recordedAtUnixMs, HeadRecordReader.ReadCurrent(file, Identity, Id).Unwrap().RecordedAtUnixMs);
    }

    [Theory]
    [InlineData(false, 60L)]
    [InlineData(true, 72L)]
    public void HeaderQualificationSavesThePublicSuccessorAndDoesNotOpenItsOrigin(bool hasOrigin, long expectedOffset) {
        SizedPtr sourceTicket = SizedPtr.Create(60, RbfFile.MeasureWriteSize(1, RecordMetadataCodec.EncodedSize).Value.FrameLength);
        ForkOrigin? origin = hasOrigin ? new ForkOrigin(HeadId.Create(Identity, 2), sourceTicket) : null;
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        SizedPtr headerTicket = file.Append(HeadRecordCodec.HeaderTag, Header(origin)).Unwrap();

        CheckedHeadHeader header = HeadRecordReader.CheckHeader(file, Identity, Id).Unwrap();
        Assert.Equal(Id, header.HeadId);
        Assert.Equal(headerTicket, header.HeaderTicket);
        Assert.Equal(file.GetPhysicalOffsetImmediatelyAfter(headerTicket), header.RecordsOffset);
        Assert.Equal(expectedOffset, header.RecordsOffset);
        Assert.Equal(origin, header.Origin);

        SizedPtr initial = AppendEntry(file, new byte[] { 0xff });
        Assert.Equal(expectedOffset, initial.Offset);
        Assert.Equal(initial, HeadRecordReader.CheckInitialization(file, Identity, Id).Unwrap());
        Assert.Equal(initial, HeadRecordReader.ReadCurrent(file, header).Unwrap().Id.Ticket);
    }

    [Fact]
    public void HeaderOnlyQualifiesItsHeaderButCannotSupplyCurrentOrInitialization() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        Assert.True(HeadRecordReader.CheckHeader(file, Identity, Id).IsSuccess);
        Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
        Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.CheckInitialization(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BareFenceCannotSupplyAHeader() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        Assert.Equal("HeadStore.InvalidHeadHeader", HeadRecordReader.CheckHeader(file, Identity, Id).Error!.ErrorCode);
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
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(file, new byte[] { 0xff });
        Assert.Equal("HeadStore.InvalidHeadHeader", HeadRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("identity")]
    [InlineData("local")]
    [InlineData("zeroLocal")]
    public void FullRbfCrcDoesNotPermitAdoptingUnexpectedHeaderFields(string field) {
        byte[] payload = Header();
        switch (field) {
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(payload, 1); break;
            case "identity": payload[4] ^= 1; break;
            case "local": BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 7); break;
            case "zeroLocal": BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), 0); break;
        }
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(HeadRecordCodec.HeaderTag, payload).Unwrap();
        AppendEntry(file, new byte[] { 0xff });
        Assert.Equal("HeadStore.InvalidHeadHeader", HeadRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderPayloadCrcFailureIsRequiredEvenWithAHealthyHead() {
        SizedPtr header;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            header = file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            AppendEntry(file, Bytes(128));
            file.DurableFlush();
        }
        FlipByte(header.Offset + header.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        var scan = reader.ScanForward(showTombstone: true).GetEnumerator();
        Assert.True(scan.MoveNext());
        Assert.StartsWith("Rbf.", HeadRecordReader.ReadCurrent(reader, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BadInitialPayloadDoesNotRevokeAHealthyCurrentHead() {
        SizedPtr initial;
        SizedPtr head;
        byte[] expected = Bytes(128, 18);
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            initial = AppendEntry(file, Bytes(128));
            head = AppendEntry(file, expected, -500);
            file.DurableFlush();
        }
        // Corrupt the middle of a publicly written frame, without building or resealing RBF wire bytes.
        FlipByte(initial.Offset + initial.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        HeadEntry entry = HeadRecordReader.ReadCurrent(reader, Identity, Id).Unwrap();
        Assert.Equal(head, entry.Id.Ticket);
        Assert.Equal(-500, entry.RecordedAtUnixMs);
        Assert.Equal(expected, entry.Payload.ToArray());
        Assert.StartsWith("Rbf.", HeadRecordReader.CheckInitialization(reader, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void BadHeadPayloadFailsInsteadOfReturningTheHealthyInitialEntry() {
        SizedPtr head;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            AppendEntry(file, Bytes(128));
            head = AppendEntry(file, Bytes(128, 18));
            file.DurableFlush();
        }
        FlipByte(head.Offset + head.Length / 2);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        Assert.StartsWith("Rbf.", HeadRecordReader.ReadCurrent(reader, Identity, Id).Error!.ErrorCode);
        Assert.True(HeadRecordReader.CheckInitialization(reader, Identity, Id).IsSuccess);
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
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(file, Bytes(128));
        file.Append((uint)tag, Bytes(length), new byte[metadataLength]).Unwrap();
        Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void OversizedEntryIsRejectedBeforeAnyCompleteFrameRead() {
        string readLog = NewPath("read-log");
        SizedPtr oversized;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            oversized = file.Append(HeadRecordCodec.SingleEntryTag,
                Bytes(HeadStore.MaxHeadEntryPayloadLength + 1), Metadata(-1)).Unwrap();
            file.SetupReadLog(readLog);
            Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.ReadCurrent(file, Identity, Id).Error!.ErrorCode);
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
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        file.Append(2, Bytes(128), Metadata(-1)).Unwrap();
        SizedPtr head = AppendEntry(file, Bytes(128, 18));
        Assert.Equal(head, HeadRecordReader.ReadCurrent(file, Identity, Id).Unwrap().Id.Ticket);
        Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.CheckInitialization(file, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void ForwardTerminationErrorPropagatesInsteadOfBecomingMissingHeader() {
        SizedPtr header;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            header = file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            AppendEntry(file, Bytes(128));
            file.DurableFlush();
        }
        FlipByte(header.Offset);
        using var reader = RbfFile.OpenReadOnlyExisting(_path, RbfCacheMode.Off);
        var scan = reader.ScanForward(showTombstone: true).GetEnumerator();
        Assert.False(scan.MoveNext());
        Assert.NotNull(scan.TerminationError);
        var result = HeadRecordReader.CheckHeader(reader, Identity, Id);
        Assert.Equal(scan.TerminationError, result.Error);
    }

    [Fact]
    public void EntryBytesRemainOwnedAfterPoolReuseQueriesAndFileDisposal() {
        byte[] original = Bytes(128);
        HeadEntry entry;
        using (var file = RbfFile.CreateNew(_path, RbfCacheMode.Off)) {
            file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
            SizedPtr first = AppendEntry(file, original, -1);
            entry = HeadRecordReader.ReadCurrent(file, Identity, Id).Unwrap();
            Array.Fill(original, (byte)0);
            byte[] rented = ArrayPool<byte>.Shared.Rent(first.Length);
            try { Array.Fill(rented, (byte)0x55); }
            finally { ArrayPool<byte>.Shared.Return(rented); }
            AppendEntry(file, Bytes(128, 18), 0);
            Assert.Equal(0, HeadRecordReader.ReadCurrent(file, Identity, Id).Unwrap().RecordedAtUnixMs);
            Assert.Equal(first, entry.Id.Ticket);
        }
        Assert.Equal(Bytes(128), entry.Payload.ToArray());
        Assert.Equal(-1, entry.RecordedAtUnixMs);
    }

    [Fact]
    public void ReaderRequestsPhysicalScansAndDoesNotOwnItsFile() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(file, Bytes(128));
        var wrapper = new ObservedRbfFile(file);
        Assert.True(HeadRecordReader.ReadCurrent(wrapper, Identity, Id).IsSuccess);
        Assert.True(wrapper.ForwardShowTombstone);
        Assert.True(wrapper.ReverseShowTombstone);
        Assert.Equal(0, wrapper.DisposeCount);
        Assert.True(file.Append(HeadRecordCodec.SingleEntryTag, Bytes(128), Metadata(0)).IsSuccess);
    }

    [Fact]
    public void HeadMustEndAtTheAdmittedHandlesActualEof() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        file.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(file, Bytes(128));
        var wrapper = new ObservedRbfFile(file) { TailOffsetDelta = 4 };
        Assert.Equal("HeadStore.InvalidHeadRecord", HeadRecordReader.ReadCurrent(wrapper, Identity, Id).Error!.ErrorCode);
    }

    [Fact]
    public void HeaderQualificationRejectsAnUnsupportedFormatBeforeScanning() {
        using var file = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        var wrapper = new ObservedRbfFile(file) { ReportedFormat = (RbfFormat)0 };
        Assert.Equal("HeadStore.InvalidHeadHeader", HeadRecordReader.CheckHeader(wrapper, Identity, Id).Error!.ErrorCode);
        Assert.False(wrapper.ForwardShowTombstone);
    }

    [Fact]
    public void HeaderQualificationCannotBeReusedOnAnotherHandleOrWithDefaultIdentity() {
        using var first = RbfFile.CreateNew(_path, RbfCacheMode.Off);
        first.Append(HeadRecordCodec.HeaderTag, Header()).Unwrap();
        AppendEntry(first, Bytes(128));
        CheckedHeadHeader header = HeadRecordReader.CheckHeader(first, Identity, Id).Unwrap();
        using var second = RbfFile.CreateNew(NewPath("other"), RbfCacheMode.Off);
        Assert.Throws<ArgumentException>(() => HeadRecordReader.ReadCurrent(second, header));
        Assert.Throws<ArgumentException>(() => HeadRecordReader.CheckHeader(first, default, Id));
        Assert.Throws<ArgumentException>(() => HeadRecordReader.CheckHeader(first, Identity, default));
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
        public AteliaResult<RbfReverseSequence> ScanReverse(RbfScanBoundary boundary, bool showTombstone = false) { ReverseShowTombstone = showTombstone; return file.ScanReverse(boundary, showTombstone); }
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
