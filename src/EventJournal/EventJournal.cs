using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public sealed partial class EventJournal : IDisposable {
    public const uint EventFrameTag = 0x3146_4A45; // "EJF1" as little-endian bytes.
    public const uint RefMoveFrameTag = 0x4D46_4A45; // "EJFM" as little-endian bytes.
    public const uint RefOpFrameTag = 0x4F46_4A45; // "EJFO" as little-endian bytes.

    private readonly EventJournalOptions _options;
    private readonly RbfSegmentStore.RbfSegmentStore _segments;
    private readonly IRbfFile _refOpLog;
    private readonly string _refObjectsPath;
    private readonly string _forwardPlanCachePath;
    private readonly Dictionary<string, RefId> _branches;
    private readonly Dictionary<RefId, RefState> _refStates = new();
    private readonly ForwardPlanCache _forwardPlanCache = new(maxEntries: 4096, maxEstimatedBytes: 16 * 1024 * 1024);
    private readonly Dictionary<RefId, RefForwardBinding> _forwardPlanBindings = new();
    private ulong _nextSequenceNumber;
    private bool _disposed;

    private EventJournal(string journalPath, EventJournalOptions options, RbfSegmentStore.RbfSegmentStore segments, IRbfFile refOpLog, Dictionary<string, RefId> branches, ulong nextSequenceNumber) {
        JournalPath = Path.GetFullPath(journalPath);
        _options = options;
        _segments = segments;
        _refOpLog = refOpLog;
        _refObjectsPath = RefObjectsDirectory(JournalPath);
        _forwardPlanCachePath = ForwardPlanCacheDirectory(JournalPath);
        _branches = branches;
        _nextSequenceNumber = nextSequenceNumber;
    }

    public string JournalPath { get; }
    public uint ActiveSegmentNumber => _segments.ActiveSegmentNumber;

    public static EventJournal CreateNew(string journalPath, EventJournalOptions? options = null) {
        options = (options ?? new EventJournalOptions()).Normalized();
        string fullPath = Path.GetFullPath(journalPath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath)) { throw new IOException($"EventJournal path already exists: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        RbfSegmentStore.RbfSegmentStore? segments = null;
        IRbfFile? refOpLog = null;
        try {
            segments = RbfSegmentStore.RbfSegmentStore.CreateNew(EventsStorePath(fullPath), options.EventSegmentStoreOptions);
            refOpLog = CreateRefOpLog(fullPath, options);
            return new EventJournal(fullPath, options, segments, refOpLog, new Dictionary<string, RefId>(StringComparer.Ordinal), nextSequenceNumber: 1);
        }
        catch {
            refOpLog?.Dispose();
            segments?.Dispose();
            TryDeleteDirectory(fullPath);
            throw;
        }
    }

    public static EventJournal OpenExisting(string journalPath, EventJournalOptions? options = null) {
        options = (options ?? new EventJournalOptions()).Normalized();
        string fullPath = Path.GetFullPath(journalPath);
        var segments = RbfSegmentStore.RbfSegmentStore.OpenExisting(EventsStorePath(fullPath), options.EventSegmentStoreOptions);
        IRbfFile? refOpLog = null;
        try {
            refOpLog = OpenRefOpLog(fullPath, options, createIfMissing: false);
            return new EventJournal(fullPath, options, segments, refOpLog, ReplayRefOpLog(refOpLog), ComputeNextSequenceNumber(segments));
        }
        catch {
            refOpLog?.Dispose();
            segments.Dispose();
            throw;
        }
    }

    public static EventJournal OpenOrCreate(string journalPath, EventJournalOptions? options = null) {
        options = (options ?? new EventJournalOptions()).Normalized();
        string fullPath = Path.GetFullPath(journalPath);
        if (File.Exists(fullPath)) { throw new IOException($"EventJournal path is a file: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        var segments = RbfSegmentStore.RbfSegmentStore.OpenOrCreate(EventsStorePath(fullPath), options.EventSegmentStoreOptions);
        IRbfFile? refOpLog = null;
        try {
            refOpLog = OpenRefOpLog(fullPath, options, createIfMissing: true);
            return new EventJournal(fullPath, options, segments, refOpLog, ReplayRefOpLog(refOpLog), ComputeNextSequenceNumber(segments));
        }
        catch {
            refOpLog?.Dispose();
            segments.Dispose();
            throw;
        }
    }

    public AteliaResult<EventAddress> AppendEventFrame(
        EventAddress? parent,
        ReadOnlySpan<byte> payload,
        uint opaqueEventKind = 0,
        AddressHint hint = default,
        long? utcUnixTimeMilliseconds = null,
        EventPayloadWriteOptions? writeOptions = null
    ) {
        ThrowIfDisposed();

        var lengthError = ValidateLogicalPayloadLength(payload.Length);
        if (lengthError is not null) { return lengthError; }

        if (parent is { } parentAddress) {
            var parentResult = ReadEventHeaderChecked(parentAddress);
            if (parentResult.IsFailure) {
                return new EventJournalError(
                    "ParentInvalid",
                    "Cannot append EventFrame because its parent is missing or invalid.",
                    "Append only after the parent EventAddress has been committed and can be checked-read.",
                    Cause: parentResult.Error
                );
            }
        }

        EventPayloadCodecPolicy codecPolicy = writeOptions?.PayloadCodecPolicy ?? _options.PayloadCodecPolicy;
        var storedPayload = EventPayloadCodec.EncodeForStore(payload, codecPolicy, out AteliaError? codecError);
        if (codecError is not null) { return codecError; }

        long timestamp = utcUnixTimeMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = new EventFrameHeader(storedPayload.CodecId, _nextSequenceNumber, timestamp, opaqueEventKind, hint, (uint)payload.Length, parent);
        Span<byte> tailMeta = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, tailMeta);

        try {
            using var lease = _segments.OpenActiveWriter();
            var appendResult = lease.File.Append(EventFrameTag, storedPayload.Payload, tailMeta);
            if (appendResult.IsFailure) { return appendResult.Error!; }

            lease.File.DurableFlush();
            _nextSequenceNumber++;
            return new EventAddress(appendResult.Unwrap(), lease.SegmentNumber, hint);
        }
        finally {
            storedPayload.Dispose();
        }
    }

    public AteliaResult<EventFrameHeader> ReadEventHeaderPreview(EventAddress address) {
        ThrowIfDisposed();

        var addressError = ValidateAddress(address);
        if (addressError is not null) { return addressError; }

        try {
            using var lease = _segments.OpenReader(address.SegmentNumber);
            using var tailMetaResult = lease.File.ReadPooledTailMeta(address.Ticket).ToDisposable();
            if (tailMetaResult.IsFailure) { return tailMetaResult.Error!; }

            RbfPooledTailMeta tailMeta = tailMetaResult.Unwrap();
            if (tailMeta.Tag != EventFrameTag) { return WrongFrameTagError(tailMeta.Tag); }
            if (tailMeta.IsTombstone) { return TombstoneError(); }

            var headerResult = EventFrameHeaderCodec.Decode(tailMeta.TailMeta);
            if (headerResult.IsFailure) { return headerResult.Error!; }

            EventFrameHeader header = headerResult.Unwrap();
            var hintError = ValidateHint(address, header);
            if (hintError is not null) { return hintError; }

            return header;
        }
        catch (Exception ex) when (IsReadException(ex)) {
            return ReadFailedError(address, ex);
        }
    }

    public AteliaResult<EventFrameHeader> ReadEventHeaderChecked(EventAddress address) {
        ThrowIfDisposed();

        var storedFrameResult = ReadCheckedStoredEventFrame(address);
        if (storedFrameResult.IsFailure) { return storedFrameResult.Error!; }

        using CheckedStoredEventFrame storedFrame = storedFrameResult.Unwrap();
        return storedFrame.Header;
    }

    public AteliaResult<EventFrame> ReadEvent(EventAddress address) {
        ThrowIfDisposed();

        var storedFrameResult = ReadCheckedStoredEventFrame(address);
        if (storedFrameResult.IsFailure) { return storedFrameResult.Error!; }

        CheckedStoredEventFrame storedFrame = storedFrameResult.Unwrap();
        try {
            RbfPooledFrame rbfFrame = storedFrame.Frame;
            EventFrameHeader header = storedFrame.Header;
            ReadOnlySpan<byte> storedPayload = rbfFrame.PayloadAndMeta[..^rbfFrame.TailMetaLength];

            if (header.PayloadCodecId == EventPayloadCodecId.Identity) {
                if (header.PayloadLength != (uint)storedPayload.Length) {
                    storedFrame.Dispose();
                    return new EventJournalError(
                        "PayloadLengthMismatch",
                        $"Identity EventFrame header logical payload length {header.PayloadLength} does not match stored payload length {storedPayload.Length}.",
                        "Treat this EventFrame as corrupted."
                    );
                }

                return new EventFrame(address, header, storedFrame.TakeFrame());
            }

            var decodedResult = EventPayloadCodec.DecodeToArray(header.PayloadCodecId, storedPayload, header.PayloadLength);
            if (decodedResult.IsFailure) {
                storedFrame.Dispose();
                return decodedResult.Error!;
            }

            byte[] decodedPayload = decodedResult.Unwrap();
            return new EventFrame(address, header, storedFrame.TakeFrame(), decodedPayload, (int)header.PayloadLength);
        }
        catch {
            storedFrame.Dispose();
            throw;
        }
    }

    private AteliaResult<CheckedStoredEventFrame> ReadCheckedStoredEventFrame(EventAddress address) {
        var addressError = ValidateAddress(address);
        if (addressError is not null) { return addressError; }

        try {
            using var lease = _segments.OpenReader(address.SegmentNumber);
            var frameResult = lease.File.ReadPooledFrame(address.Ticket);
            if (frameResult.IsFailure) { return frameResult.Error!; }

            RbfPooledFrame frame = frameResult.Unwrap();
            try {
                if (frame.Tag != EventFrameTag) { return DisposeStoredAndReturn(frame, WrongFrameTagError(frame.Tag)); }
                if (frame.IsTombstone) { return DisposeStoredAndReturn(frame, TombstoneError()); }
                if (frame.TailMetaLength != EventFrameHeaderCodec.FixedLength) {
                    return DisposeStoredAndReturn(frame,
                        new EventJournalError(
                            "FrameTailMetaLengthInvalid",
                            $"EventFrame TailMeta length must be {EventFrameHeaderCodec.FixedLength}, got {frame.TailMetaLength}.",
                            "Verify that the frame was written by EventJournal v2."
                        )
                    );
                }

                ReadOnlySpan<byte> tailMeta = frame.PayloadAndMeta[^frame.TailMetaLength..];
                var headerResult = EventFrameHeaderCodec.Decode(tailMeta);
                if (headerResult.IsFailure) { return DisposeStoredAndReturn(frame, headerResult.Error!); }

                EventFrameHeader header = headerResult.Unwrap();
                ReadOnlySpan<byte> storedPayload = frame.PayloadAndMeta[..^frame.TailMetaLength];
                if (header.PayloadCodecId == EventPayloadCodecId.Identity && header.PayloadLength != (uint)storedPayload.Length) {
                    return DisposeStoredAndReturn(frame,
                        new EventJournalError(
                            "PayloadLengthMismatch",
                            $"Identity EventFrame header logical payload length {header.PayloadLength} does not match stored payload length {storedPayload.Length}.",
                            "Treat this EventFrame as corrupted."
                        )
                    );
                }

                var hintError = ValidateHint(address, header);
                if (hintError is not null) { return DisposeStoredAndReturn(frame, hintError); }

                return new CheckedStoredEventFrame(header, frame);
            }
            catch {
                frame.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (IsReadException(ex)) {
            return ReadFailedError(address, ex);
        }
    }

    public AteliaResult<IReadOnlyList<EventAddress>> ReadAncestorChain(
        EventAddress head,
        bool checkedRead = false,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (maxDepth is <= 0) {
            return new EventJournalError(
                "MaxDepthInvalid",
                $"maxDepth must be positive when provided, got {maxDepth}.",
                "Use null for unbounded traversal or a positive depth limit."
            );
        }

        var ancestors = new List<EventAddress>();
        HashSet<EventAddress>? seen = detectCycles ? new HashSet<EventAddress>() : null;
        EventAddress current = head;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            if (maxDepth is { } depthLimit && ancestors.Count >= depthLimit) {
                return new EventJournalError(
                    "TraversalDepthExceeded",
                    $"Ancestor traversal exceeded maxDepth={depthLimit} before reaching root.",
                    "Increase maxDepth or inspect the parent chain for unexpectedly long history."
                );
            }

            if (seen is not null && !seen.Add(current)) {
                return new EventJournalError(
                    "TraversalCycleDetected",
                    "Ancestor traversal encountered the same EventAddress twice.",
                    "Inspect the EventFrame parent chain for corruption."
                );
            }

            ancestors.Add(current);

            var headerResult = checkedRead ? ReadEventHeaderChecked(current) : ReadEventHeaderPreview(current);
            if (headerResult.IsFailure) { return headerResult.Error!; }

            EventFrameHeader header = headerResult.Unwrap();
            if (header.Parent is not { } parent) { break; }
            current = parent;
        }

        return ancestors;
    }

    public AteliaResult<IReadOnlyList<EventAddress>> ReadChronologicalChain(
        EventAddress head,
        bool checkedRead = false,
        int? maxDepth = null,
        bool detectCycles = true,
        CancellationToken cancellationToken = default
    ) {
        var planResult = BuildEphemeralForwardPlan(head, maxDepth, detectCycles, cancellationToken);
        if (planResult.IsFailure) { return planResult.Error!; }

        var replayResult = ReplayForwardPlanAddresses(planResult.Unwrap(), checkedRead, cancellationToken);
        if (replayResult.IsFailure) {
            EvictForwardPlan(head);
        }

        return replayResult;
    }

    public AteliaResult<bool> IsAncestor(EventAddress ancestor, EventAddress descendant, bool checkedRead = false, int? maxDepth = null, CancellationToken cancellationToken = default) {
        var chainResult = ReadAncestorChain(descendant, checkedRead, maxDepth, detectCycles: true, cancellationToken);
        if (chainResult.IsFailure) { return chainResult.Error!; }

        return chainResult.Unwrap().Contains(ancestor);
    }

    public void Dispose() {
        if (_disposed) { return; }
        _refOpLog.Dispose();
        _segments.Dispose();
        _disposed = true;
    }

    private static string EventsStorePath(string journalPath) => Path.Combine(journalPath, "events");
    private static string ForwardPlanCacheDirectory(string journalPath) => Path.Combine(journalPath, "cache", "forward-plans", "v1");

    private static ulong ComputeNextSequenceNumber(RbfSegmentStore.RbfSegmentStore segments) {
        ulong maxSequenceNumber = 0;
        bool found = false;

        for (uint segmentNumber = 1; segmentNumber <= segments.ActiveSegmentNumber; segmentNumber++) {
            using var lease = segments.OpenReader(segmentNumber);
            var enumerator = lease.File.ScanForward().GetEnumerator();
            while (enumerator.MoveNext()) {
                RbfFrameInfo info = enumerator.Current;
                if (info.Tag != EventFrameTag || info.IsTombstone) { continue; }

                using var tailMeta = info.ReadPooledTailMeta().Unwrap();
                var header = EventFrameHeaderCodec.Decode(tailMeta.TailMeta).Unwrap();
                maxSequenceNumber = Math.Max(maxSequenceNumber, header.SequenceNumber);
                found = true;
            }

            if (enumerator.TerminationError is not null) { throw new InvalidDataException($"EventJournal segment {segmentNumber} forward scan failed: {enumerator.TerminationError.Message}"); }
        }

        return found ? maxSequenceNumber + 1 : 1;
    }

    private static AteliaError? ValidateAddress(EventAddress address) {
        if (address.Ticket.Packed == 0 || address.SegmentNumber == 0) {
            return new EventJournalError(
                "AddressInvalid",
                "EventAddress cannot have a zero ticket or segment number.",
                "Use EventAddress? to represent null/unborn states."
            );
        }

        return null;
    }

    private AteliaError? ValidateLogicalPayloadLength(int payloadLength) {
        if (payloadLength > _options.MaxLogicalPayloadLength) {
            return new EventJournalError(
                "PayloadLogicalLengthExceeded",
                $"Logical payload length {payloadLength} exceeds the configured EventJournal maximum {_options.MaxLogicalPayloadLength}.",
                "Reduce the event payload size, or use a future chunked/streaming payload design."
            );
        }

        return null;
    }

    private static AteliaError? ValidateHint(EventAddress address, EventFrameHeader header) {
        if (address.Hint != header.Hint) {
            return new EventJournalError(
                "HintMismatch",
                $"EventAddress hint 0x{address.Hint.Packed:X8} does not match EventFrame header hint 0x{header.Hint.Packed:X8}.",
                "Use the canonical EventAddress returned when the EventFrame was appended."
            );
        }

        return null;
    }

    private static EventJournalError WrongFrameTagError(uint actualTag) => new(
        "FrameTagMismatch",
        $"Expected EventFrame tag 0x{EventFrameTag:X8}, got 0x{actualTag:X8}.",
        "Verify that the address points to an EventJournal EventFrame."
    );

    private static EventJournalError TombstoneError() => new(
        "FrameIsTombstone",
        "The addressed EventFrame is a tombstone.",
        "Do not use tombstone frames as EventJournal events."
    );

    private static EventJournalError ReadFailedError(EventAddress address, Exception ex) => new(
        "ReadFailed",
        $"Failed to open or read EventFrame at segment {address.SegmentNumber}, ticket 0x{address.Ticket.Packed:X16}: {ex.Message}",
        "Verify that the EventAddress belongs to this EventJournal and that the store is not corrupted.",
        Cause: new EventJournalError("ReadException", ex.GetType().FullName ?? ex.GetType().Name, ex.Message)
    );

    private static AteliaResult<CheckedStoredEventFrame> DisposeStoredAndReturn(RbfPooledFrame frame, AteliaError error) {
        frame.Dispose();
        return error;
    }

    private static bool IsReadException(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentOutOfRangeException;

    private void ThrowIfDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch {
            // Best-effort cleanup after failed creation.
        }
    }

    private sealed class CheckedStoredEventFrame : IDisposable {
        private RbfPooledFrame? _frame;

        public CheckedStoredEventFrame(EventFrameHeader header, RbfPooledFrame frame) {
            Header = header;
            _frame = frame;
        }

        public EventFrameHeader Header { get; }

        public RbfPooledFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(CheckedStoredEventFrame));

        public RbfPooledFrame TakeFrame() {
            RbfPooledFrame frame = Frame;
            _frame = null;
            return frame;
        }

        public void Dispose() {
            var frame = Interlocked.Exchange(ref _frame, null);
            frame?.Dispose();
        }
    }
}
