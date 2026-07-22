using System.Buffers.Binary;
using System.Text;

namespace Atelia.EventJournal;

public enum RefOpOperation : uint {
    Create = 1,
    Fork = 2,
    BindName = 3,
    Archive = 4
}

public readonly record struct RefOpFrame(
    RefOpOperation Operation,
    string BranchName,
    RefId RefId,
    RefId SourceRefId,
    ulong SourceMoveSequenceNumber,
    EventAddress? SourceHead,
    EventAddress? StartHead,
    long UtcUnixTimeMilliseconds,
    uint ReasonKind
);

public static class RefOpFrameCodec {
    public const int FixedHeaderLength = 96;

    private const uint Magic = 0x4F52_4A45; // "EJRO" as little-endian bytes.
    private const ushort FormatVersion = 1;
    private const uint HasRefIdFlag = 1u;
    private const uint HasSourceRefIdFlag = 1u << 1;
    private const uint HasSourceHeadFlag = 1u << 2;
    private const uint HasStartHeadFlag = 1u << 3;
    private const uint KnownFlags = HasRefIdFlag | HasSourceRefIdFlag | HasSourceHeadFlag | HasStartHeadFlag;

    public static byte[] Encode(in RefOpFrame frame) {
        if (!Enum.IsDefined(frame.Operation)) { throw new ArgumentOutOfRangeException(nameof(frame), frame.Operation, "Unknown ref op operation."); }

        byte[] branchNameBytes = Encoding.UTF8.GetBytes(frame.BranchName);
        if (branchNameBytes.Length > ushort.MaxValue) { throw new ArgumentOutOfRangeException(nameof(frame), branchNameBytes.Length, "Branch name is too long for RefOpFrame."); }

        byte[] payload = new byte[FixedHeaderLength + branchNameBytes.Length];
        Span<byte> header = payload.AsSpan(0, FixedHeaderLength);

        uint flags = 0;
        if (!frame.RefId.IsDefault) { flags |= HasRefIdFlag; }
        if (!frame.SourceRefId.IsDefault) { flags |= HasSourceRefIdFlag; }
        if (frame.SourceHead is not null) { flags |= HasSourceHeadFlag; }
        if (frame.StartHead is not null) { flags |= HasStartHeadFlag; }

        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], FixedHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)frame.Operation);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..24], frame.RefId.Packed);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..32], frame.SourceRefId.Packed);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..40], frame.SourceMoveSequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(header[40..48], frame.UtcUnixTimeMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], frame.ReasonKind);
        BinaryPrimitives.WriteUInt16LittleEndian(header[52..54], (ushort)branchNameBytes.Length);
        EventAddressCodec.EncodeNullable(frame.SourceHead, header[56..72]);
        EventAddressCodec.EncodeNullable(frame.StartHead, header[72..88]);
        branchNameBytes.CopyTo(payload.AsSpan(FixedHeaderLength));

        return payload;
    }

    public static AteliaResult<RefOpFrame> Decode(ReadOnlySpan<byte> payload) {
        if (payload.Length < FixedHeaderLength) {
            return new EventJournalError(
                "RefOpLengthInvalid",
                $"RefOpFrame payload must be at least {FixedHeaderLength} bytes, got {payload.Length}.",
                "Treat this ref-op-log frame as corrupted."
            );
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(payload[0..4]);
        if (magic != Magic) {
            return new EventJournalError(
                "RefOpMagicInvalid",
                $"Unsupported RefOpFrame magic 0x{magic:X8}.",
                "Verify that the RBF frame belongs to ref-op-log."
            );
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
        if (version != FormatVersion) {
            return new EventJournalError(
                "RefOpVersionUnsupported",
                $"Unsupported RefOpFrame version {version}.",
                "Open this journal with an implementation that supports this ref-op-log format."
            );
        }

        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]);
        if (headerLength != FixedHeaderLength) {
            return new EventJournalError(
                "RefOpLengthInvalid",
                $"RefOpFrame declares header length {headerLength}, expected {FixedHeaderLength}.",
                "Treat this ref-op-log frame as corrupted."
            );
        }

        var operation = (RefOpOperation)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
        if (!Enum.IsDefined(operation)) {
            return new EventJournalError(
                "RefOpOperationInvalid",
                $"Unsupported ref op operation {(uint)operation}.",
                "Treat this ref-op-log frame as corrupted."
            );
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
        if ((flags & ~KnownFlags) != 0) {
            return new EventJournalError(
                "RefOpFlagsUnsupported",
                $"RefOpFrame has unsupported flags 0x{flags & ~KnownFlags:X8}.",
                "Open this journal with an implementation that understands these flags."
            );
        }

        ushort branchNameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[52..54]);
        if (payload.Length != FixedHeaderLength + branchNameLength) {
            return new EventJournalError(
                "RefOpLengthInvalid",
                $"RefOpFrame branch name length {branchNameLength} does not match payload length {payload.Length}.",
                "Treat this ref-op-log frame as corrupted."
            );
        }

        RefId refId = DecodeOptionalRefId(payload[16..24], flags, HasRefIdFlag);
        RefId sourceRefId = DecodeOptionalRefId(payload[24..32], flags, HasSourceRefIdFlag);

        if (!TryDecodeAddressWithFlag(payload[56..72], flags, HasSourceHeadFlag, out EventAddress? sourceHead, out AteliaError? sourceHeadError)) { return sourceHeadError!; }
        if (!TryDecodeAddressWithFlag(payload[72..88], flags, HasStartHeadFlag, out EventAddress? startHead, out AteliaError? startHeadError)) { return startHeadError!; }

        string branchName;
        try {
            branchName = Encoding.UTF8.GetString(payload[FixedHeaderLength..]);
        }
        catch (DecoderFallbackException ex) {
            return new EventJournalError(
                "RefOpBranchNameInvalid",
                "RefOpFrame contains invalid UTF-8 branch name bytes.",
                "Treat this ref-op-log frame as corrupted.",
                Cause: new EventJournalError("Utf8DecodeFailed", ex.Message)
            );
        }

        return new RefOpFrame(
            operation,
            branchName,
            refId,
            sourceRefId,
            BinaryPrimitives.ReadUInt64LittleEndian(payload[32..40]),
            sourceHead,
            startHead,
            BinaryPrimitives.ReadInt64LittleEndian(payload[40..48]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[48..52])
        );
    }

    private static RefId DecodeOptionalRefId(ReadOnlySpan<byte> source, uint flags, uint flag) {
        ulong packed = BinaryPrimitives.ReadUInt64LittleEndian(source);
        return (flags & flag) == 0 ? default : new RefId(packed);
    }

    private static bool TryDecodeAddressWithFlag(ReadOnlySpan<byte> source, uint flags, uint flag, out EventAddress? address, out AteliaError? error) {
        if (!EventAddressCodec.TryDecodeNullable(source, out address, out error)) { return false; }

        bool flagSet = (flags & flag) != 0;
        if (flagSet == address.HasValue) { return true; }

        error = new EventJournalError(
            "RefOpAddressFlagMismatch",
            "RefOpFrame address presence flag does not match encoded nullable address.",
            "Treat this ref-op-log frame as corrupted."
        );
        return false;
    }
}
