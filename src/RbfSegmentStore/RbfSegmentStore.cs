using Atelia.Rbf;

namespace Atelia.RbfSegmentStore;

public sealed class RbfSegmentStore : IRbfSegmentStore {
    private readonly string _storePath;
    private readonly string _segmentsPath;
    private readonly Dictionary<uint, HistoricalReaderEntry> _historicalReaders = new();
    private long _lruClock;
    private IRbfFile _activeFile;
    private int _activeLeaseCount;
    private bool _disposed;

    private RbfSegmentStore(string storePath, RbfSegmentStoreOptions options, uint activeSegmentNumber, IRbfFile activeFile) {
        _storePath = Path.GetFullPath(storePath);
        _segmentsPath = RbfSegmentPath.SegmentsDirectory(_storePath);
        Options = options;
        ActiveSegmentNumber = activeSegmentNumber;
        _activeFile = activeFile;
    }

    public uint ActiveSegmentNumber { get; private set; }
    public RbfSegmentStoreOptions Options { get; }

    public static RbfSegmentStore CreateNew(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath)) { throw new IOException($"Store path already exists: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        IRbfFile? activeFile = null;
        try {
            activeFile = CreateSegment(fullPath, 1, options);
            return new RbfSegmentStore(fullPath, options, 1, activeFile);
        }
        catch {
            activeFile?.Dispose();
            throw;
        }
    }

    public static RbfSegmentStore OpenExisting(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        string segmentsPath = RbfSegmentPath.SegmentsDirectory(fullPath);
        if (!Directory.Exists(segmentsPath)) { throw new DirectoryNotFoundException($"RBF segment store does not exist: {segmentsPath}"); }

        uint activeSegmentNumber = DiscoverActiveSegment(segmentsPath, allowEmpty: false);
        return OpenDiscovered(fullPath, options, activeSegmentNumber);
    }

    public static RbfSegmentStore OpenOrCreate(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        if (File.Exists(fullPath)) { throw new IOException($"Store path is a file: {fullPath}"); }

        string segmentsPath = RbfSegmentPath.SegmentsDirectory(fullPath);
        if (!Directory.Exists(segmentsPath)) {
            Directory.CreateDirectory(fullPath);
            IRbfFile activeFile = CreateSegment(fullPath, 1, options);
            return new RbfSegmentStore(fullPath, options, 1, activeFile);
        }

        uint activeSegmentNumber = DiscoverActiveSegment(segmentsPath, allowEmpty: true);
        if (activeSegmentNumber == 0) {
            IRbfFile activeFile = CreateSegment(fullPath, 1, options);
            return new RbfSegmentStore(fullPath, options, 1, activeFile);
        }

        return OpenDiscovered(fullPath, options, activeSegmentNumber);
    }

    public RbfSegmentWriterLease OpenActiveWriter() {
        ThrowIfDisposed();
        EnsureNoActiveLease();

        if (_activeFile.TailOffset >= Options.SegmentSizeThresholdBytes) {
            RotateActiveSegment();
        }

        _activeLeaseCount++;
        return new RbfSegmentWriterLease(new RbfSegmentLeaseState(this, ActiveSegmentNumber, _activeFile, RbfSegmentLeaseKind.Active));
    }

    public RbfSegmentReaderLease OpenReader(uint segmentNumber) {
        ThrowIfDisposed();
        if (segmentNumber == 0) { throw new ArgumentOutOfRangeException(nameof(segmentNumber), segmentNumber, "Segment number 0 is reserved."); }
        if (segmentNumber > ActiveSegmentNumber) { throw new FileNotFoundException($"Segment {segmentNumber} does not exist."); }

        if (segmentNumber == ActiveSegmentNumber) {
            EnsureNoActiveLease();
            _activeLeaseCount++;
            return new RbfSegmentReaderLease(new RbfSegmentLeaseState(this, segmentNumber, _activeFile, RbfSegmentLeaseKind.Active));
        }

        HistoricalReaderEntry entry = GetHistoricalReader(segmentNumber);
        entry.LeaseCount++;
        entry.LastUsed = ++_lruClock;
        EvictIdleHistoricalReaders();
        return new RbfSegmentReaderLease(new RbfSegmentLeaseState(this, segmentNumber, entry.File, RbfSegmentLeaseKind.Historical));
    }

    internal void ReleaseLease(uint segmentNumber, RbfSegmentLeaseKind kind) {
        if (kind == RbfSegmentLeaseKind.Active) {
            if (_activeLeaseCount > 0) { _activeLeaseCount--; }
            return;
        }

        if (_historicalReaders.TryGetValue(segmentNumber, out var entry)) {
            if (entry.LeaseCount > 0) { entry.LeaseCount--; }
            entry.LastUsed = ++_lruClock;
            EvictIdleHistoricalReaders();
        }
    }

    public void Dispose() {
        if (_disposed) { return; }

        _activeFile.Dispose();
        foreach (var entry in _historicalReaders.Values) {
            entry.File.Dispose();
        }

        _historicalReaders.Clear();
        _disposed = true;
    }

    private static RbfSegmentStore OpenDiscovered(string fullPath, RbfSegmentStoreOptions options, uint activeSegmentNumber) {
        string activePath = RbfSegmentPath.GetSegmentPath(fullPath, activeSegmentNumber);
        if (options.RecoverActiveTailOnOpen) { RecoverActiveTail(activePath, options.CacheMode); }

        IRbfFile activeFile = RbfFile.OpenExisting(activePath, options.CacheMode);
        return new RbfSegmentStore(fullPath, options, activeSegmentNumber, activeFile);
    }

    private static IRbfFile CreateSegment(string storePath, uint segmentNumber, RbfSegmentStoreOptions options) {
        RbfSegmentPath.EnsureBucketDirectory(storePath, segmentNumber);
        return RbfFile.CreateNew(RbfSegmentPath.GetSegmentPath(storePath, segmentNumber), options.CacheMode);
    }

    private static uint DiscoverActiveSegment(string segmentsPath, bool allowEmpty) {
        var discovered = new SortedSet<uint>();

        foreach (string bucketDirectory in Directory.EnumerateDirectories(segmentsPath)) {
            string bucketName = Path.GetFileName(bucketDirectory);
            if (!RbfSegmentPath.TryParseBucketName(bucketName, out uint bucketNumber)) {
                throw new InvalidDataException($"Invalid segment bucket directory: {bucketDirectory}");
            }

            foreach (string entryPath in Directory.EnumerateFileSystemEntries(bucketDirectory)) {
                if (Directory.Exists(entryPath)) { throw new InvalidDataException($"Unexpected directory inside segment bucket: {entryPath}"); }

                string fileName = Path.GetFileName(entryPath);
                if (!RbfSegmentPath.TryParseSegmentFileName(fileName, out uint segmentNumber)) {
                    throw new InvalidDataException($"Invalid segment file name: {entryPath}");
                }

                if (segmentNumber == 0) { throw new InvalidDataException("Segment number 0 is reserved."); }
                if ((segmentNumber >> RbfSegmentPath.SegmentBucketBits) != bucketNumber) {
                    throw new InvalidDataException($"Segment file is in the wrong bucket: {entryPath}");
                }

                if (!discovered.Add(segmentNumber)) { throw new InvalidDataException($"Duplicate segment number: {segmentNumber}"); }
            }
        }

        foreach (string filePath in Directory.EnumerateFiles(segmentsPath)) {
            throw new InvalidDataException($"Unexpected file in segments directory: {filePath}");
        }

        if (discovered.Count == 0) {
            if (allowEmpty) { return 0; }
            throw new InvalidDataException("RBF segment store contains no segments.");
        }

        uint expected = 1;
        foreach (uint segmentNumber in discovered) {
            if (segmentNumber != expected) { throw new InvalidDataException($"Segment numbering has a gap at {expected}."); }
            expected++;
        }

        return discovered.Max;
    }

    private static void RecoverActiveTail(string activePath, RbfCacheMode cacheMode) {
        long fileLength = new FileInfo(activePath).Length;
        if (fileLength == RbfSegmentPath.RbfHeaderOnlyLength) { return; }

        RbfRecoveryHit? recoveryHit = null;
        using (var scanner = RbfRecovery.OpenReadOnly(activePath, cacheMode)) {
            foreach (RbfRecoveryHit hit in scanner.ScanBackward()) {
                recoveryHit = hit;
                break;
            }
        }

        if (recoveryHit is not { } foundHit) { throw new InvalidDataException($"Active segment is not recoverable: {activePath}"); }

        RbfRecovery.TruncateToSuggestedTail(activePath, foundHit);
    }

    private HistoricalReaderEntry GetHistoricalReader(uint segmentNumber) {
        if (_historicalReaders.TryGetValue(segmentNumber, out var entry)) { return entry; }

        string path = RbfSegmentPath.GetSegmentPath(_storePath, segmentNumber);
        var file = RbfFile.OpenReadOnlyExisting(path, Options.CacheMode);
        entry = new HistoricalReaderEntry(file) { LastUsed = ++_lruClock };
        _historicalReaders.Add(segmentNumber, entry);
        return entry;
    }

    private void RotateActiveSegment() {
        _activeFile.Dispose();
        ActiveSegmentNumber++;
        _activeFile = CreateSegment(_storePath, ActiveSegmentNumber, Options);
    }

    private void EvictIdleHistoricalReaders() {
        while (_historicalReaders.Count > Options.HistoricalReaderPoolCapacity) {
            var candidate = _historicalReaders
                .Where(static pair => pair.Value.LeaseCount == 0)
                .OrderBy(static pair => pair.Value.LastUsed)
                .FirstOrDefault();

            if (candidate.Value is null) { return; }

            candidate.Value.File.Dispose();
            _historicalReaders.Remove(candidate.Key);
        }
    }

    private void EnsureNoActiveLease() {
        if (_activeLeaseCount != 0) { throw new InvalidOperationException("The active segment already has a live lease."); }
    }

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class HistoricalReaderEntry {
        internal HistoricalReaderEntry(IRbfFile file) {
            File = file;
        }

        internal IRbfFile File { get; }
        internal int LeaseCount { get; set; }
        internal long LastUsed { get; set; }
    }
}
