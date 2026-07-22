using System.Globalization;
using Atelia.Rbf;

namespace Atelia.EventJournal;

public sealed class RefObjectStore : IDisposable {
    private const string SegmentFileExtension = ".rbf";
    private const long RbfHeaderOnlyLength = 4;

    private readonly string _objectPath;
    private IRbfFile _activeFile;
    private bool _disposed;

    private RefObjectStore(string objectsRootPath, RefId refId, RefObjectStoreOptions options, uint activeSegmentNumber, IRbfFile activeFile) {
        ObjectsRootPath = Path.GetFullPath(objectsRootPath);
        RefId = refId;
        Options = options;
        ActiveSegmentNumber = activeSegmentNumber;
        _objectPath = GetObjectPath(ObjectsRootPath, refId);
        _activeFile = activeFile;
    }

    public string ObjectsRootPath { get; }
    public RefId RefId { get; }
    public RefObjectStoreOptions Options { get; }
    public uint ActiveSegmentNumber { get; private set; }

    public static RefObjectStore CreateNew(string objectsRootPath, RefId refId, RefObjectStoreOptions? options = null) {
        if (refId.IsDefault) { throw new ArgumentOutOfRangeException(nameof(refId), refId, "RefId cannot be default."); }
        options = (options ?? new RefObjectStoreOptions()).Validated();

        string objectPath = GetObjectPath(objectsRootPath, refId);
        if (Directory.Exists(objectPath) || File.Exists(objectPath)) { throw new IOException($"Ref object path already exists: {objectPath}"); }

        Directory.CreateDirectory(objectPath);
        IRbfFile? activeFile = null;
        try {
            activeFile = RbfFile.CreateNew(GetSegmentPath(objectPath, 1), options.CacheMode);
            return new RefObjectStore(objectsRootPath, refId, options, 1, activeFile);
        }
        catch {
            activeFile?.Dispose();
            TryDeleteDirectory(objectPath);
            throw;
        }
    }

    public static RefObjectStore OpenExisting(string objectsRootPath, RefId refId, RefObjectStoreOptions? options = null) {
        if (refId.IsDefault) { throw new ArgumentOutOfRangeException(nameof(refId), refId, "RefId cannot be default."); }
        options = (options ?? new RefObjectStoreOptions()).Validated();

        string objectPath = GetObjectPath(objectsRootPath, refId);
        if (!Directory.Exists(objectPath)) { throw new DirectoryNotFoundException($"Ref object does not exist: {objectPath}"); }

        uint activeSegmentNumber = DiscoverActiveSegment(objectPath);
        string activePath = GetSegmentPath(objectPath, activeSegmentNumber);
        if (options.RecoverActiveTailOnOpen) { RecoverActiveTail(activePath, options.CacheMode); }

        IRbfFile activeFile = RbfFile.OpenExisting(activePath, options.CacheMode);
        return new RefObjectStore(objectsRootPath, refId, options, activeSegmentNumber, activeFile);
    }

    public AteliaResult<FrameAddress> AppendMove(in RefMoveFrame move) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (move.RefId != RefId) {
            return new EventJournalError(
                "RefMoveRefIdMismatch",
                $"RefMoveFrame RefId {move.RefId} does not match object RefId {RefId}.",
                "Append moves only to their owning ref object."
            );
        }

        if (_activeFile.TailOffset >= Options.SegmentSizeThresholdBytes) { RotateActiveSegment(); }

