using Atelia.Rbf;

namespace Atelia.RbfSegmentStore;

public sealed class RbfSegmentStore : IRbfSegmentStore {
    private readonly string _storePath;
    private readonly RbfSegmentStoreLayout _layout;
    private readonly Dictionary<uint, HistoricalReaderEntry> _historicalReaders = new();
    private long _lruClock;
    private IRbfFile _activeFile;
    private int _activeLeaseCount;
    private bool _disposed;

    private RbfSegmentStore(string storePath, RbfSegmentStoreOptions options, RbfSegmentStoreLayout layout, uint activeSegmentNumber, IRbfFile activeFile) {
        _storePath = Path.GetFullPath(storePath);
        _layout = layout;
        Options = options;
        ActiveSegmentNumber = activeSegmentNumber;
        _activeFile = activeFile;
    }

    public uint ActiveSegmentNumber { get; private set; }
    public RbfSegmentStoreLayout Layout => _layout;
    public RbfSegmentStoreOptions Options { get; }

    public static RbfSegmentStore CreateNew(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath)) { throw new IOException($"Store path already exists: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        IRbfFile? activeFile = null;
        try {
            activeFile = CreateSegment(fullPath, options.NewStoreLayout, 1, options);
            return new RbfSegmentStore(fullPath, options, options.NewStoreLayout, 1, activeFile);
        }
        catch {
            activeFile?.Dispose();
            throw;
        }
    }

    public static RbfSegmentStore OpenExisting(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        RbfSegmentStoreLayout layout = DiscoverLayout(fullPath)
            ?? throw new DirectoryNotFoundException($"RBF segment store does not exist: {fullPath}");

        uint activeSegmentNumber = DiscoverActiveSegment(RbfSegmentPath.LayoutDirectory(fullPath, layout), layout, allowEmpty: false);
        return OpenDiscovered(fullPath, options, layout, activeSegmentNumber);
    }

    public static RbfSegmentStore OpenOrCreate(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        string fullPath = Path.GetFullPath(storePath);
        if (File.Exists(fullPath)) { throw new IOException($"Store path is a file: {fullPath}"); }

        RbfSegmentStoreLayout? discoveredLayout = DiscoverLayout(fullPath);
        if (discoveredLayout is null) {
            Directory.CreateDirectory(fullPath);
            IRbfFile activeFile = CreateSegment(fullPath, options.NewStoreLayout, 1, options);
            return new RbfSegmentStore(fullPath, options, options.NewStoreLayout, 1, activeFile);
        }

        RbfSegmentStoreLayout layout = discoveredLayout.Value;
        uint activeSegmentNumber = DiscoverActiveSegment(RbfSegmentPath.LayoutDirectory(fullPath, layout), layout, allowEmpty: true);
        if (activeSegmentNumber == 0) {
            IRbfFile activeFile = CreateSegment(fullPath, layout, 1, options);
            return new RbfSegmentStore(fullPath, options, layout, 1, activeFile);
        }

        return OpenDiscovered(fullPath, options, layout, activeSegmentNumber);
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

    private static RbfSegmentStore OpenDiscovered(string fullPath, RbfSegmentStoreOptions options, RbfSegmentStoreLayout layout, uint activeSegmentNumber) {
        string activePath = RbfSegmentPath.GetSegmentPath(fullPath, layout, activeSegmentNumber);
        if (options.RecoverActiveTailOnOpen) { RecoverActiveTail(activePath, options.CacheMode); }

        IRbfFile activeFile = RbfFile.OpenExisting(activePath, options.CacheMode);
        return new RbfSegmentStore(fullPath, options, layout, activeSegmentNumber, activeFile);
    }

    private static IRbfFile CreateSegment(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber, RbfSegmentStoreOptions options) {
        RbfSegmentPath.EnsureSegmentDirectory(storePath, layout, segmentNumber);
        return RbfFile.CreateNew(RbfSegmentPath.GetSegmentPath(storePath, layout, segmentNumber), options.CacheMode);
    }

    private static RbfSegmentStoreLayout? DiscoverLayout(string fullPath) {
        bool hasBucketed = Directory.Exists(RbfSegmentPath.BucketedDirectory(fullPath));
        bool hasFlat = Directory.Exists(RbfSegmentPath.FlatDirectory(fullPath));

        if (hasBucketed && hasFlat) {
            throw new InvalidDataException($"RBF segment store contains both '{RbfSegmentPath.BucketedDirectoryName}' and '{RbfSegmentPath.FlatDirectoryName}' layout directories: {fullPath}");
        }

        if (hasBucketed) { return RbfSegmentStoreLayout.Bucketed; }
        if (hasFlat) { return RbfSegmentStoreLayout.Flat; }
        return null;
    }

    private static uint DiscoverActiveSegment(string layoutPath, RbfSegmentStoreLayout layout, bool allowEmpty) {
        return layout switch {
            RbfSegmentStoreLayout.Bucketed => DiscoverBucketedActiveSegment(layoutPath, allowEmpty),
            RbfSegmentStoreLayout.Flat => DiscoverFlatActiveSegment(layoutPath, allowEmpty),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown RBF segment store layout.")
        };
    }

    private static uint DiscoverBucketedActiveSegment(string bucketsPath, bool allowEmpty) {
        var discovered = new SortedSet<uint>();

        foreach (string bucketDirectory in Directory.EnumerateDirectories(bucketsPath)) {
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

        foreach (string filePath in Directory.EnumerateFiles(bucketsPath)) {
            throw new InvalidDataException($"Unexpected file in buckets directory: {filePath}");
        }

        return ValidateDiscoveredSegments(discovered, allowEmpty);
    }

    private static uint DiscoverFlatActiveSegment(string segmentsPath, bool allowEmpty) {
        var discovered = new SortedSet<uint>();

        foreach (string entryPath in Directory.EnumerateFileSystemEntries(segmentsPath)) {
            if (Directory.Exists(entryPath)) { throw new InvalidDataException($"Unexpected directory inside flat segments directory: {entryPath}"); }

            string fileName = Path.GetFileName(entryPath);
            if (!RbfSegmentPath.TryParseSegmentFileName(fileName, out uint segmentNumber)) {
                throw new InvalidDataException($"Invalid segment file name: {entryPath}");
            }

            if (segmentNumber == 0) { throw new InvalidDataException("Segment number 0 is reserved."); }
            if (!discovered.Add(segmentNumber)) { throw new InvalidDataException($"Duplicate segment number: {segmentNumber}"); }
        }

        return ValidateDiscoveredSegments(discovered, allowEmpty);
    }

    private static uint ValidateDiscoveredSegments(SortedSet<uint> discovered, bool allowEmpty) {
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

        string path = RbfSegmentPath.GetSegmentPath(_storePath, _layout, segmentNumber);
        var file = RbfFile.OpenReadOnlyExisting(path, Options.CacheMode);
        entry = new HistoricalReaderEntry(file) { LastUsed = ++_lruClock };
        _historicalReaders.Add(segmentNumber, entry);
        return entry;
    }

    private void RotateActiveSegment() {
        _activeFile.Dispose();
        ActiveSegmentNumber++;
        _activeFile = CreateSegment(_storePath, _layout, ActiveSegmentNumber, Options);
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
