using System.Buffers.Binary;
using Atelia.Data.Hashing;
using Atelia.Rbf;

namespace Atelia.EventJournal;

public readonly record struct EventFrameHeader(
    ulong SequenceNumber,
    long UtcUnixTimeMilliseconds,
    uint OpaqueEventKind,
    AddressHint Hint,
    ulong PayloadLength,
    EventAddress? Parent
);

public static class EventFrameHeaderCodec {
    public const int FixedLength = 64;

    private const uint Magic = 0x3148_4A45; // "EJH1" as little-endian bytes.
    private const ushort FormatVersion = 1;
    private const uint HasParentFlag = 1u;
    private const uint KnownFlags = HasParentFlag;

    public static void Encode(in EventFrameHeader header, Span<byte> destination) {
        if (destination.Length < FixedLength) { throw new ArgumentException("Destination is too small for EventFrameHeader.", nameof(destination)); }
        if (header.PayloadLength > (ulong)RbfFile.MaxPayloadAndMetaLength) { throw new ArgumentOutOfRangeException(nameof(header), header.PayloadLength, "PayloadLength exceeds the RBF single-frame payload limit."); }

        Span<byte> headerBytes = destination[..FixedLength];
        headerBytes.Clear();

        uint flags = header.Parent is null ? 0 : HasParentFlag;
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes[6..8], FixedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[8..12], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(headerBytes[12..20], header.SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(headerBytes[20..28], header.UtcUnixTimeMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[28..32], header.OpaqueEventKind);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[32..36], header.Hint.Packed);
        BinaryPrimitives.WriteUInt64LittleEndian(headerBytes[36..44], header.PayloadLength);
        EventAddressCodec.EncodeNullable(header.Parent, headerBytes[44..60]);

        uint crc = RollingCrc.CrcForward(headerBytes[..60]);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes[60..64], crc);
    }

    public static AteliaResult<EventFrameHeader> Decode(ReadOnlySpan<byte> tailMeta) {
        if (tailMeta.Length != FixedLength) {
            return new EventJournalError(
                "HeaderLengthInvalid",
                $"EventFrame TailMeta must be exactly {FixedLength} bytes, got {tailMeta.Length}.",
                "Read a v1 EventFrame TailMeta and pass it to the fixed header codec."
            );
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta[0..4]);
        if (magic != Magic) {
            return new EventJournalError(
                "HeaderMagicInvalid",
                $"Unsupported EventFrame header magic 0x{magic:X8}.",
                "Verify that the RBF frame is an EventFrame."
            );
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(tailMeta[4..6]);
        if (version != FormatVersion) {
            return new EventJournalError(
                "HeaderVersionUnsupported",
                $"Unsupported EventFrame header version {version}.",
                "Open this journal with an EventJournal implementation that supports the stored format."
            );
        }

        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(tailMeta[6..8]);
        if (headerLength != FixedLength) {
            return new EventJournalError(
                "HeaderLengthInvalid",
                $"EventFrame header declares length {headerLength}, expected {FixedLength}.",
                "The TailMeta may be corrupted or from an unsupported future format."
            );
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta[60..64]);
        uint actualCrc = RollingCrc.CrcForward(tailMeta[..60]);
        if (expectedCrc != actualCrc) {
            return new EventJournalError(
                "HeaderCrcMismatch",
                $"EventFrame header CRC mismatch: expected 0x{expectedCrc:X8}, actual 0x{actualCrc:X8}.",
                "Treat this EventFrame header as corrupted. Use checked reads or recovery tooling before trusting it."
            );
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta[8..12]);
        if ((flags & ~KnownFlags) != 0) {
            return new EventJournalError(
                "HeaderFlagsUnsupported",
                $"EventFrame header has unsupported flags 0x{flags & ~KnownFlags:X8}.",
                "Open this journal with an implementation that understands these flags."
            );
        }

        if (!EventAddressCodec.TryDecodeNullable(tailMeta[44..60], out EventAddress? parent, out AteliaError? parentError)) {
            return new EventJournalError(
                "HeaderParentInvalid",
                "EventFrame header contains an invalid parent address.",
                "Inspect or repair the EventFrame TailMeta.",
                Cause: parentError
            );
        }

        bool hasParentFlag = (flags & HasParentFlag) != 0;
        if (hasParentFlag != parent.HasValue) {
            return new EventJournalError(
                "HeaderParentFlagMismatch",
                "EventFrame HasParent flag does not match the encoded parent address.",
                "Inspect or repair the EventFrame TailMeta."
            );
        }

        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(tailMeta[36..44]);
        if (payloadLength > (ulong)RbfFile.MaxPayloadAndMetaLength) {
            return new EventJournalError(
                "HeaderPayloadLengthInvalid",
                $"EventFrame header payload length {payloadLength} exceeds the RBF single-frame limit.",
                "Inspect or repair the EventFrame TailMeta."
            );
        }

        return new EventFrameHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(tailMeta[12..20]),
            BinaryPrimitives.ReadInt64LittleEndian(tailMeta[20..28]),
            BinaryPrimitives.ReadUInt32LittleEndian(tailMeta[28..32]),
            new AddressHint(BinaryPrimitives.ReadUInt32LittleEndian(tailMeta[32..36])),
            payloadLength,
            parent
        );
    }
}
