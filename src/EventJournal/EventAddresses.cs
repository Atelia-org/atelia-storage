using System.Buffers.Binary;
using Atelia.Data;

namespace Atelia.EventJournal;

public readonly record struct AddressHint(uint Packed) {
    public static AddressHint None => default;
}

public readonly record struct FrameAddress(SizedPtr Ticket, uint SegmentNumber);

public readonly record struct EventAddress(SizedPtr Ticket, uint SegmentNumber, AddressHint Hint) {
    public FrameAddress FrameAddress => new(Ticket, SegmentNumber);
}

public static class EventAddressCodec {
    public const int SizedPtrLength = sizeof(ulong);
    public const int FrameAddressLength = SizedPtrLength + sizeof(uint);
    public const int EventAddressLength = FrameAddressLength + sizeof(uint);

    public static void Encode(EventAddress address, Span<byte> destination) {
        if (destination.Length < EventAddressLength) { throw new ArgumentException("Destination is too small for EventAddress.", nameof(destination)); }

        BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], address.Ticket.Packed);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], address.SegmentNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], address.Hint.Packed);
    }

    public static AteliaResult<EventAddress> Decode(ReadOnlySpan<byte> source) {
        if (source.Length != EventAddressLength) {
            return new EventJournalError(
                "AddressLengthInvalid",
                $"EventAddress must be exactly {EventAddressLength} bytes, got {source.Length}.",
                "Use the fixed-width EventAddress codec."
            );
        }

        ulong ticketPacked = BinaryPrimitives.ReadUInt64LittleEndian(source[..8]);
        uint segmentNumber = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
        uint hintPacked = BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]);

        if (ticketPacked == 0 || segmentNumber == 0) {
            return new EventJournalError(
                "AddressInvalid",
                "EventAddress cannot have a zero ticket or segment number.",
                "Use EventAddress? for null parent/unborn state instead of a half-empty address."
            );
        }

        return new EventAddress(SizedPtr.FromPacked(ticketPacked), segmentNumber, new AddressHint(hintPacked));
    }

    internal static void EncodeNullable(EventAddress? address, Span<byte> destination) {
        if (destination.Length < EventAddressLength) { throw new ArgumentException("Destination is too small for EventAddress.", nameof(destination)); }

        if (address is null) {
            destination[..EventAddressLength].Clear();
            return;
        }

        Encode(address.Value, destination);
    }

    internal static bool TryDecodeNullable(ReadOnlySpan<byte> source, out EventAddress? address, out AteliaError? error) {
        address = null;
        error = null;

        if (source.Length != EventAddressLength) {
            error = new EventJournalError(
                "AddressLengthInvalid",
                $"Nullable EventAddress must be exactly {EventAddressLength} bytes, got {source.Length}.",
                "Use the fixed-width EventAddress codec."
            );
            return false;
        }

        ulong ticketPacked = BinaryPrimitives.ReadUInt64LittleEndian(source[..8]);
        uint segmentNumber = BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
        uint hintPacked = BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]);

        if (ticketPacked == 0 && segmentNumber == 0 && hintPacked == 0) { return true; }

        if (ticketPacked == 0 || segmentNumber == 0) {
            error = new EventJournalError(
                "AddressInvalid",
                "Nullable EventAddress contains a half-empty non-null address.",
                "Null must be encoded as all zero bytes; non-null addresses need both ticket and segment number."
            );
            return false;
        }

        address = new EventAddress(SizedPtr.FromPacked(ticketPacked), segmentNumber, new AddressHint(hintPacked));
        return true;
    }
}
