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

/// <summary>
/// End-exclusive physical append frontier for one EventJournal events store.
/// The coordinate names the active segment's physical tail at capture time.
/// For a non-saturated segment it is also the next frame start before any
/// rotation. It is not a selected-branch head and therefore also covers
/// physical orphan frames written before capture.
/// </summary>
public readonly record struct EventJournalPhysicalAppendFrontier {
    private const long MaximumPhysicalTailOffset =
        SizedPtr.MaxOffset + SizedPtr.MaxLength + SizedPtr.Alignment;

    public EventJournalPhysicalAppendFrontier(
        uint segmentNumber,
        long tailOffset
    ) {
        if (segmentNumber == 0) {
            throw new ArgumentOutOfRangeException(
                nameof(segmentNumber),
                segmentNumber,
                "A physical append frontier requires a non-zero segment number."
            );
        }
        if (tailOffset < SizedPtr.Alignment
            || tailOffset > MaximumPhysicalTailOffset
            || (tailOffset & SizedPtr.AlignmentMask) != 0) {
            throw new ArgumentOutOfRangeException(
                nameof(tailOffset),
                tailOffset,
                "The frontier tail offset must be aligned and physically reachable by RBF."
            );
        }

        SegmentNumber = segmentNumber;
        TailOffset = tailOffset;
    }

    public uint SegmentNumber { get; }

    public long TailOffset { get; }

    /// <summary>
    /// Returns whether an already checked event from the same EventJournal was
    /// physically appended before this frontier. Same-segment equality is not
    /// contained because <see cref="TailOffset"/> is end-exclusive.
    /// </summary>
    public bool Contains(EventAddress address) {
        if (SegmentNumber == 0
            || TailOffset < SizedPtr.Alignment
            || TailOffset > MaximumPhysicalTailOffset
            || (TailOffset & SizedPtr.AlignmentMask) != 0) {
            throw new InvalidOperationException(
                "A default or invalid physical append frontier cannot classify events."
            );
        }
        if (address.SegmentNumber == 0
            || address.Ticket.Packed == 0
            || address.Ticket.Length <= 0) {
            throw new ArgumentException(
                "A physical append frontier can classify only a non-default EventAddress.",
                nameof(address)
            );
        }

        return address.SegmentNumber < SegmentNumber
            || address.SegmentNumber == SegmentNumber
                && address.Ticket.Offset < TailOffset;
    }
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
