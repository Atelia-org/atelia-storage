using Atelia.Rbf;

namespace Atelia.RbfSegmentStore;

public sealed class RbfSegmentStore : IRbfSegmentStore {
    private readonly string _storePath;
    private readonly RbfSegmentStoreLayout _layout;
    private readonly bool _isReadOnly;
    private readonly Dictionary<uint, HistoricalReaderEntry> _historicalReaders = new();
    private long _lruClock;
    private IRbfFile _activeFile;
    private int _activeLeaseCount;
    private bool _disposed;
    private bool _faulted;
    internal Action<string>? OperationProbe { get; set; }

    private RbfSegmentStore(string storePath, RbfSegmentStoreOptions options, RbfSegmentStoreLayout layout, uint activeSegmentNumber, IRbfFile activeFile, bool isReadOnly = false) {
        _storePath = Path.GetFullPath(storePath);
        _layout = layout;
        _isReadOnly = isReadOnly;
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
            activeFile.DurableFlush();
            SegmentLocator.Publish(fullPath, options.NewStoreLayout, 1);
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
        return OpenLocated(fullPath, options, false);
    }

    public static RbfSegmentStore OpenReadOnlyExisting(string storePath, RbfSegmentStoreOptions? options = null) {
        options = (options ?? new RbfSegmentStoreOptions()).Validated();
        return OpenLocated(Path.GetFullPath(storePath), options, true);
    }

    public static RbfSegmentStore OpenOrCreate(string storePath, RbfSegmentStoreOptions? options = null) {
        string fullPath = Path.GetFullPath(storePath);
        return Directory.Exists(fullPath) || File.Exists(fullPath)
            ? OpenExisting(fullPath, options) : CreateNew(fullPath, options);
    }

