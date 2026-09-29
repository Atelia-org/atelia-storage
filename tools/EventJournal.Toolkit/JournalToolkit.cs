using System.Buffers;
using System.Security.Cryptography;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using Journal = Atelia.EventJournal.EventJournal;
namespace Atelia.EventJournal.Toolkit;

public static class JournalToolkit {
    // Fault/consistency probes are internal and never part of the CLI or storage API.
    private static readonly AsyncLocal<Action<string>?> ProbeSlot = new();
    internal static Action<string>? Probe { get => ProbeSlot.Value; set => ProbeSlot.Value = value; }
    public static AuditReport Audit(string source, CancellationToken cancellationToken = default) => new AuditEngine(source, cancellationToken).Run().Report;
    public static AuditReport RebuildIndexes(string source, string output, CancellationToken cancellationToken = default) {
        ToolkitPaths.RequireOutsideNew(source, output);
        var engine = new AuditEngine(source, cancellationToken);
        engine.Run();
        if (!engine.Report.Completed || engine.Report.FactsStatus != "Healthy" || !engine.UniqueBoundaries || !engine.ValidFormat) { return engine.Report; }
        try {
            cancellationToken.ThrowIfCancellationRequested();
            Probe?.Invoke("BeforeCandidateCreate");
            // Re-check immediately before creating output, then use create-only files throughout.
            ToolkitPaths.RequireOutsideNew(source, output);
            ToolkitPaths.CreateNewDirectory(output);
            var outputs = new List<CandidateOutput>();
            foreach (var store in engine.Stores.OrderBy(s => s.Root, StringComparer.Ordinal)) {
                Write(store.Root + "/active.segment", "Locator", stream => stream.Write(SegmentLocator.Encode(store.Layout, store.Segments.Keys.Max())));
            }
            Write("refs/catalog.snapshot", "CatalogSnapshot", stream => CatalogSnapshotCodec.Write(stream,
                new CatalogSnapshot(engine.LogBoundary, engine.Branches, engine.Tags)));
            Probe?.Invoke("BeforeSourceRecheck");
            var after = engine.Capture();
            if (!engine.Before.EqualsInventory(after)) { engine.Incomplete("IoFailure", "."); return engine.Report; }
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = new CandidateManifest(1, true, engine.Root, after.Facts, after.Indexes,
                outputs.OrderBy(o => o.RelativePath, StringComparer.Ordinal).ToArray());
            Probe?.Invoke("BeforeManifestPublish");
            string candidateRoot = Path.Combine(output, "candidate");
            var actualOutputs = ToolkitPaths.EnumerateFilesNoReparse(candidateRoot).Select(p => Path.GetRelativePath(candidateRoot, p).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal);
            if (!actualOutputs.SequenceEqual(outputs.Select(o => o.RelativePath).Order(StringComparer.Ordinal))) { engine.Incomplete("IoFailure", "."); return engine.Report; }
            foreach (var item in outputs) {
                string candidate = Path.Combine(output, "candidate", item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                ToolkitPaths.RequireNoReparse(candidate);
                if (new FileInfo(candidate).Length != item.Length || AuditEngine.Hash(candidate) != item.Sha256) { engine.Incomplete("IoFailure", "."); return engine.Report; }
            }
            if (!engine.Before.EqualsInventory(engine.Capture())) { engine.Incomplete("IoFailure", "."); return engine.Report; }
            cancellationToken.ThrowIfCancellationRequested();
            string temporary = Path.Combine(output, ".manifest.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(ToolkitJson.Serialize(manifest)); writer.Flush(); stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ToolkitPaths.RequireNoReparse(output);
            File.Move(temporary, Path.Combine(output, "manifest.json"), overwrite: false);
            return engine.Report;
            void Write(string relative, string kind, Action<Stream> write) {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(output, "candidate", relative.Replace('/', Path.DirectorySeparatorChar));
                ToolkitPaths.RequireNoReparse(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
                outputs.Add(new(kind, relative, new FileInfo(target).Length, AuditEngine.Hash(target)));
                Probe?.Invoke("CandidateWritten");
            }
        }
        catch (OperationCanceledException) { engine.Incomplete("OperationCancelled", "."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { engine.Incomplete("IoFailure", "."); }
        return engine.Report;
    }
}

internal sealed record Inventory(SourceFact[] Facts, SourceIndex[] Indexes) {
    internal bool EqualsInventory(Inventory other) => Facts.SequenceEqual(other.Facts) && Indexes.SequenceEqual(other.Indexes);
}
internal sealed class StoreInventory(string root, RbfSegmentStoreLayout layout) {
    internal string Root { get; } = root;
    internal RbfSegmentStoreLayout Layout { get; } = layout;
    internal SortedDictionary<uint, string> Segments { get; } = new();
}
internal sealed record LocatedMove(RefMoveFrame Move, string Path, long Offset);
internal sealed record Allocation(RefOpFrame Op, long Offset, long NextOffset);
internal sealed class AuditEngine {
    internal readonly string Root;
    private readonly CancellationToken _token;
    private readonly bool _legacy;
    internal AuditReport Report { get; } = new();
    internal List<StoreInventory> Stores { get; } = new();
    internal Inventory Before { get; private set; } = new([], []);
    internal bool UniqueBoundaries { get; private set; } = true;
    internal bool ValidFormat { get; private set; }
    internal Dictionary<string, RefId> Branches { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, EventAddress> Tags { get; } = new(StringComparer.Ordinal);
    internal RbfScanBoundary LogBoundary { get; private set; } = RbfScanBoundary.Empty;
    private readonly Dictionary<EventAddress, EventFrameHeader> _events = new();
    private readonly HashSet<EventAddress> _referenced = new();
    private readonly Dictionary<RefId, Allocation> _allocations = new();
    private readonly HashSet<RefId> _everBound = new();
    private readonly Dictionary<RefId, RefOpFrame> _archives = new();
    private readonly Dictionary<RefId, List<LocatedMove>> _moves = new();
    private readonly HashSet<string> _acceptedDirectories = new(StringComparer.Ordinal) { ".", "events", "refs", "refs/objects" };
    private CatalogSnapshot? _snapshot;
    private bool _snapshotMatched;
    private long _suffixCount;
    private ulong _sequence;
    private bool _coverageComplete = true;
    internal AuditEngine(string source, CancellationToken token, bool legacy = false) { Root = Path.GetFullPath(source); _token = token; _legacy = legacy; }
    internal EventAddress? LastEventAddress { get; private set; }
    internal ulong LastEventSequence => _sequence;
    internal EventAddress? RefHead(RefId id) => _moves[id][^1].Move.NewTarget;
    internal AuditEngine Run() {
        try {
            _token.ThrowIfCancellationRequested();
            ToolkitPaths.RequireNoReparse(Root);
            Before = Capture();
            InventoryStores();
            if (!_legacy) {
                try { JournalFormat.ValidateMarker(Root); ValidFormat = true; }
                catch (StorageOpenException e) { Fact(e.ReasonCode, "journal.format", e.Offset); }
                ReadSnapshot();
            }
            foreach (var store in Stores.Where(s => s.Root == "events")) { ScanEvents(store); }
            ScanLog();
            foreach (var store in Stores.Where(s => s.Root != "events")) { ScanMoves(store); }
            ValidateRefs();
            foreach (var address in _events.Keys.Where(a => !_referenced.Contains(a))) { Warning("OrphanEvent", EventPath(address), address.Ticket.Offset, address: address); }
            if (_snapshot is not null && (!_snapshotMatched || _suffixCount > Math.Max(1024L, _snapshot.LiveCount))) { Index("CatalogInvalid", "refs/catalog.snapshot", "Invalid"); }
            JournalToolkit.Probe?.Invoke("AuditBeforeSourceRecheck");
            if (!Before.EqualsInventory(Capture())) { Incomplete("IoFailure", "."); return this; }
            _token.ThrowIfCancellationRequested();
            Report.Completed = _coverageComplete;
        }
        catch (OperationCanceledException) { Incomplete("OperationCancelled", "."); }
        catch (ArgumentException) { Fact("DirectoryInventoryInvalid", "."); Report.Completed = false; UniqueBoundaries = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Incomplete("IoFailure", "."); }
        return this;
    }
    internal void Incomplete(string code, string path) { Report.Completed = false; Report.FactsStatus = "Incomplete"; Add("Error", code, path); }
    private void Fact(string code, string path, long? offset = null, RefId? id = null, EventAddress? address = null) {
        if (Report.FactsStatus != "Incomplete") { Report.FactsStatus = "Invalid"; }
        Add("Error", code, path, offset, id, address);
    }
    private void Warning(string code, string path, long? offset = null, RefId? id = null, EventAddress? address = null) => Add("Warning", code, path, offset, id, address);
    private void Add(string severity, string code, string path, long? offset = null, RefId? id = null, EventAddress? address = null) =>
        Report.Findings.Add(new(severity, code, path, offset, id?.ToHexString(), address is { } a ? AuditEventAddress.From(a) : null));
    private void Index(string code, string path, string status, long? offset = null) {
        int Rank(string s) => s switch { "Ambiguous" => 3, "Invalid" => 2, "Missing" => 1, _ => 0 };
        if (Rank(status) > Rank(Report.IndexesStatus)) { Report.IndexesStatus = status; }
        Add("Error", code, path, offset);
    }
    private string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    private string Relative(string full) => Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
    internal static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private IEnumerable<string> Walk(string directory, bool directories = false) {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal)) {
            _token.ThrowIfCancellationRequested();
            bool isDirectory = ToolkitPaths.RequireOrdinaryEntry(path);
            if (isDirectory) {
                if (directories) { yield return Relative(path); }
                foreach (string entry in Walk(path, directories)) { yield return entry; }
            }
            else if (!directories) { yield return Relative(path); }
        }
    }
    internal Inventory Capture() {
        var facts = new List<SourceFact>(); var indexes = new Dictionary<string, SourceIndex>(StringComparer.Ordinal);
        foreach (string path in Walk(Root)) {
            string? kind = path == "journal.format" ? "Format" : path == "refs/ref-op-log.rbf" ? "RefOpLog"
                : path.StartsWith("events/", StringComparison.Ordinal) && path.EndsWith(".rbf", StringComparison.Ordinal) ? "EventSegment"
                : path.StartsWith("refs/objects/", StringComparison.Ordinal) && path.EndsWith(".rbf", StringComparison.Ordinal) ? "RefMoveSegment" : null;
            if (kind is not null) { facts.Add(new(kind, path, new FileInfo(Full(path)).Length, Hash(Full(path)))); }
            if (path == "refs/catalog.snapshot" || IsLocatorPath(path)) {
                indexes[path] = new(path == "refs/catalog.snapshot" ? "CatalogSnapshot" : "Locator", path, true, new FileInfo(Full(path)).Length, Hash(Full(path)));
            }
        }
        AddAbsent("events/active.segment", "Locator"); AddAbsent("refs/catalog.snapshot", "CatalogSnapshot");
        string objects = Full("refs/objects");
        if (Directory.Exists(objects)) { foreach (string obj in Directory.EnumerateDirectories(objects)) { ToolkitPaths.RequireNoReparse(obj); string relative = Relative(obj) + "/active.segment"; if (IsLocatorPath(relative)) { AddAbsent(relative, "Locator"); } } }
        _token.ThrowIfCancellationRequested();
        return new(facts.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(), indexes.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
        void AddAbsent(string path, string kind) { indexes.TryAdd(path, new(kind, path, false, null, null)); }
    }
    private static bool IsLocatorPath(string path) {
        if (path == "events/active.segment") { return true; }
        var parts = path.Split('/');
        return parts.Length == 4 && parts[0] == "refs" && parts[1] == "objects" && parts[3] == "active.segment"
            && RefId.ParseHex(parts[2]).IsSuccess && parts[2] != "0000000000000000";
    }
    private void InventoryStores() {
        AddStore("events", RbfSegmentStoreLayout.Bucketed);
        string objects = Full("refs/objects");
        if (!Directory.Exists(objects)) { Fact("DirectoryInventoryInvalid", "refs/objects"); }
        else {
            foreach (string obj in Directory.EnumerateDirectories(objects).Order(StringComparer.Ordinal)) {
                string rel = Relative(obj); string name = Path.GetFileName(obj);
                var id = RefId.ParseHex(name);
                if (id.IsFailure || id.Unwrap().IsDefault) { Fact("DirectoryInventoryInvalid", rel); UniqueBoundaries = false; continue; }
                _acceptedDirectories.Add(rel); Report.Counts.RefObjects++;
                AddStore(rel, RbfSegmentStoreLayout.Flat);
            }
        }
        var acceptedFiles = new HashSet<string>(Stores.SelectMany(s => s.Segments.Values), StringComparer.Ordinal) { "journal.format", "refs/ref-op-log.rbf", "refs/catalog.snapshot" };
        acceptedFiles.UnionWith(Stores.Select(s => s.Root + "/active.segment"));
        foreach (string path in Walk(Root)) {
            if (path.StartsWith("cache/", StringComparison.Ordinal) || path.EndsWith(".tmp", StringComparison.Ordinal)) { Warning("IgnoredTemporaryMetadata", path); continue; }
            if (!acceptedFiles.Contains(path)) {
                Fact("DirectoryInventoryInvalid", path); UniqueBoundaries = false;
                if (path.EndsWith(".rbf", StringComparison.Ordinal)) {
                    // CRC coverage is possible, but this path has no valid physical identity for semantic replay.
                    _coverageComplete = false; Scan(path, "InvalidTail", (_, _, _) => { });
                }
            }
        }
        foreach (string dir in Walk(Root, directories: true)) {
            if (dir == "cache" || dir.StartsWith("cache/", StringComparison.Ordinal)) { continue; }
            if (!_acceptedDirectories.Contains(dir)) { Fact("DirectoryInventoryInvalid", dir); UniqueBoundaries = false; }
        }
    }
    private void AddStore(string root, RbfSegmentStoreLayout layout) {
        var store = new StoreInventory(root, layout); Stores.Add(store);
        string layoutRoot = root + (layout == RbfSegmentStoreLayout.Bucketed ? "/buckets" : "/segments");
        _acceptedDirectories.Add(layoutRoot);
        if (!Directory.Exists(Full(layoutRoot))) { Fact("DirectoryInventoryInvalid", layoutRoot); UniqueBoundaries = false; return; }
        foreach (string relative in Walk(Full(layoutRoot))) {
            string name = Path.GetFileName(relative);
            if (!RbfSegmentPath.TryParseSegmentFileName(name, out uint number) || number == 0
                || Relative(RbfSegmentPath.GetSegmentPath(Full(root), layout, number)) != relative || !store.Segments.TryAdd(number, relative)) {
                Fact("DirectoryInventoryInvalid", relative); UniqueBoundaries = false; continue;
            }
            if (layout == RbfSegmentStoreLayout.Bucketed) { _acceptedDirectories.Add(root + "/buckets/" + RbfSegmentPath.BucketName(number)); }
        }
        uint expected = 1;
        foreach (uint number in store.Segments.Keys) {
            if (number != expected) { Fact("DirectoryInventoryInvalid", store.Segments[number]); UniqueBoundaries = false; }
            expected = number == uint.MaxValue ? 0 : number + 1;
        }
        if (store.Segments.Count == 0) { Fact("DirectoryInventoryInvalid", root); UniqueBoundaries = false; return; }
        uint highest = store.Segments.Keys.Max();
        foreach (var segment in store.Segments.Where(s => s.Key != highest)) {
            if (new FileInfo(Full(segment.Value)).Length == 4) { Fact("DirectoryInventoryInvalid", segment.Value); UniqueBoundaries = false; }
        }
        if (_legacy) { return; }
        string locator = root + "/active.segment";
        if (!File.Exists(Full(locator))) {
            Index("MetadataMissing", locator, "Missing");
            if (highest > 1 && new FileInfo(Full(store.Segments[highest])).Length == 4) {
                Index("NextSegmentPresent", locator, "Ambiguous"); UniqueBoundaries = false;
            }
        }
        else {
            try {
                var located = SegmentLocator.Read(Full(root));
                if (located.Layout != layout || located.Active != highest) {
                    Index(located.Active < highest ? "NextSegmentPresent" : "ActiveSegmentMissing", locator, "Ambiguous"); UniqueBoundaries = false;
                }
            }
            catch (StorageOpenException e) {
                Index(e.ReasonCode, locator, "Invalid", e.Offset);
                if (highest > 1 && new FileInfo(Full(store.Segments[highest])).Length == 4) { Index("BoundaryMismatch", locator, "Ambiguous"); UniqueBoundaries = false; }
            }
        }
    }
    private void ReadSnapshot() {
        string path = "refs/catalog.snapshot";
        if (!File.Exists(Full(path))) { Index("MetadataMissing", path, "Missing"); return; }
        try { using var stream = new FileStream(Full(path), FileMode.Open, FileAccess.Read, FileShare.Read); _snapshot = CatalogSnapshotCodec.Read(stream, Full(path)); }
        catch (StorageOpenException e) { Index(e.ReasonCode, path, "Invalid", e.Offset); }
    }
    private void Scan(string path, string code, Action<IRbfFile, RbfFrameInfo, RbfPooledFrame> consume) {
        _token.ThrowIfCancellationRequested();
        try {
            using var file = RbfFile.OpenReadOnlyExisting(Full(path), RbfCacheMode.Off);
            var enumerator = file.ScanForward(showTombstone: true).GetEnumerator();
            long nextOffset = 4;
            while (enumerator.MoveNext()) {
                _token.ThrowIfCancellationRequested();
                var info = enumerator.Current;
                nextOffset = file.GetPhysicalOffsetImmediatelyAfter(info.Ticket);
                using var result = info.ReadPooledFrame().ToDisposable();
                if (result.IsFailure) { Fact(code, path, info.Ticket.Offset); continue; }
                Report.Counts.StoredFrameBytesChecked = checked(Report.Counts.StoredFrameBytesChecked + info.Ticket.Length);
                consume(file, info, result.Unwrap());
            }
            if (enumerator.TerminationError is not null || nextOffset != file.TailOffset) { _coverageComplete = false; Fact(code, path, nextOffset); }
        }
        catch (InvalidDataException) { _coverageComplete = false; long length = new FileInfo(Full(path)).Length; Fact(code, path, length >= 4 && length % 4 != 0 ? length - length % 4 : 0); }
        catch (StorageOpenException e) { Fact(e.ReasonCode, path, e.Offset); }
        catch (ArgumentException) { _coverageComplete = false; Fact(code, path, 0); }
    }
    private void ScanEvents(StoreInventory store) {
        foreach (var segment in store.Segments) {
            Report.Counts.EventSegments++;
            Scan(segment.Value, "EventFrameInvalid", (file, info, frame) => {
                if (frame.IsTombstone || frame.Tag != Journal.EventFrameTag || frame.TailMetaLength != EventFrameHeaderCodec.FixedLength) {
                    Fact("EventFrameInvalid", segment.Value, info.Ticket.Offset); return;
                }
                var decoded = EventFrameHeaderCodec.Decode(frame.PayloadAndMeta[^frame.TailMetaLength..]);
                if (decoded.IsFailure) { Fact("EventFrameInvalid", segment.Value, info.Ticket.Offset); return; }
                var header = decoded.Unwrap();
                var address = new EventAddress(info.Ticket, segment.Key, header.Hint);
                LastEventAddress = address;
                Report.Counts.Events++;
                if (_sequence == ulong.MaxValue || header.SequenceNumber != _sequence + 1) { Fact("EventSequenceInvalid", segment.Value, info.Ticket.Offset, address: address); }
                _sequence = header.SequenceNumber;
                if (header.Parent is { } parent) {
                    _referenced.Add(parent);
                    if (!_events.ContainsKey(parent)) { Fact("ParentInvalid", segment.Value, info.Ticket.Offset, address: address); }
                }
                var payload = frame.PayloadAndMeta[..^frame.TailMetaLength];
                if (header.PayloadCodecId == EventPayloadCodecId.Identity) {
                    if (header.PayloadLength != payload.Length) { Fact("PayloadCodecInvalid", segment.Value, info.Ticket.Offset, address: address); }
                }
                else {
                    var logical = EventPayloadCodec.DecodeToArray(header.PayloadCodecId, payload, header.PayloadLength);
                    if (logical.IsFailure) { Fact("PayloadCodecInvalid", segment.Value, info.Ticket.Offset, address: address); }
                    else { ArrayPool<byte>.Shared.Return(logical.Unwrap()); }
                }
                if (!_events.TryAdd(address, header)) { Fact("EventFrameInvalid", segment.Value, info.Ticket.Offset, address: address); }
            });
        }
    }
    private void CheckTarget(EventAddress? target, string code, string path, long offset, RefId? id = null) {
        if (target is not { } address) { return; }
        _referenced.Add(address);
        if (!_events.ContainsKey(address)) { Fact(code, path, offset, id, address); }
    }
    private string EventPath(EventAddress address) => Stores.FirstOrDefault(s => s.Root == "events")?.Segments.GetValueOrDefault(address.SegmentNumber) ?? "events";
    private void MatchSnapshot(IRbfFile file) {
        if (_snapshot is null || _snapshotMatched || _snapshot.Boundary.EndExclusive != LogBoundary.EndExclusive) { return; }
        if (_snapshot.Boundary != LogBoundary || !_snapshot.Branches.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(Branches.OrderBy(p => p.Key, StringComparer.Ordinal))
            || !_snapshot.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(Tags.OrderBy(p => p.Key, StringComparer.Ordinal))) { Index("CatalogInvalid", "refs/catalog.snapshot", "Invalid"); }
        _snapshotMatched = true;
    }
    private void ScanLog() {
        const string path = "refs/ref-op-log.rbf";
        if (!File.Exists(Full(path))) { Fact("DirectoryInventoryInvalid", path); return; }
        Scan(path, "CatalogInvalid", (file, info, frame) => {
            MatchSnapshot(file);
            if (_snapshotMatched) { _suffixCount++; }
            if (frame.IsTombstone || frame.TailMetaLength != 0 || info.Ticket.Length > 248) { Fact("CatalogInvalid", path, info.Ticket.Offset); return; }
            if (frame.Tag == Journal.TagBindingFrameTag) {
                Report.Counts.TagBindings++;
                try {
                    var tag = TagBindingFrameCodec.Decode(frame.PayloadAndMeta);
                    if (!Tags.TryAdd(tag.Name, tag.Target)) { Fact("TagBindingInvalid", path, info.Ticket.Offset, address: tag.Target); }
                    CheckTarget(tag.Target, "TagTargetInvalid", path, info.Ticket.Offset);
                }
                catch (InvalidDataException) { Fact("TagBindingInvalid", path, info.Ticket.Offset); }
            }
            else if (frame.Tag == Journal.RefOpFrameTag) {
                Report.Counts.RefOpFrames++;
                var decoded = RefOpFrameCodec.Decode(frame.PayloadAndMeta);
                if (decoded.IsFailure) { Fact("RefAllocationInvalid", path, info.Ticket.Offset); return; }
                var op = decoded.Unwrap();
                if (Journal.ValidateBranchName(op.BranchName) is not null) { Fact("RefAllocationInvalid", path, info.Ticket.Offset); return; }
                switch (op.Operation) {
                    case RefOpOperation.Create:
                    case RefOpOperation.Fork:
                        var id = new RefId(info.Ticket.Packed);
                        bool valid = op.RefId.IsDefault && !Branches.ContainsKey(op.BranchName) && (op.Operation == RefOpOperation.Create
                            ? op.SourceRefId.IsDefault && op.SourceMoveSequenceNumber == 0 && op.SourceHead is null
                            : !op.SourceRefId.IsDefault && op.SourceMoveSequenceNumber != 0 && op.SourceHead is not null && op.SourceHead == op.StartHead && _allocations.ContainsKey(op.SourceRefId) && !_archives.ContainsKey(op.SourceRefId));
                        if (!valid || !_allocations.TryAdd(id, new(op, info.Ticket.Offset, file.GetPhysicalOffsetImmediatelyAfter(info.Ticket)))) { Fact("RefAllocationInvalid", path, info.Ticket.Offset, id); }
                        CheckTarget(op.SourceHead, "RefTargetInvalid", path, info.Ticket.Offset, id);
                        CheckTarget(op.StartHead, "RefTargetInvalid", path, info.Ticket.Offset, id);
                        break;
                    case RefOpOperation.BindName:
                        if (!_allocations.TryGetValue(op.RefId, out var allocation) || allocation.NextOffset != info.Ticket.Offset
                            || op != allocation.Op with { Operation = RefOpOperation.BindName, RefId = op.RefId }
                            || Branches.ContainsKey(op.BranchName) || !_everBound.Add(op.RefId)) { Fact("RefAllocationInvalid", path, info.Ticket.Offset, op.RefId); }
                        else { Branches.Add(op.BranchName, op.RefId); }
                        break;
                    case RefOpOperation.Archive:
                        if (op.RefId.IsDefault || !op.SourceRefId.IsDefault || op.SourceMoveSequenceNumber <= 1 || op.SourceHead is not null || op.StartHead is not null
                            || !Branches.TryGetValue(op.BranchName, out var active) || active != op.RefId || !_archives.TryAdd(op.RefId, op)) { Fact("RefAllocationInvalid", path, info.Ticket.Offset, op.RefId); }
                        else { Branches.Remove(op.BranchName); }
                        break;
                }
            }
            else { Fact("CatalogInvalid", path, info.Ticket.Offset); }
            var boundary = file.GetScanBoundaryAfter(info.Ticket);
            if (boundary.IsFailure) { Fact("CatalogInvalid", path, info.Ticket.Offset); }
            else { Report.Counts.StoredFrameBytesChecked = checked(Report.Counts.StoredFrameBytesChecked + info.Ticket.Length); LogBoundary = boundary.Unwrap(); }
        });
        // Empty log and a snapshot anchored exactly at EOF also need a comparison.
        if (_snapshot is not null && !_snapshotMatched && _snapshot.Boundary.EndExclusive == LogBoundary.EndExclusive) {
            if (_snapshot.Boundary != LogBoundary || !_snapshot.Branches.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(Branches.OrderBy(p => p.Key, StringComparer.Ordinal))
                || !_snapshot.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(Tags.OrderBy(p => p.Key, StringComparer.Ordinal))) { Index("CatalogInvalid", "refs/catalog.snapshot", "Invalid"); }
            _snapshotMatched = true;
        }
    }
    private void ScanMoves(StoreInventory store) {
        var id = RefId.ParseHex(store.Root.Split('/')[2]).Unwrap();
        var moves = new List<LocatedMove>(); _moves.Add(id, moves);
        foreach (var segment in store.Segments) {
            Scan(segment.Value, "RefMoveInvalid", (file, info, frame) => {
                Report.Counts.RefMoves++;
                if (frame.Tag != Journal.RefMoveFrameTag || frame.IsTombstone || frame.TailMetaLength != 0) { Fact("RefMoveInvalid", segment.Value, info.Ticket.Offset, id); return; }
                var decoded = RefMoveFrameCodec.Decode(frame.PayloadAndMeta);
                if (decoded.IsFailure) { Fact("RefMoveInvalid", segment.Value, info.Ticket.Offset, id); return; }
                var move = decoded.Unwrap();
                if (move.RefId != id) { Fact("RefMoveInvalid", segment.Value, info.Ticket.Offset, id); }
                if (moves.Count == 0) {
                    if (move.Operation != RefMoveOperation.Init || move.MoveSequenceNumber != 1 || move.OldTarget is not null || move.ExpectedOldTarget is not null) { Fact("RefInitInvalid", segment.Value, info.Ticket.Offset, id); }
                }
                else {
                    var previous = moves[^1].Move;
                    if (previous.MoveSequenceNumber == ulong.MaxValue || move.MoveSequenceNumber != previous.MoveSequenceNumber + 1 || previous.Operation == RefMoveOperation.Close
                        || move.Operation == RefMoveOperation.Init || move.OldTarget != previous.NewTarget || move.ExpectedOldTarget != move.OldTarget
                        || move.Operation == RefMoveOperation.Close && move.NewTarget is not null
                        || move.Operation == RefMoveOperation.Advance && (move.NewTarget is null || !_events.TryGetValue(move.NewTarget.Value, out var header) || header.Parent != move.OldTarget)) {
                        Fact("RefMoveInvalid", segment.Value, info.Ticket.Offset, id);
                    }
                }
                CheckTarget(move.ExpectedOldTarget, "RefTargetInvalid", segment.Value, info.Ticket.Offset, id);
                CheckTarget(move.OldTarget, "RefTargetInvalid", segment.Value, info.Ticket.Offset, id);
                CheckTarget(move.NewTarget, "RefTargetInvalid", segment.Value, info.Ticket.Offset, id);
                moves.Add(new(move, segment.Value, info.Ticket.Offset));
            });
        }
    }
    private void ValidateRefs() {
        foreach (var objectEntry in _moves) {
            if (!_allocations.ContainsKey(objectEntry.Key)) { Fact("RefAllocationInvalid", "refs/objects/" + objectEntry.Key, id: objectEntry.Key); }
        }
        foreach (var entry in _allocations) {
            var id = entry.Key; var allocation = entry.Value;
            _moves.TryGetValue(id, out var moves);
            if (!_everBound.Contains(id)) {
                bool legalEmpty = moves is null || moves.Count == 0 && Stores.Any(s => s.Root == "refs/objects/" + id
                    && s.Segments.Count == 1 && s.Segments.ContainsKey(1) && new FileInfo(Full(s.Segments[1])).Length == 4);
                if (legalEmpty || moves is { Count: > 0 }) { Warning("UnpublishedRef", "refs/ref-op-log.rbf", allocation.Offset, id); }
                else { Fact("RefInitInvalid", "refs/objects/" + id, id: id); }
            }
            else if (moves is null || moves.Count == 0) { Fact("RefInitInvalid", "refs/objects/" + id, id: id); }
            if (moves is { Count: > 0 }) {
                var init = moves[0];
                if (init.Move.NewTarget != allocation.Op.StartHead || init.Move.UtcUnixTimeMilliseconds != allocation.Op.UtcUnixTimeMilliseconds || init.Move.ReasonKind != allocation.Op.ReasonKind) {
                    Fact("RefInitInvalid", init.Path, init.Offset, id);
                }
                var last = moves[^1];
                if (_archives.TryGetValue(id, out var archive)) {
                    if (last.Move.Operation != RefMoveOperation.Close || last.Move.MoveSequenceNumber != archive.SourceMoveSequenceNumber
                        || last.Move.ReasonKind != archive.ReasonKind) { Fact("RefMoveInvalid", last.Path, last.Offset, id); }
                }
                else if (last.Move.Operation == RefMoveOperation.Close) { Fact(_everBound.Contains(id) ? "IncompleteArchive" : "RefMoveInvalid", last.Path, last.Offset, id); }
            }
            if (allocation.Op.Operation == RefOpOperation.Fork) {
                bool validSource = false;
                if (_moves.TryGetValue(allocation.Op.SourceRefId, out var sourceMoves)
                    && allocation.Op.SourceMoveSequenceNumber > 0 && allocation.Op.SourceMoveSequenceNumber <= (ulong)sourceMoves.Count) {
                    // Valid move chains are contiguous from 1; one indexed lookup per fork.
                    var sourceMove = sourceMoves[(int)(allocation.Op.SourceMoveSequenceNumber - 1)].Move;
                    validSource = sourceMove.MoveSequenceNumber == allocation.Op.SourceMoveSequenceNumber
                        && sourceMove.NewTarget == allocation.Op.SourceHead && sourceMove.Operation != RefMoveOperation.Close;
                }
                if (!validSource) { Fact("RefAllocationInvalid", "refs/ref-op-log.rbf", allocation.Offset, id); }
            }
        }
    }
}
