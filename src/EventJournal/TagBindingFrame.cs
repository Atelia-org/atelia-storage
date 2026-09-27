using System.Buffers.Binary;
using System.Text;

namespace Atelia.EventJournal;

internal readonly record struct TagBindingFrame(string Name, EventAddress Target);

internal static class TagBindingFrameCodec {
    internal const int HeaderLength = 32;
    private const uint Magic = 0x4754_4A45; // "EJTG"

    internal static byte[] Encode(string name, EventAddress target) {
        byte[] payload = new byte[HeaderLength + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), HeaderLength);
        EventAddressCodec.Encode(target, payload.AsSpan(8, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), checked((ushort)name.Length));
        Encoding.ASCII.GetBytes(name, payload.AsSpan(HeaderLength));
        return payload;
    }

    internal static TagBindingFrame Decode(ReadOnlySpan<byte> payload) {
        if (payload.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(payload) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) != HeaderLength) {
            throw new InvalidDataException("Invalid or unsupported tag binding header.");
        }
        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload[24..]);
        if (length is < 1 or > 128 || payload.Length != HeaderLength + length ||
            payload[26..HeaderLength].ContainsAnyExcept((byte)0)) {
            throw new InvalidDataException("Invalid tag binding length or reserved bytes.");
        }
        foreach (byte b in payload[HeaderLength..]) {
            if (b > 127) { throw new InvalidDataException("Tag name must contain only ASCII bytes."); }
        }
        string name = Encoding.ASCII.GetString(payload[HeaderLength..]);
        if (EventJournal.ValidateTagName(name) is not null) { throw new InvalidDataException("Invalid persisted tag name."); }
        var target = EventAddressCodec.Decode(payload[8..24]);
        if (target.IsFailure) { throw new InvalidDataException($"Invalid tag address: {target.Error!.Message}"); }
        return new(name, target.Unwrap());
    }
}