        Span<byte> payload = stackalloc byte[RefMoveFrameCodec.FixedLength];
        RefMoveFrameCodec.Encode(in move, payload);
        var appendResult = _activeFile.Append(EventJournal.RefMoveFrameTag, payload);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        _activeFile.DurableFlush();
        return new FrameAddress(appendResult.Unwrap(), ActiveSegmentNumber);
    }

    public AteliaResult<IReadOnlyList<RefMoveFrame>> ReadAllMoves() {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var moves = new List<RefMoveFrame>();
        for (uint segmentNumber = 1; segmentNumber <= ActiveSegmentNumber; segmentNumber++) {
            if (!ReadSegmentMoves(segmentNumber, moves, out AteliaError? error)) { return error!; }
        }

        if (moves.Count == 0) {
            return new EventJournalError(
                "RefObjectEmpty",
                $"Ref object {RefId} contains no RefMoveFrame.",
                "A valid ref object must start with Init move sequence 1."
            );
        }

        RefMoveFrame first = moves[0];
        if (first.Operation != RefMoveOperation.Init || first.MoveSequenceNumber != 1) {
            return new EventJournalError(
                "RefObjectFirstMoveInvalid",
                "Ref object first move must be Init with MoveSequenceNumber 1.",
                "Treat this ref object as malformed."
            );
        }

        return moves;
    }

    public void Dispose() {
        if (_disposed) { return; }
        _activeFile.Dispose();
        _disposed = true;
    }

    internal static string GetObjectPath(string objectsRootPath, RefId refId) => Path.Combine(objectsRootPath, refId.ToHexString());

    internal static string GetSegmentFileName(uint segmentNumber) => segmentNumber.ToString("x8", CultureInfo.InvariantCulture) + SegmentFileExtension;

    internal static string GetSegmentPath(string objectPath, uint segmentNumber) {
        if (segmentNumber == 0) { throw new ArgumentOutOfRangeException(nameof(segmentNumber), segmentNumber, "Ref object segment number 0 is reserved."); }
        return Path.Combine(objectPath, GetSegmentFileName(segmentNumber));
    }

    private bool ReadSegmentMoves(uint segmentNumber, List<RefMoveFrame> moves, out AteliaError? error) {
        error = null;

        if (segmentNumber == ActiveSegmentNumber) { return ReadMovesFromFile(_activeFile, moves, out error); }

        using IRbfFile file = RbfFile.OpenReadOnlyExisting(GetSegmentPath(_objectPath, segmentNumber), Options.CacheMode);
        return ReadMovesFromFile(file, moves, out error);
    }

    private bool ReadMovesFromFile(IRbfFile file, List<RefMoveFrame> moves, out AteliaError? error) {
        error = null;

        var enumerator = file.ScanForward().GetEnumerator();
        while (enumerator.MoveNext()) {
            RbfFrameInfo info = enumerator.Current;
            if (info.Tag != EventJournal.RefMoveFrameTag) {
                error = new EventJournalError(
                    "RefObjectUnexpectedFrameTag",
                    $"Ref object {RefId} contains unexpected frame tag 0x{info.Tag:X8}.",
                    "A ref object may only contain RefMoveFrame records."
                );
                return false;
            }

            using var frameResult = info.ReadPooledFrame().ToDisposable();
            if (frameResult.IsFailure) {
                error = frameResult.Error;
                return false;
            }

            RbfPooledFrame frame = frameResult.Unwrap();
            if (frame.TailMetaLength != 0) {
                error = new EventJournalError(
                    "RefMoveTailMetaInvalid",
                    "RefMoveFrame must not carry RBF TailMeta.",
                    "Treat this ref object as malformed."
                );
                return false;
            }

            var moveResult = RefMoveFrameCodec.Decode(frame.PayloadAndMeta);
            if (moveResult.IsFailure) {
                error = moveResult.Error;
                return false;
            }

            RefMoveFrame move = moveResult.Unwrap();
            if (move.RefId != RefId) {
                error = new EventJournalError(
                    "RefMoveRefIdMismatch",
                    $"RefMoveFrame RefId {move.RefId} does not match object RefId {RefId}.",
                    "Treat this ref object as malformed."
                );
                return false;
            }

            moves.Add(move);
        }

        if (enumerator.TerminationError is not null) {
            error = enumerator.TerminationError;
            return false;
        }

        return true;
    }

    private void RotateActiveSegment() {
        _activeFile.Dispose();
        ActiveSegmentNumber++;
        _activeFile = RbfFile.CreateNew(GetSegmentPath(_objectPath, ActiveSegmentNumber), Options.CacheMode);
    }

    private static uint DiscoverActiveSegment(string objectPath) {
        var discovered = new SortedSet<uint>();

        foreach (string entryPath in Directory.EnumerateFileSystemEntries(objectPath)) {
            if (Directory.Exists(entryPath)) { throw new InvalidDataException($"Unexpected directory inside ref object: {entryPath}"); }

            string fileName = Path.GetFileName(entryPath);
            if (fileName.EndsWith(".rbf.archived", StringComparison.Ordinal)) { continue; }
            if (!TryParseSegmentFileName(fileName, out uint segmentNumber)) { throw new InvalidDataException($"Invalid ref object segment file name: {entryPath}"); }
            if (!discovered.Add(segmentNumber)) { throw new InvalidDataException($"Duplicate ref object segment number: {segmentNumber}"); }
        }

        if (discovered.Count == 0) { throw new InvalidDataException($"Ref object contains no active segments: {objectPath}"); }

        uint expected = 1;
        foreach (uint segmentNumber in discovered) {
            if (segmentNumber != expected) { throw new InvalidDataException($"Ref object segment numbering has a gap at {expected}."); }
            expected++;
        }

        return discovered.Max;
    }

    private static bool TryParseSegmentFileName(string name, out uint segmentNumber) {
        segmentNumber = 0;
        if (!name.EndsWith(SegmentFileExtension, StringComparison.Ordinal)) { return false; }

        string stem = name[..^SegmentFileExtension.Length];
        return stem.Length == 8
            && IsLowerHex(stem)
            && uint.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out segmentNumber)
            && segmentNumber != 0;
    }

    private static bool IsLowerHex(string value) {
        foreach (char c in value) {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { return false; }
        }

        return true;
    }

    private static void RecoverActiveTail(string activePath, RbfCacheMode cacheMode) {
        long fileLength = new FileInfo(activePath).Length;
        if (fileLength == RbfHeaderOnlyLength) { return; }

        RbfRecoveryHit? recoveryHit = null;
        using (var scanner = RbfRecovery.OpenReadOnly(activePath, cacheMode)) {
            foreach (RbfRecoveryHit hit in scanner.ScanBackward()) {
                recoveryHit = hit;
                break;
            }
        }

        if (recoveryHit is not { } foundHit) { throw new InvalidDataException($"Ref object active segment is not recoverable: {activePath}"); }

        RbfRecovery.TruncateToSuggestedTail(activePath, foundHit);
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch {
            // Best-effort cleanup after failed creation.
        }
    }
}
