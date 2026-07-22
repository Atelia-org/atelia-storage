using Atelia.Rbf;
using Atelia.RbfSegmentStore;

namespace Atelia.EventJournal;

public sealed class EventJournal : IDisposable {
    public const uint EventFrameTag = 0x3146_4A45; // "EJF1" as little-endian bytes.

    private readonly RbfSegmentStore.RbfSegmentStore _segments;
    private ulong _nextSequenceNumber;
    private bool _disposed;

    private EventJournal(string journalPath, RbfSegmentStore.RbfSegmentStore segments, ulong nextSequenceNumber) {
        JournalPath = Path.GetFullPath(journalPath);
        _segments = segments;
        _nextSequenceNumber = nextSequenceNumber;
    }

    public string JournalPath { get; }
    public uint ActiveSegmentNumber => _segments.ActiveSegmentNumber;

    public static EventJournal CreateNew(string journalPath, EventJournalOptions? options = null) {
        options ??= new EventJournalOptions();
        string fullPath = Path.GetFullPath(journalPath);
        if (Directory.Exists(fullPath) || File.Exists(fullPath)) { throw new IOException($"EventJournal path already exists: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        RbfSegmentStore.RbfSegmentStore? segments = null;
        try {
            segments = RbfSegmentStore.RbfSegmentStore.CreateNew(EventsStorePath(fullPath), options.SegmentStoreOptions);
            return new EventJournal(fullPath, segments, nextSequenceNumber: 1);
        }
        catch {
            segments?.Dispose();
            TryDeleteDirectory(fullPath);
            throw;
        }
    }

    public static EventJournal OpenExisting(string journalPath, EventJournalOptions? options = null) {
        options ??= new EventJournalOptions();
        string fullPath = Path.GetFullPath(journalPath);
        var segments = RbfSegmentStore.RbfSegmentStore.OpenExisting(EventsStorePath(fullPath), options.SegmentStoreOptions);
        try {
            return new EventJournal(fullPath, segments, ComputeNextSequenceNumber(segments));
        }
        catch {
            segments.Dispose();
            throw;
        }
    }

    public static EventJournal OpenOrCreate(string journalPath, EventJournalOptions? options = null) {
        options ??= new EventJournalOptions();
        string fullPath = Path.GetFullPath(journalPath);
        if (File.Exists(fullPath)) { throw new IOException($"EventJournal path is a file: {fullPath}"); }

        Directory.CreateDirectory(fullPath);
        var segments = RbfSegmentStore.RbfSegmentStore.OpenOrCreate(EventsStorePath(fullPath), options.SegmentStoreOptions);
        try {
            return new EventJournal(fullPath, segments, ComputeNextSequenceNumber(segments));
        }
        catch {
            segments.Dispose();
            throw;
        }
    }

    public AteliaResult<EventAddress> AppendEventFrame(
        EventAddress? parent,
        ReadOnlySpan<byte> payload,
        uint opaqueEventKind = 0,
        AddressHint hint = default,
        long? utcUnixTimeMilliseconds = null
    ) {
        ThrowIfDisposed();

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

        long timestamp = utcUnixTimeMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = new EventFrameHeader(_nextSequenceNumber, timestamp, opaqueEventKind, hint, (ulong)payload.Length, parent);
        Span<byte> tailMeta = stackalloc byte[EventFrameHeaderCodec.FixedLength];
        EventFrameHeaderCodec.Encode(in header, tailMeta);

        using var lease = _segments.OpenActiveWriter();
        var appendResult = lease.File.Append(EventFrameTag, payload, tailMeta);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        lease.File.DurableFlush();
        _nextSequenceNumber++;
        return new EventAddress(appendResult.Unwrap(), lease.SegmentNumber, hint);
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

        using var eventFrameResult = ReadEvent(address).ToDisposable();
        if (eventFrameResult.IsFailure) { return eventFrameResult.Error!; }

        EventFrame eventFrame = eventFrameResult.Unwrap();
        return eventFrame.Header;
    }

    public AteliaResult<EventFrame> ReadEvent(EventAddress address) {
        ThrowIfDisposed();

        var addressError = ValidateAddress(address);
        if (addressError is not null) { return addressError; }

        try {
            using var lease = _segments.OpenReader(address.SegmentNumber);
            var frameResult = lease.File.ReadPooledFrame(address.Ticket);
            if (frameResult.IsFailure) { return frameResult.Error!; }

            RbfPooledFrame frame = frameResult.Unwrap();
            try {
                if (frame.Tag != EventFrameTag) { return DisposeAndReturn(frame, WrongFrameTagError(frame.Tag)); }
                if (frame.IsTombstone) { return DisposeAndReturn(frame, TombstoneError()); }
                if (frame.TailMetaLength != EventFrameHeaderCodec.FixedLength) {
                    return DisposeAndReturn(frame,
                        new EventJournalError(
                            "FrameTailMetaLengthInvalid",
                            $"EventFrame TailMeta length must be {EventFrameHeaderCodec.FixedLength}, got {frame.TailMetaLength}.",
                            "Verify that the frame was written by EventJournal v1."
                        )
                    );
                }

                ReadOnlySpan<byte> tailMeta = frame.PayloadAndMeta[^frame.TailMetaLength..];
                var headerResult = EventFrameHeaderCodec.Decode(tailMeta);
                if (headerResult.IsFailure) { return DisposeAndReturn(frame, headerResult.Error!); }

                EventFrameHeader header = headerResult.Unwrap();
                int payloadLength = frame.PayloadAndMeta.Length - frame.TailMetaLength;
                if (header.PayloadLength != (ulong)payloadLength) {
                    return DisposeAndReturn(frame,
                        new EventJournalError(
                            "PayloadLengthMismatch",
                            $"EventFrame header payload length {header.PayloadLength} does not match checked payload length {payloadLength}.",
                            "Treat this EventFrame as corrupted."
                        )
                    );
                }

                var hintError = ValidateHint(address, header);
                if (hintError is not null) { return DisposeAndReturn(frame, hintError); }

                return new EventFrame(address, header, frame);
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
        var reverseResult = ReadAncestorChain(head, checkedRead, maxDepth, detectCycles, cancellationToken);
        if (reverseResult.IsFailure) { return reverseResult.Error!; }

        var chronological = new List<EventAddress>(reverseResult.Unwrap());
        chronological.Reverse();
        return chronological;
    }

    public AteliaResult<bool> IsAncestor(EventAddress ancestor, EventAddress descendant, bool checkedRead = false, int? maxDepth = null, CancellationToken cancellationToken = default) {
        var chainResult = ReadAncestorChain(descendant, checkedRead, maxDepth, detectCycles: true, cancellationToken);
        if (chainResult.IsFailure) { return chainResult.Error!; }

        return chainResult.Unwrap().Contains(ancestor);
    }

    public void Dispose() {
        if (_disposed) { return; }
        _segments.Dispose();
        _disposed = true;
    }

    private static string EventsStorePath(string journalPath) => Path.Combine(journalPath, "events");

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

    private static AteliaResult<EventFrame> DisposeAndReturn(RbfPooledFrame frame, AteliaError error) {
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
}
