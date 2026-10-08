using System.Buffers.Binary;
using System.Text;

namespace Atelia.Binary;

public ref partial struct BareValueReader {
    /// <summary>Reads a string preserving its UTF-16 code-unit sequence.</summary>
    public string ReadString(int maxPayloadByteCount = int.MaxValue) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadByteCount);
        var cursor = this;
        uint header = cursor.ReadVarUInt32();
        uint declaredByteCount = (header & 1) == 0 ? header : header >> 1;
        if (declaredByteCount > int.MaxValue || declaredByteCount > (uint)maxPayloadByteCount) {
            throw new InvalidDataException("The string payload exceeds the permitted byte count.");
        }
        ReadOnlySpan<byte> payload = cursor.ReadRawBytes((int)declaredByteCount);
        string value;
        if ((header & 1) == 0) {
            value = string.Create(payload.Length / 2, payload, static (characters, bytes) => {
                for (int i = 0; i < characters.Length; i++) {
                    characters[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2));
                }
            });
        }
        else {
            try {
                value = BareValueEncoding.StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException exception) {
                throw new InvalidDataException("The string payload is not valid UTF-8.", exception);
            }
        }
        this = cursor;
        return value;
    }

    /// <summary>Reads a length-prefixed byte span borrowed from the source.</summary>
    public ReadOnlySpan<byte> ReadBytes(int maxByteCount = int.MaxValue) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxByteCount);
        var cursor = this;
        uint byteCount = cursor.ReadVarUInt32();
        if (byteCount > int.MaxValue || byteCount > (uint)maxByteCount) {
            throw new InvalidDataException("The byte payload exceeds the permitted byte count.");
        }
        ReadOnlySpan<byte> value = cursor.ReadRawBytes((int)byteCount);
        this = cursor;
        return value;
    }
}