    private static RbfSegmentStore OpenLocated(string fullPath, RbfSegmentStoreOptions options, bool readOnly) {
        if (!Directory.Exists(fullPath)) { throw new DirectoryNotFoundException(fullPath); }
        var (layout, active) = SegmentLocator.Read(fullPath);
        var opposite = layout == RbfSegmentStoreLayout.Flat ? RbfSegmentStoreLayout.Bucketed : RbfSegmentStoreLayout.Flat;
        if (Path.Exists(RbfSegmentPath.LayoutDirectory(fullPath, opposite))) {
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "LayoutConflict", fullPath);
        }
        string activePath = RbfSegmentPath.GetSegmentPath(fullPath, layout, active);
        if (active != uint.MaxValue && Path.Exists(RbfSegmentPath.GetSegmentPath(fullPath, layout, active + 1))) {
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "NextSegmentPresent", fullPath);
        }
        return OpenDiscovered(fullPath, options, layout, active, readOnly);
    }

    public RbfSegmentWriterLease OpenActiveWriter() {
        ThrowIfDisposed();
        if (_isReadOnly) {
            throw new InvalidOperationException(
                "Cannot open an active writer on a read-only RBF segment store."
            );
        }
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

    /// <inheritdoc />
    public void ConfirmDurable(uint segmentNumber) {
        ThrowIfDisposed();
        if (_isReadOnly) {
            throw new InvalidOperationException(
                "Cannot confirm durability on a read-only RBF segment store."
            );
        }
        if (segmentNumber == 0) { throw new ArgumentOutOfRangeException(nameof(segmentNumber), segmentNumber, "Segment number 0 is reserved."); }
        if (segmentNumber > ActiveSegmentNumber) { throw new FileNotFoundException($"Segment {segmentNumber} does not exist."); }
        EnsureNoActiveLease();

        if (_historicalReaders.TryGetValue(segmentNumber, out var leased) && leased.LeaseCount != 0) {
            throw new InvalidOperationException($"Historical segment {segmentNumber} has a live reader lease.");
        }
        try {
            OperationProbe?.Invoke("ConfirmDurable");
            if (segmentNumber == ActiveSegmentNumber) {
                _activeFile.DurableFlush();
                return;
            }
            if (_historicalReaders.TryGetValue(segmentNumber, out var entry)) {
                entry.File.Dispose();
                _historicalReaders.Remove(segmentNumber);
            }
            string path = RbfSegmentPath.GetSegmentPath(_storePath, _layout, segmentNumber);
            using var file = RbfFile.OpenExisting(path, Options.CacheMode);
            file.DurableFlush();
        }
        catch { _faulted = true; throw; }
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

        _disposed = true;
        List<Exception>? errors = null;
        void Release(IDisposable resource) { try { resource.Dispose(); } catch (Exception e) { (errors ??= new()).Add(e); } }
        Release(_activeFile);
        foreach (var entry in _historicalReaders.Values) { Release(entry.File); }
        _historicalReaders.Clear();
        if (errors is not null) { throw new AggregateException(errors); }
    }

    private static RbfSegmentStore OpenDiscovered(
        string fullPath,
        RbfSegmentStoreOptions options,
        RbfSegmentStoreLayout layout,
        uint activeSegmentNumber,
        bool isReadOnly
    ) {
        string activePath = RbfSegmentPath.GetSegmentPath(fullPath, layout, activeSegmentNumber);
        IRbfFile? activeFile = null;
        try {
            activeFile = isReadOnly
                ? RbfFile.OpenReadOnlyExisting(activePath, options.CacheMode)
                : RbfFile.OpenExisting(activePath, options.CacheMode);
            ValidateActiveTail(activeFile, activePath);
            if (activeSegmentNumber > 1 && new FileInfo(activePath).Length == RbfSegmentPath.RbfHeaderOnlyLength) {
                string previousPath = RbfSegmentPath.GetSegmentPath(fullPath, layout, activeSegmentNumber - 1);
                try {
                    using var previous = RbfFile.OpenReadOnlyExisting(previousPath, options.CacheMode);
                    ValidateActiveTail(previous, previousPath);
                    if (new FileInfo(previousPath).Length == RbfSegmentPath.RbfHeaderOnlyLength) {
                        throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", previousPath, 4);
                    }
                }
                catch (InvalidDataException e) {
                    throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", previousPath, innerException: e);
                }
                catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException) {
                    throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "ActiveSegmentMissing", previousPath, innerException: e);
                }
            }
            return new RbfSegmentStore(fullPath, options, layout, activeSegmentNumber, activeFile, isReadOnly);
        }
        catch (InvalidDataException e) {
            activeFile?.Dispose();
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", activePath, innerException: e);
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException) {
            activeFile?.Dispose();
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "ActiveSegmentMissing", activePath, innerException: e);
        }
        catch { activeFile?.Dispose(); throw; }
    }

    private static IRbfFile CreateSegment(string storePath, RbfSegmentStoreLayout layout, uint segmentNumber, RbfSegmentStoreOptions options) {
        RbfSegmentPath.EnsureSegmentDirectory(storePath, layout, segmentNumber);
        return RbfFile.CreateNew(RbfSegmentPath.GetSegmentPath(storePath, layout, segmentNumber), options.CacheMode);
    }

    private static void ValidateActiveTail(IRbfFile activeFile, string activePath) {
        long length = new FileInfo(activePath).Length;
        var enumerator = activeFile.ScanReverse(showTombstone: true).GetEnumerator();
        bool found = enumerator.MoveNext();
        if (!found) {
            if (length == 4 && enumerator.TerminationError is null) { return; }
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", activePath, length);
        }
        var boundary = activeFile.GetScanBoundaryAfter(enumerator.Current.Ticket);
        if (!boundary.IsSuccess || boundary.Value.EndExclusive != length) {
            throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "InvalidTail", activePath, length);
        }
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
        uint nextSegmentNumber = NextSegmentNumber(ActiveSegmentNumber);
        string nextPath = RbfSegmentPath.GetSegmentPath(_storePath, _layout, nextSegmentNumber);
        IRbfFile? nextFile = null;
        try {
            if (Path.Exists(nextPath)) { throw new StorageOpenException(StorageOpenErrorKind.MaintenanceRequired, "NextSegmentPresent", nextPath); }
            OperationProbe?.Invoke("OldFlush");
            _activeFile.DurableFlush();
            OperationProbe?.Invoke("NextCreate");
            nextFile = CreateSegment(_storePath, _layout, nextSegmentNumber, Options);
            OperationProbe?.Invoke("NextFlush");
            nextFile.DurableFlush();
            SegmentLocator.Publish(_storePath, _layout, nextSegmentNumber, OperationProbe);
            OperationProbe?.Invoke("OldDispose");
            _activeFile.Dispose();
            _activeFile = nextFile;
            nextFile = null;
            ActiveSegmentNumber = nextSegmentNumber;
        }
        catch { _faulted = true; throw; }
        finally { nextFile?.Dispose(); }
    }

    internal static uint NextSegmentNumber(uint activeSegmentNumber) =>
        activeSegmentNumber == uint.MaxValue
            ? throw new InvalidOperationException(
                "No SegmentNumber remains after the active Segment."
            )
            : activeSegmentNumber + 1;

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
        if (_faulted) { throw new InvalidOperationException("The segment store is faulted; dispose and reopen it."); }
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
