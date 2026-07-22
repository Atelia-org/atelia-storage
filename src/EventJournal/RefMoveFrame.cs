using System.Buffers.Binary;

namespace Atelia.EventJournal;

public enum RefMoveOperation : uint {
    Init = 1,
    Advance = 2,
    Move = 3,
    Close = 4
}

public readonly record struct RefMoveFrame(
    RefId RefId,
    ulong MoveSequenceNumber,
    long UtcUnixTimeMilliseconds,
    RefMoveOperation Operation,
    EventAddress? ExpectedOldTarget,
    EventAddress? OldTarget,
    EventAddress? NewTarget,
    uint ReasonKind
);

public static class RefMoveFrameCodec {
    public const int FixedLength = 96;

    private const uint Magic = 0x4D52_4A45; // "EJRM" as little-endian bytes.
    private const ushort FormatVersion = 1;
    private const uint HasExpectedOldTargetFlag = 1u;
    private const uint HasOldTargetFlag = 1u << 1;
    private const uint HasNewTargetFlag = 1u << 2;
    private const uint KnownFlags = HasExpectedOldTargetFlag | HasOldTargetFlag | HasNewTargetFlag;

    public static void Encode(in RefMoveFrame frame, Span<byte> destination) {
        if (destination.Length < FixedLength) { throw new ArgumentException("Destination is too small for RefMoveFrame.", nameof(destination)); }
        if (frame.RefId.IsDefault) { throw new ArgumentException("RefMoveFrame must carry a non-default RefId.", nameof(frame)); }
        if (!Enum.IsDefined(frame.Operation)) { throw new ArgumentOutOfRangeException(nameof(frame), frame.Operation, "Unknown ref move operation."); }

        Span<byte> bytes = destination[..FixedLength];
        bytes.Clear();

        uint flags = 0;
        if (frame.ExpectedOldTarget is not null) { flags |= HasExpectedOldTargetFlag; }
        if (frame.OldTarget is not null) { flags |= HasOldTargetFlag; }
        if (frame.NewTarget is not null) { flags |= HasNewTargetFlag; }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..8], FixedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..12], (uint)frame.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..16], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..24], frame.RefId.Packed);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..32], frame.MoveSequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..40], frame.UtcUnixTimeMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..44], frame.ReasonKind);
        EventAddressCodec.EncodeNullable(frame.ExpectedOldTarget, bytes[48..64]);
        EventAddressCodec.EncodeNullable(frame.OldTarget, bytes[64..80]);
        EventAddressCodec.EncodeNullable(frame.NewTarget, bytes[80..96]);
    }

    public static AteliaResult<RefMoveFrame> Decode(ReadOnlySpan<byte> payload) {
        if (payload.Length != FixedLength) {
            return new EventJournalError(
                "RefMoveLengthInvalid",
                $"RefMoveFrame payload must be exactly {FixedLength} bytes, got {payload.Length}.",
                "Verify that the frame was written by EventJournal ref-store v1."
            );
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..4]);
        if (magic != Magic) {
            return new EventJournalError(
                "RefMoveMagicInvalid",
                $"Unsupported RefMoveFrame magic 0x{magic:X8}.",
                "Verify that the RBF frame belongs to a ref object move chain."
            );
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
        if (version != FormatVersion) {
            return new EventJournalError(
                "RefMoveVersionUnsupported",
                $"Unsupported RefMoveFrame version {version}.",
                "Open this journal with an implementation that supports this ref move format."
            );
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]);
        if (length != FixedLength) {
            return new EventJournalError(
                "RefMoveLengthInvalid",
                $"RefMoveFrame declares length {length}, expected {FixedLength}.",
                "Treat this ref move frame as corrupted."
            );
        }

        var operation = (RefMoveOperation)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
        if (!Enum.IsDefined(operation)) {
            return new EventJournalError(
                "RefMoveOperationInvalid",
                $"Unsupported ref move operation {(uint)operation}.",
                "Treat this ref move frame as corrupted."
            );
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
        if ((flags & ~KnownFlags) != 0) {
            return new EventJournalError(
                "RefMoveFlagsUnsupported",
                $"RefMoveFrame has unsupported flags 0x{flags & ~KnownFlags:X8}.",
                "Open this journal with an implementation that understands these flags."
            );
        }

        var refId = new RefId(BinaryPrimitives.ReadUInt64LittleEndian(payload[16..24]));
        if (refId.IsDefault) {
            return new EventJournalError(
                "RefMoveRefIdInvalid",
                "RefMoveFrame contains default RefId 0.",
                "Treat this ref move frame as corrupted."
            );
        }

        if (!TryDecodeAddressWithFlag(payload[48..64], flags, HasExpectedOldTargetFlag, out EventAddress? expectedOldTarget, out AteliaError? expectedError)) { return expectedError!; }
        if (!TryDecodeAddressWithFlag(payload[64..80], flags, HasOldTargetFlag, out EventAddress? oldTarget, out AteliaError? oldError)) { return oldError!; }
        if (!TryDecodeAddressWithFlag(payload[80..96], flags, HasNewTargetFlag, out EventAddress? newTarget, out AteliaError? newError)) { return newError!; }

        return new RefMoveFrame(
            refId,
            BinaryPrimitives.ReadUInt64LittleEndian(payload[24..32]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[32..40]),
            operation,
            expectedOldTarget,
            oldTarget,
            newTarget,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[40..44])
        );
    }

    private static bool TryDecodeAddressWithFlag(ReadOnlySpan<byte> source, uint flags, uint flag, out EventAddress? address, out AteliaError? error) {
        if (!EventAddressCodec.TryDecodeNullable(source, out address, out error)) { return false; }

        bool flagSet = (flags & flag) != 0;
        if (flagSet == address.HasValue) { return true; }

        error = new EventJournalError(
            "RefMoveAddressFlagMismatch",
            "RefMoveFrame address presence flag does not match encoded nullable address.",
            "Treat this ref move frame as corrupted."
        );
        return false;
    }
}
