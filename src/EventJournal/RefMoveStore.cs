using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.EventJournal;

internal readonly record struct RefMoveEndpoints(
    RefMoveFrame Init,
    FrameAddress InitAddress,
    RefMoveFrame Last,
    FrameAddress LastAddress
);

internal sealed class RefMoveStore : IDisposable {
    private readonly SegmentStore _segments;
    private bool _disposed;

    private RefMoveStore(RefId refId, SegmentStore segments) {
        RefId = refId;
        _segments = segments;
    }

    internal bool IsDisposed => _disposed;
    internal Action? DisposeProbe { get; set; }
    internal RefId RefId { get; }
    internal uint ActiveSegmentNumber => _segments.ActiveSegmentNumber;

    internal static RefMoveStore CreateNew(string refObjectRootPath, RefId refId, RbfSegmentStoreOptions options) {
        ValidateRefId(refId);
        if (options.NewStoreLayout != RbfSegmentStoreLayout.Flat) {
            throw new ArgumentException("Ref move store must use Flat segment layout.", nameof(options));
        }

        return new RefMoveStore(refId, SegmentStore.CreateNew(GetObjectPath(refObjectRootPath, refId), options));
    }

    internal static RefMoveStore OpenExisting(string refObjectRootPath, RefId refId, RbfSegmentStoreOptions options) {
        ValidateRefId(refId);
        if (options.NewStoreLayout != RbfSegmentStoreLayout.Flat) {
            throw new ArgumentException("Ref move store must use Flat segment layout.", nameof(options));
        }

        return new RefMoveStore(refId, SegmentStore.OpenExisting(GetObjectPath(refObjectRootPath, refId), options));
    }

    internal static RefMoveStore OpenReadOnlyExisting(
        string refObjectRootPath,
        RefId refId,
        RbfSegmentStoreOptions options
    ) {
        ValidateRefId(refId);
        if (options.NewStoreLayout != RbfSegmentStoreLayout.Flat) {
            throw new ArgumentException(
                "Ref move store must use Flat segment layout.",
                nameof(options)
            );
        }

        return new RefMoveStore(
            refId,
            SegmentStore.OpenReadOnlyExisting(
                GetObjectPath(refObjectRootPath, refId),
                options
            )
        );
    }

    internal AteliaResult<FrameAddress> AppendMove(in RefMoveFrame move) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (move.RefId != RefId) {
            return new EventJournalError(
                "RefMoveRefIdMismatch",
                $"RefMoveFrame RefId {move.RefId} does not match object RefId {RefId}.",
                "Append moves only to their owning ref object."
            );
        }

        Span<byte> payload = stackalloc byte[RefMoveFrameCodec.FixedLength];
        RefMoveFrameCodec.Encode(in move, payload);

