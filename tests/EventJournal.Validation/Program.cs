using System.Diagnostics;
using System.Text.Json;
using Atelia.EventJournal;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
using Store = Atelia.RbfSegmentStore.RbfSegmentStore;

// Explicit opt-in harness. All writable operations are beneath a newly created fixture root.
// Fixture synthesis uses production codecs and RBF append; flush once at completion, never
// reported as public API write latency. Measure opens/reads preserve fixture bytes.
static class Program {
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    sealed record Manifest(string Kind, int Count, ulong Ref, ulong TargetTicket, uint TargetSegment, string Construction) {
        [System.Text.Json.Serialization.JsonIgnore]
        public EventAddress Target => new(SizedPtr.FromPacked(TargetTicket), TargetSegment, default);
    }
    static EventJournalOptions Options(int capacity = 8) => new() {
        RefStoreCacheCapacity = capacity,
        EventSegmentStoreOptions = new() { HistoricalReaderPoolCapacity = 4 },
        RefSegmentStoreOptions = new() { HistoricalReaderPoolCapacity = 4 }
    };
    static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    public static int Main(string[] args) {
        try {
            if (args.Length < 2) { throw new ArgumentException("Expected fixture|measure|kill-child|verify <temporary-root> ..."); }
            string root = Path.GetFullPath(args[1]);
            switch (args[0]) {
                case "fixture": Fixture(root, args[2], int.Parse(args[3])); break;
                case "measure": Measure(root, args[2], int.Parse(args[3])); break;
                case "kill-child": KillChild(root, args[2]); break;
                case "verify": Verify(root, args[2]); break;
                case "write-sample": WriteSample(root, int.Parse(args[2])); break;
                case "closed-handle": ClosedHandle(root); break;
                case "describe": Describe(root); break;
                default: throw new ArgumentException("Unknown command.");
            }
            return 0;
        }
        catch (Exception e) {
            Emit(new { status = "Failed", type = e.GetType().Name, code = e is StorageOpenException s ? s.ReasonCode : null });
            return 1;
        }
    }
    static string EventPath(string root, uint number) => RbfSegmentPath.GetSegmentPath(Path.Combine(root, "events"), RbfSegmentStoreLayout.Bucketed, number);
    static string RefPath(string root, RefId id, uint number) => RbfSegmentPath.GetSegmentPath(Path.Combine(root, "refs", "objects", id.Packed.ToString("x16")), RbfSegmentStoreLayout.Flat, number);
    static void Snapshot(string root, RefId id, Dictionary<string, EventAddress>? tags = null) {
        using var log = RbfFile.OpenExisting(Path.Combine(root, "refs", "ref-op-log.rbf"));
        var tail = log.ScanReverse(true).GetEnumerator();
        if (!tail.MoveNext()) { throw new InvalidOperationException("Fixture log must be nonempty."); }
        var snapshot = new CatalogSnapshot(log.GetScanBoundaryAfter(tail.Current.Ticket).Unwrap(),
            id.IsDefault ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal) { ["main"] = id }, tags ?? new(StringComparer.Ordinal));
        using var stream = File.Create(Path.Combine(root, "refs", CatalogSnapshotCodec.FileName));
        CatalogSnapshotCodec.Write(stream, snapshot);
        stream.Flush(true);
    }
    static void Fixture(string root, string kind, int count) {
        if (count < 1 || Directory.Exists(root) || File.Exists(root)) { throw new ArgumentException("Fixture must be new and count positive."); }
        var timer = Stopwatch.StartNew();
        RefId id; EventAddress target;
        using (var journal = Journal.CreateNew(root, Options())) {
            target = journal.AppendEventFrame(null, ReadOnlySpan<byte>.Empty).Unwrap();
            id = kind == "control-allocations" ? default : journal.CreateBranch("main", target).Unwrap();
            if (kind == "cache") {
                for (int i = 1; i < count; i++) { journal.CreateBranch($"b{i:D8}", target).Unwrap(); }
            }
            if (kind == "churn-public") {
                for (int i = 0; i < count; i++) {
                    var transient = journal.CreateBranch("transient", null).Unwrap();
                    journal.ArchiveRef(transient, null).Unwrap();
                }
            }
            if (kind == "shrink-public") {
                for (int i = 1; i < count; i++) { journal.CreateBranch($"b{i:D8}", null).Unwrap(); }
                for (int i = 1; i < count; i++) { journal.ArchiveRef(journal.OpenBranch($"b{i:D8}").Unwrap(), null).Unwrap(); }
            }
        }
        string construction = kind.EndsWith("public") || kind == "cache" ? "public APIs; production fsync unchanged" : kind == "segments" ? "production codecs+RBF append; each synthetic segment flushed; fixture creation is not write latency" : "production codecs+RBF append; one completion flush; fixture creation is not write latency";
        if (kind == "moves") {
            using var file = RbfFile.OpenExisting(RefPath(root, id, 1), RbfCacheMode.Off);
            byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
            for (int i = 1; i <= count; i++) {
                var move = new RefMoveFrame(id, (ulong)i + 1, 0, RefMoveOperation.Move, target, target, target, 0);
                RefMoveFrameCodec.Encode(move, payload);
                file.Append(Journal.RefMoveFrameTag, payload).Unwrap();
            }
            file.DurableFlush();
        }
        if (kind is "orphans" or "chain") {
            using var file = RbfFile.OpenExisting(EventPath(root, 1), RbfCacheMode.Off);
            byte[] meta = new byte[EventFrameHeaderCodec.FixedLength];
            EventAddress? parent = kind == "chain" ? target : null;
            for (int i = 1; i <= count; i++) {
                var header = new EventFrameHeader(EventPayloadCodecId.Identity, (ulong)i + 1, 0, 0, default, 0, parent);
                EventFrameHeaderCodec.Encode(header, meta);
                var next = new EventAddress(file.Append(Journal.EventFrameTag, [], meta).Unwrap(), 1, default);
                if (kind == "chain") { parent = next; }
            }
            file.DurableFlush();
            if (kind == "chain") { target = parent!.Value; }
        }
        if (kind == "chain") {
            using var journal = Journal.OpenExisting(root, Options());
            journal.MoveRef(id, journal.GetHead(id), target).Unwrap();
        }
        if (kind == "control-allocations") {
            using (var file = RbfFile.OpenExisting(Path.Combine(root, "refs", "ref-op-log.rbf"), RbfCacheMode.Off)) {
                var op = new RefOpFrame(RefOpOperation.Create, "unbound", default, default, 0, null, null, 0, 0);
                byte[] payload = RefOpFrameCodec.Encode(op);
                for (int i = 0; i < count; i++) { file.Append(Journal.RefOpFrameTag, payload).Unwrap(); }
                file.DurableFlush();
            }
            Snapshot(root, default);
            using var journal = Journal.OpenExisting(root, Options());
            id = journal.CreateBranch("main", target).Unwrap();
        }
        if (kind == "tags") {
            var tags = new Dictionary<string, EventAddress>(StringComparer.Ordinal);
            using (var file = RbfFile.OpenExisting(Path.Combine(root, "refs", "ref-op-log.rbf"), RbfCacheMode.Off)) {
                for (int i = 0; i < count; i++) {
                    string name = $"t{i:D8}";
                    byte[] payload = TagBindingFrameCodec.Encode(name, target);
                    file.Append(Journal.TagBindingFrameTag, payload).Unwrap();
                    tags.Add(name, target);
                }
                file.DurableFlush();
            }
            Snapshot(root, id, tags);
        }
        if (kind == "segments") {
            var init = new RefMoveFrame(id, 1, 0, RefMoveOperation.Init, null, null, target, 0);
            byte[] payload = new byte[RefMoveFrameCodec.FixedLength];
            for (uint number = 2; number <= count; number++) {
                string path = RefPath(root, id, number);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = RbfFile.CreateNew(path, RbfCacheMode.Off);
                var move = init with { MoveSequenceNumber = number, Operation = RefMoveOperation.Move, ExpectedOldTarget = target, OldTarget = target };
                RefMoveFrameCodec.Encode(move, payload);
                file.Append(Journal.RefMoveFrameTag, payload).Unwrap();
                file.DurableFlush();
            }
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(RefPath(root, id, 1)))!, "active.segment"), SegmentLocator.Encode(RbfSegmentStoreLayout.Flat, (uint)count));
        }
        File.WriteAllText(root + ".fixture.json", JsonSerializer.Serialize(new Manifest(kind, count, id.Packed, target.Ticket.Packed, target.SegmentNumber, construction), Json));
        using (var journal = Journal.OpenReadOnlyExisting(root, Options())) {
            if (journal.GetHead(id) != target) { throw new InvalidOperationException("Fixture head mismatch."); }
        }
        var catalog = JournalFormat.Validate(root);
        long suffixCount = 0;
        using (var log = RbfFile.OpenReadOnlyExisting(Path.Combine(root, "refs", "ref-op-log.rbf"))) {
            var suffix = log.ScanForward(catalog.Boundary, true).Unwrap().GetEnumerator();
            while (suffix.MoveNext()) { suffixCount++; }
            if (suffix.TerminationError is not null) { throw new InvalidOperationException("Fixture suffix invalid."); }
        }
        Emit(new { status = "FixtureReady", kind, count, construction, elapsedMs = timer.Elapsed.TotalMilliseconds,
            snapshotLive = catalog.LiveCount, suffixCount, snapshotBytes = new FileInfo(Path.Combine(root,"refs",CatalogSnapshotCodec.FileName)).Length });
    }
    static Manifest ReadManifest(string root) => JsonSerializer.Deserialize<Manifest>(File.ReadAllText(root + ".fixture.json"), Json)!;
    static int DatasetHandles(string root) {
        if (!OperatingSystem.IsLinux()) { return -1; }
        return Directory.EnumerateFiles("/proc/self/fd").Count(p => {
            try { return File.ResolveLinkTarget(p, false)?.FullName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true; }
            catch (IOException) { return false; }
        });
    }
    static void Measure(string root, string mode, int repetitions) {
        var manifest = ReadManifest(root);
        var id = new RefId(manifest.Ref);
        var samples = new List<double>(); long totalAlloc = 0;
        int maxHandles = 0, retained = 0, plans = 0; long parentReads = 0;
        Journal? warm = mode.StartsWith("warm") ? Journal.OpenReadOnlyExisting(root, Options()) : null;
        try {
            for (int i = 0; i < repetitions; i++) {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                var journal = warm ?? Journal.OpenReadOnlyExisting(root, Options());
                if (mode.Contains("chain")) {
                    var addresses = journal.ReadChronologicalChain(id).Unwrap();
                    if (addresses.Count != manifest.Count + 1) { throw new InvalidOperationException("Chain length mismatch."); }
                }
                else if (mode == "cache") {
                    foreach (var name in journal.ListBranches()) { journal.GetHead(journal.OpenBranch(name).Unwrap()); }
                    journal.GetHead(id);
                }
                else if (mode != "open") { if (journal.GetHead(id) != manifest.Target) { throw new InvalidOperationException("Head mismatch."); } }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                totalAlloc += GC.GetAllocatedBytesForCurrentThread() - before;
                samples.Add(elapsed);
                maxHandles = Math.Max(maxHandles, DatasetHandles(root));
                retained = Math.Max(retained, journal.RetainedRefEntryCount);
                plans = Math.Max(plans, journal.ForwardPlanCacheEntryCount);
                parentReads = Math.Max(parentReads, (long)journal.ForwardPlanCacheStats.ParentWalkReads);
                if (warm is null) { journal.Dispose(); }
            }
        }
        finally { warm?.Dispose(); }
        samples.Sort();
        Emit(new { status = "Measured", manifest.Kind, manifest.Count, mode, repetitions,
            p50Ms = samples[(samples.Count - 1) / 2], p95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
            allocatedBytesPerIteration = totalAlloc / repetitions, maxDatasetHandles = maxHandles,
            datasetHandlesAfterDispose = DatasetHandles(root), retainedRefEntries = retained, forwardPlanEntries = plans,
            parentWalkReads = parentReads, osCache = "uncontrolled; cold means new journal instance, not dropped OS cache" });
    }
    static void Describe(string root) {
        var manifest = ReadManifest(root);
        using var journal = Journal.OpenReadOnlyExisting(root, Options());
        var snapshot = JournalFormat.Validate(root);
        Emit(new { status = "FixtureStatistics", manifest.Kind, manifest.Count,
            snapshotLiveCount = journal.CatalogCounts.SnapshotLiveCount, suffixCount = journal.CatalogCounts.SuffixCount,
            liveBranches = journal.ListBranches().Count, liveTags = snapshot.Tags.Count,
            snapshotBytes = new FileInfo(Path.Combine(root,"refs",CatalogSnapshotCodec.FileName)).Length });
    }
    static void WriteSample(string root, int repetitions) {
        using var journal = Journal.CreateNew(root, Options());
        var target = journal.AppendEventFrame(null, []).Unwrap();
        var id = journal.CreateBranch("main", target).Unwrap();
        var samples = new List<double>(); long allocation = 0;
        for (int i = 0; i < repetitions; i++) {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            if (!journal.MoveRef(id, target, target).Unwrap()) { throw new InvalidOperationException("Public MoveRef failed."); }
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            allocation += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        samples.Sort();
        Emit(new { status = "PublicWriteSample", operation = "warm MoveRef same valid target", repetitions,
            p50Ms = samples[(samples.Count - 1) / 2], p95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
            allocatedBytesPerIteration = allocation / repetitions, productionDurableFlush = "unchanged", directory = root });
    }
    static void ClosedHandle(string root) {
        using (var store = Store.CreateNew(root)) {
            IRbfFile file;
            using (var writer = store.OpenActiveWriter()) {
                file = writer.File;
                file.Append(1, []).Unwrap(); file.DurableFlush();
            }
            // Dispose the actual OS handle while keeping the owning IRbfFile wrapper alive.
            var handle = (Microsoft.Win32.SafeHandles.SafeFileHandle)file.GetType()
                .GetField("_handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(file)!;
            handle.Dispose();
            bool rejected = false, faulted = false;
            try { store.ConfirmDurable(1); }
            catch (ObjectDisposedException) { rejected = true; }
            try { using var reader = store.OpenReader(1); }
            catch (InvalidOperationException) { faulted = true; }
            if (!rejected || !faulted) { throw new InvalidOperationException("Closed-handle failure did not fault store."); }
            Emit(new { status = "ClosedHandleFaultConfirmed", mechanism = "actual SafeFileHandle.Dispose; no exception injection", confirmDurableRejected = rejected, subsequentCallRejected = faulted });
        }
        using var reopened = Store.OpenReadOnlyExisting(root);
        Emit(new { status = "ClosedHandleStrictReopenSucceeded", active = reopened.ActiveSegmentNumber });
    }
    static void Checkpoint(Journal journal) => typeof(Journal).GetMethod("CheckpointCatalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(journal, null);
    static void Ready(string phase) { Emit(new { status = "ReadyToKill", phase }); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); }
    static void KillChild(string root, string phase) {
        if (phase.StartsWith("rotation:")) {
            string stage = phase[9..];
            using var store = Store.CreateNew(root, new() { SegmentSizeThresholdBytes = 32 });
            using (var writer = store.OpenActiveWriter()) { writer.File.Append(1, []).Unwrap(); }
            store.OperationProbe = p => { if (p == stage) { Ready(phase); } };
            using var next = store.OpenActiveWriter();
            throw new InvalidOperationException("Probe not reached.");
        }
        using var journal = Journal.CreateNew(root, Options());
        var target = journal.AppendEventFrame(null, []).Unwrap();
        journal.CreateBranch("main", target).Unwrap();
        journal.OperationProbe = p => { if (p == phase) { Ready(phase); } };
        journal.TagPublicationProbe = p => { if ("tag:" + p == phase) { Ready(phase); } };
        if (phase.StartsWith("tag:")) { journal.CreateTag("saved", target).Unwrap(); }
        else if (phase.StartsWith("Checkpoint")) { Checkpoint(journal); }
        else { journal.AppendEventFrame(target, []).Unwrap(); }
        throw new InvalidOperationException("Probe not reached.");
    }
    static void Verify(string root, string phase) {
        try {
            if (phase.StartsWith("rotation:")) {
                using var store = Store.OpenReadOnlyExisting(root);
                Emit(new { status = "StrictReopenSucceeded", phase, active = store.ActiveSegmentNumber });
            }
            else {
                using var journal = Journal.OpenReadOnlyExisting(root, Options());
                var id = journal.OpenBranch("main").Unwrap();
                journal.GetHead(id);
                bool tag = journal.ResolveTag("saved").IsSuccess;
                long eventTail = journal.ReadPhysicalAppendFrontier().TailOffset;
                ulong tailSequence = 0;
                if (phase.StartsWith("Event")) {
                    var address = phase == "EventBeforeAppend" ? journal.GetHead(id)!.Value : new EventAddress(SizedPtr.Create(96, 88), 1, default);
                    using var frame = journal.ReadEvent(address).Unwrap();
                    tailSequence = frame.Header.SequenceNumber;
                    if (phase != "EventBeforeAppend" && frame.Header.Parent != journal.GetHead(id)) {
                        throw new InvalidOperationException("Killed event parent mismatch.");
                    }
                }
                ulong nextSequence = (ulong)typeof(Journal).GetField("_nextSequenceNumber", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(journal)!;
                Emit(new { status = "StrictReopenSucceeded", phase, savedTagPresent = tag, eventTail, tailSequence, nextSequence });
            }
        }
        catch (StorageOpenException e) { Emit(new { status = "StrictReopenRejected", phase, kind = e.Kind.ToString(), code = e.ReasonCode }); }
    }
}
