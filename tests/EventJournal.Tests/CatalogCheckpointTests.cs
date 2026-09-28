using System.Buffers.Binary;
using System.Reflection;
using Atelia.Data;
using Atelia.Data.Hashing;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Xunit;

namespace Atelia.EventJournal.Tests;

public sealed class CatalogCheckpointTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-checkpoint-" + Guid.NewGuid().ToString("N"));
    private string LogPath => Path.Combine(_root, "refs", "ref-op-log.rbf");
    private string SnapshotPath => Path.Combine(_root, "refs", CatalogSnapshotCodec.FileName);
    private static EventJournalOptions NoCache => new() { RefOpLogOptions = new() { CacheMode = RbfCacheMode.Off } };

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void ReaderEnforcesExactSuffixBudget(int count, bool accepted) {
        CreateEmpty();
        AppendAllocations(count);
        if (accepted) {
            using var journal = EventJournal.OpenReadOnlyExisting(_root, NoCache);
            Assert.Equal((0L, (long)count), journal.CatalogCounts);
            Assert.Empty(journal.ListBranches()); // Allocation without Bind is unpublished.
        }
        else {
            var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
            Assert.Equal(StorageOpenErrorKind.MaintenanceRequired, ex.Kind);
            Assert.Equal("SuffixBudgetExceeded", ex.ReasonCode);
        }
    }

    [Fact]
    public void AtBudgetReaderRejectsBeforeReadingNextPayload() {
        CreateEmpty();
        AppendAllocations(1024);
        SizedPtr excess;
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            var op = Allocation("x");
            excess = log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in op)).Unwrap();
            log.DurableFlush();
        }
        // Payload CRC is bad while trailer/fence remain good. Budget error wins over CRC decoding.
        Flip(LogPath, excess.Offset + 4);
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
        Assert.Equal("SuffixBudgetExceeded", ex.ReasonCode);
        Assert.Equal(excess.Offset, ex.Offset);
        var reads = CaptureReplayReads(out Exception? failure);
        Assert.Equal("SuffixBudgetExceeded", Assert.IsType<StorageOpenException>(failure).ReasonCode);
        Assert.All(reads, read => Assert.True(read.Offset + read.Requested <= excess.Offset));
    }

    [Theory]
    [InlineData(1022, 2, false)]
    [InlineData(1023, 2, true)]
    [InlineData(1024, 2, true)]
    [InlineData(1023, 1, false)]
    [InlineData(1024, 1, true)]
    public void WriterReservesWholeOperationBeforeFirstWrite(int count, int records, bool checkpoint) {
        CreateEmpty();
        AppendAllocations(count);
        using var journal = EventJournal.OpenExisting(_root, NoCache);
        EventAddress target = journal.AppendEventFrame(null, []).Unwrap();
        var stages = new List<string>();
        journal.OperationProbe = stages.Add;
        if (records == 2) { journal.CreateBranch("main", null).Unwrap(); }
        else { journal.CreateTag("saved", target).Unwrap(); }
        Assert.Equal(checkpoint, stages.Contains("CheckpointBeforeLogFlush"));
        Assert.Equal(checkpoint ? records : count + records, journal.CatalogCounts.SuffixCount);
        Assert.Equal(1, journal.ListBranches().Count + (records == 1 ? 1 : 0));
    }

    [Fact]
    public void ForkReservesTwoRecordsAndArchiveReservesBeforeClose() {
        RefId main;
        EventAddress target;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) {
            target = journal.AppendEventFrame(null, []).Unwrap();
            main = journal.CreateBranch("main", target).Unwrap();
        }
        AppendAllocations(1021);
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            var stages = new List<string>();
            journal.OperationProbe = stages.Add;
            var child = journal.ForkBranch("child", main, target).Unwrap();
            Assert.Contains("CheckpointBeforeLogFlush", stages);
            Assert.Equal(2, journal.CatalogCounts.SuffixCount);
            journal.ArchiveRef(child, target).Unwrap();
        }
        // Reach Q exactly with a bound live ref and refuse checkpoint before Close.
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            long add = 1024 - journal.CatalogCounts.SuffixCount;
            journal.Dispose();
            AppendAllocations((int)add);
        }
        long length = new FileInfo(LogPath).Length;
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            journal.OperationProbe = stage => { if (stage == "CheckpointBeforeLogFlush") { throw new IOException("injected"); } };
            Assert.Throws<IOException>(() => journal.ArchiveRef(main, target));
            Assert.Throws<InvalidOperationException>(() => journal.OpenBranch("main"));
        }
        Assert.Equal(length, new FileInfo(LogPath).Length);
        using var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Equal(target, reopened.GetHead(main));
    }

    [Theory]
    [InlineData("CheckpointBeforeLogFlush")]
    [InlineData("CheckpointBeforeBoundary")]
    [InlineData("CheckpointBeforeWrite")]
    [InlineData("CheckpointBeforeTempFlush")]
    [InlineData("CheckpointBeforeReplace")]
    [InlineData("CheckpointAfterReplace")]
    [InlineData("CheckpointBeforeInstall")]
    public void FailedPrepublicationCheckpointFaultsAndTagRemainsNotAttempted(string failureStage) {
        CreateEmpty();
        AppendAllocations(1024);
        EventAddress target;
        long before = new FileInfo(LogPath).Length;
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            target = journal.AppendEventFrame(null, []).Unwrap();
            journal.OperationProbe = stage => { if (stage == failureStage) { throw new IOException("injected"); } };
            var ex = Assert.Throws<TagPublicationException>(() => journal.CreateTag("saved", target));
            Assert.Equal(TagPublicationOutcome.NotAttempted, ex.Outcome);
            Assert.Throws<InvalidOperationException>(() => journal.ResolveTag("saved"));
        }
        Assert.Equal(before, new FileInfo(LogPath).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "refs"), "*.tmp"));
        using var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Equal("EventJournal.TagNotFound", reopened.ResolveTag("saved").Error!.ErrorCode);
        Assert.Empty(reopened.ListBranches());
    }

    [Fact]
    public void CheckpointThenConfirmedTagInstallFailureCannotBeDowngraded() {
        CreateEmpty();
        AppendAllocations(1024);
        EventAddress target;
        using (var journal = EventJournal.OpenExisting(_root, NoCache)) {
            target = journal.AppendEventFrame(null, []).Unwrap();
            journal.TagPublicationProbe = stage => { if (stage == TagPublicationStage.AfterDurableFlush) { throw new IOException("injected"); } };
            var ex = Assert.Throws<TagPublicationException>(() => journal.CreateTag("saved", target));
            Assert.Equal(TagPublicationOutcome.Confirmed, ex.Outcome);
            Assert.Equal((0L, 1L), journal.CatalogCounts);
        }
        using var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Equal(target, reopened.ResolveTag("saved").Unwrap());
    }

    [Fact]
    public void InvalidInputsAndCompleteOperationCapacityDoNotCheckpointOrWrite() {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) { id = journal.CreateBranch("main", null).Unwrap(); }
        AppendAllocations(1022);
        using var opened = EventJournal.OpenExisting(_root, NoCache);
        var stages = new List<string>();
        opened.OperationProbe = stages.Add;
        byte[] snapshot = File.ReadAllBytes(SnapshotPath);
        long length = new FileInfo(LogPath).Length;
        Assert.True(opened.CreateBranch("bad/name", null).IsFailure);
        Assert.True(opened.CreateBranch("main", null).IsFailure);
        Assert.True(opened.ArchiveRef(id, new EventAddress(SizedPtr.Create(4, 24), 1, default)).IsFailure);
        Assert.True(opened.CreateTag("bad/name", default).IsFailure);
        Assert.True(opened.ForkBranch("child", id, default).IsFailure);
        // First allocation fits, second Bind start exceeds address range. Reject the entire operation.
        object log = typeof(EventJournal).GetField("_refOpLog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(opened)!;
        var field = log.GetType().GetField("_tailOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long actual = (long)field.GetValue(log)!;
        try {
            field.SetValue(log, SizedPtr.MaxOffset - 4);
            Assert.Equal("EventJournal.RefOpCapacityExhausted", opened.CreateBranch("child", null).Error!.ErrorCode);
        }
        finally { field.SetValue(log, actual); }
        Assert.Empty(stages);
        Assert.Equal(length, new FileInfo(LogPath).Length);
        Assert.Equal(snapshot, File.ReadAllBytes(SnapshotPath));
        Assert.Equal(id, opened.OpenBranch("main").Unwrap());
    }

    [Fact]
    public void AllocationWithoutBindAndCloseWithoutArchiveRemainDistinct() {
        using (var journal = EventJournal.CreateNew(_root, NoCache)) {
            journal.OperationProbe = stage => { if (stage == "RefOpAfterDurableFlush") { throw new IOException("injected"); } };
            Assert.Throws<IOException>(() => journal.CreateBranch("orphan", null));
        }
        using (var reopened = EventJournal.OpenExisting(_root, NoCache)) {
            Assert.Empty(reopened.ListBranches());
            var id = reopened.CreateBranch("main", null).Unwrap();
            reopened.OperationProbe = stage => { if (stage == "RefMoveAfterDurableFlush") { throw new IOException("injected"); } };
            Assert.Throws<IOException>(() => reopened.ArchiveRef(id, null));
        }
        using var final = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        var main = final.OpenBranch("main").Unwrap();
        Assert.Throws<InvalidOperationException>(() => final.GetHead(main));
        Assert.Equal(RefMoveOperation.Close, final.ReadReflog(main).Unwrap()[^1].Operation);
    }

    [Theory]
    [InlineData(0)] // Snapshot CRC.
    [InlineData(1)] // Boundary beyond EOF.
    [InlineData(2)] // Correct length, wrong content witness.
    [InlineData(3)] // Overlarge anchor ticket.
    public void BadSnapshotNeverFallsBackToFullReplay(int kind) {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) { id = journal.CreateBranch("main", null).Unwrap(); }
        using (var log = RbfFile.OpenReadOnlyExisting(LogPath, RbfCacheMode.Off)) {
            var reverse = log.ScanReverse().GetEnumerator();
            Assert.True(reverse.MoveNext());
            var boundary = log.GetScanBoundaryAfter(reverse.Current.Ticket).Unwrap();
            PublishSnapshot(new(boundary, new() { ["main"] = id }, new()));
        }
        byte[] bytes = File.ReadAllBytes(SnapshotPath);
        if (kind == 0) { bytes[^1] ^= 1; }
        else {
            if (kind == 1) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 4096); }
            if (kind == 2) { bytes[32] ^= 1; }
            if (kind == 3) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), SizedPtr.Create(4, 1024).Packed); }
            RollingCrc.SealCodewordForward(bytes);
        }
        File.WriteAllBytes(SnapshotPath, bytes);
        var before = File.ReadAllBytes(LogPath);
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenExisting(_root, NoCache));
        Assert.Equal(kind is 1 or 2 ? "BoundaryMismatch" : "MetadataCorrupt", ex.ReasonCode);
        Assert.Equal(before, File.ReadAllBytes(LogPath));
    }

    [Theory]
    [InlineData(1)] // Unknown frame.
    [InlineData(2)] // TailMeta is illegal.
    [InlineData(3)] // Large payload rejected before rental.
    public void InvalidSuffixMetadataNeverFallsBack(int kind) {
        CreateEmpty();
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            var op = Allocation("main");
            log.Append(kind == 1 ? 123u : EventJournal.RefOpFrameTag,
                kind == 3 ? new byte[4096] : RefOpFrameCodec.Encode(in op), kind == 2 ? new byte[] { 1 } : default).Unwrap();
            log.DurableFlush();
        }
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
        Assert.Equal(kind == 3 ? "ControlFrameTooLarge" : "CatalogInvalid", ex.ReasonCode);
    }

    [Theory]
    [InlineData(0)] // Foreign name.
    [InlineData(1)] // Future allocation.
    [InlineData(2)] // Duplicate active name.
    [InlineData(3)] // Duplicate active RefId under another name.
    public void BindMustUseEarlierMatchingAllocationAndUniqueActiveIdentity(int kind) {
        CreateEmpty();
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            var allocation = Allocation("main");
            var first = log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in allocation)).Unwrap();
            var bind = allocation with { Operation = RefOpOperation.BindName, RefId = new(first.Packed) };
            if (kind == 0) { bind = bind with { BranchName = "foreign" }; }
            if (kind == 1) {
                long future = log.TailOffset + ((RefOpFrameCodec.FixedHeaderLength + 4 + 3) & ~3) + 28;
                bind = bind with { RefId = new(SizedPtr.Create(future, first.Length).Packed) };
            }
            log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in bind)).Unwrap();
            if (kind == 1) { log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in allocation)).Unwrap(); }
            if (kind == 2) {
                var second = log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in allocation)).Unwrap();
                bind = bind with { RefId = new(second.Packed) };
                log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in bind)).Unwrap();
            }
            if (kind == 3) {
                bind = bind with { BranchName = "foreign" };
                log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in bind)).Unwrap();
            }
            log.DurableFlush();
        }
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
        Assert.Equal("CatalogInvalid", ex.ReasonCode);
    }

    [Theory]
    [InlineData(54, true)]
    [InlineData(55, true)]
    [InlineData(88, true)]
    [InlineData(95, true)]
    [InlineData(16, false)] // raw RefId without presence flag
    [InlineData(24, false)] // raw SourceRefId without presence flag
    [InlineData(12, false)] // HasRefId with zero raw RefId
    [InlineData(13, false)] // HasSourceRefId with zero raw SourceRefId (mapped below)
    public void NoncanonicalRefOpFieldsAreRejectedByCodecAndCheckedSuffix(int offset, bool reserved) {
        CreateEmpty();
        var op = Allocation("main");
        byte[] payload = RefOpFrameCodec.Encode(in op);
        if (offset == 13) { payload[12] = 2; }
        else { payload[offset] = 1; }
        var decoded = RefOpFrameCodec.Decode(payload);
        Assert.Equal(reserved ? "EventJournal.RefOpFlagsUnsupported" : "EventJournal.RefOpRefIdFlagMismatch", decoded.Error!.ErrorCode);
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            log.Append(EventJournal.RefOpFrameTag, payload).Unwrap(); // RBF CRC is freshly correct.
            log.DurableFlush();
        }
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
        Assert.Equal(reserved ? StorageOpenErrorKind.FormatUnsupported : StorageOpenErrorKind.MaintenanceRequired, ex.Kind);
        Assert.Equal(reserved ? "UnsupportedFlags" : "InvalidTail", ex.ReasonCode);
    }

    [Theory]
    [InlineData(44)]
    [InlineData(47)]
    public void RefMoveReservedBytesUseUnsupportedFlagsIncludingLocalStateRead(int offset) {
        RefId id;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) { id = journal.CreateBranch("main", null).Unwrap(); }
        var move = new RefMoveFrame(id, 2, 0, RefMoveOperation.Move, null, null, null, 0);
        byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
        RefMoveFrameCodec.Encode(in move, payload);
        payload[offset] = 1;
        Assert.Equal("EventJournal.RefMoveFlagsUnsupported", RefMoveFrameCodec.Decode(payload).Error!.ErrorCode);
        string refLog = Path.Combine(_root, "refs", "objects", id.Packed.ToString("x16"), "segments", "00000001.rbf");
        using (var log = RbfFile.OpenExisting(refLog, RbfCacheMode.Off)) {
            log.Append(EventJournal.RefMoveFrameTag, payload).Unwrap();
            log.DurableFlush();
        }
        using var reopened = EventJournal.OpenExisting(_root, NoCache);
        var result = reopened.MoveRef(id, null, null);
        Assert.Equal("EventJournal.FormatUnsupported", result.Error!.ErrorCode);
        Assert.Equal("UnsupportedFlags", result.Error.Details!["ReasonCode"]);
    }

    [Fact]
    public void ArchivedIdentityCannotBeResurrectedByReplayedBind() {
        using (var journal = EventJournal.CreateNew(_root, NoCache)) {
            var id = journal.CreateBranch("main", null).Unwrap();
            journal.ArchiveRef(id, null).Unwrap();
        }
        byte[] payload;
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            var enumerator = log.ScanForward().GetEnumerator();
            Assert.True(enumerator.MoveNext());
            Assert.True(enumerator.MoveNext());
            using var frame = enumerator.Current.ReadPooledFrame().ToDisposable();
            payload = frame.Unwrap().PayloadAndMeta.ToArray();
            log.Append(EventJournal.RefOpFrameTag, payload).Unwrap();
            log.DurableFlush();
        }
        var ex = Assert.Throws<StorageOpenException>(() => EventJournal.OpenReadOnlyExisting(_root, NoCache));
        Assert.Equal("CatalogInvalid", ex.ReasonCode);
    }

    [Fact]
    public void ActualFileChurnKeepsSnapshotAndReopenSuffixBounded() {
        long peak = 0;
        RefId firstRetired = default;
        RefId live;
        using (var journal = EventJournal.CreateNew(_root, NoCache)) {
            live = journal.CreateBranch("unborn", null).Unwrap();
            for (int i = 0; i < 1100; i++) {
                var id = journal.CreateBranch("cycle", null).Unwrap();
                if (i == 0) { firstRetired = id; }
                journal.ArchiveRef(id, null).Unwrap();
                Assert.True(journal.CatalogCounts.SuffixCount <= Math.Max(1024, journal.CatalogCounts.SnapshotLiveCount));
                peak = Math.Max(peak, journal.CatalogCounts.SnapshotLiveCount);
            }
        }
        Assert.InRange(peak, 1, 2);
        using (var stream = File.OpenRead(SnapshotPath)) {
            var snapshot = CatalogSnapshotCodec.Read(stream, SnapshotPath);
            var reads = CaptureReplayReads(out Exception? failure);
            Assert.Null(failure);
            Assert.All(reads, read => Assert.True(read.Offset >= snapshot.Boundary.AnchorTicket.Offset));
            Assert.True(reads.Sum(read => read.Requested) < 1024 * 600); // at most Q small checked frames+allocation reads
        }
        // An obsolete allocation with invalid framing cannot be scanned during daily catalog restore.
        Flip(LogPath, SizedPtr.FromPacked(firstRetired.Packed).Offset);
        using var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Equal(new[] { "unborn" }, reopened.ListBranches());
        Assert.Null(reopened.GetHead(live));
        Assert.InRange(reopened.CatalogCounts.SuffixCount, 0, 1024);
        Assert.InRange(new FileInfo(SnapshotPath).Length, 68, 400);
        Assert.True(new FileInfo(LogPath).Length > 1024 * 252); // History is retained; snapshot controls daily scan.
    }

    [Fact]
    public void ActualExpansionThenContractionCheckpointsBeforeThresholdCrossing() {
        var ids = new List<RefId>();
        using (var journal = EventJournal.CreateNew(_root, NoCache)) {
            for (int i = 0; i < 2500; i++) { ids.Add(journal.CreateBranch("b" + i, null).Unwrap()); }
            Assert.Equal(2304, journal.CatalogCounts.SnapshotLiveCount);
            for (int i = 0; i < 2499; i++) {
                long beforeLive = journal.ListBranches().Count;
                long oldL0 = journal.CatalogCounts.SnapshotLiveCount;
                journal.ArchiveRef(ids[i], null).Unwrap();
                if (oldL0 > 2 * Math.Max(1024, beforeLive - 1)) {
                    Assert.Equal(beforeLive, journal.CatalogCounts.SnapshotLiveCount); // preoperation state
                    Assert.Equal(1, journal.CatalogCounts.SuffixCount);
                }
            }
            Assert.InRange(journal.CatalogCounts.SnapshotLiveCount, 1, 2048);
        }
        using var reopened = EventJournal.OpenReadOnlyExisting(_root, NoCache);
        Assert.Single(reopened.ListBranches());
        Assert.Null(reopened.GetHead(ids[^1]));
        Assert.InRange(reopened.CatalogCounts.SnapshotLiveCount, 1, 2048);
    }

    [Theory]
    [InlineData(2048, false)]
    [InlineData(2049, true)]
    public void ShrinkThresholdIsStrictAndUsesNextLiveCount(int initialLive, bool expectedCheckpoint) {
        CreateEmpty();
        var branches = new Dictionary<string, RefId>();
        RbfScanBoundary boundary;
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            for (int i = 0; i < initialLive; i++) {
                var allocation = Allocation("b" + i);
                var ticket = log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in allocation)).Unwrap();
                branches.Add(allocation.BranchName, new(ticket.Packed));
            }
            log.DurableFlush();
            var reverse = log.ScanReverse().GetEnumerator();
            Assert.True(reverse.MoveNext());
            boundary = log.GetScanBoundaryAfter(reverse.Current.Ticket).Unwrap();
        }
        PublishSnapshot(new(boundary, branches, new()));
        // Catalog-only synthetic fixture for exact arithmetic: Bind/ref objects/Close are omitted.
        // The separate 2500→1 public-write test witnesses a healthy full journal.
        using (var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off)) {
            for (int i = 0; i < initialLive - 1023; i++) {
                var archive = new RefOpFrame(RefOpOperation.Archive, "b" + i, branches["b" + i], default, 2, null, null, 0, 0);
                log.Append(EventJournal.RefOpFrameTag, RefOpFrameCodec.Encode(in archive)).Unwrap();
            }
            log.DurableFlush();
        }
        using var opened = EventJournal.OpenExisting(_root, NoCache);
        var stages = new List<string>();
        opened.OperationProbe = stages.Add;
        var target = opened.AppendEventFrame(null, []).Unwrap();
        opened.CreateTag("saved", target).Unwrap(); // Lnext=1024.
        Assert.Equal(expectedCheckpoint, stages.Contains("CheckpointBeforeLogFlush"));
        Assert.Equal(expectedCheckpoint ? 1023 : initialLive, opened.CatalogCounts.SnapshotLiveCount);
    }

    private IReadOnlyList<(long Offset, long Requested)> CaptureReplayReads(out Exception? failure) {
        string report = Path.Combine(_root, "catalog-reads.csv");
        failure = null;
        var snapshot = JournalFormat.Validate(_root);
        using (var log = RbfFile.OpenReadOnlyExisting(LogPath, RbfCacheMode.Off)) {
            log.SetupReadLog(report);
            try {
                typeof(EventJournal).GetMethod("ReplayRefOpLog", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object?[] { log, LogPath, snapshot, null, 0L });
            }
            catch (TargetInvocationException ex) { failure = ex.InnerException; }
        }
        return File.ReadLines(report)
            .Where(line => line.Length != 0 && char.IsAsciiDigit(line[0]))
            .Select(line => line.Split(','))
            .Select(fields => (long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private static RefOpFrame Allocation(string name) => new(RefOpOperation.Create, name, default, default, 0, null, null, 0, 0);
    private void CreateEmpty() { using var journal = EventJournal.CreateNew(_root, NoCache); }
    private void AppendAllocations(int count) {
        using var log = RbfFile.OpenExisting(LogPath, RbfCacheMode.Off);
        var allocation = Allocation("orphan");
        byte[] payload = RefOpFrameCodec.Encode(in allocation);
        for (int i = 0; i < count; i++) { log.Append(EventJournal.RefOpFrameTag, payload).Unwrap(); }
        log.DurableFlush();
    }
    private void PublishSnapshot(CatalogSnapshot snapshot) => JournalFormat.Publish(SnapshotPath, stream => CatalogSnapshotCodec.Write(stream, snapshot));
    private static void Flip(string path, long offset) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = offset;
        int value = file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)(value ^ 1));
        file.Flush(true);
    }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }
}
