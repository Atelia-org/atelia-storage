using Atelia.Rbf;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;

namespace Atelia.EventJournal;

internal sealed class RefMoveStore : IDisposable {
    private readonly SegmentStore _segments;
    private bool _disposed;

    private RefMoveStore(RefId refId, SegmentStore segments) {
        RefId = refId;
        _segments = segments;
    }

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

        using var lease = _segments.OpenActiveWriter();
        var appendResult = lease.File.Append(EventJournal.RefMoveFrameTag, payload);
        if (appendResult.IsFailure) { return appendResult.Error!; }

        lease.File.DurableFlush();
        return new FrameAddress(appendResult.Unwrap(), lease.SegmentNumber);
    }

    internal AteliaResult<IReadOnlyList<RefMoveFrame>> ReadAllMoves() {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var moves = new List<RefMoveFrame>();
        for (uint segmentNumber = 1; segmentNumber <= ActiveSegmentNumber; segmentNumber++) {
            using var lease = _segments.OpenReader(segmentNumber);
            if (!ReadMovesFromFile(lease.File, moves, out AteliaError? error)) { return error!; }
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
        _segments.Dispose();
        _disposed = true;
    }

    internal static string GetObjectPath(string refObjectsRootPath, RefId refId) {
        return Path.Combine(refObjectsRootPath, refId.ToHexString());
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

    private static void ValidateRefId(RefId refId) {
        if (refId.IsDefault) { throw new ArgumentOutOfRangeException(nameof(refId), refId, "RefId cannot be default."); }
    }
}