        return AppendPreparedMove(in move, payload);
    }

    // Caller can prepare the known-size buffer before catalog checkpoint/allocation publication.
    internal AteliaResult<FrameAddress> AppendPreparedMove(scoped in RefMoveFrame move, scoped ReadOnlySpan<byte> payload) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (move.RefId != RefId || payload.Length != RefMoveFrameCodec.FixedLength) {
            throw new ArgumentException("Prepared move does not belong to this object or has invalid length.");
        }
        using var lease = _segments.OpenActiveWriter();
        var appendResult = lease.File.Append(EventJournal.RefMoveFrameTag, payload);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        lease.File.DurableFlush();
        return new FrameAddress(appendResult.Unwrap(), lease.SegmentNumber);
    }

    // Daily state loading reads two endpoints, never the intervening move history.
    internal AteliaResult<RefMoveEndpoints> ReadEndpoints() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RefMoveFrame init;
        FrameAddress initAddress;
        using (var first = _segments.OpenReader(1)) {
            var result = ReadEndpoint(first.File, 1, first: true);
            if (result.IsFailure) { return result.Error!; }
            (init, initAddress) = result.Unwrap();
        }

        uint lastSegment = ActiveSegmentNumber;
        using (var active = _segments.OpenReader(lastSegment)) {
            if (active.File.TailOffset != 4 || lastSegment == 1) {
                var result = ReadEndpoint(active.File, lastSegment, first: false);
                if (result.IsFailure) { return result.Error!; }
                var (last, address) = result.Unwrap();
                return new RefMoveEndpoints(init, initAddress, last, address);
            }
        }
        // Rotation can publish an empty active. Only its immediate predecessor is eligible.
        using (var previous = _segments.OpenReader(lastSegment - 1)) {
            var result = ReadEndpoint(previous.File, lastSegment - 1, first: false);
            if (result.IsFailure) { return result.Error!; }
            var (last, address) = result.Unwrap();
            return new RefMoveEndpoints(init, initAddress, last, address);
        }
    }

    private AteliaResult<(RefMoveFrame Move, FrameAddress Address)> ReadEndpoint(IRbfFile file, uint segmentNumber, bool first) {
        RbfFrameInfo info;
        if (first) {
            var scan = file.ScanForward(showTombstone: true).GetEnumerator();
            if (!scan.MoveNext()) { return scan.TerminationError ?? EmptyObjectError(); }
            info = scan.Current;
            if (info.Ticket.Offset != 4) { return EmptyObjectError(); }
        }
        else {
            var scan = file.ScanReverse(showTombstone: true).GetEnumerator();
            if (!scan.MoveNext()) { return scan.TerminationError ?? EmptyObjectError(); }
            info = scan.Current;
            if (file.GetPhysicalOffsetImmediatelyAfter(info.Ticket) != file.TailOffset) {
                return new EventJournalError("RefObjectTailInvalid", "The last move does not end at the physical file tail.");
            }
        }
        var move = ReadCheckedMove(info);
        if (move.IsFailure) {
            if (move.Error is EventJournalError error) {
                var details = error.Details is null ? new Dictionary<string, string>() : new Dictionary<string, string>(error.Details);
                details["SegmentNumber"] = segmentNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
                details["Offset"] = info.Ticket.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new EventJournalError(error.ErrorName, error.Message, error.RecoveryHint, details, error.Cause);
            }
            return move.Error!;
        }
        return (move.Unwrap(), new FrameAddress(info.Ticket, segmentNumber));
    }

    private EventJournalError EmptyObjectError() => new(
        "RefObjectEmpty", "A required ref segment contains no complete move.",
        "A ref object must contain Init; an empty active may have only one nonempty predecessor."
    );

    private AteliaResult<RefMoveFrame> ReadCheckedMove(RbfFrameInfo info) {
        if (info.IsTombstone || info.Tag != EventJournal.RefMoveFrameTag) {
            return new EventJournalError("RefObjectUnexpectedFrameTag", "Ref objects contain only non-tombstone RefMoveFrame records.");
        }
        if (info.TailMetaLength != 0 || info.PayloadLength != RefMoveFrameCodec.FixedLength) {
            return new EventJournalError("RefMoveLengthInvalid", "RefMoveFrame must have its fixed payload length and no TailMeta.");
        }
        using var frame = info.ReadPooledFrame().ToDisposable();
        if (frame.IsFailure) { return frame.Error!; }
        var decoded = RefMoveFrameCodec.Decode(frame.Unwrap().PayloadAndMeta);
        if (decoded.IsFailure) { return decoded.Error!; }
        if (decoded.Value.RefId != RefId) {
            return new EventJournalError("RefMoveRefIdMismatch", "RefMoveFrame identity does not match its owning ref object.");
        }
        return decoded.Value;
    }

    internal AteliaResult<IReadOnlyList<RefMoveFrame>> ReadAllMoves() {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var moves = new List<RefMoveFrame>();
        for (uint segmentNumber = 1; ; segmentNumber++) {
            using var lease = _segments.OpenReader(segmentNumber);
            if (!ReadMovesFromFile(lease.File, moves, out AteliaError? error)) { return error!; }
            if (segmentNumber == ActiveSegmentNumber) { break; }
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
        _disposed = true;
        try { _segments.Dispose(); }
        finally { DisposeProbe?.Invoke(); }
    }

    internal static string GetObjectPath(string refObjectsRootPath, RefId refId) {
        return Path.Combine(refObjectsRootPath, refId.ToHexString());
    }

    private bool ReadMovesFromFile(IRbfFile file, List<RefMoveFrame> moves, out AteliaError? error) {
        error = null;

        var enumerator = file.ScanForward(showTombstone: true).GetEnumerator();
        while (enumerator.MoveNext()) {
            var moveResult = ReadCheckedMove(enumerator.Current);
            if (moveResult.IsFailure) {
                error = moveResult.Error;
                return false;
            }
            moves.Add(moveResult.Unwrap());
        }

        if (enumerator.TerminationError is not null) {
            error = enumerator.TerminationError;
            return false;
        }

        return true;
    }

    private static void ValidateRefId(RefId refId) {
        if (refId.IsDefault) { throw new ArgumentOutOfRangeException(nameof(refId), refId, "RefId cannot be default."); }
    }
}
